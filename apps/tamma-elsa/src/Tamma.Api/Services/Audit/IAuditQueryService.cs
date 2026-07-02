using Tamma.Api.Dtos.Audit;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-3 — read-only, keyset-paginated query surface over the curated
/// <c>audit_records</c> read-model (Story 37-1). Two physically-separate scopes:
/// per-tenant audit (a tenant's own schema, or the sole user's rows in
/// single-user mode) and platform audit (the control-plane rows). A tenant
/// query NEVER reads platform / another tenant's rows and vice-versa.
///
/// <para>This service reads the MATERIALIZED table only — it never re-projects
/// from the raw DCB event streams.</para>
/// </summary>
public interface IAuditQueryService
{
    /// <summary>
    /// Query one tenant's curated audit trail. In SaaS mode this reads the
    /// tenant's own schema filtered by <paramref name="tenantId"/> (physical
    /// isolation + explicit predicate = defence-in-depth). In single-user mode
    /// the sole user's rows live in the control plane keyed by their user id, so
    /// the read is scoped by <paramref name="callerUserId"/>.
    /// </summary>
    Task<AuditQueryResponse> QueryTenantAsync(
        Guid tenantId,
        Guid? callerUserId,
        AuditQueryFilter filter,
        TammaMode mode,
        CancellationToken ct);

    /// <summary>
    /// Query the platform-scope curated audit trail (control-plane rows:
    /// impersonation, tenant lifecycle, platform RBAC, platform-level BYOK).
    /// Physically the control-plane <c>audit_records</c> rows with no tenant
    /// owner — a tenant's rows are in a different schema and are never returned.
    /// </summary>
    Task<AuditQueryResponse> QueryPlatformAsync(
        Guid? callerUserId,
        AuditQueryFilter filter,
        TammaMode mode,
        CancellationToken ct);
}
