using System.Text.RegularExpressions;
using Tamma.Api.Auth;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PromptStore;

/// <summary>
/// Source layer from which a resolved prompt was produced.
///
/// <para>Story 27-18 — the generic <c>action-default</c> tier is GONE.
/// Resolution is exactly <c>override → system default → TammaError</c>; there is
/// no <c>UserActionDefault</c> / <c>SystemActionDefault</c> / <c>TenantActionDefault</c>
/// source any more.</para>
/// </summary>
public enum PromptSource
{
    /// <summary>User's <c>role-action</c> override (single-user mode).</summary>
    UserOverride,
    /// <summary>System default for a specific role+action pair.</summary>
    SystemRoleAction,
    /// <summary>Tenant's <c>role-action</c> override (SaaS mode, Story 27-2).</summary>
    TenantOverride,
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
/// Prompt resolution service implementing the fail-loud role+action resolution
/// and 2-layer role-system fallback defined in <c>CLAUDE.md</c> /
/// SPEC §3, §7 (<c>docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md</c>).
/// <para>
/// <b>Story 27-18.</b> Resolution order for <c>(userId, role, action)</c> follows
/// EXACTLY one chain — there is NO generic action-default tier and NO empty/plain
/// terminal fallback:
/// </para>
/// <list type="number">
///   <item>User's role+action override → if exists, use it.</item>
///   <item>System default role+action template → if exists, use it.</item>
///   <item>Otherwise → throw <see cref="TammaError"/>. A taxonomy-valid
///         <c>(role, action)</c> with no override and no system default is a hard
///         error, never a silent empty/degraded prompt (SPEC §7).</item>
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
    private readonly PromptEventsService? _events;

    /// <summary>
    /// IMP-2 (Wave B): primary constructor — inject <see cref="PromptEventsService"/> so
    /// all DCB audit events are emitted from the service layer, not from endpoint handlers.
    /// Any future non-HTTP caller (CLI tool, Elsa activity, admin script) therefore gets
    /// a full audit trail automatically, mirroring the 27-14 convention-store pattern.
    /// </summary>
    public PromptStoreService(IPromptRepository repository, PromptEventsService events)
    {
        _repository = repository;
        _events = events;
    }

    /// <summary>
    /// Backward-compat constructor used by unit tests that construct the service
    /// without an event repository. Production DI always uses the two-argument
    /// overload.
    /// </summary>
    internal PromptStoreService(IPromptRepository repository)
    {
        _repository = repository;
        _events = null;
    }

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the prompt for a (<paramref name="userId"/>, <paramref name="role"/>, <paramref name="action"/>)
    /// tuple following the fail-loud chain: user override → system default →
    /// <see cref="TammaError"/>. There is no action-default tier and no
    /// empty/plain terminal — a <c>(role, action)</c> with neither an override
    /// nor a system default is a hard error (SPEC §7, Story 27-18).
    /// </summary>
    /// <exception cref="TammaError">
    /// No user override and no system default for <c>(role, action)</c>.
    /// </exception>
    public async Task<ResolvedPrompt> ResolveRoleActionAsync(Guid? userId, string role, string action)
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

        // No fallback tier — fail loud (Story 27-18 / SPEC §7).
        throw NoPromptError(role, action, userId: userId, tenantId: null);
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
    /// a <c>wasCreated</c> flag. DCB audit events (<c>PROMPT.CREATED.SUCCESS</c> /
    /// <c>PROMPT.UPDATED.SUCCESS</c>) are emitted from within this method (IMP-2).
    /// </summary>
    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleActionAsync(
        Guid? userId,
        Guid? tenantId,
        string role,
        string action,
        UpsertPromptInput input)
    {
        var (saved, wasCreated) = await _repository.UpsertAsync(new PromptOverride
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

        // IMP-2: emit DCB event from the service layer so any future non-HTTP
        // caller (CLI tool, Elsa activity, admin script) gets a full audit trail.
        if (_events is not null)
        {
            var emitData = new Dictionary<string, object?>
            {
                ["templateLength"] = saved.Template.Length,
                ["enableTools"]    = saved.EnableTools,
                ["maxTokens"]      = saved.MaxTokens,
            };
            if (wasCreated)
                await _events.EmitCreatedAsync(tenantId, userId, role, action, emitData);
            else
                await _events.EmitUpdatedAsync(tenantId, userId, role, action, emitData);
        }

        return (saved, wasCreated);
    }

    /// <summary>
    /// Delete a user role+action override. Emits <c>PROMPT.RESET.SUCCESS</c>
    /// on success (IMP-2 — audit trail from the service layer).
    /// </summary>
    public async Task<bool> DeleteRoleActionAsync(Guid? userId, string role, string action)
    {
        var deleted = await _repository.DeleteAsync(userId, "role-action", role, action);

        if (deleted && _events is not null)
            await _events.EmitResetAsync(null, userId, role, action);

        return deleted;
    }

    // -----------------------------------------------------------------------
    // Mutations (scope = role-system)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Upsert a user role-system (identity preamble) override. Emits
    /// <c>PROMPT.CREATED.SUCCESS</c> or <c>PROMPT.UPDATED.SUCCESS</c> from
    /// within this method (IMP-2).
    /// </summary>
    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleSystemAsync(
        Guid? userId,
        Guid? tenantId,
        string role,
        UpsertPromptInput input)
    {
        var (saved, wasCreated) = await _repository.UpsertAsync(new PromptOverride
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

        // IMP-2: emit DCB event from the service layer (action = "" matches the
        // pre-refactor endpoint convention for role-system scope).
        if (_events is not null)
        {
            var emitData = new Dictionary<string, object?>
            {
                ["scope"]          = "role-system",
                ["templateLength"] = saved.Template.Length,
            };
            if (wasCreated)
                await _events.EmitCreatedAsync(tenantId, userId, role, string.Empty, emitData);
            else
                await _events.EmitUpdatedAsync(tenantId, userId, role, string.Empty, emitData);
        }

        return (saved, wasCreated);
    }

    /// <summary>
    /// Delete a user role-system override. Emits <c>PROMPT.RESET.SUCCESS</c>
    /// on success (IMP-2 — audit trail from the service layer).
    /// </summary>
    public async Task<bool> DeleteRoleSystemAsync(Guid? userId, string role)
    {
        var deleted = await _repository.DeleteAsync(userId, "role-system", role, null);

        if (deleted && _events is not null)
            await _events.EmitResetAsync(null, userId, role, string.Empty);

        return deleted;
    }

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
    /// tuple following the SaaS-mode fail-loud chain (Story 27-18 / SPEC §7):
    /// <list type="number">
    ///   <item>Tenant's role+action override → if exists, use it.</item>
    ///   <item>System default role+action → if exists, use it.</item>
    ///   <item>Otherwise → throw <see cref="TammaError"/>. No action-default tier,
    ///         no empty/plain terminal.</item>
    /// </list>
    /// <para>Method name carries the <c>ForTenant</c> suffix instead of
    /// overloading on parameter type — the user-scoped variant takes
    /// <c>Guid? userId</c>, and a non-null <c>Guid</c> binds to BOTH the
    /// nullable overload and a same-named <c>Guid</c> overload (the latter
    /// always wins by C# overload-resolution rules), which would route
    /// every existing single-user-mode call site to the SaaS path.
    /// Distinct names keep the two surfaces unambiguous.</para>
    /// </summary>
    /// <exception cref="TammaError">
    /// No tenant override and no system default for <c>(role, action)</c>.
    /// </exception>
    public async Task<ResolvedPrompt> ResolveRoleActionForTenantAsync(Guid tenantId, string role, string action)
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

        // No fallback tier — fail loud (Story 27-18 / SPEC §7).
        throw NoPromptError(role, action, userId: null, tenantId: tenantId);
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
    /// Upsert a tenant role+action override. Emits <c>PROMPT.CREATED.SUCCESS</c> /
    /// <c>PROMPT.UPDATED.SUCCESS</c> from within this method (IMP-2).
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

        var (saved, wasCreated) = await _repository.UpsertAsync(new PromptOverride
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

        // IMP-2: emit DCB event from the service layer.
        if (_events is not null)
        {
            var emitData = new Dictionary<string, object?>
            {
                ["templateLength"] = saved.Template.Length,
                ["enableTools"]    = saved.EnableTools,
                ["maxTokens"]      = saved.MaxTokens,
            };
            if (wasCreated)
                await _events.EmitCreatedAsync(tenantId, actingUserId, role, action, emitData);
            else
                await _events.EmitUpdatedAsync(tenantId, actingUserId, role, action, emitData);
        }

        return (saved, wasCreated);
    }

    /// <summary>
    /// Delete a tenant role+action override. Emits <c>PROMPT.RESET.SUCCESS</c>
    /// on success (IMP-2).
    /// </summary>
    public async Task<bool> DeleteRoleActionForTenantAsync(Guid tenantId, string role, string action)
    {
        var deleted = await _repository.DeleteByTenantAsync(tenantId, "role-action", role, action);

        if (deleted && _events is not null)
            await _events.EmitResetAsync(tenantId, null, role, action);

        return deleted;
    }

    /// <summary>
    /// Upsert a tenant role-system override. Emits <c>PROMPT.CREATED.SUCCESS</c> /
    /// <c>PROMPT.UPDATED.SUCCESS</c> from within this method (IMP-2).
    /// </summary>
    public async Task<(PromptOverride Entity, bool WasCreated)> UpsertRoleSystemForTenantAsync(
        Guid tenantId,
        Guid? actingUserId,
        string role,
        UpsertPromptInput input)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var (saved, wasCreated) = await _repository.UpsertAsync(new PromptOverride
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

        // IMP-2: emit DCB event from the service layer (action = "" mirrors the
        // pre-refactor endpoint convention for role-system scope).
        if (_events is not null)
        {
            var emitData = new Dictionary<string, object?>
            {
                ["scope"]          = "role-system",
                ["templateLength"] = saved.Template.Length,
            };
            if (wasCreated)
                await _events.EmitCreatedAsync(tenantId, actingUserId, role, string.Empty, emitData);
            else
                await _events.EmitUpdatedAsync(tenantId, actingUserId, role, string.Empty, emitData);
        }

        return (saved, wasCreated);
    }

    /// <summary>
    /// Delete a tenant role-system override. Emits <c>PROMPT.RESET.SUCCESS</c>
    /// on success (IMP-2).
    /// </summary>
    public async Task<bool> DeleteRoleSystemForTenantAsync(Guid tenantId, string role)
    {
        var deleted = await _repository.DeleteByTenantAsync(tenantId, "role-system", role, null);

        if (deleted && _events is not null)
            await _events.EmitResetAsync(tenantId, null, role, string.Empty);

        return deleted;
    }

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
    // Render + emit (IMP-2)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve, render, and emit <c>PROMPT.RENDERED.SUCCESS</c> in one call.
    /// The endpoint delegates here so the DCB audit event for a render is emitted
    /// from the service layer rather than the handler body (IMP-2 pattern).
    /// </summary>
    /// <exception cref="TammaError">
    /// No override and no system default for <c>(role, action)</c> — same as
    /// <see cref="ResolveRoleActionAsync"/>.
    /// </exception>
    public async Task<(ResolvedPrompt Resolved, RenderedPromptPair Rendered)> RenderRoleActionAsync(
        Guid? userId,
        Guid? tenantId,
        string role,
        string action,
        IReadOnlyDictionary<string, string> variables)
    {
        ResolvedPrompt resolved;
        if (tenantId is { } tid && tid != Guid.Empty)
            resolved = await ResolveRoleActionForTenantAsync(tid, role, action);
        else
            resolved = await ResolveRoleActionAsync(userId, role, action);

        var rendered = RenderFull(
            systemTemplate: resolved.SystemPrompt,
            userTemplate:   resolved.Template,
            variables:      variables);

        // IMP-2: emit the render audit event from the service layer.
        if (_events is not null)
        {
            await _events.EmitRenderedAsync(
                tenantId,
                userId,
                role,
                action,
                variableCount:    variables.Count,
                unresolvedCount:  rendered.Unresolved.Count);
        }

        return (resolved, rendered);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build the fail-loud <see cref="TammaError"/> for a <c>(role, action)</c>
    /// that resolved past every override layer with no system default. Carries
    /// the scope keys in <see cref="TammaError.Context"/> for diagnostics. This
    /// path should only be reachable for a taxonomy-INVALID action (a valid
    /// action always ships a system default seed — Story 27-18) or a genuinely
    /// missing seed, both of which are bugs worth failing loud on.
    /// </summary>
    private static TammaError NoPromptError(string role, string action, Guid? userId, Guid? tenantId)
        => new(
            "PROMPT.RESOLVE.NO_DEFAULT",
            $"No prompt available for (role='{role}', action='{action}'): no override and no system default. " +
            "Resolution is override → system default → error; there is no generic action-default fallback (Story 27-18 / SPEC §7).",
            new Dictionary<string, object?>
            {
                ["role"] = role,
                ["action"] = action,
                ["userId"] = userId,
                ["tenantId"] = tenantId,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);

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
