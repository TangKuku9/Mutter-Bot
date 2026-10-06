using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Infrastructure.Services;

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
        => await GetStructuredAsync<T>(systemPrompt, userPrompt, Array.Empty<string>(), config, ct);

    public async Task<StructuredResult<T>?> GetStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string> imageUrls,
        AiConfig config,
        CancellationToken ct = default) where T : class
    {
        // ① 优先 json_object 模式
        var (resp1, _) = await CallAsync(systemPrompt, userPrompt, imageUrls, config, "json_object", ct);
        if (resp1 is not null)
        {
            var parsed = Deserialize<T>(resp1.Content);
            if (parsed is not null)
                return new StructuredResult<T>(parsed, resp1.Model, resp1.PromptTokens, resp1.CompletionTokens);
            _logger.LogWarning("json_object 模式解析失败，降级 prompt 引导。raw={Raw}", Truncate(resp1.Content, 200));
        }

        // ② prompt 引导 + 正则提取 {...}
        var fallbackPrompt = systemPrompt + "\n\n注意：只输出一个合法的 JSON 对象，不要 Markdown、不要多余文字。";
        var (resp2, _) = await CallAsync(fallbackPrompt, userPrompt, imageUrls, config, null, ct);
        if (resp2 is not null)
        {
            var parsed = Deserialize<T>(resp2.Content);
            if (parsed is not null)
                return new StructuredResult<T>(parsed, resp2.Model, resp2.PromptTokens, resp2.CompletionTokens);
        }

        // ③ 返回 null，由调用方走预设模板兜底
        _logger.LogWarning("结构化 AI 输出解析失败，返回 null 由调用方兜底。type={Type}", typeof(T).Name);
        return null;
    }

    private async Task<(AiChatResponse? Response, bool UsedJsonMode)> CallAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<string> imageUrls,
        AiConfig config, string? responseFormat, CancellationToken ct)
    {
        try
        {
            var resp = imageUrls.Count > 0
                ? await _ai.ChatWithImagesAsync(systemPrompt, userPrompt, imageUrls, config, responseFormat, ct)
                : await _ai.ChatAsync(systemPrompt,
                    new List<(string Role, string Content)> { ("user", userPrompt) }, config, responseFormat, ct);
            return (resp, responseFormat is not null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 结构化调用失败 responseFormat={Fmt} images={Images}",
                responseFormat ?? "null", imageUrls.Count);
            return (null, false);
        }
    }

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
