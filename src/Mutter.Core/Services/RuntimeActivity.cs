using Mutter.Core.Models;

namespace Mutter.Core.Services;

public sealed class RuntimeActivity : IDisposable
{
    private static readonly AsyncLocal<ConversationTaskTrace?> Ambient = new();
    private readonly ConversationTaskTrace? _previous;
    public static ConversationTaskTrace? Current => Ambient.Value;
    public RuntimeActivity(ConversationTaskTrace trace)
    {
        _previous = Ambient.Value;
        Ambient.Value = trace;
    }
    public void Dispose() => Ambient.Value = _previous;
}
