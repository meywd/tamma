using System.Collections.Frozen;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Activities.LlmCall;
using Tamma.Api.Infrastructure;
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-8 <b>AC9</b> / D9 (amendment §A1 carve-out #4, closed 2026-07-30) — the
/// per-action list of CONCRETE SITES that carry the gate for a catalogued action,
/// computed from the running application rather than restated by hand. Serialised by
/// <c>ActionPolicyEndpoints</c> as <c>enforcementSites</c>, so the Story 43-7 admin
/// UI can render an explicit <b>"not enforced anywhere yet"</b> state for an action
/// whose array is empty.
///
/// <para><b>WHY THIS TYPE EXISTS AT ALL.</b> On the day 43-8's harnesses landed,
/// every catalog row had zero sites and the admin surface had no way to say so — a
/// row that governs nothing would have rendered exactly like a row that governs a
/// route, which is the same class of lie the epic is built to prevent. The array
/// makes the gap VISIBLE and per-row, instead of a paragraph in a story.</para>
///
/// <para><b>WHAT A SITE MEANS, precisely</b> (read this before rendering it):</para>
/// <list type="bullet">
///   <item>A site is a place that is BOUND to the action — a route carrying
///   <see cref="IActionGateMetadata"/> (either authoring shape), or a
///   <see cref="TammaApiClient"/> method carrying
///   <see cref="PerformsEffectAttribute"/>.</item>
///   <item>A binding is where the gate WILL be evaluated. <b>Story 43-9 attaches the
///   filter that actually evaluates it</b> — <c>GovernsExtensions.Governs</c> is
///   metadata-only today. So a non-empty array means "this action has a governable
///   site", not "a request was blocked here". An EMPTY array is the unambiguous
///   fact, and it is the one AC9 requires the UI to render.</item>
///   <item>A site is a SITE, not an EFFECT (43-8 AC10(a)). A capability grown inside
///   an already-bound handler is invisible here, exactly as it is to every harness
///   in this epic.</item>
/// </list>
///
/// <para><b>WHAT IT CANNOT SEE.</b> Only this host's <see cref="EndpointDataSource"/>
/// and only <see cref="TammaApiClient"/>'s declared public instance methods. Routes
/// behind a configuration branch that is false in this process are absent; the
/// out-of-process Elsa workflow API is absent; the TypeScript sidecar is absent past
/// the proxy route; Seam D/E (background actors, platform tasks) have no annotation
/// shape yet and therefore contribute no sites — an <c>automation:*</c> row shows an
/// empty array today, which is honest rather than convenient.</para>
///
/// <para><b>ONE reflection, not two.</b> The endpoint walk lives here, in production
/// code, and <c>Tamma.Api.Tests</c>'s <c>ActionEnforcementSitesTests</c> cross-checks
/// this computation against <c>GovernanceHostFixture</c>'s independently-derived
/// endpoint facts. The drift harness and the API therefore cannot disagree about
/// which routes are bound without a test going red.</para>
/// </summary>
public interface IActionEnforcementSites
{
    /// <summary>
    /// The concrete enforcement sites for <paramref name="action"/>, ordered
    /// ordinally. Empty means NOT ENFORCED ANYWHERE — render it as such.
    /// </summary>
    IReadOnlyList<string> For(ActionKey action);

    /// <summary>Every action that has at least one site, with its sites.</summary>
    IReadOnlyDictionary<ActionKey, IReadOnlyList<string>> All();
}

/// <inheritdoc cref="IActionEnforcementSites" />
public sealed class ActionEnforcementSites : IActionEnforcementSites
{
    /// <summary>Prefix for a route-plane site: <c>"route: POST /api/engine/events"</c>.</summary>
    public const string RoutePrefix = "route: ";

    /// <summary>Prefix for a method-plane site: <c>"method: Tamma…TammaApiClient.CallLlmAsync"</c>.</summary>
    public const string MethodPrefix = "method: ";

    private readonly Lazy<FrozenDictionary<ActionKey, IReadOnlyList<string>>> _sites;

    /// <summary>
    /// Endpoints are resolved LAZILY through <paramref name="endpointProvider"/>.
    /// Endpoint building happens on the first request, not at service-provider
    /// construction, so eager computation in the constructor would see an empty (or
    /// partial) data source.
    /// </summary>
    /// <param name="endpointProvider">Supplies the host's endpoints; see <see cref="DiscoverEndpoints"/>.</param>
    public ActionEnforcementSites(Func<IEnumerable<Endpoint>> endpointProvider)
    {
        ArgumentNullException.ThrowIfNull(endpointProvider);
        _sites = new Lazy<FrozenDictionary<ActionKey, IReadOnlyList<string>>>(
            () => Compute(endpointProvider(), typeof(TammaApiClient)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Resolves the host's endpoints through BOTH registration shapes and
    /// de-duplicates — the identical union
    /// <c>Tamma.Api.Tests.Actions.GovernanceHostFixture.DiscoverEndpoints</c> takes,
    /// and for the identical reason: resolving the <see cref="EndpointDataSource"/>
    /// singleton and resolving the enumerable are different code paths in different
    /// ASP.NET Core versions, so taking only one means an upgrade can silently halve
    /// what this sees — and an under-counted <c>enforcementSites</c> array reads as
    /// "not enforced anywhere", which is the one thing this type must never say
    /// falsely.
    /// </summary>
    /// <param name="services">The host's service provider.</param>
    public static IEnumerable<Endpoint> DiscoverEndpoints(IServiceProvider services)
    {
        var sources = services.GetServices<EndpointDataSource>().ToList();
        var single = services.GetService<EndpointDataSource>();
        if (single is not null && !sources.Contains(single)) sources.Add(single);

        return sources.SelectMany(s => s.Endpoints).Distinct().ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> For(ActionKey action) =>
        _sites.Value.TryGetValue(action, out var sites) ? sites : [];

    /// <inheritdoc />
    public IReadOnlyDictionary<ActionKey, IReadOnlyList<string>> All() => _sites.Value;

    /// <summary>
    /// THE COMPUTATION, pure over its inputs so the tests drive the real thing
    /// rather than a copy of it.
    /// </summary>
    /// <param name="endpoints">The host's endpoints.</param>
    /// <param name="mediationClient">The mediation client type to reflect for <c>[PerformsEffect]</c>.</param>
    public static FrozenDictionary<ActionKey, IReadOnlyList<string>> Compute(
        IEnumerable<Endpoint> endpoints, Type mediationClient)
    {
        var byAction = new Dictionary<ActionKey, SortedSet<string>>();

        void Add(ActionKey key, string site)
        {
            if (!byAction.TryGetValue(key, out var set))
                byAction[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(site);
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;

            // Story 43-12 — a route may carry MULTIPLE bindings (the merge route
            // binds git.merge.{dev,qa,main} + a per-request selector). Read ALL
            // bindings so every bound key gets this route as a site, not just the
            // last one GetMetadata<T>() would return.
            var gates = endpoint.Metadata.GetOrderedMetadata<IActionGateMetadata>();
            if (gates is null || gates.Count == 0) continue;

            // Normalised exactly as GovernanceHostFixture normalises it: an
            // attribute-routed controller pattern comes back WITHOUT a leading slash
            // ("api/Mentorship/start") while a minimal-API one carries it. Without
            // this the two authoring shapes would render differently in the UI.
            var pattern = route.RoutePattern.RawText ?? "(no-pattern)";
            if (!pattern.StartsWith('/')) pattern = "/" + pattern;

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                foreach (var gate in gates)
                    Add(gate.Action, $"{RoutePrefix}* {pattern}");
                continue;
            }

            foreach (var gate in gates)
                foreach (var method in methods)
                    Add(gate.Action, $"{RoutePrefix}{method.ToUpperInvariant()} {pattern}");
        }

        foreach (var method in mediationClient.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // Story 43-12 — GetCustomAttributes (plural): a method may carry more
            // than one [PerformsEffect] (MergePullRequestAsync performs one of
            // git.merge.{dev,qa,main}). GetCustomAttribute<T> throws on multiples.
            foreach (var performs in method.GetCustomAttributes<PerformsEffectAttribute>(inherit: false))
                Add(performs.Key, $"{MethodPrefix}{mediationClient.FullName}.{method.Name}");
        }

        return byAction.ToFrozenDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToArray());
    }
}
