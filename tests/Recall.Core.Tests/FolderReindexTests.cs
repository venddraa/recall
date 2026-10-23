using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class FolderReindexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-reindex-tests-{Guid.NewGuid():N}");
    private readonly PlainTextExtractor _extractor = new(4096);
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(_directory, "selected");
    private FileMetadataStore CreateStore() => new(Path.Combine(_directory, "data", "recall.db"));

    private static ScannedFile Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private ScannedFile WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return Snapshot(path);
    }

    private async Task<TextIndexResult> Scan(FileMetadataStore store, string root, bool force = false,
        ITextExtractor? extractor = null)
    {
        var scan = await new FileScanner().ScanAsync([root], cancellationToken: Token);
        return await store.SynchronizeScanAsync(scan, [root], extractor ?? _extractor,
            cancellationToken: Token, forceTextExtraction: force);
    }

    [Fact]
    public async Task ReindexRereadsContentsWhenSizeAndModifiedDateAreUnchanged()
    {
        var original = WriteFile(Path.Combine(Root, "notes.txt"), "alphacached");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var originalId = (await store.GetFileAsync(original.FullPath, Token))!.Id;
        WriteFile(original.FullPath, "betarebuilt");
        File.SetLastWriteTimeUtc(original.FullPath, original.ModifiedAtUtc);
        Assert.Equal(original, Snapshot(original.FullPath));

        // A regular scan still reuses unchanged metadata; only Reindex bypasses the text cache.
        await Scan(store, Root);
        Assert.Single(await store.SearchAsync("alphacached", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("betarebuilt", cancellationToken: Token));

        var result = await Scan(store, Root, force: true);

        Assert.Equal(1, result.ExtractedFiles);
        Assert.Empty(await store.SearchAsync("alphacached", cancellationToken: Token));
        Assert.Equal(original.FullPath, Assert.Single(await store.SearchAsync("betarebuilt", cancellationToken: Token)).FullPath);
        Assert.Equal(originalId, (await store.GetFileAsync(original.FullPath, Token))!.Id);
        Assert.Equal("betarebuilt", await File.ReadAllTextAsync(original.FullPath, Token));
        Assert.Equal(original, Snapshot(original.FullPath));
    }

    [Fact]
    public async Task ManualScanOfOneFolderReusesTextAndReconcilesOnlyItsOwnFiles()
    {
        var parent = WriteFile(Path.Combine(Root, "parent.txt"), "parentcached");
        var selectedRoot = Path.Combine(Root, "nested");
        var unchanged = WriteFile(Path.Combine(selectedRoot, "unchanged.txt"), "unchangedcached");
        var changed = WriteFile(Path.Combine(selectedRoot, "changed.txt"), "oldnested");
        var removed = WriteFile(Path.Combine(selectedRoot, "removed.txt"), "deletedcached");
        var sibling = WriteFile(Path.Combine(Root, "nested-backup", "sibling.txt"), "siblingcached");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var beforeParent = await store.GetFileAsync(parent.FullPath, Token);
        var beforeSibling = await store.GetFileAsync(sibling.FullPath, Token);
        WriteFile(parent.FullPath, "parentchangednotselected");
        WriteFile(sibling.FullPath, "siblingchangednotselected");
        WriteFile(changed.FullPath, "updatednestedcontent");
        File.Delete(removed.FullPath);
        var extractor = new RecordingExtractor(_extractor);

        var result = await Scan(store, selectedRoot, extractor: extractor);

        Assert.Equal(new[] { changed.FullPath }, extractor.ExtractedPaths);
        Assert.Equal(2, result.ExtractedFiles);
        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(4, result.StoredFiles);
        Assert.Equal(beforeParent, await store.GetFileAsync(parent.FullPath, Token));
        Assert.Equal(beforeSibling, await store.GetFileAsync(sibling.FullPath, Token));
        Assert.Null(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Equal(unchanged.FullPath, Assert.Single(await store.SearchAsync("unchangedcached", cancellationToken: Token)).FullPath);
        Assert.Single(await store.SearchAsync("updatednestedcontent", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("parentcached", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("siblingcached", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("oldnested", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("deletedcached", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("parentchangednotselected", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("siblingchangednotselected", cancellationToken: Token));
    }

    [Fact]
    public async Task ReindexOfNestedSelectionDoesNotRescanParentOrSiblingFolders()
    {
        var parent = WriteFile(Path.Combine(Root, "parent.txt"), "parentcached");
        var nestedRoot = Path.Combine(Root, "nested");
        var selected = WriteFile(Path.Combine(nestedRoot, "selected.txt"), "selectedcached");
        var sibling = WriteFile(Path.Combine(nestedRoot + "-backup", "sibling.txt"), "siblingcached");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var beforeParent = await store.GetFileAsync(parent.FullPath, Token);
        var beforeSibling = await store.GetFileAsync(sibling.FullPath, Token);
        WriteFile(parent.FullPath, "parentchanged");
        File.Delete(sibling.FullPath);
        WriteFile(selected.FullPath, "selectedrebuilt");

        var result = await Scan(store, nestedRoot, force: true);

        Assert.Equal(1, result.ExtractedFiles);
        Assert.Equal(3, result.StoredFiles);
        Assert.Equal(beforeParent, await store.GetFileAsync(parent.FullPath, Token));
        Assert.Equal(beforeSibling, await store.GetFileAsync(sibling.FullPath, Token));
        Assert.Single(await store.SearchAsync("parentcached", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("siblingcached", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("selectedrebuilt", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("parentchanged", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("selectedcached", cancellationToken: Token));
    }

    [Fact]
    public async Task ReindexRemovesDeletedEntriesOnlyWithinItsSelection()
    {
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "obsoletecontent");
        var other = WriteFile(Path.Combine(_directory, "other", "retained.txt"), "retainedcontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await store.IndexScanAsync([removed, other], _extractor, cancellationToken: Token);
        File.Delete(removed.FullPath);

        var result = await Scan(store, Root, force: true);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(1, result.StoredFiles);
        Assert.Null(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Empty(await store.SearchAsync("obsoletecontent", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("retainedcontent", cancellationToken: Token));
    }

    [Fact]
    public async Task UnavailableFolderPreservesItsSavedIndexDuringReindex()
    {
        var file = WriteFile(Path.Combine(Root, "cached.txt"), "cachedcontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var before = await store.GetFileAsync(file.FullPath, Token);
        Directory.Move(Root, Path.Combine(_directory, "disconnected"));

        var result = await Scan(store, Root, force: true);

        Assert.Equal(1, result.CleanupSkippedFolders);
        Assert.Equal(0, result.RemovedFiles);
        Assert.Equal(before, await store.GetFileAsync(file.FullPath, Token));
        Assert.Single(await store.SearchAsync("cachedcontent", cancellationToken: Token));
    }

    [Fact]
    public async Task FailedForcedExtractionClearsStaleTextAndContinuesWithHealthyFiles()
    {
        var broken = WriteFile(Path.Combine(Root, "broken.txt"), "OK");
        var healthy = WriteFile(Path.Combine(Root, "healthy.txt"), "healthycontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        await File.WriteAllBytesAsync(broken.FullPath, [0xc3, 0x28], Token);
        File.SetLastWriteTimeUtc(broken.FullPath, broken.ModifiedAtUtc);
        Assert.Equal(broken, Snapshot(broken.FullPath));

        var result = await Scan(store, Root, force: true);

        Assert.Equal(1, result.ExtractedFiles);
        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.InvalidEncoding]);
        Assert.Equal(2, result.StoredFiles);
        Assert.Equal(1, result.StoredTextFiles);
        Assert.Empty(await store.SearchAsync("OK", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("broken", cancellationToken: Token));
        Assert.Equal(healthy.FullPath, Assert.Single(await store.SearchAsync("healthycontent", cancellationToken: Token)).FullPath);
    }

    [Fact]
    public async Task CancelledReindexRollsBackForcedTextAndCleanupTogether()
    {
        var retained = WriteFile(Path.Combine(Root, "keep.txt"), "oldcontent");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "removedcontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var before = await store.GetFileAsync(retained.FullPath, Token);
        WriteFile(retained.FullPath, "newcontent");
        File.SetLastWriteTimeUtc(retained.FullPath, retained.ModifiedAtUtc);
        File.Delete(removed.FullPath);
        var scan = await new FileScanner().ScanAsync([Root], cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SynchronizeScanAsync(scan, [Root],
            _extractor, new CancelOnProgress(cancellation), cancellation.Token, forceTextExtraction: true));

        Assert.Equal(before, await store.GetFileAsync(retained.FullPath, Token));
        Assert.NotNull(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Single(await store.SearchAsync("oldcontent", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("removedcontent", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("newcontent", cancellationToken: Token));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackForcedTextAndFtsUpdates()
    {
        var retained = WriteFile(Path.Combine(Root, "keep.txt"), "oldcontent");
        var removed = WriteFile(Path.Combine(Root, "removed.txt"), "removedcontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);
        var before = await store.GetFileAsync(retained.FullPath, Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_reindex BEFORE DELETE ON file_metadata BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        WriteFile(retained.FullPath, "newcontent");
        File.SetLastWriteTimeUtc(retained.FullPath, retained.ModifiedAtUtc);
        File.Delete(removed.FullPath);

        await Assert.ThrowsAsync<SqliteException>(() => Scan(store, Root, force: true));

        Assert.Equal(before, await store.GetFileAsync(retained.FullPath, Token));
        Assert.NotNull(await store.GetFileAsync(removed.FullPath, Token));
        Assert.Single(await store.SearchAsync("oldcontent", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("removedcontent", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("newcontent", cancellationToken: Token));
    }

    [Fact]
    public async Task ForcedReindexStillEnforcesFileSizeLimits()
    {
        var file = WriteFile(Path.Combine(Root, "large.txt"), "largercontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);

        var result = await Scan(store, Root, force: true, extractor: new PlainTextExtractor(4));

        Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.TooLarge]);
        Assert.Equal(0, result.StoredTextFiles);
        Assert.NotNull(await store.GetFileAsync(file.FullPath, Token));
        Assert.Empty(await store.SearchAsync("largercontent", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("large", cancellationToken: Token));
        Assert.Equal("largercontent", await File.ReadAllTextAsync(file.FullPath, Token));
    }

    [Fact]
    public async Task SnapshotOutsideTheReindexSelectionIsRejectedBeforeWriting()
    {
        var original = WriteFile(Path.Combine(Root, "notes.txt"), "originalcontent");
        var outside = WriteFile(Path.Combine(_directory, "outside", "private.txt"), "privatecontent");
        var store = CreateStore();
        await store.InitializeAsync(Token);
        await Scan(store, Root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SynchronizeScanAsync(
            new ScanResult([outside], [], 0), [Root], _extractor, cancellationToken: Token, forceTextExtraction: true));

        Assert.NotNull(await store.GetFileAsync(original.FullPath, Token));
        Assert.Null(await store.GetFileAsync(outside.FullPath, Token));
        Assert.Single(await store.SearchAsync("originalcontent", cancellationToken: Token));
        Assert.Empty(await store.SearchAsync("privatecontent", cancellationToken: Token));
    }

    private sealed class RecordingExtractor(ITextExtractor inner) : ITextExtractor
    {
        public List<string> ExtractedPaths { get; } = [];
        public bool CanReuse(ScannedFile file) => inner.CanReuse(file);
        public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
        {
            ExtractedPaths.Add(file.FullPath);
            return inner.Extract(file, cancellationToken);
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
