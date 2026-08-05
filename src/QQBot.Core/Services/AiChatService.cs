using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

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
        List<(string Role, string Content, string? ImageUrl)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var (role, msgContent, imageUrl) in conversationHistory)
        {
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var contentParts = new List<object>
                {
                    new { type = "text", text = msgContent }
                };

                var resolvedUrl = await ResolveImageUrlAsync(imageUrl);
                if (!string.IsNullOrEmpty(resolvedUrl))
                {
                    contentParts.Add(new { type = "image_url", image_url = new { url = resolvedUrl } });
                }

                messages.Add(new { role, content = contentParts });
            }
            else
            {
                messages.Add(new { role, content = msgContent });
            }
        }

        return await SendRequestAsync(messages, config, null, ct);
    }

    private async Task<AiChatResponse> SendRequestAsync(List<object> messages, AiConfig config, string? responseFormat, CancellationToken ct)
    {
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

        // 联网搜索：根据 API 地址推断服务商模式，附加对应参数
        if (config.WebSearchEnabled)
        {
            var mode = AiProviderPreset.InferWebSearchMode(config.ApiBaseUrl);
            switch (mode)
            {
                case "enable_search":
                    // 通义千问：顶层附加 enable_search，平台自动检索并融入回答
                    requestBody["enable_search"] = true;
                    _logger.LogInformation("Web search enabled (enable_search mode) for {Url}", config.ApiBaseUrl);
                    break;
                case "web_search_tool":
                    // 智谱 GLM：tools 声明 web_search 工具，enable=true，平台自动执行
                    requestBody["tools"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "web_search",
                            ["web_search"] = new Dictionary<string, object>
                            {
                                ["enable"] = true,
                                ["search_result"] = true
                            }
                        }
                    };
                    _logger.LogInformation("Web search enabled (web_search_tool mode) for {Url}", config.ApiBaseUrl);
                    break;
                default:
                    _logger.LogWarning("WebSearchEnabled is on but URL {Url} does not match any supported provider, skipping", config.ApiBaseUrl);
                    break;
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
        string? model;
        int promptTokens = 0, completionTokens = 0;

        try
        {
            content = json.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "";

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
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens
        };

        _logger.LogDebug("AI response: Model={Model}, Tokens={Prompt}+{Completion}",
            result.Model, result.PromptTokens, result.CompletionTokens);

        return result;
    }

    private const long MaxImageSizeBytes = 10 * 1024 * 1024; // 10MB

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
