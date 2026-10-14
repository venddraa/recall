using Recall.Core.Extraction;

namespace Recall.Core.Storage;

public sealed record TextIndexResult(long StoredFiles, long StoredTextFiles, int ExtractedFiles,
    IReadOnlyDictionary<TextExtractionStatus, int> SkippedFiles,
    int RemovedFiles = 0, int CleanupSkippedFolders = 0);

public sealed record TextIndexProgress(int ProcessedFiles, int ExtractedFiles);
