using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Auth;

/// <summary>
/// API-key authentication handler. Mirrors the TS unified-auth middleware
/// (audit finding 029):
/// <list type="bullet">
///   <item>Accepts both <c>Authorization: Bearer ...</c> (TS) and
///         <c>Authorization: ApiKey ...</c> (transitional).</item>
///   <item>Hash lookup with scrypt fallback for legacy TS-issued keys
///         (audit finding 003).</item>
///   <item>Treats future-dated <c>RevokedAt</c> as a 24h rotation grace
///         period — logs WARN but allows through.</item>
///   <item>Validates <c>X-Tenant-Id</c> for service-scope keys.</item>
///   <item>Checks <c>github_installations.SuspendedAt</c> for
///         installation-scope keys.</item>
///   <item>Populates a typed <see cref="AuthPrincipal"/> on
///         <see cref="HttpContext.Items"/> (audit finding 030).</item>
///   <item>Emits structured INFO/WARN audit logs for every request.</item>
/// </list>
/// </summary>
public class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var headerValue = authHeader.ToString();
        string? rawKey = null;
        if (headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            rawKey = headerValue["Bearer ".Length..].Trim();
        else if (headerValue.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            rawKey = headerValue["ApiKey ".Length..].Trim();

        if (string.IsNullOrEmpty(rawKey))
            return AuthenticateResult.NoResult();

        // The Bearer scheme is also used by JWT — only treat the token as an
        // API key if it matches the expected prefix. This avoids fighting
        // with the JWT bearer handler (which runs as the default scheme).
        if (!rawKey.StartsWith(ApiKeyHasher.KeyPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        using var scope = serviceProvider.CreateScope();
        var apiKeyRepo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();

        var sha256Hash = ApiKeyHasher.Hash(rawKey);
        var apiKey = await apiKeyRepo.GetByHashAsync(sha256Hash);
        var keyPrefix = ApiKeyHasher.Prefix(rawKey);

        if (apiKey is null)
        {
            // Legacy fallback: TS hashed API keys with scrypt. Look up by
            // scrypt-derived hash so old keys still verify post-cutover.
            var legacyHash = ApiKeyHasher.LegacyScryptHash(rawKey);
            apiKey = await apiKeyRepo.GetByHashAsync(legacyHash);
        }

        if (apiKey is null)
        {
            Logger.LogWarning(
                "Auth failure: invalid API key prefix={Prefix} method={Method} path={Path}",
                keyPrefix, Request.Method, Request.Path.Value);
            return AuthenticateResult.Fail("Invalid API key");
        }

        // Rotation grace: RevokedAt in the future means the key is rotating
        // and the old value is still valid until that timestamp. Audit
        // finding 029.
        if (apiKey.RevokedAt is not null)
        {
            if (apiKey.RevokedAt.Value <= DateTime.UtcNow)
            {
                Logger.LogWarning(
                    "Auth failure: revoked key keyId={KeyId} prefix={Prefix} path={Path}",
                    apiKey.Id, keyPrefix, Request.Path.Value);
                return AuthenticateResult.Fail("API key has been revoked");
            }
            Logger.LogWarning(
                "rotating-key-still-in-use keyId={KeyId} scope={Scope} gracePeriodEnd={GracePeriodEnd}",
                apiKey.Id, apiKey.Scope, apiKey.RevokedAt.Value);
        }

        // Build the typed principal first so we can also enforce
        // scope-specific guards (X-Tenant-Id for service, suspended check
        // for installation).
        AuthPrincipal? typedPrincipal = null;
        Guid? effectiveTenantId = apiKey.TenantId;

        switch (apiKey.Scope)
        {
            case "user":
            {
                if (!Guid.TryParse(apiKey.OwnerId, out var userId))
                {
                    Logger.LogWarning("Auth failure: malformed user-scope OwnerId keyId={KeyId}", apiKey.Id);
                    return AuthenticateResult.Fail("Invalid API key scope");
                }
                var role = "member";
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var user = await userRepo.GetByIdAsync(userId);
                if (user is not null) role = user.Role;
                typedPrincipal = new UserAuthPrincipal(
                    apiKey.Id, userId, role, apiKey.TenantId ?? Guid.Empty);
                break;
            }
            case "installation":
            {
                if (!long.TryParse(apiKey.OwnerId, out var installationId))
                {
                    Logger.LogWarning("Auth failure: malformed installation OwnerId keyId={KeyId}", apiKey.Id);
                    return AuthenticateResult.Fail("Invalid API key scope");
                }
                var instRepo = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
                var inst = await instRepo.GetByInstallationIdAsync(installationId);
                if (inst?.SuspendedAt is not null)
                {
                    Logger.LogWarning(
                        "Auth failure: installation suspended installationId={InstallationId} keyId={KeyId}",
                        installationId, apiKey.Id);
                    return AuthenticateResult.Fail("Installation is suspended");
                }
                typedPrincipal = new InstallationAuthPrincipal(
                    apiKey.Id, installationId, apiKey.TenantId);
                break;
            }
            case "service":
            {
                Guid? tenantFromHeader = null;
                if (Request.Headers.TryGetValue("X-Tenant-Id", out var headerVals))
                {
                    var raw = headerVals.ToString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        if (!Guid.TryParse(raw, out var tid))
                        {
                            Logger.LogWarning(
                                "Auth failure: malformed X-Tenant-Id={Value} keyId={KeyId}",
                                raw, apiKey.Id);
                            return AuthenticateResult.Fail("Invalid X-Tenant-Id");
                        }
                        var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                        var tenant = await tenantRepo.GetByIdAsync(tid);
                        if (tenant is null)
                        {
                            Logger.LogWarning(
                                "Auth failure: X-Tenant-Id not found tenantId={TenantId} keyId={KeyId}",
                                tid, apiKey.Id);
                            return AuthenticateResult.Fail("Invalid X-Tenant-Id: tenant not found");
                        }
                        tenantFromHeader = tid;
                        effectiveTenantId = tid;
                    }
                }
                typedPrincipal = new ServiceAuthPrincipal(
                    apiKey.Id, apiKey.OwnerId, apiKey.Permissions, tenantFromHeader);
                break;
            }
            default:
                Logger.LogWarning("Auth failure: unknown scope={Scope} keyId={KeyId}",
                    apiKey.Scope, apiKey.Id);
                return AuthenticateResult.Fail("Invalid API key scope");
        }

        // Last-used update is fire-and-forget so it never blocks the request.
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = serviceProvider.CreateScope();
                var bgRepo = bgScope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
                await bgRepo.UpdateLastUsedAsync(apiKey.Id);
            }
            catch
            {
                // Intentionally swallow — instrumentation is best-effort.
            }
        });

        Context.SetAuthPrincipal(typedPrincipal);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.OwnerId),
            new("scope", apiKey.Scope),
            new("key_id", apiKey.Id.ToString()),
        };
        if (effectiveTenantId.HasValue)
        {
            claims.Add(new Claim("tid", effectiveTenantId.Value.ToString()));
            claims.Add(new Claim("tenantId", effectiveTenantId.Value.ToString()));
        }
        if (typedPrincipal is UserAuthPrincipal up)
            claims.Add(new Claim("role", up.Role));
        foreach (var perm in apiKey.Permissions)
            claims.Add(new Claim("permission", perm));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        Logger.LogInformation(
            "Authenticated request keyId={KeyId} scope={Scope} ownerId={OwnerId} tenantId={TenantId} method={Method} path={Path}",
            apiKey.Id, apiKey.Scope, apiKey.OwnerId,
            effectiveTenantId, Request.Method, Request.Path.Value);

        return AuthenticateResult.Success(ticket);
    }
}
