using System.Security.Claims;
using Tamma.Api.Dtos.Prompts;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class PromptEndpoints
{
    public static async Task<IResult> ListAll(
        IPromptRepository promptRepo,
        ClaimsPrincipal principal)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var prompts = await promptRepo.ListAsync(userId is not null ? Guid.Parse(userId) : null);
        return Results.Ok(prompts.Select(p =>
            new PromptResponse(p.Role, p.Action, p.Template, p.SystemPrompt, p.Variables, p.EnableTools, p.MaxTokens, "user")));
    }

    public static Task<IResult> ListSystemDefaults()
    {
        return Task.FromResult(Results.Ok(new { message = "System defaults - stub" }));
    }

    public static Task<IResult> GetSystemDefault(string role, string action)
    {
        return Task.FromResult(Results.Ok(new PromptResponse(
            role, action,
            $"Default template for {role}/{action}",
            $"You are a {role} assistant.",
            Array.Empty<string>(), false, 4096, "system")));
    }

    public static async Task<IResult> GetPrompt(
        string role,
        string action,
        IPromptRepository promptRepo,
        ClaimsPrincipal principal)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var prompt = await promptRepo.GetAsync(
            userId is not null ? Guid.Parse(userId) : null,
            "role-action", role, action);

        if (prompt is null)
            return Results.Ok(new PromptResponse(
                role, action,
                $"Default template for {role}/{action}",
                $"You are a {role} assistant.",
                Array.Empty<string>(), false, 4096, "system"));

        return Results.Ok(new PromptResponse(
            prompt.Role, prompt.Action, prompt.Template, prompt.SystemPrompt,
            prompt.Variables, prompt.EnableTools, prompt.MaxTokens, "user"));
    }

    public static async Task<IResult> UpsertPrompt(
        string role,
        string action,
        UpsertPromptRequest req,
        IPromptRepository promptRepo,
        ClaimsPrincipal principal,
        ITenantContext tenantContext)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var prompt = await promptRepo.UpsertAsync(new PromptOverride
        {
            UserId = userId is not null ? Guid.Parse(userId) : null,
            TenantId = tenantContext.TenantId,
            Scope = "role-action",
            Role = role,
            Action = action,
            Template = req.Template,
            SystemPrompt = req.SystemPrompt,
            Variables = req.Variables ?? [],
            EnableTools = req.EnableTools ?? false,
            MaxTokens = req.MaxTokens ?? 4096
        });

        return Results.Ok(new PromptResponse(
            prompt.Role, prompt.Action, prompt.Template, prompt.SystemPrompt,
            prompt.Variables, prompt.EnableTools, prompt.MaxTokens, "user"));
    }

    public static async Task<IResult> DeletePrompt(
        string role,
        string action,
        IPromptRepository promptRepo,
        ClaimsPrincipal principal)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deleted = await promptRepo.DeleteAsync(
            userId is not null ? Guid.Parse(userId) : null,
            "role-action", role, action);
        return deleted
            ? Results.Ok(new { message = "Prompt override deleted" })
            : Results.NotFound(new { error = "Prompt override not found" });
    }

    public static async Task<IResult> UpsertSystemPrompt(
        string role,
        string action,
        UpsertPromptRequest req,
        IPromptRepository promptRepo,
        ITenantContext tenantContext)
    {
        var prompt = await promptRepo.UpsertAsync(new PromptOverride
        {
            UserId = null, // System-level
            TenantId = tenantContext.TenantId,
            Scope = "role-system",
            Role = role,
            Action = action,
            Template = req.Template,
            SystemPrompt = req.SystemPrompt,
            Variables = req.Variables ?? [],
            EnableTools = req.EnableTools ?? false,
            MaxTokens = req.MaxTokens ?? 4096
        });
        return Results.Ok(new { message = "System prompt updated" });
    }

    public static async Task<IResult> DeleteSystemPrompt(
        string role,
        string action,
        IPromptRepository promptRepo)
    {
        await promptRepo.DeleteAsync(null, "role-system", role, action);
        return Results.Ok(new { message = "System prompt deleted" });
    }

    public static Task<IResult> RenderPrompt(
        string role,
        string action,
        RenderPromptRequest req)
    {
        // Stub render
        return Task.FromResult(Results.Ok(new RenderedPromptResponse(
            $"System prompt for {role}",
            $"User prompt for {action}")));
    }
}
