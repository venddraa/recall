using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class NotebookTextExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-notebook-tests-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(1024, 1024 * 1024, 4096);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private ScannedFile WriteNotebook(object[] cells, string name = "notes.ipynb", int version = 4) =>
        WriteJson(JsonSerializer.Serialize(new { nbformat = version, nbformat_minor = 5, metadata = new { }, cells }), name);

    private ScannedFile WriteJson(string json, string name = "notes.ipynb")
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return Record(path);
    }

    private static object Cell(string type, object source, object[]? outputs = null) => new
    {
        cell_type = type,
        id = Guid.NewGuid().ToString("N"),
        metadata = new { },
        source,
        outputs = outputs ?? [],
        execution_count = (int?)null
    };

    private static ScannedFile Record(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    [Fact]
    public void ReadsMarkdownCodeAndRawWithStringFragmentsAndUnicode()
    {
        var file = WriteNotebook([
            Cell("markdown", new[] { "clear", " sky index\r\n" }),
            Cell("code", "print('angka 123')\rnext_line()"),
            Cell("raw", new[] { "café — ", "matahari ☀" })
        ], "NOTES.IPYNB");

        var result = _extractor.Extract(file, Token);

        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Equal("clear sky index\n\nprint('angka 123')\nnext_line()\ncafé — matahari ☀", result.Text);
    }

    [Fact]
    public async Task SourceAndPlainTextOutputsAreSearchableWithSnippets()
    {
        var file = WriteNotebook([
            Cell("markdown", "clear-sky index transformation"),
            Cell("code", "calculate()", [
                new { output_type = "stream", name = "stdout", text = new[] { "BAPP ", "calculation complete" } },
                new { output_type = "execute_result", execution_count = 1, data = new Dictionary<string, object>
                    { ["text/plain"] = "radiation result 123" }, metadata = new { } },
                new { output_type = "display_data", data = new Dictionary<string, object>
                    { ["text/plain"] = new[] { "remembered ", "table contents" } }, metadata = new { } }
            ])
        ]);
        var store = CreateStore();
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([file], _extractor, cancellationToken: Token);

        Assert.Equal(1, indexed.ExtractedFiles);
        foreach (var query in new[] { "clear sky index", "BAP", "radiation result 123", "remembered table contents" })
        {
            var result = Assert.Single(await store.SearchAsync(query, cancellationToken: Token));
            Assert.Equal(file.FullPath, result.FullPath);
            Assert.NotEmpty(result.Snippet);
        }
        Assert.Contains("radiation result 123", Assert.Single(await store.SearchAsync("radiation", cancellationToken: Token)).Snippet);
    }

    [Fact]
    public async Task MetadataImagesAttachmentsAndHtmlAreNotIndexed()
    {
        var file = WriteJson("""
            {
              "nbformat":4,"nbformat_minor":5,"metadata":{"title":"metadata-only-needle"},
              "cells":[
                {"cell_type":"markdown","source":"visible explanation","metadata":{},
                 "attachments":{"picture.png":{"image/png":"attachment-only-needle"}}},
                {"cell_type":"code","source":"calculate()","metadata":{"note":"cell-metadata-needle"},
                 "outputs":[{"output_type":"display_data","metadata":{},
                   "data":{"image/png":"binary-only-needle","text/html":"html-only-needle"}}]}
              ]
            }
            """);
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await store.IndexScanAsync([file], _extractor, cancellationToken: Token);

        Assert.Single(await store.SearchAsync("visible explanation", cancellationToken: Token));
        foreach (var query in new[] { "metadata", "attachment", "binary", "html" })
            Assert.Empty(await store.SearchAsync(query, cancellationToken: Token));
    }

    [Fact]
    public void Utf8BomAndEscapedTextAreDecoded()
    {
        var file = WriteJson("""
            {"nbformat":4,"cells":[{"cell_type":"markdown","source":"caf\u00e9\nquote: \"123\""}]}
            """);
        var bytes = File.ReadAllBytes(file.FullPath);
        File.WriteAllBytes(file.FullPath, [0xef, 0xbb, 0xbf, .. bytes]);

        Assert.Equal("café\nquote: \"123\"", _extractor.Extract(Record(file.FullPath), Token).Text);
    }

    [Theory]
    [InlineData("{\"nbformat\":4,\"cells\":[]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"markdown\",\"source\":[]}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"raw\",\"source\":\" \\n\\t\"}]}")]
    public void EmptyNotebookHasNoSearchableText(string json)
    {
        var result = _extractor.Extract(WriteJson(json), Token);

        Assert.Equal(TextExtractionStatus.NoSearchableText, result.Status);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"cells\":[]}")]
    [InlineData("{\"nbformat\":\"4\",\"cells\":[]}")]
    [InlineData("{\"nbformat\":4,\"cells\":{}}")]
    [InlineData("{\"nbformat\":4,\"cells\":[null]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"source\":\"missing type\"}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"code\",\"source\":[\"valid first\",42]}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"code\",\"source\":null}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"markdown\",\"source\":\"\\ud800\"}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"code\",\"source\":\"valid first\",\"outputs\":{}}]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[{\"cell_type\":\"code\",\"source\":\"valid first\",\"outputs\":[{\"output_type\":\"stream\",\"text\":false}]}]}")]
    public void MalformedNotebookReturnsNoPartialText(string json)
    {
        var result = _extractor.Extract(WriteJson(json), Token);

        Assert.Equal(TextExtractionStatus.ParserFailed, result.Status);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void OtherMajorVersionsAreUnsupported(int version)
    {
        var result = _extractor.Extract(WriteNotebook([Cell("markdown", "not indexed")], version: version), Token);

        Assert.Equal(TextExtractionStatus.Unsupported, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public void UnknownCellTypesAreSkippedWithoutDroppingKnownCells()
    {
        var file = WriteNotebook([Cell("future-cell", "future source"), Cell("markdown", "known source")]);

        Assert.Equal("known source", _extractor.Extract(file, Token).Text);
    }

    [Fact]
    public void EnforcesFileAndExtractedTextLimits()
    {
        var file = WriteNotebook([Cell("markdown", "remember 123")]);
        var fileLimit = new FileTextExtractor(1024, 1, 4096).Extract(file, Token);
        var textLimit = new FileTextExtractor(1024, 1024 * 1024, 8).Extract(file, Token);

        Assert.Equal(TextExtractionStatus.TooLarge, fileLimit.Status);
        Assert.Null(fileLimit.Text);
        Assert.Equal(TextExtractionStatus.TooLarge, textLimit.Status);
        Assert.Null(textLimit.Text);
    }

    [Fact]
    public void ExtractedTextLimitAlsoIncludesOutputs()
    {
        var file = WriteNotebook([Cell("code", "source", [new { output_type = "stream", text = "output contents" }])]);
        var result = new FileTextExtractor(1024, 1024 * 1024, 12).Extract(file, Token);

        Assert.Equal(TextExtractionStatus.TooLarge, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public void ExcessiveJsonNestingIsSkipped()
    {
        var nested = new string('[', 80) + "0" + new string(']', 80);
        var file = WriteJson("{\"nbformat\":4,\"cells\":[],\"metadata\":{\"nested\":" + nested + "}}");

        Assert.Equal(TextExtractionStatus.ParserFailed, _extractor.Extract(file, Token).Status);
    }

    [Fact]
    public async Task ReadingNeverExecutesCodeOrModifiesNotebook()
    {
        Directory.CreateDirectory(_directory);
        var marker = Path.Combine(_directory, "executed.txt");
        var file = WriteNotebook([Cell("code", $"from pathlib import Path\nPath({JsonSerializer.Serialize(marker)}).write_text('executed')")]);
        var before = await File.ReadAllBytesAsync(file.FullPath, Token);

        var result = _extractor.Extract(file, Token);

        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.False(File.Exists(marker));
        Assert.Equal(before, await File.ReadAllBytesAsync(file.FullPath, Token));
        Assert.Equal(file.ModifiedAtUtc, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public void DetectsChangedMissingAndLinkedNotebookPaths()
    {
        var file = WriteNotebook([Cell("markdown", "remember this")]);
        File.SetLastWriteTimeUtc(file.FullPath, file.ModifiedAtUtc.AddSeconds(10));
        Assert.Equal(TextExtractionStatus.Changed, _extractor.Extract(file, Token).Status);
        Assert.False(_extractor.CanReuse(file));
        var current = Record(file.FullPath);
        var link = Path.Combine(_directory, "linked.ipynb");
        File.CreateSymbolicLink(link, file.FullPath);
        Assert.Equal(TextExtractionStatus.SymbolicLink, _extractor.Extract(Record(link), Token).Status);
        Assert.False(_extractor.CanReuse(Record(link)));
        File.Delete(file.FullPath);
        Assert.Equal(TextExtractionStatus.Unavailable, _extractor.Extract(current, Token).Status);
    }

    [Fact]
    public async Task FifoDoesNotBlockIndexing()
    {
        if (!OperatingSystem.IsLinux())
            return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pipe.ipynb");
        Assert.Equal(0, MkFifo(path, 0x180));

        var result = await Task.Run(() => _extractor.Extract(Record(path), Token), Token)
            .WaitAsync(TimeSpan.FromSeconds(3), Token);

        Assert.Equal(TextExtractionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task FailedReindexClearsStaleContentAndContinuesWithOtherFiles()
    {
        var original = WriteNotebook([Cell("markdown", "previous contents")]);
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await store.IndexScanAsync([original], _extractor, cancellationToken: Token);
        var broken = WriteJson("invalid replacement json");
        var good = WriteNotebook([Cell("markdown", "unique recovered contents")], "good.ipynb");

        var indexed = await store.IndexScanAsync([broken, good], _extractor, cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.ParserFailed]);
        Assert.Equal(1, indexed.ExtractedFiles);
        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Equal(broken.FullPath, Assert.Single(await store.SearchAsync("notes", cancellationToken: Token)).FullPath);
        Assert.Contains("unique recovered contents", Assert.Single(await store.SearchAsync("unique recovered", cancellationToken: Token)).Snippet);
    }

    [Fact]
    public async Task UnchangedNotebookIsReusedAndChangedSourceIsReplaced()
    {
        var original = WriteNotebook([Cell("code", "previous source")]);
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await store.IndexScanAsync([original], _extractor, cancellationToken: Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE file_text SET processed_at_utc = '2000-01-01T00:00:00.0000000Z';";
            await command.ExecuteNonQueryAsync(Token);
        }
        await store.IndexScanAsync([original], _extractor, cancellationToken: Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT processed_at_utc FROM file_text;";
            Assert.Equal("2000-01-01T00:00:00.0000000Z", await command.ExecuteScalarAsync(Token));
        }
        var updated = WriteNotebook([Cell("code", "replacement source with new information")]);
        await store.IndexScanAsync([updated], _extractor, cancellationToken: Token);

        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Contains("replacement source", Assert.Single(await store.SearchAsync("replacement", cancellationToken: Token)).Snippet);
    }

    [Fact]
    public void CancellationIsPropagated()
    {
        var file = WriteNotebook([Cell("markdown", "remember this")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _extractor.Extract(file, cancellation.Token));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
