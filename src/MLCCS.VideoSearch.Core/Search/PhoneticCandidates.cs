using System.Globalization;
using System.Text;

namespace MLCCS.VideoSearch.Core.Search;

public sealed record PhoneticTerm(string Original, string Normalized, string Pinyin, string Initials, string Fuzzy);

public static class PhoneticCandidates
{
    public static IReadOnlyList<string> Expand(string queryPinyin, IEnumerable<PhoneticTerm> corpus, int maximum = 8, int tolerance = 2)
    {
        if (maximum is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(maximum));
        var normalized = NormalizeLatin(queryPinyin);
        return corpus.Select(term => new { term.Original, Distance = Math.Min(Distance(normalized, NormalizeLatin(term.Pinyin)), Distance(normalized, NormalizeLatin(term.Fuzzy))) })
            .Where(x => x.Distance <= tolerance && !string.Equals(x.Original, queryPinyin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Distance).ThenBy(x => x.Original, StringComparer.Ordinal)
            .Select(x => x.Original).Distinct(StringComparer.Ordinal).Take(maximum).ToArray();
    }

    public static string NormalizeLatin(string input)
    {
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Replace("zh", "z", StringComparison.Ordinal).Replace("ch", "c", StringComparison.Ordinal).Replace("sh", "s", StringComparison.Ordinal)
            .Replace('l', 'n').Replace("ang", "an", StringComparison.Ordinal).Replace("eng", "en", StringComparison.Ordinal);
    }

    private static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[right.Length];
    }
}

