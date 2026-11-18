using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>单次探测请求的结果。URL 用于在同一原生路径测试图片。</summary>
public sealed record ProbeSendResult(
    int? Status, JsonElement? Root, string? Error = null, string? Url = null, bool TimedOut = false);

public sealed class WebSearchProbeRunner(HttpClient httpClient, ILogger<WebSearchProbeRunner> logger,
    IAiCallStore? callStore = null, TimeProvider? clock = null,
    IReadOnlyDictionary<string, string>? requestHeaders = null,
    string driverTemplateId = "", int? requestTimeoutSeconds = null)
{
    public WebSearchProbeRunner WithConnection(
        IReadOnlyDictionary<string, string> headers, string templateId, int? timeoutSeconds) =>
        new(httpClient, logger, callStore, clock, headers, templateId, timeoutSeconds);

    public async Task<ProbeSendResult> SendAsync(
        string apiBaseUrl, string apiKey, string endpoint, string? shape, string model, CancellationToken ct = default,
        WebSearchImageChallenge? image = null, string? verifiedUrl = null)
    {
        var isGemini = endpoint == "gemini";
        var question = WebSearchProbe.ProbeQuestion;
        var message = image is null
            ? AiMessage.Plain("user", question)
            : AiMessage.WithImages("user", WebSearchImageChallenge.Instruction + "\n" + question, [image.Image]);
        var probe = new AiChatRequest(string.Empty, [message]);
        var config = new AiConfig
        {
            ApiBaseUrl = apiBaseUrl, ModelName = model, ProviderTemplateId = driverTemplateId,
            MaxTokens = WebSearchProbe.ProbeMaxOutputTokens
        };

        Dictionary<string, object> body;
        string[] urls;
        if (isGemini)
        {
            if (!GeminiNativeRequestBuilder.TryBuild(probe, config, shape is not null, out body, out var reason))
                throw new InvalidOperationException(reason);
            ((Dictionary<string, object>)body["generationConfig"]).Remove("temperature");
            urls = WebSearchProbe.GeminiUrlCandidates(apiBaseUrl, model);
            if (verifiedUrl is not null && urls.Contains(verifiedUrl))
                urls = [verifiedUrl];
        }
        else
        {
            body = OpenAiRequestBuilder.Build(probe, config, shape);
            body.Remove("temperature");
            urls = [$"{apiBaseUrl.TrimEnd('/')}/chat/completions"];
        }

        // 同一个用例的所有路径共享超时预算。
        var timeoutSeconds = Math.Clamp(requestTimeoutSeconds ?? 20, 5, 45);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            foreach (var url in urls)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                ApplyHeaders(request, apiKey, isGemini);
                using var response = await AiHttpAudit.SendAsync(httpClient, request, model, endpoint, deadline.Token,
                    callStore, logger, clock, "probe", "probe");
                if (isGemini && response.StatusCode == HttpStatusCode.NotFound) continue;

                JsonElement? root = null;
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                        root = document.RootElement.Clone();
                    }
                    catch (JsonException) { /* 判定器会报告无效响应。 */ }
                }

                return new((int)response.StatusCode, root, Url: url);
            }
            return new(404, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Web search probe timed out model={Model} shape={Shape}", model, shape);
            return new(null, null, $"上游探测超过 {timeoutSeconds} 秒", TimedOut: true);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Web search probe connection failed model={Model} shape={Shape} error={Error}",
                model, shape, ex.HttpRequestError);
            return new(null, null, "连接模型服务失败");
        }
    }

    private void ApplyHeaders(HttpRequestMessage request, string apiKey, bool isGemini)
    {
        if (apiKey.Length > 0)
        {
            if (isGemini) request.Headers.Add("x-goog-api-key", apiKey);
            if (!isGemini || !apiKey.StartsWith("AIza", StringComparison.Ordinal))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        if (requestHeaders is null) return;
        foreach (var (name, value) in requestHeaders)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
