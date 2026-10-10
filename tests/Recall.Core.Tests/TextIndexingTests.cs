using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class TextIndexingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-index-tests-{Guid.NewGuid():N}");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private SqliteConnection Connect(FileMetadataStore store)
    {
        var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private async Task<ScannedFile> WriteFile(string name, byte[] bytes)
    {
        var folder = Path.Combine(_directory, "documents");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(path, bytes, Token);
        var info = new FileInfo(path);
        return new(path, name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private string? ReadField(FileMetadataStore store, string path, string column)
    {
        using var connection = Connect(store);
        using var command = connection.CreateCommand();
        // column is fixed by each test; document paths always use parameters.
        command.CommandText = $"SELECT {column} FROM file_text AS t JOIN file_metadata AS m ON m.id = t.file_id WHERE full_path = $path;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public async Task MigratesM5DatabaseAndPreservesMetadataIds()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        using (var connection = Connect(store))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE file_metadata (
                    id INTEGER PRIMARY KEY, full_path TEXT NOT NULL COLLATE BINARY UNIQUE,
                    filename TEXT NOT NULL, extension TEXT NOT NULL, size INTEGER NOT NULL CHECK(size >= 0),
                    modified_at_utc TEXT NOT NULL, scanned_at_utc TEXT NOT NULL);
                INSERT INTO file_metadata VALUES
                    (42, '/selected/original.txt', 'original.txt', '.txt', 3,
                    '2026-01-02T03:04:05.0000000Z', '2026-01-02T03:04:05.0000000Z');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        await store.InitializeAsync(Token);
        await store.InitializeAsync(Token);
        Assert.Equal(42, (await store.GetFileAsync("/selected/original.txt", Token))!.Id);
        Assert.Equal(1, await store.GetFileCountAsync(Token));
        Assert.Equal(0, await store.GetTextFileCountAsync(Token));
    }

    [Fact]
    public async Task SavesNormalizedTextAndSkipReasonsAcrossRestart()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var text = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("ingat 123\r\nclear-sky index"));
        var pdf = await WriteFile("paper.pdf", [1, 2]);
        var broken = await WriteFile("broken.txt", [0xc3, 0x28]);
        var result = await store.IndexScanAsync([text, pdf, broken], new PlainTextExtractor(1024), cancellationToken: Token);
        Assert.Equal(3, result.StoredFiles);
        Assert.Equal(1, result.ExtractedFiles);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.Unsupported]);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.InvalidEncoding]);
        Assert.Equal(1, await CreateStore().GetTextFileCountAsync(Token));
        Assert.Equal("ingat 123\nclear-sky index", ReadField(store, text.FullPath, "content"));
        Assert.Null(ReadField(store, broken.FullPath, "content"));
        Assert.Equal("InvalidEncoding", ReadField(store, broken.FullPath, "status"));
    }

    [Fact]
    public async Task UnchangedTextIsReusedAndChangedTextIsReplaced()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("before"));
        var extractor = new PlainTextExtractor(1024);
        await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        using (var connection = Connect(store))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE file_text SET processed_at_utc = '2000-01-01T00:00:00.0000000Z';";
            command.ExecuteNonQuery();
        }
        await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        Assert.Equal("2000-01-01T00:00:00.0000000Z", ReadField(store, file.FullPath, "processed_at_utc"));
        var updated = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("after - different"));
        await store.IndexScanAsync([updated], extractor, cancellationToken: Token);
        Assert.Equal("after - different", ReadField(store, file.FullPath, "content"));
        Assert.Equal(1, await store.GetFileCountAsync(Token));
    }

    [Fact]
    public async Task FailedReextractionClearsStaleTextAndContinuesWithOtherFiles()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("old text"));
        var extractor = new PlainTextExtractor(1024);
        await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        var broken = await WriteFile("notes.txt", [0xc3, 0x28]);
        var good = await WriteFile("good.md", Encoding.UTF8.GetBytes("new text"));
        var result = await store.IndexScanAsync([broken, good], extractor, cancellationToken: Token);
        Assert.Null(ReadField(store, file.FullPath, "content"));
        Assert.Equal("InvalidEncoding", ReadField(store, file.FullPath, "status"));
        Assert.Equal("new text", ReadField(store, good.FullPath, "content"));
        Assert.Equal(1, result.ExtractedFiles);
    }

    [Fact]
    public async Task SmallerLimitDoesNotReusePreviouslyExtractedOversizedText()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("12345"));
        await store.IndexScanAsync([file], new PlainTextExtractor(10), cancellationToken: Token);
        var result = await store.IndexScanAsync([file], new PlainTextExtractor(4), cancellationToken: Token);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.TooLarge]);
        Assert.Null(ReadField(store, file.FullPath, "content"));
    }

    [Fact]
    public async Task UnavailableFileIsNotReusedAndCanBeRetriedAfterRecovery()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("12345"));
        var extractor = new PlainTextExtractor(1024);
        await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        File.Delete(file.FullPath);
        var result = await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.Unavailable]);
        Assert.Null(ReadField(store, file.FullPath, "content"));
        var recovered = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("recovered"));
        await store.IndexScanAsync([recovered], extractor, cancellationToken: Token);
        Assert.Equal("recovered", ReadField(store, recovered.FullPath, "content"));
    }

    [Fact]
    public async Task CancellationRollsBackMetadataAndTextTogether()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("old text"));
        var extractor = new PlainTextExtractor(1024);
        await store.IndexScanAsync([file], extractor, cancellationToken: Token);
        var before = await store.GetFileAsync(file.FullPath, Token);
        var updated = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("new text - longer"));
        using var cancellation = new CancellationTokenSource();
        IEnumerable<ScannedFile> CancelAfterFirst()
        {
            yield return updated;
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IndexScanAsync(
            CancelAfterFirst(), extractor, cancellationToken: cancellation.Token));
        Assert.Equal(before, await store.GetFileAsync(file.FullPath, Token));
        Assert.Equal("old text", ReadField(store, file.FullPath, "content"));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackTheEntireTextBatch()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("new text"));
        await Assert.ThrowsAsync<SqliteException>(() => store.IndexScanAsync(
            [file, file with { FullPath = Path.Combine(_directory, "invalid.txt"), Size = -1 }],
            new PlainTextExtractor(1024), cancellationToken: Token));
        Assert.Equal(0, await store.GetFileCountAsync(Token));
        Assert.Equal(0, await store.GetTextFileCountAsync(Token));
    }

    [Fact]
    public async Task MetadataOnlyUpdateInvalidatesTextWhenFileChanges()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", Encoding.UTF8.GetBytes("old text"));
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);
        await store.SaveScanAsync([file with { Size = 999 }], Token);
        Assert.Equal(0, await store.GetTextFileCountAsync(Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
