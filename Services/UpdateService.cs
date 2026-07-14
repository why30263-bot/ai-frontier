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

    private string PreferencesPath => Path.Combine(_updateRoot, "update-preferences.json");

    public async Task<UpdateCheckResult> CheckAsync()
    {
        var preferences = await LoadPreferencesAsync();
        try
        {
            var release = await Http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, JsonOptions);
            var latest = ParseVersion(release?.TagName);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            preferences.LastCheckedAt = DateTimeOffset.Now;
            await SavePreferencesAsync(preferences);

            if (latest is null || latest <= current)
            {
                return new(false, current, latest, "已是最新版本", false, preferences.AutoUpdate, string.Empty, string.Empty, string.Empty);
            }

            var installer = release!.Assets.FirstOrDefault(asset =>
                asset.Name.Equals("AIFrontier-Setup.exe", StringComparison.OrdinalIgnoreCase));
            var checksum = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals("AIFrontier-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase));
            if (installer is null || checksum is null)
            {
                return new(true, current, latest, $"发现 {latest}，但发布文件不完整", false, false, release.HtmlUrl, string.Empty, string.Empty);
            }

            var shouldPrompt = !preferences.NeverPrompt &&
                !preferences.SkippedVersion.Equals(latest.ToString(), StringComparison.OrdinalIgnoreCase) &&
                !preferences.AutoUpdate;
            var status = preferences.AutoUpdate
                ? $"发现 {latest}，已启用自动更新"
                : preferences.NeverPrompt
                    ? $"发现 {latest}；已关闭更新提醒"
                    : preferences.SkippedVersion == latest.ToString()
                        ? $"已跳过版本 {latest}"
                        : $"发现新版本 {latest}";
            return new(
                true,
                current,
                latest,
                status,
                shouldPrompt,
                preferences.AutoUpdate,
                release.HtmlUrl,
                installer.BrowserDownloadUrl,
                checksum.BrowserDownloadUrl);
        }
        catch
        {
            return new(false, Assembly.GetExecutingAssembly().GetName().Version, null, "当前离线，稍后仍会检查官网更新", false, preferences.AutoUpdate, string.Empty, string.Empty, string.Empty);
        }
    }

    public async Task<string> ApplyChoiceAsync(UpdateCheckResult update, UpdateChoice choice)
    {
        var preferences = await LoadPreferencesAsync();
        switch (choice)
        {
            case UpdateChoice.SkipVersion:
                preferences.SkippedVersion = update.LatestVersion?.ToString() ?? string.Empty;
                await SavePreferencesAsync(preferences);
                return $"已跳过版本 {update.LatestVersion}；下个新版本仍会询问";
            case UpdateChoice.NeverPrompt:
                preferences.NeverPrompt = true;
                preferences.AutoUpdate = false;
                await SavePreferencesAsync(preferences);
                return "已关闭更新弹窗；应用仍会定时检查，可在偏好页恢复提醒";
            case UpdateChoice.EnableAutoUpdate:
                preferences.AutoUpdate = true;
                preferences.NeverPrompt = false;
                preferences.SkippedVersion = string.Empty;
                await SavePreferencesAsync(preferences);
                return await DownloadVerifyAndInstallAsync(update, true);
            case UpdateChoice.InstallNow:
                return await DownloadVerifyAndInstallAsync(update, false);
            default:
                return "稍后再提醒";
        }
    }

    public async Task<string> ResetPromptPreferencesAsync()
    {
        var preferences = await LoadPreferencesAsync();
        preferences.NeverPrompt = false;
        preferences.SkippedVersion = string.Empty;
        await SavePreferencesAsync(preferences);
        return preferences.AutoUpdate ? "已恢复提醒；自动更新仍开启" : "已恢复新版本提醒";
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
            LaunchInstaller(pending.InstallerPath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> DownloadVerifyAndInstallAsync(UpdateCheckResult update, bool automatic)
    {
        if (!update.IsAvailable || update.LatestVersion is null ||
            string.IsNullOrWhiteSpace(update.InstallerUrl) || string.IsNullOrWhiteSpace(update.ChecksumUrl))
        {
            return "没有可安装的新版本";
        }

        try
        {
            Directory.CreateDirectory(_updateRoot);
            var installerPath = Path.Combine(_updateRoot, $"AIFrontier-Setup-{update.LatestVersion}.exe");
            var bytes = await Http.GetByteArrayAsync(update.InstallerUrl);
            await File.WriteAllBytesAsync(installerPath, bytes);
            var expectedHash = (await Http.GetStringAsync(update.ChecksumUrl))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.IsNullOrWhiteSpace(expectedHash) || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installerPath);
                return $"版本 {update.LatestVersion} 下载完成，但安全校验失败，未执行安装";
            }

            LaunchInstaller(installerPath, automatic);
            return automatic
                ? $"已启用自动更新，正在安装 {update.LatestVersion}"
                : $"正在安装 {update.LatestVersion}";
        }
        catch (Exception exception)
        {
            return $"更新失败：{exception.Message}";
        }
    }

    private static void LaunchInstaller(string installerPath, bool automatic) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = automatic
                ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
                : "/SILENT /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });

    private async Task<UpdatePreferences> LoadPreferencesAsync()
    {
        Directory.CreateDirectory(_updateRoot);
        if (!File.Exists(PreferencesPath))
        {
            return new UpdatePreferences();
        }
        try
        {
            await using var stream = File.OpenRead(PreferencesPath);
            return await JsonSerializer.DeserializeAsync<UpdatePreferences>(stream, JsonOptions) ?? new UpdatePreferences();
        }
        catch
        {
            return new UpdatePreferences();
        }
    }

    private async Task SavePreferencesAsync(UpdatePreferences preferences)
    {
        Directory.CreateDirectory(_updateRoot);
        await File.WriteAllTextAsync(PreferencesPath, JsonSerializer.Serialize(preferences, JsonOptions));
    }

    private static Version? ParseVersion(string? value) =>
        Version.TryParse(value?.Trim().TrimStart('v', 'V'), out var version) ? version : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier-Updater/1.1");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    private sealed class UpdatePreferences
    {
        public bool AutoUpdate { get; set; }
        public bool NeverPrompt { get; set; }
        public string SkippedVersion { get; set; } = string.Empty;
        public DateTimeOffset? LastCheckedAt { get; set; }
    }

    private sealed record PendingUpdate(string Version, string InstallerPath);
}

public sealed record UpdateCheckResult(
    bool IsAvailable,
    Version? CurrentVersion,
    Version? LatestVersion,
    string Status,
    bool ShouldPrompt,
    bool AutoUpdateEnabled,
    string ReleaseUrl,
    string InstallerUrl,
    string ChecksumUrl);

public enum UpdateChoice
{
    Later,
    InstallNow,
    EnableAutoUpdate,
    SkipVersion,
    NeverPrompt
}
