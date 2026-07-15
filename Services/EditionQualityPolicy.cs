using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Defines the single publishing boundary for editions that may enter the reader.
/// Keep editorial validation independent from transport and storage so every source
/// (bundled, cached, or remote) is evaluated by exactly the same rules.
/// </summary>
public sealed class EditionQualityPolicy
{
    public const int QualifiedSchemaVersion = 2;
    public const int BatchSize = 10;
    public const int MinimumVisibleItems = BatchSize * 2;

    private static readonly HashSet<string> ContentTypes =
        ["论文", "开源项目", "模型发布", "Agent产品", "产业事件"];

    private static readonly HashSet<string> AllowedTopics =
        ["大模型", "Agent", "重要研究", "开源项目", "产业动态"];

    private static readonly string[] BannedReaderHeadings =
    [
        "完整报道", "发布信息与适用范围", "趋势参考", "为什么值得看",
        "报道边界", "来源链", "筛选说明", "真实性声明"
    ];

    private readonly Func<DateTimeOffset> _now;

    public EditionQualityPolicy(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public bool IsQualified(NewsEdition? edition)
    {
        if (edition is null ||
            edition.SchemaVersion != QualifiedSchemaVersion ||
            edition.Items is null ||
            edition.Items.Count < MinimumVisibleItems)
        {
            return false;
        }

        var now = _now();
        if (edition.GeneratedAt == default ||
            edition.GeneratedAt > now.AddMinutes(5) ||
            !DateOnly.TryParse(edition.EditionDate, out _) ||
            edition.WindowHours is < 24 or > 336)
        {
            return false;
        }

        // Publishing is atomic: one malformed or duplicate item rejects the edition.
        if (edition.Items.Any(item => item is null) ||
            edition.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != edition.Items.Count ||
            edition.Items.Select(item => item.SourceUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edition.Items.Count ||
            !edition.Items.All(IsQualifiedItem))
        {
            return false;
        }

        // Pool size and category balancing are ranking concerns. The publishing
        // boundary only requires every persisted article to be independently sound.
        return true;
    }

    public NewsEdition? ChooseNewest(params NewsEdition?[] editions) =>
        editions
            .Where(IsQualified)
            .OrderByDescending(edition => edition!.GeneratedAt)
            .FirstOrDefault();

    public NewsEdition CreateEmptyEdition() => new()
    {
        SchemaVersion = QualifiedSchemaVersion,
        EditionDate = _now().ToString("yyyy-MM-dd"),
        WindowHours = 72,
        GeneratedAt = DateTimeOffset.MinValue,
        Items = []
    };

    public static bool IsQualifiedItem(NewsItem? item)
    {
        if (item is null ||
            string.IsNullOrWhiteSpace(item.Id) ||
            !ContentTypes.Contains(item.ContentType) ||
            item.Topics is null || item.Topics.Count == 0 ||
            item.Topics.Any(topic => string.IsNullOrWhiteSpace(topic) || !AllowedTopics.Contains(topic)) ||
            item.BriefSections is null || item.BriefSections.Count is < 3 or > 5 ||
            string.IsNullOrWhiteSpace(item.SourceName) ||
            !Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps ||
            !DateTimeOffset.TryParse(item.PublishedAt, out _) ||
            !HasChineseLead(item.Title) ||
            !IsChineseEditorialText(item.Title, 2) ||
            !IsChineseEditorialText(item.Summary, 50) ||
            !IsChineseEditorialText(item.ReaderContext, 12) ||
            item.TermExplanations is null || item.TermExplanations.Count is < 2 or > 4 ||
            item.TermExplanations.Select(term => term.Term.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.TermExplanations.Count ||
            item.TermExplanations.Any(term => term is null ||
                string.IsNullOrWhiteSpace(term.Term) || term.Term.Length > 40 ||
                !IsChineseEditorialText(term.Explanation, 12)) ||
            item.TechnicalRelevanceScore < 0.55 || item.TechnicalRelevanceScore > 1 ||
            item.InnovationScore < 0.35 || item.InnovationScore > 1)
        {
            return false;
        }

        var totalHanCharacters = item.BriefSections.Sum(section =>
            section?.Body?.Count(character => character is >= '\u3400' and <= '\u9fff') ?? 0);

        return totalHanCharacters >= 275 &&
            item.BriefSections.All(section => section is not null &&
                !IsBannedReaderHeading(section.Title) &&
                IsChineseEditorialText(section.Title, 2) &&
                IsChineseEditorialText(section.Body, 45)) &&
            IsChineseEditorialText(item.BriefSections[0].Body, 60);
    }

    private static bool IsBannedReaderHeading(string title) =>
        BannedReaderHeadings.Any(heading =>
            title.Contains(heading, StringComparison.OrdinalIgnoreCase));

    private static bool HasChineseLead(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Take(16).Count(character => character is >= '\u3400' and <= '\u9fff') >= 4;
    }

    private static bool IsChineseEditorialText(string? value, int minimumHanCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hanCount = value.Count(character => character is >= '\u3400' and <= '\u9fff');
        if (hanCount < minimumHanCharacters)
        {
            return false;
        }

        // Product names are allowed, but a standalone English sentence/paragraph is not.
        foreach (var segment in value.Split(['。', '！', '？', '.', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var segmentHan = segment.Count(character => character is >= '\u3400' and <= '\u9fff');
            var latinLetters = segment.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            if (latinLetters >= 24 && segmentHan < 4 && latinLetters > segmentHan * 4)
            {
                return false;
            }
        }

        return true;
    }
}
