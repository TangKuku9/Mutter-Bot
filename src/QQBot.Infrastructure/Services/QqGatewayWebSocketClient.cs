using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QQBot.Core.Configuration;
using QQBot.Core.Interfaces;
using QQBot.Core.Services;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Infrastructure.Services;

public class QqGatewayWebSocketClient : IQqGatewayConnection, IDisposable
{
    private readonly AccessTokenManager _tokenManager;
    private readonly ILogger<QqGatewayWebSocketClient> _logger;
    private readonly string _apiBaseUrl;

    // INTERACTION intent 安全网：订阅未获授权的 intent 会导致 Identify 直接断连（全站离线），
    // 故带上后若仍秒断，自动摘除该 intent 重连。
    private readonly bool _interactionIntentRequested;
    private readonly RichUiCapability _richUi;
    private volatile bool _interactionIntentEnabled;
    private DateTime _lastIdentifyAt = DateTime.MinValue;
    private int _intentQuickFailStreak;
    private static readonly TimeSpan IntentQuickFailWindow = TimeSpan.FromSeconds(5);

    private static readonly HttpClient _http = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private Task? _heartbeatTask;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _eventCounts = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _seqLock = new();
    private int? _lastSeq;
    private string? _sessionId;
    private volatile int _heartbeatInterval;
    private volatile bool _heartbeatAckReceived;
    private volatile bool _identified;

    private int _reconnectDelay = 1000;
    private const int MaxReconnectDelay = 30000;
    private const int MaxMessageSize = 1024 * 1024;

    private static readonly JsonSerializerOptions ReceiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SendJsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                ConnectionStateChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<QqDispatch>? DispatchReceived;

    /// <summary>
    /// 本次进程启动以来各事件类型的到达计数。面板用它区分“平台没下发全量消息”
    /// 与“下发了我们没落库”——这两种故障在群里看起来一模一样。
    /// </summary>
    public IReadOnlyDictionary<string, long> EventCounts => CloneEventCounts();

    private Dictionary<string, long> CloneEventCounts()
    {
        var snapshot = new Dictionary<string, long>(_eventCounts.Count);
        foreach (var kv in _eventCounts) snapshot[kv.Key] = kv.Value;
        return snapshot;
    }

    public QqGatewayWebSocketClient(
        AccessTokenManager tokenManager, IOptions<QQBotOptions> options,
        RichUiCapability richUi, ILogger<QqGatewayWebSocketClient> logger)
    {
        _tokenManager = tokenManager;
        _logger = logger;
        _apiBaseUrl = options.Value.ApiBaseUrl;
        _richUi = richUi;
        _interactionIntentRequested = options.Value.RichUi.InteractionIntent;
        _interactionIntentEnabled = _interactionIntentRequested;
        if (_interactionIntentRequested)
            _logger.LogInformation("RichUi: 已配置订阅 INTERACTION intent（1<<26），未获平台授权时会自动降级");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
        await WaitForConnectionAsync(_cts.Token);
    }

    private async Task WaitForConnectionAsync(CancellationToken ct)
    {
        var timeout = 30;
        for (var i = 0; i < timeout * 10 && !IsConnected && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CancellationTokenSource? iterCts = null;
            try
            {
                iterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _identified = false;

                _ws?.Dispose();
                _ws = new ClientWebSocket();

                var token = await _tokenManager.GetTokenAsync(ct);
                var gatewayUrl = await GetGatewayUrlAsync(token, ct);

                _logger.LogInformation("正在连接 QQ 官方 Gateway WebSocket: {Url}", gatewayUrl);
                await _ws.ConnectAsync(new Uri(gatewayUrl), ct);

                var hello = await ReceiveSingleFrameAsync(iterCts.Token);
                if (hello.Op != GatewayOp.Hello || hello.D is null)
                    throw new InvalidOperationException($"期望 Hello(op=10)，实际收到 op={hello.Op}");

                var helloData = hello.D.Value.Deserialize<HelloData>(ReceiveJsonOptions)
                    ?? throw new InvalidOperationException("Hello 数据反序列化失败");
                _heartbeatInterval = helloData.HeartbeatInterval;
                _logger.LogInformation("收到 Hello，心跳间隔 {Interval}ms", _heartbeatInterval);

                if (!string.IsNullOrEmpty(_sessionId))
                {
                    _logger.LogInformation("使用已有 session 恢复连接: {SessionId}, seq={Seq}", _sessionId, GetLastSeq());
                    await SendResumeAsync(token, iterCts.Token);
                }
                else
                {
                    await SendIdentifyAsync(token, iterCts.Token);
                }

                _heartbeatAckReceived = true;
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(iterCts.Token));

                await ReceiveLoopAsync(iterCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("QQ Gateway 连接已取消");
                break;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogWarning(ex, "QQ Gateway 连接断开，{Delay}ms 后重连", _reconnectDelay);
                MaybeDegradeInteractionIntent("连接断开");
                try
                {
                    await Task.Delay(_reconnectDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _reconnectDelay = Math.Min(_reconnectDelay * 2, MaxReconnectDelay);
            }
            finally
            {
                iterCts?.Cancel();
                if (_heartbeatTask is not null)
                {
                    try { await _heartbeatTask; }
                    catch { }
                    _heartbeatTask = null;
                }
                iterCts?.Dispose();
            }
        }

        IsConnected = false;
    }

    private async Task<string> GetGatewayUrlAsync(string token, CancellationToken ct)
    {
        var url = $"{_apiBaseUrl.TrimEnd('/')}/gateway";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"QQBot {token}");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 200 ? body[..200] : body;
            _logger.LogError("获取 Gateway URL 失败: status={Status}, body={Body}", response.StatusCode, snippet);
            throw new InvalidOperationException($"获取 Gateway URL 失败: {(int)response.StatusCode} {response.StatusCode}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ReceiveJsonOptions, ct);
        var wssUrl = json.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Gateway 响应中 url 为空");
        return wssUrl;
    }

    private async Task<GatewayPayload> ReceiveSingleFrameAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket 在收到 Hello 前关闭");

            messageBuffer.Write(buffer, 0, result.Count);

            if (messageBuffer.Length > MaxMessageSize)
                throw new InvalidOperationException("Hello 消息超过最大长度");
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
        return JsonSerializer.Deserialize<GatewayPayload>(json, ReceiveJsonOptions)
            ?? throw new InvalidOperationException("Hello 消息反序列化失败");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("收到 WebSocket 关闭消息");
                        IsConnected = false;
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);

                    if (messageBuffer.Length > MaxMessageSize)
                    {
                        _logger.LogWarning("收到超大消息，已丢弃: {Size} bytes", messageBuffer.Length);
                        messageBuffer.SetLength(0);
                        break;
                    }
                } while (!result.EndOfMessage);

                if (messageBuffer.Length == 0 && !result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                await ProcessPayloadAsync(json, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket 接收消息异常");
                throw;
            }
        }
    }

    private async Task ProcessPayloadAsync(string json, CancellationToken ct)
    {
        GatewayPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GatewayPayload>(json, ReceiveJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gateway 消息反序列化失败");
            return;
        }

        if (payload is null)
            return;

        switch (payload.Op)
        {
            case GatewayOp.Hello:
                if (payload.D is not null)
                {
                    var hello = payload.D.Value.Deserialize<HelloData>(ReceiveJsonOptions);
                    if (hello is not null && hello.HeartbeatInterval > 0)
                    {
                        _heartbeatInterval = hello.HeartbeatInterval;
                        _logger.LogDebug("心跳间隔更新为 {Interval}ms", _heartbeatInterval);
                    }
                }
                break;

            case GatewayOp.Dispatch:
                HandleDispatch(payload);
                break;

            case GatewayOp.HeartbeatAck:
                _heartbeatAckReceived = true;
                _logger.LogDebug("收到心跳 ACK");
                break;

            case GatewayOp.Reconnect:
                _logger.LogInformation("收到 Reconnect(7)，服务器要求重连");
                TriggerReconnect();
                break;

            case GatewayOp.InvalidSession:
                HandleInvalidSession(payload);
                break;

            case GatewayOp.Heartbeat:
                _logger.LogDebug("收到服务器主动心跳请求");
                await SendJsonAsync(new { op = GatewayOp.Heartbeat, d = GetLastSeq() }, ct);
                break;

            default:
                _logger.LogWarning("收到未知 op={Op}", payload.Op);
                break;
        }
    }

    private void HandleDispatch(GatewayPayload payload)
    {
        if (payload.S.HasValue)
            SetLastSeq(payload.S.Value);

        if (payload.T == "READY")
        {
            if (payload.D is not null)
            {
                try
                {
                    var ready = payload.D.Value.Deserialize<ReadyData>(ReceiveJsonOptions);
                    if (ready is not null)
                    {
                        _sessionId = ready.SessionId;
                        _logger.LogInformation("收到 READY，session_id={SessionId}, version={Version}, user={Username}",
                            _sessionId, ready.Version, ready.User.Username);
                    }
                }
                catch (Exception ex)
                {
                    // READY 反序列化失败不应让整个连接挂掉；标记为已连接，下次重连重新 Identify
                    _logger.LogError(ex, "READY 反序列化失败，仍标记为已连接");
                }
            }
            _identified = true;
            _reconnectDelay = 1000;
            IsConnected = true;
            NoteIdentifySucceeded();
        }
        else if (payload.T == "RESUMED")
        {
            // Resume 成功后服务端下发 RESUMED 事件（d 为空字符串）
            _identified = true;
            _reconnectDelay = 1000;
            IsConnected = true;
            _logger.LogInformation("收到 RESUMED，session 已恢复: {SessionId}, seq={Seq}", _sessionId, GetLastSeq());
        }

        if (payload.D is not null && payload.T is not null)
        {
            var dispatch = new QqDispatch(payload.T, payload.Id, payload.S ?? 0, payload.D.Value);
            var arrived = _eventCounts.AddOrUpdate(payload.T, 1, (_, c) => c + 1);
            if (payload.T == "GROUP_MESSAGE_CREATE" && arrived == 1)
                _logger.LogInformation("首次收到 GROUP_MESSAGE_CREATE：群全量消息下发已打通，围观与择机发言有数据可用");
            if (payload.T == "GROUP_AT_MESSAGE_CREATE" && arrived == 1)
                _logger.LogInformation("首次收到 GROUP_AT_MESSAGE_CREATE：@ 机器人事件正常下发");
            try
            {
                DispatchReceived?.Invoke(this, dispatch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch 处理异常，eventType={EventType}", payload.T);
            }
        }
    }

    private void HandleInvalidSession(GatewayPayload payload)
    {
        var resumable = payload.D is not null && payload.D.Value.ValueKind == JsonValueKind.True;
        if (!resumable)
        {
            _logger.LogInformation("收到 InvalidSession(9)，不可恢复，清除 session 重新 Identify");
            _sessionId = null;
            ClearSeq();
        }
        else
        {
            _logger.LogInformation("收到 InvalidSession(9)，可恢复，尝试 Resume");
        }
        MaybeDegradeInteractionIntent("InvalidSession");
        TriggerReconnect();
    }

    /// <summary>Identify 成功后清零快断计数（证明当前 intents 组合被平台接受）。</summary>
    private void NoteIdentifySucceeded()
    {
        Volatile.Write(ref _intentQuickFailStreak, 0);
    }

    /// <summary>
    /// 若本次连接是“发出 Identify 后 5 秒内就断”，且当时带了 INTERACTION intent，
    /// 连续两次则判定为 intent 未授权，自动摘除后重连，避免机器人因配置错误整体离线。
    /// </summary>
    private void MaybeDegradeInteractionIntent(string where)
    {
        if (!_interactionIntentEnabled) return;

        var last = _lastIdentifyAt;
        if (last == DateTime.MinValue) return;
        var since = DateTime.UtcNow - last;
        if (since > IntentQuickFailWindow) return;

        var streak = Interlocked.Increment(ref _intentQuickFailStreak);
        if (streak < 2) return;

        _interactionIntentEnabled = false;
        // 告知 UI 层：收不到互动事件了，不得再下发回调按钮（会自动退回指令按钮/文字链）
        _richUi.ReportInteractionUnavailable();
        _logger.LogError(
            "RichUi: {Where} 发生在 Identify 后 {Sec:F1}s 内（连续 {N} 次），判定 INTERACTION intent 未获授权，"
            + "已自动降级为基础 intents。请先在开放平台申请事件权限，再开启 QQBot:RichUi:InteractionIntent",
            where, since.TotalSeconds, streak);
    }

    private void TriggerReconnect()
    {
        IsConnected = false;
        try
        {
            if (_ws is not null && _ws.State == WebSocketState.Open)
                _ws.Abort();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "触发重连时关闭 WebSocket 异常");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_heartbeatInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (ct.IsCancellationRequested || _ws?.State != WebSocketState.Open)
                break;

            try
            {
                var seq = GetLastSeq();
                _heartbeatAckReceived = false;
                await SendJsonAsync(new { op = GatewayOp.Heartbeat, d = seq }, ct);
                _logger.LogDebug("已发送心跳，seq={Seq}, identified={Identified}", seq, _identified);

                var ackTimeout = _heartbeatInterval * 2;
                var slice = Math.Max(_heartbeatInterval / 10, 100);
                var waited = 0;
                while (waited < ackTimeout && !ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    if (_heartbeatAckReceived)
                        break;
                    var step = Math.Min(slice, ackTimeout - waited);
                    try
                    {
                        await Task.Delay(step, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    waited += step;
                }

                if (!_heartbeatAckReceived)
                {
                    _logger.LogWarning("心跳未收到 ACK（等待 {Ms}ms），触发重连", ackTimeout);
                    TriggerReconnect();
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "心跳任务异常，触发重连");
                TriggerReconnect();
                return;
            }
        }
    }

    private async Task SendIdentifyAsync(string token, CancellationToken ct)
    {
        var intents = GatewayOp.GroupAndC2CIntent
            | (_interactionIntentEnabled ? GatewayOp.InteractionIntent : 0);
        var payload = new
        {
            op = GatewayOp.Identify,
            d = new IdentifyData
            {
                Token = $"QQBot {token}",
                Intents = intents,
                Shard = new[] { 0, 1 }
            }
        };
        _lastIdentifyAt = DateTime.UtcNow;
        await SendJsonAsync(payload, ct);
        _logger.LogInformation("已发送 Identify，intents={Intents}（含 INTERACTION={WithInteraction}）",
            intents, _interactionIntentEnabled);
    }

    private async Task SendResumeAsync(string token, CancellationToken ct)
    {
        var payload = new
        {
            op = GatewayOp.Resume,
            d = new ResumeData
            {
                Token = $"QQBot {token}",
                SessionId = _sessionId!,
                Seq = GetLastSeq() ?? 0
            }
        };
        await SendJsonAsync(payload, ct);
        _logger.LogInformation("已发送 Resume，session_id={SessionId}, seq={Seq}", _sessionId, GetLastSeq());
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open)
                return;
            var json = JsonSerializer.Serialize(payload, SendJsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private int? GetLastSeq()
    {
        lock (_seqLock) return _lastSeq;
    }

    private void SetLastSeq(int value)
    {
        lock (_seqLock) _lastSeq = value;
    }

    private void ClearSeq()
    {
        lock (_seqLock) _lastSeq = null;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        CancelCts();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭 WebSocket 时异常");
            }
        }

        _identified = false;
        IsConnected = false;
        _logger.LogInformation("QQ Gateway 已断开");
    }

    public void Dispose()
    {
        // 主机停机路径上可能被调用两次（Host.DisposeAsync 与 DI 作用域释放）；
        // 第二次对已释放的 CTS 调 Cancel 会抛 ObjectDisposedException 并让进程 abort。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancelCts();
        _cts?.Dispose();
        _ws?.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CancelCts()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已被并发释放，取消意图已达成，忽略
        }
    }
}
