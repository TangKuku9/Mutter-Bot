using System.Reflection;
using Mutter.Core.Models.Config;
using Mutter.Web.Controllers;
using Mutter.Web.Models;

internal static partial class RuntimeChecks
{
    /// <summary>
    /// 外搜 Key 归属：同一服务商沿用，跨服务商绝不沿用旧 Key/旧网关；
    /// 旧版 9–12 位 Key 的 4+****+4 打码也必须识别为“未修改”。
    /// </summary>
    private static Task AiKeyOwnershipAsync()
    {
        const string currentKey = "test-key-1234567890"; // 15 位：新版与旧版打码相同
        const string masked = "test****7890";

        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "tavily", "") == currentKey,
            "same provider keeps the saved key when the form is left empty");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "tavily", masked) == currentKey,
            "same provider treats the masked value as unchanged");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "brave", "") == "",
            "a provider switch never reuses the old key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "brave", masked) == "",
            "a provider switch never treats the old mask as a new key");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "brave", "brave-new-key") == "brave-new-key",
            "a genuinely new key is accepted after a provider switch");
        Require(ConfigController.ResolveProviderAwareApiKey("tavily", currentKey, "searxng", "anything") == "",
            "SearXNG never stores a key");

        // 9–12 位 Key 在旧版本显示为前 4 + **** + 后 4；升级后旧页面仍可能提交这种值。
        const string shortKey = "abcdefghi";
        Require(ConfigController.ResolveProviderAwareApiKey("custom", shortKey, "custom", "abcd****fghi") == shortKey,
            "the legacy 4+****+4 mask is still recognized as unchanged after the masking threshold change");
        Require(ConfigController.ResolveProviderAwareApiKey("custom", shortKey, "brave", "abcd****fghi") == "",
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

        // 应用预设：预设不含外搜 Key，目标服务商与当前不同时允许先落库，面板再提示补填；
        // 普通保存仍必须拒绝空 Key。
        var validate = typeof(ConfigController).GetMethod("NormalizeAndValidateAiConfigInput",
            BindingFlags.NonPublic | BindingFlags.Static);
        Require(validate is not null, "the shared AI config validator remains reachable for contract checks");
        var presetConfig = new AiConfigInputDto
        {
            ExternalSearch = new ExternalSearchInputDto { Enabled = true, Provider = "brave", ApiKey = "" }
        };
        var strict = (string?)validate!.Invoke(null, [presetConfig, null, false]);
        var relaxed = (string?)validate.Invoke(null, [presetConfig, null, true]);
        Require(strict is not null && strict.Contains("API Key"),
            "a normal save still rejects an enabled provider without a key");
        Require(relaxed is null,
            "applying a preset may land an enabled provider without a key so the panel can ask for one");
        return Task.CompletedTask;
    }
}
