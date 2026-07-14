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
    private readonly HashSet<string> _bookmarkedIds = [];

    public ObservableCollection<NewsItem> Items { get; } = [];

    [ObservableProperty]
    public partial NewsItem? SelectedItem { get; set; }

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

    public async Task LoadAsync()
    {
        var edition = await _newsService.LoadAsync();
        _allItems.Clear();
        _allItems.AddRange(edition.Items);
        EditionLabel = $"{edition.EditionDate} · 过去 {edition.WindowHours} 小时 · {edition.Items.Count} 条经筛选信息";
        WorkflowProfilePath = _preferenceService.WorkflowProfilePath;

        var profile = await _preferenceService.LoadAsync();
        PreferenceSummary = BuildPreferenceSummary(profile);
        ApplyFilter();
        var codex = await _codexIntegrationService.GetStatusAsync();
        CodexStatus = codex.Status;
        CodexWorkspacePath = _codexIntegrationService.WorkspacePath;
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
        CodexStatus = result.Status;
        ShowFeedback(result.Status);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void SetCategory(string category, bool savedOnly = false)
    {
        _activeCategory = category;
        _savedOnly = savedOnly;
        ApplyFilter();
    }

    public void Select(NewsItem item) => SelectedItem = item;

    public void NextBatch()
    {
        if (_filteredItems.Count <= BatchSize)
        {
            ShowFeedback($"当前栏目共 {_filteredItems.Count} 条，没有下一批");
            return;
        }
        _batchStart = (_batchStart + BatchSize) % _filteredItems.Count;
        RenderBatch();
        ShowFeedback("已换一批；每批固定显示 10 条，不重复凑数");
    }

    public async Task RecordFeedbackAsync(string action, double? score = null)
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (action == "bookmark")
        {
            if (!_bookmarkedIds.Add(SelectedItem.Id))
            {
                _bookmarkedIds.Remove(SelectedItem.Id);
                ShowFeedback("已取消收藏");
            }
            else
            {
                ShowFeedback("已收藏；收藏不会强行改变推荐主题");
            }
            if (_savedOnly)
            {
                ApplyFilter();
            }
            return;
        }

        var profile = await _preferenceService.RecordAsync(SelectedItem, action, score);
        PreferenceSummary = BuildPreferenceSummary(profile);
        var message = action switch
        {
            "like" => "已记录喜欢，会适度提高同主题内容排序",
            "dislike" => "已记录不感兴趣，会适度降低同主题内容排序",
            "less-topic" => "已记录减少此类，但不会屏蔽重大事件",
            "rating" => $"已记录 {score:0} 星偏好",
            _ => "偏好已更新"
        };
        ShowFeedback(message);
    }

    public async Task OpenSourceAsync()
    {
        if (SelectedItem is not null && Uri.TryCreate(SelectedItem.SourceUrl, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var selectedId = SelectedItem?.Id;
        var filtered = _allItems.Where(item =>
            (_activeCategory == "全部" || item.Category == _activeCategory) &&
            (!_savedOnly || _bookmarkedIds.Contains(item.Id)) &&
            (query.Length == 0 ||
             item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))));

        _filteredItems = BalanceForHome(filtered.ToList());
        _batchStart = 0;
        RenderBatch(selectedId);
    }

    private void RenderBatch(string? selectedId = null)
    {
        Items.Clear();
        var take = Math.Min(BatchSize, _filteredItems.Count);
        for (var index = 0; index < take; index++)
        {
            Items.Add(_filteredItems[(_batchStart + index) % _filteredItems.Count]);
        }
        BatchLabel = _filteredItems.Count > BatchSize
            ? $"本批 {take} 条 · 共 {_filteredItems.Count} 条"
            : $"本批 {take} 条";
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
                var item = remaining.FirstOrDefault(item => item.Category == category);
                if (item is null) continue;
                front.Add(item);
                remaining.Remove(item);
            }
        }
        front.AddRange(remaining);
        return front;
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
