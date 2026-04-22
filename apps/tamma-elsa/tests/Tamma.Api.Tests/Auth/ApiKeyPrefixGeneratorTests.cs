using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class ApiKeyPrefixGeneratorTests
{
    [Test]
    public void GenerateTenantKey_StartsWithTenantBanner()
    {
        var tid = Guid.NewGuid();
        var key = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        key.Should().StartWith("tamma_sk_t_");
    }

    [Test]
    public void GeneratePlatformKey_StartsWithPlatformBanner()
    {
        var key = ApiKeyPrefixGenerator.GeneratePlatformKey();
        key.Should().StartWith("tamma_sk_pl_");
    }

    [Test]
    public void GenerateUserKey_StartsWithUserBanner()
    {
        var key = ApiKeyPrefixGenerator.GenerateUserKey();
        key.Should().StartWith("tamma_sk_u_");
    }

    [Test]
    public void GenerateTenantKey_RoundTripsThroughParser()
    {
        var tid = Guid.NewGuid();
        var key = ApiKeyPrefixGenerator.GenerateTenantKey(tid);

        ApiKeyPrefixParser.TryParse(key, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Tenant);
        parsed.TenantId.Should().Be(tid);
        parsed.RawKey.Should().Be(key);
    }

    [Test]
    public void GeneratePlatformKey_RoundTripsThroughParser()
    {
        var key = ApiKeyPrefixGenerator.GeneratePlatformKey();
        ApiKeyPrefixParser.TryParse(key, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.Platform);
        parsed.TenantId.Should().BeNull();
    }

    [Test]
    public void GenerateUserKey_RoundTripsThroughParser()
    {
        var key = ApiKeyPrefixGenerator.GenerateUserKey();
        ApiKeyPrefixParser.TryParse(key, out var parsed).Should().BeTrue();
        parsed!.Scope.Should().Be(ApiKeyScope.User);
        parsed.TenantId.Should().BeNull();
    }

    [Test]
    public void GenerateTenantKey_DifferentTenantsProduceDifferentEmbeddedSegments()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var k1 = ApiKeyPrefixGenerator.GenerateTenantKey(t1);
        var k2 = ApiKeyPrefixGenerator.GenerateTenantKey(t2);

        // Strip the random suffix; just compare the t1-vs-t2 segments.
        var seg1 = k1["tamma_sk_t_".Length..].Split('_')[0];
        var seg2 = k2["tamma_sk_t_".Length..].Split('_')[0];
        seg1.Should().NotBe(seg2);
    }

    [Test]
    public void GenerateTenantKey_TwoCallsForSameTenantHaveDifferentSuffixes()
    {
        var tid = Guid.NewGuid();
        var k1 = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        var k2 = ApiKeyPrefixGenerator.GenerateTenantKey(tid);
        k1.Should().NotBe(k2, "the random suffix must differ across calls");
    }

    [Test]
    public void Reserved_x_and_s_MarkersDocumentedForFutureScopes()
    {
        ApiKeyPrefixGenerator.ReservedMarkers.Should().Contain("x_");
        ApiKeyPrefixGenerator.ReservedMarkers.Should().Contain("s_");
    }

    [Test]
    public void GeneratedKeys_AreUnderHttpHeaderLimit()
    {
        // Wire-length sanity check (brief AC1 — well under 8 KiB).
        var k1 = ApiKeyPrefixGenerator.GenerateTenantKey(Guid.NewGuid());
        var k2 = ApiKeyPrefixGenerator.GeneratePlatformKey();
        var k3 = ApiKeyPrefixGenerator.GenerateUserKey();
        k1.Length.Should().BeLessThan(200);
        k2.Length.Should().BeLessThan(200);
        k3.Length.Should().BeLessThan(200);
    }
}
