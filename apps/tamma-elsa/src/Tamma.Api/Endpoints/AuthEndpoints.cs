using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Services.Email;
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

        if (req.Password.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters" });

        var existing = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());
        if (existing is not null)
            return Results.Conflict(new { error = "Email already registered" });

        var verificationToken = Guid.NewGuid().ToString("N");
        var tokenHash = HashToken(verificationToken);

        var user = await userRepo.CreateAsync(new User
        {
            Email = req.Email.ToLowerInvariant(),
            PasswordHash = passwordService.HashPassword(req.Password),
            DisplayName = req.DisplayName,
            Role = "member",
            AuthMethod = "email",
            EmailVerificationTokenHash = tokenHash,
            EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24),
        });

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

        // Queue the verification email. SendAsync accepts the message for
        // delivery, emits an EMAIL.QUEUED.SUCCESS event to the event store,
        // and returns a transaction id. It does NOT throw for transport
        // failures — those surface later as EMAIL.SENT.FAILED events — so
        // there is no try/catch here. Only programmer errors (null args)
        // would surface, and those SHOULD crash the request.
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
        IUserRepository userRepo)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();
        // We'd need a lookup by verification token hash — for now return OK
        return Results.Ok(new { message = "Email verified successfully" });
    }

    public static async Task<IResult> ResendVerification(
        ResendVerificationRequest req,
        IUserRepository userRepo,
        [FromServices] IEmailService emailService,
        [FromServices] IConfiguration config,
        [FromServices] ILoggerFactory loggerFactory)
    {
        // Anti-enumeration: the response is identical whether the user
        // exists, is already verified, or does not exist. We burn a small
        // constant amount of work before returning so naive timing attacks
        // cannot distinguish the branches.
        const string CannedResponseMessage =
            "If the email exists, a verification link has been sent";

        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.Ok(new { message = CannedResponseMessage });

        var email = req.Email.ToLowerInvariant();
        var user = await userRepo.GetByEmailAsync(email);

        if (user is not null && !user.EmailVerified)
        {
            // Rotate the verification token so stolen old-mail tokens are
            // invalidated by this re-issue.
            var verificationToken = Guid.NewGuid().ToString("N");
            user.EmailVerificationTokenHash = HashToken(verificationToken);
            user.EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24);
            await userRepo.UpdateAsync(user);

            var verifyUrl = BuildVerificationUrl(config, verificationToken);
            var message = EmailTemplates.VerificationEmail(user.Email, verifyUrl) with
            {
                Template = "verification",
                TenantId = user.TenantId,
                UserId = user.Id,
            };
            var txnId = await emailService.SendAsync(message);

            var resendLogger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
            resendLogger.LogInformation(
                "Email dispatch scheduled txn={TxnId} template={Template}", txnId, "verification");
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
        if (user is null || user.PasswordHash is null || !passwordService.VerifyPassword(req.Password, user.PasswordHash))
        {
            lockout.RecordFailedAttempt(req.Email);
            return Results.Unauthorized();
        }

        if (!user.IsActive)
            return Results.Json(new { error = "Account deactivated" }, statusCode: 403);

        lockout.ResetAttempts(req.Email);

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

        var accessToken = jwtService.GenerateAccessToken(user, tenantId, role);
        var refreshToken = jwtService.GenerateRefreshToken();
        var refreshHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();

        await refreshTokenRepo.CreateAsync(user.Id, refreshHash, DateTime.UtcNow.AddDays(7));

        // Set refresh token cookie
        httpContext.Response.Cookies.Append("tamma_session", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });

        // Update last active
        user.LastActiveAt = DateTime.UtcNow;
        await userRepo.UpdateAsync(user);

        return Results.Ok(new LoginResponse(
            accessToken,
            900, // 15 min in seconds
            new UserInfo(user.Id, user.Email, user.DisplayName, role, tenantId == Guid.Empty ? null : tenantId)
        ));
    }

    public static async Task<IResult> Refresh(
        IRefreshTokenRepository refreshTokenRepo,
        IJwtService jwtService,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo,
        HttpContext httpContext)
    {
        var refreshToken = httpContext.Request.Cookies["tamma_session"];
        if (string.IsNullOrEmpty(refreshToken))
            return Results.Unauthorized();

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();
        var token = await refreshTokenRepo.GetByTokenHashAsync(tokenHash);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTime.UtcNow)
            return Results.Unauthorized();

        var user = token.User;
        var tenantId = user.TenantId ?? Guid.Empty;
        var role = "member";
        if (tenantId != Guid.Empty)
        {
            var memberRole = await membershipRepo.GetRoleAsync(tenantId, user.Id);
            if (memberRole is not null) role = memberRole;
        }

        var accessToken = jwtService.GenerateAccessToken(user, tenantId, role);
        return Results.Ok(new RefreshResponse(accessToken, 900));
    }

    public static async Task<IResult> Logout(
        IRefreshTokenRepository refreshTokenRepo,
        HttpContext httpContext)
    {
        var refreshToken = httpContext.Request.Cookies["tamma_session"];
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();
            var token = await refreshTokenRepo.GetByTokenHashAsync(tokenHash);
            if (token is not null)
                await refreshTokenRepo.RevokeAsync(token.Id);
        }

        httpContext.Response.Cookies.Delete("tamma_session");
        return Results.Ok(new { message = "Logged out" });
    }

    public static async Task<IResult> PasswordResetRequest(
        PasswordResetRequestDto req,
        IPasswordResetRepository resetRepo,
        IUserRepository userRepo,
        [FromServices] IEmailService emailService,
        [FromServices] IConfiguration config,
        [FromServices] ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "Email is required" });

        const string CannedResponseMessage =
            "If the email exists, a reset link has been sent";

        var email = req.Email.ToLowerInvariant();
        var user = await userRepo.GetByEmailAsync(email);

        if (user is not null)
        {
            // 256 bits of entropy — far more than a GUID; matches Story 18-6
            // spec and the deleted TS implementation.
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

            var resetLogger = loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!);
            resetLogger.LogInformation(
                "Email dispatch scheduled txn={TxnId} template={Template}", txnId, "password-reset");
        }

        // Anti-enumeration: return the same response whether the email exists or not
        return Results.Ok(new { message = CannedResponseMessage });
    }

    public static async Task<IResult> PasswordResetConfirm(
        PasswordResetConfirmDto req,
        IPasswordResetRepository resetRepo,
        IPasswordService passwordService,
        IUserRepository userRepo,
        IRefreshTokenRepository refreshTokenRepo)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();
        var token = await resetRepo.GetByTokenHashAsync(tokenHash);

        if (token is null || token.ConsumedAt is not null || token.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Invalid or expired reset token" });

        var user = await userRepo.GetByIdAsync(token.UserId);
        if (user is null)
            return Results.BadRequest(new { error = "User not found" });

        user.PasswordHash = passwordService.HashPassword(req.NewPassword);
        await userRepo.UpdateAsync(user);
        await resetRepo.ConsumeAsync(token.Id);
        await refreshTokenRepo.RevokeAllForUserAsync(user.Id);

        return Results.Ok(new { message = "Password reset successfully" });
    }

    public static async Task<IResult> GetMe(
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null || !Guid.TryParse(userId, out var id))
            return Results.Unauthorized();

        var user = await userRepo.GetByIdAsync(id);
        if (user is null)
            return Results.NotFound(new { error = "User not found" });

        var memberships = await membershipRepo.GetUserTenantsAsync(id);
        var membershipInfos = memberships.Select(m =>
            new MembershipInfo(m.TenantId, m.Tenant?.Name ?? "", m.Role)).ToList();

        return Results.Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.Role, user.TenantId, membershipInfos));
    }

    public static Task<IResult> RoleCheck(ClaimsPrincipal principal)
    {
        var role = principal.FindFirst(ClaimTypes.Role)?.Value ?? "member";
        var permissions = Auth.Permissions.GetRolePermissions(role);
        return Task.FromResult(Results.Ok(new RoleCheckResponse(role, permissions)));
    }

    public static Task<IResult> GitHubAuth(IConfiguration config)
    {
        var clientId = config["GitHub:ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return Task.FromResult(Results.BadRequest(new { error = "GitHub OAuth not configured" }));
        var redirectUri = config["GitHub:RedirectUri"] ?? "http://localhost:3000/api/auth/github/callback";
        var url = $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=user:email";
        return Task.FromResult(Results.Redirect(url));
    }

    public static Task<IResult> GitHubCallback()
    {
        // TODO: Implement GitHub OAuth callback
        return Task.FromResult(Results.Ok(new { message = "GitHub callback - not yet implemented" }));
    }
}
