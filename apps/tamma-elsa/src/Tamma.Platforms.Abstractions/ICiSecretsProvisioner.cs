using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — neutral CI-secrets provisioner. Bridges the Tamma
/// secret cabinet (Epic 29's source of truth) and the platform's
/// CI-side secret store (GitHub Actions secrets, GitLab CI variables,
/// Gitea Actions secrets).
///
/// <para>Plaintext-in interface: callers (the rotation cascade,
/// onboarding bootstrap) hand the provisioner a plaintext secret value;
/// the provisioner is responsible for the platform-native wire shape
/// (libsodium sealed-box for GitHub + Gitea, masked variable for
/// GitLab, plaintext POST for Gitea). The plaintext is wrapped in
/// <see cref="RedactedSecret"/> at construction so log accidents are
/// caught by the type system rather than relying on every call site
/// to remember the redaction.</para>
///
/// <para>Capability gating: callers MUST check
/// <see cref="IGitPlatformDriver.Capabilities"/> for
/// <see cref="PlatformCapability.Secrets"/> before invoking; the
/// provisioner additionally enforces this at runtime per-target —
/// targets whose scope is unsupported on the platform produce a
/// <see cref="CiSecretProvisionResult"/> with
/// <c>scope_not_supported_on_platform</c> rather than silently
/// dropping the call.</para>
///
/// <para>Per-target error isolation: every method takes a list of
/// <see cref="CiSecretTarget"/> values and returns a per-target
/// <see cref="CiSecretProvisionResult"/>. A 5xx on one target does
/// NOT short-circuit the others; the caller decides whether the
/// partial success is acceptable.</para>
///
/// <para>Cross-tenant safety: the provisioner is mounted on a
/// <see cref="IGitPlatformDriver"/> instance bound to a specific
/// <see cref="PlatformInstallation"/> (one tenant, one platform). A
/// tenant's provisioner never reaches another tenant's CI store —
/// that property is enforced by the resolver (Story 31-2) which
/// composes one driver per (tenantId, kind).</para>
/// </summary>
public interface ICiSecretsProvisioner
{
    /// <summary>
    /// The platform this provisioner targets — populated by the
    /// driver. Useful for logging and capability assertions.
    /// </summary>
    PlatformKind Kind { get; }

    /// <summary>
    /// Provision (create-or-update) <paramref name="secretName"/> =
    /// <paramref name="secretValue"/> at the given <paramref name="scope"/>
    /// + targets. Per-target results returned in the same order as
    /// <paramref name="targets"/>.
    ///
    /// <para>The <see cref="RedactedSecret"/> wrapper is the public
    /// boundary — the inner plaintext is revealed once to the
    /// platform-native crypto primitive (libsodium sealed-box for
    /// GitHub/Gitea) or directly to the HTTPS POST body
    /// (GitLab/Gitea-plaintext). Implementations MUST NOT log the
    /// revealed bytes.</para>
    /// </summary>
    Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret secretValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Rotate <paramref name="secretName"/> at every target — same
    /// shape as <see cref="ProvisionSecretAsync"/> but signals
    /// rotation intent (drivers MAY emit a different audit-event
    /// type or surface the old key id alongside the new one).
    /// </summary>
    Task<IReadOnlyList<CiSecretProvisionResult>> RotateSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret newValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Delete <paramref name="secretName"/> at every target. 404 on
    /// the platform is treated as success (idempotent delete).
    /// </summary>
    Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate secrets currently provisioned at the given scope +
    /// target. Returns metadata only — the secret VALUE is never
    /// exposed by this call. Useful for admin dashboards
    /// (Story 31-9) and the rotation-cascade reconciliation step.
    ///
    /// <para>Returns
    /// <see cref="PlatformResult{T}.ServiceUnavailable"/> when the
    /// platform does not implement listing for that scope (Gitea ≤
    /// 1.24 does not list user-scope secrets; GitHub does not list
    /// environment-secret values).</para>
    /// </summary>
    Task<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>> ListSecretsAsync(
        CiSecretScope scope,
        CiSecretTarget target,
        CancellationToken ct = default);
}
