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

    private readonly string _bundledProfilePath;
    private readonly AtomicJsonStore<PreferenceProfile> _profileStore;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string WorkflowProfilePath => Path.Combine(_storageRoot, "workflow-profile.json");

    public PreferenceService()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontier"),
            Path.Combine(AppContext.BaseDirectory, "Data", "preference-profile.json"))
    {
    }

    public PreferenceService(string storageRoot, string? bundledProfilePath = null)
    {
        _storageRoot = storageRoot;
        _bundledProfilePath = bundledProfilePath ?? Path.Combine(AppContext.BaseDirectory, "Data", "preference-profile.json");
        _profileStore = new AtomicJsonStore<PreferenceProfile>(WorkflowProfilePath, JsonOptions);
    }

    public async Task<PreferenceProfile> LoadAsync()
    {
        Directory.CreateDirectory(_storageRoot);
        var profile = await _profileStore.LoadAsync();
        if (profile is null && File.Exists(_bundledProfilePath))
        {
            profile = await new AtomicJsonStore<PreferenceProfile>(_bundledProfilePath, JsonOptions).LoadAsync();
        }

        return NormalizeProfile(profile ?? new PreferenceProfile());
    }

    public async Task<PreferenceProfile> RecordAsync(NewsItem item, string action, double? score = null)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_storageRoot);
            var profile = await LoadAsync();
            profile.ArticlePreferences ??= [];
            profile.BookmarkedIds ??= [];
            if (!profile.ArticlePreferences.TryGetValue(item.Id, out var state))
            {
                state = new ArticlePreference();
                profile.ArticlePreferences[item.Id] = state;
            }

            var before = Contribution(state);
            ApplyAction(state, action, score);
            var after = Contribution(state);
            var topicDelta = after.Topic - before.Topic;
            var sourceDelta = after.Source - before.Source;

            foreach (var topic in item.Topics.Prepend(item.Category).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Adjust(profile.TopicWeights, topic, topicDelta);
            }
            Adjust(profile.SourceWeights, NormalizeSource(item), sourceDelta);
            profile.BookmarkedIds = profile.ArticlePreferences
                .Where(pair => pair.Value.IsBookmarked)
                .Select(pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            profile.ExplicitFeedbackCount = profile.ArticlePreferences.Values.Count(value =>
                value.IsBookmarked || !string.IsNullOrWhiteSpace(value.Reaction) || value.Rating is not null);
            profile.UpdatedAt = DateTimeOffset.Now;
            profile.PromptHint = BuildPromptHint(profile);

            // The profile is the source of truth. Atomic replacement ensures a crash
            // cannot leave a half-written JSON document behind.
            _ = await _profileStore.SaveAsync(profile);

            try
            {
                var feedback = new FeedbackEvent(item.Id, action, score, item.Category, item.SourceName, DateTimeOffset.Now);
                var eventLine = JsonSerializer.Serialize(feedback, JsonOptions) + Environment.NewLine;
                await File.AppendAllTextAsync(
                    Path.Combine(_storageRoot, "feedback-events.jsonl"),
                    eventLine,
                    new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Analytics must never prevent the preference itself from being usable.
            }

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PreferenceProfile NormalizeProfile(PreferenceProfile profile)
    {
        profile.TopicWeights ??= [];
        profile.SourceWeights ??= [];
        profile.BlockedSources ??= [];
        profile.BlockedTopics ??= [];
        profile.ArticlePreferences ??= [];
        profile.BookmarkedIds ??= [];
        return profile;
    }

    private static void Adjust(Dictionary<string, double> values, string key, double delta)
    {
        if (Math.Abs(delta) < 0.0001)
        {
            return;
        }
        var current = values.TryGetValue(key, out var existing) ? existing : 1d;
        values[key] = Math.Round(Math.Clamp(current + delta, 0.2, 2.0), 2);
    }

    public static string NormalizeSource(NewsItem item)
    {
        var source = $"{item.SourceName} {item.SourceUrl}".ToLowerInvariant();
        if (source.Contains("arxiv") || source.Contains("doi.org") || source.Contains("aclanthology") ||
            source.Contains("openreview") || source.Contains("neurips") || source.Contains("icml") || source.Contains("iclr"))
        {
            return "????";
        }
        if (source.Contains("github.com") || source.Contains("github trending"))
        {
            return "GitHub";
        }
        if (source.Contains("twitter.com") || source.Contains("x.com/") || source.Contains("reddit") || source.Contains("hacker news"))
        {
            return "????";
        }
        if (source.Contains("techcrunch") || source.Contains("venturebeat") || source.Contains("the verge") ||
            source.Contains("wired") || source.Contains("mit technology review") || source.Contains("???") || source.Contains("????"))
        {
            return "????";
        }
        if (source.Contains("openai") || source.Contains("anthropic") || source.Contains("deepmind") ||
            source.Contains("google") || source.Contains("microsoft") || source.Contains("meta") ||
            source.Contains("huggingface") || source.Contains("??"))
        {
            return "????";
        }
        return "????";
    }

    private static void ApplyAction(ArticlePreference state, string action, double? score)
    {
        switch (action)
        {
            case "detail": state.DetailOpened = true; break;
            case "source-open": state.SourceOpened = true; break;
            case "codex-open": state.CodexOpened = true; break;
            case "bookmark": state.IsBookmarked = !state.IsBookmarked; break;
            case "like": state.Reaction = state.Reaction == "like" ? string.Empty : "like"; break;
            case "dislike": state.Reaction = state.Reaction == "dislike" ? string.Empty : "dislike"; break;
            case "less-topic": state.Reaction = state.Reaction == "less-topic" ? string.Empty : "less-topic"; break;
            case "rating" when score is not null:
                state.Rating = Math.Clamp(score.Value, 1, 5);
                break;
            case "rating-clear":
                state.Rating = null;
                break;
        }
    }

    private static (double Topic, double Source) Contribution(ArticlePreference state)
    {
        var topic = 0d;
        var source = 0d;
        if (state.DetailOpened) topic += 0.02;
        if (state.SourceOpened) { topic += 0.05; source += 0.04; }
        if (state.CodexOpened) { topic += 0.06; source += 0.02; }
        if (state.IsBookmarked) { topic += 0.18; source += 0.08; }
        topic += state.Reaction switch { "like" => 0.16, "dislike" => -0.18, "less-topic" => -0.30, _ => 0d };
        source += state.Reaction switch { "like" => 0.08, "dislike" => -0.06, _ => 0d };
        if (state.Rating is not null)
        {
            topic += (state.Rating.Value - 3d) * 0.10;
            source += (state.Rating.Value - 3d) * 0.035;
        }
        return (topic, source);
    }

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

        return $"??????[{string.Join(", ", preferredTopics)}]???[{string.Join(", ", preferredSources)}]?????[{profile.Depth}]????????????????????????????";
    }
}
