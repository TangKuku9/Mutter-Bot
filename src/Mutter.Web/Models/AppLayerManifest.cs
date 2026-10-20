using System.Text.Json;

namespace Mutter.Web.Models;

/// <summary>
/// 应用层包内的文件清单。应用层包只替换程序文件，不包含 .NET 运行时；
/// 安装前逐文件校验，避免半包/错包把运行目录改坏。
/// </summary>
public sealed class AppLayerManifest
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Version { get; set; } = string.Empty;
    public string Rid { get; set; } = string.Empty;
    public string Flavor { get; set; } = string.Empty;
    public string? RuntimeVersion { get; set; }
    public List<AppLayerFile> Files { get; set; } = [];

    /// <summary>新版本不再包含、需要从安装目录删除的应用层文件（相对路径）。</summary>
    public List<string> Delete { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static AppLayerManifest? TryParse(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var manifest = JsonSerializer.Deserialize<AppLayerManifest>(
                File.ReadAllText(path), JsonOptions);
            if (manifest is null)
            {
                error = "应用层清单为空";
                return null;
            }

            if (!manifest.Validate(out error))
                return null;

            return manifest;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"读取应用层清单失败：{ex.Message}";
            return null;
        }
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (Schema != CurrentSchema)
        {
            error = $"不支持的应用层清单版本：{Schema}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            error = "应用层清单缺少版本号";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Rid) || string.IsNullOrWhiteSpace(Flavor))
        {
            error = "应用层清单缺少平台信息";
            return false;
        }

        if (Files.Count is < 1 or > 2000)
        {
            error = "应用层清单文件数量异常";
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!file.Validate(out error))
                return false;
            if (!paths.Add(file.Path))
            {
                error = $"应用层清单含重复文件：{file.Path}";
                return false;
            }
        }

        foreach (var path in Delete)
        {
            if (!IsSafeRelativePath(path, out error))
                return false;
            if (IsProtectedPath(path))
            {
                error = $"应用层清单试图删除受保护文件：{path}";
                return false;
            }
        }

        return true;
    }

    /// <summary>相对路径校验：禁止绝对路径、盘符、.. 和受保护文件。</summary>
    public static bool IsSafeRelativePath(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "应用层清单包含空路径";
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Contains('\0'))
        {
            error = $"应用层清单包含非法路径：{path}";
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
        {
            error = $"应用层清单包含非法路径：{path}";
            return false;
        }

        if (Path.IsPathRooted(normalized))
        {
            error = $"应用层清单包含绝对路径：{path}";
            return false;
        }

        if (IsProtectedPath(normalized))
        {
            error = $"应用层清单包含受保护文件：{path}";
            return false;
        }

        return true;
    }

    public static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var first = normalized.Split('/', 2)[0];
        var name = first;

        if (name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".update", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".backup", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("update-settings.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("app-layer.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cookies.txt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("mutter-bot.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("mutter-bot.db-wal", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("mutter-bot.db-shm", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Contains(".bak.", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AppLayerFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (!AppLayerManifest.IsSafeRelativePath(Path, out error))
            return false;

        if (Size is < 0 or > 512L * 1024 * 1024)
        {
            error = $"应用层清单文件大小异常：{Path}";
            return false;
        }

        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            error = $"应用层清单文件 SHA256 无效：{Path}";
            return false;
        }

        return true;
    }
}
