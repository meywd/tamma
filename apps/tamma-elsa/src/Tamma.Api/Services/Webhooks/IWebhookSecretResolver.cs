using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Webhooks;

/// <summary>
/// Story 31-7 — combined "find the installation row + read its webhook
/// secret" port. Surfaces a one-call interface over the
/// <see cref="Tamma.Data.Repositories.ITenantPlatformInstallationRepository"/>
/// + <see cref="IPlatformCredentialReader"/> so the receiver doesn't
/// need to take a project reference on the repo (which lives in
/// <c>Tamma.Data</c> — already pulled in transitively, but the
/// receiver's job is HTTP plumbing, not data access).
///
/// <para><b>Cross-tenant safety</b>: <see cref="ResolveInstallationAsync"/>
/// scopes by <c>(<see cref="PlatformKind"/>, externalId)</c> only; the
/// receiver never trusts a tenant id from the request.</para>
/// </summary>
public interface IWebhookSecretResolver
{
    /// <summary>
    /// Look up the installation row for a webhook delivery — keyed by
    /// the platform kind and the external id extracted from the
    /// payload. Returns null when no matching row exists (the
    /// "webhook arrived before onboarding linked the install" race).
    /// </summary>
    Task<PlatformInstallation?> ResolveInstallationAsync(
        PlatformKind kind,
        string installationExternalId,
        CancellationToken ct = default);

    /// <summary>
    /// Read the active-version webhook secret for an installation row.
    /// Returns null when:
    /// <list type="bullet">
    ///   <item>The row's <c>WebhookSecretScope</c>/<c>WebhookSecretName</c>
    ///         columns are null (no per-installation secret configured).</item>
    ///   <item>The secret store has no active version for the
    ///         (scope, name) tuple (the operator hasn't entered a
    ///         value yet).</item>
    /// </list>
    /// The receiver falls back to a config-level secret when this
    /// returns null.
    /// </summary>
    Task<string?> ReadWebhookSecretAsync(
        PlatformInstallation installation,
        CancellationToken ct = default);
}
