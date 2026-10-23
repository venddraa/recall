using Recall.Core.Extraction;

namespace Recall.Core.Storage;

public sealed record TextIndexResult(long StoredFiles, long StoredTextFiles, int ExtractedFiles,
    IReadOnlyDictionary<TextExtractionStatus, int> SkippedFiles,
    int RemovedFiles = 0, int CleanupSkippedFolders = 0,
    IReadOnlyList<SkippedFileDetail>? SkippedFileDetails = null)
{
    public const int MaximumSkippedFileDetails = 100;
}

public sealed record SkippedFileDetail(string FullPath, TextExtractionStatus Status);

public sealed record TextIndexProgress(int ProcessedFiles, int ExtractedFiles);
