namespace Mutter.Web.Models;

/// <summary>
/// 更新偏好。保存在安装目录的 update-settings.json，更新程序会把它列为受保护文件。
/// 它不是密钥：公钥可以公开，私钥只在发布流水线的 Secret 里。
/// </summary>
public sealed class UpdateSettings
{
    public const string ModeAuto = "auto";
    public const string ModeFull = "full";
    public const string ModeCheck = "check";

    public const string SourceGitHub = "github";
    public const string SourceMirror = "mirror";
    public const string SourceCustom = "custom";

    /// <summary>
    /// 官方发布清单的公钥。私钥只存在于 GitHub Actions Secret UPDATE_SIGNING_KEY；
    /// 用户也可以在面板里改成自己的公钥（例如使用自建镜像）。
    /// </summary>
    public const string DefaultPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGGu4MO77xKlKL9kmjXNBVughwEpSfRZtxlEVE+0j3EfiL7usXJUF2Z7lkkvJpaMiiMcbd0eBNW3EfuA5qD15YA==";

    public string Mode { get; set; } = ModeAuto;
    public string Source { get; set; } = SourceGitHub;
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>清单签名公钥（PEM 或 Base64 SPKI）。镜像/自定义源必须提供。</summary>
    public string PublicKey { get; set; } = DefaultPublicKey;

    public UpdateSettings Clone() => (UpdateSettings)MemberwiseClone();

    public void Normalize()
    {
        Mode = (Mode ?? string.Empty).Trim().ToLowerInvariant();
        Source = (Source ?? string.Empty).Trim().ToLowerInvariant();
        ManifestUrl = (ManifestUrl ?? string.Empty).Trim();
        PublicKey = (PublicKey ?? string.Empty).Trim();

        if (Mode.Length == 0) Mode = ModeAuto;
        if (Source.Length == 0) Source = SourceGitHub;
        if (Source == SourceGitHub) ManifestUrl = string.Empty;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (Mode is not (ModeAuto or ModeFull or ModeCheck))
        {
            error = "更新方式只能是 auto / full / check";
            return false;
        }

        if (Source is not (SourceGitHub or SourceMirror or SourceCustom))
        {
            error = "更新源只能是 github / mirror / custom";
            return false;
        }

        if (Source != SourceGitHub)
        {
            if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "镜像/自定义更新源需要填写合法的 HTTPS 清单地址";
                return false;
            }

            if (ManifestUrl.Length > 2048)
            {
                error = "更新清单地址过长";
                return false;
            }

            if (string.IsNullOrWhiteSpace(PublicKey))
            {
                error = "镜像/自定义更新源必须填写清单签名公钥";
                return false;
            }
        }

        if (PublicKey.Length > 8192)
        {
            error = "公钥长度异常";
            return false;
        }

        return true;
    }
}
