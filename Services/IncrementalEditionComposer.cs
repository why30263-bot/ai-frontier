using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed record IncrementalMergeResult(NewsEdition? Edition, int AcceptedCount);

/// <summary>
/// Publishes ready articles at the top of a growing local pool. Only ordinary
/// unbookmarked articles older than fifteen days are removed.
/// </summary>
public sealed class IncrementalEditionComposer
{
    private readonly EditionQualityPolicy _policy;
    private readonly Func<DateTimeOffset> _now;

    public IncrementalEditionComposer(
        EditionQualityPolicy? policy = null,
        Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _policy = policy ?? new EditionQualityPolicy(_now);
    }

    public IncrementalMergeResult TryMerge(
        NewsEdition currentEdition,
        IReadOnlyList<NewsItem> generated,
        IReadOnlySet<string>? bookmarkedIds = null)
    {
        if (!_policy.IsQualified(currentEdition) || generated.Count == 0)
        {
            return new(null, 0);
        }

        bookmarkedIds ??= new HashSet<string>(StringComparer.Ordinal);
        var now = _now();
        var cutoff = now.AddDays(-15);
        var retained = currentEdition.Items
            .Where(item => bookmarkedIds.Contains(item.Id) || RetentionDate(item) >= cutoff)
            .ToList();
        var knownIds = retained.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var knownUrls = retained
            .Select(item => NormalizeUrl(item.SourceUrl))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptedItems = new List<NewsItem>();

        foreach (var item in generated.OrderByDescending(entry => ParseDate(entry.PublishedAt)))
        {
            var normalizedUrl = NormalizeUrl(item.SourceUrl);
            if (!EditionQualityPolicy.IsQualifiedItem(item) ||
                normalizedUrl.Length == 0 ||
                knownIds.Contains(item.Id) ||
                knownUrls.Contains(normalizedUrl))
            {
                continue;
            }
            item.AddedAt = now;
            acceptedItems.Add(item);
            knownIds.Add(item.Id);
            knownUrls.Add(normalizedUrl);
        }

        if (acceptedItems.Count == 0)
        {
            return new(currentEdition, 0);
        }

        var edition = new NewsEdition
        {
            SchemaVersion = EditionQualityPolicy.QualifiedSchemaVersion,
            EditionDate = now.ToString("yyyy-MM-dd"),
            WindowHours = 336,
            GeneratedAt = now,
            Items = acceptedItems.Concat(retained).ToList()
        };
        return _policy.IsQualified(edition)
            ? new(edition, acceptedItems.Count)
            : new(null, 0);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static DateTimeOffset RetentionDate(NewsItem item) =>
        item.AddedAt ?? ParseDate(item.PublishedAt);

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
