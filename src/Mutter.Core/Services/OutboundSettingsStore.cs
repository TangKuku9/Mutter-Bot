using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mutter.Core.Services;

/// <summary>
/// 出站网络设置的持久化：安装目录的 outbound-settings.json。
/// 与更新设置相同，更新程序把它列为受保护文件，完整包/应用层包都不会覆盖用户选择。
/// </summary>
public sealed class OutboundSettingsStore
{
    public const string FileName = "outbound-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<OutboundSettingsStore> _logger;
    private readonly object _sync = new();
    private readonly string _path;
    private bool _loaded;
    private bool _allowSystemProxy;

    public OutboundSettingsStore(ILogger<OutboundSettingsStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(AppContext.BaseDirectory, FileName);
    }

    public string FilePath => _path;

    /// <summary>默认关闭：直连并校验解析地址，代理会自行解析目标而绕过该保证。</summary>
    public bool AllowSystemProxy
    {
        get
        {
            lock (_sync)
            {
                EnsureLoadedLocked();
                return _allowSystemProxy;
            }
        }
    }

    public bool TrySave(bool allowSystemProxy, out string error)
    {
        error = string.Empty;
        lock (_sync)
        {
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(new Settings(allowSystemProxy), JsonOptions),
                    new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
                _allowSystemProxy = allowSystemProxy;
                _loaded = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"保存网络设置失败：{ex.Message}";
                return false;
            }
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded)
            return;
        _loaded = true;

        if (!File.Exists(_path))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path), JsonOptions);
            if (loaded is not null)
                _allowSystemProxy = loaded.AllowSystemProxy;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件损坏时保持默认直连，不让启动失败。
            _logger.LogWarning(ex, "读取网络设置失败，按默认直连处理");
        }
    }

    private sealed record Settings(bool AllowSystemProxy);
}
