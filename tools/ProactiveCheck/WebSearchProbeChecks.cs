using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Web.Controllers;

internal static partial class RuntimeChecks
{
    private static async Task SearchProbeContractsAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var capabilities = new AiSearchCapabilityStore();
        var compat = AddConnection("openai", "zhipu");
        var gemini = AddConnection("gemini", "custom");
        await db.SaveChangesAsync();
        using var client = new HttpClient(env.Http, disposeHandler: false);
        var runner = new WebSearchProbeRunner(client, NullLogger<WebSearchProbeRunner>.Instance);
        var controller = new ConfigController(db, capabilities);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        env.Http.Handler = (request, body, _) =>
        {
            Require(request.RequestUri!.AbsolutePath == "/v1/chat/completions" &&
                    request.Headers.GetValues("X-Probe-Connection").Single() == "fixture",
                "兼容检测沿用保存的协议、路径与请求头");
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            if (!payload.TryGetProperty("tools", out var tools) || !tools[0].TryGetProperty("web_search", out var searchTool))
                return Task.FromResult(JsonReply("NO_SEARCH"));
            Require(searchTool.GetProperty("search_result").GetBoolean(), "智谱工具请求返回真实检索结果");
            var content = payload.GetProperty("messages")[0].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.Array
                ? AnswerImage(content[1].GetProperty("image_url").GetProperty("url").GetString()!)
                : "News release";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { message = new { content = text } } },
                    web_search = new[] { new { link = "https://example.invalid/news" } }
                })
            });
        };
        await using var stream = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Response.Body = stream;
        await controller.ProbeWebSearchStream(Target(compat), runner, default);
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        var events = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<WebSearchProbeEventDto>(line, jsonOptions)!).ToArray();
        var report = events[^1].Report!;
        Require(events[0].Type == "start" && events[^1].Type == "done" &&
                events.Count(e => e.Type == "case") == events[0].Cases!.Count &&
                env.Http.Requests.Count == 2 && report.Results[0].Status == "verified" &&
                report.Results[0].ImageStatus == "verified" && report.Results.Skip(1).All(r => r.Status == "not_tested"),
            "常见快速路径只发文字、图文各一次；流式报告明确标出未测项");
        Require(capabilities.GetProbeResult(compat.ApiBaseUrl, compat.DefaultModel, "web_search_tool", compat.Id, compat.Revision) == (true, true),
            "文字与图文的有效证据都可读回");
        Require(capabilities.GetProbeResult(compat.ApiBaseUrl, compat.DefaultModel, "web_search_tool", compat.Id, "new-version") == (false, null),
            "同一账号修改连接后不继承旧版本的检测结论");

        env.Http.Requests.Clear();
        var fullScan = Target(compat);
        fullScan.StopOnVerified = false;
        fullScan.PreferredShape = "web_search_options";
        report = (WebSearchProbeReportDto)((OkObjectResult)await controller.ProbeWebSearch(fullScan, runner, default)).Value!;
        Require(report.Results[0] is { Shape: "web_search_options", Status: "no_evidence" } &&
                report.Results[1] is { Shape: "web_search_tool", Status: "verified" } &&
                report.Results.All(r => r.Status != "not_tested") &&
                env.Http.Requests.Count == report.Results.Count + 1,
            "证据不足时继续尝试可用候选，完整检测在命中后仍扫描其余兼容形状");

        env.Http.Requests.Clear();
        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        report = (WebSearchProbeReportDto)((OkObjectResult)await controller.ProbeWebSearch(fullScan, runner, default)).Value!;
        Require(env.Http.Requests.Count == 1 && report.Reachable && !report.AuthFailed &&
                report.Results[0].Status == "rate_limited" && report.Results.Skip(1).All(r => r.Status == "not_tested"),
            "限流后即使完整检测也停止追加请求，仍返回完整报告");
        Require(capabilities.GetProbeResult(compat.ApiBaseUrl, compat.DefaultModel, "web_search_tool", compat.Id, compat.Revision) == (true, true),
            "限流不覆盖已验证的图文能力");

        env.Http.Requests.Clear();
        env.Http.Handler = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/v1beta/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"candidates":[{"content":{"parts":[{"text":"没有输出图片答案"}]},
                      "groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.invalid/news"}}]}}]}
                    """)
            });
        report = (WebSearchProbeReportDto)((OkObjectResult)await controller.ProbeWebSearch(Target(gemini), runner, default)).Value!;
        var result = report.Results.Single();
        Require(env.Http.Requests.Count == 3 && env.Http.Requests[2].Url == env.Http.Requests[1].Url &&
                env.Http.Requests[2].Body.Contains("inline_data") &&
                result.Status == "verified" && result.ImageStatus == "image_unconfirmed",
            "原生探测只回退一次路径，图文复用命中路径，缺少图片答案时保留文字验证");
        Require(capabilities.GetProbeResult(gemini.ApiBaseUrl, gemini.DefaultModel, "gemini_native", gemini.Id, gemini.Revision) == (true, null),
            "图片未确认时不记录图文失败");

        AiProviderConnectionEntity AddConnection(string protocol, string template)
        {
            var connection = new AiProviderConnectionEntity
            {
                ApiBaseUrl = "https://example.invalid/v1", ApiKey = "synthetic",
                Name = protocol, Protocol = protocol, TemplateId = template, DefaultModel = "fixture",
                ModelsJson = """[{"id":"fixture"}]""", HeadersJson = """{"X-Probe-Connection":"fixture"}"""
            };
            db.AiProviderConnections.Add(connection);
            return connection;
        }

        static WebSearchProbeDto Target(AiProviderConnectionEntity connection) => new()
        {
            ProviderConnectionId = connection.Id, ProviderConnectionVersion = connection.Revision,
            ApiBaseUrl = connection.ApiBaseUrl, ModelName = connection.DefaultModel, StopOnVerified = true
        };
    }

    private static string AnswerImage(string dataUrl)
    {
        var png = Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
        var colors = new List<string>();
        for (var offset = 41; offset < 53; offset += 3)
        {
            colors.Add(Convert.ToHexString(png.AsSpan(offset, 3)) switch
            {
                "FF0000" => "red", "00B000" => "green", "003BFF" => "blue", "FFEB00" => "yellow",
                "FF8500" => "orange", "8E27CC" => "purple", "FFFFFF" => "white", "000000" => "black",
                _ => throw new InvalidOperationException("未知的探测图颜色")
            });
        }
        return "COLORS: " + string.Join(", ", colors) + "\nNews release";
    }
}
