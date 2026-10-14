namespace Mutter.Core.Models;

/// <summary>主动消息配额与授权状态</summary>
public class ActiveMessageQuotaStatus
{
    /// <summary>自然月累计（历史口径，仅供面板展示）。</summary>
    public int UsedThisMonth { get; set; }

    /// <summary>今日已发条数（内存计数器，进程重启后归零）。</summary>
    public int UsedToday { get; set; }

    /// <summary>今日上限（自设，不超官方 1000 条/群/日）。</summary>
    public int Quota { get; set; }

    /// <summary>最近一分钟内已发条数。</summary>
    public int SentLastMinute { get; set; }

    /// <summary>每分钟上限（自设，不超官方 20 条/群/分钟）。</summary>
    public int MinuteQuota { get; set; }

    public bool Rejected { get; set; }
    public DateTime? LastSentAt { get; set; }
}

/// <summary>主动消息发送结果</summary>
public class ActiveMessageSendResult
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    /// <summary>发送成功时平台分配的消息 id（可用于撤回），解析失败为 null。</summary>
    public string? MessageId { get; set; }
}
