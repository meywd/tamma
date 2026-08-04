using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The static, shipped-in-code acceptance defaults (Story 39-5 AC5). A deployment
/// with ZERO configuration behaves safely: autonomy 70 (the supervised baseline),
/// conservative round bounds, no always-escalate classes, and sensible reviewer
/// selection per type.
///
/// <para>Shared knobs are the same for every type; REVIEWER SELECTION is per-type
/// (Design Decision D2 / step 2) so 39-14's PlanReview migration is
/// behavior-preserving with zero config: the <c>plan</c> and <c>review</c> types
/// default to a 7-role PANEL with a MAJORITY decision rule, while every other
/// type defaults to a single <c>architect</c> reviewer with a UNANIMOUS rule.</para>
///
/// <para>The ACCEPTOR REQUIREMENT is no longer a per-type shipped constant here
/// (Story 43-16, form α). It is DERIVED — see <see cref="AcceptanceFloors"/>: the
/// shipped acceptor floor is <see cref="AcceptorRequirement.Human"/> while the
/// resolved base-row dial is below the document type's catalog level
/// (<c>ActionCatalog.Get(document-type:&lt;type&gt;).DefaultMinAutonomy</c>), and
/// <see cref="AcceptorRequirement.Any"/> at or above it. <see cref="For"/> therefore
/// returns <see cref="AcceptorRequirement.Any"/> for EVERY type; the stored
/// per-type <see cref="AcceptorRequirement"/> survives only as the named-type
/// override a per-type <c>PUT</c> may still set (an explicit <c>any</c> still lowers;
/// a base-row <c>PUT</c> still cannot erase the derived floor — CD-1).</para>
///
/// <para>The static constructor calls <see cref="AcceptanceRules.Validate"/> on
/// every per-type default — an invalid default REFUSES to load (the fail-loud
/// posture of <c>PromptFileLoader</c>). Every value here is pinned by
/// <c>AcceptanceDefaultsDriftTests</c>.</para>
/// </summary>
public static class AcceptanceDefaults
{
    /// <summary>Shared autonomy default — the supervised baseline.</summary>
    public const int DefaultAutonomyLevel = 70;

    /// <summary>Shared revision-rounds default.</summary>
    public const int DefaultMaxRevisionRounds = 2;

    /// <summary>Shared validation-repair-attempts default.</summary>
    public const int DefaultMaxValidationRepairAttempts = 2;

    /// <summary>Shared ambiguity-escalation-threshold default.</summary>
    public const double DefaultAmbiguityEscalationThreshold = 0.7;

    private const string DefaultDecisionGuidance =
        "Accept when the review approves with no blocking issues and the document satisfies its acceptance " +
        "criteria. Request revision when the review raises addressable, non-blocking concerns within the round " +
        "budget. Escalate when the review is undecidable, blocking issues remain after revision, the input " +
        "ambiguity exceeds the threshold, or the decision requires a human judgment call.";

    private const string DefaultRoutingGuidance =
        "At the supervised baseline (autonomy 70) assign nearly every acceptance decision to a human role. As " +
        "the autonomy level rises, decide more routine, unambiguous, fully-approved documents yourself and " +
        "assign only the contested or high-impact ones. Always assign — never reject or hard-accept — anything " +
        "you are not confident the rules unambiguously permit.";

    /// <summary>
    /// The 7-role plan/review panel roster (Design Decision D2 — pinned by the
    /// drift test). Mirrors <c>PlanReviewWorkflow</c>'s roster (architect,
    /// developer, tester, security, devops, product_owner, senior_developer);
    /// <c>tech_writer</c> is intentionally excluded.
    /// </summary>
    public static IReadOnlyList<string> PanelRoster { get; } = new[]
    {
        AgentRole.Architect.ToWire(),
        AgentRole.Developer.ToWire(),
        AgentRole.Tester.ToWire(),
        AgentRole.Security.ToWire(),
        AgentRole.Devops.ToWire(),
        AgentRole.ProductOwner.ToWire(),
        AgentRole.SeniorDeveloper.ToWire(),
    };

    /// <summary>
    /// The shared-knobs BASE row used for the principal-base tier — single
    /// reviewer (<c>architect</c>), unanimous. <see cref="For"/> layers the
    /// per-type reviewer default on top of these same knobs.
    /// </summary>
    public static AcceptanceRules Rules { get; }

    private static readonly AcceptanceRules s_panelRules;
    private static readonly AcceptanceRules s_productOwnerRules;
    private static readonly AcceptanceRules s_testerRules;
    private static readonly AcceptanceRules s_securityRules;
    private static readonly AcceptanceRules s_techWriterRules;

    static AcceptanceDefaults()
    {
        Rules = new AcceptanceRules
        {
            AutonomyLevel = DefaultAutonomyLevel,
            MaxRevisionRounds = DefaultMaxRevisionRounds,
            MaxValidationRepairAttempts = DefaultMaxValidationRepairAttempts,
            AmbiguityEscalationThreshold = DefaultAmbiguityEscalationThreshold,
            AlwaysEscalate = Array.Empty<EscalationClass>(),
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.Architect.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
            DecisionGuidance = DefaultDecisionGuidance,
            RoutingGuidance = DefaultRoutingGuidance,
        }.Validate();

        s_panelRules = (Rules with
        {
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.Panel,
                ReviewerRole: null,
                PanelRoles: PanelRoster,
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Majority),
        }).Validate();

        // Story 41-1b D1 — the per-type rows for the six Epic 41 types. Each is
        // the base row with ONLY the reviewer (and, where stated, the acceptor
        // floor) overridden; ux_designer / scrum_master are deliberately NOT
        // rostered here (that is 41-1a's D2).
        s_productOwnerRules = (Rules with
        {
            // backlog-ordering: ranking a backlog is a PO judgment; an architect
            // reviewer is nonsense.
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.ProductOwner.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
        }).Validate();

        // test-plan: strategy is reviewed by QA, not architecture.
        s_testerRules = (Rules with
        {
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.Tester.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
        }).Validate();

        // threat-model: "unmitigated high-risk => escalation" is a security-owned
        // call — security reviews. The human acceptor is no longer a stored
        // constant (Story 43-16): it is DERIVED from threat-model's catalog level
        // against the dial in AcceptanceFloors. Only the reviewer stays here.
        s_securityRules = (Rules with
        {
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.Security.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
        }).Validate();

        // Story 41-1c D6 — prose: a SINGLE tech_writer reviewer, unanimous,
        // AcceptorRequirement unchanged from base. Deliberately NOT a panel row:
        // PanelRoster excludes tech_writer by design (the drift test pins the
        // exclusion), and prose review is a docs-review judgment, not an
        // architecture one. Per-kind overrides (a runbook wants ops eyes, a
        // stakeholder update may want none) are left to the consuming stories
        // via the existing per-document-type autonomy override.
        s_techWriterRules = (Rules with
        {
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.TechWriter.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
        }).Validate();

        // Fail loud if any per-type default is invalid.
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            _ = For(type);
    }

    /// <summary>
    /// The per-type default: <c>plan</c> and <c>review</c> get the 7-role
    /// majority panel; the 41-1b types get their D1 reviewer rows
    /// (<c>acceptance-criteria</c> and <c>ux-spec</c> the panel;
    /// <c>backlog-ordering</c> and <c>sprint-plan</c> a product_owner reviewer;
    /// <c>test-plan</c> a tester reviewer; <c>threat-model</c> a security
    /// reviewer); <c>prose</c> gets a single <c>tech_writer</c> reviewer (41-1c
    /// D6); every other type (including <c>design</c>) gets the single-<c>architect</c>
    /// unanimous base row. The ACCEPTOR REQUIREMENT is uniformly
    /// <see cref="AcceptorRequirement.Any"/> here — the human floor for
    /// <c>design</c>/<c>sprint-plan</c>/<c>threat-model</c> is DERIVED in
    /// <see cref="AcceptanceFloors"/> (Story 43-16), not stored.
    /// </summary>
    public static AcceptanceRules For(DocumentTypeKey type) => type switch
    {
        // acceptance-criteria: it is the merge gate's definition of done and
        // 41-15 verifies against it — the same breadth plan/review get.
        // ux-spec: cross-functional; the 7-role panel is the honest default
        // until 41-28 defines a design panel.
        DocumentTypeKey.Plan or DocumentTypeKey.Review
            or DocumentTypeKey.AcceptanceCriteria or DocumentTypeKey.UxSpec => s_panelRules,
        // design (39-13 D4) is no longer a human-acceptor row here — the human
        // floor is derived (Story 43-16). It keeps its single-architect base row.
        DocumentTypeKey.BacklogOrdering => s_productOwnerRules,
        // sprint-plan keeps its product_owner reviewer; its human acceptor is
        // derived (Story 43-16), no longer a stored constant.
        DocumentTypeKey.SprintPlan => s_productOwnerRules,
        DocumentTypeKey.TestPlan => s_testerRules,
        DocumentTypeKey.ThreatModel => s_securityRules,
        // prose (41-1c D6): reviewed by the tech_writer, never the architect
        // catch-all — AC6 pins that prose does not reach `_ => Rules` by accident.
        DocumentTypeKey.Prose => s_techWriterRules,
        _ => Rules,
    };
}
