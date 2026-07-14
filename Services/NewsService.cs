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

    private readonly BuiltInCollectorService _collector = new();
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIFrontier",
        "cache");

    public async Task<NewsEdition> LoadAsync()
    {
        var configuration = await LoadConfigurationAsync();
        var localEdition = await LoadLocalEditionAsync();
        var remoteEdition = await TryLoadRemoteEditionAsync(configuration.RemoteNewsUrl);
        var edition = ChooseNewest(localEdition, remoteEdition);

        var lacksRequiredCategories = configuration.CategoryMinimums.Any(pair =>
            edition.Items.Count(item => item.Category == pair.Key) < pair.Value);
        if (edition.Items.Count < 10 || lacksRequiredCategories ||
            DateTimeOffset.Now - edition.GeneratedAt > TimeSpan.FromHours(configuration.StaleAfterHours))
        {
            var collected = await _collector.CollectAsync(configuration);
            edition.Items = Merge(edition.Items, collected, configuration);
            edition.GeneratedAt = DateTimeOffset.Now;
            edition.EditionDate = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            edition.WindowHours = 72;
        }

        foreach (var item in edition.Items)
        {
            EnrichForReading(item);
        }

        Directory.CreateDirectory(_cacheRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_cacheRoot, "news.json"),
            JsonSerializer.Serialize(edition, JsonOptions),
            new UTF8Encoding(false));
        return edition;
    }

    private static async Task<NewsEdition> LoadLocalEditionAsync()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "news.json");
        await using var stream = File.OpenRead(bundledPath);
        return await JsonSerializer.DeserializeAsync<NewsEdition>(stream, JsonOptions)
            ?? throw new InvalidDataException("news.json 内容为空。 ");
    }

    private static async Task<FeedConfiguration> LoadConfigurationAsync()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "source-feeds.json");
        await using var stream = File.OpenRead(bundledPath);
        var local = await JsonSerializer.DeserializeAsync<FeedConfiguration>(stream, JsonOptions) ?? new FeedConfiguration();

        if (!string.IsNullOrWhiteSpace(local.RemoteConfigUrl))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remote = await Http.GetFromJsonAsync<FeedConfiguration>(local.RemoteConfigUrl, JsonOptions, timeout.Token);
                if (remote?.Feeds.Count > 0)
                {
                    return remote;
                }
            }
            catch
            {
                // A bundled source list always remains available offline.
            }
        }
        return local;
    }

    private static async Task<NewsEdition?> TryLoadRemoteEditionAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await Http.GetFromJsonAsync<NewsEdition>(url, JsonOptions, timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    private static NewsEdition ChooseNewest(NewsEdition local, NewsEdition? remote) =>
        remote is { Items.Count: > 0 } && remote.GeneratedAt > local.GeneratedAt ? remote : local;

    private static List<NewsItem> Merge(
        IEnumerable<NewsItem> curated,
        IEnumerable<NewsItem> collected,
        FeedConfiguration configuration)
    {
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<NewsItem>();

        foreach (var item in curated.Concat(collected))
        {
            var normalizedTitle = new string(item.Title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if ((!string.IsNullOrWhiteSpace(item.SourceUrl) && !seenUrls.Add(item.SourceUrl)) || !seenTitles.Add(normalizedTitle))
            {
                continue;
            }
            merged.Add(item);
        }

        var ordered = merged.OrderByDescending(item => item.PublishedAt).ToList();
        var remaining = new List<NewsItem>(ordered);
        var balanced = new List<NewsItem>();
        foreach (var (category, minimum) in configuration.CategoryMinimums)
        {
            foreach (var item in remaining.Where(item => item.Category == category).Take(minimum).ToList())
            {
                balanced.Add(item);
                remaining.Remove(item);
            }
        }
        balanced.AddRange(remaining);
        return balanced.Take(Math.Max(10, configuration.MaxItems)).ToList();
    }

    private static void EnrichForReading(NewsItem item)
    {
        if (item.KeyFacts.Count == 0)
        {
            item.KeyFacts = item.Details.Take(4).ToList();
        }
        item.Context = string.IsNullOrWhiteSpace(item.Context)
            ? "这条新闻位于当前大模型与 Agent 从能力展示走向实际工作流的背景下，需要结合原始来源和后续验证理解。"
            : item.Context;
        item.BeginnerExplainer = string.IsNullOrWhiteSpace(item.BeginnerExplainer)
            ? "简单说，它关注的不是 AI 会不会回答一个问题，而是能力、工具和工程流程能否一起稳定完成真实任务。"
            : item.BeginnerExplainer;
        item.Impact = string.IsNullOrWhiteSpace(item.Impact) ? item.WhyItMatters : item.Impact;
        item.Limitations = string.IsNullOrWhiteSpace(item.Limitations)
            ? "当前结论只适用于来源明确披露的范围；论文、活动或趋势信号不等于技术已经在所有场景成熟。"
            : item.Limitations;
        item.WhatToWatch = string.IsNullOrWhiteSpace(item.WhatToWatch)
            ? "后续重点看官方文档、独立复现、成本变化、真实部署和失败案例。"
            : item.WhatToWatch;
        if (item.SourceTrail.Count == 0)
        {
            item.SourceTrail = [$"{item.SourceName}: {item.SourceUrl}"];
        }
        item.BriefSections ??= [];
        if (item.BriefSections.Count == 0)
        {
            item.BriefSections = BuildBriefSections(item);
        }
        item.FullBrief = string.Join(
            Environment.NewLine + Environment.NewLine,
            item.BriefSections.Select(section => $"{section.Title}{Environment.NewLine}{section.Body}"));
        item.ReadMinutes = Math.Max(item.ReadMinutes, Math.Clamp(item.FullBrief.Length / 300 + 2, 3, 8));
        EnrichTrendSignals(item);
    }

    private static void EnrichTrendSignals(NewsItem item)
    {
        var sourceKind = PreferenceService.NormalizeSource(item);
        item.IndependentSourceCount = Math.Max(item.IndependentSourceCount, Math.Max(1, item.SourceTrail.Count));
        item.IsPrimarySourceVerified = item.IsPrimarySourceVerified ||
            sourceKind is "官方公告" or "论文原文" or "GitHub";

        if (item.SourceQualityScore <= 0)
        {
            item.SourceQualityScore = Math.Clamp(
                (item.IsPrimarySourceVerified ? 0.65 : 0.35) +
                Math.Min(0.25, (item.IndependentSourceCount - 1) * 0.08) +
                (item.Confidence.Contains("高", StringComparison.OrdinalIgnoreCase) ? 0.10 : 0), 0, 1);
        }
        if (item.InnovationScore <= 0)
        {
            var noveltyTerms = new[] { "发布", "首次", "新", "突破", "开源", "方法", "基准", "刷新", "agent", "模型" };
            var matches = noveltyTerms.Count(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Summary.Contains(term, StringComparison.OrdinalIgnoreCase));
            item.InnovationScore = Math.Clamp(0.48 + matches * 0.07 +
                (item.Category is "重要研究" or "大模型" or "Agent" ? 0.08 : 0), 0, 1);
        }
        if (item.TechnicalRelevanceScore <= 0)
        {
            item.TechnicalRelevanceScore = item.Category switch
            {
                "重要研究" or "大模型" or "Agent" or "开源项目" => 0.90,
                "产业动态" => 0.68,
                _ => 0.58
            };
        }
        if (item.FreshnessScore <= 0)
        {
            var ageHours = DateTimeOffset.TryParse(item.PublishedAt, out var published)
                ? Math.Max(0, (DateTimeOffset.Now - published).TotalHours)
                : 36;
            item.FreshnessScore = Math.Exp(-Math.Log(2) * ageHours / 24d);
        }

        if (item.HotScore <= 0)
        {
            item.HotScore = item.HasDiscussionMetrics
                ? 100 * (0.40 * item.DiscussionScore + 0.15 * item.VelocityScore +
                    0.15 * item.SourceQualityScore + 0.15 * item.InnovationScore +
                    0.10 * item.TechnicalRelevanceScore + 0.05 * item.FreshnessScore)
                : 100 * (0.35 * item.SourceQualityScore + 0.35 * item.InnovationScore +
                    0.20 * item.TechnicalRelevanceScore + 0.10 * item.FreshnessScore);
            item.HotScore = Math.Round(item.HotScore, 1);
        }
        if (string.IsNullOrWhiteSpace(item.HeatReason))
        {
            var verification = item.IsPrimarySourceVerified ? "已核查一手来源" : "目前为可信二手来源";
            item.HeatReason = $"{verification}，{item.IndependentSourceCount} 组独立来源；统计截至 {DateTimeOffset.Now:MM-dd HH:mm}。";
        }
        item.HeatMeasuredAt ??= DateTimeOffset.Now;
    }

    private static List<BriefSection> BuildBriefSections(NewsItem item)
    {
        var facts = item.KeyFacts
            .Where(fact => !string.IsNullOrWhiteSpace(fact) &&
                !fact.Equals(item.Summary, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();
        var factText = facts.Count == 0
            ? "当前公开材料没有提供更多可独立列出的细节，具体数据和条件需要回到原始页面核对。"
            : string.Join("；", facts.Select(fact => fact.Trim().TrimEnd('。', '；') + "。"));

        return item.Category switch
        {
            "重要研究" =>
            [
                Section("研究背景", item.Context),
                Section("研究要解决的问题", item.Summary),
                Section("方法与实验思路", factText),
                Section("主要结果与贡献", $"{item.Impact} 当前材料表明这项工作的价值首先在于研究问题、方法或实验发现本身。"),
                Section("证据边界", $"{item.Limitations} 论文结论只覆盖作者给出的数据、任务和实验条件，仍需独立复现。")
            ],
            "开源项目" =>
            [
                Section("它想解决什么问题", item.Context),
                Section("项目怎样工作", item.Summary),
                Section("目前提供了什么", factText),
                Section("适合谁关注", $"{item.BeginnerExplainer} {item.Impact}"),
                Section("成熟度、成本与风险", $"{item.Limitations} {item.WhatToWatch}")
            ],
            "大模型" =>
            [
                Section("发布信息与适用范围", $"这条信息对应 {item.PublishedAt} 的公开资料，来源为 {item.SourceName}。{item.Summary}"),
                Section("这次更新了什么", factText),
                Section("能力和使用方式有什么变化", $"{item.BeginnerExplainer} {item.Impact}"),
                Section("行业背景", item.Context),
                Section("限制与待验证问题", $"{item.Limitations} {item.WhatToWatch}")
            ],
            "Agent" =>
            [
                Section("任务与使用场景", item.Context),
                Section("Agent 怎样完成任务", item.Summary),
                Section("关键能力与流程", factText),
                Section("可能带来的变化", $"{item.BeginnerExplainer} {item.Impact}"),
                Section("可靠性、权限与失败风险", $"{item.Limitations} {item.WhatToWatch}")
            ],
            _ =>
            [
                Section("新闻导语", item.Summary),
                Section("事件经过", factText),
                Section("参与方、时间与范围", $"这条信息对应 {item.PublishedAt} 的公开报道，来源为 {item.SourceName}。具体地点或市场范围以原始来源披露为准；原文未说明时不作猜测。"),
                Section("背景与原因", item.Context),
                Section("影响与下一步", $"{item.Impact} {item.WhatToWatch} {item.Limitations}")
            ]
        };
    }

    private static BriefSection Section(string title, string body) => new()
    {
        Title = title,
        Body = body.Trim()
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)");
        return client;
    }
}
