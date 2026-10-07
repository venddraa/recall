namespace Recall.Core.Scanning;

public sealed record ScannedFile(
    string FullPath,
    string Filename,
    string Extension,
    long Size,
    DateTime ModifiedAtUtc);
