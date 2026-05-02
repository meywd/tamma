using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Services.Auth;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.RateLimit;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AuthEndpoints
{
    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string BuildVerificationUrl(IConfiguration config, string token)
    {
        var baseUrl = (config["Dashboard:Url"] ?? "http://localhost:3001").TrimEnd('/');
        return $"{baseUrl}/verify?token={Uri.EscapeDataString(token)}";
    }

    private static string BuildResetUrl(IConfiguration config, string token)
    {
        var baseUrl = (config["Dashboard:Url"] ?? "http://localhost:3001").TrimEnd('/');
        return $"{baseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
    }

    /// <summary>
    /// Loads the user's full membership list and projects it to the JWT
    /// <c>tenants</c> claim shape. Story 28-9 — every token (login, refresh,
    /// switch-org, GitHub callback) carries the array so the dashboard can
    /// render a tenant switcher and the switch-org gate can validate
    /// membership without a DB hit.
    /// </summary>
    private static async Task<List<TenantClaim>> LoadTenantClaimsAsync(
        ITenantMembershipRepository membershipRepo, Guid userId)
    {
        var memberships = await membershipRepo.GetUserTenantsAsync(userId);
        return memberships
            .Where(m => m.TenantId != Guid.Empty)
            .Select(m => new TenantClaim(m.TenantId, m.Role))
            .ToList();
    }

    /// <summary>
    /// Story 28-9 — persists the per-user "active tenant" across refreshes.
    /// The Phase-2 <c>prevent_tenant_id_change</c> trigger blocks any
    /// <c>uuid → uuid</c> change to <c>users.TenantId</c>, so once the user
    /// has a personal tenant we cannot rebind that column. The fallback is
    /// <c>users.Settings.activeTenantId</c>, a JSON field already designed
    /// for per-user mutable preferences. First-time activation (when
    /// <c>users.TenantId</c> is still NULL) still uses
    /// <c>UpdateActiveTenantAsync</c> because <c>NULL → uuid</c> IS allowed
    /// and that path keeps the EnsurePersonalTenantMiddleware bootstrap
    /// working.
    /// </summary>
    private static async Task PersistActiveTenantAsync(
        IUserRepository userRepo, Guid userId, Guid tenantId)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) return;

        if (user.TenantId is null || user.TenantId.Value == Guid.Empty)
        {
            // Trigger permits NULL → uuid; do the legacy update.
            await userRepo.UpdateActiveTenantAsync(userId, tenantId);
            return;
        }

        // uuid → uuid blocked at the DB level. Stash the runtime active
        // tenant in the Settings JSON instead.
        var raw = user.Settings ?? "{}";
        Dictionary<string, object?> settings;
        try
        {
            settings = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(raw)
                ?? new Dictionary<string, object?>();
        }
        catch
        {
            // Defensive — if the column was hand-edited or written by a
            // legacy code path with a non-object shape, reset rather than
            // throw.
            settings = new Dictionary<string, object?>();
        }
        settings["activeTenantId"] = tenantId.ToString();
        await userRepo.UpdateUserSettingsAsync(
            userId, System.Text.Json.JsonSerializer.Serialize(settings));
    }

    /// <summary>
    /// Story 28-9 — read counterpart to <see cref="PersistActiveTenantAsync"/>.
    /// Resolution order: Settings JSON <c>activeTenantId</c> (set by
    /// switch-org on uuid→uuid moves), then the legacy <c>users.TenantId</c>
    /// column (still authoritative when the user has never switched).
    /// </summary>
    private static Guid? ReadActiveTenantId(User user)
    {
        var settingsRaw = user.Settings ?? "{}";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settingsRaw);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("activeTenantId", out var prop)
                && prop.ValueKind == System.Text.Json.JsonValueKind.String
                && Guid.TryParse(prop.GetString(), out var fromSettings)
                && fromSettings != Guid.Empty)
            {
                return fromSettings;
            }
        }
        catch
        {
            // fall through to column
        }
        return user.TenantId is null || user.TenantId.Value == Guid.Empty
            ? null
            : user.TenantId.Value;
    }

    private static CookieOptions BuildSessionCookie(IConfiguration config, int maxAgeSeconds)
    {
        // Cookie domain comes from config so dev (localhost) gets no Domain
        // attribute and production gets ".tamma.dev" so the cookie rides on
        // every subdomain. Audit finding 004.
        var domain = config["Cookie:Domain"];
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(maxAgeSeconds),
        };
        if (!string.IsNullOrEmpty(domain))
            options.Domain = domain;
        return options;
    }

    /// <summary>
    /// Story 28-R2 / PF-S9 — atomic bootstrap superadmin promotion.
    /// Tries to claim the single-row <c>platform_bootstrap</c>
    /// sentinel; on success, updates the user's
    /// <c>platform_role</c> to <c>"platform_admin"</c>. The schema's
    /// unique-PK + <c>CHECK (Id = 1)</c> constraint guarantees that
    /// concurrent first-user registrations race for exactly one
    /// claim — every loser silently stays at the default
    /// <c>"user"</c> role.
    ///
    /// <para>This replaces the previous TOCTOU race
    /// (<c>userRepo.CountAsync()</c> + create) where two concurrent
    /// transactions could both observe an empty users table and both
    /// mint <c>platform_admin</c>. The race is now mathematically
    /// impossible: the DB rejects a second sentinel row.</para>
    ///
    /// <para>Failures (DB unreachable, transient errors) are logged
    /// but do NOT propagate — the user has already been created and
    /// the registration response must succeed. If the bootstrap
    /// claim never lands, the deploy stays without a platform admin
    /// until an operator manually promotes one; that's the
    /// fail-secure posture.</para>
    /// </summary>
    private static async Task TryPromoteBootstrapAdminAsync(
        IUserRepository userRepo,
        IPlatformBootstrapRepository bootstrapRepo,
        User user,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var won = await bootstrapRepo.TryClaimAsync(user.Id);
            if (won)
            {
                await userRepo.SetPlatformRoleAsync(user.Id, "platform_admin");
                loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                    .LogInformation(
                        "USER.BOOTSTRAP_ADMIN.SUCCESS userId={UserId} email={Email}",
                        user.Id, user.Email);
            }
        }
        catch (Exception ex)
        {
            // Never fail the registration over a bootstrap-claim
            // hiccup; the user can still log in as a regular user.
            loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                .LogWarning(ex,
                    "Bootstrap-superadmin promotion failed for userId={UserId}",
                    user.Id);
        }
    }

    // ─── Endpoints ────────────────────────────────────────────────────────

    public static async Task<IResult> Register(
        RegisterRequest req,
        IUserRepository userRepo,
        IPasswordService passwordService,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IPlatformBootstrapRepository bootstrapRepo,
        [FromServices] IEmailService emailService,
        [FromServices] IConfiguration config,
        [FromServices] ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "Email and password are required" });

        // Full strength validation per Story 18-1 AC 4 / audit finding 013.
        // Replaces the old length-only check.
        var strength = PasswordStrengthValidator.Validate(req.Password);
        if (!strength.Valid)
            return Results.BadRequest(new { error = "Password too weak", details = strength.Errors });

        var existing = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());
        if (existing is not null)
            return Results.Conflict(new { error = "Email already registered" });

        var verificationToken = Guid.NewGuid().ToString("N");
        var tokenHash = HashToken(verificationToken);

        User user;
        try
        {
            user = await userRepo.CreateAsync(new User
            {
                Email = req.Email.ToLowerInvariant(),
                PasswordHash = passwordService.HashPassword(req.Password),
                DisplayName = req.DisplayName,
                Role = "member",
                // Default to "user". Bootstrap superadmin promotion
                // happens AFTER the user row commits (PF-S9) — we
                // can't use the user id until insert.
                PlatformRole = "user",
                AuthMethod = "email",
                EmailVerificationTokenHash = tokenHash,
                EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24),
            });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Race with the precheck — the case-insensitive unique index
            // (Phase-1 hardening migration ix_users_email_lower) caught it.
            return Results.Conflict(new { error = "Email already registered" });
        }

        // PF-S9 — atomic bootstrap superadmin claim. The single-row
        // platform_bootstrap table has a unique PK + CHECK (Id = 1)
        // constraint, so concurrent first-user registrations race for
        // exactly one row. The winner becomes platform_admin; everyone
        // else stays "user". This replaces the previous TOCTOU race
        // where two concurrent count-then-insert paths could both
        // mint platform_admin.
        await TryPromoteBootstrapAdminAsync(
            userRepo, bootstrapRepo, user, loggerFactory);

        // Auto-create personal tenant
        var slug = user.Email.Split('@')[0].ToLowerInvariant().Replace(".", "-").Replace("+", "-");
        var tenant = await tenantRepo.CreateAsync(new Tenant
        {
            Name = user.DisplayName ?? user.Email,
            Slug = $"personal-{slug}-{Guid.NewGuid().ToString()[..8]}",
            Type = "personal",
            OwnerId = user.Id
        });
        await membershipRepo.AddAsync(tenant.Id, user.Id, "owner");
        await userRepo.UpdateActiveTenantAsync(user.Id, tenant.Id);

        var verifyUrl = BuildVerificationUrl(config, verificationToken);
        // Story 28-1 PR B — registration verification email is
        // platform-scope: no tenant DB exists yet to land the row in
        // (the personal tenant we just minted is shared-infra-only
        // until provisioning runs separately). Leaving TenantId unset
        // routes through IPlatformEmailOutboxRepository →
        // platform_email_outbox. UserId is preserved for correlation.
        // Decision matrix: .dev/decisions/story-28-1-design-calls.md §5.
        var message = EmailTemplates.VerificationEmail(user.Email, verifyUrl) with
        {
            Template = "verification",
            TenantId = null,
            UserId = user.Id,
        };
        var txnId = await emailService.SendAsync(message);

        var regLogger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
        regLogger.LogInformation(
            "Email dispatch scheduled txn={TxnId} template={Template}", txnId, "verification");

        return Results.Created($"/api/admin/users/{user.Id}",
            new RegisterResponse(user.Id, "Registration successful. Please verify your email."));
    }

    public static async Task<IResult> VerifyEmail(
        VerifyEmailRequest req,
        IUserRepository userRepo,
        [FromServices] ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return Results.BadRequest(new { error = "token is required" });

        var tokenHash = HashToken(req.Token);
        var user = await userRepo.GetByEmailVerificationTokenHashAsync(tokenHash);

        if (user is null)
            return Results.BadRequest(new { error = "Invalid or expired verification token" });
        if (user.EmailVerificationExpiresAt is null || user.EmailVerificationExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Verification token has expired" });
        if (user.EmailVerified)
            return Results.BadRequest(new { error = "Email already verified" });

        await userRepo.SetEmailVerifiedAsync(user.Id);

        loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
            .LogInformation("USER.EMAIL_VERIFIED.SUCCESS userId={UserId}", user.Id);

        return Results.Ok(new { message = "Email verified successfully" });
    }

    public static async Task<IResult> ResendVerification(
        ResendVerificationRequest req,
        IUserRepository userRepo,
        [FromServices] IEmailService emailService,
        [FromServices] IConfiguration config,
        [FromServices] IRateLimitService rateLimit,
        [FromServices] ILoggerFactory loggerFactory)
    {
        const string CannedResponseMessage =
            "If the email exists, a verification link has been sent";

        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.Ok(new { message = CannedResponseMessage });

        var email = req.Email.ToLowerInvariant();

        // Rate limit before any DB work — return 429 outright. Story 18-1
        // AC 8: 3 per hour per email. Audit finding 014.
        if (rateLimit.IsLimited("resend-verification", email))
            return Results.Json(
                new { error = "Too many requests. Please try again later." },
                statusCode: 429);

        var user = await userRepo.GetByEmailAsync(email);

        if (user is not null && !user.EmailVerified)
        {
            var verificationToken = Guid.NewGuid().ToString("N");
            await userRepo.UpdateVerificationTokenAsync(
                user.Id, HashToken(verificationToken), DateTime.UtcNow.AddHours(24));

            var verifyUrl = BuildVerificationUrl(config, verificationToken);
            // Story 28-1 PR B — verification email is platform-scope (the
            // user may not have a tenant yet, or the tenant DB may not be
            // provisioned). Routes through IPlatformEmailOutboxRepository.
            var message = EmailTemplates.VerificationEmail(user.Email, verifyUrl) with
            {
                Template = "verification",
                TenantId = null,
                UserId = user.Id,
            };
            var txnId = await emailService.SendAsync(message);

            // Only consume quota when we actually did work — prevents
            // attackers from rate-limiting a victim by spraying their
            // email at the endpoint.
            rateLimit.Record("resend-verification", email);

            loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                .LogInformation("Email dispatch scheduled txn={TxnId} template={Template}",
                    txnId, "verification");
        }

        return Results.Ok(new { message = CannedResponseMessage });
    }

    public static async Task<IResult> Login(
        LoginRequest req,
        IUserRepository userRepo,
        IPasswordService passwordService,
        IJwtService jwtService,
        ILoginLockoutService lockout,
        IRefreshTokenRepository refreshTokenRepo,
        ITenantMembershipRepository membershipRepo,
        [FromServices] IConfiguration config,
        HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "Email and password are required" });

        if (lockout.IsLocked(req.Email))
        {
            var remaining = lockout.GetRemainingLockoutSeconds(req.Email);
            return Results.Json(new { error = $"Account locked. Try again in {remaining} seconds" }, statusCode: 429);
        }

        var user = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());

        // Constant-time anti-enumeration: always pay the argon2id cost,
        // even when the user lookup misses. Audit finding 012.
        if (user is null || user.PasswordHash is null)
        {
            passwordService.VerifyPassword(req.Password, passwordService.DummyHash);
            lockout.RecordFailedAttempt(req.Email);
            return Results.Unauthorized();
        }

        if (!passwordService.VerifyPassword(req.Password, user.PasswordHash))
        {
            lockout.RecordFailedAttempt(req.Email);
            return Results.Unauthorized();
        }

        if (!user.IsActive)
            return Results.Json(new { error = "Account deactivated" }, statusCode: 403);

        // Email verification gate. Audit finding 006 / Story 18-2 AC 2.
        if (!user.EmailVerified)
            return Results.Json(new { error = "Please verify your email" }, statusCode: 403);

        lockout.ResetAttempts(req.Email);

        // Migrate scrypt-format hashes to argon2id transparently. Audit
        // finding 001.
        if (passwordService.NeedsRehash(user.PasswordHash))
        {
            var newHash = passwordService.HashPassword(req.Password);
            await userRepo.UpdatePasswordHashAsync(user.Id, newHash);
        }

        // Determine tenant
        var tenantId = user.TenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            var memberships = await membershipRepo.GetUserTenantsAsync(user.Id);
            if (memberships.Count > 0)
                tenantId = memberships[0].TenantId;
        }

        // Determine role in current tenant
        var role = "member";
        if (tenantId != Guid.Empty)
        {
            var memberRole = await membershipRepo.GetRoleAsync(tenantId, user.Id);
            if (memberRole is not null) role = memberRole;
        }

        // Story 28-9 — every access token carries the full tenants list so
        // the dashboard can show a switcher and switch-org can validate
        // membership against the token.
        var tenantClaims = await LoadTenantClaimsAsync(membershipRepo, user.Id);

        var accessToken = jwtService.GenerateAccessToken(
            user, tenantId == Guid.Empty ? null : tenantId, role, tenantClaims);
        var refreshToken = jwtService.GenerateRefreshToken();
        var refreshHash = HashToken(refreshToken);

        await refreshTokenRepo.CreateAsync(user.Id, refreshHash, DateTime.UtcNow.AddDays(7));

        // Cookie carries the ACCESS JWT (not the refresh token), with the
        // configured parent domain so it rides cross-subdomain. 15-minute
        // max-age matches the JWT expiry. Audit finding 004.
        httpContext.Response.Cookies.Append("tamma_session", accessToken,
            BuildSessionCookie(config, 900));

        await userRepo.UpdateLastActiveAsync(user.Id);

        return Results.Ok(new LoginResponse(
            accessToken,
            refreshToken,
            900, // 15 min in seconds
            new UserInfo(user.Id, user.Email, user.DisplayName, role, tenantId == Guid.Empty ? null : tenantId)
        ));
    }

    public static async Task<IResult> Refresh(
        RefreshRequest req,
        IRefreshTokenRepository refreshTokenRepo,
        IJwtService jwtService,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo,
        [FromServices] IConfiguration config,
        [FromServices] ILoggerFactory loggerFactory,
        HttpContext httpContext)
    {
        // Refresh tokens come in via the request body (TS contract). The
        // tamma_session cookie carries the access JWT, not the refresh
        // token (audit finding 004).
        var refreshToken = req.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
            return Results.BadRequest(new { error = "refreshToken is required" });

        var tokenHash = HashToken(refreshToken);
        var token = await refreshTokenRepo.GetByTokenHashAsync(tokenHash);

        if (token is null)
            return Results.Json(new { error = "Invalid refresh token" }, statusCode: 401);

        // Reuse-detection: a presented refresh token that is already revoked
        // means an attacker (or stale client) is replaying. Revoke the entire
        // token family for that user. Story 18-2 §180 / audit finding 007.
        if (token.RevokedAt is not null)
        {
            await refreshTokenRepo.RevokeAllForUserAsync(token.UserId);
            loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                .LogWarning("USER.REFRESH_TOKEN_REUSE userId={UserId} — all sessions revoked", token.UserId);
            return Results.Json(new { error = "Refresh token has been revoked" }, statusCode: 401);
        }

        if (token.ExpiresAt < DateTime.UtcNow)
            return Results.Json(new { error = "Refresh token has expired" }, statusCode: 401);

        var user = token.User;
        if (user is null)
            return Results.Json(new { error = "User not found" }, statusCode: 401);

        // Story 28-9 — preserve the active tenant across refresh. Resolution
        // honours the runtime activeTenantId (Settings JSON, written by
        // switch-org on uuid→uuid moves) before the legacy users.TenantId
        // column. Only when the user has lost membership in their stored
        // active tenant do we fall back to the first available membership;
        // that happens when an admin removed them between refreshes.
        var tenantClaims = await LoadTenantClaimsAsync(membershipRepo, user.Id);
        var storedTenantId = ReadActiveTenantId(user) ?? Guid.Empty;
        Guid tenantId = Guid.Empty;
        string role = "member";

        if (storedTenantId != Guid.Empty
            && tenantClaims.Any(t => t.TenantId == storedTenantId))
        {
            tenantId = storedTenantId;
            role = tenantClaims.First(t => t.TenantId == storedTenantId).Role;
        }
        else if (tenantClaims.Count > 0)
        {
            // Membership lost — drop to the first available tenant and
            // persist it so subsequent refreshes are stable.
            tenantId = tenantClaims[0].TenantId;
            role = tenantClaims[0].Role;
            await PersistActiveTenantAsync(userRepo, user.Id, tenantId);
            loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                .LogInformation(
                    "USER.REFRESH_TENANT_FALLBACK userId={UserId} oldTenantId={OldTenantId} newTenantId={NewTenantId} — active tenant lost; reset to first available",
                    user.Id, storedTenantId, tenantId);
        }
        // else: zero memberships — leave tenantId Empty; access token will
        // emit empty `tenants` claim and middleware fail-closed (as today).

        // Rotate: revoke the presented token, mint and persist a new one.
        await refreshTokenRepo.RevokeAsync(token.Id);
        var newRefresh = jwtService.GenerateRefreshToken();
        var newRefreshHash = HashToken(newRefresh);
        await refreshTokenRepo.CreateAsync(user.Id, newRefreshHash, DateTime.UtcNow.AddDays(7));

        var accessToken = jwtService.GenerateAccessToken(
            user, tenantId == Guid.Empty ? null : tenantId, role, tenantClaims);

        // Update the session cookie to the new access JWT.
        httpContext.Response.Cookies.Append("tamma_session", accessToken,
            BuildSessionCookie(config, 900));

        return Results.Ok(new RefreshResponse(accessToken, newRefresh, 900));
    }

    public static async Task<IResult> Logout(
        IRefreshTokenRepository refreshTokenRepo,
        [FromServices] IConfiguration config,
        [FromServices] IPlatformEventPublisher eventPublisher,
        [FromServices] IRateLimitService rateLimit,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        // Logout accepts an optional body refresh token to revoke it.
        // Read the cookie domain so the cookie clear matches the cookie set.
        var domain = config["Cookie:Domain"];
        var deleteOptions = new CookieOptions { Path = "/" };
        if (!string.IsNullOrEmpty(domain))
            deleteOptions.Domain = domain;
        httpContext.Response.Cookies.Delete("tamma_session", deleteOptions);

        // Story 28-9 AC6 — `?all=true` revokes EVERY active refresh token
        // for the user, across all tenants and devices. Used by the
        // dashboard's "sign out everywhere" affordance and by admin
        // forced-logout. Falls back to the per-token revocation when
        // `?all=true` is absent (or the user isn't authenticated).
        //
        // Story 28-R2 / Finding H2 — emit a USER.LOGOUT_ALL.SUCCESS audit
        // event when the bulk-revoke path actually runs, AND rate-limit it
        // (3/hour per user) so a logout-bombed token cannot churn the
        // refresh table or the audit log.
        var revokeAll = string.Equals(
            httpContext.Request.Query["all"].ToString(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (revokeAll)
        {
            var userIdRaw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdRaw, out var userId))
            {
                // Per-user rate limit on `?all=true` only. The same shared
                // 3/hour window used by password-reset / verification-resend.
                // Fall through to per-token revocation when over-limit so
                // the user can still terminate the *current* session.
                var rateKey = userId.ToString("D");
                if (rateLimit.IsLimited("logout-all", rateKey))
                    return Results.Json(
                        new { error = "logout_all_rate_limited",
                            message = "Too many sign-out-everywhere requests. Please retry later." },
                        statusCode: StatusCodes.Status429TooManyRequests);

                var revokedCount = await refreshTokenRepo.RevokeAllForUserAsync(userId);
                rateLimit.Record("logout-all", rateKey);

                await PublishLogoutAllEventAsync(
                    eventPublisher, principal, userId, httpContext, revokedCount);

                return Results.Ok(new
                {
                    message = "Logged out everywhere",
                    revokedAll = true,
                    revokedTokenCount = revokedCount,
                });
            }
            // Fall through to per-token path if we can't identify the user.
        }

        if (httpContext.Request.HasJsonContentType())
        {
            try
            {
                var body = await httpContext.Request.ReadFromJsonAsync<RefreshRequest>();
                if (!string.IsNullOrEmpty(body?.RefreshToken))
                {
                    var tokenHash = HashToken(body.RefreshToken);
                    var token = await refreshTokenRepo.GetByTokenHashAsync(tokenHash);
                    if (token is not null)
                        await refreshTokenRepo.RevokeAsync(token.Id);
                }
            }
            catch
            {
                // Best-effort revocation — do not leak parse errors.
            }
        }

        return Results.Ok(new { message = "Logged out" });
    }

    /// <summary>
    /// Story 28-R2 / Finding H2 — publishes a <c>USER.LOGOUT_ALL.SUCCESS</c>
    /// platform event capturing the actor identity, the revoke count, and
    /// the request fingerprint (IP + user-agent + JTI). Best-effort: if the
    /// publisher throws (DB outage, downstream timeout) we swallow the error
    /// because the bulk revoke already succeeded and we must not mask that
    /// from the caller. The event row is the audit-log breadcrumb; the
    /// actual logout happened in the DB.
    /// </summary>
    private static async Task PublishLogoutAllEventAsync(
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        Guid userId,
        HttpContext httpContext,
        int revokedCount)
    {
        try
        {
            var evt = BuildAuthAuditEvent(
                "USER.LOGOUT_ALL.SUCCESS",
                principal,
                userId,
                httpContext,
                extraData: new Dictionary<string, object?>
                {
                    ["revokedTokenCount"] = revokedCount,
                });
            await publisher.AppendAndPublishAsync(evt);
        }
        catch
        {
            // Audit failures must not break the user's logout flow. The
            // structured logger picks these up via Serilog request logging.
        }
    }

    /// <summary>
    /// Story 28-R2 / Finding H2 — common shape for auth-domain audit events
    /// (<c>USER.LOGOUT_ALL.SUCCESS</c>, <c>USER.ORG_SWITCHED.SUCCESS</c>).
    /// Captures actor identity (sub + email), request fingerprint
    /// (actorIp + userAgent + jti), and lets callers attach event-specific
    /// extras via <paramref name="extraData"/>.
    ///
    /// <para>Both <c>tags</c> and <c>data</c> carry the actor — tags for
    /// SQL filtering (<c>WHERE tags->>'userId' = ?</c>), data for the
    /// immutable event-store record. <c>tenantId</c> is optional because
    /// these events are user-scoped, not tenant-scoped (a logout
    /// terminates sessions across all of the user's tenants in one shot).</para>
    /// </summary>
    private static PlatformEvent BuildAuthAuditEvent(
        string eventType,
        ClaimsPrincipal principal,
        Guid userId,
        HttpContext httpContext,
        Guid? tenantId = null,
        IReadOnlyDictionary<string, object?>? extraData = null)
    {
        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        // PF-S6 — resolve the actor IP through TrustedProxyResolver. The
        // resolver honours X-Forwarded-For ONLY when the immediate peer
        // sits in an operator-configured trusted-proxy CIDR list
        // (Tamma:TrustedProxies:Cidrs). Untrusted origins fall straight
        // through to the socket peer; this stops audit-log poisoning
        // from internet-facing requests that forge an XFF header.
        // Default-empty list = trust nothing — appropriate for a
        // directly-exposed Kestrel.
        var resolver = httpContext.RequestServices.GetService<TrustedProxyResolver>();
        string? actorIp;
        if (resolver is not null)
        {
            actorIp = resolver.ResolveActorIp(httpContext);
        }
        else
        {
            // Test contexts that don't register the resolver still need
            // an actorIp populated; fall back to the socket peer (the
            // safe default — never trust XFF in this path).
            actorIp = httpContext.Connection.RemoteIpAddress?.ToString();
        }
        // Truncate to 64 chars so a forged header stuffed with
        // kilobytes of garbage can't bloat the event.
        if (!string.IsNullOrEmpty(actorIp) && actorIp.Length > 64)
            actorIp = actorIp[..64];
        if (userAgent.Length > 256) userAgent = userAgent[..256];

        var tags = new Dictionary<string, string?>
        {
            ["userId"] = userId.ToString("D"),
            ["source"] = "auth",
        };
        if (!string.IsNullOrEmpty(email)) tags["actorEmail"] = email;
        if (!string.IsNullOrEmpty(actorIp)) tags["actorIp"] = actorIp;
        if (!string.IsNullOrEmpty(jti)) tags["jti"] = jti;
        if (tenantId is not null && tenantId.Value != Guid.Empty)
            tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["userId"] = userId.ToString("D"),
            ["actorEmail"] = email,
            ["actorIp"] = actorIp,
            ["userAgent"] = userAgent,
            ["jti"] = jti,
        };
        if (extraData is not null)
            foreach (var kv in extraData) data[kv.Key] = kv.Value;

        return new PlatformEvent
        {
            Type = eventType,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };
    }

    public static async Task<IResult> PasswordResetRequest(
        PasswordResetRequestDto req,
        IPasswordResetRepository resetRepo,
        IUserRepository userRepo,
        [FromServices] IEmailService emailService,
        [FromServices] IConfiguration config,
        [FromServices] IRateLimitService rateLimit,
        [FromServices] ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "Email is required" });

        const string CannedResponseMessage =
            "If the email exists, a reset link has been sent";

        var email = req.Email.ToLowerInvariant();

        // Rate limit per Story 18-6. Audit finding 014.
        if (rateLimit.IsLimited("password-reset-request", email))
            return Results.Json(
                new { error = "Too many reset requests. Please try again later." },
                statusCode: 429);

        var user = await userRepo.GetByEmailAsync(email);

        // GitHub-only users have no password to reset. Sending them a reset
        // email would let an inbox-owner silently set a password on a
        // GitHub-OAuth account. Audit finding 015.
        if (user is null || user.AuthMethod == "github" || user.PasswordHash is null)
            return Results.Ok(new { message = CannedResponseMessage });

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenHash = HashToken(rawToken);
        var expiresAt = DateTime.UtcNow.AddHours(1);

        await resetRepo.CreateAsync(user.Id, tokenHash, expiresAt);

        var resetUrl = BuildResetUrl(config, rawToken);
        // Story 28-1 PR B — password reset is platform-scope. Reset is
        // an account-recovery flow; pinning it to a single TenantId
        // would mean the email vanishes if that tenant is later
        // deleted. Routes through IPlatformEmailOutboxRepository.
        var message = EmailTemplates.PasswordResetEmail(user.Email, resetUrl) with
        {
            Template = "password-reset",
            TenantId = null,
            UserId = user.Id,
        };
        var txnId = await emailService.SendAsync(message);
        rateLimit.Record("password-reset-request", email);

        loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
            .LogInformation("Email dispatch scheduled txn={TxnId} template={Template}",
                txnId, "password-reset");

        return Results.Ok(new { message = CannedResponseMessage });
    }

    public static async Task<IResult> PasswordResetConfirm(
        PasswordResetConfirmDto req,
        IPasswordResetRepository resetRepo,
        IPasswordService passwordService,
        IUserRepository userRepo,
        IRefreshTokenRepository refreshTokenRepo)
    {
        // Strength check on the new password — story 18-6 implied.
        var strength = PasswordStrengthValidator.Validate(req.NewPassword);
        if (!strength.Valid)
            return Results.BadRequest(new { error = "Password too weak", details = strength.Errors });

        var tokenHash = HashToken(req.Token);
        var token = await resetRepo.GetByTokenHashAsync(tokenHash);

        if (token is null || token.ConsumedAt is not null || token.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Invalid or expired reset token" });

        var user = await userRepo.GetByIdAsync(token.UserId);
        if (user is null)
            return Results.BadRequest(new { error = "User not found" });

        await userRepo.UpdatePasswordHashAsync(user.Id, passwordService.HashPassword(req.NewPassword));
        await resetRepo.ConsumeAsync(token.Id);
        await refreshTokenRepo.RevokeAllForUserAsync(user.Id);

        return Results.Ok(new { message = "Password reset successfully" });
    }

    /// <summary>
    /// Story 28-9 — <c>POST /api/v1/auth/switch-org { tenantId }</c>. Re-issues
    /// the access JWT and rotates the refresh token to scope the session to a
    /// new tenant. Membership is verified against the DB; non-members get 403.
    /// On success, the user's <c>active_tenant_id</c> is persisted, the old
    /// refresh token is revoked, and the <c>tamma_session</c> cookie is
    /// updated with the new JWT so the dashboard's next request lands in the
    /// new tenant context.
    /// </summary>
    public static async Task<IResult> SwitchOrg(
        SwitchOrgRequest req,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo,
        IJwtService jwtService,
        IRefreshTokenRepository refreshTokenRepo,
        ISessionCookieWriter cookieWriter,
        IPlatformEventPublisher eventPublisher,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var userIdRaw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId))
            return Results.Unauthorized();

        if (req.TenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required" });

        // Membership gate — DB lookup, not the token. The token's `tenants`
        // claim is for UI hints; the DB is the source of truth at every
        // privileged transition.
        var role = await membershipRepo.GetRoleAsync(req.TenantId, userId);
        if (role is null)
            return Results.Json(
                new { error = "Not a member of the target organization" },
                statusCode: 403);

        var user = await userRepo.GetByIdAsync(userId);
        if (user is null)
            return Results.Json(new { error = "User not found" }, statusCode: 401);

        // Story 28-R2 / Finding H2 — capture the "previous active tenant" for
        // the audit event BEFORE the persist call rebinds it. The user's
        // current active tenant lives either on users.TenantId (first-time
        // bootstrap) or in users.Settings.activeTenantId (post-bootstrap
        // when the prevent_tenant_id_change trigger pinned the column).
        var fromTenantId = ExtractActiveTenantId(user) ?? user.TenantId;

        // Persist new active tenant before issuing the token so a refresh
        // racing with switch-org converges on the same tenant. Goes through
        // PersistActiveTenantAsync because the Phase-2
        // prevent_tenant_id_change trigger blocks uuid→uuid updates of
        // users.TenantId — Settings JSON is the runtime stash.
        await PersistActiveTenantAsync(userRepo, userId, req.TenantId);

        // Story 28-9 — rotate the refresh token alongside the access token so
        // the entire session is bound to the new tenant. The dashboard's
        // refresh handler picks up the new (cookie-only) refresh token; the
        // body returns the rotated value for clients that read the JSON.
        // Caller-supplied refresh token is optional — if not present, all of
        // the user's existing refresh tokens are revoked so a stale tab
        // can't keep re-issuing access tokens for the previous tenant.
        var presented = req.RefreshToken;
        int revokedAllCount = 0;
        bool revokedAllPath = false;
        if (!string.IsNullOrEmpty(presented))
        {
            var presentedHash = HashToken(presented);
            var existing = await refreshTokenRepo.GetByTokenHashAsync(presentedHash);
            if (existing is not null && existing.UserId == userId && existing.RevokedAt is null)
            {
                await refreshTokenRepo.RevokeAsync(existing.Id);
            }
        }
        else
        {
            // No refresh token in the request body — revoke all active
            // refresh tokens for this user so stale clients can't keep the
            // old tenant alive. Same shape as a password-reset.
            //
            // Story 28-R2 / Finding H2: capture the count + flip a flag so
            // the audit event records the "switch-org-no-refresh" reason.
            revokedAllCount = await refreshTokenRepo.RevokeAllForUserAsync(userId);
            revokedAllPath = true;
        }

        var newRefresh = jwtService.GenerateRefreshToken();
        var newRefreshHash = HashToken(newRefresh);
        await refreshTokenRepo.CreateAsync(userId, newRefreshHash, DateTime.UtcNow.AddDays(7));

        var tenantClaims = await LoadTenantClaimsAsync(membershipRepo, userId);
        var accessToken = jwtService.GenerateAccessToken(
            user, req.TenantId, role, tenantClaims);

        // Cookie write so the next browser request lands in the new tenant
        // automatically. Addresses audit finding 018 (the Story-18-3
        // OrgEndpoints.SwitchOrg only returned the JWT in JSON and never wrote
        // the cookie; that handler has since been deleted, and this handler is
        // the canonical surface for the switch).
        cookieWriter.WriteSession(httpContext, accessToken);

        // Story 28-R2 / Finding H2 — emit USER.ORG_SWITCHED.SUCCESS once the
        // mutation is durable. Best-effort: an audit-publisher failure must
        // not invalidate an otherwise-successful org switch.
        await PublishOrgSwitchedEventAsync(
            eventPublisher, principal, userId, httpContext,
            fromTenantId, req.TenantId, role, revokedAllPath, revokedAllCount);

        return Results.Ok(new SwitchOrgResponse(
            AccessToken: accessToken,
            RefreshToken: newRefresh,
            TenantId: req.TenantId,
            Role: role,
            ExpiresIn: 900));
    }

    /// <summary>
    /// Story 28-R2 / Finding H2 — projects the user's currently-active tenant
    /// from <c>users.Settings.activeTenantId</c> if present (the post-bootstrap
    /// stash), falling back to <c>users.TenantId</c> at the call site.
    /// Returns <c>null</c> when the JSON is malformed or the field is absent.
    /// </summary>
    private static Guid? ExtractActiveTenantId(User user)
    {
        if (string.IsNullOrWhiteSpace(user.Settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(user.Settings);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("activeTenantId", out var prop)) return null;
            if (prop.ValueKind != JsonValueKind.String) return null;
            return Guid.TryParse(prop.GetString(), out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task PublishOrgSwitchedEventAsync(
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        Guid userId,
        HttpContext httpContext,
        Guid? fromTenantId,
        Guid toTenantId,
        string role,
        bool revokedAllPath,
        int revokedAllCount)
    {
        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["fromTenantId"] = fromTenantId?.ToString("D"),
                ["toTenantId"] = toTenantId.ToString("D"),
                ["role"] = role,
            };
            if (revokedAllPath)
            {
                // Tag explicitly so SIEM can spot mass-revocations driven by
                // the switch-org-no-refresh path (legacy clients, dashboard
                // tab without a refresh token).
                extra["reason"] = "switch-org-no-refresh";
                extra["revokedTokenCount"] = revokedAllCount;
            }

            var evt = BuildAuthAuditEvent(
                "USER.ORG_SWITCHED.SUCCESS",
                principal,
                userId,
                httpContext,
                tenantId: toTenantId,
                extraData: extra);
            await publisher.AppendAndPublishAsync(evt);
        }
        catch
        {
            // Audit failures must not break the org switch.
        }
    }

    public static async Task<IResult> GetMe(
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo)
    {
        // sub claim (mapped to Name when MapInboundClaims=false) carries the
        // user GUID. Falls back to NameIdentifier in case middleware mapped
        // it. Audit finding 011.
        var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null || !Guid.TryParse(userId, out var id))
            return Results.Unauthorized();

        var user = await userRepo.GetByIdAsync(id);
        if (user is null)
            return Results.Unauthorized();

        var memberships = await membershipRepo.GetUserTenantsAsync(id);
        var membershipInfos = memberships.Select(m =>
            new MembershipInfo(m.TenantId, m.Tenant?.Name ?? "", m.Role)).ToList();

        var role = principal.FindFirst("role")?.Value
            ?? principal.FindFirst(ClaimTypes.Role)?.Value
            ?? user.Role;
        // Story 28-R2 / Finding C1 — fall back to the dedicated
        // users.platform_role column instead of the legacy
        // `role == "owner"` inference, which let every signed-up user
        // pass platform-admin gates (every user is auto-owner of their
        // personal tenant).
        var platformRole = principal.FindFirst("platformRole")?.Value
            ?? (string.IsNullOrWhiteSpace(user.PlatformRole) ? "user" : user.PlatformRole);

        var payload = new MeUserPayload(
            user.Id,
            user.Email,
            user.DisplayName,
            user.GitHubId,
            user.GitHubLogin,
            role,
            platformRole,
            user.AuthMethod,
            user.TenantId,
            membershipInfos);

        return Results.Ok(new MeResponse(payload));
    }

    private static readonly Dictionary<string, string> ServicePermissionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elsa"] = "elsa:access",
        ["logs"] = "logs:access",
        ["admin"] = "admin:access",
    };

    public static Task<IResult> RoleCheck(
        [FromQuery] string? service,
        ClaimsPrincipal principal)
    {
        // nginx auth_request gates cross-subdomain access using ONLY the HTTP
        // status. Body is for humans / debugging. Audit finding 010.
        if (string.IsNullOrEmpty(service))
            return Task.FromResult(Results.BadRequest(
                new { error = "Missing required query parameter: service" }));

        if (!ServicePermissionMap.TryGetValue(service, out var permission))
            return Task.FromResult(Results.BadRequest(
                new { error = $"Unknown service: {service}" }));

        var role = principal.FindFirst("role")?.Value
            ?? principal.FindFirst(ClaimTypes.Role)?.Value
            ?? "member";

        if (Permissions.HasPermission(role, permission))
            return Task.FromResult(Results.Ok(new RoleCheckResponse(true, role)));

        return Task.FromResult(Results.Json(
            new { error = "Insufficient role" }, statusCode: 403));
    }

    // Browser user login goes through oauth2-proxy at /oauth2/start →
    // /oauth2/callback (registered with the OAuth App identified by
    // GITHUB_OAUTH_CLIENT_ID). Tamma.Api does not own a parallel GitHub
    // OAuth flow; the prior /api/auth/github + /api/auth/github/callback
    // pair was deleted because its callback URL was registered with neither
    // the OAuth App nor the GitHub App and could never complete in prod.
    // /api/github/callback (handled by GitHubEndpoints.Callback) is the
    // GitHub App install-completion redirect, not a user-OAuth flow.
}
