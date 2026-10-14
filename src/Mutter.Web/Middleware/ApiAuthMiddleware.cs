using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Mutter.Web.Services;

namespace Mutter.Web.Middleware;

/// <summary>
/// 管理面板认证中间件。
/// 未登录的页面请求重定向到 /login.html；未登录的 API/Hub 请求返回 401。
/// 同时保留对原 Bearer Token（ApiAuth:Token）的兼容，便于非浏览器客户端调用 API。
/// </summary>
public class ApiAuthMiddleware
{
    private const string SessionCookieName = "mutter_panel_session";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly PanelSessionStore _sessions;

    public ApiAuthMiddleware(RequestDelegate next, IConfiguration config, PanelSessionStore sessions)
    {
        _next = next;
        _config = config;
        _sessions = sessions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

        // 公开路径：登录页、登录/登出/状态接口、站点图标
        var isPublicPath =
            path == "/login.html" ||
            path == "/api/auth/login" ||
            path == "/api/auth/logout" ||
            path == "/api/auth/status" ||
            path == "/vite.svg" ||
            path == "/favicon.ico";

        if (isPublicPath)
        {
            await _next(context);
            return;
        }

        // 1) 会话 Cookie 认证（浏览器）
        var sessionToken = context.Request.Cookies[SessionCookieName];
        var sessionValid = _sessions.IsValid(sessionToken);

        // 2) Bearer Token 认证（非浏览器客户端，向后兼容）
        var bearerValid = false;
        var bearerToken = _config["ApiAuth:Token"];
        if (!string.IsNullOrEmpty(bearerToken))
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            bearerValid = authHeader == $"Bearer {bearerToken}";
        }

        if (sessionValid || bearerValid)
        {
            await _next(context);
            return;
        }

        // 未认证：API/Hub 返回 401，其它（页面/静态资源）重定向到登录页
        var isApiOrHub = path.StartsWith("/api/") || path.StartsWith("/hub/");
        if (isApiOrHub)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        // 页面请求：携带 return 参数跳转到登录页（仅允许相对路径，防止开放重定向）
        var returnPath = context.Request.Path.Value ?? "/";
        if (string.IsNullOrEmpty(returnPath) || returnPath == "/")
            context.Response.Redirect("/login.html");
        else
            context.Response.Redirect($"/login.html?return={Uri.EscapeDataString(returnPath)}");
    }
}
