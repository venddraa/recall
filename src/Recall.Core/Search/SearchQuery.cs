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
        var letterOrDigitCount = 0;

        void CompleteTerm()
        {
            if (current.Length == 0)
            {
                return;
            }

            var term = $"\"{current}\"";
            terms.Add(letterOrDigitCount >= 3 ? term + "*" : term);
            current.Clear();
            letterOrDigitCount = 0;
        }

        foreach (var rune in query.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune);
                letterOrDigitCount++;
            }
            else if (current.Length > 0 && Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                // Marks extend an existing word; they cannot create an empty FTS
                // term or turn a two-letter word into a three-letter prefix.
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
