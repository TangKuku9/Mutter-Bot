namespace Mutter.Web.Models;

public class UpdateInfoDto
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public string ReleaseUrl { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
    public string? Error { get; set; }

    public bool CanSelfUpdate { get; set; }
    public string? SelfUpdateReason { get; set; }
    public string? AssetName { get; set; }
    public long AssetSize { get; set; }

    /// <summary>是否已有下载完成、等待用户确认安装的更新包。</summary>
    public bool Staged { get; set; }
    public string? StagedVersion { get; set; }
}
