using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mutter.Web;
using Mutter.Web.Controllers;
using Mutter.Web.Hubs;
using Mutter.Web.Services;

/// <summary>只测试本机临时宿主、合成凭据及临时证书，不连接生产服务。</summary>
internal static class PanelSecurityChecks
{
    private const string Password = "synthetic-panel-password-2026!";
    private const string NextPassword = "synthetic-rotated-password-2026!";
    private const string ApiToken = "synthetic-api-token-32-bytes-minimum-2026";

    public static async Task<bool> RunAsync()
    {
        var passed = true;
        foreach (var (name, check) in new (string, Func<Task>)[]
        {
            ("公网首次初始化拒绝接管，本机仍能安全完成初始化", BootstrapAsync),
            ("HTTPS、可信代理和伪造转发头", TransportAsync),
            ("API、静态资源、跨站请求及 Bearer 隔离", AuthenticationAsync),
            ("实际 WebSocket 跨站拒绝与会话撤销断开", WebSocketAsync),
            ("密码轮换、外部凭据变更与长口令限制", PasswordRotationAsync),
            ("登录哈希并发、全局速率、失败锁定及有界来源表", LoginLimitsAsync),
            ("会话容量、绝对期限及撤销通知", SessionLimitsAsync),
            ("本机初始化码过期轮换与成功消费", BootstrapTokenLifecycleAsync)
        })
        {
            try { await check().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine($"PASS  {name}"); }
            catch (Exception ex) { passed = false; Console.WriteLine($"FAIL  {name}: {ex}"); }
        }
        return passed;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task BootstrapAsync()
    {
        await using var host = await TestHost.StartAsync("CHANGE_ME_STRONG_PASSWORD");
        foreach (var path in new[] { "/api/auth/change-password", "/api/Auth/Change-Password/", "/api/auth/%63hange-password" })
        {
            using var response = await host.SendAsync(HttpMethod.Post, path, new
            {
                currentPassword = "CHANGE_ME_STRONG_PASSWORD", newPassword = Password, confirmPassword = Password
            }, publicHost: true);
            Require(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
                "a public caller knowing the shipped password must not claim the panel, including route aliases");
        }
        using var login = await host.LoginAsync("CHANGE_ME_STRONG_PASSWORD", publicHost: true);
        Require(login.StatusCode == HttpStatusCode.Unauthorized && !login.Headers.Contains("Set-Cookie"),
            "the default password cannot create a public session or reveal initialization state");
        Require(host.Configuration["PanelAuth:Password"] == "CHANGE_ME_STRONG_PASSWORD", "remote bootstrap must not write the password");

        using var local = await host.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = "CHANGE_ME_STRONG_PASSWORD", newPassword = Password, confirmPassword = Password
        });
        Require(local.StatusCode == HttpStatusCode.Unauthorized && host.Configuration["PanelAuth:Password"] == "CHANGE_ME_STRONG_PASSWORD",
            "a loopback-looking proxy without a maintenance token cannot claim the panel");
        await using var localHost = await TestHost.StartAsync("CHANGE_ME_STRONG_PASSWORD");
        using var wrongToken = await localHost.ChangePasswordAsync("CHANGE_ME_STRONG_PASSWORD", Password, new string('0', 32));
        Require(wrongToken.StatusCode == HttpStatusCode.Unauthorized,
            "a public default password plus a wrong token cannot initialize");
        var token = localHost.ReadBootstrapToken();
        using var accepted = await localHost.ChangePasswordAsync("CHANGE_ME_STRONG_PASSWORD", Password, token);
        Require(accepted.IsSuccessStatusCode && PanelPasswordHasher.Verify(localHost.Configuration["PanelAuth:Password"], Password)
            && !File.Exists(localHost.App.Services.GetRequiredService<PanelBootstrapStore>().FilePath),
            "SSH-shaped local maintenance with a valid token persists only a password hash and consumes the token");
        using var consumed = await localHost.ChangePasswordAsync("CHANGE_ME_STRONG_PASSWORD", Password, token);
        Require(consumed.StatusCode == HttpStatusCode.Unauthorized, "the initialization token is single-use");
        using var initialized = await localHost.LoginAsync(Password, publicHost: true);
        Require(initialized.IsSuccessStatusCode, "any public client can log in over HTTPS after initialization");
        await using var proxyHost = await TestHost.StartAsync("CHANGE_ME_STRONG_PASSWORD", trustLoopbackProxy: true);
        using var proxyBootstrap = await proxyHost.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = "CHANGE_ME_STRONG_PASSWORD", newPassword = Password, confirmPassword = Password
        }, https: false, headers: new() { ["X-Forwarded-Proto"] = "https" });
        Require(proxyBootstrap.StatusCode == HttpStatusCode.Unauthorized,
            "even a proxy forwarding to a loopback Host without client IP information cannot bootstrap");
    }

    private static async Task TransportAsync()
    {
        await using var host = await TestHost.StartAsync();
        using var insecure = await host.SendAsync(HttpMethod.Get, "/login.html", https: false, publicHost: true);
        Require((int)insecure.StatusCode == 426, "public plaintext access must fail closed");
        using var forged = await host.SendAsync(HttpMethod.Get, "/api/auth/status", https: false, publicHost: true,
            headers: new() { ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "127.0.0.1" });
        Require((int)forged.StatusCode == 426, "unknown upstreams cannot spoof HTTPS or a local source");
        using var rebind = await host.SendAsync(HttpMethod.Get, "/login.html", https: false,
            headers: new() { ["Host"] = "attacker.example.test" });
        Require((int)rebind.StatusCode == 426, "loopback transport with an attacker-controlled Host is not local maintenance");
        using var local = await host.SendAsync(HttpMethod.Get, "/api/auth/status", https: false);
        Require(local.IsSuccessStatusCode, "direct loopback HTTP is available for SSH port forwarding");

        await using var proxyHost = await TestHost.StartAsync(trustLoopbackProxy: true);
        using var proxied = await proxyHost.SendAsync(HttpMethod.Get, "/api/auth/status", https: false, publicHost: true,
            headers: new() { ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "198.51.100.20" });
        Require(proxied.IsSuccessStatusCode && proxied.Headers.Contains("Strict-Transport-Security"),
            "an explicitly trusted TLS-terminating proxy supplies HTTPS and the original client address");
        var options = host.App.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Require(!options.AddServerHeader && options.Limits.MaxRequestBodySize == 1024 * 1024
            && options.Limits.MaxConcurrentConnections == 256 && options.Limits.MaxConcurrentUpgradedConnections == 64,
            "the actual host applies bounded request and connection budgets");
    }

    private static async Task AuthenticationAsync()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var path in new[] { "/api", "/api/security-probe", "/api/config/ai", "/api/config/qqbot", "/api/chat/timeline",
            "/api/groups", "/api/runtime", "/api/update/settings", "/api/auth/sessions", "/API/CONFIG/AI/" })
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Require(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous access is denied for " + path);
        }
        using var page = await host.SendAsync(HttpMethod.Get, "/");
        Require(page.StatusCode == HttpStatusCode.Redirect && page.Headers.Location?.OriginalString.StartsWith("/login.html", StringComparison.Ordinal) == true,
            "private documents redirect to a local login URL");
        using var loginPage = await host.SendAsync(HttpMethod.Get, "/login.html");
        Require(loginPage.IsSuccessStatusCode && loginPage.Headers.GetValues("Content-Security-Policy").Single().Contains("script-src 'self'", StringComparison.Ordinal)
            && loginPage.Headers.CacheControl?.NoStore == true && !loginPage.Headers.Contains("Server"),
            "the public login document has a restrictive CSP, no cache, and no server banner");
        using var loginScript = await host.SendAsync(HttpMethod.Get, "/login.js");
        Require(loginScript.IsSuccessStatusCode, "the external CSP-compatible login module is public");
        using var loginCsrf = await host.SendAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = Password }, requestHeader: false);
        Require(loginCsrf.StatusCode == HttpStatusCode.Forbidden, "login CSRF is rejected before password verification");
        for (var i = 0; i < 21; i++)
        {
            using var empty = await host.SendAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "" });
            Require(empty.StatusCode == HttpStatusCode.BadRequest, "empty login forms cannot consume the global hash budget");
        }

        using var loggedIn = await host.LoginAsync(Password, publicHost: true);
        Require(loggedIn.IsSuccessStatusCode, "a strong password creates a session");
        var setCookie = loggedIn.Headers.GetValues("Set-Cookie").Single();
        var cookie = setCookie.Split(';')[0];
        Require(cookie.StartsWith("__Host-mutter_panel_session=", StringComparison.Ordinal) && setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)
            && !setCookie.Contains("domain=", StringComparison.OrdinalIgnoreCase), "public cookies are secure, host-bound, HttpOnly and SameSite Strict");
        foreach (var origin in new[] { "https://attacker.example.test", "null", "https://127.0.0.1.attacker.example.test" })
        {
            using var crossSite = await host.SendAsync(HttpMethod.Post, "/api/security-probe", new { value = "write" }, cookie,
                headers: new() { ["Origin"] = origin });
            Require(crossSite.StatusCode == HttpStatusCode.Forbidden, "cross-origin writes cannot use an existing cookie");
        }
        using var noHeader = await host.SendAsync(HttpMethod.Post, "/api/security-probe", new { value = "write" }, cookie, requestHeader: false);
        Require(noHeader.StatusCode == HttpStatusCode.Forbidden, "simple requests cannot mutate state with a cookie");
        using var crossFetch = await host.SendAsync(HttpMethod.Get, "/api/security-probe", cookie: cookie, headers: new() { ["Sec-Fetch-Site"] = "cross-site" });
        Require(crossFetch.StatusCode == HttpStatusCode.Forbidden, "cross-site fetch metadata is rejected");
        using var valid = await host.SendAsync(HttpMethod.Post, "/api/security-probe", new { value = "write" }, cookie);
        Require(valid.IsSuccessStatusCode && host.Mutations == 1, "same-origin authenticated writes continue working");
        using var bearer = await host.SendAsync(HttpMethod.Post, "/api/security-probe", new { value = "write" }, requestHeader: false,
            headers: new() { ["Authorization"] = "Bearer " + ApiToken });
        Require(bearer.IsSuccessStatusCode && host.Mutations == 2, "valid non-browser bearer requests remain supported");
        using var bearerPage = await host.SendAsync(HttpMethod.Get, "/", headers: new() { ["Authorization"] = "Bearer " + ApiToken });
        Require(bearerPage.StatusCode == HttpStatusCode.Redirect, "an API bearer cannot unlock documents");
        using var bearerHub = await host.SendAsync(HttpMethod.Post, "/hub/botstatus/negotiate?negotiateVersion=1", headers: new() { ["Authorization"] = "Bearer " + ApiToken });
        Require(bearerHub.StatusCode == HttpStatusCode.Unauthorized, "an API bearer cannot open a realtime stream");
        using var oversized = await host.SendAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = new string('x', 20000) });
        Require(oversized.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest
            && !oversized.Headers.Contains("Set-Cookie"), $"oversized login bodies are rejected before hashing (got {(int)oversized.StatusCode}: {await oversized.Content.ReadAsStringAsync()})");
        host.SaveConfiguration(PanelPasswordHasher.Hash(Password), "short-token");
        using var weakBearer = await host.SendAsync(HttpMethod.Get, "/api/security-probe", headers: new() { ["Authorization"] = "Bearer short-token" });
        Require(weakBearer.StatusCode == HttpStatusCode.Unauthorized, "short legacy bearer tokens fail closed");
    }

    private static async Task WebSocketAsync()
    {
        await using var host = await TestHost.StartAsync();
        using var login = await host.LoginAsync(Password);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using (var crossSite = host.Socket(cookie, "https://attacker.example.test"))
        {
            var denied = false;
            try { await crossSite.ConnectAsync(host.SocketUri, CancellationToken.None); }
            catch (WebSocketException) { denied = true; }
            Require(denied, "a real cross-site websocket handshake must be rejected");
        }
        using var socket = host.Socket(cookie, host.HttpsAddress);
        await socket.ConnectAsync(host.SocketUri, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var handshake = await socket.ReceiveAsync(buffer, deadline.Token);
        Require(handshake.Count > 0 && Encoding.UTF8.GetString(buffer, 0, handshake.Count).Contains("{}", StringComparison.Ordinal), "the same-origin authenticated SignalR connection starts");
        using var logout = await host.SendAsync(HttpMethod.Post, "/api/auth/logout", cookie: cookie);
        Require(logout.IsSuccessStatusCode, "logout succeeds");
        var disconnected = false;
        try
        {
            // SignalR 可能先发送协议级 close(type=7)，再关闭 WebSocket 传输。
            while (!disconnected)
                disconnected = (await socket.ReceiveAsync(buffer, deadline.Token)).MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException) { disconnected = true; }
        Require(disconnected, "logout must terminate an already-open realtime connection");
        using var stale = await host.SendAsync(HttpMethod.Get, "/api/security-probe", cookie: cookie);
        Require(stale.StatusCode == HttpStatusCode.Unauthorized, "a revoked cookie cannot be replayed");
    }

    private static async Task PasswordRotationAsync()
    {
        await using var host = await TestHost.StartAsync();
        using var login = await host.LoginAsync(Password);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var weak = await host.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = Password, newPassword = "short-pass", confirmPassword = "short-pass"
        }, cookie);
        Require(weak.StatusCode == HttpStatusCode.BadRequest, "new passwords need the public-panel minimum length");
        using var changed = await host.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = Password, newPassword = NextPassword, confirmPassword = NextPassword
        }, cookie);
        Require(changed.IsSuccessStatusCode && PanelPasswordHasher.Verify(host.Configuration["PanelAuth:Password"], NextPassword),
            "changing the password immediately updates the persisted hash");
        using var revoked = await host.SendAsync(HttpMethod.Get, "/api/security-probe", cookie: cookie);
        Require(revoked.StatusCode == HttpStatusCode.Unauthorized, "password changes revoke existing sessions");
        using var oldLogin = await host.LoginAsync(Password);
        Require(oldLogin.StatusCode == HttpStatusCode.Unauthorized, "the previous password no longer logs in");
        using var newLogin = await host.LoginAsync(NextPassword);
        Require(newLogin.IsSuccessStatusCode, "the replacement password logs in without restarting");
        var current = newLogin.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        host.SaveConfiguration(PanelPasswordHasher.Hash(Password), ApiToken);
        using var externallyRevoked = await host.SendAsync(HttpMethod.Get, "/api/security-probe", cookie: current);
        Require(externallyRevoked.StatusCode == HttpStatusCode.Unauthorized, "out-of-band credential rotation revokes sessions too");
        await using var weakHost = await TestHost.StartAsync(PanelPasswordHasher.Hash("short-existing"));
        using var weakPublic = await weakHost.LoginAsync("short-existing", publicHost: true);
        Require(weakPublic.StatusCode == HttpStatusCode.Unauthorized && !weakPublic.Headers.Contains("Set-Cookie"),
            "a short legacy password cannot create a public session and is indistinguishable from a wrong password");
        using var weakLocal = await weakHost.LoginAsync("short-existing");
        Require(weakLocal.StatusCode == HttpStatusCode.Forbidden && !weakLocal.Headers.Contains("Set-Cookie"),
            "a short legacy password enters the local forced-rotation flow instead of a session");
    }

    private static Task LoginLimitsAsync()
    {
        var clock = new SecurityClock();
        var guard = new PanelLoginGuard(clock);
        Require(guard.TryBeginAttempt("first", out var lease, out _), "first hash admitted");
        Require(!guard.TryBeginAttempt("parallel", out _, out _), "unbounded concurrent password hashes are rejected");
        lease!.Dispose();
        for (var i = 0; i < 5; i++)
        {
            Require(guard.TryBeginAttempt("failed", out var attempt, out _), "a failed attempt can run within the failure budget");
            using (attempt) guard.RecordFailure("failed");
        }
        Require(!guard.TryBeginAttempt("failed", out _, out var wait) && wait > TimeSpan.FromMinutes(14), "five failures lock a source");
        for (var i = 0; i < 14; i++)
        {
            Require(guard.TryBeginAttempt("distributed-" + i, out var attempt, out _), "distributed attempts consume the global budget");
            attempt!.Dispose();
        }
        Require(!guard.TryBeginAttempt("next-source", out _, out _), "new IP addresses cannot bypass the global hash budget");
        clock.Advance(TimeSpan.FromHours(2));
        Require(guard.TryBeginAttempt("cleanup", out var cleanup, out _), "the old rate window and sources expire");
        cleanup!.Dispose();
        for (var i = 0; i < 5000; i++) guard.RecordFailure("bounded-" + i);
        Require(!guard.TryBeginAttempt("overflow-source", out _, out _), "the source map has a hard capacity");
        clock.Advance(TimeSpan.FromHours(2));
        Require(guard.TryBeginAttempt("after-expiry", out var recovered, out _), "expired source entries are reclaimed");
        recovered!.Dispose();
        return Task.CompletedTask;
    }

    private static Task SessionLimitsAsync()
    {
        var clock = new SecurityClock();
        using var sessions = new PanelSessionStore(TimeSpan.FromHours(2), clock);
        sessions.SynchronizeCredentials("admin", "synthetic-hash", "");
        var oldest = sessions.CreateSession();
        Require(sessions.TryValidate(oldest, out var observed), "created token validates");
        for (var i = 0; i < 32; i++) { clock.Advance(TimeSpan.FromSeconds(1)); sessions.CreateSession(); }
        Require(sessions.GetSessions().Count == 32 && !sessions.IsValid(oldest) && observed.Revocation.IsCancellationRequested,
            "session capacity evicts and revokes the oldest session");
        var active = sessions.CreateSession();
        for (var hour = 0; hour < 11; hour++) { clock.Advance(TimeSpan.FromHours(1)); Require(sessions.IsValid(active), "active sessions renew within their absolute lifetime"); }
        clock.Advance(TimeSpan.FromHours(1));
        Require(!sessions.IsValid(active), "a stolen token cannot renew forever");
        var rotated = sessions.CreateSession();
        sessions.SynchronizeCredentials("admin", "replacement-hash", "");
        Require(!sessions.IsValid(rotated), "credential changes revoke all tokens");
        return Task.CompletedTask;
    }

    /// <summary>验证初始化码生命周期：过期轮换、正确校验、消费后失效。</summary>
    private static Task BootstrapTokenLifecycleAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mutter-panel-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new SecurityClock();
            var environment = new BootstrapHostEnvironment { ContentRootPath = directory };
            using var loggerFactory = NullLoggerFactory.Instance;
            var store = new PanelBootstrapStore(environment,
                loggerFactory.CreateLogger<PanelBootstrapStore>(), clock);
            store.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var path = store.FilePath;
            Require(File.Exists(path), "the bootstrap token file is created in the maintenance directory");
            var first = File.ReadAllText(path).Trim();
            Require(first.Length == 32 && first.All(Uri.IsHexDigit), "the bootstrap token is 128-bit hex");
            Require(!store.TryValidate(new string('0', 32)), "a wrong token is rejected in fixed time");
            clock.Advance(TimeSpan.FromMinutes(31));
            store.TryValidate(new string('0', 32));
            var rotated = File.ReadAllText(path).Trim();
            Require(rotated.Length == 32 && !rotated.Equals(first, StringComparison.OrdinalIgnoreCase),
                "a stale on-disk token is replaced with a fresh one after its lifetime");
            Require(store.TryValidate(rotated), "a fresh on-disk token validates in fixed time");
            store.Consume();
            Require(!File.Exists(path), "consuming the token removes the maintenance file");
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BootstrapHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "mutter";
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class SecurityClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutter-panel-security-" + Guid.NewGuid().ToString("N"));
        private readonly RSA _key = RSA.Create(2048);
        private X509Certificate2 _certificate = null!;
        private HttpClient _client = null!;
        public WebApplication App { get; private set; } = null!;
        public IConfigurationRoot Configuration => (IConfigurationRoot)App.Configuration;
        public string HttpsAddress { get; private set; } = "";
        private string HttpAddress { get; set; } = "";
        public Uri SocketUri => new(HttpsAddress.Replace("https://", "wss://", StringComparison.Ordinal) + "/hub/botstatus");
        public int Mutations;
        private bool _trustProxy;

        public static async Task<TestHost> StartAsync(string? password = null, bool trustLoopbackProxy = false)
        {
            var host = new TestHost { _trustProxy = trustLoopbackProxy };
            try { await host.StartInternalAsync(password); return host; }
            catch { await host.DisposeAsync(); throw; }
        }

        private async Task StartInternalAsync(string? password)
        {
            Directory.CreateDirectory(Path.Combine(_directory, "wwwroot"));
            File.WriteAllText(Path.Combine(_directory, "wwwroot", "login.html"), "<!doctype html><title>Synthetic login</title><script type=module src=/login.js></script>");
            File.WriteAllText(Path.Combine(_directory, "wwwroot", "login.js"), "/* synthetic public login module */");
            SaveConfiguration(password ?? PanelPasswordHasher.Hash(Password), ApiToken, reload: false);
            var certificate = new CertificateRequest("CN=localhost", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            certificate.CertificateExtensions.Add(names.Build());
            using (var generated = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)))
                _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _directory, WebRootPath = Path.Combine(_directory, "wwwroot"), EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddJsonFile(Path.Combine(_directory, "settings.json"), optional: false, reloadOnChange: false);
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(_certificate));
            });
            builder.Services.AddSingleton<LiveLogBus>();
            App = builder.ConfigureMutterWeb();
            App.MapGet("/api/security-probe", () => Results.Json(new { value = "synthetic-protected-data" })).RequireAuthorization();
            App.MapPost("/api/security-probe", () => { Interlocked.Increment(ref Mutations); return Results.Ok(); }).RequireAuthorization();
            await App.StartAsync();
            HttpsAddress = App.Urls.Single(value => value.StartsWith("https://", StringComparison.Ordinal));
            HttpAddress = App.Urls.Single(value => value.StartsWith("http://", StringComparison.Ordinal));
            _client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == _certificate.Thumbprint
            }) { Timeout = TimeSpan.FromSeconds(8) };
        }

        public void SaveConfiguration(string password, string apiToken, bool reload = true)
        {
            File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(new
            {
                PanelAuth = new { Username = "admin", Password = password, SessionTimeoutMinutes = 120 },
                ApiAuth = new { Token = apiToken },
                PanelSecurity = new { TrustedProxies = _trustProxy ? new[] { "127.0.0.1", "::1" } : ["192.0.2.222"] }
            }));
            if (reload) Configuration.Reload();
        }

        public Task<HttpResponseMessage> LoginAsync(string password, bool publicHost = false) =>
            SendAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password }, publicHost: publicHost);

        public Task<HttpResponseMessage> ChangePasswordAsync(string currentPassword, string newPassword, string? bootstrapToken = null) =>
            SendAsync(HttpMethod.Post, "/api/auth/change-password", new
            {
                currentPassword, newPassword, confirmPassword = newPassword, bootstrapToken = bootstrapToken ?? ""
            });

        public string ReadBootstrapToken() =>
            File.ReadAllText(App.Services.GetRequiredService<PanelBootstrapStore>().FilePath).Trim();

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? cookie = null,
            bool https = true, bool publicHost = false, bool requestHeader = true, Dictionary<string, string>? headers = null)
        {
            using var request = new HttpRequestMessage(method, (https ? HttpsAddress : HttpAddress) + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            if (requestHeader) request.Headers.Add(PanelRequestSecurity.RequestHeader, "1");
            if (publicHost) request.Headers.Host = "panel.example.test";
            if (headers is not null)
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return await _client.SendAsync(request);
        }

        public ClientWebSocket Socket(string cookie, string origin)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Cookie", cookie);
            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == _certificate.Thumbprint;
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
            _certificate?.Dispose(); _key.Dispose();
            var fullPath = Path.GetFullPath(_directory);
            Require(fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("mutter-panel-security-", StringComparison.Ordinal), "cleanup is confined to the synthetic test directory");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
