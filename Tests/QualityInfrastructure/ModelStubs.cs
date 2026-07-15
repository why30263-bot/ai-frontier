using System.Text.Json.Serialization;

namespace AIFrontier.Models;

public sealed class NewsEdition
{
    public int SchemaVersion { get; set; } = 2;
    public string EditionDate { get; set; } = string.Empty;
    public int WindowHours { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<NewsItem> Items { get; set; } = [];
}

public sealed class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ReaderContext { get; set; } = string.Empty;
    public List<TermExplanation> TermExplanations { get; set; } = [];
    public List<BriefSection> BriefSections { get; set; } = [];
    public string PublishedAt { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset? AddedAt { get; set; }
    public bool IsRead { get; set; }
    public bool HasRecentArrival => AddedAt is { } added &&
        DateTimeOffset.Now - added <= TimeSpan.FromDays(3);
    public bool IsNew => HasRecentArrival && !IsRead;
    public double InnovationScore { get; set; }
    public double TechnicalRelevanceScore { get; set; }
    public double SourceQualityScore { get; set; }
    public double FreshnessScore { get; set; }
}

public sealed class TermExplanation
{
    public string Term { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}

public sealed class BriefSection
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    [JsonPropertyName("heading")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeadingAlias
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Title = value;
            }
        }
    }
}
