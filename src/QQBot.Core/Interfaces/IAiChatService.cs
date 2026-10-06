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
    /// 单条用户消息带多张图片的多模态调用，可同时指定响应格式（结构化输出用）。
    /// </summary>
    Task<AiChatResponse> ChatWithImagesAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string> imageUrls,
        AiConfig config,
        string? responseFormat = null,
        CancellationToken ct = default);

    /// <summary>
    /// 预筛出确实能附进请求的图片：模型侧不收的类型（QQ 表情包多为 GIF）与已失效的图链都会被剔除。
    /// 返回值仍是入参中的原始 URL 与顺序，可直接用于提交。
    /// </summary>
    Task<IReadOnlyList<string>> AttachableImagesAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default);

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
