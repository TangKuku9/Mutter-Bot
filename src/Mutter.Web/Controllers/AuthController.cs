using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Mutter.Web.Services;

namespace Mutter.Web.Controllers;

/// <summary>
/// 管理面板登录认证与账户管理接口。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const string SessionCookieName = "mutter_panel_session";

    private readonly IConfiguration _config;
    private readonly PanelSessionStore _sessions;
    private readonly PanelLoginGuard _loginGuard;

    public AuthController(IConfiguration config, PanelSessionStore sessions, PanelLoginGuard loginGuard)
    {
        _config = config;
        _sessions = sessions;
        _loginGuard = loginGuard;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
        public string ConfirmPassword { get; set; } = "";
    }

    public class RevokeSessionRequest
    {
        public string TokenPrefix { get; set; } = "";
    }

    /// <summary>登录，校验账号密码后下发会话 Cookie。</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 登录失败限流：锁定期间直接拒绝
        var locked = _loginGuard.GetLockedRemaining(ip);
        if (locked is not null)
            return Unauthorized(new { error = $"失败次数过多，请 {(int)locked.Value.TotalMinutes +1} 分钟后再试" });

        var expectedUser = _config["PanelAuth:Username"] ?? "admin";
        var storedPass = _config["PanelAuth:Password"] ?? "admin";

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "请输入账号和密码" });

        if (!string.Equals(req.Username, expectedUser, StringComparison.Ordinal) ||
            !PanelPasswordHasher.Verify(storedPass, req.Password))
        {
            _loginGuard.RecordFailure(ip);
            return Unauthorized(new { error = "账号或密码错误" });
        }

        _loginGuard.RecordSuccess(ip);

        // 明文旧密码登录成功后自动升级为哈希存储
        if (!PanelPasswordHasher.IsHashed(storedPass))
        {
            try { WritePasswordToAppSettings(PanelPasswordHasher.Hash(req.Password)); }
            catch { /* 写文件失败不阻塞登录，下次再试 */ }
        }

        var token = _sessions.CreateSession(ip);
        var timeoutMinutes = _config.GetValue("PanelAuth:SessionTimeoutMinutes", 120);

        Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(timeoutMinutes)
        });

        return Ok(new { username = expectedUser });
    }

    /// <summary>注销当前会话。</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var token = Request.Cookies[SessionCookieName];
        _sessions.Remove(token);
        Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });
        return Ok();
    }

    /// <summary>查询当前会话状态。</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var token = Request.Cookies[SessionCookieName];
        var user = _config["PanelAuth:Username"] ?? "admin";
        if (_sessions.IsValid(token))
            return Ok(new { authenticated = true, username = user });

        return Ok(new { authenticated = false });
    }

    /// <summary>修改密码：校验当前密码后写入 appsettings.json（哈希存储），并清除所有已有会话强制重新登录。</summary>
    [HttpPost("change-password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var token = Request.Cookies[SessionCookieName];
        if (!_sessions.IsValid(token))
            return Unauthorized(new { error = "会话已过期，请重新登录" });

        var storedPass = _config["PanelAuth:Password"] ?? "admin";

        // 校验当前密码
        if (!PanelPasswordHasher.Verify(storedPass, req.CurrentPassword))
            return BadRequest(new { error = "当前密码错误" });

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest(new { error = "新密码至少 8 位" });

        if (req.NewPassword != req.ConfirmPassword)
            return BadRequest(new { error = "两次输入的新密码不一致" });

        if (PanelPasswordHasher.Verify(storedPass, req.NewPassword))
            return BadRequest(new { error = "新密码不能与当前密码相同" });

        // 更新 appsettings.json（哈希格式）
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            WritePasswordToAppSettings(PanelPasswordHasher.Hash(req.NewPassword));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"写入配置失败：{ex.Message}" });
        }

        // 清除所有会话，强制重新登录
        _sessions.ClearAll();
        Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });

        return Ok(new { message = "密码修改成功，请重新登录" });
    }

    /// <summary>列出当前活跃的登录会话（令牌仅显示前缀）。</summary>
    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        var token = Request.Cookies[SessionCookieName];
        var currentPrefix = string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)];
        var list = _sessions.GetSessions().Select(s => new
        {
            s.TokenPrefix,
            IsCurrent = s.TokenPrefix == currentPrefix,
            s.CreatedAt,
            s.LastSeenAt,
            s.ExpiresAt,
            s.RemoteIp
        });
        return Ok(list);
    }

    /// <summary>按前缀注销指定会话（不允许远程注销当前会话，避免误操作）。</summary>
    [HttpPost("sessions/revoke")]
    public IActionResult RevokeSession([FromBody] RevokeSessionRequest req)
    {
        var token = Request.Cookies[SessionCookieName];
        var currentPrefix = string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)];
        if (string.IsNullOrWhiteSpace(req.TokenPrefix))
            return BadRequest(new { error = "缺少会话标识" });
        if (req.TokenPrefix == currentPrefix)
            return BadRequest(new { error = "不能注销当前会话，请使用退出登录" });

        var removed = _sessions.RemoveByPrefix(req.TokenPrefix);
        return removed ? Ok(new { message = "会话已注销" }) : NotFound(new { error = "会话不存在或已过期" });
    }

    /// <summary>把存储值写入 appsettings.json 的 PanelAuth:Password。</summary>
    private static void WritePasswordToAppSettings(string storedValue)
    {
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = System.IO.File.ReadAllText(appSettingsPath);
        var doc = JsonNode.Parse(json) ?? new JsonObject();
        if (doc["PanelAuth"] is null)
            doc["PanelAuth"] = new JsonObject();
        doc["PanelAuth"]!["Password"] = storedValue;

        var options = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(appSettingsPath, doc.ToJsonString(options));
    }
}

/// <summary>
/// 面板密码哈希工具。存储格式：sha256$&lt;saltBase64&gt;$&lt;hashHex&gt;；
/// 兼容历史明文（登录成功后由调用方自动升级）。
/// </summary>
public static class PanelPasswordHasher
{
    private const string Prefix = "sha256$";

    public static bool IsHashed(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt);
        return $"{Prefix}{Convert.ToBase64String(salt)}${Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static bool Verify(string? stored, string password)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        if (!IsHashed(stored))
        {
            // 旧明文兼容
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(password));
        }

        var parts = stored.Split('$');
        if (parts.Length != 3) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromHexString(parts[2]);
            var actual = Derive(password, salt);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        // PBKDF2-SHA256，10 万次迭代
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}
