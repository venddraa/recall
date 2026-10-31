using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Recall.Core.Tests;

public sealed class PdfTextExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-pdf-tests-{Guid.NewGuid():N}");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private ScannedFile WritePdf(string name, string? text) => WritePdfPages(name, [text]);

    private ScannedFile WritePdfPages(string name, params string?[] pageTexts)
    {
        Directory.CreateDirectory(_directory);
        var builder = new PdfDocumentBuilder();
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text is not null)
            {
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                page.AddText(text, 12, new PdfPoint(40, 750), font);
            }
        }
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, builder.Build());
        return Record(path);
    }

    private static ScannedFile Record(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsSinglePageTextExactlyAtCharacterLimit(bool leadingBlankPage)
    {
        const string text = "clear sky index";
        var file = leadingBlankPage
            ? WritePdfPages("boundary.pdf", null, text)
            : WritePdf("boundary.pdf", text);
        var extractor = new FileTextExtractor(1024, 1024 * 1024, text.Length);

        var result = extractor.Extract(file, Token);

        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Equal(text, result.Text);
    }

    [Theory]
    [InlineData(1, TextExtractionStatus.TooLarge)]
    [InlineData(0, TextExtractionStatus.Extracted)]
    [InlineData(-1, TextExtractionStatus.Extracted)]
    public void CountsOnlySeparatorsThatAreWrittenBetweenPages(int charactersOverLimit, TextExtractionStatus expectedStatus)
    {
        const string expected = "clear sky\nindex";
        var file = WritePdfPages("pages.pdf", "clear sky", "index");
        var extractor = new FileTextExtractor(1024, 1024 * 1024, expected.Length - charactersOverLimit);

        var result = extractor.Extract(file, Token);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedStatus == TextExtractionStatus.Extracted ? expected : null, result.Text);
    }

    [Fact]
    public async Task CharacterLimitKeepsFilenameAndOtherFilesSearchableAndRecoversOnRetry()
    {
        const string content = "uniqueoverflow";
        var oversized = WritePdf("oversized-report.pdf", content);
        var good = WritePdf("good.pdf", "retained");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([oversized, good],
            new FileTextExtractor(1024, 1024 * 1024, content.Length - 1), cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.TooLarge]);
        Assert.Equal(1, indexed.ExtractedFiles);
        var result = Assert.Single(await store.SearchAsync("oversized report", cancellationToken: Token));
        Assert.Equal(oversized.FullPath, result.FullPath);
        Assert.Equal(TextExtractionStatus.TooLarge, result.ExtractionStatus);
        Assert.Empty(result.Snippet);
        Assert.Empty(await store.SearchAsync("uniqueoverflow", cancellationToken: Token));
        Assert.Equal(good.FullPath, Assert.Single(await store.SearchAsync("retained", cancellationToken: Token)).FullPath);

        // The same unchanged PDF can be retried at the exact boundary without
        // forcing extraction or retaining a stale TooLarge result.
        var retried = await store.IndexScanAsync([oversized],
            new FileTextExtractor(1024, 1024 * 1024, content.Length), cancellationToken: Token);

        Assert.Equal(1, retried.ExtractedFiles);
        var recovered = Assert.Single(await store.SearchAsync("uniqueoverflow", cancellationToken: Token));
        Assert.Equal(oversized.FullPath, recovered.FullPath);
        Assert.Equal(TextExtractionStatus.Extracted, recovered.ExtractionStatus);
        Assert.Equal(content, recovered.Snippet);
        Assert.Equal(oversized, Record(oversized.FullPath));
    }

    [Fact]
    public async Task TextLayerIsSearchableAndAppearsInSnippet()
    {
        var file = WritePdf("report.pdf", "clear sky index transformation");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([file], new FileTextExtractor(1024, 1024 * 1024, 1024),
            cancellationToken: Token);
        var result = Assert.Single(await store.SearchAsync("clear sky index", cancellationToken: Token));

        Assert.Equal(1, indexed.ExtractedFiles);
        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Contains("clear sky index", result.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TextExtractionStatus.Extracted, result.ExtractionStatus);
    }

    [Fact]
    public async Task PdfWithoutTextRemainsSearchableByFilename()
    {
        var file = WritePdf("scanned-report.pdf", null);
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([file], new FileTextExtractor(1024, 1024 * 1024, 1024),
            cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.NoSearchableText]);
        var result = Assert.Single(await store.SearchAsync("scanned report", cancellationToken: Token));
        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Equal(TextExtractionStatus.NoSearchableText, result.ExtractionStatus);
        Assert.Empty(result.Snippet);
        Assert.Empty(await store.SearchAsync("clear sky", cancellationToken: Token));
    }

    [Fact]
    public async Task BrokenPdfDoesNotStopOtherFiles()
    {
        var brokenPath = Path.Combine(_directory, "broken.pdf");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(brokenPath, "not a pdf", Token);
        var good = WritePdf("good.pdf", "remember this phrase");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([Record(brokenPath), good],
            new FileTextExtractor(1024, 1024 * 1024, 1024), cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.ParserFailed]);
        var failed = Assert.Single(await store.SearchAsync("broken", cancellationToken: Token));
        Assert.Equal(TextExtractionStatus.ParserFailed, failed.ExtractionStatus);
        Assert.Empty(failed.Snippet);
        Assert.Equal(good.FullPath, Assert.Single(await store.SearchAsync("remember this", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task OversizedPdfKeepsFilenameSearchable()
    {
        var file = WritePdf("large-report.pdf", "searchable text");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([file], new FileTextExtractor(1024, 1, 1024),
            cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.TooLarge]);
        var result = Assert.Single(await store.SearchAsync("large report", cancellationToken: Token));
        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Equal(TextExtractionStatus.TooLarge, result.ExtractionStatus);
        Assert.Empty(await store.SearchAsync("searchable text", cancellationToken: Token));
    }

    [Fact]
    public async Task ChangedPdfReplacesOldIndexedText()
    {
        var original = WritePdf("notes.pdf", "previous phrase");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);
        var extractor = new FileTextExtractor(1024, 1024 * 1024, 1024);
        await store.IndexScanAsync([original], extractor, cancellationToken: Token);

        var updated = WritePdf("notes.pdf", "replacement phrase with more words");
        await store.IndexScanAsync([updated], extractor, cancellationToken: Token);

        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Equal(updated.FullPath, Assert.Single(await store.SearchAsync("replacement", cancellationToken: Token)).FullPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
