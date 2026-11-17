using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mutter.Web.Services;

public sealed class PanelAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration, PanelSessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MutterPanel";
    public const string MethodClaim = "mutter_auth_method";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = configuration["PanelAuth:Username"] ?? "";
        var password = configuration["PanelAuth:Password"] ?? "";
        var apiToken = configuration["ApiAuth:Token"] ?? "";
        sessions.SynchronizeCredentials(user, password, apiToken);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (Request.Headers.ContainsKey("Authorization"))
        {
            // Bearer 只用于非浏览器 API；不接受 URL 查询令牌，也不让其解锁网页或实时流。
            if (PanelRequestSecurity.IsApi(Request.Path) && apiToken.Length is >= 32 and <= 512
                && !apiToken.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                && Request.Headers.Authorization.Count == 1
                && AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var supplied)
                && supplied.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                && supplied.Parameter?.Length is >= 32 and <= 512
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Parameter)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(apiToken))))
                return Task.FromResult(Success(user, "bearer", null, null));
            return Task.FromResult(AuthenticateResult.Fail("Invalid API credentials"));
        }

        var token = Request.Cookies[PanelRequestSecurity.CookieName(Request)];
        if (!sessions.TryValidate(token, out var session)) return Task.FromResult(AuthenticateResult.NoResult());
        return Task.FromResult(Success(user, "session", token, session.ExpiresAt));
    }

    private static AuthenticateResult Success(string user, string method, string? token, DateTimeOffset? expires)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, user), new(MethodClaim, method) };
        if (token is not null) claims.Add(new(ClaimTypes.Sid, token));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = expires }, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new { error = "Unauthorized" });
    }
}
