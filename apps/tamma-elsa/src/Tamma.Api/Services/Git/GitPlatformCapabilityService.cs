using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Epic 31 P2 (plan §4) — the read-only mediation surface behind
/// <c>GET /api/v1/git/{owner}/{repo}/capabilities</c> that the engine's
/// <c>CheckPlatformCapabilityActivity</c> consults BEFORE a
/// capability-gated action step runs. It is MACHINERY (a probe), not a
/// governed effect: it changes nothing outside Tamma and asks about the
/// resolved driver's LIVE capability set (feature-detected per
/// installation — never the static matrix alone; P1's capability
/// contract test is what makes the answer trustworthy).
///
/// <para>Same guard discipline as the mediation ops: the cross-tenant
/// repo guard runs FIRST; an unauthorized repo answers exactly like a
/// mediation 403 so the probe cannot be used to enumerate other
/// tenants' platforms. No DCB event — the probe is not an effect; the
/// audited decision (the SKIPPED/DEGRADED event) is emitted by the
/// workflow's alternative step.</para>
/// </summary>
public interface IGitPlatformCapabilityService
{
    Task<GitCapabilitiesResult> GetCapabilitiesAsync(
        Guid? tenantId, string repo, CancellationToken ct = default);
}

/// <summary>Typed result of the capability probe.</summary>
public sealed record GitCapabilitiesResult
{
    public bool Success { get; init; }

    /// <summary>Wire-form platform kind (github/gitea/…); null on failure.</summary>
    public string? PlatformKind { get; init; }

    /// <summary>Capability names (the <see cref="PlatformCapability"/> member
    /// names, e.g. <c>PrLifecycle</c>), ordinal-sorted.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>"byok" | "platform" credential-source LABEL (never the credential).</summary>
    public string? CredentialSource { get; init; }

    /// <summary>REPO_NOT_AUTHORIZED | GIT_TOKEN_UNAVAILABLE on failure.</summary>
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
}

/// <inheritdoc />
public sealed class GitPlatformCapabilityService : IGitPlatformCapabilityService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IPlatformResolver _platformResolver;
    private readonly ILogger<GitPlatformCapabilityService> _logger;

    public GitPlatformCapabilityService(
        IGitRepoAuthorizer authorizer,
        IPlatformResolver platformResolver,
        ILogger<GitPlatformCapabilityService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitCapabilitiesResult> GetCapabilitiesAsync(
        Guid? tenantId, string repo, CancellationToken ct = default)
    {
        try
        {
            var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
            if (!authz.Allowed)
            {
                return new GitCapabilitiesResult
                {
                    Success = false,
                    FailureCode = GitFailureCodes.RepoNotAuthorized,
                    FailureReason = authz.Reason,
                };
            }

            var resolution = await _platformResolver
                .ResolveForMediationAsync(tenantId, ct)
                .ConfigureAwait(false);
            if (resolution is null)
            {
                return new GitCapabilitiesResult
                {
                    Success = false,
                    FailureCode = GitFailureCodes.TokenUnavailable,
                    FailureReason = "no platform driver could be resolved for the principal",
                };
            }

            return new GitCapabilitiesResult
            {
                Success = true,
                PlatformKind = Tamma.Platforms.PlatformResolver.ToWireKind(resolution.Driver.Kind),
                Capabilities = resolution.Driver.Capabilities
                    .Select(c => c.ToString())
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList(),
                CredentialSource = resolution.Source == MediationCredentialSource.TenantInstallation
                    ? GitCredentialSources.Byok
                    : GitCredentialSources.Platform,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The probe mirrors the mediation's no-throw posture — a probe
            // outage must not surface a raw 5xx to the workflow.
            _logger.LogError(ex, "git capability probe failed for repo {Repo}", Tamma.Core.Logging.LogSanitizer.Clean(repo));
            return new GitCapabilitiesResult
            {
                Success = false,
                FailureCode = GitFailureCodes.PlatformError,
                FailureReason = "capability probe failed",
            };
        }
    }
}
