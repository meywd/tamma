namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Canonical secret-cabinet slugs for per-tenant integration credentials
/// (JIRA + email BYOK), kept as one source of truth so the write endpoints
/// (<c>IntegrationCredentialEndpoints</c>) and the per-request resolvers
/// (<c>JiraCredentialResolver</c> / <c>EmailCredentialResolver</c>) cannot drift
/// apart on the slug. Aligns with the provider BYOK convention
/// (<c>Tamma.Activities.LlmCall.Credentials.ProviderCabinetNames</c>): a stable
/// lower-kebab path under a feature prefix.
///
/// <para>Each integration stores its whole credential BUNDLE as a single
/// tenant-scoped cabinet secret (a JSON payload) under one slug — NON-migration:
/// this reuses the existing <c>secrets</c>/<c>secret_versions</c> cabinet with a
/// new name, no new table/column. The bundle is unique per
/// <c>(scope, tenantId, name)</c>, so two tenants can both hold
/// <c>integration/jira/config</c> without collision (the cabinet's tenant-scoped
/// unique index).</para>
/// </summary>
public static class IntegrationCabinetNames
{
    /// <summary>
    /// Tenant-scoped JIRA credential bundle (baseUrl + email + apiToken) —
    /// <c>integration/jira/config</c>. Matches the Story 29-1 slug grammar
    /// (lower-kebab with <c>/</c> path separators).
    /// </summary>
    public const string JiraConfig = "integration/jira/config";

    /// <summary>
    /// Tenant-scoped email transport credential bundle (transport + from +
    /// SMTP/Resend secret) — <c>integration/email/config</c>.
    /// </summary>
    public const string EmailConfig = "integration/email/config";
}
