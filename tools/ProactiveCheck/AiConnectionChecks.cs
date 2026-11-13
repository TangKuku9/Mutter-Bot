using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Ai;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Web.Controllers;
using Mutter.Web.Models;

internal static class AiConnectionChecks
{
    public static async Task<bool> RunAsync()
    {
        try
        {
            await using var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sqlite).Options);
            await db.Database.EnsureCreatedAsync();
            db.AiConfigs.Add(new AiConfigEntity { ApiBaseUrl = "https://gateway.example.test/v1", ApiKey = "account-a-secret", ModelName = "Model/A" });
            db.AiPresets.AddRange(
                new AiConfigPresetEntity { Name = "account-b", ApiKey = "account-b-secret", ConfigJson = Legacy("https://gateway.example.test/v1", "Model/A") },
                new AiConfigPresetEntity { Name = "unknown", ConfigJson = Legacy("https://unknown.example.test/v1", "unknown-model") },
                new AiConfigPresetEntity { Name = "malformed", ConfigJson = "{\"modelName\":[]}" });
            await db.SaveChangesAsync();
            await db.EnsureSchemaUpgradedAsync();
            var migrated = await db.AiProviderConnections.ToListAsync();
            var ids = migrated.Select(c => c.Id).Order().ToArray();
            await db.EnsureSchemaUpgradedAsync();
            Require(ids.SequenceEqual(await db.AiProviderConnections.OrderBy(c => c.Id).Select(c => c.Id).ToArrayAsync()), "migration must be idempotent");
            Require(migrated.Count == 2 && migrated.Select(c => c.ApiKey).Distinct().Count() == 2, "same endpoint with different credentials remains separate");
            var migratedPreset = await db.AiPresets.SingleAsync(p => p.Name == "account-b");
            var migratedConfig = JsonSerializer.Deserialize<AiPresetConfigDto>(migratedPreset.ConfigJson, AiConnectionStore.JsonOptions)!;
            Require(migratedConfig.Version == 2 && migratedConfig.ProviderConnectionId.Length > 0 && migratedPreset.ApiKey == "", "presets migrate to references without duplicated secrets");
            Console.WriteLine("PASS  AI 连接迁移幂等、同地址不同账号隔离、异常旧预设保留");

            var handler = new CatalogHandler();
            using var http = new HttpClient(handler);
            var executor = new RecordingExecutor();
            var capabilities = new AiSearchCapabilityStore();
            var connections = new AiConnectionsController(db, new ClientFactory(http), executor, capabilities);
            var configApi = new ConfigController(db, capabilities);
            var list = (List<AiConnectionDto>)((OkObjectResult)(await connections.List(default)).Result!).Value!;
            Require(!JsonSerializer.Serialize(list).Contains("account-a-secret", StringComparison.Ordinal)
                && !JsonSerializer.Serialize(list).Contains("account-b-secret", StringComparison.Ordinal), "connection catalog must mask credentials");
            var b = list.Single(c => c.Id == migratedConfig.ProviderConnectionId);
            var currentInput = Config(b.Id!, "Model/A");
            currentInput.ApiKey = "old-masked-placeholder";
            currentInput.ApiBaseUrl = "https://wrong-target.example.test/v1";
            Require(await configApi.UpdateAiConfig(currentInput) is NoContentResult, "connection can be selected through the normal config API");
            var runtime = (await new ConfigRepository(db).GetAiConfigAsync())!;
            Require(runtime.ApiKey == "account-b-secret" && runtime.ApiBaseUrl == b.ApiBaseUrl, "runtime resolves the selected connection, ignoring stale form credentials");
            Require((await configApi.ApplyAiPreset((await db.AiPresets.SingleAsync(p => p.Name == "unknown")).Id)).Result is BadRequestObjectResult,
                "ambiguous keyless legacy preset requires explicit binding");
            Console.WriteLine("PASS  连接目录不回显密钥，切换连接与旧预设绑定正确");

            var created = await configApi.CreateAiPreset(new AiPresetSaveDto { Name = "reference-test", Config = Config(b.Id!, "Model/A") });
            var preset = (AiPresetDetailDto)((CreatedAtActionResult)created.Result!).Value!;
            var edit = Copy(b);
            edit.ApiKey = "rotated-b-secret";
            edit.Headers = new() { ["X-Tenant"] = "tenant-secret" };
            capabilities.RecordProbe(b.ApiBaseUrl, "Model/A", "shape", true, b.Id!);
            Require((await connections.Update(b.Id!, edit, default)).Result is OkObjectResult, "connection can be updated");
            Require(capabilities.GetProbeImageCapability(b.ApiBaseUrl, "Model/A", "shape", b.Id!) is null, "editing connection clears its capability cache");
            Require((await configApi.ApplyAiPreset(preset.Id)).Result is OkObjectResult, "referenced preset remains usable after rotation");
            runtime = (await new ConfigRepository(db).GetAiConfigAsync())!;
            Require(runtime.ApiKey == "rotated-b-secret" && runtime.Headers["X-Tenant"] == "tenant-secret", "presets use current connection credentials and headers");
            var storedPreset = await db.AiPresets.SingleAsync(p => p.Id == preset.Id);
            Require(storedPreset.ApiKey.Length == 0 && !storedPreset.ConfigJson.Contains("rotated-b-secret", StringComparison.Ordinal), "new presets never store the connection key");
            Require(await connections.Delete(b.Id!, default) is ConflictObjectResult, "referenced connection cannot be deleted");
            edit.Models = [];
            Require((await connections.Update(b.Id!, edit, default)).Result is BadRequestObjectResult, "empty model directory cannot be saved");
            Console.WriteLine("PASS  预设引用最新密钥与请求头，删除保护与验证缓存失效正确");

            var localInput = new AiConnectionInputDto
            {
                Name = "local", ApiBaseUrl = "http://127.0.0.1:1234/v1", ApiKey = "",
                DefaultModel = "local-model", Models = [new() { Id = "local-model", Name = "Local" }]
            };
            var local = (AiConnectionDto)((OkObjectResult)(await connections.Create(localInput, default)).Result!).Value!;
            Require(await configApi.UpdateAiConfig(Config(local.Id!, "local-model")) is NoContentResult, "keyless local connection is selectable");
            Require((await new ConfigRepository(db).GetAiConfigAsync())!.ApiKey.Length == 0, "keyless connection must not inherit the previous account key");
            var beforeModels = (await db.AiProviderConnections.FindAsync(local.Id))!.ModelsJson;
            var discovery = Copy(local);
            discovery.Headers = new() { ["X-Tenant"] = "catalog-tenant" };
            var discovered = (List<AiConnectionModel>)((OkObjectResult)(await connections.Discover(discovery, default)).Result!).Value!;
            Require(discovered.Select(m => m.Id).SequenceEqual(["Model/A", "model/a"]), "discovery preserves model ID case");
            Require(handler.Tenant == "catalog-tenant", "discovery sends this connection's custom headers");
            Require((await db.AiProviderConnections.FindAsync(local.Id))!.ModelsJson == beforeModels, "discovery is a draft and does not overwrite saved models");
            handler.Status = HttpStatusCode.ServiceUnavailable;
            Require((await connections.Discover(discovery, default)).Result is ObjectResult { StatusCode: 502 }, "discovery failure is actionable");
            Require((await db.AiProviderConnections.FindAsync(local.Id))!.ModelsJson == beforeModels, "discovery failure keeps existing models");
            Require(await connections.Test(Copy(local), default) is OkObjectResult && executor.Last?.ApiKey == "", "test uses the same runtime connection resolution");
            Console.WriteLine("PASS  无认证本地连接、模型目录获取、失败保留与真实调用路径正确");

            var cloned = (AiConnectionDto)((OkObjectResult)(await connections.Duplicate(b.Id!, default)).Result!).Value!;
            var clonedEntity = (await db.AiProviderConnections.FindAsync(cloned.Id))!;
            Require(cloned.Id != b.Id && clonedEntity.ApiKey == "rotated-b-secret", "duplicating a connection preserves its independent credentials");
            Require((await connections.Create(new AiConnectionInputDto { Name = "bad", ApiBaseUrl = "file:///tmp/model" }, default)).Result is BadRequestObjectResult,
                "invalid endpoint is rejected");
            capabilities.RecordProbe("same", "Model/A", "s", true, "account-a");
            Require(capabilities.GetProbeImageCapability("same", "Model/A", "s", "account-b") is null
                && capabilities.GetProbeImageCapability("same", "model/a", "s", "account-a") is null,
                "capability results remain scoped to exact connection and model");
            Console.WriteLine("PASS  连接复制、接口校验及同名模型的能力隔离正确");
            using var protocolHttp = new HttpClient(new ProtocolHandler());
            var transport = new HttpModelTransport(protocolHttp, NullLogger<HttpModelTransport>.Instance);
            var realExecutor = new AiModelExecutor(transport, NullLogger<AiModelExecutor>.Instance);
            var nativeConfig = new AiConfig
            {
                ApiBaseUrl = "https://gateway.example.test/v1beta", ApiKey = "native-key", Protocol = "gemini",
                ModelName = "gemini-model", MaxTokens = 64, MaxInputTokens = 512,
                Headers = new Dictionary<string, string> { ["X-Tenant"] = "runtime-tenant" }
            };
            var nativeResult = await realExecutor.ExecuteAsync(new AiChatRequest("Reply briefly.",
                [AiMessage.Plain("user", "OK")]), nativeConfig, AiSearchStrategy.CompatNoSearch, default);
            Require(nativeResult.Content == "OK", "explicit Gemini connection uses its native HTTP protocol and custom headers");
            nativeConfig.Protocol = "openai";
            var compatibleResult = await realExecutor.ExecuteAsync(new AiChatRequest("Reply briefly.",
                [AiMessage.Plain("user", "OK")]), nativeConfig, AiSearchStrategy.CompatNoSearch, default);
            Require(compatibleResult.Content == "OK", "OpenAI-compatible connection passes custom headers to the actual transport");
            Console.WriteLine("PASS  OpenAI 兼容与 Gemini 原生连接的实际 HTTP 协议和请求头正确");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  AI 连接自检：{ex.Message}");
            return false;
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Legacy(string url, string model) => JsonSerializer.Serialize(new { version = 1, apiBaseUrl = url, modelName = model });
    private static AiConfigInputDto Config(string id, string model) => new()
    {
        ProviderConnectionId = id, ModelName = model, Temperature = 0.7,
        MaxTokens = 512, MaxContextMessages = 20, MaxInputTokens = 4096
    };
    private static AiConnectionInputDto Copy(AiConnectionDto value) => new()
    {
        Id = value.Id, Name = value.Name, TemplateId = value.TemplateId, Protocol = value.Protocol,
        ApiBaseUrl = value.ApiBaseUrl, ApiKey = value.ApiKey, Headers = new(value.Headers),
        TimeoutSeconds = value.TimeoutSeconds, DefaultModel = value.DefaultModel,
        Models = value.Models.Select(m => new AiConnectionModel { Id = m.Id, Name = m.Name, IsManual = m.IsManual }).ToList()
    };
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Tenant { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tenant = request.Headers.TryGetValues("X-Tenant", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"Model/A\"},{\"id\":\"model/a\"}]}", Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class RecordingExecutor : IAiModelExecutor
    {
        public AiConfig? Last { get; private set; }
        public Task<AiChatResponse> ExecuteAsync(AiChatRequest request, AiConfig config, AiSearchStrategy strategy, CancellationToken ct)
        {
            Last = config;
            return Task.FromResult(new AiChatResponse { Content = "OK", Model = config.ModelName });
        }
    }
    private sealed class ProtocolHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Headers.TryGetValues("X-Tenant", out var tenant) && tenant.Single() == "runtime-tenant", "transport must apply the connection headers");
            var native = request.RequestUri!.AbsolutePath.EndsWith("/models/gemini-model:generateContent", StringComparison.Ordinal);
            Require(native || request.RequestUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal), "protocol determines the HTTP endpoint");
            if (native) Require(request.Headers.GetValues("x-goog-api-key").Single() == "native-key", "Gemini uses the native API-key header");
            else Require(request.Headers.Authorization?.Parameter == "native-key", "compatible protocol uses the selected connection key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(native
                    ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]}}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1}}"
                    : "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}", Encoding.UTF8, "application/json")
            });
        }
    }
}
