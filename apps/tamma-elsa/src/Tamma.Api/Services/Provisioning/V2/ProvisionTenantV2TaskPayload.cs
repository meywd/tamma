namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 — payload that travels on the platform queue
/// (<see cref="Tamma.Data.Repositories.IPlatformQueuedTaskRepository"/>) for
/// the v2 provisioning workflow.
///
/// <para><b>Why platform queue, not per-tenant queue</b>: the existing v1
/// pattern (Story 28-1 PR B, doc-commented on <c>CranlTenantProvisioner</c>)
/// puts provisioning + deprovisioning tasks on the platform queue because
/// at provisioning time the tenant's database doesn't exist yet — the
/// task's whole job is to create it. The 30-1 audit explicitly preserved
/// this constraint for v2; this payload + the matching
/// <see cref="ProvisionTenantV2TaskHandler"/> uphold it.</para>
///
/// <para>The payload is a snapshot of the
/// <see cref="ProvisioningRequest"/> the operator submitted plus the
/// tenant id + the operator's chosen <see cref="ProviderKey"/>. The
/// request travels in JSON-friendly form (the V2 record is also
/// JSON-serialisable but flattening the two fields here keeps
/// inter-version compatibility loose — adding new request fields in the
/// V2 record doesn't auto-add them to the queue payload).</para>
/// </summary>
public sealed class ProvisionTenantV2TaskPayload
{
    /// <summary>Stable task-type identifier the
    /// <see cref="ProvisionTenantV2TaskHandler"/> matches on.</summary>
    public const string TaskType = "provisioning.tenant.v2";

    /// <summary>Tenant the workflow targets.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Which lifecycle action this task performs. Defaults to
    /// <see cref="ProvisioningOperation.Provision"/> so payloads serialized
    /// before this field existed still deserialize as provision tasks.</summary>
    public ProvisioningOperation Operation { get; set; } = ProvisioningOperation.Provision;

    /// <summary>Stable provider key the dispatch step looks up in
    /// <see cref="TenantProviderRegistry"/>. Convention is lowercase
    /// snake_case (e.g. <c>"cranl"</c>, <c>"hetzner"</c>).</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The infrastructure shape requested. Stored as the
    /// <see cref="ProvisioningTopology"/> bit-flag value — JSON
    /// round-trips it as the integer literal of the flag.</summary>
    public ProvisioningTopology Topology { get; set; }

    /// <summary>Provider-specific region identifier; <c>null</c> asks for
    /// the provider default. Free-form per the 30-1 ADR §3.</summary>
    public string? Region { get; set; }

    /// <summary>Resource sizing hint passed straight through to the
    /// provider.</summary>
    public string? Tier { get; set; }

    /// <summary>Operator-supplied prefix for provider resource names.</summary>
    public string? CustomName { get; set; }

    /// <summary>For <see cref="ProvisioningTopology.Managed"/> / BYO only.
    /// MUST be <c>null</c> for other topologies.</summary>
    public string? ExistingDatabaseUrl { get; set; }

    /// <summary>For <see cref="ProvisioningTopology.Managed"/> / BYO only.</summary>
    public string? ExistingEngineUrl { get; set; }

    /// <summary>Optional invoking-org id for the per-provider quota check
    /// (AC6). When <c>null</c>, quota enforcement is skipped — useful for
    /// platform-owner provisioning flows that bypass per-org limits.</summary>
    public Guid? InvokingOrgId { get; set; }

    /// <summary>Round-trip helper — rebuild the
    /// <see cref="ProvisioningRequest"/> the workflow hands the provider.</summary>
    public ProvisioningRequest ToProvisioningRequest() =>
        new(
            Topology,
            Region,
            Tier,
            CustomName,
            ExistingDatabaseUrl,
            ExistingEngineUrl,
            ExtraTags: null);
}
