using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/TangKuku9/Mutter-Bot";
    private const string LatestReleaseApi = "https://api.github.com/repos/TangKuku9/Mutter-Bot/releases/latest";
    private const string LatestReleasePage = RepoUrl + "/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private ReleaseCache? _cache;

    public UpdateService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// 查询 GitHub 最新正式版并计算当前进程是否具备一键更新条件。
    /// 只认已发布、非预发布的 Release，避免把 main 上未发版的提交当成可安装版本。
    /// </summary>
    public async Task<SelfUpdateCheck> GetStatusAsync(CancellationToken ct)
    {
        var currentVersion = GetCurrentVersion();
        var info = new UpdateInfoDto
        {
            CurrentVersion = currentVersion,
            ReleaseUrl = LatestReleasePage,
            CheckedAt = DateTimeOffset.UtcNow
        };

        var (release, error) = await GetReleaseAsync(ct);
        if (release is null)
        {
            info.Error = error;
            return new SelfUpdateCheck(info, null);
        }

        info.LatestVersion = NormalizeVersion(release.TagName);
        info.ReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? LatestReleasePage : release.HtmlUrl;

        var latest = ParseVersion(info.LatestVersion);
        var running = ParseVersion(currentVersion);
        if (latest is null || running is null)
        {
            info.Error = $"无法解析版本号：{info.LatestVersion}";
            return new SelfUpdateCheck(info, null);
        }

        info.HasUpdate = latest > running;
        var (plan, reason) = BuildSelfUpdatePlan(release, info.LatestVersion);
        info.CanSelfUpdate = plan is not null;
        info.SelfUpdateReason = reason;
        info.AssetName = plan?.AssetName;
        info.AssetSize = plan?.AssetSize ?? 0;

        return new SelfUpdateCheck(info, plan);
    }

    private async Task<(GitHubRelease? Release, string? Error)> GetReleaseAsync(CancellationToken ct)
    {
        var cached = _cache;
        if (cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
            return (cached.Release, null);

        await _fetchLock.WaitAsync(ct);
        try
        {
            cached = _cache;
            if (cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
                return (cached.Release, null);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                request.Headers.UserAgent.ParseAdd($"Mutter-Bot/{GetCurrentVersion()}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var response = await client.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var message = response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => "GitHub API 限流，请稍后再试",
                        HttpStatusCode.NotFound => "未找到正式 Release",
                        _ => $"GitHub API 返回 {(int)response.StatusCode}"
                    };
                    return (null, message);
                }

                var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(timeout.Token);
                if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                    return (null, "GitHub 返回的版本信息不完整");

                _cache = new ReleaseCache(release, DateTime.UtcNow);
                return (release, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "连接 GitHub 超时");
            }
            catch (HttpRequestException)
            {
                return (null, "无法连接 GitHub");
            }
            catch (JsonException)
            {
                return (null, "GitHub 响应格式异常");
            }
            catch (NotSupportedException)
            {
                return (null, "GitHub 响应格式异常");
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private static (SelfUpdatePlan? Plan, string? Reason) BuildSelfUpdatePlan(GitHubRelease release, string latestVersion)
    {
        if (!IsPublishedRuntime())
            return (null, "开发运行模式不支持一键更新，请手动更新");

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return (null, "当前操作系统不支持一键更新");

        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            return (null, "当前不是 systemd 服务，请手动更新");

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return (null, "当前架构不支持一键更新");

        var appDir = GetAppDirectory();
        if (!CanWrite(appDir))
            return (null, "安装目录不可写，请手动更新");

        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        var flavor = DetectFlavor(appDir);
        var assetName = $"mutter-bot-v{latestVersion}-{rid}-{flavor}.{extension}";
        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.Ordinal));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return (null, $"最新版本缺少匹配的更新包：{assetName}");

        var checksum = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.Ordinal));
        if (checksum is null || string.IsNullOrWhiteSpace(checksum.BrowserDownloadUrl))
            return (null, "最新版本缺少 SHA256SUMS.txt，无法安全更新");

        var plan = new SelfUpdatePlan(
            latestVersion,
            assetName,
            asset.BrowserDownloadUrl,
            asset.Size,
            checksum.BrowserDownloadUrl);
        return (plan, null);
    }

    private static string GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V');

    private static Version? ParseVersion(string value)
    {
        var core = NormalizeVersion(value).Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private static string GetAppDirectory() =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsPublishedRuntime()
    {
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        return string.Equals(processName, "mutter-bot", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(GetAppDirectory(), "install.json"));
    }

    private static string DetectFlavor(string appDir)
    {
        var hasHostFxr =
            File.Exists(Path.Combine(appDir, "hostfxr.dll")) ||
            File.Exists(Path.Combine(appDir, "libhostfxr.so"));
        return hasHostFxr ? "self-contained" : "framework-dependent";
    }

    private static bool CanWrite(string appDir)
    {
        var probe = Path.Combine(appDir, $".update-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    private sealed record ReleaseCache(GitHubRelease Release, DateTime FetchedAt);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed record SelfUpdateCheck(UpdateInfoDto Info, SelfUpdatePlan? Plan);
