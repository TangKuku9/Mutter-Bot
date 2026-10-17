using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mutter.Core.Services;

/// <summary>一次探测的判定结果。只有拿到正向证据才说"已验证"，其余宁可报"无证据"。</summary>
public record WebSearchProbeVerdict(
    string Status,
    string? GroundingField = null,
    string? Note = null,
    int? PromptTokensDelta = null);

/// <summary>一个探测用例：形状 + 所走端点。OpenAI 兼容形状打 /chat/completions，Gemini 形状打原生 :generateContent。</summary>
public sealed record WebSearchProbeCase(string Shape, string Description, string Endpoint);

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
    /// 探测用例矩阵。全量逐个跑、不命中即停：中转站可能同时接受多种形状但只有一种真生效。
    /// OpenAI 兼容形状覆盖常见厂商；gemini_native 单独走原生端点——部分地址（含 Google 官方
    /// /v1beta/openai 兼容层）的联网检索只在原生端点上生效，兼容层会静默丢弃或拒绝。
    /// </summary>
    public static readonly WebSearchProbeCase[] Cases =
    [
        new("enable_search", "OpenAI 兼容：顶层 enable_search:true（通义/百炼）", "openai"),
        new("web_search_tool", "OpenAI 兼容：tools 声明 web_search（智谱 GLM）", "openai"),
        new("google_search_tool", "OpenAI 兼容：tools 声明 function google_search", "openai"),
        new("web_search_options", "OpenAI 兼容：顶层 web_search_options（OpenAI Responses 风格）", "openai"),
        new("builtin_web_search", "OpenAI 兼容：tools 内联 google_search（Gemini 兼容层直传）", "openai"),
        new("gemini_native", "Gemini 原生 :generateContent + tools:[{google_search}]（declarative tool）", "gemini")
    ];

    /// <summary>按 OpenAI 兼容形状构造请求体；未知形状（基线 "__baseline__"）不附任何搜索参数。</summary>
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

    /// <summary>Gemini 原生 :generateContent 请求体；withTool=false 即原生侧基线。</summary>
    public static Dictionary<string, object> BuildGeminiBody(bool withTool, string question)
    {
        var body = new Dictionary<string, object>
        {
            ["contents"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = question } }
                }
            },
            ["generationConfig"] = new Dictionary<string, object> { ["maxOutputTokens"] = 64 }
        };
        if (withTool)
            body["tools"] = new List<object> { new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() } };
        return body;
    }

    /// <summary>
    /// Gemini 原生端点的候选路径，按可能性排序。裸地址先试官方 /v1beta 再试直拼；
    /// 地址挂在 Google 官方 OpenAI 兼容层（/v1beta/openai）或各家兼容后缀下时，剥离后从上一级找原生端点。
    /// </summary>
    public static string[] GeminiUrlCandidates(string apiBaseUrl, string model)
    {
        var modelSeg = Uri.EscapeDataString(model);
        void Collect(string root, List<string> into)
        {
            var r = root.TrimEnd('/');
            if (r.EndsWith("/v1beta") || r.EndsWith("/v1alpha") || r.EndsWith("/v1"))
                into.Add($"{r}/models/{modelSeg}:generateContent");
            else
            {
                into.Add($"{r}/v1beta/models/{modelSeg}:generateContent");
                into.Add($"{r}/models/{modelSeg}:generateContent");
            }
        }

        var b = apiBaseUrl.TrimEnd('/');
        var list = new List<string>();
        var stem = Regex.Replace(b, "/(openai|compatible-mode|compat)$", "");
        if (stem != b)
        {
            // 已知挂在兼容层下：原生端点只可能在剥离后的路径上，不浪费候选去探兼容层自身
            Collect(stem, list);
        }
        else
        {
            Collect(b, list);
        }
        return list.Distinct().ToArray();
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

    /// <summary>读请求侧 token：OpenAI 兼容是 usage.prompt_tokens，Gemini 原生是 usageMetadata.promptTokenCount。</summary>
    public static int? ReadPromptTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
            return pt.GetInt32();
        if (root.TryGetProperty("usageMetadata", out var um) && um.ValueKind == JsonValueKind.Object &&
            um.TryGetProperty("promptTokenCount", out var pc) && pc.ValueKind == JsonValueKind.Number)
            return pc.GetInt32();
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

/// <summary>单次探测请求的结果。</summary>
public sealed record ProbeSendResult(int? Status, JsonElement? Root, string? Error);

/// <summary>
/// 探测端点的运行时壳：真实发 HTTP。逻辑部分全部委托给 <see cref="WebSearchProbe"/>。
/// </summary>
public class WebSearchProbeRunner(HttpClient httpClient, ILogger<WebSearchProbeRunner> logger)
{
    /// <summary>单形状请求超时；探测不该让用户等太久。</summary>
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>shape 为 null 表示该端点的基线请求（不带任何搜索参数）。</summary>
    public async Task<ProbeSendResult> SendAsync(
        string apiBaseUrl, string apiKey, string endpoint, string? shape, string model, CancellationToken ct = default)
    {
        return endpoint == "gemini"
            ? await SendGeminiAsync(apiBaseUrl, apiKey, withTool: shape is not null, model, ct)
            : await SendOpenAiAsync(apiBaseUrl, apiKey, shape ?? "__baseline__", model, ct);
    }

    private async Task<ProbeSendResult> SendOpenAiAsync(
        string apiBaseUrl, string apiKey, string shape, string model, CancellationToken ct)
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
            try { root = JsonDocument.Parse(raw).RootElement.Clone(); } catch { /* 非 JSON 响应保留状态码 */ }
            return new ProbeSendResult((int)response.StatusCode, root, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Web search probe timed out for {Url} shape={Shape}", url, shape);
            return new ProbeSendResult(null, null, $"请求超时（>{PerRequestTimeout.TotalSeconds:F0}s）");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Web search probe failed for {Url}: {Message}", url, ex.Message);
            return new ProbeSendResult(null, null, ex.Message);
        }
    }

    private async Task<ProbeSendResult> SendGeminiAsync(
        string apiBaseUrl, string apiKey, bool withTool, string model, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(WebSearchProbe.BuildGeminiBody(withTool, WebSearchProbe.ProbeQuestion));
        string? lastPath = null;
        foreach (var url in WebSearchProbe.GeminiUrlCandidates(apiBaseUrl, model))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PerRequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-goog-api-key", apiKey);
                // Google 官方 Key 认 x-goog-api-key；中转通常认 Bearer。Key 不是 AIza 开头时两个都带，无害。
                if (!apiKey.StartsWith("AIza"))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.SendAsync(request, cts.Token);
                var raw = await response.Content.ReadAsStringAsync(cts.Token);
                JsonElement? root = null;
                try { root = JsonDocument.Parse(raw).RootElement.Clone(); } catch { }
                if ((int)response.StatusCode == 404)
                {
                    lastPath = url;
                    continue; // 该路径没有原生端点，试下一个候选
                }
                return new ProbeSendResult((int)response.StatusCode, root, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Web search probe timed out for {Url}", url);
                return new ProbeSendResult(null, null, $"请求超时（>{PerRequestTimeout.TotalSeconds:F0}s）");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Web search probe failed for {Url}: {Message}", url, ex.Message);
                return new ProbeSendResult(null, null, ex.Message);
            }
        }
        // 全部候选 404：以 404 状态回传，让上层报"无原生端点"而不是笼统的拒绝
        return new ProbeSendResult(404, null, $"未找到 Gemini 原生端点（最后尝试 {lastPath}）");
    }
}
