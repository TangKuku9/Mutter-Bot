using Microsoft.Extensions.DependencyInjection;
using QQBot.Core.EventHandlers;
using QQBot.Core.Game.Ai;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Services;
using QQBot.Core.Interfaces;
using QQBot.Core.Services;

namespace QQBot.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessageParser, MessageParser>();
        services.AddHttpClient<IAiChatService, AiChatService>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<MessageRouter>();
        services.AddSingleton<GameService>();
        services.AddSingleton<BotCommandService>();
        services.AddSingleton<MessageEventHandler>();

        // 修仙RPG 游戏域
        services.AddSingleton<IdleSettlementEngine>();
        services.AddSingleton<BreakthroughEngine>();
        services.AddSingleton<DailyTaskEngine>();
        services.AddSingleton<AlchemyEngine>();
        services.AddSingleton<ExploreEngine>();
        services.AddSingleton<EventApplier>();
        services.AddSingleton<GameCommandRouter>();
        services.AddSingleton<CultivationGameService>();
        services.AddSingleton<EventGenerator>();
        services.AddSingleton<ActionReferee>();

        return services;
    }
}
