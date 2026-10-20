using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/TangKuku9/Mutter-Bot";
    private const string LatestReleaseApi = "https://api.github.com/repos/TangKuku9/Mutter-Bot/releases/latest";
    private const string LatestReleasePage = RepoUrl + "/releases/latest";
    public const string OfficialManifestUrl = RepoUrl + "/releases/latest/download/update.json";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UpdateSettingsStore _settingsStore;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private ManifestCache? _cache;

    public UpdateService(
        IHttpClientFactory httpClientFactory,
        UpdateSettingsStore settingsStore,
        ILogger<UpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>
    /// 查询最新正式版并计算当前进程是否具备一键更新条件。
    /// 优先读取发布清单（可来自 GitHub 或签名镜像），清单不可用时回退旧的 GitHub API 路径。
    /// </summary>
    public async Task<SelfUpdateCheck> GetStatusAsync(CancellationToken ct = default, string? forceKind = null)
    {
        var currentVersion = GetCurrentVersion();
        var settings = _settingsStore.Get();
        forceKind = NormalizeKind(forceKind);

        var info = new UpdateInfoDto
        {
            CurrentVersion = currentVersion,
            ReleaseUrl = LatestReleasePage,
            CheckedAt = DateTimeOffset.UtcNow,
            Source = settings.Source
        };

        var fetch = await FetchManifestAsync(settings, ct, useCache: true);
        if (fetch.Manifest is not null)
        {
            var (plan, reason) = ApplyManifestInfo(info, fetch, settings, currentVersion, forceKind);
            info.CanSelfUpdate = plan is not null;
            info.SelfUpdateReason = reason;
            return new SelfUpdateCheck(info, plan);
        }

        if (settings.Source == UpdateSettings.SourceGitHub)
        {
            // 兼容尚未上传 update.json 的旧版本，以及 GitHub API 可用但清单暂时缺失的情况。
            var (release, error) = await GetReleaseAsync(ct);
            if (release is not null)
            {
                var (plan, reason) = ApplyLegacyRelease(info, release, currentVersion);
                info.CanSelfUpdate = plan is not null;
                info.SelfUpdateReason = reason;
                info.SignatureStatus = "legacy";
                if (fetch.Error is not null)
                    _logger.LogInformation("更新清单不可用，已回退 GitHub API：{Error}", fetch.Error);
                return new SelfUpdateCheck(info, plan);
            }

            info.Error = fetch.Error ?? error;
            info.SignatureStatus = fetch.SignatureStatus;
            return new SelfUpdateCheck(info, null);
        }

        info.Error = fetch.Error;
        info.SignatureStatus = fetch.SignatureStatus;
        info.ManifestSigned = fetch.Signed;
        return new SelfUpdateCheck(info, null);
    }

    /// <summary>面板“测试更新源”使用：只取清单并校验签名，不下载更新包。</summary>
    public async Task<UpdateSourceTestDto> TestSourceAsync(UpdateSettings settings, CancellationToken ct)
    {
        var normalized = (settings ?? new UpdateSettings()).Clone();
        normalized.Normalize();
        if (!normalized.Validate(out var configError))
            return new UpdateSourceTestDto { Error = configError };

        var started = Stopwatch.StartNew();
        var fetch = await FetchManifestAsync(normalized, ct, useCache: false);
        started.Stop();

        var result = new UpdateSourceTestDto
        {
            Reachable = fetch.Manifest is not null,
            Error = fetch.Error,
            Version = fetch.Manifest?.Version,
            LatencyMs = (int)Math.Min(int.MaxValue, started.ElapsedMilliseconds),
            SignatureStatus = fetch.SignatureStatus,
            AssetCount = fetch.Manifest?.Assets.Count ?? 0
        };

        if (fetch.Manifest is not null)
        {
            var (rid, flavor, _) = GetEnvironment();
            var assets = fetch.Manifest.Assets
                .Where(a => string.IsNullOrEmpty(rid) || (a.Rid == rid && a.Flavor == flavor))
                .ToList();
            result.AppAssetSize = assets.Where(a => a.Kind == "app").Select(a => a.Size).DefaultIfEmpty(0).Max();
            result.FullAssetSize = assets.Where(a => a.Kind == "full").Select(a => a.Size).DefaultIfEmpty(0).Max();
        }

        return result;
    }

    private (SelfUpdatePlan? Plan, string? Reason) ApplyManifestInfo(
        UpdateInfoDto info,
        ManifestFetch fetch,
        UpdateSettings settings,
        string currentVersion,
        string? forceKind)
    {
        var manifest = fetch.Manifest!;
        info.LatestVersion = NormalizeVersion(manifest.Version);
        info.ReleaseUrl = string.IsNullOrWhiteSpace(manifest.ReleaseUrl)
            ? LatestReleasePage
            : manifest.ReleaseUrl;
        info.SignatureStatus = fetch.SignatureStatus;
        info.ManifestSigned = fetch.Signed;

        var latest = ParseVersion(info.LatestVersion);
        var running = ParseVersion(currentVersion);
        if (latest is null || running is null)
        {
            info.Error = $"无法解析版本号：{info.LatestVersion}";
            return (null, null);
        }

        info.HasUpdate = latest > running;

        var (rid, flavor, envReason) = GetEnvironment();
        var assets = string.IsNullOrEmpty(rid)
            ? new List<UpdateAsset>()
            : manifest.Assets.Where(a => a.Rid == rid && a.Flavor == flavor).ToList();
        var full = assets.FirstOrDefault(a => a.Kind == "full");
        var app = assets.FirstOrDefault(a => a.Kind == "app");

        info.FullAssetName = full?.Name;
        info.FullAssetSize = full?.Size ?? 0;
        info.AppAssetName = app?.Name;
        info.AppAssetSize = app?.Size ?? 0;

        var appReason = string.Empty;
        var appCompatible = app is not null &&
                            IsAppRuntimeCompatible(app.RuntimeVersion, flavor, out appReason);
        info.RecommendedKind = appCompatible ? "app" : "full";
        info.RuntimeChanged = app is not null && !appCompatible;

        if (!info.HasUpdate)
            return (null, null);

        if (settings.Mode == UpdateSettings.ModeCheck)
            return (null, "更新方式为“仅检查”，未启用一键更新");

        if (envReason is not null)
            return (null, envReason);

        if (full is null)
            return (null, $"最新版本缺少匹配的完整包（{rid}-{flavor}）");

        var desired = forceKind ??
                      (settings.Mode == UpdateSettings.ModeFull ? "full" : info.RecommendedKind);
        if (desired == "app" && !appCompatible)
        {
            info.RuntimeChanged = true;
            info.RecommendedKind = "full";
            return (null, string.IsNullOrWhiteSpace(appReason)
                ? "应用层包与当前 .NET 运行时版本不匹配，请改用完整包"
                : appReason);
        }

        var asset = desired == "app" ? app : full;
        if (asset is null)
            return (null, "最新版本缺少匹配的更新包");

        if (!TryResolveAssetUrl(fetch.ManifestUrl, asset.Url, out var assetUrl, out var urlError))
            return (null, urlError);

        info.AssetName = asset.Name;
        info.AssetSize = asset.Size;
        info.AssetKind = asset.Kind;

        var plan = new SelfUpdatePlan(
            info.LatestVersion,
            asset.Name,
            assetUrl,
            asset.Size,
            asset.Kind,
            asset.Sha256,
            null,
            asset.RuntimeVersion,
            fetch.SignatureVerified);
        return (plan, null);
    }

    private static (SelfUpdatePlan? Plan, string? Reason) ApplyLegacyRelease(
        UpdateInfoDto info, GitHubRelease release, string currentVersion)
    {
        info.LatestVersion = NormalizeVersion(release.TagName);
        info.ReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? LatestReleasePage : release.HtmlUrl;
        info.Source = UpdateSettings.SourceGitHub;
        info.RecommendedKind = "full";
        info.AssetKind = "full";

        var latest = ParseVersion(info.LatestVersion);
        var running = ParseVersion(currentVersion);
        if (latest is null || running is null)
        {
            info.Error = $"无法解析版本号：{info.LatestVersion}";
            return (null, null);
        }

        info.HasUpdate = latest > running;
        var (plan, reason) = BuildLegacyPlan(release, info.LatestVersion);
        info.FullAssetName = plan?.AssetName;
        info.FullAssetSize = plan?.AssetSize ?? 0;
        info.RecommendedKind = "full";
        info.RuntimeChanged = false;

        if (plan is not null)
        {
            info.AssetName = plan.AssetName;
            info.AssetSize = plan.AssetSize;
            info.AssetKind = "full";
        }

        return (info.HasUpdate ? plan : null, info.HasUpdate ? reason : null);
    }

    private async Task<ManifestFetch> FetchManifestAsync(
        UpdateSettings settings, CancellationToken ct, bool useCache)
    {
        var manifestUrl = settings.Source == UpdateSettings.SourceGitHub
            ? OfficialManifestUrl
            : settings.ManifestUrl;

        if (useCache && TryGetCached(settings, manifestUrl, out var cached))
            return cached;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (useCache && TryGetCached(settings, manifestUrl, out cached))
                return cached;

            var (bytes, _, error) = await GetBytesAsync(manifestUrl, MaxManifestBytes, ct);
            if (bytes is null)
                return new ManifestFetch(null, error ?? "无法获取更新清单", false, false, "missing", manifestUrl);

            var (sigBytes, sigStatus, _) = await GetBytesAsync(
                manifestUrl + ".sig", MaxSignatureBytes, ct);
            var signed = sigBytes is not null;
            var signatureStatus = "missing";

            if (signed && !string.IsNullOrWhiteSpace(settings.PublicKey))
            {
                var signature = Encoding.UTF8.GetString(sigBytes!);
                if (!UpdateSignature.TryVerify(bytes, signature, settings.PublicKey, out var verifyError))
                    return new ManifestFetch(null, verifyError, false, true, "invalid", manifestUrl);

                signatureStatus = "valid";
            }
            else if (!signed && settings.Source != UpdateSettings.SourceGitHub)
            {
                return new ManifestFetch(
                    null, "更新清单缺少签名文件（update.json.sig）", false, false, "missing", manifestUrl);
            }
            else if (string.IsNullOrWhiteSpace(settings.PublicKey) &&
                     settings.Source != UpdateSettings.SourceGitHub)
            {
                return new ManifestFetch(
                    null,
                    "镜像/自定义更新源必须配置清单签名公钥，否则无法防止镜像篡改更新包",
                    false,
                    signed,
                    "not_configured",
                    manifestUrl);
            }
            else
            {
                // 官方源允许未签名清单（与旧版 GitHub API + SHA256SUMS 行为一致），
                // 但一旦有签名就用配置的公钥严格校验。镜像/自定义源不允许这条降级路径。
                signatureStatus = signed ? "not_configured" : "missing";
                if (!signed && settings.Source == UpdateSettings.SourceGitHub)
                    _logger.LogInformation("官方更新清单未签名，按未签名来源处理");
                if (sigStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _logger.LogInformation("读取官方更新清单签名失败：{Status}", sigStatus);
            }

            var manifest = UpdateManifest.TryParse(bytes, out var parseError);
            if (manifest is null)
                return new ManifestFetch(null, parseError, false, signed, signatureStatus, manifestUrl);

            var fetch = new ManifestFetch(
                manifest,
                null,
                signatureStatus == "valid",
                signed,
                signatureStatus,
                manifestUrl);

            if (useCache)
                _cache = new ManifestCache(
                    settings.Source,
                    manifestUrl,
                    settings.PublicKey,
                    fetch,
                    DateTime.UtcNow);

            return fetch;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private bool TryGetCached(UpdateSettings settings, string manifestUrl, out ManifestFetch fetch)
    {
        var cached = _cache;
        if (cached is not null &&
            DateTime.UtcNow - cached.FetchedAt < CacheDuration &&
            string.Equals(cached.Source, settings.Source, StringComparison.Ordinal) &&
            string.Equals(cached.ManifestUrl, manifestUrl, StringComparison.Ordinal) &&
            string.Equals(cached.PublicKey, settings.PublicKey, StringComparison.Ordinal))
        {
            fetch = cached.Fetch;
            return true;
        }

        fetch = default!;
        return false;
    }

    private async Task<(byte[]? Bytes, HttpStatusCode? Status, string? Error)> GetBytesAsync(
        string url, int maxBytes, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ManifestTimeout);

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"Mutter-Bot/{GetCurrentVersion()}");
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode, $"HTTP {(int)response.StatusCode}");

            if (response.Content.Headers.ContentLength > maxBytes)
                return (null, response.StatusCode, "更新清单响应过大");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token);
                if (read <= 0)
                    break;
                if (buffer.Length + read > maxBytes)
                    return (null, response.StatusCode, "更新清单响应过大");
                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, null, "连接更新源超时");
        }
        catch (HttpRequestException ex)
        {
            return (null, null, $"无法连接更新源：{ex.Message}");
        }
    }

    private async Task<(GitHubRelease? Release, string? Error)> GetReleaseAsync(CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
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

    private static (SelfUpdatePlan? Plan, string? Reason) BuildLegacyPlan(
        GitHubRelease release, string latestVersion)
    {
        var (rid, flavor, envReason) = GetEnvironment();
        if (envReason is not null)
            return (null, envReason);

        var extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
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
            asset.Name,
            asset.BrowserDownloadUrl,
            asset.Size,
            "full",
            null,
            checksum.BrowserDownloadUrl,
            null,
            false);
        return (plan, null);
    }

    private static (string Rid, string Flavor, string? Reason) GetEnvironment()
    {
        if (!IsPublishedRuntime())
            return (string.Empty, string.Empty, "开发运行模式不支持一键更新，请手动更新");

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return (string.Empty, string.Empty, "当前操作系统不支持一键更新");

        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            return (string.Empty, string.Empty, "当前不是 systemd 服务，请手动更新");

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return (string.Empty, string.Empty, "当前架构不支持一键更新");

        var appDir = GetAppDirectory();
        if (!CanWrite(appDir))
            return (string.Empty, string.Empty, "安装目录不可写，请手动更新");

        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        return (rid, DetectFlavor(appDir), null);
    }

    public static bool IsAppRuntimeCompatible(string? manifestRuntime, string flavor, out string reason)
    {
        reason = string.Empty;
        if (string.Equals(flavor, "framework-dependent", StringComparison.Ordinal))
            return true;

        if (string.IsNullOrWhiteSpace(manifestRuntime))
        {
            reason = "应用层包未声明运行时版本";
            return false;
        }

        if (!Version.TryParse(manifestRuntime.Trim().TrimStart('v', 'V'), out var expected))
        {
            reason = $"无法解析应用层包的运行时版本：{manifestRuntime}";
            return false;
        }

        var current = Environment.Version;
        if (expected.Major != current.Major ||
            expected.Minor != current.Minor ||
            expected.Build != current.Build)
        {
            reason = $"应用层包需要 .NET {expected}，本机为 {current}";
            return false;
        }

        return true;
    }

    public static bool TryResolveAssetUrl(
        string manifestUrl, string assetUrl, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "更新清单地址必须是 HTTPS";
            return false;
        }

        if (!Uri.TryCreate(baseUri, assetUrl, out var uri))
        {
            error = $"更新包地址无效：{assetUrl}";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "更新包地址必须是 HTTPS";
            return false;
        }

        resolved = uri.ToString();
        return true;
    }

    private static string? NormalizeKind(string? kind)
    {
        var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value is "app" or "full" ? value : null;
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

    private sealed record ManifestFetch(
        UpdateManifest? Manifest,
        string? Error,
        bool SignatureVerified,
        bool Signed,
        string SignatureStatus,
        string ManifestUrl);

    private sealed record ManifestCache(
        string Source,
        string ManifestUrl,
        string PublicKey,
        ManifestFetch Fetch,
        DateTime FetchedAt);

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
