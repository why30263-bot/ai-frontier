using System.Net.Http.Json;
using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

public sealed class CloudFeedHealthService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly EditionQualityPolicy _policy = new();

    public async Task<CloudFeedHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.RemoteNewsUrl))
        {
            return new(false, false, false, null, "未配置云端资讯地址。");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var edition = await Http.GetFromJsonAsync<NewsEdition>(
                configuration.RemoteNewsUrl,
                JsonOptions,
                deadline.Token);
            var qualified = _policy.IsQualified(edition);
            var fresh = qualified && edition!.GeneratedAt != default &&
                DateTimeOffset.Now - edition.GeneratedAt <= TimeSpan.FromHours(Math.Max(6, configuration.StaleAfterHours));
            return new(
                true,
                qualified,
                fresh,
                qualified ? edition : null,
                fresh ? "云端今日资讯已就绪。" : qualified ? "云端资讯已超过更新时限。" : "云端资讯未通过完整性检查。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, false, false, null, "暂时无法连接云端资讯源。");
        }
    }

    public static async Task<FeedConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "source-feeds.json");
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<FeedConfiguration>(stream, JsonOptions, cancellationToken)
                ?? new FeedConfiguration();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new FeedConfiguration();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier/1.3 (+https://github.com/why30263-bot/ai-frontier)");
        return client;
    }
}
