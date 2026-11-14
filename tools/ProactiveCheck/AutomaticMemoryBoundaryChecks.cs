using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;

internal static partial class RuntimeChecks
{
    private static async Task AutomaticMemoryOwnershipAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        const string evidence = "请忘掉我之前的喝茶偏好";
        var sourceA = await NewMemoryMessageAsync(env, evidence + "，现在开始喝咖啡。", user: "MEMBER-A");
        var sourceB = await NewMemoryMessageAsync(env, "我今天只是路过和大家打个招呼。", user: "MEMBER-B");
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = env.Clock.GetUtcNow().UtcDateTime;
        var old = new GroupMemoryItemEntity
        {
            GroupId = "GROUP", SubjectUserId = "MEMBER-B", Content = "乙自述：喜欢喝茶。",
            TagsJson = "[\"饮食\"]", FirstSeenAt = now.AddDays(-1), LastSeenAt = now.AddDays(-1), ExpiresAt = now.AddDays(30)
        };
        db.GroupMemoryItems.Add(old);
        await db.SaveChangesAsync();
        var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
        var batch = (await repo.PrepareAsync("GROUP", true, default))!;
        var handleB = batch.UserHandles.Single(p => p.Value == "MEMBER-B").Key;
        var handleA = batch.UserHandles.Single(p => p.Value == "MEMBER-A").Key;
        var patch = new AutomaticMemoryRepository.Patch();
        foreach (var op in new[] { "add", "replace", "remove" })
            patch.Operations.Add(new()
            {
                Op = op, TargetId = op == "add" ? null : old.Id, SubjectUserId = handleB,
                Content = "被他人改成喝咖啡。", SourceRowIds = [sourceA], Evidence = evidence, Tags = ["饮食"]
            });
        patch.Operations.Add(new()
        {
            Op = "add", SubjectUserId = handleA, Content = "混合来源不能归给个人。",
            SourceRowIds = [sourceA, sourceB], Evidence = evidence, Tags = ["饮食"]
        });
        patch.Operations.Add(new()
        {
            Op = "add", SubjectUserId = handleA, Content = "现在开始喝咖啡。",
            SourceRowIds = [sourceA], Evidence = "现在开始喝咖啡", Tags = ["咖啡"]
        });
        Require(await repo.CompleteAsync(batch, patch, "success", default), "the batch completes despite rejected operations");
        var items = await db.GroupMemoryItems.AsNoTracking().ToListAsync();
        Require(items.Count == 2 && items.Single(i => i.Id == old.Id).Content == "乙自述：喜欢喝茶。",
            "another member's valid handle cannot authorize add, replace or remove using A's evidence");
        Require(items.Single(i => i.SubjectUserId == "MEMBER-A").Content.Contains("现在开始喝咖啡"),
            "a genuine self-description in the same batch remains writable");

        var ownSource = await NewMemoryMessageAsync(env, evidence + "。", user: "MEMBER-B");
        var ownBatch = (await repo.PrepareAsync("GROUP", true, default))!;
        await repo.CompleteAsync(ownBatch, new() { Operations = [new()
        {
            Op = "remove", TargetId = old.Id, SubjectUserId = ownBatch.UserHandles.Single().Key,
            SourceRowIds = [ownSource], Evidence = evidence
        }] }, "success", default);
        Require(!await db.GroupMemoryItems.AnyAsync(i => i.Id == old.Id)
            && await db.GroupMemoryItems.AnyAsync(i => i.SubjectUserId == "MEMBER-A"),
            "the owner's own forget evidence still removes only their memory");
    }

    private static async Task AutomaticMemoryIdentityAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        string[] identities = ["SYNTHETIC_OPENID_ALPHA_123456789012", "SYNTHETIC_OPENID_BETA_123456789012",
            "SYNTHETIC_OPENID_GAMMA_123456789012", "SYNTHETIC_OPENID_DELTA_123456789012"];
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = env.Clock.GetUtcNow().UtcDateTime;
        string[] names = [identities[0], AutomaticMemoryRepository.Cut(identities[1], 24), "合成昵称丙", ""];
        for (var i = 0; i < identities.Length; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                GroupId = "GROUP", UserId = identities[i], UserName = names[i], MessageId = "identity-source-" + i,
                PlainText = "我喜欢喝茶，也喜欢在图书馆看书。", HasAtBot = true, CreatedAt = now
            });
            db.GroupMemoryItems.Add(new GroupMemoryItemEntity
            {
                GroupId = "GROUP", SubjectUserId = identities[i],
                Content = AutomaticMemoryRepository.Cut(identities[i], 24) + "自述：喜欢喝茶。",
                TagsJson = "[\"喝茶\"]", FirstSeenAt = now.AddDays(-1), LastSeenAt = now.AddDays(-1), ExpiresAt = now.AddDays(30)
            });
        }
        const string inactiveId = "SYNTHETIC_INACTIVE_OPENID_123456789";
        db.GroupMemoryItems.Add(new GroupMemoryItemEntity
        {
            GroupId = "GROUP", SubjectUserId = inactiveId,
            Content = AutomaticMemoryRepository.Cut(inactiveId, 24) + "自述：" + inactiveId + "喜欢看书。",
            FirstSeenAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(30)
        });
        db.GroupMemoryItems.Add(new GroupMemoryItemEntity
        {
            GroupId = "GROUP", Content = AutomaticMemoryRepository.Cut(identities[0], 24) + "曾提到：喜欢去图书馆。",
            FirstSeenAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(30)
        });
        await db.SaveChangesAsync();
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[]}"));
        await env.Services.GetRequiredService<GroupMemoryLearningService>().RunGroupAsync("GROUP");
        using var outer = JsonDocument.Parse(env.Http.Requests.Single().Body);
        var payloadText = outer.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        foreach (var id in identities.Append(inactiveId))
            Require(!payloadText.Contains(id) && !payloadText.Contains(AutomaticMemoryRepository.Cut(id, 24)),
                "raw and truncated identities cannot re-enter through display names or old memory content");
        using var payload = JsonDocument.Parse(payloadText);
        var members = payload.RootElement.GetProperty("members").EnumerateArray().ToArray();
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        for (var i = 0; i < members.Length; i++)
        {
            var name = i == 2 ? "合成昵称丙" : "成员" + members[i].GetProperty("id").GetString();
            Require(members[i].GetProperty("name").GetString() == name
                && messages[i].GetProperty("userName").GetString() == name,
                "missing names use the batch alias while genuine nicknames remain intact");
        }
        var existing = payload.RootElement.GetProperty("existing").EnumerateArray().ToArray();
        Require(existing.Length == 6 && existing.All(i => i.GetProperty("content").GetString()!.Contains("喜欢")),
            "identity sanitization preserves the old facts, including inactive members and single-source group facts");
        Require(await db.ChatMessages.AnyAsync(m => m.UserName == identities[0])
            && await db.GroupMemoryItems.AnyAsync(i => i.SubjectUserId == inactiveId && i.Content.Contains(inactiveId)),
            "model presentation does not rewrite stored identities or historical facts");
    }

    private static async Task AutomaticMemoryFinalTraceAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ai = env.Services.GetRequiredService<IAiChatService>();
        var config = env.Config() with { ContextWindowTokens = 8192, MaxInputTokens = 6000, WebSearchEnabled = false };
        var memory = new AutomaticMemoryRecall("[自动记忆资料]" + new string('忆', 220), [701, 702]);
        foreach (var (systemLength, image, includeMemory, retained) in new[]
                 { (10, false, true, true), (5900, false, true, false), (1800, true, true, false), (10, true, false, false) })
        {
            var trace = new ConversationTaskTrace("GROUP", "合成记忆预算验证", false, env.Clock.GetUtcNow(), env.Clock);
            using var activity = new RuntimeActivity(trace);
            using var operation = AiCallScope.Begin(default, "reply");
            operation.AutomaticMemory = memory;
            var messages = new List<(string Role, string Content, IReadOnlyList<string>? ImageUrls)>();
            if (includeMemory) messages.Add(("user", memory.Text, null));
            messages.Add(("user", "当前问题", image ? ["synthetic://memory-budget-image"] : null));
            await ai.ChatAsync(new string('系', systemLength), messages, config);
            using var payload = JsonDocument.Parse(env.Http.Requests.Last().Body);
            var outgoing = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Require(outgoing.Any(m => m.GetProperty("content").ValueKind == JsonValueKind.String
                && m.GetProperty("content").GetString() == memory.Text) == retained,
                "the captured request reflects the final system and image budget");
            var memorySteps = trace.Snapshot().Steps.Where(s => s.Stage == "memory").ToArray();
            if (!includeMemory)
                Require(memorySteps.Length == 0, "a preliminary request without memory must not report it as budget-dropped");
            else
            {
                var detail = memorySteps.Single().Detail!;
                Require(detail.Contains(retained ? $"2 条 / {memory.Text.Length} 字：701,702" : "0 条 / 0 字：")
                    && detail.Contains("输入预算已剔除") != retained,
                    "the trace reports retained count, characters and ids after the last budget fit");
            }
        }
    }
}
