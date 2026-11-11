namespace Mutter.Core.Models.Config;

/// <summary>仅包含可分群覆盖的参数；null 表示继承，不包含全局开关、总日限和账号频控。</summary>
public sealed class ProactiveOverrides
{
    public int? WindowSeconds { get; set; }
    public int? WindowMaxMessages { get; set; }
    public int? CooldownMinutes { get; set; }
    public int? MaxPerGroupPerDay { get; set; }
    public int? QuietStartHour { get; set; }
    public int? QuietEndHour { get; set; }
    public int? Liveliness { get; set; }
    public int? MaxAttachedImages { get; set; }
    public int? HistoryMaxMessages { get; set; }
    public int? EvalMaxTokens { get; set; }
    public bool? WebSearchEnabled { get; set; }
    public bool? MemoryInjectionEnabled { get; set; }
    public bool? AllowActiveChannel { get; set; }
    public int? ActivePerGroupPerMinute { get; set; }
    public int? ActivePerGroupPerDay { get; set; }

    public bool HasValues() => WindowSeconds.HasValue || WindowMaxMessages.HasValue || CooldownMinutes.HasValue
        || MaxPerGroupPerDay.HasValue || QuietStartHour.HasValue || QuietEndHour.HasValue || Liveliness.HasValue
        || MaxAttachedImages.HasValue || HistoryMaxMessages.HasValue || EvalMaxTokens.HasValue
        || WebSearchEnabled.HasValue || MemoryInjectionEnabled.HasValue || AllowActiveChannel.HasValue
        || ActivePerGroupPerMinute.HasValue || ActivePerGroupPerDay.HasValue;

    public bool HasCompleteQuietHours() => QuietStartHour.HasValue == QuietEndHour.HasValue;

    public ProactiveOverrides Normalize()
    {
        WindowSeconds = Clamp(WindowSeconds, 15, 600);
        WindowMaxMessages = Clamp(WindowMaxMessages, 2, 50);
        CooldownMinutes = Clamp(CooldownMinutes, 1, 1440);
        MaxPerGroupPerDay = Clamp(MaxPerGroupPerDay, 1, 200);
        QuietStartHour = Clamp(QuietStartHour, 0, 23);
        QuietEndHour = Clamp(QuietEndHour, 0, 23);
        Liveliness = Clamp(Liveliness, 0, 2);
        MaxAttachedImages = Clamp(MaxAttachedImages, 0, ProactiveChatConfig.CeilingAttachedImages);
        HistoryMaxMessages = Clamp(HistoryMaxMessages, 0, 50);
        EvalMaxTokens = Clamp(EvalMaxTokens, 64, 8192);
        ActivePerGroupPerMinute = Clamp(ActivePerGroupPerMinute, 1, ProactiveChatConfig.CeilingGroupPerMinute);
        ActivePerGroupPerDay = Clamp(ActivePerGroupPerDay, 1, ProactiveChatConfig.CeilingGroupPerDay);
        return this;
    }

    internal void ApplyTo(ProactiveChatConfig value)
    {
        value.WindowSeconds = WindowSeconds ?? value.WindowSeconds;
        value.WindowMaxMessages = WindowMaxMessages ?? value.WindowMaxMessages;
        value.CooldownMinutes = CooldownMinutes ?? value.CooldownMinutes;
        value.MaxPerGroupPerDay = MaxPerGroupPerDay ?? value.MaxPerGroupPerDay;
        // 静默区间是一个整体，旧库或损坏数据的半个区间不能改变全局时段。
        if (QuietStartHour.HasValue && QuietEndHour.HasValue)
        {
            value.QuietStartHour = QuietStartHour.Value;
            value.QuietEndHour = QuietEndHour.Value;
        }
        value.Liveliness = Liveliness ?? value.Liveliness;
        value.MaxAttachedImages = MaxAttachedImages ?? value.MaxAttachedImages;
        value.HistoryMaxMessages = HistoryMaxMessages ?? value.HistoryMaxMessages;
        value.EvalMaxTokens = EvalMaxTokens ?? value.EvalMaxTokens;
        value.WebSearchEnabled = WebSearchEnabled ?? value.WebSearchEnabled;
        value.MemoryInjectionEnabled = MemoryInjectionEnabled ?? value.MemoryInjectionEnabled;
        value.AllowActiveChannel = AllowActiveChannel ?? value.AllowActiveChannel;
        value.ActivePerGroupPerMinute = ActivePerGroupPerMinute ?? value.ActivePerGroupPerMinute;
        value.ActivePerGroupPerDay = ActivePerGroupPerDay ?? value.ActivePerGroupPerDay;
    }

    private static int? Clamp(int? value, int min, int max) => value.HasValue ? Math.Clamp(value.Value, min, max) : null;
}
