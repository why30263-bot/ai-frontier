using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Builds a hidden ready-news buffer one article at a time. Each completed item
/// is validated and persisted immediately, so later timeouts cannot discard it.
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

    public async Task<LocalCodexUpdateResult> UpdateAsync(
        NewsEdition currentEdition,
        IReadOnlyList<NewsItem> alreadyReady,
        int targetReadyCount,
        Func<NewsItem, CancellationToken, Task<ReadyNewsMutationResult>> saveReadyItem,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        var executable = CodexIntegrationService.FindCodexExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new(false, false, 0, alreadyReady.Count, "未检测到已安装并登录的本机 Codex。");
        }
        if (alreadyReady.Count >= targetReadyCount)
        {
            return new(true, true, 0, alreadyReady.Count, $"已有 {alreadyReady.Count} 篇最新资讯就绪");
        }

        reportStatus?.Invoke($"后台资讯池已有 {alreadyReady.Count} 篇 · 正在采集新来源…");
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
            return new(false, true, 0, alreadyReady.Count, "资讯源采集暂时失败，后台已就绪内容不受影响。");
        }

        var selectedCandidates = SelectUnseenCandidates(
            currentEdition,
            alreadyReady,
            candidates,
            Math.Max(12, targetReadyCount + 4));
        if (selectedCandidates.Count == 0)
        {
            return new(true, true, 0, alreadyReady.Count, $"已有 {alreadyReady.Count} 篇最新资讯就绪 · 暂无更多新候选");
        }

        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIFrontier",
            "local-update-workspace");
        Directory.CreateDirectory(workspace);
        const string instructions = """
            你是 AI Frontier 的本地中文科技记者。每次只处理一条给定来源，写成结构化中文资讯。
            不编造来源、日期、数字或结论。只返回 JSON，不要 Markdown、解释、声明或文件操作。
            """;
        await using var codex = new CodexChatService(
            executable,
            workspace,
            instructions,
            turnTimeout: TimeSpan.FromSeconds(75));
        var initialized = await codex.InitializeAsync(cancellationToken);
        if (!initialized.Success)
        {
            return new(false, false, 0, alreadyReady.Count, initialized.Status);
        }

        var readyCount = alreadyReady.Count;
        var completedThisRun = 0;
        var failedThisRun = 0;
        for (var index = 0; index < selectedCandidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readyCount >= targetReadyCount)
            {
                break;
            }
            var candidate = selectedCandidates[index];
            reportStatus?.Invoke($"已有 {readyCount} 篇就绪 · 正在准备第 {index + 1}/{selectedCandidates.Count} 篇");

            var receivedCharacters = 0;
            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var startedAt = DateTimeOffset.Now;
            var heartbeat = ReportHeartbeatAsync(
                () => Volatile.Read(ref receivedCharacters),
                startedAt,
                readyCount,
                index + 1,
                selectedCandidates.Count,
                reportStatus,
                heartbeatCancellation.Token);
            var answer = await codex.SendMessageResultAsync(
                BuildPrompt(candidate),
                delta => Interlocked.Add(ref receivedCharacters, delta.Length),
                cancellationToken);
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }

            if (!answer.Success)
            {
                failedThisRun++;
                reportStatus?.Invoke($"第 {index + 1} 篇未完成，正在继续下一篇 · 已有 {readyCount} 篇就绪");
                continue;
            }

            var generated = TryParseItems(answer.Response);
            var grounded = GroundGeneratedItems(generated, [candidate]).FirstOrDefault();
            if (grounded is null || !EditionQualityPolicy.IsQualifiedItem(grounded))
            {
#if DEBUG
                await File.WriteAllTextAsync(
                    Path.Combine(workspace, "last-invalid-news.json"),
                    answer.Response,
                    cancellationToken);
#endif
                failedThisRun++;
                reportStatus?.Invoke($"第 {index + 1} 篇未通过质量检查，继续下一篇 · 已有 {readyCount} 篇就绪");
                continue;
            }

            var saveResult = await saveReadyItem(grounded, cancellationToken);
            readyCount = saveResult.Count;
            if (saveResult.Persisted && saveResult.Changed)
            {
                completedThisRun++;
                reportStatus?.Invoke($"已有 {readyCount} 篇最新资讯就绪 · 后台继续准备");
            }
            else
            {
                failedThisRun++;
                reportStatus?.Invoke($"第 {index + 1} 篇未进入资讯池，继续下一篇 · 已有 {readyCount} 篇就绪");
            }
        }

        var status = completedThisRun > 0
            ? $"已有 {readyCount} 篇最新资讯就绪 · 本轮新增 {completedThisRun} 篇"
            : failedThisRun > 0
                ? $"已有 {readyCount} 篇资讯就绪 · 本轮候选未能完成，可稍后重试"
                : $"已有 {readyCount} 篇最新资讯就绪";
        return new(completedThisRun > 0 || failedThisRun == 0, true, completedThisRun, readyCount, status);
    }

    private static List<NewsItem> SelectUnseenCandidates(
        NewsEdition currentEdition,
        IReadOnlyList<NewsItem> ready,
        IReadOnlyList<NewsItem> candidates,
        int maximumNewItems)
    {
        var knownUrls = currentEdition.Items
            .Concat(ready)
            .Select(item => NormalizeUrl(item.SourceUrl))
            .Where(url => url.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceUrl))
            .Where(item => !knownUrls.Contains(NormalizeUrl(item.SourceUrl)))
            .DistinctBy(item => NormalizeUrl(item.SourceUrl), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => ParseDate(item.PublishedAt))
            .ThenByDescending(item => item.TechnicalRelevanceScore + item.InnovationScore)
            .Take(Math.Max(0, maximumNewItems))
            .ToList();
    }

    private static List<NewsItem> GroundGeneratedItems(
        IReadOnlyList<NewsItem> generated,
        IReadOnlyList<NewsItem> candidates)
    {
        var candidateByUrl = candidates
            .GroupBy(item => NormalizeUrl(item.SourceUrl), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var grounded = new List<NewsItem>();
        foreach (var item in generated)
        {
            if (!candidateByUrl.TryGetValue(NormalizeUrl(item.SourceUrl), out var source))
            {
                continue;
            }
            item.Id = source.Id;
            item.SourceUrl = source.SourceUrl;
            item.SourceName = source.SourceName;
            item.PublishedAt = source.PublishedAt;
            item.Brand = source.Brand;
            item.BrandColor = source.BrandColor;
            item.LogoAsset = source.LogoAsset;
            item.SourceTrail = [source.SourceUrl];
            item.IsPrimarySourceVerified = source.IsPrimarySourceVerified;
            item.IndependentSourceCount = Math.Max(item.IndependentSourceCount, source.IndependentSourceCount);
            grounded.Add(item);
        }
        return grounded;
    }

    private static List<NewsItem> TryParseItems(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return [];
        }
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<GeneratedItemsResponse>(
                response[start..(end + 1)],
                JsonOptions)?.Items ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildPrompt(NewsItem candidate)
    {
        var source = JsonSerializer.Serialize(candidate, JsonOptions);
        return $$"""
            请把下面这一条来源写成可直接进入 AI Frontier 的中文资讯，只返回 {"items":[NewsItem]}。

            硬性要求：sourceUrl 必须原样复制；contentType 只能是“论文”“开源项目”“模型发布”“Agent产品”“产业事件”；topics 只能使用“大模型”“Agent”“重要研究”“开源项目”“产业动态”。中文标题先说做到什么；summary 至少 50 个汉字；readerContext 至少 12 个汉字；termExplanations 2-4 个且每个解释至少 12 个汉字；briefSections 3-5 节，每一节必须使用 {"title":"章节标题","body":"正文"}，不要使用 heading；每节至少 55 个汉字、合计至少 300 个汉字。论文按背景、方法、结果、贡献和限制，其他类型按问题、能力或机制、应用、影响和限制。technicalRelevanceScore 至少 0.55，innovationScore 至少 0.35。不要写筛选说明、真实性声明、为什么值得看等空话。

            来源资料：
            {{source}}

            只输出 JSON 对象。
            """;
    }

    private static async Task ReportHeartbeatAsync(
        Func<int> readCharacters,
        DateTimeOffset startedAt,
        int readyCount,
        int currentIndex,
        int total,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var elapsed = Math.Max(1, (int)(DateTimeOffset.Now - startedAt).TotalSeconds);
            var characters = readCharacters();
            reportStatus?.Invoke(characters > 0
                ? $"已有 {readyCount} 篇就绪 · 第 {currentIndex}/{total} 篇已接收约 {characters} 字"
                : $"已有 {readyCount} 篇就绪 · 正在准备第 {currentIndex}/{total} 篇 · {elapsed} 秒");
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private sealed class GeneratedItemsResponse
    {
        public List<NewsItem> Items { get; set; } = [];
    }
}
