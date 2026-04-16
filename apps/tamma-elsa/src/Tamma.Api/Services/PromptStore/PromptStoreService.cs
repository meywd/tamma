using System.Text.RegularExpressions;
using Tamma.Api.Auth;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PromptStore;

/// <summary>
/// Source layer from which a resolved prompt was produced.
/// </summary>
public enum PromptSource
{
    /// <summary>User's tenant-scoped <c>role-action</c> override.</summary>
    UserOverride,
    /// <summary>System default for a specific role+action pair.</summary>
    SystemRoleAction,
    /// <summary>User's tenant-scoped <c>action-default</c> override.</summary>
    UserActionDefault,
    /// <summary>Generic system default for an action, across all roles.</summary>
    SystemActionDefault,
}

/// <summary>
/// A resolved prompt template bundled with its source layer.
/// </summary>
public sealed record ResolvedPrompt(
    string Role,
    string Action,
    string Template,
    string SystemPrompt,
    IReadOnlyList<string> Variables,
    bool EnableTools,
    int MaxTokens,
    PromptSource Source);

/// <summary>
/// Rendering result for a template — the substituted text and any variables
/// that could not be resolved (either missing from the map or over size limit).
/// </summary>
public sealed record RenderResult(string Rendered, IReadOnlyList<string> Unresolved);

/// <summary>
/// Rendering result combining system and user prompt halves.
/// </summary>
public sealed record RenderedPromptPair(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Input record for upsert operations.
/// </summary>
public sealed record UpsertPromptInput(
    string Template,
    string? SystemPrompt = null,
    IReadOnlyList<string>? Variables = null,
    bool? EnableTools = null,
    int? MaxTokens = null);

/// <summary>
/// Prompt resolution service implementing the 4-layer role+action fallback
/// and 2-layer role-system fallback defined in <c>CLAUDE.md</c>.
/// <para>
/// Resolution order for <c>(userId, role, action)</c>:
/// </para>
/// <list type="number">
///   <item>User's role+action override → if exists, use it.</item>
///   <item>System default role+action template → if exists, use it.</item>
///   <item>User's action default override → if exists, use it.</item>
///   <item>System default action template → safety net.</item>
/// </list>
/// <para>
/// Resolution order for system prompt <c>(userId, role)</c>:
/// </para>
/// <list type="number">
///   <item>User's role system prompt override → if exists, use it.</item>
///   <item>System default role system prompt.</item>
/// </list>
/// </summary>
public sealed class PromptStoreService
{
    /// <summary>Maximum size of a single variable value (100 KB).</summary>
    public const int MaxVariableValueLength = 100_000;

    /// <summary>Maximum size of the rendered template (1 MB).</summary>
    public const int MaxTemplateLength = 1_000_000;

    private static readonly Regex VariablePattern = new(
        @"\{\{([^}]{1,64})\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPromptRepository _repository;

    public PromptStoreService(IPromptRepository repository)
    {
        _repository = repository;
    }

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the prompt for a (<paramref name="userId"/>, <paramref name="role"/>, <paramref name="action"/>)
    /// tuple following the 4-layer fallback order.
    /// </summary>
    public async Task<ResolvedPrompt?> ResolveRoleActionAsync(Guid? userId, string role, string action)
    {
        // Layer 1: user's role+action override
        if (userId is not null)
        {
            var userOverride = await _repository.GetAsync(userId, "role-action", role, action);
            if (userOverride is not null)
            {
                return ToResolved(role, action, userOverride, PromptSource.UserOverride);
            }
        }

        // Layer 2: system default role+action
        var systemRoleAction = SystemPrompts.GetRoleAction(role, action);
        if (systemRoleAction is not null)
        {
            return new ResolvedPrompt(
                Role: role,
                Action: action,
                Template: systemRoleAction.Template,
                SystemPrompt: systemRoleAction.SystemPrompt,
                Variables: systemRoleAction.Variables,
                EnableTools: systemRoleAction.EnableTools,
                MaxTokens: systemRoleAction.MaxTokens,
                Source: PromptSource.SystemRoleAction);
        }

        // Layer 3: user's action-default override
        if (userId is not null)
        {
            var userActionDefault = await _repository.GetAsync(userId, "action-default", null, action);
            if (userActionDefault is not null)
            {
                return ToResolved(role, action, userActionDefault, PromptSource.UserActionDefault);
            }
        }

        // Layer 4: system action default (safety net)
        var systemActionDefault = SystemPrompts.GetActionDefault(action);
        if (systemActionDefault is not null)
        {
            // Use the role-system prompt if the role is known, otherwise no system prompt
            var sysPrompt = SystemPrompts.RoleSystemPrompts.TryGetValue(role, out var rolePrompt)
                ? rolePrompt
                : string.Empty;
            return new ResolvedPrompt(
                Role: role,
                Action: action,
                Template: systemActionDefault.Template,
                SystemPrompt: sysPrompt,
                Variables: systemActionDefault.Variables,
                EnableTools: systemActionDefault.EnableTools,
                MaxTokens: systemActionDefault.MaxTokens,
                Source: PromptSource.SystemActionDefault);
        }

        return null;
    }

    /// <summary>
    /// Resolve the role system prompt (role identity preamble) for a user.
    /// Falls through to the shipped system default if the user has no override.
    /// </summary>
    public async Task<string?> ResolveRoleSystemAsync(Guid? userId, string role)
    {
        if (userId is not null)
        {
            var userOverride = await _repository.GetAsync(userId, "role-system", role, null);
            if (userOverride is not null)
            {
                return userOverride.Template;
            }
        }

        return SystemPrompts.RoleSystemPrompts.TryGetValue(role, out var prompt) ? prompt : null;
    }

    // -----------------------------------------------------------------------
    // Mutations (scope = role-action)
    // -----------------------------------------------------------------------

    /// <summary>Upsert a user role+action override.</summary>
    public async Task<PromptOverride> UpsertRoleActionAsync(
        Guid? userId,
        Guid? tenantId,
        string role,
        string action,
        UpsertPromptInput input)
    {
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            TenantId = tenantId,
            Scope = "role-action",
            Role = role,
            Action = action,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        });
    }

    public Task<bool> DeleteRoleActionAsync(Guid? userId, string role, string action)
        => _repository.DeleteAsync(userId, "role-action", role, action);

    // -----------------------------------------------------------------------
    // Mutations (scope = role-system)
    // -----------------------------------------------------------------------

    public async Task<PromptOverride> UpsertRoleSystemAsync(
        Guid? userId,
        Guid? tenantId,
        string role,
        UpsertPromptInput input)
    {
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            TenantId = tenantId,
            Scope = "role-system",
            Role = role,
            Action = null,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        });
    }

    public Task<bool> DeleteRoleSystemAsync(Guid? userId, string role)
        => _repository.DeleteAsync(userId, "role-system", role, null);

    // -----------------------------------------------------------------------
    // Mutations (scope = action-default)
    // -----------------------------------------------------------------------

    public async Task<PromptOverride> UpsertActionDefaultAsync(
        Guid? userId,
        Guid? tenantId,
        string action,
        UpsertPromptInput input)
    {
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            TenantId = tenantId,
            Scope = "action-default",
            Role = null,
            Action = action,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        });
    }

    public Task<bool> DeleteActionDefaultAsync(Guid? userId, string action)
        => _repository.DeleteAsync(userId, "action-default", null, action);

    // -----------------------------------------------------------------------
    // Listing
    // -----------------------------------------------------------------------

    public Task<List<PromptOverride>> ListUserOverridesAsync(Guid? userId)
        => _repository.ListAsync(userId);

    // -----------------------------------------------------------------------
    // Rendering (static — pure functions)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Render a single template by single-pass {{variable}} substitution.
    /// Values larger than <see cref="MaxVariableValueLength"/> are treated as
    /// unresolved. The resulting text is truncated at <see cref="MaxTemplateLength"/>.
    /// </summary>
    public static RenderResult Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return new RenderResult(string.Empty, Array.Empty<string>());
        }

        var unresolved = new HashSet<string>(StringComparer.Ordinal);

        var replaced = VariablePattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!variables.TryGetValue(key, out var value))
            {
                unresolved.Add(key);
                return match.Value;
            }
            if (value.Length > MaxVariableValueLength)
            {
                unresolved.Add(key);
                return match.Value;
            }
            return value;
        });

        if (replaced.Length > MaxTemplateLength)
        {
            replaced = replaced[..MaxTemplateLength];
        }

        return new RenderResult(replaced, unresolved.ToArray());
    }

    /// <summary>
    /// Render the combined system + user template halves, merging the unresolved
    /// variable sets without duplication.
    /// </summary>
    public static RenderedPromptPair RenderFull(
        string systemTemplate,
        string userTemplate,
        IReadOnlyDictionary<string, string> variables)
    {
        var system = Render(systemTemplate, variables);
        var user = Render(userTemplate, variables);

        var combined = new HashSet<string>(system.Unresolved, StringComparer.Ordinal);
        foreach (var v in user.Unresolved)
        {
            combined.Add(v);
        }

        return new RenderedPromptPair(system.Rendered, user.Rendered, combined.ToArray());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ResolvedPrompt ToResolved(
        string role,
        string action,
        PromptOverride o,
        PromptSource source) => new(
            Role: role,
            Action: action,
            Template: o.Template,
            SystemPrompt: o.SystemPrompt ?? string.Empty,
            Variables: o.Variables,
            EnableTools: o.EnableTools,
            MaxTokens: o.MaxTokens,
            Source: source);
}
