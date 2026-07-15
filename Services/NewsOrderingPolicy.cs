using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Applies the same arrival/read grouping to every channel after that channel's
/// own relevance ranking has been calculated.
/// </summary>
public static class NewsOrderingPolicy
{
    public static List<NewsItem> PrioritizeArrivalState(IEnumerable<NewsItem> rankedItems)
    {
        var ranked = rankedItems.ToList();
        var unreadNew = ranked
            .Where(item => item.IsNew)
            .OrderByDescending(item => item.AddedAt);
        var readRecent = ranked
            .Where(item => item.HasRecentArrival && item.IsRead)
            .OrderByDescending(item => item.AddedAt);
        var ordinary = ranked.Where(item => !item.HasRecentArrival);
        return unreadNew.Concat(readRecent).Concat(ordinary).ToList();
    }
}
