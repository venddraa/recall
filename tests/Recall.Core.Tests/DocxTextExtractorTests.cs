using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class DocxTextExtractorTests : IDisposable
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-docx-tests-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(1024, 1024 * 1024, 4096);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private ScannedFile WriteDocument(string body, string name = "notes.docx", string wordNamespace = WordNamespace) =>
        WriteArchive($"<w:document xmlns:w=\"{wordNamespace}\"><w:body>{body}</w:body></w:document>", name);

    private ScannedFile WriteArchive(string xml, string name = "notes.docx", string partName = "word/document.xml")
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            WriteEntry("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            WriteEntry(partName, xml);

            void WriteEntry(string entryName, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return Record(path);
    }

    private static ScannedFile Record(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    [Theory]
    [InlineData("notes.docx", WordNamespace)]
    [InlineData("notes.DOCX", "http://purl.oclc.org/ooxml/wordprocessingml/main")]
    public void ReadsSplitRunsUnicodeAndXmlEntities(string name, string wordNamespace)
    {
        var file = WriteDocument("""
            <w:p><w:r><w:t>clear</w:t></w:r><w:r><w:t xml:space="preserve"> sky index</w:t></w:r></w:p>
            <w:p><w:r><w:t>café &amp; matahari ☀</w:t></w:r></w:p>
            """, name, wordNamespace);

        var result = _extractor.Extract(file, Token);

        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Equal("clear sky index\ncafé & matahari ☀\n", result.Text);
    }

    [Fact]
    public void PreservesTabsLineBreaksAndTableParagraphBoundaries()
    {
        var file = WriteDocument("""
            <w:p><w:r><w:t>first</w:t><w:tab/><w:t>second</w:t><w:br/><w:t>third</w:t><w:cr/><w:t>fourth</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>left cell</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:r><w:t>right cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);

        var result = _extractor.Extract(file, Token);

        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.StartsWith("first\tsecond\nthird\nfourth\n", result.Text!);
        Assert.Contains("left cell\n", result.Text);
        Assert.Contains("right cell\n", result.Text);
        Assert.DoesNotContain("left cellright cell", result.Text);
    }

    [Fact]
    public void DoesNotIndexDeletedMovedOrFieldInstructionText()
    {
        var file = WriteDocument("""
            <w:p><w:del><w:r><w:t>deleted secret</w:t><w:delText>removed</w:delText></w:r></w:del>
            <w:moveFrom><w:r><w:t>old location</w:t></w:r></w:moveFrom>
            <w:r><w:instrText>HYPERLINK private-address</w:instrText><w:t>visible result</w:t></w:r></w:p>
            """);

        Assert.Equal("visible result\n", _extractor.Extract(file, Token).Text);
    }

    [Fact]
    public void TextBoxCompatibilityFallbackDoesNotDuplicateText()
    {
        var file = WriteDocument("""
            <w:p xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
              <mc:AlternateContent><mc:Choice Requires="wps"><w:r><w:t>text box</w:t></w:r></mc:Choice>
                <mc:Fallback><w:r><w:t>text box</w:t></w:r></mc:Fallback></mc:AlternateContent>
            </w:p>
            """);

        Assert.Equal("text box\n", _extractor.Extract(file, Token).Text);
    }

    [Fact]
    public void NonBreakingHyphenKeepsWordBoundaries()
    {
        var file = WriteDocument("<w:p><w:r><w:t>clear</w:t><w:noBreakHyphen/><w:t>sky index</w:t></w:r></w:p>");

        Assert.Equal("clear-sky index\n", _extractor.Extract(file, Token).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<w:p><w:r><w:t> </w:t></w:r></w:p>")]
    public void EmptyBodyHasNoSearchableText(string body)
    {
        var result = _extractor.Extract(WriteDocument(body), Token);

        Assert.Equal(TextExtractionStatus.NoSearchableText, result.Status);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body>")]
    [InlineData("<document><body>not WordprocessingML</body></document>")]
    [InlineData("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'/>")]
    [InlineData("<!DOCTYPE document [<!ENTITY external SYSTEM 'file:///not-selected/private.txt'>]><w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>&external;</w:t></w:r></w:p></w:body></w:document>")]
    public void MalformedOrUnsafeXmlIsSkipped(string xml)
    {
        var result = _extractor.Extract(WriteArchive(xml), Token);

        Assert.Equal(TextExtractionStatus.ParserFailed, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public void MissingDocumentPartIsSkipped()
    {
        var file = WriteArchive("<other/>", partName: "other.xml");

        Assert.Equal(TextExtractionStatus.ParserFailed, _extractor.Extract(file, Token).Status);
    }

    [Fact]
    public void EnforcesFileAndTextLimitsWithoutReturningPartialText()
    {
        var file = WriteDocument("<w:p><w:r><w:t>remember 123</w:t></w:r></w:p>");
        var fileLimit = new FileTextExtractor(1024, 1, 4096).Extract(file, Token);
        var textLimit = new FileTextExtractor(1024, 1024 * 1024, 8).Extract(file, Token);

        Assert.Equal(TextExtractionStatus.TooLarge, fileLimit.Status);
        Assert.Null(fileLimit.Text);
        Assert.Equal(TextExtractionStatus.TooLarge, textLimit.Status);
        Assert.Null(textLimit.Text);
    }

    [Fact]
    public void HighlyCompressedOversizedXmlIsSkipped()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "compressed.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("word/document.xml", CompressionLevel.Fastest).Open();
            var buffer = new byte[16 * 1024];
            Array.Fill(buffer, (byte)' ');
            for (var written = 0; written <= 40 * 1024 * 1024; written += buffer.Length)
                stream.Write(buffer);
        }
        var file = Record(path);
        Assert.True(file.Size < 1024 * 1024);

        Assert.Equal(TextExtractionStatus.TooLarge, _extractor.Extract(file, Token).Status);
    }

    [Fact]
    public void DetectsMissingOrChangedFilesAndRejectsSymlinks()
    {
        var file = WriteDocument("<w:p><w:r><w:t>remember 123</w:t></w:r></w:p>");
        File.SetLastWriteTimeUtc(file.FullPath, file.ModifiedAtUtc.AddSeconds(10));
        Assert.Equal(TextExtractionStatus.Changed, _extractor.Extract(file, Token).Status);
        var current = Record(file.FullPath);
        var link = Path.Combine(_directory, "link.docx");
        File.CreateSymbolicLink(link, current.FullPath);
        Assert.Equal(TextExtractionStatus.SymbolicLink, _extractor.Extract(Record(link), Token).Status);
        File.Delete(file.FullPath);
        Assert.Equal(TextExtractionStatus.Unavailable, _extractor.Extract(current, Token).Status);
        Assert.False(_extractor.CanReuse(current));
    }

    [Fact]
    public async Task FifoDoesNotBlockIndexing()
    {
        if (!OperatingSystem.IsLinux())
            return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pipe.docx");
        Assert.Equal(0, MkFifo(path, 0x180));

        var result = await Task.Run(() => _extractor.Extract(Record(path), Token), Token)
            .WaitAsync(TimeSpan.FromSeconds(3), Token);

        Assert.Equal(TextExtractionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ReadingPreservesOriginalBytesAndTimestamp()
    {
        var file = WriteDocument("<w:p><w:r><w:t>remember 123</w:t></w:r></w:p>");
        var before = await File.ReadAllBytesAsync(file.FullPath, Token);

        _extractor.Extract(file, Token);

        Assert.Equal(before, await File.ReadAllBytesAsync(file.FullPath, Token));
        Assert.Equal(file.ModifiedAtUtc, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public async Task IndexesContentAndTableTextWithSearchSnippets()
    {
        var file = WriteDocument("""
            <w:p><w:r><w:t>remember</w:t></w:r><w:r><w:t xml:space="preserve"> this 123</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>BAPP table contents</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([file], _extractor, cancellationToken: Token);
        var result = Assert.Single(await store.SearchAsync("remember this 123", cancellationToken: Token));

        Assert.Equal(1, indexed.ExtractedFiles);
        Assert.Equal(file.FullPath, result.FullPath);
        Assert.Contains("remember this 123", result.Snippet);
        Assert.Equal(file.FullPath, Assert.Single(await store.SearchAsync("BAP", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task CorruptAndEmptyDocxKeepFilenameSearchWhileOtherFilesContinue()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "broken-report.docx");
        await File.WriteAllTextAsync(path, "not a zip file", Token);
        var empty = WriteDocument("", "empty-report.docx");
        var good = WriteDocument("<w:p><w:r><w:t>unique contents</w:t></w:r></w:p>", "good.docx");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
        await store.InitializeAsync(Token);

        var indexed = await store.IndexScanAsync([Record(path), empty, good], _extractor, cancellationToken: Token);

        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.ParserFailed]);
        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.NoSearchableText]);
        Assert.Equal(1, indexed.ExtractedFiles);
        Assert.Equal(path, Assert.Single(await store.SearchAsync("broken report", cancellationToken: Token)).FullPath);
        Assert.Equal("", Assert.Single(await store.SearchAsync("empty report", cancellationToken: Token)).Snippet);
        Assert.Equal(good.FullPath, Assert.Single(await store.SearchAsync("unique contents", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task ReusesUnchangedTextAndReindexesChangedDocx()
    {
        var original = WriteDocument("<w:p><w:r><w:t>previous phrase</w:t></w:r></w:p>");
        var store = new FileMetadataStore(Path.Combine(_directory, "data", "recall.db"));
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

        File.Delete(original.FullPath);
        var updated = WriteDocument("<w:p><w:r><w:t>replacement phrase with more words</w:t></w:r></w:p>");
        await store.IndexScanAsync([updated], _extractor, cancellationToken: Token);

        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Contains("replacement phrase", Assert.Single(await store.SearchAsync("replacement", cancellationToken: Token)).Snippet);
    }

    [Fact]
    public void CancellationIsPropagated()
    {
        var file = WriteDocument("<w:p><w:r><w:t>remember 123</w:t></w:r></w:p>");
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
