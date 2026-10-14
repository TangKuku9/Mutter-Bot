using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

public class ContextBuilder : IContextBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContextBuilder> _logger;

    private const double CharsPerToken = 2.0;
    private const int MinConversationTurns = 4;

    /// <summary>
    /// 私聊上下文的时间边界。私聊是两个人一问一答，断档超过这个时长就是另一件事了：
    /// 库里出现过 9-27 的"野鸡网址"和 10-06 的"咕咕嘎嘎"被拼成同一段对话，
    /// bot 因此回答了一个早已结束的话题。群聊不设这个边界（多人插话，间隔本就零散）。
    /// </summary>
    private static readonly TimeSpan PrivateContextMaxAge = TimeSpan.FromHours(2);

    public ContextBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<ContextBuilder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<BuildContextResult> BuildGroupContextAsync(
        string groupId, string currentMessage, string senderName, CancellationToken ct = default)
        => await BuildGroupContextAsync(groupId, currentMessage, senderName, null, ct);

    /// <summary>
    /// 建群聊上下文。<paramref name="overrideConfig"/> 供非 @ 回复的调用方（目前只有 AI 择机发言）
    /// 换掉"上下文条数 / 输出上限"这两个字段：它复用建对话与人设解析的全部逻辑，
    /// 但不再连带继承 AI 面板里为 @ 回复调好的那套规模参数 —— 两者的需求本来就不同。
    /// 传 null 即完全沿用全局配置（@ 回复与私聊的老行为）。
    /// </summary>
    public async Task<BuildContextResult> BuildGroupContextAsync(
        string groupId, string currentMessage, string senderName,
        Func<AiConfig, AiConfig>? overrideConfig, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var globalConfig = await configRepo.GetAiConfigAsync(ct) ?? new AiConfig();
        var groupConfig = await configRepo.GetGroupConfigAsync(groupId, ct);

        var effectiveConfig = ApplyGroupOverrides(globalConfig, groupConfig);
        if (overrideConfig is not null)
            effectiveConfig = overrideConfig(effectiveConfig);

        var history = await messageRepo.GetGroupHistoryAsync(
            groupId, effectiveConfig.MaxContextMessages * 2, ct);

        var conversation = BuildConversation(history, currentMessage, senderName, effectiveConfig);

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, effectiveConfig.MaxTokens);

        _logger.LogDebug("Group {GroupId} context built: {Count} msgs, ~{Tokens} tokens, truncated={Truncated}",
            groupId, truncatedConversation.Count, estimatedTokens, wasTruncated);

        var (personaConfig, characterPrompt) = ApplyPersona(
            effectiveConfig, groupConfig?.ActiveCharacter,
            await configRepo.GetCustomCharactersAsync(ct));

        return new BuildContextResult
        {
            Messages = truncatedConversation,
            EffectiveConfig = personaConfig,
            EstimatedTokens = estimatedTokens,
            MessageCount = truncatedConversation.Count,
            WasTruncated = wasTruncated,
            CharacterPrompt = characterPrompt
        };
    }

    public async Task<BuildContextResult> BuildPrivateContextAsync(
        string userId, string currentMessage, string senderName = "User", CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var config = await configRepo.GetAiConfigAsync(ct) ?? new AiConfig();

        var history = await messageRepo.GetPrivateHistoryAsync(
            userId, config.MaxContextMessages * 2, DateTime.UtcNow - PrivateContextMaxAge, ct);

        var conversation = BuildConversation(history, currentMessage, senderName, config);

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, config.MaxTokens);

        _logger.LogDebug("Private {UserId} context built: {Count} msgs, ~{Tokens} tokens",
            userId, truncatedConversation.Count, estimatedTokens);

        var (personaConfig, characterPrompt) = ApplyPersona(
            config, null, await configRepo.GetCustomCharactersAsync(ct));

        return new BuildContextResult
        {
            Messages = truncatedConversation,
            EffectiveConfig = personaConfig,
            EstimatedTokens = estimatedTokens,
            MessageCount = truncatedConversation.Count,
            WasTruncated = wasTruncated,
            CharacterPrompt = characterPrompt
        };
    }

    /// <summary>
    /// 解析人设。面板「人设」下拉框存进 AiConfig.Personality 的是角色 Name，
    /// 而 Personality 同时允许手填自由文本性格描述——两种语义共用一个字段。
    /// 命中已知角色名时改用该角色的 SystemPromptOverride，并把名字从 Personality 摘掉，
    /// 否则会把 "realdushe" 这类标识符当作性格描述原样塞进提示词（人设反而不生效）；
    /// 未命中则保持自由文本语义。群级 ActiveCharacter 优先于全局人设。
    /// </summary>
    private static (AiConfig Config, string? CharacterPrompt) ApplyPersona(
        AiConfig config, string? groupCharacter, IReadOnlyList<AiCharacter> customCharacters)
    {
        var characterPrompt = (string?)null;

        if (!string.IsNullOrWhiteSpace(config.Personality))
        {
            var globalCharacter = AiCharacter.Resolve(config.Personality, customCharacters);
            if (globalCharacter != null)
            {
                config = config with { Personality = string.Empty };
                if (!string.IsNullOrEmpty(globalCharacter.SystemPromptOverride))
                    characterPrompt = globalCharacter.SystemPromptOverride;
            }
        }

        if (!string.IsNullOrWhiteSpace(groupCharacter))
        {
            var resolved = AiCharacter.Resolve(groupCharacter, customCharacters);
            if (resolved != null && !string.IsNullOrEmpty(resolved.SystemPromptOverride))
                characterPrompt = resolved.SystemPromptOverride;
        }

        return (config, characterPrompt);
    }

    private List<(string Role, string Content)> BuildConversation(
        List<ChatMessage> history, string currentMessage, string senderName, AiConfig config)
    {
        var conversation = new List<(string Role, string Content)>();

        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.PlainText))
                continue;

            var text = msg.PlainText.Replace("[Image]", "").Replace("[Voice]", "")
                .Replace("[Video]", "").Trim();
            if (string.IsNullOrWhiteSpace(text) && !msg.PlainText.Contains("[@Bot]"))
                continue;

            if (msg.IsBotMessage)
            {
                // 跳过游戏系统回复（非AI生成），避免AI模仿系统文本风格而脱离角色
                if (msg.AiModelUsed is null)
                    continue;
                conversation.Add(("assistant", msg.PlainText));
            }
            else
            {
                // 此前这里会跳过"游戏指令消息"（签到/突破/修行等）以免造成
                // "说了没人答"的半截对话、让 AI 脑补系统回复。该功能已随修仙域移到
                // xianxia 分支，主分支不再有这类消息，故移除该过滤。
                // 若将来并入其它"用户指令 + 系统代答"型功能，需把这条排除逻辑加回来。
                conversation.Add(("user", $"[{msg.UserName}]: {msg.PlainText}"));
            }
        }

        if (conversation.Count > config.MaxContextMessages)
        {
            conversation = conversation.TakeLast(config.MaxContextMessages).ToList();
        }

        // 本条消息在建上下文前就已落库，会原样出现在 history 末尾；不去掉就拼两遍，
        // 模型会把同一句话读成"对方连着追问两次"。是否重复取决于后台写库有没有跑赢这次查询，
        // 时好时坏的毛病多半就是这么来的。
        var current = $"[{senderName}]: {currentMessage}";
        if (conversation.Count > 0 && conversation[^1].Content == current)
            conversation.RemoveAt(conversation.Count - 1);

        conversation.Add(("user", current));

        return conversation;
    }

    private (List<(string Role, string Content)> Messages, int EstimatedTokens, bool WasTruncated)
        TruncateToBudget(List<(string Role, string Content)> conversation, int maxTokens)
    {
        var tokenBudget = (int)(maxTokens * 0.75);

        var totalChars = conversation.Sum(m => m.Content.Length);
        var estimatedTokens = (int)(totalChars / CharsPerToken);

        if (estimatedTokens <= tokenBudget)
            return (conversation, estimatedTokens, false);

        var minMessages = MinConversationTurns * 2;
        var truncated = new List<(string Role, string Content)>(conversation);

        while (truncated.Count > minMessages)
        {
            totalChars = truncated.Sum(m => m.Content.Length);
            estimatedTokens = (int)(totalChars / CharsPerToken);

            if (estimatedTokens <= tokenBudget)
                return (truncated, estimatedTokens, true);

            truncated.RemoveAt(0);
        }

        totalChars = truncated.Sum(m => m.Content.Length);
        estimatedTokens = (int)(totalChars / CharsPerToken);

        return (truncated, estimatedTokens, true);
    }

    private static AiConfig ApplyGroupOverrides(AiConfig globalConfig, GroupConfig? groupConfig)
    {
        if (groupConfig == null) return globalConfig;

        // 只覆盖有群级差异的字段，其余（含 WebSearchEnabled 等）一律继承全局，
        // 避免手工逐字段拷贝时漏掉新增字段。
        return globalConfig with
        {
            SystemPrompt = groupConfig.CustomSystemPrompt ?? globalConfig.SystemPrompt,
            Temperature = groupConfig.CustomTemperature ?? globalConfig.Temperature
        };
    }

}
