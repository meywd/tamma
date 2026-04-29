namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-7 AC1 — port for verifying inbound webhook signatures from
/// a git platform. Each <see cref="PlatformKind"/> registers a concrete
/// implementation under a keyed-DI key; the receiver endpoint at
/// <c>POST /api/webhooks/{platform}</c> resolves the right verifier and
/// hands it the raw body + secret + a header lookup callback.
///
/// <para>Two signature shapes are in scope today:</para>
/// <list type="bullet">
///   <item><b>HMAC-SHA256</b> (GitHub, Gitea, Forgejo, optional GitLab):
///         a per-platform header carries <c>sha256=&lt;hex&gt;</c>. The
///         verifier MUST do the comparison via
///         <c>CryptographicOperations.FixedTimeEquals</c> and MUST NOT
///         leak the failure mode beyond the
///         <see cref="WebhookVerificationOutcome"/> shape returned.</item>
///   <item><b>Static-token</b> (GitLab default — header
///         <c>X-Gitlab-Token</c>): plaintext shared secret compared
///         constant-time against the configured value.</item>
/// </list>
///
/// <para><b>Fail-closed invariant</b> (audit finding 001): when the
/// secret is null/empty the verifier MUST return
/// <see cref="WebhookVerificationOutcome.SecretNotConfigured"/> rather
/// than synthesising a passing result. The receiver translates that to
/// HTTP 503 — never 200, never silently bypassed.</para>
///
/// <para>Body bytes are passed by <see cref="ReadOnlyMemory{T}"/> so
/// the verifier can compute the HMAC without allocating a string copy.
/// The header lookup is a delegate so callers can either back it with
/// <c>HttpContext.Request.Headers[name]</c> or hand-rolled dictionary
/// in tests.</para>
/// </summary>
public interface IWebhookSignatureVerifier
{
    /// <summary>
    /// The platform this verifier is wired for. The receiver checks
    /// <c>verifier.Kind == requestedKind</c> after keyed-DI resolution
    /// to defend against a misconfigured DI key (e.g. a Gitea verifier
    /// registered under <see cref="PlatformKind.GitHub"/>).
    /// </summary>
    PlatformKind Kind { get; }

    /// <summary>
    /// Verify the signature on a webhook delivery.
    /// </summary>
    /// <param name="body">
    /// Raw request body bytes. The verifier may read but MUST NOT
    /// mutate. Pass an empty span only if the caller has already
    /// confirmed the body is empty (the receiver passes the read body
    /// verbatim).
    /// </param>
    /// <param name="secret">
    /// Configured platform secret (HMAC key or static token). Null or
    /// empty triggers the fail-closed path.
    /// </param>
    /// <param name="getHeader">
    /// Case-insensitive header lookup. Returns null when the header is
    /// absent. Implementations call this for the platform-specific
    /// signature/token header; they MUST NOT iterate every header.
    /// </param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    Task<WebhookVerificationResult> VerifyAsync(
        ReadOnlyMemory<byte> body,
        string? secret,
        Func<string, string?> getHeader,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome bucket for <see cref="IWebhookSignatureVerifier.VerifyAsync"/>.
/// Distinguishes the three failure modes the receiver translates to
/// different HTTP status codes:
/// <list type="bullet">
///   <item><see cref="Ok"/> → 200 (continue dispatch).</item>
///   <item><see cref="MissingHeader"/> / <see cref="BadSignature"/> →
///         401 (fail-closed; rate-limit on repeats).</item>
///   <item><see cref="SecretNotConfigured"/> → 503 (operator must
///         configure a secret; receiver MUST NOT silently accept).</item>
/// </list>
/// </summary>
public enum WebhookVerificationOutcome
{
    /// <summary>Signature matches the configured secret over the body bytes.</summary>
    Ok = 0,

    /// <summary>
    /// The platform-specific signature/token header was absent from the
    /// request. Receiver returns 401.
    /// </summary>
    MissingHeader = 1,

    /// <summary>
    /// The signature header was present but did not match the HMAC of
    /// the body / static-token comparison failed. Receiver returns 401.
    /// </summary>
    BadSignature = 2,

    /// <summary>
    /// No webhook secret configured for this platform binding. The
    /// receiver MUST return 503 — never accept the delivery. Audit
    /// finding 001 invariant.
    /// </summary>
    SecretNotConfigured = 3,
}

/// <summary>
/// Tagged outcome from <see cref="IWebhookSignatureVerifier.VerifyAsync"/>.
/// The <see cref="Reason"/> is suitable for non-PII log statements;
/// the verifier MUST NOT include the secret, computed digest, or the
/// raw body in this string.
/// </summary>
public sealed record WebhookVerificationResult(
    WebhookVerificationOutcome Outcome,
    string? Reason = null)
{
    /// <summary>Convenience for the happy path.</summary>
    public static WebhookVerificationResult Success { get; } =
        new(WebhookVerificationOutcome.Ok);

    /// <summary>The platform-specific signature header was absent.</summary>
    public static WebhookVerificationResult MissingHeader(string headerName) =>
        new(WebhookVerificationOutcome.MissingHeader,
            $"Missing required header '{headerName}'");

    /// <summary>The signature was present but didn't validate.</summary>
    public static WebhookVerificationResult BadSignature(string detail) =>
        new(WebhookVerificationOutcome.BadSignature, detail);

    /// <summary>No secret was configured for this platform binding.</summary>
    public static WebhookVerificationResult SecretNotConfigured { get; } =
        new(WebhookVerificationOutcome.SecretNotConfigured,
            "No webhook secret configured for this platform");
}
