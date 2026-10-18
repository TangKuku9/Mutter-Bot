using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UpdateController : ControllerBase
{
    private const string LatestReleaseApi = "https://api.github.com/repos/TangKuku9/Mutter-Bot/releases/latest";
    private const string LatestReleasePage = "https://github.com/TangKuku9/Mutter-Bot/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim FetchLock = new(1, 1);
    private static CachedUpdate? _cache;

    private readonly IHttpClientFactory _httpClientFactory;

    public UpdateController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// GitHub 的 /releases/latest 只返回已发布、非预发布的正式版，对应 main 的稳定发布基线。
    /// 结果缓存 10 分钟，避免面板自动检查触发未认证 API 的限流。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<UpdateInfoDto>> GetStatus(CancellationToken ct)
    {
        var cached = _cache;
        if (cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
            return Ok(cached.Info);

        await FetchLock.WaitAsync(ct);
        try
        {
            cached = _cache;
            if (cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
                return Ok(cached.Info);

            var info = await FetchLatestAsync(ct);
            if (info.Error is null)
                _cache = new CachedUpdate(info, DateTime.UtcNow);

            return Ok(info);
        }
        finally
        {
            FetchLock.Release();
        }
    }

    private async Task<UpdateInfoDto> FetchLatestAsync(CancellationToken ct)
    {
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        var info = new UpdateInfoDto
        {
            CurrentVersion = currentVersion,
            ReleaseUrl = LatestReleasePage,
            CheckedAt = DateTimeOffset.UtcNow
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd($"Mutter-Bot/{currentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                info.Error = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "GitHub API 限流，请稍后再试",
                    HttpStatusCode.NotFound => "未找到正式 Release",
                    _ => $"GitHub API 返回 {(int)response.StatusCode}"
                };
                return info;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(timeout.Token);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                info.Error = "GitHub 返回的版本信息不完整";
                return info;
            }

            info.LatestVersion = release.TagName.Trim().TrimStart('v', 'V');
            info.ReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? LatestReleasePage : release.HtmlUrl;

            var latest = ParseVersion(info.LatestVersion);
            var running = ParseVersion(currentVersion);
            if (latest is null || running is null)
            {
                info.Error = $"无法解析版本号：{info.LatestVersion}";
                return info;
            }

            info.HasUpdate = latest > running;
            info.CheckedAt = DateTimeOffset.UtcNow;
            return info;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            info.Error = "连接 GitHub 超时";
            return info;
        }
        catch (HttpRequestException)
        {
            info.Error = "无法连接 GitHub";
            return info;
        }
        catch (JsonException)
        {
            info.Error = "GitHub 响应格式异常";
            return info;
        }
        catch (NotSupportedException)
        {
            info.Error = "GitHub 响应格式异常";
            return info;
        }
    }

    private static Version? ParseVersion(string value)
    {
        var core = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private sealed record CachedUpdate(UpdateInfoDto Info, DateTime FetchedAt);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;
    }
}
