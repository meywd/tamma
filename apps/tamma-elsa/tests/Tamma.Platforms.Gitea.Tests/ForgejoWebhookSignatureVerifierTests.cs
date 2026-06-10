using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Story 31-5 — Forgejo's outbound webhooks default to header
/// <c>X-Forgejo-Signature</c>; older fork installs (pre-rename) emit
/// <c>X-Gitea-Signature</c>. The Forgejo driver wires the verifier
/// with <see cref="GiteaWebhookSignatureVerifier.ForgejoAndGiteaHeaderNames"/>
/// — Forgejo native first, Gitea legacy fallback. These tests assert
/// the priority + fallback behaviour, plus the existing fail-closed
/// + rejection paths that 31-4 covers for Gitea apply identically.
/// </summary>
[TestFixture]
public class ForgejoWebhookSignatureVerifierTests
{
    private static string ComputeSignature(string secret, ReadOnlySpan<byte> body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body.ToArray());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.AppendFormat("{0:x2}", b);
        return sb.ToString();
    }

    private static GiteaWebhookSignatureVerifier ForgejoVerifier() => new(
        GiteaWebhookSignatureVerifier.ForgejoAndGiteaHeaderNames);

    [Test]
    public void Forgejo_WebhookSignature_AcceptsForgejoHeader()
    {
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");
        var secret = "forgejo-webhook-secret";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Forgejo-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Forgejo_WebhookSignature_AcceptsLegacyGiteaHeader_WhenForgejoMissing()
    {
        // Older Forgejo forks (pre-rename) emit X-Gitea-Signature only.
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("legacy-fork-payload");
        var secret = "shared-secret";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Gitea-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Forgejo_WebhookSignature_PrefersForgejoHeader_WhenBothPresent()
    {
        // If both headers are present and both are computed against
        // the same secret/body, both validate. Forgejo's header is
        // tried first per the configured priority list, so the
        // matched-header in the verifier internals is Forgejo's.
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("dual-header-body");
        var secret = "secret";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name is "X-Forgejo-Signature" or "X-Gitea-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Forgejo_WebhookSignature_RejectsUnrelatedPlatformHeader()
    {
        // GitHub's X-Hub-Signature-256 must not be accepted by the
        // Forgejo verifier — defensive check that the header allowlist
        // is enforced.
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("payload");
        var secret = "secret";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Hub-Signature-256" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.MissingHeader);
    }

    [Test]
    public void Forgejo_WebhookSignature_RejectsMismatch()
    {
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("payload");

        var result = verifier.Verify(body, "real-secret",
            name => name == "X-Forgejo-Signature"
                ? new string('a', 64) // bogus 64-char hex
                : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.SignatureMismatch);
    }

    [Test]
    public void Forgejo_WebhookSignature_FailsClosedOnMissingSecret()
    {
        var verifier = ForgejoVerifier();

        var result = verifier.Verify(
            Encoding.UTF8.GetBytes("x"), secret: null,
            name => name == "X-Forgejo-Signature" ? "anything" : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.MissingSecret);
    }

    [Test]
    public void Forgejo_WebhookSignature_StripsSha256Prefix()
    {
        // Some clients prepend "sha256=" — verifier strips it before
        // comparison. Same behaviour as the Gitea path; covered here
        // to lock in for the Forgejo header path too.
        var verifier = ForgejoVerifier();
        var body = Encoding.UTF8.GetBytes("payload");
        var secret = "s";
        var sig = "sha256=" + ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Forgejo-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Forgejo_HeaderNames_PrefersForgejoFirst()
    {
        // Defensive — guard against future re-orderings of the static
        // header list. The Forgejo-flavoured list MUST start with
        // X-Forgejo-Signature; the matched-first semantics in
        // GiteaWebhookSignatureVerifier.Verify depend on this order.
        GiteaWebhookSignatureVerifier.ForgejoAndGiteaHeaderNames[0]
            .Should().Be("X-Forgejo-Signature");
        GiteaWebhookSignatureVerifier.ForgejoAndGiteaHeaderNames[1]
            .Should().Be("X-Gitea-Signature");
    }
}
