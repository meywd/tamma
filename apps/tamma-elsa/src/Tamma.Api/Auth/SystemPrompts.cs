using Tamma.Api.Services.Agents;

namespace Tamma.Api.Auth;

/// <summary>
/// Immutable description of a system-shipped prompt template.
/// Corresponds to <c>PromptTemplate</c> in <c>default-prompts.ts</c>.
/// </summary>
/// <param name="Role">Agent role (developer, tester, security, etc.).</param>
/// <param name="Action">The action this prompt is for (context-scan, plan-implementation, etc.).</param>
/// <param name="Template">The user-facing prompt template with <c>{{variable}}</c> placeholders.</param>
/// <param name="SystemPrompt">System prompt (role identity preamble).</param>
/// <param name="Variables">Variable names expected by the template.</param>
/// <param name="EnableTools">Whether tool use is enabled for this prompt.</param>
/// <param name="MaxTokens">Maximum tokens for the LLM response.</param>
/// <param name="Version">Monotonically increasing version number.</param>
public sealed record PromptTemplate(
    string? Role,
    string Action,
    string Template,
    string SystemPrompt,
    IReadOnlyList<string> Variables,
    bool EnableTools,
    int MaxTokens,
    int Version = 1);

/// <summary>
/// System-shipped prompt registry. Immutable at runtime.
///
/// <para>
/// <b>Story 27-18 — taxonomy reshape.</b> The flat 8×10 cartesian product
/// (80 cells) and the generic <c>action-default</c> safety-net tier are GONE.
/// The registry is the jagged per-role <c>(role, action)</c> taxonomy of
/// <see cref="RolePhaseMap"/> (SPEC §4 — 8 roles × their specific action sets).
/// Prompts key off the IDENTICAL <c>(role, action)</c> taxonomy that
/// conventions use; there is no generic fallback action anywhere.
/// </para>
///
/// <para>
/// <b>File-backed registry.</b> This type is a facade: the actual prompt
/// content lives in the embedded repo files
/// <c>Prompts/{role}/{action}.md</c> (one per taxonomy cell) and
/// <c>Prompts/{role}/_system.md</c> (role identity preambles), loaded once at
/// static init by <see cref="PromptFileLoader"/>. The bodies were generated
/// verbatim from the previous in-code body builders (Story 27-18 transitional
/// seeds, SPEC §3.5) and remain the authoritative system defaults until Story
/// 27-16 regenerates per-cell authoritative bodies — which is now a matter of
/// editing markdown files, not C#.
/// </para>
///
/// <para>
/// Exposes two layers used by <c>PromptStoreService</c>:
/// <list type="bullet">
///   <item><see cref="RoleSystemPrompts"/> — 8 role identity preambles keyed by role wire string.</item>
///   <item><see cref="RoleActionTemplates"/> — the jagged per-role <c>(role, action)</c> templates
///         (one non-empty body per cell in each role's <see cref="RolePhaseMap"/> action set).</item>
/// </list>
/// There is intentionally no third "generic action-default" tier — resolution is
/// <c>override → system default → TammaError</c> (see <c>PromptStoreService</c>).
/// </para>
///
/// <para>
/// <b>Fail-loud drift invariants</b> (enforced by <see cref="PromptFileLoader"/>
/// at static init): a taxonomy cell with no prompt file throws
/// <c>PROMPT.SEED.NO_BODY_FAMILY</c>; a prompt file whose <c>(role, action)</c>
/// is not in the taxonomy throws <c>PROMPT.SEED.UNKNOWN_CELL</c>; malformed
/// front matter throws <c>PROMPT.SEED.MALFORMED_FILE</c> naming the file.
/// </para>
/// </summary>
public static class SystemPrompts
{
    // -----------------------------------------------------------------------
    // Role catalogue (derived from the AgentRole taxonomy)
    // -----------------------------------------------------------------------

    /// <summary>The 8 agent roles, as wire strings (from <see cref="AgentRole"/>).</summary>
    public static readonly IReadOnlyList<string> Roles =
        Enum.GetValues<AgentRole>().Select(r => r.ToWire()).ToArray();

    // -----------------------------------------------------------------------
    // Layer 1 — System prompts (role identity preambles, from Prompts/{role}/_system.md)
    // -----------------------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, string> RoleSystemPrompts;

    // -----------------------------------------------------------------------
    // Layer 2 — Role + action templates (jagged per-role taxonomy, from Prompts/{role}/{action}.md)
    // -----------------------------------------------------------------------

    public static readonly IReadOnlyList<PromptTemplate> RoleActionTemplates;

    private static readonly IReadOnlyDictionary<string, PromptTemplate> RoleActionIndex;

    static SystemPrompts()
    {
        (RoleSystemPrompts, RoleActionTemplates) = PromptFileLoader.Load();
        RoleActionIndex = RoleActionTemplates.ToDictionary(t => Key(t.Role!, t.Action));
    }

    // -----------------------------------------------------------------------
    // Lookups
    // -----------------------------------------------------------------------

    /// <summary>Resolve the system-default role+action template, or null if unknown.</summary>
    public static PromptTemplate? GetRoleAction(string role, string action)
        => RoleActionIndex.TryGetValue(Key(role, action), out var t) ? t : null;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string Key(string role, string action) => $"{role}:{action}";
}
