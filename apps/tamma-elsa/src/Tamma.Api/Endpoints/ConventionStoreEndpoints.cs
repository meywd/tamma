using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Conventions;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Minimal-API handlers for the DB-backed convention store (Story 27-10) —
/// <c>/api/conventions/*</c> (tenant CRUD + resolution + registry) and
/// <c>/api/admin/conventions/*</c> (platform-admin system-default management).
///
/// <para>This is a NEW surface; the legacy <c>/api/convention-templates</c>
/// routes (<see cref="ConventionEndpoints"/>) are untouched. Style, auth, and
/// error→HTTP mapping mirror <see cref="PromptEndpoints"/>:</para>
/// <list type="bullet">
///   <item><b>Boundary validation</b> — path params are parsed via
///     <see cref="AgentRoleExtensions.Parse"/> / <see cref="AgentActionExtensions.Parse"/>
///     (unknown token → 400) and the pair is checked against
///     <see cref="RolePhaseMap.IsRoleEligibleForPhase"/> (known-but-ineligible →
///     400). Errors use the consistent <c>{ error, code? }</c> shape.</item>
///   <item><b>Resolution fail-loud</b> — <see cref="IConventionStore.ResolveAsync"/>
///     throws <see cref="TammaError"/> (<c>CONVENTION_NOT_FOUND</c>) on a miss;
///     the boundary maps that to 404 (never an empty body — locked user
///     mandate, mirrors how <see cref="PromptEndpoints"/> maps prompt-not-found).</item>
///   <item><b>Mode-aware tenant scoping</b> — SaaS mode keys overrides by the
///     ambient tenant id; single-user mode is the sole user's personal tenant.
///     The resolution chain is always tenant → system → error.</item>
/// </list>
/// </summary>
public static class ConventionStoreEndpoints
{
    // =======================================================================
    // List (merged, every taxonomy cell with its resolved tier)
    // =======================================================================

    /// <summary>
    /// <c>GET /api/conventions</c> — the resolved convention for every taxonomy
    /// cell, each tagged with <c>isOverride</c> / <c>source</c>. Any authed user.
    /// </summary>
    public static async Task<IResult> ListAll(
        IConventionStore store,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct)
    {
        var tenantId = TenantScope(tenantContext, modeProvider);
        var summaries = await store.ListAsync(tenantId, ct);
        var response = summaries.Select(ToResponse).ToList();
        return Results.Ok(response);
    }

    // =======================================================================
    // Resolve (POST body {role, action})
    // =======================================================================

    /// <summary>
    /// <c>POST /api/conventions/resolve</c> — fail-loud resolution of one
    /// <c>(role, action)</c> to its body + source. Miss → 404 (NEVER empty).
    /// Any authed user.
    /// </summary>
    public static async Task<IResult> Resolve(
        ResolveConventionRequest req,
        IConventionStore store,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct)
    {
        if (!TryParsePair(req?.Role, req?.Action, out var role, out var action, out var error))
        {
            return error;
        }

        var tenantId = TenantScope(tenantContext, modeProvider);
        ConventionResolution resolved;
        try
        {
            resolved = await store.ResolveAsync(tenantId, role, action, ct);
        }
        catch (TammaError ex)
        {
            // CONVENTION_NOT_FOUND → 404 (consistent with PromptEndpoints; the
            // locked mandate forbids an empty/plain body on a miss).
            return NotFound(ex);
        }

        return Results.Ok(new ResolvedConventionResponse(
            resolved.Role,
            resolved.Action,
            resolved.Body,
            SourceLabel(resolved.Source),
            resolved.Version));
    }

    // =======================================================================
    // System defaults (read-only)
    // =======================================================================

    /// <summary>
    /// <c>GET /api/conventions/defaults</c> — the system-default row for every
    /// taxonomy cell (<c>tenant_id IS NULL</c>). Any authed user.
    /// </summary>
    public static async Task<IResult> ListSystemDefaults(
        IConventionStore store,
        CancellationToken ct)
    {
        // Passing tenantId=null returns the resolved set with no tenant overlay —
        // i.e. the pure system-default tier for every cell.
        var summaries = await store.ListAsync(null, ct);
        var response = summaries.Select(ToResponse).ToList();
        return Results.Ok(response);
    }

    /// <summary>
    /// <c>GET /api/conventions/defaults/:role/:action</c> — one system default.
    /// Any authed user. 400 on invalid/ineligible pair; 404 when no system
    /// default exists for a valid cell.
    /// </summary>
    public static async Task<IResult> GetSystemDefault(
        string role,
        string action,
        IConventionStore store,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }

        ConventionResolution resolved;
        try
        {
            // tenantId=null ⇒ skip the tenant tier, resolve the system default.
            resolved = await store.ResolveAsync(null, parsedRole, parsedAction, ct);
        }
        catch (TammaError ex)
        {
            return NotFound(ex);
        }

        return Results.Ok(new ResolvedConventionResponse(
            resolved.Role,
            resolved.Action,
            resolved.Body,
            SourceLabel(resolved.Source),
            resolved.Version));
    }

    // =======================================================================
    // Registry (UI pickers) — sourced from RolePhaseMap
    // =======================================================================

    /// <summary><c>GET /api/conventions/registry/roles</c> — valid role wire strings.</summary>
    public static IResult RegistryRoles()
    {
        var roles = RolePhaseMap.EligibleActions.Keys
            .Select(r => r.ToWire())
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(roles);
    }

    /// <summary><c>GET /api/conventions/registry/actions</c> — actions per role.</summary>
    public static IResult RegistryActions()
    {
        var response = RolePhaseMap.EligibleActions
            .Select(kv => new RoleActionsResponse(
                kv.Key.ToWire(),
                kv.Value.Select(a => a.ToWire()).OrderBy(a => a, StringComparer.Ordinal).ToList()))
            .OrderBy(r => r.Role, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(response);
    }

    /// <summary><c>GET /api/conventions/registry/role-actions</c> — full (role, action) matrix.</summary>
    public static IResult RegistryRoleActions()
    {
        var cells = new List<RoleActionCell>();
        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            foreach (var action in actions)
            {
                cells.Add(new RoleActionCell(roleWire, action.ToWire()));
            }
        }

        var ordered = cells
            .OrderBy(c => c.Role, StringComparer.Ordinal)
            .ThenBy(c => c.Action, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(ordered);
    }

    // =======================================================================
    // Tenant override CRUD ( :role/:action )
    // =======================================================================

    /// <summary>
    /// <c>GET /api/conventions/:role/:action</c> — resolved convention for the
    /// caller's tenant (tenant override → system default). Miss → 404 (NEVER
    /// empty). Any authed user.
    /// </summary>
    public static async Task<IResult> GetResolved(
        string role,
        string action,
        IConventionStore store,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }

        var tenantId = TenantScope(tenantContext, modeProvider);
        ConventionResolution resolved;
        try
        {
            resolved = await store.ResolveAsync(tenantId, parsedRole, parsedAction, ct);
        }
        catch (TammaError ex)
        {
            return NotFound(ex);
        }

        // resolved already carries Id/Version/UpdatedAt from the winning row —
        // no second DB roundtrip needed (I-4: single-roundtrip resolve).
        return Results.Ok(new ConventionResponse(
            resolved.Id,
            resolved.Role,
            resolved.Action,
            resolved.Body,
            Enabled: true,
            resolved.Version,
            IsOverride: resolved.Source == ConventionSource.Tenant,
            Source: SourceLabel(resolved.Source),
            UpdatedAt: resolved.UpdatedAt));
    }

    /// <summary>
    /// <c>PUT /api/conventions/:role/:action</c> — create/update the caller's
    /// TENANT override. Gated by the <c>ConventionManage</c> policy
    /// (tenant_owner / tenant_admin; member → 403 before this method runs).
    /// </summary>
    public static async Task<IResult> UpsertTenantOverride(
        string role,
        string action,
        UpsertConventionRequest req,
        IConventionStore store,
        ConventionEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }
        if (req is null)
        {
            return Results.BadRequest(new { error = "Request body required.", code = "CONVENTION_BODY_REQUIRED" });
        }
        if (!TryValidateBody(req.Body, out var bodyError))
        {
            return bodyError;
        }

        var tenantId = TenantScope(tenantContext, modeProvider);
        if (tenantId is null)
        {
            return Results.BadRequest(new { error = "No ambient tenant — cannot write a tenant override.", code = "TENANT_REQUIRED" });
        }

        var userId = principal.GetUserId() ?? Guid.Empty;
        var enabled = req.Enabled ?? true;

        var (row, wasCreated) = await store.UpsertAsync(tenantId.Value, parsedRole, parsedAction, req.Body, enabled, userId, ct);

        await events.EmitTenantOverrideUpsertedAsync(
            tenantId.Value, parsedRole, parsedAction, userId, wasCreated, row.Version, ct);

        return Results.Ok(new ConventionResponse(
            row.Id,
            parsedRole.ToWire(),
            parsedAction.ToWire(),
            req.Body,
            enabled,
            row.Version,
            IsOverride: true,
            Source: "tenant",
            UpdatedAt: row.UpdatedAt));
    }

    /// <summary>
    /// <c>DELETE /api/conventions/:role/:action</c> — delete the caller's TENANT
    /// override (falls back to the system default). 204 on success. Gated by the
    /// <c>ConventionManage</c> policy.
    /// </summary>
    public static async Task<IResult> DeleteTenantOverride(
        string role,
        string action,
        IConventionStore store,
        ConventionEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }

        var tenantId = TenantScope(tenantContext, modeProvider);
        if (tenantId is null)
        {
            return Results.BadRequest(new { error = "No ambient tenant — cannot delete a tenant override.", code = "TENANT_REQUIRED" });
        }

        var userId = principal.GetUserId() ?? Guid.Empty;

        // DeleteAsync returns true if a row was actually removed; no-op when no
        // override exists. Either way the cell falls back to the system default,
        // so 204 is the right contract (mirrors a reset-to-default).
        var wasDeleted = await store.DeleteAsync(tenantId.Value, parsedRole, parsedAction, ct);

        await events.EmitTenantOverrideDeletedAsync(
            tenantId.Value, parsedRole, parsedAction, userId, wasDeleted, ct);

        return Results.NoContent();
    }

    // =======================================================================
    // System-default admin CRUD + reset ( /api/admin/conventions/:role/:action )
    // platform-admin only (policy gated at registration).
    // =======================================================================

    /// <summary>
    /// <c>PUT /api/admin/conventions/:role/:action</c> — create/update the
    /// SYSTEM default. Platform-admin only.
    /// </summary>
    public static async Task<IResult> UpsertSystemDefault(
        string role,
        string action,
        UpsertConventionRequest req,
        IConventionStore store,
        ConventionEventsService events,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }
        if (req is null)
        {
            return Results.BadRequest(new { error = "Request body required.", code = "CONVENTION_BODY_REQUIRED" });
        }
        if (!TryValidateBody(req.Body, out var bodyError))
        {
            return bodyError;
        }

        var adminUserId = principal.GetUserId() ?? Guid.Empty;
        var enabled = req.Enabled ?? true;

        var (row, wasCreated) = await store.UpsertSystemDefaultAsync(parsedRole, parsedAction, req.Body, enabled, adminUserId, ct);

        await events.EmitSystemDefaultUpsertedAsync(
            parsedRole, parsedAction, adminUserId, wasCreated, row.Version, ct);

        return Results.Ok(new ConventionResponse(
            row.Id,
            parsedRole.ToWire(),
            parsedAction.ToWire(),
            req.Body,
            enabled,
            row.Version,
            IsOverride: false,
            Source: "system",
            UpdatedAt: row.UpdatedAt));
    }

    /// <summary>
    /// <c>DELETE /api/admin/conventions/:role/:action</c> — delete the SYSTEM
    /// default. Platform-admin only. 204 on success.
    /// </summary>
    public static async Task<IResult> DeleteSystemDefault(
        string role,
        string action,
        IConventionStore store,
        ConventionEventsService events,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }

        var adminUserId = principal.GetUserId() ?? Guid.Empty;
        var wasDeleted = await store.DeleteSystemDefaultAsync(parsedRole, parsedAction, ct);

        await events.EmitSystemDefaultDeletedAsync(
            parsedRole, parsedAction, adminUserId, wasDeleted, ct);

        return Results.NoContent();
    }

    /// <summary>
    /// <c>POST /api/admin/conventions/:role/:action/reset</c> — reset the SYSTEM
    /// default to the code baseline (<see cref="ConventionSeedSpecs"/>).
    /// Platform-admin only.
    /// </summary>
    public static async Task<IResult> ResetSystemDefault(
        string role,
        string action,
        IConventionStore store,
        ConventionEventsService events,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!TryParsePair(role, action, out var parsedRole, out var parsedAction, out var error))
        {
            return error;
        }

        var adminUserId = principal.GetUserId() ?? Guid.Empty;
        Convention row;
        try
        {
            (row, _) = await store.ResetSystemDefaultAsync(parsedRole, parsedAction, adminUserId, ct);
        }
        catch (TammaError ex)
        {
            // CONVENTION_NOT_A_TAXONOMY_CELL — defensive; the boundary already
            // rejects non-taxonomy cells via TryParsePair, so this should not
            // fire, but map it cleanly rather than letting it 500.
            return Results.BadRequest(new { error = ex.Message, code = ex.Code });
        }

        await events.EmitSystemDefaultResetAsync(parsedRole, parsedAction, adminUserId, ct);

        return Results.Ok(new ConventionResponse(
            row.Id,
            parsedRole.ToWire(),
            parsedAction.ToWire(),
            row.Body,
            row.Enabled,
            row.Version,
            IsOverride: false,
            Source: "system",
            UpdatedAt: row.UpdatedAt));
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    /// <summary>
    /// Parse + taxonomy-validate a <c>(role, action)</c> pair at the boundary.
    /// Delegates to the shared <see cref="RoleActionParsing.TryParsePair"/>
    /// helper (I-5) so the same taxonomy contract is enforced by both the
    /// convention store and the prompt store endpoints.
    /// Returns false with a 400 <paramref name="error"/> result when the token
    /// is unknown (parse throws) OR the pair is known-but-ineligible
    /// (e.g. developer/deploy). Never returns a parsed pair that fails
    /// <see cref="RolePhaseMap.IsRoleEligibleForPhase"/>.
    /// </summary>
    internal static bool TryParsePair(
        string? role,
        string? action,
        out AgentRole parsedRole,
        out AgentAction parsedAction,
        out IResult error)
        => RoleActionParsing.TryParsePair(role, action, out parsedRole, out parsedAction, out error);

    private static bool TryValidateBody(string? body, out IResult error)
    {
        error = Results.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            error = Results.BadRequest(new { error = "Convention body is required.", code = "CONVENTION_BODY_REQUIRED" });
            return false;
        }
        if (body.Length > 50_000)
        {
            error = Results.BadRequest(new { error = "Convention body exceeds the 50000-character limit.", code = "CONVENTION_BODY_TOO_LONG" });
            return false;
        }
        return true;
    }

    /// <summary>
    /// The ambient tenant id to scope convention reads and writes against.
    /// SaaS mode uses the request-ambient tenant; single-user mode uses the
    /// sole user's personal tenant (also the ambient tenant —
    /// <c>EnsurePersonalTenantMiddleware</c> binds it on startup). Returns
    /// <c>null</c> only when no ambient tenant is set (resolution then
    /// targets system defaults only).
    ///
    /// <para>The <paramref name="modeProvider"/> parameter is retained for
    /// future mode-aware scoping; today both modes derive the tenant from
    /// the ambient context and the parameter is unused.</para>
    /// </summary>
    private static Guid? TenantScope(
        ITenantContext tenantContext, ITammaModeProvider modeProvider)
        => tenantContext.TenantId;

    private static string SourceLabel(ConventionSource source) => source switch
    {
        ConventionSource.Tenant => "tenant",
        _ => "system",
    };

    private static ConventionResponse ToResponse(ConventionSummary s) => new(
        Id: s.Id,
        Role: s.Role,
        Action: s.Action,
        Body: s.Body,
        Enabled: s.Enabled,
        Version: s.Version,
        IsOverride: s.Source == ConventionSource.Tenant,
        Source: SourceLabel(s.Source),
        UpdatedAt: s.UpdatedAt);

    private static IResult NotFound(TammaError ex)
        => Results.NotFound(new { error = ex.Message, code = ex.Code });
}
