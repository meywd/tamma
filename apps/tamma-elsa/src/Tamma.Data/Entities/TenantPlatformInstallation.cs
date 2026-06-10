using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 31-2 AC1 — control-plane-resident registry row that ties a
/// tenant to one git platform binding.
///
/// <para>Generalises <see cref="GitHubInstallation"/> across every
/// <c>PlatformKind</c> the platform abstraction (Story 31-1) covers.
/// One row per <c>(tenant_id, platform_kind, installation_external_id)</c>
/// triple — a tenant may eventually own multiple bindings (a GitHub
/// org + a self-hosted Gitea instance for example), one of which is
/// flagged as <see cref="IsPrimary"/> when the caller has not specified
/// which kind they want.</para>
///
/// <para>Auth is intentionally stored as a <c>SecretRef</c> tuple
/// (scope + name) — never the credential itself. Story 31-2's
/// <c>PlatformResolver</c> reads through Story 29's
/// <c>ISecretStore</c> + <c>ISecretStoreBackend</c> at resolve time so
/// rotation (29-7) only re-mints the secret-store version; this row
/// never has to be rewritten when a credential rolls.</para>
///
/// <para>Mode behavior:
/// <list type="bullet">
///   <item>single-user mode: rows carry the synthetic single-user
///         tenant id; typically one row per <c>PlatformKind</c>.</item>
///   <item>SaaS mode: rows carry real tenant ids; the resolver scopes
///         lookups by <c>tenant_id</c>.</item>
/// </list>
/// </para>
/// </summary>
[Table("tenant_platform_installations")]
public class TenantPlatformInstallation
{
    /// <summary>Internal Tamma installation id.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Owning tenant. Never null — single-user mode uses the synthetic
    /// single-user tenant id, SaaS mode uses the real tenant id.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Lower-snake string form of <c>PlatformKind</c>. Persisted as a
    /// short string with a CHECK constraint so an unknown value can't
    /// be smuggled in. Mirrors the Postgres-side
    /// <c>('github','gitea','forgejo','gitlab','bitbucket','azure_devops')</c>
    /// CHECK in the migration.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string PlatformKind { get; set; } = string.Empty;

    /// <summary>
    /// Platform base URL. <c>https://api.github.com</c> for github.com;
    /// the self-hosted host for Gitea/Forgejo/GitLab/Bitbucket Server;
    /// the org-scoped <c>https://dev.azure.com/{org}</c> for Azure DevOps.
    /// </summary>
    [Required]
    [MaxLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Platform-side identifier (GitHub installation id, GitLab group
    /// id, Azure DevOps organization id). Opaque string; drivers parse.
    /// Null when the platform's auth model does not need an external id
    /// (e.g. plain Git over SSH).
    /// </summary>
    [MaxLength(255)]
    public string? InstallationExternalId { get; set; }

    /// <summary>
    /// <see cref="SecretRow.Scope"/> for the installation credential —
    /// <c>"platform"</c> or <c>"tenant"</c>. Combined with
    /// <see cref="CredentialSecretName"/> to build a
    /// <c>SecretRef</c> at resolve time.
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string CredentialSecretScope { get; set; } = "tenant";

    /// <summary>
    /// <see cref="SecretRow.Name"/> for the installation credential.
    /// Lower-kebab-case slug per Story 29-1 (e.g.
    /// <c>github-installation/123</c>, <c>gitea/api-token</c>).
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string CredentialSecretName { get; set; } = string.Empty;

    /// <summary>
    /// Optional webhook secret ref (scope + name same shape as the
    /// credential). When null the installation has no associated
    /// webhook and 31-7's receiver path won't try to verify a
    /// signature.
    /// </summary>
    [MaxLength(16)]
    public string? WebhookSecretScope { get; set; }

    /// <summary>Webhook secret slug — see <see cref="CredentialSecretName"/>.</summary>
    [MaxLength(255)]
    public string? WebhookSecretName { get; set; }

    /// <summary>
    /// Lifecycle status — <c>"connected"</c> | <c>"suspended"</c> |
    /// <c>"disconnected"</c>. The migration constrains this via CHECK.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "connected";

    /// <summary>
    /// When a tenant connects multiple platforms, exactly one row per
    /// (tenant_id, platform_kind) tuple is the "primary" — the one
    /// callers without an explicit kind hint resolve to. Defaults to
    /// true so the first-cut single-platform UI always lights up
    /// correctly.
    /// </summary>
    public bool IsPrimary { get; set; } = true;

    /// <summary>
    /// Free-form metadata as JSONB. Drivers may stash platform-side
    /// metadata that doesn't fit the typed columns (e.g. GitLab group
    /// path, Bitbucket workspace slug). Defaults to <c>{}</c>.
    /// </summary>
    [Required]
    [Column("Metadata", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";

    /// <summary>UTC create timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last metadata edit.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Soft-delete marker. When non-null the row is excluded from
    /// resolver lookups but kept for audit. Restored by setting back
    /// to null.
    /// </summary>
    public DateTime? DeletedAt { get; set; }
}
