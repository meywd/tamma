using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Services.PromptStore;
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
        ITammaModeProvider modeProvider)
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

        var userId = TryGetUserId(principal);
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

        var actionDefaults = SystemPrompts.ActionDefaults.ToDictionary(
            kv => kv.Key,
            kv => new PromptResponse(
                kv.Value.Role,
                kv.Value.Action,
                kv.Value.Template,
                kv.Value.SystemPrompt,
                kv.Value.Variables.ToArray(),
                kv.Value.EnableTools,
                kv.Value.MaxTokens,
                "system"));

        var response = new SystemDefaultsResponse(
            RoleActionTemplates: roleAction,
            SystemPrompts: SystemPrompts.RoleSystemPrompts,
            ActionDefaults: actionDefaults);

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

    /// <summary>
    /// Get the system action-default template for an action (Layer-4 safety net
    /// in the 4-layer resolution model). Per CLAUDE.md API spec
    /// <c>GET /api/prompts/defaults/:action</c>. The template's Role is null
    /// because action-defaults are role-agnostic; the <c>{{role}}</c>
    /// placeholder in the body is interpolated at render time.
    /// </summary>
    public static Task<IResult> GetActionDefault(string action)
    {
        var template = SystemPrompts.GetActionDefault(action);
        if (template is null)
        {
            return Task.FromResult(Results.NotFound(new { error = "No action default for this action" }));
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
        // legacy 4-layer fallback keyed on userId.
        ResolvedPrompt? resolved;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            resolved = await store.ResolveRoleActionForTenantAsync(tenantId, role, action);
        }
        else
        {
            var userId = TryGetUserId(principal);
            resolved = await store.ResolveRoleActionAsync(userId, role, action);
        }

        if (resolved is null)
        {
            return Results.NotFound(new { error = "No prompt available for this role/action" });
        }

        var sourceLabel = resolved.Source switch
        {
            PromptSource.UserOverride or PromptSource.UserActionDefault => "user",
            PromptSource.TenantOverride or PromptSource.TenantActionDefault => "tenant",
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
        ResolvedPrompt? resolved;
        if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
        {
            resolved = await store.ResolveRoleActionForTenantAsync(tenantId, role, action);
        }
        else
        {
            resolved = await store.ResolveRoleActionAsync(userId, role, action);
        }

        if (resolved is null)
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
