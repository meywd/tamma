using Microsoft.AspNetCore.Http;
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
    /// <summary>Binds a minimal-API endpoint to a catalogued action. May be called
    /// more than once on the same route (Story 43-12) — each call adds one
    /// <see cref="ActionGateMetadata"/>. A route with MORE THAN ONE binding MUST
    /// also attach a key selector (<see cref="SelectsGovernanceKeyWith{TSelector}"/>)
    /// or the enforcement seam fails closed with
    /// <c>ACTION.GATE.MISCONFIGURED</c>.</summary>
    public static RouteHandlerBuilder Governs(this RouteHandlerBuilder builder, ActionKey action) =>
        builder.WithMetadata(new ActionGateMetadata(action));

    /// <summary>
    /// Story 43-12 (D2) — attach the per-request key selector a MULTI-BINDING route
    /// uses to pick WHICH of its bound keys the gate evaluates for a given request.
    /// The selector type is resolved from DI at request time (so it can inject
    /// services such as the git mediation client); it must be registered.
    /// </summary>
    public static RouteHandlerBuilder SelectsGovernanceKeyWith<TSelector>(this RouteHandlerBuilder builder)
        where TSelector : class, IActionKeySelector =>
        builder.WithMetadata(new ActionKeySelectorMetadata(typeof(TSelector)));

    // --- Story 43-12 (D2) multi-binding key selection ---

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

/// <summary>
/// Story 43-12 (D2) — resolves WHICH catalog key a multi-binding route's gate
/// evaluates for a specific request. A route whose action depends on the request
/// (the merge route: the key depends on the PR's base branch, which no static
/// metadata can express) carries several <see cref="IActionGateMetadata"/> bindings
/// plus one selector; the enforcement seam invokes the selector to pick.
///
/// <para><b>Fail-closed is the selector's own job.</b> A selector that cannot read
/// what it needs to decide returns the STRICTEST candidate (a DECISION, per AC3),
/// not an exception — an exception here would ride Seam C's transient fail-OPEN arm,
/// which is the opposite of fail-closed. See
/// <c>MergeTargetActionKeySelector</c>.</para>
/// </summary>
public interface IActionKeySelector
{
    /// <summary>
    /// Pick one of <paramref name="candidates"/> (the route's bound keys, in binding
    /// order) for THIS request. Never throws for a read it could not do — it returns
    /// the fail-closed candidate instead.
    /// </summary>
    Task<ActionKey> SelectAsync(HttpContext http, IReadOnlyList<ActionKey> candidates, CancellationToken ct);
}

/// <summary>The endpoint metadata marker naming the selector type a multi-binding
/// route uses. Resolved from DI by the enforcement seam.</summary>
public interface IActionKeySelectorMetadata
{
    /// <summary>The <see cref="IActionKeySelector"/> implementation type to resolve.</summary>
    Type SelectorType { get; }
}

/// <inheritdoc cref="IActionKeySelectorMetadata" />
/// <param name="SelectorType">The selector implementation type to resolve from DI.</param>
public sealed record ActionKeySelectorMetadata(Type SelectorType) : IActionKeySelectorMetadata;
