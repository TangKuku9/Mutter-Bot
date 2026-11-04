namespace Mutter.Infrastructure.Entities;

/// <summary>已清理明细的累计计数；只保留汇总，不保留提示词、用户或群标识。</summary>
public sealed class AiUsageArchiveEntity
{
    public int Id { get; set; } = 1;
    public long CallCount { get; set; }
    public long FailedCount { get; set; }
    public long UnknownUsageCount { get; set; }
    public long LegacyCount { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
}
