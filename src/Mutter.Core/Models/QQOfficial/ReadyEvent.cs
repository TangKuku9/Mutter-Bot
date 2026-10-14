using System.Text.Json.Serialization;

namespace Mutter.Core.Models.QQOfficial;

public class ReadyData
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public ReadyUser User { get; set; } = new();
}

public class ReadyUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("bot")]
    public bool Bot { get; set; }
}
