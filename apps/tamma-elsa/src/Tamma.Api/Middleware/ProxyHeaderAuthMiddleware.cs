using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Tamma.Api.Auth;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Middleware;

/// <summary>
/// Bridges an oauth2-proxy session into a Tamma <c>tamma_session</c> JWT.
///
/// Why this exists: oauth2-proxy is the OAuth gateway for elsa.tamma.dev
/// and logs.tamma.dev (which can't run their own OAuth) and for the
/// dashboard. After a user signs in through GitHub, oauth2-proxy sets the
/// <c>_oauth2_proxy</c> cookie. But Tamma.Api's authentication is JWT-only
/// (<c>tamma_session</c>), and there is no GitHub callback inside Tamma.Api
/// that mints that JWT — the prior <c>/api/auth/github*</c> path was
/// retired because the OAuth App's single registered callback is owned by
/// oauth2-proxy.
///
/// The bridge runs AFTER <c>UseAuthentication</c> (so JWT-bearing requests
/// short-circuit) and BEFORE <c>UseAuthorization</c>. When a request
/// arrives unauthenticated but with a <c>_oauth2_proxy</c> cookie, the
/// middleware:
///   1. Calls oauth2-proxy <c>/oauth2/userinfo</c> with the cookie to get
///      the user's GitHub <c>email</c> and login.
///   2. Looks up the user by email; creates a new user + personal tenant
///      if absent (same shape as the legacy /api/auth/github callback).
///   3. Mints a <c>tamma_session</c> JWT and sets it as a cookie on the
///      response so subsequent requests skip the bridge entirely.
///   4. Builds a <see cref="ClaimsPrincipal"/> on
///      <see cref="HttpContext.User"/> so the current request also sees
///      the authenticated identity.
///
/// Failure mode: any error (oauth2-proxy unreachable, userinfo malformed,
/// DB error) is swallowed and the request continues unauthenticated. The
/// downstream <c>RequireAuthorization</c> policy then either lets the
/// request through (anonymous endpoint) or 401s (the same behavior as if
/// the cookie was never present).
///
/// Because the bridge fires only when a <c>_oauth2_proxy</c> cookie is
/// present, CLI clients that never authenticate via oauth2-proxy pass
/// through immediately to JWT/API-key auth — the "optional" mode the user
/// asked for.
/// </summary>
public class ProxyHeaderAuthMiddleware : IMiddleware
{
    private const string ProxyCookieName = "_oauth2_proxy";
    private const string SessionCookieName = "tamma_session";
    private const string UserInfoPath = "/oauth2/userinfo";
    private const int SessionCookieMaxAgeSeconds = 900;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantMembershipRepository _membershipRepo;
    private readonly IPlatformBootstrapRepository _bootstrapRepo;
    private readonly IJwtService _jwt;
    private readonly ILogger<ProxyHeaderAuthMiddleware> _log;

    public ProxyHeaderAuthMiddleware(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IPlatformBootstrapRepository bootstrapRepo,
        IJwtService jwt,
        ILogger<ProxyHeaderAuthMiddleware> log)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _membershipRepo = membershipRepo;
        _bootstrapRepo = bootstrapRepo;
        _jwt = jwt;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Already authenticated by JWT or API-key — let it through.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        // No oauth2-proxy session — nothing to bridge. CLI/anonymous path.
        var proxyCookie = context.Request.Cookies[ProxyCookieName];
        if (string.IsNullOrEmpty(proxyCookie))
        {
            await next(context);
            return;
        }

        try
        {
            var userInfo = await FetchUserInfoAsync(proxyCookie, context.RequestAborted);
            if (userInfo is null || string.IsNullOrEmpty(userInfo.Email))
            {
                _log.LogDebug("Proxy bridge: userinfo returned no email; passing through anonymous");
                await next(context);
                return;
            }

            var user = await UpsertUserAsync(userInfo, context.RequestAborted);

            var activeTenantId = user.TenantId ?? Guid.Empty;
            var role = "member";
            if (activeTenantId != Guid.Empty)
            {
                var memberRole = await _membershipRepo.GetRoleAsync(activeTenantId, user.Id);
                if (memberRole is not null) role = memberRole;
            }

            var memberships = await _membershipRepo.GetUserTenantsAsync(user.Id);
            var tenantClaims = memberships
                .Where(m => m.TenantId != Guid.Empty)
                .Select(m => new TenantClaim(m.TenantId, m.Role))
                .ToList();

            var jwt = _jwt.GenerateAccessToken(
                user,
                activeTenantId == Guid.Empty ? null : activeTenantId,
                role,
                tenantClaims);

            context.Response.Cookies.Append(SessionCookieName, jwt, BuildSessionCookie());
            context.User = BuildPrincipalFromJwt(jwt);

            await _userRepo.UpdateLastActiveAsync(user.Id);
        }
        catch (Exception ex)
        {
            // Swallow — let the request continue as anonymous so RequireAuth
            // returns its normal 401 / lets-through-anonymous response. Log
            // for diagnosis but do not surface to the caller.
            _log.LogWarning(ex, "Proxy bridge failed; continuing as anonymous");
        }

        await next(context);
    }

    private async Task<UserInfoResponse?> FetchUserInfoAsync(string proxyCookie, CancellationToken ct)
    {
        var baseUrl = _config["OAuth2Proxy:Url"] ?? "http://oauth2-proxy:4180";
        var client = _httpClientFactory.CreateClient("oauth2-proxy");
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + UserInfoPath);
        // Forward the proxy session cookie so oauth2-proxy can validate.
        req.Headers.Add("Cookie", $"{ProxyCookieName}={proxyCookie}");
        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<UserInfoResponse>(cancellationToken: ct);
    }

    private async Task<User> UpsertUserAsync(UserInfoResponse info, CancellationToken ct)
    {
        var email = info.Email!.ToLowerInvariant();
        var existing = await _userRepo.GetByEmailAsync(email);
        if (existing is not null) return existing;

        var login = string.IsNullOrEmpty(info.User) ? email.Split('@')[0] : info.User;
        var displayName = string.IsNullOrEmpty(info.PreferredUsername) ? login : info.PreferredUsername;

        var created = await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = displayName,
            GitHubLogin = login,
            AuthMethod = "github",
            EmailVerified = true,
            Role = "member",
            PlatformRole = "user",
        });

        // PF-S9 — first-user-via-bridge races for the bootstrap sentinel
        // the same way the email Register and (deleted) GitHub callback
        // paths do. Failure here never breaks login; the user just stays
        // a regular member until an operator promotes them.
        try
        {
            var won = await _bootstrapRepo.TryClaimAsync(created.Id);
            if (won)
            {
                await _userRepo.SetPlatformRoleAsync(created.Id, "platform_admin");
                _log.LogInformation(
                    "USER.BOOTSTRAP_ADMIN.SUCCESS userId={UserId} email={Email}",
                    created.Id, created.Email);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bootstrap-superadmin promotion failed for userId={UserId}", created.Id);
        }

        // Auto-create personal tenant — same shape as Register / deleted callback.
        var slug = login.ToLowerInvariant().Replace(".", "-").Replace("+", "-");
        var personalTenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = displayName,
            Slug = $"personal-{slug}-{Guid.NewGuid().ToString()[..8]}",
            Type = "personal",
            OwnerId = created.Id,
        });
        await _membershipRepo.AddAsync(personalTenant.Id, created.Id, "owner");
        await _userRepo.UpdateActiveTenantAsync(created.Id, personalTenant.Id);

        return await _userRepo.GetByIdAsync(created.Id) ?? created;
    }

    private CookieOptions BuildSessionCookie()
    {
        var domain = _config["Cookie:Domain"];
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(SessionCookieMaxAgeSeconds),
        };
        if (!string.IsNullOrEmpty(domain)) options.Domain = domain;
        return options;
    }

    private ClaimsPrincipal BuildPrincipalFromJwt(string token)
    {
        // Decode the JWT we just minted and turn its claims into a
        // ClaimsPrincipal for HttpContext.User. We can't use IJwtService's
        // ValidateToken easily here without re-doing signature work, but
        // since we just minted this token ourselves a non-validating read
        // is safe and avoids a redundant verification round-trip.
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var identity = new ClaimsIdentity(jwt.Claims, JwtBearerDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private sealed record UserInfoResponse(
        [property: JsonPropertyName("user")] string? User,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("preferredUsername")] string? PreferredUsername);
}
