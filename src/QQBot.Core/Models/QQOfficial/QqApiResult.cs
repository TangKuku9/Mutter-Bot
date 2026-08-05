using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

public class QqApiResult
{
    [JsonPropertyName("err_code")]
    public int? ErrCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    public bool IsSuccess => ErrCode == null || ErrCode == 0;
}
