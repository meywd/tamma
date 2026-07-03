namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Which tier of the tenant→system→fail-loud chain resolved an integration
/// credential (mirrors git BYOK's <c>GitCredentialSources</c>). Recorded on the
/// resolution so callers / tests can assert the tier that answered.
/// </summary>
public enum IntegrationCredentialSource
{
    /// <summary>The tenant's own BYOK bundle, read from the tenant-scoped secret
    /// cabinet. The legitimate SaaS tier.</summary>
    Tenant,

    /// <summary>The process-level <c>Jira:*</c> / <c>Email:*</c> configuration —
    /// the single-user "system" tier (the sole principal's legitimate creds).
    /// Deliberately NOT consulted in SaaS: a shared platform credential with no
    /// per-tenant scoping is the confused-deputy the fail-loud guard blocks.</summary>
    System,
}
