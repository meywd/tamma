using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.Gitea.Tests;

[TestFixture]
public class GiteaWebhookSignatureVerifierTests
{
    private static string ComputeSignature(string secret, ReadOnlySpan<byte> body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body.ToArray());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.AppendFormat("{0:x2}", b);
        return sb.ToString();
    }

    [Test]
    public void Verify_AcceptsValidSignature()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var body = Encoding.UTF8.GetBytes("{\"event\":\"push\"}");
        var secret = "super-secret-token";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Gitea-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Verify_AcceptsForgejoHeader_WhenConfigured()
    {
        var verifier = new GiteaWebhookSignatureVerifier(
            GiteaWebhookSignatureVerifier.GiteaAndForgejoHeaderNames);
        var body = Encoding.UTF8.GetBytes("payload");
        var secret = "s";
        var sig = ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Forgejo-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Verify_StripSha256Prefix()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var body = Encoding.UTF8.GetBytes("xyz");
        var secret = "s";
        var sig = "sha256=" + ComputeSignature(secret, body);

        var result = verifier.Verify(body, secret,
            name => name == "X-Gitea-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }

    [Test]
    public void Verify_RejectsMismatchedSignature()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var body = Encoding.UTF8.GetBytes("payload");

        var result = verifier.Verify(body, "real-secret",
            name => name == "X-Gitea-Signature"
                ? new string('a', 64) // bogus 64-char hex
                : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.SignatureMismatch);
    }

    [Test]
    public void Verify_RejectsMissingHeader()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var result = verifier.Verify(
            Encoding.UTF8.GetBytes("x"), "secret", _ => null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.MissingHeader);
    }

    [Test]
    public void Verify_FailsClosedOnMissingSecret()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var result = verifier.Verify(
            Encoding.UTF8.GetBytes("x"), secret: null,
            name => "anything");

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.MissingSecret);
    }

    [Test]
    public void Verify_FailsClosedOnEmptySecret()
    {
        var verifier = new GiteaWebhookSignatureVerifier();
        var result = verifier.Verify(
            Encoding.UTF8.GetBytes("x"), secret: "",
            name => "value");

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.MissingSecret);
    }
}
