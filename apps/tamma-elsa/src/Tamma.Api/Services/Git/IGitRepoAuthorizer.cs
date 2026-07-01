namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC2) — the cross-tenant guard. THE load-bearing control
/// (design §1.3: "a mis-scoped platform token = cross-tenant write/merge").
/// Authorizes that the acting tenant (from <c>ITenantContext</c> / X-Tenant-Id,
/// NEVER the request body) may act on <c>{repo}</c> BEFORE any token resolution
/// or platform call. Fail-closed: no installation / a tenant mismatch ⇒ denied.
/// </summary>
public interface IGitRepoAuthorizer
{
    Task<GitRepoAuthorization> AuthorizeAsync(Guid? tenantId, string repo, CancellationToken ct = default);
}

/// <summary>The authorization decision. <see cref="Allowed"/> false ⇒ the caller
/// returns 403 <c>REPO_NOT_AUTHORIZED</c> and NEVER resolves a token or calls the
/// platform.</summary>
public sealed record GitRepoAuthorization(bool Allowed, string? Reason)
{
    public static GitRepoAuthorization Allow() => new(true, null);
    public static GitRepoAuthorization Deny(string reason) => new(false, reason);
}
