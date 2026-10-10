using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class FileSearchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-search-tests-{Guid.NewGuid():N}");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private async Task<ScannedFile> WriteFile(string name, string text)
    {
        var folder = Path.Combine(_directory, "documents");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), Token);
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public async Task FindsAllWordsInContentRegardlessOfCaseOrPunctuation()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var matching = await WriteFile("paper.txt", "The CLEAR-sky index transformation is used.");
        var partial = await WriteFile("partial.txt", "clear sky without the required final word");
        await store.IndexScanAsync([matching, partial], new PlainTextExtractor(1024), cancellationToken: Token);

        var results = await store.SearchAsync("clear, SKY index", cancellationToken: Token);

        var result = Assert.Single(results);
        Assert.Equal(matching.FullPath, result.FullPath);
        Assert.Equal(DateTimeKind.Utc, result.ModifiedAtUtc.Kind);
    }

    [Fact]
    public async Task FindsFilenameEvenWhenContentIsUnsupported()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("annual-report-2026.pdf", "not extracted");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Equal(file.FullPath, Assert.Single(await store.SearchAsync("annual report 2026", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task FilenameMatchRanksBeforeContentOnlyMatch()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var filenameMatch = await WriteFile("clear-sky-index.txt", "unrelated");
        var contentMatch = await WriteFile("notes.txt", "clear sky index");
        await store.IndexScanAsync([contentMatch, filenameMatch], new PlainTextExtractor(1024), cancellationToken: Token);

        var results = await store.SearchAsync("clear sky index", cancellationToken: Token);

        Assert.Equal(2, results.Count);
        Assert.Equal(filenameMatch.FullPath, results[0].FullPath);
        Assert.True(results[0].Rank < results[1].Rank);
    }

    [Fact]
    public async Task SearchIsCaseAndDiacriticInsensitive()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("catatan.txt", "Café déjà vu");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Single(await store.SearchAsync("CAFE DEJA", cancellationToken: Token));
    }

    [Fact]
    public async Task QueryOperatorsAndQuotesAreTreatedAsLiteralWords()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var expected = await WriteFile("literal.txt", "one OR two quote");
        await store.IndexScanAsync([expected], new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Equal(expected.FullPath, Assert.Single(await store.SearchAsync("one OR two \"quote\"", cancellationToken: Token)).FullPath);
        Assert.Empty(await store.SearchAsync("***", cancellationToken: Token));
    }

    [Fact]
    public async Task LimitIsAppliedAndValidated()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var files = new List<ScannedFile>();
        for (var index = 0; index < 5; index++)
            files.Add(await WriteFile($"file-{index}.txt", "shared keyword"));
        await store.IndexScanAsync(files, new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Equal(2, (await store.SearchAsync("shared", 2, Token)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchAsync("shared", 0, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchAsync("shared", 501, Token));
    }

    [Fact]
    public async Task ChangedAndInvalidContentIsRemovedFromSearchIndex()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var original = await WriteFile("notes.txt", "obsolete phrase");
        await store.IndexScanAsync([original], new PlainTextExtractor(1024), cancellationToken: Token);
        Assert.Single(await store.SearchAsync("obsolete", cancellationToken: Token));

        var brokenPath = original.FullPath;
        await File.WriteAllBytesAsync(brokenPath, [0xc3, 0x28], Token);
        var info = new FileInfo(brokenPath);
        var broken = new ScannedFile(brokenPath, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
        await store.IndexScanAsync([broken], new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Empty(await store.SearchAsync("obsolete", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("notes", cancellationToken: Token));
    }

    [Fact]
    public async Task MetadataOnlyUpdateKeepsFilenameIndexSynchronized()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("before.txt", "neutral content");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);
        await store.SaveScanAsync([file with { Filename = "after.txt" }], Token);

        Assert.Single(await store.SearchAsync("after", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("before", cancellationToken: Token));
    }

    [Fact]
    public async Task MigrationFromM6BuildsSearchIndexForExistingRows()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE file_metadata (
                    id INTEGER PRIMARY KEY, full_path TEXT NOT NULL COLLATE BINARY UNIQUE,
                    filename TEXT NOT NULL, extension TEXT NOT NULL, size INTEGER NOT NULL CHECK(size >= 0),
                    modified_at_utc TEXT NOT NULL, scanned_at_utc TEXT NOT NULL);
                CREATE TABLE file_text (
                    file_id INTEGER PRIMARY KEY REFERENCES file_metadata(id) ON DELETE CASCADE,
                    status TEXT NOT NULL, content TEXT, processed_at_utc TEXT NOT NULL,
                    CHECK ((status = 'Extracted' AND content IS NOT NULL)
                        OR (status <> 'Extracted' AND content IS NULL)));
                INSERT INTO file_metadata VALUES
                    (7, '/selected/legacy.txt', 'legacy.txt', '.txt', 12,
                    '2026-01-02T03:04:05.0000000Z', '2026-01-02T03:04:05.0000000Z');
                INSERT INTO file_text VALUES
                    (7, 'Extracted', 'remember legacy content', '2026-01-02T03:04:05.0000000Z');
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        await store.InitializeAsync(Token);
        var result = Assert.Single(await store.SearchAsync("remember content", cancellationToken: Token));
        Assert.Equal(7, result.Id);
    }

    [Fact]
    public async Task FailedBatchRollsBackSearchIndexWithMetadataAndText()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var original = await WriteFile("notes.txt", "original searchable phrase");
        var extractor = new PlainTextExtractor(1024);
        await store.IndexScanAsync([original], extractor, cancellationToken: Token);
        var updated = await WriteFile("notes.txt", "replacement searchable phrase");

        await Assert.ThrowsAsync<SqliteException>(() => store.IndexScanAsync(
            [updated, updated with { FullPath = Path.Combine(_directory, "invalid.txt"), Size = -1 }],
            extractor, cancellationToken: Token));

        Assert.Single(await store.SearchAsync("original", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("replacement", cancellationToken: Token));
    }

    [Fact]
    public async Task CancellationAndNullQueryAreHandled()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync("anything", cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SearchAsync(null!, cancellationToken: Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
