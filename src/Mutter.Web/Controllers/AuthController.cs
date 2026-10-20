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

    /// <summary>随安装包发布的已知初始口令；命中任意一个即视为未初始化，强制先改密。</summary>
    private static readonly string[] DefaultPasswords = ["CHANGE_ME_STRONG_PASSWORD", "admin"];

    private static bool IsUsingDefaultPassword(string? stored) =>
        DefaultPasswords.Any(p =>
            string.Equals(stored, p, StringComparison.Ordinal) ||
            PanelPasswordHasher.Verify(stored, p));

    /// <summary>登录，校验账号密码后下发会话 Cookie。</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 登录失败限流：锁定期间直接拒绝
        var locked = _loginGuard.GetLockedRemaining(ip);
        if (locked is not null)
            return Unauthorized(new { error = $"失败次数过多，请 {(int)locked.Value.TotalMinutes +1} 分钟后再试" });

        var expectedUser = _config["PanelAuth:Username"];
        var storedPass = _config["PanelAuth:Password"];
        if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(storedPass))
            return StatusCode(500, new { error = "面板账号未配置，请检查 appsettings.json 的 PanelAuth 配置" });

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "请输入账号和密码" });

        if (!string.Equals(req.Username, expectedUser, StringComparison.Ordinal) ||
            !PanelPasswordHasher.Verify(storedPass, req.Password))
        {
            _loginGuard.RecordFailure(ip);
            return Unauthorized(new { error = "账号或密码错误" });
        }

        _loginGuard.RecordSuccess(ip);

        // 初始密码未修改：不下发会话，必须先走强制改密流程
        if (IsUsingDefaultPassword(storedPass))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                mustChangePassword = true,
                error = "当前密码为初始默认密码，请先修改密码后再登录"
            });

        // 明文或旧格式哈希在登录成功后透明升级到新格式
        if (!PanelPasswordHasher.IsHashed(storedPass) || PanelPasswordHasher.NeedsRehash(storedPass))
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
            Secure = _config.GetValue("PanelAuth:RequireSecureCookie", false),
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
        var user = _config["PanelAuth:Username"] ?? string.Empty;
        if (_sessions.IsValid(token))
            return Ok(new { authenticated = true, username = user });

        return Ok(new { authenticated = false });
    }

    /// <summary>修改密码：校验当前密码后写入 appsettings.json（哈希存储），并清除所有已有会话强制重新登录。初始密码未修改时允许无会话首改。</summary>
    [HttpPost("change-password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var token = Request.Cookies[SessionCookieName];
        var hasSession = _sessions.IsValid(token);

        var storedPass = _config["PanelAuth:Password"];
        if (string.IsNullOrWhiteSpace(storedPass))
            return StatusCode(500, new { error = "面板账号未配置，请检查 appsettings.json 的 PanelAuth 配置" });

        // 无会话只放行「初始密码首改」；该路径同样受登录失败限流保护
        if (!hasSession)
        {
            if (!IsUsingDefaultPassword(storedPass))
                return Unauthorized(new { error = "会话已过期，请重新登录" });

            var locked = _loginGuard.GetLockedRemaining(ip);
            if (locked is not null)
                return Unauthorized(new { error = $"失败次数过多，请 {(int)locked.Value.TotalMinutes +1} 分钟后再试" });
        }

        // 校验当前密码
        if (!PanelPasswordHasher.Verify(storedPass, req.CurrentPassword))
        {
            if (!hasSession)
                _loginGuard.RecordFailure(ip);
            return BadRequest(new { error = "当前密码错误" });
        }

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest(new { error = "新密码至少 8 位" });

        if (req.NewPassword != req.ConfirmPassword)
            return BadRequest(new { error = "两次输入的新密码不一致" });

        if (PanelPasswordHasher.Verify(storedPass, req.NewPassword))
            return BadRequest(new { error = "新密码不能与当前密码相同" });

        if (DefaultPasswords.Contains(req.NewPassword, StringComparer.Ordinal))
            return BadRequest(new { error = "新密码不能使用初始默认密码" });

        // 更新 appsettings.json（哈希格式）
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

        return Ok(new { message = hasSession ? "密码修改成功，请重新登录" : "初始密码修改成功，请使用新密码登录" });
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
/// 面板密码哈希工具。存储格式：sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashHex&gt;；
/// 历史格式无迭代数段（sha256$&lt;salt&gt;$&lt;hash&gt;，10 万次），校验时按旧参数解析，
/// 登录成功后由调用方透明升级到新格式。
/// </summary>
public static class PanelPasswordHasher
{
    private const string Prefix = "sha256$";
    public const int Iterations = 600_000;
    private const int LegacyIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    public static bool IsHashed(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt, Iterations);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToHexString(hash).ToLowerInvariant()}";
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
        if (parts.Length is not (3 or 4)) return false;
        try
        {
            int iterations;
            string saltPart, hashPart;
            if (parts.Length == 4)
            {
                if (!int.TryParse(parts[1], out iterations) ||
                    iterations <= 0 || iterations > MaxIterations)
                    return false;
                saltPart = parts[2];
                hashPart = parts[3];
            }
            else
            {
                iterations = LegacyIterations;
                saltPart = parts[1];
                hashPart = parts[2];
            }

            var salt = Convert.FromBase64String(saltPart);
            var expected = Convert.FromHexString(hashPart);
            var actual = Derive(password, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>旧格式（无迭代数段）需要在新一次成功登录后升级到新格式。</summary>
    public static bool NeedsRehash(string? stored) =>
        IsHashed(stored) && stored!.Split('$').Length == 3;

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}
