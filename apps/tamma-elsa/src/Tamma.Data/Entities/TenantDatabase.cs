namespace Tamma.Data.Entities;

/// <summary>
/// Unified-tenancy Phase 0 (plan 2026-06-09 §2.1) — one row per Postgres
/// database available for tenant-schema placement: the operator's DB pool.
/// A database hosts 1..N tenant schemas; <c>PlacementClass</c> says whether
/// it is a shared pool member or a dedicated (single-tenant) DB. The admin
/// connection string (provisioner role — creates schemas/roles) is encrypted
/// with the same AES-GCM KEK envelope used for tenant connection strings.
/// </summary>
public class TenantDatabase
{
    public Guid Id { get; set; }

    /// <summary>Operator-facing name, e.g. <c>shared-eu-1</c>, <c>dedicated-acme</c>.</summary>
    public string Label { get; set; } = null!;

    public string Host { get; set; } = null!;
    public int Port { get; set; } = 5432;

    /// <summary>AES-GCM/KEK envelope of the provisioner-role connection string.</summary>
    public byte[] AdminConnectionStringEncrypted { get; set; } = null!;

    /// <summary><c>shared</c> | <c>dedicated</c>.</summary>
    public string PlacementClass { get; set; } = "shared";

    /// <summary>Plan tiers allowed to land here, e.g. <c>{free,team}</c>.</summary>
    public string[] TierEligibility { get; set; } = [];

    /// <summary>Max tenant schemas (NULL = unbounded); used for shared pools.</summary>
    public int? TenantCapacity { get; set; }

    /// <summary>Maintained on placement/move operations.</summary>
    public int TenantCount { get; set; }

    /// <summary><c>active</c> | <c>draining</c> | <c>full</c> | <c>retired</c>.</summary>
    public string Status { get; set; } = "active";

    /// <summary>KEK version of the admin-connection envelope.</summary>
    public short KekVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
