namespace Mutter.Infrastructure.Entities;

public sealed class ActiveMessageAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; }
}
