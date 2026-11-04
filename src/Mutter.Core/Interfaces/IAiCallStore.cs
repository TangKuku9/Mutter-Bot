using Mutter.Core.Models;

namespace Mutter.Core.Interfaces;

public interface IAiCallStore
{
    Task SaveAsync(AiCallRecord call, CancellationToken ct);
}
