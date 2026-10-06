using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Interfaces;

public interface IQqGatewayConnection
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }

    /// <summary>本次启动以来按事件类型统计的到达次数；未接网关的实现返回空集合。</summary>
    IReadOnlyDictionary<string, long> EventCounts { get; }
    event EventHandler<bool>? ConnectionStateChanged;
    event EventHandler<QqDispatch>? DispatchReceived;
}
