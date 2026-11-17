using Mutter.Web.Services;

namespace Mutter.Web.Middleware;

/// <summary>在认证及业务处理前拒绝不安全传输和跨站入口；不以客户端 IP 白名单限制公网访问。</summary>
public sealed class PanelSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!PanelRequestSecurity.TryOrigin(request, out var origin))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socketOrigin = new UriBuilder(origin) { Scheme = request.IsHttps ? "wss" : "ws" }.Uri.GetLeftPart(UriPartial.Authority);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
                + $"img-src 'self' data:; font-src 'self' data:; connect-src 'self' {socketOrigin}; "
                + "base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'";
            if (request.IsHttps) headers.StrictTransportSecurity = "max-age=31536000";
            if (PanelRequestSecurity.IsApi(request.Path) || PanelRequestSecurity.IsHub(request.Path)
                || !Path.HasExtension(request.Path.Value) || request.Path.Value!.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });

        // 明文 HTTP 只用于本机维护或 SSH 转发；拒绝伪造转发头后直连公网 HTTP。
        if (!request.IsHttps && !PanelRequestSecurity.IsDirectLocalRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new { error = "公网管理面板必须通过 HTTPS 访问" });
            return;
        }

        if (PanelRequestSecurity.IsApi(request.Path) || PanelRequestSecurity.IsHub(request.Path))
        {
            var suppliedOrigin = request.Headers.Origin;
            if (request.Headers["Sec-Fetch-Site"] == "cross-site"
                || suppliedOrigin.Count > 1
                || suppliedOrigin.Count == 1 && !PanelRequestSecurity.MatchesOrigin(suppliedOrigin[0], origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "不允许跨站访问管理面板" });
                return;
            }
        }
        await next(context);
    }
}
