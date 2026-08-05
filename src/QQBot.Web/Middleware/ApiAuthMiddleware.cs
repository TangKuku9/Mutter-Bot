using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace QQBot.Web.Middleware;

public class ApiAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public ApiAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

        // 只对 /api/ 路径要求认证，SignalR hub 和静态文件不要求
        if (path.StartsWith("/api/"))
        {
            var token = _config["ApiAuth:Token"];
            if (!string.IsNullOrEmpty(token))
            {
                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (authHeader == null || authHeader != $"Bearer {token}")
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                    return;
                }
            }
        }

        await _next(context);
    }
}
