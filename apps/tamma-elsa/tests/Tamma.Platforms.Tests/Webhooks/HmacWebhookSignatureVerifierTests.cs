using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Webhooks;

namespace Tamma.Platforms.Tests.Webhooks;

/// <summary>
/// Story 31-7 — unit coverage for the HMAC-SHA256 verifier (the
/// GitHub / Gitea / Forgejo path).
/// </summary>
[TestFixture]
public class HmacWebhookSignatureVerifierTests
{
    private const string Secret = "shhh-very-secret-value";
    private static readonly byte[] BodyBytes = Encoding.UTF8.GetBytes(
        """{"action":"created","installation":{"id":42}}""");

    private static string ComputeSig(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Func<string, string?> Headers(params (string Name, string? Value)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return name => dict.TryGetValue(name, out var v) ? v : null;
    }

    [Test]
    public async Task VerifyAsync_GitHub_ValidSignature_ReturnsOk()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var sig = ComputeSig(BodyBytes, Secret);
        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers(("X-Hub-Signature-256", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public async Task VerifyAsync_BadSignature_ReturnsBadSignature()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var result = await verifier.VerifyAsync(
            BodyBytes, Secret,
            Headers(("X-Hub-Signature-256", "sha256=" + new string('0', 64))));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }

    [Test]
    public async Task VerifyAsync_MissingHeader_ReturnsMissingHeader()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers());

        result.Outcome.Should().Be(WebhookVerificationOutcome.MissingHeader);
    }

    [Test]
    public async Task VerifyAsync_NullSecret_ReturnsSecretNotConfigured()
    {
        // Audit finding 001 fail-closed invariant — receiver returns 503.
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var sig = ComputeSig(BodyBytes, Secret);
        var result = await verifier.VerifyAsync(
            BodyBytes, secret: null,
            Headers(("X-Hub-Signature-256", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.SecretNotConfigured);
    }

    [Test]
    public async Task VerifyAsync_EmptySecret_ReturnsSecretNotConfigured()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var result = await verifier.VerifyAsync(
            BodyBytes, secret: "",
            Headers(("X-Hub-Signature-256", "sha256=anything")));

        result.Outcome.Should().Be(WebhookVerificationOutcome.SecretNotConfigured);
    }

    [Test]
    public async Task VerifyAsync_SignatureWithoutPrefix_TreatedAsHexAndCompared()
    {
        // GitHub always sends "sha256=<hex>" but defensively the
        // verifier accepts a bare hex too.
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var hexOnly = Convert.ToHexString(hmac.ComputeHash(BodyBytes)).ToLowerInvariant();

        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers(("X-Hub-Signature-256", hexOnly)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public async Task VerifyAsync_NonHexSignature_ReturnsBadSignature()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var result = await verifier.VerifyAsync(
            BodyBytes, Secret,
            Headers(("X-Hub-Signature-256", "sha256=this-is-not-hex-data-just-text-no-good")));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }

    [Test]
    public async Task VerifyAsync_Forgejo_FallbackToGiteaHeader_Works()
    {
        // Forgejo derives from Gitea and historically sends
        // X-Gitea-Signature; the new X-Forgejo-Signature is preferred
        // when present but fallback must work.
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.Forgejo,
            primaryHeader: "X-Forgejo-Signature",
            fallbackHeader: "X-Gitea-Signature");

        var sig = ComputeSig(BodyBytes, Secret);
        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers(("X-Gitea-Signature", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public async Task VerifyAsync_Forgejo_PrefersForgejoHeader()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.Forgejo,
            primaryHeader: "X-Forgejo-Signature",
            fallbackHeader: "X-Gitea-Signature");

        var sig = ComputeSig(BodyBytes, Secret);
        // X-Gitea-Signature deliberately bogus to assert primary wins
        var result = await verifier.VerifyAsync(
            BodyBytes, Secret,
            Headers(
                ("X-Forgejo-Signature", sig),
                ("X-Gitea-Signature", "sha256=" + new string('0', 64))));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public async Task VerifyAsync_Gitea_HappyPath()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.Gitea, "X-Gitea-Signature");

        var sig = ComputeSig(BodyBytes, Secret);
        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers(("X-Gitea-Signature", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public void Kind_ReturnsConstructorValue()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.Gitea, "X-Gitea-Signature");
        verifier.Kind.Should().Be(PlatformKind.Gitea);
    }

    [Test]
    public async Task VerifyAsync_DifferentBody_FailsVerification()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        // Sign body A, send body B
        var sig = ComputeSig(BodyBytes, Secret);
        var differentBody = Encoding.UTF8.GetBytes("""{"action":"created","installation":{"id":99}}""");

        var result = await verifier.VerifyAsync(
            differentBody, Secret, Headers(("X-Hub-Signature-256", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }

    [Test]
    public async Task VerifyAsync_DifferentSecret_FailsVerification()
    {
        var verifier = new HmacWebhookSignatureVerifier(
            PlatformKind.GitHub, "X-Hub-Signature-256");

        var sig = ComputeSig(BodyBytes, "the-attackers-guess");

        var result = await verifier.VerifyAsync(
            BodyBytes, Secret, Headers(("X-Hub-Signature-256", sig)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }
}
