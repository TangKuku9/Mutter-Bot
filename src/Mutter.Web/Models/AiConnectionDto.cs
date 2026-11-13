using Mutter.Core.Models.Config;

namespace Mutter.Web.Models;

public class AiConnectionInputDto
{
    public string? Id { get; set; }
    /// <summary>编辑时回传读取到的时间戳，用于拒绝过期草稿；创建/探测不需要。</summary>
    public DateTime? UpdatedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>省略时保留连接原模板，避免部分更新把它重置成 custom。</summary>
    public string? TemplateId { get; set; }
    /// <summary>省略时保留连接原协议。</summary>
    public string? Protocol { get; set; }
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>省略时保留连接原超时。</summary>
    public int? TimeoutSeconds { get; set; }
    public string DefaultModel { get; set; } = string.Empty;
    public List<AiConnectionModel> Models { get; set; } = [];
}

public sealed class AiConnectionDto : AiConnectionInputDto
{
    public string Revision { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public bool IsCurrent { get; set; }
    public int PresetCount { get; set; }
}
