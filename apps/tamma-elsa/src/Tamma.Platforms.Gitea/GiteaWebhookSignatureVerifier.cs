using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Verifies Gitea (and Forgejo, via reuse) outbound webhook signatures
/// using HMAC-SHA256 over the raw request body.
///
/// <para>Gitea sends the signature in <c>X-Gitea-Signature</c> as
/// hex-encoded lowercase bytes. Forgejo currently ships compat headers
/// (<c>X-Forgejo-Signature</c>, <c>X-Gitea-Signature</c>); 31-5
/// configures the header-name list to accept both.</para>
///
/// <para>Fail-closed: if the secret is null/empty,
/// <see cref="Verify"/> returns <see cref="VerificationResult.MissingSecret"/>.
/// Story 31-7's receiver routes that to a 503-shaped response so a
/// misconfigured webhook never silently passes.</para>
/// </summary>
public sealed class GiteaWebhookSignatureVerifier
{
    /// <summary>Default header list — Gitea-only.</summary>
    public static readonly IReadOnlyList<string> DefaultHeaderNames =
        new[] { "X-Gitea-Signature" };

    /// <summary>Header list including Forgejo compat (used by 31-5).</summary>
    public static readonly IReadOnlyList<string> GiteaAndForgejoHeaderNames =
        new[] { "X-Gitea-Signature", "X-Forgejo-Signature" };

    private readonly IReadOnlyList<string> _headerNames;
    private readonly ILogger _logger;

    public GiteaWebhookSignatureVerifier(
        IReadOnlyList<string>? headerNames = null,
        ILogger? logger = null)
    {
        _headerNames = headerNames is { Count: > 0 }
            ? headerNames
            : DefaultHeaderNames;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Verify the HMAC-SHA256 signature on a body.
    /// </summary>
    /// <param name="body">Raw request bytes — EXACTLY as received.</param>
    /// <param name="secret">Webhook secret — fail-closed when empty.</param>
    /// <param name="getHeader">
    /// Header reader. Implementations should return null for missing
    /// headers and the verbatim string for present ones; case-insensitive
    /// header name matching is the caller's job (typical
    /// <c>HttpRequest.Headers[name]</c> already does this).
    /// </param>
    public VerificationResult Verify(
        ReadOnlySpan<byte> body,
        string? secret,
        Func<string, string?> getHeader)
    {
        ArgumentNullException.ThrowIfNull(getHeader);

        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning(
                "Gitea webhook signature verification rejected — no secret configured (fail-closed)");
            return VerificationResult.MissingSecret;
        }

        // Find first non-null header from the list (Gitea-then-Forgejo
        // order so the canonical header wins).
        string? provided = null;
        string? matchedHeader = null;
        foreach (var headerName in _headerNames)
        {
            var value = getHeader(headerName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                provided = value.Trim();
                matchedHeader = headerName;
                break;
            }
        }
        if (provided is null)
        {
            _logger.LogWarning(
                "Gitea webhook signature verification rejected — no signature header present");
            return VerificationResult.MissingHeader;
        }

        // Strip optional "sha256=" prefix some clients add.
        if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            provided = provided["sha256=".Length..];
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        var computed = hmac.ComputeHash(body.ToArray());
        var computedHex = ToHexLower(computed);

        var providedBytes = Encoding.ASCII.GetBytes(provided);
        var computedBytes = Encoding.ASCII.GetBytes(computedHex);

        if (providedBytes.Length != computedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(providedBytes, computedBytes))
        {
            _logger.LogWarning(
                "Gitea webhook signature mismatch (header={Header})", matchedHeader);
            return VerificationResult.SignatureMismatch;
        }

        return VerificationResult.Valid;
    }

    private static string ToHexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
        return sb.ToString();
    }

    /// <summary>
    /// Outcomes a verifier emits. Fail-closed: callers that need a
    /// boolean SHOULD treat anything other than <see cref="Valid"/> as
    /// rejected.
    /// </summary>
    public enum VerificationResult
    {
        Valid = 1,
        SignatureMismatch = 2,
        MissingHeader = 3,
        MissingSecret = 4,
    }
}
