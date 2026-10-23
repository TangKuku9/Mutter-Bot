namespace Mutter.Infrastructure.Entities;

using Mutter.Core.Models.Config;

/// <summary>择机发言全局配置，单行表（Id 恒为 1）。由管理面板读写。</summary>
public class ProactiveChatConfigEntity
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public int WindowSeconds { get; set; } = 60;
    public int WindowMaxMessages { get; set; } = 12;
    public int CooldownMinutes { get; set; } = 45;
    public int MaxPerGroupPerDay { get; set; } = 3;
    public int MaxGlobalPerDay { get; set; } = 12;
    public int QuietStartHour { get; set; } = 23;
    public int QuietEndHour { get; set; } = 8;
    public int Liveliness { get; set; } = 1;

    /// <summary>判定时最多附带给 AI 的图片张数，0=不带图。</summary>
    public int MaxAttachedImages { get; set; } = ProactiveChatConfig.DefaultMaxAttachedImages;

    /// <summary>判定时可参考的背景历史条数（与 AI 面板的上下文条数分开）。</summary>
    public int HistoryMaxMessages { get; set; } = ProactiveChatConfig.DefaultHistoryMaxMessages;

    /// <summary>单次判定调用的输出上限（与 AI 面板的 max_tokens 分开）。</summary>
    public int EvalMaxTokens { get; set; } = ProactiveChatConfig.DefaultEvalMaxTokens;

    /// <summary>是否允许择机发言在改写阶段联网核实，见 <see cref="ProactiveChatConfig.WebSearchEnabled"/>。</summary>
    public bool WebSearchEnabled { get; set; }

    public bool AllowActiveChannel { get; set; } = true;
    public int ActivePerGroupPerMinute { get; set; } = 3;
    public int ActivePerGroupPerDay { get; set; } = 20;
    public int ActivePerAccountPerMinute { get; set; } = 20;

    /// <summary>是否把群记忆注入择机判定（群级可覆盖），见 <see cref="ProactiveChatConfig.MemoryInjectionEnabled"/>。</summary>
    public bool MemoryInjectionEnabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; }
}
