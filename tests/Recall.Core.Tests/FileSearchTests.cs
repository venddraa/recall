using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Search;
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
    public async Task ThreeCharacterPrefixFindsLongerFilenameAndContentWords()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var filename = await WriteFile("BAPP-report.pdf", "unsupported content");
        var content = await WriteFile("notes.txt", "Dokumen BAPP sudah ditandatangani");
        var unrelated = await WriteFile("other.txt", "ABAPP saja");
        await store.IndexScanAsync([filename, content, unrelated], new PlainTextExtractor(1024), cancellationToken: Token);

        var results = await store.SearchAsync("BAP", cancellationToken: Token);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.FullPath == filename.FullPath);
        Assert.Contains(results, result => result.FullPath == content.FullPath);
        Assert.Empty(await store.SearchAsync("APP", cancellationToken: Token));
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

    [Theory]
    [InlineData("The CLEAR-sky index transformation is used.", "clear sky index", "CLEAR|sky|index")]
    [InlineData("Dokumen BAPP dan ABAPP selesai.", "BAP", "BAPP")]
    [InlineData("Café déjà vu", "CAFE DEJA", "Café|déjà")]
    [InlineData("Cafe\u0301 déjà vu", "cafe", "Cafe\u0301")]
    [InlineData("an annual report", "an", "an")]
    [InlineData("😀 catatan BAPP terbaru", "bap", "BAPP")]
    [InlineData("alpha, alpha; ALPHA!", "alpha alpha", "alpha|alpha|ALPHA")]
    [InlineData("baris pertama\nremembered\tphrase\nbaris terakhir", "remembered phrase", "remembered|phrase")]
    public async Task SnippetHighlightsFollowFtsMatchesAndPreserveOriginalText(
        string content, string query, string expectedMatches)
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", content);
        await store.IndexScanAsync([file], new PlainTextExtractor(4096), cancellationToken: Token);

        var result = Assert.Single(await store.SearchAsync(query, cancellationToken: Token));

        var parts = Assert.IsAssignableFrom<IReadOnlyList<SearchSnippetPart>>(result.SnippetParts);
        Assert.Equal(content, result.Snippet);
        Assert.Equal(content, string.Concat(parts.Select(part => part.Text)));
        Assert.Equal(expectedMatches, string.Join("|", parts.Where(part => part.IsMatch).Select(part => part.Text)));
        Assert.DoesNotContain(parts, part => string.IsNullOrEmpty(part.Text));
    }

    [Fact]
    public async Task SnippetPreservesLiteralMarkupAndOnlyMarksActualMatches()
    {
        const string content = "<b>unrelated</b> [match] & <Run Foreground='Red'>needle</Run> </match>";
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", content);
        await store.IndexScanAsync([file], new PlainTextExtractor(4096), cancellationToken: Token);

        var result = Assert.Single(await store.SearchAsync("needle", cancellationToken: Token));

        var parts = Assert.IsAssignableFrom<IReadOnlyList<SearchSnippetPart>>(result.SnippetParts);
        Assert.Equal(content, result.Snippet);
        Assert.Equal(content, string.Concat(parts.Select(part => part.Text)));
        Assert.Equal("needle", Assert.Single(parts, part => part.IsMatch).Text);
    }

    [Fact]
    public async Task FilenameOnlyMatchDoesNotHighlightUnrelatedContent()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("needle.txt", "No matching content here.");
        await store.IndexScanAsync([file], new PlainTextExtractor(4096), cancellationToken: Token);

        var result = Assert.Single(await store.SearchAsync("needle", cancellationToken: Token));

        Assert.Equal("No matching content here.", result.Snippet);
        Assert.False(Assert.Single(result.SnippetParts!).IsMatch);
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

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(205)]
    public async Task PagesPreserveRankingWithoutDuplicatesAndHaveAccurateLookahead(int fileCount)
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var files = new List<ScannedFile>();
        for (var index = 0; index < fileCount; index++)
            files.Add(await WriteFile($"set-{index:D3}/report.txt",
                string.Join(" ", Enumerable.Repeat("shared", 1 + index % 4)) + " other text"));
        await store.IndexScanAsync(files, new PlainTextExtractor(1024), cancellationToken: Token);
        var expected = await store.SearchAsync("shared", 500, Token);
        var actualPaths = new List<string>();

        for (var offset = 0; offset < fileCount; offset += 100)
        {
            var page = await store.SearchAsync("shared", 101, Token, offset: offset);
            Assert.Equal(Math.Min(101, fileCount - offset), page.Count);
            actualPaths.AddRange(page.Take(100).Select(result => result.FullPath));
        }

        Assert.Equal(expected.Select(result => result.FullPath), actualPaths);
        Assert.Equal(fileCount, actualPaths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PageOffsetIsAppliedAfterFilteringSelectedFolders()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var a = await WriteFile("Selected/a.txt", "shared content");
        var b = await WriteFile("Selected/nested/b.txt", "shared content");
        var c = await WriteFile("Selected/c.txt", "shared content");
        var sibling = await WriteFile("SelectedOther/first.txt", "shared shared shared content");
        await store.IndexScanAsync([a, b, c, sibling], new PlainTextExtractor(1024), cancellationToken: Token);
        string[] roots = [Path.GetDirectoryName(a.FullPath)!];
        var expected = await store.SearchFoldersAsync("shared", roots, cancellationToken: Token);

        var page = await store.SearchFoldersAsync("shared", roots, 1, Token, offset: 1);

        Assert.Equal(expected[1].FullPath, Assert.Single(page).FullPath);
        Assert.DoesNotContain(page, result => result.FullPath == sibling.FullPath);
    }

    [Fact]
    public async Task PagePastTheLastResultReturnsAnEmptyList()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", "shared content");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);

        Assert.Empty(await store.SearchAsync("shared", cancellationToken: Token, offset: 1));
        Assert.Empty(await store.SearchFoldersAsync("shared", [Path.GetDirectoryName(file.FullPath)!],
            cancellationToken: Token, offset: int.MaxValue));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public async Task NegativePageOffsetsAreRejectedEvenForEmptyQueriesAndFolders(int offset)
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SearchAsync("shared", cancellationToken: Token, offset: offset));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SearchAsync("", cancellationToken: Token, offset: offset));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SearchFoldersAsync("shared", [], cancellationToken: Token, offset: offset));
    }

    [Fact]
    public async Task PageRequestsHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateStore().SearchAsync("shared", cancellationToken: cancellation.Token, offset: 100));
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
        Assert.Equal(TextExtractionStatus.InvalidEncoding,
            Assert.Single(await store.SearchAsync("notes", cancellationToken: Token)).ExtractionStatus);
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

    [Fact]
    public async Task SnippetShowsMatchBeyondTheBeginningWithoutReturningWholeDocument()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("long.txt", string.Join(" ", Enumerable.Repeat("intro", 200))
            + " remembered phrase " + string.Join(" ", Enumerable.Repeat("outro", 200)));
        await store.IndexScanAsync([file], new PlainTextExtractor(10000), cancellationToken: Token);
        var result = Assert.Single(await store.SearchAsync("remembered phrase", cancellationToken: Token));
        Assert.Contains("remembered phrase", result.Snippet);
        Assert.True(result.Snippet.Length < 500);
        Assert.Contains("…", result.Snippet);
        var parts = Assert.IsAssignableFrom<IReadOnlyList<SearchSnippetPart>>(result.SnippetParts);
        Assert.Equal(result.Snippet, string.Concat(parts.Select(part => part.Text)));
        Assert.Equal("remembered|phrase",
            string.Join("|", parts.Where(part => part.IsMatch).Select(part => part.Text)));
    }

    [Fact]
    public async Task UnsupportedFilenameMatchHasNoContentSnippet()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("unique-report.pdf", "private unsupported content");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);
        var result = Assert.Single(await store.SearchAsync("unique report", cancellationToken: Token));
        Assert.Equal("", result.Snippet);
        Assert.Equal(TextExtractionStatus.Unsupported, result.ExtractionStatus);
        Assert.Empty(result.SnippetParts!);
    }

    [Fact]
    public async Task FolderFilterIncludesDescendantsButExcludesSiblingPrefixesAndCaseVariants()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var selected = await WriteFile("Chosen/a.txt", "remembered phrase");
        var nested = await WriteFile("Chosen/nested/b.txt", "remembered phrase");
        var sibling = await WriteFile("ChosenOther/c.txt", "remembered phrase");
        var differentCase = await WriteFile("chosen/d.txt", "remembered phrase");
        await store.IndexScanAsync([selected, nested, sibling, differentCase], new PlainTextExtractor(1024), cancellationToken: Token);
        var root = Path.GetDirectoryName(selected.FullPath)!;
        var results = await store.SearchFoldersAsync("remembered phrase", [root, root + "/"], cancellationToken: Token);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.FullPath == selected.FullPath);
        Assert.Contains(results, result => result.FullPath == nested.FullPath);
        Assert.Empty(await store.SearchFoldersAsync("remembered phrase", [], cancellationToken: Token));
    }

    [Fact]
    public async Task FolderFilterUsesLiteralPathsAndCanSearchMultipleRoots()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var first = await WriteFile("literal_%'/a.txt", "123 remembered");
        var other = await WriteFile("literal_AB'/b.txt", "123 remembered");
        var second = await WriteFile("second/c.txt", "123 remembered");
        await store.IndexScanAsync([first, other, second], new PlainTextExtractor(1024), cancellationToken: Token);
        var results = await store.SearchFoldersAsync("123 remembered",
            [Path.GetDirectoryName(first.FullPath)!, Path.GetDirectoryName(second.FullPath)!], cancellationToken: Token);
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, result => result.FullPath == other.FullPath);
        Assert.Equal(3, (await store.SearchFoldersAsync("123 remembered", ["/"], cancellationToken: Token)).Count);
    }

    [Fact]
    public async Task SearchWorksFromSavedIndexWhenOriginalFileIsUnavailable()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("notes.txt", "remembered content 123");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);
        File.Delete(file.FullPath);
        var result = Assert.Single(await store.SearchFoldersAsync("remembered content",
            [Path.GetDirectoryName(file.FullPath)!], cancellationToken: Token));
        Assert.Contains("remembered content 123", result.Snippet);
        Assert.Equal(TextExtractionStatus.Extracted, result.ExtractionStatus);
    }

    [Fact]
    public async Task MetadataOnlyResultRemainsSearchableWithoutAnExtractionStatus()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("pending-report.txt", "content not indexed yet");
        await store.SaveScanAsync([file], Token);

        var result = Assert.Single(await store.SearchAsync("pending report", cancellationToken: Token));

        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Null(result.ExtractionStatus);
        Assert.Empty(result.Snippet);
    }

    [Fact]
    public async Task EachResultKeepsItsStoredStatusAfterRestartWithoutReadingOriginalFiles()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var unsupported = await WriteFile("report-unsupported.zip", "unsupported");
        var large = await WriteFile("report-large.txt", new string('x', 64));
        var ready = await WriteFile("report-ready.txt", "remembered");
        await store.IndexScanAsync([unsupported, large, ready], new PlainTextExtractor(16), cancellationToken: Token);
        File.Delete(unsupported.FullPath);
        File.Delete(large.FullPath);
        File.Delete(ready.FullPath);

        var results = await CreateStore().SearchFoldersAsync("report", [Path.GetDirectoryName(ready.FullPath)!],
            cancellationToken: Token);
        var byPath = results.ToDictionary(result => result.FullPath);

        Assert.Equal(3, results.Count);
        Assert.Equal(TextExtractionStatus.Unsupported, byPath[unsupported.FullPath].ExtractionStatus);
        Assert.Equal(TextExtractionStatus.TooLarge, byPath[large.FullPath].ExtractionStatus);
        Assert.Equal(TextExtractionStatus.Extracted, byPath[ready.FullPath].ExtractionStatus);
        Assert.Equal("remembered", byPath[ready.FullPath].Snippet);
    }

    [Theory]
    [InlineData("FutureStatus")]
    [InlineData("999")]
    [InlineData("0")]
    public async Task UnknownStoredStatusDoesNotHideTheFilenameMatch(string savedStatus)
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("unknown-report.zip", "unsupported");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE file_text SET status = $status;";
            command.Parameters.AddWithValue("$status", savedStatus);
            await command.ExecuteNonQueryAsync(Token);
        }

        var result = Assert.Single(await store.SearchAsync("unknown report", cancellationToken: Token));

        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Null(result.ExtractionStatus);
        Assert.Empty(result.Snippet);
    }

    [Fact]
    public async Task SuccessfulRetryReplacesThePreviousFailureStatusAndRestoresPreview()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("recoverable.txt", "remembered text");
        await store.IndexScanAsync([file], new PlainTextExtractor(4), cancellationToken: Token);
        Assert.Equal(TextExtractionStatus.TooLarge,
            Assert.Single(await store.SearchAsync("recoverable", cancellationToken: Token)).ExtractionStatus);

        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);

        var result = Assert.Single(await store.SearchAsync("remembered", cancellationToken: Token));
        Assert.Equal(TextExtractionStatus.Extracted, result.ExtractionStatus);
        Assert.Contains("remembered text", result.Snippet);
    }

    [Fact]
    public async Task EmptyTextFileIsDistinguishedFromUnsupportedOrUnprocessedFiles()
    {
        var store = CreateStore();
        await store.InitializeAsync(Token);
        var file = await WriteFile("empty-report.txt", "");
        await store.IndexScanAsync([file], new PlainTextExtractor(1024), cancellationToken: Token);

        var result = Assert.Single(await store.SearchAsync("empty report", cancellationToken: Token));

        Assert.Equal(TextExtractionStatus.Extracted, result.ExtractionStatus);
        Assert.Empty(result.Snippet);
    }

    [Fact]
    public async Task EmptyQueryStillHonorsCancellation()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync("", cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
