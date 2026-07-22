using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Documents.Policy;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-7 (Design Decision D3) — the PURE reviewer <c>(role, action)</c>
/// derivation the review producers dispatch through. Pulls the plan/task review
/// action from <see cref="RolePhaseMap.GetReviewActionForRole"/> for a
/// <c>document</c> subject, and from a LOCAL diff-review map (kept out of
/// <see cref="RolePhaseMap"/> per AC9's frozen surface, drift-pinned by test) for a
/// <c>diff</c> subject. An explicit <c>reviewerAction</c> override wins over
/// derivation; either way the pair is validated fail-loud through the agent
/// taxonomy so a bad reviewer selection is a LOUD <c>TammaError</c>, never a
/// silent mismatch.
///
/// <para>Doc-comment posture mirrors <c>TriagePoDecisionHelper</c>: no Elsa runtime
/// dependency, every branch named, reject-not-clamp.</para>
/// </summary>
public static class ReviewerSelectionHelper
{
    /// <summary>Error code for an unknown / ineligible reviewer <c>(role, action)</c> pair.</summary>
    public const string InvalidReviewerCode = "REVIEW.PRODUCER.INVALID_REVIEWER";

    /// <summary>Error code for a role that has no seat on the diff (code-review) panel.</summary>
    public const string RoleNotOnDiffPanelCode = "REVIEW.PRODUCER.ROLE_NOT_ON_DIFF_PANEL";

    /// <summary>Subject-kind wire for a document reference (39-4 <c>ReviewSubject.Kind</c>).</summary>
    public const string DocumentSubjectKind = "document";

    /// <summary>Subject-kind wire for a diff reference (39-4 <c>ReviewSubject.Kind</c>).</summary>
    public const string DiffSubjectKind = "diff";

    /// <summary>A resolved reviewer dispatch pair.</summary>
    public sealed record ReviewerSpec(AgentRole Role, AgentAction Action);

    /// <summary>
    /// The diff (code-review) review action per role (Design Decision D3). Lives
    /// HERE, not in <see cref="RolePhaseMap"/> (AC9 freezes that file); the drift
    /// test pins this map against the taxonomy. <c>devops</c> / <c>product_owner</c>
    /// / <c>tech_writer</c> have no diff-review seat.
    /// </summary>
    private static AgentAction DiffReviewAction(AgentRole role) => role switch
    {
        AgentRole.SeniorDeveloper => AgentAction.CodeReview,
        AgentRole.Developer => AgentAction.CodeReview,
        AgentRole.Architect => AgentAction.CodeReviewArchitecture,
        AgentRole.Security => AgentAction.CodeReviewSecurity,
        AgentRole.Tester => AgentAction.CodeReviewCoverage,
        _ => throw new TammaError(
            RoleNotOnDiffPanelCode,
            $"Role '{role.ToWire()}' has no seat on the diff (code-review) panel — only " +
            "senior_developer, developer, architect, security, tester review diffs.",
            new Dictionary<string, object?> { ["role"] = role.ToWire() },
            retryable: false,
            severity: TammaErrorSeverity.High),
    };

    /// <summary>The 7-role document-review roster (the domain of GetReviewActionForRole).</summary>
    private static readonly AgentRole[] s_documentRoster =
    [
        AgentRole.Architect,
        AgentRole.SeniorDeveloper,
        AgentRole.Security,
        AgentRole.Developer,
        AgentRole.Tester,
        AgentRole.Devops,
        AgentRole.ProductOwner,
    ];

    /// <summary>The 5-role diff-review roster.</summary>
    private static readonly AgentRole[] s_diffRoster =
    [
        AgentRole.SeniorDeveloper,
        AgentRole.Developer,
        AgentRole.Architect,
        AgentRole.Security,
        AgentRole.Tester,
    ];

    /// <summary>
    /// Resolve the reviewer <c>(role, action)</c> for a review dispatch (AC4 runtime
    /// half). <paramref name="actionOverride"/> (when non-empty) wins over
    /// derivation; otherwise the action comes from the document-review map (kind
    /// <c>document</c>) or the diff-review map (kind <c>diff</c>). The resolved pair
    /// is asserted taxonomy-eligible via <see cref="RolePhaseMap.IsRoleEligibleForPhase"/>.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <see cref="InvalidReviewerCode"/> for an unknown role/action, an
    /// ineligible pair, or an unknown subject kind; code
    /// <see cref="RoleNotOnDiffPanelCode"/> for a role with no diff seat.
    /// </exception>
    public static ReviewerSpec Resolve(string role, string? actionOverride, string subjectKind, string? documentTypeKey)
    {
        AgentRole parsedRole;
        try
        {
            parsedRole = AgentRoleExtensions.Parse(role);
        }
        catch (ArgumentException ex)
        {
            throw Invalid($"Reviewer role '{role}' is not a known agent role: {ex.Message}", role, actionOverride);
        }

        AgentAction action;
        if (!string.IsNullOrWhiteSpace(actionOverride))
        {
            try
            {
                action = AgentActionExtensions.Parse(actionOverride!);
            }
            catch (ArgumentException ex)
            {
                throw Invalid($"Reviewer action override '{actionOverride}' is not a known agent action: {ex.Message}", role, actionOverride);
            }
        }
        else
        {
            action = subjectKind switch
            {
                DocumentSubjectKind => ResolveDocumentAction(parsedRole),
                DiffSubjectKind => DiffReviewAction(parsedRole),
                _ => throw Invalid(
                    $"Unknown review subject kind '{subjectKind}' — expected 'document' or 'diff'.",
                    role, actionOverride),
            };
        }

        if (!RolePhaseMap.IsRoleEligibleForPhase(action.ToWire(), parsedRole.ToWire()))
            throw Invalid(
                $"Reviewer role '{parsedRole.ToWire()}' is not eligible for action '{action.ToWire()}' " +
                "(not in that role's taxonomy action set).",
                role, action.ToWire());

        return new ReviewerSpec(parsedRole, action);
    }

    private static AgentAction ResolveDocumentAction(AgentRole role)
    {
        try
        {
            return RolePhaseMap.GetReviewActionForRole(role);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid(
                $"Role '{role.ToWire()}' is not on a document review panel.",
                role.ToWire(), null);
        }
    }

    /// <summary>
    /// Every <c>(role, action)</c> pair the producers can dispatch (Design Decision
    /// D9 pin surface): the 7 document-review pairs + the 5 diff-review pairs = 12.
    /// The <c>ContractBindingTests</c> classification guard iterates this so a
    /// reviewer cell reachable by policy but bound nowhere fails the build.
    /// </summary>
    public static IReadOnlyList<(string Role, string Action)> AllDispatchablePairs { get; } = BuildAllPairs();

    private static IReadOnlyList<(string Role, string Action)> BuildAllPairs()
    {
        var pairs = new List<(string, string)>();
        foreach (var role in s_documentRoster)
            pairs.Add((role.ToWire(), RolePhaseMap.GetReviewActionForRole(role).ToWire()));
        foreach (var role in s_diffRoster)
            pairs.Add((role.ToWire(), DiffReviewAction(role).ToWire()));
        return pairs;
    }

    /// <summary>
    /// The 7-role document panel roster (the superset the panel graph iterates).
    /// </summary>
    public static IReadOnlyList<AgentRole> DocumentPanelRoster => s_documentRoster;

    /// <summary>
    /// The effective panel roster from the acceptance rules (Design Decision D11):
    /// a panel selection's <c>PanelRoles</c>, or a single-reviewer selection's one
    /// role. Each role is validated fail-loud. An empty
    /// <paramref name="acceptanceRulesJson"/> falls back to
    /// <see cref="AcceptanceDefaults.Rules"/> (single architect).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <see cref="InvalidReviewerCode"/> for a roster role that is not a known
    /// agent role.
    /// </exception>
    public static IReadOnlyList<string> ResolvePanelRoster(string? acceptanceRulesJson)
    {
        var rules = string.IsNullOrWhiteSpace(acceptanceRulesJson)
            ? AcceptanceDefaults.Rules
            : AcceptanceRulesJson.Deserialize(acceptanceRulesJson!);

        var sel = rules.ReviewerSelection;
        var roster = sel.Mode == ReviewerMode.Panel
            ? (sel.PanelRoles ?? Array.Empty<string>()).ToList()
            : new List<string> { sel.ReviewerRole ?? string.Empty };

        foreach (var r in roster)
        {
            try
            {
                AgentRoleExtensions.Parse(r);
            }
            catch (ArgumentException ex)
            {
                throw Invalid($"Panel roster role '{r}' is not a known agent role: {ex.Message}", r, null);
            }
        }

        return roster;
    }

    private static TammaError Invalid(string message, string? role, string? action) =>
        new(
            InvalidReviewerCode,
            message,
            new Dictionary<string, object?> { ["role"] = role, ["action"] = action },
            retryable: false,
            severity: TammaErrorSeverity.High);
}
