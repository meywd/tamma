using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="IPlatformEventRepository"/> bound to
/// <see cref="ControlPlaneDbContext"/>. Mirrors
/// <see cref="EventRepository"/>'s shape but lives on the CP context so
/// inserts never touch a tenant DB and reads never need a tenant query
/// filter.
///
/// <para>The dedup contract: when an insert collides with the partial
/// unique index added by Story 28-6 on
/// <c>(tenant_id, type, tags-&gt;&gt;'step', tags-&gt;&gt;'attempt')
/// where type LIKE 'TENANT.PROVISION.STEP_%'</c>, this repo swallows the
/// <see cref="DbUpdateException"/> and returns <c>null</c> so workflow
/// retries are idempotent. Callers should treat <c>null</c> as
/// "already recorded" and continue.</para>
/// </summary>
public sealed class PlatformEventRepository : IPlatformEventRepository
{
    private readonly ControlPlaneDbContext _db;

    public PlatformEventRepository(ControlPlaneDbContext db)
    {
        _db = db;
    }

    public async Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.Type))
            throw new ArgumentException("PlatformEvent.Type is required", nameof(evt));

        if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;

        _db.PlatformEvents.Add(evt);

        try
        {
            await _db.SaveChangesAsync(ct);
            return evt;
        }
        catch (DbUpdateException)
        {
            // Detach the failed entity so the caller can keep using the
            // context. The next SaveChanges would otherwise resubmit it.
            _db.Entry(evt).State = EntityState.Detached;

            // Step-dedup index hit (or any other unique constraint on this
            // table). Idempotent semantics — return null so the caller
            // knows the event was already recorded by a previous attempt.
            return null;
        }
    }

    public async Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.PlatformEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<PlatformEvent>> QueryAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        string? typePrefix = null,
        DateTime? since = null,
        bool includePlatformWide = false,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = 1;
        if (limit > 1000) limit = 1000;

        var query = _db.PlatformEvents.AsNoTracking().AsQueryable();

        if (tenantId.HasValue)
        {
            var tid = tenantId.Value;
            query = includePlatformWide
                ? query.Where(e => e.TenantId == tid || e.TenantId == null)
                : query.Where(e => e.TenantId == tid);
        }

        if (userId.HasValue)
        {
            var uid = userId.Value;
            query = query.Where(e => e.UserId == uid);
        }

        if (!string.IsNullOrEmpty(typePrefix))
        {
            // EF.Functions.Like → SQL LIKE; index-friendly for prefix matches
            // because the (Type, CreatedAt) index from Story 28-1 covers it.
            var like = typePrefix + "%";
            query = query.Where(e => EF.Functions.Like(e.Type, like));
        }

        if (since.HasValue)
        {
            var s = since.Value;
            query = query.Where(e => e.CreatedAt >= s);
        }

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
