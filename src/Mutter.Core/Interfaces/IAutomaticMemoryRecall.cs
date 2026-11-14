using Mutter.Core.Models;

namespace Mutter.Core.Interfaces;

public interface IAutomaticMemoryRecall
{
    Task<AutomaticMemoryRecall> RecallAsync(string groupId, string query, DateTime? before = null,
        long? beforeRowId = null, CancellationToken ct = default);
}
