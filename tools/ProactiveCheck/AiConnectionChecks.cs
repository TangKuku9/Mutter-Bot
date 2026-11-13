using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using Mutter.Web;
using Mutter.Web.Controllers;
using Mutter.Web.Models;

internal static class AiConnectionChecks
{
    public static async Task<bool> RunAsync()
    {
        try
        {
            await VerifyWebExtensionAuthAsync();
            await VerifyLegacySchemaUpgradeAsync();
            await VerifyOutboundPolicyAsync();
            VerifyRequestCompatibility();
            await using var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sqlite).Options);
            await db.Database.EnsureCreatedAsync();
            db.AiConfigs.Add(new AiConfigEntity { ApiBaseUrl = "https://gateway.example.test/v1", ApiKey = "account-a-secret", ModelName = "Model/A" });
            db.AiPresets.AddRange(
                new AiConfigPresetEntity { Name = "account-b", ApiKey = "account-b-secret", ConfigJson = Legacy("https://gateway.example.test/v1", "Model/A") },
                new AiConfigPresetEntity { Name = "unknown", ConfigJson = Legacy("https://unknown.example.test/v1", "unknown-model") },
                new AiConfigPresetEntity { Name = "legacy-external", ConfigJson = "{\"version\":1,\"apiBaseUrl\":\"https://gateway.example.test/v1\",\"modelName\":\"Model/A\",\"externalSearch\":{\"provider\":\"tavily\",\"apiKey\":\"tvly-legacy-secret\"}}" },
                new AiConfigPresetEntity { Name = "malformed", ConfigJson = "{\"modelName\":[]}" });
            await db.SaveChangesAsync();
            await db.EnsureSchemaUpgradedAsync();
            var migrated = await db.AiProviderConnections.ToListAsync();
            var ids = migrated.Select(c => c.Id).Order().ToArray();
            await db.EnsureSchemaUpgradedAsync();
            Require(ids.SequenceEqual(await db.AiProviderConnections.OrderBy(c => c.Id).Select(c => c.Id).ToArrayAsync()), "migration must be idempotent");
            Require(migrated.Count == 2 && migrated.Select(c => c.ApiKey).Distinct().Count() == 2, "same endpoint with different credentials remains separate");
            var currentConnectionId = (await db.AiConfigs.AsNoTracking().FirstAsync()).ProviderConnectionId;
            Require(migrated.Single(c => c.Id == currentConnectionId).ApiKey == "account-a-secret",
                "current config keeps its own credential through migration");
            var migratedPreset = await db.AiPresets.SingleAsync(p => p.Name == "account-b");
            var migratedConfig = JsonSerializer.Deserialize<AiPresetConfigDto>(migratedPreset.ConfigJson, AiConnectionStore.JsonOptions)!;
            Require(migratedConfig.Version == 2 && migratedConfig.ProviderConnectionId.Length > 0 && migratedPreset.ApiKey == "", "presets migrate to references without duplicated secrets");
            var externalPreset = await db.AiPresets.SingleAsync(p => p.Name == "legacy-external");
            Require(!externalPreset.ConfigJson.Contains("tvly-legacy-secret", StringComparison.Ordinal),
                "legacy presets must not retain an external search key in ConfigJson");
            // 已绑定预设的旧 Key 副本（列与 JSON）也要清掉：否则详情接口会回显“前 4 + 后 4”的打码值。
            var boundPreset = await db.AiPresets.SingleAsync(p => p.Name == "account-b");
            boundPreset.ApiKey = "leftover-preset-secret";
            var boundConfig = JsonSerializer.Deserialize<AiPresetConfigDto>(boundPreset.ConfigJson, AiConnectionStore.JsonOptions)!;
            boundConfig.ApiKey = "leftover-json-secret";
            boundPreset.ConfigJson = JsonSerializer.Serialize(boundConfig, AiConnectionStore.JsonOptions);
            await db.SaveChangesAsync();
            await db.EnsureSchemaUpgradedAsync();
            db.ChangeTracker.Clear();
            var boundAfter = await db.AiPresets.AsNoTracking().SingleAsync(p => p.Name == "account-b");
            Require(boundAfter.ApiKey.Length == 0 && !boundAfter.ConfigJson.Contains("leftover-json-secret", StringComparison.Ordinal),
                "bound presets keep no key copy in either the column or ConfigJson");
            Require(await db.AiPresets.AnyAsync(p => p.Name == "malformed"),
                "a malformed preset is preserved instead of being deleted by migration");
            // 跳过分支（地址无效）也必须把清掉的悬空引用落盘，否则每次启动都重复告警与计数。
            db.AiPresets.Add(new AiConfigPresetEntity
            {
                Name = "dangling",
                ConfigJson = "{\"providerConnectionId\":\"ghost-connection\",\"apiBaseUrl\":\"not-a-url\",\"modelName\":\"m\"}"
            });
            await db.SaveChangesAsync();
            await db.EnsureSchemaUpgradedAsync();
            db.ChangeTracker.Clear();
            Require(!(await db.AiPresets.AsNoTracking().SingleAsync(p => p.Name == "dangling"))
                    .ConfigJson.Contains("ghost-connection", StringComparison.Ordinal),
                "clearing a dangling preset reference must be persisted even on a skip path");
            Console.WriteLine("PASS  AI 连接迁移幂等、同地址不同账号隔离、异常旧预设保留");

            var handler = new CatalogHandler();
            using var http = new HttpClient(handler);
            var executor = new RecordingExecutor();
            var capabilities = new AiSearchCapabilityStore();
            var connections = new AiConnectionsController(db, new ClientFactory(http), executor, capabilities);
            var configApi = new ConfigController(db, capabilities);
            VerifyTemplates((AiConfigDto)((OkObjectResult)(await configApi.GetAiConfig()).Result!).Value!);
            // 迁移已经清过列；这里再塞一个残留值，确认详情接口自己也不会按“未绑定”路径回显部分打码。
            var seededPreset = await db.AiPresets.SingleAsync(p => p.Id == boundAfter.Id);
            seededPreset.ApiKey = "live-leftover-secret";
            await db.SaveChangesAsync();
            var boundDetail = (AiPresetDetailDto)((OkObjectResult)(await configApi.GetAiPreset(boundAfter.Id)).Result!).Value!;
            Require(boundDetail.ApiKey.Length == 0, "preset detail must not echo key fragments for bound presets");
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
            b = Saved(await connections.Update(b.Id!, edit, default));
            Require(capabilities.GetProbeImageCapability(b.ApiBaseUrl, "Model/A", "shape", b.Id!) is null, "editing connection clears its capability cache");
            Require((await configApi.ApplyAiPreset(preset.Id)).Result is OkObjectResult, "referenced preset remains usable after rotation");
            runtime = (await new ConfigRepository(db).GetAiConfigAsync())!;
            Require(runtime.ApiKey == "rotated-b-secret" && runtime.Headers["X-Tenant"] == "tenant-secret", "presets use current connection credentials and headers");
            var storedPreset = await db.AiPresets.SingleAsync(p => p.Id == preset.Id);
            Require(storedPreset.ApiKey.Length == 0 && !storedPreset.ConfigJson.Contains("rotated-b-secret", StringComparison.Ordinal), "new presets never store the connection key");
            Require(await connections.Delete(b.Id!, default) is ConflictObjectResult, "referenced connection cannot be deleted");
            edit = Copy(b);
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
            var echoed = HttpModelTransport.RedactForLog(
                "sk-abcdef123456\r\n{\"error\":\"bad key AIzaSyDTESTKEY0000000000\",\"api_key\":\"search-live-123456\",\"Authorization\":\"Bearer abcdefgh12345678\"}");
            Require(new[] { "abcdef123456", "AIzaSyDTESTKEY0000000000", "search-live-123456", "abcdefgh12345678", "\r", "\n" }
                .All(secret => !echoed.Contains(secret, StringComparison.Ordinal)), "upstream errors must not leak credentials or inject log lines");
            var allowedAddresses = OutboundAddressPolicy.FilterAllowed([
                IPAddress.Parse("169.254.169.254"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse("fd00:ec2::254"), IPAddress.Parse("fd20:ce::254"),
                IPAddress.Parse("192.168.1.10")]);
            Require(allowedAddresses.Length == 1 && allowedAddresses[0].Equals(IPAddress.Parse("192.168.1.10")),
                "resolved-address filtering must drop link-local/metadata (IPv4 and IPv6 ULAs) and keep private addresses");
            var mismatched = await connections.Create(new AiConnectionInputDto
            {
                Name = "mismatch", TemplateId = "openai", Protocol = "gemini",
                ApiBaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-6-luna",
                Models = [new() { Id = "gpt-6-luna", Name = "gpt-6-luna" }]
            }, default);
            Require(mismatched.Result is BadRequestObjectResult,
                "a built-in template must reject a conflicting connection protocol");
            // 旧版本可能留下“模板/协议错配”的连接：不改变模板与协议时应仍可编辑，改出新错配仍要拒绝。
            var legacyCreate = (AiConnectionDto)((OkObjectResult)(await connections.Create(new AiConnectionInputDto
            {
                Name = "legacy-mismatch", TemplateId = "openai", Protocol = "openai",
                ApiBaseUrl = "https://legacy-mismatch.example.test/v1", DefaultModel = "legacy-model",
                Models = [new() { Id = "legacy-model", Name = "legacy-model" }]
            }, default)).Result!).Value!;
            var legacyEntity = (await db.AiProviderConnections.FindAsync(legacyCreate.Id))!;
            legacyEntity.Protocol = "gemini"; // 模拟旧版本落下的错配
            await db.SaveChangesAsync();
            var legacyEdit = new AiConnectionInputDto
            {
                UpdatedAt = legacyCreate.UpdatedAt,
                Name = "legacy-mismatch-renamed", TemplateId = "openai", Protocol = "gemini",
                ApiBaseUrl = legacyEntity.ApiBaseUrl, DefaultModel = "legacy-model",
                Models = [new() { Id = "legacy-model", Name = "legacy-model" }]
            };
            legacyEdit.UpdatedAt = Saved(await connections.Update(legacyCreate.Id!, legacyEdit, default)).UpdatedAt;
            legacyEdit.TemplateId = "gemini";
            legacyEdit.Protocol = "openai";
            Require((await connections.Update(legacyCreate.Id!, legacyEdit, default)).Result is BadRequestObjectResult,
                "changing a built-in template into a conflicting protocol is still rejected");
            capabilities.RecordProbe("same", "Model/A", "s", true, "account-a");
            Require(capabilities.GetProbeImageCapability("same", "Model/A", "s", "account-a") is true,
                "probe records must be readable under the same key");
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

            // ===== 回归：Gemini 原生连接的结构化输出必须以 responseMimeType 表达 =====
            using var structuredHttp = new HttpClient(new StructuredGeminiHandler());
            var structuredTransport = new HttpModelTransport(structuredHttp, NullLogger<HttpModelTransport>.Instance);
            var structuredExecutor = new AiModelExecutor(structuredTransport, NullLogger<AiModelExecutor>.Instance);
            var structuredConfig = new AiConfig
            {
                ApiBaseUrl = "https://gateway.example.test/v1beta", ApiKey = "native-key", Protocol = "gemini",
                ModelName = "gemini-model", MaxTokens = 64, MaxInputTokens = 512
            };
            var structuredResult = await structuredExecutor.ExecuteAsync(
                new AiChatRequest("Return JSON.", [AiMessage.Plain("user", "OK")], "json_object"),
                structuredConfig, AiSearchStrategy.CompatNoSearch, default);
            Require(structuredResult.Content.Contains("\"ok\":true", StringComparison.Ordinal),
                "Gemini structured output must run through responseMimeType instead of failing locally");
            Console.WriteLine("PASS  Gemini 原生连接的结构化输出走 responseMimeType，不再本地 415");

            // ===== 回归：打码值往返、大小写改名与 origin 校验 =====
            var listed = (List<AiConnectionDto>)((OkObjectResult)(await connections.List(default)).Result!).Value!;
            var maskedConnection = listed.Single(c => c.Id == b.Id);
            Require(maskedConnection.ApiKey == AiConnectionStore.SecretMask
                && maskedConnection.Headers["X-Tenant"] == AiConnectionStore.SecretMask,
                "connection list must return full masks for key and headers");
            var caseOnly = Copy(maskedConnection);
            caseOnly.Headers = new() { ["x-tenant"] = AiConnectionStore.SecretMask };
            var beforeCase = maskedConnection.Revision;
            maskedConnection = Saved(await connections.Update(b.Id!, caseOnly, default));
            var afterCase = (await db.AiProviderConnections.FindAsync(b.Id))!;
            Require(afterCase.ApiKey == "rotated-b-secret" && AiConnectionStore.Headers(afterCase)["x-tenant"] == "tenant-secret"
                && afterCase.Revision == beforeCase, "masked round-trips and header casing keep credentials and capability revision");
            var newHeaderMask = Copy(maskedConnection);
            newHeaderMask.Headers = new()
            {
                ["X-Tenant"] = AiConnectionStore.SecretMask,
                ["X-New"] = AiConnectionStore.SecretMask
            };
            Require((await connections.Update(b.Id!, newHeaderMask, default)).Result is BadRequestObjectResult,
                "a mask pasted into a brand-new header must be rejected instead of stored as empty");
            // 带连接 ID 但字段类型错误的预设不能让连接编辑 500
            db.AiPresets.Add(new AiConfigPresetEntity
            {
                Name = "broken-bound",
                ConfigJson = $"{{\"providerConnectionId\":\"{b.Id}\",\"modelName\":[]}}"
            });
            await db.SaveChangesAsync();
            maskedConnection = Saved(await connections.Update(b.Id!, Copy(maskedConnection), default));
            var moved = Copy(maskedConnection);
            moved.ApiBaseUrl = "https://attacker.example.test/v1";
            Require((await connections.Update(b.Id!, moved, default)).Result is BadRequestObjectResult,
                "masked credentials must not follow a new origin on update");
            Require((await connections.Discover(moved, default)).Result is BadRequestObjectResult,
                "masked credentials must not be replayed to a new origin by discovery");
            Console.WriteLine("PASS  打码密钥与请求头可往返、改大小写不丢值、换 origin 拒绝解析");

            // ===== 回归：连接版本失效必须清空旧搜索策略，匹配版本保留 =====
            var configEntity = await db.AiConfigs.FirstAsync();
            configEntity.WebSearchEndpoint = "openai";
            configEntity.WebSearchShape = "enable_search";
            configEntity.WebSearchVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var staleInput = Config(b.Id!, "Model/A");
            staleInput.WebSearchEndpoint = "openai";
            staleInput.WebSearchShape = "enable_search";
            staleInput.ProviderConnectionVersion = "stale-revision";
            Require(await configApi.UpdateAiConfig(staleInput) is NoContentResult, "stale version save is accepted (safely degraded)");
            var degraded = await db.AiConfigs.AsNoTracking().FirstAsync();
            Require(degraded.WebSearchEndpoint.Length == 0 && degraded.WebSearchShape.Length == 0
                && degraded.WebSearchVerifiedAt is null,
                "stale connection version must clear the search strategy and its verified time");
            var freshInput = Config(b.Id!, "Model/A");
            freshInput.WebSearchEndpoint = "openai";
            freshInput.WebSearchShape = "enable_search";
            freshInput.ProviderConnectionVersion = (await db.AiProviderConnections.FindAsync(b.Id))!.Revision;
            Require(await configApi.UpdateAiConfig(freshInput) is NoContentResult, "matching version is accepted");
            Require((await db.AiConfigs.AsNoTracking().FirstAsync()).WebSearchShape == "enable_search",
                "matching version keeps the selected strategy");
            Console.WriteLine("PASS  连接版本失效清空搜索策略，版本匹配时保留");

            // ===== 回归：被当前配置/预设引用的模型不能移除 =====
            var guardResult = await connections.Create(new AiConnectionInputDto
            {
                Name = "ref-guard", ApiBaseUrl = "https://ref.example.test/v1", DefaultModel = "X",
                Models = [new() { Id = "X", Name = "X" }, new() { Id = "Y", Name = "Y" }]
            }, default);
            var guard = (AiConnectionDto)((OkObjectResult)guardResult.Result!).Value!;
            Require(await configApi.UpdateAiConfig(Config(guard.Id!, "X")) is NoContentResult, "bind config to the guard connection");
            var removeX = Copy(guard);
            removeX.Models = [new() { Id = "Y", Name = "Y" }];
            removeX.DefaultModel = "Y";
            Require((await connections.Update(guard.Id!, removeX, default)).Result is ConflictObjectResult,
                "model referenced by the current config cannot be removed");
            Require(await configApi.UpdateAiConfig(Config(guard.Id!, "Y")) is NoContentResult, "switch the current model to Y");
            var guardPreset = await configApi.CreateAiPreset(new AiPresetSaveDto { Name = "guard-preset", Config = Config(guard.Id!, "X") });
            Require(guardPreset.Result is CreatedAtActionResult, "preset can reference X");
            Require((await connections.Update(guard.Id!, removeX, default)).Result is ConflictObjectResult,
                "model referenced by a preset cannot be removed");
            Console.WriteLine("PASS  当前配置与预设引用的模型都不能被移除");

            // ===== 回归：刚探测并选用的策略，即使目标同时变化也必须保留 =====
            var keepEntity = await db.AiConfigs.FirstAsync();
            keepEntity.WebSearchEndpoint = "openai";
            keepEntity.WebSearchShape = "enable_search";
            keepEntity.WebSearchVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            capabilities.RecordProbe(guard.ApiBaseUrl, "X", "enable_search", true, guard.Id!);
            var keepInput = Config(guard.Id!, "X");
            keepInput.WebSearchEndpoint = "openai";
            keepInput.WebSearchShape = "enable_search";
            keepInput.ProviderConnectionVersion = (await db.AiProviderConnections.FindAsync(guard.Id))!.Revision;
            Require(await configApi.UpdateAiConfig(keepInput) is NoContentResult, "probe-selected strategy save is accepted");
            var keptEntity = await db.AiConfigs.AsNoTracking().FirstAsync();
            Require(keptEntity.WebSearchShape == "enable_search" && keptEntity.WebSearchImageCapable is true,
                "a freshly probed strategy must survive a simultaneous target change");
            Console.WriteLine("PASS  刚探测选用的搜索策略不会因切换目标被静默清空");

            // ===== 回归：解绑连接时清空主配置里的密钥副本 =====
            Require(await configApi.UpdateAiConfig(Config(b.Id!, "Model/A")) is NoContentResult,
                "bind the config back to the keyed connection");
            var boundEntity = await db.AiConfigs.AsNoTracking().FirstAsync();
            Require(boundEntity.ApiKey == "rotated-b-secret", "bound config stores a compatibility copy for old APIs");
            var unbindInput = Config(string.Empty, "Model/A");
            unbindInput.ApiBaseUrl = boundEntity.ApiBaseUrl;
            unbindInput.ApiKey = AiConnectionStore.SecretMask;
            Require(await configApi.UpdateAiConfig(unbindInput) is NoContentResult, "unbind save is accepted");
            var unboundEntity = await db.AiConfigs.AsNoTracking().FirstAsync();
            Require(unboundEntity.ProviderConnectionId.Length == 0 && unboundEntity.ApiKey.Length == 0,
                "unbinding must clear the compatibility key copy");
            Console.WriteLine("PASS  解绑连接会清空主配置的密钥副本");

            // 两个页面先读到同一快照，再先后 PUT；使用独立请求上下文，避免只测 EF 跟踪缓存。
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sqlite).Options;
            await using var firstDb = new AppDbContext(options);
            await using var secondDb = new AppDbContext(options);
            var firstApi = new AiConnectionsController(firstDb, new ClientFactory(http), executor);
            var secondApi = new AiConnectionsController(secondDb, new ClientFactory(http), executor);
            var firstDraft = Copy(guard);
            var staleDraft = Copy(guard);
            firstDraft.Models.Add(new() { Id = "new-model", Name = "new-model" });
            var latest = Saved(await firstApi.Update(guard.Id!, firstDraft, default));
            staleDraft.Name = "stale-rename";
            Require((await secondApi.Update(guard.Id!, staleDraft, default)).Result is ConflictObjectResult,
                "a stale browser draft must not overwrite a completed save");
            var retry = Copy(latest);
            retry.Name = "fresh-rename";
            var savedRetry = Saved(await secondApi.Update(guard.Id!, retry, default));
            Require(savedRetry.Models.Any(m => m.Id == "new-model") && savedRetry.UpdatedAt != latest.UpdatedAt,
                "reloading and retrying preserves the other writer's model and advances the edit version");
            Console.WriteLine("PASS  过期页面草稿被拒绝，重新读取后可安全保存");

            // ===== 回归：Gemini 原生目录发现（字段名、前缀剥离、认证头） =====
            var geminiHandler = new GeminiCatalogHandler();
            using var geminiHttp = new HttpClient(geminiHandler);
            var geminiApi = new AiConnectionsController(db, new ClientFactory(geminiHttp), executor);
            var geminiDiscover = await geminiApi.Discover(new AiConnectionInputDto
            {
                Name = "gemini-discover", Protocol = "gemini", ApiBaseUrl = "https://g.example.test/v1beta",
                ApiKey = "gemini-key", DefaultModel = "gemini-x",
                Models = [new() { Id = "gemini-x", Name = "gemini-x" }]
            }, default);
            var geminiModels = (List<AiConnectionModel>)((OkObjectResult)geminiDiscover.Result!).Value!;
            Require(geminiModels.Select(m => m.Id).SequenceEqual(["gemini-x", "gemini-y"]),
                "gemini discovery must read the models array and strip the models/ prefix");
            Require(geminiHandler.LastPath == "/v1beta/models?pageSize=1000",
                "gemini discovery must call the native models endpoint");
            Console.WriteLine("PASS  Gemini 原生目录发现正确解析 models 数组与前缀");

            // ===== 回归：连接数量上限 =====
            var existingCount = await db.AiProviderConnections.CountAsync();
            for (var i = existingCount; i < 100; i++)
                db.AiProviderConnections.Add(new AiProviderConnectionEntity
                {
                    Name = $"bulk-{i}", ApiBaseUrl = $"https://bulk{i}.example.test/v1",
                    ModelsJson = "[{\"id\":\"m\",\"name\":\"m\"}]", DefaultModel = "m"
                });
            await db.SaveChangesAsync();
            var overflow = await connections.Create(new AiConnectionInputDto
            {
                Name = "overflow", ApiBaseUrl = "https://overflow.example.test/v1",
                DefaultModel = "m", Models = [new() { Id = "m", Name = "m" }]
            }, default);
            Require(overflow.Result is BadRequestObjectResult, "connection cap must reject the 101st row");
            Console.WriteLine("PASS  连接数量上限生效");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  AI 连接自检：{ex.Message}");
            return false;
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static AiConnectionDto Saved(ActionResult<AiConnectionDto> result) =>
        result.Result is OkObjectResult { Value: AiConnectionDto saved } ? saved
            : throw new InvalidOperationException($"connection save failed: {result.Result}");

    private static async Task VerifyOutboundPolicyAsync()
    {
        foreach (var host in new[] { "169.254.169.254", "[::ffff:169.254.169.254]", "metadata.google.internal.", "METADATA." })
            Require(!AiConnectionStore.IsValidUrl($"http://{host}/"), $"metadata host {host} must be rejected");
        Require(AiConnectionStore.IsValidUrl("http://127.0.0.1:1234/v1"), "local models remain supported");

        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        try
        {
            using var handler = OutboundAddressPolicy.CreateHandler();
            handler.Proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                await client.GetAsync("http://[fd00:ec2::254]/");
                throw new InvalidOperationException("metadata request must not reach a proxy");
            }
            catch (HttpRequestException ex)
            {
                Require(ex.ToString().Contains("云元数据", StringComparison.Ordinal), "connection policy must reject the actual target");
            }
            Require(!proxy.Pending(), "a configured forward proxy must not receive protected requests");
        }
        finally { proxy.Stop(); }
        Console.WriteLine("PASS  元数据域名与解析地址受限，系统转发代理无法绕过连接校验");
    }

    /// <summary>
    /// 公开入口 ConfigureMutterWeb 必须自带鉴权：漏挂时所有 API/Hub 在无会话时也能访问。
    /// 起一个随机端口的宿主，断言未登录请求得到 401。
    /// </summary>
    private static async Task VerifyWebExtensionAuthAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.ConfigureMutterWeb();
        await app.StartAsync();
        try
        {
            var address = app.Urls.First();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"{address}/api/config/ai");
            Require(response.StatusCode == HttpStatusCode.Unauthorized,
                "the public web extension must protect API routes with the panel auth middleware");
        }
        finally
        {
            await app.StopAsync();
        }
        Console.WriteLine("PASS  ConfigureMutterWeb 公开入口自带面板鉴权");
    }

    /// <summary>
    /// 用“删掉新表/新列”的库模拟升级前结构，验证补表/补列与数据迁移是真实执行过的，
    /// 而不是只在 EnsureCreated 建好的新结构上走 duplicate 分支。
    /// </summary>
    private static async Task VerifyLegacySchemaUpgradeAsync()
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sqlite).Options);
        await db.Database.EnsureCreatedAsync();
        db.AiConfigs.Add(new AiConfigEntity
        {
            ApiBaseUrl = "https://legacy.example.test/v1", ApiKey = "legacy-secret", ModelName = "legacy-model"
        });
        await db.SaveChangesAsync();
        // 还原升级前结构：没有连接表，AiConfigs 也没有 ProviderConnectionId。
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AiProviderConnections\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AiConfigs\" DROP COLUMN \"ProviderConnectionId\"");
        await db.EnsureSchemaUpgradedAsync();
        await db.EnsureSchemaUpgradedAsync();
        var columnCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('AiConfigs') WHERE name = 'ProviderConnectionId'").SingleAsync();
        Require(columnCount == 1, "legacy database must gain the ProviderConnectionId column");
        var connections = await db.AiProviderConnections.ToListAsync();
        Require(connections.Count == 1 && connections[0].ApiKey == "legacy-secret",
            "legacy credentials must migrate into a connection");
        var runtime = (await new ConfigRepository(db).GetAiConfigAsync())!;
        Require(runtime.ProviderConnectionId == connections[0].Id && runtime.ApiKey == "legacy-secret"
            && runtime.ModelName == "legacy-model", "legacy config must resolve through the migrated connection");
        // 悬空引用：连接被删掉后读取必须降级而不是抛异常，重启迁移自愈。
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AiProviderConnections\"");
        var degraded = (await new ConfigRepository(db).GetAiConfigAsync())!;
        Require(degraded.ApiKey == "legacy-secret" && degraded.ApiBaseUrl == "https://legacy.example.test/v1",
            "dangling connection must degrade to legacy fields instead of throwing");
        await db.EnsureSchemaUpgradedAsync();
        Require(await db.AiProviderConnections.CountAsync() == 1,
            "startup migration must rebuild a dangling connection");
        Console.WriteLine("PASS  旧库结构升级（补表/补列）幂等且保留凭据");
    }

    private static void VerifyTemplates(AiConfigDto config)
    {
        var templates = config.AvailableProviders.Where(p => p.Id != "custom").ToList();
        Require(templates.Select(p => p.Id).Distinct().Count() == templates.Count
            && templates.Any(p => p.Category == "china") && templates.Any(p => p.Category == "international"),
            "config API must expose distinct templates in both provider groups");
        foreach (var template in templates)
        {
            Require(AiConnectionStore.IsValidUrl(template.ApiBaseUrl) && template.Protocol is "openai" or "gemini"
                && template.Models.Contains(template.DefaultModel) && template.DocumentationUrl.StartsWith("https://", StringComparison.Ordinal)
                && template.ModelsUpdatedAt.Length > 0, $"template {template.Id} must expose a valid endpoint, model and provenance");
        }
        Console.WriteLine($"PASS  {templates.Count} 家服务商目录通过实际配置接口返回，默认模型与协议完整");
    }

    private static void VerifyRequestCompatibility()
    {
        var request = new AiChatRequest("Return JSON.", [AiMessage.Plain("user", "OK")], "json_object");
        foreach (var (provider, model) in new[]
        {
            ("openai", "gpt-6-luna"), ("kimi", "kimi-k3.1-pro"), ("minimax", "MiniMax-M3.1-Flash")
        })
        {
            var config = new AiConfig { ProviderTemplateId = provider, ModelName = model, MaxTokens = 1024, Temperature = 0.3 };
            var body = OpenAiRequestBuilder.Build(request, config, null);
            Require(body.GetValueOrDefault("max_completion_tokens") is 1024
                && !body.ContainsKey("max_tokens") && !body.ContainsKey("temperature"), "current reasoning models must use accepted request parameters");
            Require(config.Temperature == 0.3, "provider constraints must not rewrite saved preferences");
            if (provider == "minimax")
                Require(body.GetValueOrDefault("reasoning_split") is true, "MiniMax thinking must remain separate from reply content");
        }
        var legacy = OpenAiRequestBuilder.Build(request, new AiConfig { ModelName = "custom-model", MaxTokens = 512, Temperature = 0 }, null);
        Require(legacy.GetValueOrDefault("max_tokens") is 512 && legacy.GetValueOrDefault("temperature") is 0d,
            "generic compatible services retain their original request contract");
        var claude = OpenAiRequestBuilder.Build(request, new AiConfig { ProviderTemplateId = "anthropic" }, null);
        Require(!claude.ContainsKey("response_format") && claude.ContainsKey("messages"), "Claude compatible layer must not receive unsupported json_object requests");
        // 聚合平台保留平台契约，预算字段与 JSON 约束不按模型原厂改写。
        foreach (var (template, model) in new[]
        {
            ("openrouter", "openai/gpt-6-luna"),
            ("siliconflow", "Pro/moonshotai/Kimi-K2.7-Code"),
            ("together", "MiniMaxAI/MiniMax-M3.1-Flash"),
            ("groq", "openai/gpt-oss-120b"),
            ("openrouter", "anthropic/claude-sonnet-5.5")
        })
        {
            var body = OpenAiRequestBuilder.Build(request,
                new AiConfig { ProviderTemplateId = template, ModelName = model, MaxTokens = 1024, Temperature = 0.3 }, null);
            Require(body.GetValueOrDefault("max_tokens") is 1024 && !body.ContainsKey("max_completion_tokens")
                && !body.ContainsKey("reasoning_split") && body.ContainsKey("temperature") && body.ContainsKey("response_format"),
                $"{template} must retain its documented budget and output contract");
        }
        Console.WriteLine("PASS  新 GPT、Kimi、MiniMax 参数约束与 Claude 兼容层处理正确，旧兼容接口行为保留");
    }
    private static string Legacy(string url, string model) => JsonSerializer.Serialize(new { version = 1, apiBaseUrl = url, modelName = model });
    private static AiConfigInputDto Config(string id, string model) => new()
    {
        ProviderConnectionId = id, ModelName = model, Temperature = 0.7,
        MaxTokens = 512, MaxContextMessages = 20, MaxInputTokens = 4096
    };
    private static AiConnectionInputDto Copy(AiConnectionDto value) => new()
    {
        Id = value.Id, UpdatedAt = value.UpdatedAt, Name = value.Name, TemplateId = value.TemplateId, Protocol = value.Protocol,
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
    private sealed class GeminiCatalogHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.PathAndQuery;
            Require(request.Headers.TryGetValues("x-goog-api-key", out var keys) && keys.Single() == "gemini-key",
                "gemini discovery must use the native API-key header");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[{\"name\":\"models/gemini-x\"},{\"name\":\"gemini-y\"},{\"name\":\"models/bad\\u0001id\"}]}",
                    Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class StructuredGeminiHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri!.AbsolutePath.EndsWith(":generateContent", StringComparison.Ordinal),
                "structured output must use the native Gemini endpoint");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Require(body.Contains("\"responseMimeType\":\"application/json\"", StringComparison.Ordinal),
                "structured output must set generationConfig.responseMimeType");
            Require(!body.Contains("\"tools\"", StringComparison.Ordinal),
                "structured request must not combine search tools with JSON mode");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"ok\\\":true}\"}]}}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1}}",
                    Encoding.UTF8, "application/json")
            };
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
