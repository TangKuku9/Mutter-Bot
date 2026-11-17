using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mutter.Web.Services;

/// <summary>
/// 仅用于本机维护的短时初始化码。写在站点 logs 目录下、仅服务账户可读，
/// 绝不通过 API 返回。无会话改密仍须知道当前口令；初始化码只用于把
/// “看起来像回环”的请求与真正能读服务器维护文件的本机操作区分开，
/// 阻止不加转发头的同机反向代理冒充直接本机连接。
/// </summary>
public sealed class PanelBootstrapStore(IHostEnvironment environment, ILogger<PanelBootstrapStore> logger,
    TimeProvider? clock = null) : IHostedService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public string FilePath { get; } = Path.Combine(environment.ContentRootPath, "logs", "panel-bootstrap.token");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (_sync) EnsureFreshToken();
            logger.LogInformation("面板本机维护初始化码位于 {Path}，有效期 {Minutes} 分钟；成功改密后失效。",
                FilePath, (int)TokenLifetime.TotalMinutes);
        }
        catch (Exception ex)
        {
            // 已登录的强口令面板不因维护码不可写而停止；无会话初始化会继续失败关闭。
            logger.LogWarning(ex, "无法生成面板本机维护初始化码；无会话初始化将保持关闭");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { Consume(); }
        catch (Exception ex) { logger.LogWarning(ex, "停止时未能删除面板维护初始化码文件"); }
        return Task.CompletedTask;
    }

    /// <summary>校验候选初始化码；文件系统异常时按不通过处理，失败关闭。</summary>
    public bool TryValidate(string? candidate)
    {
        lock (_sync)
        {
            try
            {
                EnsureFreshToken();
                if (candidate?.Trim().ToLowerInvariant() is not { Length: 32 } supplied
                    || !supplied.All(Uri.IsHexDigit) || _token is null) return false;
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied),
                    Encoding.ASCII.GetBytes(_token));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "读取面板维护初始化码失败，按不通过处理");
                return false;
            }
        }
    }

    /// <summary>使当前初始化码失效并删除维护文件；随后的校验会生成新码。</summary>
    public void Consume()
    {
        lock (_sync)
        {
            _token = null;
            _expiresAt = default;
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }

    private void EnsureFreshToken()
    {
        var now = _clock.GetUtcNow();
        if (_token is not null && _expiresAt > now && File.Exists(FilePath)) return;
        var nowUtc = now.UtcDateTime;
        if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath).Add(TokenLifetime) > nowUtc)
        {
            var saved = File.ReadAllText(FilePath).Trim();
            if (saved.Length == 32 && saved.All(Uri.IsHexDigit))
            {
                _token = saved.ToLowerInvariant();
                _expiresAt = new DateTimeOffset(File.GetLastWriteTimeUtc(FilePath), TimeSpan.Zero).Add(TokenLifetime);
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(FilePath, options))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(_token);
        }
        _expiresAt = now.Add(TokenLifetime);
    }
}
