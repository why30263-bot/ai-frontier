namespace AIFrontier.Models;

public sealed class NewsItem
{
    public string Title { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = [];
    public string SourceName { get; set; } = string.Empty;
    public string PublishedAt { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string ReaderContext { get; set; } = string.Empty;
    public List<TermExplanation> TermExplanations { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public string FullBrief { get; set; } = string.Empty;
    public List<string> KeyFacts { get; set; } = [];
    public string Context { get; set; } = string.Empty;
    public string Limitations { get; set; } = string.Empty;
    public List<string> SourceTrail { get; set; } = [];
}

public sealed class TermExplanation
{
    public string Term { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}
