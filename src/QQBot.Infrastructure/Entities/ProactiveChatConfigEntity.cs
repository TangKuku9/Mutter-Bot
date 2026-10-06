namespace QQBot.Infrastructure.Entities;

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
    public bool AllowActiveChannel { get; set; } = true;
    public int ActivePerGroupPerMinute { get; set; } = 3;
    public int ActivePerGroupPerDay { get; set; } = 20;
    public int ActivePerAccountPerMinute { get; set; } = 20;
    public DateTime UpdatedAt { get; set; }
}
