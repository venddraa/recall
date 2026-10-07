using System.Diagnostics;
using System.Security;
using Recall.Core.Folders;

namespace Recall.Core.Scanning;

public sealed class FileScanner
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    public Task<ScanResult> ScanAsync(
        IEnumerable<string> selectedFolders,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFolders);
        var roots = selectedFolders.Select(FolderPath.Normalize)
            .Distinct(StringComparer.Ordinal).ToArray();

        return Task.Run(() => Scan(roots, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(
        string[] roots,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>(roots.Reverse());
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<ScannedFile>();
        var failures = new List<ScanFailure>();
        var skippedLinks = 0;
        var progressTimer = Stopwatch.StartNew();

        void ReportProgress(bool force = false)
        {
            if (force || progressTimer.ElapsedMilliseconds >= 250)
            {
                progress?.Report(new ScanProgress(files.Count, failures.Count, skippedLinks));
                progressTimer.Restart();
            }
        }

        void RecordFailure(string path, Exception exception)
        {
            failures.Add(new ScanFailure(path, exception.Message));
            Trace.TraceWarning("Scan entry unavailable: {0}: {1}", path, exception.Message);
        }

        Trace.TraceInformation("File scan started for {0} selected folders.", roots.Length);
        ReportProgress(force: true);
        while (pendingDirectories.TryPop(out var directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedDirectories.Add(directoryPath))
            {
                continue;
            }

            try
            {
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists)
                {
                    throw new DirectoryNotFoundException("The selected directory is unavailable.");
                }

                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedLinks++;
                    ReportProgress();
                    continue;
                }

                foreach (var entry in directory.EnumerateFileSystemInfos("*", EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        entry.Refresh();
                        var attributes = entry.Attributes;
                        if (attributes != (FileAttributes)(-1)
                            && (attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // Never follow links to another directory or read linked file metadata.
                            skippedLinks++;
                        }
                        else if (!entry.Exists)
                        {
                            throw new FileNotFoundException("The entry disappeared or is no longer accessible.");
                        }
                        else if (entry is DirectoryInfo)
                        {
                            pendingDirectories.Push(FolderPath.Normalize(entry.FullName));
                        }
                        else if (entry is FileInfo file)
                        {
                            files.Add(new ScannedFile(
                                file.FullName, file.Name, file.Extension, file.Length, file.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception exception) when (IsFileSystemFailure(exception))
                    {
                        RecordFailure(entry.FullName, exception);
                    }

                    ReportProgress();
                }
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                RecordFailure(directoryPath, exception);
            }

            ReportProgress();
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(force: true);
        cancellationToken.ThrowIfCancellationRequested();
        Trace.TraceInformation("File scan completed: {0} files, {1} unavailable entries, {2} skipped links.",
            files.Count, failures.Count, skippedLinks);
        return new ScanResult(files.AsReadOnly(), failures.AsReadOnly(), skippedLinks);
    }

    private static bool IsFileSystemFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or SecurityException;
    }
}
