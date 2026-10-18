namespace Mutter.Web.Models;

public class UpdateProgressDto
{
    public bool Active { get; set; }
    public int Percent { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string Phase { get; set; } = "idle";
}
