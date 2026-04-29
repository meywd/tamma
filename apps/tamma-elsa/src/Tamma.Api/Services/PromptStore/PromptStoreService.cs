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
    /// <summary>User's <c>role-action</c> override (single-user mode).</summary>
    UserOverride,
    /// <summary>System default for a specific role+action pair.</summary>
    SystemRoleAction,
    /// <summary>User's <c>action-default</c> override (single-user mode).</summary>
    UserActionDefault,
    /// <summary>Generic system default for an action, across all roles.</summary>
    SystemActionDefault,
    /// <summary>Tenant's <c>role-action</c> override (SaaS mode, Story 27-2).</summary>
    TenantOverride,
    /// <summary>Tenant's <c>action-default</c> override (SaaS mode, Story 27-2).</summary>
    TenantActionDefault,
}

/// <summary>
/// A resolved prompt template bundled with its source layer.
/// </summary>
/// <param name="Version">Override version (from <c>prompt_overrides.Version</c>) when the
/// resolution layer is user-scoped; defaults to 1 for system-shipped templates which are
/// unversioned in the in-memory registry.</param>
public sealed record ResolvedPrompt(
    string Role,
    string Action,
    string Template,
    string SystemPrompt,
    IReadOnlyList<string> Variables,
    bool EnableTools,
    int MaxTokens,
    PromptSource Source,
    int Version = 1);

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

    /// <summary>
    /// Upsert a user role+action override. Returns the persisted entity and
    /// a <c>wasCreated</c> flag the endpoint uses to choose between the
    /// <c>PROMPT.CREATED.SUCCESS</c> and <c>PROMPT.UPDATED.SUCCESS</c> event
    /// types (audit prompts/007).
    /// </summary>
    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleActionAsync(
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

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleSystemAsync(
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

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertActionDefaultAsync(
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

    // =======================================================================
    // SaaS-mode resolution (Story 27-2)
    //
    // The single-user methods above are keyed on <c>userId</c>. SaaS-mode
    // methods are keyed on <c>tenantId</c> — tenant_admin owns the team's
    // overrides, member users read them without edit access. There is
    // intentionally NO per-user override layer on top of tenant overrides
    // (CLAUDE.md "Prompt Store Architecture / Resolution Order — SaaS mode").
    // The two surfaces are PARALLEL, not layered.
    //
    // Mode is settled at process startup (Tamma:Mode env or the entry-point
    // binary); request handlers pick the right method by inspecting whether
    // tenant_admin context is present, NOT by inspecting both keys per
    // request.
    // =======================================================================

    /// <summary>
    /// Resolve the prompt for a (<paramref name="tenantId"/>, <paramref name="role"/>, <paramref name="action"/>)
    /// tuple following the SaaS-mode 4-layer fallback order:
    /// <list type="number">
    ///   <item>Tenant's role+action override → if exists, use it.</item>
    ///   <item>System default role+action → if exists, use it.</item>
    ///   <item>Tenant's action-default override → if exists, use it.</item>
    ///   <item>System default action template → safety net.</item>
    /// </list>
    /// <para>Method name carries the <c>ForTenant</c> suffix instead of
    /// overloading on parameter type — the user-scoped variant takes
    /// <c>Guid? userId</c>, and a non-null <c>Guid</c> binds to BOTH the
    /// nullable overload and a same-named <c>Guid</c> overload (the latter
    /// always wins by C# overload-resolution rules), which would route
    /// every existing single-user-mode call site to the SaaS path.
    /// Distinct names keep the two surfaces unambiguous.</para>
    /// </summary>
    public async Task<ResolvedPrompt?> ResolveRoleActionForTenantAsync(Guid tenantId, string role, string action)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        // Layer 1: tenant's role+action override
        var tenantOverride = await _repository.GetByTenantAsync(tenantId, "role-action", role, action);
        if (tenantOverride is not null)
        {
            return ToResolved(role, action, tenantOverride, PromptSource.TenantOverride);
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

        // Layer 3: tenant's action-default override
        var tenantActionDefault = await _repository.GetByTenantAsync(tenantId, "action-default", null, action);
        if (tenantActionDefault is not null)
        {
            return ToResolved(role, action, tenantActionDefault, PromptSource.TenantActionDefault);
        }

        // Layer 4: system action default (safety net)
        var systemActionDefault = SystemPrompts.GetActionDefault(action);
        if (systemActionDefault is not null)
        {
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
    /// Resolve the role system prompt (role identity preamble) for a tenant.
    /// SaaS-mode 2-layer fallback:
    /// <list type="number">
    ///   <item>Tenant's role-system override → if exists, use it.</item>
    ///   <item>System default role system prompt.</item>
    /// </list>
    /// </summary>
    public async Task<string?> ResolveRoleSystemForTenantAsync(Guid tenantId, string role)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var tenantOverride = await _repository.GetByTenantAsync(tenantId, "role-system", role, null);
        if (tenantOverride is not null)
        {
            return tenantOverride.Template;
        }

        return SystemPrompts.RoleSystemPrompts.TryGetValue(role, out var prompt) ? prompt : null;
    }

    /// <summary>
    /// Upsert a tenant role+action override. Returns the persisted entity
    /// and a <c>wasCreated</c> flag the endpoint uses to choose between
    /// <c>PROMPT.CREATED.SUCCESS</c> and <c>PROMPT.UPDATED.SUCCESS</c> events.
    /// <para><c>actingUserId</c> records WHO inside the tenant performed the
    /// edit (audit trail) — only tenant_owner / tenant_admin should reach
    /// here; the API layer enforces RBAC via the <c>settings:manage</c>
    /// permission (members get 403).</para>
    /// </summary>
    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleActionForTenantAsync(
        Guid tenantId,
        Guid? actingUserId,
        string role,
        string action,
        UpsertPromptInput input)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = null,           // SaaS-mode row: principal_xor → tenant_id only
            TenantId = tenantId,
            Scope = "role-action",
            Role = role,
            Action = action,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        }, actingUserId);
    }

    public Task<bool> DeleteRoleActionForTenantAsync(Guid tenantId, string role, string action)
        => _repository.DeleteByTenantAsync(tenantId, "role-action", role, action);

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleSystemForTenantAsync(
        Guid tenantId,
        Guid? actingUserId,
        string role,
        UpsertPromptInput input)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = null,
            TenantId = tenantId,
            Scope = "role-system",
            Role = role,
            Action = null,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        }, actingUserId);
    }

    public Task<bool> DeleteRoleSystemForTenantAsync(Guid tenantId, string role)
        => _repository.DeleteByTenantAsync(tenantId, "role-system", role, null);

    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertActionDefaultForTenantAsync(
        Guid tenantId,
        Guid? actingUserId,
        string action,
        UpsertPromptInput input)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        return await _repository.UpsertAsync(new PromptOverride
        {
            UserId = null,
            TenantId = tenantId,
            Scope = "action-default",
            Role = null,
            Action = action,
            Template = input.Template,
            SystemPrompt = input.SystemPrompt,
            Variables = input.Variables?.ToArray() ?? Array.Empty<string>(),
            EnableTools = input.EnableTools ?? false,
            MaxTokens = input.MaxTokens ?? 4096,
        }, actingUserId);
    }

    public Task<bool> DeleteActionDefaultForTenantAsync(Guid tenantId, string action)
        => _repository.DeleteByTenantAsync(tenantId, "action-default", null, action);

    /// <summary>
    /// List every tenant-scoped override row for <paramref name="tenantId"/>.
    /// Equivalent of <see cref="ListUserOverridesAsync"/> for SaaS mode —
    /// member users hit this through GET /api/prompts to see what their
    /// tenant_admin has customised.
    /// </summary>
    public Task<List<PromptOverride>> ListTenantOverridesAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        return _repository.ListByTenantAsync(tenantId);
    }

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
            Source: source,
            Version: o.Version);
}
