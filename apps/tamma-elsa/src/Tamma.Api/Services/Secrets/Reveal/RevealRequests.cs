namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Story 29-3 outcome of a successful
/// <see cref="SecretRevealService.IssueCreateAsync"/> /
/// <see cref="SecretRevealService.IssueRotateAsync"/> call. Carries the
/// secret metadata <em>and</em> the one-shot reveal token. The
/// plaintext is NOT included — the caller has to follow up with
/// <see cref="SecretRevealService.ConsumeAsync"/> within
/// <see cref="ExpiresAt"/>.
/// </summary>
/// <param name="Metadata">Persisted metadata row (from the secret
/// cabinet) for the newly-created or newly-rotated secret.</param>
/// <param name="RevealToken">Raw base64url-encoded 32-byte bearer
/// token. Returned once in the HTTP response body; stored only as an
/// HMAC-SHA256 hash on the server.</param>
/// <param name="ExpiresAt">UTC timestamp past which the token no
/// longer resolves. Set to <c>now + 60s</c> per Story 29-3 AC1.</param>
public sealed record RevealTokenIssueResult(
    SecretMetadata Metadata,
    string RevealToken,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Story 29-3 outcome of a successful
/// <see cref="SecretRevealService.ConsumeAsync"/> call. Carries the
/// plaintext value exactly once — subsequent
/// <see cref="SecretRevealService.ConsumeAsync"/> calls against the
/// same token return
/// <see cref="RevealTokenConsumeOutcome.AlreadyConsumed"/> /
/// <see cref="RevealTokenConsumeOutcome.Expired"/> /
/// <see cref="RevealTokenConsumeOutcome.NotFound"/>.
/// </summary>
/// <param name="Outcome">Success vs the three failure modes.</param>
/// <param name="SecretId">Secret id (on success + the three failure
/// modes where the token matched a known row).</param>
/// <param name="VersionNumber">Version number revealed (on success).</param>
/// <param name="SecretName">Slug of the parent secret (on
/// success).</param>
/// <param name="Plaintext">The raw secret value (on success
/// only).</param>
/// <param name="ExpiresAt">UTC timestamp the token expired at (or
/// would have expired at).</param>
public sealed record RevealTokenConsumeResult(
    RevealTokenConsumeOutcome Outcome,
    Guid? SecretId,
    int? VersionNumber,
    string? SecretName,
    string? Plaintext,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Success / failure enum for
/// <see cref="SecretRevealService.ConsumeAsync"/>. The endpoint layer
/// maps each variant onto an HTTP status:
/// <list type="bullet">
///   <item><description><see cref="Success"/> → 200 with the
///     plaintext payload.</description></item>
///   <item><description><see cref="AlreadyConsumed"/> → 410 Gone with
///     <c>error = "already_consumed"</c>.</description></item>
///   <item><description><see cref="Expired"/> → 410 Gone with
///     <c>error = "expired"</c>.</description></item>
///   <item><description><see cref="NotFound"/> → 404 Not Found —
///     token does not map to any row.</description></item>
/// </list>
/// </summary>
public enum RevealTokenConsumeOutcome
{
    Success,
    AlreadyConsumed,
    Expired,
    NotFound,
}

/// <summary>
/// Context captured at reveal time for the audit event. Populated by
/// the endpoint layer from the request headers so the audit row can
/// tie the reveal back to a concrete session.
/// </summary>
/// <param name="UserAgent">Raw <c>User-Agent</c> header, truncated to
/// the column limit (512 chars).</param>
/// <param name="RemoteIp">Caller IP — SHA-256 hashed before persistence
/// so the audit log does not leak the raw IP.</param>
public sealed record RevealCallerContext(string? UserAgent, string? RemoteIp);
