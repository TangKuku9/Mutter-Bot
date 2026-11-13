using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Repositories;

public static class AiConnectionStore
{
    public const string SecretMask = "********";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static List<AiConnectionModel> Models(AiProviderConnectionEntity connection) =>
        JsonSerializer.Deserialize<List<AiConnectionModel>>(connection.ModelsJson, JsonOptions) ?? [];

    public static Dictionary<string, string> Headers(AiProviderConnectionEntity connection) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(connection.HeadersJson, JsonOptions) ?? [];

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    public static bool IsValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Query.Length == 0;

    public static void Apply(AiConfig config, AiProviderConnectionEntity connection)
    {
        config.ProviderConnectionId = connection.Id;
        config.ApiBaseUrl = connection.ApiBaseUrl;
        config.ApiKey = connection.ApiKey;
        config.Protocol = connection.Protocol;
        config.Headers = Headers(connection);
        config.ConnectionTimeoutSeconds = connection.TimeoutSeconds;
    }

    /// <summary>启动时执行的幂等迁移。未知归属的无密钥预设保留为待绑定。</summary>
    public static async Task MigrateLegacyAsync(AppDbContext db, CancellationToken ct = default)
    {
        var connections = await db.AiProviderConnections.ToListAsync(ct);
        foreach (var connection in connections.Where(c => c.Revision.Length == 0))
            connection.Revision = Guid.NewGuid().ToString("N");
        var current = await db.AiConfigs.FirstOrDefaultAsync(ct);
        static string Protocol(string url, string endpoint) => endpoint == "gemini"
            || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "generativelanguage.googleapis.com"
                ? "gemini" : "openai";
        AiProviderConnectionEntity FindOrCreate(string url, string key, string model, string name, string protocol)
        {
            var normalized = NormalizeUrl(url);
            var found = connections.FirstOrDefault(c => c.ApiBaseUrl == normalized && c.ApiKey == key
                && c.Protocol == protocol && c.HeadersJson == "{}");
            if (found == null)
            {
                found = new AiProviderConnectionEntity
                {
                    Name = name, ApiBaseUrl = normalized, ApiKey = key, DefaultModel = model, Protocol = protocol
                };
                var template = AiProviderPreset.GetDefaults().FirstOrDefault(p =>
                    p.Id != "custom" && NormalizeUrl(p.ApiBaseUrl) == normalized);
                found.TemplateId = template?.Id ?? "custom";
                db.AiProviderConnections.Add(found);
                connections.Add(found);
            }
            var models = Models(found);
            if (model.Length > 0 && models.All(m => m.Id != model))
            {
                models.Add(new AiConnectionModel { Id = model, Name = model });
                found.ModelsJson = JsonSerializer.Serialize(models, JsonOptions);
            }
            return found;
        }

        if (current is { ProviderConnectionId.Length: 0 } && IsValidUrl(current.ApiBaseUrl)
            && current.ModelName.Length > 0)
        {
            current.ProviderConnectionId = FindOrCreate(current.ApiBaseUrl, current.ApiKey,
                current.ModelName, "现有连接", Protocol(current.ApiBaseUrl, current.WebSearchEndpoint)).Id;
        }

        foreach (var preset in await db.AiPresets.ToListAsync(ct))
        {
            JsonObject? config;
            try { config = JsonNode.Parse(preset.ConfigJson) as JsonObject; }
            catch (JsonException) { continue; }
            static string ReadString(JsonObject value, string key) => value[key] is JsonValue node
                && node.TryGetValue<string>(out var text) ? text : "";
            if (config == null || ReadString(config, "providerConnectionId").Length > 0
                || config["connectionBindingRequired"] is JsonValue flag && flag.TryGetValue<bool>(out var required) && required) continue;
            var url = ReadString(config, "apiBaseUrl");
            var model = ReadString(config, "modelName");
            if (!IsValidUrl(url) || model.Length == 0) continue;
            var protocol = Protocol(url, ReadString(config, "webSearchEndpoint"));
            var sameCurrent = current != null && NormalizeUrl(current.ApiBaseUrl) == NormalizeUrl(url)
                && protocol == Protocol(current.ApiBaseUrl, current.WebSearchEndpoint);
            if (preset.ApiKey.Length == 0 && !sameCurrent)
            {
                config["connectionBindingRequired"] = true;
                preset.ConfigJson = config.ToJsonString(JsonOptions);
                continue;
            }
            var connection = FindOrCreate(url, preset.ApiKey.Length > 0 ? preset.ApiKey : current!.ApiKey,
                model, $"预设 · {preset.Name}", protocol);
            config["providerConnectionId"] = connection.Id;
            config["providerConnectionVersion"] = connection.Revision;
            config["version"] = 2;
            config["apiKey"] = "";
            preset.ConfigJson = config.ToJsonString(JsonOptions);
            preset.ApiKey = "";
        }
        await db.SaveChangesAsync(ct);
    }
}
