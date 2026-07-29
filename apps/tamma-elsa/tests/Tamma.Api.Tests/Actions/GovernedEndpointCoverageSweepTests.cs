using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Infrastructure;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 (AC2, AC8, AC11 — D4/D5/D7/D8) — the load-bearing route harness:
/// EVERY mutating endpoint of the booted <c>Tamma.Api</c> host must carry an
/// <see cref="ActionGateMetadata"/> binding or an entry in the shrink-only,
/// count-pinned <see cref="KnownUngovernedEndpoints"/> baseline.
///
/// <para><b>Scope is ALL mutating endpoints, not a convenient subset</b> (D4).
/// Restricting the sweep to the ~17 <c>EngineServiceOnly</c> mediation routes would
/// put the other ~200 outside the harness BY CONSTRUCTION, and the harness would
/// pass forever while the surface it claims to guard grew. The cost is a large
/// day-one baseline — honest, visible and ratcheted — instead of an invisible
/// gap.</para>
///
/// <para><b>Controller actions and SignalR endpoints are IN SCOPE</b> (D5).
/// <c>MentorshipController</c>'s <c>[HttpPost]</c> actions and the two
/// <c>MapHub</c> registrations produce endpoints that no source grep can see; they
/// are discovered here because the sweep reads
/// <c>EndpointDataSource</c> off a booted host.
/// <see cref="ControllerActions_AreInScope"/> and
/// <see cref="SignalREndpoints_AreDiscovered"/> pin that, so nobody can quietly
/// re-introduce a "skip controllers" exemption. The SignalR handshake endpoints are
/// exempted as a NAMED, COUNT-PINNED class — a third hub fails the build and forces
/// a decision — never by a wildcard.</para>
///
/// <para><b>The ratchet has all three properties</b> (D7). (a) staleness: a
/// baselined endpoint that is now bound, or that no longer exists, fails;
/// (b) justification classification: a placeholder string cannot buy an entry;
/// (c) a COUNT PIN, so an ADDITION fails. (c) is not decoration —
/// <c>ContractBindingTests.cs:255-271</c> documents shrink-only as prose with no
/// assertion behind it, and additions there are undetectable.</para>
///
/// <para><b>WHAT THIS HARNESS CANNOT SEE</b> — see also
/// <see cref="GovernanceHostFixture"/>, which lists the host-level blind spots
/// (the out-of-process Elsa workflow API, the TypeScript sidecar, config-conditional
/// routes). Two more belong here:</para>
/// <list type="bullet">
///   <item><b>A binding proves a SITE, not an EFFECT.</b> A new capability grown
///   inside an already-bound handler passes.</item>
///   <item><b>Enforcement is test-time, not build-time</b> (AC10(f), D1). A
///   developer who skips <c>dotnet test</c> can push an ungoverned route and only
///   learn on CI. That cost was accepted in place of a Roslyn analyzer with a
///   measured ~40% structural miss rate.</item>
/// </list>
///
/// <para>NAMING: Story 43-8 AC2 calls this <c>GovernedEndpointCoverageTests</c>. The
/// <c>…SweepTests</c> suffix is deliberate — the epic's drift suites are selected in
/// CI by <c>--filter "FullyQualifiedName~Drift|~Sweep|~Catalog"</c>, and a harness
/// that harness-filter misses is a harness that does not run.</para>
/// </summary>
[TestFixture]
public class GovernedEndpointCoverageSweepTests
{
    /// <summary>
    /// GET endpoints that are governed despite being non-mutating, named
    /// individually (never by a pattern). Today: the secret reveal, which is
    /// catalogued as <c>effect:secret.reveal</c> — informational-only and never
    /// enforceable, but catalogued, so the harness must account for it.
    /// </summary>
    private static readonly IReadOnlySet<string> GovernedGetEndpoints =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "GET /api/v1/secrets/reveal/{token}",
        };

    /// <summary>
    /// The ONLY exemption, and it is a named class rather than a rule: SignalR's
    /// handshake/transport endpoints are POST by protocol and carry no application
    /// effect. Count-pinned in <see cref="ExemptEndpointClass_isCountPinned"/>.
    /// </summary>
    private static bool IsExempt(GovernanceHostFixture.EndpointFact fact) =>
        fact.Kind is GovernanceHostFixture.EndpointKind.SignalRNegotiate
                  or GovernanceHostFixture.EndpointKind.SignalRHub;

    /// <summary>
    /// Every endpoint the coverage rule applies to: the four mutating verbs, PLUS
    /// endpoints that declare NO method (they accept POST — dropping them would have
    /// silently excluded seven endpoints), PLUS the explicitly named governed GETs.
    /// </summary>
    internal static IReadOnlyList<GovernanceHostFixture.EndpointFact> InScopeEndpoints() =>
        GovernanceHostFixture.Endpoints
            .Where(f => GovernanceHostFixture.MutatingMethods.Contains(f.Method)
                        || f.Method == "*"
                        || GovernedGetEndpoints.Contains(f.SiteKey))
            .Where(f => !IsExempt(f))
            .ToArray();

    /// <summary>
    /// THE RULE, pure over its inputs so the discrimination tests drive the real
    /// classifier rather than a copy of it.
    /// </summary>
    internal static (List<string> Ungoverned, List<string> Stale) Classify(
        IReadOnlyList<GovernanceHostFixture.EndpointFact> inScope,
        IReadOnlyDictionary<string, KnownUngovernedEndpoints.Entry> baseline)
    {
        var ungoverned = inScope
            .Where(f => !f.IsGoverned && !baseline.ContainsKey(f.SiteKey))
            .Select(f => f.SiteKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k =>
                $"  {k}: a mutating endpoint with no governance decision. Either bind it — "
                + ".Governs(actionKey) for a minimal API, [Governs(ns, key)] for a controller action — "
                + "or add a justified KnownUngovernedEndpoints entry AND bump the count pin in the "
                + "same commit. Do not delete the pin.")
            .ToList();

        var live = inScope.ToLookup(f => f.SiteKey, StringComparer.Ordinal);

        var stale = baseline.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(key => !live[key].Any()
                ? $"  {key}: baselined as ungoverned, but no such in-scope endpoint exists any more — "
                  + "DELETE the entry and decrement the count pin."
                : live[key].All(f => f.IsGoverned)
                    ? $"  {key}: baselined as ungoverned, but the endpoint now carries a binding — "
                      + "DELETE the entry and decrement the count pin (the ratchet only turns one way)."
                    : null)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        return (ungoverned, stale);
    }

    // ====================================================================
    // Anti-no-op tripwires
    // ====================================================================

    [Test]
    public void The_sweep_actually_sees_endpoints()
    {
        // If endpoint discovery ever returns nothing (the pipeline not built, the
        // data-source resolution changed across an ASP.NET Core upgrade), every
        // assertion in this fixture would pass vacuously.
        GovernanceHostFixture.Endpoints.Should().HaveCountGreaterThan(200,
            "the Tamma.Api host maps several hundred endpoints; an empty or tiny result means "
            + "discovery broke, not that the API shrank");

        InScopeEndpoints().Should().HaveCountGreaterThan(150,
            "Program.cs alone contains 200+ mutating Map* calls");
    }

    [Test]
    public void ControllerActions_AreInScope()
    {
        // REGRESSION PIN against re-introducing a "skip controllers" exemption.
        // MentorshipController is the repo's only controller; its POST actions are
        // invisible to syntax analysis and must appear in the sweep.
        var controllerPosts = GovernanceHostFixture.Endpoints
            .Where(f => f.Kind == GovernanceHostFixture.EndpointKind.ControllerAction
                        && GovernanceHostFixture.MutatingMethods.Contains(f.Method))
            .ToList();

        controllerPosts.Should().NotBeEmpty(
            "MentorshipController's [HttpPost] actions must be discovered as endpoints — they are one "
            + "of the two surfaces a Roslyn analyzer could never see, and the reason this harness "
            + "boots the app");

        var inScopeKeys = InScopeEndpoints().Select(f => f.SiteKey).ToHashSet(StringComparer.Ordinal);
        controllerPosts.Select(f => f.SiteKey).Should().OnlyContain(k => inScopeKeys.Contains(k),
            "controller actions are governed like anything else — the annotation exists ([Governs]), "
            + "so there is no need for an exemption");
    }

    [Test]
    public void SignalREndpoints_AreDiscovered()
    {
        // The second surface no syntax walker sees: MapHub expands into endpoints
        // that appear in no source file.
        GovernanceHostFixture.Endpoints
            .Where(f => f.Kind is GovernanceHostFixture.EndpointKind.SignalRNegotiate
                                or GovernanceHostFixture.EndpointKind.SignalRHub)
            .Should().NotBeEmpty(
                "app.MapHub<OrchestratorChannelHub>/<UserChannelHub> must be visible to the sweep; "
                + "if this is empty the classifier stopped recognising SignalR metadata and those "
                + "endpoints are silently unclassified");
    }

    [Test]
    public void ExemptEndpointClass_isCountPinned()
    {
        // A NAMED, COUNT-PINNED exemption class (D5). Adding a third hub fails here
        // and forces a decision, which a wildcard exemption would not.
        GovernanceHostFixture.Endpoints
            .Count(f => f.Kind == GovernanceHostFixture.EndpointKind.SignalRNegotiate)
            .Should().Be(2,
                "two hubs are mapped (/hubs/orchestrator, /hubs/user), so exactly two negotiate "
                + "endpoints are exempt. A third means a new hub arrived: decide whether its surface "
                + "needs governing before bumping this number.");

        GovernanceHostFixture.Endpoints
            .Count(f => f.Kind == GovernanceHostFixture.EndpointKind.SignalRHub)
            .Should().Be(2,
                "each MapHub also produces a transport endpoint that declares NO HTTP method — it "
                + "accepts POST. Those two are exempt for the same reason as negotiate, and pinned "
                + "for the same reason: a third hub must be a decision, not a default.");
    }

    // ====================================================================
    // The coverage rule
    // ====================================================================

    [Test]
    public void EveryMutatingEndpoint_IsGovernedOrJustified()
    {
        var (ungoverned, stale) = Classify(InScopeEndpoints(), KnownUngovernedEndpoints.BySiteKey);

        ungoverned.Should().BeEmpty(
            "every mutating endpoint must carry a governance decision:"
            + Environment.NewLine + string.Join(Environment.NewLine, ungoverned));

        stale.Should().BeEmpty(
            "KnownUngovernedEndpoints must list ONLY endpoints that exist and are still unbound:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void GovernedGetEndpoints_AreInScope()
    {
        // A named GET is only governed if it is actually still there.
        var live = GovernanceHostFixture.Endpoints.Select(f => f.SiteKey).ToHashSet(StringComparer.Ordinal);
        var missing = GovernedGetEndpoints.Where(k => !live.Contains(k)).ToList();

        missing.Should().BeEmpty(
            "these GETs are named as governed but no longer exist as endpoints — remove them from "
            + "GovernedGetEndpoints (or restore the route): " + string.Join(", ", missing));
    }

    [Test]
    public void InScopeEndpointCount_isPinned()
    {
        // Correction 4: derive the number at runtime, then PIN it — so the sweep
        // cannot silently stop seeing endpoints, and so every added route is a
        // visible diff that asks "should this be governed?".
        InScopeEndpoints().Select(f => f.SiteKey).Distinct(StringComparer.Ordinal)
            .Should().HaveCount(KnownUngovernedEndpoints.PinnedInScopeCount,
                "the in-scope mutating surface is pinned. A change means routes were added or "
                + "removed: reconcile KnownUngovernedEndpoints in the same commit.");
    }

    // ====================================================================
    // Ratchet discipline (AC8) — all three properties, asserted
    // ====================================================================

    [Test]
    public void Baseline_countIsPinned()
    {
        KnownUngovernedEndpoints.All.Should().HaveCount(KnownUngovernedEndpoints.PinnedCount,
            "the ungoverned backlog may only SHRINK. A larger number is new ungoverned surface and "
            + "must be a deliberate, reviewed change; a smaller one means an endpoint was governed, "
            + "which is the point.");
    }

    [Test]
    public void Baseline_hasNoDuplicateSiteKeys()
    {
        var duplicates = KnownUngovernedEndpoints.All
            .GroupBy(e => $"{e.Method} {e.Pattern}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"  {g.Key} ×{g.Count()}")
            .ToList();

        duplicates.Should().BeEmpty(
            "a duplicated baseline entry inflates the count pin and hides a real addition:"
            + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
    }

    [Test]
    public void PreProvisionedJustificationKeyword_isStillUnused()
    {
        // Review F18(b). `gate-evaluation-endpoint-cannot-gate-itself` is a
        // classifier arm with ZERO uses: AC11 names it for the route Story 43-9 adds
        // (POST /api/v1/governance/evaluate), which does not exist yet. A vocabulary
        // arm nothing classifies is normally dead weight; this one is kept
        // deliberately, so the fact that it is pre-provisioned is ASSERTED rather
        // than left as a comment a reader has to trust.
        var uses = KnownUngovernedEndpoints.All
            .Where(e => e.Justification.Contains(
                "gate-evaluation-endpoint-cannot-gate-itself", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"  {e.Method} {e.Pattern}")
            .ToList();

        uses.Should().BeEmpty(
            "measured 2026-07-29: 0 uses, because the gate-evaluation route arrives with Story 43-9. "
            + "When 43-9 lands it, DELETE this test (the arm is live from then on) rather than "
            + "widening it — an unused arm and a used arm are different facts:"
            + Environment.NewLine + string.Join(Environment.NewLine, uses));
    }

    [Test]
    public void Baseline_justificationsAreClassified()
    {
        var unclassified = KnownUngovernedEndpoints.All
            .Where(e => !KnownUngovernedEndpoints.IsClassified(e.Justification))
            .Select(e => $"  {e.Method} {e.Pattern}: '{e.Justification}'")
            .ToList();

        unclassified.Should().BeEmpty(
            "every justification must classify as one of ["
            + string.Join(", ", KnownUngovernedEndpoints.JustificationKeywords)
            + "] — a bare placeholder must not buy an entry into the ratchet:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — the rule must FAIL on ungoverned input
    // ====================================================================

    private static GovernanceHostFixture.EndpointFact Fact(
        string method, string pattern, ActionKey? action = null) =>
        new(method, pattern, GovernanceHostFixture.EndpointKind.MinimalApi, $"HTTP: {method} {pattern}", action);

    private static IReadOnlyDictionary<string, KnownUngovernedEndpoints.Entry> Baseline(
        params KnownUngovernedEndpoints.Entry[] entries) =>
        entries.ToDictionary(e => $"{e.Method} {e.Pattern}", e => e, StringComparer.Ordinal);

    [Test]
    public void Discrimination_anUngovernedUnbaselinedEndpointIsReported()
    {
        var (ungoverned, _) = Classify([Fact("POST", "/api/fixture/thing")], Baseline());

        ungoverned.Should().ContainSingle()
            .Which.Should().Contain("POST /api/fixture/thing",
                "a new mutating route with no binding and no baseline entry MUST fail — if it does "
                + "not, this harness reads as coverage while covering nothing");
    }

    [Test]
    public void Discrimination_aBaselinedEndpointIsSuppressed()
    {
        var (ungoverned, stale) = Classify(
            [Fact("POST", "/api/fixture/thing")],
            Baseline(new KnownUngovernedEndpoints.Entry("POST", "/api/fixture/thing", "human-operated: fixture")));

        ungoverned.Should().BeEmpty();
        stale.Should().BeEmpty();
    }

    [Test]
    public void Discrimination_aBaselinedButNowBoundEndpointIsStale()
    {
        var key = new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire());

        var (_, stale) = Classify(
            [Fact("POST", "/api/fixture/thing", key)],
            Baseline(new KnownUngovernedEndpoints.Entry("POST", "/api/fixture/thing", "human-operated: fixture")));

        stale.Should().ContainSingle()
            .Which.Should().Contain("now carries a binding",
                "the baseline must drain as endpoints get governed, or it becomes a snapshot nobody "
                + "revisits");
    }

    [Test]
    public void Discrimination_aBaselineEntryForADeletedEndpointIsStale()
    {
        var (_, stale) = Classify(
            [Fact("POST", "/api/fixture/thing")],
            Baseline(
                new KnownUngovernedEndpoints.Entry("POST", "/api/fixture/thing", "human-operated: fixture"),
                new KnownUngovernedEndpoints.Entry("DELETE", "/api/fixture/gone", "human-operated: fixture")));

        stale.Should().ContainSingle()
            .Which.Should().Contain("no such in-scope endpoint exists");
    }

    [Test]
    public void Discrimination_theJustificationClassifierRejectsPlaceholders()
    {
        KnownUngovernedEndpoints.IsClassified("TODO").Should().BeFalse();
        KnownUngovernedEndpoints.IsClassified("").Should().BeFalse();
        KnownUngovernedEndpoints.IsClassified("   ").Should().BeFalse();
        KnownUngovernedEndpoints.IsClassified("legacy").Should().BeFalse();
        KnownUngovernedEndpoints.IsClassified("human-operated: admin console").Should().BeTrue();
    }

    [Test]
    public void Discrimination_theScopeFilterExcludesReads_andIncludesEveryMutatingVerb()
    {
        // Guards the other direction: a scope filter that quietly dropped PATCH or
        // DELETE would make the whole harness look green.
        GovernanceHostFixture.MutatingMethods.Should().BeEquivalentTo(
            new[] { "POST", "PUT", "PATCH", "DELETE" });

        var methodsInScope = InScopeEndpoints()
            .Select(f => f.Method)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        methodsInScope.Should().Contain(["POST", "PUT", "PATCH", "DELETE"],
            "all four mutating verbs must actually reach the rule");
        methodsInScope.Where(m => !GovernanceHostFixture.MutatingMethods.Contains(m))
            .Should().OnlyContain(m => m == "GET" || m == "*",
                "the only non-mutating entries in scope are the explicitly named governed GETs and "
                + "the method-less endpoints, which accept POST");
    }
}
