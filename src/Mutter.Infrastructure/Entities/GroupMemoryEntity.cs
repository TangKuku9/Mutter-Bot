namespace Mutter.Infrastructure.Entities;

public sealed class GroupMemoryEntity
{
    public string GroupId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
}
