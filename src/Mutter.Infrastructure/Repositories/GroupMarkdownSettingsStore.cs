using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Repositories;

public sealed class GroupMarkdownSettingsStore(IServiceScopeFactory scopes, RichUiCapability richUi) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _writes = new(1, 1);

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.GroupMarkdownSettings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == 1, ct);
        if (row is not null)
        {
            var groupIds = JsonSerializer.Deserialize<string[]>(row.GroupIdsJson) ?? [];
            richUi.SetGroupMarkdown(new GroupMarkdownPolicy(row.Mode, groupIds.ToFrozenSet(StringComparer.Ordinal)));
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SaveAsync(GroupMarkdownPolicy policy, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = JsonSerializer.Serialize(policy.GroupIds.Order(StringComparer.Ordinal));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "GroupMarkdownSettings" ("Id", "Mode", "GroupIdsJson") VALUES (1, {(int)policy.Mode}, {ids})
                ON CONFLICT ("Id") DO UPDATE SET "Mode" = excluded."Mode", "GroupIdsJson" = excluded."GroupIdsJson";
                """, ct);
            richUi.SetGroupMarkdown(policy);
        }
        finally { _writes.Release(); }
    }

    public void Dispose() => _writes.Dispose();
}
