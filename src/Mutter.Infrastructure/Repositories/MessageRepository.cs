using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly AppDbContext _dbContext;

    public MessageRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        _dbContext.ChatMessages.Add(message);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        _dbContext.ChatMessages.AddRange(messages);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<List<ChatMessage>> GetGroupHistoryAsync(string groupId, int limit = 20, CancellationToken ct = default)
    {
        var boundary = ConversationTurnScope.Current?.CreatedAt;
        var boundaryId = await CurrentTurnRowIdAsync(ct);
        return await _dbContext.ChatMessages
            .Where(m => m.ChatType == 0 && m.GroupId == groupId)
            .Where(m => boundary == null || m.IsBotMessage
                || (boundaryId != null ? m.Id <= boundaryId : m.CreatedAt <= boundary))
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<List<ChatMessage>> GetPrivateHistoryAsync(
        string userId, int limit = 20, DateTime? since = null, CancellationToken ct = default)
    {
        // 私聊上下文必须两侧都要：只取用户自己那几行，模型看到的就是一串"没人回答的提问"，
        // 于是它替自己编出上一轮答过什么（线上真实后果：对方只说了句「咕咕嘎嘎」，
        // bot 回「催啥催嘛我这转半天打不开」——它在回答一个不存在的追问）。
        // bot 行的 UserId 是 bot 自己，所以按 PeerUserId 认领对话对象，避免把对 A 说的话带进 B 的上下文。
        var query = _dbContext.ChatMessages.Where(m => m.ChatType == 1 &&
            (m.UserId == userId && !m.IsBotMessage || m.IsBotMessage && m.PeerUserId == userId));

        if (since is not null)
            query = query.Where(m => m.CreatedAt >= since);
        if (ConversationTurnScope.Current is { } turn)
        {
            var boundaryId = await CurrentTurnRowIdAsync(ct);
            query = query.Where(m => m.IsBotMessage
                || (boundaryId != null ? m.Id <= boundaryId : m.CreatedAt <= turn.CreatedAt));
        }

        return await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> IsMessageIdExistsAsync(string messageId, CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .AnyAsync(m => m.MessageId == messageId, ct);
    }

    public async Task<int> MarkAtBotAsync(string messageId, CancellationToken ct = default)
    {
        // 单条 UPDATE，不存在就 0 行，不需要先查一遍
        return await _dbContext.ChatMessages
            .Where(m => m.MessageId == messageId && !m.HasAtBot)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAtBot, true), ct);
    }

    public async Task<(List<ChatMessage> Items, int TotalCount)> GetPagedMessagesAsync(
        string? groupId, int page, int pageSize, string? source = null, CancellationToken ct = default)
    {
        var query = _dbContext.ChatMessages.AsQueryable();

        if (!string.IsNullOrEmpty(groupId))
            query = query.Where(m => m.GroupId == groupId);

        query = source switch
        {
            "at" => query.Where(m => !m.IsBotMessage && m.HasAtBot),
            "observe" => query.Where(m => !m.IsBotMessage && !m.HasAtBot),
            "bot" => query.Where(m => m.IsBotMessage && !m.IsProactive),
            "proactive" => query.Where(m => m.IsBotMessage && m.IsProactive),
            _ => query
        };

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<long> CountMessagesAsync(DateTime? since = null, CancellationToken ct = default)
    {
        var query = _dbContext.ChatMessages.AsQueryable();
        if (since.HasValue)
            query = query.Where(m => m.CreatedAt >= since.Value);
        return await query.LongCountAsync(ct);
    }

    public async Task<long> CountAiCallsAsync(CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages.LongCountAsync(m => m.AiModelUsed != null, ct)
            + await UnrepresentedReplies().LongCountAsync(ct);
    }

    public async Task<long> SumTokensAsync(CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages.Where(m => m.PromptTokens != null)
            .SumAsync(m => (long)m.PromptTokens!.Value + (m.CompletionTokens ?? 0), ct)
            + await UnrepresentedReplies().SumAsync(r => (long)r.PromptTokens + r.CompletionTokens, ct);
    }

    public async Task<List<DailyTokenStats>> GetDailyTokenStatsAsync(int days, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = await ReadUsageAsync(since, ct);

        return aiMessages
            .GroupBy(m => m.CreatedAt.ToString("yyyy-MM-dd"))
            .Select(g => new DailyTokenStats
            {
                Date = g.Key,
                PromptTokens = g.Sum(m => m.PromptTokens ?? 0),
                CompletionTokens = g.Sum(m => m.CompletionTokens ?? 0),
                CallCount = g.Count()
            })
            .OrderBy(x => x.Date)
            .ToList();
    }

    public async Task<List<GroupTokenStats>> GetGroupTokenStatsAsync(int days, int top, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = (await ReadUsageAsync(since, ct)).Where(m => m.ChatType == 0 && m.GroupId != null).ToList();

        var names = await _dbContext.KnownGroups.AsNoTracking()
            .ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);

        return aiMessages
            .GroupBy(m => m.GroupId)
            .Select(g => new GroupTokenStats
            {
                GroupId = g.Key!,
                GroupName = names.TryGetValue(g.Key!, out var name) ? name ?? string.Empty : string.Empty,
                TotalTokens = g.Sum(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0)),
                CallCount = g.Count()
            })
            .OrderByDescending(x => x.TotalTokens)
            .Take(top)
            .ToList();
    }

    public async Task<List<UserTokenStats>> GetUserTokenStatsAsync(int days, int top, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = await ReadUsageAsync(since, ct);

        return aiMessages
            .GroupBy(m => new { m.UserId, m.UserName })
            .Select(g => new UserTokenStats
            {
                UserId = g.Key.UserId,
                UserName = g.Key.UserName,
                TotalTokens = g.Sum(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0)),
                CallCount = g.Count()
            })
            .OrderByDescending(x => x.TotalTokens)
            .Take(top)
            .ToList();
    }
    private async Task<long?> CurrentTurnRowIdAsync(CancellationToken ct)
    {
        var turn = ConversationTurnScope.Current;
        if (turn is null) return null;
        if (turn.MessageRowId is not null) return turn.MessageRowId;
        return await _dbContext.ChatMessages.Where(m => m.MessageId == turn.MessageId)
            .Select(m => (long?)m.Id).SingleOrDefaultAsync(ct);
    }

    private IQueryable<OutgoingReply> UnrepresentedReplies() =>
        _dbContext.OutgoingReplies.Where(r => r.Model != null
            && !_dbContext.ChatMessages.Any(m => m.MessageId == r.Id));

    private async Task<List<ChatMessage>> ReadUsageAsync(DateTime since, CancellationToken ct)
    {
        var messages = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.PromptTokens != null && m.CreatedAt >= since).ToListAsync(ct);
        var generated = await UnrepresentedReplies().AsNoTracking().Where(r => r.CreatedAt >= since).ToListAsync(ct);
        messages.AddRange(generated.Select(r => new ChatMessage
        {
            MessageId = r.Id, GroupId = r.GroupId, ChatType = r.GroupId is null ? 1 : 0,
            UserId = r.BotId, UserName = "Bot", AiModelUsed = r.Model,
            PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens, CreatedAt = r.CreatedAt
        }));
        return messages;
    }

}
