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
        var tasks = configuration.Feeds
            .Select(source => CollectFeedSafeAsync(source))
            .ToList();
        if (configuration.GitHubDiscovery.Enabled)
        {
            var queries = configuration.GitHubDiscovery.Queries.Count > 0
                ? configuration.GitHubDiscovery.Queries
                : [new GitHubDiscoveryQuery
                {
                    Query = configuration.GitHubDiscovery.Query,
                    Category = "????",
                    MinimumStars = configuration.GitHubDiscovery.MinimumStars,
                    MaxItems = configuration.GitHubDiscovery.MaxItems
                }];
            tasks.AddRange(queries.Select(query => CollectGitHubSafeAsync(configuration, query)));
        }
        var results = (await Task.WhenAll(tasks)).ToList();
        var freshCutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(24, configuration.FreshHours));
        var supplementCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, configuration.SupplementDays));
        var eligible = results
            .SelectMany(items => items)
            .Where(item => !DateTimeOffset.TryParse(item.PublishedAt, out var date) || date >= supplementCutoff)
            .ToList();
        foreach (var item in eligible.Where(item => DateTimeOffset.TryParse(item.PublishedAt, out var date) && date < freshCutoff))
        {
            if (!item.Tags.Contains("????")) item.Tags.Add("????");
        }
        var queues = results
            .Select(items => new Queue<NewsItem>(items
                .Where(item => eligible.Contains(item))
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

        return EnsureCategoryCoverage(selected, eligible, configuration);
    }

    private async Task<IReadOnlyList<NewsItem>> CollectGitHubSafeAsync(
        FeedConfiguration configuration,
        GitHubDiscoveryQuery discovery)
    {
        try
        {
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
                    : "????????????? README ?????????????";
                var language = repository.TryGetProperty("language", out var languageNode) && languageNode.ValueKind == JsonValueKind.String
                    ? languageNode.GetString() ?? "???"
                    : "???";
                var pushedAt = repository.GetProperty("pushed_at").GetString() ?? DateTimeOffset.UtcNow.ToString("O");
                var summary = $"{description}?? {stars:N0}??????{language}?";
                var category = InferCategory(discovery.Category, title, description);
                const string contentType = "????";
                output.Add(new NewsItem
                {
                    Id = Slug(link),
                    Category = category,
                    ContentType = contentType,
                    Topics = InferTopics(category, title, description, contentType),
                    Brand = "GitHub",
                    BrandColor = "#24292F",
                    LogoAsset = "Assets/Brands/github.svg",
                    Title = title,
                    Summary = summary,
                    PublishedAt = DateTimeOffset.Parse(pushedAt).ToLocalTime().ToString("yyyy-MM-dd"),
                    SourceName = $"GitHub {discovery.Category}????",
                    SourceUrl = link,
                    ReadMinutes = 4,
                    Confidence = "????",
                    WhyItMatters = WhyItMatters(discovery.Category),
                    KeyFacts = [$"GitHub ????? {stars:N0} ? Star?????? {language}?", description],
                    Context = "???????????????????????????? AI ??????",
                    BeginnerExplainer = BeginnerExplanation(discovery.Category),
                    Impact = WhyItMatters(discovery.Category),
                    Limitations = "Star ?????????????????????????????????????",
                    WhatToWatch = "???????Release?Issue ?????????????????????????????",
                    Details = [$"GitHub ????? {stars:N0} ? Star?????? {language}?", description],
                    SourceTrail = [$"GitHub repository API: {link}"],
                    Tags = [discovery.Category, "GitHub", "????", "????", "??????"]
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
            summary = "?????????????????????????????????";
        }

        var category = InferCategory(source.Category, title, description);
        var contentType = InferContentType(source, title, description);
        return new NewsItem
        {
            Id = Slug(link),
            Category = category,
            ContentType = contentType,
            Topics = InferTopics(category, title, description, contentType),
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
            WhyItMatters = WhyItMatters(category),
            KeyFacts = [summary],
            Context = "????????????????????? RSS?Atom ??? API ???",
            BeginnerExplainer = BeginnerExplanation(category),
            Impact = "???????????????????????????????????",
            Limitations = "?????????????????????????????????????",
            WhatToWatch = "???????????????????????????????????",
            Details = [summary, "???????????????? GitHub ???? Codex ?????????????"],
            SourceTrail = [$"{source.Name}: {link}"],
            Tags = [category, source.Brand, "????", "??????"]
        };
    }

    private static List<NewsItem> EnsureCategoryCoverage(
        IEnumerable<NewsItem> selected,
        IEnumerable<NewsItem> eligible,
        FeedConfiguration configuration)
    {
        var pool = eligible
            .Concat(selected)
            .GroupBy(item => item.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.PublishedAt)
            .ToList();
        var result = new List<NewsItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, minimum) in configuration.CategoryMinimums)
        {
            foreach (var item in pool.Where(item => MatchesCategory(item, category)).Take(minimum))
            {
                if (seen.Add(item.SourceUrl)) result.Add(item);
            }
        }
        foreach (var item in selected.Concat(pool))
        {
            if (result.Count >= configuration.MaxItems) break;
            if (seen.Add(item.SourceUrl)) result.Add(item);
        }
        return result.Take(configuration.MaxItems).ToList();
    }

    private static string InferCategory(string fallback, string title, string description)
    {
        var text = $"{title} {description}".ToLowerInvariant();
        var agentTerms = new[] { "agent", "agentic", "multi-agent", "tool use", "computer use", "???", "????" };
        if (agentTerms.Any(text.Contains)) return "Agent";
        var modelTerms = new[] { "large language model", "foundation model", " llm", "gpt-", "gemini", "claude", "reasoning model", "???", "????" };
        return modelTerms.Any(text.Contains) ? "???" : fallback;
    }

    private static string InferContentType(FeedSource source, string title, string description)
    {
        var text = $"{source.Name} {source.Url} {title} {description}".ToLowerInvariant();
        if (source.Trust.Contains("??", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("arxiv.org") || text.Contains("aclanthology.org"))
        {
            return "??";
        }
        if (text.Contains("github.com") || source.Category == "????" &&
            (text.Contains("open source") || text.Contains("??")))
        {
            return "????";
        }
        if (source.Category == "???" ||
            new[] { "model release", "introducing", "??", "????", "checkpoint", "weights" }.Any(text.Contains))
        {
            return "?????";
        }
        if (source.Category == "????")
        {
            return "????";
        }
        return "????";
    }

    private static List<string> InferTopics(
        string fallback,
        string title,
        string description,
        string contentType)
    {
        var text = $"{title} {description}".ToLowerInvariant();
        var topics = new List<string>();
        void Add(string topic)
        {
            if (!topics.Contains(topic, StringComparer.OrdinalIgnoreCase))
            {
                topics.Add(topic);
            }
        }

        Add(fallback);
        if (new[] { "agent", "agentic", "multi-agent", "tool use", "computer use", "???", "????" }.Any(text.Contains))
        {
            Add("Agent");
        }
        if (new[] { "large language model", "foundation model", " llm", "gpt-", "gemini", "claude", "reasoning model", "???", "????" }.Any(text.Contains))
        {
            Add("???");
        }
        if (contentType == "??")
        {
            Add("????");
        }
        if (contentType == "????")
        {
            Add("????");
        }
        return topics;
    }

    private static bool MatchesCategory(NewsItem item, string category) =>
        string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase) ||
        item.Topics?.Any(topic => string.Equals(topic, category, StringComparison.OrdinalIgnoreCase)) == true;

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
        value.Length <= length ? value : value[..length].TrimEnd() + "?";

    private static string Slug(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "feed-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeTitle(string title) =>
        NonWordRegex().Replace(title.ToLowerInvariant(), string.Empty);

    private static string WhyItMatters(string category) => category switch
    {
        "???" => "?????????????????????? AI ?????????????",
        "Agent" => "Agent ??????????????????????????????????",
        "????" => "?????????????????????????????????",
        "????" => "???????????????????????????????????",
        _ => "???????? AI ???????????????"
    };

    private static string BeginnerExplanation(string category) => category switch
    {
        "Agent" => "??? Agent ????????????????? AI ???",
        "????" => "????????????????????????????????????",
        "????" => "?????????????????????????????????",
        _ => "????????????????????????? AI ?????"
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
