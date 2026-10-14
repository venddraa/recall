using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Monitoring;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class PathChangeIndexerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-path-changes-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(4096, 1024 * 1024, 4096);
    private string Root => Path.Combine(_directory, "selected");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<FileMetadataStore> Store()
    {
        Directory.CreateDirectory(Root);
        var store = new FileMetadataStore(Path.Combine(_directory, "state", "recall.db"));
        await store.InitializeAsync(Token);
        return store;
    }

    [Fact]
    public async Task FileUpdateDoesNotReprocessUnrelatedFilesAndPreservesId()
    {
        var first = Write("first.txt", "previous contents");
        var other = Write("other.txt", "unchanged contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([first, other], Token);
        var originalId = (await store.GetFileAsync(first, Token))!.Id;
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE file_text SET processed_at_utc = 'sentinel';";
            await command.ExecuteNonQueryAsync(Token);
        }
        Write("first.txt", "replacement contents with more words");

        var result = await indexer.UpdateAsync([first], Token);

        Assert.Equal(1, result.ExtractedFiles);
        Assert.Equal(originalId, (await store.GetFileAsync(first, Token))!.Id);
        Assert.Empty(await store.SearchAsync("previous", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("replacement", cancellationToken: Token));
        using var read = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await read.OpenAsync(Token);
        using var query = read.CreateCommand();
        query.CommandText = "SELECT processed_at_utc FROM file_text JOIN file_metadata ON id=file_id WHERE full_path=$path;";
        query.Parameters.AddWithValue("$path", other);
        Assert.Equal("sentinel", await query.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task DirectoryRenameAndDeletionRemoveOldTreeButPreserveSibling()
    {
        var oldFile = Write("old/sub/note.txt", "remembered contents");
        var sibling = Write("old-backup/keep.txt", "sibling contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([Path.Combine(Root, "old"), sibling], Token);
        var old = Path.Combine(Root, "old");
        var renamed = Path.Combine(Root, "new");
        Directory.Move(old, renamed);

        await indexer.UpdateAsync([old, renamed], Token);

        Assert.Null(await store.GetFileAsync(oldFile, Token));
        Assert.Equal(Path.Combine(renamed, "sub", "note.txt"), Assert.Single(await store.SearchAsync("remembered", cancellationToken: Token)).FullPath);
        Assert.NotNull(await store.GetFileAsync(sibling, Token));
        Directory.Delete(renamed, recursive: true);
        await indexer.UpdateAsync([renamed], Token);
        Assert.Empty(await store.SearchAsync("remembered", cancellationToken: Token));
        Assert.Single(await store.SearchAsync("sibling", cancellationToken: Token));
    }

    [Fact]
    public async Task DirectoryAndFileReplacementRemoveObsoleteShapes()
    {
        var path = Write("entry", "original file");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([path], Token);
        File.Delete(path);
        var child = Write("entry/child.txt", "child contents");

        await indexer.UpdateAsync([path], Token);

        Assert.Null(await store.GetFileAsync(path, Token));
        Assert.NotNull(await store.GetFileAsync(child, Token));
        Directory.Delete(path, recursive: true);
        Write("entry", "new file");
        await indexer.UpdateAsync([path], Token);
        Assert.Null(await store.GetFileAsync(child, Token));
        Assert.NotNull(await store.GetFileAsync(path, Token));
        Assert.Equal(1, await store.GetFileCountAsync(Token));
    }

    [Fact]
    public async Task DirectoryImportExcludesRecallStateAndDoesNotFollowLinkedParents()
    {
        var visible = Write("import/visible.txt", "visible contents");
        var state = Write("state/config.json", "private configuration");
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        File.WriteAllText(Path.Combine(outside, "nested", "private.txt"), "private external contents");
        var link = Path.Combine(Root, "linked");
        Directory.CreateSymbolicLink(link, outside);
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], [Path.GetDirectoryName(state)!]);

        await indexer.UpdateAsync([Root, state, Path.Combine(_directory, "outside", "nested")], Token);

        Assert.Equal(1, await store.GetFileCountAsync(Token));
        Assert.NotNull(await store.GetFileAsync(visible, Token));
        Assert.Empty(await store.SearchAsync("private", cancellationToken: Token));
        var linkedScan = await new FileScanner().ScanAsync([Path.Combine(link, "nested")], cancellationToken: Token);
        Assert.Empty(linkedScan.Files);
        Assert.Equal(1, linkedScan.SkippedLinks);
    }

    [Fact]
    public async Task UnavailableSelectedRootIsNotTreatedAsDeletedFiles()
    {
        var file = Write("keep.txt", "cached contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([file], Token);
        Directory.Move(Root, Path.Combine(_directory, "offline"));

        await Assert.ThrowsAnyAsync<IOException>(() => indexer.UpdateAsync([file], Token));

        Assert.NotNull(await store.GetFileAsync(file, Token));
        Assert.Single(await store.SearchAsync("cached", cancellationToken: Token));
    }

    [Fact]
    public async Task RootReplacedAtSamePathPreservesCachedIndex()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var file = Write("cached.txt", "cached contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([file], Token);
        Directory.Move(Root, Path.Combine(_directory, "previous-root"));
        Directory.CreateDirectory(Root);

        await Assert.ThrowsAnyAsync<IOException>(() => indexer.UpdateAsync([file], Token));

        Assert.Single(await store.SearchAsync("cached", cancellationToken: Token));
    }

    [Fact]
    public async Task DeleteFailureRollsBackTheEntireEventBatch()
    {
        var file = Write("deleted.txt", "cached contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        await indexer.UpdateAsync([file], Token);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON file_metadata BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        File.Delete(file);
        var added = Write("added.txt", "new contents");

        await Assert.ThrowsAsync<SqliteException>(() => indexer.UpdateAsync([file, added], Token));

        Assert.NotNull(await store.GetFileAsync(file, Token));
        Assert.Null(await store.GetFileAsync(added, Token));
        Assert.Single(await store.SearchAsync("cached", cancellationToken: Token));
    }

    [Fact]
    public async Task UnreadableIndividualFileDoesNotStopOtherFileUpdates()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var broken = Write("broken.txt", "private contents");
        var good = Write("good.txt", "good contents");
        var store = await Store();
        var indexer = new PathChangeIndexer(store, _extractor, [Root], []);
        File.SetUnixFileMode(broken, UnixFileMode.None);
        try
        {
            var result = await indexer.UpdateAsync([broken, good], Token);
            Assert.Equal(1, result.SkippedFiles[TextExtractionStatus.Unavailable]);
            Assert.Single(await store.SearchAsync("good contents", cancellationToken: Token));
        }
        finally
        {
            File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
