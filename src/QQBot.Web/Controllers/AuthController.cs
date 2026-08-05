using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using QQBot.Web.Services;

namespace QQBot.Web.Controllers;

/// <summary>
/// 管理面板登录认证接口。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const string SessionCookieName = "qqbot_panel_session";

    private readonly IConfiguration _config;
    private readonly PanelSessionStore _sessions;

    public AuthController(IConfiguration config, PanelSessionStore sessions)
    {
        _config = config;
        _sessions = sessions;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>登录，校验账号密码后下发会话 Cookie。</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        var expectedUser = _config["PanelAuth:Username"] ?? "admin";
        var expectedPass = _config["PanelAuth:Password"] ?? "admin";

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "请输入账号和密码" });

        if (!string.Equals(req.Username, expectedUser, StringComparison.Ordinal) ||
            !string.Equals(req.Password, expectedPass, StringComparison.Ordinal))
            return Unauthorized(new { error = "账号或密码错误" });

        var token = _sessions.CreateSession();
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
}
