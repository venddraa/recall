using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class ScanSynchronizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-sync-tests-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(4096, 1024 * 1024, 4096);
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(_directory, "selected");
    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private ScannedFile WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private async Task<TextIndexResult> Synchronize(FileMetadataStore store, params string[] roots)
    {
        var scan = await new FileScanner().ScanAsync(roots, cancellationToken: Token);
        return await store.SynchronizeScanAsync(scan, roots, _extractor, cancellationToken: Token);
    }

    [Fact]
    public async Task CompletedScanRemovesDeletedMetadataTextAndSearchEntries()
    {
        var keep = WriteFile(Path.Combine(Root, "keep.txt"), "retained contents");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "obsolete contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        var originalId = (await store.GetFileAsync(keep.FullPath, Token))!.Id;
        File.Delete(removed.FullPath);

        var result = await Synchronize(store, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(0, result.CleanupSkippedFolders);
        Assert.Equal(1, result.StoredFiles);
        Assert.Equal(1, result.StoredTextFiles);
        Assert.Null(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Empty(await store.SearchAsync("obsolete", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("removed", cancellationToken: Token));
        Assert.Equal(originalId, (await store.GetFileAsync(keep.FullPath, Token))!.Id);
        Assert.Single(await store.SearchAsync("retained", cancellationToken: Token));
        Assert.Equal("retained contents", await File.ReadAllTextAsync(keep.FullPath, Token));
        Assert.Equal(keep.ModifiedAtUtc, File.GetLastWriteTimeUtc(keep.FullPath));
        AssertTableCounts(store, 1);
    }

    [Fact]
    public async Task RenamedFileKeepsContentSearchAndRemovesOldFilename()
    {
        var original = WriteFile(Path.Combine(Root, "oldname.txt"), "remember this phrase");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        var newPath = Path.Combine(Root, "subfolder", "newname.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(original.FullPath, newPath);

        var result = await Synchronize(store, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(1, result.StoredFiles);
        Assert.Empty(await store.SearchAsync("oldname", cancellationToken: Token));
        Assert.Equal(newPath, Assert.Single(await store.SearchAsync("newname", cancellationToken: Token)).FullPath);
        Assert.Equal(newPath, Assert.Single(await store.SearchAsync("remember this", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task SuccessfullyScannedEmptyFolderRemovesAllItsOldEntries()
    {
        var file = WriteFile(Path.Combine(Root, "old.txt"), "old contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        File.Delete(file.FullPath);

        var result = await Synchronize(store, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(0, result.StoredFiles);
        Assert.Equal(0, result.StoredTextFiles);
        AssertTableCounts(store, 0);
    }

    [Fact]
    public async Task UnavailableRootPreservesItsIndexWhileHealthyRootIsCleaned()
    {
        var unavailable = WriteFile(Path.Combine(Root, "cached.txt"), "cached contents");
        var healthyRoot = Path.Combine(_directory, "healthy");
        var deleted = WriteFile(Path.Combine(healthyRoot, "removed.txt"), "removed contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root, healthyRoot);
        Directory.Move(Root, Path.Combine(_directory, "disconnected"));
        File.Delete(deleted.FullPath);

        var result = await Synchronize(store, Root, healthyRoot);

        Assert.Equal(1, result.CleanupSkippedFolders);
        Assert.Equal(1, result.RemovedFiles);
        Assert.NotNull(await store.GetFileAsync(unavailable.FullPath, Token));
        Assert.Single(await store.SearchAsync("cached", cancellationToken: Token));
        Assert.Null(await store.GetFileAsync(deleted.FullPath, Token));
    }

    [Fact]
    public async Task NestedScanFailurePreventsCleanupOfItsWholeSelectedRoot()
    {
        var unreadable = WriteFile(Path.Combine(Root, "nested", "cached.txt"), "cached contents");
        var deleted = WriteFile(Path.Combine(Root, "deleted.txt"), "old contents");
        var seen = WriteFile(Path.Combine(Root, "seen.txt"), "visible contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        File.Delete(deleted.FullPath);
        var partial = new ScanResult([seen], [new ScanFailure(Path.GetDirectoryName(unreadable.FullPath)!, "Permission denied")], 0);

        var result = await store.SynchronizeScanAsync(partial, [Root], _extractor, cancellationToken: Token);

        Assert.Equal(1, result.CleanupSkippedFolders);
        Assert.Equal(0, result.RemovedFiles);
        Assert.Equal(3, result.StoredFiles);
        Assert.NotNull(await store.GetFileAsync(unreadable.FullPath, Token));
        Assert.NotNull(await store.GetFileAsync(deleted.FullPath, Token));
    }

    [Fact]
    public async Task CleanupDoesNotTouchSiblingCaseVariantOrUnselectedRoots()
    {
        var selected = Path.Combine(_directory, "Docs_%'");
        var selectedFile = WriteFile(Path.Combine(selected, "removed.txt"), "selected contents");
        var sibling = WriteFile(Path.Combine(selected + "-backup", "keep.txt"), "sibling contents");
        var caseVariant = WriteFile(Path.Combine(_directory, "docs_%'", "keep.txt"), "case contents");
        var other = WriteFile(Path.Combine(_directory, "other", "keep.txt"), "other contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await store.IndexScanAsync([selectedFile, sibling, caseVariant, other], _extractor, cancellationToken: Token);
        File.Delete(selectedFile.FullPath);

        var result = await Synchronize(store, selected);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(3, result.StoredFiles);
        foreach (var file in new[] { sibling, caseVariant, other })
            Assert.NotNull(await store.GetFileAsync(file.FullPath, Token));
    }

    [Fact]
    public async Task OverlappingRootsKeepSeenFilesAndCountDeletionsOnce()
    {
        var nested = Path.Combine(Root, "nested");
        var keep = WriteFile(Path.Combine(nested, "keep.txt"), "keep contents");
        var deleted = WriteFile(Path.Combine(nested, "deleted.txt"), "deleted contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root, nested);
        File.Delete(deleted.FullPath);

        var result = await Synchronize(store, Root, nested, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(1, result.StoredFiles);
        Assert.Equal(keep.FullPath, Assert.Single(await store.SearchAsync("keep", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task ChangedFileWithExtractionFailureKeepsMetadataAndClearsStaleText()
    {
        var broken = WriteFile(Path.Combine(Root, "broken.txt"), "previous contents");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "removed contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        await File.WriteAllBytesAsync(broken.FullPath, [0xc3, 0x28], Token);
        File.Delete(removed.FullPath);

        var result = await Synchronize(store, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.InvalidEncoding]);
        Assert.Equal(1, result.StoredFiles);
        Assert.Equal(0, result.StoredTextFiles);
        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("broken", cancellationToken: Token));
    }

    [Fact]
    public async Task FileReplacedBySymlinkLosesItsOldIndexWithoutReadingTarget()
    {
        var file = WriteFile(Path.Combine(Root, "linked.txt"), "obsolete contents");
        var target = WriteFile(Path.Combine(_directory, "outside", "private.txt"), "private target contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        File.Delete(file.FullPath);
        File.CreateSymbolicLink(file.FullPath, target.FullPath);

        var result = await Synchronize(store, Root);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(0, result.StoredFiles);
        Assert.Empty(await store.SearchAsync("obsolete", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("private", cancellationToken: Token));
        Assert.Equal("private target contents", await File.ReadAllTextAsync(target.FullPath, Token));
    }

    [Fact]
    public async Task SnapshotContainingFileOutsideSelectionIsRejectedBeforeWriting()
    {
        var original = WriteFile(Path.Combine(Root, "original.txt"), "original contents");
        var outside = WriteFile(Path.Combine(_directory, "outside", "outside.txt"), "outside contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SynchronizeScanAsync(
            new ScanResult([outside], [], 0), [Root], _extractor, cancellationToken: Token));

        Assert.Equal(1, await store.GetFileCountAsync(Token));
        Assert.NotNull(await store.GetFileAsync(original.FullPath, Token));
        Assert.Null(await store.GetFileAsync(outside.FullPath, Token));
    }

    [Fact]
    public async Task CancellationPreservesPreviousMetadataTextAndSearch()
    {
        var keep = WriteFile(Path.Combine(Root, "keep.txt"), "previous contents");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "removed contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        var before = await store.GetFileAsync(keep.FullPath, Token);
        var updated = WriteFile(keep.FullPath, "replacement contents with more words");
        File.Delete(removed.FullPath);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SynchronizeScanAsync(
            new ScanResult([updated], [], 0), [Root], _extractor,
            new CancelOnProgress(cancellation), cancellation.Token));

        Assert.Equal(before, await store.GetFileAsync(keep.FullPath, Token));
        Assert.NotNull(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Single(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("replacement", cancellationToken: Token));
        AssertTableCounts(store, 2);
    }

    [Fact]
    public async Task CleanupDatabaseFailureRollsBackUpdatesAndFtsDeletion()
    {
        var keep = WriteFile(Path.Combine(Root, "keep.txt"), "previous contents");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "removed contents");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Synchronize(store, Root);
        var before = await store.GetFileAsync(keep.FullPath, Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_cleanup BEFORE DELETE ON file_metadata BEGIN SELECT RAISE(ABORT, 'injected cleanup failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        WriteFile(keep.FullPath, "replacement contents with more words");
        var added = WriteFile(Path.Combine(Root, "added.txt"), "newly added contents");
        File.Delete(removed.FullPath);

        await Assert.ThrowsAsync<SqliteException>(() => Synchronize(store, Root));

        Assert.Equal(before, await store.GetFileAsync(keep.FullPath, Token));
        Assert.Null(await store.GetFileAsync(added.FullPath, Token));
        Assert.Single(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("removed", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("replacement", cancellationToken: Token));
        AssertTableCounts(store, 2);
    }

    private static void AssertTableCounts(FileMetadataStore store, long expected)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (var table in new[] { "file_metadata", "file_text", "file_search" })
        {
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(expected, command.ExecuteScalar());
        }
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<TextIndexProgress>
    {
        public void Report(TextIndexProgress value) => cancellation.Cancel();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
