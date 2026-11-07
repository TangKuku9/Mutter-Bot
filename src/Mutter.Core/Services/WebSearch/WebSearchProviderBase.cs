using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// 通用 HTTP 基座：超时钳制、响应体 1MB 上限、状态码归类。
/// 各 Provider 只写「拼请求 + 解析 JSON」这两步，其余稳定性约束全部在这里收敛；
/// Key 永远不进日志。
/// </summary>
public abstract class WebSearchProviderBase(HttpClient httpClient, ILogger logger)
{
    /// <summary>响应体读取上限：搜索结果只是文本，1MB 足够，还能挡住异常响应。</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    protected HttpClient HttpClient { get; } = httpClient;
    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// 发请求并读回 JSON；非 2xx 归类为失败。HttpClient.Timeout 只作兜底，
    /// 这里再叠一层按配置钳制的 per-request 超时，保证面板改超时立即生效。
    /// </summary>
    protected async Task<JsonDocument> SendAsync(
        HttpRequestMessage request, ExternalSearchConfig config, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1,
            ExternalSearchConfig.CeilingTimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchRequestException(WebSearchFailureKind.Transient, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new WebSearchRequestException(WebSearchFailureKind.Transient, null, ex.Message);
        }

        using var _ = response;
        var status = (int)response.StatusCode;
        // 先看 Content-Length，再流式限读；不能把异常大响应整串读进内存后才判断。
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, status, "response too large");
        var raw = await ReadCappedAsync(response.Content, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var reason = response.StatusCode == HttpStatusCode.Forbidden
                         && raw.Contains("JSON", StringComparison.OrdinalIgnoreCase)
                ? "SearXNG 实例未开启 JSON 输出（settings.yml 的 search.formats 需含 json）"
                : $"auth rejected {status}";
            throw new WebSearchRequestException(WebSearchFailureKind.Auth, status, reason);
        }

        if (!response.IsSuccessStatusCode)
            throw new WebSearchRequestException(WebSearchFailureKind.Transient, status,
                $"http {status}");

        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, ex.Message);
        }
    }

    /// <summary>流式读取响应体，超过上限立即失败，避免异常响应撑大内存。</summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, "response too large");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>防 SSRF：只放行 http/https。</summary>
    protected static void RequireHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, $"非法地址 {url}");
    }

    /// <summary>从 JSON 对象里按顺序找第一个非空字符串字段（兼容不同版本的字段命名）。</summary>
    protected static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }
}

/// <summary>Provider 抛出的归类失败。</summary>
public sealed class WebSearchRequestException(WebSearchFailureKind kind, int? httpStatus, string reason)
    : Exception(reason)
{
    public WebSearchFailureKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
}
