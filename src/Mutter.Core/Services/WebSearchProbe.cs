using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>一次探测的判定结果。</summary>
public record WebSearchProbeVerdict(
    string Status,
    string? GroundingField = null,
    string? Note = null,
    string? Evidence = null);

/// <summary>一个探测用例：形状 + 所走端点。OpenAI 兼容形状打 /chat/completions，Gemini 形状打原生 :generateContent。</summary>
public sealed record WebSearchProbeCase(string Shape, string Description, string Endpoint);

/// <summary>
/// 联网搜索请求形状和上游证据判定。探测与正式请求共用形状定义。
/// </summary>
public static class WebSearchProbe
{
    public const string ProbeQuestion =
        "请使用联网搜索查找最近七天发布的一篇科技新闻，简短给出新闻标题并标注来源。检索不可用时写 NO_SEARCH。";
    // 思考模型的输出额度也可能用于推理。
    public const int ProbeMaxOutputTokens = 512;

    public static readonly WebSearchProbeCase[] Cases =
    [
        new("enable_search", "OpenAI 兼容：顶层 enable_search:true（通义/百炼）", "openai"),
        new("web_search_tool", "OpenAI 兼容：tools 声明 web_search（智谱 GLM）", "openai"),
        new("google_search_tool", "OpenAI 兼容：tools 声明 function google_search", "openai"),
        new("web_search_options", "OpenAI 兼容：顶层 web_search_options（搜索模型）", "openai"),
        new("builtin_web_search", "OpenAI 兼容：tools 内联 google_search（Gemini 兼容层直传）", "openai"),
        new("gemini_native", "Gemini 原生 :generateContent + tools:[{google_search}]（declarative tool）", "gemini")
    ];

    /// <summary>已绑定连接只试正式请求会使用的协议。形状提示只影响测试顺序。</summary>
    public static WebSearchProbeCase[] OrderedCases(string apiBaseUrl, string protocol, string templateId, string preferredShape)
    {
        var likelyShape = templateId switch
        {
            "qwen" => "enable_search",
            "zhipu" => "web_search_tool",
            "openai" => "web_search_options",
            _ => AiProviderPreset.InferWebSearchMode(apiBaseUrl)
        };
        if (Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri))
        {
            if (protocol.Length == 0 && uri.Host == "generativelanguage.googleapis.com")
                protocol = "gemini";
            if (uri.Host == "api.openai.com")
                likelyShape = "web_search_options";
        }

        return Cases
            .Where(c => protocol is not ("openai" or "gemini") || c.Endpoint == protocol)
            .OrderBy(c => c.Shape == preferredShape ? 0 : c.Shape == likelyShape ? 1 : 2)
            .ToArray();
    }

    /// <summary>
    /// 把搜索形状写进 OpenAI 兼容请求体。
    /// </summary>
    public static void ApplyShapeToBody(Dictionary<string, object> body, string shape)
    {
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
            if (r.EndsWith("/v1beta") || r.EndsWith("/v1alpha"))
            {
                into.Add($"{r}/models/{modelSeg}:generateContent");
                into.Add($"{Regex.Replace(r, "/(v1beta|v1alpha)$", "/v1")}/models/{modelSeg}:generateContent");
            }
            else if (r.EndsWith("/v1"))
            {
                // 以 /v1 结尾的中转原生端点通常挂在 /v1beta；
                // 也有网关直接挂 /v1 下，两条都试，404 就换下一条。
                into.Add($"{Regex.Replace(r, "/v1$", "/v1beta")}/models/{modelSeg}:generateContent");
                into.Add($"{r}/models/{modelSeg}:generateContent");
            }
            else
            {
                into.Add($"{r}/v1beta/models/{modelSeg}:generateContent");
                into.Add($"{r}/v1/models/{modelSeg}:generateContent");
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
    /// 读出响应里的正文。OpenAI 兼容取 choices[0].message.content，Gemini 原生取
    /// candidates[0].content.parts[].text 并跳过 thought 部分。
    /// </summary>
    public static string ReadResponseText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object ||
                    !choice.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("content", out var content))
                    continue;

                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? string.Empty;

                if (content.ValueKind == JsonValueKind.Array)
                {
                    return ReadMessageContent(content);
                }
            }
        }

        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var cand in candidates.EnumerateArray())
            {
                if (cand.ValueKind != JsonValueKind.Object ||
                    !cand.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Object ||
                    !content.TryGetProperty("parts", out var parts) ||
                    parts.ValueKind != JsonValueKind.Array)
                    continue;

                sb.Append(ReadTextParts(parts, skipThought: true));
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static string ReadMessageContent(JsonElement content) =>
        content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : ReadTextParts(content);

    private static string ReadTextParts(JsonElement parts, bool skipThought = false)
    {
        if (parts.ValueKind != JsonValueKind.Array) return string.Empty;
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (skipThought && Property(part, "thought").ValueKind == JsonValueKind.True) continue;
            text.Append(StringProperty(part, "text"));
        }
        return text.ToString();
    }

    public static WebSearchProbeVerdict Verdict(
        ProbeSendResult response, string endpoint, WebSearchImageChallenge? image = null)
    {
        var failure = response.Status switch
        {
            null when response.TimedOut => new WebSearchProbeVerdict("timeout", Note: response.Error),
            null => new WebSearchProbeVerdict("unreachable", Note: response.Error ?? "请求未得到响应"),
            401 or 403 => new WebSearchProbeVerdict("auth_failed", Note: $"上游拒绝鉴权或访问（HTTP {response.Status}）"),
            402 => new WebSearchProbeVerdict("quota_exceeded", Note: "上游要求付费或补充额度（HTTP 402）"),
            429 => new WebSearchProbeVerdict("rate_limited", Note: "上游限流或额度不足（HTTP 429）"),
            408 or 504 => new WebSearchProbeVerdict("timeout", Note: $"上游超时（HTTP {response.Status}）"),
            404 when endpoint == "gemini" =>
                new WebSearchProbeVerdict("no_native_endpoint", Note: "该地址和模型未找到 Gemini 原生接口（HTTP 404）"),
            >= 500 => new WebSearchProbeVerdict("upstream_error", Note: $"上游服务错误（HTTP {response.Status}）"),
            >= 400 => new WebSearchProbeVerdict("rejected", Note: $"上游拒绝该请求（HTTP {response.Status}）"),
            < 200 or >= 300 =>
                new WebSearchProbeVerdict("invalid_response", Note: $"上游返回了非预期状态（HTTP {response.Status}）"),
            _ => null
        };
        if (failure is not null) return failure;

        var root = response.Root ?? default;
        if (root.ValueKind != JsonValueKind.Object)
            return new("invalid_response", Note: "上游未返回有效的 JSON 对象");
        if (Property(root, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return new("upstream_error", Note: "上游响应中包含错误");

        var isGemini = endpoint == "gemini";
        var reply = FirstItem(Property(root, isGemini ? "candidates" : "choices"));
        if (reply.ValueKind != JsonValueKind.Object)
            return new("invalid_response", Note: "上游响应缺少模型答复");

        var message = Property(reply, "message");
        var field = FindGroundingField(root, reply, message);
        var evidence = field is null ? null : "grounding_metadata";

        if (image is not null)
        {
            var text = isGemini
                ? ReadTextParts(Property(Property(reply, "content"), "parts"), skipThought: true)
                : ReadMessageContent(Property(message, "content"));
            if (!image.Matches(text))
                return new("image_unconfirmed", field, "答复未正确识别本次图片的颜色顺序", evidence);
        }

        if (field is not null)
            return new("verified", field,
                image is null ? "上游返回了有效的网页检索结果或引用" : "图片答对，且上游返回了网页检索证据",
                evidence);

        var note = HasItems(Property(message, "tool_calls")) ||
                   ArrayHas(Property(Property(reply, "content"), "parts"),
                       part => Property(part, "functionCall").ValueKind == JsonValueKind.Object)
            ? "模型发出了等待客户端执行的工具调用，响应中没有检索结果"
            : NoEvidenceNote(root, reply);
        return new("no_evidence", Note: image is null ? note : $"图片已识别。{note}");
    }

    private static string NoEvidenceNote(JsonElement root, JsonElement reply)
    {
        var usage = Property(root, "usage");
        var searchCount = Property(Property(Property(usage, "x_tools"), "search"), "count");
        // 百炼 Chat Completions 在意图识别阶段就会设置 count。
        if (searchCount.ValueKind == JsonValueKind.Number && searchCount.TryGetInt32(out var count) && count > 0)
            return "上游报告了检索意图，暂未提供可核验的检索结果";

        if (StringProperty(reply, "finish_reason") == "length" ||
            StringProperty(reply, "finishReason") == "MAX_TOKENS")
            return "模型输出达到长度上限，未收到有效检索证据";

        return "上游接受了请求，暂未提供可核验的检索结果";
    }

    private static string? FindGroundingField(JsonElement root, JsonElement reply, JsonElement message)
    {
        // 只读协议约定的响应位置，忽略输入回显、工具参数、正文和其他无关数据。
        foreach (var container in new[] { reply, message, root })
        {
            foreach (var field in new[] { "groundingMetadata", "grounding_metadata" })
            {
                var metadata = Property(container, field);
                if (HasWebChunks(Property(metadata, "groundingChunks")) ||
                    HasWebChunks(Property(metadata, "grounding_chunks")))
                    return field;
            }
        }

        foreach (var container in new[] { root, message, Property(root, "search_info"), Property(root, "searchInfo") })
        {
            foreach (var field in new[] { "web_search", "search_result", "search_results", "searchResults" })
            {
                if (ArrayHas(Property(container, field), item => HasWebUrl(item, "link") || HasWebUrl(item, "url")))
                    return field;
            }
        }

        if (HasWebCitation(message) || ArrayHas(Property(message, "content"), HasWebCitation))
            return "annotations";

        return null;
    }

    private static bool HasWebChunks(JsonElement chunks) =>
        ArrayHas(chunks, chunk => HasWebUrl(Property(chunk, "web"), "uri"));

    private static bool HasWebCitation(JsonElement message) =>
        ArrayHas(Property(message, "annotations"), annotation =>
            StringProperty(annotation, "type") == "url_citation" &&
            (HasWebUrl(Property(annotation, "url_citation"), "url") || HasWebUrl(annotation, "url")));

    private static bool HasWebUrl(JsonElement element, string name) =>
        Uri.TryCreate(StringProperty(element, name), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.Host.Length > 0;

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? StringProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool HasItems(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0;

    private static JsonElement FirstItem(JsonElement array) => HasItems(array) ? array[0] : default;

    private static bool ArrayHas(JsonElement array, Func<JsonElement, bool> predicate) =>
        array.ValueKind == JsonValueKind.Array && array.EnumerateArray().Any(predicate);
}
