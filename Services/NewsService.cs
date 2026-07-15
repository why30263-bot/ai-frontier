using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class NewsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly SemaphoreSlim BackgroundRefreshGate = new(1, 1);
    private readonly BuiltInCollectorService _collector;
    private readonly EditionQualityPolicy _editionPolicy;
    private readonly QualifiedEditionStore _qualifiedEditionStore;
    private readonly PreferenceService _preferences;
    private readonly string _cacheRoot;

    private string CandidateCachePath => Path.Combine(_cacheRoot, "candidates", "discovered.json");

    public NewsService()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIFrontier",
            "cache");
        _collector = new BuiltInCollectorService();
        _editionPolicy = new EditionQualityPolicy();
        _preferences = new PreferenceService();
        _qualifiedEditionStore = new QualifiedEditionStore(
            Path.Combine(_cacheRoot, "qualified-v2", "news.json"),
            _editionPolicy,
            JsonOptions);
    }

    public async Task<NewsEdition> LoadAsync(bool refreshRemote = true)
    {
        // First paint is a local-only operation. Network configuration, remote
        // editions and candidate discovery are deliberately deferred.
        var configurationTask = LoadBundledConfigurationAsync();
        var bundledEditionTask = TryLoadLocalEditionAsync();
        var cachedEditionTask = _qualifiedEditionStore.LoadAsync();
        await Task.WhenAll(configurationTask, bundledEditionTask, cachedEditionTask);

        var configuration = await configurationTask;
        var bundledEdition = await bundledEditionTask;
        var cachedEdition = await cachedEditionTask;

        // Fail closed: only an entire, validated Chinese edition can enter the reading flow.
        // Raw RSS/GitHub candidates and legacy caches are never considered here.
        var edition = _editionPolicy.ChooseNewest(bundledEdition, cachedEdition)
            ?? _editionPolicy.CreateEmptyEdition();

        foreach (var item in edition.Items)
        {
            PrepareForReading(item);
        }

        if (refreshRemote)
        {
            _ = RefreshRemoteSourcesInBackgroundAsync(configuration, edition);
        }

        return edition;
    }

    private async Task RefreshRemoteSourcesInBackgroundAsync(
        FeedConfiguration bundledConfiguration,
        NewsEdition displayedEdition)
    {
        if (!await BackgroundRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var configuration = await TryLoadRemoteConfigurationAsync(bundledConfiguration)
                ?? bundledConfiguration;
            var remoteEdition = await TryLoadRemoteEditionAsync(configuration.RemoteNewsUrl);
            var newestEdition = _editionPolicy.ChooseNewest(displayedEdition, remoteEdition)
                ?? displayedEdition;

            if (ReferenceEquals(newestEdition, remoteEdition))
            {
                // The next refresh/load observes the new edition. Current reading is
                // never interrupted by a background network result.
                var profile = await _preferences.LoadAsync();
                var bookmarked = (profile.BookmarkedIds ?? []).ToHashSet(StringComparer.Ordinal);
                remoteEdition = BookmarkedEditionMerger.Preserve(
                    remoteEdition!,
                    displayedEdition,
                    bookmarked);
                newestEdition = remoteEdition;
                _ = await _qualifiedEditionStore.SaveAsync(remoteEdition);
            }

            var lacksRequiredCategories = configuration.CategoryMinimums.Any(pair =>
                newestEdition.Items.Count(item => MatchesCategory(item, pair.Key)) < pair.Value);
            var isStale = newestEdition.GeneratedAt == default ||
                DateTimeOffset.Now - newestEdition.GeneratedAt > TimeSpan.FromHours(configuration.StaleAfterHours);
            if (newestEdition.Items.Count >= 10 && !lacksRequiredCategories && !isStale)
            {
                return;
            }

            var collected = await _collector.CollectAsync(configuration);
            var candidateEdition = new NewsEdition
            {
                // Candidate snapshots deliberately cannot pass the qualified-edition gate.
                SchemaVersion = 0,
                EditionDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                WindowHours = Math.Max(24, configuration.FreshHours),
                GeneratedAt = DateTimeOffset.Now,
                Items = collected.ToList()
            };

            _ = await new AtomicJsonStore<NewsEdition>(CandidateCachePath, JsonOptions)
                .SaveAsync(candidateEdition);
        }
        catch
        {
            // A collection failure never replaces the last qualified Chinese edition.
        }
        finally
        {
            BackgroundRefreshGate.Release();
        }
    }

    private async Task<NewsEdition?> TryLoadLocalEditionAsync()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "news.json");
        if (!File.Exists(bundledPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(bundledPath);
            var edition = await JsonSerializer.DeserializeAsync<NewsEdition>(stream, JsonOptions);
            return _editionPolicy.IsQualified(edition) ? edition : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<FeedConfiguration> LoadBundledConfigurationAsync()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "source-feeds.json");
        try
        {
            await using var stream = File.OpenRead(bundledPath);
            return await JsonSerializer.DeserializeAsync<FeedConfiguration>(stream, JsonOptions)
                ?? new FeedConfiguration();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FeedConfiguration();
        }
    }

    private static async Task<FeedConfiguration?> TryLoadRemoteConfigurationAsync(FeedConfiguration local)
    {
        if (string.IsNullOrWhiteSpace(local.RemoteConfigUrl))
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var remote = await Http.GetFromJsonAsync<FeedConfiguration>(
                local.RemoteConfigUrl,
                JsonOptions,
                timeout.Token);
            if (remote?.Feeds.Count > 0)
            {
                return remote;
            }
        }
        catch
        {
            // The bundled source list remains available offline.
        }

        return null;
    }

    private async Task<NewsEdition?> TryLoadRemoteEditionAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var edition = await Http.GetFromJsonAsync<NewsEdition>(url, JsonOptions, timeout.Token);
            return _editionPolicy.IsQualified(edition) ? edition : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesCategory(NewsItem item, string category) =>
        string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase) ||
        item.Topics?.Any(topic => string.Equals(topic, category, StringComparison.OrdinalIgnoreCase)) == true;

    private static void PrepareForReading(NewsItem item)
    {
        item.Topics = item.Topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        item.FullBrief = string.Join(
            Environment.NewLine + Environment.NewLine,
            item.BriefSections.Select(section => $"{section.Title}{Environment.NewLine}{section.Body}"));
        item.ReadMinutes = Math.Max(1, Math.Clamp(item.FullBrief.Length / 350 + 1, 2, 10));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier/1.2 (+https://github.com/why30263-bot/ai-frontier)");
        return client;
    }
}
