using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mutter.Core.Configuration;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using SkiaSharp;

internal static partial class RuntimeChecks
{
    private static async Task GroupStickersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        foreach (var groupId in new[] { "GROUP", "OTHER" })
            await configs.SaveGroupConfigAsync(new GroupConfig
            {
                GroupId = groupId, IsEnabled = true, AutoCollectStickers = true, UseStickersInReplies = true
            });
        await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "DISABLED", IsEnabled = true });

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aI1sAAAAASUVORK5CYII=");
        byte[] animatedPng = [.. png[..33], 0, 0, 0, 0, .. "acTL"u8, 0, 0, 0, 0, .. png[33..]];
        env.StickerHttp.Handler = (request, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/animated" ? animatedPng : png)
        });

        var handler = env.Services.GetRequiredService<MessageEventHandler>();
        var collector = env.Services.GetRequiredService<GroupStickerCollector>();
        var library = env.Services.GetRequiredService<IGroupStickerLibrary>();
        await collector.StartAsync(default);
        QqDispatch Dispatch(string kind, string id, string group, string body, params string[] files) =>
            new(kind, id, 1, JsonSerializer.SerializeToElement(new GroupMessageEvent
            {
                Id = id, GroupOpenid = group, Content = body,
                Author = new AuthorMember { MemberOpenid = "USER", Username = "虚拟用户" },
                Attachments = files.Select(file => new Attachment
                {
                    ContentType = "image/png", Url = "https://example.org/" + file
                }).ToList()
            }));

        const string message = "GROUP_MESSAGE_CREATE";
        const string face = """<faceType=6,faceId="1",ext="">""";
        await handler.HandleDispatchAsync(Dispatch(message, "plain-photo", "GROUP", "", "photo"));
        await handler.HandleDispatchAsync(Dispatch(message, "many-attachments", "GROUP", face, "first", "second"));
        await handler.HandleDispatchAsync(Dispatch(message, "animated", "GROUP", face, "animated"));
        await handler.HandleDispatchAsync(Dispatch(message, "first-sticker", "GROUP", face, "static"));
        await handler.HandleDispatchAsync(Dispatch(message, "first-sticker", "GROUP", face, "static"));
        await handler.HandleDispatchAsync(Dispatch(message, "second-sticker", "GROUP", face, "static"));
        await handler.HandleDispatchAsync(Dispatch(message, "other-sticker", "OTHER", face, "static"));
        await handler.HandleDispatchAsync(Dispatch(message, "disabled-sticker", "DISABLED", face, "disabled"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        IReadOnlyList<GroupSticker> group;
        IReadOnlyList<GroupSticker> other;
        do
        {
            timeout.Token.ThrowIfCancellationRequested();
            group = await library.ListAsync("GROUP", timeout.Token);
            other = await library.ListAsync("OTHER", timeout.Token);
            if (group is [{ SeenCount: 2 }] && other.Count == 1) break;
            await Task.Delay(15, timeout.Token);
        } while (true);
        Require(env.Http.Requests.Count == 0
            && env.StickerHttp.Requests.Select(r => r.Url.AbsolutePath).SequenceEqual(["/animated", "/static", "/static", "/static"]),
            "仅下载明确标记、未重复投递且已开启的表情，收藏不调用模型");
        Require(group[0].Hash == other[0].Hash && Directory.GetFiles(env.StickerDirectory).Length == 1,
            "同一图片跨群去重，两群各自记录出现次数");
        Require(await library.ReadAsync("DISABLED", group[0].Hash, false, timeout.Token) is null
            && (await library.ChoicesAsync("GROUP", proactive: false, timeout.Token)).Items.Count == 0,
            "群外无法取图，未标注图片不进入候选");

        var hash = group[0].Hash;
        await library.UpdateAsync("GROUP", hash, "开心鼓掌", true, timeout.Token);
        await library.UpdateAsync("OTHER", hash, "另一个群的私有标签", true, timeout.Token);

        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("干得漂亮！\n[mutter_sticker:1]"));
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        await queue.StartAsync(default);
        await handler.HandleDispatchAsync(Dispatch("GROUP_AT_MESSAGE_CREATE", "sticker-reply", "GROUP", "我完成了！"));
        await queue.StopAsync(timeout.Token);
        using var sentPrompt = JsonDocument.Parse(env.Http.Requests.Single().Body);
        var prompt = sentPrompt.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Require(prompt.Contains("开心鼓掌") && !prompt.Contains("另一个群的私有标签") && !prompt.Contains(hash),
            "候选只在当轮原请求提供当前群标签和临时编号");
        var wire = SendMessageRequest.From(env.Api.RichMessages.Single());
        Require(wire.MsgType == 2 && wire.MsgId == "sticker-reply"
            && wire.Markdown is { ForceVerifyImageResource: true } markdown
            && markdown.Content.Contains("qqbot-media://image/v1/synthetic-sticker")
            && !markdown.Content.Contains("mutter_sticker") && env.StickerMaterials.Hashes.SequenceEqual([hash]),
            "答复与表情使用一条被动 Markdown 消息");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Require(await db.ChatMessages.CountAsync(timeout.Token) == 2
            && await db.ChatMessages.AnyAsync(row => row.IsBotMessage && row.HasImage && row.PlainText == "干得漂亮！", timeout.Token)
            && (await library.ListAsync("GROUP", timeout.Token)).Single().SentCount == 1,
            "收藏不落下普通聊天，成功发送后记录真实图片与使用次数");

        var bad = new AiChatResponse { Content = "好消息\n[mutter_sticker:8]" };
        Require(GroupStickerReplies.TakeChoice(bad, group) is null && bad.Content == "好消息", "无效候选编号被清理");
        Require(await library.DeleteAsync("GROUP", hash, timeout.Token)
            && await library.ReadAsync("OTHER", hash, false, timeout.Token) is not null
            && await library.DeleteAsync("OTHER", hash, timeout.Token)
            && Directory.GetFiles(env.StickerDirectory).Length == 0,
            "删除一个群的条目保留另一群图片，释放最后一份引用后清理文件");

        await collector.StopAsync(timeout.Token);
    }

    private static async Task StickerVisualDeduplicationAsync()
    {
        await using var env = await TestEnvironment.CreateAsync(legacy: true);
        using var scope = env.Services.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var id in new[] { "GROUP", "OTHER" }) await configs.AddKnownGroupAsync(id, null, ct);
        await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AutoCollectStickers = true }, ct);

        var original = DrawTestSticker(160, SKEncodedImageFormat.Png);
        // 合法 PNG tEXt 块，像素和压缩数据与原图完全一致。
        var textChunk = Convert.FromHexString("0000001974455874436f6d6d656e740073796e7468657469632d737469636b6572cea6d1c7");
        byte[] otherMetadata = [.. original[..33], .. textChunk, .. original[33..]];
        var resizedJpeg = DrawTestSticker(128, SKEncodedImageFormat.Jpeg);
        var differentMark = DrawTestSticker(128, SKEncodedImageFormat.Jpeg, differentMark: true);
        env.StickerHttp.Handler = (request, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath switch
            {
                "/original" => original,
                "/metadata" => otherMetadata,
                "/resized" => resizedJpeg,
                "/changed" => differentMark,
                _ => throw new InvalidOperationException("Unknown synthetic sticker")
            })
        });

        var library = env.Services.GetRequiredService<IGroupStickerLibrary>();
        var handler = env.Services.GetRequiredService<MessageEventHandler>();
        var collector = env.Services.GetRequiredService<GroupStickerCollector>();
        async Task<IReadOnlyList<GroupSticker>> SendAsync(string attachment, int expectedSeen, string? name = null)
        {
            var ext = name is null ? "" : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { text = name }));
            var messageId = "synthetic-sticker-" + expectedSeen;
            await handler.HandleDispatchAsync(new QqDispatch("GROUP_MESSAGE_CREATE", messageId, expectedSeen,
                JsonSerializer.SerializeToElement(new GroupMessageEvent
                {
                    GroupOpenid = "GROUP", Id = messageId, Content = $"<faceType=6,faceId=\"{expectedSeen}\",ext=\"{ext}\">",
                    Author = new AuthorMember { MemberOpenid = "USER", Username = "合成测试用户" },
                    Attachments = [new Attachment { Url = "https://example.org/" + attachment, ContentType = "image/png" }]
                })), ct);
            IReadOnlyList<GroupSticker> rows;
            while ((rows = await library.ListAsync("GROUP", ct)).Sum(row => row.SeenCount) < expectedSeen)
                await Task.Delay(15, ct);
            return rows;
        }

        await collector.StartAsync(ct);
        try
        {
            var initial = (await SendAsync("original", 1)).Single();
            Require(initial.Label == "", "首次收藏允许无名表情");
            await library.UpdateAsync("GROUP", initial.Hash, "手动取名", false, ct);
            await db.StickerAssets.Where(row => row.Hash == initial.Hash)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.VisualFingerprint, (byte[]?)null), ct);

            var repeat = await SendAsync("metadata", 2, "QQ 新标记");
            Require(repeat is [{ Label: "手动取名", IsEnabled: false, SeenCount: 2 }] && repeat[0].Hash == initial.Hash
                && await db.StickerAssets.AnyAsync(row => row.Hash == initial.Hash && row.VisualFingerprint != null, ct),
                "存量表情按需判重，换 QQ 标记或 PNG 元数据时沿用名称和停用状态");
            repeat = await SendAsync("resized", 3);
            Require(repeat is [{ Label: "手动取名", SeenCount: 3 }] && repeat[0].Hash == initial.Hash
                && Directory.GetFiles(env.StickerDirectory).Length == 1,
                "缩放和有损转码后的同一画面只增加原条目次数");
            var changed = await SendAsync("changed", 4);
            Require(changed.Count == 2 && changed.Any(row => row.Hash != initial.Hash && row.SeenCount == 1),
                "保留同一底图上有明显局部改动的表情");

            Require(await library.ImportAsync("OTHER", "目标群名称", resizedJpeg, ct) is null
                && (await library.CopyAsync("GROUP", "OTHER", [initial.Hash], ct))
                    is { Status: StickerCopyStatus.Success, AlreadyInTarget: 1, Copied: 0 }
                && (await library.ListAsync("OTHER", ct)).Single().Label == "目标群名称",
                "群间复制按画面跳过不同编码的同图，保留目标命名");

            var duplicateHash = Convert.ToHexStringLower(SHA256.HashData(otherMetadata));
            await File.WriteAllBytesAsync(Path.Combine(env.StickerDirectory, duplicateHash + ".image"), otherMetadata, ct);
            db.StickerAssets.Add(new StickerAssetEntity
            {
                Hash = duplicateHash, ContentType = "image/png", Width = 160, Height = 160, Bytes = otherMetadata.Length
            });
            db.GroupStickers.Add(new GroupStickerEntity { GroupId = "GROUP", Hash = duplicateHash, IsEnabled = true, SeenCount = 1 });
            await db.SaveChangesAsync(ct);
            var legacyDuplicates = await SendAsync("metadata", 6);
            Require(legacyDuplicates.Count == 3
                && legacyDuplicates.Single(row => row.Hash == initial.Hash) is { Label: "手动取名", SeenCount: 4 }
                && legacyDuplicates.Single(row => row.Hash == duplicateHash).SeenCount == 1,
                "旧库已有未命名重复项时，重发优先记到已有名称的同图条目");
        }
        finally { await collector.StopAsync(CancellationToken.None); }
    }

    private static byte[] DrawTestSticker(int side, SKEncodedImageFormat format, bool differentMark = false)
    {
        using var bitmap = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(new SKColor(249, 246, 225));
            canvas.Scale(side / 160f);
            paint.Color = new SKColor(242, 177, 56);
            canvas.DrawCircle(80, 70, 52, paint);
            paint.Color = new SKColor(32, 41, 58);
            canvas.DrawCircle(62, 63, 7, paint);
            canvas.DrawCircle(98, 63, 7, paint);
            canvas.DrawRoundRect(new SKRect(55, 86, 105, 103), 7, 7, paint);
            canvas.DrawRect(124, 126, 4, 17, paint);
            canvas.DrawRect(128, 139, 14, 4, paint);
            if (differentMark) canvas.DrawRect(133, 125, 11, 11, paint);
        }
        using var encoded = bitmap.Encode(format, 86);
        return encoded.ToArray();
    }

    private static async Task CopyGroupStickersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        foreach (var groupId in new[] { "GROUP", "OTHER", "FULL" }) await configs.AddKnownGroupAsync(groupId, null);
        await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AutoCollectStickers = true });
        var library = env.Services.GetRequiredService<GroupStickerLibrary>();
        var first = TestStickerPng(16, 16);
        var second = TestStickerPng(17, 16);
        var firstHash = Convert.ToHexStringLower(SHA256.HashData(first));
        var secondHash = Convert.ToHexStringLower(SHA256.HashData(second));
        await library.CollectAsync("GROUP", "来源群名称", first, default);
        await library.CollectAsync("GROUP", "待复制名称", second, default);
        await library.UpdateAsync("GROUP", secondHash, "待复制名称", false, default);
        await library.RecordSentAsync("GROUP", secondHash, default);
        await library.ImportAsync("OTHER", "目标群名称", first, default);
        await library.RecordSentAsync("OTHER", firstHash, default);
        var existing = (await library.ListAsync("OTHER", default)).Single();

        Require((await library.CopyAsync("GROUP", "GROUP", [secondHash], default)).Status == StickerCopyStatus.InvalidSelection
            && (await library.CopyAsync("GROUP", "UNKNOWN", [secondHash], default)).Status == StickerCopyStatus.UnknownGroup
            && (await library.CopyAsync("GROUP", "OTHER", [secondHash, new string('f', 64)], default)).Status
                == StickerCopyStatus.StickerUnavailable
            && (await library.ListAsync("OTHER", default)).Single() == existing,
            "拒绝同群、未知群和来源群之外的表情，失败时没有部分复制");

        env.Clock.Advance(TimeSpan.FromMinutes(1));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            library.CopyAsync("GROUP", "OTHER", [firstHash, secondHash, secondHash], default))));
        var copied = await library.ListAsync("OTHER", default);
        Require(results.All(result => result.Status == StickerCopyStatus.Success)
            && results.Sum(result => result.Copied) == 1 && results.Sum(result => result.AlreadyInTarget) == 3
            && copied.Single(row => row.Hash == firstHash) == existing,
            "重复提交与重复哈希复用目标条目，保留目标名称和发送次数");
        Require(copied.Single(row => row.Hash == secondHash) is { Label: "待复制名称", IsEnabled: false, SeenCount: 0, SentCount: 0 } item
            && item.FirstSeenAt == env.Clock.GetUtcNow().UtcDateTime
            && (await library.ListAsync("GROUP", default)).Single(row => row.Hash == secondHash) is { SeenCount: 1, SentCount: 1 },
            "新条目沿用名称和停用状态，在目标群独立统计");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fillers = Enumerable.Range(0, GroupStickerLibrary.MaxPerGroup - 1).Select(index => index.ToString("x64")).ToArray();
        db.StickerAssets.AddRange(fillers.Select(hash => new StickerAssetEntity { Hash = hash, Bytes = 1 }));
        db.GroupStickers.AddRange(fillers.Select(hash => new GroupStickerEntity { GroupId = "FULL", Hash = hash }));
        await db.SaveChangesAsync();
        Require((await library.CopyAsync("GROUP", "FULL", [firstHash, secondHash], default))
                is { Status: StickerCopyStatus.TargetFull, Available: 1 }
            && await db.GroupStickers.CountAsync(row => row.GroupId == "FULL") == GroupStickerLibrary.MaxPerGroup - 1
            && (await library.CopyAsync("GROUP", "FULL", [firstHash], default)).Copied == 1
            && (await library.CopyAsync("GROUP", "FULL", [firstHash], default))
                is { Status: StickerCopyStatus.Success, Copied: 0, AlreadyInTarget: 1 },
            "容量不足整批拒绝，满库仍能跳过已存在图片");

        Require(Directory.GetFiles(env.StickerDirectory).Length == 2
            && await library.DeleteAsync("GROUP", secondHash, default)
            && await library.ReadAsync("OTHER", secondHash, forReply: false, default) is { } file
            && file.Bytes.SequenceEqual(second), "复制共用原文件，删除来源收藏后仍能在目标群读取");
    }

    private static async Task ProactiveStickersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.KnownGroups.Add(new KnownGroupEntity { GroupOpenId = "GROUP" });
        await db.SaveChangesAsync();
        var group = new GroupConfig
        {
            GroupId = "GROUP", IsEnabled = true, AiProactiveEnabled = true, UseStickersInReplies = true,
            StickerReplyTendency = 0, StickerProactiveTendency = 2, StickerIntervalMinutes = 9
        };
        await configs.SaveGroupConfigAsync(group);
        await configs.SaveProactiveConfigAsync(new ProactiveChatConfig
        {
            Enabled = true, AllowActiveChannel = true, QuietStartHour = 0, QuietEndHour = 0
        });
        var library = env.Services.GetRequiredService<IGroupStickerLibrary>();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aI1sAAAAASUVORK5CYII=");
        Require(await library.ImportAsync("GROUP", "庆祝", png, default) is null
            && (await library.ListAsync("GROUP", default)).Single().SeenCount == 0,
            "手动导入静态图片不会增加群里出现次数");
        Require((await library.ChoicesAsync("GROUP", proactive: false, default)) is { Tendency: 0, Items.Count: 1 }
            && (await library.ChoicesAsync("GROUP", proactive: true, default)).Items.Count == 0,
            "仅 @ 回复时择机模型没有表情候选");
        group.UseStickersInProactive = true;
        await configs.SaveGroupConfigAsync(group);

        var workflow = env.Services.GetRequiredService<ProactiveWorkflow>();
        var now = env.Clock.GetUtcNow().UtcDateTime;
        ProactiveWindow Window(string id) => new()
        {
            GroupId = "GROUP", LastMessageId = "", LastMessageUtcAt = now,
            Messages = [new ObservedMessage("虚拟用户", "终于写完了", id, now)]
        };
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("""{"line":"","sticker_id":1}"""));
        await workflow.RunAsync(Window("no-text"), default);
        Require(env.Api.RichMessages.Count == 0 && env.Api.PassiveCalls == 0 && env.StickerMaterials.Hashes.Count == 0,
            "选中表情不触发单发表情");

        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("""{"line":"辛苦，庆祝一下！","sticker_id":1}"""));
        await workflow.RunAsync(Window("has-text"), default);
        using var request = JsonDocument.Parse(env.Http.Requests.Last().Body);
        var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var sent = env.Api.RichMessages.Single();
        var wire = SendMessageRequest.From(sent);
        Require(env.Http.Requests.Count == 2 && prompt.Contains("更愿意用图") && prompt.Contains("sticker_id")
            && sent.IsActive && wire.MsgType == 2 && wire.Markdown is { ForceVerifyImageResource: true } markdown
            && markdown.Content.Contains("qqbot-media://image/v1/synthetic-sticker"),
            "择机配图倾向随原请求传入，主动渠道可发图文，无额外模型调用");
        Require((await library.ListAsync("GROUP", default)).Single().SentCount == 1,
            "主动图文确认送达后增加图库发送次数");
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        Require((await library.ChoicesAsync("GROUP", proactive: false, default)).Items.Count == 0
            && (await library.ChoicesAsync("GROUP", proactive: true, default)).Items.Count == 0,
            "同群最低间隔同时限制 @ 回复与择机发言");
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        Require((await library.ChoicesAsync("GROUP", proactive: true, default)) is { Tendency: 2, Items.Count: 1 },
            "同群最低间隔到期后恢复候选");
    }

    private static async Task OversizedGroupStickersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await configs.AddKnownGroupAsync("GROUP", null);
        await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AutoCollectStickers = true });
        var library = env.Services.GetRequiredService<IGroupStickerLibrary>();
        var png = TestStickerPng(1280, 1024, noise: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        Require(png.Length > StickerSettings.MaxStoredBytes && png.Length <= StickerSettings.MaxSourceBytes,
            "测试图片在尺寸限额内，原始字节数超限");
        Require(await library.ImportAsync("GROUP", "上传时取的名", png, ct) is null, "允许上传大于存储限额的原图");
        var stored = (await library.ListAsync("GROUP", ct)).Single();
        var file = await library.ReadAsync("GROUP", stored.Hash, false, ct);
        Require(file is { ContentType: "image/webp" } && file.Bytes.Length <= StickerSettings.MaxStoredBytes
            && stored.Hash == Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), "按压缩后的文件大小和内容计算存储与哈希");

        var widePng = TestStickerPng(2560, 1280);
        Require(widePng.Length < StickerSettings.MaxStoredBytes, "宽图仅尺寸超限");
        env.StickerHttp.Handler = (request, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/wide" ? widePng : png)
        });
        var collector = env.Services.GetRequiredService<GroupStickerCollector>();
        await collector.StartAsync(ct);
        try
        {
            collector.TryEnqueue("GROUP", new StickerAttachment("https://example.org/large", "消息附带名称", png.Length));
            collector.TryEnqueue("GROUP", new StickerAttachment("https://example.org/wide", "宽图", widePng.Length));
            IReadOnlyList<GroupSticker> rows;
            while ((rows = await library.ListAsync("GROUP", ct)).Count < 2) await Task.Delay(20, ct);
            Require(rows.Single(row => row.Hash == stored.Hash) is { Label: "上传时取的名", SeenCount: 1 }
                && Directory.GetFiles(env.StickerDirectory).Length == 2, "群聊大图和同图上传压缩结果一致，同群只保存一份");
            var wide = await library.ReadAsync("GROUP", rows.Single(row => row.Label == "宽图").Hash, false, ct);
            Require(wide is { Width: 2048, Height: 1024, ContentType: "image/webp" }
                && wide.Bytes.Length <= StickerSettings.MaxStoredBytes, "宽图等比缩小，尺寸和大小均合规");
            using var decoded = SKBitmap.Decode(wide!.Bytes);
            Require(decoded.GetPixel(512, 512).Alpha == 128, "转码保留透明度");
        }
        finally { await collector.StopAsync(CancellationToken.None); }
    }

    private static async Task GroupStickerNamingAsync()
    {
        await using var env = await TestEnvironment.CreateAsync(legacy: true);
        using var scope = env.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await configs.AddKnownGroupAsync("GROUP", null);
        await configs.AddKnownGroupAsync("OTHER", null);
        var groupConfig = await db.GroupConfigs.SingleAsync(row => row.GroupId == "GROUP");
        Require(!groupConfig.AutoNameStickers, "升级旧库后自动取名默认为关闭");
        var connection = new AiProviderConnectionEntity
        {
            Name = "合成视觉连接", ApiBaseUrl = "https://vision.example.org/v1", ApiKey = "synthetic-vision-key",
            HeadersJson = """{"X-Feature":"synthetic"}"""
        };
        db.AiProviderConnections.Add(connection);
        await db.SaveChangesAsync();
        await configs.SaveAiConfigAsync(env.Config() with
        {
            ProviderConnectionId = connection.Id, ModelName = "synthetic-vision", WebSearchEnabled = true,
            WebSearchEndpoint = "openai", WebSearchShape = "enable_search", MaxAttachedImages = 0
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var library = env.Services.GetRequiredService<IGroupStickerLibrary>();
        var naming = env.Services.GetRequiredService<GroupStickerNamingService>();
        var png = TestStickerPng(16, 16);
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        Require(await library.ImportAsync("GROUP", null, png, ct) is null, "合规原图直接入库");
        var stored = await library.ReadAsync("GROUP", hash, false, ct);
        Require(stored is { ContentType: "image/png" } && stored.Bytes.SequenceEqual(png), "合规图片保留原始内容和哈希");
        Require((await naming.SuggestAsync("OTHER", hash, ct)).Status == GroupStickerNamingService.NameStatus.NotFound
            && env.Http.Requests.Count == 0, "他群图片不可提交到模型");
        env.Http.Handler = (request, body, cancellationToken) =>
        {
            using var json = JsonDocument.Parse(body);
            var payload = json.RootElement;
            Require(request.RequestUri!.AbsoluteUri == "https://vision.example.org/v1/chat/completions"
                && request.Headers.Authorization?.Parameter == "synthetic-vision-key"
                && request.Headers.GetValues("X-Feature").Single() == "synthetic"
                && payload.GetProperty("model").GetString() == "synthetic-vision"
                && !payload.TryGetProperty("enable_search", out _), "复用 AI 设置当前连接、请求头及模型，取名仅调用视觉模型");
            var image = payload.GetProperty("messages")[1].GetProperty("content")[1]
                .GetProperty("image_url").GetProperty("url").GetString();
            Require(image == "data:image/png;base64," + Convert.ToBase64String(png), "只传当前表情的本地图片内容");
            return Task.FromResult(JsonReply("“开心鼓掌”"));
        };
        Require((await naming.SuggestAsync("GROUP", hash, ct)).Label == "开心鼓掌"
            && (await library.ListAsync("GROUP", ct)).Single().Label == "", "手动取名先返回建议，未保存时图库名称不变");

        groupConfig.AutoNameStickers = true;
        await db.SaveChangesAsync(ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Http.Handler = async (_, _, token) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    started.SetResult();
                    await release.Task.WaitAsync(token);
                    return JsonReply("由 AI 生成");
                case 2:
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                default:
                    return JsonReply("可爱歪头");
            }
        };
        await naming.StartAsync(ct);
        try
        {
            var editedPng = TestStickerPng(17, 16);
            var editedHash = Convert.ToHexStringLower(SHA256.HashData(editedPng));
            Require(await library.ImportAsync("GROUP", "", editedPng, ct) is null, "取名等待期间上传已完成");
            await started.Task.WaitAsync(ct);
            await library.UpdateAsync("GROUP", editedHash, "管理员取名", true, ct);
            Require(await library.ImportAsync("GROUP", null, editedPng, ct) is null, "重复上传继续复用条目");
            var failedPng = TestStickerPng(18, 16);
            var failedHash = Convert.ToHexStringLower(SHA256.HashData(failedPng));
            Require(await library.ImportAsync("GROUP", null, failedPng, ct) is null, "模型故障前已保存图片");
            var namedPng = TestStickerPng(19, 16);
            var namedHash = Convert.ToHexStringLower(SHA256.HashData(namedPng));
            await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AutoCollectStickers = true }, ct);
            await env.Services.GetRequiredService<GroupStickerLibrary>().CollectAsync("GROUP", "", namedPng, ct);
            release.SetResult();
            IReadOnlyList<GroupSticker> rows;
            while (!(rows = await library.ListAsync("GROUP", ct)).Any(row => row.Hash == namedHash && row.Label == "可爱歪头"))
                await Task.Delay(20, ct);
            Require(rows.Single(row => row.Hash == editedHash).Label == "管理员取名"
                && rows.Single(row => row.Hash == failedHash).Label == ""
                && rows.Single(row => row.Hash == hash).Label == "" && calls == 3,
                "自动取名只处理新增的未命名表情，保留手动名称，模型失败时保留图片且不重复调用");
            await library.UpdateAsync("GROUP", namedHash, "", false, ct, preserveLabel: true);
            Require((await library.ListAsync("GROUP", ct)).Single(row => row.Hash == namedHash)
                is { Label: "可爱歪头", IsEnabled: false }, "仅修改启停时保留后台刚生成的名称");
        }
        finally { await naming.StopAsync(CancellationToken.None); }
    }

    private static byte[] TestStickerPng(int width, int height, bool noise = false)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (noise)
        {
            var pixels = new byte[width * height * 4];
            new Random(17).NextBytes(pixels);
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        else bitmap.Erase(new SKColor(255, 100, 40, 128));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static async Task QqStickerMaterialsAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var settings = Options.Create(new MutterOptions
        {
            AppId = "synthetic-app", ClientSecret = "synthetic-secret", ApiBaseUrl = "https://api.example.org"
        });
        var ticket = 0;
        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var apiHttp = new FakeHttp
        {
            Handler = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/app/getAppAccessToken" => Json("""{"access_token":"synthetic-token","expires_in":3600}"""),
                "/v2/media-images/prepare" => Json("""{"upload_id":"upload","presigned_url":"https://storage.example.org/upload"}"""),
                "/v2/media-images/complete" => Json(JsonSerializer.Serialize(new
                {
                    image_id = $"image-{++ticket}", url = $"qqbot-media://image/v1/image-{ticket}", ttl = 3600
                })),
                "/v2/media-images/image-1/delete" => Json("""{"err_code":40035008}"""),
                _ => throw new InvalidOperationException("Unexpected synthetic QQ request")
            })
        };
        using var apiClient = new HttpClient(apiHttp);
        var tokens = new AccessTokenManager(apiClient, settings, NullLogger<AccessTokenManager>.Instance);
        var api = new QqBotApiService(apiClient, tokens, settings, NullLogger<QqBotApiService>.Instance);
        var service = new QqStickerMaterialService(api, env.Services.GetRequiredService<IHttpClientFactory>(),
            env.Services.GetRequiredService<IServiceScopeFactory>(), settings, env.Clock,
            NullLogger<QqStickerMaterialService>.Instance);
        var file = new StickerFile([7, 8, 9], "image/png", 1, 1);
        var hash = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
        var md5 = Convert.ToHexStringLower(MD5.HashData(file.Bytes));
        env.StickerHttp.Handler = async (request, _, ct) =>
        {
            Require(request.Method == HttpMethod.Put && request.Headers.Authorization is null
                && request.Content?.Headers.ContentType?.MediaType == "image/png"
                && (await request.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(file.Bytes),
                "预签名 PUT 只发送原始图片和 MIME，不携带 QQ 鉴权头");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{md5}\"");
            return response;
        };

        var reference = await service.GetReferenceAsync(hash, file, default);
        Require(reference == "qqbot-media://image/v1/image-1" && reference == await service.GetReferenceAsync(hash, file, default)
            && env.StickerHttp.Requests.Count == 1, "同一机器人和内容哈希复用已上传素材");
        var prepared = apiHttp.Requests[1];
        using var parameters = JsonDocument.Parse(prepared.Body);
        Require(prepared.Url.AbsolutePath.EndsWith("/prepare") && prepared.Authorization == "QQBot synthetic-token"
            && parameters.RootElement.GetProperty("md5").GetString() == md5
            && parameters.RootElement.GetProperty("sha1").GetString() == Convert.ToHexStringLower(SHA1.HashData(file.Bytes)),
            "准备请求使用实际文件摘要和 QQ 鉴权");
        using var complete = JsonDocument.Parse(apiHttp.Requests[2].Body);
        Require(complete.RootElement.GetProperty("md5").GetString() == md5
            && complete.RootElement.GetProperty("upload_id").GetString() == "upload", "完成请求使用 PUT 返回的 ETag");

        await service.InvalidateAsync(hash, reference!, default);
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        Require(await service.GetReferenceAsync(hash, file, default) == "qqbot-media://image/v1/image-2"
            && apiHttp.Requests.Count(request => request.Url.AbsolutePath.EndsWith("/delete")) == 1,
            "只有已缓存并明确失效的素材才被替换");
    }
}
