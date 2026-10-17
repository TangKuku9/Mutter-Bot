using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

public class AiChatService : IAiChatService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiChatService> _logger;

    public AiChatService(HttpClient httpClient, ILogger<AiChatService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default)
    {
        return await ChatAsync(systemPrompt, conversationHistory, config, null, ct);
    }

    public async Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content)> conversationHistory,
        AiConfig config,
        string? responseFormat,
        CancellationToken ct = default)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var (role, msgContent) in conversationHistory)
        {
            messages.Add(new { role, content = msgContent });
        }

        return await SendRequestAsync(messages, config, responseFormat, ct);
    }

    public async Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content, IReadOnlyList<string>? ImageUrls)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var (role, msgContent, imageUrls) in conversationHistory)
        {
            if (imageUrls is { Count: > 0 })
                messages.Add(new { role, content = await BuildContentPartsAsync(msgContent, imageUrls) });
            else
                messages.Add(new { role, content = msgContent });
        }

        return await SendRequestAsync(messages, config, null, ct);
    }

    public async Task<AiChatResponse> ChatWithImagesAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string> imageUrls,
        AiConfig config,
        string? responseFormat = null,
        CancellationToken ct = default)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        messages.Add(new
        {
            role = "user",
            content = await BuildContentPartsAsync(userPrompt, imageUrls)
        });

        return await SendRequestAsync(messages, config, responseFormat, ct);
    }

    /// <summary>图文混排的一条消息：正文在前，图片按给定顺序附在后面。不可读的图片跳过。</summary>
    private async Task<List<object>> BuildContentPartsAsync(string text, IReadOnlyList<string> imageUrls)
    {
        var parts = new List<object> { new { type = "text", text } };

        foreach (var url in imageUrls)
        {
            var resolved = await AttachableAsync(url, CancellationToken.None);
            if (resolved is not null)
                parts.Add(new { type = "image_url", image_url = new { url = resolved } });
        }

        return parts;
    }

    /// <summary>
    /// 调用方预筛：返回真正能附进请求的图片（顺序不变）。
    /// 先问一遍再写提示词，prompt 里"附了几张"才不会和实际发出的不符。
    /// </summary>
    public async Task<IReadOnlyList<string>> AttachableImagesAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var keep = new List<string>(imageUrls.Count);
        foreach (var url in imageUrls)
            if (await AttachableAsync(url, ct) is not null)
                keep.Add(url);
        return keep;
    }

    /// <summary>能附则返回最终送出的 URL（本地文件已转 base64），否则 null。</summary>
    private async Task<string?> AttachableAsync(string url, CancellationToken ct)
    {
        var resolved = await ResolveImageUrlAsync(url);
        if (string.IsNullOrEmpty(resolved)) return null;

        // 模型是按 URL 自行取图的，类型我们无法替换。QQ 表情包大量是 GIF，而 Gemini 收到
        // image/gif 会把整次请求打成 500 —— 一张动图连带毁掉整条回复/评估；官方图链又有一小时
        // 时效。所以先花一个极小的 Range 请求确认类型与存活，不合格就只留文字占位。
        if (!IsRemoteUrl(resolved)) return resolved;

        if (SupportedImageMime(await SniffImageMimeAsync(resolved, ct)))
            return resolved;

        _logger.LogInformation("图片未随请求附带（模型不收该类型或链接已失效）url={Url}", TruncateUrl(resolved));
        return null;
    }

    /// <summary>模型侧确认支持的类型才附：png/jpeg。gif、webp、未知一律不附，避免整次调用被打成 500。</summary>
    public static bool SupportedImageMime(string? mime) =>
        mime is "image/png" or "image/jpeg";

    /// <summary>按文件头判断图片类型，认不出返回 null。</summary>
    public static string? DetectImageMime(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        if (head.Length >= 12 &&
            head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
            head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            return "image/webp";
        if (head.Length >= 3 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F')
            return "image/gif";
        return null;
    }

    private static bool IsRemoteUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string TruncateUrl(string url) => url.Length <= 80 ? url : url[..80] + "…";

    /// <summary>只取头部若干字节，同时兼作链接存活探测。QQ 的图链不支持 HEAD，用带 Range 的 GET。</summary>
    private async Task<string?> SniffImageMimeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SniffTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, HeadBytes);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            var buffer = new byte[HeadBytes + 1];
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n <= 0) break;
                read += n;
            }
            return DetectImageMime(buffer.AsSpan(0, read));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "图片嗅探失败 url={Url}", TruncateUrl(url));
            return null;
        }
    }

    private async Task<AiChatResponse> SendRequestAsync(List<object> messages, AiConfig config, string? responseFormat, CancellationToken ct)
    {
        // 已选用的 Gemini 原生通道：只有纯文本、非结构化请求才能无损转换。
        // 转换不了（带图/JSON 输出）时继续走兼容接口，宁可本次不检索也不破坏原有能力。
        if (config.WebSearchEnabled &&
            config.WebSearchEndpoint == "gemini" &&
            config.WebSearchShape == "gemini_native")
        {
            if (responseFormat is null &&
                TryBuildGeminiContents(messages, out var geminiContents, out var systemInstruction))
            {
                try
                {
                    return await SendGeminiNativeAsync(geminiContents, systemInstruction, config, ct);
                }
                catch (Exception ex)
                {
                    // 原生通道失败时退回兼容接口，回复不断；搜索可能失效，日志留痕
                    _logger.LogWarning(ex,
                        "Gemini 原生联网请求失败，退回兼容接口（本次可能不检索）url={Url} model={Model}",
                        config.ApiBaseUrl, config.ModelName);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Gemini 原生联网跳过：请求含图片或结构化输出，本次走兼容接口 url={Url}", config.ApiBaseUrl);
            }
        }

        // 使用 Dictionary 动态构造请求体，以支持按服务商附加联网搜索参数
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = config.ModelName,
            ["messages"] = messages,
            ["temperature"] = config.Temperature,
            ["max_tokens"] = config.MaxTokens
        };

        // 结构化输出：response_format 仅在非空时附加（OpenAI 兼容 json_object 模式）
        if (!string.IsNullOrEmpty(responseFormat))
        {
            requestBody["response_format"] = new Dictionary<string, object>
            {
                ["type"] = responseFormat
            };
        }

        // 联网搜索：优先用探测报告里“选用”的策略；没有才回退到旧的按地址推断。
        if (config.WebSearchEnabled)
        {
            if (config.WebSearchEndpoint == "openai" && !string.IsNullOrEmpty(config.WebSearchShape))
            {
                // 与探测共用同一份形状定义，避免"探测能过、正式请求参数写错"
                WebSearchProbe.ApplyShapeToBody(requestBody, config.WebSearchShape);
                _logger.LogInformation("Web search enabled (selected shape {Shape}) for {Url}",
                    config.WebSearchShape, config.ApiBaseUrl);
            }
            else if (config.WebSearchEndpoint == "gemini")
            {
                // 原生通道已在上方尝试；转换失败或请求失败时本次不检索，不拿兼容层参数硬套
                _logger.LogInformation("Gemini 原生联网本次未生效（已回退兼容接口）url={Url}", config.ApiBaseUrl);
            }
            else
            {
                var mode = AiProviderPreset.InferWebSearchMode(config.ApiBaseUrl);
                if (mode == "none")
                {
                    _logger.LogWarning(
                        "WebSearchEnabled is on but no verified shape and URL {Url} does not match any supported provider, skipping",
                        config.ApiBaseUrl);
                }
                else
                {
                    WebSearchProbe.ApplyShapeToBody(requestBody, mode);
                    _logger.LogInformation("Web search enabled (inferred mode {Mode}) for {Url}", mode, config.ApiBaseUrl);
                }
            }
        }

        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{config.ApiBaseUrl.TrimEnd('/')}/chat/completions");

        httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        httpRequest.Content = JsonContent.Create(requestBody);

        _logger.LogDebug("Calling AI: {Url}, Messages: {Count}", httpRequest.RequestUri, messages.Count);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI request failed: {Url}", httpRequest.RequestUri);
            throw new InvalidOperationException($"AI request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI request cancelled");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "AI request timeout: {Url}", httpRequest.RequestUri);
            throw new InvalidOperationException($"AI request timeout: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AI error: {StatusCode}, Body: {Body}",
                (int)response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);
            throw new InvalidOperationException(
                $"AI error {(int)response.StatusCode}: {errorBody[..Math.Min(200, errorBody.Length)]}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        string content;
        string rawContent;
        string? model;
        string? finishReason = null;
        int promptTokens = 0, completionTokens = 0;

        try
        {
            var choice = json.GetProperty("choices")[0];
            rawContent = choice.GetProperty("message")
                .GetProperty("content").GetString() ?? "";
            content = rawContent;
            if (choice.TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
                finishReason = reason.GetString();

            // 出口统一清理：代理按列宽硬折进来的换行与行首空格，既弄丑了发进群的话，
            // 也是结构化输出里那些"裸换行让 JSON 解析失败"的同一个源头。
            content = CjkTextFixer.Cleanse(content);

            model = json.GetProperty("model").GetString();

            if (json.TryGetProperty("usage", out var usage))
            {
                promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            }
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogError(ex, "AI response missing required fields");
            throw new InvalidOperationException("AI response missing choices/message/content", ex);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "AI response JSON parse failed");
            throw new InvalidOperationException("AI response JSON invalid", ex);
        }

        var result = new AiChatResponse
        {
            Content = content,
            RawContent = rawContent,
            Model = model,
            FinishReason = finishReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens
        };

        _logger.LogDebug("AI response: Model={Model}, Tokens={Prompt}+{Completion}",
            result.Model, result.PromptTokens, result.CompletionTokens);

        return result;
    }

    /// <summary>
    /// 把 OpenAI messages 转成 Gemini contents。只要有一条消息不是纯文本（图片/多模态），
    /// 或角色无法映射，就返回 false——调用方退回兼容接口，避免丢图或改变语义。
    /// </summary>
    private static bool TryBuildGeminiContents(
        List<object> messages, out List<object> contents, out string? systemInstruction)
    {
        contents = new List<object>();
        systemInstruction = null;
        var systemParts = new List<string>();

        var elements = JsonSerializer.SerializeToElement(messages);
        if (elements.ValueKind != JsonValueKind.Array) return false;

        foreach (var message in elements.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) return false;
            var role = message.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            if (role is null) return false;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return false;

            var text = content.GetString() ?? string.Empty;
            if (role == "system")
            {
                systemParts.Add(text);
                continue;
            }
            if (role is not ("user" or "assistant")) return false;

            contents.Add(new Dictionary<string, object>
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = text } }
            });
        }

        systemInstruction = systemParts.Count == 0 ? null : string.Join("\n\n", systemParts);
        return contents.Count > 0;
    }

    /// <summary>
    /// Gemini 原生 :generateContent 请求（declarative google_search）。仅用于纯文本、非结构化请求。
    /// 端点候选与探测共用 <see cref="WebSearchProbe.GeminiUrlCandidates"/>，404 换下一条。
    /// </summary>
    private async Task<AiChatResponse> SendGeminiNativeAsync(
        List<object> contents, string? systemInstruction, AiConfig config, CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new Dictionary<string, object>
            {
                ["temperature"] = config.Temperature,
                ["maxOutputTokens"] = config.MaxTokens
            },
            ["tools"] = new List<object>
            {
                new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() }
            }
        };

        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            requestBody["systemInstruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = systemInstruction } }
            };
        }

        string? lastError = null;
        foreach (var url in WebSearchProbe.GeminiUrlCandidates(config.ApiBaseUrl, config.ModelName))
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("x-goog-api-key", config.ApiKey);
            // Google 官方 Key 认 x-goog-api-key；中转通常认 Bearer。Key 不是 AIza 开头时两个都带，无害。
            if (!config.ApiKey.StartsWith("AIza", StringComparison.Ordinal))
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            httpRequest.Content = JsonContent.Create(requestBody);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, ct);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastError = $"404 {url}";
                    continue;
                }

                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Gemini native error {(int)response.StatusCode}: {raw[..Math.Min(300, raw.Length)]}");

                JsonElement json;
                try
                {
                    json = JsonDocument.Parse(raw).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Gemini native response invalid JSON", ex);
                }

                if (!json.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array ||
                    candidates.GetArrayLength() == 0)
                    throw new InvalidOperationException("Gemini native response missing candidates");

                var candidate = candidates[0];
                var finishReason = candidate.TryGetProperty("finishReason", out var fr) &&
                                   fr.ValueKind == JsonValueKind.String
                    ? fr.GetString()
                    : null;

                var rawContent = WebSearchProbe.ReadResponseText(json);
                var content = CjkTextFixer.Cleanse(rawContent);

                int promptTokens = 0, completionTokens = 0;
                if (json.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt) && pt.ValueKind == JsonValueKind.Number)
                        promptTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ctok) && ctok.ValueKind == JsonValueKind.Number)
                        completionTokens = ctok.GetInt32();
                }

                _logger.LogDebug(
                    "Gemini native response: Model={Model}, Tokens={Prompt}+{Completion}, finish={Finish}",
                    config.ModelName, promptTokens, completionTokens, finishReason);

                return new AiChatResponse
                {
                    Content = content,
                    RawContent = rawContent,
                    Model = config.ModelName,
                    FinishReason = finishReason,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens
                };
            }
        }

        throw new InvalidOperationException($"未找到 Gemini 原生端点：{lastError}");
    }

    private const long MaxImageSizeBytes = 10 * 1024 * 1024; // 10MB

    /// <summary>嗅探只需要文件头，12 字节够认 PNG/JPEG/GIF/WEBP。</summary>
    private const int HeadBytes = 12;

    private static readonly TimeSpan SniffTimeout = TimeSpan.FromSeconds(5);

    private async Task<string> ResolveImageUrlAsync(string imageUrl)
    {
        if (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://"))
            return imageUrl;

        if (imageUrl.StartsWith("file://"))
        {
            var path = imageUrl.Substring("file://".Length);
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > MaxImageSizeBytes)
                {
                    _logger.LogWarning("Image too large ({Size} bytes, max {Max}): {Path}", fileInfo.Length, MaxImageSizeBytes, path);
                    return string.Empty;
                }
                var bytes = await File.ReadAllBytesAsync(path);
                var ext = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                return $"data:{ext};base64,{Convert.ToBase64String(bytes)}";
            }
        }

        if (File.Exists(imageUrl))
        {
            var fileInfo = new FileInfo(imageUrl);
            if (fileInfo.Length > MaxImageSizeBytes)
            {
                _logger.LogWarning("Image too large ({Size} bytes, max {Max}): {Path}", fileInfo.Length, MaxImageSizeBytes, imageUrl);
                return string.Empty;
            }
            var bytes = await File.ReadAllBytesAsync(imageUrl);
            var ext = Path.GetExtension(imageUrl).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
            return $"data:{ext};base64,{Convert.ToBase64String(bytes)}";
        }

        return imageUrl;
    }
}
