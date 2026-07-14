using System.Collections.ObjectModel;
using AIFrontier.Models;
using AIFrontier.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.System;

namespace AIFrontier.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly NewsService _newsService = new();
    private readonly PreferenceService _preferenceService = new();
    private readonly UpdateService _updateService = new();
    private readonly CodexIntegrationService _codexIntegrationService = new();
    private readonly List<NewsItem> _allItems = [];
    private List<NewsItem> _filteredItems = [];
    private const int BatchSize = 10;
    private int _batchStart;
    private string _activeCategory = "全部";
    private bool _savedOnly;
    private bool _trendMode;
    private bool _recommendationMode;
    private readonly HashSet<string> _bookmarkedIds = [];
    private readonly HashSet<string> _openedThisSession = [];
    private PreferenceProfile _profile = new();

    public ObservableCollection<NewsItem> Items { get; } = [];

    [ObservableProperty]
    public partial NewsItem? SelectedItem { get; set; }

    partial void OnSelectedItemChanged(NewsItem? value) => UpdateSelectedFeedbackState();

    [ObservableProperty]
    public partial string EditionLabel { get; set; } = "正在读取今日简报…";

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFeedbackMessageOpen { get; set; }

    [ObservableProperty]
    public partial string PreferenceSummary { get; set; } = "偏好会在你评分后逐步形成。";

    [ObservableProperty]
    public partial string WorkflowProfilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "正在检查版本…";

    [ObservableProperty]
    public partial string CodexStatus { get; set; } = "正在检测本机 Codex…";

    [ObservableProperty]
    public partial string CodexWorkspacePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BatchLabel { get; set; } = "每批 10 条";

    [ObservableProperty]
    public partial string ViewExplanation { get; set; } = "今天值得读的 AI 前沿进展";

    [ObservableProperty]
    public partial bool IsSelectedLiked { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedDisliked { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedBookmarked { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedLessTopic { get; set; }

    [ObservableProperty]
    public partial double SelectedRatingValue { get; set; }

    public async Task LoadAsync()
    {
        var selectedId = SelectedItem?.Id;
        var edition = await _newsService.LoadAsync();
        _allItems.Clear();
        _allItems.AddRange(edition.Items);
        EditionLabel = $"{edition.EditionDate} · {edition.Items.Count} 条";
        WorkflowProfilePath = _preferenceService.WorkflowProfilePath;

        _profile = await _preferenceService.LoadAsync();
        _bookmarkedIds.Clear();
        _bookmarkedIds.UnionWith(_profile.BookmarkedIds ?? []);
        PreferenceSummary = BuildPreferenceSummary(_profile);
        ApplyFilter();
        var selectedIndex = selectedId is null
            ? -1
            : _filteredItems.FindIndex(item => item.Id == selectedId);
        if (selectedIndex >= 0)
        {
            _batchStart = selectedIndex / BatchSize * BatchSize;
            RenderBatch(selectedId);
        }
        CodexWorkspacePath = _codexIntegrationService.WorkspacePath;
        _ = DetectCodexAsync();
    }

    private async Task DetectCodexAsync()
    {
        var codex = await _codexIntegrationService.GetStatusAsync();
        CodexStatus = codex.Status;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var result = await _updateService.CheckAsync();
        UpdateStatus = result.Status;
        return result;
    }

    public async Task ApplyUpdateChoiceAsync(UpdateCheckResult update, UpdateChoice choice)
    {
        UpdateStatus = await _updateService.ApplyChoiceAsync(update, choice);
        ShowFeedback(UpdateStatus);
    }

    public async Task ResetUpdatePromptsAsync()
    {
        UpdateStatus = await _updateService.ResetPromptPreferencesAsync();
        ShowFeedback(UpdateStatus);
    }

    public async Task ConnectCodexAsync()
    {
        var result = await _codexIntegrationService.ConnectAsync();
        CodexStatus = result.Status;
        CodexWorkspacePath = _codexIntegrationService.WorkspacePath;
        ShowFeedback(result.Status);
    }

    public async Task AnalyzeWithCodexAsync()
    {
        if (SelectedItem is null)
        {
            ShowFeedback("请先选择一条资讯");
            return;
        }
        var result = await _codexIntegrationService.AnalyzeAsync(SelectedItem);
        if (result.IsConnected)
        {
            _profile = await _preferenceService.RecordAsync(SelectedItem, "codex-open");
        }
        CodexStatus = result.Status;
        ShowFeedback(result.Status);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void SetCategory(string category, bool savedOnly = false)
    {
        _trendMode = category == "三日热榜";
        _recommendationMode = category == "为你推荐";
        _activeCategory = _trendMode || _recommendationMode ? "全部" : category;
        _savedOnly = savedOnly;
        ViewExplanation = _trendMode
            ? "近三天讨论较多的 AI 话题"
            : _recommendationMode
                ? "根据你的阅读和反馈推荐"
                : savedOnly
                    ? "你收藏的资讯"
                    : "今天值得读的 AI 前沿进展";
        ApplyFilter();
    }

    public async Task SelectAsync(NewsItem item)
    {
        SelectedItem = item;
        if (_openedThisSession.Add(item.Id))
        {
            _profile = await _preferenceService.RecordAsync(item, "detail");
            PreferenceSummary = BuildPreferenceSummary(_profile);
        }
    }

    public void NextBatch()
    {
        if (_filteredItems.Count <= BatchSize)
        {
            ShowFeedback($"当前栏目共 {_filteredItems.Count} 条，没有下一批");
            return;
        }
        _batchStart = _batchStart + BatchSize >= _filteredItems.Count ? 0 : _batchStart + BatchSize;
        RenderBatch();
        ShowFeedback("已换一批");
    }

    public async Task RecordFeedbackAsync(string action, double? score = null)
    {
        if (SelectedItem is null)
        {
            return;
        }

        _profile = await _preferenceService.RecordAsync(SelectedItem, action, score);
        _bookmarkedIds.Clear();
        _bookmarkedIds.UnionWith(_profile.BookmarkedIds ?? []);
        PreferenceSummary = BuildPreferenceSummary(_profile);
        if (action == "bookmark")
        {
            var saved = _bookmarkedIds.Contains(SelectedItem.Id);
            ShowFeedback(saved ? "已收藏并保存在本机" : "已取消收藏");
            UpdateSelectedFeedbackState();
            ApplyFilter();
            return;
        }
        UpdateSelectedFeedbackState();
        var message = action switch
        {
            "like" => IsSelectedLiked ? "已喜欢" : "已取消喜欢",
            "dislike" => IsSelectedDisliked ? "已标记为不感兴趣" : "已取消不感兴趣",
            "less-topic" => IsSelectedLessTopic ? "已减少此类内容" : "已取消减少此类内容",
            "rating" => $"已评分 {score:0} 星",
            "rating-clear" => "已清除评分",
            _ => "已保存"
        };
        ShowFeedback(message);
    }

    public async Task OpenSourceAsync()
    {
        if (SelectedItem is not null && Uri.TryCreate(SelectedItem.SourceUrl, UriKind.Absolute, out var uri))
        {
            if (await Launcher.LaunchUriAsync(uri))
            {
                _profile = await _preferenceService.RecordAsync(SelectedItem, "source-open");
            }
            else
            {
                ShowFeedback("无法打开原始网站");
            }
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var selectedId = SelectedItem?.Id;
        var filtered = _allItems.Where(item =>
            (_activeCategory == "全部" || MatchesChannel(item, _activeCategory)) &&
            (!_savedOnly || _bookmarkedIds.Contains(item.Id)) &&
            (!_trendMode || !DateTimeOffset.TryParse(item.PublishedAt, out var published) ||
                published >= DateTimeOffset.Now.AddHours(-72)) &&
            (query.Length == 0 ||
             item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))));

        var filteredList = filtered.ToList();
        if (_trendMode)
        {
            filteredList = filteredList.OrderByDescending(item => item.HotScore).ToList();
        }
        else if (_activeCategory == "全部" && !_savedOnly && query.Length == 0)
        {
            // The edition pipeline publishes complete, coverage-checked pages.
            // Personalization may reorder within a page but must not pull all
            // papers or open-source entries into page one and weaken page two.
            filteredList = filteredList
                .Chunk(BatchSize)
                .SelectMany(batch => RankForUser(batch.ToList(), _recommendationMode ? 0.30 : 0.20))
                .ToList();
        }
        else
        {
            filteredList = RankForUser(filteredList, _recommendationMode ? 0.30 : 0.20);
        }
        _filteredItems = filteredList;
        _batchStart = 0;
        RenderBatch(selectedId);
    }

    private void RenderBatch(string? selectedId = null)
    {
        Items.Clear();
        var take = Math.Min(BatchSize, Math.Max(0, _filteredItems.Count - _batchStart));
        for (var index = 0; index < take; index++)
        {
            Items.Add(_filteredItems[_batchStart + index]);
        }
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredItems.Count / (double)BatchSize));
        var page = Math.Min(pageCount, _batchStart / BatchSize + 1);
        BatchLabel = $"第 {page}/{pageCount} 批 · {take} 条";
        SelectedItem = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
    }

    private List<NewsItem> BalanceForHome(List<NewsItem> items)
    {
        if (_activeCategory != "全部" || _savedOnly || SearchText.Trim().Length > 0)
        {
            return items;
        }

        var remaining = new List<NewsItem>(items);
        var front = new List<NewsItem>();
        var minimums = new (string Category, int Count)[]
        {
            ("大模型", 2),
            ("Agent", 2),
            ("重要研究", 2),
            ("开源项目", 2),
            ("产业动态", 2)
        };
        for (var round = 0; round < minimums.Max(rule => rule.Count); round++)
        {
            foreach (var (category, count) in minimums)
            {
                if (round >= count) continue;
                var item = remaining.FirstOrDefault(item => MatchesChannel(item, category));
                if (item is null) continue;
                front.Add(item);
                remaining.Remove(item);
            }
        }
        front.AddRange(remaining);
        return front;
    }

    private List<NewsItem> RankForUser(List<NewsItem> items, double personalWeight)
    {
        if (items.Count < 2)
        {
            return items;
        }

        var learnedStrength = personalWeight * Math.Min(1d, _profile.ExplicitFeedbackCount / 8d);
        const double explorationWeight = 0.10;
        var importanceWeight = 1d - learnedStrength - explorationWeight;
        return items.Select(item => new
            {
                Item = item,
                Score = importanceWeight * EditorialImportance(item) +
                    learnedStrength * PersonalAffinity(item) +
                    explorationWeight * ExplorationScore(item)
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal)
            .Select(entry => entry.Item)
            .ToList();
    }

    internal static double EditorialImportance(NewsItem item)
    {
        var contentTypeWeight = item.ContentType switch
        {
            "论文" => 1.00,
            "模型发布" => 0.95,
            "开源项目" => 0.90,
            "Agent产品" => 0.88,
            "产业事件" => 0.68,
            _ => 0.60
        };
        var freshness = item.FreshnessScore > 0
            ? NormalizeScore(item.FreshnessScore)
            : PublishedFreshness(item.PublishedAt);
        var source = Math.Max(
            NormalizeScore(item.SourceQualityScore),
            item.IsPrimarySourceVerified ? 0.85 : 0d);

        return 0.30 * NormalizeScore(item.TechnicalRelevanceScore) +
            0.25 * NormalizeScore(item.InnovationScore) +
            0.15 * contentTypeWeight +
            0.15 * freshness +
            0.15 * source;
    }

    private static double NormalizeScore(double value) =>
        Math.Clamp(value > 1d ? value / 100d : value, 0d, 1d);

    private static double PublishedFreshness(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var published))
        {
            return 0d;
        }

        var ageHours = Math.Max(0d, (DateTimeOffset.Now - published).TotalHours);
        return Math.Clamp(1d - ageHours / (24d * 7d), 0d, 1d);
    }

    private double PersonalAffinity(NewsItem item)
    {
        var keys = item.Topics.Prepend(item.Category).Distinct(StringComparer.OrdinalIgnoreCase);
        var topic = keys.Select(key => _profile.TopicWeights.TryGetValue(key, out var weight) ? weight : 1d).Max();
        var sourceKey = PreferenceService.NormalizeSource(item);
        var source = _profile.SourceWeights.TryGetValue(sourceKey, out var sourceWeight) ? sourceWeight : 1d;
        return Math.Clamp((topic * 0.72 + source * 0.28) / 2d, 0.1, 1.0);
    }

    private static double ExplorationScore(NewsItem item)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in $"{DateTime.Today:yyyyMMdd}:{item.Id}") hash = hash * 31 + character;
            return Math.Abs(hash % 1000) / 999d;
        }
    }

    private static bool MatchesChannel(NewsItem item, string channel) =>
        item.Category.Equals(channel, StringComparison.OrdinalIgnoreCase) ||
        item.Topics.Any(topic => topic.Equals(channel, StringComparison.OrdinalIgnoreCase));

    private void UpdateSelectedFeedbackState()
    {
        if (SelectedItem is null || !_profile.ArticlePreferences.TryGetValue(SelectedItem.Id, out var state))
        {
            IsSelectedLiked = false;
            IsSelectedDisliked = false;
            IsSelectedBookmarked = false;
            IsSelectedLessTopic = false;
            SelectedRatingValue = 0;
            return;
        }
        IsSelectedLiked = state.Reaction == "like";
        IsSelectedDisliked = state.Reaction == "dislike";
        IsSelectedBookmarked = state.IsBookmarked;
        IsSelectedLessTopic = state.Reaction == "less-topic";
        SelectedRatingValue = state.Rating ?? 0;
    }

    private void ShowFeedback(string message)
    {
        FeedbackMessage = message;
        IsFeedbackMessageOpen = false;
        IsFeedbackMessageOpen = true;
    }

    private static string BuildPreferenceSummary(PreferenceProfile profile)
    {
        var topics = profile.TopicWeights
            .OrderByDescending(pair => pair.Value)
            .Take(3)
            .Select(pair => pair.Key);
        return $"当前优先：{string.Join("、", topics)} · 阅读深度：{profile.Depth}";
    }
}
