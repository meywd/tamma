using Tamma.Core.Actions;

namespace Tamma.Api.Infrastructure;

/// <summary>
/// Story 43-8 (AC1, D3) — the minimal-API authoring shape for
/// <see cref="ActionGateMetadata"/>.
///
/// <para>
/// <c>.Governs(key)</c> attaches metadata ONLY, and — <b>corrected 2026-08-01 by
/// Story 43-9's Decision D15</b> — it always will. This doc-comment used to say
/// that 43-9 would add <c>.AddEndpointFilter&lt;ActionGateFilter&gt;()</c> HERE
/// "so that annotating a route and enforcing on it remain one call". That design
/// is OVERTURNED. Enforcement is a separate, visible, per-route opt-in:
/// <see cref="EnforcesGovernanceExtensions.EnforcesGovernance"/> for minimal APIs
/// and <see cref="EnforcesGovernanceAttribute"/> for controller actions. The three
/// reasons — blast radius, Seam A's "never blocks" being structural rather than a
/// keyed carve-out, and the fact that the controller-attribute plane never passes
/// through this method at all — are recorded on
/// <see cref="IGovernanceEnforcementMetadata"/>.
/// </para>
///
/// <para>
/// 43-8's other claim is unaffected and still true: landing the metadata first was
/// deliberate, because the drift harnesses must be able to see a binding before
/// enforcement is attached to it.
/// </para>
///
/// <para>
/// WHAT A BINDING PROVES (43-8 AC10(a)): that a route is bound to a catalog member
/// whose <c>SiteKey</c> is this route. It does NOT prove the handler's body performs
/// only that action — a new capability grown inside an already-governed handler
/// passes every harness in this epic.
/// </para>
/// </summary>
public static class GovernsExtensions
{
    /// <summary>Binds a minimal-API endpoint to a catalogued action.</summary>
    public static RouteHandlerBuilder Governs(this RouteHandlerBuilder builder, ActionKey action) =>
        builder.WithMetadata(new ActionGateMetadata(action));

    // REMOVED 2026-07-29 (adversarial review F17): a
    // `Governs(this RouteGroupBuilder, ActionKey)` overload also shipped here. It
    // attached ONE ActionGateMetadata to EVERY endpoint in the group, but
    // GovernedEndpointBindingSweepTests requires a binding's descriptor SiteKey to
    // equal that endpoint's own $"{method} {pattern}" — so at most one route in a
    // group could ever match and the overload GUARANTEED N-1 binding failures. It
    // had zero call sites in src/ and tests/. A helper that cannot be used correctly
    // is worse than no helper, so it is deleted rather than documented; a genuine
    // single-route group can still call .Governs on the RouteHandlerBuilder that
    // MapPost/MapPut/… returns. Story 43-9 binds routes individually.
}
