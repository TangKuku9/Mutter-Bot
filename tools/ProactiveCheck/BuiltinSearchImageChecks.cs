using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Services;

internal static partial class RuntimeChecks
{
    private static async Task BuiltinSearchImagesAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var config = env.Config() with
        {
            WebSearchEnabled = true,
            WebSearchEndpoint = "openai",
            WebSearchShape = "web_search_tool"
        };
        await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SaveAiConfigAsync(config);

        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "choices": [{"message": {
                    "content": "## 大熊猫\n\n这是大熊猫。![自写图](https://untrusted.example.net/fake.png)",
                    "annotations": [
                      {"type":"url_citation","url_citation":{"url":"https://example.org/unavailable"}},
                      {"type":"url_citation","url_citation":{"url":"https://example.org/source"}}
                    ]
                  }}]
                }
                """, Encoding.UTF8, "application/json")
        });
        env.PreviewHttp.Handler = (request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/unavailable")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://127.0.0.1/private");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(PreviewPage("""
                <html><head>
                <!-- <meta property="og:image" content="https://example.net/no.png"> -->
                <script>let m = '<meta property="og:image" content="https://example.net/no.png">';</script>
                <meta property="og:image" content=" ">
                <meta content='/panda.png?a=1&amp;b=2' PROPERTY="og:image">
                </head></html>
                """));
        };

        var handler = env.Services.GetRequiredService<MessageEventHandler>();
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        await queue.StartAsync(default);
        await handler.HandleDispatchAsync(new QqDispatch("GROUP_AT_MESSAGE_CREATE", "synthetic-builtin-at", 1,
            JsonSerializer.SerializeToElement(new GroupMessageEvent
            {
                Id = "synthetic-builtin-at", GroupOpenid = "GROUP", Content = "找一张大熊猫图片",
                Author = new AuthorMember { MemberOpenid = "USER", Username = "虚拟用户" }
            })));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await queue.StopAsync(deadline.Token);

        using var modelRequest = JsonDocument.Parse(env.Http.Requests.Single().Body);
        var root = modelRequest.RootElement;
        Require(root.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search"
            && root.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("用户明确想看图"),
            "群内找图使用内建搜索，且引导模型引用带图来源");
        Require(env.PreviewHttp.Requests.Select(request => request.Url.AbsolutePath)
            .SequenceEqual(["/unavailable", "/source"]), "不跟随回环重定向，第二条结构化引用可以继续配图");
        var sent = SendMessageRequest.From(env.Api.RichMessages.Single());
        Require(sent.MsgId == "synthetic-builtin-at" && sent.MsgType == 2
            && sent.Markdown is { ForceVerifyImageResource: true } markdown
            && markdown.Content.Contains("![检索配图](https://example.org/panda.png?a=1&b=2)")
            && !markdown.Content.Contains("untrusted.example.net")
            && !markdown.Content.Contains("example.net/no.png"), "只嵌入引用页声明的有效图片");
        Require(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatMessages
            .AnyAsync(message => message.IsBotMessage && message.HasImage), "发送确认后历史记录标记图片");

        var chat = env.Services.GetRequiredService<IAiChatService>();
        var before = env.PreviewHttp.Requests.Count;
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("正文里出现 https://example.org/source"));
        var noEvidence = await chat.ChatReplyAsync("测试", [("user", "找大熊猫图片")], [], config, true);
        Require(noEvidence.SearchMarkdown is null && env.PreviewHttp.Requests.Count == before,
            "无结构化引文时不会访问正文中的地址");

        Require(!OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("10.0.0.1"))
            && !OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("::ffff:127.0.0.1"))
            && !OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("fd00::1"))
            && !OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("100.100.100.200"))
            && OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("1.1.1.1"))
            && OutboundAddressPolicy.IsPublicWebAddress(IPAddress.Parse("2606:4700::1111")),
            "来源网页连接只允许公网 IP");
    }

    private static HttpResponseMessage PreviewPage(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };
}
