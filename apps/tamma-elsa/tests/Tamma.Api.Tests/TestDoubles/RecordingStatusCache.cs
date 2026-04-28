using Tamma.Api.Services.TenantStatus;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ITenantStatusCache"/>. Backed by an
/// in-memory dictionary so tests can pre-populate entries
/// (<c>cache.Entries[id] = "active"</c>) and observe reads /
/// invalidations through the recorded lists.
///
/// <para>Consolidates the per-file <c>RecordingStatusCache</c> /
/// <c>NoopStatusCache</c> stubs across <c>AdminTenantsTests</c>,
/// <c>AdminTenantsAuditAndNoteTests</c>, <c>TenantContextMiddlewareTests</c>,
/// and <c>QuickWinsRound2Tests</c> (PF-C4 cleanup).</para>
///
/// <para>Tests that don't need pre-populated state can ignore
/// <see cref="Entries"/>; <see cref="TryGet"/> will simply return
/// <c>false</c> for missing tenants — matching the previous "no-op"
/// flavour.</para>
/// </summary>
internal sealed class RecordingStatusCache : ITenantStatusCache
{
    /// <summary>Mutable backing store. Tests can seed entries directly.</summary>
    public Dictionary<Guid, string?> Entries { get; } = new();

    /// <summary>Tenant ids passed to <see cref="Invalidate"/>, in call order.</summary>
    public List<Guid> Invalidations { get; } = new();

    /// <summary>Tenant ids passed to <see cref="TryGet"/>, in call order.</summary>
    public List<Guid> Reads { get; } = new();

    public bool TryGet(Guid tenantId, out string? status)
    {
        Reads.Add(tenantId);
        return Entries.TryGetValue(tenantId, out status);
    }

    public void Set(Guid tenantId, string? status) => Entries[tenantId] = status;

    public void Invalidate(Guid tenantId)
    {
        Invalidations.Add(tenantId);
        Entries.Remove(tenantId);
    }
}
