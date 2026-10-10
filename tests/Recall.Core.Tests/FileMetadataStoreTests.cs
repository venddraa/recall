using Microsoft.Data.Sqlite;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class FileMetadataStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-sqlite-tests-{Guid.NewGuid():N}");

    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private static ScannedFile FileRecord(string path, long size = 3) => new(
        path, Path.GetFileName(path), Path.GetExtension(path), size,
        new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task InitializesAnEmptyVersionedDatabaseWithWalAndCanReopen()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await CreateStore().GetFileCountAsync(TestContext.Current.CancellationToken));

        using var connection = OpenConnection(store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, command.ExecuteScalar());
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task MetadataSurvivesRestartWithUtcDatesAndLargeFileSizes()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var record = FileRecord("/selected/research.txt", 5_000_000_000);
        Assert.Equal(1, await store.SaveScanAsync([record], TestContext.Current.CancellationToken));

        var restored = await CreateStore().GetFileAsync(record.FullPath, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(record, restored.File);
        Assert.True(restored.Id > 0);
        Assert.Equal(DateTimeKind.Utc, restored.ScannedAtUtc.Kind);
        Assert.Null(await store.GetFileAsync("/selected/missing.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedScanUpdatesMetadataWithoutDuplicateOrChangingId()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var original = FileRecord("/selected/notes.txt");
        await store.SaveScanAsync([original], TestContext.Current.CancellationToken);
        var before = await store.GetFileAsync(original.FullPath, TestContext.Current.CancellationToken);
        var updated = original with { Size = 42, ModifiedAtUtc = original.ModifiedAtUtc.AddDays(1) };

        Assert.Equal(1, await store.SaveScanAsync([updated, updated], TestContext.Current.CancellationToken));
        var after = await store.GetFileAsync(updated.FullPath, TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(updated, after.File);
    }

    [Fact]
    public async Task PathsAreCaseSensitiveAndSpecialCharactersAreStoredLiterally()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var literal = FileRecord("/selected/notes'; DROP TABLE file_metadata; --.txt");
        Assert.Equal(3, await store.SaveScanAsync(
            [FileRecord("/selected/Notes.txt"), FileRecord("/selected/notes.txt"), literal],
            TestContext.Current.CancellationToken));
        Assert.Equal(literal, (await store.GetFileAsync(literal.FullPath, TestContext.Current.CancellationToken))!.File);
    }

    [Fact]
    public async Task MissingEntriesAreNotRemovedByLaterOrEmptyScan()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveScanAsync([FileRecord("/unavailable/keep.txt")], TestContext.Current.CancellationToken);
        Assert.Equal(2, await store.SaveScanAsync([FileRecord("/selected/new.txt")], TestContext.Current.CancellationToken));
        Assert.Equal(2, await store.SaveScanAsync([], TestContext.Current.CancellationToken));
        Assert.NotNull(await store.GetFileAsync("/unavailable/keep.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedBatchRollsBackBothUpdatesAndInserts()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var original = FileRecord("/selected/original.txt");
        await store.SaveScanAsync([original], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveScanAsync(
            [original with { Size = 99 }, FileRecord("/selected/new.txt"), FileRecord("/selected/invalid.txt", -1)],
            TestContext.Current.CancellationToken));

        Assert.Equal(1, await store.GetFileCountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, (await store.GetFileAsync(original.FullPath, TestContext.Current.CancellationToken))!.File);
        Assert.Null(await store.GetFileAsync("/selected/new.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDuringBatchRollsBackAllWrites()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var original = FileRecord("/selected/original.txt");
        await store.SaveScanAsync([original], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        IEnumerable<ScannedFile> CancelAfterFirstRow()
        {
            yield return original with { Size = 99 };
            cancellation.Cancel();
            yield return FileRecord("/selected/new.txt");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveScanAsync(CancelAfterFirstRow(), cancellation.Token));
        Assert.Equal(1, await store.GetFileCountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, (await store.GetFileAsync(original.FullPath, TestContext.Current.CancellationToken))!.File);
    }

    [Fact]
    public async Task UnreadableDatabaseIsReportedWithoutReplacingItsContents()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        const string original = "This is not a SQLite database.";
        await File.WriteAllTextAsync(store.DatabasePath, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllTextAsync(store.DatabasePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutModifyingDatabase()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        using (var connection = OpenConnection(store.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 4;";
            command.ExecuteNonQuery();
        }

        var before = await File.ReadAllBytesAsync(store.DatabasePath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.DatabasePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanningAndSavingNeverModifyOriginalDocument()
    {
        Directory.CreateDirectory(_directory);
        var document = Path.Combine(_directory, "original.txt");
        await File.WriteAllTextAsync(document, "123", TestContext.Current.CancellationToken);
        var timestamp = File.GetLastWriteTimeUtc(document);
        var scan = await new FileScanner().ScanAsync([_directory], cancellationToken: TestContext.Current.CancellationToken);
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveScanAsync(scan.Files, TestContext.Current.CancellationToken);

        Assert.Equal("123", await File.ReadAllTextAsync(document, TestContext.Current.CancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(document));
    }

    [Fact]
    public async Task TemporaryWriteLockIsRetriedWithoutLosingMetadata()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        using var connection = OpenConnection(store.DatabasePath);
        using var transaction = connection.BeginTransaction();
        var save = store.SaveScanAsync([FileRecord("/selected/notes.txt")], TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Assert.False(save.IsCompleted);
        }
        finally
        {
            transaction.Rollback();
        }

        Assert.Equal(1, await save);
        Assert.Equal(1, await store.GetFileCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewDatabaseAndApplicationDirectoryHaveOwnerOnlyPermissionsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(store.DatabasePath)!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
