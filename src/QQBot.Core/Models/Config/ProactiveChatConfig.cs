namespace QQBot.Core.Models.Config;

/// <summary>
/// 择机主动发言配置（全局单行，只由管理面板读写）。
/// Enabled 默认 false：不开启时观察链路完全不接管消息，行为与旧版一致。
/// </summary>
public class ProactiveChatConfig
{
    /// <summary>官方频控硬上限：单群主动消息 20 条/分钟。</summary>
    public const int CeilingGroupPerMinute = 20;

    /// <summary>官方频控硬上限：单群主动消息 1000 条/日。</summary>
    public const int CeilingGroupPerDay = 1000;

    /// <summary>官方频控硬上限：机器人账号维度 60 条/分钟（已认证）。</summary>
    public const int CeilingAccountPerMinute = 60;

    public bool Enabled { get; set; }

    /// <summary>攒批窗口长度（秒）。窗口内消息攒够一起问 AI，而不是逐条调用。</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>单个窗口最多带入的消息条数（超出取最近的）。</summary>
    public int WindowMaxMessages { get; set; } = 12;

    /// <summary>同群两次插话的最小间隔（分钟）。</summary>
    public int CooldownMinutes { get; set; } = 45;

    /// <summary>同群每日最多插话条数。</summary>
    public int MaxPerGroupPerDay { get; set; } = 3;

    /// <summary>全部群每日合计最多插话条数。</summary>
    public int MaxGlobalPerDay { get; set; } = 12;

    /// <summary>静默起始小时（北京时间，含）。与结束小时相等表示不设静默时段。</summary>
    public int QuietStartHour { get; set; } = 23;

    /// <summary>静默结束小时（北京时间，不含）。</summary>
    public int QuietEndHour { get; set; } = 8;

    /// <summary>
    /// 发言倾向：0=克制（宁可不说话）、1=正常、2=主动（有机会就接）。
    /// 它改的是判定标准本身，不是阈值 —— 之前的置信度旋钮就是死旋钮，这次换成模型看得懂的话。
    /// </summary>
    public int Liveliness { get; set; } = LivelinessNormal;

    public const int LivelinessRestrained = 0;
    public const int LivelinessNormal = 1;
    public const int LivelinessEager = 2;

    /// <summary>被动回复槽位过期后，是否允许改走 msg_id=null 的真主动消息。</summary>
    public bool AllowActiveChannel { get; set; } = true;

    /// <summary>真主动消息：单群每分钟上限（自行收紧，不超官方 20）。</summary>
    public int ActivePerGroupPerMinute { get; set; } = 3;

    /// <summary>真主动消息：单群每日上限（自行收紧，不超官方 1000）。</summary>
    public int ActivePerGroupPerDay { get; set; } = 20;

    /// <summary>真主动消息：账号维度每分钟上限（不超官方 60）。</summary>
    public int ActivePerAccountPerMinute { get; set; } = 20;

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 把面板传入的值收敛到可用区间：下限保 1（0 等于静默熔断，交给 Enabled），
    /// 上限锁死官方频控，避免误填把机器人推到 QQ 侧限流封禁。
    /// </summary>
    public ProactiveChatConfig Normalize()
    {
        WindowSeconds = Clamp(WindowSeconds, 15, 600);
        WindowMaxMessages = Clamp(WindowMaxMessages, 2, 50);
        CooldownMinutes = Clamp(CooldownMinutes, 1, 24 * 60);
        MaxPerGroupPerDay = Clamp(MaxPerGroupPerDay, 1, 200);
        MaxGlobalPerDay = Clamp(MaxGlobalPerDay, 1, 500);
        QuietStartHour = ClampHour(QuietStartHour);
        QuietEndHour = ClampHour(QuietEndHour);
        Liveliness = Clamp(Liveliness, LivelinessRestrained, LivelinessEager);
        ActivePerGroupPerMinute = Clamp(ActivePerGroupPerMinute, 1, CeilingGroupPerMinute);
        ActivePerGroupPerDay = Clamp(ActivePerGroupPerDay, 1, CeilingGroupPerDay);
        ActivePerAccountPerMinute = Clamp(ActivePerAccountPerMinute, 1, CeilingAccountPerMinute);
        return this;
    }

    /// <summary>按北京时间判断当前是否处于静默时段（跨零点区间同样成立）。</summary>
    public bool IsQuietNow(DateTime utcNow)
    {
        if (QuietStartHour == QuietEndHour) return false;
        var hour = utcNow.AddHours(8).Hour;
        return QuietStartHour < QuietEndHour
            ? hour >= QuietStartHour && hour < QuietEndHour
            : hour >= QuietStartHour || hour < QuietEndHour;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private static int ClampHour(int value) => value is < 0 or > 23 ? 0 : value;
}
