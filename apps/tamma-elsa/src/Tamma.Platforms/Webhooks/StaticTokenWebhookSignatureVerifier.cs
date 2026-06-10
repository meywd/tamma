using System.Security.Cryptography;
using System.Text;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Webhooks;

/// <summary>
/// Story 31-7 AC1 — static-token webhook verifier (GitLab default).
/// Reads a configurable header and compares it constant-time against
/// the configured secret value.
///
/// <para>GitLab's webhook config lets the operator set a "Secret token"
/// which the platform sends verbatim in <c>X-Gitlab-Token</c>. Less
/// secure than HMAC (the secret travels with every delivery rather
/// than being used as an HMAC key) but is what GitLab offers by
/// default; HMAC is opt-in via the platform's "URL hash" feature
/// which the GitHub/Gitea drivers use instead.</para>
///
/// <para><b>Audit finding 001 invariant</b>: missing/empty secret →
/// <see cref="WebhookVerificationOutcome.SecretNotConfigured"/> (HTTP
/// 503 from receiver), never silently accepted.</para>
/// </summary>
public sealed class StaticTokenWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly string _headerName;

    public StaticTokenWebhookSignatureVerifier(PlatformKind kind, string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        Kind = kind;
        _headerName = headerName;
    }

    /// <inheritdoc />
    public PlatformKind Kind { get; }

    /// <inheritdoc />
    public Task<WebhookVerificationResult> VerifyAsync(
        ReadOnlyMemory<byte> body,
        string? secret,
        Func<string, string?> getHeader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(getHeader);

        if (string.IsNullOrEmpty(secret))
        {
            return Task.FromResult(WebhookVerificationResult.SecretNotConfigured);
        }

        var token = getHeader(_headerName);
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(WebhookVerificationResult.MissingHeader(_headerName));
        }

        // Compare via FixedTimeEquals to avoid leaking the token via a
        // timing oracle. Encode both sides as UTF-8 bytes; the helper
        // returns false on length mismatch (no early-exit timing).
        var expected = Encoding.UTF8.GetBytes(secret);
        var actual = Encoding.UTF8.GetBytes(token);

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return Task.FromResult(WebhookVerificationResult.BadSignature(
                $"Static token in header '{_headerName}' did not match"));
        }

        return Task.FromResult(WebhookVerificationResult.Success);
    }
}
