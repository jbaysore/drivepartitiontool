namespace Dpt.Core.Search;

/// <summary>
/// Orders matches: exact name &gt; name starts with the query &gt; a word in the name starts with it &gt; substring,
/// then recently modified, then items in frequently visited folders.
/// </summary>
public static class SearchRanking
{
    public static double Score(ReadOnlySpan<char> name, IReadOnlyList<string> terms, DateTime modifiedUtc, DateTime nowUtc, double locationBoost, bool isFolder)
    {
        double score = 0;
        var stem = name;
        var dot = name.LastIndexOf('.');
        if (dot > 0 && !isFolder) stem = name[..dot];

        foreach (var term in terms)
        {
            if (stem.Equals(term, StringComparison.OrdinalIgnoreCase) || name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (StartsAtWordBoundary(name, term)) score += 40;
            else score += 15;
        }

        if (terms.Count > 0) score /= terms.Count;

        // Recency: up to +20, halving every 30 days.
        var ageDays = Math.Max(0, (nowUtc - modifiedUtc).TotalDays);
        score += 20 * Math.Pow(0.5, ageDays / 30);

        // Frequently visited locations (computed by the app): up to +15.
        score += Math.Clamp(locationBoost, 0, 1) * 15;

        // Shorter names are slightly more likely to be what was meant.
        score -= Math.Min(name.Length, 80) * 0.05;
        if (isFolder) score += 2;
        return score;
    }

    public static bool StartsAtWordBoundary(ReadOnlySpan<char> name, string term)
    {
        var start = 0;
        while (true)
        {
            var i = name[start..].IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var at = start + i;
            if (at == 0) return true;
            var prev = name[at - 1];
            if (!char.IsLetterOrDigit(prev) || (char.IsLower(prev) && char.IsUpper(name[at]))) return true;
            start = at + 1;
            if (start >= name.Length) return false;
        }
    }
}
