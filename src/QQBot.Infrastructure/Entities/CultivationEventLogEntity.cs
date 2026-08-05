namespace QQBot.Infrastructure.Entities;

/// <summary>
/// 事件日志/审计。索引 (GroupId, MemberOpenId, CreatedAt) + (GroupId, EventType, CreatedAt)。
/// </summary>
public class CultivationEventLogEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string? MemberOpenId { get; set; }          // 可空（群级事件）
    public string EventType { get; set; } = string.Empty; // idle/breakthrough/alchemy/event/ai_referee/daily
    public string DetailJson { get; set; } = string.Empty;
    public string? AiRawOutput { get; set; }           // AI原始输出（审计用，可空）
    public string AppliedChangesJson { get; set; } = string.Empty; // 引擎实际应用的变更
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
