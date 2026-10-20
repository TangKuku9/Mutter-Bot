using System.Text;
using System.Text.Json;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

/// <summary>
/// update-settings.json 的读写。文件放在安装目录根部，更新程序把它列为受保护文件，
/// 因此完整包/应用层包都不会覆盖它。
/// </summary>
public sealed class UpdateSettingsStore
{
    public const string FileName = "update-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<UpdateSettingsStore> _logger;
    private readonly object _sync = new();
    private readonly string _path;
    private UpdateSettings _current;
    private bool _loaded;

    public UpdateSettingsStore(ILogger<UpdateSettingsStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(AppContext.BaseDirectory, FileName);
        _current = new UpdateSettings();
    }

    public string FilePath => _path;

    public UpdateSettings Get()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            return _current.Clone();
        }
    }

    public bool TrySave(UpdateSettings settings, out string error)
    {
        var next = (settings ?? new UpdateSettings()).Clone();
        next.Normalize();
        if (!next.Validate(out error))
            return false;

        if (!string.IsNullOrWhiteSpace(next.PublicKey) &&
            !UpdateSignature.TryValidatePublicKey(next.PublicKey, out error))
            return false;

        lock (_sync)
        {
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(next, JsonOptions),
                    new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
                _current = next;
                _loaded = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"保存更新设置失败：{ex.Message}";
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
            var loaded = JsonSerializer.Deserialize<UpdateSettings>(
                File.ReadAllText(_path), JsonOptions);
            if (loaded is null)
                return;

            loaded.Normalize();
            if (!loaded.Validate(out var error))
            {
                _logger.LogWarning("更新设置无效，按默认值处理：{Error}", error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(loaded.PublicKey) &&
                !UpdateSignature.TryValidatePublicKey(loaded.PublicKey, out var keyError))
            {
                _logger.LogWarning("更新设置里的公钥无效，按默认值处理：{Error}", keyError);
                return;
            }

            _current = loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取更新设置失败，按默认值处理");
        }
    }
}
