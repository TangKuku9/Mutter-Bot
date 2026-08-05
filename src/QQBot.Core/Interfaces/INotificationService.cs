namespace QQBot.Core.Interfaces;

public interface INotificationService
{
    Task NotifyNewMessageAsync(object messageDto);
    Task NotifyConnectionStatusAsync(bool isConnected, string info);
    Task NotifyAiResponseStartedAsync(string? groupId, string senderName);
    Task NotifyAiResponseCompletedAsync(object responseDto);
}
