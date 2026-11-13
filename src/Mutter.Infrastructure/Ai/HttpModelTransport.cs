using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Configuration;
using Mutter.Core.Services;

namespace Mutter.Infrastructure.Ai;

/// <summary>现有兼容协议的 HTTP 实现；不拥有搜索决策或工作流重试。</summary>
public sealed class HttpModelTransport(HttpClient httpClient, ILogger<HttpModelTransport> logger,
    IAiCallStore? callStore = null, TimeProvider? clock = null,
    UpstreamHealthTracker? health = null, RuntimeOptions? runtime = null)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger _logger = logger;
    private readonly UpstreamHealthTracker? _health = health;
    private readonly TimeSpan _attemptCap =
        TimeSpan.FromSeconds(runtime?.AiPerAttemptTimeoutSeconds ?? 90);
    private readonly TimeSpan _degradedAttemptCap =
        TimeSpan.FromSeconds(runtime?.DegradedPerAttemptTimeoutSeconds ?? 45);
    public async Task<AiChatResponse> SendOpenAiAsync(
        AiChatRequest request, AiConfig config, string? searchShape, CancellationToken ct)
    {
        var requestBody = OpenAiRequestBuilder.Build(request, config, searchShape);
        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/chat/completions";

        _logger.LogInformation(
            "AI 请求 transport=openai images={Images} search={Search} url={Url}",
            request.Messages.Sum(m => m.Images.Count), searchShape ?? "off", url);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        if (config.ApiKey.Length > 0) httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        httpRequest.Content = JsonContent.Create(requestBody);

        HttpResponseMessage response;
        try
        {
            AiCallScope.Current?.BeginAttempt();
            response = await SendWithBudgetAsync(httpRequest, config, "openai", searchShape is not null, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI request failed: {Url}", url);
            throw new InvalidOperationException($"AI request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI request cancelled");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "AI request timeout: {Url}", url);
            throw new InvalidOperationException($"AI request timeout: {ex.Message}", ex);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AI error: {StatusCode}, Body: {Body}",
                    (int)response.StatusCode, RedactForLog(raw));
                // 异常消息会被上层原样写进日志：上游回显的 Key 不能绕过 RedactForLog。
                throw new AiRequestException((int)response.StatusCode, RedactForLog(raw));
            }

            JsonElement json;
            try
            {
                json = JsonDocument.Parse(raw).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("AI response invalid JSON", ex);
            }

            var rawContent = WebSearchProbe.ReadResponseText(json);
            var finishReason = ReadFinishReason(json);
            var model = json.TryGetProperty("model", out var modelElement) &&
                        modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : config.ModelName;
            var (promptTokens, completionTokens) = ReadUsage(json);

            _logger.LogDebug(
                "AI response: transport=openai model={Model}, tokens={Prompt}+{Completion}, finish={Finish}",
                model, promptTokens, completionTokens, finishReason);

            return new AiChatResponse
            {
                Content = CjkTextFixer.Cleanse(rawContent),
                RawContent = rawContent,
                Model = model,
                FinishReason = finishReason,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
    }

    public async Task<AiChatResponse> SendGeminiNativeBodyAsync(
        Dictionary<string, object> requestBody, AiConfig config, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var url in WebSearchProbe.GeminiUrlCandidates(config.ApiBaseUrl, config.ModelName))
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            // 本地无认证代理可以留空 Key：不要发送空的 x-goog-api-key / Bearer。
            if (config.ApiKey.Length > 0)
            {
                httpRequest.Headers.Add("x-goog-api-key", config.ApiKey);
                // Google 官方 Key 认 x-goog-api-key；中转通常认 Bearer。Key 不是 AIza 开头时两个都带，无害。
                if (!config.ApiKey.StartsWith("AIza", StringComparison.Ordinal))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            }
            httpRequest.Content = JsonContent.Create(requestBody);

            _logger.LogInformation(
                "AI 请求 transport=gemini search={Search} url={Url}",
                requestBody.ContainsKey("tools") ? "on" : "off", url);

            HttpResponseMessage response;
            try
            {
                AiCallScope.Current?.BeginAttempt();
                response = await SendWithBudgetAsync(httpRequest, config, "gemini", requestBody.ContainsKey("tools"), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("Gemini transport failed; request will not be replayed", ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastError = $"404 {url}";
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gemini native error: {StatusCode}, Body: {Body}",
                        (int)response.StatusCode, RedactForLog(raw));
                    throw new AiRequestException((int)response.StatusCode, RedactForLog(raw));
                }

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
                var (promptTokens, completionTokens) = ReadGeminiUsage(json);

                _logger.LogDebug(
                    "AI response: transport=gemini model={Model}, tokens={Prompt}+{Completion}, finish={Finish}",
                    config.ModelName, promptTokens, completionTokens, finishReason);

                return new AiChatResponse
                {
                    Content = CjkTextFixer.Cleanse(rawContent),
                    RawContent = rawContent,
                    Model = config.ModelName,
                    FinishReason = finishReason,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens
                };
            }
        }

        throw new AiRequestException(404, $"未找到 Gemini 原生端点：{lastError}");
    }

    /// <summary>
    /// 单次尝试预算：min(配置的尝试上限, 根期限剩余)。上游挂死时最多烧掉一个尝试份额，
    /// 不再把整条生成期限耗在一根悬空连接上——降级链的后续阶段因此总有预算可用。
    /// 结束后按结果向健康追踪上报一次；路径/格式等确定性拒绝不代表上游不可用。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithBudgetAsync(HttpRequestMessage request, AiConfig config,
        string transport, bool search, CancellationToken ct)
    {
        var cap = _health is { IsDegraded: true } ? _degradedAttemptCap : _attemptCap;
        // 连接上的超时是显式用户设置，允许比默认尝试上限更短或更长；仍需受根期限约束。
        if (config.ConnectionTimeoutSeconds is int timeout)
            cap = TimeSpan.FromSeconds(timeout);
        foreach (var (name, value) in config.Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        var scope = AiCallScope.Current;
        if (scope is not null && scope.Remaining < cap) cap = scope.Remaining;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(cap);
        var started = Stopwatch.GetTimestamp();
        long ElapsedMs() => Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;
        try
        {
            var response = await AiHttpAudit.SendAsync(_httpClient, request, config.ModelName, transport,
                attempt.Token, callStore, logger, clock, stage: search ? "search" : null);
            if (response.IsSuccessStatusCode)
                _health?.Record(true, ElapsedMs());
            else if (response.StatusCode == HttpStatusCode.RequestTimeout
                || response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var retry = response.Headers.RetryAfter;
                var retryAfter = retry?.Delta
                    ?? (retry?.Date is { } date ? date - (clock ?? TimeProvider.System).GetUtcNow() : (TimeSpan?)null);
                _health?.Record(false, ElapsedMs(), (int)response.StatusCode,
                    retryAfter: retryAfter);
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _health?.Record(false, ElapsedMs());
            throw;
        }
        catch (HttpRequestException)
        {
            _health?.Record(false, ElapsedMs());
            throw;
        }
    }

    private static string? ReadFinishReason(JsonElement json)
    {
        if (!json.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        return choice.TryGetProperty("finish_reason", out var reason) &&
               reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;
    }

    private static (int Prompt, int Completion) ReadUsage(JsonElement json)
    {
        if (!json.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);

        var prompt = usage.TryGetProperty("prompt_tokens", out var pt) &&
                     pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt32()
            : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var ct) &&
                         ct.ValueKind == JsonValueKind.Number
            ? ct.GetInt32()
            : 0;
        return (prompt, completion);
    }

    private static (int Prompt, int Completion) ReadGeminiUsage(JsonElement json)
    {
        if (!json.TryGetProperty("usageMetadata", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return (0, 0);

        var prompt = usage.TryGetProperty("promptTokenCount", out var pt) &&
                     pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt32()
            : 0;
        var completion = usage.TryGetProperty("candidatesTokenCount", out var ct) &&
                         ct.ValueKind == JsonValueKind.Number
            ? ct.GetInt32()
            : 0;
        return (prompt, completion);
    }

    /// <summary>
    /// 上游错误体只用于排障：压成单行、去掉控制字符（防日志注入/伪造行），
    /// 并把疑似密钥打码——部分网关会在 401 里回显收到的 Key。
    /// </summary>
    internal static string RedactForLog(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var text = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(sk-[A-Za-z0-9_\-]{6,})", "sk-***");
        // Google 官方 Key 以 AIza 开头，不属于 sk- 形态。
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(AIza[0-9A-Za-z_\-]{10,})", "AIza***");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(bearer\s+)[A-Za-z0-9_\-\.]{8,}", "$1***");
        // 网关常以 JSON 回显入参：把常见密钥字段的值一并打码。
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(""(?:api[_-]?key|apikey|key|token|secret|authorization|x-goog-api-key)""\s*:\s*"")[^""]{6,}("")",
            "$1***$2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[\x00-\x1F\x7F]", "");
        return text.Length > 200 ? text[..200] : text;
    }

}
