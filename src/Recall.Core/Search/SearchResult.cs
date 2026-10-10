namespace Recall.Core.Search;

public sealed record SearchResult(
    long Id,
    string FullPath,
    string Filename,
    string Extension,
    long Size,
    DateTime ModifiedAtUtc,
    double Rank);
