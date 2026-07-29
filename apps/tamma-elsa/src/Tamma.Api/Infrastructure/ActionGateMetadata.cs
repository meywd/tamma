using Tamma.Core.Actions;

namespace Tamma.Api.Infrastructure;

/// <summary>
/// Story 43-8 (AC1, D3) — the ONE thing every drift harness and (from Story 43-9)
/// the enforcement filter read to learn which catalogued action an endpoint
/// performs. Two authoring shapes attach it, because two route-authoring styles
/// genuinely exist in this codebase and neither can be forced into the other:
/// <list type="bullet">
///   <item><c>RouteHandlerBuilder.Governs(ActionKey)</c> — minimal APIs
///   (<see cref="GovernsExtensions"/>), which attaches an
///   <see cref="ActionGateMetadata"/> instance;</item>
///   <item><c>[Governs(ActionNamespace.Effect, "…")]</c> — controller actions
///   (<see cref="GovernsAttribute"/>), which the MVC attribute-to-metadata
///   pipeline surfaces in <c>Endpoint.Metadata</c>.</item>
/// </list>
/// Both implement <see cref="IActionGateMetadata"/>, so a harness or a filter looks
/// up exactly one type: <c>endpoint.Metadata.GetMetadata&lt;IActionGateMetadata&gt;()</c>.
/// </summary>
public interface IActionGateMetadata
{
    /// <summary>The catalogued action this endpoint performs.</summary>
    ActionKey Action { get; }
}

/// <summary>
/// The minimal-API shape of <see cref="IActionGateMetadata"/> (Story 43-8 AC1).
/// A marker with no behaviour: Story 43-9 adds the endpoint filter that reads it.
/// </summary>
/// <param name="Action">The catalogued action this endpoint performs.</param>
public sealed record ActionGateMetadata(ActionKey Action) : IActionGateMetadata;

/// <summary>
/// The controller-action shape of <see cref="IActionGateMetadata"/> (Story 43-8
/// AC1, D3). An attribute rather than a builder call because a controller action
/// has no <c>RouteHandlerBuilder</c>; MVC copies action-method attributes into the
/// endpoint's metadata collection, so both shapes are visible to the same
/// <c>GetMetadata&lt;IActionGateMetadata&gt;()</c> lookup.
///
/// <para>The key is authored as (namespace, wire) rather than as an
/// <see cref="ActionKey"/> because attribute arguments must be compile-time
/// constants. The wire string is checked against the catalog by
/// <c>GovernedEndpointBindingTests</c> — a typo fails the build, it does not
/// silently govern nothing.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class GovernsAttribute(ActionNamespace ns, string key) : Attribute, IActionGateMetadata
{
    /// <inheritdoc />
    public ActionKey Action { get; } = new(ns, key);
}
