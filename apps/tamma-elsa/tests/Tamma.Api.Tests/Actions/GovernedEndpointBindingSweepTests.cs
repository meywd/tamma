using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Infrastructure;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 (AC3, D6) — the binding is the RIGHT one, on the route plane.
///
/// <para>Coverage alone is not enough: <c>.Governs(anyExistingMember)</c> silences
/// <c>GovernedEndpointCoverageSweepTests</c> with a WRONG binding — a governed-looking
/// route pointing at an unrelated action, which is worse than an ungoverned route
/// because the admin UI would show it as covered. This harness closes that by
/// requiring the bound descriptor's <c>SiteKey</c> to name the very route the
/// metadata is attached to.</para>
///
/// <para><b>SiteKey shape.</b> Effect descriptors carry
/// <c>"POST /api/engine/events — EngineEndpoints.AppendEvents"</c>: a route part, an
/// em-dash, then the handler. The route part must equal
/// <c>$"{METHOD} {routePattern}"</c> exactly.</para>
///
/// <para><b>WHAT THIS HARNESS CANNOT DO</b> (AC10(b), D6) — <b>the same check is
/// impossible for an attributed C# method.</b> A <c>[PerformsEffect]</c> on a
/// <c>TammaApiClient</c> method has no route pattern to compare against, so nothing
/// verifies that the declared effect is the one the method causes:
/// <c>[PerformsEffect(GitBranchDelete)]</c> on the release-creating method passes
/// everything. The route plane gets a structural check; the method plane gets review
/// of a small enumerable set. Do not read a green run here as covering both.</para>
///
/// <para><b>State (updated 2026-07-30).</b> 21 production routes now carry
/// <see cref="IActionGateMetadata"/>: the 17 mediation routes bound with
/// <c>.Governs</c> in <c>Program.cs</c> and <c>MentorshipController</c>'s four
/// <c>[HttpPost]</c> actions bound with <c>[Governs]</c> (Story 43-8 AC1 steps 2–3,
/// carve-outs §A1 #1/#2). The live assertion therefore has real inputs, and
/// <see cref="TheBindingSweep_hasRealInputs"/> keeps it that way — it replaced the
/// deleted day-one pin <c>NoProductionRouteIsBoundYet_isTheDayOneState</c>, which
/// was removed on its own failure message's instruction.
/// <see cref="Discrimination_aMisBoundRouteIsDetected"/> and its siblings still
/// drive the SAME classifier over a purpose-built <see cref="WebApplication"/>, so
/// the harness remains proven to discriminate independently of what production
/// happens to contain.</para>
///
/// <para><b>This check earned its keep on the first use.</b> Three catalog SiteKeys
/// were PRETTIFIED renderings of their routes rather than the live patterns —
/// <c>{documentId}</c>, and <c>{n}</c> twice — and would have been rejected here the
/// moment their routes were bound. They were corrected in the same change (the wave-4
/// tracker-SiteKey precedent). A SiteKey is compared ORDINALLY against
/// <c>RoutePattern.RawText</c>, constraints included; it is not documentation.</para>
///
/// <para>NAMING: Story 43-8 AC3 calls this <c>GovernedEndpointBindingTests</c>; the
/// <c>…SweepTests</c> suffix is a naming convention shared with the other Epic 43
/// drift harnesses, nothing more.
/// <b>Correction (2026-07-29, conformance round):</b> this note previously justified
/// the suffix by claiming it "makes the epic's CI drift filter
/// (<c>~Drift|~Sweep|~Catalog</c>) select it". <b>No such filter exists.</b> CI runs
/// each test project whole and unfiltered (<c>.github/workflows/ci.yml:248</c>:
/// <c>dotnet test "$proj" --no-build -c Release …</c>, no <c>--filter</c>), and the
/// string appears nowhere in <c>docs/</c>. AC10 makes these doc-comments
/// load-bearing, so a reader must not be told a selection mechanism protects this
/// file when none does. The suffix is fine; the justification was not.</para>
/// </summary>
[TestFixture]
public class GovernedEndpointBindingSweepTests
{
    /// <summary>
    /// THE CHECK, pure over its inputs so the negative tests drive the real thing.
    /// </summary>
    /// <param name="bindings">(HTTP method, route pattern, bound action) triples.</param>
    internal static List<string> Classify(IEnumerable<(string Method, string Pattern, ActionKey Action)> bindings)
    {
        var problems = new List<string>();

        foreach (var (method, pattern, action) in bindings)
        {
            if (!ActionCatalog.TryGet(action, out var descriptor) || descriptor is null)
            {
                problems.Add(
                    $"  {method} {pattern}: bound to '{action.ToWire()}', which is not a catalogued "
                    + "action. Fix the key, or add the catalog member if this is a new capability.");
                continue;
            }

            var expected = $"{method} {pattern}";
            var declaredRoute = RoutePartOf(descriptor.SiteKey);

            if (!string.Equals(declaredRoute, expected, StringComparison.Ordinal))
            {
                problems.Add(
                    $"  {method} {pattern}: bound to '{action.ToWire()}', whose catalog SiteKey names "
                    + $"'{declaredRoute}'. A binding that points at a different site is worse than no "
                    + "binding — it makes the coverage harness green and the admin UI claim this route "
                    + "is governed by an action it does not perform. Bind the route to its OWN catalog "
                    + "member, or correct the descriptor's SiteKey.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The route half of a descriptor <c>SiteKey</c>: everything before the em-dash
    /// separator. A SiteKey with no separator is returned whole (an in-process site
    /// such as <c>Tamma.Activities.LlmCall.Tools.ShellExecuteTool → ProcessStartInfo</c>
    /// will then simply not match any route, which is the correct outcome).
    /// </summary>
    private static string RoutePartOf(string siteKey)
    {
        var separator = siteKey.IndexOf(" — ", StringComparison.Ordinal);
        return separator < 0 ? siteKey : siteKey[..separator];
    }

    // ====================================================================
    // Against the real host
    // ====================================================================

    [Test]
    public void EveryBoundEndpoint_ResolvesInTheCatalog_AndItsSiteKeyMatchesTheRoute()
    {
        var bindings = GovernanceHostFixture.Endpoints
            .Where(f => f.Action is not null)
            .Select(f => (f.Method, f.Pattern, Action: f.Action!.Value));

        var problems = Classify(bindings);

        problems.Should().BeEmpty(
            "every route binding must point at the catalog member whose SiteKey is that route:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // DELETED 2026-07-30: `NoProductionRouteIsBoundYet_isTheDayOneState`. It pinned
    // the day-one fact that zero production routes carried ActionGateMetadata, and
    // its own failure message instructed the first binder to DELETE it rather than
    // raise the number (story 43-8 §A3 step 2: "delete that test in the same commit
    // … its NAME asserts zero bindings, so raising the number would make the name a
    // lie"). Story 43-8 AC1 steps 2–3 landed 21 real bindings, so the test is
    // deleted on its own instruction. What replaces it is not another count pin but
    // a LOWER BOUND with teeth — EveryBoundEndpoint_… now has real inputs, and
    // TheBindingSweep_hasRealInputs below fails if the bound set ever collapses to
    // nothing (which would silently make this whole fixture vacuous again).

    [Test]
    public void TheBindingSweep_hasRealInputs()
    {
        // ANTI-NO-OP TRIPWIRE, and the replacement for the deleted day-one pin.
        // EveryBoundEndpoint_… passes trivially over an empty input set: from
        // 2026-07-29 to 2026-07-30 that is exactly what it did. If bindings ever
        // disappear — a .Governs chain dropped in a merge, a metadata lookup broken
        // by a framework upgrade — this fixture must go RED rather than quiet.
        var bound = GovernanceHostFixture.Endpoints.Where(f => f.Action is not null).ToList();

        bound.Should().HaveCountGreaterThanOrEqualTo(21,
            "21 routes carry a binding as of 2026-07-30: the 17 mediation routes bound with "
            + ".Governs in Program.cs plus MentorshipController's 4 [HttpPost] actions bound with "
            + "[Governs]. Fewer means bindings were lost, or that endpoint metadata discovery "
            + "stopped seeing them — either way EveryBoundEndpoint_… is no longer checking what "
            + "it claims to check. A LOWER bound, not an equality: adding governance to a route "
            + "must never require editing this test.");

        bound.Select(f => f.Kind).Distinct().Should().Contain(
            GovernanceHostFixture.EndpointKind.ControllerAction,
            "the [Governs] ATTRIBUTE path must be exercised by the real host, not only the "
            + "builder path. Nothing else proves that MVC copies an action-method attribute "
            + "implementing IActionGateMetadata into Endpoint.Metadata — the assumption Story "
            + "43-9's filter and ActionEnforcementSites both rest on.");

        bound.Select(f => f.Kind).Distinct().Should().Contain(
            GovernanceHostFixture.EndpointKind.MinimalApi,
            "and the minimal-API builder path likewise");
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — real endpoints, real metadata, real classifier
    // ====================================================================

    /// <summary>
    /// Builds a throwaway <see cref="WebApplication"/>, maps routes with
    /// <c>.Governs(...)</c>, and reads the endpoints back out of the route builder's
    /// data sources. This exercises the WHOLE path — the extension method, the
    /// metadata object, endpoint metadata lookup — not a hand-built fact list.
    /// </summary>
    private static IReadOnlyList<(string Method, string Pattern, ActionKey Action)> BuildAndReadBindings(
        Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();

        return endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Endpoint: e,
                Gate: e.Metadata.GetMetadata<IActionGateMetadata>(),
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(x => x.Gate is not null)
            .SelectMany(x => x.Methods.Select(m =>
                (Method: m.ToUpperInvariant(), Pattern: x.Endpoint.RoutePattern.RawText!, x.Gate!.Action)))
            .ToArray();
    }

    [Test]
    public void Discrimination_theGovernsExtensionActuallyAttachesDiscoverableMetadata()
    {
        // If this fails, every "is it governed?" question in this epic answers
        // "no" for the wrong reason and the coverage harness would be a lie in the
        // other direction.
        var bindings = BuildAndReadBindings(app =>
            app.MapPost("/api/engine/events", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire())));

        bindings.Should().ContainSingle();
        bindings[0].Method.Should().Be("POST");
        bindings[0].Action.ToWire().Should().Be("effect:engine.events.append");
    }

    [Test]
    public void Discrimination_aCorrectlyBoundRouteIsClean()
    {
        var bindings = BuildAndReadBindings(app =>
            app.MapPost("/api/engine/events", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire())));

        Classify(bindings).Should().BeEmpty(
            "the catalog SiteKey for effect:engine.events.append is 'POST /api/engine/events — …', "
            + "which is exactly this route");
    }

    [Test]
    public void Discrimination_aMisBoundRouteIsDetected()
    {
        // THE case this harness exists for: a real route, really annotated, with a
        // real catalog member that belongs to a DIFFERENT site. The coverage harness
        // is perfectly happy with it.
        var bindings = BuildAndReadBindings(app =>
            app.MapPost("/api/engine/events", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.GitBranchDelete.ToWire())));

        var problems = Classify(bindings);

        problems.Should().ContainSingle()
            .Which.Should().Contain("names 'DELETE /api/v1/git/{owner}/{repo}/branches'",
                "a binding pointing at another action's site must fail, otherwise .Governs(anything) "
                + "silences the coverage harness");
    }

    [Test]
    public void Discrimination_aWrongMethodOnTheRightRouteIsDetected()
    {
        // Subtler: right path, wrong verb. The SiteKey carries the method, so this
        // must fail too.
        var bindings = BuildAndReadBindings(app =>
            app.MapPut("/api/engine/events", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire())));

        Classify(bindings).Should().ContainSingle()
            .Which.Should().Contain("PUT /api/engine/events");
    }

    [Test]
    public void Discrimination_anUnknownActionKeyIsDetected()
    {
        var problems = Classify(
        [
            ("POST", "/api/engine/events", new ActionKey(ActionNamespace.Effect, "no.such.effect")),
        ]);

        problems.Should().ContainSingle()
            .Which.Should().Contain("not a catalogued action");
    }
}
