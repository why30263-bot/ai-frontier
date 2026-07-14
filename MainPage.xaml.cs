using AIFrontier.Models;
using AIFrontier.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontier;

public sealed partial class MainPage : Page
{
    private bool _isCompact;
    private bool _compactDetailVisible;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Items.CollectionChanged += (_, _) =>
            EmptyState.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshTimer.Tick += async (_, _) => await RefreshNewsAsync(false);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshNewsAsync(false);
            NewsList.SelectedItem = ViewModel.SelectedItem;
            UpdateResponsiveLayout(ActualWidth);
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            ViewModel.FeedbackMessage = $"读取新闻失败：{ex.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private async Task RefreshNewsAsync(bool showMessage)
    {
        await ViewModel.LoadAsync();
        NewsList.SelectedItem = ViewModel.SelectedItem;
        if (showMessage)
        {
            ViewModel.FeedbackMessage = "已读取 D 盘最新简报";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshNewsAsync(true);
        }
        catch (Exception ex)
        {
            ViewModel.FeedbackMessage = $"刷新失败：{ex.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        _isCompact = width < 980;
        ContentRoot.Padding = _isCompact ? new Thickness(12) : new Thickness(24, 18, 24, 24);
        ShellNavigation.PaneDisplayMode = _isCompact
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;

        if (_isCompact)
        {
            FeedColumn.Width = _compactDetailVisible ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = _compactDetailVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            FeedPane.Visibility = _compactDetailVisible ? Visibility.Collapsed : Visibility.Visible;
            DetailPane.Visibility = _compactDetailVisible ? Visibility.Visible : Visibility.Collapsed;
            CompactBackButton.Visibility = Visibility.Visible;
        }
        else
        {
            _compactDetailVisible = false;
            FeedColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(440);
            FeedPane.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            CompactBackButton.Visibility = Visibility.Collapsed;
        }
    }

    private void NewsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NewsItem item)
        {
            return;
        }

        ViewModel.Select(item);
        PreferenceRating.Value = 0;
        if (_isCompact)
        {
            _compactDetailVisible = true;
            UpdateResponsiveLayout(ActualWidth);
        }
    }

    private void CompactBackButton_Click(object sender, RoutedEventArgs e)
    {
        _compactDetailVisible = false;
        UpdateResponsiveLayout(ActualWidth);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var preferenceSelected = tag == "偏好";
        PreferencePane.Visibility = preferenceSelected ? Visibility.Visible : Visibility.Collapsed;
        FeedPane.Visibility = preferenceSelected ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = preferenceSelected ? Visibility.Collapsed : (_isCompact ? Visibility.Collapsed : Visibility.Visible);
        if (preferenceSelected)
        {
            return;
        }

        ViewModel.SetCategory(tag == "收藏" ? "全部" : tag, tag == "收藏");
        _compactDetailVisible = false;
        UpdateResponsiveLayout(ActualWidth);
    }

    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
        {
            await ViewModel.RecordFeedbackAsync(action);
        }
    }

    private async void PreferenceRating_ValueChanged(RatingControl sender, object args)
    {
        if (sender.Value > 0 && ViewModel.SelectedItem is not null)
        {
            await ViewModel.RecordFeedbackAsync("rating", sender.Value);
        }
    }

    private async void OpenSourceButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.OpenSourceAsync();
}
