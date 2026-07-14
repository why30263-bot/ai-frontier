using System.Text;
using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class PreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _storageRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIFrontier");

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string WorkflowProfilePath => Path.Combine(_storageRoot, "workflow-profile.json");

    public async Task<PreferenceProfile> LoadAsync()
    {
        Directory.CreateDirectory(_storageRoot);
        var localPath = WorkflowProfilePath;
        var path = File.Exists(localPath)
            ? localPath
            : Path.Combine(AppContext.BaseDirectory, "Data", "preference-profile.json");

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PreferenceProfile>(stream, JsonOptions)
            ?? new PreferenceProfile();
    }

    public async Task<PreferenceProfile> RecordAsync(NewsItem item, string action, double? score = null)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_storageRoot);
            var profile = await LoadAsync();
            var topicDelta = action switch
            {
                "like" => 0.12,
                "dislike" => -0.12,
                "less-topic" => -0.25,
                "rating" when score is not null => (score.Value - 3d) * 0.12,
                _ => 0d
            };
            var sourceDelta = action switch
            {
                "like" => 0.06,
                "dislike" => -0.04,
                "rating" when score is not null => (score.Value - 3d) * 0.04,
                _ => 0d
            };

            Adjust(profile.TopicWeights, item.Category, topicDelta);
            Adjust(profile.SourceWeights, NormalizeSource(item.SourceName), sourceDelta);
            profile.UpdatedAt = DateTimeOffset.Now;
            profile.PromptHint = BuildPromptHint(profile);

            var feedback = new FeedbackEvent(item.Id, action, score, item.Category, item.SourceName, DateTimeOffset.Now);
            var eventLine = JsonSerializer.Serialize(feedback, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(
                Path.Combine(_storageRoot, "feedback-events.jsonl"),
                eventLine,
                new UTF8Encoding(false));

            await File.WriteAllTextAsync(
                WorkflowProfilePath,
                JsonSerializer.Serialize(profile, JsonOptions),
                new UTF8Encoding(false));

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Adjust(Dictionary<string, double> values, string key, double delta)
    {
        var current = values.TryGetValue(key, out var existing) ? existing : 1d;
        values[key] = Math.Round(Math.Clamp(current + delta, 0.2, 2.0), 2);
    }

    private static string NormalizeSource(string source) => source switch
    {
        "arXiv" => "论文原文",
        "GitHub Trending" => "GitHub",
        "官方渠道汇总" => "官方公告",
        _ => "官方公告"
    };

    private static string BuildPromptHint(PreferenceProfile profile)
    {
        var preferredTopics = profile.TopicWeights
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => $"{pair.Key}:{pair.Value:0.00}");
        var preferredSources = profile.SourceWeights
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => $"{pair.Key}:{pair.Value:0.00}");

        return $"排序偏好主题[{string.Join(", ", preferredTopics)}]；来源[{string.Join(", ", preferredSources)}]；解释深度[{profile.Depth}]。偏好只影响排序与篇幅，不得降低事实核查和原始来源门槛。";
    }
}
