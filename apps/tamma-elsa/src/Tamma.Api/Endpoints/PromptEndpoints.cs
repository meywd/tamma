using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Minimal-API handlers for <c>/api/prompts</c>. These are the single-line
/// delegates wired in <c>Program.cs</c>; the heavy lifting lives in
/// <see cref="PromptStoreService"/> and <see cref="PromptEventsService"/>.
/// </summary>
public static class PromptEndpoints
{
    // =======================================================================
    // List
    // =======================================================================

    public static async Task<IResult> ListAll(
        PromptStoreService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        ILoggerFactory loggerFactory)
    {
        // Story 27-2 — SaaS mode lists tenant overrides; single-user mode
        // lists the caller's user-scoped overrides. The two surfaces are
        // disjoint thanks to the principal_xor CHECK on prompt_overrides.
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            var tenantOverrides = await store.ListTenantOverridesAsync(tenantId);
            var tenantResponse = tenantOverrides.Select(p => new PromptResponse(
                p.Role,
                p.Action,
                p.Template,
                p.SystemPrompt,
                p.Variables,
                p.EnableTools,
                p.MaxTokens,
                "tenant")).ToList();
            return Results.Ok(tenantResponse);
        }

        // Story 28-1 PR D moved prompt_overrides to the per-tenant DB, so
        // every read goes through PromptRepository.RequireTenantId() and
        // tenantDbFactory.CreateAsync(tid). Two ways this can blow up at
        // request time:
        //   1. tenantContext.TenantId is null. Possible briefly during
        //      oauth2-proxy → bridge → personal-tenant bootstrap, OR when
        //      EnsurePersonalTenantMiddleware bails out (e.g. slug-
        //      collision retries exhausted).
        //   2. Tenant exists but its DB isn't reachable yet — CP says the
        //      tenant is provisioning, the per-tenant DB is mid-migration,
        //      or the connection resolver hasn't warmed the pool.
        //
        // Both of those used to surface as a 500 with
        // InvalidOperationException, and the dashboard's useTenantPrompts
        // hook would render an error banner instead of the (perfectly fine)
        // system defaults that come from /api/prompts/system. Since "no
        // overrides yet" is a real, valid state for any new user, we
        // degrade to an empty list on either failure mode and log loudly
        // so ops can see the underlying cause.
        if (tenantContext.TenantId is null)
        {
            return Results.Ok(new List<PromptResponse>());
        }

        var userId = TryGetUserId(principal);
        try
        {
            var overrides = await store.ListUserOverridesAsync(userId);
            var response = overrides.Select(p => new PromptResponse(
                p.Role,
                p.Action,
                p.Template,
                p.SystemPrompt,
                p.Variables,
                p.EnableTools,
                p.MaxTokens,
                "user")).ToList();
            return Results.Ok(response);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            || ex is Npgsql.NpgsqlException
            || ex is Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Don't 500 the dashboard for an infra hiccup — the user-visible
            // behaviour matches "no overrides yet". The exception still
            // shows up in tamma-api logs (LogError), and the system defaults
            // still render via the parallel /api/prompts/system call.
            loggerFactory.CreateLogger("PromptEndpoints.ListAll").LogError(ex,
                "ListAll: returning empty list because per-tenant prompt store " +
                "could not be queried (tenant={TenantId}, user={UserId})",
                tenantContext.TenantId, userId);
            return Results.Ok(new List<PromptResponse>());
        }
    }

    // =======================================================================
    // System defaults (read-only)
    // =======================================================================

    public static Task<IResult> ListSystemDefaults()
    {
        var roleAction = SystemPrompts.RoleActionTemplates
            .Select(t => new PromptResponse(
                t.Role,
                t.Action,
                t.Template,
                t.SystemPrompt,
                t.Variables.ToArray(),
                t.EnableTools,
                t.MaxTokens,
                "system"))
            .ToList();

        // Story 27-18 — the generic action-default tier is gone; the payload now
        // exposes only the jagged (role, action) templates + the role identity
        // preambles.
        var response = new SystemDefaultsResponse(
            RoleActionTemplates: roleAction,
            SystemPrompts: SystemPrompts.RoleSystemPrompts);

        return Task.FromResult(Results.Ok(response));
    }

    public static Task<IResult> GetSystemDefault(string role, string action)
    {
        var template = SystemPrompts.GetRoleAction(role, action);
        if (template is null)
        {
            return Task.FromResult(Results.NotFound(new { error = "No system default for this role/action" }));
        }

        return Task.FromResult(Results.Ok(new PromptResponse(
            template.Role,
            template.Action,
            template.Template,
            template.SystemPrompt,
            template.Variables.ToArray(),
            template.EnableTools,
            template.MaxTokens,
            "system")));
    }

    // =======================================================================
    // User role+action overrides
    // =======================================================================

    public static async Task<IResult> GetPrompt(
        string role,
        string action,
        PromptStoreService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        // Story 27-2 — SaaS mode reads through the tenant-scoped resolver
        // (no user override layer on top, by design — see CLAUDE.md
        // "Resolution Order — SaaS mode"). Single-user mode keeps the
        // user-scoped resolution keyed on userId.
        //
        // Story 27-18 — resolution now throws TammaError (no override + no
        // system default) instead of returning null. At this HTTP boundary we
        // translate that into the existing 404 contract; the service still fails
        // loud internally (no silent empty/plain fallback).
        ResolvedPrompt resolved;
        try
        {
            if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
            {
                resolved = await store.ResolveRoleActionForTenantAsync(tenantId, role, action);
            }
            else
            {
                var userId = TryGetUserId(principal);
                resolved = await store.ResolveRoleActionAsync(userId, role, action);
            }
        }
        catch (TammaError)
        {
            return Results.NotFound(new { error = "No prompt available for this role/action" });
        }

        var sourceLabel = resolved.Source switch
        {
            PromptSource.UserOverride => "user",
            PromptSource.TenantOverride => "tenant",
            _ => "system",
        };

        return Results.Ok(new PromptResponse(
            resolved.Role,
            resolved.Action,
            resolved.Template,
            resolved.SystemPrompt,
            resolved.Variables.ToArray(),
            resolved.EnableTools,
            resolved.MaxTokens,
            sourceLabel));
    }

    public static async Task<IResult> UpsertPrompt(
        string role,
        string action,
        UpsertPromptRequest req,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = TryGetUserId(principal);
        var input = new UpsertPromptInput(
            Template: req.Template,
            SystemPrompt: req.SystemPrompt,
            Variables: req.Variables,
            EnableTools: req.EnableTools,
            MaxTokens: req.MaxTokens);

        // Story 27-2 — SaaS mode upserts a tenant-scoped row; single-user
        // mode upserts the caller's user-scoped row. The endpoint is RBAC-
        // gated by the PromptManage policy (prompts:manage permission =
        // admin+owner — Auth/Permissions.cs), so member users in SaaS mode
        // hit a 403 BEFORE reaching this method.
        PromptOverride saved;
        bool wasCreated;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            (saved, wasCreated) = await store.UpsertRoleActionForTenantAsync(
                tenantId, userId, role, action, input);
        }
        else
        {
            // Repository tells us whether this was a CREATE or UPDATE so we can
            // emit the right DCB event type (audit prompts/007).
            (saved, wasCreated) = await store.UpsertRoleActionAsync(userId, null, role, action, input);
        }

        var emitData = new Dictionary<string, object?>
        {
            ["templateLength"] = saved.Template.Length,
            ["enableTools"] = saved.EnableTools,
            ["maxTokens"] = saved.MaxTokens,
        };
        if (wasCreated)
        {
            await events.EmitCreatedAsync(tenantContext.TenantId, userId, role, action, emitData);
        }
        else
        {
            await events.EmitUpdatedAsync(tenantContext.TenantId, userId, role, action, emitData);
        }

        var sourceLabel = modeProvider.Mode == TammaMode.SaaS ? "tenant" : "user";
        return Results.Ok(new PromptResponse(
            saved.Role,
            saved.Action,
            saved.Template,
            saved.SystemPrompt,
            saved.Variables,
            saved.EnableTools,
            saved.MaxTokens,
            sourceLabel));
    }

    /// <summary>
    /// Delete a user's role+action override. Per CLAUDE.md ("delete user
    /// override — falls back to system default"), this is semantically a
    /// reset-to-default operation, so we emit <c>PROMPT.RESET.SUCCESS</c>
    /// rather than a generic delete (audit prompts/007).
    /// </summary>
    public static async Task<IResult> DeletePrompt(
        string role,
        string action,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = TryGetUserId(principal);
        bool deleted;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            deleted = await store.DeleteRoleActionForTenantAsync(tenantId, role, action);
        }
        else
        {
            deleted = await store.DeleteRoleActionAsync(userId, role, action);
        }

        if (!deleted)
        {
            return Results.NotFound(new { error = "Prompt override not found" });
        }

        await events.EmitResetAsync(tenantContext.TenantId, userId, role, action);

        return Results.Ok(new { message = "Prompt override deleted" });
    }

    // =======================================================================
    // System prompt overrides (scope = role-system)
    // CLAUDE.md "Prompt Store Architecture" defines role-system overrides as
    // keyed by (userId, role) only — there is no action axis. The earlier port
    // accepted an {action} URL segment but silently ignored it (audit prompts/
    // 005); the route is now {role}-only to match the data model.
    // =======================================================================

    public static async Task<IResult> UpsertSystemPrompt(
        string role,
        UpsertPromptRequest req,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = TryGetUserId(principal);
        var input = new UpsertPromptInput(
            Template: req.Template,
            SystemPrompt: req.SystemPrompt,
            Variables: req.Variables,
            EnableTools: req.EnableTools,
            MaxTokens: req.MaxTokens);

        PromptOverride saved;
        bool wasCreated;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            (saved, wasCreated) = await store.UpsertRoleSystemForTenantAsync(
                tenantId, userId, role, input);
        }
        else
        {
            (saved, wasCreated) = await store.UpsertRoleSystemAsync(userId, null, role, input);
        }

        var emitData = new Dictionary<string, object?>
        {
            ["scope"] = "role-system",
            ["templateLength"] = saved.Template.Length,
        };
        if (wasCreated)
        {
            await events.EmitCreatedAsync(tenantContext.TenantId, userId, role, string.Empty, emitData);
        }
        else
        {
            await events.EmitUpdatedAsync(tenantContext.TenantId, userId, role, string.Empty, emitData);
        }

        return Results.Ok(new { message = "System prompt updated", scope = "role-system", role });
    }

    public static async Task<IResult> DeleteSystemPrompt(
        string role,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = TryGetUserId(principal);
        bool deleted;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            deleted = await store.DeleteRoleSystemForTenantAsync(tenantId, role);
        }
        else
        {
            deleted = await store.DeleteRoleSystemAsync(userId, role);
        }

        if (!deleted)
        {
            return Results.NotFound(new { error = "System prompt override not found" });
        }

        await events.EmitResetAsync(tenantContext.TenantId, userId, role, string.Empty);

        return Results.Ok(new { message = "System prompt deleted" });
    }

    // =======================================================================
    // Render
    // =======================================================================

    public static async Task<IResult> RenderPrompt(
        string role,
        string action,
        RenderPromptRequest req,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = TryGetUserId(principal);
        // Story 27-18 — resolution fails loud; translate TammaError into the
        // existing 404 contract at this HTTP boundary.
        ResolvedPrompt resolved;
        try
        {
            if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
            {
                resolved = await store.ResolveRoleActionForTenantAsync(tenantId, role, action);
            }
            else
            {
                resolved = await store.ResolveRoleActionAsync(userId, role, action);
            }
        }
        catch (TammaError)
        {
            return Results.NotFound(new { error = "No prompt available for this role/action" });
        }

        // Ensure the role variable is always available if missing from the request
        var variables = new Dictionary<string, string>(req.Variables ?? new Dictionary<string, string>());
        variables.TryAdd("role", role);

        var rendered = PromptStoreService.RenderFull(
            systemTemplate: resolved.SystemPrompt,
            userTemplate: resolved.Template,
            variables: variables);

        await events.EmitRenderedAsync(
            tenantContext.TenantId,
            userId,
            role,
            action,
            variableCount: variables.Count,
            unresolvedCount: rendered.Unresolved.Count);

        // Version is sourced from the resolved override row when present, else 1
        // (system-shipped templates are unversioned in the SystemPrompts registry).
        // Field names match the TS RenderedPrompt contract (audit prompts/003).
        return Results.Ok(new RenderedPromptResponse(
            Role: resolved.Role,
            Action: resolved.Action,
            Version: resolved.Version,
            RenderedTemplate: rendered.UserPrompt,
            RenderedSystemPrompt: rendered.SystemPrompt,
            EnableTools: resolved.EnableTools,
            MaxTokens: resolved.MaxTokens,
            UnresolvedVariables: rendered.Unresolved.ToArray()));
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private static Guid? TryGetUserId(ClaimsPrincipal principal)
        => principal.GetUserId();
}
