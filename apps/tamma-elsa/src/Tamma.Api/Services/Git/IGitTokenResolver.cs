namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC3) — resolves the per-tenant git token BYOK→platform,
/// tenant→system→error (never empty/default, <c>feedback_resolution_no_empty_fallback</c>).
/// The resolved token is request-scoped and used for exactly one platform call;
/// it NEVER appears in a response, log line, or DCB event. Only the
/// <see cref="GitTokenResolution.Source"/> LABEL is safe to surface.
/// </summary>
public interface IGitTokenResolver
{
    /// <summary>
    /// Resolve the git token for the acting tenant + repo. Returns null when the
    /// credential genuinely cannot be resolved (⇒ the caller returns 503
    /// <c>GIT_TOKEN_UNAVAILABLE</c>, fail-closed — NEVER a call with an empty token).
    /// </summary>
    Task<GitTokenResolution?> ResolveAsync(Guid? tenantId, string repo, CancellationToken ct = default);
}

/// <summary>The resolved token + its source label. The token is load-bearing
/// sensitive and must never be logged / returned / persisted.</summary>
public sealed record GitTokenResolution(string Token, string Source);
