using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QQBot.Core;
using QQBot.Core.Configuration;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Infrastructure.Services;

public class QqBotApiService : IQqBotApiService
{
    private readonly HttpClient _httpClient;
    private readonly AccessTokenManager _tokenManager;
    private readonly ILogger<QqBotApiService> _logger;
    private readonly string _apiBaseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string BotId { get; private set; } = string.Empty;
    public string BotName { get; private set; } = string.Empty;

    public QqBotApiService(HttpClient httpClient, AccessTokenManager tokenManager, IOptions<QQBotOptions> options, ILogger<QqBotApiService> logger)
    {
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _logger = logger;
        _apiBaseUrl = options.Value.ApiBaseUrl;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Get, "/users/@me", null, ct);
        await EnsureSuccessAsync(resp, ct);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        BotId = json.GetProperty("id").GetString() ?? string.Empty;
        BotName = json.GetProperty("username").GetString() ?? string.Empty;

        _logger.LogInformation("QQ Bot 初始化完成: BotId={BotId}, BotName={BotName}", BotId, BotName);
    }

    public async Task SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
    {
        var body = SendMessageRequest.Text(content, msgId, msgSeq);
        var resp = await SendAsync(HttpMethod.Post, $"/v2/groups/{groupOpenId}/messages", body, ct);
        await EnsureSuccessAsync(resp, ct);

        _logger.LogDebug("已发送群消息: group={GroupOpenId}, msg={Message}", groupOpenId, content);
    }

    public async Task SendPrivateMessageAsync(string userOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
    {
        var body = SendMessageRequest.Text(content, msgId, msgSeq);
        var resp = await SendAsync(HttpMethod.Post, $"/v2/users/{userOpenId}/messages", body, ct);
        await EnsureSuccessAsync(resp, ct);

        _logger.LogDebug("已发送私聊消息: user={UserOpenId}, msg={Message}", userOpenId, content);
    }

    public async Task<string?> UploadMediaAsync(string groupOpenId, int fileType, string url, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                group_openid = groupOpenId,
                file_type = fileType,
                url = url,
                srv_send_msg = false
            };
            var resp = await SendAsync(HttpMethod.Post, "/v2/files/upload", body, ct);
            await EnsureSuccessAsync(resp, ct);

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            return json.GetProperty("file_info").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上传媒体失败: group={GroupOpenId}, fileType={FileType}", groupOpenId, fileType);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        var token = await _tokenManager.GetTokenAsync(ct);
        var request = new HttpRequestMessage(method, $"{_apiBaseUrl.TrimEnd('/')}{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);

        if (body != null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await _httpClient.SendAsync(request, ct);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        QqApiResult? result = null;
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<QqApiResult>(content, JsonOptions);
            }
            catch (JsonException)
            {
            }
        }

        if (!resp.IsSuccessStatusCode || (result != null && !result.IsSuccess))
        {
            var errCode = result?.ErrCode ?? (int)resp.StatusCode;
            var message = result?.Message ?? resp.ReasonPhrase ?? resp.StatusCode.ToString();
            var traceId = result?.TraceId;
            throw new QqApiException(errCode, message, traceId);
        }
    }
}
