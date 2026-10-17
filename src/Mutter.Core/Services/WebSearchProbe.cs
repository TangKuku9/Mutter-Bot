using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mutter.Core.Services;

/// <summary>一次探测的判定结果。只有拿到正向证据才说"已验证"，其余宁可报"无证据"。</summary>
public record WebSearchProbeVerdict(
    string Status,
    string? GroundingField = null,
    string? Note = null,
    int? PromptTokensDelta = null);

/// <summary>
/// 联网搜索探测的核心逻辑：对给定形状构造请求体、解析响应证据。
/// 抽成静态纯函数是为了能被 ProactiveCheck 直接断言，不用起真实 HTTP。
/// </summary>
public static class WebSearchProbe
{
    /// <summary>探测用最小 prompt：短到输出只可能来自模型本身，token 差值只可能来自注入的搜索结果。</summary>
    public const string ProbeQuestion = "现在北京时间几点？直接回答时间，不要解释。";

    /// <summary>
    /// 已知携带"确实检索过"证据的响应字段（递归匹配）。各家叫法不同，命中任一即视为已验证。
    /// </summary>
    public static readonly string[] GroundingFieldNames =
    [
        "groundingMetadata", "grounding_metadata", "webSearchQueries", "web_search_queries",
        "search_result", "searchResults", "search_results", "search_info", "searchInfo",
        "references", "annotations", "web_search", "citations"
    ];

    /// <summary>
    /// 探测的请求形状。矩阵逐个全跑，不命中即停：中转站可能同时接受多种形状但只有一种真生效。
    /// </summary>
    public static readonly (string Shape, string Description)[] Shapes =
    [
        ("enable_search", "顶层 enable_search:true（通义/百炼）"),
        ("web_search_tool", "tools 声明 web_search（智谱 GLM）"),
        ("google_search_tool", "tools 声明 google_search（Gemini 风格）"),
        ("web_search_options", "顶层 web_search_options:{}（OpenAI Responses 风格）"),
        ("builtin_web_search", "tools 声明 builtin_function google_search（Gemini 兼容层）")
    ];

    /// <summary>按形状构造 OpenAI 兼容请求体；不做任何网络操作。</summary>
    public static Dictionary<string, object> BuildRequestBody(string shape, string model, string question)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 64,
            ["messages"] = new List<object> { new Dictionary<string, object> { ["role"] = "user", ["content"] = question } }
        };
        switch (shape)
        {
            case "enable_search":
                body["enable_search"] = true;
                break;
            case "web_search_tool":
                body["tools"] = new List<object> { new Dictionary<string, object> { ["type"] = "web_search", ["web_search"] = new Dictionary<string, object> { ["enable"] = true, ["search_result"] = true } } };
                break;
            case "google_search_tool":
                body["tools"] = new List<object> { new Dictionary<string, object> { ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = "google_search", ["parameters"] = new Dictionary<string, object>() } } };
                break;
            case "web_search_options":
                body["web_search_options"] = new Dictionary<string, object>();
                break;
            case "builtin_web_search":
                body["tools"] = new List<object> { new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() } };
                break;
        }
        return body;
    }

    /// <summary>
    /// 递归扫描响应 JSON 找 grounding 证据字段。返回首个命中的字段名，找不到为 null。
    /// </summary>
    public static string? FindGroundingField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in element.EnumerateObject())
        {
            if (GroundingFieldNames.Any(n => string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase)) &&
                prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? !IsEmptyContainer(prop.Value)
                    : false)
            {
                return prop.Name;
            }
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var hit = FindGroundingField(prop.Value);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    /// <summary>从 usage.prompt_tokens 读请求侧 token；缺失返回 null。</summary>
    public static int? ReadPromptTokens(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("prompt_tokens", out var pt) &&
            pt.ValueKind == JsonValueKind.Number)
        {
            return pt.GetInt32();
        }
        return null;
    }

    /// <summary>把一次探测的原始证据归约为判定。</summary>
    public static WebSearchProbeVerdict Verdict(string shape, int? httpStatus, JsonElement? responseRoot, int? promptTokensDelta)
    {
        if (httpStatus is null)
            return new WebSearchProbeVerdict("unreachable", Note: "请求未得到响应");

        if (httpStatus is 401 or 403)
            return new WebSearchProbeVerdict("auth_failed", Note: "Key 无效或无权限（该结论与搜索无关）");

        if (httpStatus is >= 400 and < 500)
            return new WebSearchProbeVerdict("rejected", Note: $"上游以 {httpStatus} 拒绝了该形状");

        if (httpStatus >= 500)
            return new WebSearchProbeVerdict("upstream_error", Note: $"上游服务端错误 {httpStatus}");

        // 2xx：只有两种可能——真检索了，或被静默忽略
        if (responseRoot is { } root)
        {
            var field = FindGroundingField(root);
            if (field is not null)
                return new WebSearchProbeVerdict("verified", field, "响应携带检索证据字段", promptTokensDelta);

            if (promptTokensDelta is >= 50)
                return new WebSearchProbeVerdict("verified", null,
                    $"无元数据字段，但带搜索比不带多 {promptTokensDelta} 个 prompt token——搜索结果被注入了上下文",
                    promptTokensDelta);

            return new WebSearchProbeVerdict("no_evidence", null,
                promptTokensDelta is int d
                    ? $"2xx 但无检索证据，token 差 {d}——形状被静默忽略的可能性大"
                    : "2xx 但无检索证据，且响应缺 usage 字段，无法做 token 差分",
                promptTokensDelta);
        }

        return new WebSearchProbeVerdict("no_evidence", Note: "2xx 但响应不是可解析的 JSON");
    }

    private static bool IsEmptyContainer(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => !e.EnumerateObject().Any(),
        JsonValueKind.Array => !e.EnumerateArray().Any(),
        _ => true
    };
}

/// <summary>
/// 探测端点的运行时壳：真实发 HTTP。逻辑部分全部委托给 <see cref="WebSearchProbe"/>。
/// </summary>
public class WebSearchProbeRunner(HttpClient httpClient, ILogger<WebSearchProbeRunner> logger)
{
    /// <summary>单形状请求超时；探测不该让用户等太久。</summary>
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(20);

    public async Task<(int? Status, JsonElement? Root, string? Error)> SendOnceAsync(
        string apiBaseUrl, string apiKey, string shape, string model, CancellationToken ct = default)
    {
        var body = WebSearchProbe.BuildRequestBody(shape, model, WebSearchProbe.ProbeQuestion);
        var url = $"{apiBaseUrl.TrimEnd('/')}/chat/completions";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerRequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            JsonElement? root = null;
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); } catch { /* 非 JSON 响应保留 raw */ }
            return ((int)response.StatusCode, root, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Web search probe timed out for {Url} shape={Shape}", url, shape);
            return (null, null, $"请求超时（>{PerRequestTimeout.TotalSeconds:F0}s）");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Web search probe failed for {Url}: {Message}", url, ex.Message);
            return (null, null, ex.Message);
        }
    }
}
