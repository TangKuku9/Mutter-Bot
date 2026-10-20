namespace Mutter.Web.Models;

public class UpdateSettingsDto
{
    public string Mode { get; set; } = UpdateSettings.ModeAuto;
    public string Source { get; set; } = UpdateSettings.SourceGitHub;
    public string ManifestUrl { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>只读：官方清单地址，供面板展示与“恢复官方源”。</summary>
    public string OfficialManifestUrl { get; set; } = string.Empty;

    public UpdateSettings ToSettings() => new()
    {
        Mode = Mode,
        Source = Source,
        ManifestUrl = ManifestUrl,
        PublicKey = PublicKey
    };

    public static UpdateSettingsDto FromSettings(UpdateSettings settings, string officialManifestUrl) => new()
    {
        Mode = settings.Mode,
        Source = settings.Source,
        ManifestUrl = settings.ManifestUrl,
        PublicKey = settings.PublicKey,
        OfficialManifestUrl = officialManifestUrl
    };
}

public class UpdateSourceTestDto
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
    public int LatencyMs { get; set; }
    public string SignatureStatus { get; set; } = "not_configured";
    public int AssetCount { get; set; }
    public long AppAssetSize { get; set; }
    public long FullAssetSize { get; set; }
}
