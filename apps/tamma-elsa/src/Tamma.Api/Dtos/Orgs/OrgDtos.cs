namespace Tamma.Api.Dtos.Orgs;

public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgSettingsRequest(object Settings);
public record UpdateMemberRoleRequest(string Role);
public record CreateOrgInviteRequest(string Email, string Role);
public record AcceptInviteRequest(string Token);
public record TransferOwnershipRequest(Guid NewOwnerId);
public record SwitchOrgRequest(Guid TenantId);
public record OrgResponse(Guid Id, string Name, string Slug, string Type, Guid? OwnerId, string Settings, DateTime CreatedAt);
public record MemberResponse(Guid UserId, string Role, DateTime JoinedAt, string? DisplayName, string? Email);
