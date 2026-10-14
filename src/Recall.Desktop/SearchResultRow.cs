using System.Globalization;
using Recall.Core.Extraction;
using Recall.Core.Search;

namespace Recall.Desktop;

public sealed class SearchResultRow(SearchResult result)
{
    public string Filename => result.Filename;
    public string FullPath => result.FullPath;
    public string Folder => Path.GetDirectoryName(result.FullPath) ?? result.FullPath;
    public string Type => string.IsNullOrEmpty(result.Extension) ? "FILE" : result.Extension.TrimStart('.').ToUpperInvariant();
    public string Modified => result.ModifiedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Snippet => string.IsNullOrWhiteSpace(result.Snippet)
        ? "This file matched its name.\n\n" + MissingPreviewReason
        : result.Snippet;

    private string MissingPreviewReason => result.ExtractionStatus switch
    {
        TextExtractionStatus.Extracted => "Text indexing completed, but no previewable text was found.",
        TextExtractionStatus.Unsupported => "This file type was not supported when last indexed. Use Reindex Folder to retry.",
        TextExtractionStatus.TooLarge => "Content exceeded Recall's indexing size limits. Only the filename was indexed.",
        TextExtractionStatus.InvalidEncoding => "The text encoding could not be read during indexing.",
        TextExtractionStatus.Binary => "The file was detected as binary data and skipped for text indexing.",
        TextExtractionStatus.Changed => "The file changed while its text was being read. Reindex its folder to retry.",
        TextExtractionStatus.Unavailable => "The file could not be read when last indexed. Check its availability and permissions, then reindex its folder.",
        TextExtractionStatus.SymbolicLink => "Content was skipped because the file or its containing path was a symbolic link.",
        TextExtractionStatus.NoSearchableText when result.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            => "No searchable text was found in this PDF. Recall does not read scanned images as text.",
        TextExtractionStatus.NoSearchableText => "No searchable text was found in this document during indexing.",
        TextExtractionStatus.ParserFailed => "Text extraction failed for this document. Reindex its folder to retry.",
        _ => "No content indexing details are available for this file. Use Index → Indexed Folders → Reindex Folder to read its contents."
    };
    public string Size
    {
        get
        {
            var size = (double)result.Size;
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString("0.#", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }
}
