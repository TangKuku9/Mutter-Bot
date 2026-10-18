namespace Mutter.Web.Models;

public class UpdateInfoDto
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public string ReleaseUrl { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
    public string? Error { get; set; }
}
