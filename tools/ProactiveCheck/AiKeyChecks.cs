using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Web.Controllers;
using Mutter.Web.Models;

internal static partial class RuntimeChecks
{
    /// <summary>
    /// 外搜 Key 归属：同一服务商沿用，跨服务商绝不沿用旧 Key/旧网关；
    /// 旧版 9–12 位 Key 的 4+****+4 打码也必须识别为“未修改”。
    /// </summary>
    private static async Task AiKeyOwnershipAsync()
    {
        const string currentKey = "test-key-1234567890"; // 15 位：新版与旧版打码相同
        const string masked = "test****7890";
        const string searchUrl = "https://api.tavily.com";
        const string otherUrl = "https://attacker.example.test";

        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", "", searchUrl) == currentKey,
            "same provider keeps the saved key when the form is left empty");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", masked, searchUrl) == currentKey,
            "same provider treats the masked value as unchanged");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "", "tavily", masked, "") == currentKey,
            "the default endpoint keeps the saved key");
        // 一次保存就能把真 Key 绑到新网关，之后的探测会以“已保存地址”为基准通过 origin 校验。
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", masked, otherUrl) == "",
            "changing the search origin invalidates the saved key instead of following it");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", "", otherUrl) == "",
            "an empty form after an origin change does not replay the saved key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", "fresh-key", otherUrl) == "fresh-key",
            "a genuinely new key is accepted together with a new gateway");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "tavily", masked, searchUrl + "/v1")
            == currentKey, "same origin with a different path still reuses the key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "brave", "", searchUrl) == "",
            "a provider switch never reuses the old key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "brave", masked, searchUrl) == "",
            "a provider switch never treats the old mask as a new key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "brave", "brave-new-key", searchUrl) == "brave-new-key",
            "a genuinely new key is accepted after a provider switch");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, searchUrl, "searxng", "anything", searchUrl) == "",
            "SearXNG never stores a key");

        // 9–12 位 Key 在旧版本显示为前 4 + **** + 后 4；升级后旧页面仍可能提交这种值。
        const string shortKey = "abcdefghi";
        Require(ConfigController.ResolveProviderAwareApiKey("custom", shortKey, "https://custom.test", "custom", "abcd****fghi", "https://custom.test") == shortKey,
            "the legacy 4+****+4 mask is still recognized as unchanged after the masking threshold change");
        Require(ConfigController.ResolveProviderAwareApiKey("custom", shortKey, "https://custom.test", "brave", "abcd****fghi", "https://custom.test") == "",
            "the legacy mask is not forwarded to another provider");

        var changed = new ExternalSearchInputDto
        {
            Provider = "brave",
            ApiKey = masked,
            BaseUrl = "https://api.tavily.com/"
        };
        ConfigController.ClearStaleExternalSearchSubmit(changed,
            new ExternalSearchConfig { Provider = "tavily", ApiKey = currentKey, BaseUrl = "https://api.tavily.com" });
        Require(changed.ApiKey.Length == 0 && changed.BaseUrl.Length == 0,
            "a provider switch clears the old mask and the old gateway address");

        var fresh = new ExternalSearchInputDto
        {
            Provider = "brave",
            ApiKey = "brave-new-key",
            BaseUrl = "https://api.brave.com"
        };
        ConfigController.ClearStaleExternalSearchSubmit(fresh,
            new ExternalSearchConfig { Provider = "tavily", ApiKey = currentKey, BaseUrl = "https://api.tavily.com" });
        Require(fresh.ApiKey == "brave-new-key" && fresh.BaseUrl == "https://api.brave.com",
            "new credentials and a new gateway survive the provider switch");

        // 经过真实的创建、序列化、应用、落库和回读路径，避免单测只验证 Key 解析函数。
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var api = new ConfigController(db, scope.ServiceProvider.GetRequiredService<AiSearchCapabilityStore>());
        Require(await api.UpdateAiConfig(new AiConfigInputDto
        {
            ExternalSearch = new() { Enabled = true, Provider = "brave" }
        }) is BadRequestObjectResult, "normal saves must reject an enabled search provider without a key");
        var entity = await db.AiConfigs.SingleAsync();
        var previous = new ExternalSearchConfig
        {
            Enabled = true, Provider = "tavily", BaseUrl = "https://old-search.example.test",
            ApiKey = currentKey, VerifiedAt = env.Clock.GetUtcNow().UtcDateTime
        };
        foreach (var (provider, address, retainKey) in new[]
        {
            ("tavily", "https://old-search.example.test/", true),
            ("tavily", "https://new-search.example.test", false),
            ("brave", "https://new-search.example.test", false),
            ("custom", "https://old-search.example.test", false),
            ("searxng", "https://new-search.example.test", false)
        })
        {
            entity.ExternalSearchJson = ExternalSearchJsonCodec.Encode(previous);
            await db.SaveChangesAsync();
            var created = await api.CreateAiPreset(new AiPresetSaveDto
            {
                Name = $"target-{provider}-{retainKey}", IncludeApiKey = false,
                Config = new AiConfigInputDto
                {
                    ApiBaseUrl = env.Config().ApiBaseUrl, ModelName = env.Config().ModelName,
                    MaxTokens = 512, MaxContextMessages = 20, WebSearchEnabled = true,
                    ExternalSearch = new ExternalSearchInputDto
                    {
                        Enabled = true, Provider = provider, BaseUrl = address, ApiKey = "not-stored-in-preset"
                    }
                }
            });
            Require(created.Result is CreatedAtActionResult, "the search preset is created through the controller");
            var detail = (AiPresetDetailDto)((CreatedAtActionResult)created.Result!).Value!;
            Require(detail.Config.ExternalSearch!.ApiKey.Length == 0, "the preset never contains a search secret");
            var applied = await api.ApplyAiPreset(detail.Id);
            Require(applied.Result is OkObjectResult, "the preset can be applied before filling in a new search key");
            db.ChangeTracker.Clear();
            entity = await db.AiConfigs.SingleAsync();
            var stored = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson);
            Require(stored.Provider == provider && stored.BaseUrl == address && stored.Enabled == true,
                "restoring a preset keeps its explicit search target and switch");
            Require(stored.ApiKey == (retainKey ? currentKey : string.Empty),
                "a search preset inherits credentials only for the same provider and address");
            Require(retainKey || stored.VerifiedAt is null, "changing a search target clears its verification");
            var response = (AiConfigDto)((OkObjectResult)applied.Result!).Value!;
            // 回显只暴露“已配置”状态：完整掩码，不再给出首尾 4 位。
            Require(retainKey ? response.ExternalSearch!.ApiKey == AiConnectionStore.SecretMask : response.ExternalSearch!.ApiKey.Length == 0,
                "the returned form accurately shows whether a search key was retained");
        }

        // 普通表单保存仍支持相同服务商的打码值，不受预设“明确写空”策略影响。
        entity.ExternalSearchJson = ExternalSearchJsonCodec.Encode(previous);
        await db.SaveChangesAsync();
        Require(await api.UpdateAiConfig(new AiConfigInputDto
        {
            ExternalSearch = new ExternalSearchInputDto
            {
                Enabled = true, Provider = previous.Provider, BaseUrl = previous.BaseUrl, ApiKey = masked
            }
        }) is NoContentResult, "a normal save still accepts an unchanged masked search key");
        Require(ExternalSearchJsonCodec.Decode((await db.AiConfigs.AsNoTracking().SingleAsync()).ExternalSearchJson).ApiKey == currentKey,
            "a normal save preserves the original search secret");

        // 普通保存也不能把已存 Key 带去新网关：必须明确重填。
        var moved = await api.UpdateAiConfig(new AiConfigInputDto
        {
            ExternalSearch = new ExternalSearchInputDto
            {
                Enabled = true, Provider = previous.Provider,
                BaseUrl = "https://new-search.example.test", ApiKey = masked
            }
        });
        Require(moved is BadRequestObjectResult,
            "a normal save cannot move the stored search key to a new origin");
        var afterMove = ExternalSearchJsonCodec.Decode((await db.AiConfigs.AsNoTracking().SingleAsync()).ExternalSearchJson);
        Require(afterMove.BaseUrl == previous.BaseUrl && afterMove.ApiKey == currentKey,
            "a rejected origin change leaves the stored search target untouched");
        Require(await api.UpdateAiConfig(new AiConfigInputDto
        {
            ExternalSearch = new ExternalSearchInputDto
            {
                Enabled = true, Provider = previous.Provider,
                BaseUrl = "https://new-search.example.test", ApiKey = "replacement-key"
            }
        }) is NoContentResult, "a new key may be saved together with a new search origin");
    }
}
