using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Folders;
using Recall.Core.Storage;

namespace Recall.Core.Monitoring;

public sealed record FolderMonitorUpdate(bool Updating = false, TextIndexResult? Result = null, string? Error = null);

public sealed class FolderMonitor : IAsyncDisposable
{
    private const int MaximumPendingPaths = 4096;
    private readonly PathChangeIndexer _indexer;
    private readonly IProgress<FolderMonitorUpdate>? _progress;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _rootCheckInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lifecycle = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private Task? _startTask;
    private Task? _worker;
    private Task? _rootCheck;
    private Task? _disposeTask;
    private string? _failureReason;
    private int _disposed;

    public FolderMonitor(FileMetadataStore store, ITextExtractor extractor, IEnumerable<string> selectedFolders,
        IEnumerable<string> excludedFolders, IProgress<FolderMonitorUpdate>? progress = null,
        TimeSpan? debounce = null, TimeSpan? rootCheckInterval = null)
    {
        _indexer = new(store, extractor, selectedFolders, excludedFolders);
        _progress = progress;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(600);
        _rootCheckInterval = rootCheckInterval ?? TimeSpan.FromSeconds(10);
        if (_debounce <= TimeSpan.Zero || _rootCheckInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce), "Monitoring intervals must be positive.");
    }

    public string? FailureReason => Volatile.Read(ref _failureReason);
    public int WatcherCount => _watchers.Count;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_startTask is not null)
                throw new InvalidOperationException("The monitor has already been started.");
            _startTask = Task.Run(() =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
                _indexer.CheckRoots();
                foreach (var root in _indexer.Roots)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var watcher = new FileSystemWatcher(root)
                    {
                        Filter = "*",
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                            | NotifyFilters.Size | NotifyFilters.Attributes
                    };
                    watcher.Created += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, args) => Fail(args.GetException());
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }
                linked.Token.ThrowIfCancellationRequested();
                _worker = Task.Run(ProcessChangesAsync);
                _rootCheck = Task.Run(CheckRootsAsync);
            }, cancellationToken);
            return _startTask;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => RequestUpdate(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        RequestUpdate(args.OldFullPath);
        RequestUpdate(args.FullPath);
    }

    public bool RequestUpdate(string path)
    {
        if (Volatile.Read(ref _disposed) != 0 || FailureReason is not null)
            return false;
        path = FolderPath.Normalize(path);
        if (!_indexer.IncludesPath(path))
            return false;
        var overflow = false;
        lock (_pending)
        {
            if (_pending.Count >= MaximumPendingPaths && !_pending.Contains(path))
                overflow = true;
            else
                _pending.Add(path);
        }
        if (overflow)
        {
            Fail(new InternalBufferOverflowException("Too many pending file changes."));
            return false;
        }
        _signal.Writer.TryWrite(true);
        return true;
    }

    // Holding this lease pauses writes; filesystem events continue to accumulate during a manual scan.
    public async Task<IDisposable> PauseAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken stopToken;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            stopToken = _stop.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
        await _gate.WaitAsync(linked.Token);
        return new GateLease(_gate);
    }

    private async Task ProcessChangesAsync()
    {
        try
        {
            while (await _signal.Reader.WaitToReadAsync(_stop.Token))
            {
                while (_signal.Reader.TryRead(out _)) { }
                await Task.Delay(_debounce, _stop.Token);
                using var pause = await PauseAsync(_stop.Token);
                string[] paths;
                lock (_pending)
                {
                    paths = _pending.ToArray();
                    _pending.Clear();
                }
                if (paths.Length == 0)
                    continue;
                _progress?.Report(new(Updating: true));
                TextIndexResult? result = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        result = await _indexer.UpdateAsync(paths, _stop.Token);
                        var transient = result.SkippedFiles.GetValueOrDefault(TextExtractionStatus.Changed)
                            + result.SkippedFiles.GetValueOrDefault(TextExtractionStatus.Unavailable);
                        if (transient == 0 || attempt == 2)
                            break;
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 && attempt < 2)
                    {
                        Trace.TraceWarning("Automatic indexing will retry a busy database.");
                    }
                    await Task.Delay(_debounce, _stop.Token);
                }
                if (FailureReason is null && !_stop.IsCancellationRequested)
                    _progress?.Report(new(Result: result));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            StopWatchers();
        }
    }

    private async Task CheckRootsAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_rootCheckInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token))
                _indexer.CheckRoots();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        lock (_lifecycle)
        {
            if (_disposed != 0)
                return;
            const string message = "Automatic updates stopped. Choose Index → Scan Folders to recover.";
            if (Interlocked.CompareExchange(ref _failureReason, message, null) is not null)
                return;
            Trace.TraceWarning("Folder monitoring stopped: {0}", exception.GetType().Name);
            _stop.Cancel();
            _progress?.Report(new(Error: message));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
            return new(_disposeTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _stop.CancelAsync();
        if (_startTask is not null)
        {
            try { await _startTask; }
            catch (Exception) { /* The caller of StartAsync receives startup errors. */ }
        }
        StopWatchers();
        await Task.WhenAll(_worker ?? Task.CompletedTask, _rootCheck ?? Task.CompletedTask);
        // A manual scan can still hold its lease while the window is closing.
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
        lock (_lifecycle)
            _stop.Dispose();
        lock (_pending)
            _pending.Clear();
    }

    private void StopWatchers()
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
        }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
