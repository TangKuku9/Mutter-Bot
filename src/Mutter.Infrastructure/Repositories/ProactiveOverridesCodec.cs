using System.Text.Json;
using System.Text.Json.Serialization;
using Mutter.Core.Models.Config;

namespace Mutter.Infrastructure.Repositories;

public static class ProactiveOverridesCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 逐字段解析：一个坏字段（如 1.5、溢出整数）只丢弃该字段，不再让整份覆盖回退为空；
    /// 未知识别键进 Extra，等未来版本解释。
    /// </summary>
    public static ProactiveOverrides Decode(string? json)
    {
        var result = new ProactiveOverrides();
        if (string.IsNullOrWhiteSpace(json)) return result.Normalize();
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return result.Normalize();

        var extra = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            try
            {
                if (!TrySet(result, prop.Name, prop.Value)) extra[prop.Name] = prop.Value.Clone();
            }
            // FormatException：类型不符（字符串/小数）；OverflowException：整数溢出；GetBoolean 的类型不符是 InvalidOperationException。
            catch (FormatException) { }
            catch (OverflowException) { }
            catch (InvalidOperationException) { }
        }
        if (extra.Count > 0) result.Extra = extra;
        return result.Normalize();
    }

    private static JsonDocument? TryParse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>已知键返回 true 并赋值（空/坏值由调用方丢弃）；未知识别键返回 false 进 Extra。</summary>
    private static bool TrySet(ProactiveOverrides v, string name, JsonElement value)
    {
        switch (name.ToLowerInvariant())
        {
            case "windowseconds": v.WindowSeconds = value.GetInt32(); return true;
            case "windowmaxmessages": v.WindowMaxMessages = value.GetInt32(); return true;
            case "cooldownminutes": v.CooldownMinutes = value.GetInt32(); return true;
            case "maxpergroupperday": v.MaxPerGroupPerDay = value.GetInt32(); return true;
            case "quietstarthour": v.QuietStartHour = value.GetInt32(); return true;
            case "quietendhour": v.QuietEndHour = value.GetInt32(); return true;
            case "liveliness": v.Liveliness = value.GetInt32(); return true;
            case "maxattachedimages": v.MaxAttachedImages = value.GetInt32(); return true;
            case "historymaxmessages": v.HistoryMaxMessages = value.GetInt32(); return true;
            case "evalmaxtokens": v.EvalMaxTokens = value.GetInt32(); return true;
            case "websearchenabled": v.WebSearchEnabled = value.GetBoolean(); return true;
            case "memoryinjectionenabled": v.MemoryInjectionEnabled = value.GetBoolean(); return true;
            case "allowactivechannel": v.AllowActiveChannel = value.GetBoolean(); return true;
            case "activepergroupperminute": v.ActivePerGroupPerMinute = value.GetInt32(); return true;
            case "activepergroupperday": v.ActivePerGroupPerDay = value.GetInt32(); return true;
            default: return false;
        }
    }

    public static string Encode(ProactiveOverrides? values) => JsonSerializer.Serialize(values?.Normalize() ?? new(), Options);
}
