namespace Tamma.Api.Services.Provisioning.Cranl;

/// <summary>
/// Configuration for the Cranl HTTP client.
///
/// <para>Bound from the <c>Cranl:*</c> configuration section. When
/// <see cref="ApiKey"/> is blank the Null seam wins and no external
/// Cranl resources are minted — tenant placement stays on the unified
/// schema-per-tenant model (the <c>tenant_databases</c> pool, central
/// DB by default).</para>
///
/// <para>See <c>docs/vendors/cranl/README.md</c> for endpoint reference and
/// the per-tenant provisioning flow.</para>
/// </summary>
public sealed class CranlOptions
{
    /// <summary>Base URL of the Cranl API. Default <c>https://app.cranl.com/api</c>.</summary>
    public string BaseUrl { get; set; } = "https://app.cranl.com/api";

    /// <summary>
    /// API key for the Tamma org's Cranl account. Format
    /// <c>cranl_sk_&lt;32 chars&gt;</c>. Sent as
    /// <c>Authorization: Bearer &lt;ApiKey&gt;</c> on every request. Tamma
    /// stores ONE org-scoped key, not per-tenant — every tenant's resources
    /// belong to the same Cranl org.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Cranl organization id that owns every Tamma-provisioned project. The
    /// human owner of the Cranl account creates this once and configures it
    /// here; Tamma never creates orgs.
    /// </summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>
    /// Cranl repository id for the Tamma engine repo (registered via Cranl's
    /// GitHub App integration). Required by <c>POST /api/applications</c>.
    /// </summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>
    /// Default region used when an admin caller does not specify one. The
    /// docs list <c>germany-1</c>, <c>us-east-1</c>, <c>saudi-arabia-1</c>,
    /// <c>egypt-1</c>, <c>india-1</c> as available servers.
    /// </summary>
    public string DefaultRegion { get; set; } = "germany-1";

    /// <summary>Build type for provisioned applications. <c>nixpacks</c> or <c>dockerfile</c>.</summary>
    public string DefaultBuildType { get; set; } = "dockerfile";

    /// <summary>Path inside the Tamma repo that contains the Elsa Dockerfile.</summary>
    public string AppBuildPath { get; set; } = "/apps/tamma-elsa";

    /// <summary>Git branch the Cranl app deploys from.</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// User agent header sent on every request. Helps the Cranl team identify
    /// Tamma traffic in their logs / rate-limit metrics.
    /// </summary>
    public string UserAgent { get; set; } = "Tamma-API";

    /// <summary>
    /// Per-request timeout. Cranl's API is mostly fast (&lt; 2s) but the
    /// provisioning long-tail (db create, app deploy) is async-status, so we
    /// keep this conservative.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when both <see cref="ApiKey"/> and <see cref="OrganizationId"/>
    /// are populated. The DI extension reads this to choose between the
    /// Cranl-backed provisioner and the Null fallback.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(OrganizationId);
}
