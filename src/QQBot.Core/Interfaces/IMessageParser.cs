using QQBot.Core.Models;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Interfaces;

public interface IMessageParser
{
    ParsedMessage Parse(string content, List<Attachment>? attachments);
}
