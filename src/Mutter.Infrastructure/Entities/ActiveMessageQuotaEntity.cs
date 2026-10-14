namespace Mutter.Infrastructure.Entities;

public class ActiveMessageQuotaEntity
{
    public string Key { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Month { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Rejected { get; set; }
    public DateTime? LastSentAt { get; set; }
}
