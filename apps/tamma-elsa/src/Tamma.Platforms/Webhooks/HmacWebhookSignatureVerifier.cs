using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Webhooks;

/// <summary>
/// Story 31-7 AC1 — generic HMAC-SHA256 webhook signature verifier.
/// Reads <c>sha256=&lt;hex&gt;</c> from a configurable header, computes
/// HMAC-SHA256 over the body bytes, and compares constant-time via
/// <see cref="CryptographicOperations.FixedTimeEquals"/>.
///
/// <para>The header name + optional fallback are constructor-injected
/// so this one class supports GitHub (<c>X-Hub-Signature-256</c>),
/// Gitea (<c>X-Gitea-Signature</c>), and Forgejo
/// (<c>X-Forgejo-Signature</c> with <c>X-Gitea-Signature</c> fallback)
/// without per-driver subclasses. Stories 31-3 / 31-4 / 31-5 will
/// register one instance per platform under the keyed-DI key.</para>
///
/// <para><b>Audit finding 001 invariant</b>: when the secret is null
/// or empty the verifier returns <see cref="WebhookVerificationOutcome.SecretNotConfigured"/>
/// — the receiver translates that to HTTP 503 instead of letting an
/// unconfigured platform binding accept arbitrary deliveries.</para>
///
/// <para><b>Sig prefix</b>: GitHub and Gitea both prefix the hex digest
/// with <c>sha256=</c>; Forgejo follows Gitea. The prefix is
/// case-insensitive on parse but expected lowercase on output. A
/// signature without the prefix is rejected as bad.</para>
/// </summary>
public sealed class HmacWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly string _primaryHeader;
    private readonly string? _fallbackHeader;
    private readonly ILogger<HmacWebhookSignatureVerifier>? _logger;

    public HmacWebhookSignatureVerifier(
        PlatformKind kind,
        string primaryHeader,
        string? fallbackHeader = null,
        ILogger<HmacWebhookSignatureVerifier>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryHeader);
        Kind = kind;
        _primaryHeader = primaryHeader;
        _fallbackHeader = fallbackHeader;
        _logger = logger;
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

        // Audit finding 001 — fail-closed on missing secret. Never
        // synthesize a passing result when the operator hasn't
        // configured a secret for this platform binding.
        if (string.IsNullOrEmpty(secret))
        {
            return Task.FromResult(WebhookVerificationResult.SecretNotConfigured);
        }

        // Pull primary header; fall back to the secondary if configured
        // (Forgejo: X-Forgejo-Signature, fallback X-Gitea-Signature).
        var sigHeader = getHeader(_primaryHeader);
        var headerUsed = _primaryHeader;
        if (string.IsNullOrEmpty(sigHeader) && _fallbackHeader is not null)
        {
            sigHeader = getHeader(_fallbackHeader);
            headerUsed = _fallbackHeader;
        }
        if (string.IsNullOrEmpty(sigHeader))
        {
            return Task.FromResult(WebhookVerificationResult.MissingHeader(_primaryHeader));
        }

        // Strip optional sha256= prefix (GitHub, Gitea, Forgejo all use it).
        var expected = sigHeader;
        const string prefix = "sha256=";
        if (sigHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            expected = sigHeader[prefix.Length..];
        }

        // Compute HMAC over the body bytes (no string round-trip).
        Span<byte> computed = stackalloc byte[32];
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            if (!hmac.TryComputeHash(body.Span, computed, out var written) || written != 32)
            {
                _logger?.LogWarning(
                    "HMAC compute returned {Written} bytes for {Kind} — rejecting",
                    written, Kind);
                return Task.FromResult(WebhookVerificationResult.BadSignature("HMAC computation failed"));
            }
        }

        // Decode the expected hex into a stack buffer; reject any
        // signature that isn't a valid 64-char lowercase hex string —
        // the comparison must be constant-time but the parse can fail
        // fast.
        Span<byte> expectedBytes = stackalloc byte[32];
        if (!TryDecodeHex(expected, expectedBytes))
        {
            return Task.FromResult(WebhookVerificationResult.BadSignature(
                $"Signature in header '{headerUsed}' is not 64-char hex"));
        }

        if (!CryptographicOperations.FixedTimeEquals(computed, expectedBytes))
        {
            return Task.FromResult(WebhookVerificationResult.BadSignature(
                "HMAC mismatch"));
        }

        return Task.FromResult(WebhookVerificationResult.Success);
    }

    /// <summary>
    /// Constant-time hex decode — returns false on length mismatch or
    /// non-hex character. Doesn't allocate; writes into the caller's
    /// destination span.
    /// </summary>
    private static bool TryDecodeHex(string hex, Span<byte> dest)
    {
        if (hex.Length != dest.Length * 2) return false;
        for (var i = 0; i < dest.Length; i++)
        {
            var hi = FromHex(hex[i * 2]);
            var lo = FromHex(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            dest[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int FromHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
