using Mutter.Core.Models.Config;

namespace Mutter.Web.Models;

public class AiConnectionInputDto
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TemplateId { get; set; } = "custom";
    public string Protocol { get; set; } = "openai";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
    public string DefaultModel { get; set; } = string.Empty;
    public List<AiConnectionModel> Models { get; set; } = [];
}

public sealed class AiConnectionDto : AiConnectionInputDto
{
    public string Revision { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public bool IsCurrent { get; set; }
    public int PresetCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}
