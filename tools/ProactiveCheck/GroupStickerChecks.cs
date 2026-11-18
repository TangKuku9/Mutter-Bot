using System.Net;
using System.Net.Http.Headers;
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
using Mutter.Infrastructure.Services;

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
