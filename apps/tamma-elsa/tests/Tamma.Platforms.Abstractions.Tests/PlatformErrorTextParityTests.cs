using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Epic 31 P2 — pins the legacy status-prefixed wire strings
/// <see cref="PlatformErrorText"/> projects a <see cref="PlatformError"/> into.
/// These strings are LOAD-BEARING: mediation's <c>ParsePlatformStatus</c> reads
/// the leading numeric prefix into <c>platformStatusCode</c>, and the ADL
/// cores' <c>ClassifyError</c> helpers substring-match the status tokens — the
/// P2 swap is behavior-identical only while these projections hold.
/// </summary>
[TestFixture]
public class PlatformErrorTextParityTests
{
    [Test]
    public void VariantProjections_ArePinned()
    {
        PlatformErrorText.ToLegacyString(new PlatformError.AuthExpired())
            .Should().StartWith("401:");
        PlatformErrorText.ToLegacyString(new PlatformError.PermissionDenied())
            .Should().StartWith("403:");
        PlatformErrorText.ToLegacyString(new PlatformError.NotFound())
            .Should().StartWith("404:");
        PlatformErrorText.ToLegacyString(new PlatformError.RateLimited(TimeSpan.FromSeconds(5)))
            .Should().StartWith("429:").And.Contain("rate limit");
        PlatformErrorText.ToLegacyString(new PlatformError.ServiceUnavailable())
            .Should().StartWith("503:").And.Contain("unavailable");
        PlatformErrorText.ToLegacyString(new PlatformError.Unknown("boom"))
            .Should().Be("boom");
    }

    [Test]
    public void InvalidRequest_KnownDriverCodes_MapBackToTheirHttpIdentity()
    {
        // The GitHubErrorMapper codes → the status prefix the live path carried,
        // so downstream Contains()-classifiers land in the same coarse class.
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("not_mergeable", "Pull Request is not mergeable"))
            .Should().Be("405: Pull Request is not mergeable");
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("merge_conflict", "merge conflict"))
            .Should().Be("409: merge conflict");
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("conflict", null))
            .Should().Be("409: conflict");
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("already_exists", "Reference already exists"))
            .Should().Be("422: Reference already exists");
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("validation_failed", "Validation Failed"))
            .Should().Be("422: Validation Failed");
        // Numeric codes (the mapper's "other 4xx" arm) keep their own status.
        PlatformErrorText.ToLegacyString(new PlatformError.InvalidRequest("400", "bad request"))
            .Should().Be("400: bad request");
    }

    [Test]
    public void CapabilityUnsupported_KeepsItsExactCodeAsTheHeadToken_NoFakeStatus()
    {
        var text = PlatformErrorText.ToLegacyString(
            new PlatformError.InvalidRequest("capability_unsupported", "platform cannot do this"));
        text.Should().StartWith("capability_unsupported:");
        // No numeric prefix → mediation's ParsePlatformStatus yields null,
        // and the FIRST-CLASS failureCode carries the classification instead.
        int.TryParse(text.Split(':')[0], out _).Should().BeFalse();
    }

    [Test]
    public void IsCapabilityUnsupported_IsExactMatchOnly()
    {
        PlatformErrorText.IsCapabilityUnsupported(
            new PlatformError.InvalidRequest("capability_unsupported", null)).Should().BeTrue();
        PlatformErrorText.IsCapabilityUnsupported(
            new PlatformError.InvalidRequest("CAPABILITY_UNSUPPORTED", null)).Should().BeFalse(
            "exact ordinal match — a lookalike must classify as a real failure");
        PlatformErrorText.IsCapabilityUnsupported(
            new PlatformError.InvalidRequest("merge_conflict", null)).Should().BeFalse();
        PlatformErrorText.IsCapabilityUnsupported(new PlatformError.NotFound()).Should().BeFalse();
    }
}
