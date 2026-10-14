using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mutter.Core;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.QQOfficial;

namespace Mutter.Infrastructure.Services;

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

    public QqBotApiService(HttpClient httpClient, AccessTokenManager tokenManager, IOptions<MutterOptions> options, ILogger<QqBotApiService> logger)
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

        _logger.LogInformation("Mutter 初始化完成: BotId={BotId}, BotName={BotName}", BotId, BotName);
    }

    public async Task<string?> SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
    {
        return await SendGroupRichAsync(groupOpenId, OutboundMessage.Text(content, msgId, msgSeq), ct);
    }

    public async Task<string?> SendPrivateMessageAsync(string userOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
    {
        return await SendPrivateRichAsync(userOpenId, OutboundMessage.Text(content, msgId, msgSeq), ct);
    }

    public async Task<string?> SendGroupRichAsync(string groupOpenId, OutboundMessage msg, CancellationToken ct = default)
    {
        var body = SendMessageRequest.From(msg);
        var resp = await SendAsync(HttpMethod.Post, $"/v2/groups/{groupOpenId}/messages", body, ct);
        await EnsureSuccessAsync(resp, ct);

        var sentId = await ReadSentMessageIdAsync(resp, ct);
        _logger.LogDebug(
            "已发送群消息: group={GroupOpenId}, msgType={MsgType}, keyboard={HasKeyboard}, sentId={SentId}, msg={Message}",
            groupOpenId, body.MsgType, body.Keyboard is not null, sentId ?? "-", Describe(msg));
        return sentId;
    }

    public async Task<string?> SendPrivateRichAsync(string userOpenId, OutboundMessage msg, CancellationToken ct = default)
    {
        var body = SendMessageRequest.From(msg);
        var resp = await SendAsync(HttpMethod.Post, $"/v2/users/{userOpenId}/messages", body, ct);
        await EnsureSuccessAsync(resp, ct);

        var sentId = await ReadSentMessageIdAsync(resp, ct);
        _logger.LogDebug(
            "已发送私聊消息: user={UserOpenId}, msgType={MsgType}, sentId={SentId}, msg={Message}",
            userOpenId, body.MsgType, sentId ?? "-", Describe(msg));
        return sentId;
    }

    public async Task<bool> DeleteGroupMessageAsync(string groupOpenId, string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(groupOpenId) || string.IsNullOrEmpty(messageId)) return false;

        try
        {
            var resp = await SendAsync(HttpMethod.Delete, $"/v2/groups/{groupOpenId}/messages/{messageId}", null, ct);
            await EnsureSuccessAsync(resp, ct);
            return true;
        }
        catch (QqApiException ex) when (ex.ErrCode == 40064004)
        {
            _logger.LogDebug("撤回超时跳过（发送已超 2 分钟）: group={GroupOpenId}, messageId={MessageId}", groupOpenId, messageId);
            return false;
        }
        catch (QqApiException ex)
        {
            _logger.LogWarning("撤回群消息失败 err={ErrCode} group={GroupOpenId}, messageId={MessageId}", ex.ErrCode, groupOpenId, messageId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤回群消息异常 group={GroupOpenId}, messageId={MessageId}", groupOpenId, messageId);
            return false;
        }
    }

    /// <summary>发送接口响应体中携带平台分配的消息 id（形如 ROBOT1.0_...），解析失败返回 null。</summary>
    private async Task<string?> ReadSentMessageIdAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            return json.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task ReplyInteractionAsync(string interactionId, InteractionReply reply, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/interactions/{interactionId}", reply.ToWire(), ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>日志降噪：只记正文前 80 字预览，避免 markdown 长面板刷屏日志。</summary>
    private static string Describe(OutboundMessage msg)
    {
        var text = msg.UseMarkdown ? msg.MarkdownText : msg.PlainText;
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > 80 ? text[..80] + "…" : text;
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
