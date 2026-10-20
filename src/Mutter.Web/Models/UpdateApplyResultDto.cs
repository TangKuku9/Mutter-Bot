namespace Mutter.Web.Models;

public class UpdateApplyResultDto
{
    public string Version { get; set; } = string.Empty;
    public string Kind { get; set; } = "full";
    public bool Restarting { get; set; }
}
