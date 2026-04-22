using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Api.Authorization;
using Tamma.Api.Dtos.ApiKeys;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 28-7 deferred-item — tenant-scoped API-key CRUD:
/// <list type="bullet">
///   <item><c>POST /api/v1/orgs/{tenantId}/api-keys</c></item>
///   <item><c>GET  /api/v1/orgs/{tenantId}/api-keys</c></item>
///   <item><c>GET  /api/v1/orgs/{tenantId}/api-keys/{id}</c></item>
///   <item><c>DELETE /api/v1/orgs/{tenantId}/api-keys/{id}</c></item>
/// </list>
///
/// <para>Tenant-scoped keys carry the <c>tamma_sk_t_&lt;b32-tenant&gt;_&lt;rand&gt;</c>
/// prefix per Story 28-7 AC. Path-tenant membership is already enforced by
/// <see cref="RequireTenantMembershipFilter"/>; the role gate below bumps
/// that to "admin or higher" because minting credentials is a destructive
/// operation.</para>
/// </summary>
public static class OrgApiKeysEndpoints
{
    public static async Task<IResult> CreateApiKey(
        Guid tenantId,
        CreateApiKeyRequest req,
        HttpContext http,
        ControlPlaneDbContext cp,
        IApiKeyRepository apiKeyRepo,
        IPlatformApiKeyIndexRepository indexRepo)
    {
        if (!RequireAdmin(http, out var forbid))
            return forbid!;

        if (string.IsNullOrWhiteSpace(req.Label))
            return Results.BadRequest(new { error = "label is required" });
        if (req.RateLimitRpm is int rpm && rpm <= 0)
            return Results.BadRequest(new { error = "rateLimitRpm must be > 0" });

        var rawKey = ApiKeyPrefixGenerator.GenerateTenantKey(tenantId);
        var keyHash = ApiKeyHasher.HashArgon2(rawKey);
        var keyPrefix = ApiKeyHasher.Prefix(rawKey);

        var permissions = req.Permissions ?? Array.Empty<string>();

        var apiKey = new ApiKey
        {
            Scope = "tenant",
            OwnerId = tenantId.ToString(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Label = req.Label,
            Permissions = permissions,
            TenantId = tenantId,
        };

        cp.ApiKeys.Add(apiKey);
        if (req.RateLimitRpm is int rpmValue)
            cp.Entry(apiKey).Property("RateLimitRpm").CurrentValue = rpmValue;

        var indexRow = new PlatformApiKeyIndex
        {
            KeyPrefix = keyPrefix,
            HashedSuffix = ApiKeyAuthHandler.HashSuffixForIndex(rawKey),
            TenantId = tenantId,
            ApiKeyId = apiKey.Id == Guid.Empty ? Guid.NewGuid() : apiKey.Id,
            Scope = "tenant",
        };
        if (apiKey.Id == Guid.Empty)
            apiKey.Id = indexRow.ApiKeyId;
        else
            indexRow.ApiKeyId = apiKey.Id;

        cp.PlatformApiKeyIndex.Add(indexRow);
        await cp.SaveChangesAsync();

        return Results.Created(
            $"/api/v1/orgs/{tenantId}/api-keys/{apiKey.Id}",
            new CreateApiKeyResponse(
                apiKey.Id,
                apiKey.Label,
                apiKey.Scope,
                apiKey.KeyPrefix,
                apiKey.Permissions,
                apiKey.TenantId,
                req.RateLimitRpm,
                apiKey.CreatedAt == default ? DateTime.UtcNow : apiKey.CreatedAt,
                rawKey,
                "Store this key securely. It will never be shown again."));
    }

    public static async Task<IResult> ListApiKeys(Guid tenantId, ControlPlaneDbContext cp)
    {
        var rows = await cp.ApiKeys
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var projected = rows.Select(k => AdminApiKeysEndpoints.BuildSummary(cp, k)).ToList();
        return Results.Ok(projected);
    }

    public static async Task<IResult> GetApiKey(Guid tenantId, Guid id, ControlPlaneDbContext cp)
    {
        var k = await cp.ApiKeys.FindAsync(id);
        if (k is null || k.TenantId != tenantId)
            return Results.NotFound(new { error = "API key not found" });
        return Results.Ok(AdminApiKeysEndpoints.BuildSummary(cp, k));
    }

    public static async Task<IResult> DeleteApiKey(
        Guid tenantId,
        Guid id,
        HttpContext http,
        IApiKeyRepository apiKeyRepo,
        IPlatformApiKeyIndexRepository indexRepo)
    {
        if (!RequireAdmin(http, out var forbid))
            return forbid!;

        var existing = await apiKeyRepo.GetByIdAsync(id);
        if (existing is null || existing.TenantId != tenantId)
            return Results.NotFound(new { error = "API key not found" });
        await apiKeyRepo.RevokeAsync(id);
        await indexRepo.RevokeByApiKeyIdAsync(id);
        return Results.NoContent();
    }

    /// <summary>
    /// Minting / revoking credentials is admin+; regular members cannot. The
    /// membership filter already ran and stashed the tenant role on
    /// <c>HttpContext.Items</c>.
    /// </summary>
    private static bool RequireAdmin(HttpContext http, out IResult? forbid)
    {
        var role = http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (role is null)
        {
            forbid = Results.Json(
                new { error = "Tenant role not resolved" },
                statusCode: StatusCodes.Status500InternalServerError);
            return false;
        }
        if (!TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin))
        {
            forbid = Results.Json(
                new { error = "Requires admin role or higher" },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        forbid = null;
        return true;
    }
}
