using System.Security.Claims;
using Tamma.Api.Auth;
using System.Text.Json;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Repositories;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Agent configuration + resolver endpoints.
///
/// <c>GetConfig / UpdateConfig / ValidateConfig</c> manage the raw tenant
/// JSONB override stored in <c>agent_configs</c>. <c>ResolveAgent</c> and
/// <c>ResolveForPhase</c> produce a fully-merged <see cref="ResolvedAgentConfig"/>
/// via <see cref="IAgentResolverService"/>.
/// </summary>
public static class AgentEndpoints
{
    // -----------------------------------------------------------------------
    // Config CRUD — raw tenant override JSON
    // -----------------------------------------------------------------------

    /// <summary>Return the current tenant's agent config (or empty platform default marker).</summary>
    public static async Task<IResult> GetConfig(
        IAgentConfigRepository configRepo,
        ITenantContext tenantContext)
    {
        var config = await configRepo.GetAsync(tenantContext.TenantId);
        if (config is null)
        {
            return Results.Ok(new AgentConfigResponse(new { }, "platform-default", 0));
        }
        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(config.Config) ?? new { },
            "tenant-override",
            config.Version));
    }

    /// <summary>
    /// Upsert the tenant's agent config. Validates schema, increments
    /// version, and appends a domain event for audit.
    ///
    /// <para>
    /// Story 28-1 PR A (Decision #1): writes without a tenant context are
    /// rejected with 400. Platform defaults moved to code
    /// (<c>DefaultAgentConfig.ForRole</c>); the legacy "edit the platform
    /// default by PUTing with a null tenant" behaviour was a no-op that
    /// silently dropped the request AND emitted a false success audit
    /// event. Both lies are gone now: callers see an explicit 400 and no
    /// <c>AGENT_CONFIG.UPDATED.SUCCESS</c> hits the event store.
    /// </para>
    /// </summary>
    public static async Task<IResult> UpdateConfig(
        UpdateAgentConfigRequest req,
        IAgentConfigRepository configRepo,
        IEventRepository events,
        ITenantContext tenantContext,
        ClaimsPrincipal principal)
    {
        var configJson = JsonSerializer.Serialize(req.Config);
        // Schema-level validation before write
        var (valid, errors) = ValidateConfigShape(configJson);
        if (!valid)
        {
            return Results.BadRequest(new { valid = false, errors });
        }

        // Story 28-1 PR A: short-circuit before persistence + audit so we
        // don't poison the DCB stream with a SUCCESS event for a write that
        // never happened. Platform defaults are immutable from this surface.
        if (tenantContext.TenantId is null)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "PUT /api/v1/agents/config requires tenant context; " +
                         "platform defaults are immutable from this endpoint. " +
                         "Edit DefaultAgentConfig.ForRole in code instead.",
            });
        }

        var userGuid = principal.GetUserId();

        var saved = await configRepo.UpsertAsync(tenantContext.TenantId, configJson, userGuid);

        // Emit audit event (DCB pattern). Reachable only after a real write
        // — every emitted event corresponds to a state transition.
        await events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = "AGENT_CONFIG.UPDATED.SUCCESS",
            TenantId = tenantContext.TenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantContext.TenantId?.ToString(),
                userId = userGuid,
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new
            {
                version = saved.Version,
            }),
            CreatedAt = DateTime.UtcNow,
        });

        return Results.Ok(new AgentConfigResponse(
            JsonSerializer.Deserialize<object>(saved.Config) ?? new { },
            "tenant-override",
            saved.Version));
    }

    /// <summary>
    /// Validate the shape of a proposed config without persisting.
    /// Checks:
    /// <list type="bullet">
    ///   <item>Valid JSON (not malformed).</item>
    ///   <item>Root is an object.</item>
    ///   <item>If <c>roles</c> is present, each entry key is a valid role
    ///         (see <see cref="RolePhaseMap.ValidRoles"/>).</item>
    ///   <item>No forbidden prototype-pollution keys in role names.</item>
    /// </list>
    /// </summary>
    public static IResult ValidateConfig(ValidateConfigRequest req)
    {
        var configJson = JsonSerializer.Serialize(req.Config);
        var (valid, errors) = ValidateConfigShape(configJson);
        return Results.Ok(new { valid, errors });
    }

    // -----------------------------------------------------------------------
    // Resolver endpoints — merged (default + tenant override)
    // -----------------------------------------------------------------------

    /// <summary>
    /// GET <c>/api/v1/agents/{role}/resolve</c> — resolve the full agent
    /// config for a role with tenant override applied.
    /// </summary>
    public static async Task<IResult> ResolveAgent(
        string role,
        IAgentResolverService resolver,
        ITenantContext tenantContext)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(tenantContext.TenantId, role);
            return Results.Ok(resolved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// POST <c>/api/v1/agents/resolve-for-phase</c> — resolve the config
    /// for a specific (phase, role) pair. Body fields: <c>phase</c>,
    /// <c>role</c>.
    /// </summary>
    public static async Task<IResult> ResolveForPhase(
        ResolveForPhaseRequest req,
        IAgentResolverService resolver,
        ITenantContext tenantContext)
    {
        // The existing DTO uses (Phase, TaskType) where TaskType semantically
        // carries the role. Keeping the record shape backward-compatible.
        var role = string.IsNullOrWhiteSpace(req.TaskType) ? req.Role ?? string.Empty : req.TaskType;
        try
        {
            var resolved = await resolver.ResolveForPhaseAsync(
                tenantContext.TenantId, req.Phase, role, req.TaskOverrides);
            return Results.Ok(resolved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    // -----------------------------------------------------------------------
    // Story 32-1 — first-class agent entity CRUD (/api/v1/agents)
    // -----------------------------------------------------------------------

    /// <summary>
    /// POST <c>/api/v1/agents</c> — create a first-class agent + its Version=1
    /// snapshot. <c>visibility</c> drives the gate: <c>public</c> requires the
    /// platform-admin claim (else 403); <c>private</c> derives the owner from
    /// the process mode (SaaS → tenant; single-user → user). Member-role
    /// callers are rejected by the <c>AgentManage</c> policy before reaching
    /// here; the public-write gate is enforced in-handler.
    /// </summary>
    public static async Task<IResult> CreateAgent(
        CreateAgentRequest req,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { error = "invalid_request", detail = "name is required." });
        }

        // Role must be a taxonomy-valid wire string (normalize legacy aliases).
        string canonicalRole;
        try
        {
            canonicalRole = AgentRoleExtensions.Parse(req.Role).ToWire();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_role", detail = ex.Message });
        }

        if (!TryParseVisibility(req.Visibility, out var visibility))
        {
            return Results.BadRequest(new
            {
                error = "invalid_visibility",
                detail = "visibility must be 'public' or 'private'.",
            });
        }

        var configJson = req.Config.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : req.Config.GetRawText();
        var (valid, errors) = AgentConfigValidator.Validate(configJson);
        if (!valid)
        {
            return Results.BadRequest(new { valid = false, errors });
        }

        var agent = new Agent
        {
            Name = req.Name,
            Role = canonicalRole,
            Visibility = visibility,
        };

        if (visibility == AgentVisibility.Public)
        {
            // Public agents are platform-owned. Defence-in-depth: the route is
            // gated, but a private-owner could otherwise sneak a public create
            // through the AgentManage policy. Require platform-admin here.
            if (!IsPlatformAdmin(principal))
            {
                return Results.Json(
                    new { error = "forbidden", detail = "public agents require platform admin." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }
        else
        {
            // Private — derive the owner from the process mode (CLAUDE.md
            // "Universal rule"). SaaS → tenant; single-user → user.
            if (modeProvider.Mode == TammaMode.SaaS)
            {
                if (tenantContext.TenantId is not Guid tid)
                {
                    return Results.BadRequest(new
                    {
                        error = "no_tenant_context",
                        detail = "a private agent in SaaS mode requires tenant context.",
                    });
                }
                agent.OwnerTenantId = tid;
            }
            else
            {
                if (principal.GetUserId() is not Guid uid)
                {
                    return Results.BadRequest(new
                    {
                        error = "no_user_context",
                        detail = "a private agent in single-user mode requires a user id.",
                    });
                }
                agent.OwnerUserId = uid;
            }
        }

        try
        {
            var created = await agents.CreateAsync(
                agent, configJson, req.Notes, principal.GetUserId());

            return Results.Created($"/api/v1/agents/{created.Id}", new CreateAgentResponse(
                created.Id, created.Name, created.Role,
                VisibilityWire(created.Visibility), StatusWire(created.Status),
                CurrentVersion: 1));
        }
        catch (TammaError ex)
        {
            return Results.BadRequest(new { error = ex.Code, detail = ex.Message });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new
            {
                error = "agent_name_conflict",
                detail = "an agent with this name/role already exists for this scope.",
            });
        }
    }

    /// <summary>
    /// POST <c>/api/v1/agents/{id}/versions</c> — publish a new immutable
    /// version. RBAC matches the agent's ownership (public → platform admin;
    /// private → owning tenant/user).
    /// </summary>
    public static async Task<IResult> PublishVersion(
        Guid id,
        PublishVersionRequest req,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            // 404 (not 403) for an agent the caller cannot see — avoid leaking existence.
            return Results.NotFound();
        }
        if (!CanWriteAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.Json(
                new { error = "forbidden", detail = "not permitted to publish for this agent." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var configJson = req.Config.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : req.Config.GetRawText();
        var (valid, errors) = AgentConfigValidator.Validate(configJson);
        if (!valid)
        {
            return Results.BadRequest(new { valid = false, errors });
        }

        try
        {
            var version = await agents.PublishVersionAsync(
                id, configJson, req.Notes, principal.GetUserId());
            if (version is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(new PublishVersionResponse(
                version.Id, version.Version, version.CreatedAt));
        }
        catch (TammaError ex)
        {
            return Results.BadRequest(new { error = ex.Code, detail = ex.Message });
        }
    }

    /// <summary>POST <c>/api/v1/agents/{id}/archive</c>.</summary>
    public static async Task<IResult> ArchiveAgent(
        Guid id,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.NotFound();
        }
        if (!CanWriteAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.Json(
                new { error = "forbidden", detail = "not permitted to archive this agent." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var archived = await agents.ArchiveAsync(id, principal.GetUserId());
        return archived is null
            ? Results.NotFound()
            : Results.Ok(new { id = archived.Id, status = StatusWire(archived.Status) });
    }

    /// <summary>
    /// GET <c>/api/v1/agents</c> — list visible agents (all public ∪ the
    /// caller's own private). Cross-tenant private agents are never returned.
    /// </summary>
    public static async Task<IResult> ListAgents(
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var (tenantId, userId) = ResolvePrincipalScope(principal, tenantContext, modeProvider);
        var visible = await agents.ListVisibleAsync(tenantId, userId);
        var summaries = visible.Select(ToSummary).ToList();
        return Results.Ok(summaries);
    }

    /// <summary>
    /// GET <c>/api/v1/agents/{id}</c> — a private agent not owned by the caller
    /// returns 404 (not 403, to avoid leaking existence).
    /// </summary>
    public static async Task<IResult> GetAgent(
        Guid id,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.NotFound();
        }

        var versions = await agents.ListVersionsAsync(id);
        return Results.Ok(new AgentDetail(
            agent.Id, agent.Name, agent.Role,
            VisibilityWire(agent.Visibility), StatusWire(agent.Status),
            agent.OwnerTenantId, agent.OwnerUserId, agent.CurrentVersionId,
            agent.CreatedAt, agent.UpdatedAt,
            versions.Select(v => new AgentVersionSummary(v.Id, v.Version, v.Notes, v.CreatedAt)).ToList()));
    }

    /// <summary>GET <c>/api/v1/agents/{id}/versions</c>.</summary>
    public static async Task<IResult> ListVersions(
        Guid id,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.NotFound();
        }
        var versions = await agents.ListVersionsAsync(id);
        return Results.Ok(versions
            .Select(v => new AgentVersionSummary(v.Id, v.Version, v.Notes, v.CreatedAt))
            .ToList());
    }

    /// <summary>GET <c>/api/v1/agents/{id}/versions/{version}</c>.</summary>
    public static async Task<IResult> GetVersion(
        Guid id,
        int version,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.NotFound();
        }
        var ver = await agents.GetVersionAsync(id, version);
        if (ver is null)
        {
            return Results.NotFound();
        }

        using var doc = JsonDocument.Parse(ver.ConfigJson);
        return Results.Ok(new AgentVersionDetail(
            ver.Id, ver.AgentId, ver.Version, doc.RootElement.Clone(),
            ver.Notes, ver.CreatedAt));
    }

    // -----------------------------------------------------------------------
    // Story 32-2 — entity-aware registry / resolution surface (/api/agents)
    // -----------------------------------------------------------------------

    /// <summary>
    /// GET <c>/api/agents/resolve?role=&amp;phase=</c> — resolve the EFFECTIVE
    /// first-class agent for the calling principal + role via the 32-2
    /// precedence chain. Returns the enriched <see cref="ResolvedAgentConfig"/>
    /// (carrying <c>AgentId</c>/<c>AgentVersion</c>/extended <c>Source</c>).
    /// Unknown role/phase → 400; unresolvable (no selection + no system default)
    /// → 404 with code <c>agent_resolve_no_default</c> (the fail-loud branch
    /// NEVER returns a blank config).
    /// </summary>
    public static async Task<IResult> Resolve(
        string? role,
        string? phase,
        IAgentResolverService resolver)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return Results.BadRequest(new { error = "invalid_request", detail = "role is required." });
        }

        try
        {
            var resolved = string.IsNullOrWhiteSpace(phase)
                ? await resolver.ResolveForRoleAsync(role)
                : await resolver.ResolveForRoleAndPhaseAsync(phase, role);
            return Results.Ok(resolved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_role", detail = ex.Message });
        }
        catch (TammaError ex) when (ex.Code == "AGENT.RESOLVE.NO_DEFAULT")
        {
            // No blank config — fail loud. 404: no agent resolvable for the role.
            return Results.Json(
                new { error = "agent_resolve_no_default", detail = ex.Message },
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// PUT <c>/api/agents/role-selections/{role}</c> — select which agent serves
    /// a role for the calling principal. The target must be in (public ∪ own
    /// private) — a cross-tenant/non-existent target returns 404 (no existence
    /// leak). Member-role callers are 403'd by the <c>AgentManage</c> route
    /// policy before reaching here.
    /// </summary>
    public static async Task<IResult> SelectForRole(
        string role,
        SelectRoleRequest req,
        IAgentRegistryService registry,
        ClaimsPrincipal principal)
    {
        var canonicalRole = RolePhaseMap.NormalizeRole(role);
        if (!RolePhaseMap.ValidRoles.Contains(canonicalRole))
        {
            return Results.BadRequest(new
            {
                error = "invalid_role",
                detail = $"Unknown role '{role}'.",
            });
        }

        try
        {
            var selection = await registry.SelectForRoleAsync(
                canonicalRole, req.AgentId, principal.GetUserId());
            return Results.Ok(new AgentRoleSelectionResponse(
                selection.Role, selection.AgentId, selection.Visibility));
        }
        catch (TammaError ex) when (ex.Code == "AGENT.SELECT.NOT_FOUND")
        {
            // 404 (not 403) — never leak the existence of another tenant's agent.
            return Results.NotFound(new { error = "agent_not_found", detail = ex.Message });
        }
        catch (TammaError ex)
        {
            return Results.BadRequest(new { error = ex.Code, detail = ex.Message });
        }
    }

    /// <summary>
    /// GET <c>/api/agents/role-selections</c> — the calling principal's current
    /// role→agent selection map.
    /// </summary>
    public static async Task<IResult> GetRoleSelections(IAgentRegistryService registry)
    {
        var selections = await registry.GetRoleSelectionsAsync();
        var response = selections.Values
            .Select(s => new AgentRoleSelectionResponse(s.Role, s.AgentId, s.Visibility))
            .OrderBy(s => s.Role, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(response);
    }

    /// <summary>
    /// POST <c>/api/agents/{id}/rollback</c> — rollback (AC 13): repoint the
    /// agent's active version at an EXISTING prior version (no new snapshot). The
    /// subsequent resolve returns that version's config. RBAC matches the agent's
    /// ownership (public → platform admin; private → owning tenant/user); same
    /// 404/403 discipline as publish.
    /// </summary>
    public static async Task<IResult> RollbackVersion(
        Guid id,
        RollbackVersionRequest req,
        IAgentRepository agents,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var agent = await agents.GetByIdAsync(id);
        if (agent is null || !CanSeeAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.NotFound();
        }
        if (!CanWriteAgent(agent, principal, tenantContext, modeProvider))
        {
            return Results.Json(
                new { error = "forbidden", detail = "not permitted to roll back this agent." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var version = await agents.SetActiveVersionAsync(id, req.Version, principal.GetUserId());
        if (version is null)
        {
            // Agent exists (checked above) ⇒ the target version doesn't.
            return Results.NotFound(new
            {
                error = "version_not_found",
                detail = $"agent {id} has no version {req.Version}.",
            });
        }
        return Results.Ok(new PublishVersionResponse(version.Id, version.Version, version.CreatedAt));
    }

    // -----------------------------------------------------------------------
    // Story 32-1 — visibility / RBAC helpers
    // -----------------------------------------------------------------------

    private static AgentSummary ToSummary(Agent a) => new(
        a.Id, a.Name, a.Role, VisibilityWire(a.Visibility), StatusWire(a.Status),
        a.OwnerTenantId, a.OwnerUserId, a.CurrentVersionId);

    private static string VisibilityWire(AgentVisibility v)
        => v == AgentVisibility.Public ? "public" : "private";

    private static string StatusWire(AgentStatus s)
        => s == AgentStatus.Archived ? "archived" : "active";

    private static bool TryParseVisibility(string? raw, out AgentVisibility visibility)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "public": visibility = AgentVisibility.Public; return true;
            case "private": visibility = AgentVisibility.Private; return true;
            default: visibility = default; return false;
        }
    }

    private static bool IsPlatformAdmin(ClaimsPrincipal principal)
        => string.Equals(
            principal.FindFirst("platformRole")?.Value, "platform_admin", StringComparison.Ordinal);

    /// <summary>
    /// Resolve the caller's private-agent principal scope from the process
    /// mode. SaaS → (tenantId, null); single-user → (null, userId).
    /// </summary>
    private static (Guid? TenantId, Guid? UserId) ResolvePrincipalScope(
        ClaimsPrincipal principal, ITenantContext tenantContext, ITammaModeProvider modeProvider)
        => modeProvider.Mode == TammaMode.SaaS
            ? (tenantContext.TenantId, (Guid?)null)
            : ((Guid?)null, principal.GetUserId());

    /// <summary>
    /// Visibility check for reads: any public agent, or a private agent owned
    /// by the caller's scope. Mirrors <c>IAgentRepository.ListVisibleAsync</c>.
    /// </summary>
    private static bool CanSeeAgent(
        Agent agent, ClaimsPrincipal principal,
        ITenantContext tenantContext, ITammaModeProvider modeProvider)
    {
        if (agent.Visibility == AgentVisibility.Public)
        {
            return true;
        }
        var (tenantId, userId) = ResolvePrincipalScope(principal, tenantContext, modeProvider);
        return (tenantId is not null && agent.OwnerTenantId == tenantId) ||
               (userId is not null && agent.OwnerUserId == userId);
    }

    /// <summary>
    /// Write check: public agents require platform admin; private agents
    /// require the caller to own the agent in the current scope. (Member-role
    /// callers are already 403'd by the AgentManage route policy.)
    /// </summary>
    private static bool CanWriteAgent(
        Agent agent, ClaimsPrincipal principal,
        ITenantContext tenantContext, ITammaModeProvider modeProvider)
    {
        if (agent.Visibility == AgentVisibility.Public)
        {
            return IsPlatformAdmin(principal);
        }
        var (tenantId, userId) = ResolvePrincipalScope(principal, tenantContext, modeProvider);
        return (tenantId is not null && agent.OwnerTenantId == tenantId) ||
               (userId is not null && agent.OwnerUserId == userId);
    }

    private static bool IsUniqueViolation(Microsoft.EntityFrameworkCore.DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg &&
           pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Schema- AND semantic-level validation. Returns (valid, errors).
    /// Tolerant of empty configs (valid — fall through to platform defaults).
    ///
    /// <para>Story 32-1 — the rules were extracted into the shared
    /// <see cref="AgentConfigValidator"/> so the legacy <c>config</c> surface
    /// and the new Epic 32 agent-version surface validate identically (Finding
    /// 014 provider regex / budget range / ReDoS / prototype-pollution guards
    /// apply to both). This thin shim preserves the existing call sites.</para>
    /// </summary>
    private static (bool Valid, string[] Errors) ValidateConfigShape(string configJson)
        => AgentConfigValidator.Validate(configJson);
}
