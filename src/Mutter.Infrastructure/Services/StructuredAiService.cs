using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;

namespace Mutter.Infrastructure.Services;

/// <summary>
/// 结构化 AI 输出实现：JSON mode + 三级降级 + 数值钳制。
/// AI 永不直接写库——本服务只返回解析后的 DTO，数值钳制由 EventApplier 在应用时再做一次。
/// </summary>
public class StructuredAiService : IStructuredAiService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAiChatService _ai;
    private readonly ILogger<StructuredAiService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public StructuredAiService(IServiceScopeFactory scopeFactory, IAiChatService ai, ILogger<StructuredAiService> logger)
    {
        _scopeFactory = scopeFactory;
        _ai = ai;
        _logger = logger;
    }

    public async Task<bool> IsAiConfiguredAsync(CancellationToken ct = default)
    {
        var config = await LoadAiConfigAsync(ct);
        return config is not null
               && !string.IsNullOrWhiteSpace(config.ApiBaseUrl)
               && !string.IsNullOrWhiteSpace(config.ApiKey)
               && !string.IsNullOrWhiteSpace(config.ModelName);
    }

    public async Task<StructuredResult<T>?> GetStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        AiConfig config,
        CancellationToken ct = default) where T : class
        => (await GetStructuredOutcomeAsync<T>(systemPrompt, userPrompt, Array.Empty<string>(), config, ct)).Result;

    public async Task<StructuredResult<T>?> GetStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string> imageUrls,
        AiConfig config,
        CancellationToken ct = default) where T : class
        => (await GetStructuredOutcomeAsync<T>(systemPrompt, userPrompt, imageUrls, config, ct)).Result;

    public async Task<StructuredOutcome<T>> GetStructuredOutcomeAsync<T>(
        string systemPrompt,
        string userPrompt,
        AiConfig config,
        CancellationToken ct = default) where T : class
        => await GetStructuredOutcomeAsync<T>(systemPrompt, userPrompt, Array.Empty<string>(), config, ct);

    public async Task<StructuredOutcome<T>> GetStructuredOutcomeAsync<T>(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string> imageUrls,
        AiConfig config,
        CancellationToken ct = default) where T : class
    {
        // ① 优先 json_object 模式
        var first = await CallAsync(systemPrompt, userPrompt, imageUrls, config, "json_object", ct);
        if (first.Response is not null)
        {
            var parsed = first.Response.FinishReason == "length" ? null : Deserialize<T>(first.Response.Content);
            if (parsed is not null)
                return Success(parsed, first.Response);
            _logger.LogWarning("json_object 模式解析失败或输出被截断，降级 prompt 引导。raw={Raw}", Truncate(first.Response.Content, 200));
        }

        // ② prompt 引导 + 正则提取 {...}
        var fallbackPrompt = systemPrompt + "\n\n注意：只输出一个合法的 JSON 对象，不要 Markdown、不要多余文字。";
        var second = await CallAsync(fallbackPrompt, userPrompt, imageUrls, config, null, ct);
        if (second.Response is not null)
        {
            var parsed = second.Response.FinishReason == "length" ? null : Deserialize<T>(second.Response.Content);
            if (parsed is not null)
                return Success(parsed, second.Response);
        }

        // ③ 将末次调用的具体失败与响应带回，避免调用方把 API 错误、截断和解析失败混为一谈。
        if (second.Response is { } response)
        {
            var kind = response.FinishReason == "length"
                ? StructuredAiFailureKind.Truncated
                : StructuredAiFailureKind.Unparsable;
            _logger.LogWarning("结构化 AI 输出失败 kind={Kind} type={Type}", kind, typeof(T).Name);
            return Failure<T>(kind, response);
        }

        _logger.LogWarning("结构化 AI 调用失败 type={Type} error={Error}", typeof(T).Name, second.ErrorMessage);
        return new StructuredOutcome<T>(null, StructuredAiFailureKind.ApiError, second.ErrorMessage);
    }

    private async Task<(AiChatResponse? Response, string? ErrorMessage)> CallAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<string> imageUrls,
        AiConfig config, string? responseFormat, CancellationToken ct)
    {
        try
        {
            var resp = imageUrls.Count > 0
                ? await _ai.ChatWithImagesAsync(systemPrompt, userPrompt, imageUrls, config, responseFormat, ct)
                : await _ai.ChatAsync(systemPrompt,
                    new List<(string Role, string Content)> { ("user", userPrompt) }, config, responseFormat, ct);
            return (resp, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 结构化调用失败 responseFormat={Fmt} images={Images}",
                responseFormat ?? "null", imageUrls.Count);
            return (null, ex.Message);
        }
    }

    private static StructuredOutcome<T> Success<T>(T data, AiChatResponse response) where T : class
    {
        var result = new StructuredResult<T>(data, response.Model, response.PromptTokens, response.CompletionTokens);
        return new StructuredOutcome<T>(result, Model: response.Model,
            PromptTokens: response.PromptTokens, CompletionTokens: response.CompletionTokens);
    }

    private static StructuredOutcome<T> Failure<T>(StructuredAiFailureKind kind, AiChatResponse response) where T : class =>
        new(null, kind, RawContent: response.RawContent ?? response.Content, Model: response.Model,
            PromptTokens: response.PromptTokens, CompletionTokens: response.CompletionTokens);

    private T? Deserialize<T>(string? raw) where T : class
    {
        var candidate = ExtractRepairableJson(raw);
        return candidate is null ? null : TryParse<T>(candidate, out _);
    }

    /// <summary>
    /// 取出模型输出里的 JSON 对象并修成可解析形态：剥掉 ```json 围栏，再把字符串值里的裸
    /// 换行/制表符转义。代理层（faroapi→gemini 这类）常在长中文值中间折行，裸换行会让整份
    /// JSON 变成非法；此前严格解析直接判失败，既浪费一次降级调用，也把整条链路打成 null。
    /// </summary>
    public static string? ExtractRepairableJson(string? raw)
    {
        var obj = string.IsNullOrEmpty(raw) ? null : ExtractJsonObject(raw);
        return obj is null ? null : EscapeStringControlChars(obj);
    }

    private static string EscapeStringControlChars(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length + 16);
        var inStr = false;
        var escape = false;
        foreach (var c in json)
        {
            if (inStr)
            {
                if (escape) { escape = false; sb.Append(c); continue; }
                if (c == '\\') { escape = true; sb.Append(c); continue; }
                if (c == '"') { inStr = false; sb.Append(c); continue; }
                switch (c)
                {
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                }
                sb.Append(c);
                continue;
            }

            if (c == '"') inStr = true;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static T? TryParse<T>(string json, out bool failed) where T : class
    {
        try
        {
            failed = false;
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch
        {
            failed = true;
            return null;
        }
    }

    /// <summary>从含杂质的文本中提取首个 {...} JSON 块。</summary>
    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inStr = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inStr)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }

    private async Task<AiConfig?> LoadAiConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await repo.GetAiConfigAsync(ct);
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
