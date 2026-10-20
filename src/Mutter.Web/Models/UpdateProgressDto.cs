namespace Mutter.Web.Models;

public class UpdateProgressDto
{
    public bool Active { get; set; }
    public int Percent { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string Phase { get; set; } = "idle";
    public string? Version { get; set; }
    public string? AssetName { get; set; }
    public string Kind { get; set; } = "full";
    public string? Error { get; set; }
    public bool Resumable { get; set; }
    public bool Staged { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
