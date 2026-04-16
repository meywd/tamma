using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
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
        ClaimsPrincipal principal)
    {
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

    // =======================================================================
    // User role+action overrides
    // =======================================================================

    public static async Task<IResult> GetPrompt(
        string role,
        string action,
        PromptStoreService store,
        ClaimsPrincipal principal)
    {
        var userId = TryGetUserId(principal);
        var resolved = await store.ResolveRoleActionAsync(userId, role, action);

        if (resolved is null)
        {
            return Results.NotFound(new { error = "No prompt available for this role/action" });
        }

        return Results.Ok(new PromptResponse(
            resolved.Role,
            resolved.Action,
            resolved.Template,
            resolved.SystemPrompt,
            resolved.Variables.ToArray(),
            resolved.EnableTools,
            resolved.MaxTokens,
            resolved.Source == PromptSource.UserOverride || resolved.Source == PromptSource.UserActionDefault
                ? "user"
                : "system"));
    }

    public static async Task<IResult> UpsertPrompt(
        string role,
        string action,
        UpsertPromptRequest req,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        var userId = TryGetUserId(principal);
        var input = new UpsertPromptInput(
            Template: req.Template,
            SystemPrompt: req.SystemPrompt,
            Variables: req.Variables,
            EnableTools: req.EnableTools,
            MaxTokens: req.MaxTokens);

        var saved = await store.UpsertRoleActionAsync(userId, tenantContext.TenantId, role, action, input);

        await events.EmitUpdatedAsync(
            tenantContext.TenantId,
            userId,
            role,
            action,
            new Dictionary<string, object?>
            {
                ["templateLength"] = saved.Template.Length,
                ["enableTools"] = saved.EnableTools,
                ["maxTokens"] = saved.MaxTokens,
            });

        return Results.Ok(new PromptResponse(
            saved.Role,
            saved.Action,
            saved.Template,
            saved.SystemPrompt,
            saved.Variables,
            saved.EnableTools,
            saved.MaxTokens,
            "user"));
    }

    public static async Task<IResult> DeletePrompt(
        string role,
        string action,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        var userId = TryGetUserId(principal);
        var deleted = await store.DeleteRoleActionAsync(userId, role, action);

        if (!deleted)
        {
            return Results.NotFound(new { error = "Prompt override not found" });
        }

        await events.EmitDeletedAsync(tenantContext.TenantId, userId, role, action);

        return Results.Ok(new { message = "Prompt override deleted" });
    }

    // =======================================================================
    // System prompt overrides (scope = role-system)
    // =======================================================================

    public static async Task<IResult> UpsertSystemPrompt(
        string role,
        string action,
        UpsertPromptRequest req,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        var userId = TryGetUserId(principal);
        var input = new UpsertPromptInput(
            Template: req.Template,
            SystemPrompt: req.SystemPrompt,
            Variables: req.Variables,
            EnableTools: req.EnableTools,
            MaxTokens: req.MaxTokens);

        var saved = await store.UpsertRoleSystemAsync(userId, tenantContext.TenantId, role, input);

        await events.EmitUpdatedAsync(
            tenantContext.TenantId,
            userId,
            role,
            action,
            new Dictionary<string, object?>
            {
                ["scope"] = "role-system",
                ["templateLength"] = saved.Template.Length,
            });

        return Results.Ok(new { message = "System prompt updated", scope = "role-system", role });
    }

    public static async Task<IResult> DeleteSystemPrompt(
        string role,
        string action,
        PromptStoreService store,
        PromptEventsService events,
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        var userId = TryGetUserId(principal);
        var deleted = await store.DeleteRoleSystemAsync(userId, role);

        if (!deleted)
        {
            return Results.NotFound(new { error = "System prompt override not found" });
        }

        await events.EmitDeletedAsync(tenantContext.TenantId, userId, role, action);

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
        ITenantContext tenantContext)
    {
        var userId = TryGetUserId(principal);
        var resolved = await store.ResolveRoleActionAsync(userId, role, action);

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

        return Results.Ok(new RenderedPromptResponse(
            SystemPrompt: rendered.SystemPrompt,
            UserPrompt: rendered.UserPrompt,
            Unresolved: rendered.Unresolved.ToArray()));
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private static Guid? TryGetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
