using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

public interface ITextExtractor
{
    bool CanReuse(ScannedFile file);
    TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken);
}
