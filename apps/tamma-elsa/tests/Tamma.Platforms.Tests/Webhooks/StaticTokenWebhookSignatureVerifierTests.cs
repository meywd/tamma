using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Webhooks;

namespace Tamma.Platforms.Tests.Webhooks;

/// <summary>
/// Story 31-7 — unit coverage for the static-token verifier (the
/// GitLab default path).
/// </summary>
[TestFixture]
public class StaticTokenWebhookSignatureVerifierTests
{
    private const string Secret = "gitlab-secret-shared-with-tamma";

    private static Func<string, string?> Headers(params (string Name, string? Value)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return name => dict.TryGetValue(name, out var v) ? v : null;
    }

    [Test]
    public async Task VerifyAsync_GitLab_ValidToken_ReturnsOk()
    {
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");

        var result = await verifier.VerifyAsync(
            ReadOnlyMemory<byte>.Empty, Secret,
            Headers(("X-Gitlab-Token", Secret)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.Ok);
    }

    [Test]
    public async Task VerifyAsync_GitLab_BadToken_ReturnsBadSignature()
    {
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");

        var result = await verifier.VerifyAsync(
            ReadOnlyMemory<byte>.Empty, Secret,
            Headers(("X-Gitlab-Token", "definitely-not-the-secret")));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }

    [Test]
    public async Task VerifyAsync_GitLab_MissingHeader_ReturnsMissingHeader()
    {
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");

        var result = await verifier.VerifyAsync(
            ReadOnlyMemory<byte>.Empty, Secret, Headers());

        result.Outcome.Should().Be(WebhookVerificationOutcome.MissingHeader);
    }

    [Test]
    public async Task VerifyAsync_GitLab_NullSecret_ReturnsSecretNotConfigured()
    {
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");

        var result = await verifier.VerifyAsync(
            ReadOnlyMemory<byte>.Empty, secret: null,
            Headers(("X-Gitlab-Token", Secret)));

        result.Outcome.Should().Be(WebhookVerificationOutcome.SecretNotConfigured);
    }

    [Test]
    public async Task VerifyAsync_GitLab_TokenLengthDifferent_StillCompared()
    {
        // FixedTimeEquals returns false on length mismatch — verify
        // that path returns BadSignature and not e.g. an exception.
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");

        var result = await verifier.VerifyAsync(
            ReadOnlyMemory<byte>.Empty, Secret,
            Headers(("X-Gitlab-Token", "x")));

        result.Outcome.Should().Be(WebhookVerificationOutcome.BadSignature);
    }

    [Test]
    public void Kind_ReturnsConstructorValue()
    {
        var verifier = new StaticTokenWebhookSignatureVerifier(
            PlatformKind.GitLab, "X-Gitlab-Token");
        verifier.Kind.Should().Be(PlatformKind.GitLab);
    }
}
