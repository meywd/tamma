namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Caller-supplied options for
/// <see cref="ITenantInfrastructureProvider.DeprovisionAsync"/>. Kept as a
/// dedicated record (rather than just <c>CancellationToken</c>) so the
/// deprovisioning saga (Story 30-9) can pass cleanup-mode + reason
/// without breaking the interface for new providers.
/// </summary>
/// <param name="CleanupMode">How aggressively to surface failures during
/// teardown — see <see cref="DeprovisioningCleanupMode"/>. Defaults to
/// <see cref="DeprovisioningCleanupMode.BestEffort"/> so a partly-orphaned
/// tenant doesn't block the saga.</param>
/// <param name="Reason">Audit string attached to the resulting events
/// (e.g. <c>"tenant_deleted"</c>, <c>"plan_downgrade"</c>).
/// <c>null</c> when the caller didn't supply one.</param>
public sealed record DeprovisioningRequest(
    DeprovisioningCleanupMode CleanupMode = DeprovisioningCleanupMode.BestEffort,
    string? Reason = null);

/// <summary>How a deprovisioning call handles partial failures.</summary>
public enum DeprovisioningCleanupMode
{
    /// <summary>Tear down what we can; swallow per-resource failures and
    /// log them. The saga still reports overall success — the
    /// reconciliation workflow (Story 30-9) sweeps orphans later.</summary>
    BestEffort,

    /// <summary>Any resource that fails to tear down throws. Used by
    /// admin "delete-now" flows where the operator needs hard
    /// confirmation that nothing is left.</summary>
    Strict
}
