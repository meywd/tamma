using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Api.Infrastructure;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 <b>AC9</b> / D9 (amendment §A1 carve-out #4, closed 2026-07-30) —
/// <c>enforcementSites</c>: the per-action list of concrete bound sites the admin
/// API serialises so the Story 43-7 UI can render <b>"not enforced anywhere yet"</b>
/// for an action that governs nothing.
///
/// <para><b>The honesty this fixture protects.</b> AC9's requirement is not that the
/// field exists — it is that <b>a catalog row with zero enforcement sites must not
/// render as governed</b>. That only holds if (i) the array is computed from the
/// RUNNING host rather than restated by hand, (ii) it is genuinely EMPTY for the
/// ~172 catalog rows that have no site, and (iii) it cannot silently disagree with
/// the drift harness about which routes are bound. All three are asserted
/// below.</para>
///
/// <para><b>Ordering note (why this landed after the bindings, not before).</b> Until
/// 2026-07-30 every action had zero sites, so a UI rendering this field would have
/// shown one uniform "not enforced" state and the field would have been untestable
/// in the interesting direction. It is meaningful now precisely because 21 routes and
/// 17 mediation methods carry real bindings.</para>
/// </summary>
[TestFixture]
public class ActionEnforcementSitesTests
{
    private static IActionEnforcementSites Live =>
        GovernanceHostFixture.Services.GetRequiredService<IActionEnforcementSites>();

    /// <summary>The 19 mediation effects: bound on BOTH the route and method planes.
    /// Story 43-12: the coarse GitPullRequestMerge is retired and replaced by the
    /// per-target trio — all three bind the merge route (multi-binding) and the ONE
    /// MergePullRequestAsync method ([PerformsEffect] × 3), so each has a route AND a
    /// method site. 17 → 19.</summary>
    private static readonly ExternalEffect[] MediationEffects =
    [
        ExternalEffect.EngineEventsAppend, ExternalEffect.EnginePlatformEventsAppend,
        ExternalEffect.EngineDocumentPersist, ExternalEffect.EngineDocumentSetStatus,
        ExternalEffect.EngineChannelOutboxEnqueue, ExternalEffect.LlmCall,
        ExternalEffect.GitBranchCreate, ExternalEffect.GitBranchDelete,
        ExternalEffect.GitPullRequestCreate,
        ExternalEffect.GitMergeDev, ExternalEffect.GitMergeQa, ExternalEffect.GitMergeMain,
        ExternalEffect.GitReleaseCreate, ExternalEffect.GitIssuePatch,
        ExternalEffect.JiraTicketPatch, ExternalEffect.CiTestsTrigger,
        ExternalEffect.AgentDispatchRun, ExternalEffect.NotifySlackQueue,
        ExternalEffect.NotifyEmailSend,
    ];

    /// <summary>The 4 mentorship effects: route plane only (no mediation-client method exists).</summary>
    private static readonly ExternalEffect[] MentorshipEffects =
    [
        ExternalEffect.MentorshipSessionStart, ExternalEffect.MentorshipSessionPause,
        ExternalEffect.MentorshipSessionResume, ExternalEffect.MentorshipSessionCancel,
    ];

    private static ActionKey Key(ExternalEffect effect) =>
        new(ActionNamespace.Effect, effect.ToWire());

    // ====================================================================
    // Against the real host
    // ====================================================================

    [Test]
    public void TheComputation_agreesWithTheDriftHarnessAboutWhichRoutesAreBound()
    {
        // THE SHARED-SEAM ASSERTION. ActionEnforcementSites walks EndpointDataSource
        // in production; GovernanceHostFixture walks it independently for the
        // harnesses. Two reflections over the same host is exactly the drift this
        // epic exists to prevent, so if they ever disagree about which (action,
        // route) pairs exist, this goes red rather than the API and the sweep
        // quietly telling an admin different things.
        var fromHarness = GovernanceHostFixture.Endpoints
            // Story 43-12 — every bound key (the merge route carries three), so a
            // multi-binding route contributes its route as a site for each key.
            .SelectMany(f => f.BoundActions.Select(a => (Value: a, Site: $"{ActionEnforcementSites.RoutePrefix}{f.SiteKey}")))
            .Distinct()
            .OrderBy(x => x.Value.ToWire(), StringComparer.Ordinal)
            .ThenBy(x => x.Site, StringComparer.Ordinal)
            .ToList();

        var fromProduction = Live.All()
            .SelectMany(kv => kv.Value
                .Where(s => s.StartsWith(ActionEnforcementSites.RoutePrefix, StringComparison.Ordinal))
                .Select(s => (kv.Key, Site: s)))
            .OrderBy(x => x.Key.ToWire(), StringComparer.Ordinal)
            .ThenBy(x => x.Site, StringComparer.Ordinal)
            .ToList();

        fromHarness.Should().NotBeEmpty(
            "21 routes carry a binding as of 2026-07-30; an empty harness view means endpoint "
            + "discovery broke and this whole comparison would be vacuous");

        fromProduction.Should().BeEquivalentTo(fromHarness,
            "the API's view of which routes are bound and the drift harness's view are computed "
            + "from the same EndpointDataSource and must never diverge");
    }

    [Test]
    public void EveryMediationEffect_hasBothARouteSiteAndAMethodSite()
    {
        var problems = new List<string>();

        foreach (var effect in MediationEffects)
        {
            var sites = Live.For(Key(effect));

            if (!sites.Any(s => s.StartsWith(ActionEnforcementSites.RoutePrefix, StringComparison.Ordinal)))
                problems.Add($"  effect:{effect.ToWire()}: no route site (the .Governs binding is gone).");

            if (!sites.Any(s => s.StartsWith(ActionEnforcementSites.MethodPrefix, StringComparison.Ordinal)))
                problems.Add($"  effect:{effect.ToWire()}: no method site (the [PerformsEffect] is gone).");
        }

        problems.Should().BeEmpty(
            "each of the 17 mediation effects is bound twice — at the route the engine calls and at "
            + "the TammaApiClient method that calls it. Both planes must appear:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryMentorshipEffect_hasExactlyItsControllerRouteSite()
    {
        // The [Governs] ATTRIBUTE plane end-to-end: catalogued member → attribute on
        // an MVC action → endpoint metadata → API response. Nothing else in the repo
        // exercises that path.
        foreach (var effect in MentorshipEffects)
        {
            var sites = Live.For(Key(effect));
            var descriptor = ActionCatalog.ByKey[Key(effect)];
            var expected = descriptor.SiteKey[..descriptor.SiteKey.IndexOf(" — ", StringComparison.Ordinal)];

            sites.Should().ContainSingle($"effect:{effect.ToWire()} is bound at exactly one site")
                .Which.Should().Be($"{ActionEnforcementSites.RoutePrefix}{expected}",
                    "the reported site must be the very route the descriptor's SiteKey names — the "
                    + "same equality GovernedEndpointBindingSweepTests enforces");
        }
    }

    [Test]
    public void MemberWithNoSite_ReportsEmpty()
    {
        // AC9's load-bearing case. If this ever returns something non-empty for an
        // unbound row, the UI's "not enforced anywhere yet" state stops being
        // reachable and the field becomes decorative.
        Live.For(new ActionKey(ActionNamespace.AgentAction, "deploy")).Should().BeEmpty();
        Live.For(new ActionKey(ActionNamespace.Automation, "channel-outbox-sweeper")).Should().BeEmpty(
            "Seam D (background actors) has no annotation shape yet — an automation:* row honestly "
            + "reports zero sites rather than borrowing credibility from its SiteKey string");
        Live.For(new ActionKey(ActionNamespace.Effect, ExternalEffect.ProcessSpawn.ToWire()))
            .Should().BeEmpty("an in-process effect has no bound site to report");
        Live.For(new ActionKey(ActionNamespace.Effect, "no.such.effect")).Should().BeEmpty();
    }

    [Test]
    public void TheGovernedShareOfTheCatalog_isSmall_andSaysSo()
    {
        // AC9's honesty requirement as a NUMBER, so nobody can read a green suite as
        // "the catalog is enforced". 21 of 197 rows have a site today. This is a
        // LOWER bound on the unbound share — it must not need editing when the next
        // story governs more, but it must fail if the field silently starts claiming
        // sites for rows that have none.
        var withSites = ActionCatalog.All.Count(d => Live.For(d.Key).Count > 0);

        withSites.Should().Be(37,
            "37 catalog rows have a live site (43-17 follow-up: 35 → 37, + the two formerly-unowned engine callbacks ci.workflow.dispatch and llm.task.execute; Story 31-13: 24 → 35, + the 7 PR-lifecycle verbs "
            + "and the 4 formerly-ungoverned issue callbacks, now bound + enforcing): the 19 "
            + "mediation effects (the merge trio git.merge.{dev,qa,main} replaced the coarse merge, "
            + "+2), the 4 mentorship effects, secret.read (42-10), and the 11 PR+issue verbs. Every "
            + "other row reports an empty array and MUST render as 'not enforced anywhere yet'.");

        ActionCatalog.All.Count.Should().BeGreaterThan(withSites * 5,
            "the governed share is a small minority of the catalog. That is the fact AC9 exists to "
            + "put in front of an admin, and it must not be possible to lose it silently.");
    }

    [Test]
    public void RouteSitesAndTheBaseline_partitionTheInScopeSurface()
    {
        // Plan step 12: "the day-one governed-route count matches the
        // KnownUngovernedEndpoints complement, so the two numbers can never drift
        // apart silently". Every in-scope mutating endpoint is EITHER bound OR
        // baselined — never both, never neither.
        var inScope = GovernedEndpointCoverageSweepTests.InScopeEndpoints();

        var bound = inScope.Where(f => f.IsGoverned).Select(f => f.SiteKey)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var baselined = KnownUngovernedEndpoints.BySiteKey.Keys.ToHashSet(StringComparer.Ordinal);

        bound.Overlaps(baselined).Should().BeFalse(
            "a route cannot be both bound and baselined; the coverage sweep's staleness arm should "
            + "have caught it first");

        (bound.Count + baselined.Count).Should().Be(KnownUngovernedEndpoints.PinnedInScopeCount,
            $"bound ({bound.Count}) + baselined ({baselined.Count}) must exhaust the in-scope "
            + $"surface ({KnownUngovernedEndpoints.PinnedInScopeCount}). If this fails the two pins "
            + "were reconciled independently and one of them is wrong.");

        // 43-17 follow-up — 33 → 35: POST /api/engine/trigger-ci and
        // POST /api/engine/execute-task moved baseline→bound. Partition holds:
        // 35 bound + 210 baselined (208 backlog + 2 exceptions) = 245 in-scope.
        // Story 31-13 — 22 → 33: +7 new PR-lifecycle routes and +4 issue callbacks
        // that moved baseline→bound (all born/now enforcing).
        bound.Count.Should().Be(35);

        // Story 43-9 D17, 2026-08-01 — `baselined` is now the UNION of two
        // separately-pinned collections, so it can no longer equal PinnedCount
        // alone. The partition property is unchanged and is what this test exists
        // for; the arithmetic is restated rather than relaxed:
        //   • KnownUngovernedEndpoints.All (216) — the ungoverned BACKLOG, strictly
        //     shrink-only, because "a new ungoverned route is not a reason to raise
        //     the pin, it is the signal the ratchet exists to produce";
        //   • ReviewedUngovernedExceptions (2) — routes that CANNOT be governed
        //     without circularity (the gate-evaluation route, and the route a
        //     person uses to override a gate denial), each named, dated and
        //     reviewed, itself count-pinned and itself shrink-only.
        // Asserting both terms means neither can absorb growth from the other.
        baselined.Count.Should().Be(
            KnownUngovernedEndpoints.PinnedCount
            + KnownUngovernedEndpoints.ExceptionPinHistory[^1]);
        KnownUngovernedEndpoints.All.Count.Should().Be(KnownUngovernedEndpoints.PinnedCount);
        KnownUngovernedEndpoints.ReviewedUngovernedExceptions.Count.Should()
            .Be(KnownUngovernedEndpoints.ExceptionPinHistory[^1]);
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — the computation drives real endpoints
    // ====================================================================

    private static IReadOnlyList<Endpoint> BuildEndpoints(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    [Test]
    public void Discrimination_aBoundRouteProducesASite_andAnUnboundOneDoesNot()
    {
        var endpoints = BuildEndpoints(app =>
        {
            app.MapPost("/api/engine/events", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire()));
            app.MapPost("/api/fixture/unbound", () => Results.Ok());
        });

        var sites = ActionEnforcementSites.Compute(endpoints, typeof(NoAttributesFixtureClient));

        sites.Should().ContainKey(new ActionKey(ActionNamespace.Effect, "engine.events.append"))
            .WhoseValue.Should().Equal("route: POST /api/engine/events");

        sites.Values.SelectMany(v => v).Should().NotContain(s => s.Contains("/api/fixture/unbound"),
            "an endpoint with no ActionGateMetadata contributes no site — otherwise every route in "
            + "the host would look governed");
    }

    [Test]
    public void Discrimination_anAttributedMethodProducesAMethodSite()
    {
        var sites = ActionEnforcementSites.Compute([], typeof(TammaApiClient));

        sites.Should().ContainKey(new ActionKey(ActionNamespace.Effect, "llm.call"))
            .WhoseValue.Should().ContainSingle()
            .Which.Should().Be("method: Tamma.Activities.LlmCall.TammaApiClient.CallLlmAsync");

        sites.Should().HaveCount(26,
            "with no endpoints at all, the ONLY sites are the [PerformsEffect] methods. Story 43-12: "
            + "17 → 19 because MergePullRequestAsync now carries three attributes (git.merge.{dev,qa,"
            + "main}) — one method, three effect KEYS — so the distinct-key count is 19. Story 31-13: "
            + "19 → 26, + the 7 PR-lifecycle verb methods (Close/Reopen/Comment/ReviewComment/"
            + "RequestReviewers/SetLabels/SetDraft), each one method with one distinct effect key.");
    }

    [Test]
    public void Discrimination_aMethodlessEndpointIsReportedAsWildcard()
    {
        // Endpoints that declare no HTTP method accept POST. The coverage sweep
        // records them as "*"; this computation must not silently drop them, or a
        // bound one would report zero sites while being governed.
        var endpoints = BuildEndpoints(app =>
            app.Map("/api/fixture/any-method", () => Results.Ok())
                .Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.LlmCall.ToWire())));

        ActionEnforcementSites.Compute(endpoints, typeof(NoAttributesFixtureClient))
            .Values.SelectMany(v => v)
            .Should().Contain("route: * /api/fixture/any-method");
    }

    /// <summary>A client with no <c>[PerformsEffect]</c> anywhere, so route-plane tests stay isolated.</summary>
    private sealed class NoAttributesFixtureClient
    {
        public Task<bool> DoNothingAsync() => Task.FromResult(true);
    }
}
