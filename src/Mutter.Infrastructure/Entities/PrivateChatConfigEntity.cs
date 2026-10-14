namespace Mutter.Infrastructure.Entities;

/// <summary>
/// 私聊白名单配置：仅记录在案且启用的用户才会被响应
/// </summary>
public class PrivateChatConfigEntity
{
    public string UserId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    /// <summary>管理员可在私聊中使用 / 开头的内置命令</summary>
    public bool IsAdmin { get; set; }
}
