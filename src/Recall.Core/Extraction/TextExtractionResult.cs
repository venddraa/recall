namespace Recall.Core.Extraction;

public enum TextExtractionStatus
{
    Extracted,
    Unsupported,
    TooLarge,
    InvalidEncoding,
    Binary,
    Changed,
    Unavailable,
    SymbolicLink,
    NoSearchableText,
    ParserFailed
}

public sealed record TextExtractionResult(TextExtractionStatus Status, string? Text = null);
