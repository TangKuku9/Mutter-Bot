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

    public static ProactiveOverrides Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return (JsonSerializer.Deserialize<ProactiveOverrides>(json, Options) ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }

    public static string Encode(ProactiveOverrides? values) => JsonSerializer.Serialize(values?.Normalize() ?? new(), Options);
}
