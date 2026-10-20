using System.Text.Json;

namespace Mutter.Web.Models;

/// <summary>
/// Release 资产清单。它替代“按固定文件名猜资产”的旧逻辑：
/// 发布时同时产出完整包、应用层包，运行时按运行时版本选择最小的可用包。
/// </summary>
public sealed class UpdateManifest
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Version { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public List<UpdateAsset> Assets { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static UpdateManifest? TryParse(byte[] bytes, out string error)
    {
        error = string.Empty;
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, JsonOptions);
            if (manifest is null)
            {
                error = "更新清单为空";
                return null;
            }

            if (!manifest.Validate(out error))
                return null;

            return manifest;
        }
        catch (JsonException ex)
        {
            error = $"更新清单不是合法 JSON：{ex.Message}";
            return null;
        }
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (Schema != CurrentSchema)
        {
            error = $"不支持的更新清单版本：{Schema}";
            return false;
        }

        if (!System.Version.TryParse(Version.Trim().TrimStart('v', 'V'), out _))
        {
            error = $"更新清单版本号无效：{Version}";
            return false;
        }

        if (Assets.Count is < 1 or > 64)
        {
            error = "更新清单资产数量异常";
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            if (!asset.Validate(out error))
                return false;
            if (!names.Add(asset.Name))
            {
                error = $"更新清单含重复资产：{asset.Name}";
                return false;
            }
        }

        return true;
    }
}

public sealed class UpdateAsset
{
    public string Name { get; set; } = string.Empty;

    /// <summary>full=完整包；app=应用层包。旧版本客户端只认完整包的文件名，因此两者并存。</summary>
    public string Kind { get; set; } = "full";

    public string Rid { get; set; } = string.Empty;
    public string Flavor { get; set; } = string.Empty;

    /// <summary>应用层包对应的 .NET 运行时版本；框架依赖包为空。</summary>
    public string? RuntimeVersion { get; set; }

    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>可以是相对清单地址的文件名，也可以是绝对 HTTPS 地址。</summary>
    public string Url { get; set; } = string.Empty;

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Name) ||
            Name.Contains('/') || Name.Contains('\\') ||
            Name is "." or "..")
        {
            error = "更新清单包含非法资产名";
            return false;
        }

        if (Kind is not ("full" or "app"))
        {
            error = $"未知的更新包类型：{Kind}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Rid) || string.IsNullOrWhiteSpace(Flavor))
        {
            error = $"更新包 {Name} 缺少平台信息";
            return false;
        }

        if (Kind == "app" &&
            Flavor == "self-contained" &&
            string.IsNullOrWhiteSpace(RuntimeVersion))
        {
            error = $"应用层包 {Name} 缺少运行时版本";
            return false;
        }

        if (Size is <= 0 or > 512L * 1024 * 1024)
        {
            error = $"更新包 {Name} 大小异常";
            return false;
        }

        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            error = $"更新包 {Name} 的 SHA256 无效";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            error = $"更新包 {Name} 缺少下载地址";
            return false;
        }

        return true;
    }
}
