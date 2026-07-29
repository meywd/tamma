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

    /// <summary>Binds every endpoint in a route group to a catalogued action.</summary>
    public static RouteGroupBuilder Governs(this RouteGroupBuilder builder, ActionKey action)
    {
        builder.WithMetadata(new ActionGateMetadata(action));
        return builder;
    }
}
