using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QQBot.Core.Configuration;

namespace QQBot.Infrastructure.Services;

public class AccessTokenManager
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AccessTokenManager> _logger;
    private readonly string _appId;
    private readonly string _clientSecret;
    private readonly string _apiBaseUrl;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string _token = string.Empty;
    private DateTimeOffset _expiresAt;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AccessTokenManager(HttpClient httpClient, IOptions<QQBotOptions> options, ILogger<AccessTokenManager> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var opt = options.Value;
        _appId = opt.AppId;
        _clientSecret = opt.ClientSecret;
        _apiBaseUrl = opt.ApiBaseUrl;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await FetchTokenAsync(ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_token) && _expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
        {
            return _token;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_token) && _expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
            {
                return _token;
            }

            await FetchTokenAsync(ct);
            return _token;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task FetchTokenAsync(CancellationToken ct)
    {
        var url = $"{_apiBaseUrl.TrimEnd('/')}/app/getAppAccessToken";
        var body = new { appId = _appId, clientSecret = _clientSecret };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var snippet = errorBody.Length > 200 ? errorBody[..200] : errorBody;
                _logger.LogError("获取 access_token 失败: status={Status}, body={Body}", response.StatusCode, snippet);
                throw new InvalidOperationException($"获取 access_token 失败: {(int)response.StatusCode} {response.StatusCode}, body: {snippet}");
            }

            var rawBody = await response.Content.ReadAsStringAsync(ct);
            JsonElement json;
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(rawBody, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError("获取 access_token 失败: 响应非 JSON, body={Body}", rawBody.Length > 500 ? rawBody[..500] : rawBody);
                throw new InvalidOperationException($"获取 access_token 失败: 响应非 JSON ({rawBody[..Math.Min(200, rawBody.Length)]})", ex);
            }

            // QQ 官方在凭证错误/限流时可能返回 HTTP 200 + {"code":xxx,"message":"..."}
            if (!json.TryGetProperty("access_token", out var tokenEl) || string.IsNullOrEmpty(tokenEl.GetString()))
            {
                var snippet = rawBody.Length > 500 ? rawBody[..500] : rawBody;
                _logger.LogError("获取 access_token 失败: 响应无 access_token 字段, body={Body}", snippet);

                // 尝试提取官方业务错误码与消息
                if (json.TryGetProperty("code", out var codeEl) && json.TryGetProperty("message", out var msgEl))
                {
                    throw new InvalidOperationException($"获取 access_token 失败: code={codeEl}, message={msgEl.GetString()}");
                }
                throw new InvalidOperationException($"获取 access_token 失败: 响应无 access_token 字段, body: {snippet}");
            }

            var token = tokenEl.GetString()!;

            var expiresInElement = json.GetProperty("expires_in");
            int expiresIn = expiresInElement.ValueKind == JsonValueKind.Number
                ? expiresInElement.GetInt32()
                : int.Parse(expiresInElement.GetString()!);

            _token = token;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            _logger.LogInformation("access_token 获取成功，有效期 {ExpiresIn}s，到期时间 {ExpiresAt}", expiresIn, _expiresAt);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 access_token 异常");
            throw new InvalidOperationException("获取 access_token 失败", ex);
        }
    }
}
