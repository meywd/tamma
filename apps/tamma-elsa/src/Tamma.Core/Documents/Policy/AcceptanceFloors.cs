namespace Tamma.Core.Documents.Policy;

/// <summary>
/// THE one non-wholesale rule in acceptance-rules resolution (Story 43-0
/// amendment A1 / epic-43 carried defect CD-1, closed 2026-07-30): a SHIPPED
/// human-acceptance requirement is a <b>FLOOR</b>, not a default.
///
/// <para><b>The defect this closes.</b> 39-5 D2 resolves three tiers WHOLESALE —
/// per-type override row, else principal BASE override row, else
/// <see cref="AcceptanceDefaults.For"/> — with no field merge. So the moment a
/// base row existed it shadowed tier 3 ENTIRELY, and ONE
/// <c>PUT /api/acceptance-rules/base</c> silently erased the
/// <see cref="AcceptorRequirement.Human"/> floor that <c>design</c>,
/// <c>sprint-plan</c> and <c>threat-model</c> ship — without any of those three
/// having been written. A later per-type save then read the degraded value as
/// "what is in force" (Story 43-0's preserve-on-absent) and baked it into a type
/// row, after which deleting the base row no longer restored the floor.</para>
///
/// <para><b>Why a floor and not a per-field merge.</b> CD-1 named two candidate
/// fixes. A general per-field merge across tiers was REJECTED: it is exactly
/// what 39-5 D2 rejected ("field-level deep-merging makes provenance
/// unexplainable in the admin UI and has no precedent — a prompt override
/// replaces the template entirely"), and it would silently change resolution for
/// EVERY field of every stored base row. This is the narrower, monotone fix:
/// wholesale-row precedence is untouched for every other field, and exactly one
/// field composes across tiers, by <c>max()</c> over a two-element lattice
/// (<see cref="AcceptorRequirement.Any"/> &lt; <see cref="AcceptorRequirement.Human"/>).
/// It is the SAME shape the epic already uses everywhere else that a policy
/// input can only tighten: <c>AutonomyGateEvaluator</c>'s platform ceiling and
/// its legacy always-escalate floor both compose by <c>max()</c>, and the action
/// catalog already hardcodes <c>document-type:design|sprint-plan|threat-model</c>
/// at <c>AutonomyDial.AlwaysHuman</c> <i>because</i> those types ship
/// <c>AcceptorRequirement.Human</c> — so before this fix, one base PUT put the
/// catalog and the acceptance-rules resolver into open disagreement about
/// whether a design needs a person.</para>
///
/// <para><b>The tier-1 exemption is the product decision.</b> The floor is
/// applied to the BASE tier and the SYSTEM-DEFAULT tier (where it is a no-op),
/// and deliberately NOT to a per-type override row: writing
/// <c>PUT /api/acceptance-rules/design</c> with an explicit
/// <c>"acceptorRequirement": "any"</c> still lowers it. Lowering a shipped human
/// floor must NAME THE TYPE — that is the difference between silence and intent,
/// and it is the semantic
/// <c>AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor</c>
/// already pinned for the per-type route. A base row is one row standing in for
/// every document type with three different floors; it has no way to express
/// intent about any one of them, which is precisely why it may not lower them.</para>
///
/// <para><b>Not closed here, deliberately:</b> tier-2 shadowing of
/// <c>threat-model</c>'s <c>security</c> <see cref="ReviewerSelection"/> (the
/// other half of CD-1). Reviewer selection carries no ordering — there is no
/// <c>max(architect, security)</c> — so no monotone floor exists for it, and
/// "deployment-wide reviewer selection" is a legitimate thing for a base row to
/// say. It stays wholesale and is recorded as such in the story.</para>
/// </summary>
public static class AcceptanceFloors
{
    /// <summary>
    /// The two-element lattice: <see cref="AcceptorRequirement.Human"/> is
    /// strictly above <see cref="AcceptorRequirement.Any"/>, so <c>max</c> can
    /// only tighten. (A future third member must be inserted in ascending
    /// strictness order for this to remain correct — the enum's order IS the
    /// lattice, exactly as <c>AutonomyDial</c>'s integers are.)
    /// </summary>
    public static AcceptorRequirement Max(AcceptorRequirement a, AcceptorRequirement b) =>
        (AcceptorRequirement)System.Math.Max((int)a, (int)b);

    /// <summary>The shipped, non-lowerable acceptor floor for a document type.</summary>
    public static AcceptorRequirement ShippedFloorFor(DocumentTypeKey type) =>
        AcceptanceDefaults.For(type).AcceptorRequirement;

    /// <summary>
    /// Raise <paramref name="resolved"/>'s <see cref="AcceptorRequirement"/> to
    /// the shipped floor for <paramref name="type"/> when a NON-per-type tier
    /// produced it. Returns the same instance untouched when nothing needs
    /// raising, so the common path allocates nothing and the
    /// <see cref="ResolvedAcceptanceRules.AcceptorRequirementFloored"/> flag is
    /// only ever true when the floor actually bit.
    /// </summary>
    public static ResolvedAcceptanceRules ApplyShippedAcceptorFloor(
        ResolvedAcceptanceRules resolved, DocumentTypeKey type)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var floored = Max(resolved.Rules.AcceptorRequirement, ShippedFloorFor(type));
        if (floored == resolved.Rules.AcceptorRequirement)
        {
            return resolved;
        }

        return resolved with
        {
            Rules = resolved.Rules with { AcceptorRequirement = floored },
            AcceptorRequirementFloored = true,
        };
    }
}
