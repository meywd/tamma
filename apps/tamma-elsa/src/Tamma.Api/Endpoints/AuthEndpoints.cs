using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.Auth;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.RateLimit;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AuthEndpoints
{
    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // Story 45-7: both links are CUSTOMER-facing, so the base comes from the
    // shared DashboardUrls resolver (Dashboard:CustomerUrl → Dashboard:Url →
    // dash.tamma.dev). The PATHS are frozen: /verify and /reset-password are
    // in the inbox of every user who has ever registered; the customer app
    // routes both (45-2/45-3). Do not change them here.
    private static string BuildVerificationUrl(IConfiguration config, string token)
    {
        var baseUrl = DashboardUrls.CustomerBase(config);
        return $"{baseUrl}/verify?token={Uri.EscapeDataString(token)}";
    }

    private static string BuildResetUrl(IConfiguration config, string token)
    {
        var baseUrl = DashboardUrls.CustomerBase(config);
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
            // Story 28-9 AC1 residual — carry the tenant slug so the active
            // tenant's `active_tenant_slug` claim can be sourced from this
            // list without a second DB hit. `GetUserTenantsAsync` already
            // `.Include(m => m.Tenant)`s the navigation. Coalesce to "" so a
            // null slug (legacy/partial row) degrades gracefully.
            .Select(m => new TenantClaim(m.TenantId, m.Role, m.Tenant?.Slug ?? string.Empty))
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
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] Tamma.Api.Services.Billing.IBillingProvider billing,
        [FromServices] Tamma.Data.Repositories.IPlatformQueuedTaskRepository platformTasks)
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

        // Story 35-1 (AC6) — non-blocking Stripe customer mapping on the
        // registration tenant-create path (same hook as OrgEndpoints.CreateOrg).
        // Single-user → no-op; SaaS Stripe failure → enqueue retry, never block
        // registration.
        await Tamma.Api.Services.Billing.BillingTenantCreateHook.RunAsync(
            billing, platformTasks, loggerFactory, tenant, user.Email);

        var verifyUrl = BuildVerificationUrl(config, verificationToken);
        // Story 28-1 PR B — registration verification email is
        // platform-scope: no tenant schema exists yet to land the row in
        // (the personal tenant we just minted has no placement until
        // provisioning runs separately). Leaving TenantId unset
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
        [FromServices] ILoggerFactory loggerFactory,
        HttpContext httpContext)
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

        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
        logger.LogInformation("USER.EMAIL_VERIFIED.SUCCESS userId={UserId}", user.Id);

        // Story 28-5 AC1 follow-up (2026-05-30) — for every tenant the
        // user OWNS that is currently in pending_verification, flip Status
        // → provisioning and emit TENANT.PROVISIONING_REQUESTED. This is
        // the verify-email coupling Doc 03 §0 + Story 28-5 AC1 specified:
        // the workflow trigger fires after the human proves control of the
        // email address, not earlier (resists bot-driven provisioning).
        //
        // Conditional, idempotent guard: only tenants explicitly stamped
        // pending_verification transition. NULL-Status tenants (the
        // default — see Register, which leaves Status unset)
        // are LEFT ALONE because TenantStatusEvaluator treats NULL as
        // active. Promoting them would 503 the user until a Story 28-5
        // workflow consumer drains the event, which is not yet wired in
        // production. The conditional pattern matches AdminTenantsEndpoints
        // .RetryTenant which sets Status='pending_verification' explicitly
        // before triggering.
        //
        // Best-effort: a missing publisher / DB hiccup must not fail the
        // verify-email flow — EmailVerified=true is the user-visible
        // contract; provisioning is a downstream side-effect.
        await TryTriggerProvisioningForOwnedTenantsAsync(
            user.Id, httpContext, logger);

        return Results.Ok(new { message = "Email verified successfully" });
    }

    /// <summary>
    /// Story 28-5 AC1 follow-up (2026-05-30) — finds tenants owned by
    /// <paramref name="userId"/> with Status='pending_verification', flips
    /// each to 'provisioning' in a single DB round trip, and emits one
    /// <c>TENANT.PROVISIONING_REQUESTED</c> per transitioned tenant. The
    /// CP DbContext and the publisher are both pulled off
    /// <paramref name="httpContext"/>'s request services so the handler
    /// stays unit-testable through composite providers; missing services
    /// are tolerated (audit-only emission is best-effort).
    /// </summary>
    private static async Task TryTriggerProvisioningForOwnedTenantsAsync(
        Guid userId,
        HttpContext httpContext,
        ILogger logger)
    {
        try
        {
            // Open an explicit scope so ControlPlaneDbContext (scoped) is
            // resolvable even from a root provider (e.g. tests that wire
            // RequestServices to the test factory's root container). Mirrors
            // the IServiceScopeFactory pattern used by PlatformEventPublisher.
            var scopeFactory = httpContext.RequestServices
                .GetService<IServiceScopeFactory>();
            if (scopeFactory is null) return;
            using var scope = scopeFactory.CreateScope();

            var db = scope.ServiceProvider.GetService<ControlPlaneDbContext>();
            if (db is null) return;

            // Bypass the soft-delete query filter — tenants in
            // pending_verification by definition have not been deleted,
            // but explicit is better than implicit. Pre-filter on the
            // shadow Status column via EF.Property so the WHERE happens
            // server-side (avoids loading every owned tenant just to skip
            // most of them) AND ensures shadow-column hydration on the
            // tracked entity for the subsequent mutation.
            var pending = await db.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.OwnerId == userId
                            && t.DeletedAt == null
                            && EF.Property<string?>(t, "Status") == "pending_verification")
                .ToListAsync();

            if (pending.Count == 0) return;

            var transitioned = new List<Tenant>(pending.Count);
            foreach (var tenant in pending)
            {
                db.Entry(tenant).Property("Status").CurrentValue = "provisioning";
                tenant.UpdatedAt = DateTime.UtcNow;
                transitioned.Add(tenant);
            }

            await db.SaveChangesAsync();

            // Publisher is optional — emission is the audit breadcrumb
            // for Story 28-11 dashboard + the trigger source the future
            // Elsa workflow listens on; failing to publish must not roll
            // back the Status transition because the tenant row IS now in
            // provisioning and the admin retry path can recover.
            var publisher = httpContext.RequestServices
                .GetService<IPlatformEventPublisher>()
                ?? scope.ServiceProvider.GetService<IPlatformEventPublisher>();
            if (publisher is null) return;

            foreach (var tenant in transitioned)
            {
                var evt = BuildVerifyEmailProvisioningEvent(tenant.Id, userId);
                try
                {
                    await publisher.AppendAndPublishAsync(evt);
                }
                catch (Exception innerEx)
                {
                    logger.LogWarning(innerEx,
                        "TENANT.PROVISIONING_REQUESTED publish failed tenantId={TenantId}",
                        tenant.Id);
                }
            }
        }
        catch (Exception ex)
        {
            // EmailVerified=true is the user-visible contract; provisioning
            // is a downstream side-effect. Never roll back the response
            // over a trigger-side hiccup.
            logger.LogWarning(ex,
                "verify-email provisioning trigger failed userId={UserId}", userId);
        }
    }

    /// <summary>
    /// Story 28-5 AC1 follow-up (2026-05-30) — builds the
    /// <c>TENANT.PROVISIONING_REQUESTED</c> platform_events row emitted by
    /// verify-email. Shape matches the admin-retry equivalent in
    /// <c>AdminTenantsEndpoints.BuildAdminEvent</c> but with
    /// <c>source="verify-email"</c> so dashboards / SIEM can tell the two
    /// trigger origins apart. The user IS the owner here (we just
    /// confirmed their email) so tags carry <c>userId</c> as well as
    /// <c>tenantId</c>; no <c>actorEmail</c>/<c>actorIp</c> because this
    /// is a system-initiated transition, not an operator action.
    /// </summary>
    private static PlatformEvent BuildVerifyEmailProvisioningEvent(
        Guid tenantId, Guid userId)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["userId"] = userId.ToString("D"),
            ["source"] = "verify-email",
        };
        var data = new Dictionary<string, object?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["userId"] = userId.ToString("D"),
            ["requestedAt"] = DateTime.UtcNow,
            ["source"] = "verify-email",
        };
        return new PlatformEvent
        {
            Type = "TENANT.PROVISIONING_REQUESTED",
            TenantId = tenantId,
            UserId = userId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };
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

        // Story 37-10 — best-effort resolve the sensitive-action emitter off the
        // request scope (mirrors the Refresh handler's IPlatformEventPublisher
        // pattern) so the many tests that call Login/Refresh directly with
        // positional args keep compiling; a missing registration simply skips the
        // audit emission (never-throws / best-effort).
        var auditEmitter = TryResolveEmitter(httpContext);

        if (lockout.IsLocked(req.Email))
        {
            var remaining = lockout.GetRemainingLockoutSeconds(req.Email);
            await EmitLoginFailureAsync(auditEmitter, req.Email, "locked_out", httpContext);
            return Results.Json(new { error = $"Account locked. Try again in {remaining} seconds" }, statusCode: 429);
        }

        var user = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());

        // Constant-time anti-enumeration: always pay the argon2id cost,
        // even when the user lookup misses. Audit finding 012.
        if (user is null || user.PasswordHash is null)
        {
            passwordService.VerifyPassword(req.Password, passwordService.DummyHash);
            lockout.RecordFailedAttempt(req.Email);
            await EmitLoginFailureAsync(auditEmitter, req.Email, "bad_credentials", httpContext);
            return Results.Unauthorized();
        }

        if (!passwordService.VerifyPassword(req.Password, user.PasswordHash))
        {
            lockout.RecordFailedAttempt(req.Email);
            await EmitLoginFailureAsync(auditEmitter, req.Email, "bad_credentials", httpContext);
            return Results.Unauthorized();
        }

        if (!user.IsActive)
        {
            await EmitLoginFailureAsync(auditEmitter, req.Email, "account_deactivated", httpContext);
            return Results.Json(new { error = "Account deactivated" }, statusCode: 403);
        }

        // Email verification gate. Audit finding 006 / Story 18-2 AC 2.
        if (!user.EmailVerified)
        {
            await EmitLoginFailureAsync(auditEmitter, req.Email, "unverified_email", httpContext);
            return Results.Json(new { error = "Please verify your email" }, statusCode: 403);
        }

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

        // Story 28-9 AC3 — bind the refresh row to the active tenant (null
        // for rootless tokens when the user has 0/2+ memberships per AC4)
        // and seed the JTI chain head with a fresh UUID. The first row in
        // a session lineage IS its own chain head; rotation propagates the
        // value to children so reuse-detection can revoke the whole
        // lineage atomically.
        await refreshTokenRepo.CreateAsync(
            user.Id,
            tenantId: tenantId == Guid.Empty ? null : tenantId,
            refreshHash,
            DateTime.UtcNow.AddDays(7),
            jtiChainHead: Guid.NewGuid());

        // Cookie carries the ACCESS JWT (not the refresh token), with the
        // configured parent domain so it rides cross-subdomain. 15-minute
        // max-age matches the JWT expiry. Audit finding 004.
        httpContext.Response.Cookies.Append("tamma_session", accessToken,
            BuildSessionCookie(config, 900));

        await userRepo.UpdateLastActiveAsync(user.Id);

        // Story 37-10 — AUTH.LOGIN.SUCCESS. Platform-edge event carrying the
        // resolved active tenant (null => control-plane), so the 37-1 projector
        // routes the curated row to that tenant's schema in SaaS.
        await EmitLoginSuccessAsync(
            auditEmitter, user, tenantId == Guid.Empty ? null : tenantId, httpContext);

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
        // Story 28-9 AC3 follow-up (2026-05-30) — IPlatformEventPublisher
        // is best-effort resolved off the request scope so existing tests
        // that pre-date this signature don't need an explicit injection.
        // When the publisher is registered (production + tests that wire
        // a recording double), reuse-detection emits AUTH.REFRESH_REUSE_DETECTED;
        // when it isn't (a small slice of pure-unit tests), the handler
        // silently skips the emission rather than failing the refresh.
        var eventPublisher = httpContext.RequestServices
            .GetService<IPlatformEventPublisher>();
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
        // session lineage (Story 28-9 AC3 — JtiChainHead). Story 18-2 §180
        // / audit finding 007.
        //
        // Pre-Story-28-9 rows carry a NULL JtiChainHead — for those we fall
        // back to the previous "burn every token for the user" semantics so
        // the security posture stays at least as strong as before. New rows
        // burn only the affected lineage so concurrent sessions on other
        // devices survive a single compromised token's reuse.
        if (token.RevokedAt is not null)
        {
            var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
            int burnedCount;
            Guid? chainHeadForEvent;
            if (token.JtiChainHead is { } chainHead && chainHead != Guid.Empty)
            {
                burnedCount = await refreshTokenRepo.RevokeChainAsync(
                    chainHead, RefreshTokenRevokedReasons.ReuseDetected);
                chainHeadForEvent = chainHead;
                logger.LogWarning(
                    "AUTH.REFRESH_REUSE_DETECTED userId={UserId} chainHead={ChainHead} burned={Burned} — session lineage revoked",
                    token.UserId, chainHead, burnedCount);
            }
            else
            {
                burnedCount = await refreshTokenRepo.RevokeAllForUserAsync(
                    token.UserId, RefreshTokenRevokedReasons.ReuseDetected);
                chainHeadForEvent = null;
                logger.LogWarning(
                    "USER.REFRESH_TOKEN_REUSE userId={UserId} — all sessions revoked (legacy path; pre-28-9 row had no chain head)",
                    token.UserId);
            }

            // Story 28-9 AC3 follow-up (2026-05-30) — durable audit row in
            // platform_events so SIEM / SOC2 reviews can detect refresh-token
            // theft without scraping logs. Best-effort: a publisher failure
            // must NOT mask the 401 — the security action (lineage burn)
            // already happened.
            if (eventPublisher is not null)
            {
                try
                {
                    var evt = BuildRefreshReuseDetectedEvent(
                        token.UserId, token.TenantId, chainHeadForEvent,
                        burnedCount, httpContext);
                    await eventPublisher.AppendAndPublishAsync(evt);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "AUTH.REFRESH_REUSE_DETECTED publish failed userId={UserId}",
                        token.UserId);
                }
            }

            return Results.Json(new { error = "Refresh token has been revoked" }, statusCode: 401);
        }

        if (token.ExpiresAt < DateTime.UtcNow)
            return Results.Json(new { error = "Refresh token has expired" }, statusCode: 401);

        var user = token.User;
        if (user is null)
            return Results.Json(new { error = "User not found" }, statusCode: 401);

        // Story 28-9 AC3 — the refresh token's DB-side TenantId is the
        // binding source of truth. A token minted for tenant A can NEVER
        // mint an access token for tenant B; the DB column is the durable
        // expression of that contract (the access-token's tenantId claim
        // expires after 15 minutes, the refresh row persists for 7 days).
        //
        // Pre-Story-28-9 rows carry a NULL TenantId — for those we fall
        // back to the previous logic (active-tenant from Settings.JSON,
        // with first-available fallback when membership is lost). New
        // rows lock to their own TenantId; membership loss in the bound
        // tenant returns 401 instead of silently failing into a different
        // tenant (that's what /auth/switch-org is for).
        var tenantClaims = await LoadTenantClaimsAsync(membershipRepo, user.Id);
        Guid tenantId;
        string role = "member";

        if (token.TenantId is { } boundTenantId && boundTenantId != Guid.Empty)
        {
            // Story 28-9 AC3 — DB-bound refresh token. Re-resolve the role
            // for the bound tenant (this is the "mid-session role change"
            // catch-up per AC3: a demotion in the active tenant flows into
            // the next refresh's access token).
            var claim = tenantClaims.FirstOrDefault(t => t.TenantId == boundTenantId);
            if (claim.TenantId == Guid.Empty)
            {
                // Membership in the bound tenant was revoked between
                // refreshes — the bound refresh row is dead, the user
                // must re-login or switch-org. Fail closed.
                loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
                    .LogInformation(
                        "USER.REFRESH_MEMBERSHIP_LOST userId={UserId} tenantId={TenantId} — refresh denied; user must switch-org or re-login",
                        user.Id, boundTenantId);
                return Results.Json(
                    new { error = "Refresh token bound to a tenant the user no longer belongs to",
                          action = "POST /api/v1/auth/switch-org" },
                    statusCode: 401);
            }
            tenantId = boundTenantId;
            role = claim.Role;
        }
        else
        {
            // Legacy path — pre-Story-28-9 row (NULL TenantId). Resolve
            // via Settings JSON. Preserved behaviour for rows that
            // pre-date the migration; new rows ALWAYS take the bound path
            // above.
            var storedTenantId = ReadActiveTenantId(user) ?? Guid.Empty;
            tenantId = Guid.Empty;

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
            // else: zero memberships — leave tenantId Empty; access token
            // will emit empty `tenants` claim and middleware fail-closed.
        }

        // Rotate: revoke the presented token, mint and persist a new one.
        // Story 28-9 AC3 — stamp the consumed row with `rotation_consumed`
        // so the next time it appears at /auth/refresh we can recognise it
        // as the previous link in this lineage (vs a stolen-and-replayed
        // token from another lineage). Propagate the chain head to the new
        // row so reuse-detection sees the whole lineage as one unit; mint
        // a fresh chain head when the parent had none (pre-28-9 row).
        await refreshTokenRepo.RevokeAsync(
            token.Id, RefreshTokenRevokedReasons.RotationConsumed);
        var newRefresh = jwtService.GenerateRefreshToken();
        var newRefreshHash = HashToken(newRefresh);
        var inheritedChainHead = token.JtiChainHead ?? Guid.NewGuid();
        await refreshTokenRepo.CreateAsync(
            user.Id,
            tenantId: tenantId == Guid.Empty ? null : tenantId,
            newRefreshHash,
            DateTime.UtcNow.AddDays(7),
            jtiChainHead: inheritedChainHead);

        var accessToken = jwtService.GenerateAccessToken(
            user, tenantId == Guid.Empty ? null : tenantId, role, tenantClaims);

        // Update the session cookie to the new access JWT.
        httpContext.Response.Cookies.Append("tamma_session", accessToken,
            BuildSessionCookie(config, 900));

        // Story 37-10 — AUTH.TOKEN.REFRESHED (distinct from the reuse-detection
        // event, which is untouched above). Platform-edge event carrying the
        // resolved tenant when present. Best-effort emitter off the request scope.
        await EmitTokenRefreshedAsync(
            TryResolveEmitter(httpContext),
            user.Id, tenantId == Guid.Empty ? null : tenantId, user.Email, httpContext);

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

                // Story 28-9 AC3 — tag the revocation reason so SIEM /
                // SOC2 audit queries can tell a user-initiated logout-all
                // apart from an admin force-logout or a reuse-detected
                // burn.
                var revokedCount = await refreshTokenRepo.RevokeAllForUserAsync(
                    userId, RefreshTokenRevokedReasons.LogoutAll);
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
                        await refreshTokenRepo.RevokeAsync(
                            token.Id, RefreshTokenRevokedReasons.ManualLogout);
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

    // ─── Story 37-10 — curated auth sensitive-action emission ──────────────

    /// <summary>Best-effort resolve the sensitive-action emitter off the request
    /// scope. Swallows resolution failures (e.g. a test double whose fallback is a
    /// root provider that can't resolve the scoped emitter) — audit emission is a
    /// side effect that must never break auth.</summary>
    private static ISensitiveActionEmitter? TryResolveEmitter(HttpContext httpContext)
    {
        try { return httpContext.RequestServices?.GetService<ISensitiveActionEmitter>(); }
        catch { return null; }
    }

    /// <summary>Resolve the request fingerprint (ip + user-agent) with the same
    /// TrustedProxyResolver logic as <see cref="BuildAuthAuditEvent"/>. Falls back
    /// to the socket peer when the resolver isn't registered (test contexts).</summary>
    private static (string? Ip, string? UserAgent) ResolveRequestFingerprint(HttpContext httpContext)
    {
        TrustedProxyResolver? resolver;
        try { resolver = httpContext.RequestServices?.GetService<TrustedProxyResolver>(); }
        catch { resolver = null; }
        var ip = resolver is not null
            ? resolver.ResolveActorIp(httpContext)
            : httpContext.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(ip) && ip.Length > 64) ip = ip[..64];

        var ua = httpContext.Request.Headers.UserAgent.ToString();
        if (ua.Length > 256) ua = ua[..256];

        return (ip, string.IsNullOrEmpty(ua) ? null : ua);
    }

    /// <summary>Emit <c>AUTH.LOGIN.FAILURE</c> (platform-edge; no trusted tenant
    /// yet). Redaction-safe — the submitted email + machine-readable reason only,
    /// NEVER the password. No-op when the emitter isn't registered.</summary>
    private static async Task EmitLoginFailureAsync(
        ISensitiveActionEmitter? emitter, string email, string reason, HttpContext httpContext)
    {
        if (emitter is null) return;
        var (ip, ua) = ResolveRequestFingerprint(httpContext);

        // The submitted email is attacker-controlled and unbounded (no validation
        // filter). Clamp to the RFC 5321 max (254) before it enters the audit
        // tags/data so a padded value can't overflow the ActorEmailSnapshot
        // varchar(320) column downstream. The projector also caps defensively,
        // but capping at the source keeps the emitted event tidy too.
        if (email.Length > 254) email = email[..254];

        var tags = new Dictionary<string, string?>
        {
            ["reason"] = reason,
            ["actorEmail"] = email,
            ["source"] = "auth",
        };
        if (!string.IsNullOrEmpty(ip)) tags["ip"] = ip;
        if (!string.IsNullOrEmpty(ua)) tags["userAgent"] = ua;

        var data = new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["actorEmail"] = email,
            ["ip"] = ip,
            ["userAgent"] = ua,
        };

        await emitter.EmitAsync(
            SensitiveAction.ForPlatform(
                SensitiveActionCatalog.LoginFailure, tenantId: null, actorUserId: null, tags, data),
            httpContext.RequestAborted);
    }

    /// <summary>Emit <c>AUTH.LOGIN.SUCCESS</c>. Platform-edge event carrying the
    /// resolved active tenant (null => control-plane). No password material.</summary>
    private static async Task EmitLoginSuccessAsync(
        ISensitiveActionEmitter? emitter, User user, Guid? tenantId, HttpContext httpContext)
    {
        if (emitter is null) return;
        var (ip, ua) = ResolveRequestFingerprint(httpContext);

        var tags = new Dictionary<string, string?>
        {
            ["actorUserId"] = user.Id.ToString("D"),
            ["actorEmail"] = user.Email,
            ["source"] = "auth",
        };
        if (!string.IsNullOrEmpty(ip)) tags["ip"] = ip;
        if (!string.IsNullOrEmpty(ua)) tags["userAgent"] = ua;

        var data = new Dictionary<string, object?>
        {
            ["userId"] = user.Id.ToString("D"),
            ["actorEmail"] = user.Email,
            ["ip"] = ip,
            ["userAgent"] = ua,
        };

        await emitter.EmitAsync(
            SensitiveAction.ForPlatform(
                SensitiveActionCatalog.LoginSuccess, tenantId, user.Id, tags, data),
            httpContext.RequestAborted);
    }

    /// <summary>Emit <c>AUTH.TOKEN.REFRESHED</c> on a successful refresh-token
    /// rotation. Platform-edge event carrying the resolved tenant when present.</summary>
    private static async Task EmitTokenRefreshedAsync(
        ISensitiveActionEmitter? emitter, Guid userId, Guid? tenantId,
        string? email, HttpContext httpContext)
    {
        if (emitter is null) return;
        var (ip, ua) = ResolveRequestFingerprint(httpContext);

        var tags = new Dictionary<string, string?>
        {
            ["actorUserId"] = userId.ToString("D"),
            ["source"] = "auth",
        };
        if (!string.IsNullOrEmpty(email)) tags["actorEmail"] = email;
        if (!string.IsNullOrEmpty(ip)) tags["ip"] = ip;
        if (!string.IsNullOrEmpty(ua)) tags["userAgent"] = ua;

        var data = new Dictionary<string, object?>
        {
            ["userId"] = userId.ToString("D"),
            ["ip"] = ip,
            ["userAgent"] = ua,
        };

        await emitter.EmitAsync(
            SensitiveAction.ForPlatform(
                SensitiveActionCatalog.TokenRefreshed, tenantId, userId, tags, data),
            httpContext.RequestAborted);
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

    /// <summary>
    /// Story 28-9 AC3 follow-up (2026-05-30) — builds the
    /// <c>AUTH.REFRESH_REUSE_DETECTED</c> platform_events row emitted when
    /// a revoked refresh token is replayed. Unlike <see cref="BuildAuthAuditEvent"/>
    /// this is on an unauthenticated path (the caller's identity is the
    /// owner of the revoked token, not the request principal), so the
    /// shape is derived from the refresh-token row plus the request
    /// fingerprint resolved through <see cref="TrustedProxyResolver"/>.
    ///
    /// <para>Tags are the SIEM-filter surface: <c>userId</c>, <c>tenantId</c>
    /// (when bound), <c>jtiChainHead</c> (when known), <c>actorIp</c>,
    /// <c>source=auth</c>. Data carries the same identifiers plus
    /// <c>revokedTokenCount</c> so a dashboard can show "this incident
    /// burned N sessions" without joining back to refresh_tokens.
    /// <c>TenantId</c> on the event row mirrors <c>token.TenantId</c> so
    /// platform-scope reviews can spot pre-Story-28-9 rows distinctly from
    /// tenant-scoped incidents.</para>
    /// </summary>
    private static PlatformEvent BuildRefreshReuseDetectedEvent(
        Guid userId,
        Guid? tenantId,
        Guid? jtiChainHead,
        int revokedTokenCount,
        HttpContext httpContext)
    {
        var resolver = httpContext.RequestServices.GetService<TrustedProxyResolver>();
        string? actorIp = resolver is not null
            ? resolver.ResolveActorIp(httpContext)
            : httpContext.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(actorIp) && actorIp.Length > 64)
            actorIp = actorIp[..64];

        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 256) userAgent = userAgent[..256];

        var tags = new Dictionary<string, string?>
        {
            ["userId"] = userId.ToString("D"),
            ["source"] = "auth",
        };
        if (tenantId is not null && tenantId.Value != Guid.Empty)
            tags["tenantId"] = tenantId.Value.ToString("D");
        if (jtiChainHead is not null && jtiChainHead.Value != Guid.Empty)
            tags["jtiChainHead"] = jtiChainHead.Value.ToString("D");
        if (!string.IsNullOrEmpty(actorIp))
            tags["actorIp"] = actorIp;

        var data = new Dictionary<string, object?>
        {
            ["userId"] = userId.ToString("D"),
            ["tenantId"] = tenantId?.ToString("D"),
            ["jtiChainHead"] = jtiChainHead?.ToString("D"),
            ["actorIp"] = actorIp,
            ["userAgent"] = userAgent,
            ["revokedTokenCount"] = revokedTokenCount,
        };

        return new PlatformEvent
        {
            Type = "AUTH.REFRESH_REUSE_DETECTED",
            TenantId = tenantId,
            UserId = userId,
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
        // Story 28-9 AC3 — tag the bulk revoke with the explicit reason so
        // an admin tracing "why did every session vanish" sees the
        // password-reset breadcrumb without consulting platform_events.
        await refreshTokenRepo.RevokeAllForUserAsync(
            user.Id, RefreshTokenRevokedReasons.PasswordReset);

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
        ControlPlaneDbContext cpDb,
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

        // Story 28-9 AC2 — the handover (revoke-old + insert-new + persist
        // active tenant) is a SINGLE CP transaction so a crash mid-sequence
        // can never leave a revoked-old-without-new-issued (half-rotated)
        // session. Concurrent switch-org calls from the same user are
        // serialised by a Postgres SELECT ... FOR UPDATE row-lock on the
        // user's current refresh-token row: the second caller blocks here
        // until the first caller's transaction commits, then proceeds against
        // the first caller's rotated state. The access-token issue is pure
        // compute and the cookie write + audit-event publish are deliberately
        // AFTER commit (the spec: "single CP transaction PLUS a RabbitMQ
        // event publish after commit").
        var newRefresh = jwtService.GenerateRefreshToken();
        var newRefreshHash = HashToken(newRefresh);

        var presented = req.RefreshToken;
        int revokedAllCount = 0;
        bool revokedAllPath = false;

        await using (var tx = await cpDb.Database.BeginTransactionAsync())
        {
            // Acquire the FOR UPDATE lock FIRST so concurrent switch-org
            // calls from the same user serialise on the user's current
            // refresh row before any mutation happens. Returns null when the
            // user holds no active refresh token (rootless session) — in that
            // case there is nothing to lock and nothing to serialise against,
            // so we proceed (the insert below still runs inside the txn).
            await refreshTokenRepo.FindActiveTokenForUpdateAsync(userId);

            // Persist new active tenant inside the transaction so a refresh
            // racing with switch-org converges on the same tenant. Goes
            // through PersistActiveTenantAsync because the Phase-2
            // prevent_tenant_id_change trigger blocks uuid→uuid updates of
            // users.TenantId — Settings JSON is the runtime stash.
            await PersistActiveTenantAsync(userRepo, userId, req.TenantId);

            // Story 28-9 — rotate the refresh token alongside the access
            // token so the entire session is bound to the new tenant.
            // Caller-supplied refresh token is optional — if not present, all
            // of the user's existing refresh tokens are revoked so a stale
            // tab can't keep re-issuing access tokens for the previous tenant.
            if (!string.IsNullOrEmpty(presented))
            {
                var presentedHash = HashToken(presented);
                var existing = await refreshTokenRepo.GetByTokenHashAsync(presentedHash);
                if (existing is not null && existing.UserId == userId && existing.RevokedAt is null)
                {
                    // Story 28-9 AC3 — explicit reason so the audit row records
                    // the switch-org context (vs a generic manual logout).
                    await refreshTokenRepo.RevokeAsync(
                        existing.Id, RefreshTokenRevokedReasons.SwitchOrg);
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
                // Story 28-9 AC3 — explicit reason for SIEM / SOC2.
                revokedAllCount = await refreshTokenRepo.RevokeAllForUserAsync(
                    userId, RefreshTokenRevokedReasons.SwitchOrg);
                revokedAllPath = true;
            }

            // Story 28-9 AC3 — bind the new refresh row to the TARGET tenant.
            // Switch-org STARTS A NEW CHAIN because the tenant context changed:
            // a token from the previous lineage could not refresh against the
            // new tenant (tenant_mismatch_on_refresh) and the lineage's chain
            // head therefore terminates at the old refresh row.
            await refreshTokenRepo.CreateAsync(
                userId,
                tenantId: req.TenantId,
                newRefreshHash,
                DateTime.UtcNow.AddDays(7),
                jtiChainHead: Guid.NewGuid());

            // Commit the atomic handover. After this point the rotation is
            // durable; the token issue, cookie write, and audit emission are
            // post-commit best-effort.
            await tx.CommitAsync();
        }

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
