using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

public sealed class FileTextExtractor : ITextExtractor
{
    private readonly PlainTextExtractor _plainText;
    private readonly PdfTextExtractor _pdf;
    private readonly DocxTextExtractor _docx;

    public FileTextExtractor(int maximumTextFileBytes, int maximumDocumentFileBytes,
        int maximumExtractedDocumentTextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDocumentFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractedDocumentTextCharacters);
        _plainText = new(maximumTextFileBytes);
        _pdf = new(maximumDocumentFileBytes, maximumExtractedDocumentTextCharacters);
        _docx = new(maximumDocumentFileBytes, maximumExtractedDocumentTextCharacters);
    }

    public bool CanReuse(ScannedFile file) => GetExtractor(file).CanReuse(file);

    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken) =>
        GetExtractor(file).Extract(file, cancellationToken);

    private ITextExtractor GetExtractor(ScannedFile file) => Path.GetExtension(file.FullPath).ToLowerInvariant() switch
    {
        ".pdf" => _pdf,
        ".docx" => _docx,
        _ => _plainText
    };
}
