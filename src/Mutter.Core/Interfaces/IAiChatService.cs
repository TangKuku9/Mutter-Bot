using Mutter.Core.Models;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Interfaces;

public interface IAiChatService
{
    Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content)> conversationHistory,
        AiConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// 历史里某一条消息可带多张图片（末条即本轮用户消息）。
    /// 早先这里是单个 <c>string? ImageUrl</c>，调用方只好写 <c>ImageUrls[0]</c>——
    /// 群友一次发好几张时，只有第一张进模型，其余静默丢弃。
    /// </summary>
    Task<AiChatResponse> ChatAsync(
        string systemPrompt,
        List<(string Role, string Content, IReadOnlyList<string>? ImageUrls)> conversationHistory,
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
