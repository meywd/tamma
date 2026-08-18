using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamma.Core.Logging;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data;
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
    ///
    /// <para>Lookup strategy (Story 28-7 deferred-item):
    /// <list type="number">
    ///   <item><b>Fast path</b> — <c>platform_api_key_index</c> by
    ///         <c>(KeyPrefix, HashedSuffix)</c>. O(1) on the CP, avoids
    ///         hitting the <c>api_keys</c> table for the common case.</item>
    ///   <item><b>Fallback</b> — <c>api_keys.KeyHash</c> lookup by
    ///         SHA-256 hash (legacy row shape). Preserved so keys issued
    ///         before the index existed still auth.</item>
    /// </list>
    /// Hash verification uses <see cref="ApiKeyHasher.Verify"/> which accepts
    /// Argon2id, SHA-256, and scrypt formats; legacy rows are transparently
    /// upgraded to Argon2id after a successful verify.</para>
    /// </summary>
    private async Task<AuthenticateResult> AuthenticatePrefixed(
        ParsedApiKey parsed,
        bool requireTenant)
    {
        using var scope = serviceProvider.CreateScope();
        var apiKeyRepo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();

        var displayPrefix = ApiKeyPrefixParser.SafeDisplayPrefix(parsed.RawKey);
        var apiKey = await ResolveApiKeyForPrefixedAsync(parsed, scope, apiKeyRepo);

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

            // Story 28-8 H7 — consult the per-pod status cache before
            // hitting the resolver. A cached non-active value short-
            // circuits with the proper Doc 04 §8.1 status code (503 /
            // 424 / 410 / 404) so callers get an actionable signal
            // instead of a generic 401. The cache populates on the
            // resolver-miss path below.
            var statusCache = scope.ServiceProvider.GetService<ITenantStatusCache>();
            if (statusCache is not null
                && statusCache.TryGet(tenantIdFromPrefix, out var cachedStatus)
                && !TenantStatusEvaluator.IsActive(cachedStatus))
            {
                Logger.LogWarning(
                    "Auth gate: tenant-scoped key blocked by cached status keyId={KeyId} tenantId={TenantId} status={Status}",
                    apiKey.Id,
                    tenantIdFromPrefix,
                    LogSanitizer.Clean(cachedStatus));
                await TenantStatusEvaluator
                    .WriteNonActiveResponseAsync(Context, tenantIdFromPrefix, cachedStatus, Context.RequestAborted)
                    .ConfigureAwait(false);
                // Response already written — fail the auth result so the
                // pipeline short-circuits. Challenge handler is a no-op
                // once HasStarted is true.
                return AuthenticateResult.Fail("Tenant not in active state");
            }

            var resolver = scope.ServiceProvider.GetService<ITenantConnectionResolver>();
            if (resolver is not null)
            {
                try
                {
                    // We don't need the data source value here — the call
                    // primes the LRU pool for downstream EF queries and
                    // also surfaces TenantNotFound / NotProvisioned (caught
                    // below + mapped to a Doc 04 §8.1 status code).
                    _ = await resolver.GetDataSourceAsync(tenantIdFromPrefix, Context.RequestAborted);
                    // Active tenant — refresh the cache so siblings on
                    // this pod skip the resolver round-trip on the next
                    // request.
                    statusCache?.Set(tenantIdFromPrefix, TenantStatusEvaluator.StatusActive);
                }
                catch (TenantNotFoundException)
                {
                    Logger.LogWarning(
                        "Auth gate: tenant-scoped key references unknown tenant keyId={KeyId} tenantId={TenantId}",
                        apiKey.Id,
                        tenantIdFromPrefix);
                    statusCache?.Set(tenantIdFromPrefix, "not_found");
                    await TenantStatusEvaluator
                        .WriteNonActiveResponseAsync(Context, tenantIdFromPrefix, status: "not_found", Context.RequestAborted)
                        .ConfigureAwait(false);
                    return AuthenticateResult.Fail("Tenant not found");
                }
                catch (TenantNotProvisionedException tnp)
                {
                    Logger.LogWarning(
                        "Auth gate: tenant-scoped key references non-active tenant keyId={KeyId} tenantId={TenantId} status={Status}",
                        apiKey.Id,
                        tenantIdFromPrefix,
                        LogSanitizer.Clean(tnp.Status));
                    statusCache?.Set(tenantIdFromPrefix, tnp.Status);
                    await TenantStatusEvaluator
                        .WriteNonActiveResponseAsync(Context, tenantIdFromPrefix, tnp.Status, Context.RequestAborted)
                        .ConfigureAwait(false);
                    return AuthenticateResult.Fail("Tenant not in active state");
                }
                catch (TenantConnectionStringMissingException)
                {
                    // Provisioning bug; treat as 503-equivalent for the
                    // caller (no Doc 04 status mapping — closest match is
                    // 503 unavailable) and log loudly so ops sees it.
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
    ///
    /// <para>Story 28-7 deferred-item: now also accepts Argon2id-format
    /// rows via <see cref="ApiKeyHasher.Verify"/>. Look-up is by <em>KeyPrefix</em>
    /// candidates (not KeyHash directly) so per-key salted hashes still
    /// resolve — a legacy un-prefixed token like
    /// <c>tamma_sk_&lt;random&gt;</c> has a 12-char display prefix that is
    /// indexed on <c>api_keys.KeyPrefix</c>.</para>
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

        var keyPrefix = ApiKeyHasher.Prefix(rawKey);

        // Primary lookup: legacy rows kept the raw SHA-256 in KeyHash, so the
        // fast path is still a unique hash lookup. This path survives when
        // the old row has NOT yet been rehashed.
        var sha256Hash = ApiKeyHasher.Hash(rawKey);
        var apiKey = await apiKeyRepo.GetByHashAsync(sha256Hash);

        if (apiKey is null)
        {
            // Legacy fallback: TS hashed API keys with scrypt. Look up by
            // scrypt-derived hash so old keys still verify post-cutover.
            var legacyHash = ApiKeyHasher.LegacyScryptHash(rawKey);
            apiKey = await apiKeyRepo.GetByHashAsync(legacyHash);
        }

        // Argon2id path: KeyHash is per-key-salted so a hash-equality lookup
        // can't find it. Fall through to scanning the small pool of active
        // rows that share the display prefix; this is O(n) in prefix
        // collisions only, and prefixes are 12 chars of base64url so the
        // number of active rows per prefix is effectively 1.
        if (apiKey is null)
        {
            var candidates = await apiKeyRepo.ListValidByScopeAsync("service");
            apiKey = ResolveByVerify(candidates, rawKey, keyPrefix);
            if (apiKey is null)
            {
                var userCandidates = await apiKeyRepo.ListValidByScopeAsync("user");
                apiKey = ResolveByVerify(userCandidates, rawKey, keyPrefix);
            }
            if (apiKey is null)
            {
                var instCandidates = await apiKeyRepo.ListValidByScopeAsync("installation");
                apiKey = ResolveByVerify(instCandidates, rawKey, keyPrefix);
            }
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

        // Legacy row: upgrade to Argon2id on successful verify (transparent
        // to the caller). Only rewrite when the stored hash is a direct
        // SHA-256/scrypt match; if the row is already Argon2id, NeedsRehash
        // returns false.
        if (ApiKeyHasher.NeedsRehash(apiKey.KeyHash))
        {
            _ = RehashAsync(apiKey.Id, rawKey);
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
    /// Story 28-7 deferred-item: resolve a prefixed key (tenant/platform/
    /// user scope) against the CP routing index first, then fall back to
    /// the legacy <c>KeyHash</c> lookup + Argon2-aware Verify. Rehashes
    /// legacy rows on the way through.
    /// </summary>
    private async Task<ApiKey?> ResolveApiKeyForPrefixedAsync(
        ParsedApiKey parsed,
        IServiceScope scope,
        IApiKeyRepository apiKeyRepo)
    {
        var displayPrefix = ApiKeyHasher.Prefix(parsed.RawKey);
        var indexRepo = scope.ServiceProvider.GetService<IPlatformApiKeyIndexRepository>();

        // Fast path: CP routing index.
        if (indexRepo is not null)
        {
            var suffixHash = HashSuffixForIndex(parsed.RawKey);
            var index = await indexRepo.GetByPrefixAndSuffixAsync(displayPrefix, suffixHash);
            if (index is not null)
            {
                var candidate = await apiKeyRepo.GetByIdAsync(index.ApiKeyId);
                if (candidate is not null && ApiKeyHasher.Verify(parsed.RawKey, candidate.KeyHash))
                {
                    if (ApiKeyHasher.NeedsRehash(candidate.KeyHash))
                        _ = RehashAsync(candidate.Id, parsed.RawKey);
                    return candidate;
                }
            }
        }

        // Legacy lookup by KeyHash (only works for SHA-256 legacy rows; new
        // rows have per-key-salted Argon2 hashes and won't match directly).
        var sha256Hash = ApiKeyHasher.Hash(parsed.RawKey);
        var apiKey = await apiKeyRepo.GetByHashAsync(sha256Hash);
        if (apiKey is not null && ApiKeyHasher.Verify(parsed.RawKey, apiKey.KeyHash))
        {
            if (ApiKeyHasher.NeedsRehash(apiKey.KeyHash))
                _ = RehashAsync(apiKey.Id, parsed.RawKey);
            return apiKey;
        }

        return null;
    }

    /// <summary>
    /// Verify + filter <paramref name="candidates"/> against <paramref name="rawKey"/>
    /// using the Argon2-aware <see cref="ApiKeyHasher.Verify"/>. First
    /// filters by KeyPrefix equality (cheap) to bound the constant-time
    /// Argon2 computation to near-singleton candidate sets.
    /// </summary>
    private static ApiKey? ResolveByVerify(List<ApiKey> candidates, string rawKey, string keyPrefix)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.KeyPrefix, keyPrefix, StringComparison.Ordinal))
                continue;
            if (ApiKeyHasher.Verify(rawKey, candidate.KeyHash))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Computes the SHA-256 of the raw key's suffix (everything after the
    /// <c>tamma_sk_</c> banner). Stored on the routing index so the auth
    /// handler can do a constant-time equality check without persisting
    /// plaintext key material anywhere.
    /// </summary>
    internal static string HashSuffixForIndex(string rawKey)
    {
        var suffix = rawKey.StartsWith(ApiKeyHasher.KeyPrefix, StringComparison.Ordinal)
            ? rawKey[ApiKeyHasher.KeyPrefix.Length..]
            : rawKey;
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(suffix))).ToLowerInvariant();
    }

    /// <summary>
    /// Background rehash on successful legacy verify. Isolated scope + try/
    /// catch so a DB hiccup never fails the auth path.
    /// </summary>
    private async Task RehashAsync(Guid apiKeyId, string rawKey)
    {
        try
        {
            using var bgScope = serviceProvider.CreateScope();
            var bgRepo = bgScope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
            var argon2 = ApiKeyHasher.HashArgon2(rawKey);
            await bgRepo.UpdateHashAsync(apiKeyId, argon2);
        }
        catch
        {
            // Intentionally swallow — rehash is best-effort; legacy verify
            // still works on the next request.
        }
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
    ///
    /// <para>Story 28-7 deferred-item: enforces the per-key RPM limit
    /// from the <c>api_keys.RateLimitRpm</c> shadow column. Over-limit
    /// requests surface as a 401 (same shape as a hash miss) so a
    /// misbehaving caller can't probe the rate-limit boundary.</para>
    /// </summary>
    private async Task<AuthenticateResult> BuildSuccessTicket(
        ApiKey apiKey,
        IServiceScope scope,
        Guid? prefixedTenantId)
    {
        AuthPrincipal? typedPrincipal = null;
        Guid? effectiveTenantId = prefixedTenantId ?? apiKey.TenantId;

        // Per-key RPM gate. Resolved from the CP shadow column via the EF
        // model; null means "no operator-set limit" — preserves back-compat
        // with keys minted before Story 28-7's shadow column landed.
        var rateLimitRpm = TryReadRateLimitRpm(scope, apiKey.Id);
        var rpmLimiter = scope.ServiceProvider
            .GetService<Tamma.Api.Services.RateLimit.IApiKeyRateLimiter>();
        if (rpmLimiter is not null && rpmLimiter.IsLimited(apiKey.Id, rateLimitRpm))
        {
            Logger.LogWarning(
                "Auth failure: api-key rate limit exceeded keyId={KeyId} rpm={Rpm}",
                apiKey.Id, rateLimitRpm);
            return AuthenticateResult.Fail("API key rate limit exceeded");
        }
        rpmLimiter?.Record(apiKey.Id);

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
                // OwnerId for an installation key is the installation ENTITY id (a Guid).
                // Both issuance sites write it that way — InstallationRouterService's
                // `OwnerId = install.Id.ToString()` and ApiKeyRotationService's
                // `installationEntityId.ToString()` — and ListByOwnerAsync is queried with
                // the same Guid. This branch used to accept ONLY `long.TryParse`, i.e. the
                // GitHub installation id, which is a DIFFERENT column (install.InstallationId).
                // So every installation key failed here after a successful lookup: 401
                // "Invalid API key scope" with a "malformed installation OwnerId" warning,
                // on the very first request. Fixing the key prefix (2026-08-18) was
                // necessary but not sufficient — the key never got past ticket building.
                //
                // Guid first (what is written today); the long form is still accepted so a
                // row written by any older path keeps working.
                var instRepo = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
                GitHubInstallation? inst;
                if (Guid.TryParse(apiKey.OwnerId, out var installationEntityId))
                {
                    inst = await instRepo.GetByEntityIdAsync(installationEntityId);
                }
                else if (long.TryParse(apiKey.OwnerId, out var githubInstallationId))
                {
                    inst = await instRepo.GetByInstallationIdAsync(githubInstallationId);
                }
                else
                {
                    Logger.LogWarning("Auth failure: malformed installation OwnerId keyId={KeyId}", apiKey.Id);
                    return AuthenticateResult.Fail("Invalid API key scope");
                }

                if (inst is null)
                {
                    Logger.LogWarning(
                        "Auth failure: installation not found for key keyId={KeyId}", apiKey.Id);
                    return AuthenticateResult.Fail("Invalid API key scope");
                }

                var installationId = inst.InstallationId;

                // Note the not-found deny above is also a tightening: this branch used to
                // read `inst?.SuspendedAt`, so a key whose installation row no longer
                // existed skipped the suspension check and still built a valid ticket.
                if (inst.SuspendedAt is not null)
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

        // Story 37-10 (AC8) — AUTH.APIKEY.USED, throttled to one heartbeat per
        // key per time bucket so the hot per-request auth path does not flood the
        // audit trail. Prefix only, never the key. Best-effort + never-throws.
        await EmitApiKeyUsedHeartbeatAsync(apiKey, effectiveTenantId, scope);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Story 37-10 — emit the throttled <c>AUTH.APIKEY.USED</c> heartbeat. The
    /// throttle (<see cref="Tamma.Api.Services.Audit.IApiKeyAuditHeartbeat"/>) and
    /// the emitter are resolved from the request scope; a missing registration
    /// simply skips the emission. Platform-edge event carrying the tenant when the
    /// key is tenant-scoped. Never throws (audit is best-effort).
    /// </summary>
    private async Task EmitApiKeyUsedHeartbeatAsync(
        ApiKey apiKey, Guid? tenantId, IServiceScope scope)
    {
        try
        {
            var heartbeat = scope.ServiceProvider
                .GetService<Tamma.Api.Services.Audit.IApiKeyAuditHeartbeat>();
            var emitter = scope.ServiceProvider
                .GetService<Tamma.Api.Services.Audit.ISensitiveActionEmitter>();
            if (heartbeat is null || emitter is null) return;

            // Throttle: one event per key per bucket (heartbeat, not per-request).
            if (!heartbeat.ShouldEmit(apiKey.Id))
            {
                Logger.LogDebug(
                    "AUTH.APIKEY.USED suppressed by throttle keyId={KeyId}", apiKey.Id);
                return;
            }

            var ip = Context.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(ip) && ip.Length > 64) ip = ip[..64];

            // Safe display prefix: the banner + a few chars only, short enough that
            // the credential redactor's secret-prefix regex (which needs 6+ trailing
            // chars after a known key banner) does NOT scrub it away. Never the key.
            var safePrefix = apiKey.KeyPrefix.Length > 12
                ? apiKey.KeyPrefix[..12]
                : apiKey.KeyPrefix;

            var tags = new Dictionary<string, string?>
            {
                ["apiKeyId"] = apiKey.Id.ToString("D"),
                ["apiKeyPrefix"] = safePrefix,
                ["scope"] = apiKey.Scope,
                ["source"] = "api-key",
            };
            if (!string.IsNullOrEmpty(ip)) tags["ip"] = ip;

            var data = new Dictionary<string, object?>
            {
                ["apiKeyId"] = apiKey.Id.ToString("D"),
                ["apiKeyPrefix"] = safePrefix,
                ["scope"] = apiKey.Scope,
                ["ip"] = ip,
            };

            // Hot path: this runs inside request authentication. A stalled CP
            // DB/bus must NOT block the auth request until the DB command timeout.
            // Bound the emit with a short timeout linked to the request-abort
            // token so a slow audit sink can't hang authentication (the throttle
            // caps frequency, but the once-per-bucket request still shouldn't stall).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Context.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await emitter.EmitAsync(
                Tamma.Api.Services.Audit.SensitiveAction.ForPlatform(
                    Tamma.Core.Audit.SensitiveActionCatalog.ApiKeyUsed,
                    tenantId, actorUserId: null, tags, data),
                cts.Token);
        }
        catch (Exception ex)
        {
            // Best-effort — an audit emit failure must never fail authentication.
            Logger.LogWarning(ex,
                "AUTH.APIKEY.USED emission failed keyId={KeyId}; auth is unaffected.",
                apiKey.Id);
        }
    }

    /// <summary>
    /// Story 28-7 deferred-item helper: reads the shadow
    /// <c>api_keys.RateLimitRpm</c> column via the CP DbContext. Returns
    /// <c>null</c> when unset or the context is unavailable (unit tests
    /// that don't register it). Defensive try/catch — a missing column on
    /// unmigrated dev DBs must not fail auth.
    /// </summary>
    private static int? TryReadRateLimitRpm(IServiceScope scope, Guid apiKeyId)
    {
        try
        {
            var cp = scope.ServiceProvider.GetService<ControlPlaneDbContext>();
            if (cp is null) return null;

            var tracked = cp.ChangeTracker.Entries<ApiKey>()
                .FirstOrDefault(e => e.Entity.Id == apiKeyId);
            if (tracked is not null)
            {
                var val = tracked.Property<int?>("RateLimitRpm").CurrentValue;
                return val;
            }

            // Not tracked — do a lightweight projection query.
            return cp.ApiKeys
                .Where(k => k.Id == apiKeyId)
                .Select(k => EF.Property<int?>(k, "RateLimitRpm"))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
