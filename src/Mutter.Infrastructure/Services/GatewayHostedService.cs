using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;

namespace Mutter.Infrastructure.Services;

/// <summary>宿主管理接入任务；事件处理可等待，关闭时先停止接入，再排空会话工作。</summary>
public sealed class GatewayHostedService(
    AccessTokenManager tokens, IQqGatewayConnection gateway, IQqBotApiService api,
    MessageEventHandler messages, INotificationService notifications,
    ILogger<GatewayHostedService> logger) : BackgroundService
{
    private readonly Channel<bool> _states = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        gateway.DispatchReceived += messages.HandleDispatchAsync;
        gateway.ConnectionStateChanged += OnConnectionStateChanged;
        try
        {
            await tokens.InitializeAsync(stoppingToken);
            try
            {
                await api.InitializeAsync(stoppingToken);
                messages.SetBotId(api.BotId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Bot 信息初始化失败，连接后重试"); }
            await gateway.ConnectAsync(stoppingToken);
            await foreach (var connected in _states.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (connected && string.IsNullOrEmpty(api.BotId))
                    {
                        await api.InitializeAsync(stoppingToken);
                        messages.SetBotId(api.BotId);
                    }
                    await notifications.NotifyConnectionStatusAsync(connected, connected ? "已连接" : "已断开");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "网关状态通知失败"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "QQ 网关启动失败"); }
        finally
        {
            // 先让已接纳事件完成交接，再解除处理器；否则排空只会读走事件而不落库。
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await gateway.DisconnectAsync(deadline.Token);
            }
            finally
            {
                gateway.DispatchReceived -= messages.HandleDispatchAsync;
                gateway.ConnectionStateChanged -= OnConnectionStateChanged;
            }
        }
    }

    private void OnConnectionStateChanged(object? sender, bool connected) => _states.Writer.TryWrite(connected);
}
