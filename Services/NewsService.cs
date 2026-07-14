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

        if (edition.Items.Count < 10 || DateTimeOffset.Now - edition.GeneratedAt > TimeSpan.FromHours(configuration.StaleAfterHours))
        {
            var collected = await _collector.CollectAsync(configuration);
            edition.Items = Merge(edition.Items, collected, configuration.MaxItems);
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

    private static List<NewsItem> Merge(IEnumerable<NewsItem> curated, IEnumerable<NewsItem> collected, int maxItems)
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

        return merged
            .OrderByDescending(item => item.PublishedAt)
            .Take(Math.Max(10, maxItems))
            .ToList();
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
        item.FullBrief = string.IsNullOrWhiteSpace(item.FullBrief)
            ? BuildFullBrief(item)
            : item.FullBrief;
        item.ReadMinutes = Math.Max(item.ReadMinutes, Math.Clamp(item.FullBrief.Length / 300 + 2, 3, 8));
    }

    private static string BuildFullBrief(NewsItem item)
    {
        var facts = item.KeyFacts
            .Where(fact => !string.IsNullOrWhiteSpace(fact) &&
                !fact.Equals(item.Summary, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();
        var factText = facts.Count == 0
            ? "当前公开材料没有提供更多可独立列出的细节，具体数据和条件需要回到原始页面核对。"
            : string.Join("；", facts.Select(fact => fact.Trim().TrimEnd('。', '；') + "。"));
        var scope = item.Category switch
        {
            "开源项目" => "影响范围主要是公开仓库、开发者社区以及可能采用该项目的团队",
            "重要研究" => "影响范围首先是论文所定义的实验与研究场景，不能直接等同于成熟产品",
            "大模型" => "影响范围取决于模型实际开放地区、调用方式、价格和使用限制",
            "Agent" => "影响范围主要是多步骤任务、工具调用和自动化工作流",
            _ => "具体地点或市场范围以原始来源披露为准；若原文没有说明，这里不作猜测"
        };

        return $"""
            时间、参与方与范围：这条信息对应 {item.PublishedAt} 的公开资料，来源为 {item.SourceName}。{scope}。

            具体发生了什么：{item.Summary.Trim()} {factText}

            为什么会发生：{item.Context.Trim()}

            目前能看到的影响：{item.Impact.Trim()} 这表示它可能改变相关产品、研究或开发工作的选择，但实际影响仍取决于功能是否真正开放、结果能否复现，以及真实用户是否持续采用。

            阅读边界：{item.Limitations.Trim()} 因此，上述内容是根据当前公开材料整理出的事件全貌，不把论文结论、项目热度或厂商表述自动当成已经普遍成立的事实。
            """;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)");
        return client;
    }
}
