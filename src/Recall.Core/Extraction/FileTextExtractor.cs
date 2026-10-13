using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

public sealed class FileTextExtractor(int maximumTextFileBytes, int maximumPdfFileBytes,
    int maximumExtractedPdfTextCharacters) : ITextExtractor
{
    private readonly PlainTextExtractor _plainText = new(maximumTextFileBytes);
    private readonly PdfTextExtractor _pdf = new(maximumPdfFileBytes, maximumExtractedPdfTextCharacters);

    public bool CanReuse(ScannedFile file) => IsPdf(file)
        ? _pdf.CanReuse(file)
        : _plainText.CanReuse(file);

    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken) => IsPdf(file)
        ? _pdf.Extract(file, cancellationToken)
        : _plainText.Extract(file, cancellationToken);

    private static bool IsPdf(ScannedFile file) =>
        string.Equals(Path.GetExtension(file.FullPath), ".pdf", StringComparison.OrdinalIgnoreCase);
}
