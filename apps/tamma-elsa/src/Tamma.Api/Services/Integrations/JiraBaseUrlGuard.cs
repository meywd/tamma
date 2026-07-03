using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// SSRF guard for the per-tenant JIRA <c>baseUrl</c> (and the <c>ticketId</c> that
/// is interpolated into the request path). A tenant supplies its own JIRA base URL;
/// without validation a <c>tenant_admin</c> could point it at an internal address —
/// <c>http://169.254.169.254/…</c> (cloud metadata), <c>http://postgres:5432</c>,
/// <c>http://127.0.0.1</c> — and have the server fetch it from inside the docker
/// network (SSRF), or smuggle <c>../</c> through the ticket id for path traversal.
///
/// <para><b>Defense in depth.</b> This guard is applied at BOTH write time (the
/// credential endpoint) and use time (<see cref="JiraApiClient"/>). The hard floor
/// is always enforced:</para>
/// <list type="bullet">
///   <item>scheme MUST be <c>https</c> (plain <c>http</c> is rejected);</item>
///   <item>the host — whether a literal IP or a DNS name that resolves to one —
///     must NOT fall in a private / loopback / link-local / unique-local / metadata
///     range (see <see cref="IsBlockedAddress"/>);</item>
///   <item>an optional allowlist of host suffixes (e.g. <c>.atlassian.net</c>) may
///     be layered on top — when configured, the host must match one. The
///     private-range rejection is the hard floor even when the allowlist is empty.</item>
/// </list>
///
/// <para><see cref="SafeConnectAsync"/> is the belt-and-suspenders anti-rebinding
/// control: wired as the <c>SocketsHttpHandler.ConnectCallback</c> for the JIRA
/// client, it re-checks the ACTUAL resolved address at connect time, so a host that
/// passed validation but rebinds its DNS to a private address before the socket
/// opens still cannot be reached.</para>
/// </summary>
public static class JiraBaseUrlGuard
{
    /// <summary>A JIRA key (<c>PROJ-42</c>) or numeric id — letters, digits, hyphen
    /// only. No <c>/</c>, <c>.</c>, whitespace or other path metacharacters, so it
    /// cannot break out of <c>/rest/api/3/issue/{ticketId}</c> via <c>../</c>.</summary>
    private static readonly Regex TicketIdPattern = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled);

    private const int MaxTicketIdLength = 255;

    /// <summary>Validate a candidate JIRA <c>baseUrl</c>. When
    /// <paramref name="dnsResolve"/> is null the system resolver is used; tests inject
    /// a deterministic one.</summary>
    public static async Task<JiraBaseUrlValidation> ValidateAsync(
        string? baseUrl,
        IReadOnlyList<string>? allowedHostSuffixes = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolve = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return JiraBaseUrlValidation.Invalid("invalid_base_url", "baseUrl must be an absolute https URL.");
        }

        // Scheme: https only. Plain http is refused (no cleartext, no SSRF over http).
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return JiraBaseUrlValidation.Invalid("invalid_base_url", "baseUrl must use the https scheme.");
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return JiraBaseUrlValidation.Invalid("invalid_base_url", "baseUrl has no host.");
        }

        // Optional allowlist. When configured the host must match a suffix; a matched
        // host is a trusted SaaS/self-hosted destination and short-circuits (still
        // protected at connect time by SafeConnectAsync).
        if (allowedHostSuffixes is { Count: > 0 })
        {
            if (!MatchesAllowlist(host, allowedHostSuffixes))
            {
                return JiraBaseUrlValidation.Invalid("host_not_allowed",
                    "baseUrl host is not in the configured JIRA allowlist.");
            }
            return JiraBaseUrlValidation.Valid(uri);
        }

        // Literal IP host — check directly, no DNS.
        if (IPAddress.TryParse(host, out var literal))
        {
            return IsBlockedAddress(literal)
                ? JiraBaseUrlValidation.Invalid("host_not_allowed",
                    "baseUrl host resolves to a private, loopback, link-local, or metadata address.")
                : JiraBaseUrlValidation.Valid(uri);
        }

        // Named host — resolve and reject if ANY resolved address is blocked (a
        // partially-private result is treated as hostile). A resolution failure is
        // NOT treated as a private mapping (the host is simply unresolvable here);
        // the connect-time SafeConnectAsync remains the hard guard.
        var resolver = dnsResolve ?? DefaultResolveAsync;
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Unresolvable at validation time — allow past the DNS floor; scheme +
            // connect-time guard still apply.
            return JiraBaseUrlValidation.Valid(uri);
        }

        if (addresses.Any(IsBlockedAddress))
        {
            return JiraBaseUrlValidation.Invalid("host_not_allowed",
                "baseUrl host resolves to a private, loopback, link-local, or metadata address.");
        }

        return JiraBaseUrlValidation.Valid(uri);
    }

    /// <summary>Whether <paramref name="ticketId"/> is a safe JIRA key/id (no path
    /// metacharacters — prevents <c>../</c> traversal in the request path).</summary>
    public static bool IsValidTicketId(string? ticketId) =>
        !string.IsNullOrWhiteSpace(ticketId)
        && ticketId.Length <= MaxTicketIdLength
        && TicketIdPattern.IsMatch(ticketId);

    /// <summary>
    /// Whether <paramref name="ip"/> is in a range Tamma must never fetch from
    /// server-side: loopback (127/8, ::1), any-address (0.0.0.0, ::), private
    /// (10/8, 172.16/12, 192.168/16), link-local + metadata (169.254/16, fe80::/10),
    /// and IPv6 unique-local (fc00::/7). IPv4-mapped IPv6 is unwrapped first.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true; // 127.0.0.0/8 and ::1
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 (incl. 0.0.0.0)
                10 => true,                                  // 10.0.0.0/8
                127 => true,                                 // 127.0.0.0/8 (also loopback above)
                169 when b[1] == 254 => true,                // 169.254.0.0/16 (link-local + metadata)
                172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12
                192 when b[1] == 168 => true,                // 192.168.0.0/16
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal)
            {
                return true; // :: and fe80::/10
            }
            // fc00::/7 unique-local.
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>SocketsHttpHandler.ConnectCallback</c> for the JIRA client: resolve the
    /// endpoint host at CONNECT time and open a socket only to a non-blocked address,
    /// closing the DNS-rebinding TOCTOU window that a validate-then-connect flow
    /// leaves open.
    /// </summary>
    public static async ValueTask<Stream> SafeConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowed = addresses.Where(a => !IsBlockedAddress(a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(
                "JIRA host resolves only to disallowed (private/loopback/link-local/metadata) addresses.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool MatchesAllowlist(string host, IReadOnlyList<string> suffixes)
    {
        foreach (var raw in suffixes)
        {
            var suffix = raw?.Trim();
            if (string.IsNullOrEmpty(suffix))
            {
                continue;
            }
            // Exact host match, or a dot-boundary suffix match (".atlassian.net"
            // matches "acme.atlassian.net" but not "evilatlassian.net").
            if (string.Equals(host, suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var dotted = suffix.StartsWith('.') ? suffix : "." + suffix;
            if (host.EndsWith(dotted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static Task<IPAddress[]> DefaultResolveAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>Result of <see cref="JiraBaseUrlGuard.ValidateAsync"/>.</summary>
public readonly record struct JiraBaseUrlValidation(bool IsValid, Uri? Uri, string? ErrorCode, string? ErrorDetail)
{
    public static JiraBaseUrlValidation Valid(Uri uri) => new(true, uri, null, null);
    public static JiraBaseUrlValidation Invalid(string code, string detail) => new(false, null, code, detail);
}
