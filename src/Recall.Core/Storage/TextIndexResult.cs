using Recall.Core.Extraction;

namespace Recall.Core.Storage;

public sealed record TextIndexResult(long StoredFiles, long StoredTextFiles, int ExtractedFiles,
    IReadOnlyDictionary<TextExtractionStatus, int> SkippedFiles);

public sealed record TextIndexProgress(int ProcessedFiles, int ExtractedFiles);
