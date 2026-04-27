using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="IPlatformEventRepository"/> that
/// drops every appended event on the floor (still stamps Id/CreatedAt
/// on the returned record so downstream code that reads the field
/// works). <see cref="GetByIdAsync"/> always returns <c>null</c> and
/// <see cref="QueryAsync"/> always returns an empty list.
///
/// <para>Use this when the test exercises the rotation coordinator's
/// happy path but doesn't need to assert the emitted event payloads.
/// Consolidates the per-file <c>NoopEventRepository</c> stubs across
/// <c>KekRotationAdvisoryLockTests</c> and <c>KekRotationPostFixTests</c>
/// (PF-C4 cleanup).</para>
/// </summary>
internal sealed class NoopPlatformEventRepository : IPlatformEventRepository
{
    public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
        if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;
        return Task.FromResult<PlatformEvent?>(evt);
    }

    public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<PlatformEvent?>(null);

    public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        string? typePrefix = null,
        DateTime? since = null,
        bool includePlatformWide = false,
        int limit = 100,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformEvent>>(Array.Empty<PlatformEvent>());
}
