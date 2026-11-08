using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

public class ContextBuilder : IContextBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContextBuilder> _logger;

    /// <summary>首次向数据库请求的候选条数。</summary>
    private const int InitialHistoryCandidates = 40;

    /// <summary>
    /// 单次最多从库里取多少条候选。取候选而不是"直接取 N 条"，是因为历史里有些消息
    /// （纯图片、非 AI 生成的 bot 回复）会在建对话时被丢掉 —— 直接按 N 取会让实际
    /// 条数少于面板设定值。多取一些再裁到 N，面板上的数字才作数。
    /// </summary>
    private const int MaxHistoryCandidates = 200;

    /// <summary>
    /// 从 <paramref name="initialCandidates"/> 条候选起步、每次翻倍，直到条数够用或已到上限。
    /// 返回有界列表（到上限即止，不重复末值），调用方可以直接 Last()/Count，
    /// 不必担心惰性序列在封顶后无限产出同一个值。
    /// 抽成纯函数是为了能被自检直接覆盖：它保证"面板填 N 条"时不会因为过滤而缩水。
    /// </summary>
    public static List<int> CandidateFetchLadder(int maxContextMessages, int initialCandidates)
    {
        // 天真的 Math.Clamp(target, min, max) 在 target > max 时会抛
        // "min cannot be greater than max"，所以先夹 target 再决定起点
        var target = Math.Clamp(maxContextMessages, 1, MaxHistoryCandidates);
        var n = Math.Clamp(initialCandidates, target, MaxHistoryCandidates);

        var ladder = new List<int> { n };
        while (n < MaxHistoryCandidates)
        {
            n = Math.Min(n * 2, MaxHistoryCandidates);
            ladder.Add(n);
        }

        return ladder;
    }

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

        // 历史里的纯图片/非 AI bot 行会被 BuildConversation 丢掉，所以按阶梯多取候选，
        // 直到条数够用为止 —— 面板填 N 条就得真给 N 条，不能因为过滤而缩水。
        var candidatesFetched = 0;
        var conversation = new List<(string Role, string Content)>();

        foreach (var candidates in CandidateFetchLadder(
                     effectiveConfig.MaxContextMessages, InitialHistoryCandidates))
        {
            var history = await messageRepo.GetGroupHistoryAsync(groupId, candidates, ct);
            candidatesFetched = history.Count;
            conversation = BuildConversation(history, currentMessage, senderName, effectiveConfig);

            // 够条数，或已经把库里的候选取尽（再翻倍也不会有更多）
            if (conversation.Count >= effectiveConfig.MaxContextMessages || history.Count < candidates)
                break;
        }

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, effectiveConfig.MaxTokens, effectiveConfig.ContextWindowTokens, effectiveConfig.MaxInputTokens);

        // 候选数与实际条数一起打出来：缩水时一眼可见，不用再猜
        _logger.LogDebug(
            "Group {GroupId} context built: 候选 {Candidates} → {Count} 条, ~{Tokens} tokens, truncated={Truncated}",
            groupId, candidatesFetched, truncatedConversation.Count, estimatedTokens, wasTruncated);

        var (personaConfig, characterPrompt) = ApplyPersona(
            effectiveConfig, groupConfig?.ActiveCharacter,
            await configRepo.GetCustomCharactersAsync(ct));

        // 主动工作流传入 overrideConfig，并自行按它的独立开关注入，避免重复拼接。
        var memory = overrideConfig is null && effectiveConfig.GroupMemoryInjectionEnabled
            && groupConfig?.MemoryInjectionEnabled != false
            ? await scope.ServiceProvider.GetRequiredService<IGroupMemoryRepository>().GetAsync(groupId, ct)
            : null;

        return new BuildContextResult
        {
            Messages = truncatedConversation,
            EffectiveConfig = personaConfig,
            EstimatedTokens = estimatedTokens,
            MessageCount = truncatedConversation.Count,
            WasTruncated = wasTruncated,
            GroupMemory = memory?.Summary,
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

        // 与群聊同样按阶梯取候选：私聊里非 AI 生成的 bot 行与纯图片同样会被丢掉
        var since = DateTime.UtcNow - PrivateContextMaxAge;
        var candidatesFetched = 0;
        var conversation = new List<(string Role, string Content)>();

        foreach (var candidates in CandidateFetchLadder(
                     config.MaxContextMessages, InitialHistoryCandidates))
        {
            var history = await messageRepo.GetPrivateHistoryAsync(userId, candidates, since, ct);
            candidatesFetched = history.Count;
            conversation = BuildConversation(history, currentMessage, senderName, config);

            if (conversation.Count >= config.MaxContextMessages || history.Count < candidates)
                break;
        }

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, config.MaxTokens, config.ContextWindowTokens, config.MaxInputTokens);

        _logger.LogDebug("Private {UserId} context built: 候选 {Candidates} → {Count} 条, ~{Tokens} tokens",
            userId, candidatesFetched, truncatedConversation.Count, estimatedTokens);

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
            if (ConversationTurnScope.Current is { } turn && turn.MessageIds.Contains(msg.MessageId))
                continue;
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
        var current = ConversationTurnScope.Current?.CombinedInput ?? $"[{senderName}]: {currentMessage}";
        if (conversation.Count > 0 && conversation[^1].Content == current)
            conversation.RemoveAt(conversation.Count - 1);

        conversation.Add(("user", current));

        return conversation;
    }

    /// <summary>输入预算独立于回复长度；最终系统词和图片由 AiRequestBudget 再统一检查。</summary>
    public static int InputTokenBudget(int maxTokens) => AiRequestBudget.InputBudget(maxTokens);

    public (List<(string Role, string Content)> Messages, int EstimatedTokens, bool WasTruncated)
        TruncateToBudget(List<(string Role, string Content)> conversation, int maxTokens,
            int contextWindow = AiRequestBudget.DefaultContextWindow,
            int maxInputTokens = AiRequestBudget.DefaultInputBudget)
    {
        var budget = AiRequestBudget.InputBudget(maxTokens, contextWindow, maxInputTokens);
        var trimmed = conversation.ToList();
        while (trimmed.Count > 1 && EstimateTokens(trimmed) > budget)
            trimmed.RemoveAt(0);
        return (trimmed, EstimateTokens(trimmed), trimmed.Count != conversation.Count);
    }

    private static int EstimateTokens(List<(string Role, string Content)> conversation) =>
        conversation.Sum(m => AiRequestBudget.EstimateText(m.Content) + 8);

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
