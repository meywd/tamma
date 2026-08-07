namespace Tamma.Platforms;

/// <summary>
/// Epic 31 P2 — the <c>Platform:</c> config section (owner point 1:
/// "config activates the platform" in single-user mode). Consumed by
/// <see cref="PlatformResolver"/> as a CONFIG-BACKED SOURCE: when a
/// principal has no <c>tenant_platform_installations</c> row, the
/// resolver synthesizes an in-memory
/// <see cref="Abstractions.Models.PlatformInstallation"/> from this
/// section and composes a driver through the exact same keyed-factory
/// seam the DB path uses. NOTHING is persisted — no config↔DB drift,
/// idempotent by construction, no re-seed semantics.
///
/// <para>Two-scoping rule (CLAUDE.md):</para>
/// <list type="bullet">
///   <item><b>single-user mode</b> — the sole user owns this section;
///         it IS the activation switch.</item>
///   <item><b>SaaS mode</b> — the section is the deployment-level
///         SYSTEM tier (the same role the legacy <c>GitHub:Token</c>
///         fallback played): a tenant's own installation row always
///         wins; a tenant without one falls back here.</item>
/// </list>
///
/// <para>Shape:</para>
/// <code>
/// "Platform": {
///   "Kind": "github",                  // wire kind: github|gitea|forgejo|gitlab|…
///   "BaseUrl": "https://api.github.com",
///   "Credential": "ghp_…",             // EITHER: env/config plaintext
///   "CredentialSecretName": "github/op-token", // OR: a secret-cabinet slug
///   "CredentialSecretScope": "platform",       // secret scope (default "platform")
///   "WebhookSecretName": "github/webhook"      // reserved for P4's registration caller
/// }
/// </code>
/// </summary>
public sealed class SingleUserPlatformOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Platform";

    /// <summary>Wire-form platform kind (<c>github</c>, <c>gitea</c>,
    /// <c>forgejo</c>, <c>gitlab</c>, …). Empty = section inactive.</summary>
    public string? Kind { get; set; }

    /// <summary>Platform API base URL; empty = the driver's default
    /// (github.com for kind github).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Credential plaintext straight from env/config
    /// (PAT, or the driver's JSON credential wire format). Wins over
    /// <see cref="CredentialSecretName"/> when both are set.</summary>
    public string? Credential { get; set; }

    /// <summary>Secret-cabinet slug to read the credential from when
    /// <see cref="Credential"/> is not inlined.</summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Secret scope for <see cref="CredentialSecretName"/> —
    /// default <c>platform</c> (deployment-level, no tenant owner).</summary>
    public string CredentialSecretScope { get; set; } = "platform";

    /// <summary>Optional platform-side installation id (GitHub App
    /// installation id for an app-mode credential).</summary>
    public string? InstallationExternalId { get; set; }

    /// <summary>Webhook secret slug — parsed now, consumed by P4's
    /// webhook-registration caller.</summary>
    public string? WebhookSecretName { get; set; }

    /// <summary>True when the section names a kind (the activation switch).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Kind);
}
