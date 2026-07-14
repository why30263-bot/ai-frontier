using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AIFrontier.Models;

public sealed class NewsEdition
{
    public int SchemaVersion { get; set; }
    public string EditionDate { get; set; } = string.Empty;
    public int WindowHours { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<NewsItem> Items { get; set; } = [];
}

public sealed class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string BrandColor { get; set; } = "#202020";
    public string LogoAsset { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string FullBrief { get; set; } = string.Empty;
    public List<BriefSection> BriefSections { get; set; } = [];
    public string PublishedAt { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public int ReadMinutes { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string WhyItMatters { get; set; } = string.Empty;
    public List<string> Details { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> KeyFacts { get; set; } = [];
    public string Context { get; set; } = string.Empty;
    public string BeginnerExplainer { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Limitations { get; set; } = string.Empty;
    public string WhatToWatch { get; set; } = string.Empty;
    public List<string> SourceTrail { get; set; } = [];

    [JsonIgnore]
    public SolidColorBrush BrandBrush => new(ParseHex(BrandColor));

    [JsonIgnore]
    public Uri LogoUri => new($"ms-appx:///{LogoAsset.Replace('\\', '/')}");

    [JsonIgnore]
    public ImageSource? LogoSource => string.IsNullOrWhiteSpace(LogoAsset)
        ? null
        : new SvgImageSource(LogoUri);

    [JsonIgnore]
    public string ReadingMeta => $"{PublishedAt}  ·  {ReadMinutes} 分钟  ·  {Confidence}";

    [JsonIgnore]
    public string AccessibleTitle => $"{Category}，{Title}，来源 {SourceName}";

    private static Color ParseHex(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return Colors.DimGray;
        }

        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

public sealed class BriefSection
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

public sealed class PreferenceProfile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, double> TopicWeights { get; set; } = [];
    public Dictionary<string, double> SourceWeights { get; set; } = [];
    public string Depth { get; set; } = "入门解释";
    public int MaxItems { get; set; } = 12;
    public List<string> BlockedSources { get; set; } = [];
    public List<string> BlockedTopics { get; set; } = [];
    public string PromptHint { get; set; } = string.Empty;
}

public sealed record FeedbackEvent(
    string NewsId,
    string Action,
    double? Score,
    string Category,
    string Source,
    DateTimeOffset CreatedAt);
