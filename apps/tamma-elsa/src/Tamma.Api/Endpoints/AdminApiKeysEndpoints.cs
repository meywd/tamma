using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.ApiKeys;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 28-7 deferred-item — platform-admin API-key CRUD:
/// <list type="bullet">
///   <item><c>POST /api/admin/api-keys</c> — create a platform/user/service key.</item>
///   <item><c>GET /api/admin/api-keys</c> — list active keys (metadata only).</item>
///   <item><c>GET /api/admin/api-keys/{id}</c> — read metadata for a single key.</item>
///   <item><c>DELETE /api/admin/api-keys/{id}</c> — soft-revoke.</item>
/// </list>
/// All endpoints require <c>OwnerAccess</c> so a regular admin can't mint
/// platform-wide credentials (matches the existing service-key gate in
/// <c>Program.cs</c>). Reveal-once-on-create per cross-ref 29-3.
/// </summary>
public static class AdminApiKeysEndpoints
{
    private static readonly string[] AllowedScopes = { "platform", "user", "service" };

    /// <summary>
    /// Creates a new platform-owned API key. Plaintext <c>Key</c> in the
    /// response is the only time it's retrievable — reveal-once.
    /// </summary>
    public static async Task<IResult> CreateApiKey(
        CreateApiKeyRequest req,
        IApiKeyRepository apiKeyRepo,
        IPlatformApiKeyIndexRepository indexRepo,
        ControlPlaneDbContext cp)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            return Results.BadRequest(new { error = "label is required" });
        if (req.RateLimitRpm is int rpm && rpm <= 0)
            return Results.BadRequest(new { error = "rateLimitRpm must be > 0" });

        var scope = string.IsNullOrWhiteSpace(req.Scope) ? "platform" : req.Scope!;
        if (!AllowedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                error = $"scope must be one of: {string.Join(", ", AllowedScopes)}"
            });
        }
        scope = scope.ToLowerInvariant();

        // Mint the key. Platform scope gets the pl_ marker; user/service fall
        // back to the legacy un-prefixed shape since they aren't tenant-routed.
        var rawKey = scope switch
        {
            "platform" => ApiKeyPrefixGenerator.GeneratePlatformKey(),
            "user" => ApiKeyPrefixGenerator.GenerateUserKey(),
            _ => ApiKeyHasher.NewKey(),
        };
        var keyHash = ApiKeyHasher.HashArgon2(rawKey);
        var keyPrefix = ApiKeyHasher.Prefix(rawKey);

        var permissions = req.Permissions ?? Array.Empty<string>();
        var apiKey = new ApiKey
        {
            Scope = scope,
            // Platform-scope keys have no natural owner; use the literal
            // "platform" so they're still filterable and the downstream
            // unique constraints (which expect a non-null OwnerId) hold.
            OwnerId = scope == "platform" ? "platform" : scope,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Label = req.Label,
            Permissions = permissions,
            TenantId = null,
        };

        // Persist the key first, then its routing index row; write the
        // RateLimitRpm shadow column alongside. Wrapped in a single
        // SaveChanges so a failure between the two leaves no orphan.
        cp.ApiKeys.Add(apiKey);
        if (req.RateLimitRpm is int rpmValue)
        {
            cp.Entry(apiKey).Property("RateLimitRpm").CurrentValue = rpmValue;
        }

        var indexRow = new PlatformApiKeyIndex
        {
            KeyPrefix = keyPrefix,
            HashedSuffix = ApiKeyAuthHandler.HashSuffixForIndex(rawKey),
            TenantId = null,
            ApiKeyId = apiKey.Id == Guid.Empty ? Guid.NewGuid() : apiKey.Id,
            Scope = scope,
        };
        // The DB default (gen_random_uuid()) populates ApiKey.Id, but the
        // EF graph needs the key wired NOW so the index FK points right.
        if (apiKey.Id == Guid.Empty)
            apiKey.Id = indexRow.ApiKeyId;
        else
            indexRow.ApiKeyId = apiKey.Id;

        cp.PlatformApiKeyIndex.Add(indexRow);
        await cp.SaveChangesAsync();

        return Results.Created(
            $"/api/admin/api-keys/{apiKey.Id}",
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

    /// <summary>
    /// Lists every active platform/service/user API key (i.e. any row that
    /// is NOT tenant-scoped). Metadata only.
    /// </summary>
    public static async Task<IResult> ListApiKeys(ControlPlaneDbContext cp)
    {
        var rows = await cp.ApiKeys
            .Where(k => k.TenantId == null)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var projected = rows.Select(k => BuildSummary(cp, k)).ToList();
        return Results.Ok(projected);
    }

    public static async Task<IResult> GetApiKey(Guid id, ControlPlaneDbContext cp)
    {
        var k = await cp.ApiKeys.FindAsync(id);
        if (k is null) return Results.NotFound(new { error = "API key not found" });
        return Results.Ok(BuildSummary(cp, k));
    }

    /// <summary>
    /// Soft-revokes an API key — same semantics as the existing service-key
    /// delete. Also revokes the corresponding <c>platform_api_key_index</c>
    /// row so future auth attempts fail fast on the O(1) lookup.
    /// </summary>
    public static async Task<IResult> DeleteApiKey(
        Guid id,
        IApiKeyRepository apiKeyRepo,
        IPlatformApiKeyIndexRepository indexRepo)
    {
        var existing = await apiKeyRepo.GetByIdAsync(id);
        if (existing is null)
            return Results.NotFound(new { error = "API key not found" });
        await apiKeyRepo.RevokeAsync(id);
        await indexRepo.RevokeByApiKeyIdAsync(id);
        return Results.NoContent();
    }

    internal static ApiKeySummaryResponse BuildSummary(ControlPlaneDbContext cp, ApiKey k)
    {
        int? rpm = null;
        try
        {
            rpm = cp.Entry(k).Property<int?>("RateLimitRpm").CurrentValue;
        }
        catch
        {
            // Shadow property not populated — treat as no limit.
        }
        return new ApiKeySummaryResponse(
            k.Id,
            k.Label,
            k.Scope,
            k.KeyPrefix,
            k.OwnerId,
            k.Permissions,
            k.TenantId,
            rpm,
            k.CreatedAt,
            k.LastUsedAt,
            k.RevokedAt);
    }
}
