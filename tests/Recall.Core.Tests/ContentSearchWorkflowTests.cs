using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Recall.Core.Extraction;
using Recall.Core.Folders;
using Recall.Core.Scanning;
using Recall.Core.Storage;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Recall.Core.Tests;

public sealed class ContentSearchWorkflowTests : IDisposable
{
    private const string RememberedText = "jejak surya7421";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-workflow-tests-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(4096, 1024 * 1024, 4096);
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(_directory, "selected");
    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private Dictionary<string, string> WriteCorpus()
    {
        Directory.CreateDirectory(Path.Combine(Root, "nested"));
        var paths = new Dictionary<string, string>
        {
            [".txt"] = Path.Combine(Root, "opaque-a.txt"),
            [".pdf"] = Path.Combine(Root, "nested", "opaque-b.pdf"),
            [".docx"] = Path.Combine(Root, "opaque-c.docx"),
            [".ipynb"] = Path.Combine(Root, "nested", "opaque-d.ipynb")
        };
        File.WriteAllText(paths[".txt"], RememberedText, new UTF8Encoding(false));

        var pdf = new PdfDocumentBuilder();
        var page = pdf.AddPage(PageSize.A4);
        var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(RememberedText, 12, new PdfPoint(40, 750), font);
        File.WriteAllBytes(paths[".pdf"], pdf.Build());

        using (var archive = ZipFile.Open(paths[".docx"], ZipArchiveMode.Create))
        {
            WritePart("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            WritePart("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            WritePart("word/document.xml", $$"""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>{{RememberedText}}</w:t></w:r></w:p></w:body>
                </w:document>
                """);

            void WritePart(string name, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        File.WriteAllText(paths[".ipynb"], JsonSerializer.Serialize(new
        {
            nbformat = 4, nbformat_minor = 5, metadata = new { },
            cells = new[]
            {
                new
                {
                    cell_type = "code", id = "workflow-cell", metadata = new { },
                    source = new[] { "calculate()" }, execution_count = (int?)null,
                    outputs = new[] { new { output_type = "stream", name = "stdout", text = RememberedText } }
                }
            }
        }), new UTF8Encoding(false));
        return paths;
    }

    private async Task<TextIndexResult> Scan(FileMetadataStore store, params string[] roots)
    {
        var scan = await new FileScanner().ScanAsync(roots, cancellationToken: Token);
        Assert.Empty(scan.Failures);
        return await store.SynchronizeScanAsync(scan, roots, _extractor, cancellationToken: Token);
    }

    [Fact]
    public async Task ContentSearchAcrossFormatsRespectsSavedSelectionAndSurvivesRestartWithUnavailableSources()
    {
        var paths = WriteCorpus();
        var originals = paths.Values.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        var outside = Path.Combine(_directory, "previously-selected");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), RememberedText, Token);
        var archivePath = Path.Combine(Root, "opaque-archive.zip");
        await File.WriteAllTextAsync(archivePath, RememberedText, Token);
        var store = CreateStore();
        await store.InitializeAsync(Token);

        // Simulate an older folder selection, then search only the currently saved selection.
        var indexed = await Scan(store, Root, outside);
        Assert.Equal(6, indexed.StoredFiles);
        Assert.Equal(5, indexed.StoredTextFiles);
        var configPath = Path.Combine(_directory, "config", "folders.json");
        await new FolderConfigurationStore(configPath).SaveAsync([Root], Token);
        var folders = await new FolderConfigurationStore(configPath).LoadAsync(Token);
        var results = await store.SearchFoldersAsync(RememberedText, folders, cancellationToken: Token);

        Assert.Equal(paths.Values.Order(StringComparer.Ordinal), results.Select(result => result.FullPath).Order(StringComparer.Ordinal));
        Assert.All(results, result =>
        {
            Assert.DoesNotContain("jejak", result.Filename, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("surya7421", result.Filename, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TextExtractionStatus.Extracted, result.ExtractionStatus);
            Assert.Contains(RememberedText, result.Snippet);
            Assert.Contains(result.SnippetParts!, part => part.IsMatch && part.Text == "surya7421");
        });
        var archive = Assert.Single(await store.SearchFoldersAsync("opaque archive", folders, cancellationToken: Token));
        Assert.Equal(archivePath, archive.FullPath);
        Assert.Equal(TextExtractionStatus.Unsupported, archive.ExtractionStatus);
        Assert.Empty(archive.Snippet);
        foreach (var (path, original) in originals)
        {
            Assert.Equal(original.Bytes, await File.ReadAllBytesAsync(path, Token));
            Assert.Equal(original.Modified, File.GetLastWriteTimeUtc(path));
        }

        // Only move synthetic fixtures, simulating a disconnected source drive.
        Directory.Move(Root, Root + "-offline");
        var restarted = CreateStore();
        await restarted.InitializeAsync(Token);
        var unavailableScan = await new FileScanner().ScanAsync(folders, cancellationToken: Token);
        Assert.Single(unavailableScan.Failures);
        var preserved = await restarted.SynchronizeScanAsync(unavailableScan, folders, _extractor, cancellationToken: Token);
        Assert.Equal(1, preserved.CleanupSkippedFolders);
        Assert.Equal(0, preserved.RemovedFiles);
        Assert.Equal(6, preserved.StoredFiles);
        var cached = await restarted.SearchFoldersAsync(RememberedText, folders, cancellationToken: Token);
        Assert.Equal(results.Select(result => (result.FullPath, result.Snippet)),
            cached.Select(result => (result.FullPath, result.Snippet)));
    }

    [Fact]
    public async Task RescanReconcilesChangedRenamedDeletedAndCorruptDocumentsTogether()
    {
        var paths = WriteCorpus();
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        Assert.Equal(4, (await store.SearchFoldersAsync(RememberedText, [Root], cancellationToken: Token)).Count);

        // External edits affect only this test's own temporary documents.
        const string replacement = "hasil berubah9817 selesai";
        await File.WriteAllTextAsync(paths[".txt"], replacement, Token);
        var renamedPdf = Path.Combine(Root, "nested", "renamed-b.pdf");
        File.Move(paths[".pdf"], renamedPdf);
        File.Delete(paths[".docx"]);
        await File.WriteAllTextAsync(paths[".ipynb"],
            "{\"nbformat\":4,\"cells\":[{\"cell_type\":\"markdown\",\"source\":\"partialonly9037\"},", Token);

        var indexed = await Scan(store, Root);

        Assert.Equal(3, indexed.StoredFiles);
        Assert.Equal(2, indexed.StoredTextFiles);
        Assert.Equal(2, indexed.RemovedFiles);
        Assert.Equal(1, indexed.SkippedFiles[TextExtractionStatus.ParserFailed]);
        Assert.Equal(renamedPdf, Assert.Single(await store.SearchFoldersAsync(RememberedText, [Root], cancellationToken: Token)).FullPath);
        var changed = Assert.Single(await store.SearchFoldersAsync("hasil berubah9817", [Root], cancellationToken: Token));
        Assert.Equal(paths[".txt"], changed.FullPath);
        Assert.Contains(replacement, changed.Snippet);
        var corrupt = Assert.Single(await store.SearchFoldersAsync("opaque d", [Root], cancellationToken: Token));
        Assert.Equal(paths[".ipynb"], corrupt.FullPath);
        Assert.Equal(TextExtractionStatus.ParserFailed, corrupt.ExtractionStatus);
        Assert.Empty(corrupt.Snippet);
        Assert.Empty(await store.SearchFoldersAsync("partialonly9037", [Root], cancellationToken: Token));
        Assert.Empty(await store.SearchFoldersAsync("opaque b", [Root], cancellationToken: Token));
        Assert.Empty(await store.SearchFoldersAsync("opaque c", [Root], cancellationToken: Token));
        Assert.Null(await store.GetFileAsync(paths[".pdf"], Token));
        Assert.Null(await store.GetFileAsync(paths[".docx"], Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
