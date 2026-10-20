namespace Mutter.Infrastructure.Entities;

/// <summary>
/// 用户自定义的 AI 配置预设：整套可写配置的快照。
/// Key 单独存一列而不是塞进 ConfigJson，避免列表/详情接口因为序列化整份配置而意外泄露明文。
/// </summary>
public class AiConfigPresetEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";

    /// <summary>空 = 该预设未保存密钥，应用时沿用当前配置的 Key。</summary>
    public string ApiKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
