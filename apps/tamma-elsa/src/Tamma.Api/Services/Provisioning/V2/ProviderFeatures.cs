namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Optional capabilities a provider may advertise on top of its
/// <see cref="ProvisioningTopology"/> support. Drives the onboarding UI's
/// per-tier filters (Story 30-7) and the cost dashboard (Story 30-10).
/// </summary>
/// <remarks>
/// Bit-flags. Providers compose them in
/// <see cref="ProviderCapabilities.Features"/>.
/// </remarks>
[Flags]
public enum ProviderFeatures
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>Provider can attach customer-supplied DNS hostnames to the
    /// provisioned engine.</summary>
    CustomDomains = 1 << 0,

    /// <summary>Provider can scale compute up/down without re-provisioning.</summary>
    AutoscaleCompute = 1 << 1,

    /// <summary>Provider's <see cref="ProvisioningTopology.DatabaseOnly"/> /
    /// <see cref="ProvisioningTopology.DedicatedCompute"/> outcomes include
    /// a fully isolated database (no shared schema with other tenants).</summary>
    DedicatedDb = 1 << 2,

    /// <summary>Provider takes scheduled backups of the tenant's database.</summary>
    BackupManagement = 1 << 3
}
