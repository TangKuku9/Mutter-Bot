using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using QQBot.Web.Services;

namespace QQBot.Web.Controllers;

/// <summary>
/// 管理面板登录认证与账户管理接口。
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

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
        public string ConfirmPassword { get; set; } = "";
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

    /// <summary>修改密码：校验当前密码后写入 appsettings.json，并清除所有已有会话强制重新登录。</summary>
    [HttpPost("change-password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        // 校验当前会话
        var token = Request.Cookies[SessionCookieName];
        if (!_sessions.IsValid(token))
            return Unauthorized(new { error = "会话已过期，请重新登录" });

        var expectedPass = _config["PanelAuth:Password"] ?? "admin";

        // 校验当前密码
        if (!string.Equals(req.CurrentPassword, expectedPass, StringComparison.Ordinal))
            return Unauthorized(new { error = "当前密码错误" });

        // 校验新密码
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 4)
            return BadRequest(new { error = "新密码至少 4 位" });

        if (req.NewPassword != req.ConfirmPassword)
            return BadRequest(new { error = "两次输入的新密码不一致" });

        if (req.NewPassword == req.CurrentPassword)
            return BadRequest(new { error = "新密码不能与当前密码相同" });

        // 更新 appsettings.json
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            var json = System.IO.File.ReadAllText(appSettingsPath);
            var doc = JsonNode.Parse(json) ?? new JsonObject();
            if (doc["PanelAuth"] is null)
                doc["PanelAuth"] = new JsonObject();
            doc["PanelAuth"]!["Password"] = req.NewPassword;

            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(appSettingsPath, doc.ToJsonString(options));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"保存密码失败: {ex.Message}" });
        }

        // 清除所有会话，强制重新登录（安全策略）
        _sessions.ClearAll();

        // 清除当前响应的 Cookie
        Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });

        return Ok(new { message = "密码修改成功，请重新登录" });
    }
}
