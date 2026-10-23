namespace Mutter.Infrastructure.Entities;

/// <summary>
/// 择机发言决策留痕。每次评估一行（含未发言的），面板据此回答「bot 为什么突然说话/为什么不说」。
/// </summary>
public class ProactiveChatLogEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public DateTime EvaluatedAt { get; set; }
    public int MessageCount { get; set; }

    /// <summary>这次评估实际随消息附带了几张图片（0 表示纯文本判断）。</summary>
    public int ImageCount { get; set; }

    /// <summary>这次判定注入了几条群记忆（第一阶段每群至多一条，0 表示没带）。</summary>
    public int MemoryCount { get; set; }

    /// <summary>注入的群记忆实际占了多少字符，用于回答"这次提示词为什么这么大"。</summary>
    public int MemoryPromptChars { get; set; }

    /// <summary>见 <see cref="Mutter.Core.Proactive.ProactiveOutcomes"/>。</summary>
    public string Outcome { get; set; } = string.Empty;

    public bool ShouldSpeak { get; set; }
    public string? Topic { get; set; }
    public string? Line { get; set; }
    public string? Reason { get; set; }
    public string? Detail { get; set; }
    public string? SentMessageId { get; set; }
    public string? Model { get; set; }

    /// <summary>判定那一步的用量。改写那一步的用量单独记，面板才能分清成本花在哪。</summary>
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    /// <summary>这次判定给出的检索词；空表示没进联网改写。</summary>
    public string? SearchQuery { get; set; }

    /// <summary>是否真的发起了带联网的改写调用（不代表上游一定返回了检索结果）。</summary>
    public bool SearchUsed { get; set; }

    /// <summary>联网改写那一步的用量；没走改写时为 0。</summary>
    public int SpeechPromptTokens { get; set; }
    public int SpeechCompletionTokens { get; set; }
}
