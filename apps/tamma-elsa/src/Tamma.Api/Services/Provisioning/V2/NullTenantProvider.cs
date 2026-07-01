namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Always-registered no-op v2 provider. Two reasons it exists:
/// <list type="bullet">
///   <item><description><b>Single-user mode</b> (CLAUDE.md §"Operating
///     Modes"): the entry-point binaries <c>tamma start</c> /
///     <c>tamma server</c> never need to provision a tenant, but the
///     dispatch workflow + onboarding UI both want a non-null
///     <see cref="ITenantInfrastructureProvider"/> in DI to keep the
///     code path symmetric. The null seam satisfies the type without
///     pretending it can do work.</description></item>
///   <item><description><b>Tests</b>: every Epic 30 test fixture wires
///     <see cref="NullTenantProvider"/> as the baseline and replaces it
///     per-test where a real backend is being exercised.</description></item>
/// </list>
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item><description><see cref="ProviderKey"/> = <c>"null"</c>.</description></item>
///   <item><description><see cref="GetCapabilities"/> returns
///     <see cref="ProviderCapabilities.None"/> (no topologies supported,
///     no regions). Distinct from the v1 null provisioner
///     which fakes "shared infra Ready" — that behaviour stays on v1
///     until 30-3 retires it.</description></item>
///   <item><description><see cref="ProvisionAsync"/> +
///     <see cref="DeprovisionAsync"/> +
///     <see cref="ResolveEndpointsAsync"/> throw
///     <see cref="NotSupportedException"/>. A caller that hits this
///     path has a configuration bug — the dispatch workflow shouldn't
///     route real tenants here.</description></item>
///   <item><description><see cref="GetStatusAsync"/> returns a stable
///     <c>None</c> snapshot so health-check / diagnostic endpoints that
///     enumerate every provider don't have to special-case the null
///     seam.</description></item>
/// </list>
/// </summary>
public sealed class NullTenantProvider : ITenantInfrastructureProvider
{
    /// <summary>Reserved key for the no-op seam.</summary>
    public const string Key = "null";

    private static readonly ProviderCapabilities CapabilitiesValue =
        ProviderCapabilities.None(Key, "No-op (single-user / dev seam)");

    public string ProviderKey => Key;

    public ProviderCapabilities GetCapabilities() => CapabilitiesValue;

    public Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId,
        ProvisioningRequest request,
        CancellationToken ct)
    {
        throw new NotSupportedException(
            "NullTenantProvider does not provision infrastructure. " +
            "Wire a real ITenantInfrastructureProvider (cranl, hetzner, " +
            "cloudflare, byo) in single-user mode this path is unused; " +
            "in SaaS mode this indicates a misrouted dispatch.");
    }

    public Task<ProvisioningStatusSnapshot> GetStatusAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        return Task.FromResult(new ProvisioningStatusSnapshot(
            ProvisioningState.None,
            Detail: "null_provider_no_state",
            FailureReason: null,
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    public Task DeprovisionAsync(
        Guid tenantId,
        DeprovisioningRequest request,
        CancellationToken ct)
    {
        throw new NotSupportedException(
            "NullTenantProvider does not deprovision infrastructure.");
    }

    public Task<TenantEndpoints> ResolveEndpointsAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        throw new NotSupportedException(
            "NullTenantProvider has no endpoints to resolve.");
    }
}
