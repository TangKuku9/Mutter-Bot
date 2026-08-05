using QQBot.Core.Models.Config;

namespace QQBot.Core.Game.Interfaces;

/// <summary>
/// 结构化 AI 输出结果，包含反序列化 DTO 与 AI 用量信息。
/// </summary>
public record StructuredResult<T>(T Data, string? Model, int PromptTokens, int CompletionTokens) where T : class;

/// <summary>
/// 结构化 AI 输出服务。封装 JSON mode + 解析降级 + 数值钳制。
/// 用于 AI 事件生成器与自由动作裁判等需要结构化结果的游戏场景。
/// </summary>
public interface IStructuredAiService
{
    /// <summary>
    /// 调用 AI 获取结构化 JSON 并反序列化为 T。
    /// 三级降级：① json_object 模式 → ② prompt 引导 + 正则提取 {...} → ③ 返回 null 由调用方兜底。
    /// </summary>
    Task<StructuredResult<T>?> GetStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        AiConfig config,
        CancellationToken ct = default) where T : class;

    /// <summary>AI 配置是否可用（ApiBaseUrl/ApiKey/ModelName 齐全）。</summary>
    Task<bool> IsAiConfiguredAsync(CancellationToken ct = default);
}
