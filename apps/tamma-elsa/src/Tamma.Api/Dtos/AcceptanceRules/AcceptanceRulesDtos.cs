using System.Text.Json.Serialization;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Dtos.AcceptanceRules;

/// <summary>
/// PUT body for <c>/api/acceptance-rules/{documentTypeKey}</c> (Story 39-5). The
/// shape mirrors <see cref="AcceptanceRules"/>; <see cref="ToRules"/> maps it to
/// the domain record which the service validates fail-loud (rejects out-of-range
/// knobs and unknown taxonomy keys) before any write.
/// </summary>
public sealed record AcceptanceRulesUpsertRequest(
    [property: JsonPropertyName("autonomyLevel")] int AutonomyLevel,
    [property: JsonPropertyName("maxRevisionRounds")] int MaxRevisionRounds,
    [property: JsonPropertyName("maxValidationRepairAttempts")] int MaxValidationRepairAttempts,
    [property: JsonPropertyName("ambiguityEscalationThreshold")] double AmbiguityEscalationThreshold,
    [property: JsonPropertyName("alwaysEscalate")] IReadOnlyList<EscalationClass>? AlwaysEscalate,
    [property: JsonPropertyName("reviewerSelection")] ReviewerSelection ReviewerSelection,
    [property: JsonPropertyName("decisionGuidance")] string DecisionGuidance,
    [property: JsonPropertyName("routingGuidance")] string RoutingGuidance,
    // Story 39-13 D4 — the per-type autonomy floor.
    //
    // Story 43-0: NULLABLE and defaulted to null, which means "the caller did not
    // say", NOT "any". It used to be a non-nullable `= AcceptorRequirement.Any`,
    // and that default was the live data-loss bug: the admin dialog's PUT body
    // omitted the property, the binder invented `any`, and every save of `design`
    // (or `sprint-plan`, or `threat-model`) silently erased its shipped
    // `human` acceptor floor. A body that does not mention the field must never be
    // reinterpreted as a value — the caller's silence is preserved by
    // `AcceptanceRulesEndpoints.Upsert`, which passes the CURRENTLY EFFECTIVE
    // requirement into `ToRules` (same posture as the single-field writes in
    // `ActionPolicyEndpoints`, which call this "the 43-0 bug class").
    //
    // AN EXPLICIT `"acceptorRequirement": null` IS TREATED IDENTICALLY TO OMITTING
    // THE FIELD — both preserve what is in force (review MINOR-5, 2026-07-29). This
    // is a plain `AcceptorRequirement?`, not a tri-state: `ToRules` reduces it with
    // `?? currentAcceptorRequirement`, so "absent" and "present and null" collapse to
    // the same instruction and a caller CANNOT say "clear this field" here. That is
    // correct for this member — there is no cleared state; the floor is always one of
    // the enum's values — but it is worth stating, because Story 44-2 builds an entire
    // `Optional<T>` tri-state apparatus in the same commit precisely to keep those two
    // cases distinguishable on the tracker's PATCH bodies. The difference is
    // deliberate, not an inconsistency: a nullable column can be cleared, an
    // always-present enum floor cannot.
    //
    // Legacy stored rows are unaffected by this: persistence round-trips the DOMAIN
    // record `Tamma.Core.Documents.Policy.AcceptanceRules`, whose own
    // `AcceptorRequirement { get; init; } = AcceptorRequirement.Any` property default
    // is the legacy-body safety net. This DTO only binds inbound PUT bodies.
    [property: JsonPropertyName("acceptorRequirement")] AcceptorRequirement? AcceptorRequirement
        = null)
{
    /// <summary>
    /// Map to the domain record (unvalidated — the service calls <c>Validate()</c>).
    /// </summary>
    /// <param name="currentAcceptorRequirement">
    /// The requirement that is in force for this document type RIGHT NOW (the
    /// resolved override row, or the shipped per-type default). Used only when the
    /// body omitted <c>acceptorRequirement</c> — an omission preserves, it never
    /// resets. There is deliberately no parameterless overload: a call site that
    /// cannot say what the current value is has no business writing this field.
    /// </param>
    public Tamma.Core.Documents.Policy.AcceptanceRules ToRules(
        AcceptorRequirement currentAcceptorRequirement) => new()
    {
        AutonomyLevel = AutonomyLevel,
        MaxRevisionRounds = MaxRevisionRounds,
        MaxValidationRepairAttempts = MaxValidationRepairAttempts,
        AmbiguityEscalationThreshold = AmbiguityEscalationThreshold,
        AlwaysEscalate = AlwaysEscalate ?? Array.Empty<EscalationClass>(),
        ReviewerSelection = ReviewerSelection,
        DecisionGuidance = DecisionGuidance ?? string.Empty,
        RoutingGuidance = RoutingGuidance ?? string.Empty,
        AcceptorRequirement = AcceptorRequirement ?? currentAcceptorRequirement,
    };
}
