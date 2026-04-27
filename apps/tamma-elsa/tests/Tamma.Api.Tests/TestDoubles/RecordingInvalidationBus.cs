using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ITenantStatusInvalidationBus"/>.
/// Records every <see cref="PublishAsync"/> call so admin-endpoint tests
/// can assert that status flips fire the cluster NOTIFY alongside the
/// local cache flush.
///
/// <para>Consolidates duplicate copies in <c>AdminTenantsTests</c> and
/// <c>AdminTenantsAuditAndNoteTests</c> (PF-C4 cleanup).</para>
/// </summary>
internal sealed class RecordingInvalidationBus : ITenantStatusInvalidationBus
{
    public List<Guid> Publishes { get; } = new();

    public ValueTask PublishAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        Publishes.Add(tenantId);
        return ValueTask.CompletedTask;
    }
}
