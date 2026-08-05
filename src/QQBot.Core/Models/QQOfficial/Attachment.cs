using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

public class Attachment
{
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("size")]
    public int? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("voice_wav_url")]
    public string? VoiceWavUrl { get; set; }

    [JsonPropertyName("asr_refer_text")]
    public string? AsrReferText { get; set; }

    public bool IsImage => ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
}
