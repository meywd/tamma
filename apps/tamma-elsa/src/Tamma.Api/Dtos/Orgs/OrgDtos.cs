namespace Tamma.Api.Dtos.Orgs;

public record CreateOrgRequest(string Name, string Slug);

/// <summary>
/// Settings update payload. All fields optional — at least one must be
/// provided. <c>Settings</c> is a free-form JSON object that replaces the
/// <c>tenants.settings</c> JSONB column. <c>Name</c> renames the tenant
/// (validated 2-100 chars, trimmed). <c>Plan</c> updates the billing
/// plan (must be one of <c>free | pro | enterprise</c>).
/// </summary>
public record UpdateOrgSettingsRequest(string? Name, string? Plan, object? Settings);

public record UpdateMemberRoleRequest(string Role);
public record CreateOrgInviteRequest(string Email, string Role);
public record AcceptInviteRequest(string Token);
public record TransferOwnershipRequest(Guid NewOwnerId);
public record SwitchOrgRequest(Guid TenantId);

public record OrgResponse(
    Guid Id,
    string Name,
    string Slug,
    string Type,
    string Plan,
    Guid? OwnerId,
    string Settings,
    DateTime CreatedAt);

public record MemberResponse(Guid UserId, string Role, DateTime JoinedAt, string? DisplayName, string? Email);

/// <summary>
/// Per-tenant projection used by <c>GET /api/v1/tenants</c>. Mirrors the TS
/// <c>{ tenants: [{ id, name, slug, plan, role, joinedAt, isActive }] }</c>
/// shape (finding 019).
/// </summary>
public record TenantSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    string Plan,
    string Role,
    DateTime JoinedAt,
    bool IsActive);

/// <summary>
/// Pending-invite projection used by <c>GET /api/v1/orgs/:tenantId/invites</c>.
/// Includes <c>InvitedBy</c> per finding 015.
/// </summary>
public record PendingInviteResponse(
    Guid Id,
    string? Email,
    string Role,
    Guid InvitedBy,
    DateTime ExpiresAt,
    DateTime CreatedAt);

/// <summary>
/// Audit-log row projection used by
/// <c>GET /api/v1/orgs/:tenantId/audit</c> (story 18-7). Strips the
/// platform-internal <c>Metadata</c> column from the source
/// <see cref="Tamma.Data.Entities.DomainEvent"/>; the dashboard only
/// needs id + type + timestamp + tags + data to render the audit table.
/// Tags + Data are emitted as raw JSON strings so the dashboard can
/// JSON.parse them client-side without a server-side schema change for
/// every new event type.
/// </summary>
public record AuditEventResponse(
    Guid Id,
    string Type,
    DateTime CreatedAt,
    string Tags,
    string Data);
