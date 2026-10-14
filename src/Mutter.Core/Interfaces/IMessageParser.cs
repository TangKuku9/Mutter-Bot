using Mutter.Core.Models;
using Mutter.Core.Models.QQOfficial;

namespace Mutter.Core.Interfaces;

public interface IMessageParser
{
    ParsedMessage Parse(string content, List<Attachment>? attachments);
}
