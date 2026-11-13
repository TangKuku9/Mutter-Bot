namespace Mutter.Infrastructure.Entities;

public sealed class AiProviderConnectionEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string TemplateId { get; set; } = "custom";
    public string Protocol { get; set; } = "openai";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string HeadersJson { get; set; } = "{}";
    public int TimeoutSeconds { get; set; } = 60;
    public string DefaultModel { get; set; } = string.Empty;
    public string ModelsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
