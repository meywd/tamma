namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-1 — the 11 compliance-relevant categories every catalogued
/// sensitive action falls under. Stored on <c>audit_records.category</c> as
/// the member name (lowercased) so the curated trail reads as
/// <c>secret</c>/<c>rbac</c>/… in ad-hoc SQL. Every value MUST have ≥1
/// catalogued action code (asserted by the catalog-completeness test).
/// </summary>
public enum AuditCategory
{
    /// <summary>Prompt / persona / convention / agent-config / sanitization-rule edits.</summary>
    Config,

    /// <summary>Role / membership / invite changes.</summary>
    Rbac,

    /// <summary>Secret read / write / reveal / rotate / revoke.</summary>
    Secret,

    /// <summary>Provider-key / provider-chain (bring-your-own-key) changes.</summary>
    Byok,

    /// <summary>Plan / subscription / budget changes.</summary>
    Billing,

    /// <summary>Data export / DSAR (data-subject-access-request).</summary>
    Export,

    /// <summary>Login success/failure, logout, password reset, token refresh/reuse.</summary>
    Auth,

    /// <summary>Platform-admin impersonation start/end.</summary>
    Impersonation,

    /// <summary>Tenant provision / deprovision / move / lifecycle.</summary>
    Tenant,

    /// <summary>Agent dispatch / autonomous code action.</summary>
    Agent,

    /// <summary>Persona / system-prompt changes.</summary>
    Persona,
}
