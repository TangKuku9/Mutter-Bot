using System.Text.Json;

namespace QQBot.Core.Models.QQOfficial;

public record QqDispatch(string EventType, string? EventId, int Seq, JsonElement Data);
