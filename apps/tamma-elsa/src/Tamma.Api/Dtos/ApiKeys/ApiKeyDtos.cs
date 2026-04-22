namespace Tamma.Api.Dtos.ApiKeys;

/// <summary>
/// Request body for <c>POST /api/admin/api-keys</c> (platform scope) and
/// <c>POST /api/v1/orgs/{tenantId}/api-keys</c> (tenant scope).
///
/// <para><c>Scope</c> for the platform endpoint must be one of
/// <c>platform</c>, <c>user</c>, or <c>service</c>. The tenant endpoint
/// locks scope to <c>tenant</c> — the body value is ignored if sent.</para>
///
/// <para><c>RateLimitRpm</c> is the per-key RPM ceiling wired into the
/// Story 28-7 shadow column (<c>api_keys.RateLimitRpm</c>). Null = use the
/// handler default. Values &lt;= 0 are rejected as a bad request.</para>
/// </summary>
public record CreateApiKeyRequest(
    string Label,
    string? Scope = null,
    string[]? Permissions = null,
    int? RateLimitRpm = null);

/// <summary>
/// Response for <c>POST /api-keys</c>. The plaintext <c>Key</c> is populated
/// ONCE on creation and never returned from any other endpoint — classic
/// reveal-once-on-create UX (cross-ref Story 29-3).
/// </summary>
public record CreateApiKeyResponse(
    Guid Id,
    string Label,
    string Scope,
    string Prefix,
    string[] Permissions,
    Guid? TenantId,
    int? RateLimitRpm,
    DateTime CreatedAt,
    string Key,
    string Warning);

/// <summary>
/// Metadata-only response for list / delete. Never includes the plaintext
/// key or the stored hash.
/// </summary>
public record ApiKeySummaryResponse(
    Guid Id,
    string Label,
    string Scope,
    string Prefix,
    string OwnerId,
    string[] Permissions,
    Guid? TenantId,
    int? RateLimitRpm,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);
