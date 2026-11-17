using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Mutter.Web.Services;

namespace Mutter.Web.Middleware;

/// <summary>
/// 管理面板认证中间件。
/// 未登录的页面请求重定向到 /login.html；未登录的 API/Hub 请求返回 401。
/// 同时保留对原 Bearer Token（ApiAuth:Token）的兼容，便于非浏览器客户端调用 API。
/// </summary>
public class ApiAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        // 所有浏览器写操作（含登录/退出/首次改密）都必须有自定义头；同源检查在前置中间件完成。
        // 标准表单无法添加这个头，跨站脚本添加它会触发不被允许的 CORS 预检。
        if (PanelRequestSecurity.IsApi(context.Request.Path) && !PanelRequestSecurity.IsSafeMethod(context.Request.Method)
            && !context.User.HasClaim(PanelAuthenticationHandler.MethodClaim, "bearer")
            && context.Request.Headers[PanelRequestSecurity.RequestHeader] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "缺少管理面板请求校验头" });
            return;
        }

        // 公开路径：登录页、登录/登出/状态接口、改密接口（无会话的初始密码首改）、品牌与站点图标。
        // 登录页在未登录时就要加载 logo，浏览器也会在没有会话时请求 favicon，因此这些公开静态资源
        // 必须放行；其余 wwwroot 资源仍走登录保护。
        var isPublicPath =
            path == "/login.html" ||
            path == "/login.js" ||
            path == "/login-theme.js" ||
            path == "/logo-mark.svg" ||
            path == "/logo-mark-mono-white.svg" ||
            path == "/logo-lockup.svg" ||
            path == "/logo-lockup-dark.svg" ||
            path == "/logo-lockup-mono-reverse.svg" ||
            path == "/favicon.ico" ||
            path == "/favicon.svg" ||
            path == "/favicon-16.png" ||
            path == "/favicon-32.png" ||
            path == "/apple-touch-icon.png";

        if (isPublicPath || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // 未认证：API/Hub 返回 401，其它（页面/静态资源）重定向到登录页
        var isApiOrHub = PanelRequestSecurity.IsApi(context.Request.Path) || PanelRequestSecurity.IsHub(context.Request.Path);
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
