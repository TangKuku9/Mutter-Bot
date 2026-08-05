using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Interfaces;

public interface IAiChatService
{
    Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default);

    Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content, string? ImageUrl)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// 带响应格式约束的聊天（供结构化输出使用，如 responseFormat="json_object"）。
    /// 实现：把 responseFormat 写入请求体 response_format 字段；为 null 则等价于普通 Chat。
    /// </summary>
    Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content)> conversationHistory,
        AiConfig config,
        string? responseFormat,
        CancellationToken ct = default);
}
