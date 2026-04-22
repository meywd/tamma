using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class ApiKeyPrefixParserTests
{
    [Test]
    public void TryParse_NullOrEmpty_ReturnsFalse()
    {
        ApiKeyPrefixParser.TryParse(null, out var p1).Should().BeFalse();
        p1.Should().BeNull();
        ApiKeyPrefixParser.TryParse(string.Empty, out var p2).Should().BeFalse();
        p2.Should().BeNull();
    }

    [Test]
    public void TryParse_TokenWithoutBanner_ReturnsFalse()
    {
        // No "tamma_sk_" — this is not an API key at all (probably a JWT).
        ApiKeyPrefixParser.TryParse("eyJhbGciOiJIUzI1NiJ9.something", out var parsed)
            .Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Test]
    public void TryParse_LegacyUnprefixed_ReturnsLegacyScope()
    {
        // "tamma_sk_<random>" with no scope letter → legacy fallback.
        var rawKey = "tamma_sk_abc123def456";
        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Legacy);
        parsed.TenantId.Should().BeNull();
        parsed.IsLegacy.Should().BeTrue();
    }

    [Test]
    public void TryParse_PlatformPrefix_ReturnsPlatformScope()
    {
        var rawKey = "tamma_sk_pl_abc123def";
        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Platform);
        parsed.TenantId.Should().BeNull();
    }

    [Test]
    public void TryParse_PlatformPrefixWithEmptyBody_Rejects()
    {
        // "tamma_sk_pl_" with no random body — junk input.
        ApiKeyPrefixParser.TryParse("tamma_sk_pl_", out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Test]
    public void TryParse_UserPrefix_ReturnsUserScope()
    {
        var rawKey = "tamma_sk_u_abc123def";
        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.User);
        parsed.TenantId.Should().BeNull();
    }

    [Test]
    public void TryParse_UserPrefixWithEmptyBody_Rejects()
    {
        ApiKeyPrefixParser.TryParse("tamma_sk_u_", out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Test]
    public void TryParse_TenantPrefix_DecodesEmbeddedTenantId()
    {
        var tid = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var encodedTid = Base32.Encode(tid.ToByteArray());
        var rawKey = $"tamma_sk_t_{encodedTid}_random_body_here";

        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Tenant);
        parsed.TenantId.Should().Be(tid);
    }

    [Test]
    public void TryParse_TenantPrefixWithMissingBody_Rejects()
    {
        // No second underscore after the tenant id segment.
        var encodedTid = Base32.Encode(Guid.NewGuid().ToByteArray());
        ApiKeyPrefixParser.TryParse($"tamma_sk_t_{encodedTid}", out var parsed)
            .Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Test]
    public void TryParse_TenantPrefixWithMalformedTenantSegment_ReturnsUnknownScope()
    {
        // Banner+marker correct, but the tenant segment has invalid base32
        // characters. Returns scope=Unknown so the handler can 401 without
        // leaking that the prefix shape "almost" matched.
        var rawKey = "tamma_sk_t_NOT-VALID-BASE32_random";
        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Unknown);
        parsed.TenantId.Should().BeNull();
    }

    [Test]
    public void TryParse_TenantPrefixWithEmptyBodyAfterTenant_Rejects()
    {
        var encodedTid = Base32.Encode(Guid.NewGuid().ToByteArray());
        // Trailing underscore with no random body — malformed.
        ApiKeyPrefixParser.TryParse($"tamma_sk_t_{encodedTid}_", out var parsed)
            .Should().BeFalse();
    }

    [Test]
    public void TryParse_TenantSegmentIsCaseInsensitive()
    {
        // base32 decoder accepts lowercase; verify the parser passes
        // lowercase tenant ids through to the same Guid.
        var tid = Guid.NewGuid();
        var upper = Base32.Encode(tid.ToByteArray());
        var lower = upper.ToLowerInvariant();
        var rawKey = $"tamma_sk_t_{lower}_random";

        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.TenantId.Should().Be(tid);
    }

    [Test]
    public void TryParse_FutureScopeMarker_FallsThroughToLegacy()
    {
        // A future-but-undeclared marker like "tamma_sk_x_<rand>" parses as
        // a legacy key — the actual rejection happens in the auth handler
        // via the legacy-fallback flag (or hash miss). This keeps the
        // parser permissive; the security gate is downstream.
        var rawKey = "tamma_sk_x_some_random_thing";
        ApiKeyPrefixParser.TryParse(rawKey, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Legacy);
    }

    [Test]
    public void SafeDisplayPrefix_NeverEchoesTenantSegment()
    {
        var tid = Guid.NewGuid();
        var key = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var display = ApiKeyPrefixParser.SafeDisplayPrefix(key);

        // 12-char display rule from ApiKeyHasher.Prefix.
        display.Length.Should().BeLessOrEqualTo(12);
        // Just enough to disambiguate; doesn't include the whole tenant id.
        display.Should().StartWith("tamma_sk_t_");
        // We never include the full tenant segment — the encoded tenant
        // segment is 26 chars by itself.
        display.Should().NotContain(Base32.Encode(tid.ToByteArray()));
    }

    [Test]
    public void SafeDisplayPrefix_OnEmptyInput_ReturnsEmpty()
    {
        ApiKeyPrefixParser.SafeDisplayPrefix(string.Empty).Should().Be(string.Empty);
    }
}
