using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="IPlatformEventRepository"/>.
/// Records every <see cref="AppendAsync"/> call into
/// <see cref="AppendedEvents"/> after stamping <c>Id</c> and
/// <c>CreatedAt</c> (matching the real Postgres repo behaviour).
/// <see cref="GetByIdAsync"/> and <see cref="QueryAsync"/> serve from
/// the recorded list with full filter semantics.
///
/// <para>Consolidates the per-file <c>RecordingEventRepository</c>
/// stubs across <c>KekRotationCoordinatorTests</c>,
/// <c>KekRotationPostFixTests</c>, and <c>KekRotationRetryTests</c>
/// (PF-C4 cleanup).</para>
/// </summary>
internal sealed class RecordingPlatformEventRepository : IPlatformEventRepository
{
    public List<PlatformEvent> AppendedEvents { get; } = new();

    public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
        if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;
        AppendedEvents.Add(evt);
        return Task.FromResult<PlatformEvent?>(evt);
    }

    public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(AppendedEvents.FirstOrDefault(e => e.Id == id));

    public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        string? typePrefix = null,
        DateTime? since = null,
        bool includePlatformWide = false,
        int limit = 100,
        CancellationToken ct = default)
    {
        IEnumerable<PlatformEvent> query = AppendedEvents;
        if (tenantId is not null)
        {
            query = includePlatformWide
                ? query.Where(e => e.TenantId == tenantId || e.TenantId == null)
                : query.Where(e => e.TenantId == tenantId);
        }
        if (userId is not null) query = query.Where(e => e.UserId == userId);
        if (typePrefix is not null) query = query.Where(e => e.Type.StartsWith(typePrefix));
        if (since is not null) query = query.Where(e => e.CreatedAt >= since);
        return Task.FromResult<IReadOnlyList<PlatformEvent>>(
            query.OrderByDescending(e => e.CreatedAt).Take(limit).ToList());
    }
}
