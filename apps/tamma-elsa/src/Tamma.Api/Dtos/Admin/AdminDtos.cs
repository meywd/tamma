namespace Tamma.Api.Dtos.Admin;

/// <summary>
/// Request to create a service-to-service API key. <c>ServiceName</c> is
/// required and persisted as the row's <c>OwnerId</c> so individual services
/// (e.g. <c>elsa-server</c>, <c>tamma-api-dotnet</c>) are independently
/// revocable. Matches TS <c>POST /api/admin/service-keys</c> contract.
/// </summary>
public record CreateServiceKeyRequest(string ServiceName, string Label, string[] Permissions);

/// <summary>
/// Response for service-key create / list / rotate. Includes lifecycle signals
/// (<c>LastUsedAt</c>, <c>RevokedAt</c>, <c>RotatedFromId</c>) so the admin
/// dashboard can audit key state. <c>RawKey</c> and <c>Warning</c> are only
/// populated on create / rotate; null on list.
/// </summary>
public record ServiceKeyResponse(
    Guid Id,
    string ServiceName,
    string Label,
    string Prefix,
    string[] Permissions,
    DateTime CreatedAt,
    DateTime? LastUsedAt = null,
    DateTime? RevokedAt = null,
    Guid? RotatedFromId = null,
    string? RawKey = null,
    string? Warning = null);

public record UpdateUserRoleRequest(string Role);
public record InviteUserRequest(string Email, string Role);
public record CreateUserApiKeyRequest(string Label);

/// <summary>
/// Story 16.2 contract for <c>GET /api/admin/users/{id}/keys</c>. The
/// dashboard's <c>apiKeysApi.list</c> client unwraps <c>r.apiKeys</c> and
/// expects the seven fields on <see cref="UserApiKeyEntry"/> — keep them
/// in sync if either side changes.
/// </summary>
public record UserApiKeyListResponse(List<UserApiKeyEntry> ApiKeys);

public record UserApiKeyEntry(
    Guid Id,
    string KeyPrefix,
    string Label,
    string UserId,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    DateTime? RevokedAt);

/// <summary>
/// Story 16.2 contract for <c>POST /api/admin/users/{id}/keys</c>. The
/// raw <c>key</c> is returned ONCE here so the dashboard can show it to
/// the user; subsequent reads via the list endpoint never include it.
/// Mirrors the dashboard's <c>CreateApiKeyResult</c> type.
/// </summary>
public record CreateUserApiKeyResponse(
    Guid Id,
    string Key,
    string Prefix,
    string Label,
    DateTime CreatedAt);

public record AdminUserResponse(Guid Id, string Email, string? DisplayName, string Role, bool IsActive, DateTime CreatedAt);

/// <summary>
/// Request body for <c>POST /api/admin/tenants/{tenantId}/provision</c>.
/// <c>Region</c> is the Cranl server id (e.g. <c>germany-1</c>); falls back
/// to <c>Cranl:DefaultRegion</c> when blank. <c>CustomName</c> is an
/// optional display name for the Cranl project — defaults to
/// <c>tamma-tenant-&lt;short-uuid&gt;</c>.
/// </summary>
public record ProvisionTenantRequest(string? Region = null, string? CustomName = null);

/// <summary>
/// Response for both <c>POST /provision</c> + <c>GET /provisioning</c> + the
/// teardown endpoint. <c>State</c> matches the snake_case storage form
/// (<c>none</c>, <c>pending</c>, <c>database_provisioning</c>,
/// <c>database_ready</c>, <c>app_provisioning</c>, <c>app_deploying</c>,
/// <c>ready</c>, <c>failed</c>, <c>deprovisioning</c>, <c>deprovisioned</c>).
/// </summary>
public record TenantProvisioningResponse(
    Guid TenantId,
    string State,
    string? Detail,
    string? AppDefaultDomain,
    DateTimeOffset UpdatedAt);
