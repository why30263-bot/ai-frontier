using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class ReadyNewsQueueService
{
    public const int TargetBufferSize = 10;
    public const int PublishBatchSize = 3;
    private const int MaximumBufferSize = 40;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AtomicJsonStore<ReadyNewsQueue> _store;

    public ReadyNewsQueueService(string? path = null)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIFrontier",
            "cache",
            "ready-news.json");
        _store = new AtomicJsonStore<ReadyNewsQueue>(path, JsonOptions);
    }

    public async Task<ReadyNewsQueue> LoadAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken) ?? new ReadyNewsQueue();

    public async Task<ReadyNewsMutationResult> EnqueueAsync(
        NewsItem item,
        CancellationToken cancellationToken = default)
    {
        if (!EditionQualityPolicy.IsQualifiedItem(item))
        {
            return new(true, false, await CountAsync(cancellationToken));
        }
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var queue = await LoadAsync(cancellationToken);
            var normalized = NormalizeUrl(item.SourceUrl);
            if (queue.Items.Any(existing =>
                string.Equals(existing.Id, item.Id, StringComparison.Ordinal) ||
                string.Equals(NormalizeUrl(existing.SourceUrl), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return new(true, false, queue.Items.Count);
            }
            var previousCount = queue.Items.Count;
            item.AddedAt = DateTimeOffset.Now;
            queue.Items.Add(item);
            queue.Items = queue.Items
                .Where(entry => entry.AddedAt is { } preparedAt &&
                    preparedAt >= DateTimeOffset.Now.AddDays(-15))
                .OrderByDescending(entry => ParseDate(entry.PublishedAt))
                .Take(MaximumBufferSize)
                .ToList();
            queue.UpdatedAt = DateTimeOffset.Now;
            var persisted = await _store.SaveAsync(queue, cancellationToken);
            var changed = persisted && queue.Items.Any(entry =>
                string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
            return new(persisted, changed, persisted ? queue.Items.Count : previousCount);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<NewsItem>> PeekAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken);
        return queue.Items.Take(Math.Max(0, count)).ToList();
    }

    public async Task<ReadyNewsMutationResult> RemoveAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        var remove = ids.ToHashSet(StringComparer.Ordinal);
        return await RemoveWhereAsync(item => remove.Contains(item.Id), cancellationToken);
    }

    public async Task<ReadyNewsMutationResult> ReconcilePublishedAsync(
        IEnumerable<NewsItem> published,
        CancellationToken cancellationToken = default)
    {
        var publishedItems = published.ToList();
        var ids = publishedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var urls = publishedItems
            .Select(item => NormalizeUrl(item.SourceUrl))
            .Where(url => url.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await RemoveWhereAsync(
            item => ids.Contains(item.Id) || urls.Contains(NormalizeUrl(item.SourceUrl)),
            cancellationToken);
    }

    private async Task<ReadyNewsMutationResult> RemoveWhereAsync(
        Func<NewsItem, bool> shouldRemove,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var queue = await LoadAsync(cancellationToken);
            var previousCount = queue.Items.Count;
            queue.Items.RemoveAll(item => shouldRemove(item));
            if (queue.Items.Count == previousCount)
            {
                return new(true, false, previousCount);
            }
            queue.UpdatedAt = DateTimeOffset.Now;
            var persisted = await _store.SaveAsync(queue, cancellationToken);
            return new(persisted, persisted, persisted ? queue.Items.Count : previousCount);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).Items.Count;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

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
