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
    private readonly AtomicJsonStore<LocalNewsUpdateSettings> _settingsStore;
    private readonly QualifiedEditionStore _editionStore;
    private readonly CloudFeedHealthService _cloud = new();
    private readonly LocalCodexNewsUpdater _local = new();
    private readonly EditionQualityPolicy _policy = new();

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
                    var cloudPublished = !remoteIsNewer ||
                        await _editionStore.SaveAsync(cloud.Edition!, cancellationToken);
                    var changed = remoteIsNewer && cloudPublished;
                    if (cloudPublished)
                    {
                        settings.LastCompletedLocalDate = today;
                        settings.LastCompletedMode = NewsUpdateMode.CloudPreferred;
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
                DateTimeOffset.Now - lastAttempt < TimeSpan.FromHours(2))
            {
                var connected = CodexIntegrationService.FindCodexExecutable() is not null;
                return new(false, false, false, connected, "本机更新刚刚尝试过，稍后会自动重试；上一期资讯仍可正常阅读。");
            }

            reportStatus?.Invoke("已连接 Codex · 正在更新今日资讯…");
            settings.LastLocalAttemptAt = DateTimeOffset.Now;
            settings.LastStatus = "已连接 Codex · 正在更新今日资讯…";
            await _settingsStore.SaveAsync(settings, cancellationToken);
            var local = await _local.UpdateAsync(displayedEdition, reportStatus, cancellationToken);
            reportStatus?.Invoke(local.Success ? "内容已通过检查 · 正在发布到本机资讯流…" : local.Status);
            var completed = local.Success && local.Edition is not null;
            var published = completed && (!local.Changed ||
                await _editionStore.SaveAsync(local.Edition!, cancellationToken));
            if (published)
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
            return new(true, published && local.Changed, true, local.CodexConnected, settings.LastStatus);
        }
        finally
        {
            Gate.Release();
        }
    }
}
