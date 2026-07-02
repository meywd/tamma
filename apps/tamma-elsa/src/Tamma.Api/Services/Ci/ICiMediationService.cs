namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) — the managed CI (GitHub Actions) execution layer behind the
/// <c>/api/v1/ci/{owner}/{repo}/...</c> endpoints. Composes the same rule-1 sequence
/// as Story 38-1's git mediation ENTIRELY inside <c>Tamma.Api</c>: cross-tenant
/// guard → per-tenant token resolution (BYOK→platform) → CI call with the RESOLVED
/// token → exactly-one terminal DCB audit event. ALWAYS returns a typed, key-free
/// <see cref="CiMediationResult"/> — a failure never throws a raw 5xx.
/// </summary>
public interface ICiMediationService
{
    Task<CiMediationResult> TriggerTestsAsync(Guid? tenantId, string repo, TriggerTestsRequest body, CancellationToken ct = default);
    Task<CiMediationResult> GetBuildStatusAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default);
}
