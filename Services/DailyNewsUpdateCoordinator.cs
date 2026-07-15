using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class DailyNewsUpdateCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private readonly AtomicJsonStore<LocalNewsUpdateSettings> _settingsStore;
    private readonly QualifiedEditionStore _editionStore;
    private readonly CloudFeedHealthService _cloud = new();
    private readonly LocalCodexNewsUpdater _local = new();
    private readonly EditionQualityPolicy _policy = new();
    private readonly ReadyNewsQueueService _readyQueue = new();
    private readonly IncrementalEditionComposer _composer = new();
    private readonly PreferenceService _preferences = new();

    public DailyNewsUpdateCoordinator()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIFrontier");
        _settingsStore = new AtomicJsonStore<LocalNewsUpdateSettings>(
            Path.Combine(root, "local-news-update.json"),
            JsonOptions);
        _editionStore = new QualifiedEditionStore(
            Path.Combine(root, "cache", "qualified-v2", "news.json"),
            _policy,
            JsonOptions);
    }

    public async Task<LocalNewsUpdateSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        await _settingsStore.LoadAsync(cancellationToken) ?? new LocalNewsUpdateSettings();

    public async Task SavePreferencesAsync(
        bool personalMode,
        bool fallbackEnabled,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            settings.Mode = personalMode ? NewsUpdateMode.LocalCodexOnly : NewsUpdateMode.CloudPreferred;
            settings.LocalFallbackEnabled = fallbackEnabled;
            await _settingsStore.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<DailyNewsUpdateResult> RunAsync(
        NewsEdition displayedEdition,
        bool force,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            var useLocal = settings.Mode == NewsUpdateMode.LocalCodexOnly;
            if (!useLocal)
            {
                reportStatus?.Invoke("正在检查云端今日资讯…");
                var cloud = await _cloud.CheckAsync(cancellationToken);
                settings.LastCloudCheckAt = DateTimeOffset.Now;
                var route = DailyNewsUpdatePolicy.Decide(
                    settings.Mode,
                    settings.LocalFallbackEnabled,
                    settings.ConsecutiveCloudFailures + 1,
                    cloud);
                if (route == DailyNewsUpdateRoute.UseCloud)
                {
                    settings.ConsecutiveCloudFailures = 0;
                    settings.LastCloudSuccessAt = DateTimeOffset.Now;
                    var remoteIsNewer = cloud.Edition is not null &&
                        _policy.ChooseNewest(displayedEdition, cloud.Edition) == cloud.Edition;
                    if (remoteIsNewer)
                    {
                        var profile = await _preferences.LoadAsync();
                        var bookmarked = (profile.BookmarkedIds ?? []).ToHashSet(StringComparer.Ordinal);
                        cloud = cloud with
                        {
                            Edition = BookmarkedEditionMerger.Preserve(
                                cloud.Edition!,
                                displayedEdition,
                                bookmarked)
                        };
                    }
                    var cloudPublished = !remoteIsNewer ||
                        await _editionStore.SaveAsync(cloud.Edition!, cancellationToken);
                    var changed = remoteIsNewer && cloudPublished;
                    if (cloudPublished)
                    {
                        settings.LastStatus = cloud.Status;
                    }
                    else
                    {
                        settings.LastStatus = "云端资讯已获取，但暂时无法写入本机缓存；稍后会自动重试。";
                    }
                    await _settingsStore.SaveAsync(settings, cancellationToken);
                    return new(true, changed, false, CodexIntegrationService.FindCodexExecutable() is not null, settings.LastStatus);
                }

                settings.ConsecutiveCloudFailures++;
                useLocal = route == DailyNewsUpdateRoute.UseLocalCodex;
                if (!useLocal)
                {
                    settings.LastStatus = "云端资讯暂未更新，将在下次启动时再次检查。";
                    await _settingsStore.SaveAsync(settings, cancellationToken);
                    return new(true, false, false, CodexIntegrationService.FindCodexExecutable() is not null, settings.LastStatus);
                }
            }

            if (!force &&
                settings.LastLocalAttemptAt is { } lastAttempt &&
                DateTimeOffset.Now - lastAttempt < TimeSpan.FromMinutes(5))
            {
                var count = await _readyQueue.CountAsync(cancellationToken);
                var connected = CodexIntegrationService.FindCodexExecutable() is not null;
                return new(false, false, false, connected, $"已有 {count} 篇最新资讯就绪 · 后台稍后继续补充");
            }

            var readyQueue = await _readyQueue.LoadAsync(cancellationToken);
            if (readyQueue.Items.Count >= ReadyNewsQueueService.TargetBufferSize)
            {
                return new(true, false, true, true, $"已有 {readyQueue.Items.Count} 篇最新资讯就绪");
            }

            reportStatus?.Invoke($"已有 {readyQueue.Items.Count} 篇最新资讯就绪 · 后台正在补充…");
            settings.LastLocalAttemptAt = DateTimeOffset.Now;
            settings.LastStatus = $"已有 {readyQueue.Items.Count} 篇最新资讯就绪 · 后台正在补充…";
            await _settingsStore.SaveAsync(settings, cancellationToken);
            var local = await _local.UpdateAsync(
                displayedEdition,
                readyQueue.Items,
                ReadyNewsQueueService.TargetBufferSize,
                (item, token) => _readyQueue.EnqueueAsync(item, token),
                reportStatus,
                cancellationToken);
            if (local.Success)
            {
                settings.LastLocalSuccessAt = DateTimeOffset.Now;
                settings.LastCompletedLocalDate = today;
                settings.LastCompletedMode = settings.Mode;
                settings.LastStatus = local.Status;
            }
            else
            {
                settings.LastStatus = local.Status;
            }
            await _settingsStore.SaveAsync(settings, cancellationToken);
            return new(true, false, true, local.CodexConnected, settings.LastStatus);
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<int> GetReadyCountAsync(CancellationToken cancellationToken = default) =>
        _readyQueue.CountAsync(cancellationToken);

    public async Task<ReadyNewsPromotionResult> PublishReadyAsync(
        NewsEdition displayedEdition,
        int count = ReadyNewsQueueService.PublishBatchSize,
        CancellationToken cancellationToken = default)
    {
        await PublishGate.WaitAsync(cancellationToken);
        try
        {
            var reconciled = await _readyQueue.ReconcilePublishedAsync(
                displayedEdition.Items,
                cancellationToken);
            if (!reconciled.Persisted)
            {
                return new(0, reconciled.Count, false, "后台资讯池暂时无法同步，稍后会自动重试");
            }

            var ready = await _readyQueue.PeekAsync(count, cancellationToken);
            if (ready.Count == 0)
            {
                return new(0, reconciled.Count, false, "后台还没有已就绪的新资讯，正在继续准备");
            }

            var profile = await _preferences.LoadAsync();
            var bookmarked = (profile.BookmarkedIds ?? []).ToHashSet(StringComparer.Ordinal);
            var merge = _composer.TryMerge(displayedEdition, ready, bookmarked);
            if (merge.Edition is null || merge.AcceptedCount == 0 ||
                !await _editionStore.SaveAsync(merge.Edition, cancellationToken))
            {
                var remainingOnFailure = await _readyQueue.CountAsync(cancellationToken);
                return new(0, remainingOnFailure, false, "新资讯暂时无法发布，已继续保留在后台资讯池");
            }

            var oldIds = displayedEdition.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var publishedIds = merge.Edition.Items
                .Where(item => !oldIds.Contains(item.Id))
                .Select(item => item.Id)
                .Take(merge.AcceptedCount)
                .ToList();
            var removal = await _readyQueue.RemoveAsync(publishedIds, cancellationToken);
            var status = removal.Persisted
                ? $"已发布 {publishedIds.Count} 篇最新资讯 · 后台还有 {removal.Count} 篇就绪"
                : $"已发布 {publishedIds.Count} 篇最新资讯 · 后台队列将在下次刷新时自动同步";
            return new(
                publishedIds.Count,
                removal.Count,
                publishedIds.Count > 0,
                status);
        }
        finally
        {
            PublishGate.Release();
        }
    }
}
