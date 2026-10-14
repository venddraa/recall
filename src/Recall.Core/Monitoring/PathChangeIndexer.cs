using System.Security;
using System.Diagnostics;
using Recall.Core.Extraction;
using Recall.Core.Folders;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Core.Monitoring;

public sealed class PathChangeIndexer
{
    private readonly FileMetadataStore _store;
    private readonly ITextExtractor _extractor;
    private readonly string[] _roots;
    private readonly string[] _selectedRoots;
    private readonly string[] _exclusions;
    private readonly Dictionary<string, (uint DeviceMajor, uint DeviceMinor, ulong Inode)> _identities = new(StringComparer.Ordinal);

    public PathChangeIndexer(FileMetadataStore store, ITextExtractor extractor,
        IEnumerable<string> selectedFolders, IEnumerable<string> excludedFolders)
    {
        _store = store;
        _extractor = extractor;
        var roots = selectedFolders.Select(FolderPath.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        _selectedRoots = roots;
        _roots = roots.Where(root => !roots.Any(parent => parent != root && FolderPath.ContainsPath(parent, root))).ToArray();
        _exclusions = excludedFolders.Select(FolderPath.Normalize).ToArray();
    }

    public IReadOnlyList<string> Roots => Array.AsReadOnly(_roots);

    public bool IncludesPath(string path) => _roots.Any(root => FolderPath.ContainsPath(root, path))
        && !_exclusions.Any(folder => FolderPath.ContainsPath(folder, path));

    public void CheckRoots()
    {
        foreach (var root in _selectedRoots)
        {
            if (ReadOnlyFileAccess.HasSymbolicLink(root) || !Directory.Exists(root))
                throw new IOException("A monitored folder is unavailable or linked. Run a manual scan to reconnect.");
            if (ReadOnlyFileAccess.DirectoryIdentity(root) is { } identity)
            {
                lock (_identities)
                {
                    if (_identities.TryGetValue(root, out var previous) && previous != identity)
                        throw new IOException("A monitored folder was replaced or its drive was changed. Run a manual scan to reconnect.");
                    _identities[root] = identity;
                }
            }
        }
    }

    public async Task<TextIndexResult> UpdateAsync(IEnumerable<string> changedPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckRoots();
        var paths = new List<string>();
        foreach (var path in changedPaths.Select(FolderPath.Normalize).Distinct(StringComparer.Ordinal).OrderBy(path => path.Length))
        {
            if (IncludesPath(path) && !paths.Any(parent => FolderPath.ContainsPath(parent, path)))
                paths.Add(path);
        }
        var files = new Dictionary<string, ScannedFile>(StringComparer.Ordinal);
        var removed = new List<string>();
        var completeFolders = new List<string>();
        var failedFolders = 0;
        var unavailable = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(path);
                if (ReadOnlyFileAccess.HasSymbolicLink(path))
                {
                    removed.Add(path);
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    var scan = await new FileScanner().ScanAsync([path], cancellationToken: cancellationToken,
                        excludedFolders: _exclusions);
                    foreach (var file in scan.Files)
                        files[file.FullPath] = file;
                    if (scan.Failures.Count == 0)
                        completeFolders.Add(path);
                    else
                        failedFolders++;
                }
                else
                {
                    var info = new FileInfo(path);
                    files[path] = new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Never interpret a disconnected selected root as deletion of all its cached files.
                CheckRoots();
                removed.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                unavailable++;
                Trace.TraceWarning("Changed path unavailable: {0}: {1}", path, exception.GetType().Name);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        CheckRoots();
        var result = await _store.ApplyChangesAsync(files.Values, removed, completeFolders, _extractor, cancellationToken);
        var skipped = result.SkippedFiles.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (unavailable > 0)
            skipped[TextExtractionStatus.Unavailable] = skipped.GetValueOrDefault(TextExtractionStatus.Unavailable) + unavailable;
        return result with { CleanupSkippedFolders = failedFolders, SkippedFiles = skipped };
    }
}
