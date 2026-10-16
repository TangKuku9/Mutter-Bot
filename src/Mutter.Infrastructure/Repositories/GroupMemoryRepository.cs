using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Repositories;

public sealed class GroupMemoryRepository : IGroupMemoryRepository
{
    private const int MaxSummaryChars = 4000;
    private readonly AppDbContext _db;

    public GroupMemoryRepository(AppDbContext db) => _db = db;

    public async Task<GroupMemory?> GetAsync(string groupId, CancellationToken ct = default)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null) return null;
        var e = await _db.GroupMemories.AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == id, ct);
        return e is null ? null : ToModel(e);
    }

    public async Task<GroupMemory> SaveAsync(string groupId, string summary, CancellationToken ct = default)
    {
        var id = NormalizeGroupId(groupId) ?? throw new ArgumentException("群 ID 不能为空", nameof(groupId));
        var text = NormalizeSummary(summary);
        var e = await _db.GroupMemories.SingleOrDefaultAsync(x => x.GroupId == id, ct);
        if (e is null)
        {
            e = new GroupMemoryEntity { GroupId = id, Version = 1 };
            _db.GroupMemories.Add(e);
        }
        else e.Version++;
        e.Summary = text;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToModel(e);
    }

    public async Task<bool> DeleteAsync(string groupId, CancellationToken ct = default)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null) return false;
        var e = await _db.GroupMemories.SingleOrDefaultAsync(x => x.GroupId == id, ct);
        if (e is null) return false;
        _db.GroupMemories.Remove(e);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public static string? NormalizeGroupId(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string NormalizeSummary(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= MaxSummaryChars ? text : text[..MaxSummaryChars];
    }

    private static GroupMemory ToModel(GroupMemoryEntity e) => new()
    {
        GroupId = e.GroupId,
        Summary = e.Summary,
        // SQLite 的 TEXT 列不带时区，EF 读回来 Kind 是 Unspecified，而刚保存的那份是 Utc。
        // 不显式标回去，同一行会在 PUT 响应里带 Z、在 GET 响应里不带，两种时间形状。
        UpdatedAt = DateTime.SpecifyKind(e.UpdatedAt, DateTimeKind.Utc),
        Version = e.Version
    };
}
