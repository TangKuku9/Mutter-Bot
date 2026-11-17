using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Mutter.Web.Services;

namespace Mutter.Web.Controllers;

/// <summary>
/// 管理面板登录认证与账户管理接口。
/// </summary>
[ApiController]
[Route("api/auth")]
[RequestSizeLimit(16 * 1024)]
public class AuthController : ControllerBase
{
    private string SessionCookieName => PanelRequestSecurity.CookieName(Request);

    private readonly IConfiguration _config;
    private readonly PanelSessionStore _sessions;
    private readonly PanelLoginGuard _loginGuard;
    private readonly PanelPasswordStore _passwords;
    private readonly PanelBootstrapStore _bootstrap;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, PanelSessionStore sessions, PanelLoginGuard loginGuard,
        PanelPasswordStore passwords, PanelBootstrapStore bootstrap, ILogger<AuthController> logger)
    {
        _config = config;
        _sessions = sessions;
        _loginGuard = loginGuard;
        _passwords = passwords;
        _bootstrap = bootstrap;
        _logger = logger;
    }

    public class LoginRequest
    {
        [StringLength(64)] public string Username { get; set; } = "";
        [StringLength(PanelRequestSecurity.MaximumPasswordLength)]
        public string Password { get; set; } = "";
    }

    public class ChangePasswordRequest
    {
        [StringLength(PanelRequestSecurity.MaximumPasswordLength)] public string CurrentPassword { get; set; } = "";
        [StringLength(PanelRequestSecurity.MaximumPasswordLength, MinimumLength = PanelRequestSecurity.MinimumPasswordLength)]
        public string NewPassword { get; set; } = "";
        [StringLength(PanelRequestSecurity.MaximumPasswordLength)] public string ConfirmPassword { get; set; } = "";
        [StringLength(32)] public string BootstrapToken { get; set; } = "";
    }

    public class RevokeSessionRequest
    {
        public string TokenPrefix { get; set; } = "";
    }

    /// <summary>登录，校验账号密码后下发会话 Cookie。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        // 无效表单不应占用全局哈希预算，否则空请求就能耗尽合法登录名额。
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "请输入账号和密码" });
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_loginGuard.TryBeginAttempt(ip, out var attempt, out var retry)) return TooManyAttempts(retry);
        using var lease = attempt;

        var expectedUser = _config["PanelAuth:Username"];
        var storedPass = _config["PanelAuth:Password"];
        if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(storedPass))
            return StatusCode(500, new { error = "面板账号未配置，请检查 appsettings.json 的 PanelAuth 配置" });

        var passwordValid = PanelPasswordHasher.Verify(storedPass, req.Password);
        if (!string.Equals(req.Username, expectedUser, StringComparison.Ordinal) || !passwordValid)
        {
            _loginGuard.RecordFailure(ip);
            return Unauthorized(new { error = "账号或密码错误" });
        }

        // 初始密码未修改：不下发会话，必须先走强制改密流程
        if (PanelRequestSecurity.DefaultPasswords.Contains(req.Password, StringComparer.Ordinal))
        {
            if (!PanelRequestSecurity.IsDirectLocalRequest(HttpContext))
            {
                _loginGuard.RecordFailure(ip);
                return Unauthorized(new { error = "账号或密码错误" });
            }
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                mustChangePassword = true,
                error = "当前密码为初始默认密码，请先修改密码后再登录"
            });
        }
        if (req.Password.Length < PanelRequestSecurity.MinimumPasswordLength)
        {
            if (!PanelRequestSecurity.IsDirectLocalRequest(HttpContext))
            {
                _loginGuard.RecordFailure(ip);
                return Unauthorized(new { error = "账号或密码错误" });
            }
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                mustChangePassword = true,
                error = "旧口令需要更新，请通过本机维护入口和初始化码设置新密码"
            });
        }

        // 明文或旧格式哈希在登录成功后透明升级到新格式
        if (!PanelPasswordHasher.IsHashed(storedPass) || PanelPasswordHasher.NeedsRehash(storedPass))
        {
            try { _passwords.Save(PanelPasswordHasher.Hash(req.Password)); }
            catch (Exception ex) { _logger.LogWarning(ex, "面板旧口令哈希升级未能保存"); }
        }

        _loginGuard.RecordSuccess(ip);
        _sessions.SynchronizeCredentials(expectedUser, _config["PanelAuth:Password"] ?? "", _config["ApiAuth:Token"] ?? "");
        var token = _sessions.CreateSession(ip);
        var timeoutMinutes = Math.Clamp(_config.GetValue("PanelAuth:SessionTimeoutMinutes", 120), 5, 720);

        Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps || _config.GetValue("PanelAuth:RequireSecureCookie", false),
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(timeoutMinutes)
        });

        return Ok(new { username = expectedUser });
    }

    /// <summary>注销当前会话。</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        var token = Request.Cookies[SessionCookieName];
        _sessions.Remove(token);
        Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/", Secure = Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Strict });
        return Ok();
    }

    /// <summary>查询当前会话状态。</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult Status()
    {
        var token = Request.Cookies[SessionCookieName];
        var user = _config["PanelAuth:Username"] ?? string.Empty;
        if (_sessions.IsValid(token))
            return Ok(new { authenticated = true, username = user });

        return Ok(new { authenticated = false });
    }

    /// <summary>修改密码并撤销会话；仅直接本机连接允许持已知初始口令进行无会话初始化。</summary>
    [HttpPost("change-password")]
    [AllowAnonymous]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var token = Request.Cookies[SessionCookieName];
        var hasSession = _sessions.IsValid(token);

        var storedPass = _config["PanelAuth:Password"];
        if (string.IsNullOrWhiteSpace(storedPass))
            return StatusCode(500, new { error = "面板账号未配置，请检查 appsettings.json 的 PanelAuth 配置" });

        if (!hasSession && (!PanelRequestSecurity.IsDirectLocalRequest(HttpContext)
            || !_bootstrap.TryValidate(req.BootstrapToken)))
            return Unauthorized(new { error = "请先登录；首次设置请通过本机或 SSH 端口转发，填写服务器维护目录中的初始化码" });
        if (!_loginGuard.TryBeginAttempt(ip, out var attempt, out var retry)) return TooManyAttempts(retry);
        using var lease = attempt;

        // 校验当前密码
        if (!PanelPasswordHasher.Verify(storedPass, req.CurrentPassword))
        {
            _loginGuard.RecordFailure(ip);
            return BadRequest(new { error = "当前密码错误" });
        }

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length is < PanelRequestSecurity.MinimumPasswordLength or > PanelRequestSecurity.MaximumPasswordLength)
            return BadRequest(new { error = "新密码须为 15 至 128 位，建议使用随机口令或长口令短语" });

        if (req.NewPassword != req.ConfirmPassword)
            return BadRequest(new { error = "两次输入的新密码不一致" });

        if (string.Equals(req.CurrentPassword, req.NewPassword, StringComparison.Ordinal))
            return BadRequest(new { error = "新密码不能与当前密码相同" });

        if (PanelRequestSecurity.DefaultPasswords.Contains(req.NewPassword, StringComparer.Ordinal))
            return BadRequest(new { error = "新密码不能使用初始默认密码" });

        // 更新 appsettings.json（哈希格式）
        try
        {
            _passwords.Save(PanelPasswordHasher.Hash(req.NewPassword));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "面板密码保存失败");
            return StatusCode(500, new { error = "密码保存失败，请检查服务器配置与日志" });
        }

        // 清除所有会话，强制重新登录。若令牌文件暂时不可写，口令仍已安全更换，
        // 不再接受默认口令登录；重启即可重新生成本机维护码。
        try { _bootstrap.Consume(); }
        catch (Exception ex) { _logger.LogWarning(ex, "初始化码文件未能删除"); }
        _sessions.ClearAll();
        _loginGuard.RecordSuccess(ip);
        Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/", Secure = Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Strict });

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

    private ObjectResult TooManyAttempts(TimeSpan retry)
    {
        Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "认证请求过于频繁，请稍后重试" });
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
            if (salt.Length is < 8 or > 64 || expected.Length != 32) return false;
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
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
