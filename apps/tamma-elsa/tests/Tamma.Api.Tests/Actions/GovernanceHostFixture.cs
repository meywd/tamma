using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Infrastructure;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 — the ONE booted <c>Tamma.Api</c> host every route/registration drift
/// harness in this folder reflects over, plus the reflection itself.
///
/// <para><b>Why a booted host and not source analysis</b> (43-8 D1/D2): three of the
/// four registration shapes in this repo are invisible to a syntax walker.
/// <c>app.MapControllers()</c> expands at runtime from
/// <c>[HttpPost]</c>/<c>[Route]</c> attributes; <c>app.MapHub&lt;T&gt;()</c> expands
/// into a hub endpoint plus a protocol <c>/negotiate</c> endpoint that appear in no
/// source file; and <c>PlatformTaskWorker</c> is registered by a
/// <c>TryAddEnumerable</c> inside an extension method with no
/// <c>AddHostedService&lt;&gt;</c> line anywhere. Everything here therefore reads
/// <see cref="EndpointDataSource"/> and the built <see cref="IServiceCollection"/>.</para>
///
/// <para><b>What this fixture CANNOT see</b> — recorded here rather than only in a
/// design doc, because a reader of a PASSING harness must not be misled:</para>
/// <list type="bullet">
///   <item><b>The Elsa engine's HTTP surface.</b> <c>Tamma.ElsaServer</c> calls
///   <c>UseWorkflowsApi()</c> in a DIFFERENT PROCESS. None of its routes are in this
///   host's <see cref="EndpointDataSource"/>, and no harness in this epic sees
///   them.</item>
///   <item><b>The TypeScript intelligence sidecar.</b> Governed only as far as the
///   C# proxy route; everything past the proxy is ungoverned surface with no drift
///   signal.</item>
///   <item><b>Conditional registrations.</b> Endpoints and hosted services behind a
///   configuration branch that is FALSE in the test host are simply absent — this
///   host runs single-user, no Cranl key, alert workers gated off. A route that only
///   exists in SaaS mode is invisible here and ships uncatalogued.</item>
///   <item><b>Effects inside a handler.</b> A binding names a SITE. A new capability
///   grown inside an already-bound handler changes nothing any harness observes.</item>
///   <item><b>Whether the enforcement FILTER is attached</b>, as opposed to the
///   enforcement MARKER. <see cref="EndpointFact.EnforcesGovernance"/> is computed
///   from <c>IGovernanceEnforcementMetadata</c> alone, because ASP.NET Core
///   records endpoint filters as factories rather than as metadata — there is
///   nothing to reflect over. Adversarial review F8 (2026-08-01) closed the
///   dangerous half of that gap in the PRODUCTION code rather than here:
///   <see cref="AutonomyGateEndpointFilter"/> now REFUSES to gate a route carrying
///   no marker (409 <c>ACTION.GATE.MISCONFIGURED</c>), so a route can no longer
///   enforce while this fixture reports it unenforced. The remaining direction —
///   a marker attached WITHOUT the filter, which would read as enforced here while
///   the route gates nothing — is only reachable by hand-writing
///   <c>.WithMetadata(new GovernanceEnforcementMetadata())</c>;
///   <c>.EnforcesGovernance()</c> attaches both and is the only supported
///   spelling. Recorded, not hidden.</item>
/// </list>
/// </summary>
[SetUpFixture]
public class GovernanceHostFixture
{
    private static WebApplicationFactory<Program>? s_factory;
    private static IReadOnlyList<ServiceDescriptor>? s_descriptors;
    private static IReadOnlyList<EndpointFact>? s_endpoints;

    /// <summary>Every discovered endpoint, one fact per (endpoint, HTTP method).</summary>
    public static IReadOnlyList<EndpointFact> Endpoints =>
        s_endpoints ?? throw new InvalidOperationException("GovernanceHostFixture did not run.");

    /// <summary>
    /// The booted host's service provider. Added 2026-07-30 so
    /// <c>ActionEnforcementSitesTests</c> can resolve the PRODUCTION
    /// <c>IActionEnforcementSites</c> off the same host this fixture reflects, and
    /// cross-check it against <see cref="Endpoints"/> — one reflection in
    /// production, one independent view in the harness, and a test that fails if
    /// they ever disagree.
    /// </summary>
    public static IServiceProvider Services =>
        s_factory?.Services ?? throw new InvalidOperationException("GovernanceHostFixture did not run.");

    /// <summary>
    /// The COMPLETE service-descriptor list of the built host — the only view that
    /// sees both awkward hosted-service registration shapes (43-8 D2).
    /// </summary>
    public static IReadOnlyList<ServiceDescriptor> ServiceDescriptors =>
        s_descriptors ?? throw new InvalidOperationException("GovernanceHostFixture did not run.");

    [OneTimeSetUp]
    public void BootHost()
    {
        // ApiTestFixture (the assembly-root [SetUpFixture]) has already started the
        // Postgres containers and pointed the connection-string env vars at them;
        // this factory is a second host over the same containers, the
        // ActionPolicyEndpointsTests shape.
        var captured = new List<ServiceDescriptor>();
        s_factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.DisableAlertHostedServices();
                // Runs AFTER Program.cs's own registrations, so the snapshot is the
                // whole collection. DisableAlertHostedServices only swaps *Options
                // singletons — it removes no IHostedService descriptor, so the
                // registration sweep still sees every actor.
                b.ConfigureServices(services => captured.AddRange(services));
            });

        // Force the full pipeline (endpoint building happens on first request, not
        // at service-provider construction).
        using var client = s_factory.CreateClient();

        s_descriptors = captured;
        s_endpoints = DiscoverEndpoints(s_factory.Services);
    }

    [OneTimeTearDown]
    public void DisposeHost() => s_factory?.Dispose();

    /// <summary>
    /// One (endpoint, HTTP method) pair as the harnesses see it.
    /// </summary>
    /// <param name="Method">Upper-case HTTP method, or <c>"*"</c> when the endpoint declares none.</param>
    /// <param name="Pattern">The raw route pattern (<c>/api/v1/git/{owner}/{repo}/branches</c>).</param>
    /// <param name="Kind">Which registration shape produced it (see <see cref="EndpointKind"/>).</param>
    /// <param name="DisplayName">Endpoint display name, for failure messages only.</param>
    /// <param name="Action">The bound catalog action, when the endpoint carries gate metadata.</param>
    /// <param name="EnforcesGovernance">
    /// Story 43-9 D15 — whether the endpoint separately opted INTO enforcement
    /// (<c>.EnforcesGovernance()</c> for a minimal API, <c>[EnforcesGovernance]</c>
    /// for a controller action). Binding and enforcing are two different facts:
    /// <see cref="IsGoverned"/> says WHICH action a route performs,
    /// this says whether the gate DECIDES it. Defaulted so the many existing
    /// synthetic <c>EndpointFact</c>s in this folder's discrimination tests keep
    /// compiling unchanged.
    /// </param>
    public sealed record EndpointFact(
        string Method,
        string Pattern,
        string Kind,
        string DisplayName,
        ActionKey? Action,
        bool EnforcesGovernance = false,
        IReadOnlyList<ActionKey>? Actions = null)
    {
        /// <summary>The ratchet/baseline key: <c>"POST /api/v1/thing"</c>.</summary>
        public string SiteKey => $"{Method} {Pattern}";

        /// <summary>Whether this endpoint carries an <see cref="IActionGateMetadata"/> binding.</summary>
        public bool IsGoverned => Action is not null;

        /// <summary>
        /// Story 43-12 — ALL bindings on this endpoint (a route may carry more than
        /// one: the merge route binds git.merge.{dev,qa,main}). <see cref="Action"/>
        /// stays the FIRST binding for back-compat; consumers that must see every
        /// bound key (the enforcement-sites agreement check, the binding sweep) read
        /// this. Synthetic facts that pass only <see cref="Action"/> get a
        /// single-element list here.
        /// </summary>
        public IReadOnlyList<ActionKey> BoundActions =>
            Actions ?? (Action is { } a ? new[] { a } : Array.Empty<ActionKey>());
    }

    /// <summary><see cref="EndpointFact.Kind"/> values.</summary>
    public static class EndpointKind
    {
        /// <summary>A minimal-API <c>app.Map*</c> route.</summary>
        public const string MinimalApi = "minimal-api";

        /// <summary>An attribute-routed controller action (invisible to syntax analysis).</summary>
        public const string ControllerAction = "controller-action";

        /// <summary>A SignalR <c>/negotiate</c> endpoint (POST by protocol).</summary>
        public const string SignalRNegotiate = "signalr-negotiate";

        /// <summary>A SignalR hub transport endpoint.</summary>
        public const string SignalRHub = "signalr-hub";
    }

    /// <summary>The four mutating HTTP verbs the coverage harness governs.</summary>
    public static readonly IReadOnlySet<string> MutatingMethods =
        new HashSet<string>(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    private static IReadOnlyList<EndpointFact> DiscoverEndpoints(IServiceProvider services)
    {
        // Union of BOTH resolution shapes. WebApplication composes its route
        // groups into the DI-registered EndpointDataSource, but resolving the
        // singleton and resolving the enumerable are different code paths in
        // different ASP.NET Core versions; taking both and de-duplicating means an
        // upgrade cannot silently halve the sweep.
        var sources = services.GetServices<EndpointDataSource>().ToList();
        var single = services.GetService<EndpointDataSource>();
        if (single is not null && !sources.Contains(single)) sources.Add(single);

        var endpoints = sources.SelectMany(s => s.Endpoints).Distinct().ToList();

        var facts = new List<EndpointFact>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;

            // Attribute-routed controller patterns come back WITHOUT a leading
            // slash ("api/Mentorship/start") while minimal-API ones carry it.
            // Normalise, so a site key is comparable with an ActionDescriptor.SiteKey
            // whichever authoring shape produced the route.
            var pattern = route.RoutePattern.RawText ?? "(no-pattern)";
            if (!pattern.StartsWith('/')) pattern = "/" + pattern;
            // Story 43-12 — read ALL bindings (GetOrderedMetadata), not just the last
            // (GetMetadata). The merge route carries three. The first is the fact's
            // Action (back-compat); BoundActions carries them all.
            var gates = endpoint.Metadata.GetOrderedMetadata<IActionGateMetadata>();
            var gate = gates.Count > 0 ? gates[0] : null;
            var boundActions = gates.Count > 0
                ? gates.Select(g => g.Action).ToArray()
                : null;
            // Story 43-9 D15 — the enforcement opt-in is a SECOND, independent
            // piece of metadata. Both authoring planes implement the same
            // interface (a minimal-API marker record and an MVC filter attribute),
            // so one lookup sees both; that is the whole reason the interface
            // exists rather than the filter being attached inside Governs().
            var enforced = endpoint.Metadata.GetMetadata<IGovernanceEnforcementMetadata>() is not null;
            var kind = ClassifyKind(endpoint, pattern);

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                // NO DECLARED METHOD MEANS EVERY METHOD, POST INCLUDED. Recording
                // these as "*" and letting the coverage rule treat them as in scope
                // is deliberate: the seven endpoints in this shape today are the
                // three health checks and the four SignalR endpoints, and a filter
                // keyed on an explicit POST would have silently dropped all seven —
                // the exact "sweep that reads as coverage" failure this story is
                // about.
                facts.Add(new EndpointFact("*", pattern, kind, endpoint.DisplayName ?? pattern, gate?.Action, enforced, boundActions));
                continue;
            }

            facts.AddRange(methods.Select(m =>
                new EndpointFact(m.ToUpperInvariant(), pattern, kind, endpoint.DisplayName ?? pattern, gate?.Action, enforced, boundActions)));
        }

        return facts;
    }

    private static string ClassifyKind(Endpoint endpoint, string pattern)
    {
        if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            return EndpointKind.ControllerAction;

        // NegotiateMetadata is the framework's own marker on the endpoint MapHub
        // synthesises for the negotiate handshake — a far better discriminator than
        // matching on the "/negotiate" suffix, which an application route could also
        // carry.
        if (endpoint.Metadata.GetMetadata<NegotiateMetadata>() is not null)
            return EndpointKind.SignalRNegotiate;

        // The hub's own transport endpoint carries HubMetadata but NOT
        // NegotiateMetadata — and it declares no HTTP methods at all, so it accepts
        // POST. Recognising it by framework metadata (rather than by a "/hubs/"
        // path prefix) means an application route that happens to live under
        // /hubs cannot inherit the exemption.
        if (endpoint.Metadata.GetMetadata<HubMetadata>() is not null)
            return EndpointKind.SignalRHub;

        return EndpointKind.MinimalApi;
    }
}
