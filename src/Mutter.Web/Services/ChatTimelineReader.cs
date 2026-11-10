using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Models;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

/// <summary>分页、查找和精确的投递关联都在数据库端完成；历史数据保持原样。</summary>
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

    public async Task<ChatTimelinePageDto> ReadAsync(string? groupId, string? source, string? search,
        int page, int pageSize, string? snapshot, CancellationToken ct = default)
    {
        if (!TrySnapshot(snapshot, out var messageCap, out var decisionCap)) throw new ArgumentException("无效的历史分页快照");
        if (snapshot is null)
        {
            messageCap = await db.ChatMessages.MaxAsync(m => (long?)m.Id, ct) ?? 0;
            decisionCap = await db.ProactiveChatLogs.MaxAsync(l => (long?)l.Id, ct) ?? 0;
        }
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
        var timeline = messages.Select(m => new TimelineKey { Kind = 0, Id = m.Id, Time = m.CreatedAt })
            .Concat(logs.Select(l => new TimelineKey { Kind = 1, Id = l.Id, Time = l.EvaluatedAt }));
        var total = await timeline.CountAsync(ct);
        var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, maxPage);
        var keys = await timeline.OrderByDescending(k => k.Time).ThenByDescending(k => k.Kind).ThenByDescending(k => k.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var messageIds = keys.Where(k => k.Kind == 0).Select(k => k.Id).ToArray();
        var logIds = keys.Where(k => k.Kind == 1).Select(k => k.Id).ToArray();
        var rows = await db.ChatMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var events = await db.ProactiveChatLogs.AsNoTracking().Where(l => logIds.Contains(l.Id))
            .Select(l => new { l.Id, l.GroupId, l.EvaluatedAt, l.Outcome, l.Topic, l.Reason, l.Detail }).ToDictionaryAsync(l => l.Id, ct);
        var names = await db.KnownGroups.AsNoTracking().ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);
        var links = await Links(messageCap, decisionCap).Where(l => messageIds.Contains(l.MessageId)).Distinct().ToListAsync(ct);
        var uniqueLinks = links.GroupBy(l => l.MessageId).Where(g => g.Select(l => l.DecisionId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().DecisionId);
        var items = keys.Select(k =>
        {
            if (k.Kind == 1)
            {
                var log = events[k.Id];
                return new ChatTimelineItemDto { Kind = "decision", Id = log.Id, CreatedAt = log.EvaluatedAt,
                    GroupId = log.GroupId, ChatType = 0, GroupName = names.GetValueOrDefault(log.GroupId),
                    DecisionId = log.Id, Outcome = log.Outcome, Topic = log.Topic, Reason = log.Reason ?? log.Detail };
            }
            var m = rows[k.Id];
            return new ChatTimelineItemDto { Id = m.Id, CreatedAt = m.CreatedAt, ChatType = m.ChatType,
                GroupId = m.GroupId, GroupName = m.GroupId is null ? null : names.GetValueOrDefault(m.GroupId),
                UserId = m.UserId, UserName = m.UserName, PlainText = m.PlainText, HasImage = m.HasImage,
                HasAtBot = m.HasAtBot, IsBotMessage = m.IsBotMessage, IsProactive = m.IsProactive,
                DecisionId = uniqueLinks.TryGetValue(m.Id, out var id) ? id : null };
        }).ToList();
        return new ChatTimelinePageDto { Items = items, TotalCount = total, Page = page, PageSize = pageSize,
            Snapshot = FormattableString.Invariant($"{messageCap}:{decisionCap}") };
    }

    private IQueryable<DecisionLink> Links(long messageCap = long.MaxValue, long decisionCap = long.MaxValue)
    {
        var messages = db.ChatMessages.Where(m => m.Id <= messageCap && m.IsBotMessage && m.IsProactive && m.MessageId != "");
        var logs = db.ProactiveChatLogs.Where(l => l.Id <= decisionCap && l.SentMessageId != null && l.SentMessageId != "");
        var direct = from m in messages
                     join l in logs on new { m.GroupId, SentId = m.MessageId } equals new { GroupId = (string?)l.GroupId, SentId = l.SentMessageId! }
                     select new DecisionLink { MessageId = m.Id, DecisionId = l.Id };
        var mapped = from m in messages
                     join r in db.OutgoingReplies on m.MessageId equals r.Id
                     join l in logs on new { r.GroupId, SentId = r.PlatformMessageId } equals new { GroupId = (string?)l.GroupId, SentId = l.SentMessageId }
                     where m.GroupId == r.GroupId && r.IsProactive && r.State == DeliveryStates.Sent
                     select new DecisionLink { MessageId = m.Id, DecisionId = l.Id };
        return direct.Concat(mapped);
    }

    public async Task<ChatDecisionDto?> DecisionAsync(long id, CancellationToken ct = default)
    {
        var log = await db.ProactiveChatLogs.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (log is null) return null;
        var name = await db.KnownGroups.AsNoTracking().Where(g => g.GroupOpenId == log.GroupId).Select(g => g.Name).FirstOrDefaultAsync(ct);
        // DetailVersion=1 起，记忆/附图/联网这些诊断值的 0/空值就是“确实没有”，
        // 包括模型调用失败、还没回写 Model 的那次；不能再拿 Model 是否为空代替判断，
        // 否则失败那次的记忆与附图会被误报成“未记录”。
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
        // 用量和诊断值分开：只有门禁结果确实知道是 0；模型调用的 0 也可能是上游没回 usage。
        var usageKnown = (log.DetailVersion >= ProactiveChatLogEntity.DiagnosticsVersion
                && ProactiveOutcomes.IsBeforeAi(log.Outcome))
            || log.PromptTokens > 0 || log.CompletionTokens > 0;
        var speechKnown = (known && !log.SearchUsed) || log.SpeechPromptTokens > 0 || log.SpeechCompletionTokens > 0;
        return new ChatDecisionDto { Id = log.Id, GroupId = log.GroupId, GroupName = name, EvaluatedAt = log.EvaluatedAt,
            Outcome = log.Outcome, Topic = log.Topic, Line = log.Line, Reason = log.Reason, Detail = log.Detail,
            MessageCount = log.MessageCount, Model = log.Model,
            ImageCount = known || log.ImageCount > 0 ? log.ImageCount : null,
            MemoryCount = known || log.MemoryCount > 0 ? log.MemoryCount : null,
            MemoryPromptChars = known || log.MemoryPromptChars > 0 ? log.MemoryPromptChars : null,
            SearchUsed = known || log.SearchUsed ? log.SearchUsed : null, SearchQuery = log.SearchQuery,
            PromptTokens = usageKnown ? log.PromptTokens : null, CompletionTokens = usageKnown ? log.CompletionTokens : null,
            SpeechPromptTokens = speechKnown ? log.SpeechPromptTokens : null, SpeechCompletionTokens = speechKnown ? log.SpeechCompletionTokens : null,
            Window = window };
    }

    private sealed class TimelineKey { public int Kind { get; set; } public long Id { get; set; } public DateTime Time { get; set; } }
    private sealed class DecisionLink { public long MessageId { get; set; } public long DecisionId { get; set; } }
}
