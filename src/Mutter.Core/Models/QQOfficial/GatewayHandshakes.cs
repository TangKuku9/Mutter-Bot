using System.Text.Json.Serialization;

namespace Mutter.Core.Models.QQOfficial;

public class HelloData
{
    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; set; }
}

public class IdentifyData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("intents")]
    public int Intents { get; set; }

    [JsonPropertyName("shard")]
    public int[] Shard { get; set; } = new[] { 0, 1 };
}

public class ResumeData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("seq")]
    public int Seq { get; set; }
}
