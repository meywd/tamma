using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.OAuth;
using Tamma.Api.Services.RateLimit;
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

    // ─── Endpoints ────────────────────────────────────────────────────────

    public static async Task<IResult> Register(
        RegisterRequest req,
        IUserRepository userRepo,
        IPasswordService passwordService,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
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
        var message = EmailTemplates.VerificationEmail(user.Email, verifyUrl) with
        {
            Template = "verification",
            TenantId = tenant.Id,
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
            var message = EmailTemplates.VerificationEmail(user.Email, verifyUrl) with
            {
                Template = "verification",
                TenantId = user.TenantId,
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
                await refreshTokenRepo.RevokeAllForUserAsync(userId);
                return Results.Ok(new { message = "Logged out everywhere", revokedAll = true });
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
        var message = EmailTemplates.PasswordResetEmail(user.Email, resetUrl) with
        {
            Template = "password-reset",
            TenantId = user.TenantId,
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
            await refreshTokenRepo.RevokeAllForUserAsync(userId);
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

        return Results.Ok(new SwitchOrgResponse(
            AccessToken: accessToken,
            RefreshToken: newRefresh,
            TenantId: req.TenantId,
            Role: role,
            ExpiresIn: 900));
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
        var platformRole = principal.FindFirst("platformRole")?.Value
            ?? (role == "owner" ? "platform_admin" : "user");

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

    public static Task<IResult> GitHubAuth(
        [FromQuery] string? rd,
        [FromQuery] string? invite,
        IConfiguration config,
        HttpContext httpContext)
    {
        var clientId = config["GitHub:ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return Task.FromResult(Results.BadRequest(new { error = "GitHub OAuth not configured" }));

        var redirectUri = config["GitHub:RedirectUri"]
            ?? "http://localhost:3000/api/auth/github/callback";

        // CSRF nonce: random 32 bytes, persisted in a short-lived strict
        // cookie that the callback must read back. Audit finding 009.
        var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        httpContext.Response.Cookies.Append("tamma_oauth_csrf", csrf, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(10),
        });

        var allowedDomain = config["Cookie:Domain"]?.TrimStart('.') ?? "tamma.dev";
        var sanitizedRd = RedirectUrlSanitizer.Sanitize(rd, allowedDomain);

        var statePayload = new OAuthStatePayload(sanitizedRd, invite, csrf);
        var state = OAuthStateCodec.Encode(statePayload);

        // Scope adjustment: read:user is required to fetch the user's id
        // and login when the user has no public email. Audit finding 009.
        var url =
            "https://github.com/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=" + Uri.EscapeDataString("read:user user:email") +
            $"&state={Uri.EscapeDataString(state)}";

        return Task.FromResult(Results.Redirect(url));
    }

    /// <summary>
    /// GitHub OAuth callback. Implements the primary new-user and
    /// existing-user paths. Invite-via-state and installation-auto-link are
    /// scaffolded but flagged TODO in audit finding 008 — see related
    /// findings 021 (invite token storage) and 023 (user_installations
    /// invalidated; tenant_memberships used instead).
    /// </summary>
    public static async Task<IResult> GitHubCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        IConfiguration config,
        HttpContext httpContext,
        IGitHubOAuthService oauth,
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IInviteRepository inviteRepo,
        IJwtService jwtService,
        IRefreshTokenRepository refreshTokenRepo,
        ILoggerFactory loggerFactory)
    {
        var dashboardUrl = (config["Dashboard:Url"] ?? "http://localhost:3001").TrimEnd('/');
        var allowedDomain = config["Cookie:Domain"]?.TrimStart('.') ?? "tamma.dev";
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);

        if (!string.IsNullOrEmpty(error))
            return Results.Redirect($"{dashboardUrl}/login?error={Uri.EscapeDataString(error)}");

        if (string.IsNullOrEmpty(code))
            return Results.Redirect($"{dashboardUrl}/login?error=missing_code");

        // CSRF: state.csrf must match the cookie value the start endpoint set.
        // Audit finding 009.
        var statePayload = string.IsNullOrEmpty(state) ? null : OAuthStateCodec.TryDecode(state);
        var csrfCookie = httpContext.Request.Cookies["tamma_oauth_csrf"];
        if (statePayload is null || string.IsNullOrEmpty(csrfCookie) ||
            !string.Equals(statePayload.Csrf, csrfCookie, StringComparison.Ordinal))
        {
            logger.LogWarning("OAuth callback CSRF mismatch — rejecting");
            return Results.Redirect($"{dashboardUrl}/login?error=csrf_mismatch");
        }
        // Burn the CSRF cookie on use.
        httpContext.Response.Cookies.Delete("tamma_oauth_csrf", new CookieOptions { Path = "/" });

        var accessToken = await oauth.ExchangeCodeForTokenAsync(code);
        if (string.IsNullOrEmpty(accessToken))
            return Results.Redirect($"{dashboardUrl}/login?error=token_exchange_failed");

        var profile = await oauth.GetUserProfileAsync(accessToken);
        if (profile is null)
            return Results.Redirect($"{dashboardUrl}/login?error=github_user_fetch_failed");

        // Invite handling: state.invite is the raw token; UserInvites stores
        // SHA-256 hash. Audit finding 021.
        string assignedRole = "member";
        UserInvite? invite = null;
        Guid? inviteTenantId = null;
        if (!string.IsNullOrEmpty(statePayload.Invite))
        {
            var inviteHash = HashToken(statePayload.Invite);
            invite = await inviteRepo.GetByTokenHashAsync(inviteHash);
            if (invite is not null && invite.AcceptedAt is null && invite.ExpiresAt > DateTime.UtcNow)
            {
                assignedRole = invite.Role;
                inviteTenantId = invite.TenantId;
            }
        }

        // Upsert the user. Lookup priority: GitHub id → email (for account
        // linking). Audit finding 008.
        var user = await userRepo.GetByGitHubIdAsync(profile.Id);
        if (user is null && !string.IsNullOrEmpty(profile.Email))
        {
            var byEmail = await userRepo.GetByEmailAsync(profile.Email.ToLowerInvariant());
            if (byEmail is not null)
            {
                // Account linking — flip authMethod to "both" and attach the
                // GitHub id.
                await userRepo.SetGitHubIdAsync(byEmail.Id, profile.Id, profile.Login);
                await userRepo.UpdateAuthMethodAsync(byEmail.Id, "both");
                user = await userRepo.GetByIdAsync(byEmail.Id);
            }
        }

        if (user is null)
        {
            // New user. Per audit finding 026, Email is NOT NULL — synthesize
            // a placeholder when GitHub didn't return one.
            var placeholderEmail = string.IsNullOrEmpty(profile.Email)
                ? $"{profile.Id}+{profile.Login}@users.noreply.github.com"
                : profile.Email.ToLowerInvariant();

            user = await userRepo.CreateAsync(new User
            {
                Email = placeholderEmail,
                DisplayName = profile.Name ?? profile.Login,
                GitHubId = profile.Id,
                GitHubLogin = profile.Login,
                AvatarUrl = profile.AvatarUrl,
                AuthMethod = "github",
                EmailVerified = true,
                Role = "member",
            });

            // Auto-create personal tenant.
            var slug = profile.Login.ToLowerInvariant().Replace(".", "-").Replace("+", "-");
            var personalTenant = await tenantRepo.CreateAsync(new Tenant
            {
                Name = profile.Name ?? profile.Login,
                Slug = $"personal-{slug}-{Guid.NewGuid().ToString()[..8]}",
                Type = "personal",
                OwnerId = user.Id
            });
            await membershipRepo.AddAsync(personalTenant.Id, user.Id, "owner");
            await userRepo.UpdateActiveTenantAsync(user.Id, personalTenant.Id);
        }

        // Apply invite role to the invited tenant. tenant_memberships is
        // the post-Phase-1 home for cross-tenant role assignment (admin-db
        // ruled user_installations is folded into tenant_memberships).
        if (invite is not null && inviteTenantId is not null)
        {
            await membershipRepo.AddAsync(inviteTenantId.Value, user.Id, assignedRole);
            await inviteRepo.AcceptAsync(invite.Id);
            // Switch active tenant to the invited org so the user lands in
            // the right context.
            await userRepo.UpdateActiveTenantAsync(user.Id, inviteTenantId.Value);
            user = await userRepo.GetByIdAsync(user.Id) ?? user;
        }

        // Resolve the role for JWT claims based on the active tenant.
        var activeTenantId = user.TenantId ?? Guid.Empty;
        var role = "member";
        if (activeTenantId != Guid.Empty)
        {
            var memberRole = await membershipRepo.GetRoleAsync(activeTenantId, user.Id);
            if (memberRole is not null) role = memberRole;
        }

        // Story 28-9 — populate the tenants claim for cross-org dashboard
        // navigation.
        var tenantClaims = await LoadTenantClaimsAsync(membershipRepo, user.Id);

        var jwt = jwtService.GenerateAccessToken(
            user, activeTenantId == Guid.Empty ? null : activeTenantId, role, tenantClaims);
        var refreshToken = jwtService.GenerateRefreshToken();
        await refreshTokenRepo.CreateAsync(user.Id, HashToken(refreshToken),
            DateTime.UtcNow.AddDays(7));

        httpContext.Response.Cookies.Append("tamma_session", jwt,
            BuildSessionCookie(config, 900));

        await userRepo.UpdateLastActiveAsync(user.Id);

        // Final redirect: sanitized rd or the dashboard default.
        var sanitizedRd = RedirectUrlSanitizer.Sanitize(statePayload.Rd, allowedDomain);
        return Results.Redirect(sanitizedRd ?? dashboardUrl);
    }
}
