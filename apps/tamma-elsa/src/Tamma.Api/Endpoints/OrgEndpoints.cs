using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Api.Authorization;
using Tamma.Api.Dtos.Orgs;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.RateLimit;
using Tamma.Api.Validation;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Tenant / organization endpoints. Path-tenant mutations rely on
/// <see cref="RequireTenantMembershipFilter"/> (registered in Program.cs)
/// to verify the caller is a member of the route tenant before any
/// handler runs (findings 001, 024). Role-hierarchy / last-owner
/// invariants live in the handlers themselves (findings 012, 013, 020).
/// </summary>
public static class OrgEndpoints
{
    private const string EmailDomain = "tamma";

    public static async Task<IResult> CreateOrg(
        CreateOrgRequest req,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IEventRepository events,
        ClaimsPrincipal principal)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        // ── Validation (finding 007) ─────────────────────────────────────────
        if (!SlugValidation.IsValidName(req.Name))
            return Results.BadRequest(new { error = "Name must be between 2 and 100 characters" });

        var slug = req.Slug?.ToLowerInvariant() ?? string.Empty;
        if (!SlugValidation.IsValidSlug(slug))
            return Results.BadRequest(new { error = "Slug must be 3-40 characters, lowercase alphanumeric and hyphens only, cannot start or end with hyphen" });

        if (SlugValidation.IsReservedSlug(slug))
            return Results.BadRequest(new { error = "This slug is reserved and cannot be used" });

        var existing = await tenantRepo.GetBySlugAsync(slug);
        if (existing is not null)
            return Results.Conflict(new { error = "An organization with this slug already exists" });

        var tenant = await tenantRepo.CreateAsync(new Tenant
        {
            Name = req.Name.Trim(),
            Slug = slug,
            Type = "org",
            OwnerId = userId,
        });

        await membershipRepo.AddAsync(tenant.Id, userId.Value, TenantRoleHierarchy.Owner);

        // Finding 009: persist as the user's active tenant.
        await userRepo.UpdateActiveTenantAsync(userId.Value, tenant.Id);

        // Finding 008: emit DCB event.
        await EmitTenantEvent(events, "TENANT.CREATED.SUCCESS", tenant.Id, userId.Value, new
        {
            slug = tenant.Slug,
            name = tenant.Name,
        });

        return Results.Created($"/api/v1/orgs/{tenant.Id}",
            BuildOrgResponse(tenant));
    }

    public static async Task<IResult> GetOrg(Guid tenantId, ITenantRepository tenantRepo)
    {
        // Membership gate is enforced by RequireTenantMembershipFilter.
        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = "Organization not found" });
        return Results.Ok(BuildOrgResponse(tenant));
    }

    public static async Task<IResult> UpdateOrgSettings(
        Guid tenantId,
        UpdateOrgSettingsRequest req,
        ITenantRepository tenantRepo,
        HttpContext httpContext)
    {
        // Path-tenant membership gate already ran; require admin+ for settings.
        if (!RoleAtLeast(httpContext, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = "Organization not found" });

        var hasName = req.Name is not null;
        var hasPlan = req.Plan is not null;
        var hasSettings = req.Settings is not null;

        if (!hasName && !hasPlan && !hasSettings)
            return Results.BadRequest(new { error = "No fields to update" });

        if (hasName)
        {
            if (!SlugValidation.IsValidName(req.Name))
                return Results.BadRequest(new { error = "Name must be between 2 and 100 characters" });
            tenant.Name = req.Name!.Trim();
        }

        if (hasPlan)
        {
            var plan = req.Plan!.ToLowerInvariant();
            if (plan is not "free" and not "pro" and not "enterprise")
                return Results.BadRequest(new { error = "Plan must be one of: free, pro, enterprise" });
            tenant.Plan = plan;
        }

        if (hasSettings)
        {
            tenant.Settings = JsonSerializer.Serialize(req.Settings);
        }

        await tenantRepo.UpdateAsync(tenant);
        return Results.Ok(BuildOrgResponse(tenant));
    }

    public static async Task<IResult> ListMembers(
        Guid tenantId,
        ITenantMembershipRepository membershipRepo,
        int? limit,
        int? offset)
    {
        // Membership gate enforced by RequireTenantMembershipFilter.
        var clampedLimit = Math.Clamp(limit ?? 50, 1, 100);
        var clampedOffset = Math.Max(offset ?? 0, 0);

        var (members, total) = await membershipRepo.ListByTenantAsync(tenantId, clampedLimit, clampedOffset);
        var response = members.Select(m =>
            new MemberResponse(m.UserId, m.Role, m.JoinedAt, m.User?.DisplayName, m.User?.Email)).ToList();
        return Results.Ok(new
        {
            members = response,
            total,
            limit = clampedLimit,
            offset = clampedOffset,
        });
    }

    public static async Task<IResult> UpdateMemberRole(
        Guid tenantId,
        Guid userId,
        UpdateMemberRoleRequest req,
        ITenantMembershipRepository membershipRepo,
        IEventRepository events,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        // Validate role string (finding 012).
        if (!TenantRoleHierarchy.IsValid(req.Role))
            return Results.BadRequest(new { error = "role must be one of: owner, admin, member" });

        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
        {
            // Filter chain misconfigured — fail closed.
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);
        }

        var targetRole = await membershipRepo.GetRoleAsync(tenantId, userId);
        if (targetRole is null)
            return Results.NotFound(new { error = "User is not a member of this organization" });

        var requesterLevel = TenantRoleHierarchy.Level(requesterRole);
        var targetLevel = TenantRoleHierarchy.Level(targetRole);
        var newLevel = TenantRoleHierarchy.Level(req.Role);
        var ownerLevel = TenantRoleHierarchy.Level(TenantRoleHierarchy.Owner);

        // Only owner may touch owner-level on either side.
        if (requesterRole != TenantRoleHierarchy.Owner
            && (newLevel >= ownerLevel || targetLevel >= ownerLevel))
        {
            return Results.Json(new { error = "Only owners can change owner-level roles" }, statusCode: 403);
        }

        // Admin cannot change a peer or promote to / above their level.
        if (requesterRole == TenantRoleHierarchy.Admin)
        {
            if (targetLevel >= requesterLevel)
                return Results.Json(new { error = "Cannot change role of users at or above your level" }, statusCode: 403);
            if (newLevel >= requesterLevel)
                return Results.Json(new { error = "Cannot promote users to or above your level" }, statusCode: 403);
        }

        // Last-owner guard on demote.
        if (targetRole == TenantRoleHierarchy.Owner && req.Role != TenantRoleHierarchy.Owner)
        {
            var owners = await membershipRepo.CountOwnersAsync(tenantId);
            if (owners <= 1)
                return Results.BadRequest(new { error = "Cannot remove the last owner" });
        }

        await membershipRepo.UpdateRoleAsync(tenantId, userId, req.Role);

        // Story 18-7 task 1: emit role-changed event so the tenant audit
        // log shows every role mutation. Same fire-and-forget shape used
        // by every other tenant emitter — failure does not unwind the role
        // change (which is the user-visible side effect).
        await EmitTenantEvent(events, "TENANT.MEMBER_ROLE_CHANGED.SUCCESS", tenantId, callerId.Value, new
        {
            targetUserId = userId.ToString(),
            oldRole = targetRole,
            newRole = req.Role,
        });

        return Results.Ok(new { message = "Role updated", tenantId, userId, role = req.Role });
    }

    public static async Task<IResult> RemoveMember(
        Guid tenantId,
        Guid userId,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IEventRepository events,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);

        // Must be admin+ to remove anyone.
        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        var targetRole = await membershipRepo.GetRoleAsync(tenantId, userId);
        if (targetRole is null)
            return Results.NotFound(new { error = "User is not a member of this organization" });

        // Cannot remove self if last owner.
        if (userId == callerId.Value && targetRole == TenantRoleHierarchy.Owner)
        {
            var owners = await membershipRepo.CountOwnersAsync(tenantId);
            if (owners <= 1)
                return Results.BadRequest(new { error = "Cannot remove yourself as the last owner" });
        }

        // Admins cannot remove owners.
        if (requesterRole != TenantRoleHierarchy.Owner && targetRole == TenantRoleHierarchy.Owner)
            return Results.Json(new { error = "Cannot remove an owner" }, statusCode: 403);

        await membershipRepo.RemoveAsync(tenantId, userId);

        // Switch the removed user's active tenant if it was this one.
        await userRepo.SwitchActiveTenantAwayFromAsync(userId, tenantId);

        await EmitTenantEvent(events, "TENANT.MEMBER_REMOVED.SUCCESS", tenantId, callerId.Value, new
        {
            removedUserId = userId.ToString(),
            removedRole = targetRole,
        });

        return Results.Ok(new { ok = true });
    }

    public static async Task<IResult> CreateInvite(
        Guid tenantId,
        CreateOrgInviteRequest req,
        ITenantRepository tenantRepo,
        IInviteRepository inviteRepo,
        IEmailService emailService,
        IEventRepository events,
        ILoggerFactory loggerFactory,
        IConfiguration config,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);

        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher to invite" }, statusCode: 403);

        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "email is required" });

        if (!TenantRoleHierarchy.IsValid(req.Role))
            return Results.BadRequest(new { error = "role must be one of: owner, admin, member" });

        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null)
            return Results.NotFound(new { error = "Organization not found" });

        // Strong token: 32 random bytes hex-encoded → 256 bits entropy
        // (finding 014).
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var tokenHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();

        var emailNormalized = req.Email.Trim().ToLowerInvariant();
        var invite = await inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = emailNormalized,
            Role = req.Role,
            InviteTokenHash = tokenHash,
            InvitedBy = callerId.Value,
            // 72 hours (TS parity).
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        });

        // Build the dashboard accept URL.
        var dashboardBase = (config["Dashboard:Url"] ?? "http://localhost:3001").TrimEnd('/');
        var acceptUrl = $"{dashboardBase}/invites/accept?token={Uri.EscapeDataString(rawToken)}";

        var inviterName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? "A teammate";

        // Fire-and-forget email send. Failures log but do not 500 the request.
        var logger = loggerFactory.CreateLogger("OrgEndpoints.CreateInvite");
        _ = Task.Run(async () =>
        {
            try
            {
                await emailService.SendAsync(
                    EmailTemplates.TenantInviteEmail(
                        emailNormalized, tenant.Name, inviterName, acceptUrl, req.Role) with
                    {
                        TenantId = tenantId,
                        UserId = callerId,
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send tenant invite email for invite {InviteId}", invite.Id);
            }
        });

        await EmitTenantEvent(events, "TENANT.MEMBER_INVITED.SUCCESS", tenantId, callerId.Value, new
        {
            email = emailNormalized,
            role = req.Role,
            inviteId = invite.Id.ToString(),
        });

        // Response no longer leaks the raw token (finding 014).
        return Results.Created($"/api/v1/orgs/{tenantId}/invites/{invite.Id}",
            new { id = invite.Id, email = invite.Email, role = invite.Role, expiresAt = invite.ExpiresAt });
    }

    public static async Task<IResult> ListInvites(
        Guid tenantId,
        IInviteRepository inviteRepo,
        HttpContext httpContext)
    {
        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);
        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        var invites = await inviteRepo.ListPendingByTenantAsync(tenantId);
        var projected = invites.Select(i =>
            new PendingInviteResponse(i.Id, i.Email, i.Role, i.InvitedBy, i.ExpiresAt, i.CreatedAt)).ToList();
        return Results.Ok(new { invites = projected });
    }

    public static async Task<IResult> DeleteInvite(
        Guid tenantId,
        Guid inviteId,
        IInviteRepository inviteRepo,
        HttpContext httpContext)
    {
        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);
        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        var deleted = await inviteRepo.DeleteScopedAsync(tenantId, inviteId);
        if (!deleted)
            return Results.NotFound(new { error = "Invite not found" });
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// Story 18-7 task 3 — extend a pending invite's expiry by 72 h and
    /// re-dispatch the invite email. Token + token_hash are NOT rotated:
    /// the invite-id stays stable and the original accept link keeps
    /// working (UI invariant per brief AC §2). Rate-limited to 3 calls
    /// per invite per hour via <see cref="IRateLimitService"/>.
    /// </summary>
    public static async Task<IResult> ResendInvite(
        Guid tenantId,
        Guid inviteId,
        ITenantRepository tenantRepo,
        IInviteRepository inviteRepo,
        IEmailService emailService,
        IRateLimitService rateLimits,
        IEventRepository events,
        ILoggerFactory loggerFactory,
        IConfiguration config,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);
        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        // Tenant-scoped lookup — guards against id-spoofing across tenants.
        var invite = await inviteRepo.GetByIdScopedAsync(tenantId, inviteId);
        if (invite is null)
            return Results.NotFound(new { error = "Invite not found" });

        if (invite.AcceptedAt is not null)
            return Results.BadRequest(new { error = "Invite has already been accepted" });
        if (invite.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Invite has expired" });

        // Rate limit: 3 resends per invite per hour. Same scope/key style
        // used by AuthEndpoints.ResendVerification.
        var rateKey = $"{tenantId}:{inviteId}";
        if (rateLimits.IsLimited("resend-tenant-invite", rateKey))
        {
            return Results.Json(new
            {
                error = "rate_limited",
                message = "Too many resends. Try again later.",
            }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        rateLimits.Record("resend-tenant-invite", rateKey);

        var newExpiresAt = DateTime.UtcNow.AddHours(72);
        await inviteRepo.ExtendExpiryAsync(invite.Id, newExpiresAt);

        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        var tenantName = tenant?.Name ?? "your organization";

        // Per story brief AC §2: do NOT mint a new token. The original raw
        // token is gone (only the hash is stored), so the resend email is
        // a reminder pointing back to the dashboard accept route. The
        // dashboard's invite-accept page resolves the user's pending
        // invites by email + tenant id and lets them complete signup
        // without re-entering the raw token. Until that page exists, the
        // simplest no-leak fallback is to embed only the invite id —
        // attempting to derive the original raw token from the hash is
        // not cryptographically possible.
        var dashboardBase = (config["Dashboard:Url"] ?? "http://localhost:3001").TrimEnd('/');
        var pendingUrl = $"{dashboardBase}/invites/pending?inviteId={Uri.EscapeDataString(invite.Id.ToString())}";

        var inviterName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? "A teammate";

        var logger = loggerFactory.CreateLogger("OrgEndpoints.ResendInvite");
        _ = Task.Run(async () =>
        {
            try
            {
                await emailService.SendAsync(
                    EmailTemplates.TenantInviteEmail(
                        invite.Email ?? string.Empty, tenantName, inviterName, pendingUrl, invite.Role) with
                    {
                        TenantId = tenantId,
                        UserId = callerId,
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resend tenant invite email for invite {InviteId}", invite.Id);
            }
        });

        await EmitTenantEvent(events, "TENANT.MEMBER_INVITE_RESENT.SUCCESS", tenantId, callerId.Value, new
        {
            inviteId = invite.Id.ToString(),
            email = invite.Email,
            role = invite.Role,
            newExpiresAt,
        });

        return Results.Ok(new { id = invite.Id, expiresAt = newExpiresAt });
    }

    /// <summary>
    /// Story 18-7 task 2 — tenant-scoped audit-log read. Returns events
    /// whose <c>TenantId</c> matches the path tenant, most-recent first,
    /// paginated. <see cref="RequireTenantMembershipFilter"/> already
    /// enforces caller membership; this handler additionally requires
    /// admin+. The event store's global query filter provides defence
    /// in depth (cross-tenant rows are rejected even if the explicit
    /// filter regresses).
    /// </summary>
    public static async Task<IResult> ListTenantAudit(
        Guid tenantId,
        IEventRepository events,
        ITenantContext tenantContext,
        HttpContext httpContext,
        int? limit,
        int? offset,
        string? type)
    {
        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);
        if (!TenantRoleHierarchy.IsAtLeast(requesterRole, TenantRoleHierarchy.Admin))
            return Results.Json(new { error = "Requires admin role or higher" }, statusCode: 403);

        var clampedLimit = Math.Clamp(limit ?? 50, 1, 200);
        var clampedOffset = Math.Max(offset ?? 0, 0);

        // Set ambient tenant context so the global query filter on
        // domain_events triggers the defence-in-depth path. Idempotent —
        // RequireTenantMembershipFilter usually sets this too, but be
        // explicit so this handler is safe when called in isolation.
        tenantContext.SetTenantId(tenantId);

        var (rows, total) = await events.ListByTenantAsync(tenantId, type, clampedLimit, clampedOffset);

        var projected = rows.Select(e => new AuditEventResponse(
            e.Id,
            e.Type,
            e.CreatedAt,
            e.Tags,
            e.Data)).ToList();

        return Results.Ok(new
        {
            events = projected,
            total,
            limit = clampedLimit,
            offset = clampedOffset,
        });
    }

    public static async Task<IResult> AcceptInvite(
        AcceptInviteRequest req,
        IInviteRepository inviteRepo,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IEventRepository events,
        ClaimsPrincipal principal)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Token))
            return Results.BadRequest(new { error = "Invalid or expired invite token" });

        var tokenHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(req.Token)))
            .ToLowerInvariant();

        var invite = await inviteRepo.GetByTokenHashAsync(tokenHash);
        if (invite is null)
            return Results.BadRequest(new { error = "Invalid or expired invite token" });
        if (invite.AcceptedAt is not null)
            return Results.BadRequest(new { error = "Invite has already been accepted" });
        if (invite.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Invite has expired" });

        // Idempotent: already a member ⇒ mark accepted, return friendly message.
        var existingRole = await membershipRepo.GetRoleAsync(invite.TenantId, userId.Value);
        if (existingRole is not null)
        {
            await inviteRepo.AcceptAsync(invite.Id);
            return Results.Ok(new
            {
                tenantId = invite.TenantId,
                role = existingRole,
                message = "You are already a member of this organization",
            });
        }

        await membershipRepo.AddAsync(invite.TenantId, userId.Value, invite.Role);
        await inviteRepo.AcceptAsync(invite.Id);

        // Set as active tenant if user has none yet (finding 017).
        var user = await userRepo.GetByIdAsync(userId.Value);
        if (user is not null && (user.TenantId is null || user.TenantId.Value == Guid.Empty))
        {
            await userRepo.UpdateActiveTenantAsync(userId.Value, invite.TenantId);
        }

        await EmitTenantEvent(events, "TENANT.MEMBER_JOINED.SUCCESS", invite.TenantId, userId.Value, new
        {
            role = invite.Role,
            inviteId = invite.Id.ToString(),
        });

        return Results.Ok(new
        {
            tenantId = invite.TenantId,
            role = invite.Role,
            message = "You have joined the organization",
        });
    }

    // Story 28-9: the original `OrgEndpoints.SwitchOrg` (Story 18-3) called
    // `IUserRepository.UpdateActiveTenantAsync` directly, which at runtime
    // triggers the Phase-2 `prevent_tenant_id_change` Postgres trigger and
    // fails for every user whose personal tenant is already set (uuid→uuid
    // update path is blocked). The canonical handler now lives at
    // `AuthEndpoints.SwitchOrg` (POST /api/v1/auth/switch-org), which stashes
    // the runtime active tenant in `users.Settings.activeTenantId` JSON —
    // avoiding the trigger — and additionally rotates the refresh token.
    // The method and its route registration have both been deleted; the old
    // path `POST /api/v1/orgs/switch-org` now 404s (covered by
    // `Tamma.Api.Tests.Orgs.OrgSwitchOrgRoute404Tests`).

    public static async Task<IResult> ListTenants(
        ITenantRepository tenantRepo,
        ClaimsPrincipal principal)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        var activeTid = principal.FindFirst("tenantId")?.Value ?? principal.FindFirst("tid")?.Value;
        Guid? activeTenantId = Guid.TryParse(activeTid, out var parsed) ? parsed : null;

        var rows = await tenantRepo.ListMembershipsByUserAsync(userId.Value);
        var response = rows.Select(r => new TenantSummaryResponse(
            r.Tenant.Id,
            r.Tenant.Name,
            r.Tenant.Slug,
            r.Tenant.Plan,
            r.Role,
            r.JoinedAt,
            activeTenantId is not null && r.Tenant.Id == activeTenantId.Value
        )).ToList();
        return Results.Ok(new { tenants = response });
    }

    public static async Task<IResult> TransferOwnership(
        Guid tenantId,
        TransferOwnershipRequest req,
        // Story 19-6: per-request tx context binds to TammaAppDbContext so
        // RLS + fail-closed EF filter both fire on this code path. The
        // tenancy-scoped repositories injected separately resolve their own
        // contexts; the transaction here only spans the inline `tenant`
        // mutation since EF transactions don't cross DbContexts.
        TammaAppDbContext db,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IEventRepository events,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        // Requester must be the current owner per the path-tenant role.
        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole != TenantRoleHierarchy.Owner)
            return Results.Json(new { error = "Only the owner can transfer ownership" }, statusCode: 403);

        if (req.NewOwnerId == callerId.Value)
            return Results.BadRequest(new { error = "same_user" });

        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null || tenant.DeletedAt is not null)
            return Results.NotFound(new { error = "Tenant not found or deleted" });

        var newOwnerRole = await membershipRepo.GetRoleAsync(tenantId, req.NewOwnerId);
        if (newOwnerRole is null)
            return Results.BadRequest(new { error = "not_a_member" });

        // Atomic: wrap the role swap + tenants.OwnerId update in one tx.
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await membershipRepo.UpdateRoleAsync(tenantId, req.NewOwnerId, TenantRoleHierarchy.Owner);
            await membershipRepo.UpdateRoleAsync(tenantId, callerId.Value, TenantRoleHierarchy.Admin);
            tenant.OwnerId = req.NewOwnerId;
            await tenantRepo.UpdateAsync(tenant);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        await EmitTenantEvent(events, "TENANT.OWNERSHIP_TRANSFERRED.SUCCESS", tenantId, callerId.Value, new
        {
            previousOwnerId = callerId.Value.ToString(),
            newOwnerId = req.NewOwnerId.ToString(),
        });

        return Results.Ok(new
        {
            tenantId,
            previousOwnerId = callerId.Value,
            newOwnerId = req.NewOwnerId,
        });
    }

    public static async Task<IResult> DeleteOrg(
        Guid tenantId,
        // Story 19-6: same rationale as TransferOwnership above.
        TammaAppDbContext db,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IInviteRepository inviteRepo,
        IUserRepository userRepo,
        IDeleteConfirmationService confirmation,
        IEventRepository events,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        string? confirm)
    {
        var callerId = ResolveUserId(principal);
        if (callerId is null) return Results.Unauthorized();

        var requesterRole = httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (requesterRole != TenantRoleHierarchy.Owner)
            return Results.Json(new { error = "Only the owner can delete the organization" }, statusCode: 403);

        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null || tenant.DeletedAt is not null)
            return Results.NotFound(new { error = "Tenant not found" });

        // last_tenant guard: caller must have at least one other tenant.
        var callerTenants = await membershipRepo.GetUserTenantsAsync(callerId.Value);
        if (callerTenants.Count <= 1)
            return Results.Json(
                new { error = "last_tenant", message = "Cannot delete your only organization. Create a replacement first." },
                statusCode: StatusCodes.Status409Conflict);

        // Phase 2 (hard-delete) — caller passed ?confirm=<token>.
        if (!string.IsNullOrEmpty(confirm))
        {
            if (!confirmation.Verify(confirm, tenantId, callerId.Value))
                return Results.BadRequest(new { error = "confirmation_expired", message = "Invalid or expired confirmation token" });

            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var allMembers = await membershipRepo.ListAllByTenantAsync(tenantId);
                foreach (var m in allMembers)
                {
                    await membershipRepo.RemoveAsync(tenantId, m.UserId);
                    await userRepo.SwitchActiveTenantAwayFromAsync(m.UserId, tenantId);
                }
                await inviteRepo.DeleteAllByTenantAsync(tenantId);
                await tenantRepo.SoftDeleteAsync(tenantId);
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            await EmitTenantEvent(events, "TENANT.PURGED.SUCCESS", tenantId, callerId.Value, new
            {
                phase = "hard-delete",
            });

            return Results.NoContent();
        }

        // Phase 1 (soft-delete) — mint the HMAC confirmation token and return 202.
        await tenantRepo.SoftDeleteAsync(tenantId);
        await userRepo.SwitchActiveTenantAwayFromAsync(callerId.Value, tenantId);
        var token = confirmation.Generate(tenantId, callerId.Value);

        await EmitTenantEvent(events, "TENANT.DELETED.SUCCESS", tenantId, callerId.Value, new
        {
            phase = "soft-delete",
        });

        return Results.Json(new
        {
            message = "Organization has been soft-deleted",
            confirmationToken = token.Token,
            expiresAt = token.ExpiresAt,
        }, statusCode: StatusCodes.Status202Accepted);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Guid? ResolveUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static bool RoleAtLeast(HttpContext ctx, string min)
    {
        var role = ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        return TenantRoleHierarchy.IsAtLeast(role, min);
    }

    private static OrgResponse BuildOrgResponse(Tenant t)
        => new(t.Id, t.Name, t.Slug, t.Type, t.Plan, t.OwnerId, t.Settings, t.CreatedAt);

    private static async Task EmitTenantEvent(
        IEventRepository events,
        string type,
        Guid tenantId,
        Guid userId,
        object data)
    {
        await events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString(),
                userId = userId.ToString(),
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow,
        });
    }
}
