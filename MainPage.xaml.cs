using AIFrontier.Models;
using AIFrontier.Services;
using AIFrontier.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIFrontier;

public sealed partial class MainPage : Page
{
    private bool _isCompact;
    private bool _compactDetailVisible;
    private bool _isPreferenceVisible;
    private bool _suppressRatingFeedback;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Items.CollectionChanged += (_, _) =>
            EmptyState.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.SelectedItem) or nameof(ViewModel.SelectedRatingValue))
            {
                RestoreRatingFromViewModel();
            }
        };
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
            ViewModel.FeedbackMessage = $"???????{ex.Message}";
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
        SetBusy(RefreshButton, RefreshProgressRing, true);
        try
        {
            var reading = ViewModel.SelectedItem;
            await ViewModel.LoadAsync();
            if (reading is not null && ViewModel.Items.All(item => item.Id != reading.Id))
            {
                ViewModel.SelectedItem = reading;
            }
            NewsList.SelectedItem = ViewModel.SelectedItem;
            if (showMessage)
            {
                ViewModel.FeedbackMessage = "???";
                ViewModel.IsFeedbackMessageOpen = true;
            }
        }
        finally
        {
            SetBusy(RefreshButton, RefreshProgressRing, false);
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
            ViewModel.FeedbackMessage = $"?????{ex.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
    }

    private void NextBatchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NextBatch();
        ResetReadingPosition();
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
            FeedHeader.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Auto);
            Grid.SetRow(HeaderCommands, 1);
            Grid.SetColumn(HeaderCommands, 0);
            Grid.SetColumnSpan(HeaderCommands, 2);
            HeaderCommands.HorizontalAlignment = HorizontalAlignment.Left;
            HeaderCommands.Margin = new Thickness(0, 2, 0, 0);
            EditionText.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;
            BatchText.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;
            ReadingActions.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Auto);
            Grid.SetRow(OpenSourceButton, 1);
            Grid.SetColumn(OpenSourceButton, 0);
            Grid.SetColumnSpan(OpenSourceButton, 2);
            OpenSourceButton.Margin = new Thickness(0, 10, 0, 0);
            FeedColumn.Width = _compactDetailVisible ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = _compactDetailVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            FeedPane.Visibility = _compactDetailVisible ? Visibility.Collapsed : Visibility.Visible;
            DetailPane.Visibility = _compactDetailVisible ? Visibility.Visible : Visibility.Collapsed;
            CompactBackButton.Visibility = Visibility.Visible;
        }
        else
        {
            FeedHeader.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetRow(HeaderCommands, 0);
            Grid.SetColumn(HeaderCommands, 1);
            Grid.SetColumnSpan(HeaderCommands, 1);
            HeaderCommands.HorizontalAlignment = HorizontalAlignment.Right;
            HeaderCommands.Margin = new Thickness(0);
            EditionText.Visibility = Visibility.Visible;
            BatchText.Visibility = Visibility.Visible;
            ReadingActions.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetRow(OpenSourceButton, 0);
            Grid.SetColumn(OpenSourceButton, 1);
            Grid.SetColumnSpan(OpenSourceButton, 1);
            OpenSourceButton.Margin = new Thickness(0);
            _compactDetailVisible = false;
            FeedColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(Math.Clamp(width * 0.48, 460, 800));
            FeedPane.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            CompactBackButton.Visibility = Visibility.Collapsed;
        }

        PreferencePane.Visibility = _isPreferenceVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_isPreferenceVisible)
        {
            FeedPane.Visibility = Visibility.Collapsed;
            DetailPane.Visibility = Visibility.Collapsed;
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

    private async Task ShowDetailAsync(NewsItem item)
    {
        await ViewModel.SelectAsync(item);
        NewsList.SelectedItem = item;
        DetailScrollViewer.ChangeView(null, 0, null, true);
        if (_isCompact)
        {
            _compactDetailVisible = true;
            UpdateResponsiveLayout(ActualWidth);
            CompactBackButton.Focus(FocusState.Programmatic);
        }
    }

    private void CompactBackButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCompactDetail();
    }

    private void BackKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isCompact || !_compactDetailVisible || _isPreferenceVisible)
        {
            return;
        }

        CloseCompactDetail();
        args.Handled = true;
    }

    private void CloseCompactDetail()
    {
        _compactDetailVisible = false;
        UpdateResponsiveLayout(ActualWidth);
        NewsList.Focus(FocusState.Programmatic);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var preferenceSelected = tag == "??";
        _isPreferenceVisible = preferenceSelected;
        UpdateResponsiveLayout(ActualWidth);
        if (preferenceSelected)
        {
            return;
        }

        ViewModel.SetCategory(tag == "??" ? "??" : tag, tag == "??");
        ResetReadingPosition();
        _compactDetailVisible = false;
        UpdateResponsiveLayout(ActualWidth);
    }

    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            await ViewModel.RecordFeedbackAsync(action);
        }
    }

    private async void PreferenceRating_ValueChanged(RatingControl sender, object args)
    {
        if (!_suppressRatingFeedback && ViewModel.SelectedItem is not null)
        {
            await ViewModel.RecordFeedbackAsync(
                sender.Value > 0 ? "rating" : "rating-clear",
                sender.Value > 0 ? sender.Value : null);
        }
    }

    private async void OpenSourceButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.OpenSourceAsync();

    private async void ConnectCodexButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(ConnectCodexButton, ConnectCodexProgressRing, true);
        try
        {
            await ViewModel.ConnectCodexAsync();
        }
        catch (Exception exception)
        {
            ViewModel.FeedbackMessage = $"?? Codex ???{exception.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
        finally
        {
            SetBusy(ConnectCodexButton, ConnectCodexProgressRing, false);
        }
    }

    private async void AnalyzeWithCodexButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(AnalyzeWithCodexButton, CodexProgressRing, true);
        try
        {
            await ViewModel.AnalyzeWithCodexAsync();
        }
        catch (Exception exception)
        {
            ViewModel.FeedbackMessage = $"?? Codex ?????{exception.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
        finally
        {
            SetBusy(AnalyzeWithCodexButton, CodexProgressRing, false);
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(CheckUpdatesButton, UpdateProgressRing, true);
        try
        {
            await CheckForUpdatesAndPromptAsync(true);
        }
        finally
        {
            SetBusy(CheckUpdatesButton, UpdateProgressRing, false);
        }
    }

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
            Header = "???????",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
            ItemsSource = new[]
            {
                "????????????",
                "?????????",
                $"????????? {update.LatestVersion}?",
                "???????"
            }
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"?????{update.CurrentVersion}\n?????{update.LatestVersion}\n???????? GitHub Releases???????? SHA-256?",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(choices);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "?? AI Frontier ???",
            Content = content,
            PrimaryButtonText = "??",
            CloseButtonText = "??",
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

    private void ResetReadingPosition()
    {
        DetailScrollViewer.ChangeView(null, 0, null, true);
        if (ViewModel.Items.FirstOrDefault() is { } first)
        {
            NewsList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
        }
    }

    private void RestoreRatingFromViewModel()
    {
        _suppressRatingFeedback = true;
        try
        {
            PreferenceRating.Value = ViewModel.SelectedRatingValue;
        }
        finally
        {
            _suppressRatingFeedback = false;
        }
    }

    private static void SetBusy(Control control, ProgressRing ring, bool isBusy)
    {
        control.IsEnabled = !isBusy;
        ring.IsActive = isBusy;
        ring.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }
}
