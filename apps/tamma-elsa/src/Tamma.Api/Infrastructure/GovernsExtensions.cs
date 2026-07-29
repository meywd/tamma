using Tamma.Core.Actions;

namespace Tamma.Api.Infrastructure;

/// <summary>
/// Story 43-8 (AC1, D3) — the minimal-API authoring shape for
/// <see cref="ActionGateMetadata"/>.
///
/// <para>
/// <c>.Governs(key)</c> attaches metadata ONLY. It does not enforce anything: Story
/// 43-9 adds <c>.AddEndpointFilter&lt;ActionGateFilter&gt;()</c> here, so that
/// annotating a route and enforcing on it remain one call. Landing the metadata
/// first is deliberate — the drift harnesses must be able to see a binding before
/// enforcement is attached to it, otherwise 43-9 would attach enforcement to a
/// surface nothing verifies.
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
