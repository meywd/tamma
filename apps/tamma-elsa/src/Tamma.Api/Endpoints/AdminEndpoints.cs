using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Services;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AdminEndpoints
{
    private static readonly string[] AllowedRoles = { "owner", "admin", "member" };

    /// <summary>
    /// Aggregates infrastructure health probes (Postgres + 4 HTTP services) in
    /// parallel. Mirrors the TS <c>/api/admin/health</c> envelope shape:
    /// <c>{ services: ServiceCheck[], checkedAt: string }</c>.
    /// </summary>
    public static async Task<IResult> GetHealth(IAdminHealthService healthService, CancellationToken ct)
    {
        var result = await healthService.GetHealthAsync(ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Service-key creator. Uses centralized <see cref="ApiKeyHasher"/> so
    /// service / user / installation keys share the <c>tamma_sk_</c> prefix +
    /// base64url + SHA-256 hash format. Audit findings 003 / 020.
    /// </summary>
    public static async Task<IResult> CreateServiceKey(
        CreateServiceKeyRequest req,
        IApiKeyRepository apiKeyRepo)
    {
        if (string.IsNullOrWhiteSpace(req.ServiceName))
            return Results.BadRequest(new { error = "serviceName is required" });

        var rawKey = ApiKeyHasher.NewKey();
        var keyHash = ApiKeyHasher.Hash(rawKey);
        var prefix = ApiKeyHasher.Prefix(rawKey);

        var apiKey = await apiKeyRepo.CreateAsync(new ApiKey
        {
            Scope = "service",
            OwnerId = req.ServiceName,
            KeyHash = keyHash,
            KeyPrefix = prefix,
            Label = req.Label,
            Permissions = req.Permissions,
            TenantId = null // service keys are not tenant-scoped at creation
        });

        return Results.Created($"/api/admin/service-keys/{apiKey.Id}",
            new ServiceKeyResponse(
                apiKey.Id,
                apiKey.OwnerId,
                apiKey.Label,
                apiKey.KeyPrefix,
                apiKey.Permissions,
                apiKey.CreatedAt,
                apiKey.LastUsedAt,
                apiKey.RevokedAt,
                apiKey.RotatedFromId,
                rawKey));
    }

    public static async Task<IResult> ListServiceKeys(IApiKeyRepository apiKeyRepo)
    {
        var keys = await apiKeyRepo.ListByScopeAsync("service");
        var response = keys.Select(k =>
            new ServiceKeyResponse(
                k.Id,
                k.OwnerId,
                k.Label,
                k.KeyPrefix,
                k.Permissions,
                k.CreatedAt,
                k.LastUsedAt,
                k.RevokedAt,
                k.RotatedFromId)).ToList();
        return Results.Ok(response);
    }

    public static async Task<IResult> RotateServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var existing = await apiKeyRepo.GetByIdAsync(id);
        if (existing is null)
            return Results.NotFound(new { error = "Service key not found" });

        var rawKey = ApiKeyHasher.NewKey();
        var keyHash = ApiKeyHasher.Hash(rawKey);
        var prefix = ApiKeyHasher.Prefix(rawKey);
        var newKey = await apiKeyRepo.RotateAsync(id, keyHash, prefix);
        return Results.Ok(new ServiceKeyResponse(
            newKey.Id,
            newKey.OwnerId,
            newKey.Label,
            newKey.KeyPrefix,
            newKey.Permissions,
            newKey.CreatedAt,
            newKey.LastUsedAt,
            newKey.RevokedAt,
            newKey.RotatedFromId,
            rawKey,
            "Store this key securely. It cannot be retrieved again. Old key is valid for 24h."));
    }

    public static async Task<IResult> DeleteServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var existing = await apiKeyRepo.GetByIdAsync(id);
        if (existing is null)
            return Results.NotFound(new { error = "Service key not found" });
        await apiKeyRepo.RevokeAsync(id);
        return Results.NoContent();
    }

    public static async Task<IResult> ListUsers(
        IUserRepository userRepo,
        int? limit,
        int? offset,
        string? role)
    {
        var (users, total) = await userRepo.ListAsync(limit ?? 50, offset ?? 0, role);
        var response = users.Select(u =>
            new AdminUserResponse(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt)).ToList();
        return Results.Ok(new { users = response, total });
    }

    public static async Task<IResult> GetUser(Guid id, IUserRepository userRepo)
    {
        var user = await userRepo.GetByIdAsync(id);
        if (user is null) return Results.NotFound(new { error = "User not found" });
        return Results.Ok(new AdminUserResponse(user.Id, user.Email, user.DisplayName, user.Role, user.IsActive, user.CreatedAt));
    }

    public static async Task<IResult> UpdateUserRole(
        Guid id,
        UpdateUserRoleRequest req,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo,
        ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        // Audit finding 018: full guard set.
        if (string.IsNullOrEmpty(req.Role) || !AllowedRoles.Contains(req.Role))
            return Results.BadRequest(new
            {
                error = "Invalid role. Must be one of: owner, admin, member"
            });

        var callerSub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(callerSub, out var callerId) && callerId == id)
            return Results.BadRequest(new { error = "Cannot change your own role" });

        var callerRole = principal.FindFirst("role")?.Value
            ?? principal.FindFirst(ClaimTypes.Role)?.Value
            ?? "member";

        // Owner-only promotions: route is gated PlatformOwnerAccess in
        // Program.cs (Story 28-R2 / PF-S1) so this is mostly defense-in-
        // depth — but it makes the policy explicit for readers. Note: the
        // per-tenant `role` claim is still consulted here because per-
        // tenant role assignments (owner/admin/member) flow through this
        // route too; the platform-admin gate at the route layer keeps
        // non-platform users out altogether.
        if ((req.Role == "owner" || req.Role == "admin") && callerRole != "owner")
            return Results.Json(
                new { error = "Only owners can promote to admin or owner" },
                statusCode: 403);

        var user = await userRepo.GetByIdAsync(id);
        if (user is null) return Results.NotFound(new { error = "User not found" });

        // Story 28-R2 / PF-S1 — defense-in-depth: refuse to demote another
        // platform admin via this surface. The route is gated
        // PlatformOwnerAccess so the caller IS a platform admin; without
        // this guard one platform admin could strip the per-tenant role
        // of another platform admin (the platform_role column itself is
        // not editable from this endpoint, but combined with future
        // `platformRole` editing this becomes a privilege-escalation
        // primitive). Self-demotion is allowed; cross-platform-admin
        // demotion is not.
        if (string.Equals(user.PlatformRole, "platform_admin", StringComparison.Ordinal)
            && Guid.TryParse(callerSub, out var actor) && actor != id)
        {
            loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!)
                .LogWarning(
                    "USER.ROLE_CHANGE.BLOCKED targetUserId={TargetUserId} targetPlatformRole={TargetPlatformRole} actor={Actor} reason=cross_platform_admin_demote",
                    id, user.PlatformRole, callerSub ?? "(unknown)");
            return Results.Json(
                new
                {
                    error = "cross_platform_admin_demote_blocked",
                    message = "Cannot change the per-tenant role of another platform admin via this endpoint."
                },
                statusCode: 403);
        }

        var oldRole = user.Role;
        if (tenantContext.TenantId.HasValue)
            await membershipRepo.UpdateRoleAsync(tenantContext.TenantId.Value, id, req.Role);

        user.Role = req.Role;
        await userRepo.UpdateAsync(user);

        // Story 28-R2 / PF-S1 — structured audit log for the platform-
        // admin-scoped mutation. Includes the actor's principal id, the
        // actor's email (if present in the JWT), the target user, both
        // roles. Audit log search-friendly key is USER.ROLE_CHANGED.SUCCESS.
        var actorEmail = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? "(unknown)";
        loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!)
            .LogInformation(
                "USER.ROLE_CHANGED.SUCCESS targetUserId={TargetUserId} targetPlatformRole={TargetPlatformRole} oldRole={OldRole} newRole={NewRole} actorUserId={ActorUserId} actorEmail={ActorEmail}",
                id, user.PlatformRole, oldRole, req.Role, callerSub ?? "(unknown)", actorEmail);

        return Results.Ok(new { message = "Role updated" });
    }

    public static async Task<IResult> DeleteUser(
        Guid id,
        IUserRepository userRepo,
        IApiKeyRepository apiKeyRepo,
        ITenantMembershipRepository membershipRepo,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        // Audit finding 019: cascade + self-protection + audit log +
        // sole-owner guard.
        var callerSub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(callerSub, out var callerId) && callerId == id)
            return Results.BadRequest(new { error = "Cannot delete yourself" });

        var user = await userRepo.GetByIdAsync(id);
        if (user is null)
            return Results.NotFound(new { error = "User not found" });

        // Story 28-R2 / PF-S1 — defense-in-depth: refuse to delete another
        // platform admin via this surface. The route is gated
        // PlatformOwnerAccess so the caller IS a platform admin; that
        // does not authorise one platform admin to nuke another. Removing
        // a platform admin requires an explicit DB-side update of
        // users.platform_role first (or running this endpoint against the
        // same caller — self-deletion is already refused above for other
        // reasons). Self-deletion of one's own platform-admin role would
        // never reach this guard.
        if (string.Equals(user.PlatformRole, "platform_admin", StringComparison.Ordinal))
        {
            loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!)
                .LogWarning(
                    "USER.DELETE.BLOCKED targetUserId={TargetUserId} targetPlatformRole={TargetPlatformRole} actor={Actor} reason=cross_platform_admin_delete",
                    id, user.PlatformRole, callerSub ?? "(unknown)");
            return Results.Json(
                new
                {
                    error = "cross_platform_admin_delete_blocked",
                    message = "Cannot delete a platform admin via this endpoint. "
                        + "Demote the user (set users.platform_role = 'user') first."
                },
                statusCode: 403);
        }

        // Sole-owner guard (audit finding auth/019 follow-up). If this user
        // is the only owner of ANY tenant, refuse the delete with a 409 +
        // remediation hint — the caller must first promote another member
        // or invoke POST /api/v1/orgs/{tenantId}/transfer-ownership.
        // Otherwise the cascade below would orphan those tenants.
        var soleOwned = await membershipRepo.ListSoleOwnedTenantsAsync(id);
        if (soleOwned.Count > 0)
        {
            return Results.Json(
                new
                {
                    error = "user_is_sole_owner",
                    message = "Cannot delete a user who is the sole owner of one or more organizations. "
                        + "Promote another member to owner first, or transfer ownership to an existing member.",
                    soleOwnedTenants = soleOwned.Select(t => new
                    {
                        tenantId = t.TenantId,
                        name = t.Name,
                        slug = t.Slug,
                    }).ToList(),
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        await userRepo.SoftDeleteAsync(id);
        await apiKeyRepo.RevokeAllByOwnerAsync(id.ToString());
        // Cascade: remove all tenant memberships. Safe because the
        // sole-owner guard above ruled out orphaning any tenant.
        await membershipRepo.RemoveAllForUserAsync(id);

        // Story 28-R2 / PF-S1 — structured audit log for the platform-
        // admin-scoped mutation.
        var actorEmail = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? "(unknown)";
        loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!)
            .LogInformation(
                "USER.DELETED.SUCCESS targetUserId={TargetUserId} actorUserId={ActorUserId} actorEmail={ActorEmail}",
                id, callerSub ?? "(unknown)", actorEmail);

        return Results.Ok(new { ok = true });
    }

    public static async Task<IResult> InviteUser(
        InviteUserRequest req,
        IInviteRepository inviteRepo,
        ITenantContext tenantContext,
        System.Security.Claims.ClaimsPrincipal principal)
    {
        if (!tenantContext.TenantId.HasValue)
            return Results.BadRequest(new { error = "No tenant context" });

        var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var inviterId))
            return Results.BadRequest(new { error = "Authenticated user identity required" });

        var token = Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256
            .HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        var invite = await inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantContext.TenantId.Value,
            Email = req.Email,
            Role = req.Role,
            InviteTokenHash = tokenHash,
            InvitedBy = inviterId,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        return Results.Created($"/api/admin/users/invites/{invite.Id}",
            new { id = invite.Id, token, expiresAt = invite.ExpiresAt });
    }

    public static async Task<IResult> ListInvites(IInviteRepository inviteRepo, ITenantContext tenantContext)
    {
        if (!tenantContext.TenantId.HasValue)
            return Results.Ok(Array.Empty<object>());
        var invites = await inviteRepo.ListPendingByTenantAsync(tenantContext.TenantId.Value);
        return Results.Ok(invites.Select(i => new { i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt }));
    }

    public static async Task<IResult> DeleteInvite(Guid id, IInviteRepository inviteRepo)
    {
        await inviteRepo.DeleteAsync(id);
        return Results.Ok(new { message = "Invite deleted" });
    }

    public static async Task<IResult> CreateUserApiKey(
        Guid id,
        CreateUserApiKeyRequest req,
        IApiKeyRepository apiKeyRepo,
        ITenantContext tenantContext)
    {
        // Centralized via ApiKeyHasher — same prefix / encoding / hash as
        // service keys. Audit findings 003 / 020.
        var rawKey = ApiKeyHasher.NewKey();
        var keyHash = ApiKeyHasher.Hash(rawKey);
        var prefix = ApiKeyHasher.Prefix(rawKey);

        var apiKey = await apiKeyRepo.CreateAsync(new ApiKey
        {
            Scope = "user",
            OwnerId = id.ToString(),
            KeyHash = keyHash,
            KeyPrefix = prefix,
            Label = req.Label,
            Permissions = ["dashboard:view", "workflows:view"],
            TenantId = tenantContext.TenantId
        });

        return Results.Created($"/api/admin/users/{id}/api-keys/{apiKey.Id}",
            new ServiceKeyResponse(
                apiKey.Id,
                apiKey.OwnerId,
                apiKey.Label,
                apiKey.KeyPrefix,
                apiKey.Permissions,
                apiKey.CreatedAt,
                apiKey.LastUsedAt,
                apiKey.RevokedAt,
                apiKey.RotatedFromId,
                rawKey));
    }

    public static async Task<IResult> ListUserApiKeys(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var keys = await apiKeyRepo.ListByOwnerAsync(id.ToString());
        return Results.Ok(keys.Select(k =>
            new ServiceKeyResponse(
                k.Id,
                k.OwnerId,
                k.Label,
                k.KeyPrefix,
                k.Permissions,
                k.CreatedAt,
                k.LastUsedAt,
                k.RevokedAt,
                k.RotatedFromId)));
    }

    public static async Task<IResult> DeleteUserApiKey(Guid id, Guid keyId, IApiKeyRepository apiKeyRepo)
    {
        await apiKeyRepo.RevokeAsync(keyId);
        return Results.Ok(new { message = "API key revoked" });
    }

    // ─── Tenant provisioning (audit cranl/003) ─────────────────────────────
    //
    // Platform-owner-only endpoints that drive per-tenant Cranl provisioning.
    // ITenantProvisioner is wired by ProvisioningServiceCollectionExtensions:
    // the Cranl-backed impl runs when Cranl:ApiKey is set, otherwise the Null
    // impl flips the row to Ready immediately (shared infrastructure mode).

    public static async Task<IResult> ProvisionTenant(
        Guid tenantId,
        ProvisionTenantRequest? req,
        ITenantProvisioner provisioner,
        CranlOptions cranlOptions,
        CancellationToken ct)
    {
        var region = !string.IsNullOrWhiteSpace(req?.Region)
            ? req!.Region!
            : cranlOptions.DefaultRegion;
        var status = await provisioner.ProvisionAsync(
            tenantId,
            new ProvisioningOptions(region, req?.CustomName),
            ct);
        return Results.Accepted(
            $"/api/admin/tenants/{tenantId}/provisioning",
            new TenantProvisioningResponse(
                tenantId,
                status.State.ToStorageString(),
                status.Detail,
                status.AppDefaultDomain,
                status.UpdatedAt));
    }

    public static async Task<IResult> GetTenantProvisioning(
        Guid tenantId,
        ITenantProvisioner provisioner,
        CancellationToken ct)
    {
        var status = await provisioner.GetStatusAsync(tenantId, ct);
        return Results.Ok(
            new TenantProvisioningResponse(
                tenantId,
                status.State.ToStorageString(),
                status.Detail,
                status.AppDefaultDomain,
                status.UpdatedAt));
    }

    public static async Task<IResult> DeprovisionTenant(
        Guid tenantId,
        ITenantProvisioner provisioner,
        CancellationToken ct)
    {
        await provisioner.DeprovisionAsync(tenantId, ct);
        var status = await provisioner.GetStatusAsync(tenantId, ct);
        return Results.Accepted(
            $"/api/admin/tenants/{tenantId}/provisioning",
            new TenantProvisioningResponse(
                tenantId,
                status.State.ToStorageString(),
                status.Detail,
                status.AppDefaultDomain,
                status.UpdatedAt));
    }
}
