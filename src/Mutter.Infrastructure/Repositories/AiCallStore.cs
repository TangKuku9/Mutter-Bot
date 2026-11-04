using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Repositories;

public sealed class AiCallStore(IServiceScopeFactory scopes) : IAiCallStore
{
    public async Task SaveAsync(AiCallRecord call, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiCallRecords.Add(call);
        await db.SaveChangesAsync(ct);
    }
}
