using System.Globalization;
using System.Text;

namespace Recall.Core.Search;

public static class SearchQuery
{
    public static string? BuildFtsExpression(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new List<string>();
        var current = new StringBuilder();

        void CompleteTerm()
        {
            if (current.Length == 0)
            {
                return;
            }

            terms.Add($"\"{current.ToString().Replace("\"", "\"\"")}\"");
            current.Clear();
        }

        foreach (var rune in query.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune)
                || category is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark)
            {
                current.Append(rune);
            }
            else
            {
                CompleteTerm();
            }
        }

        CompleteTerm();
        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }
}
