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
}
