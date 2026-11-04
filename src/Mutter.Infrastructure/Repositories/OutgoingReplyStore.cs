using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Repositories;

/// <summary>单进程发送账本。未确认的意图保守计入主动配额，重启不会重新发送。</summary>
public sealed class OutgoingReplyStore(IServiceScopeFactory scopes, TimeProvider clock) : IOutgoingReplyStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<string?> PrepareAsync(OutgoingReply reply, ProactiveChatConfig? limits, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.OutgoingReplies.AnyAsync(r => r.Id == reply.Id, ct))
                return "skipped_duplicate";
            if (reply.IsProactive && limits is not null)
            {
                var blocked = await BlockReasonAsync(db, reply.GroupId!, limits, ct);
                if (blocked is not null) return blocked;
            }
            db.OutgoingReplies.Add(reply);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<string?> CheckProactiveAsync(string groupId, ProactiveChatConfig limits, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await BlockReasonAsync(db, groupId, limits, ct);
    }

    private async Task<string?> BlockReasonAsync(AppDbContext db, string groupId,
        ProactiveChatConfig limits, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var day = now.AddHours(8).Date.AddHours(-8);
        var reserved = db.OutgoingReplies.Where(r => r.IsProactive
            && (r.State == DeliveryStates.Pending || r.State == DeliveryStates.Sent || r.State == DeliveryStates.Unknown));
        // 升级前确认发送的旧记录也计入；新账本生成的 ChatMessage 不重复计数。
        var legacy = db.ChatMessages.Where(m => m.IsProactive
            && !db.OutgoingReplies.Any(r => r.Id == m.MessageId));
        var lastReservation = await reserved.Where(r => r.GroupId == groupId)
            .MaxAsync(r => (DateTime?)r.CreatedAt, ct);
        var lastLegacy = await legacy.Where(m => m.GroupId == groupId)
            .MaxAsync(m => (DateTime?)m.CreatedAt, ct);
        var last = lastReservation > lastLegacy || lastLegacy is null ? lastReservation : lastLegacy;
        if (last is not null && now - last.Value < TimeSpan.FromMinutes(limits.CooldownMinutes))
            return ProactiveOutcomes.SkippedCooldown;
        var groupCount = await reserved.CountAsync(r => r.GroupId == groupId && r.CreatedAt >= day, ct)
            + await legacy.CountAsync(m => m.GroupId == groupId && m.CreatedAt >= day, ct);
        if (groupCount >= limits.MaxPerGroupPerDay) return ProactiveOutcomes.SkippedGroupCap;
        var globalCount = await reserved.CountAsync(r => r.CreatedAt >= day, ct)
            + await legacy.CountAsync(m => m.CreatedAt >= day, ct);
        if (globalCount >= limits.MaxGlobalPerDay) return ProactiveOutcomes.SkippedGlobalCap;
        return null;
    }

    public async Task CompleteAsync(OutgoingReply reply, DeliveryResult result, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var entity = await db.OutgoingReplies.SingleAsync(r => r.Id == reply.Id, ct);
            if (entity.State == DeliveryStates.Sent) return;
            entity.State = result.State;
            entity.PlatformMessageId = result.MessageId;
            entity.Channel = result.Channel;
            entity.Detail = result.Detail;
            entity.CompletedAt = clock.GetUtcNow().UtcDateTime;
            if (result.Sent && !await db.ChatMessages.AnyAsync(m => m.MessageId == reply.Id, ct))
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    MessageId = reply.Id, ChatType = reply.GroupId is null ? 1 : 0,
                    GroupId = reply.GroupId, PeerUserId = reply.PeerUserId,
                    UserId = reply.BotId, UserName = "Bot", PlainText = reply.Content,
                    IsBotMessage = true, IsProactive = reply.IsProactive, AiModelUsed = reply.Model,
                    PromptTokens = reply.Model is null ? null : reply.PromptTokens,
                    CompletionTokens = reply.Model is null ? null : reply.CompletionTokens,
                    CreatedAt = entity.CompletedAt.Value
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally { _writeGate.Release(); }
    }
}
