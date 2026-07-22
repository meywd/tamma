using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The configurable acceptance policy for a document type (Story 39-5 AC1). It
/// expresses the autonomy dial (70–100), the revision/repair bounds, the
/// escalation criteria (ambiguity threshold + always-escalate classes), the
/// reviewer selection (single reviewer vs panel), and the two operator-authored
/// guidance strings the orchestrator reads when it decides and routes.
///
/// <para>
/// Every property carries an explicit <c>[JsonPropertyName]</c> (39-2 D8);
/// serialize/deserialize through <see cref="AcceptanceRulesJson.Options"/> so the
/// wire contract — including the <c>[Wire]</c> enum spellings — is deliberate.
/// </para>
///
/// <para>
/// <see cref="Validate"/> REJECTS out-of-range knobs and unknown taxonomy keys
/// (Design Decision D4) — it never clamps. Validation runs both fail-loud on
/// write and defensively on read so a corrupt row throws rather than silently
/// degrading.
/// </para>
/// </summary>
public sealed record AcceptanceRules
{
    /// <summary>How much the orchestrator decides itself (70 = supervised baseline, 100 = full auto). Validated 70–100.</summary>
    [JsonPropertyName("autonomyLevel")]
    public required int AutonomyLevel { get; init; }

    /// <summary>Maximum review/revision rounds before <c>Escalate(RoundsExhausted)</c>. Validated 1–10.</summary>
    [JsonPropertyName("maxRevisionRounds")]
    public required int MaxRevisionRounds { get; init; }

    /// <summary>Maximum deterministic validation-repair attempts. Validated 0–10.</summary>
    [JsonPropertyName("maxValidationRepairAttempts")]
    public required int MaxValidationRepairAttempts { get; init; }

    /// <summary>Ambiguity score at/above which a request escalates. Validated [0,1].</summary>
    [JsonPropertyName("ambiguityEscalationThreshold")]
    public required double AmbiguityEscalationThreshold { get; init; }

    /// <summary>Document/action classes that always short-circuit to <c>Escalate</c> before any acceptor runs.</summary>
    [JsonPropertyName("alwaysEscalate")]
    public required IReadOnlyList<EscalationClass> AlwaysEscalate { get; init; }

    /// <summary>How reviewers are selected for 39-7 (single reviewer role or a panel + decision rule).</summary>
    [JsonPropertyName("reviewerSelection")]
    public required ReviewerSelection ReviewerSelection { get; init; }

    /// <summary>Operator prose: what warrants acceptance, revision, escalation.</summary>
    [JsonPropertyName("decisionGuidance")]
    public required string DecisionGuidance { get; init; }

    /// <summary>Operator prose: what the orchestrator must assign to a human at this autonomy level.</summary>
    [JsonPropertyName("routingGuidance")]
    public required string RoutingGuidance { get; init; }

    /// <summary>
    /// Enforce the D4 bounds and taxonomy validity, throwing
    /// <c>ACCEPTANCE_RULES.INVALID</c> on any violation. Returns <c>this</c> for
    /// fluent use. Called on every write AND on every read (defensive) so an
    /// out-of-range or typo'd row fails loud, never silently degrades.
    /// </summary>
    /// <exception cref="TammaError">Code <c>ACCEPTANCE_RULES.INVALID</c>.</exception>
    public AcceptanceRules Validate()
    {
        if (AutonomyLevel is < 70 or > 100)
            throw Invalid(nameof(AutonomyLevel), $"AutonomyLevel must be within [70, 100]; got {AutonomyLevel}.");
        if (MaxRevisionRounds is < 1 or > 10)
            throw Invalid(nameof(MaxRevisionRounds), $"MaxRevisionRounds must be within [1, 10]; got {MaxRevisionRounds}.");
        if (MaxValidationRepairAttempts is < 0 or > 10)
            throw Invalid(nameof(MaxValidationRepairAttempts), $"MaxValidationRepairAttempts must be within [0, 10]; got {MaxValidationRepairAttempts}.");
        if (double.IsNaN(AmbiguityEscalationThreshold) || AmbiguityEscalationThreshold is < 0.0 or > 1.0)
            throw Invalid(nameof(AmbiguityEscalationThreshold), $"AmbiguityEscalationThreshold must be within [0, 1]; got {AmbiguityEscalationThreshold}.");

        if (AlwaysEscalate is null)
            throw Invalid(nameof(AlwaysEscalate), "AlwaysEscalate must not be null (use an empty list).");
        foreach (var cls in AlwaysEscalate)
        {
            if (cls is null)
                throw Invalid(nameof(AlwaysEscalate), "AlwaysEscalate entries must not be null.");
            ValidateEscalationClass(cls);
        }

        if (ReviewerSelection is null)
            throw Invalid(nameof(ReviewerSelection), "ReviewerSelection must not be null.");
        ValidateReviewerSelection(ReviewerSelection);

        if (DecisionGuidance is null)
            throw Invalid(nameof(DecisionGuidance), "DecisionGuidance must not be null.");
        if (RoutingGuidance is null)
            throw Invalid(nameof(RoutingGuidance), "RoutingGuidance must not be null.");

        return this;
    }

    private static void ValidateEscalationClass(EscalationClass cls)
    {
        if (string.IsNullOrWhiteSpace(cls.Key))
            throw Invalid(nameof(AlwaysEscalate), "An always-escalate class carries an empty key.");

        try
        {
            switch (cls.Kind)
            {
                case EscalationClassKind.DocumentType:
                    DocumentTypeKeyExtensions.Parse(cls.Key);
                    break;
                case EscalationClassKind.AgentAction:
                    AgentActionExtensions.Parse(cls.Key);
                    break;
                default:
                    throw Invalid(nameof(AlwaysEscalate), $"Unknown escalation-class kind '{cls.Kind}'.");
            }
        }
        catch (TammaError ex) when (ex.Code != "ACCEPTANCE_RULES.INVALID")
        {
            throw Invalid(nameof(AlwaysEscalate),
                $"Always-escalate {cls.Kind.ToWire()} key '{cls.Key}' is not a known taxonomy vocabulary member.");
        }
        catch (ArgumentException)
        {
            throw Invalid(nameof(AlwaysEscalate),
                $"Always-escalate {cls.Kind.ToWire()} key '{cls.Key}' is not a known taxonomy vocabulary member.");
        }
    }

    private static void ValidateReviewerSelection(ReviewerSelection sel)
    {
        switch (sel.Mode)
        {
            case ReviewerMode.SingleReviewer:
                if (string.IsNullOrWhiteSpace(sel.ReviewerRole))
                    throw Invalid(nameof(ReviewerSelection), "A single-reviewer selection requires a ReviewerRole.");
                ParseRole(sel.ReviewerRole!);
                break;
            case ReviewerMode.Panel:
                if (sel.PanelRoles is null || sel.PanelRoles.Count == 0)
                    throw Invalid(nameof(ReviewerSelection), "A panel selection requires a non-empty PanelRoles roster.");
                foreach (var r in sel.PanelRoles) ParseRole(r);
                if (sel.Quorum is { } q && (q < 1 || q > sel.PanelRoles.Count))
                    throw Invalid(nameof(ReviewerSelection),
                        $"Panel quorum {q} must be within [1, {sel.PanelRoles.Count}] (the roster size).");
                break;
            default:
                throw Invalid(nameof(ReviewerSelection), $"Unknown reviewer mode '{sel.Mode}'.");
        }
    }

    private static void ParseRole(string role)
    {
        try
        {
            AgentRoleExtensions.Parse(role);
        }
        catch (ArgumentException)
        {
            throw Invalid(nameof(ReviewerSelection), $"Reviewer role '{role}' is not a known agent role.");
        }
    }

    private static TammaError Invalid(string field, string message) =>
        new(
            "ACCEPTANCE_RULES.INVALID",
            message,
            new Dictionary<string, object?> { ["field"] = field },
            retryable: false,
            severity: TammaErrorSeverity.High);
}

/// <summary>
/// One always-escalate class: a taxonomy key interpreted per its
/// <see cref="EscalationClassKind"/> (Story 39-5 AC1; the README's "whether
/// breaking changes always escalate is acceptance-rules configuration, not a
/// hardcoded rule"). <see cref="AcceptanceRules.Validate"/> parses
/// <see cref="Key"/> against the matching registry.
/// </summary>
public sealed record EscalationClass(
    [property: JsonPropertyName("kind")] EscalationClassKind Kind,
    [property: JsonPropertyName("key")] string Key);

/// <summary>Whether an <see cref="EscalationClass.Key"/> is a document-type or agent-action wire string.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<EscalationClassKind>))]
public enum EscalationClassKind
{
    [Wire("document-type")] DocumentType,
    [Wire("agent-action")]  AgentAction,
}

/// <summary>
/// Reviewer selection for 39-7 (Story 39-5 AC1): a single reviewer role, or a
/// panel with a roster + a <see cref="ReviewDecisionRule"/> (unanimous/majority)
/// that 39-7 reads to resolve the panel verdict. <see cref="Quorum"/> is an
/// optional numeric floor; the decision rule — not a bare number — expresses
/// unanimous-vs-majority.
/// </summary>
public sealed record ReviewerSelection(
    [property: JsonPropertyName("mode")] ReviewerMode Mode,
    [property: JsonPropertyName("reviewerRole")] string? ReviewerRole,
    [property: JsonPropertyName("panelRoles")] IReadOnlyList<string> PanelRoles,
    [property: JsonPropertyName("quorum")] int? Quorum,
    [property: JsonPropertyName("decisionRule")] ReviewDecisionRule DecisionRule);

/// <summary>Single reviewer or a full panel.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<ReviewerMode>))]
public enum ReviewerMode
{
    [Wire("single-reviewer")] SingleReviewer,
    [Wire("panel")]           Panel,
}

/// <summary>How a panel's verdict is resolved (39-7 consumes this).</summary>
[JsonConverter(typeof(WireEnumJsonConverter<ReviewDecisionRule>))]
public enum ReviewDecisionRule
{
    [Wire("unanimous")] Unanimous,
    [Wire("majority")]  Majority,
}

/// <summary><see cref="EscalationClassKind"/> wire helper.</summary>
public static class EscalationClassKindExtensions
{
    public static string ToWire(this EscalationClassKind kind) => EnumWire<EscalationClassKind>.ToWire(kind);
}

/// <summary><see cref="ReviewerMode"/> wire helper.</summary>
public static class ReviewerModeExtensions
{
    public static string ToWire(this ReviewerMode mode) => EnumWire<ReviewerMode>.ToWire(mode);
}

/// <summary><see cref="ReviewDecisionRule"/> wire helper.</summary>
public static class ReviewDecisionRuleExtensions
{
    public static string ToWire(this ReviewDecisionRule rule) => EnumWire<ReviewDecisionRule>.ToWire(rule);
}
