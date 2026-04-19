using System.Collections.Frozen;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Static mapping between agent roles and workflow phases (actions).
///
/// Ported from the deleted <c>packages/api/src/services/default-prompts.ts</c>
/// (Story 12-5 — Prompt Engineering Framework). Roles and phases are kept
/// stable across the TS → C# migration (Epic 19).
///
/// There are 8 roles × 10 phases. Each role has a primary phase, and each
/// phase has one or more eligible roles.
/// </summary>
public static class RolePhaseMap
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    /// <summary>
    /// The 8 agent roles supported by the Tamma platform.
    /// </summary>
    public static readonly FrozenSet<string> ValidRoles = new HashSet<string>
    {
        "developer",
        "tester",
        "security",
        "devops",
        "architect",
        "product_owner",
        "senior_developer",
        "tech_writer",
    }.ToFrozenSet();

    /// <summary>
    /// The 10 workflow phases (a.k.a. actions) the engine dispatches to.
    /// </summary>
    public static readonly FrozenSet<string> ValidActions = new HashSet<string>
    {
        "context-scan",
        "plan",
        "plan-review",
        "implement",
        "write-tests",
        "refactor",
        "code-review",
        "triage",
        "summarize",
        "debug",
    }.ToFrozenSet();

    /// <summary>
    /// Keys rejected to prevent prototype-pollution-style lookups — port of
    /// <c>FORBIDDEN_KEYS</c> from <c>role-based-agent-resolver.ts</c>.
    /// </summary>
    public static readonly FrozenSet<string> ForbiddenKeys = new HashSet<string>
    {
        "__proto__",
        "constructor",
        "prototype",
    }.ToFrozenSet();

    /// <summary>
    /// Legacy TS role keys (Story 9-1 / 9-8) mapped onto current C# roles —
    /// see audit finding 001. Old <c>agent_configs.config</c> JSONB rows
    /// written by the deleted TS engine still use these names; instead of
    /// 400-ing, we accept them transparently and translate to the canonical
    /// C# role at validation / resolution time. Unmapped entries
    /// (<c>analyst</c>, <c>scrum_master</c>, <c>researcher</c>) fall back to
    /// <c>product_owner</c>, the closest equivalent in the new grid.
    /// </summary>
    public static readonly FrozenDictionary<string, string> LegacyRoleAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["implementer"] = "developer",
            ["reviewer"] = "senior_developer",
            ["tester"] = "tester",
            ["architect"] = "architect",
            ["documenter"] = "tech_writer",
            ["analyst"] = "product_owner",
            ["scrum_master"] = "product_owner",
            ["planner"] = "senior_developer",
            ["researcher"] = "product_owner",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy TS workflow phase keys (UPPER_SNAKE) mapped onto the C#
    /// hyphen-lowercase action vocabulary. Keeps Elsa workflows that still
    /// emit TS-era phase identifiers compatible with the new resolver.
    /// </summary>
    public static readonly FrozenDictionary<string, string> LegacyPhaseAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ISSUE_SELECTION"] = "triage",
            ["CONTEXT_ANALYSIS"] = "context-scan",
            ["PLAN_GENERATION"] = "plan",
            ["CODE_GENERATION"] = "implement",
            ["PR_CREATION"] = "implement",
            ["CODE_REVIEW"] = "code-review",
            ["TEST_EXECUTION"] = "write-tests",
            ["STATUS_MONITORING"] = "triage",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a possibly-legacy role to the canonical C# role. Returns
    /// <paramref name="role"/> unchanged if it's already canonical or not in
    /// the alias table.
    /// </summary>
    public static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return role;
        if (ValidRoles.Contains(role)) return role;
        return LegacyRoleAliases.TryGetValue(role, out var canonical) ? canonical : role;
    }

    /// <summary>
    /// Resolve a possibly-legacy phase identifier to the canonical action.
    /// Returns <paramref name="phase"/> unchanged if it's already canonical
    /// or not in the alias table.
    /// </summary>
    public static string NormalizePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return phase;
        if (ValidActions.Contains(phase)) return phase;
        return LegacyPhaseAliases.TryGetValue(phase, out var canonical) ? canonical : phase;
    }

    // -----------------------------------------------------------------------
    // Role → primary phase
    // -----------------------------------------------------------------------

    private static readonly FrozenDictionary<string, string> s_primaryPhase =
        new Dictionary<string, string>
        {
            ["developer"] = "implement",
            ["tester"] = "write-tests",
            ["security"] = "code-review",
            ["devops"] = "implement",
            ["architect"] = "plan",
            ["product_owner"] = "triage",
            ["senior_developer"] = "plan-review",
            ["tech_writer"] = "summarize",
        }.ToFrozenDictionary();

    // -----------------------------------------------------------------------
    // Phase → eligible roles
    // -----------------------------------------------------------------------

    private static readonly FrozenDictionary<string, FrozenSet<string>> s_eligibleRoles =
        new Dictionary<string, FrozenSet<string>>
        {
            // Research / analysis — any role can scan context
            ["context-scan"] = FreezeSet(
                "developer", "tester", "security", "devops",
                "architect", "product_owner", "senior_developer", "tech_writer"),
            // Planning — architect primary, senior_developer & product_owner contribute
            ["plan"] = FreezeSet("architect", "senior_developer", "product_owner"),
            // Plan review — senior_developer primary, architect + security sanity-check
            ["plan-review"] = FreezeSet("senior_developer", "architect", "security"),
            // Implementation — developer primary, devops for infra changes
            ["implement"] = FreezeSet("developer", "devops"),
            // Test authoring — tester primary, developer writes companion tests
            ["write-tests"] = FreezeSet("tester", "developer"),
            // Refactor — developer + senior_developer
            ["refactor"] = FreezeSet("developer", "senior_developer"),
            // Code review — security, senior_developer, developer; tester can eyeball
            ["code-review"] = FreezeSet("security", "senior_developer", "developer", "tester"),
            // Triage — product_owner primary, senior_developer + architect for tech triage
            ["triage"] = FreezeSet("product_owner", "senior_developer", "architect"),
            // Summarize / write docs — tech_writer primary, senior_developer for tech write-ups
            ["summarize"] = FreezeSet("tech_writer", "senior_developer", "product_owner"),
            // Debug — developer, senior_developer, devops
            ["debug"] = FreezeSet("developer", "senior_developer", "devops"),
        }.ToFrozenDictionary();

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get the primary phase (action) for a given role.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="role"/> is unknown or a forbidden key.
    /// </exception>
    public static string GetPrimaryPhaseForRole(string role)
    {
        AssertValidRole(role);
        return s_primaryPhase[role];
    }

    /// <summary>
    /// Get the set of roles eligible for a given phase (action).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="phase"/> is unknown.
    /// </exception>
    public static IReadOnlySet<string> GetEligibleRolesForPhase(string phase)
    {
        AssertValidPhase(phase);
        return s_eligibleRoles[phase];
    }

    /// <summary>
    /// Check whether a role is eligible for a given phase. Returns
    /// <c>false</c> for unknown roles or phases (non-throwing predicate).
    /// </summary>
    public static bool IsRoleEligibleForPhase(string phase, string role)
    {
        if (string.IsNullOrEmpty(phase) || string.IsNullOrEmpty(role))
        {
            return false;
        }
        if (!s_eligibleRoles.TryGetValue(phase, out var eligible))
        {
            return false;
        }
        return eligible.Contains(role);
    }

    /// <summary>
    /// Throw if <paramref name="role"/> is empty, forbidden, or not in
    /// <see cref="ValidRoles"/>.
    /// </summary>
    public static void AssertValidRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Role must not be empty.", nameof(role));
        }
        if (ForbiddenKeys.Contains(role))
        {
            throw new ArgumentException($"Forbidden role name: '{role}'.", nameof(role));
        }
        if (!ValidRoles.Contains(role))
        {
            throw new ArgumentException(
                $"Unknown role: '{role}'. Valid roles: {string.Join(", ", ValidRoles)}.",
                nameof(role));
        }
    }

    /// <summary>
    /// Throw if <paramref name="phase"/> is empty or not in
    /// <see cref="ValidActions"/>.
    /// </summary>
    public static void AssertValidPhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            throw new ArgumentException("Phase must not be empty.", nameof(phase));
        }
        if (!ValidActions.Contains(phase))
        {
            throw new ArgumentException(
                $"Unknown phase: '{phase}'. Valid phases: {string.Join(", ", ValidActions)}.",
                nameof(phase));
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static FrozenSet<string> FreezeSet(params string[] items)
        => new HashSet<string>(items).ToFrozenSet();
}
