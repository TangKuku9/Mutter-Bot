using Mutter.Core.Models;

namespace Mutter.Core.Interfaces;

public interface IGroupMemoryRepository
{
    Task<GroupMemory?> GetAsync(string groupId, CancellationToken ct = default);
    Task<GroupMemory> SaveAsync(string groupId, string summary, CancellationToken ct = default);
    Task<bool> DeleteAsync(string groupId, CancellationToken ct = default);
}
