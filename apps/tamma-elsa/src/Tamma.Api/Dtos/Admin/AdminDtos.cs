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
public record AdminUserResponse(Guid Id, string Email, string? DisplayName, string Role, bool IsActive, DateTime CreatedAt);
