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
    public List<BriefSection> BriefSections { get; set; } = [];
    public string PublishedAt { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public double InnovationScore { get; set; }
    public double TechnicalRelevanceScore { get; set; }
}

public sealed class BriefSection
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
