using System.Collections.ObjectModel;
using System.Text;
using AIFrontier.Models;
using AIFrontier.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace AIFrontier;

public sealed partial class CodexChatWindow : Window
{
    private readonly CodexChatService _service = new();
    private CancellationTokenSource? _cancellation;
    private Task _activeRequest = Task.CompletedTask;
    private long _requestGeneration;
    private bool _closed;
    private bool _allowClose;

    public ObservableCollection<CodexChatMessage> Messages { get; } = [];
    public event EventHandler? WorkspaceClosed;

    public CodexChatWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ApplyDarkTitleBar();
        AppWindow.Resize(new SizeInt32(680, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        StartVisibleConversation();
    }

    private void ApplyDarkTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = AppWindow.TitleBar;
        var background = Color.FromArgb(255, 23, 25, 29);
        var hoverBackground = Color.FromArgb(255, 45, 48, 55);
        var pressedBackground = Color.FromArgb(255, 61, 65, 74);
        var foreground = Color.FromArgb(255, 245, 247, 250);
        var inactiveForeground = Color.FromArgb(255, 174, 180, 192);

        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    public async Task AnalyzeArticleAsync(NewsItem item)
    {
        if (_closed)
        {
            return;
        }

        CurrentArticleTitle.Text = item.Title;
        Activate();
        AddMessage("你", $"请深度分析：{item.Title}");
        await StartRequestAsync((onDelta, token) =>
            _service.AnalyzeArticleAsync(item, onDelta, token));
    }

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) =>
        await SendPromptAsync(PromptTextBox.Text);

    private async void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }
        e.Handled = true;
        await SendPromptAsync(PromptTextBox.Text);
    }

    private async void QuickPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string prompt })
        {
            await SendPromptAsync(prompt);
        }
    }

    private async Task SendPromptAsync(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || BusyPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        PromptTextBox.Text = string.Empty;
        AddMessage("你", prompt);
        await StartRequestAsync((onDelta, token) =>
            _service.SendMessageAsync(prompt, onDelta, token));
    }

    private async void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        await AwaitActiveRequestAsync();
        SetBusy(true);
        try
        {
            var reset = await _service.ResetConversationAsync();
            if (!reset.Success)
            {
                throw new InvalidOperationException(reset.Status);
            }
            StartVisibleConversation();
        }
        catch (Exception exception)
        {
            AddMessage("系统", $"无法开始新对话：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private async Task StartRequestAsync(Func<Action<string>, CancellationToken, Task<CodexChatResult>> request)
    {
        _cancellation?.Cancel();
        await AwaitActiveRequestAsync();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _activeRequest = RunRequestAsync(request, _cancellation.Token);
        await _activeRequest;
    }

    private async Task RunRequestAsync(
        Func<Action<string>, CancellationToken, Task<CodexChatResult>> request,
        CancellationToken token)
    {
        var generation = Interlocked.Increment(ref _requestGeneration);
        var index = AddMessage("Codex", "正在思考…");
        Messages[index].IsPending = true;
        var buffer = new StringBuilder();
        var completed = 0;
        SetBusy(true, "Codex 已收到消息，正在思考…");
        _ = AdvanceWaitingStatusAsync(generation, index, token);

        try
        {
            var result = await request(delta =>
            {
                if (string.IsNullOrEmpty(delta) || Volatile.Read(ref completed) != 0)
                {
                    return;
                }
                buffer.Append(delta);
                var snapshot = buffer.ToString();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (Volatile.Read(ref completed) == 0 && generation == Volatile.Read(ref _requestGeneration))
                    {
                        BusyStatusText.Text = "Codex 正在组织回答…";
                        UpdateMessage(index, snapshot);
                    }
                });
            }, token);

            Interlocked.Exchange(ref completed, 1);
            UpdateMessage(index, result.Success
                ? string.IsNullOrWhiteSpace(result.Response) ? "这次没有生成可显示的回答，请稍后重试。" : result.Response
                : result.Status);
        }
        catch (OperationCanceledException)
        {
            UpdateMessage(index, "已停止本次回答。");
        }
        catch (Exception exception)
        {
            UpdateMessage(index, $"暂时无法完成回答：{exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref completed, 1);
            if (index >= 0 && index < Messages.Count)
            {
                Messages[index].IsPending = false;
            }
            if (generation == Volatile.Read(ref _requestGeneration))
            {
                SetBusy(false);
            }
            ScrollToEnd();
        }
    }

    private async Task AdvanceWaitingStatusAsync(long generation, int messageIndex, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(7), token);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation == Volatile.Read(ref _requestGeneration) && BusyPanel.Visibility == Visibility.Visible)
                {
                    BusyStatusText.Text = "Codex 正在检索和核对资料…";
                    if (messageIndex >= 0 && messageIndex < Messages.Count && Messages[messageIndex].IsPending)
                    {
                        UpdateMessage(messageIndex, "正在检索和核对资料…");
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartVisibleConversation()
    {
        Messages.Clear();
        AddMessage("Codex", "我会在这个独立窗口中持续围绕资讯解释概念、梳理证据并回答追问。窗口可以自由移动、缩放、最小化或最大化。");
    }

    private int AddMessage(string sender, string content)
    {
        Messages.Add(new CodexChatMessage
        {
            SenderLabel = sender,
            Kind = sender == "你" ? "user" : sender == "系统" ? "system" : "assistant",
            Content = content,
            TimeLabel = DateTimeOffset.Now.ToString("HH:mm")
        });
        ScrollToEnd();
        return Messages.Count - 1;
    }

    private void UpdateMessage(int index, string content)
    {
        if (index >= 0 && index < Messages.Count)
        {
            Messages[index].Content = content;
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (Messages.LastOrDefault() is { } last)
        {
            MessageList?.ScrollIntoView(last);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        if (busy && !string.IsNullOrWhiteSpace(status))
        {
            BusyStatusText.Text = status;
        }
        SendButton.IsEnabled = !busy;
        PromptTextBox.IsEnabled = !busy;
        NewConversationButton.IsEnabled = !busy;
    }

    private async Task AwaitActiveRequestAsync()
    {
        try
        {
            await _activeRequest;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _cancellation?.Cancel();
        await AwaitActiveRequestAsync();
        _cancellation?.Dispose();
        await _service.DisposeAsync();
        AppWindow.Closing -= AppWindow_Closing;
        WorkspaceClosed?.Invoke(this, EventArgs.Empty);
    }
}
