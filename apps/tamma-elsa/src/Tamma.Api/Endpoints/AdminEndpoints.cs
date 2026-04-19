using System.Security.Cryptography;
using System.Text;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Services;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class AdminEndpoints
{
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
    /// Service-key creator. ServiceName is required (persisted as OwnerId so
    /// each consuming service has its own row, independently revocable).
    /// TenantId is intentionally null — service keys are platform-level
    /// credentials and must work cross-tenant.
    /// </summary>
    public static async Task<IResult> CreateServiceKey(
        CreateServiceKeyRequest req,
        IApiKeyRepository apiKeyRepo)
    {
        if (string.IsNullOrWhiteSpace(req.ServiceName))
            return Results.BadRequest(new { error = "serviceName is required" });

        var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var prefix = rawKey[..16];

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

    /// <summary>
    /// Rotate a service key. Old key remains valid for a 24h grace period
    /// (RevokedAt = now+24h) so dependent services can roll over without an
    /// outage. The response includes a warning advertising the grace window
    /// and the rotated-from id so the caller can track the rotation chain.
    /// Returns 404 when the source id does not exist.
    /// </summary>
    public static async Task<IResult> RotateServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
    {
        var existing = await apiKeyRepo.GetByIdAsync(id);
        if (existing is null)
            return Results.NotFound(new { error = "Service key not found" });

        var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var prefix = rawKey[..16];
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

        // Reject the Guid.Empty fallback the previous implementation used
        // when no NameIdentifier claim was present. With FK on InvitedBy
        // (post-finding 019 hardening), a synthetic empty guid would either
        // FK-violate or pollute audit history. Demand a real caller id.
        var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var inviterId))
            return Results.BadRequest(new { error = "Authenticated user identity required" });

        var token = Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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
}
