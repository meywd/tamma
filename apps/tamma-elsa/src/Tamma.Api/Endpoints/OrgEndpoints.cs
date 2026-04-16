using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Orgs;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class OrgEndpoints
{
    public static async Task<IResult> CreateOrg(
        CreateOrgRequest req,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        ClaimsPrincipal principal)
    {
        var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var existing = await tenantRepo.GetBySlugAsync(req.Slug);
        if (existing is not null)
            return Results.Conflict(new { error = "Slug already taken" });

        var tenant = await tenantRepo.CreateAsync(new Tenant
        {
            Name = req.Name,
            Slug = req.Slug.ToLowerInvariant(),
            Type = "org",
            OwnerId = userId
        });

        await membershipRepo.AddAsync(tenant.Id, userId, "owner");
        return Results.Created($"/api/v1/orgs/{tenant.Id}",
            new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
    }

    public static async Task<IResult> GetOrg(Guid tenantId, ITenantRepository tenantRepo)
    {
        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = "Organization not found" });
        return Results.Ok(new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
    }

    public static async Task<IResult> UpdateOrgSettings(
        Guid tenantId,
        UpdateOrgSettingsRequest req,
        ITenantRepository tenantRepo)
    {
        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = "Organization not found" });
        tenant.Settings = System.Text.Json.JsonSerializer.Serialize(req.Settings);
        await tenantRepo.UpdateAsync(tenant);
        return Results.Ok(new { message = "Settings updated" });
    }

    public static async Task<IResult> ListMembers(
        Guid tenantId,
        ITenantMembershipRepository membershipRepo,
        int? limit,
        int? offset)
    {
        var (members, total) = await membershipRepo.ListByTenantAsync(tenantId, limit ?? 50, offset ?? 0);
        var response = members.Select(m =>
            new MemberResponse(m.UserId, m.Role, m.JoinedAt, m.User?.DisplayName, m.User?.Email)).ToList();
        return Results.Ok(new { members = response, total });
    }

    public static async Task<IResult> UpdateMemberRole(
        Guid tenantId,
        Guid userId,
        UpdateMemberRoleRequest req,
        ITenantMembershipRepository membershipRepo)
    {
        await membershipRepo.UpdateRoleAsync(tenantId, userId, req.Role);
        return Results.Ok(new { message = "Role updated" });
    }

    public static async Task<IResult> RemoveMember(
        Guid tenantId,
        Guid userId,
        ITenantMembershipRepository membershipRepo)
    {
        await membershipRepo.RemoveAsync(tenantId, userId);
        return Results.Ok(new { message = "Member removed" });
    }

    public static async Task<IResult> CreateInvite(
        Guid tenantId,
        CreateOrgInviteRequest req,
        IInviteRepository inviteRepo,
        ClaimsPrincipal principal)
    {
        var inviterId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var token = Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        var invite = await inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = req.Email,
            Role = req.Role,
            InviteTokenHash = tokenHash,
            InvitedBy = inviterId,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        return Results.Created($"/api/v1/orgs/{tenantId}/invites/{invite.Id}",
            new { id = invite.Id, token, expiresAt = invite.ExpiresAt });
    }

    public static async Task<IResult> ListInvites(Guid tenantId, IInviteRepository inviteRepo)
    {
        var invites = await inviteRepo.ListPendingByTenantAsync(tenantId);
        return Results.Ok(invites.Select(i => new { i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt }));
    }

    public static async Task<IResult> DeleteInvite(Guid tenantId, Guid inviteId, IInviteRepository inviteRepo)
    {
        await inviteRepo.DeleteAsync(inviteId);
        return Results.Ok(new { message = "Invite deleted" });
    }

    public static async Task<IResult> AcceptInvite(
        AcceptInviteRequest req,
        IInviteRepository inviteRepo,
        ITenantMembershipRepository membershipRepo,
        ClaimsPrincipal principal)
    {
        var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();

        var invite = await inviteRepo.GetByTokenHashAsync(tokenHash);
        if (invite is null || invite.AcceptedAt is not null || invite.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Invalid or expired invite" });

        await membershipRepo.AddAsync(invite.TenantId, userId, invite.Role);
        await inviteRepo.AcceptAsync(invite.Id);

        return Results.Ok(new { message = "Invite accepted", tenantId = invite.TenantId });
    }

    public static async Task<IResult> SwitchOrg(
        SwitchOrgRequest req,
        ITenantMembershipRepository membershipRepo,
        IUserRepository userRepo,
        IJwtService jwtService,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = await membershipRepo.GetRoleAsync(req.TenantId, userId);
        if (role is null)
            return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);

        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) return Results.NotFound(new { error = "User not found" });

        await userRepo.UpdateActiveTenantAsync(userId, req.TenantId);
        var accessToken = jwtService.GenerateAccessToken(user, req.TenantId, role);

        return Results.Ok(new { accessToken, expiresIn = 900 });
    }

    public static async Task<IResult> ListTenants(
        ITenantRepository tenantRepo,
        ClaimsPrincipal principal)
    {
        var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var tenants = await tenantRepo.ListByUserAsync(userId);
        return Results.Ok(tenants.Select(t =>
            new OrgResponse(t.Id, t.Name, t.Slug, t.Type, t.OwnerId, t.Settings, t.CreatedAt)));
    }

    public static async Task<IResult> TransferOwnership(
        Guid tenantId,
        TransferOwnershipRequest req,
        ITenantRepository tenantRepo,
        ITenantMembershipRepository membershipRepo,
        ClaimsPrincipal principal)
    {
        var tenant = await tenantRepo.GetByIdAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = "Organization not found" });

        var currentOwnerId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        if (tenant.OwnerId != currentOwnerId)
            return Results.Json(new { error = "Only the owner can transfer ownership" }, statusCode: 403);

        tenant.OwnerId = req.NewOwnerId;
        await tenantRepo.UpdateAsync(tenant);
        await membershipRepo.UpdateRoleAsync(tenantId, req.NewOwnerId, "owner");
        await membershipRepo.UpdateRoleAsync(tenantId, currentOwnerId, "admin");

        return Results.Ok(new { message = "Ownership transferred" });
    }

    public static async Task<IResult> DeleteOrg(Guid tenantId, ITenantRepository tenantRepo)
    {
        await tenantRepo.SoftDeleteAsync(tenantId);
        return Results.Ok(new { message = "Organization deleted" });
    }
}
