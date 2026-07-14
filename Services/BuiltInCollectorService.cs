using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed partial class BuiltInCollectorService
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<IReadOnlyList<NewsItem>> CollectAsync(FeedConfiguration configuration)
    {
        var tasks = configuration.Feeds.Select(CollectFeedSafeAsync).ToArray();
        var results = (await Task.WhenAll(tasks)).ToList();
        if (configuration.GitHubDiscovery.Enabled)
        {
            results.Add(await CollectGitHubSafeAsync(configuration));
        }
        var freshCutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(24, configuration.FreshHours));
        var supplementCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, configuration.SupplementDays));
        var freshCount = results.Sum(items => items.Count(item => !DateTimeOffset.TryParse(item.PublishedAt, out var date) || date >= freshCutoff));
        var queues = results
            .Select(items => new Queue<NewsItem>(items
                .Where(item => !DateTimeOffset.TryParse(item.PublishedAt, out var date) ||
                    date >= freshCutoff || (freshCount < configuration.MaxItems && date >= supplementCutoff))
                .OrderByDescending(item => item.PublishedAt)
                .Take(Math.Max(1, configuration.MaxItemsPerSource))))
            .Where(queue => queue.Count > 0)
            .ToList();
        var selected = new List<NewsItem>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (selected.Count < configuration.MaxItems && queues.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues)
            {
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    if (seenTitles.Add(NormalizeTitle(candidate.Title)))
                    {
                        selected.Add(candidate);
                        break;
                    }
                }
                if (selected.Count >= configuration.MaxItems)
                {
                    break;
                }
            }
        }

        return selected.OrderByDescending(item => item.PublishedAt).ToList();
    }

    private async Task<IReadOnlyList<NewsItem>> CollectGitHubSafeAsync(FeedConfiguration configuration)
    {
        try
        {
            var discovery = configuration.GitHubDiscovery;
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, configuration.SupplementDays)).ToString("yyyy-MM-dd");
            var query = Uri.EscapeDataString($"{discovery.Query} pushed:>={since}");
            var url = $"https://api.github.com/search/repositories?q={query}&sort=stars&order=desc&per_page={Math.Clamp(discovery.MaxItems * 2, 1, 10)}";
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var output = new List<NewsItem>();
            foreach (var repository in document.RootElement.GetProperty("items").EnumerateArray())
            {
                var stars = repository.GetProperty("stargazers_count").GetInt32();
                if (stars < discovery.MinimumStars)
                {
                    continue;
                }
                var title = repository.GetProperty("full_name").GetString() ?? string.Empty;
                var link = repository.GetProperty("html_url").GetString() ?? string.Empty;
                var description = repository.TryGetProperty("description", out var descriptionNode) && descriptionNode.ValueKind == JsonValueKind.String
                    ? descriptionNode.GetString() ?? string.Empty
                    : "仓库近期保持活跃，建议打开 README 与提交记录进一步判断用途。";
                var language = repository.TryGetProperty("language", out var languageNode) && languageNode.ValueKind == JsonValueKind.String
                    ? languageNode.GetString() ?? "未标注"
                    : "未标注";
                var pushedAt = repository.GetProperty("pushed_at").GetString() ?? DateTimeOffset.UtcNow.ToString("O");
                var summary = $"{description}（★ {stars:N0}，主要语言：{language}）";
                output.Add(new NewsItem
                {
                    Id = Slug(link),
                    Category = "开源项目",
                    Brand = "GitHub",
                    BrandColor = "#24292F",
                    LogoAsset = "Assets/Brands/github.svg",
                    Title = title,
                    Summary = summary,
                    PublishedAt = DateTimeOffset.Parse(pushedAt).ToLocalTime().ToString("yyyy-MM-dd"),
                    SourceName = "GitHub 项目发现",
                    SourceUrl = link,
                    ReadMinutes = 4,
                    Confidence = "项目仓库",
                    WhyItMatters = WhyItMatters("开源项目"),
                    KeyFacts = [$"GitHub 当前显示约 {stars:N0} 个 Star，主要语言为 {language}。", description],
                    Context = "该仓库由内置发现器从近期仍在更新、且已有一定社区关注度的 AI 项目中筛选。",
                    BeginnerExplainer = BeginnerExplanation("开源项目"),
                    Impact = WhyItMatters("开源项目"),
                    Limitations = "Star 数和近期推送只能反映关注度与活跃信号，不证明项目安全、稳定或适合生产环境。",
                    WhatToWatch = "检查最近提交、Release、Issue 响应、许可证、安装复现和实际资源消耗，再决定是否投入学习。",
                    Details = [$"GitHub 当前显示约 {stars:N0} 个 Star，主要语言为 {language}。", description],
                    SourceTrail = [$"GitHub repository API: {link}"],
                    Tags = ["开源项目", "GitHub", "近期活跃", "自动采集"]
                });
            }
            return output.Take(discovery.MaxItems).ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<NewsItem>> CollectFeedSafeAsync(FeedSource source)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync();
            return ParseFeed(xml, source);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<NewsItem> ParseFeed(string xml, FeedSource source)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null)
        {
            return [];
        }

        var isAtom = root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase);
        var entries = isAtom
            ? root.Elements().Where(element => element.Name.LocalName == "entry")
            : root.Descendants().Where(element => element.Name.LocalName == "item");

        return entries.Take(24).Select(entry => CreateItem(entry, source, isAtom)).Where(item => item is not null).Cast<NewsItem>().ToList();
    }

    private static NewsItem? CreateItem(XElement entry, FeedSource source, bool isAtom)
    {
        var title = Value(entry, "title");
        var link = isAtom
            ? entry.Elements().FirstOrDefault(element => element.Name.LocalName == "link")?.Attribute("href")?.Value ?? string.Empty
            : Value(entry, "link");
        var published = FirstValue(entry, "published", "updated", "pubDate", "date");
        var description = FirstValue(entry, "summary", "description", "content");
        description = CleanText(description);

        if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(link, UriKind.Absolute, out _))
        {
            return null;
        }

        var date = DateTimeOffset.TryParse(published, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var summary = Truncate(description, 240);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "由客户端从官方信息源自动发现。打开详情可查看来源和进一步核查提示。";
        }

        return new NewsItem
        {
            Id = Slug(link),
            Category = source.Category,
            Brand = source.Brand,
            BrandColor = source.BrandColor,
            LogoAsset = source.LogoAsset,
            Title = WebUtility.HtmlDecode(title.Trim()),
            Summary = summary,
            PublishedAt = date.ToLocalTime().ToString("yyyy-MM-dd"),
            SourceName = source.Name,
            SourceUrl = link,
            ReadMinutes = Math.Clamp((description.Length / 350) + 2, 2, 8),
            Confidence = source.Trust,
            WhyItMatters = WhyItMatters(source.Category),
            KeyFacts = [summary],
            Context = "这条信息由应用内置采集器直接从配置中的官方 RSS、Atom 或公开 API 获取。",
            BeginnerExplainer = BeginnerExplanation(source.Category),
            Impact = "是否形成实际影响仍取决于原文披露的能力、开放范围、成本和后续采用情况。",
            Limitations = "自动采集只能确认来源发布了该内容，不能替代人工复核、跨来源验证或专业测评。",
            WhatToWatch = "继续观察官方文档、代码仓库、评测结果和真实用户反馈是否出现可验证更新。",
            Details = [summary, "本条为内置采集器的保底结果；连接 GitHub 编辑源或 Codex 后可获得更完整的中文分析。"],
            SourceTrail = [$"{source.Name}: {link}"],
            Tags = [source.Category, source.Brand, "自动采集"]
        };
    }

    private static string Value(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim() ?? string.Empty;

    private static string FirstValue(XElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Value(parent, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static string CleanText(string value)
    {
        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WhiteSpaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length].TrimEnd() + "…";

    private static string Slug(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "feed-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeTitle(string title) =>
        NonWordRegex().Replace(title.ToLowerInvariant(), string.Empty);

    private static string WhyItMatters(string category) => category switch
    {
        "大模型" => "模型能力、价格或开放方式的变化，可能影响现有 AI 产品的能力边界与使用成本。",
        "Agent" => "Agent 关注模型能否持续规划、调用工具并完成真实任务，而不只是回答一个问题。",
        "开源项目" => "开源实现能让更多开发者复现、修改和检验方法，但热度不等于生产可用。",
        "重要研究" => "研究结果可能改变对模型能力或限制的理解，但论文结论仍需复现和同行检验。",
        _ => "这条信息可能影响 AI 产品、开发生态或行业采用节奏。"
    };

    private static string BeginnerExplanation(string category) => category switch
    {
        "Agent" => "可以把 Agent 理解为会围绕目标连续执行多步操作的 AI 系统。",
        "开源项目" => "开源表示代码或模型可以被公开检查和二次开发，但仍要查看许可证与维护状态。",
        "重要研究" => "论文是研究团队对方法和实验的正式描述，不等于技术已经成为成熟产品。",
        _ => "大模型是能够处理和生成文本、图像、音频或代码的通用 AI 基础系统。"
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)");
        return client;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhiteSpaceRegex();

    [GeneratedRegex("[^a-z0-9\\u4e00-\\u9fff]+")]
    private static partial Regex NonWordRegex();
}
