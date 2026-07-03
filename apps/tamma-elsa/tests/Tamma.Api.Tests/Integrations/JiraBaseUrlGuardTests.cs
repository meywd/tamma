using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// SSRF guard for the per-tenant JIRA <c>baseUrl</c> + <c>ticketId</c>. Pins the
/// hard floor: https-only, private/loopback/link-local/metadata addresses rejected
/// (literal AND DNS-resolved), an optional host allowlist, and a path-safe ticket id.
/// </summary>
[TestFixture]
public class JiraBaseUrlGuardTests
{
    private static Func<string, CancellationToken, Task<IPAddress[]>> Dns(params string[] ips) =>
        (_, _) => Task.FromResult(ips.Select(IPAddress.Parse).ToArray());

    private static Func<string, CancellationToken, Task<IPAddress[]>> DnsThrows() =>
        (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("nxdomain"));

    // ── scheme ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Http_Scheme_Rejected()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("http://acme.atlassian.net", dnsResolve: Dns("93.184.215.14"));
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("invalid_base_url");
    }

    [Test]
    public async Task NotAUrl_Rejected()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("not-a-url");
        r.IsValid.Should().BeFalse();
    }

    // ── literal private / metadata IPs (no DNS) ───────────────────────────────

    [TestCase("https://169.254.169.254/latest/meta-data")]  // cloud metadata
    [TestCase("https://127.0.0.1")]                          // loopback
    [TestCase("https://10.0.0.5")]                           // 10/8
    [TestCase("https://172.16.4.4")]                         // 172.16/12
    [TestCase("https://192.168.1.1")]                        // 192.168/16
    [TestCase("https://0.0.0.0")]                            // any
    [TestCase("https://[::1]")]                              // IPv6 loopback
    [TestCase("https://[fe80::1]")]                          // IPv6 link-local
    [TestCase("https://[fc00::1]")]                          // IPv6 unique-local
    public async Task LiteralPrivateOrMetadataHost_Rejected(string url)
    {
        var r = await JiraBaseUrlGuard.ValidateAsync(url);
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("host_not_allowed");
    }

    [Test]
    public async Task LiteralPublicIp_Allowed()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("https://93.184.215.14");
        r.IsValid.Should().BeTrue();
    }

    // ── named host resolved via DNS ───────────────────────────────────────────

    [Test]
    public async Task NamedHost_ResolvingToPrivate_Rejected()
    {
        // Attacker points a public-looking name at an internal address.
        var r = await JiraBaseUrlGuard.ValidateAsync("https://evil.example.com", dnsResolve: Dns("10.1.2.3"));
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("host_not_allowed");
    }

    [Test]
    public async Task NamedHost_ResolvingToAnyPrivateAmongPublic_Rejected()
    {
        // A partially-private result (DNS-rebinding style) is treated as hostile.
        var r = await JiraBaseUrlGuard.ValidateAsync("https://mixed.example.com",
            dnsResolve: Dns("93.184.215.14", "169.254.169.254"));
        r.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task NamedHost_ResolvingToPublic_Allowed()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("https://acme.atlassian.net", dnsResolve: Dns("104.192.142.1"));
        r.IsValid.Should().BeTrue();
        r.Uri!.Host.Should().Be("acme.atlassian.net");
    }

    [Test]
    public async Task NamedHost_Unresolvable_PassesDnsFloor()
    {
        // Not resolvable here — the scheme + connect-time guard still apply.
        var r = await JiraBaseUrlGuard.ValidateAsync("https://jira.example.com", dnsResolve: DnsThrows());
        r.IsValid.Should().BeTrue();
    }

    // ── allowlist ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Allowlist_Match_Allowed_WithoutDns()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("https://acme.atlassian.net",
            allowedHostSuffixes: new[] { ".atlassian.net" }, dnsResolve: DnsThrows());
        r.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Allowlist_NoMatch_Rejected()
    {
        var r = await JiraBaseUrlGuard.ValidateAsync("https://acme.example.com",
            allowedHostSuffixes: new[] { ".atlassian.net" }, dnsResolve: Dns("93.184.215.14"));
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("host_not_allowed");
    }

    [Test]
    public async Task Allowlist_SuffixSpoofing_Rejected()
    {
        // "evilatlassian.net" must NOT match the ".atlassian.net" suffix.
        var r = await JiraBaseUrlGuard.ValidateAsync("https://evilatlassian.net",
            allowedHostSuffixes: new[] { ".atlassian.net" }, dnsResolve: Dns("93.184.215.14"));
        r.IsValid.Should().BeFalse();
    }

    // ── ticket id ─────────────────────────────────────────────────────────────

    [TestCase("PROJ-42", true)]
    [TestCase("ABC-1", true)]
    [TestCase("10042", true)]
    [TestCase("../../etc/passwd", false)]
    [TestCase("PROJ/42", false)]
    [TestCase("..", false)]
    [TestCase("PROJ 42", false)]
    [TestCase("", false)]
    [TestCase("PROJ%2F..", false)]
    public void TicketId_Validation(string ticketId, bool expected)
    {
        JiraBaseUrlGuard.IsValidTicketId(ticketId).Should().Be(expected);
    }

    // ── blocked-address unit (also drives the ConnectCallback) ────────────────

    [TestCase("169.254.169.254", true)]
    [TestCase("127.0.0.1", true)]
    [TestCase("10.255.255.255", true)]
    [TestCase("172.31.0.1", true)]
    [TestCase("172.32.0.1", false)]   // just outside 172.16/12
    [TestCase("192.168.0.1", true)]
    [TestCase("8.8.8.8", false)]
    [TestCase("::1", true)]
    [TestCase("::ffff:10.0.0.1", true)] // IPv4-mapped private
    public void IsBlockedAddress(string ip, bool blocked)
    {
        JiraBaseUrlGuard.IsBlockedAddress(IPAddress.Parse(ip)).Should().Be(blocked);
    }
}
