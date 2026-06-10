namespace Tamma.Api.Services.Secrets.Query;

/// <summary>
/// Story 29-4 / 29-5 query + lifecycle surface for the secret cabinet
/// UIs. Wraps <see cref="Postgres.SecretsDbContext"/> reads + version
/// retire-writes without touching the plaintext path (that lives on
/// <see cref="Reveal.ISecretRevealService"/>).
///
/// <para><b>Scope enforcement</b>: every method accepts a required
/// <see cref="SecretScope"/> and an optional <c>tenantId</c>. The
/// implementation filters at the DB level so a bug in the endpoint
/// layer that forgets to pass the tenant id still fails closed (404
/// on detail, empty list on list). Combined with schema-level tenant
/// isolation (unified tenancy model) this gives three layers (RBAC →
/// endpoint filter → this scope check) protecting against
/// cross-tenant reads.</para>
///
/// <para>Plaintext is never returned by any method on this interface.
/// The detail + version list + audit list are all metadata-only; the
/// reveal-once UX is the only path that hands a plaintext back, and
/// it lives on <see cref="Reveal.ISecretRevealService"/>.</para>
/// </summary>
public interface ISecretQueryService
{
    /// <summary>
    /// List metadata rows matching the given scope (and tenant id,
    /// when scope is <see cref="SecretScope.Tenant"/>). Ordered by
    /// <c>updated_at DESC</c> so the most recently rotated / created
    /// secrets bubble to the top. Returns an empty list when no rows
    /// match — the UI renders an empty state.
    /// </summary>
    Task<IReadOnlyList<SecretMetadata>> ListAsync(
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Load a single secret's metadata by id. Enforces that the row's
    /// scope + tenant id match the caller's scope + tenant id; returns
    /// null on mismatch or when the row does not exist (the UI renders
    /// a 404 either way so existence is not leaked cross-tenant).
    /// </summary>
    Task<SecretMetadata?> GetAsync(
        Guid secretId,
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// List every version of a secret, newest first. Returns an empty
    /// list when the secret does not exist or is out-of-scope for the
    /// caller — same existence-leak defence as <see cref="GetAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
        Guid secretId,
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Retire a specific non-active version: flip it to
    /// <see cref="SecretVersionStatus.RetiredGrace"/> (or to
    /// <see cref="SecretVersionStatus.Revoked"/> if already
    /// retired-grace). Refuses the current active version — callers
    /// must rotate first so the successor exists before the active
    /// row is taken away.
    /// </summary>
    /// <returns>The new version status on success.</returns>
    /// <exception cref="KeyNotFoundException">No secret / version row
    /// matches — or the scope check rejected the caller.</exception>
    /// <exception cref="InvalidOperationException">Attempted to retire
    /// the active version.</exception>
    Task<SecretVersionStatus> RetireVersionAsync(
        Guid secretId,
        int versionNumber,
        SecretScope scope,
        Guid? tenantId,
        Guid actorUserId,
        CancellationToken ct = default);
}
