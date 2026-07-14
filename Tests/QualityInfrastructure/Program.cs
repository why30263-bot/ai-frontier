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

string Han(int count) => new('?', count);

NewsEdition ValidEdition(int sequence = 0, int sectionCharacters = 100)
{
    var items = Enumerable.Range(0, 20).Select(index =>
    {
        var slot = index % EditionQualityPolicy.BatchSize;
        var contentType = slot switch
        {
            0 or 1 => "??",
            2 or 3 => "????",
            4 or 5 => "????",
            6 or 7 => "Agent??",
            _ => "????"
        };
        var topic = slot switch
        {
            0 or 2 or 4 => "???",
            1 or 3 or 6 => "Agent",
            5 or 7 => "????",
            8 => "????",
            _ => "????"
        };

        return new NewsItem
        {
            Id = $"edition-{sequence}-item-{index}",
            ContentType = contentType,
            Topics = [topic],
            Title = $"??????{index}",
            Summary = Han(55),
            BriefSections =
            [
                new BriefSection { Title = "????", Body = Han(sectionCharacters) },
                new BriefSection { Title = "????", Body = Han(sectionCharacters) },
                new BriefSection { Title = "????", Body = Han(sectionCharacters) }
            ],
            PublishedAt = now.AddHours(-index).ToString("O"),
            SourceName = "??????",
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
    Check(policy.IsQualified(valid), "schema v2 ????????????");

    var v1 = ValidEdition();
    v1.SchemaVersion = 1;
    Check(!policy.IsQualified(v1), "schema v1 ?????");

    var v3 = ValidEdition();
    v3.SchemaVersion = 3;
    Check(!policy.IsQualified(v3), "?? schema v3 ?????");

    var future = ValidEdition();
    future.GeneratedAt = now.AddMinutes(6);
    Check(!policy.IsQualified(future), "???????????????");

    var malformed = ValidEdition();
    malformed.Items[19].Summary = "????";
    Check(!policy.IsQualified(malformed), "????????????");

    var nullItem = ValidEdition();
    nullItem.Items[19] = null!;
    Check(!policy.IsQualified(nullItem), "JSON ?? null ???????????????");

    var exactThreshold = ValidEdition(sectionCharacters: 100);
    exactThreshold.Items[19].BriefSections[2].Body = Han(75);
    Check(policy.IsQualified(exactThreshold), "?????? 275 ??????");
    exactThreshold.Items[19].BriefSections[2].Body = Han(74);
    Check(!policy.IsQualified(exactThreshold), "???? 274 ??????");

    var weakCoverage = ValidEdition();
    foreach (var item in weakCoverage.Items)
    {
        item.Topics = ["????"];
    }
    Check(!policy.IsQualified(weakCoverage), "???? Agent ?????????");

    var onlyOneBatch = ValidEdition();
    onlyOneBatch.Items = onlyOneBatch.Items.Take(10).ToList();
    Check(!policy.IsQualified(onlyOneBatch), "????????????");

    var incompleteBatch = ValidEdition();
    incompleteBatch.Items.Add(ValidEdition(77).Items[0]);
    Check(!policy.IsQualified(incompleteBatch), "?? 10 ?????????");

    var weakSecondBatch = ValidEdition();
    foreach (var item in weakSecondBatch.Items.Skip(10))
    {
        item.ContentType = "????";
        item.Topics = ["????"];
    }
    Check(!policy.IsQualified(weakSecondBatch), "??????????????");

    var thinAgentBatch = ValidEdition();
    var secondAgentItems = thinAgentBatch.Items.Skip(10)
        .Where(item => item.Topics.Contains("Agent"))
        .Skip(1);
    foreach (var item in secondAgentItems)
    {
        item.Topics = ["????"];
    }
    Check(!policy.IsQualified(thinAgentBatch), "??????? Agent ?????");

    var repoFirstTitle = ValidEdition();
    repoFirstTitle.Items[0].Title = "owner/repo v3.2 ??????????";
    Check(!policy.IsQualified(repoFirstTitle), "????????????????????");

    var cachePath = Path.Combine(testRoot, "qualified", "news.json");
    var concurrentWrites = Enumerable.Range(0, 24).Select(index =>
    {
        // Separate instances prove serialization is keyed by destination path,
        // not merely by one service object.
        var store = new QualifiedEditionStore(cachePath, policy);
        return store.SaveAsync(ValidEdition(index + 1));
    });
    var writeResults = await Task.WhenAll(concurrentWrites);
    Check(writeResults.All(result => result), "???????????????????");

    using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)))
    {
        Check(document.RootElement.ValueKind == JsonValueKind.Object,
            "??????????? JSON ??");
    }

    var readStore = new QualifiedEditionStore(cachePath, policy);
    var loaded = await readStore.LoadAsync();
    Check(policy.IsQualified(loaded), "???????????????????");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(cachePath)!, "*.tmp").Any(),
        "?????????????????");

    var invalidSave = ValidEdition();
    invalidSave.SchemaVersion = 1;
    Check(!await readStore.SaveAsync(invalidSave), "???????????????");
    Check(policy.IsQualified(await readStore.LoadAsync()), "?????????????????");

    var parentFile = Path.Combine(testRoot, "not-a-directory");
    await File.WriteAllTextAsync(parentFile, "??");
    var failingStore = new QualifiedEditionStore(Path.Combine(parentFile, "news.json"), policy);
    Check(!await failingStore.SaveAsync(ValidEdition(99)), "?????????? false ????");
    Check(await failingStore.LoadAsync() is null, "?????? Load ???????? null");

    var atomicPath = Path.Combine(testRoot, "atomic", "state.json");
    var atomicStore = new AtomicJsonStore<Dictionary<string, int>>(atomicPath);
    Check(await atomicStore.SaveAsync(new() { ["value"] = 7 }), "?? JSON ????????");
    var atomicValue = await atomicStore.LoadAsync();
    Check(atomicValue is not null && atomicValue.GetValueOrDefault("value") == 7,
        "?? JSON ??????????");
    await File.WriteAllTextAsync(atomicPath, "{???json");
    Check(await atomicStore.LoadAsync() is null, "????? JSON ????????????");

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
    Check(timeoutResult.Started && timeoutResult.TimedOut, "Codex ???????????");
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
