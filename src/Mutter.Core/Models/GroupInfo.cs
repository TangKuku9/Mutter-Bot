namespace Mutter.Core.Models;

/// <summary>
/// 群信息 DTO（Core 层使用）
/// </summary>
public class GroupInfo
{
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
