using System.Text.Json.Serialization;

namespace Mutter.Core.Models.QQOfficial;

public class GroupRobotEvent
{
    [JsonPropertyName("group_openid")]
    public string GroupOpenid { get; set; } = string.Empty;

    [JsonPropertyName("op_member_openid")]
    public string? OpMemberOpenid { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public class FriendEvent
{
    [JsonPropertyName("openid")]
    public string Openid { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public class MsgOptEvent
{
    [JsonPropertyName("user_openid")]
    public string? UserOpenid { get; set; }

    [JsonPropertyName("group_openid")]
    public string? GroupOpenid { get; set; }
}
