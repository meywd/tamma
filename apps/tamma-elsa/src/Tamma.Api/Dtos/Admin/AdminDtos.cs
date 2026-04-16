namespace Tamma.Api.Dtos.Admin;

public record CreateServiceKeyRequest(string Label, string[] Permissions);
public record ServiceKeyResponse(Guid Id, string Label, string Prefix, string[] Permissions, DateTime CreatedAt, string? RawKey = null);
public record UpdateUserRoleRequest(string Role);
public record InviteUserRequest(string Email, string Role);
public record CreateUserApiKeyRequest(string Label);
public record AdminUserResponse(Guid Id, string Email, string? DisplayName, string Role, bool IsActive, DateTime CreatedAt);
