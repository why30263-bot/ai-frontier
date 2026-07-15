using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontier.Services;

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/why30263-bot/ai-frontier/releases/latest";
    private const string LatestReleasePage = "https://github.com/why30263-bot/ai-frontier/releases/latest";
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan MetadataAttemptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly HttpClient MetadataHttp = CreateClient();
    private static readonly HttpClient DownloadHttp = CreateClient();
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
            var release = await GetReleaseWithRetryAsync();
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
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            return new(false, current, null, "暂时无法连接更新服务器，稍后会自动重试；也可以在浏览器中下载最新版", false, preferences.AutoUpdate, LatestReleasePage, string.Empty, string.Empty);
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
            await DownloadFileWithRetryAsync(update.InstallerUrl, installerPath);
            var expectedHash = (await GetStringWithRetryAsync(update.ChecksumUrl))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            await using var installer = File.OpenRead(installerPath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(installer));
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
        catch (Exception exception) when (IsTransientNetworkFailure(exception))
        {
            return $"更新包下载暂时失败：网络连接不稳定。稍后可再次点击检查更新，当前版本不受影响";
        }
        catch
        {
            return "自动更新失败；可以在浏览器中下载最新版，当前版本不受影响";
        }
    }

    private static async Task<GitHubRelease?> GetReleaseWithRetryAsync()
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(MetadataAttemptTimeout);
                using var response = await MetadataHttp.GetAsync(
                    LatestReleaseApi,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
                return await JsonSerializer.DeserializeAsync<GitHubRelease>(content, JsonOptions, timeout.Token);
            }
            catch (Exception exception) when (IsTransientNetworkFailure(exception) && attempt < MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }

        throw new HttpRequestException("更新服务器连续三次连接失败");
    }

    private static async Task<string> GetStringWithRetryAsync(string url)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(MetadataAttemptTimeout);
                return await MetadataHttp.GetStringAsync(url, timeout.Token);
            }
            catch (Exception exception) when (IsTransientNetworkFailure(exception) && attempt < MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }

        throw new HttpRequestException("校验文件连续三次下载失败");
    }

    private static async Task DownloadFileWithRetryAsync(string url, string destinationPath)
    {
        var temporaryPath = destinationPath + ".download";
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                HttpResponseMessage response;
                using (var headersTimeout = new CancellationTokenSource(MetadataAttemptTimeout))
                {
                    response = await DownloadHttp.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        headersTimeout.Token);
                }
                using (response)
                {
                response.EnsureSuccessStatusCode();

                using var downloadTimeout = new CancellationTokenSource(DownloadTimeout);
                await using var source = await response.Content.ReadAsStreamAsync(downloadTimeout.Token);
                await using var target = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, downloadTimeout.Token);
                await target.FlushAsync(downloadTimeout.Token);
                File.Move(temporaryPath, destinationPath, true);
                return;
                }
            }
            catch (Exception exception) when (IsTransientNetworkFailure(exception))
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
                if (attempt >= MaximumAttempts)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }

        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
        throw new HttpRequestException("安装包连续三次下载失败");
    }

    private static bool IsTransientNetworkFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or IOException;

    private static void LaunchInstaller(string installerPath, bool automatic) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = automatic
                ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
                : "/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
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
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIFrontier-Updater/1.4");
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
