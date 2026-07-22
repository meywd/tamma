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

        // Fail loud if any per-type default is invalid.
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            _ = For(type);
    }

    /// <summary>
    /// The per-type default: <c>plan</c> and <c>review</c> get the 7-role
    /// majority panel; every other type gets the single-<c>architect</c>
    /// unanimous base row.
    /// </summary>
    public static AcceptanceRules For(DocumentTypeKey type) =>
        type is DocumentTypeKey.Plan or DocumentTypeKey.Review ? s_panelRules : Rules;
}
