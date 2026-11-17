using System.Net;
using Microsoft.AspNetCore.Http;

namespace Mutter.Web.Services;

public static class PanelRequestSecurity
{
    public const string RequestHeader = "X-Mutter-Request";
    public const string SessionCookieName = "mutter_panel_session";
    public const string SecureSessionCookieName = "__Host-mutter_panel_session";
    public const int MinimumPasswordLength = 15;
    public const int MaximumPasswordLength = 128;
    public static readonly string[] DefaultPasswords = ["CHANGE_ME_STRONG_PASSWORD", "admin"];

    public static bool IsApi(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    public static string CookieName(HttpRequest request) => request.IsHttps ? SecureSessionCookieName : SessionCookieName;
    public static bool IsHub(PathString path) => path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase);
    public static bool IsSafeMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    // 只允许通过直接回环连接初始化；反代不能因其上游地址是 127.0.0.1 而变成“本机”。
    // 同时约束 Host，防止攻击者通过 DNS rebinding 把自己的站点转向回环地址。
    public static bool IsDirectLocalRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        if (address is null || !IPAddress.IsLoopback(address)) return false;
        var host = context.Request.Host.Host;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(host.Trim('[', ']'), out var hostAddress) || !IPAddress.IsLoopback(hostAddress))) return false;
        return !context.Request.Headers.Keys.Any(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryOrigin(HttpRequest request, out Uri origin)
    {
        origin = null!;
        if (Uri.CheckHostName(request.Host.Host.Trim('[', ']')) == UriHostNameType.Unknown) return false;
        return Uri.TryCreate($"{request.Scheme}://{request.Host.ToUriComponent()}", UriKind.Absolute, out origin!)
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    public static bool MatchesOrigin(string? value, Uri target) =>
        Uri.TryCreate(value, UriKind.Absolute, out var source) && source.UserInfo.Length == 0
        && source.AbsolutePath == "/" && source.Query.Length == 0 && source.Fragment.Length == 0
        && source.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)
        && source.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase) && source.Port == target.Port;
}
