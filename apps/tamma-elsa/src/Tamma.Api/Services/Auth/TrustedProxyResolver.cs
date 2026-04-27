using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Auth;

/// <summary>
/// Story 28-R2 / PF-S6 — resolves the actor IP for an inbound request,
/// honouring the <c>X-Forwarded-For</c> header ONLY when the immediate
/// peer (<c>HttpContext.Connection.RemoteIpAddress</c>) sits inside an
/// operator-configured trusted-proxy CIDR list. Untrusted origins (i.e.
/// any IP that is NOT in the allowlist — including the public internet)
/// fall straight through to the socket peer address; their
/// <c>X-Forwarded-For</c> header is ignored.
///
/// <para><b>Why this matters</b>: <see cref="AuthEndpoints"/> writes
/// <c>actorIp</c> into the <c>USER.LOGOUT_ALL.SUCCESS</c> and
/// <c>USER.ORG_SWITCHED.SUCCESS</c> audit events. Trusting an
/// unvalidated <c>X-Forwarded-For</c> header lets an internet-facing
/// attacker poison the audit log with a forged source IP — see PF-S6 in
/// <c>docs/review/epic-28-round2-postfix-review-2026-04-26.md</c>.</para>
///
/// <para><b>Configuration</b>: bind <c>Tamma:TrustedProxies:Cidrs</c>
/// (string array of CIDR blocks) at startup. The default empty list
/// means "trust nothing" — every request gets the socket peer address,
/// matching the behaviour you'd want for a directly-exposed Kestrel.
/// Operators behind nginx/traefik on a private subnet add the
/// reverse-proxy CIDR (e.g. <c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>) so
/// genuine forwarded headers flow through.</para>
///
/// <para><b>Multi-hop semantics</b>: when the header carries multiple
/// hops (<c>client, proxy1, proxy2</c>), the leftmost element is the
/// real client. The resolver walks the comma-separated list right-to-left
/// while each successive entry is itself a trusted proxy, so the first
/// untrusted entry from the right wins — that's the closest hop the
/// edge-most trusted proxy actually saw. Empty / malformed entries are
/// skipped. Truncated to 64 chars by callers (<see cref="AuthEndpoints"/>
/// already trims) so a header stuffed with kilobytes can't bloat the
/// event row.</para>
/// </summary>
public sealed class TrustedProxyResolver
{
    /// <summary>
    /// Configuration root for the CIDR allowlist. Bind to a string[]
    /// (one CIDR per element). Empty / missing = trust nothing.
    /// </summary>
    public const string CidrsConfigKey = "Tamma:TrustedProxies:Cidrs";

    private readonly IReadOnlyList<CidrRange> _trustedCidrs;
    private readonly ILogger<TrustedProxyResolver>? _logger;

    /// <summary>
    /// Production constructor — bind from <see cref="IConfiguration"/>.
    /// DI activates this overload (it carries an interface dep
    /// <see cref="IConfiguration"/> that the empty-CIDR overload does
    /// not, so the resolver is unambiguous).
    /// </summary>
    public TrustedProxyResolver(
        IConfiguration configuration,
        ILogger<TrustedProxyResolver>? logger = null)
        : this(
            (configuration ?? throw new ArgumentNullException(nameof(configuration)))
                .GetSection(CidrsConfigKey).Get<string[]>() ?? Array.Empty<string>(),
            logger)
    {
    }

    /// <summary>
    /// Test / explicit-config hook. Build a resolver from a known CIDR
    /// list without going through <see cref="IConfiguration"/>.
    /// Internal so DI never sees this overload (avoids ambiguous
    /// activation at startup).
    /// </summary>
    internal TrustedProxyResolver(
        IEnumerable<string> cidrs,
        ILogger<TrustedProxyResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cidrs);
        _logger = logger;

        var parsed = new List<CidrRange>();
        foreach (var entry in cidrs)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (CidrRange.TryParse(entry.Trim(), out var range))
            {
                parsed.Add(range);
            }
            else
            {
                _logger?.LogWarning(
                    "TrustedProxyResolver: ignoring invalid CIDR '{Entry}' "
                    + "in {ConfigKey}.", entry, CidrsConfigKey);
            }
        }
        _trustedCidrs = parsed;
    }

    /// <summary>
    /// True when the resolver has at least one trusted-proxy CIDR
    /// configured. Used by tests + diagnostic endpoints to surface "we
    /// are not trusting any forwarded headers" to operators.
    /// </summary>
    public bool HasAnyTrustedProxy => _trustedCidrs.Count > 0;

    /// <summary>
    /// Resolve the actor IP for an inbound request. Returns
    /// <c>null</c> when neither the socket nor a trusted forwarded
    /// header yields an address (test contexts without a connection,
    /// usually).
    /// </summary>
    public string? ResolveActorIp(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var socketPeer = httpContext.Connection.RemoteIpAddress;
        if (socketPeer is null)
        {
            // No socket peer (in-process test harness without a
            // connection). Fall straight back to a header-derived value
            // — but ONLY if no proxies are configured (caller is in
            // unit-test mode); otherwise refuse to honour XFF because we
            // can't validate the origin.
            if (!HasAnyTrustedProxy)
            {
                var fallback = ExtractLeftmostXff(httpContext);
                return fallback;
            }
            return null;
        }

        // Origin must be in the trusted ring before we even read the
        // header. This is the actual fix for PF-S6.
        if (!IsTrustedProxy(socketPeer))
        {
            return socketPeer.ToString();
        }

        var xff = ExtractTrustedXff(httpContext);
        return xff ?? socketPeer.ToString();
    }

    /// <summary>
    /// Returns true when the address is inside any configured trusted
    /// CIDR. Exposed for tests; production callers use
    /// <see cref="ResolveActorIp"/>.
    /// </summary>
    public bool IsTrustedProxy(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        foreach (var cidr in _trustedCidrs)
        {
            if (cidr.Contains(address)) return true;
        }
        return false;
    }

    private static string? ExtractLeftmostXff(HttpContext httpContext)
    {
        var raw = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    /// <summary>
    /// Walk the X-Forwarded-For list right-to-left through trusted
    /// proxies. The first entry that is NOT itself a trusted proxy is
    /// the real client; that's what we return. If the entire chain is
    /// trusted, fall back to the leftmost entry (best-effort guess at
    /// the originator).
    /// </summary>
    private string? ExtractTrustedXff(HttpContext httpContext)
    {
        var raw = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var hops = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (hops.Length == 0) return null;

        // Walk right-to-left.
        for (var i = hops.Length - 1; i >= 0; i--)
        {
            var trimmed = hops[i].Trim();
            if (trimmed.Length == 0) continue;

            if (!IPAddress.TryParse(StripIPv6Brackets(trimmed), out var parsed))
            {
                // Malformed hop. Treat it as untrusted so we don't
                // walk past a forged entry; return the prior (i+1) or
                // current element as the resolved client. We pick the
                // current trimmed string so the reader can see the bad
                // value in the audit log if they're debugging.
                return trimmed;
            }

            if (!IsTrustedProxy(parsed))
            {
                return parsed.ToString();
            }
            // Else: this hop IS a trusted proxy — keep walking.
        }

        // Entire chain was trusted. Return the leftmost element so the
        // event still records a candidate originator, rather than going
        // blank.
        var leftmost = hops[0].Trim();
        return string.IsNullOrWhiteSpace(leftmost) ? null : leftmost;
    }

    private static string StripIPv6Brackets(string ip)
    {
        if (ip.Length >= 2 && ip[0] == '[' && ip[^1] == ']')
        {
            return ip[1..^1];
        }
        return ip;
    }

    /// <summary>
    /// Internal CIDR helper. Supports IPv4 and IPv6.
    /// </summary>
    private sealed record CidrRange(IPAddress NetworkAddress, int PrefixLength)
    {
        private readonly byte[] _networkBytes = NetworkAddress.GetAddressBytes();

        public bool Contains(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            // CIDR families must match (no IPv4-vs-IPv6 cross-check;
            // map an IPv4-mapped-IPv6 address down to IPv4 first).
            var candidate = address;
            if (candidate.IsIPv4MappedToIPv6)
            {
                candidate = candidate.MapToIPv4();
            }
            if (candidate.AddressFamily != NetworkAddress.AddressFamily) return false;

            var candidateBytes = candidate.GetAddressBytes();
            if (candidateBytes.Length != _networkBytes.Length) return false;

            var fullBytes = PrefixLength / 8;
            var remainderBits = PrefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (candidateBytes[i] != _networkBytes[i]) return false;
            }

            if (remainderBits == 0) return true;

            var mask = (byte)(0xFF << (8 - remainderBits));
            return (candidateBytes[fullBytes] & mask) == (_networkBytes[fullBytes] & mask);
        }

        public static bool TryParse(string entry, out CidrRange range)
        {
            range = default!;
            if (string.IsNullOrWhiteSpace(entry)) return false;

            var slashIdx = entry.IndexOf('/');
            string addressPart;
            int prefix;

            if (slashIdx < 0)
            {
                // Bare IP — treat as /32 (v4) or /128 (v6) — single host.
                addressPart = entry;
                if (!IPAddress.TryParse(addressPart, out var lone)) return false;
                prefix = lone.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                range = new CidrRange(lone, prefix);
                return true;
            }

            addressPart = entry[..slashIdx];
            var prefixPart = entry[(slashIdx + 1)..];
            if (!IPAddress.TryParse(addressPart, out var addr)) return false;
            if (!int.TryParse(prefixPart, out prefix)) return false;

            var maxPrefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix < 0 || prefix > maxPrefix) return false;

            range = new CidrRange(addr, prefix);
            return true;
        }
    }
}
