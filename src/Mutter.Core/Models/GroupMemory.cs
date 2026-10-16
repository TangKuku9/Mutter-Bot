namespace Mutter.Core.Models;

public sealed class GroupMemory
{
    public string GroupId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
}
