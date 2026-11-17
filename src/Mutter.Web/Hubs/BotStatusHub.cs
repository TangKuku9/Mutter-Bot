using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using Mutter.Web.Services;

namespace Mutter.Web.Hubs;

public class BotStatusHub : Hub
{
    private readonly ILogger<BotStatusHub> _logger;
    private readonly LiveLogBus _liveLogBus;
    private readonly PanelSessionStore _sessions;

    public BotStatusHub(ILogger<BotStatusHub> logger, LiveLogBus liveLogBus, PanelSessionStore sessions)
    {
        _logger = logger;
        _liveLogBus = liveLogBus;
        _sessions = sessions;
    }

    public async Task SubscribeGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 256) throw new HubException("无效的群标识");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"group-{groupId}");
    }

    public async Task UnsubscribeGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 256) throw new HubException("无效的群标识");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group-{groupId}");
    }

    /// <summary>
    /// 客户端订阅实时日志流；连接后先推送历史缓冲，再持续推送新日志
    /// </summary>
    public async Task SubscribeLogs()
    {
        await UnsubscribeLogs();
        // 先推送历史日志
        var recent = _liveLogBus.GetRecentLogs();
        if (recent.Count > 0)
        {
            await Clients.Caller.SendAsync("ReceiveLogs", recent);
        }

        // 订阅新日志
        void OnLogEmitted(LogEntry entry)
        {
            _ = Clients.Caller.SendAsync("ReceiveLog", entry);
        }

        _liveLogBus.LogEmitted += OnLogEmitted;
        Context.Items["logHandler"] = (Action<LogEntry>)OnLogEmitted;
    }

    public Task UnsubscribeLogs()
    {
        if (Context.Items.Remove("logHandler", out var handler) && handler is Action<LogEntry> action)
            _liveLogBus.LogEmitted -= action;
        return Task.CompletedTask;
    }

    public override Task OnConnectedAsync()
    {
        var token = Context.User?.FindFirstValue(ClaimTypes.Sid);
        if (!_sessions.TryValidate(token, out var session, renew: false))
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        Context.Items["sessionRevocation"] = session.Revocation.Register(Context.Abort);
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove("sessionRevocation", out var registration) && registration is IDisposable disposable)
            disposable.Dispose();
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);

        // 取消日志订阅，避免内存泄漏
        if (Context.Items.TryGetValue("logHandler", out var handler) && handler is Action<LogEntry> a)
        {
            _liveLogBus.LogEmitted -= a;
        }

        return base.OnDisconnectedAsync(exception);
    }
}
