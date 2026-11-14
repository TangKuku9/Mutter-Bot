namespace Mutter.Infrastructure.Entities;

public sealed class GroupMemoryItemEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = "";
    public string? SubjectUserId { get; set; }
    public string Content { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
    public string SourceRowIdsJson { get; set; } = "[]";
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public long LastSourceRowId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>配置与处理游标共存，启用和建立边界必须在同一事务中完成。</summary>
public sealed class GroupMemoryLearningStateEntity
{
    public string GroupId { get; set; } = "";
    public bool Enabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public long Revision { get; set; }
    public long LastProcessedRowId { get; set; }
    public DateTime LastCompletedAt { get; set; }
    public DateTime LastScheduleCheckAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string QuotaDay { get; set; } = "";
    public int CallsToday { get; set; }
    public string LastOutcome { get; set; } = "not_enabled";
    public string RecentRunsJson { get; set; } = "[]";
}
