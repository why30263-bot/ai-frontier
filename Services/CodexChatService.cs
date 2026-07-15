using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Persistent, in-process client for <c>codex app-server --listen stdio://</c>.
/// One service owns one Codex process and one thread; every message is a new turn
/// on that same thread until Reset is called.
/// </summary>
public sealed class CodexChatService : IDisposable, IAsyncDisposable
{
    private const string DefaultDeveloperInstructions = """
        你是 AI Frontier 的中文辅助阅读伙伴。围绕当前资讯解释事实、术语、方法、结果、影响与局限，不执行文件修改或系统操作。
        回答必须适合 WinUI 纯文本气泡：不要使用 Markdown 标记，不要使用 #、**、表格或代码围栏。需要分段时使用简短中文标题；列举时只使用“• ”项目符号。
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly string _executable;
    private readonly string _workspace;
    private readonly string _developerInstructions;
    private readonly bool _requiresServiceTierOverride;
    private readonly TimeSpan _turnTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _turnSync = new();
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _processLifetime;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private ActiveTurn? _activeTurn;
    private string? _threadId;
    private string? _activeModel;
    private long _nextRequestId;
    private bool _initialized;
    private bool _disposed;

    public CodexChatService()
        : this(
            FindCodexExecutable() ?? string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontier", "codex-workspace"),
            DefaultDeveloperInstructions)
    {
    }

    public CodexChatService(
        string executable,
        string workspace,
        string developerInstructions,
        bool requiresServiceTierOverride = false,
        TimeSpan? turnTimeout = null)
    {
        _executable = executable;
        _workspace = Path.GetFullPath(workspace);
        _developerInstructions = developerInstructions;
        _requiresServiceTierOverride = requiresServiceTierOverride;
        _turnTimeout = turnTimeout ?? TurnTimeout;
    }

    public bool IsInitialized => _initialized && IsProcessAlive;
    public string? ThreadId => _threadId;
    public string? ActiveModel => _activeModel;

    public Task<CodexChatResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeSessionAsync(cancellationToken);

    public async Task<CodexChatResult> InitializeSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsInitialized && !string.IsNullOrWhiteSpace(_threadId))
            {
                return CodexChatResult.Succeeded("Codex 会话已连接。", threadId: _threadId);
            }

            await StopProcessAsync();
            Directory.CreateDirectory(_workspace);
            StartProcess();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InitializeTimeout);

            _ = await SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = "ai-frontier", title = "AI Frontier", version = "1.3.0" },
                    capabilities = new { experimentalApi = false }
                },
                RequestTimeout,
                timeout.Token);
            await SendNotificationAsync("initialized", null, timeout.Token);

            _activeModel = await ResolveCompatibleModelAsync(timeout.Token);

            var started = await SendRequestAsync(
                "thread/start",
                new
                {
                    cwd = _workspace,
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    serviceTier = "fast",
                    model = _activeModel,
                    serviceName = "AI Frontier",
                    ephemeral = false,
                    developerInstructions = _developerInstructions
                },
                RequestTimeout,
                timeout.Token);

            if (!TryReadString(started, out var threadId, "thread", "id"))
            {
                throw new InvalidDataException("Codex thread/start 响应缺少 thread.id。 ");
            }

            _threadId = threadId;
            _initialized = true;
            return CodexChatResult.Succeeded("Codex 会话已在应用内建立。", threadId: threadId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = BuildFailureMessage("初始化 Codex 会话失败", exception);
            await StopProcessAsync();
            return CodexChatResult.Failed(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<CodexChatResult> AnalyzeArticleAsync(
        NewsItem item,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default) =>
        AnalyzeArticleResultAsync(item, onDelta, cancellationToken);

    public Task<CodexChatResult> AnalyzeArticleResultAsync(
        NewsItem item,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default) =>
        SendMessageResultAsync(BuildArticlePrompt(item), onDelta, cancellationToken);

    public Task<CodexChatResult> SendMessageAsync(
        string message,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default) =>
        SendMessageResultAsync(message, onDelta, cancellationToken);

    public async Task<CodexChatResult> SendMessageResultAsync(
        string message,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(message))
        {
            return CodexChatResult.Failed("消息不能为空。", _threadId);
        }

        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsInitialized || string.IsNullOrWhiteSpace(_threadId))
            {
                var initialized = await InitializeSessionAsync(cancellationToken);
                if (!initialized.Success)
                {
                    return initialized;
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_turnTimeout);

            // Register the active turn before writing turn/start. app-server may
            // emit a very fast delta immediately after the response, before the
            // awaiting continuation gets scheduled.
            var active = new ActiveTurn(_threadId!, string.Empty, onDelta);
            lock (_turnSync)
            {
                _activeTurn = active;
            }

            var response = await SendRequestAsync(
                "turn/start",
                new
                {
                    threadId = _threadId,
                    input = new[] { new { type = "text", text = message } },
                    approvalPolicy = "never",
                    serviceTier = "fast"
                },
                RequestTimeout,
                timeout.Token);

            if (!TryReadString(response, out var turnId, "turn", "id"))
            {
                throw new InvalidDataException("Codex turn/start 响应缺少 turn.id。 ");
            }

            lock (_turnSync)
            {
                if (active.TurnId.Length == 0)
                {
                    active.TurnId = turnId;
                }
                else if (!string.Equals(active.TurnId, turnId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Codex turn 标识不一致。 ");
                }
            }

            using var registration = timeout.Token.Register(() =>
                active.Completion.TrySetCanceled(timeout.Token));
            var completed = await active.Completion.Task;
            return completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ResetAsync(CancellationToken.None);
            return CodexChatResult.Failed("Codex 响应超时，会话已安全重置。", _threadId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // app-server keeps a turn alive after the client-side token is
            // cancelled. Reset the process so an old turn cannot leak deltas
            // into the next article or block the next turn.
            await ResetAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = CodexChatResult.Failed(BuildFailureMessage("Codex 对话失败", exception), _threadId);
            if (!IsProcessAlive)
            {
                await ResetAsync(CancellationToken.None);
            }
            return result;
        }
        finally
        {
            lock (_turnSync)
            {
                _activeTurn = null;
            }
            _turnGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopProcessAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<CodexChatResult> ResetConversationAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken);
        return CodexChatResult.Succeeded("已开始新的 Codex 对话。 ");
    }

    private bool IsProcessAlive => _process is { HasExited: false };

    private void StartProcess()
    {
        if (string.IsNullOrWhiteSpace(_executable) || !File.Exists(_executable))
        {
            throw new FileNotFoundException("未检测到本机 Codex。请先安装并登录 Codex。", _executable);
        }

        // Always override the user config. Older Codex configs may contain a
        // now-invalid service_tier value, which prevents app-server from even
        // reaching the JSON protocol handshake.
        var command = $"\"\"{_executable}\" -c service_tier=fast app-server --listen stdio://\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /s /c {command}",
            WorkingDirectory = _workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };

        lock (_stderr)
        {
            _stderr.Clear();
        }

        _processLifetime = new CancellationTokenSource();
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;
        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 Codex app-server。 ");
        }

        _stdin = new StreamWriter(
            _process.StandardInput.BaseStream,
            new UTF8Encoding(false),
            bufferSize: 4 * 1024,
            leaveOpen: false)
        {
            AutoFlush = true
        };
        _stdoutLoop = ReadStdoutAsync(_process, _processLifetime.Token);
        _stderrLoop = ReadStderrAsync(_process, _processLifetime.Token);
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("无法登记 Codex 请求。 ");
        }

        try
        {
            await WriteMessageAsync(new { id, method, @params = parameters }, cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            using var registration = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        parameters is null
            ? WriteMessageAsync(new { method }, cancellationToken)
            : WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task<string?> ResolveCompatibleModelAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            "model/list",
            new { includeHidden = false, limit = 100 },
            RequestTimeout,
            cancellationToken);
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var models = data.EnumerateArray()
            .Select(item => TryReadString(item, out var model, "model") ? model : string.Empty)
            .Where(model => model.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0)
        {
            return null;
        }

        // Codex 0.117 can receive a newer model as the account default even
        // though that model rejects the old client. Prefer the newest broadly
        // compatible entry advertised by the server, then gracefully fall back.
        var priorities = new[]
        {
            "gpt-5.4", "gpt-5.3-codex", "gpt-5.2-codex", "gpt-5.1-codex", "gpt-5.1", "gpt-5"
        };
        foreach (var preferred in priorities)
        {
            var match = models.FirstOrDefault(model =>
                string.Equals(model, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return models.FirstOrDefault(model => !model.Contains("5.6", StringComparison.OrdinalIgnoreCase))
            ?? models[0];
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_stdin is null || !IsProcessAlive)
            {
                throw new IOException("Codex app-server 未运行。 ");
            }

            await _stdin.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions).AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                HandleMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailAll($"Codex 协议读取失败：{exception.Message}");
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                lock (_stderr)
                {
                    if (_stderr.Length > 8_000)
                    {
                        _stderr.Remove(0, 4_000);
                    }
                    _stderr.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // stderr is diagnostic only; stdout owns the protocol.
        }
    }

    private void HandleMessage(JsonElement message)
    {
        if (message.TryGetProperty("id", out var idElement) && TryReadRequestId(idElement, out var id))
        {
            if (_pending.TryGetValue(id, out var pending))
            {
                if (message.TryGetProperty("error", out var error))
                {
                    pending.TrySetException(new InvalidOperationException(ReadError(error)));
                }
                else if (message.TryGetProperty("result", out var result))
                {
                    pending.TrySetResult(result.Clone());
                }
            }
            return;
        }

        if (!message.TryGetProperty("method", out var methodElement))
        {
            return;
        }
        var method = methodElement.GetString();
        var parameters = message.TryGetProperty("params", out var paramsElement)
            ? paramsElement
            : default;

        switch (method)
        {
            case "item/agentMessage/delta":
                HandleAgentDelta(parameters);
                break;
            case "item/completed":
                HandleItemCompleted(parameters);
                break;
            case "turn/completed":
                HandleTurnCompleted(parameters);
                break;
            case "error":
                HandleTurnError(parameters);
                break;
        }
    }

    private void HandleAgentDelta(JsonElement parameters)
    {
        if (!TryReadString(parameters, out var turnId, "turnId") ||
            !TryReadString(parameters, out var delta, "delta"))
        {
            return;
        }

        ActiveTurn? active;
        lock (_turnSync)
        {
            active = _activeTurn;
        }
        if (!MatchesTurn(active, turnId))
        {
            return;
        }
        active.Text.Append(delta);
        try
        {
            active.OnDelta?.Invoke(delta);
        }
        catch
        {
            // A rendering callback cannot break the protocol reader.
        }
    }

    private void HandleItemCompleted(JsonElement parameters)
    {
        if (!TryReadString(parameters, out var turnId, "turnId") ||
            !parameters.TryGetProperty("item", out var item) ||
            !TryReadString(item, out var type, "type") ||
            !string.Equals(type, "agentMessage", StringComparison.Ordinal) ||
            !TryReadString(item, out var text, "text"))
        {
            return;
        }

        ActiveTurn? active;
        lock (_turnSync)
        {
            active = _activeTurn;
        }
        if (MatchesTurn(active, turnId) &&
            active.Text.Length == 0)
        {
            active.Text.Append(text);
            try
            {
                active.OnDelta?.Invoke(text);
            }
            catch
            {
            }
        }
    }

    private void HandleTurnCompleted(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("turn", out var turn) ||
            !TryReadString(turn, out var turnId, "id"))
        {
            return;
        }

        ActiveTurn? active;
        lock (_turnSync)
        {
            active = _activeTurn;
        }
        if (!MatchesTurn(active, turnId))
        {
            return;
        }

        var status = TryReadString(turn, out var value, "status") ? value : "completed";
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            active.Completion.TrySetResult(CodexChatResult.Succeeded(
                "Codex 已完成回复。",
                active.Text.ToString(),
                active.ThreadId,
                active.TurnId));
        }
        else
        {
            var error = turn.TryGetProperty("error", out var errorElement)
                ? ReadError(errorElement)
                : $"Codex turn 状态为 {status}。";
            active.Completion.TrySetResult(CodexChatResult.Failed(error, active.ThreadId, active.TurnId));
        }
    }

    private void HandleTurnError(JsonElement parameters)
    {
        if (!TryReadString(parameters, out var turnId, "turnId"))
        {
            return;
        }

        ActiveTurn? active;
        lock (_turnSync)
        {
            active = _activeTurn;
        }
        if (!MatchesTurn(active, turnId))
        {
            return;
        }

        var willRetry = parameters.TryGetProperty("willRetry", out var retry) && retry.ValueKind == JsonValueKind.True;
        if (!willRetry)
        {
            var error = parameters.TryGetProperty("error", out var errorElement)
                ? ReadError(errorElement)
                : "Codex 返回未知错误。";
            active.Completion.TrySetResult(CodexChatResult.Failed(error, active.ThreadId, active.TurnId));
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (_disposed || _processLifetime?.IsCancellationRequested == true)
        {
            return;
        }
        FailAll($"Codex app-server 已退出。{ReadStderrSuffix()}");
        _initialized = false;
        _threadId = null;
        _activeModel = null;
    }

    private bool MatchesTurn([NotNullWhen(true)] ActiveTurn? active, string turnId)
    {
        if (active is null)
        {
            return false;
        }
        lock (_turnSync)
        {
            if (active.TurnId.Length == 0)
            {
                active.TurnId = turnId;
            }
            return string.Equals(active.TurnId, turnId, StringComparison.Ordinal);
        }
    }

    private void FailAll(string message)
    {
        var exception = new IOException(message);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }

        ActiveTurn? active;
        lock (_turnSync)
        {
            active = _activeTurn;
        }
        active?.Completion.TrySetResult(CodexChatResult.Failed(message, active.ThreadId, active.TurnId));
    }

    private async Task StopProcessAsync()
    {
        _initialized = false;
        _threadId = null;
        _processLifetime?.Cancel();
        FailAll("Codex 会话已重置。 ");

        var process = _process;
        var stdoutLoop = _stdoutLoop;
        var stderrLoop = _stderrLoop;
        _process = null;
        _stdin = null;
        _stdoutLoop = null;
        _stderrLoop = null;
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }

        var readers = new[] { stdoutLoop, stderrLoop }.Where(task => task is not null).Cast<Task>().ToArray();
        if (readers.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(readers), Task.Delay(TimeSpan.FromSeconds(2)));
        }

        _processLifetime?.Dispose();
        _processLifetime = null;
        _pending.Clear();
    }

    private string BuildFailureMessage(string prefix, Exception exception)
    {
        var detail = exception is OperationCanceledException ? "操作超时。" : exception.Message;
        return $"{prefix}：{detail}{ReadStderrSuffix()}";
    }

    private string ReadStderrSuffix()
    {
        lock (_stderr)
        {
            var value = _stderr.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $" 诊断：{value}";
        }
    }

    private static string ReadError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "Codex 返回未知错误。";
        }
        return error.ToString();
    }

    private static bool TryReadRequestId(JsonElement element, out long id)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out id))
        {
            return true;
        }
        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out id))
        {
            return true;
        }
        id = 0;
        return false;
    }

    private static bool TryReadString(JsonElement element, out string value, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                value = string.Empty;
                return false;
            }
        }
        value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private static string BuildArticlePrompt(NewsItem item)
    {
        static string Bullets(IEnumerable<string> values) =>
            string.Join(Environment.NewLine, values.Select(value => $"- {value}"));

        var terms = item.TermExplanations.Count == 0
            ? "• 本文没有额外术语卡"
            : string.Join(Environment.NewLine, item.TermExplanations.Select(term => $"• {term.Term}：{term.Explanation}"));

        return $"""
            请按照固定阅读协议分析以下资讯，并在本会话中继续回答后续问题。
            回答不要使用 Markdown 标记，不要使用 #、**、表格或代码围栏。使用简短中文标题分段，列举只使用“• ”。

            标题
            {item.Title}

            - 内容类型：{item.ContentType}
            - 主题：{string.Join("、", item.Topics)}
            - 来源：{item.SourceName}
            - 日期：{item.PublishedAt}
            - 原始链接：{item.SourceUrl}

            领域定位
            {item.ReaderContext}

            术语解释
            {terms}

            摘要
            {item.Summary}

            详细内容
            {item.FullBrief}

            关键事实
            {Bullets(item.KeyFacts)}

            背景与边界
            {item.Context}
            {item.Limitations}

            来源链
            {Bullets(item.SourceTrail)}
            """;
    }

    private static string? FindCodexExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim(), "codex.cmd"));
                candidates.Add(Path.Combine(directory.Trim(), "codex.exe"));
            }
            catch
            {
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopProcessAsync().GetAwaiter().GetResult();
        _lifecycleGate.Dispose();
        _writeGate.Dispose();
        _turnGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopProcessAsync();
        _lifecycleGate.Dispose();
        _writeGate.Dispose();
        _turnGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ActiveTurn
    {
        public ActiveTurn(string threadId, string turnId, Action<string>? onDelta)
        {
            ThreadId = threadId;
            TurnId = turnId;
            OnDelta = onDelta;
        }

        public string ThreadId { get; }
        public string TurnId { get; set; }
        public Action<string>? OnDelta { get; }
        public StringBuilder Text { get; } = new();
        public TaskCompletionSource<CodexChatResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record CodexChatResult(
    bool Success,
    string Status,
    string Response,
    string? ThreadId,
    string? TurnId)
{
    public static CodexChatResult Succeeded(
        string status,
        string response = "",
        string? threadId = null,
        string? turnId = null) =>
        new(true, status, response, threadId, turnId);

    public static CodexChatResult Failed(
        string status,
        string? threadId = null,
        string? turnId = null) =>
        new(false, status, string.Empty, threadId, turnId);
}
