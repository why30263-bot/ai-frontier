using System.Text.Json;
using System.Diagnostics;
using AIFrontier.Models;
using AIFrontier.Services;

var failures = new List<string>();
var passes = new List<string>();
var now = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(8));
var policy = new EditionQualityPolicy(() => now);
var testRoot = Path.Combine(Path.GetTempPath(), "AIFrontier-quality-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

void Check(bool condition, string description)
{
    if (condition)
    {
        passes.Add(description);
        Console.WriteLine($"PASS  {description}");
    }
    else
    {
        failures.Add(description);
        Console.WriteLine($"FAIL  {description}");
    }
}

string Han(int count) => new('中', count);

NewsEdition ValidEdition(int sequence = 0, int sectionCharacters = 100)
{
    var items = Enumerable.Range(0, 20).Select(index =>
    {
        var slot = index % EditionQualityPolicy.BatchSize;
        var contentType = slot switch
        {
            0 or 1 => "论文",
            2 or 3 => "开源项目",
            4 or 5 => "模型发布",
            6 or 7 => "Agent产品",
            _ => "产业事件"
        };
        var topic = slot switch
        {
            0 or 2 or 4 => "大模型",
            1 or 3 or 6 => "Agent",
            5 or 7 => "重要研究",
            8 => "开源项目",
            _ => "产业动态"
        };

        return new NewsItem
        {
            Id = $"edition-{sequence}-item-{index}",
            ContentType = contentType,
            Topics = [topic],
            Title = $"中文技术标题{index}",
            Summary = Han(55),
            ReaderContext = $"这是人工智能领域的技术进展，帮助普通读者理解本条资讯正在解决的具体问题。{index}",
            TermExplanations =
            [
                new TermExplanation { Term = "核心术语", Explanation = "这是理解当前技术变化所必需的基础概念说明。" },
                new TermExplanation { Term = "评测指标", Explanation = "这是用来比较方法效果和适用边界的一组衡量标准。" }
            ],
            BriefSections =
            [
                new BriefSection { Title = "核心结论", Body = Han(sectionCharacters) },
                new BriefSection { Title = "方法结果", Body = Han(sectionCharacters) },
                new BriefSection { Title = "实际影响", Body = Han(sectionCharacters) }
            ],
            PublishedAt = now.AddHours(-index).ToString("O"),
            SourceName = "官方技术来源",
            SourceUrl = $"https://example.com/{sequence}/{index}",
            InnovationScore = 0.8,
            TechnicalRelevanceScore = 0.9
        };
    }).ToList();

    return new NewsEdition
    {
        SchemaVersion = 2,
        EditionDate = now.ToString("yyyy-MM-dd"),
        WindowHours = 72,
        GeneratedAt = now.AddMinutes(-sequence),
        Items = items
    };
}

try
{
    var valid = ValidEdition();
    Check(policy.IsQualified(valid), "schema v2 的完整中文技术版通过门禁");

    var v1 = ValidEdition();
    v1.SchemaVersion = 1;
    Check(!policy.IsQualified(v1), "schema v1 被严格拒绝");

    var v3 = ValidEdition();
    v3.SchemaVersion = 3;
    Check(!policy.IsQualified(v3), "未知 schema v3 被严格拒绝");

    var future = ValidEdition();
    future.GeneratedAt = now.AddMinutes(6);
    Check(!policy.IsQualified(future), "未来生成时间超过容差时整期拒绝");

    var malformed = ValidEdition();
    malformed.Items[19].Summary = "摘要过短";
    Check(!policy.IsQualified(malformed), "任一坏条目会原子拒绝整期");

    var missingGlossary = ValidEdition();
    missingGlossary.Items[0].TermExplanations = [];
    Check(!policy.IsQualified(missingGlossary), "缺少入门术语解释时整期拒绝");

    var nullItem = ValidEdition();
    nullItem.Items[19] = null!;
    Check(!policy.IsQualified(nullItem), "JSON 中的 null 条目会安全拒绝而不是使应用崩溃");

    var exactThreshold = ValidEdition(sectionCharacters: 100);
    exactThreshold.Items[19].BriefSections[2].Body = Han(75);
    Check(policy.IsQualified(exactThreshold), "正文总计恰好 275 个汉字时通过");
    exactThreshold.Items[19].BriefSections[2].Body = Han(74);
    Check(!policy.IsQualified(exactThreshold), "正文总计 274 个汉字时拒绝");

    var weakCoverage = ValidEdition();
    foreach (var item in weakCoverage.Items)
    {
        item.Topics = ["重要研究"];
    }
    Check(!policy.IsQualified(weakCoverage), "大模型与 Agent 覆盖不足时整期拒绝");

    var onlyOneBatch = ValidEdition();
    onlyOneBatch.Items = onlyOneBatch.Items.Take(10).ToList();
    Check(!policy.IsQualified(onlyOneBatch), "少于两批的内容池会被拒绝");

    var incompleteBatch = ValidEdition();
    incompleteBatch.Items.Add(ValidEdition(77).Items[0]);
    Check(!policy.IsQualified(incompleteBatch), "不是 10 的整倍数时整期拒绝");

    var weakSecondBatch = ValidEdition();
    foreach (var item in weakSecondBatch.Items.Skip(10))
    {
        item.ContentType = "产业事件";
        item.Topics = ["产业动态"];
    }
    Check(!policy.IsQualified(weakSecondBatch), "任一批缺少核心维度时整期拒绝");

    var thinAgentBatch = ValidEdition();
    var secondAgentItems = thinAgentBatch.Items.Skip(10)
        .Where(item => item.Topics.Contains("Agent"))
        .Skip(1);
    foreach (var item in secondAgentItems)
    {
        item.Topics = ["重要研究"];
    }
    Check(!policy.IsQualified(thinAgentBatch), "任一批只有一条 Agent 时会被拒绝");

    var repoFirstTitle = ValidEdition();
    repoFirstTitle.Items[0].Title = "owner/repo v3.2 发布新的智能分析能力";
    Check(!policy.IsQualified(repoFirstTitle), "仓库名开头且未先给中文结论的标题会被拒绝");

    var cachePath = Path.Combine(testRoot, "qualified", "news.json");
    var concurrentWrites = Enumerable.Range(0, 24).Select(index =>
    {
        // Separate instances prove serialization is keyed by destination path,
        // not merely by one service object.
        var store = new QualifiedEditionStore(cachePath, policy);
        return store.SaveAsync(ValidEdition(index + 1));
    });
    var writeResults = await Task.WhenAll(concurrentWrites);
    Check(writeResults.All(result => result), "同一路径的并发保存全部完成且不互相破坏");

    using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)))
    {
        Check(document.RootElement.ValueKind == JsonValueKind.Object,
            "并发保存后的缓存是完整 JSON 对象");
    }

    var readStore = new QualifiedEditionStore(cachePath, policy);
    var loaded = await readStore.LoadAsync();
    Check(policy.IsQualified(loaded), "并发保存后的缓存仍能按生产规则完整加载");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(cachePath)!, "*.tmp").Any(),
        "唯一同目录临时文件在提交后全部清理");

    var invalidSave = ValidEdition();
    invalidSave.SchemaVersion = 1;
    Check(!await readStore.SaveAsync(invalidSave), "存储层拒绝覆盖缓存的非合格版本");
    Check(policy.IsQualified(await readStore.LoadAsync()), "失败保存不影响随后读取最后合格版本");

    var parentFile = Path.Combine(testRoot, "not-a-directory");
    await File.WriteAllTextAsync(parentFile, "占位");
    var failingStore = new QualifiedEditionStore(Path.Combine(parentFile, "news.json"), policy);
    Check(!await failingStore.SaveAsync(ValidEdition(99)), "文件系统保存失败返回 false 而不抛出");
    Check(await failingStore.LoadAsync() is null, "保存失败后的 Load 调用方仍安全得到 null");

    var atomicPath = Path.Combine(testRoot, "atomic", "state.json");
    var atomicStore = new AtomicJsonStore<Dictionary<string, int>>(atomicPath);
    Check(await atomicStore.SaveAsync(new() { ["value"] = 7 }), "通用 JSON 存储可以原子保存");
    var atomicValue = await atomicStore.LoadAsync();
    Check(atomicValue is not null && atomicValue.GetValueOrDefault("value") == 7,
        "通用 JSON 存储可以读回完整数据");
    await File.WriteAllTextAsync(atomicPath, "{损坏的json");
    Check(await atomicStore.LoadAsync() is null, "损坏的偏好 JSON 会安全回退而不使应用崩溃");

    var healthyCloud = new CloudFeedHealth(true, true, true, ValidEdition(), "ok");
    var unavailableCloud = new CloudFeedHealth(false, false, false, null, "offline");
    var staleCloud = new CloudFeedHealth(true, true, false, ValidEdition(), "stale");
    Check(DailyNewsUpdatePolicy.Decide(NewsUpdateMode.CloudPreferred, true, 1, healthyCloud) == DailyNewsUpdateRoute.UseCloud,
        "云端资讯新鲜时不会消耗本机 Codex 额度");
    Check(DailyNewsUpdatePolicy.Decide(NewsUpdateMode.CloudPreferred, true, 1, unavailableCloud) == DailyNewsUpdateRoute.UseLocalCodex,
        "云端无法连接时立即由本机 Codex 接管");
    Check(DailyNewsUpdatePolicy.Decide(NewsUpdateMode.CloudPreferred, true, 1, staleCloud) == DailyNewsUpdateRoute.WaitForCloud,
        "云端首次过期时先等待下一次确认");
    Check(DailyNewsUpdatePolicy.Decide(NewsUpdateMode.CloudPreferred, true, 2, staleCloud) == DailyNewsUpdateRoute.UseLocalCodex,
        "云端连续过期两次后由本机 Codex 接管");
    Check(DailyNewsUpdatePolicy.Decide(NewsUpdateMode.LocalCodexOnly, true, 0, healthyCloud) == DailyNewsUpdateRoute.UseLocalCodex,
        "个人模式不依赖云端成品并直接使用本机 Codex");

    var timeoutProcess = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
        Arguments = "/d /c ping -n 6 127.0.0.1 >nul",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    var timeoutResult = await BoundedProcessRunner.RunAsync(timeoutProcess, TimeSpan.FromMilliseconds(200));
    Check(timeoutResult.Started && timeoutResult.TimedOut, "Codex 辅助进程超时后会被终止");
}
finally
{
    try
    {
        Directory.Delete(testRoot, recursive: true);
    }
    catch
    {
        // Test cleanup is not part of the production persistence contract.
    }
}

Console.WriteLine($"\nRESULT pass={passes.Count} fail={failures.Count}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
