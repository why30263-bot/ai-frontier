using AIFrontier.Models;
using AIFrontier.Services;
using AIFrontier.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontier;

public sealed partial class MainPage : Page
{
    private bool _isCompact;
    private bool _compactDetailVisible;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Items.CollectionChanged += (_, _) =>
            EmptyState.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshTimer.Tick += async (_, _) => await RefreshNewsAsync(false);
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAndPromptAsync(false);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshNewsAsync(false);
            NewsList.SelectedItem = ViewModel.SelectedItem;
            UpdateResponsiveLayout(ActualWidth);
            _refreshTimer.Start();
            _updateTimer.Start();
            _ = CheckForUpdatesAndPromptAsync(false);
        }
        catch (Exception ex)
        {
            ViewModel.FeedbackMessage = $"读取新闻失败：{ex.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
            ViewModel.EditionLabel = ViewModel.FeedbackMessage;
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIFrontier");
            Directory.CreateDirectory(logRoot);
            await File.WriteAllTextAsync(Path.Combine(logRoot, "startup-error.log"), ex.ToString());
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

    private void NextBatchButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NextBatch();

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

    private async void NewsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NewsItem item)
        {
            return;
        }

        await ShowDetailAsync(item);
    }

    private async void CardDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: NewsItem item })
        {
            await ShowDetailAsync(item);
        }
    }

    private async Task ShowDetailAsync(NewsItem item)
    {
        await ViewModel.SelectAsync(item);
        NewsList.SelectedItem = item;
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

    private async void ConnectCodexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ConnectCodexAsync();
        }
        catch (Exception exception)
        {
            ViewModel.FeedbackMessage = $"接入 Codex 失败：{exception.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private async void AnalyzeWithCodexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.AnalyzeWithCodexAsync();
        }
        catch (Exception exception)
        {
            ViewModel.FeedbackMessage = $"打开 Codex 分析失败：{exception.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAndPromptAsync(true);

    private async void ResetUpdatePromptsButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ResetUpdatePromptsAsync();

    private async Task CheckForUpdatesAndPromptAsync(bool forcePrompt)
    {
        var update = await ViewModel.CheckForUpdatesAsync();
        if (!update.IsAvailable)
        {
            if (forcePrompt)
            {
                ViewModel.FeedbackMessage = update.Status;
                ViewModel.IsFeedbackMessageOpen = true;
            }
            return;
        }

        if (update.AutoUpdateEnabled)
        {
            await ViewModel.ApplyUpdateChoiceAsync(update, UpdateChoice.EnableAutoUpdate);
            return;
        }

        if (!update.ShouldPrompt && !forcePrompt)
        {
            return;
        }

        var choices = new ComboBox
        {
            Header = "这次怎么处理？",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
            ItemsSource = new[]
            {
                "立即更新（只更新这一次）",
                "接受并开启自动更新",
                $"本版本不需要（跳过 {update.LatestVersion}）",
                "不要再提示更新"
            }
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"当前版本：{update.CurrentVersion}\n最新版本：{update.LatestVersion}\n更新来自项目固定 GitHub Releases，并在安装前校验 SHA-256。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(choices);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "发现 AI Frontier 新版本",
            Content = content,
            PrimaryButtonText = "确认",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var choice = choices.SelectedIndex switch
        {
            1 => UpdateChoice.EnableAutoUpdate,
            2 => UpdateChoice.SkipVersion,
            3 => UpdateChoice.NeverPrompt,
            _ => UpdateChoice.InstallNow
        };
        await ViewModel.ApplyUpdateChoiceAsync(update, choice);
    }
}
