using System.Collections.Frozen;

// NOTE: This type lives in the Tamma.Core assembly but intentionally keeps the
// `Tamma.Api.Services.Agents` namespace. It was moved here (Story 27-19) so the
// Elsa workflows (Tamma.ElsaServer) can reference the taxonomy without a
// dependency cycle through Tamma.Api. The namespace is preserved to avoid
// churning every caller's `using`. A future cleanup story may realign the
// namespace to Tamma.Core.Agents and relocate the tests to Tamma.Core.Tests.
namespace Tamma.Api.Services.Agents;

/// <summary>
/// The authoritative role ↔ action taxonomy, rebuilt on the
/// <see cref="AgentRole"/> / <see cref="AgentAction"/> enums (SPEC §4 —
/// <c>docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md</c>).
///
/// This is a CLEAN CUT to the new typed action vocabulary (Story 27-15). The
/// old flat 8×10 string vocabulary is gone; the union of the per-role action
/// sets is the 72-token <see cref="AgentAction"/> enum. Which <c>(role,
/// action)</c> pairs are valid is the per-role eligibility matrix below — shared
/// tokens (<c>context-scan</c>, <c>code-review</c>, <c>plan-review</c>,
/// <c>write-tests</c>) appear in multiple role sets intentionally; the role half
/// of the key disambiguates them.
///
/// <para>
/// The public surface keeps the string-keyed signatures so existing callers
/// (<c>AgentResolverService</c>, <c>ProviderChainResolver</c>,
/// <c>AgentEndpoints</c>, <c>DefaultAgentConfig</c>) compile unchanged; strings
/// are parsed to enums internally so a typo'd token is a <see cref="AgentActionExtensions.Parse"/>
/// throw, never a silent mismatch.
/// </para>
/// </summary>
public static class RolePhaseMap
{
    // -----------------------------------------------------------------------
    // Typed eligibility matrix — SPEC §4 per-role action sets
    // -----------------------------------------------------------------------

    /// <summary>
    /// The per-role action sets from SPEC §4. Built typed from enum members so
    /// every entry is compile-checked: a non-existent member is a compile
    /// error, never a silent string mismatch.
    /// </summary>
    private static readonly FrozenDictionary<AgentRole, FrozenSet<AgentAction>> s_eligibleActions =
        new Dictionary<AgentRole, FrozenSet<AgentAction>>
        {
            // product_owner — intake, requirements, prioritisation, acceptance
            [AgentRole.ProductOwner] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.TriageIntake,
                AgentAction.ClarifyRequirements,
                AgentAction.PlanScope,
                AgentAction.DefineAcceptanceCriteria,
                AgentAction.PrioritizeBacklog,
                AgentAction.PlanRoadmap,
                AgentAction.SummarizeStakeholder,
                AgentAction.ReviewAcceptance,
                AgentAction.ReviewScope),

            // architect — system design, technical strategy
            [AgentRole.Architect] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.TriageTechnical,
                AgentAction.PlanSystemDesign,
                AgentAction.DesignApiContract,
                AgentAction.DesignDataModel,
                AgentAction.DesignIntegration,
                AgentAction.PlanMigrationStrategy,
                AgentAction.WriteAdr,
                AgentAction.PlanReview,
                AgentAction.CodeReviewArchitecture,
                AgentAction.AssessTechnicalRisk),

            // senior_developer — tech lead: decomposition, review, mentorship
            [AgentRole.SeniorDeveloper] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.CreateTasks,
                AgentAction.PlanImplementation,
                AgentAction.PlanReview,
                AgentAction.CodeReview,
                AgentAction.PlanRefactor,
                AgentAction.DebugRootcause,
                AgentAction.TriageTechnical,
                AgentAction.SummarizeTechnical,
                AgentAction.ResolveBlocker,
                AgentAction.MentorFeedback),

            // developer — implementation
            [AgentRole.Developer] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.PlanImplementation,
                AgentAction.PlanFix,
                AgentAction.PlanDebugging,
                AgentAction.ImplementFeature,
                AgentAction.ImplementFix,
                AgentAction.WriteTests,
                AgentAction.Refactor,
                AgentAction.Debug,
                AgentAction.CodeReview,
                AgentAction.AddressReviewComments,
                AgentAction.SelfReview,
                AgentAction.ReviewFeasibility,
                AgentAction.TriageDefect),

            // tester — QA, test engineering
            [AgentRole.Tester] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.PlanTestStrategy,
                AgentAction.WriteTestCases,
                AgentAction.WriteTests,
                AgentAction.WriteRegressionTest,
                AgentAction.ExploratoryTest,
                AgentAction.VerifyAcceptance,
                AgentAction.CodeReviewCoverage,
                AgentAction.TriageDefect,
                AgentAction.ReviewTestability),

            // security — security review, threat modelling
            [AgentRole.Security] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.ThreatModel,
                AgentAction.PlanReviewSecurity,
                AgentAction.CodeReviewSecurity,
                AgentAction.AssessVulnerability,
                AgentAction.AuditDependencies,
                AgentAction.AuditSecrets,
                AgentAction.ReviewCompliance,
                AgentAction.AnalyzeSecurityIncident),

            // devops — infra, CI/CD, deployment, ops
            [AgentRole.Devops] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.PlanDeployment,
                AgentAction.ImplementInfrastructure,
                AgentAction.ConfigureCicd,
                AgentAction.Deploy,
                AgentAction.Rollback,
                AgentAction.MonitorHealth,
                AgentAction.DiagnoseIncident,
                AgentAction.PlanIncidentResponse,
                AgentAction.WritePostmortem,
                AgentAction.AssessCapacity,
                AgentAction.ReviewOperability),

            // tech_writer — documentation
            [AgentRole.TechWriter] = FreezeSet(
                AgentAction.ContextScan,
                AgentAction.SummarizeChanges,
                AgentAction.WriteUserDocs,
                AgentAction.WriteApiDocs,
                AgentAction.WriteReleaseNotes,
                AgentAction.WriteRunbook,
                AgentAction.UpdateChangelog,
                AgentAction.ReviewDocs),
        }.ToFrozenDictionary();

    /// <summary>
    /// Reverse map: action → roles whose set contains it. Built once from
    /// <see cref="s_eligibleActions"/> so <see cref="GetEligibleRolesForPhase"/>
    /// is a single dictionary lookup.
    /// </summary>
    private static readonly FrozenDictionary<AgentAction, FrozenSet<string>> s_rolesForAction =
        BuildRolesForAction();

    /// <summary>
    /// Role → primary action. Old-intent → new-token mapping (Story 27-15):
    /// this API has no runtime callers (test-only), so it just needs to stay
    /// coherent. Every value is in that role's §4 set.
    /// </summary>
    private static readonly FrozenDictionary<AgentRole, AgentAction> s_primaryAction =
        new Dictionary<AgentRole, AgentAction>
        {
            [AgentRole.Developer] = AgentAction.ImplementFeature,
            [AgentRole.Tester] = AgentAction.WriteTests,
            [AgentRole.Security] = AgentAction.CodeReviewSecurity,
            [AgentRole.Devops] = AgentAction.ImplementInfrastructure,
            [AgentRole.Architect] = AgentAction.PlanSystemDesign,
            [AgentRole.ProductOwner] = AgentAction.TriageIntake,
            [AgentRole.SeniorDeveloper] = AgentAction.PlanReview,
            [AgentRole.TechWriter] = AgentAction.SummarizeChanges,
        }.ToFrozenDictionary();

    // -----------------------------------------------------------------------
    // Constants — ValidRoles / ValidActions derived from the enums
    // -----------------------------------------------------------------------

    /// <summary>
    /// The 8 agent roles, as wire strings, derived from <see cref="AgentRole"/>.
    /// Kept as <see cref="FrozenSet{T}"/> of string so string-keyed callers
    /// compile unchanged.
    /// </summary>
    public static readonly FrozenSet<string> ValidRoles =
        Enum.GetValues<AgentRole>().Select(r => r.ToWire()).ToFrozenSet();

    /// <summary>
    /// The 72 workflow actions, as wire strings, derived from
    /// <see cref="AgentAction"/> (the union of the per-role §4 sets).
    /// </summary>
    public static readonly FrozenSet<string> ValidActions =
        Enum.GetValues<AgentAction>().Select(a => a.ToWire()).ToFrozenSet();

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
    /// Legacy TS workflow phase keys (UPPER_SNAKE) repointed onto the new
    /// typed action vocabulary (Story 27-15). Surviving tokens map to
    /// themselves (<c>context-scan</c>, <c>code-review</c>); dead tokens map to
    /// the best-fit new specific action. Keeps Elsa workflows that still emit
    /// TS-era phase identifiers green until the dispatch sites are specialised
    /// (Story 27-19).
    /// </summary>
    public static readonly FrozenDictionary<string, string> LegacyPhaseAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONTEXT_ANALYSIS"] = "context-scan",            // survives
            ["CODE_REVIEW"] = "code-review",                  // survives
            ["TEST_EXECUTION"] = "write-tests",               // survives
            ["CODE_GENERATION"] = "implement-feature",
            ["PR_CREATION"] = "implement-feature",
            ["PLAN_GENERATION"] = "plan-system-design",
            ["ISSUE_SELECTION"] = "triage-intake",
            ["STATUS_MONITORING"] = "triage-intake",
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
    // Public API — string-keyed for backward source compatibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get the primary action for a given role. No runtime callers (test-only);
    /// kept coherent with the §4 sets.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="role"/> is unknown or a forbidden key.
    /// </exception>
    public static string GetPrimaryPhaseForRole(string role)
    {
        AssertValidRole(role);
        var parsed = AgentRoleExtensions.Parse(role);
        return s_primaryAction[parsed].ToWire();
    }

    /// <summary>
    /// Get the set of roles eligible for a given action.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="phase"/> is unknown (matches old
    /// AssertValidPhase behaviour).
    /// </exception>
    public static IReadOnlySet<string> GetEligibleRolesForPhase(string phase)
    {
        AssertValidPhase(phase);
        var action = AgentActionExtensions.Parse(phase);
        // Every valid action appears in s_rolesForAction (union completeness is
        // enforced by the round-trip / coverage tests), but guard defensively.
        return s_rolesForAction.TryGetValue(action, out var roles)
            ? roles
            : FrozenSet<string>.Empty;
    }

    /// <summary>
    /// Check whether a role is eligible for a given action. Non-throwing:
    /// returns <c>false</c> for unknown/unparseable role or action (a dead
    /// token like <c>"implement"</c> yields <c>false</c>, not a throw —
    /// <c>AgentResolverService</c> relies on this predicate).
    /// </summary>
    public static bool IsRoleEligibleForPhase(string phase, string role)
    {
        if (string.IsNullOrEmpty(phase) || string.IsNullOrEmpty(role))
        {
            return false;
        }
        if (!TryParseRole(role, out var parsedRole) ||
            !TryParseAction(phase, out var parsedAction))
        {
            return false;
        }
        return s_eligibleActions.TryGetValue(parsedRole, out var actions) &&
               actions.Contains(parsedAction);
    }

    /// <summary>
    /// Select the cross-role <b>plan/task review</b> action for <paramref name="role"/>
    /// on a review panel (Story 27-19). Each reviewing role critiques the plan
    /// through its own lens, so the action is role-specific rather than a single
    /// generic <c>plan-review</c>:
    /// <list type="bullet">
    /// <item>architect / senior_developer → <c>plan-review</c></item>
    /// <item>security → <c>plan-review-security</c></item>
    /// <item>developer → <c>review-feasibility</c></item>
    /// <item>tester → <c>review-testability</c></item>
    /// <item>devops → <c>review-operability</c></item>
    /// <item>product_owner → <c>review-scope</c></item>
    /// </list>
    /// Every returned <c>(role, action)</c> satisfies
    /// <see cref="IsRoleEligibleForPhase"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for a role that is not part of a review panel
    /// (<see cref="AgentRole.TechWriter"/>).
    /// </exception>
    public static AgentAction GetReviewActionForRole(AgentRole role) => role switch
    {
        AgentRole.Architect => AgentAction.PlanReview,
        AgentRole.SeniorDeveloper => AgentAction.PlanReview,
        AgentRole.Security => AgentAction.PlanReviewSecurity,
        AgentRole.Developer => AgentAction.ReviewFeasibility,
        AgentRole.Tester => AgentAction.ReviewTestability,
        AgentRole.Devops => AgentAction.ReviewOperability,
        AgentRole.ProductOwner => AgentAction.ReviewScope,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role), role, $"Role '{role.ToWire()}' is not on a review panel."),
    };

    /// <summary>
    /// Select the <b>triage panel</b> action for <paramref name="role"/>
    /// (Story 27-19). Each panellist triages the item through its own lens:
    /// <list type="bullet">
    /// <item>security → <c>assess-vulnerability</c></item>
    /// <item>developer → <c>triage-defect</c></item>
    /// <item>devops → <c>diagnose-incident</c></item>
    /// <item>tester → <c>triage-defect</c></item>
    /// </list>
    /// Every returned <c>(role, action)</c> satisfies
    /// <see cref="IsRoleEligibleForPhase"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for a role that is not part of the triage panel.
    /// </exception>
    public static AgentAction GetTriageActionForRole(AgentRole role) => role switch
    {
        AgentRole.Security => AgentAction.AssessVulnerability,
        AgentRole.Developer => AgentAction.TriageDefect,
        AgentRole.Devops => AgentAction.DiagnoseIncident,
        AgentRole.Tester => AgentAction.TriageDefect,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role), role, $"Role '{role.ToWire()}' is not on the triage panel."),
    };

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

    private static FrozenSet<AgentAction> FreezeSet(params AgentAction[] items)
        => new HashSet<AgentAction>(items).ToFrozenSet();

    private static FrozenDictionary<AgentAction, FrozenSet<string>> BuildRolesForAction()
    {
        var accumulator = new Dictionary<AgentAction, HashSet<string>>();
        foreach (var (role, actions) in s_eligibleActions)
        {
            var roleWire = role.ToWire();
            foreach (var action in actions)
            {
                if (!accumulator.TryGetValue(action, out var roles))
                {
                    roles = new HashSet<string>(StringComparer.Ordinal);
                    accumulator[action] = roles;
                }
                roles.Add(roleWire);
            }
        }
        return accumulator.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToFrozenSet());
    }

    /// <summary>
    /// Non-throwing role parse used by <see cref="IsRoleEligibleForPhase"/>.
    /// <see cref="AgentRoleExtensions.Parse"/> throws on unknown; this swallows that to
    /// keep the predicate total.
    /// </summary>
    private static bool TryParseRole(string role, out AgentRole parsed)
    {
        var normalized = NormalizeRole(role);
        return EnumWire<AgentRole>.TryParse(normalized, out parsed);
    }

    /// <summary>
    /// Non-throwing action parse used by <see cref="IsRoleEligibleForPhase"/>.
    /// A dead token (e.g. <c>"implement"</c>) that maps to no canonical action
    /// yields <c>false</c> instead of throwing.
    /// </summary>
    private static bool TryParseAction(string phase, out AgentAction parsed)
    {
        var normalized = NormalizePhase(phase);
        return EnumWire<AgentAction>.TryParse(normalized, out parsed);
    }
}
