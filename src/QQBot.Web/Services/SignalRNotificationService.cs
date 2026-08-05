using Microsoft.AspNetCore.SignalR;
using QQBot.Core.Interfaces;
using QQBot.Web.Hubs;

namespace QQBot.Web.Services;

public class SignalRNotificationService : INotificationService
{
    private readonly IHubContext<BotStatusHub> _hubContext;

    public SignalRNotificationService(IHubContext<BotStatusHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyNewMessageAsync(object messageDto)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveMessage", messageDto);
    }

    public async Task NotifyConnectionStatusAsync(bool isConnected, string info)
    {
        await _hubContext.Clients.All.SendAsync("ConnectionStatusChanged", isConnected, info);
    }

    public async Task NotifyAiResponseStartedAsync(string? groupId, string senderName)
    {
        await _hubContext.Clients.All.SendAsync("AiResponseStarted", groupId, senderName);
    }

    public async Task NotifyAiResponseCompletedAsync(object responseDto)
    {
        await _hubContext.Clients.All.SendAsync("AiResponseCompleted", responseDto);
    }
}
