namespace Tamma.Api.Dtos.Admin;

/// <summary>
/// Unified-tenancy Phase 4 — request + response contracts for the
/// platform-admin <c>tenant_databases</c> pool CRUD
/// (<c>AdminTenantDatabasesEndpoints</c>).
///
/// <para>SECURITY INVARIANT: the admin connection string (plaintext or
/// encrypted envelope) is NEVER serialised into any response. The
/// plaintext travels INBOUND only (POST/PATCH body), is immediately
/// AES-GCM-encrypted via <c>ITenantConnectionStringProtector</c>, and
/// only its KEK version is reported back. No DTO below carries a
/// <c>byte[]</c> or any connection-string field.</para>
/// </summary>
public record AdminTenantDatabaseListItem(
    Guid Id,
    /// <summary>Operator key, unique (e.g. <c>central</c>, <c>shared-eu-1</c>).</summary>
    string Label,
    /// <summary>Parsed from the admin connection string at create/rotate time.</summary>
    string Host,
    int Port,
    /// <summary><c>shared</c> | <c>dedicated</c>.</summary>
    string PlacementClass,
    string[] TierEligibility,
    /// <summary>Max tenant schemas; null = unbounded (advisory, Phase 2 note).</summary>
    int? TenantCapacity,
    int TenantCount,
    /// <summary><c>active</c> | <c>draining</c> | <c>full</c> | <c>retired</c>.</summary>
    string Status,
    /// <summary>KEK version of the (never serialised) admin-connection envelope.</summary>
    int KekVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record AdminTenantDatabaseListResponse(
    IReadOnlyList<AdminTenantDatabaseListItem> Databases,
    int Total);

/// <summary>
/// Detail response — the pool row plus the tenants currently placed on it
/// (the tenant→database view's pool-side half).
/// </summary>
public record AdminTenantDatabaseDetailResponse(
    AdminTenantDatabaseListItem Database,
    IReadOnlyList<AdminTenantDatabaseTenantItem> Tenants);

/// <summary>One tenant placed on a pool row (shadow columns DatabaseId/SchemaName).</summary>
public record AdminTenantDatabaseTenantItem(
    Guid Id,
    string Slug,
    string? SchemaName,
    string? Status);

/// <summary>
/// Body for <c>POST /api/admin/tenant-databases</c>. Host/Port are parsed
/// FROM <see cref="AdminConnectionString"/> — they are intentionally NOT
/// accepted as body fields, so a mismatch is impossible by construction.
/// </summary>
public record CreateTenantDatabaseRequest(
    string Label,
    /// <summary>Plaintext provisioner-role connection string (inbound only; encrypted at rest).</summary>
    string AdminConnectionString,
    string? PlacementClass = null,
    string[]? TierEligibility = null,
    int? TenantCapacity = null);

/// <summary>
/// Body for <c>PATCH /api/admin/tenant-databases/{id}</c>. Null fields are
/// left unchanged (clearing <see cref="TenantCapacity"/> back to unbounded
/// is not supported via PATCH — recreate the row in this zero-data phase).
/// Supplying <see cref="AdminConnectionString"/> rotates the envelope:
/// re-probe, re-parse Host/Port, re-encrypt, stamp the current KEK version,
/// and evict the <c>TenantDatabasePool</c> decrypt cache.
/// </summary>
public record UpdateTenantDatabaseRequest(
    string? Label = null,
    string[]? TierEligibility = null,
    int? TenantCapacity = null,
    string? Status = null,
    string? AdminConnectionString = null);
