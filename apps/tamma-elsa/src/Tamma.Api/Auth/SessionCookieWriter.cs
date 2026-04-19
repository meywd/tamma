namespace Tamma.Api.Auth;

/// <summary>
/// Writes the <c>tamma_session</c> httpOnly cookie that the dashboard and
/// nginx auth_request gate rely on for cross-subdomain session state.
/// Mirrors the TS <c>reply.setCookie('tamma_session', ...)</c> calls in
/// <c>packages/api/src/routes/orgs/index.ts</c> (switch-org,
/// <see cref="OrgEndpoints.SwitchOrg"/>) and the auth login flow.
///
/// <para>Finding 018 remediation: the C# <c>SwitchOrg</c> only returned the
/// JWT in JSON and never wrote the cookie, breaking the browser's ability
/// to pick up the new tenant on subsequent requests.</para>
/// </summary>
public interface ISessionCookieWriter
{
    void WriteSession(HttpContext context, string accessToken, int maxAgeSeconds = 900);
}

public sealed class SessionCookieWriter : ISessionCookieWriter
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SessionCookieWriter(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public void WriteSession(HttpContext context, string accessToken, int maxAgeSeconds = 900)
    {
        // Dev / test default: no explicit domain (localhost). Production:
        // read from Auth:CookieDomain (.tamma.dev by convention).
        var domain = _config["Auth:CookieDomain"];
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = _env.IsDevelopment() ? null : ".tamma.dev";
        }

        // In Development the dashboard runs on http://localhost:3001, so
        // Secure would suppress the Set-Cookie header entirely. Non-dev
        // deployments serve over HTTPS behind the Cloudflare origin cert,
        // so Secure is mandatory.
        var secure = !_env.IsDevelopment();

        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
        };
        if (!string.IsNullOrWhiteSpace(domain))
        {
            options.Domain = domain;
        }

        context.Response.Cookies.Append("tamma_session", accessToken, options);
    }
}
