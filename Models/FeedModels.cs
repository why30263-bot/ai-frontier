namespace AIFrontier.Models;

public sealed class FeedConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string RemoteConfigUrl { get; set; } = string.Empty;
    public string RemoteNewsUrl { get; set; } = string.Empty;
    public int RefreshMinutes { get; set; } = 60;
    public int StaleAfterHours { get; set; } = 18;
    public int MaxItems { get; set; } = 18;
    public int BatchSize { get; set; } = 10;
    public int MaxItemsPerSource { get; set; } = 4;
    public int FreshHours { get; set; } = 72;
    public int SupplementDays { get; set; } = 7;
    public Dictionary<string, int> CategoryMinimums { get; set; } = [];
    public GitHubDiscoveryConfiguration GitHubDiscovery { get; set; } = new();
    public List<FeedSource> Feeds { get; set; } = [];
}

public sealed class GitHubDiscoveryConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Query { get; set; } = "topic:artificial-intelligence stars:>500";
    public int MinimumStars { get; set; } = 500;
    public int MaxItems { get; set; } = 4;
    public List<GitHubDiscoveryQuery> Queries { get; set; } = [];
}

public sealed class GitHubDiscoveryQuery
{
    public string Query { get; set; } = string.Empty;
    public string Category { get; set; } = "开源项目";
    public int MinimumStars { get; set; } = 100;
    public int MaxItems { get; set; } = 4;
}

public sealed class FeedSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string BrandColor { get; set; } = "#202020";
    public string LogoAsset { get; set; } = string.Empty;
    public string Trust { get; set; } = "官方来源";
}
