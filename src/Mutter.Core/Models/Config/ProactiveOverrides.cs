using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// 未知识别键（未来新增的分群参数）原样保留，避免旧前端/旧后端保存时把新字段抹掉。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// 这些是全局/账号级限制键，不属于可分群覆盖；解码与保存时统一丢弃，
    /// 避免被误当成生效值持久化。其余未知识别键照常透传。
    /// </summary>
    private static readonly HashSet<string> GlobalOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "enabled", "maxGlobalPerDay", "activePerAccountPerMinute", "updatedAt",
        "ceilingGroupPerMinute", "ceilingGroupPerDay", "ceilingAccountPerMinute", "ceilingAttachedImages"
    };

    public bool HasValues() => WindowSeconds.HasValue || WindowMaxMessages.HasValue || CooldownMinutes.HasValue
        || MaxPerGroupPerDay.HasValue || QuietStartHour.HasValue || QuietEndHour.HasValue || Liveliness.HasValue
        || MaxAttachedImages.HasValue || HistoryMaxMessages.HasValue || EvalMaxTokens.HasValue
        || WebSearchEnabled.HasValue || MemoryInjectionEnabled.HasValue || AllowActiveChannel.HasValue
        || ActivePerGroupPerMinute.HasValue || ActivePerGroupPerDay.HasValue
        || Extra is { Count: > 0 };

    public bool HasCompleteQuietHours() => QuietStartHour.HasValue == QuietEndHour.HasValue;

    public ProactiveOverrides Normalize()
    {
        // 半个静默区间（旧库/损坏数据）在读路径直接清空：两个键是一个整体，单独一个没有意义。
        if (QuietStartHour.HasValue != QuietEndHour.HasValue)
        {
            QuietStartHour = null;
            QuietEndHour = null;
        }
        DropGlobalOnlyExtras();
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
        // Extra 是未知识别键，ApplyTo 无法解释，原样保留在覆盖对象里等待 Encode 回写。
    }

    private static int? Clamp(int? value, int min, int max) => value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    /// <summary>从 Extra 移除全局/账号级限制键；其余未知识别键保持不动。</summary>
    private void DropGlobalOnlyExtras()
    {
        if (Extra is not { Count: > 0 } extra) return;
        // Extra 由 JSON 反序列化建立，默认使用区分大小写的序数比较；这里按不区分大小写匹配，
        // 防止 "Enabled"/"MAXGLOBALPERDAY" 这类变体绕过全局键禁用规则。
        foreach (var key in extra.Keys.Where(GlobalOnlyKeys.Contains).ToList()) extra.Remove(key);
        Extra = extra.Count > 0 ? extra : null;
    }
}
