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
/// <para>The ACCEPTOR REQUIREMENT is per-type too (Story 39-13 Design Decision D4):
/// <c>design</c> defaults to <see cref="AcceptorRequirement.Human"/> — a design proposal
/// is pinned to a human acceptor no matter how high the autonomy dial is set. Story
/// 41-1b (D1) added <c>sprint-plan</c> (a capacity commitment is a human commitment)
/// and <c>threat-model</c> (unmitigated high-risk is a security-owned human call) to
/// the human-pinned set; every other type keeps <see cref="AcceptorRequirement.Any"/>,
/// the pre-39-13 behavior where the autonomy dial alone decides who accepts.</para>
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
    private static readonly AcceptanceRules s_humanAcceptorRules;
    private static readonly AcceptanceRules s_productOwnerRules;
    private static readonly AcceptanceRules s_humanProductOwnerRules;
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

        // Story 39-13 D4 — the base row with the acceptance decision pinned to a human.
        // Reviewer selection is untouched (single architect, unanimous); only WHO answers
        // the accept decision changes.
        s_humanAcceptorRules = (Rules with
        {
            AcceptorRequirement = AcceptorRequirement.Human,
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

        // sprint-plan: a capacity commitment is a human commitment (the 39-13 D4
        // posture Design got), reviewed by the product owner until 41-1a/41-6
        // introduce a scrum_master surface.
        s_humanProductOwnerRules = (s_productOwnerRules with
        {
            AcceptorRequirement = AcceptorRequirement.Human,
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
        // call — security reviews, and the acceptance decision is pinned human.
        s_securityRules = (Rules with
        {
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.SingleReviewer,
                ReviewerRole: AgentRole.Security.ToWire(),
                PanelRoles: Array.Empty<string>(),
                Quorum: null,
                DecisionRule: ReviewDecisionRule.Unanimous),
            AcceptorRequirement = AcceptorRequirement.Human,
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
    /// majority panel; <c>design</c> gets the human-acceptor row (39-13 D4);
    /// the six 41-1b types get their D1 rows (<c>acceptance-criteria</c> and
    /// <c>ux-spec</c> the panel; <c>backlog-ordering</c> a product_owner
    /// reviewer; <c>sprint-plan</c> a product_owner reviewer + human acceptor;
    /// <c>test-plan</c> a tester reviewer; <c>threat-model</c> a security
    /// reviewer + human acceptor); <c>prose</c> gets a single <c>tech_writer</c>
    /// reviewer (41-1c D6); every other type gets the single-<c>architect</c>
    /// unanimous base row.
    /// </summary>
    public static AcceptanceRules For(DocumentTypeKey type) => type switch
    {
        // acceptance-criteria: it is the merge gate's definition of done and
        // 41-15 verifies against it — the same breadth plan/review get.
        // ux-spec: cross-functional; the 7-role panel is the honest default
        // until 41-28 defines a design panel.
        DocumentTypeKey.Plan or DocumentTypeKey.Review
            or DocumentTypeKey.AcceptanceCriteria or DocumentTypeKey.UxSpec => s_panelRules,
        DocumentTypeKey.Design => s_humanAcceptorRules,
        DocumentTypeKey.BacklogOrdering => s_productOwnerRules,
        DocumentTypeKey.SprintPlan => s_humanProductOwnerRules,
        DocumentTypeKey.TestPlan => s_testerRules,
        DocumentTypeKey.ThreatModel => s_securityRules,
        // prose (41-1c D6): reviewed by the tech_writer, never the architect
        // catch-all — AC6 pins that prose does not reach `_ => Rules` by accident.
        DocumentTypeKey.Prose => s_techWriterRules,
        _ => Rules,
    };
}
