using AIFrontier.Services;

await using var service = new CodexChatService();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

var initialized = await service.InitializeAsync(timeout.Token);
if (!initialized.Success || string.IsNullOrWhiteSpace(initialized.ThreadId))
{
    Console.Error.WriteLine($"INIT_FAIL {initialized.Status}");
    return 1;
}

var chunks = 0;
var first = await service.SendMessageAsync(
    "这是连接测试。请只用中文回答：连接成功。",
    delta =>
    {
        if (!string.IsNullOrEmpty(delta))
        {
            Interlocked.Increment(ref chunks);
        }
    },
    timeout.Token);
if (!first.Success || string.IsNullOrWhiteSpace(first.Response))
{
    Console.Error.WriteLine($"TURN1_FAIL {first.Status}");
    return 2;
}

var originalThread = service.ThreadId;
var second = await service.SendMessageAsync("请只回答：同一会话。", cancellationToken: timeout.Token);
if (!second.Success || service.ThreadId != originalThread)
{
    Console.Error.WriteLine($"TURN2_FAIL {second.Status}");
    return 3;
}

Console.WriteLine($"PASS model={service.ActiveModel} thread={originalThread} chunks={chunks} first={first.Response.Trim()} second={second.Response.Trim()}");
return 0;
