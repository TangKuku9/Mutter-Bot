using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mutter.Core.Models.QQOfficial;

public class GatewayPayload
{
    [JsonPropertyName("op")]
    public int Op { get; set; }

    [JsonPropertyName("d")]
    public JsonElement? D { get; set; }

    [JsonPropertyName("s")]
    public int? S { get; set; }

    [JsonPropertyName("t")]
    public string? T { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public static class GatewayOp
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;

    public const int GroupAndC2CIntent = 1 << 25;

    /// <summary>
    /// INTERACTION 事件（INTERACTION_CREATE：按钮点击/快捷菜单/赞踩/授权）。
    /// ⚠ 需平台授权，未授权却订阅会导致 Identify 报错断连。
    /// </summary>
    public const int InteractionIntent = 1 << 26;
}
