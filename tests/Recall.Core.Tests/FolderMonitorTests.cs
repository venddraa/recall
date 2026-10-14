using System.Collections.Concurrent;
using Recall.Core.Extraction;
using Recall.Core.Monitoring;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class FolderMonitorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-watch-tests-{Guid.NewGuid():N}");
    private readonly FileTextExtractor _extractor = new(4096, 1024 * 1024, 4096);
    private string Root => Path.Combine(_directory, "selected");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<FileMetadataStore> Store()
    {
        Directory.CreateDirectory(Root);
        var store = new FileMetadataStore(Path.Combine(Root, "state", "recall.db"));
        await store.InitializeAsync(Token);
        return store;
    }

    private FolderMonitor Monitor(FileMetadataStore store, IProgress<FolderMonitorUpdate>? progress = null,
        params string[] roots) => new(store, _extractor, roots.Length == 0 ? [Root] : roots,
            [Path.GetDirectoryName(store.DatabasePath)!], progress,
            debounce: TimeSpan.FromMilliseconds(100), rootCheckInterval: TimeSpan.FromMilliseconds(150));

    private async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(30, timeout.Token);
    }

    [Fact]
    public async Task NativeWatcherIndexesCreateChangeRenameAndDeleteWithoutManualScan()
    {
        var store = await Store();
        await using var monitor = Monitor(store);
        await monitor.StartAsync(Token);
        var path = Path.Combine(Root, "before.txt");
        await File.WriteAllTextAsync(path, "first remembered phrase", Token);
        await WaitUntil(async () => (await store.SearchAsync("first remembered", cancellationToken: Token)).Count == 1);

        await File.WriteAllTextAsync(path, "replacement content with more words", Token);
        await WaitUntil(async () => (await store.SearchAsync("replacement content", cancellationToken: Token)).Count == 1);
        Assert.Empty(await store.SearchAsync("first remembered", cancellationToken: Token));

        var renamed = Path.Combine(Root, "after.txt");
        File.Move(path, renamed);
        await WaitUntil(async () => await store.GetFileAsync(renamed, Token) is not null && await store.GetFileAsync(path, Token) is null);
        Assert.Equal(renamed, Assert.Single(await store.SearchAsync("replacement", cancellationToken: Token)).FullPath);

        File.Delete(renamed);
        await WaitUntil(async () => await store.GetFileCountAsync(Token) == 0);
        Assert.Null(monitor.FailureReason);
    }

    [Fact]
    public async Task EventsDuringManualScanAreQueuedAndBurstIsCoalesced()
    {
        var store = await Store();
        var updates = new ConcurrentQueue<FolderMonitorUpdate>();
        await using var monitor = Monitor(store, new InlineProgress(updates.Enqueue));
        await monitor.StartAsync(Token);
        var path = Path.Combine(Root, "burst.txt");
        using (await monitor.PauseAsync(Token))
        {
            await File.WriteAllTextAsync(path, "early contents", Token);
            var scan = await new FileScanner().ScanAsync([Root], cancellationToken: Token,
                excludedFolders: [Path.GetDirectoryName(store.DatabasePath)!]);
            await store.SynchronizeScanAsync(scan, [Root], _extractor, cancellationToken: Token);
            await File.WriteAllTextAsync(path, "final contents after manual snapshot", Token);
            for (var i = 0; i < 50; i++)
                monitor.RequestUpdate(path);
            await Task.Delay(250, Token);
            Assert.Single(await store.SearchAsync("early contents", cancellationToken: Token));
            Assert.Empty(await store.SearchAsync("final contents", cancellationToken: Token));
        }
        await WaitUntil(async () => (await store.SearchAsync("final contents", cancellationToken: Token)).Count == 1);
        await WaitUntil(() => Task.FromResult(updates.Any(update => update.Result is not null)));
        Assert.InRange(updates.Count(update => update.Result is not null), 1, 2);
    }

    [Fact]
    public async Task OverlappingRootsUseOneWatcherAndStateWritesDoNotLoop()
    {
        var store = await Store();
        var nested = Path.Combine(Root, "nested");
        Directory.CreateDirectory(nested);
        var updates = new ConcurrentQueue<FolderMonitorUpdate>();
        await using var monitor = Monitor(store, new InlineProgress(updates.Enqueue), Root, nested);
        await monitor.StartAsync(Token);
        Assert.Equal(1, monitor.WatcherCount);
        Assert.False(monitor.RequestUpdate(store.DatabasePath));
        Assert.False(monitor.RequestUpdate(Root + "-sibling/private.txt"));
        var path = Path.Combine(nested, "note.txt");
        await File.WriteAllTextAsync(path, "visible contents", Token);
        await WaitUntil(async () => (await store.SearchAsync("visible", cancellationToken: Token)).Count == 1);
        await Task.Delay(400, Token);
        Assert.Equal(1, await store.GetFileCountAsync(Token));
        Assert.Empty(await store.SearchAsync("recall db", cancellationToken: Token));
        Assert.InRange(updates.Count(update => update.Result is not null), 1, 2);
        Assert.Null(monitor.FailureReason);
    }

    [Fact]
    public async Task PendingOverflowStopsAutomaticWritesAndReportsRecovery()
    {
        var store = await Store();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = Monitor(store, new InlineProgress(update =>
        {
            if (update.Error is not null)
                failure.TrySetResult(update.Error);
        }));
        await monitor.StartAsync(Token);
        using (await monitor.PauseAsync(Token))
        {
            for (var i = 0; i <= 4096; i++)
                monitor.RequestUpdate(Path.Combine(Root, $"pending-{i}.txt"));
            Assert.Contains("Scan Folders", await failure.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.NotNull(monitor.FailureReason);
            Assert.Equal(0, await store.GetFileCountAsync(Token));
        }
    }

    [Fact]
    public async Task RootDisconnectIsReportedAndCachedFilesArePreserved()
    {
        var store = await Store();
        var watched = Path.Combine(_directory, "watched");
        Directory.CreateDirectory(watched);
        var cached = Path.Combine(watched, "note.txt");
        await File.WriteAllTextAsync(cached, "offline contents", Token);
        var indexer = new PathChangeIndexer(store, _extractor, [watched], []);
        await indexer.UpdateAsync([cached], Token);
        await using var offlineMonitor = Monitor(store, null, watched);
        await offlineMonitor.StartAsync(Token);
        Directory.Move(watched, Path.Combine(_directory, "offline"));

        await WaitUntil(() => Task.FromResult(offlineMonitor.FailureReason is not null));

        Assert.NotNull(await store.GetFileAsync(cached, Token));
        Assert.Single(await store.SearchAsync("offline contents", cancellationToken: Token));
    }

    [Fact]
    public async Task NativeDirectoryImportRenameAndDeleteReconcileEntireSubtree()
    {
        var store = await Store();
        var outside = Path.Combine(_directory, "import-source", "nested");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "note.txt"), "imported subtree contents", Token);
        await using var monitor = Monitor(store);
        await monitor.StartAsync(Token);
        var imported = Path.Combine(Root, "imported");
        Directory.Move(Path.GetDirectoryName(outside)!, imported);
        await WaitUntil(async () => (await store.SearchAsync("imported subtree", cancellationToken: Token)).Count == 1);

        var renamed = Path.Combine(Root, "renamed");
        Directory.Move(imported, renamed);
        var path = Path.Combine(renamed, "nested", "note.txt");
        await WaitUntil(async () => await store.GetFileAsync(path, Token) is not null
            && await store.GetFileAsync(Path.Combine(imported, "nested", "note.txt"), Token) is null);
        Directory.Delete(renamed, recursive: true);
        await WaitUntil(async () => await store.GetFileCountAsync(Token) == 0);
        Assert.Null(monitor.FailureReason);
    }

    [Fact]
    public async Task DisposeStopsWatchingAndRejectsSubsequentUpdates()
    {
        var store = await Store();
        var monitor = Monitor(store);
        await monitor.StartAsync(Token);
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
        var path = Path.Combine(Root, "after-close.txt");
        await File.WriteAllTextAsync(path, "not automatically indexed", Token);

        Assert.False(monitor.RequestUpdate(path));
        await Task.Delay(200, Token);
        Assert.Equal(0, await store.GetFileCountAsync(Token));
    }

    private sealed class InlineProgress(Action<FolderMonitorUpdate> action) : IProgress<FolderMonitorUpdate>
    {
        public void Report(FolderMonitorUpdate value) => action(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
