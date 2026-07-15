using System.Collections.ObjectModel;
using System.Text;
using AIFrontier.Models;
using AIFrontier.Services;
using AIFrontier.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIFrontier;

public sealed partial class MainPage : Page
{
    private bool _isCompact;
    private bool _compactDetailVisible;
    private bool _isPreferenceVisible;
    private bool _isCodexChatOpen;
    private bool _isCodexChatInline;
    private bool _isCodexChatInitialized;
    private bool _suppressRatingFeedback;
    private CancellationTokenSource? _codexChatCancellation;
    private Task _activeCodexRequest = Task.CompletedTask;
    private readonly CodexChatService _codexChatService = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    public MainPageViewModel ViewModel { get; } = new();
    public ObservableCollection<CodexChatMessage> CodexMessages { get; } = [];

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
        StartNewVisibleConversation();
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
                ViewModel.FeedbackMessage = "已刷新";
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
            ViewModel.FeedbackMessage = $"刷新失败：{ex.Message}";
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

        UpdateCodexChatLayout(width);
    }

    private void UpdateCodexChatLayout(double width)
    {
        _isCodexChatInline = width >= 1640;
        var chatVisible = _isCodexChatOpen && !_isPreferenceVisible;
        CodexChatPane.Visibility = chatVisible ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.Width = chatVisible && _isCodexChatInline
            ? new GridLength(400)
            : new GridLength(0);

        if (!chatVisible)
        {
            return;
        }

        if (_isCodexChatInline)
        {
            Grid.SetColumn(CodexChatPane, 2);
            Grid.SetColumnSpan(CodexChatPane, 1);
            CodexChatPane.Width = double.NaN;
            CodexChatPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            CodexChatPane.Margin = new Thickness(0);
            DetailColumn.Width = new GridLength(Math.Clamp(width * 0.38, 520, 700));
        }
        else
        {
            Grid.SetColumn(CodexChatPane, 0);
            Grid.SetColumnSpan(CodexChatPane, 3);
        CodexChatPane.Width = double.NaN;
        CodexChatPane.HorizontalAlignment = HorizontalAlignment.Right;
        CodexChatPane.Margin = width < 620 ? new Thickness(0) : new Thickness(Math.Max(0, width - 460), 0, 0, 0);
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
        if (_isCodexChatOpen && !_isPreferenceVisible)
        {
            CloseCodexChat();
            args.Handled = true;
            return;
        }

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

        var preferenceSelected = tag == "偏好";
        _isPreferenceVisible = preferenceSelected;
        UpdateResponsiveLayout(ActualWidth);
        if (preferenceSelected)
        {
            return;
        }

        ViewModel.SetCategory(tag == "收藏" ? "全部" : tag, tag == "收藏");
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
            ViewModel.FeedbackMessage = $"接入 Codex 失败：{exception.Message}";
            ViewModel.IsFeedbackMessageOpen = true;
        }
        finally
        {
            SetBusy(ConnectCodexButton, ConnectCodexProgressRing, false);
        }
    }

    private async void AnalyzeWithCodexButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
        {
            return;
        }

        OpenCodexChat();
        AddCodexMessage("你", $"请深度分析：{item.Title}");
        _activeCodexRequest = RunCodexRequestAsync((onDelta, cancellationToken) =>
            _codexChatService.AnalyzeArticleAsync(item, onDelta, cancellationToken));
        await _activeCodexRequest;
    }

    private void OpenCodexChat()
    {
        _isCodexChatOpen = true;
        UpdateResponsiveLayout(ActualWidth);
        CodexPromptTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseCodexChatButton_Click(object sender, RoutedEventArgs e) => CloseCodexChat();

    private void CloseCodexChat()
    {
        _isCodexChatOpen = false;
        UpdateResponsiveLayout(ActualWidth);
        AnalyzeWithCodexButton.Focus(FocusState.Programmatic);
    }

    private async void NewCodexConversationButton_Click(object sender, RoutedEventArgs e)
    {
        _codexChatCancellation?.Cancel();
        await AwaitActiveCodexRequestAsync();
        SetCodexChatBusy(true);
        try
        {
            await EnsureCodexChatInitializedAsync();
            var reset = await _codexChatService.ResetConversationAsync();
            if (!reset.Success)
            {
                throw new InvalidOperationException(reset.Status);
            }
            StartNewVisibleConversation();
            _isCodexChatInitialized = false;
            CodexPromptTextBox.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            AddCodexMessage("系统", $"无法开始新对话：{exception.Message}");
        }
        finally
        {
            SetCodexChatBusy(false);
        }
    }

    private async void CodexQuickPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string prompt })
        {
            OpenCodexChat();
            await SendCodexPromptAsync(prompt);
        }
    }

    private async void SendCodexMessageButton_Click(object sender, RoutedEventArgs e) =>
        await SendCodexPromptAsync(CodexPromptTextBox.Text);

    private async void CodexPromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendCodexPromptAsync(CodexPromptTextBox.Text);
    }

    private async Task SendCodexPromptAsync(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || CodexBusyPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        CodexPromptTextBox.Text = string.Empty;
        AddCodexMessage("你", prompt);
        _activeCodexRequest = RunCodexRequestAsync((onDelta, cancellationToken) =>
            _codexChatService.SendMessageAsync(prompt, onDelta, cancellationToken));
        await _activeCodexRequest;
    }

    private void StopCodexButton_Click(object sender, RoutedEventArgs e) =>
        _codexChatCancellation?.Cancel();

    private async Task AwaitActiveCodexRequestAsync()
    {
        try
        {
            await _activeCodexRequest;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunCodexRequestAsync(
        Func<Action<string>, CancellationToken, Task<CodexChatResult>> request)
    {
        _codexChatCancellation?.Cancel();
        _codexChatCancellation?.Dispose();
        _codexChatCancellation = new CancellationTokenSource();
        var cancellationToken = _codexChatCancellation.Token;
        var responseIndex = AddCodexMessage("Codex", string.Empty);
        CodexMessages[responseIndex].IsPending = true;
        var responseBuffer = new StringBuilder();
        var requestCompleted = 0;
        SetCodexChatBusy(true);

        try
        {
            await EnsureCodexChatInitializedAsync(cancellationToken);
            var result = await request(delta =>
            {
                if (string.IsNullOrEmpty(delta))
                {
                    return;
                }

                if (Volatile.Read(ref requestCompleted) != 0)
                {
                    return;
                }
                responseBuffer.Append(delta);
                var snapshot = responseBuffer.ToString();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (Volatile.Read(ref requestCompleted) == 0)
                    {
                        UpdateCodexMessage(responseIndex, snapshot);
                    }
                });
            }, cancellationToken);

            Interlocked.Exchange(ref requestCompleted, 1);
            if (!result.Success)
            {
                UpdateCodexMessage(responseIndex, result.Status);
            }
            else if (!string.IsNullOrWhiteSpace(result.Response))
            {
                UpdateCodexMessage(responseIndex, result.Response);
            }
            else if (responseBuffer.Length == 0)
            {
                UpdateCodexMessage(responseIndex, "这次没有生成可显示的回答，请稍后重试。");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateCodexMessage(responseIndex, "已停止本次回答。");
        }
        catch (Exception exception)
        {
            UpdateCodexMessage(responseIndex, $"暂时无法完成回答：{exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref requestCompleted, 1);
            if (responseIndex >= 0 && responseIndex < CodexMessages.Count)
            {
                CodexMessages[responseIndex].IsPending = false;
            }
            SetCodexChatBusy(false);
            ScrollCodexMessagesToEnd();
        }
    }

    private async Task EnsureCodexChatInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_isCodexChatInitialized && _codexChatService.IsInitialized)
        {
            return;
        }

        var result = await _codexChatService.InitializeAsync(cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Status);
        }
        _isCodexChatInitialized = true;
    }

    private void StartNewVisibleConversation()
    {
        CodexMessages.Clear();
        AddCodexMessage("Codex", "我会围绕当前资讯解释概念、梳理证据和回答追问。你可以直接输入问题，也可以使用快捷追问。");
    }

    private int AddCodexMessage(string sender, string content)
    {
        CodexMessages.Add(CreateCodexMessage(sender, content));
        ScrollCodexMessagesToEnd();
        return CodexMessages.Count - 1;
    }

    private void UpdateCodexMessage(int index, string content)
    {
        if (index < 0 || index >= CodexMessages.Count)
        {
            return;
        }

        CodexMessages[index].Content = content;
        ScrollCodexMessagesToEnd();
    }

    private static CodexChatMessage CreateCodexMessage(string sender, string content) => new()
    {
        SenderLabel = sender,
        Kind = sender == "你" ? "user" : sender == "系统" ? "system" : "assistant",
        Content = content,
        TimeLabel = DateTimeOffset.Now.ToString("HH:mm")
    };

    private void ScrollCodexMessagesToEnd()
    {
        if (CodexMessages.LastOrDefault() is { } last)
        {
            CodexMessageList?.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
        }
    }

    private void SetCodexChatBusy(bool isBusy)
    {
        CodexBusyPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CodexChatProgressRing.IsActive = isBusy;
        SendCodexMessageButton.IsEnabled = !isBusy;
        CodexPromptTextBox.IsEnabled = !isBusy;
        AnalyzeWithCodexButton.IsEnabled = !isBusy;
        NewCodexConversationButton.IsEnabled = !isBusy;
        CodexProgressRing.IsActive = isBusy;
        CodexProgressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        _updateTimer.Stop();
        _codexChatCancellation?.Cancel();
        await AwaitActiveCodexRequestAsync();
        _codexChatCancellation?.Dispose();
        await _codexChatService.DisposeAsync();
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
