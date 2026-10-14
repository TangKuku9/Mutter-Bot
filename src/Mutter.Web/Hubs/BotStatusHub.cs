using Microsoft.AspNetCore.SignalR;

namespace Mutter.Web.Hubs;

public class BotStatusHub : Hub
{
    private readonly ILogger<BotStatusHub> _logger;
    private readonly LiveLogBus _liveLogBus;

    public BotStatusHub(ILogger<BotStatusHub> logger, LiveLogBus liveLogBus)
    {
        _logger = logger;
        _liveLogBus = liveLogBus;
    }

    public async Task SubscribeGroup(long groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"group-{groupId}");
    }

    public async Task UnsubscribeGroup(long groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group-{groupId}");
    }

    /// <summary>
    /// 客户端订阅实时日志流；连接后先推送历史缓冲，再持续推送新日志
    /// </summary>
    public async Task SubscribeLogs()
    {
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

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);

        // 取消日志订阅，避免内存泄漏
        if (Context.Items.TryGetValue("logHandler", out var handler) && handler is Action<LogEntry> a)
        {
            _liveLogBus.LogEmitted -= a;
        }

        return base.OnDisconnectedAsync(exception);
    }
}
