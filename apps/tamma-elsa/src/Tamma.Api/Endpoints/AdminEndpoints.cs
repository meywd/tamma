using System.Security.Cryptography;
using System.Text;
using Tamma.Api.Dtos.Admin;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AdminEndpoints
{
    public static Task<IResult> GetHealth()
    {
        return Task.FromResult(Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, database = "connected" }));
    }

    public static async Task<IResult> CreateServiceKey(
        CreateServiceKeyRequest req,
        IApiKeyRepository apiKeyRepo,
        ITenantContext tenantContext)
    {
        var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var prefix = rawKey[..16];

        var apiKey = await apiKeyRepo.CreateAsync(new ApiKey
        {
            Scope = "service",
            OwnerId = "system",
            KeyHash = keyHash,
            KeyPrefix = prefix,
            Label = req.Label,
            Permissions = req.Permissions,
            TenantId = tenantContext.TenantId
        });

        return Results.Created($"/api/admin/service-keys/{apiKey.Id}",
            new ServiceKeyResponse(apiKey.Id, apiKey.Label, apiKey.KeyPrefix, apiKey.Permissions, apiKey.CreatedAt, rawKey));
    }

    public static async Task<IResult> ListServiceKeys(IApiKeyRepository apiKeyRepo)
    {
        var keys = await apiKeyRepo.ListByScopeAsync("service");
        var response = keys.Select(k =>
            new ServiceKeyResponse(k.Id, k.Label, k.KeyPrefix, k.Permissions, k.CreatedAt)).ToList();
        return Results.Ok(response);
    }

    public static async Task<IResult> RotateServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var prefix = rawKey[..16];
        var newKey = await apiKeyRepo.RotateAsync(id, keyHash, prefix);
        return Results.Ok(new ServiceKeyResponse(newKey.Id, newKey.Label, newKey.KeyPrefix, newKey.Permissions, newKey.CreatedAt, rawKey));
    }

    public static async Task<IResult> DeleteServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        await apiKeyRepo.RevokeAsync(id);
        return Results.Ok(new { message = "Service key revoked" });
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
        ITenantContext tenantContext)
    {
        var user = await userRepo.GetByIdAsync(id);
        if (user is null) return Results.NotFound(new { error = "User not found" });

        if (tenantContext.TenantId.HasValue)
            await membershipRepo.UpdateRoleAsync(tenantContext.TenantId.Value, id, req.Role);

        user.Role = req.Role;
        await userRepo.UpdateAsync(user);
        return Results.Ok(new { message = "Role updated" });
    }

    public static async Task<IResult> DeleteUser(Guid id, IUserRepository userRepo)
    {
        await userRepo.SoftDeleteAsync(id);
        return Results.Ok(new { message = "User deactivated" });
    }

    public static async Task<IResult> InviteUser(
        InviteUserRequest req,
        IInviteRepository inviteRepo,
        ITenantContext tenantContext,
        System.Security.Claims.ClaimsPrincipal principal)
    {
        if (!tenantContext.TenantId.HasValue)
            return Results.BadRequest(new { error = "No tenant context" });

        var token = Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var invite = await inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantContext.TenantId.Value,
            Email = req.Email,
            Role = req.Role,
            InviteTokenHash = tokenHash,
            InvitedBy = userId is not null ? Guid.Parse(userId) : Guid.Empty,
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
        var rawKey = $"tamma_uk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var prefix = rawKey[..16];

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

        return Results.Created($"/api/admin/users/{id}/keys/{apiKey.Id}",
            new ServiceKeyResponse(apiKey.Id, apiKey.Label, apiKey.KeyPrefix, apiKey.Permissions, apiKey.CreatedAt, rawKey));
    }

    public static async Task<IResult> ListUserApiKeys(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var keys = await apiKeyRepo.ListByOwnerAsync(id.ToString());
        return Results.Ok(keys.Select(k =>
            new ServiceKeyResponse(k.Id, k.Label, k.KeyPrefix, k.Permissions, k.CreatedAt)));
    }

    public static async Task<IResult> DeleteUserApiKey(Guid id, Guid keyId, IApiKeyRepository apiKeyRepo)
    {
        await apiKeyRepo.RevokeAsync(keyId);
        return Results.Ok(new { message = "API key revoked" });
    }
}
