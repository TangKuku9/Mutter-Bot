namespace Mutter.Infrastructure.Entities;

public sealed class ProactivePresetEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ValuesJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
}
