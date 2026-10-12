using System.Globalization;
using Recall.Core.Search;

namespace Recall.Desktop;

public sealed class SearchResultRow(SearchResult result)
{
    public string Filename => result.Filename;
    public string FullPath => result.FullPath;
    public string Folder => Path.GetDirectoryName(result.FullPath) ?? result.FullPath;
    public string Type => string.IsNullOrEmpty(result.Extension) ? "FILE" : result.Extension.TrimStart('.').ToUpperInvariant();
    public string Modified => result.ModifiedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Snippet => string.IsNullOrEmpty(result.Snippet)
        ? "This file matched its name. No extracted text is available for preview."
        : result.Snippet;
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
