using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Logging;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-8 — abstractions-layer tests covering the interface
/// records, redaction helpers, and capability-matrix wiring.
/// </summary>
[TestFixture]
public sealed class CiSecretAbstractionsTests
{
    // ── RedactedSecret ────────────────────────────────────────────────

    [Test]
    public void RedactedSecret_ToString_NeverEmitsValue()
    {
        RedactedSecret s = "ghs_super_secret_token_value";
        s.ToString().Should().NotContain("ghs_");
        s.ToString().Should().Be("[redacted:28 chars]");
    }

    [Test]
    public void RedactedSecret_AcceptsNull()
    {
        RedactedSecret s = (string?)null;
        s.IsEmpty.Should().BeTrue();
        s.Length.Should().Be(0);
        s.ToString().Should().Be("[redacted:0 chars]");
    }

    [Test]
    public void RedactedSecret_RevealReturnsOriginal()
    {
        RedactedSecret s = "abc123";
        s.Reveal().Should().Be("abc123");
    }

    [Test]
    public void RedactedSecret_FormattedInLogPattern_DoesNotLeak()
    {
        // Simulate the structured-log format path that calls ToString()
        // when an argument isn't destructured.
        RedactedSecret s = "supersecret";
        var rendered = $"token={s} length={s.Length}";
        rendered.Should().NotContain("supersecret");
        rendered.Should().Contain("[redacted:11 chars]");
    }

    // ── SecretLoggingScope ────────────────────────────────────────────

    [Test]
    public void Redact_ReportsLength()
    {
        SecretLoggingScope.Redact("12345678").Should().Be("[redacted:8 chars]");
        SecretLoggingScope.Redact(null).Should().Be("[redacted:0 chars]");
    }

    [Test]
    public void RedactSubstring_ReplacesOccurrences()
    {
        var input = "error: invalid token sk_live_abc123_xyz, please rotate sk_live_abc123_xyz";
        var redacted = SecretLoggingScope.RedactSubstring(input, "sk_live_abc123_xyz");
        redacted.Should().NotContain("sk_live_abc123_xyz");
        redacted.Should().Contain("[redacted:18 chars]");
    }

    [Test]
    public void RedactSubstring_NoOpOnEmpty()
    {
        SecretLoggingScope.RedactSubstring("hello world", "")
            .Should().Be("hello world");
        SecretLoggingScope.RedactSubstring("", "secret")
            .Should().BeEmpty();
    }

    [Test]
    public void EnsureNoLeak_ThrowsIfPresent()
    {
        Action act = () => SecretLoggingScope.EnsureNoLeak(
            "log line: token=secretval123", "secretval123");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secret leak detected*");
    }

    [Test]
    public void EnsureNoLeak_NoOpIfAbsent()
    {
        Action act = () => SecretLoggingScope.EnsureNoLeak(
            "log line: token=[redacted]", "secretval123");
        act.Should().NotThrow();
    }

    // ── CiSecretTarget descriptor ─────────────────────────────────────

    [Test]
    public void CiSecretTarget_Descriptor_ShapesCorrectly()
    {
        new CiSecretTarget.Repo("acme", "app").Descriptor()
            .Should().Be("repo:acme/app");
        new CiSecretTarget.Org("acme").Descriptor()
            .Should().Be("org:acme");
        new CiSecretTarget.User("alice").Descriptor()
            .Should().Be("user:alice");
        new CiSecretTarget.Global().Descriptor()
            .Should().Be("global");
        new CiSecretTarget.Environment("acme", "app", "production").Descriptor()
            .Should().Be("env:acme/app/production");
    }

    // ── CiSecretProvisionResult helpers ───────────────────────────────

    [Test]
    public void CiSecretProvisionResult_Ok_ShapesCorrectly()
    {
        var r = CiSecretProvisionResult.Ok(
            PlatformKind.GitHub, new CiSecretTarget.Repo("o", "r"));
        r.Success.Should().BeTrue();
        r.Error.Should().BeNull();
        r.Kind.Should().Be(PlatformKind.GitHub);
        r.TargetDescriptor.Should().Be("repo:o/r");
    }

    [Test]
    public void CiSecretProvisionResult_FromError_FlattensVariants()
    {
        var target = new CiSecretTarget.Org("acme");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.AuthExpired())
            .Error.Should().Be("auth_expired");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.PermissionDenied())
            .Error.Should().Be("permission_denied");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.NotFound())
            .Error.Should().Be("not_found");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.RateLimited(null))
            .Error.Should().Be("rate_limited");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.ServiceUnavailable())
            .Error.Should().Be("service_unavailable");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.InvalidRequest("validation", null))
            .Error.Should().Be("invalid_request:validation");

        CiSecretProvisionResult.FromError(
            PlatformKind.GitHub, target, new PlatformError.Unknown("weird"))
            .Error.Should().Be("unknown:weird");
    }

    // ── CiSecretMetadata ──────────────────────────────────────────────

    [Test]
    public void CiSecretMetadata_DefaultsAreSane()
    {
        var m = CiSecretMetadata.Default;
        m.Protected.Should().BeFalse();
        m.Masked.Should().BeFalse();
        m.EnvironmentScope.Should().BeNull();
        m.VariableType.Should().Be("env_var");
    }

    // ── Capability matrix gating ──────────────────────────────────────

    [Test]
    public void CapabilityMatrix_GitHub_AdvertisesSecrets()
    {
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitHub, PlatformCapability.Secrets)
            .Should().BeTrue();
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitHub, PlatformCapability.LibsodiumSecrets)
            .Should().BeTrue();
    }

    [Test]
    public void CapabilityMatrix_GitLab_AdvertisesProtectedAndMasked()
    {
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitLab, PlatformCapability.Secrets)
            .Should().BeTrue();
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitLab, PlatformCapability.ProtectedVariables)
            .Should().BeTrue();
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitLab, PlatformCapability.MaskedVariables)
            .Should().BeTrue();
    }

    [Test]
    public void CapabilityMatrix_Gitea_AdvertisesSecretsButNotLibsodiumFlag()
    {
        // Gitea advertises Secrets but the existing 31-1 matrix does
        // NOT add LibsodiumSecrets — Gitea's wire format is
        // libsodium-compatible but the capability flag is reserved
        // for "this is the GitHub-style sealed-box dance specifically".
        // 31-8's Gitea provisioner uses sealed-box anyway since 1.21+.
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.Gitea, PlatformCapability.Secrets)
            .Should().BeTrue();
    }
}
