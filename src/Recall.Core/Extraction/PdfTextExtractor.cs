using System.Diagnostics;
using System.Security;
using System.Text;
using Recall.Core.Scanning;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Recall.Core.Extraction;

internal sealed class PdfTextExtractor(int maximumFileBytes, int maximumTextCharacters) : ITextExtractor
{
    public bool CanReuse(ScannedFile file)
    {
        try
        {
            if (file.Size > maximumFileBytes || PlainTextExtractor.HasSymbolicLink(file.FullPath))
                return false;
            using var stream = PlainTextExtractor.OpenReadOnly(file.FullPath);
            var info = new FileInfo(file.FullPath);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.ModifiedAtUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (PlainTextExtractor.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            var info = new FileInfo(file.FullPath);
            if (!info.Exists)
                return new(TextExtractionStatus.Unavailable);
            if (info.Length > maximumFileBytes)
                return new(TextExtractionStatus.TooLarge);
            if (info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc)
                return new(TextExtractionStatus.Changed);

            using var stream = PlainTextExtractor.OpenReadOnly(file.FullPath);
            using var document = PdfDocument.Open(stream);
            var content = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageText = ContentOrderTextExtractor.GetText(page).ReplaceLineEndings("\n");
                if (content.Length + pageText.Length + 1 > maximumTextCharacters)
                    return new(TextExtractionStatus.TooLarge);
                if (content.Length > 0)
                    content.Append('\n');
                content.Append(pageText);
            }

            cancellationToken.ThrowIfCancellationRequested();
            info.Refresh();
            if (PlainTextExtractor.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc
                || stream.Length != file.Size)
                return new(TextExtractionStatus.Changed);
            var text = content.ToString();
            return string.IsNullOrWhiteSpace(text)
                ? new(TextExtractionStatus.NoSearchableText)
                : new(TextExtractionStatus.Extracted, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(TextExtractionStatus.Unavailable);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            Trace.TraceWarning("PDF parsing failed for {0}: {1}: {2}", file.FullPath,
                exception.GetType().Name, exception.Message);
            return new(TextExtractionStatus.ParserFailed);
        }
    }
}
