using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Repositories;

public static class AiConnectionStore
{
    public const string SecretMask = "********";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>连接表里的 JSON 只由本应用写入；损坏时按空处理，避免一条坏记录拖垮启动与读取。</summary>
    public static List<AiConnectionModel> Models(AiProviderConnectionEntity connection)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AiConnectionModel>>(connection.ModelsJson, JsonOptions) ?? [];
        }
        // 手工改库可能写出重复键：STJ 对这种情况抛 ArgumentException，不只是 JsonException。
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return []; }
    }

    /// <summary>大小写不敏感：请求头名在 HTTP 语义里不区分大小写，掩码还原也必须一致。</summary>
    public static Dictionary<string, string> Headers(AiProviderConnectionEntity connection) =>
        HeadersFromJson(connection.HeadersJson);

    public static Dictionary<string, string> HeadersFromJson(string? json)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return parsed == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>按“名字→值”比较请求头；JSON 键顺序不同不算变化，避免无谓地轮换 Revision。</summary>
    public static bool HeadersEqual(string? left, string? right)
    {
        var a = HeadersFromJson(left);
        var b = HeadersFromJson(right);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>scheme + host + port 相同才认为密钥可以沿用；路径变化不影响凭据归属。</summary>
    public static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(NormalizeUrl(a ?? ""), UriKind.Absolute, out var left)
        && Uri.TryCreate(NormalizeUrl(b ?? ""), UriKind.Absolute, out var right)
        && left.Scheme == right.Scheme
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    public static bool IsValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Query.Length == 0 && !IsBlockedHost(uri.Host);

    /// <summary>
    /// 链路本地与云元数据地址一律拒绝：它们不是模型服务，只可能是 SSRF 的跳板。
    /// 回环与内网地址仍然放行——本地推理服务是明确支持的用法。
    /// 真正的建连校验在 <see cref="OutboundAddressPolicy.ConnectAsync"/>（防 DNS rebinding）。
    /// </summary>
    private static bool IsBlockedHost(string host) => OutboundAddressPolicy.IsBlockedHost(host);

    /// <summary>与旧版面板一致的部分打码：长 Key 保留前 4 后 4，短 Key 全打码。</summary>
    public static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        if (secret.Length <= 12) return "****";
        return $"{secret[..4]}****{secret[^4..]}";
    }

    /// <summary>提交值是否是“未修改”的打码值；同时兼容完整掩码与旧版部分掩码。</summary>
    public static bool IsMaskedSecret(string? current, string? submitted)
    {
        if (string.IsNullOrEmpty(submitted)) return false;
        if (submitted == SecretMask) return true;
        if (string.IsNullOrEmpty(current)) return false;
        if (submitted == MaskSecret(current)) return true;
        return current.Length is > 8 and <= 12 && submitted == $"{current[..4]}****{current[^4..]}";
    }

    /// <summary>按协议写认证头；空 Key 不发任何认证头（本地无认证代理）。</summary>
    public static void ApplyAuthHeaders(HttpRequestMessage request, string protocol, string apiKey)
    {
        if (apiKey.Length == 0) return;
        if (protocol == "gemini")
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            // Google 官方 Key 认 x-goog-api-key；中转通常认 Bearer。非官方 Key 两个都带，无害。
            if (!apiKey.StartsWith("AIza", StringComparison.Ordinal))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return;
        }
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
    }

    public static void Apply(AiConfig config, AiProviderConnectionEntity connection)
    {
        config.ProviderConnectionId = connection.Id;
        config.ProviderTemplateId = connection.TemplateId;
        config.ApiBaseUrl = connection.ApiBaseUrl;
        config.ApiKey = connection.ApiKey;
        config.Protocol = connection.Protocol;
        config.Headers = Headers(connection);
        config.ConnectionTimeoutSeconds = connection.TimeoutSeconds;
    }

    /// <summary>预设快照永远不保存外搜 Key；旧数据里若残留，迁移时清掉。返回是否发生了修改。</summary>
    internal static bool ClearExternalSearchKey(JsonObject config)
    {
        if (config["externalSearch"] is not JsonObject external) return false;
        if (external["apiKey"] is not JsonValue node || !node.TryGetValue<string>(out var key) || key.Length == 0)
            return false;
        external["apiKey"] = "";
        return true;
    }

    /// <summary>
    /// 启动时执行的幂等迁移。未知归属的无密钥预设保留为待绑定；
    /// 指向已消失连接的悬空引用会被清理并尝试按旧字段重建，避免热路径读到坏配置。
    /// </summary>
    public static async Task MigrateLegacyAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var connections = await db.AiProviderConnections.ToListAsync(ct);
        var repaired = 0;
        foreach (var connection in connections.Where(c => c.Revision.Length == 0))
            connection.Revision = Guid.NewGuid().ToString("N");
        var known = connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var created = 0;
        var bound = 0;
        var pending = 0;
        var current = await db.AiConfigs.FirstOrDefaultAsync(ct);
        // 面板只读写第一行配置（历史沿用的约定），其余行属于死数据，不参与迁移。
        static string Protocol(string url, string endpoint) => endpoint == "gemini"
            || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "generativelanguage.googleapis.com"
                ? "gemini" : "openai";
        AiProviderConnectionEntity FindOrCreate(string url, string key, string model, string name, string protocol)
        {
            var normalized = NormalizeUrl(url);
            // 只复用没有自定义请求头的连接：带头连接可能属于另一套网关认证，合并会把它的头套到别的账号上。
            // 代价是迁移时为“同地址同 Key 但已存在带头的连接”多建一条无头连接，仅发生一次。
            var found = connections.FirstOrDefault(c => c.ApiBaseUrl == normalized && c.ApiKey == key
                && c.Protocol == protocol && c.HeadersJson == "{}");
            if (found == null)
            {
                found = new AiProviderConnectionEntity
                {
                    Name = name, ApiBaseUrl = normalized, ApiKey = key, DefaultModel = model, Protocol = protocol,
                    // 与升级前的单次尝试上限保持一致，避免迁移把慢模型的可用性悄悄收紧。
                    TimeoutSeconds = 90
                };
                var template = AiProviderPreset.GetDefaults().FirstOrDefault(p =>
                    p.Id != "custom" && NormalizeUrl(p.ApiBaseUrl) == normalized);
                found.TemplateId = template?.Id ?? "custom";
                db.AiProviderConnections.Add(found);
                connections.Add(found);
                known.Add(found.Id);
                created++;
            }
            var models = Models(found);
            if (model.Length > 0 && models.All(m => m.Id != model))
            {
                models.Add(new AiConnectionModel { Id = model, Name = model });
                found.ModelsJson = JsonSerializer.Serialize(models, JsonOptions);
            }
            return found;
        }

        // 悬空自愈：连接被备份恢复/手工改库/并发删除弄丢时，回退旧字段并允许重建。
        if (current is { ProviderConnectionId.Length: > 0 } && !known.Contains(current.ProviderConnectionId))
        {
            logger?.LogWarning("AI 配置引用的服务商连接已不存在，回退旧字段并重建 connection={Connection}",
                current.ProviderConnectionId);
            current.ProviderConnectionId = string.Empty;
            repaired++;
        }

        if (current is { ProviderConnectionId.Length: 0 } && IsValidUrl(current.ApiBaseUrl)
            && current.ModelName.Length > 0)
        {
            current.ProviderConnectionId = FindOrCreate(current.ApiBaseUrl, current.ApiKey,
                current.ModelName, "现有连接", Protocol(current.ApiBaseUrl, current.WebSearchEndpoint)).Id;
            bound++;
        }

        foreach (var preset in await db.AiPresets.ToListAsync(ct))
        {
            JsonObject? config;
            try { config = JsonNode.Parse(preset.ConfigJson) as JsonObject; }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { continue; }
            static string ReadString(JsonObject value, string key) => value[key] is JsonValue node
                && node.TryGetValue<string>(out var text) ? text : "";
            if (config == null) continue;
            // 旧版本可能把外搜 Key 写进预设快照：先清掉并落盘，后面任何跳过分支也不会把密钥留下。
            if (ClearExternalSearchKey(config)) preset.ConfigJson = config.ToJsonString(JsonOptions);
            var existingBinding = ReadString(config, "providerConnectionId");
            if (existingBinding.Length > 0)
            {
                if (known.Contains(existingBinding))
                {
                    // 已绑定预设的 Key 只在连接里；旧列/旧 JSON 的副本（详情页会显示成打码值）不再保留。
                    if (preset.ApiKey.Length > 0) preset.ApiKey = "";
                    if (ReadString(config, "apiKey").Length > 0)
                    {
                        config["apiKey"] = "";
                        preset.ConfigJson = config.ToJsonString(JsonOptions);
                    }
                    continue;
                }
                // 悬空引用：清掉并走下面的重建逻辑。先落盘，后面任何跳过分支都不会让坏 ID 留在库里
                //（否则每次启动都会重复告警并重新计数）。
                logger?.LogWarning("预设「{Name}」引用的服务商连接已不存在，重新绑定", preset.Name);
                config["providerConnectionId"] = "";
                preset.ConfigJson = config.ToJsonString(JsonOptions);
                repaired++;
            }
            if (config["connectionBindingRequired"] is JsonValue flag && flag.TryGetValue<bool>(out var required) && required) continue;
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
                pending++;
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
            bound++;
        }
        await db.SaveChangesAsync(ct);
        if (created + bound + pending + repaired > 0)
            logger?.LogInformation(
                "AI 服务商连接迁移完成：新建 {Created}，绑定 {Bound}，待绑定 {Pending}，修复悬空 {Repaired}",
                created, bound, pending, repaired);
    }
}
