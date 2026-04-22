using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamma.Api.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Auth;

/// <summary>
/// API-key authentication handler. Mirrors the TS unified-auth middleware
/// (audit finding 029) and adds the Story 28-7 prefix-routing layer:
/// <list type="bullet">
///   <item>Accepts both <c>Authorization: Bearer ...</c> (TS) and
///         <c>Authorization: ApiKey ...</c> (transitional).</item>
///   <item><b>Story 28-7</b>: routes the inbound key by prefix —
///         <c>tamma_sk_t_</c> tenant-scoped (decoded from the prefix
///         itself; the per-tenant data source is warmed via
///         <see cref="ITenantConnectionResolver"/> so the request can run
///         on the correct DB without a separate CP routing lookup),
///         <c>tamma_sk_pl_</c> platform-admin (CP only),
///         <c>tamma_sk_u_</c> user-scoped (CP only; tenant comes from
///         <c>X-Tenant-Id</c> at request time).</item>
///   <item>Hash lookup with scrypt fallback for legacy TS-issued keys
///         (audit finding 003).</item>
///   <item>Treats future-dated <c>RevokedAt</c> as a 24h rotation grace
///         period — logs WARN but allows through.</item>
///   <item>Validates <c>X-Tenant-Id</c> for service-scope keys.</item>
///   <item>Checks <c>github_installations.SuspendedAt</c> for
///         installation-scope keys.</item>
///   <item>Populates a typed <see cref="AuthPrincipal"/> on
///         <see cref="HttpContext.Items"/> (audit finding 030) and
///         exposes the resolved tenant id under
///         <c>HttpContext.Items["TenantId"]</c> for downstream middleware
///         (Story 28-8).</item>
///   <item>Emits structured INFO/WARN audit logs for every request.</item>
///   <item><b>Legacy fallback</b> (un-prefixed pre-Epic-28 keys): gated by
///         <c>Tamma:Auth:AllowLegacyUnprefixedKeys</c> (default
///         <c>true</c>); each verification emits the deprecation WARN
///         <c>api_key.legacy_unprefixed_auth</c> for ops cut-over
///         tracking.</item>
/// </list>
/// </summary>
public class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// Config key for the legacy-key fallback. Defaults to <c>true</c> so a
    /// missing-config deployment keeps un-prefixed keys working — flipping
    /// to <c>false</c> is an opt-in cutover step tracked by Story 28-7's
    /// follow-up runbook.
    /// </summary>
    public const string LegacyFallbackConfigKey = "Tamma:Auth:AllowLegacyUnprefixedKeys";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key used to expose the resolved
    /// tenant id to downstream middleware. Mirrors the convention used by
    /// the rest of the auth handler so middleware (Story 28-8) can pick it
    /// up without re-parsing the auth principal.
    /// </summary>
    public const string TenantIdItemKey = "TenantId";

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
        if (!ApiKeyPrefixParser.TryParse(rawKey, out var parsed))
            return AuthenticateResult.NoResult();

        // ── Story 28-7: route by parsed scope ─────────────────────────
        switch (parsed.Scope)
        {
            case ApiKeyScope.Unknown:
                // Banner matched but the scope marker was not one we ship.
                // Fail explicitly — never fall through to legacy lookup, so
                // we don't risk authing a forged future-scope key against a
                // legacy hash collision.
                Logger.LogWarning(
                    "Auth failure: unrecognised api-key scope marker prefix={Prefix} path={Path}",
                    LogSanitizer.Clean(ApiKeyPrefixParser.SafeDisplayPrefix(rawKey)),
                    LogSanitizer.Clean(Request.Path.Value));
                return AuthenticateResult.Fail("Invalid API key");

            case ApiKeyScope.Tenant:
                return await AuthenticatePrefixed(parsed, requireTenant: true);

            case ApiKeyScope.Platform:
            case ApiKeyScope.User:
                return await AuthenticatePrefixed(parsed, requireTenant: false);

            case ApiKeyScope.Legacy:
                return await AuthenticateLegacy(rawKey);

            default:
                return AuthenticateResult.Fail("Invalid API key");
        }
    }

    /// <summary>
    /// Verifies a Story-28-7 prefixed key. Tenant-scoped tokens additionally
    /// resolve the per-tenant data source so the request runs on the
    /// correct DB (the resolver throws <see cref="TenantNotFoundException"/>
    /// or <see cref="TenantNotProvisionedException"/> if the embedded
    /// tenant id is bogus or suspended; both surface as 401).
    /// </summary>
    private async Task<AuthenticateResult> AuthenticatePrefixed(
        ParsedApiKey parsed,
        bool requireTenant)
    {
        using var scope = serviceProvider.CreateScope();
        var apiKeyRepo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();

        var sha256Hash = ApiKeyHasher.Hash(parsed.RawKey);
        var apiKey = await apiKeyRepo.GetByHashAsync(sha256Hash);
        var displayPrefix = ApiKeyPrefixParser.SafeDisplayPrefix(parsed.RawKey);

        if (apiKey is null)
        {
            // Prefixed keys MUST hash directly (no scrypt fallback — that
            // path only ever applied to pre-Epic-28 TS-issued tokens).
            Logger.LogWarning(
                "Auth failure: invalid prefixed API key prefix={Prefix} method={Method} path={Path}",
                LogSanitizer.Clean(displayPrefix),
                LogSanitizer.Clean(Request.Method),
                LogSanitizer.Clean(Request.Path.Value));
            return AuthenticateResult.Fail("Invalid API key");
        }

        if (IsHardRevoked(apiKey, displayPrefix))
            return AuthenticateResult.Fail("API key has been revoked");

        // Tenant-scoped keys: warm the per-tenant pool. The resolver returns
        // (and caches) the NpgsqlDataSource the request will use to read
        // tenant-owned rows. A missing/suspended tenant is a 401 — same
        // shape as a hash mismatch so we don't leak which side failed.
        if (requireTenant)
        {
            if (parsed.TenantId is not Guid tenantIdFromPrefix)
            {
                Logger.LogWarning(
                    "Auth failure: tenant-scoped key with no tenant id in prefix prefix={Prefix} keyId={KeyId}",
                    LogSanitizer.Clean(displayPrefix),
                    apiKey.Id);
                return AuthenticateResult.Fail("Invalid API key");
            }

            // Defence-in-depth: if the stored ApiKey row also carries a
            // TenantId column, it must match the prefix-encoded id. A
            // mismatch means the key prefix was tampered with (or the
            // record was migrated incorrectly) — fail closed.
            if (apiKey.TenantId is Guid storedTid && storedTid != tenantIdFromPrefix)
            {
                Logger.LogWarning(
                    "Auth failure: api-key prefix tenant id != stored tenant id keyId={KeyId} prefixTid={PrefixTid} storedTid={StoredTid}",
                    apiKey.Id,
                    tenantIdFromPrefix,
                    storedTid);
                return AuthenticateResult.Fail("Invalid API key");
            }

            var resolver = scope.ServiceProvider.GetService<ITenantConnectionResolver>();
            if (resolver is not null)
            {
                try
                {
                    // We don't need the data source value here — the call
                    // primes the LRU pool for downstream EF queries and
                    // also surfaces TenantNotFound / NotProvisioned as 401
                    // (no oracle: same shape as bad hash).
                    _ = await resolver.GetDataSourceAsync(tenantIdFromPrefix, Context.RequestAborted);
                }
                catch (TenantNotFoundException)
                {
                    Logger.LogWarning(
                        "Auth failure: tenant-scoped key references unknown tenant keyId={KeyId} tenantId={TenantId}",
                        apiKey.Id,
                        tenantIdFromPrefix);
                    return AuthenticateResult.Fail("Invalid API key");
                }
                catch (TenantNotProvisionedException tnp)
                {
                    Logger.LogWarning(
                        "Auth failure: tenant-scoped key references suspended tenant keyId={KeyId} tenantId={TenantId} status={Status}",
                        apiKey.Id,
                        tenantIdFromPrefix,
                        LogSanitizer.Clean(tnp.Status));
                    return AuthenticateResult.Fail("Invalid API key");
                }
                catch (TenantConnectionStringMissingException)
                {
                    // Provisioning bug; treat as 503-equivalent for the
                    // caller (still 401 to avoid leaking infra state) and
                    // log loudly so ops sees it.
                    Logger.LogError(
                        "Auth failure: tenant has no encrypted connection string keyId={KeyId} tenantId={TenantId}",
                        apiKey.Id,
                        tenantIdFromPrefix);
                    return AuthenticateResult.Fail("Invalid API key");
                }
                catch (TenantConnectionDecryptionException)
                {
                    Logger.LogError(
                        "Auth failure: tenant connection-string decryption failed keyId={KeyId} tenantId={TenantId}",
                        apiKey.Id,
                        tenantIdFromPrefix);
                    return AuthenticateResult.Fail("Invalid API key");
                }
            }

            // Expose the resolved tenant id to downstream middleware
            // (Story 28-8 reads this key to populate TenantContext).
            Context.Items[TenantIdItemKey] = tenantIdFromPrefix;
        }

        return await BuildSuccessTicket(apiKey, scope, prefixedTenantId: parsed.TenantId);
    }

    /// <summary>
    /// Pre-Epic-28 fallback path for un-prefixed keys. Gated by the
    /// <see cref="LegacyFallbackConfigKey"/> flag so ops can disable
    /// legacy keys after the migration window.
    /// </summary>
    private async Task<AuthenticateResult> AuthenticateLegacy(string rawKey)
    {
        var allowLegacy = configuration.GetValue<bool?>(LegacyFallbackConfigKey) ?? true;
        if (!allowLegacy)
        {
            Logger.LogWarning(
                "Auth failure: legacy un-prefixed key rejected (cutover) prefix={Prefix} path={Path}",
                LogSanitizer.Clean(ApiKeyHasher.Prefix(rawKey)),
                LogSanitizer.Clean(Request.Path.Value));
            return AuthenticateResult.Fail("Invalid API key");
        }

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
                LogSanitizer.Clean(keyPrefix),
                LogSanitizer.Clean(Request.Method),
                LogSanitizer.Clean(Request.Path.Value));
            return AuthenticateResult.Fail("Invalid API key");
        }

        if (IsHardRevoked(apiKey, keyPrefix))
            return AuthenticateResult.Fail("API key has been revoked");

        // Deprecation breadcrumb. Brief AC5 — Prometheus tally is wired
        // through the existing log-shipping pipeline; the structured
        // message form is the contract ops scrapes against.
        Logger.LogWarning(
            "api_key.legacy_unprefixed_auth keyId={KeyId} userId={UserId} scope={Scope}",
            apiKey.Id,
            LogSanitizer.Clean(apiKey.OwnerId),
            LogSanitizer.Clean(apiKey.Scope));

        return await BuildSuccessTicket(apiKey, scope, prefixedTenantId: null);
    }

    /// <summary>
    /// Returns true when <paramref name="apiKey"/> is past its revoke
    /// timestamp (i.e. NOT in the 24h rotation grace window). Caller is
    /// expected to surface a 401 in that case. Logs a WARN for grace-window
    /// hits so ops can spot rolling-cutover overlap.
    /// </summary>
    private bool IsHardRevoked(ApiKey apiKey, string displayPrefix)
    {
        if (apiKey.RevokedAt is null) return false;

        if (apiKey.RevokedAt.Value <= DateTime.UtcNow)
        {
            Logger.LogWarning(
                "Auth failure: revoked key keyId={KeyId} prefix={Prefix} path={Path}",
                apiKey.Id,
                LogSanitizer.Clean(displayPrefix),
                LogSanitizer.Clean(Request.Path.Value));
            return true;
        }

        Logger.LogWarning(
            "rotating-key-still-in-use keyId={KeyId} scope={Scope} gracePeriodEnd={GracePeriodEnd}",
            apiKey.Id, LogSanitizer.Clean(apiKey.Scope), apiKey.RevokedAt.Value);
        return false;
    }

    /// <summary>
    /// Common scope-classification + claims-issuance path shared by the
    /// prefixed and legacy auth flows. Returns 401 on scope-specific
    /// failures (suspended installation, missing X-Tenant-Id, etc.).
    /// </summary>
    private async Task<AuthenticateResult> BuildSuccessTicket(
        ApiKey apiKey,
        IServiceScope scope,
        Guid? prefixedTenantId)
    {
        AuthPrincipal? typedPrincipal = null;
        Guid? effectiveTenantId = prefixedTenantId ?? apiKey.TenantId;

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
                    apiKey.Id, userId, role, effectiveTenantId ?? Guid.Empty);
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
                    apiKey.Id, installationId, effectiveTenantId);
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
                                LogSanitizer.Clean(raw), apiKey.Id);
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
                    LogSanitizer.Clean(apiKey.Scope), apiKey.Id);
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

        // Surface the resolved tenant id to downstream middleware. Tenant-
        // scoped keys set this above (from the parsed prefix); for the
        // service/legacy paths the value lands here once we've reconciled
        // header + stored TenantId.
        if (effectiveTenantId.HasValue)
            Context.Items[TenantIdItemKey] = effectiveTenantId.Value;

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
            apiKey.Id,
            LogSanitizer.Clean(apiKey.Scope),
            LogSanitizer.Clean(apiKey.OwnerId),
            effectiveTenantId,
            LogSanitizer.Clean(Request.Method),
            LogSanitizer.Clean(Request.Path.Value));

        return AuthenticateResult.Success(ticket);
    }
}
