namespace Mutter.Infrastructure.Entities;

public class KnownUserEntity
{
    public string UserOpenId { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
