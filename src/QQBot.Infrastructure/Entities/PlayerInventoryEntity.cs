namespace QQBot.Infrastructure.Entities;

/// <summary>
/// 玩家背包/丹药/材料。唯一索引 (GroupId, MemberOpenId, ItemId)。
/// </summary>
public class PlayerInventoryEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string MemberOpenId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;   // 物品ID（如 huiqi_dan）
    public string ItemType { get; set; } = string.Empty;  // dan/material/technique_fragment
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 0;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
