namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 45-7 — the ONE resolver for the customer-facing dashboard base URL.
///
/// Every link the API emails or redirects a customer to (email verification,
/// password reset, the two org-invite URLs, the two GitHub-install redirects)
/// is built from this value. Before this class existed the base was read
/// inline at five sites with two different defaults — four used
/// <c>http://localhost:3001</c> (the ADMIN app's dev port) and one used
/// <c>https://dash.tamma.dev</c> — so an unconfigured deployment emailed four
/// kinds of link to the admin dev server and one to production.
///
/// Resolution order (the fallback chain IS the compatibility contract):
///   1. <c>Dashboard:CustomerUrl</c> — the customer app's own host
///      (dash.tamma.dev in production).
///   2. <c>Dashboard:Url</c> — the pre-split single value. A deployment that
///      sets only this (every existing self-hosted install) behaves exactly
///      as it did before the split; a single-user install has ONE dashboard
///      and must never be forced to configure two.
///   3. <c>https://dash.tamma.dev</c> — the customer host, which
///      GitHubEndpoints had already chosen as its hardcoded fallback.
///
/// Empty/whitespace values are treated as unset so an
/// <c>ENV Dashboard__CustomerUrl=</c> stub cannot produce links to "".
/// The result is normalized with <c>TrimEnd('/')</c> so a configured trailing
/// slash cannot produce <c>https://host//verify</c>.
/// </summary>
public static class DashboardUrls
{
    /// <summary>Default customer-app host (see class remarks, step 3).</summary>
    public const string DefaultCustomerUrl = "https://dash.tamma.dev";

    /// <summary>
    /// Resolves the base URL for customer-facing links. Never returns a
    /// trailing slash.
    /// </summary>
    public static string CustomerBase(IConfiguration config)
    {
        var customer = config["Dashboard:CustomerUrl"];
        if (!string.IsNullOrWhiteSpace(customer)) return customer.TrimEnd('/');

        var legacy = config["Dashboard:Url"];
        if (!string.IsNullOrWhiteSpace(legacy)) return legacy.TrimEnd('/');

        return DefaultCustomerUrl;
    }

    /// <summary>
    /// Hostname re-layout (2026-07-28) — the customer app legitimately serves
    /// from more than one origin (app.tamma.dev AND dash.tamma.dev in Tamma's
    /// own deploy), so the CORS list needs origins beyond the two Dashboard
    /// URLs. Parsed from the CSV <c>Dashboard:AdditionalOrigins</c>
    /// (env: <c>Dashboard__AdditionalOrigins</c>). Unset / empty / whitespace
    /// yields an empty array — a deployment with one hostname per app
    /// configures nothing. Entries are trimmed and blank entries dropped;
    /// callers feed the result through <see cref="NormalizeOrigins"/> together
    /// with the primary URLs so the same authority-reduction, warning and
    /// dedupe rules apply.
    /// </summary>
    public static string[] AdditionalOrigins(IConfiguration config) =>
        (config["Dashboard:AdditionalOrigins"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Review F-CORS-2 — normalize configured dashboard URLs into CORS
    /// ORIGINS. Browsers send the <c>Origin</c> request header as
    /// <c>scheme://host[:port]</c> — never a path — and
    /// <c>CorsPolicyBuilder.WithOrigins</c> matches by exact string
    /// comparison, so a configured <c>Dashboard:Url</c> /
    /// <c>Dashboard:CustomerUrl</c> carrying a path
    /// (<c>https://portal.example.com/dash</c>) would produce an entry that
    /// can never match — a silent CORS failure.
    ///
    /// <para>Each non-blank value that parses as an absolute http(s) URI is
    /// reduced to its authority via
    /// <see cref="Uri.GetLeftPart(UriPartial)"/> (default ports are dropped,
    /// exactly as browsers omit them from <c>Origin</c>). Values that fail to
    /// parse are kept verbatim so a mis-typed entry stays visible in the
    /// policy rather than vanishing. Both cases — a changed value and an
    /// unparseable one — are reported through <paramref name="warn"/> so the
    /// operator sees the misconfiguration at startup. Results are deduped
    /// case-insensitively.</para>
    /// </summary>
    public static string[] NormalizeOrigins(
        IEnumerable<string?> configuredUrls, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(configuredUrls);

        var origins = new List<string>();
        foreach (var value in configuredUrls)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var trimmed = value.Trim().TrimEnd('/');

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var origin = uri.GetLeftPart(UriPartial.Authority);
                if (!string.Equals(origin, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    warn?.Invoke(
                        $"dashboard URL '{value}' is not a bare origin — normalized to "
                        + $"'{origin}' for CORS (browsers send Origin as scheme://host[:port], "
                        + "never a path).");
                }
                origins.Add(origin);
            }
            else
            {
                warn?.Invoke(
                    $"dashboard URL '{value}' is not an absolute http(s) URL; kept verbatim "
                    + "in the CORS origin list, but it will never match a browser Origin "
                    + "header — fix the Dashboard:Url / Dashboard:CustomerUrl value.");
                origins.Add(trimmed);
            }
        }

        return origins.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
