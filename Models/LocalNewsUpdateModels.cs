namespace AIFrontier.Models;

public enum NewsUpdateMode
{
    CloudPreferred,
    LocalCodexOnly
}

public sealed class LocalNewsUpdateSettings
{
    public int SchemaVersion { get; set; } = 1;
    public NewsUpdateMode Mode { get; set; } = NewsUpdateMode.CloudPreferred;
    public bool LocalFallbackEnabled { get; set; } = true;
    public int ConsecutiveCloudFailures { get; set; }
    public DateTimeOffset? LastCloudCheckAt { get; set; }
    public DateTimeOffset? LastCloudSuccessAt { get; set; }
    public DateTimeOffset? LastLocalAttemptAt { get; set; }
    public DateTimeOffset? LastLocalSuccessAt { get; set; }
    public string LastCompletedLocalDate { get; set; } = string.Empty;
    public NewsUpdateMode LastCompletedMode { get; set; } = NewsUpdateMode.CloudPreferred;
    public string LastStatus { get; set; } = string.Empty;
}

public sealed record DailyNewsUpdateResult(
    bool Checked,
    bool NewsChanged,
    bool UsedLocalCodex,
    bool CodexConnected,
    string Status);

public sealed record CloudFeedHealth(
    bool Reachable,
    bool Qualified,
    bool Fresh,
    NewsEdition? Edition,
    string Status);

public sealed record LocalCodexUpdateResult(
    bool Success,
    bool CodexConnected,
    bool Changed,
    NewsEdition? Edition,
    string Status);
