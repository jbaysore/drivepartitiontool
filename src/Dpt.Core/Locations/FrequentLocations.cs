namespace Dpt.Core.Locations;

public sealed record VisitStats(string Path, int Count, DateTimeOffset LastVisited);

/// <summary>Ranks visited folders by frequency and recency ("frecency").</summary>
public static class FrequentLocations
{
    /// <summary>Score = visits weighted by an exponential decay with a 7-day half-life.</summary>
    public static double Score(VisitStats v, DateTimeOffset now)
    {
        var ageDays = Math.Max(0, (now - v.LastVisited).TotalDays);
        return v.Count * Math.Pow(0.5, ageDays / 7.0);
    }

    public static IReadOnlyList<VisitStats> Rank(IEnumerable<VisitStats> visits, DateTimeOffset now, int take,
        IEnumerable<string>? exclude = null)
    {
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        return visits.Where(v => !excluded.Contains(v.Path))
            .OrderByDescending(v => Score(v, now))
            .ThenByDescending(v => v.LastVisited)
            .Take(take)
            .ToList();
    }

    /// <summary>Human label for last visit, matching the mockup ("Today", "Yesterday", "This week").</summary>
    public static string RecencyLabel(DateTimeOffset lastVisited, DateTimeOffset now)
    {
        var days = (now.Date - lastVisited.Date).TotalDays;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => "This week",
            < 31 => "This month",
            _ => lastVisited.ToString("MMM yyyy"),
        };
    }
}
