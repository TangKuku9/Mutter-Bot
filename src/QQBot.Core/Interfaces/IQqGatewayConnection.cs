using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Interfaces;

public interface IQqGatewayConnection
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }
    event EventHandler<bool>? ConnectionStateChanged;
    event EventHandler<QqDispatch>? DispatchReceived;
}
