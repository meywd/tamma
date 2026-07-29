namespace Tamma.Core.Documents.Policy;

/// <summary>
/// Resolves the effective <see cref="ResolvedAcceptanceRules"/> for a principal +
/// document type (Story 39-5 AC6). The MODEL + this interface live in
/// <c>Tamma.Core</c> so 39-6 (Elsa process) and the 39-17 agent host can depend
/// on them without a <c>Tamma.Api</c> reference; the EF-backed implementation
/// (<c>AcceptanceRulesService</c>) lives in <c>Tamma.Api</c> (Design Decision D1).
///
/// <para>The <c>ForTenant</c> method name (rather than an overload on
/// <c>Guid</c> vs <c>Guid?</c>) follows <c>PromptStoreService</c>'s
/// overload-resolution rationale: a non-null <c>Guid</c> binds to BOTH a
/// nullable and a non-nullable same-named overload, and the non-nullable always
/// wins — which would silently route single-user callers onto the SaaS path.
/// Distinct names keep the two surfaces unambiguous.</para>
/// </summary>
public interface IAcceptanceRulesResolver
{
    /// <summary>
    /// Resolve for the single-user principal (<paramref name="userId"/>): per-type
    /// user override → base user override → static default.
    /// </summary>
    Task<ResolvedAcceptanceRules> ResolveAsync(
        Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default);

    /// <summary>
    /// Resolve for the SaaS tenant (<paramref name="tenantId"/>): per-type tenant
    /// override → base tenant override → static default. Never consults user rows.
    /// </summary>
    Task<ResolvedAcceptanceRules> ResolveForTenantAsync(
        Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default);

    /// <summary>
    /// Story 43-5 AC11 — resolve the single-user principal's BASE row (the
    /// deployment-wide dial + the always-escalate list): base user override →
    /// shipped <see cref="AcceptanceDefaults.Rules"/>. Lifted from the concrete
    /// <c>AcceptanceRulesService</c> so the autonomy gate (which needs the dial
    /// for the current principal) can reach it through the interface.
    /// </summary>
    Task<ResolvedAcceptanceRules> ResolveBaseAsync(
        Guid? userId, CancellationToken ct = default);

    /// <summary>
    /// Tenant variant of <see cref="ResolveBaseAsync"/> (the <c>ForTenant</c>
    /// naming rationale above applies — never an overload on <c>Guid</c> vs
    /// <c>Guid?</c>).
    /// </summary>
    Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(
        Guid tenantId, CancellationToken ct = default);
}
