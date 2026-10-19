namespace Mutter.Web.Models;

public class UpdateStageResultDto
{
    public string Version { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public bool InProgress { get; set; }
    public bool Staged { get; set; }
}
