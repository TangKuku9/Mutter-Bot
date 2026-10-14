namespace Mutter.Infrastructure.Entities;

public class KnownGroupEntity
{
    public string GroupOpenId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
