using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.Auth;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// PF-S6 — pin the trusted-proxy resolver contract:
/// <list type="bullet">
///   <item><description>No trusted CIDR configured → XFF is ignored,
///     actor IP = socket peer.</description></item>
///   <item><description>Origin in trusted CIDR + XFF present → leftmost
///     untrusted hop wins.</description></item>
///   <item><description>Origin in trusted CIDR + entire chain trusted →
///     leftmost element returned (best-effort).</description></item>
///   <item><description>Origin NOT in trusted CIDR → XFF ignored even
///     when present.</description></item>
///   <item><description>Malformed XFF entries don't crash the resolver.</description></item>
///   <item><description>Configuration round-trip: bind via
///     <c>Tamma:TrustedProxies:Cidrs</c>.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class TrustedProxyResolverTests
{
    private static HttpContext MakeContext(string? remote, string? xff)
    {
        var ctx = new DefaultHttpContext();
        if (remote is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (xff is not null)
            ctx.Request.Headers["X-Forwarded-For"] = xff;
        return ctx;
    }

    [Test]
    public void Constructor_FromConfiguration_BindsCidrList()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:TrustedProxies:Cidrs:0"] = "10.0.0.0/8",
                ["Tamma:TrustedProxies:Cidrs:1"] = "172.16.0.0/12",
            }).Build();

        var resolver = new TrustedProxyResolver(config);
        resolver.HasAnyTrustedProxy.Should().BeTrue();
        resolver.IsTrustedProxy(IPAddress.Parse("10.5.5.5")).Should().BeTrue();
        resolver.IsTrustedProxy(IPAddress.Parse("172.20.1.1")).Should().BeTrue();
        resolver.IsTrustedProxy(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
    }

    [Test]
    public void Constructor_EmptyConfiguration_DefaultsToTrustNothing()
    {
        var config = new ConfigurationBuilder().Build();
        var resolver = new TrustedProxyResolver(config);
        resolver.HasAnyTrustedProxy.Should().BeFalse();
        resolver.IsTrustedProxy(IPAddress.Parse("10.0.0.1")).Should().BeFalse();
    }

    [Test]
    public void Constructor_InvalidCidr_IsSkipped()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:TrustedProxies:Cidrs:0"] = "not-a-cidr",
                ["Tamma:TrustedProxies:Cidrs:1"] = "10.0.0.0/8",
                ["Tamma:TrustedProxies:Cidrs:2"] = "10.0.0.0/99", // invalid prefix
            }).Build();

        var resolver = new TrustedProxyResolver(config);
        resolver.HasAnyTrustedProxy.Should().BeTrue();
        resolver.IsTrustedProxy(IPAddress.Parse("10.0.0.1")).Should().BeTrue();
    }

    [Test]
    public void ResolveActorIp_NoTrustedProxy_IgnoresXff()
    {
        var resolver = new TrustedProxyResolver(Array.Empty<string>());
        var ctx = MakeContext("203.0.113.99", "198.51.100.42");
        resolver.ResolveActorIp(ctx).Should().Be("203.0.113.99",
            "no CIDR configured = trust nothing; XFF must be ignored");
    }

    [Test]
    public void ResolveActorIp_TrustedOrigin_HonoursLeftmostUntrustedXff()
    {
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext("10.0.0.5", "198.51.100.42, 10.0.0.99");
        resolver.ResolveActorIp(ctx).Should().Be("198.51.100.42");
    }

    [Test]
    public void ResolveActorIp_UntrustedOrigin_IgnoresXff()
    {
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext("203.0.113.99", "10.0.0.99");
        resolver.ResolveActorIp(ctx).Should().Be("203.0.113.99",
            "origin outside the trusted ring → XFF must be ignored");
    }

    [Test]
    public void ResolveActorIp_MultiHopChain_StopsAtFirstUntrustedFromRight()
    {
        var resolver = new TrustedProxyResolver(
            new[] { "10.0.0.0/8", "172.16.0.0/12" });
        var ctx = MakeContext("10.0.0.5",
            "198.51.100.42, 172.16.4.7, 10.0.0.99");
        resolver.ResolveActorIp(ctx).Should().Be("198.51.100.42",
            "walk right-to-left through trusted hops; first untrusted is the client");
    }

    [Test]
    public void ResolveActorIp_AllHopsTrusted_FallsBackToLeftmost()
    {
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext("10.0.0.5", "10.0.0.50, 10.0.0.99");
        resolver.ResolveActorIp(ctx).Should().Be("10.0.0.50",
            "if every hop is trusted we still emit a candidate originator");
    }

    [Test]
    public void ResolveActorIp_TrustedOrigin_NoXffHeader_FallsBackToSocket()
    {
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext("10.0.0.5", xff: null);
        resolver.ResolveActorIp(ctx).Should().Be("10.0.0.5");
    }

    [Test]
    public void ResolveActorIp_TrustedOrigin_EmptyXff_FallsBackToSocket()
    {
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext("10.0.0.5", xff: "   ");
        resolver.ResolveActorIp(ctx).Should().Be("10.0.0.5");
    }

    [Test]
    public void ResolveActorIp_BareIpInCidrList_TreatsAsHostMask()
    {
        // Operators should be allowed to write a bare host without /32.
        var resolver = new TrustedProxyResolver(new[] { "192.168.1.10" });
        resolver.IsTrustedProxy(IPAddress.Parse("192.168.1.10")).Should().BeTrue();
        resolver.IsTrustedProxy(IPAddress.Parse("192.168.1.11")).Should().BeFalse();
    }

    [Test]
    public void ResolveActorIp_IPv6_TrustedOrigin_HonoursXff()
    {
        var resolver = new TrustedProxyResolver(new[] { "fd00::/8" });
        var ctx = MakeContext("fd00::1", "2001:db8::abcd, fd00::99");
        var resolved = resolver.ResolveActorIp(ctx);
        resolved.Should().Be("2001:db8::abcd");
    }

    [Test]
    public void ResolveActorIp_NoSocketPeer_NoTrustedProxies_ReturnsLeftmostXff()
    {
        // Test-context shape: the harness builds a DefaultHttpContext
        // without a connection. With no trusted proxies configured we
        // can fall back to the leftmost XFF entry as a best-effort
        // identity for unit tests.
        var resolver = new TrustedProxyResolver(Array.Empty<string>());
        var ctx = MakeContext(remote: null, xff: "198.51.100.42");
        resolver.ResolveActorIp(ctx).Should().Be("198.51.100.42");
    }

    [Test]
    public void ResolveActorIp_NoSocketPeer_WithTrustedProxies_ReturnsNull()
    {
        // Belt-and-braces: when trusted-proxies ARE configured we must
        // refuse to honour XFF without an origin to validate against.
        var resolver = new TrustedProxyResolver(new[] { "10.0.0.0/8" });
        var ctx = MakeContext(remote: null, xff: "198.51.100.42");
        resolver.ResolveActorIp(ctx).Should().BeNull();
    }
}
