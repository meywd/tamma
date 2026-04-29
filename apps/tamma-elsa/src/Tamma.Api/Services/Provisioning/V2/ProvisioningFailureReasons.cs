namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 — central catalogue of structured failure short-codes that
/// the v2 dispatch workflow surfaces on
/// <see cref="ProvisioningStatusSnapshot.FailureReason"/>. Locked-in by the
/// 30-1 ADR §2: every <c>FailureReason</c> the dispatch workflow emits
/// MUST be a short, kebab-cased identifier the operator UI + cost
/// dashboards can pattern-match without parsing free text.
///
/// <para>Two buckets:</para>
/// <list type="bullet">
///   <item><description><b>Provider-side</b> short codes — minted by an
///     <see cref="ITenantInfrastructureProvider"/> implementation when its
///     own state machine fails (e.g. <c>"unsupported_topology"</c> from
///     the 30-1 contract; future <c>"cranl_db_create_failed"</c> from
///     30-3). The dispatch workflow surfaces them verbatim.</description></item>
///   <item><description><b>Workflow-side</b> short codes — minted by THIS
///     module when the failure is in the dispatch glue rather than in a
///     provider call (registry miss, quota exceeded, probe timeout,
///     compensation failure). The constants below are exactly that
///     bucket.</description></item>
/// </list>
///
/// <para>Operators read <c>FailureReason</c> off the
/// <c>tenants.provisioning_detail</c> column or the SSE feed; the codes are
/// stable surface so dashboards can localise them or hyperlink to runbooks
/// without scraping prose.</para>
/// </summary>
public static class ProvisioningFailureReasons
{
    // ── Workflow-side short codes ──────────────────────────────────

    /// <summary>Registry has no provider keyed by the requested
    /// <c>provider_key</c>. Configuration-bug class — the operator
    /// requested a backend that wasn't wired into DI. The dispatch
    /// workflow refuses to advance past <c>ResolveProvider</c> in this
    /// case rather than silently defaulting to the null seam.</summary>
    public const string ProviderNotRegistered = "provider_not_registered";

    /// <summary>Single-user / dev mode — the registry only has the null
    /// seam wired and the request asked for a real backend, OR the
    /// request explicitly named the null seam. Per the ADR §6, we surface
    /// this as a structured failure rather than letting
    /// <see cref="NullTenantProvider.ProvisionAsync"/> throw, because the
    /// workflow needs a clean state-machine transition (Failed +
    /// FailureReason) instead of an unhandled exception travelling up the
    /// queue worker.</summary>
    public const string NoProvisioningInThisMode = "no_provisioning_in_this_mode";

    /// <summary>Provider declared its capabilities but the requested
    /// topology isn't in the supported flag set. Dispatch refuses fast
    /// at the preflight step. Mirrors the provider-side
    /// <c>"unsupported_topology"</c> code from the 30-1 contract — the
    /// workflow surfaces its own version of the same code when it
    /// catches the mismatch BEFORE handing off to the provider.</summary>
    public const string UnsupportedTopology = "unsupported_topology";

    /// <summary>Provider declared a region list and the requested region
    /// isn't in it. Free-text region strings from the 30-1 contract; the
    /// workflow refuses to advance.</summary>
    public const string UnsupportedRegion = "unsupported_region";

    /// <summary>Per-org quota check failed. Today's check uses
    /// <see cref="ProviderCapabilities.MaxTenantsPerOrg"/>; future
    /// per-tier breakdowns (Story 30-10) extend the check without
    /// replacing this code.</summary>
    public const string OrgQuotaExceeded = "org_quota_exceeded";

    /// <summary>The tenant row referenced by the workflow input doesn't
    /// exist in the control-plane database. Indicates a payload built
    /// from a stale snapshot — the dispatcher refuses to invent a row.</summary>
    public const string TenantNotFound = "tenant_not_found";

    /// <summary>The probe step ran the full probe budget without seeing
    /// the provider transition into <see cref="ProvisioningState.Ready"/>.
    /// Compensation runs after this — the provider's
    /// <c>DeprovisionAsync</c> tears down whatever was minted.</summary>
    public const string ProbeTimeout = "probe_timeout";

    /// <summary>Compensation itself failed — orphan resources may be left
    /// in the provider. The workflow halts (NO retry of compensation per
    /// the brief AC4); the operator must intervene manually.</summary>
    public const string CompensationFailed = "compensation_failed";

    /// <summary>The provider's <c>ProvisionAsync</c> threw an exception
    /// rather than returning a structured Failed snapshot. Indicates a
    /// provider-side contract violation (the ADR §1 says providers MUST
    /// return Failed, not throw). The workflow wraps the exception
    /// message in <see cref="ProvisioningStatusSnapshot.Detail"/> and
    /// flags this code so operators can file a provider bug.</summary>
    public const string ProviderUnexpectedException = "provider_unexpected_exception";

    /// <summary>The workflow was asked to run against a tenant whose row
    /// state is incompatible with starting / resuming provisioning
    /// (e.g. already <c>ready</c>, or in <c>deprovisioning</c>). Dispatch
    /// refuses without making a provider call.</summary>
    public const string IllegalTenantState = "illegal_tenant_state";
}
