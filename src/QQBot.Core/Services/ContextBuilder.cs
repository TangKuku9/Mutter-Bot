using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Services;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

public class ContextBuilder : IContextBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContextBuilder> _logger;
    private readonly GameCommandRouter _gameCommandRouter;

    /// <summary>
    /// 字符数换算 token 的粗略比例。中文实测约 1.5 字符/token，这里取 2.0 是刻意偏保守
    /// （宁可低估 token 数）。它只是估算，不做精确计数——项目刻意不引入 tokenizer。
    /// </summary>
    private const double CharsPerToken = 2.0;

    /// <summary>裁剪 token 时至少保留这么多条，否则对话只剩一两句时 AI 会答非所问。</summary>
    private const int MinRemainingMessages = 8;

    /// <summary>首次向数据库请求的候选条数。</summary>
    private const int InitialHistoryCandidates = 40;

    /// <summary>
    /// 单次最多从库里取多少条候选。取候选而不是"直接取 N 条"，是因为历史里有些消息
    /// （纯图片、非 AI 生成的 bot 回复）会在建对话时被丢掉 —— 直接按 N 取会让实际
    /// 条数少于面板设定值。多取一些再裁到 N，面板上的数字才作数。
    /// </summary>
    private const int MaxHistoryCandidates = 200;

    /// <summary>
    /// 输入预算的换算系数：输出上限留一部分给回复本身，其余当作输入的可用额度。
    /// 这是个保守的估计（不是精确 token 计数），只用于兜住"对话长得离谱"的情况。
    /// </summary>
    private const double InputBudgetRatio = 0.75;

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
        ILogger<ContextBuilder> logger,
        GameCommandRouter gameCommandRouter)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _gameCommandRouter = gameCommandRouter;
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
            TruncateToBudget(conversation, effectiveConfig.MaxTokens);

        // 候选数与实际条数一起打出来：缩水时一眼可见，不用再猜
        _logger.LogDebug(
            "Group {GroupId} context built: 候选 {Candidates} → {Count} 条, ~{Tokens} tokens, truncated={Truncated}",
            groupId, candidatesFetched, truncatedConversation.Count, estimatedTokens, wasTruncated);

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
            TruncateToBudget(conversation, config.MaxTokens);

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
                // 游戏指令消息（签到/突破/修行等）由修仙系统处理，其系统回复（非AI生成）
                // 已被上方跳过；若保留用户指令会造成"说了没人答"的半截对话，
                // AI 会自行脑补系统回复，造成上下文污染（如把「签到」脑补成AI说的"签到成功"）。
                // FreeAction（AI裁判）除外：其回复是 AI 生成的，需保留完整配对。
                var gameCmd = _gameCommandRouter.TryMatch(msg.PlainText);
                if (gameCmd is not null && gameCmd.Action != GameAction.FreeAction)
                    continue;
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

    /// <summary>
    /// 输入预算的硬上限。由「最大 Tokens」按 <see cref="InputBudgetRatio"/> 折算而来。
    /// 这是一个刻意保留的耦合：面板只有一个 token 旋钮，输入额度从它派生，
    /// 而不是再给用户加第二个他更看不懂的数字。
    /// </summary>
    public static int InputTokenBudget(int maxTokens) => (int)(maxTokens * InputBudgetRatio);

    /// <summary>
    /// 把对话裁进预算。规则有两层，且顺序不能颠倒：
    ///   1) 条数上限由 BuildConversation 负责（面板的「上下文条数」，保证语义完整）；
    ///   2) 这里只管「太长就砍」，但绝不砍到 <see cref="MinRemainingMessages"/> 条以下 ——
    ///      除非撞上 token 硬上限，因为那条线是防请求被上游拒掉/爆 context 的，必须服从。
    /// 早先的写法让 8 条地板无条件压过预算，结果是"超预算"的请求照样发出去，
    /// 跟这段代码的初衷正好相反。
    /// </summary>
    public (List<(string Role, string Content)> Messages, int EstimatedTokens, bool WasTruncated)
        TruncateToBudget(List<(string Role, string Content)> conversation, int maxTokens)
    {
        var tokenBudget = InputTokenBudget(maxTokens);

        if (EstimateTokens(conversation) <= tokenBudget)
            return (conversation, EstimateTokens(conversation), false);

        // 先砍到 8 条，看看够不够
        var floor = Math.Min(conversation.Count, MinRemainingMessages);
        var truncated = conversation.TakeLast(floor).ToList();
        var wasTruncated = truncated.Count < conversation.Count;

        if (EstimateTokens(truncated) <= tokenBudget)
            return (truncated, EstimateTokens(truncated), wasTruncated);

        // 还不够小：预算优先，继续砍（此时可以低于 8 条）
        while (truncated.Count > 1 && EstimateTokens(truncated) > tokenBudget)
            truncated.RemoveAt(0);

        return (truncated, EstimateTokens(truncated), true);
    }

    private static int EstimateTokens(List<(string Role, string Content)> conversation) =>
        (int)(conversation.Sum(m => m.Content.Length) / CharsPerToken);

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
