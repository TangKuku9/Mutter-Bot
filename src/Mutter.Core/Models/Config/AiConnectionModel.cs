namespace Mutter.Core.Models.Config;

/// <summary>模型 ID 在连接内唯一；可选限额只表示已知信息。</summary>
public sealed class AiConnectionModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsManual { get; set; } = true;
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
}
