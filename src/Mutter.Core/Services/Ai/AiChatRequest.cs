namespace Mutter.Core.Services;

/// <summary>
/// 一次 AI 调用的请求意图。Off 用于内部判定（如择机发言），避免继承全局联网开关后白烧 token。
/// </summary>
public enum AiSearchIntent
{
    Inherit,
    Force,
    Off
}

/// <summary>已经过校验、下载并解码的图片。传输层只负责按各自协议序列化。</summary>
public sealed record AiResolvedImage(string MimeType, byte[] Data, string SourceUrl);

/// <summary>规范化后的消息：正文 + 本条消息附带的图片。</summary>
public sealed record AiMessage(string Role, string Text, IReadOnlyList<AiResolvedImage> Images)
{
    public static AiMessage Plain(string role, string text) =>
        new(role, text, Array.Empty<AiResolvedImage>());

    public static AiMessage WithImages(string role, string text, IReadOnlyList<AiResolvedImage> images) =>
        new(role, text, images);
}

/// <summary>
/// 与具体厂商协议无关的请求。OpenAI 兼容 / Gemini 原生两个传输层都从这里构建请求体，
/// 避免"探测一种形状、正式请求另一种形状"的漂移。
/// </summary>
public sealed record AiChatRequest(
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    string? ResponseFormat = null,
    AiSearchIntent Search = AiSearchIntent.Inherit)
{
    public bool HasImages => Messages.Any(m => m.Images.Count > 0);

    public string LastUserText =>
        Messages.LastOrDefault(m => m.Role == "user")?.Text ?? string.Empty;
}
