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
    private readonly List<NewsItem> _allItems = [];
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
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync() =>
        UpdateStatus = await _updateService.CheckAndStageAsync();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void SetCategory(string category, bool savedOnly = false)
    {
        _activeCategory = category;
        _savedOnly = savedOnly;
        ApplyFilter();
    }

    public void Select(NewsItem item) => SelectedItem = item;

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

        Items.Clear();
        foreach (var item in filtered)
        {
            Items.Add(item);
        }

        SelectedItem = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
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
