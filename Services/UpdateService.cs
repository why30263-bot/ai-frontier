using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontier.Services;

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/why30263-bot/ai-frontier/releases/latest";
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _updateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIFrontier",
        "updates");

    public async Task<string> CheckAndStageAsync()
    {
        try
        {
            var release = await Http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, JsonOptions);
            var latest = ParseVersion(release?.TagName);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (latest is null || latest <= current)
            {
                return "已是最新版本";
            }

            var asset = release!.Assets.FirstOrDefault(item =>
                item.Name.Equals("AIFrontier-Setup.exe", StringComparison.OrdinalIgnoreCase));
            var checksumAsset = release.Assets.FirstOrDefault(item =>
                item.Name.Equals("AIFrontier-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase));
            if (asset is null || checksumAsset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
                string.IsNullOrWhiteSpace(checksumAsset.BrowserDownloadUrl))
            {
                return $"发现 {latest}，但发布文件不完整";
            }

            Directory.CreateDirectory(_updateRoot);
            var installerPath = Path.Combine(_updateRoot, $"AIFrontier-Setup-{latest}.exe");
            if (!File.Exists(installerPath))
            {
                var bytes = await Http.GetByteArrayAsync(asset.BrowserDownloadUrl);
                await File.WriteAllBytesAsync(installerPath, bytes);
            }
            var expectedHash = (await Http.GetStringAsync(checksumAsset.BrowserDownloadUrl))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installerPath)));
            if (string.IsNullOrWhiteSpace(expectedHash) || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installerPath);
                return $"发现 {latest}，但安装器校验失败";
            }

            var pending = new PendingUpdate(latest.ToString(), installerPath);
            await File.WriteAllTextAsync(
                Path.Combine(_updateRoot, "pending-update.json"),
                JsonSerializer.Serialize(pending, JsonOptions));
            return $"已下载 {latest}，下次启动自动安装";
        }
        catch
        {
            return "当前离线，稍后自动检查更新";
        }
    }

    public bool TryApplyStagedUpdate()
    {
        var pendingPath = Path.Combine(_updateRoot, "pending-update.json");
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        try
        {
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(pendingPath), JsonOptions);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            var target = ParseVersion(pending?.Version);
            if (pending is null || target is null || target <= current || !File.Exists(pending.InstallerPath))
            {
                File.Delete(pendingPath);
                return false;
            }

            File.Delete(pendingPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = pending.InstallerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Version? ParseVersion(string? value) =>
        Version.TryParse(value?.Trim().TrimStart('v', 'V'), out var version) ? version : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier-Updater/1.0");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    private sealed record PendingUpdate(string Version, string InstallerPath);
}
