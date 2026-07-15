using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class CodexIntegrationService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _workspace = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIFrontier",
        "codex-workspace");
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private CodexChatService? _chatService;
    private string? _chatExecutable;

    public string WorkspacePath => _workspace;
    public CodexChatService? ChatService => _chatService;

    public async Task<CodexIntegrationResult> GetStatusAsync()
    {
        var executable = FindCodexExecutable();
        if (executable is null)
        {
            return new(false, "未检测到本机 Codex。请先安装并登录 Codex。", string.Empty);
        }

        var probe = await ProbeAsync(executable);
        if (!probe.IsUsable)
        {
            return new(false, $"检测到 Codex，但当前无法启动：{probe.Message}", executable);
        }

        var connected = _chatService?.IsInitialized == true ||
            File.Exists(Path.Combine(_workspace, "integration.json"));
        return new(connected, connected ? $"已接入本机 Codex · {probe.Version}" : $"已检测到 Codex · {probe.Version}", executable);
    }

    public async Task<CodexIntegrationResult> ConnectAsync()
    {
        var executable = FindCodexExecutable();
        if (executable is null)
        {
            return new(false, "未检测到本机 Codex。请先安装并登录 Codex。", string.Empty);
        }

        var probe = await ProbeAsync(executable);
        if (!probe.IsUsable)
        {
            return new(false, $"Codex 启动检查失败：{probe.Message}", executable);
        }

        await PrepareWorkspaceAsync(executable, probe.RequiresServiceTierOverride, probe.Version);
        return new(true, $"已接入本机 Codex · {probe.Version}。深度分析会在应用内打开。", executable);
    }

    public async Task<CodexIntegrationResult> AnalyzeAsync(NewsItem item)
    {
        var executable = FindCodexExecutable();
        if (executable is null)
        {
            return new(false, "未检测到本机 Codex，无法进行深度分析。", string.Empty);
        }

        var probe = await ProbeAsync(executable);
        if (!probe.IsUsable)
        {
            return new(false, $"Codex 启动检查失败：{probe.Message}", executable);
        }

        await PrepareWorkspaceAsync(executable, probe.RequiresServiceTierOverride, probe.Version);
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "CURRENT_ARTICLE.md"),
            BuildArticleContext(item),
            new UTF8Encoding(false));

        var chat = await GetOrCreateChatServiceAsync(executable, probe.RequiresServiceTierOverride);
        var initialized = await chat.InitializeSessionAsync();
        if (!initialized.Success)
        {
            return new(false, initialized.Status, executable);
        }

        var result = await chat.AnalyzeArticleResultAsync(item);
        return new(result.Success, result.Status, executable);
    }

    private async Task PrepareWorkspaceAsync(string executable, bool requiresOverride, string version)
    {
        Directory.CreateDirectory(_workspace);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "AGENTS.md"), AgentInstructions, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "APP_OVERVIEW.md"), AppOverview, new UTF8Encoding(false));
        var state = new
        {
            schemaVersion = 1,
            connectedAt = DateTimeOffset.Now,
            codexExecutable = executable,
            codexVersion = version,
            requiresServiceTierOverride = requiresOverride,
            app = "AI Frontier"
        };
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "integration.json"),
            JsonSerializer.Serialize(state, JsonOptions),
            new UTF8Encoding(false));
    }

    private async Task<CodexChatService> GetOrCreateChatServiceAsync(
        string executable,
        bool requiresServiceTierOverride)
    {
        await _chatGate.WaitAsync();
        try
        {
            if (_chatService is not null &&
                string.Equals(_chatExecutable, executable, StringComparison.OrdinalIgnoreCase))
            {
                return _chatService;
            }

            if (_chatService is not null)
            {
                await _chatService.DisposeAsync();
            }

            _chatExecutable = executable;
            _chatService = new CodexChatService(
                executable,
                _workspace,
                AgentInstructions,
                requiresServiceTierOverride);
            return _chatService;
        }
        finally
        {
            _chatGate.Release();
        }
    }

    private static async Task<CodexProbe> ProbeAsync(string executable)
    {
        var version = await RunCodexAsync(executable, "--version");
        var versionLabel = version.ExitCode == 0 ? version.Output.Trim() : "Codex";
        var normal = await RunCodexAsync(executable, "features list");
        if (normal.ExitCode == 0)
        {
            return new(true, versionLabel, false, versionLabel);
        }

        var withOverride = await RunCodexAsync(executable, "-c service_tier=fast features list");
        if (withOverride.ExitCode == 0)
        {
            return new(true, versionLabel, true, versionLabel);
        }

        var message = string.IsNullOrWhiteSpace(withOverride.Error) ? normal.Error : withOverride.Error;
        return new(false, string.Empty, false, message.Trim());
    }

    private static async Task<ProcessResult> RunCodexAsync(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{executable}\" {arguments}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var result = await BoundedProcessRunner.RunAsync(startInfo, ProbeTimeout);
        return new(result.ExitCode, result.Output, result.Error);
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
                // Ignore malformed PATH entries.
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string BuildArticleContext(NewsItem item)
    {
        static string Bullets(IEnumerable<string> values) => string.Join(Environment.NewLine, values.Select(value => $"- {value}"));
        return $"""
            # 当前资讯

            ## 标题
            {item.Title}

            ## 基础信息
            - 分类：{item.Category}
            - 内容类型：{item.ContentType}
            - 主题：{string.Join("、", item.Topics)}
            - 来源：{item.SourceName}
            - 日期：{item.PublishedAt}
            - 可信度标签：{item.Confidence}
            - 原始链接：{item.SourceUrl}

            ## 摘要
            {item.Summary}

            ## 领域定位
            {item.ReaderContext}

            ## 术语解释
            {Bullets(item.TermExplanations.Select(term => $"{term.Term}：{term.Explanation}"))}

            ## 详细内容
            {item.FullBrief}

            ## 关键事实
            {Bullets(item.KeyFacts)}

            ## 背景
            {item.Context}

            ## 应用内现有判断
            - 入门解释：{item.BeginnerExplainer}
            - 可能影响：{item.Impact}
            - 局限与边界：{item.Limitations}
            - 后续观察：{item.WhatToWatch}

            ## 来源链
            {Bullets(item.SourceTrail)}
            """;
    }

    private const string AgentInstructions = """
        # AI Frontier Codex 辅助阅读规则

        你是 AI Frontier 的中文辅助阅读伙伴，面向 AI 基础知识不多、希望了解大模型和 Agent 最新进展的读者。

        固定阅读协议：
        1. 先区分来源明确披露的事实、你的解释、仍待验证的判断。
        2. 优先打开 CURRENT_ARTICLE.md 中的原始链接；需要时使用网络搜索做交叉验证。
        3. 用普通中文解释术语，第一次出现缩写时说明全称与含义。
        4. 给出“发生了什么、为什么重要、对普通人意味着什么、有哪些局限、接下来观察什么”。
        5. 论文不等于成熟产品，GitHub Star 不等于生产可用，厂商宣传不等于独立验证。
        6. 不编造原文没有的数字、发布日期、模型能力或引用；无法确认时明确说无法确认。
        7. 默认先给 5 分钟可读版本，再等待用户追问；不要执行与阅读无关的文件修改或系统操作。
        8. 回答用于 WinUI 纯文本气泡：不要使用 Markdown 标记，不要使用 #、**、表格或代码围栏；分段使用简短中文标题，列举只使用“• ”。
        """;

    private const string AppOverview = """
        # AI Frontier 基本运行逻辑

        AI Frontier 是一个开源 Windows AI 新闻流，关注大模型、Agent、重要研究、开源项目和产业动态。

        - 公共编辑源位于 https://github.com/why30263-bot/ai-frontier 。
        - GitHub Actions 定时采集公开 RSS、Atom、API 和近期活跃 GitHub 项目，完成去重、来源限额与中文编辑。
        - 客户端只读取通过中文编辑门槛的 news.json；远程不可用时使用最近一份合格缓存或安装包内快照，原始采集结果不会直接进入新闻流。
        - 首页按重要性、个人偏好和探索内容混合排序，并保持大模型、Agent、研究、开源和产业动态的基本覆盖。
        - 内容类型与主题分开：论文或开源项目可以同时带有大模型、Agent 等主题；报道结构由内容类型决定，栏目与推荐由主题共同决定。
        - 新闻列表只显示标题和摘要；详情页结论先行，并根据事实材料动态使用 3–5 段，不为凑结构补写套话。
        - 用户喜欢、不感兴趣、收藏和评分只保存在本机，用于调整主题、来源和阅读深度权重。
        - Codex 不是采集和更新的强制依赖；它只在用户主动点击时读取当前资讯上下文并辅助深度阅读。
        """;

    private sealed record CodexProbe(bool IsUsable, string Version, bool RequiresServiceTierOverride, string Message);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        _chatService?.Dispose();
        _chatGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_chatService is not null)
        {
            await _chatService.DisposeAsync();
        }
        _chatGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed record CodexIntegrationResult(bool IsConnected, string Status, string ExecutablePath);
