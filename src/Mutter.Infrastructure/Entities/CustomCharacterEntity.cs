namespace Mutter.Infrastructure.Entities;

/// <summary>
/// 用户自定义人设（角色设定），与内置人设合并后供全局/群级配置选用。
/// </summary>
public class CustomCharacterEntity
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
