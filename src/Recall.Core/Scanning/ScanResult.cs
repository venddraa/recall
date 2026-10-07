namespace Recall.Core.Scanning;

public sealed record ScanFailure(string Path, string Message);

public sealed record ScanProgress(int FileCount, int FailureCount, int SkippedLinks);

public sealed record ScanResult(
    IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<ScanFailure> Failures,
    int SkippedLinks);
