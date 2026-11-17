using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

/// <summary>按稳定快照读取索引候选，只为当前页加载正文和精确的投递关联。</summary>
public sealed class ChatTimelineReader(AppDbContext db)
{
    public static readonly string[] Sources = ["", "observe", "at", "bot", "proactive", "decision"];

    public static bool TrySnapshot(string? value, out long messageId, out long decisionId)
    {
        messageId = decisionId = 0;
        if (value is null) return true;
        var parts = value.Split(':');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out messageId)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out decisionId)
            && messageId >= 0 && decisionId >= 0;
    }

    /// <summary>
    /// 精确分页。保留原有协议：先统计总数、再按稳定快照取页，查找和深分页都走这里。
    /// </summary>
    public async Task<ChatTimelinePageDto> ReadAsync(string? groupId, string? source, string? search,
        int page, int pageSize, string? snapshot, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (messageCap, decisionCap) = await ResolveCapsAsync(snapshot, ct);
        var (messages, logs) = BuildSources(groupId, source, search, messageCap, decisionCap);
        var total = await CountSourcesAsync(messages, logs, source, ct);
        var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, maxPage);
        var take = (int)Math.Min((long)page * pageSize, int.MaxValue);
        var candidates = await ReadKeysAsync(messages, logs, source, take, ct);
        var keys = candidates.Skip((page - 1) * pageSize).ToList();
        var items = await MaterializeAsync(keys, decisionCap, ct);
        return new ChatTimelinePageDto
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            HasMore = (long)page * pageSize < total,
            Snapshot = FormatSnapshot(messageCap, decisionCap)
        };
    }

    /// <summary>
    /// 轻量首屏：只取最新一页，不扫描全量统计总数。多取一条仅用于判断是否还有更早记录。
    /// 两类记录分别沿时间索引取至多一页候选，再在内存合并，避免对整群 UNION 排序。
    /// </summary>
    public async Task<ChatTimelinePageDto> ReadLatestAsync(string? groupId, string? source, int pageSize,
        CancellationToken ct = default)
    {
        // 控制器之外也可能直接调用；自行钳制，避免 pageSize<=0 返回空页还带 hasMore。
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (messageCap, decisionCap) = await ResolveCapsAsync(null, ct);
        var (messages, logs) = BuildSources(groupId, source, null, messageCap, decisionCap);
        var keys = await ReadKeysAsync(messages, logs, source, pageSize + 1, ct);
        var hasMore = keys.Count > pageSize;
        if (hasMore) keys.RemoveRange(pageSize, keys.Count - pageSize);
        var items = await MaterializeAsync(keys, decisionCap, ct);
        return new ChatTimelinePageDto
        {
            Items = items,
            TotalCount = null,
            Page = 1,
            PageSize = pageSize,
            HasMore = hasMore,
            Snapshot = FormatSnapshot(messageCap, decisionCap)
        };
    }

    private static async Task<List<TimelineKey>> ReadKeysAsync(IQueryable<ChatMessage> messages,
        IQueryable<ProactiveChatLogEntity> logs, string? source, int take, CancellationToken ct)
    {
        // 同一来源的 Kind 恒定，不把它放进 SQL ORDER BY：SQLite 会因此对整个时间范围补排序。
        // 合并后的前 N 条必定包含于各来源的前 N 条，首屏候选数最多 2*(pageSize+1)。
        var messageKeys = source == "decision" ? [] : await messages
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take)
            .Select(m => new TimelineKey { Kind = 0, Id = m.Id, Time = m.CreatedAt }).ToListAsync(ct);
        if (messageKeys.Count == take)
        {
            // 消息已足够填满候选集，更早的决策不可能进入本页。先截时间范围再去重，
            // 避免大量已关联的旧决策为了凑满 Take 而回扫整段历史；同刻决策仍须保留。
            var oldestCandidate = messageKeys[^1].Time;
            logs = logs.Where(l => l.EvaluatedAt >= oldestCandidate);
        }
        var logKeys = !string.IsNullOrEmpty(source) && source != "decision" ? [] : await logs
            .OrderByDescending(l => l.EvaluatedAt).ThenByDescending(l => l.Id).Take(take)
            .Select(l => new TimelineKey { Kind = 1, Id = l.Id, Time = l.EvaluatedAt }).ToListAsync(ct);
        return messageKeys.Concat(logKeys).OrderByDescending(k => k.Time)
            .ThenByDescending(k => k.Kind).ThenByDescending(k => k.Id).Take(take).ToList();
    }

    /// <summary>在消息已经显示后独立统计精确总数，不阻塞切换。</summary>
    public async Task<ChatTimelineCountDto> CountAsync(string? groupId, string? source, string? search,
        string? snapshot, CancellationToken ct = default)
    {
        var (messageCap, decisionCap) = await ResolveCapsAsync(snapshot, ct);
        var (messages, logs) = BuildSources(groupId, source, search, messageCap, decisionCap);
        return new ChatTimelineCountDto
        {
            TotalCount = await CountSourcesAsync(messages, logs, source, ct),
            Snapshot = FormatSnapshot(messageCap, decisionCap)
        };
    }

    private static async Task<int> CountSourcesAsync(IQueryable<ChatMessage> messages,
        IQueryable<ProactiveChatLogEntity> logs, string? source, CancellationToken ct)
    {
        var messageCount = source == "decision" ? 0 : await messages.CountAsync(ct);
        var logCount = !string.IsNullOrEmpty(source) && source != "decision" ? 0 : await logs.CountAsync(ct);
        return checked(messageCount + logCount);
    }

    private async Task<(long MessageCap, long DecisionCap)> ResolveCapsAsync(string? snapshot, CancellationToken ct)
    {
        if (!TrySnapshot(snapshot, out var messageCap, out var decisionCap))
            throw new ArgumentException("无效的历史分页快照");
        if (snapshot is not null) return (messageCap, decisionCap);
        messageCap = await db.ChatMessages.MaxAsync(m => (long?)m.Id, ct) ?? 0;
        decisionCap = await db.ProactiveChatLogs.MaxAsync(l => (long?)l.Id, ct) ?? 0;
        return (messageCap, decisionCap);
    }

    private (IQueryable<ChatMessage> Messages, IQueryable<ProactiveChatLogEntity> Logs) BuildSources(
        string? groupId, string? source, string? search, long messageCap, long decisionCap)
    {
        var messages = db.ChatMessages.AsNoTracking().Where(m => m.Id <= messageCap);
        var logs = db.ProactiveChatLogs.AsNoTracking().Where(l => l.Id <= decisionCap);
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            messages = messages.Where(m => m.ChatType == 0 && m.GroupId == groupId);
            logs = logs.Where(l => l.GroupId == groupId);
        }
        messages = source switch
        {
            "observe" => messages.Where(m => m.ChatType == 0 && !m.IsBotMessage && !m.HasAtBot),
            "at" => messages.Where(m => m.ChatType == 0 && !m.IsBotMessage && m.HasAtBot),
            "bot" => messages.Where(m => m.IsBotMessage && !m.IsProactive),
            "proactive" => messages.Where(m => m.IsBotMessage && m.IsProactive),
            "decision" => messages.Where(m => false),
            _ => messages
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            messages = messages.Where(m => EF.Functions.Like(m.PlainText, pattern, "\\")
                || EF.Functions.Like(m.UserName, pattern, "\\") || EF.Functions.Like(m.UserId, pattern, "\\")
                || m.IsBotMessage && (m.UserName == "Bot" || m.UserName == "") && EF.Functions.Like("Mutter", pattern, "\\"));
            logs = logs.Where(l => EF.Functions.Like(l.Topic!, pattern, "\\") || EF.Functions.Like(l.Reason!, pattern, "\\")
                || EF.Functions.Like(l.Line!, pattern, "\\") || EF.Functions.Like(l.Detail!, pattern, "\\"));
        }
        if (!string.IsNullOrEmpty(source) && source != "decision") logs = logs.Where(l => false);
        // 同一条已发言记录展示在 Bot 消息上；没有可匹配消息的留痕仍作为事件保留。
        // 直接命中 MessageId，或经发送回执的 PlatformMessageId 映射命中，各用一个 NOT EXISTS。
        // 不要写成一个 Any(直接命中 || OutgoingReplies.Any(...))：SQLite 会把 OR 内侧退化成
        // 对每条留痕按 rowid 扫全表 ChatMessages，消息量一大就是分钟级。
        // MessageId != "" 不能省：唯一索引带 MessageId <> '' 过滤条件，少了它规划器会放弃索引，
        // 改走 Id <= cap 的 rowid 范围扫描。
        logs = logs.Where(l => string.IsNullOrEmpty(l.SentMessageId)
            || (!messages.Any(m => m.IsBotMessage && m.IsProactive && m.MessageId != ""
                    && m.GroupId == l.GroupId && m.MessageId == l.SentMessageId)
                && !db.OutgoingReplies.Any(r => r.GroupId == l.GroupId
                    && r.PlatformMessageId == l.SentMessageId
                    && r.IsProactive && r.State == DeliveryStates.Sent
                    && messages.Any(m => m.IsBotMessage && m.IsProactive && m.MessageId != ""
                        && m.GroupId == l.GroupId && m.MessageId == r.Id))));
        return (messages, logs);
    }

    private async Task<List<ChatTimelineItemDto>> MaterializeAsync(List<TimelineKey> keys, long decisionCap,
        CancellationToken ct)
    {
        if (keys.Count == 0) return [];
        var messageIds = keys.Where(k => k.Kind == 0).Select(k => k.Id).ToArray();
        var logIds = keys.Where(k => k.Kind == 1).Select(k => k.Id).ToArray();
        // 列表只投影可见字段；不读取 RawSegmentsJson、token 记账及决策窗口等大字段。
        var rows = await db.ChatMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id))
            .Select(m => new { m.Id, m.CreatedAt, m.ChatType, m.GroupId, m.UserId, m.UserName, m.PlainText,
                m.HasImage, m.HasAtBot, m.IsBotMessage, m.IsProactive }).ToDictionaryAsync(m => m.Id, ct);
        var events = await db.ProactiveChatLogs.AsNoTracking().Where(l => logIds.Contains(l.Id))
            .Select(l => new { l.Id, l.GroupId, l.EvaluatedAt, l.Outcome, l.Topic, l.Reason, l.Detail }).ToDictionaryAsync(l => l.Id, ct);
        var groupIds = rows.Values.Select(m => m.GroupId).Concat(events.Values.Select(l => l.GroupId))
            .Where(id => id is not null).Distinct().ToArray();
        var names = groupIds.Length == 0
            ? new Dictionary<string, string?>()
            : await db.KnownGroups.AsNoTracking().Where(g => groupIds.Contains(g.GroupOpenId))
                .ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);
        var proactiveIds = rows.Values.Where(m => m.IsBotMessage && m.IsProactive).Select(m => m.Id).ToArray();
        var links = proactiveIds.Length == 0
            ? new List<DecisionLink>()
            : await Links(proactiveIds, decisionCap).Distinct().ToListAsync(ct);
        var uniqueLinks = links.GroupBy(l => l.MessageId).Where(g => g.Select(l => l.DecisionId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().DecisionId);
        var items = new List<ChatTimelineItemDto>(keys.Count);
        foreach (var k in keys)
        {
            // DataRetentionService 可能在取键与取行之间清理了记录；跳过已删除的行，不要让整页请求 500。
            if (k.Kind == 1)
            {
                if (!events.TryGetValue(k.Id, out var log)) continue;
                items.Add(new ChatTimelineItemDto
                {
                    Kind = "decision", Id = log.Id, CreatedAt = log.EvaluatedAt,
                    GroupId = log.GroupId, ChatType = 0, GroupName = names.GetValueOrDefault(log.GroupId),
                    DecisionId = log.Id, Outcome = log.Outcome, Topic = log.Topic, Reason = log.Reason ?? log.Detail
                });
                continue;
            }
            if (!rows.TryGetValue(k.Id, out var m)) continue;
            items.Add(new ChatTimelineItemDto
            {
                Id = m.Id, CreatedAt = m.CreatedAt, ChatType = m.ChatType,
                GroupId = m.GroupId, GroupName = m.GroupId is null ? null : names.GetValueOrDefault(m.GroupId),
                UserId = m.UserId, UserName = m.UserName, PlainText = m.PlainText, HasImage = m.HasImage,
                HasAtBot = m.HasAtBot, IsBotMessage = m.IsBotMessage, IsProactive = m.IsProactive,
                DecisionId = uniqueLinks.TryGetValue(m.Id, out var id) ? id : null
            });
        }
        return items;
    }

    private IQueryable<DecisionLink> Links(long[] messageIds, long decisionCap)
    {
        var messages = db.ChatMessages.Where(m => messageIds.Contains(m.Id)
            && m.IsBotMessage && m.IsProactive && m.MessageId != "");
        var logs = db.ProactiveChatLogs.Where(l => l.Id <= decisionCap && l.SentMessageId != null && l.SentMessageId != "");
        var direct = from m in messages
                     join l in logs on new { m.GroupId, SentId = m.MessageId } equals new { GroupId = (string?)l.GroupId, SentId = l.SentMessageId! }
                     select new DecisionLink { MessageId = m.Id, DecisionId = l.Id };
        var mapped = from m in messages
                     join r in db.OutgoingReplies on m.MessageId equals r.Id
                     // logs 已排除空 SentMessageId，NULL 回执映射为 "" 后仍不匹配，且不会生成
                     // nullable == nullable 的 OR，两侧都能用精确发送 ID 索引而非整群扫描。
                     join l in logs on new { r.GroupId, SentId = r.PlatformMessageId ?? "" } equals new { GroupId = (string?)l.GroupId, SentId = l.SentMessageId! }
                     where m.GroupId == r.GroupId && r.IsProactive && r.State == DeliveryStates.Sent
                     select new DecisionLink { MessageId = m.Id, DecisionId = l.Id };
        return direct.Concat(mapped);
    }

    public async Task<ChatDecisionDto?> DecisionAsync(long id, CancellationToken ct = default)
    {
        var log = await db.ProactiveChatLogs.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (log is null) return null;
        var name = await db.KnownGroups.AsNoTracking().Where(g => g.GroupOpenId == log.GroupId).Select(g => g.Name).FirstOrDefaultAsync(ct);
        // v1 的异常兜底也会写入默认 0，不能视为完整诊断。v2 起明确保存已知诊断值；
        // 旧留痕仍保留可确认的正数/true，不把缺失信息伪装成“未使用”。
        var known = log.DetailVersion >= ProactiveChatLogEntity.DiagnosticsVersion;
        ProactiveLogSnapshot? window = null;
        // DetailVersion=2 起才返回观察窗口：1 是隐私门槛上线前写的旧快照，只 @ 的群也可能含有
        // 未入库的群友原话，不能再从详情接口读出来。
        if (log.DetailVersion >= ProactiveChatLogEntity.PrivacyGatedWindowVersion
            && log.WindowSnapshotJson is not null)
        {
            try { window = JsonSerializer.Deserialize<ProactiveLogSnapshot>(log.WindowSnapshotJson); }
            catch (JsonException) { /* 旧留痕或损坏快照不影响正文和其他详情读取。 */ }
            if (window?.Version != ProactiveLogSnapshot.CurrentVersion || window.Messages is null) window = null;
        }
        // 最终门禁也可能发生在模型之后，只有明确的阶段标记才能证明没有模型用量。
        var usageKnown = log.AiEvaluated == false || log.PromptTokens > 0 || log.CompletionTokens > 0;
        var speechKnown = (known && !log.SearchUsed) || log.SpeechPromptTokens > 0 || log.SpeechCompletionTokens > 0;
        return new ChatDecisionDto
        {
            Id = log.Id, GroupId = log.GroupId, GroupName = name, EvaluatedAt = log.EvaluatedAt,
            Outcome = log.Outcome, Topic = log.Topic, Line = log.Line, Reason = log.Reason, Detail = log.Detail,
            MessageCount = log.MessageCount, Model = log.Model,
            ImageCount = known || log.ImageCount > 0 ? log.ImageCount : null,
            MemoryCount = known || log.MemoryCount > 0 ? log.MemoryCount : null,
            MemoryPromptChars = known || log.MemoryPromptChars > 0 ? log.MemoryPromptChars : null,
            SearchUsed = known || log.SearchUsed ? log.SearchUsed : null, SearchQuery = log.SearchQuery,
            PromptTokens = usageKnown ? log.PromptTokens : null, CompletionTokens = usageKnown ? log.CompletionTokens : null,
            SpeechPromptTokens = speechKnown ? log.SpeechPromptTokens : null, SpeechCompletionTokens = speechKnown ? log.SpeechCompletionTokens : null,
            Window = window
        };
    }

    private static string FormatSnapshot(long messageCap, long decisionCap) =>
        FormattableString.Invariant($"{messageCap}:{decisionCap}");

    private sealed class TimelineKey { public int Kind { get; set; } public long Id { get; set; } public DateTime Time { get; set; } }
    private sealed class DecisionLink { public long MessageId { get; set; } public long DecisionId { get; set; } }
}
