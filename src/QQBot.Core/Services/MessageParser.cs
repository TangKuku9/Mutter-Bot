using System.Text.Json;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Services;

public class MessageParser : IMessageParser
{
    public ParsedMessage Parse(string content, List<Attachment>? attachments)
    {
        var result = new ParsedMessage
        {
            PlainText = content?.Trim() ?? string.Empty,
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

        return result;
    }
}
