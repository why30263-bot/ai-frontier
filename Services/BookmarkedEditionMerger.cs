using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Keeps locally bookmarked article bodies available when a newer cloud edition
/// replaces the main feed. Bookmark IDs alone are not sufficient for offline reading.
/// </summary>
public static class BookmarkedEditionMerger
{
    public static NewsEdition Preserve(
        NewsEdition incoming,
        NewsEdition existing,
        IReadOnlySet<string> bookmarkedIds)
    {
        if (bookmarkedIds.Count == 0)
        {
            return incoming;
        }

        var knownIds = incoming.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var knownUrls = incoming.Items
            .Select(item => NormalizeUrl(item.SourceUrl))
            .Where(url => url.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preserved = existing.Items.Where(item =>
            bookmarkedIds.Contains(item.Id) &&
            !knownIds.Contains(item.Id) &&
            !knownUrls.Contains(NormalizeUrl(item.SourceUrl)));

        incoming.Items = incoming.Items.Concat(preserved).ToList();
        return incoming;
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
