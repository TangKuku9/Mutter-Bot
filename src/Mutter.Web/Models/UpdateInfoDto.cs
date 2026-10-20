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

    /// <summary>本次推荐/将使用的资产（应用层或完整包）。</summary>
    public string? AssetName { get; set; }
    public long AssetSize { get; set; }

    /// <summary>full=完整包；app=应用层包。</summary>
    public string AssetKind { get; set; } = "full";

    public string? AppAssetName { get; set; }
    public long AppAssetSize { get; set; }
    public string? FullAssetName { get; set; }
    public long FullAssetSize { get; set; }
    public string RecommendedKind { get; set; } = "full";

    /// <summary>最新版有应用层包，但本机运行时版本不匹配，只能使用完整包。</summary>
    public bool RuntimeChanged { get; set; }

    /// <summary>本次检查使用的更新源：github / mirror / custom。</summary>
    public string Source { get; set; } = UpdateSettings.SourceGitHub;

    /// <summary>valid / missing / invalid / not_configured / legacy。</summary>
    public string SignatureStatus { get; set; } = "not_configured";

    public bool ManifestSigned { get; set; }

    /// <summary>是否已有下载完成、等待用户确认安装的更新包。</summary>
    public bool Staged { get; set; }
    public string? StagedVersion { get; set; }
    public string StagedKind { get; set; } = string.Empty;
}
