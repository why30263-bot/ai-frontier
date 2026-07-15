using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Runs the same collect - select - rewrite - validate boundary as the cloud workflow,
/// but uses the signed-in local Codex process. The process is hosted over stdio with
/// CreateNoWindow, so no terminal or Codex window is shown.
/// </summary>
public sealed class LocalCodexNewsUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly BuiltInCollectorService _collector = new();
    private readonly EditionQualityPolicy _policy = new();

    public async Task<LocalCodexUpdateResult> UpdateAsync(
        NewsEdition currentEdition,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        var executable = CodexIntegrationService.FindCodexExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new(false, false, false, null, "未检测到已安装并登录的本机 Codex。");
        }

        reportStatus?.Invoke("正在采集论文、模型、Agent 与开源项目…");
        var configuration = await CloudFeedHealthService.LoadConfigurationAsync(cancellationToken);
        IReadOnlyList<NewsItem> candidates;
        try
        {
            candidates = await _collector.CollectAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, true, false, null, "本机 Codex 已连接，但资讯源采集暂时失败。");
        }

        var currentUrls = currentEdition.Items
            .Select(item => NormalizeUrl(item.SourceUrl))
            .Where(url => url.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedCandidates = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceUrl))
            .Where(item => !currentUrls.Contains(NormalizeUrl(item.SourceUrl)))
            .DistinctBy(item => NormalizeUrl(item.SourceUrl), StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (selectedCandidates.Count == 0)
        {
            return new(true, true, false, currentEdition, "已检查，暂时没有新的合格资讯");
        }

        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIFrontier",
            "local-update-workspace");
        Directory.CreateDirectory(workspace);
        reportStatus?.Invoke($"已采集 {candidates.Count} 条候选 · 正在连接本机 Codex…");

        const string instructions = """
            你是 AI Frontier 的本地中文资讯编辑。你的任务是把候选资料整理成可靠、易读的 AI 前沿简报 JSON。
            只返回 JSON，不要 Markdown，不要解释，不要声明，不要求用户核实，不执行文件修改。
            """;
        await using var codex = new CodexChatService(executable, workspace, instructions);
        var initialized = await codex.InitializeAsync(cancellationToken);
        if (!initialized.Success)
        {
            return new(false, false, false, null, initialized.Status);
        }

        var prompt = BuildPrompt(currentEdition, selectedCandidates);
        reportStatus?.Invoke($"已连接 Codex · 正在筛选并撰写 {selectedCandidates.Count} 条候选…");
        var answer = await codex.SendMessageResultAsync(prompt, cancellationToken: cancellationToken);
        if (!answer.Success)
        {
            return new(false, true, false, null, answer.Status);
        }

        var edition = TryParseEdition(answer.Response);
        reportStatus?.Invoke("Codex 已完成撰写 · 正在检查中文、结构与栏目覆盖…");
        NormalizeMetadata(edition);
        if (!_policy.IsQualified(edition) || !HasGroundedSources(edition, currentEdition, selectedCandidates))
        {
            reportStatus?.Invoke("初稿未通过质量检查 · Codex 正在自动修订…");
            var repair = await codex.SendMessageResultAsync(
                "上一份 JSON 没有通过应用的硬性质量门槛。请重新输出完整 JSON：必须正好 20 条；每 10 条至少含 2 条大模型、2 条 Agent、1 篇论文、1 个开源项目；每条为中文标题、50 个以上汉字的摘要、2 到 4 个术语解释、3 到 5 个有信息量的小节，每条小节正文至少 45 个汉字，总计至少 275 个汉字。只输出 JSON。",
                cancellationToken: cancellationToken);
            edition = repair.Success ? TryParseEdition(repair.Response) : null;
            NormalizeMetadata(edition);
        }

        return _policy.IsQualified(edition) && HasGroundedSources(edition, currentEdition, selectedCandidates)
            ? new(true, true, true, edition, "本机 Codex 已完成今日资讯整理。")
            : new(false, true, false, null, "本机 Codex 已返回结果，但本次内容未通过完整性检查，已保留上一期资讯。");
    }

    private static void NormalizeMetadata(NewsEdition? edition)
    {
        if (edition is null)
        {
            return;
        }
        edition.SchemaVersion = EditionQualityPolicy.QualifiedSchemaVersion;
        edition.EditionDate = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        edition.GeneratedAt = DateTimeOffset.Now;
        edition.WindowHours = Math.Clamp(edition.WindowHours <= 0 ? 72 : edition.WindowHours, 24, 336);
    }

    private static NewsEdition? TryParseEdition(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<NewsEdition>(response[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasGroundedSources(
        NewsEdition? edition,
        NewsEdition currentEdition,
        IReadOnlyList<NewsItem> candidates)
    {
        if (edition is null)
        {
            return false;
        }
        var allowed = currentEdition.Items
            .Concat(candidates)
            .Select(item => NormalizeUrl(item.SourceUrl))
            .Where(url => url.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return edition.Items.All(item =>
            allowed.Contains(NormalizeUrl(item.SourceUrl)) &&
            DateTimeOffset.TryParse(item.PublishedAt, out var published) &&
            published <= DateTimeOffset.Now.AddMinutes(10));
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string BuildPrompt(NewsEdition currentEdition, IReadOnlyList<NewsItem> candidates)
    {
        var current = JsonSerializer.Serialize(currentEdition, JsonOptions);
        var discovered = JsonSerializer.Serialize(candidates, JsonOptions);
        return $$"""
            今天是 {{DateTimeOffset.Now:yyyy-MM-dd}}。请生成 AI Frontier 今日中文资讯版，返回一个 NewsEdition JSON 对象。

            编辑目标：像科技记者一样，第一时间说清“发生了什么、做到了什么、贡献是什么、怎么做到、有什么影响和局限”。重视大模型、Agent、论文与真正有技术价值的开源项目，不收录娱乐化话题。可以保留当前版仍重要的内容，只用更强、更近的新事件替换弱项。

            硬性格式：schemaVersion=2；editionDate={{DateTimeOffset.Now:yyyy-MM-dd}}；windowHours 24-336；generatedAt 使用当前 ISO 时间；items 正好 20 条。每连续 10 条至少有 2 条 topics 含“大模型”、2 条含“Agent”、1 条 contentType 为“论文”、1 条为“开源项目”。contentType 只能是“论文”“开源项目”“模型发布”“Agent产品”“产业事件”；topics 只能使用“大模型”“Agent”“重要研究”“开源项目”“产业动态”。

            每条必须：中文标题；50 个以上汉字的摘要并直接交代核心成果；readerContext 用一句话说明所属领域；2-4 个 termExplanations；3-5 个 briefSections，标题清楚且正文每节至少 45 个汉字、全文至少 275 个汉字，按事件原本逻辑组织。论文按背景、方法、实验结果、贡献与限制；产品或模型按需求、能力变化、实现方式、影响与限制；开源项目按解决问题、核心机制、适用场景、成熟度与限制。不要出现“完整报道、发布信息与适用范围、趋势参考、为什么值得看、真实性声明、筛选说明、请读者核实”等空话。sourceUrl 必须来自提供的资料，不要编造链接。各评分使用 0 到 1。

            当前合格版：
            {{current}}

            本机刚采集的候选资料：
            {{discovered}}

            只输出完整 JSON 对象。
            """;
    }
}
