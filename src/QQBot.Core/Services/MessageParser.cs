using System.Text.Json;
using System.Text.RegularExpressions;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Services;

public class MessageParser : IMessageParser
{
    /// <summary>
    /// QQ 表情段 &lt;faceType=..,faceId="..",ext="base64"&gt;。ext 解出来是 {"text":"赞"}，
    /// 群表情包（faceType=6）的 text 为空但附件里带图片 URL。
    /// </summary>
    private static readonly Regex FaceSegment =
        new(@"<faceType=\d+,faceId=""[^""]*"",ext=""([^""]*)"">", RegexOptions.Compiled);

    public ParsedMessage Parse(string content, List<Attachment>? attachments)
    {
        var result = new ParsedMessage
        {
            HasAtBot = false
        };

        if (attachments != null)
        {
            foreach (var a in attachments)
            {
                if (a?.IsImage == true && !string.IsNullOrEmpty(a.Url))
                {
                    result.ImageUrls.Add(a.Url!);
                }
            }
        }

        result.HasImage = result.ImageUrls.Count > 0;
        result.RawSegmentsJson = JsonSerializer.Serialize(result.ImageUrls);
        result.PlainText = NormalizeFaceTags(content, result.HasImage);

        return result;
    }

    /// <summary>
    /// 表情与纯图片消息换成可读占位，否则原始 faceType 段会一路进库、进面板、进模型上下文。
    /// 占位也保证纯图片消息不会被下游当成空内容丢掉。存量行的回填也用它，保证两边口径一致。
    /// </summary>
    public static string NormalizeFaceTags(string? content, bool hasImage)
    {
        var text = FaceSegment.Replace(
            content?.Trim() ?? string.Empty,
            m =>
            {
                var name = FaceName(m.Groups[1].Value);
                return name.Length > 0 ? $"[表情:{name}]" : (hasImage ? "[表情图]" : "[表情]");
            });

        text = text.Trim();
        return text.Length == 0 && hasImage ? "[图片]" : text;
    }

    /// <summary>解 ext 的 base64 JSON 取表情名。取不到就当作无名表情。</summary>
    private static string FaceName(string extBase64)
    {
        if (string.IsNullOrEmpty(extBase64)) return string.Empty;

        try
        {
            var padded = extBase64.Length % 4 == 0 ? extBase64 : extBase64.PadRight(extBase64.Length + (4 - extBase64.Length % 4), '=');
            var json = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("text", out var textEl)) return string.Empty;

            return (textEl.GetString() ?? string.Empty).Trim().TrimStart('[').TrimEnd(']').Trim();
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
