using System.Text.Json;
using Mutter.Core.Models.Config;

namespace Mutter.Infrastructure.Repositories;

/// <summary>
/// ExternalSearchConfig 的 JSON 单列编解码。库里的空串 = 从未配置（走代码默认值），
/// 反序列化失败同样回退默认值并容错（不抛异常打断配置读取），
/// 序列化失败回退空串——这张卡坏了不能连累整个 AiConfig 读写。
/// </summary>
public static class ExternalSearchJsonCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // 库里可能残留旧版本字段，多退少补，不做严格闭合
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    public static string Encode(ExternalSearchConfig? config)
    {
        if (config is null) return string.Empty;
        try
        {
            return JsonSerializer.Serialize(config.Normalized(), Options);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static ExternalSearchConfig Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ExternalSearchConfig { Enabled = false };

        try
        {
            var decoded = JsonSerializer.Deserialize<ExternalSearchConfig>(json, Options);
            return decoded ?? new ExternalSearchConfig { Enabled = false };
        }
        catch
        {
            return new ExternalSearchConfig { Enabled = false };
        }
    }
}
