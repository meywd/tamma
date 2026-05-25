namespace Tamma.Api.Dtos.Conventions;

/// <summary>
/// Request body for <c>PUT /api/conventions/:role/:action</c> (tenant override)
/// and <c>PUT /api/admin/conventions/:role/:action</c> (system default).
///
/// <para>Story 27-10 — the simplified schema (Story 27-9) dropped the
/// <c>name</c> / <c>description</c> columns, so the body is just the convention
/// <see cref="Body"/> plus an optional <see cref="Enabled"/> toggle (default
/// <c>true</c>). <see cref="Body"/> is required, non-empty, max 50000 chars
/// (validated at the boundary).</para>
/// </summary>
/// <param name="Body">The convention body injected into <c>{{conventions}}</c>.</param>
/// <param name="Enabled">Whether the row is active. Null ⇒ true. A disabled
/// tenant override falls through to the system default during resolution; a
/// disabled system default makes the cell resolve to 404 (no enabled tier).</param>
public sealed record UpsertConventionRequest(string Body, bool? Enabled);

/// <summary>
/// Request body for <c>POST /api/conventions/resolve</c>.
/// </summary>
/// <param name="Role">Agent role wire string (e.g. <c>developer</c>).</param>
/// <param name="Action">Agent action wire string (e.g. <c>implement-feature</c>).</param>
public sealed record ResolveConventionRequest(string Role, string Action);

/// <summary>
/// Item shape for <c>GET /api/conventions</c> (merged list) and
/// <c>GET /api/conventions/:role/:action</c> (resolved single).
///
/// <para><see cref="IsOverride"/> is a convenience boolean equal to
/// <c>Source == "tenant"</c>; the merged list surfaces every taxonomy cell once
/// with its resolved tier.</para>
/// </summary>
public sealed record ConventionResponse(
    Guid? Id,
    string Role,
    string Action,
    string Body,
    bool Enabled,
    int Version,
    bool IsOverride,
    string Source,
    DateTime? UpdatedAt);

/// <summary>
/// Response shape for <c>POST /api/conventions/resolve</c> — the resolved body
/// for a <c>(role, action)</c> with its source tier and version.
/// </summary>
public sealed record ResolvedConventionResponse(
    string Role,
    string Action,
    string Body,
    string Source,
    int Version);

/// <summary>
/// One <c>(role, action)</c> cell of the registry matrix
/// (<c>GET /api/conventions/registry/role-actions</c>).
/// </summary>
public sealed record RoleActionCell(string Role, string Action);

/// <summary>
/// Actions-per-role entry for
/// <c>GET /api/conventions/registry/actions</c>.
/// </summary>
public sealed record RoleActionsResponse(string Role, IReadOnlyList<string> Actions);
