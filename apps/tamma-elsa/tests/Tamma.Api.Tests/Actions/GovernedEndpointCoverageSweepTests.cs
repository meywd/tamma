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
/// <para>NAMING: Story 43-8 AC2 calls this <c>GovernedEndpointCoverageTests</c>; the
/// <c>…SweepTests</c> suffix is a naming convention shared with the other Epic 43
/// drift harnesses.
/// <b>Correction (2026-07-29, conformance round):</b> this note previously said the
/// suffix was needed because "the epic's drift suites are selected in CI by
/// <c>--filter "FullyQualifiedName~Drift|~Sweep|~Catalog"</c>, and a harness that
/// filter misses is a harness that does not run". <b>That filter does not exist.</b>
/// CI runs every test project whole and unfiltered
/// (<c>.github/workflows/ci.yml:248</c>), so this harness runs because it is in
/// <c>tests/Tamma.Api.Tests</c>, not because of its name. AC10 makes these
/// doc-comments load-bearing; a false safety mechanism in one is exactly the kind of
/// claim this epic exists to prevent.</para>
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
    public void TheRatchetPin_IsMechanicallyShrinkOnly()
    {
        // 2026-07-30 — adopted from TemplateExampleConformanceTests'
        // TheRatchetPin_IsMechanicallyShrinkOnly (43-8 follow-up F2) for all four of
        // this story's ratchets. Until now PinnedCount was a bare const compared with
        // HaveCount: nothing mechanically forbade replacing 216 with a larger literal,
        // so "the ratchet only turns one way" was PROSE — the exact defect this
        // story's own plan (D7) cites about ContractBindingTests.cs:255-271. Binding
        // the pin to its recorded history and asserting the history's DIRECTION means
        // raising the pin now requires appending a value that makes this test red.
        KnownUngovernedEndpoints.PinHistory.Should().NotBeEmpty();

        KnownUngovernedEndpoints.PinnedCount.Should().Be(KnownUngovernedEndpoints.PinHistory[^1],
            "the pin IS the last recorded high-water value; changing one without the other is the "
            + "shape of an undeclared re-widening");

        // Review F3 (2026-08-01): the loop below constrains the TAIL of the history
        // and never its HEAD, so [237, 216] could be rewritten as [300, 250] and stay
        // green. The seed is restated in THIS file so re-seeding is a two-file edit.
        var problems = PinHistoryProblems(
            "KnownUngovernedEndpoints.PinHistory",
            BaselinePinSeed,
            KnownUngovernedEndpoints.PinHistory);

        problems.Should().BeEmpty(
            "An entry may only leave this baseline by being GOVERNED or by ceasing to exist. A new "
            + "ungoverned route is not a reason to raise the pin — it is the signal the ratchet "
            + "exists to produce:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
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

    // NOTE (Story 43-9, 2026-08-01) — `PreProvisionedJustificationKeyword_isStillUnused`
    // WAS HERE and is DELETED, as 43-8 §A3 step 3 instructed. It pinned the
    // `gate-evaluation-endpoint-cannot-gate-itself` classifier arm at ZERO uses,
    // because the route it was minted for did not exist yet. 43-9 landed
    // POST /api/v1/governance/evaluate, so the arm is LIVE. 43-8's own failure
    // message said to delete the test rather than widen it — "an unused arm and a
    // used arm are different facts" — and its live use is now asserted by
    // ExceptionSet_entriesAreClassified plus the coverage rule itself.

    // ====================================================================
    // Story 43-9 D17 — the reviewed exception set's own three properties
    // ====================================================================

    /// <summary>
    /// The value <see cref="KnownUngovernedEndpoints.ExceptionPinHistory"/> was
    /// SEEDED at on 2026-08-01, restated HERE — a different file from the array —
    /// so that re-seeding the history is a two-file edit a reviewer sees, not a
    /// one-literal edit nobody does.
    ///
    /// <para><b>Review F3.</b> Without this, the shrink-only loop below is VACUOUS:
    /// the history has ONE element, <c>for (i = 1; i &lt; Length; i++)</c> never
    /// runs, and nothing at all constrained <c>[2]</c> → <c>[3]</c>. Binding the head
    /// makes the property non-vacuous at length 1: the only legal histories are
    /// <c>[2]</c> followed by strictly-decreasing values, so the set can shrink and
    /// cannot grow.</para>
    /// </summary>
    internal const int ExceptionPinSeed = 2;

    /// <summary>
    /// The value <see cref="KnownUngovernedEndpoints.PinHistory"/> was seeded at on
    /// 2026-07-29, restated here for the same reason as
    /// <see cref="ExceptionPinSeed"/>: the shrink-only loop constrains the TAIL of
    /// the history but never its head, so <c>[237, 216]</c> could be rewritten as
    /// <c>[300, 250]</c> and stay green.
    /// </summary>
    internal const int BaselinePinSeed = 237;

    /// <summary>
    /// THE PIN HISTORY RULE, as a pure predicate so the live assertion and the
    /// discrimination proofs drive the SAME rule rather than two copies. A history
    /// is legal iff it is non-empty, starts at its declared seed, and strictly
    /// decreases thereafter.
    /// </summary>
    internal static IReadOnlyList<string> PinHistoryProblems(string name, int seed, int[] history)
    {
        var problems = new List<string>();

        if (history.Length == 0)
        {
            problems.Add($"  {name}: has no pin history — the pin is a bare literal.");
            return problems;
        }

        if (history[0] != seed)
        {
            problems.Add(
                $"  {name}: its history STARTS at {history[0]} but the recorded seed is {seed}. "
                + "A history whose head can move is not a record — it can be silently RE-SEEDED "
                + "at a larger value, and at length 1 the strictly-decreasing loop below never "
                + "executes, so nothing else would notice.");
        }

        for (var i = 1; i < history.Length; i++)
        {
            if (history[i] >= history[i - 1])
            {
                problems.Add(
                    $"  {name}: pin history {history[i - 1]} → {history[i]} is not a decrease. "
                    + "A ratchet that turns both ways is not a ratchet.");
            }
        }

        return problems;
    }

    [Test]
    public void ExceptionSet_countIsPinned_andIsMechanicallyShrinkOnly()
    {
        // The exception set is a ratchet in its own right, with the SAME three
        // properties as the baseline it sits beside. Without this, "a named
        // exception" would be a place to put anything.
        KnownUngovernedEndpoints.ExceptionPinHistory.Should().NotBeEmpty();

        KnownUngovernedEndpoints.ReviewedUngovernedExceptions.Should()
            .HaveCount(KnownUngovernedEndpoints.ExceptionPinHistory[^1],
                "the exception set is count-pinned. Seeded at 2 on 2026-08-01 by Story 43-9: the "
                + "gate-evaluation route and the authorization-decide route, both ungoverned by "
                + "CIRCULARITY rather than by backlog. A third entry must be argued for.");

        // Review F3: head-binding is what makes the shrink-only property mean
        // something at length 1. Before it, [2] → [3] was a ONE-LITERAL edit and
        // every test in this fixture stayed green.
        var problems = PinHistoryProblems(
            "KnownUngovernedEndpoints.ExceptionPinHistory",
            ExceptionPinSeed,
            KnownUngovernedEndpoints.ExceptionPinHistory);

        problems.Should().BeEmpty(
            "an exception set that can grow without a decrease is an escape hatch, which is "
            + "precisely what D17's shape requirements forbid:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ExceptionSet_membershipIsPinnedByRoute()
    {
        // Review F3. A COUNT pin cannot tell "an exception was added" from "a
        // backlog entry was relabelled an exception while the backlog pin dropped"
        // — the reviewer's mutation was exactly the second, and it read as
        // governance PROGRESS. Pinning the MEMBERSHIP, in this file rather than in
        // the file that holds the list, makes admitting a route a change a reviewer
        // reads as a sentence rather than as a number.
        KnownUngovernedEndpoints.ReviewedUngovernedExceptions
            .Select(e => $"{e.Method} {e.Pattern}")
            .Should().BeEquivalentTo(
                new[]
                {
                    "POST /api/v1/governance/evaluate",
                    "POST /api/actions/authorizations/{id:guid}/decide",
                },
                "these two routes, and only these two, are ungoverned by CIRCULARITY. Admitting a "
                + "third is a governance decision: name it here, argue the circularity in its "
                + "justification, and do NOT pay for it by dropping the backlog pin.");
    }

    [Test]
    public void ExceptionSet_entriesAreDatedAndAttributed()
    {
        // A "named, dated, reviewed" exception that carries no date and names no
        // reviewer is just a second baseline. Assert the metadata is really there.
        //
        // Review F3: the classifier here is the STRICTER exception classifier, not
        // IsClassified. Requiring "the SAME classifier as the baseline" was the
        // blanket escape hatch — every one of the 216 backlog entries passes it by
        // construction, so a verbatim copy of a backlog justification bought an
        // exception.
        var problems = KnownUngovernedEndpoints.ReviewedUngovernedExceptions
            .Where(e => !DateOnly.TryParse(e.AddedOn, out _)
                        || string.IsNullOrWhiteSpace(e.Story)
                        || !KnownUngovernedEndpoints.IsExceptionJustified(e.Justification))
            .Select(e => $"  {e.Method} {e.Pattern}: addedOn='{e.AddedOn}' story='{e.Story}'")
            .ToList();

        problems.Should().BeEmpty(
            "every exception must carry an ISO date, the reviewing story id, and a justification "
            + "that passes the STRICTER exception classifier — it must argue the CIRCULARITY that "
            + "is the whole reason this set exists, not merely classify as the baseline does:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // ====================================================================
    // Review F3 — the exception classifier must be STRICTLY stronger than
    // the baseline classifier, proved against the live backlog
    // ====================================================================

    [Test]
    public void Discrimination_noBacklogJustification_wouldSatisfyTheExceptionClassifier()
    {
        // THE F3 PROOF. The reviewer moved DELETE /api/acceptance-rules/{documentTypeKey}
        // into the exception set with its justification COPIED VERBATIM, and 41/41
        // tests passed. Every backlog justification must therefore be REJECTED as an
        // exception justification — otherwise "reviewed exception" is just a second
        // name for "backlog entry", and the two pins can be traded against each
        // other while the diff reads as progress.
        var launderable = KnownUngovernedEndpoints.All
            .Where(e => KnownUngovernedEndpoints.IsExceptionJustified(e.Justification))
            .Select(e => $"  {e.Method} {e.Pattern}: '{e.Justification}'")
            .ToList();

        launderable.Should().BeEmpty(
            $"{launderable.Count} of {KnownUngovernedEndpoints.All.Count} backlog justifications "
            + "would buy a D17 reviewed exception verbatim. An exception exists for a route that "
            + "CANNOT be governed because gating it is circular; a backlog entry exists for a "
            + "route nobody has got to yet. If a backlog line satisfies the exception classifier, "
            + "the classifier is not doing the one job it has:"
            + Environment.NewLine + string.Join(Environment.NewLine, launderable));
    }

    [Test]
    public void Discrimination_theExceptionClassifierIsStrictlyStronger_notMerelyDifferent()
    {
        // Both directions, so the fix cannot be "a different classifier that happens
        // to reject the backlog": everything the exception classifier accepts must
        // also classify as the baseline, and the placeholders must still be rejected.
        foreach (var e in KnownUngovernedEndpoints.ReviewedUngovernedExceptions)
        {
            KnownUngovernedEndpoints.IsClassified(e.Justification).Should().BeTrue(
                "an exception justification is a baseline justification PLUS an argument, never "
                + $"something outside the vocabulary: '{e.Justification}'");
        }

        foreach (var placeholder in new[] { "", "   ", "TODO", "legacy", "circular" })
        {
            KnownUngovernedEndpoints.IsExceptionJustified(placeholder).Should().BeFalse(
                $"'{placeholder}' must not buy an exception — in particular the bare word "
                + "'circular' must not, or the new requirement degenerates into a magic word");
        }
    }

    [Test]
    public void Discrimination_aReSeededPinHistoryIsReported()
    {
        // The arm ExceptionSet_countIsPinned_andIsMechanicallyShrinkOnly relies on.
        // Drive the REAL rule with the reviewer's exact edit — [2] re-seeded to [3].
        PinHistoryProblems("fixture", 2, [3]).Should().ContainSingle()
            .Which.Should().Contain("RE-SEEDED",
                "a length-1 history re-seeded upward is precisely the F3 mutation; if the rule "
                + "stays silent here the shrink-only property is vacuous");

        PinHistoryProblems("fixture", 2, [2]).Should().BeEmpty(
            "the complement: the seeded history itself must be legal, or the rule is just always "
            + "red and proves nothing");

        PinHistoryProblems("fixture", 2, [2, 1]).Should().BeEmpty(
            "shrinking is the direction the ratchet is FOR");

        PinHistoryProblems("fixture", 2, [2, 3]).Should().ContainSingle()
            .Which.Should().Contain("is not a decrease");

        PinHistoryProblems("fixture", 2, []).Should().ContainSingle()
            .Which.Should().Contain("bare literal");
    }

    [Test]
    public void ExceptionSet_doesNotOverlapTheBaseline()
    {
        // An entry in both would be counted once by BySiteKey and twice by the two
        // pins — a way to inflate one number while the other hides it.
        var baseline = KnownUngovernedEndpoints.All
            .Select(e => $"{e.Method} {e.Pattern}").ToHashSet(StringComparer.Ordinal);

        var overlap = KnownUngovernedEndpoints.ReviewedUngovernedExceptions
            .Where(e => baseline.Contains($"{e.Method} {e.Pattern}"))
            .Select(e => $"  {e.Method} {e.Pattern}")
            .ToList();

        overlap.Should().BeEmpty(
            "a route belongs to the backlog baseline OR to the reviewed exception set, never both:"
            + Environment.NewLine + string.Join(Environment.NewLine, overlap));
    }

    [Test]
    public void ExceptionSet_routesStillExistAndAreStillUnbound()
    {
        // Staleness, both ways (D17(5)). The generic staleness arm in Classify()
        // already covers these because BySiteKey unions them in, but that arm
        // reports them as BASELINE entries; this one names the exception set, so a
        // failure points at the file that has to change.
        var live = InScopeEndpoints().ToLookup(f => f.SiteKey, StringComparer.Ordinal);

        var problems = KnownUngovernedEndpoints.ReviewedUngovernedExceptions
            .Select(e =>
            {
                var key = $"{e.Method} {e.Pattern}";
                if (!live[key].Any())
                    return $"  {key}: a reviewed exception for a route that no longer exists — DELETE it.";
                if (live[key].All(f => f.IsGoverned))
                    return $"  {key}: a reviewed exception for a route that is now BOUND — DELETE it.";
                return null;
            })
            .Where(m => m is not null).Select(m => m!).ToList();

        problems.Should().BeEmpty(
            "the exception set must drain like any other ratchet:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The exception set's staleness probe for the meta-test: drives the REAL
    /// coverage classifier with an exception-shaped entry whose route is now bound.
    /// </summary>
    internal static IReadOnlyList<string> ExceptionRatchetStalenessProbe()
    {
        var key = new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire());
        var (_, stale) = Classify(
            [Fact("POST", "/api/fixture/exception-probe", key)],
            Baseline(new KnownUngovernedEndpoints.Entry(
                "POST", "/api/fixture/exception-probe",
                "gate-evaluation-endpoint-cannot-gate-itself: fixture")));
        return stale;
    }

    [Test]
    public void Baseline_justificationsAreClassified()
    {
        var unclassified = KnownUngovernedEndpoints.All
            .Concat(KnownUngovernedEndpoints.ExceptionsAsEntries)
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
    // Ratchet-discipline surface (Story 43-8 AC8, carve-out §A1 #5) — the seam
    // RatchetDisciplineTests reads, so the meta-test can assert this ratchet has
    // all three properties by driving the REAL classifier, not a description of it.
    // ====================================================================

    /// <summary>
    /// Drives the REAL <see cref="Classify"/> with a baselined endpoint that is now
    /// bound — the stale case. Non-empty output is the proof that the staleness arm
    /// exists and fires.
    /// </summary>
    internal static IReadOnlyList<string> RatchetStalenessProbe()
    {
        var key = new ActionKey(ActionNamespace.Effect, ExternalEffect.EngineEventsAppend.ToWire());
        var (_, stale) = Classify(
            [Fact("POST", "/api/fixture/ratchet-probe", key)],
            Baseline(new KnownUngovernedEndpoints.Entry(
                "POST", "/api/fixture/ratchet-probe", "human-operated: fixture")));
        return stale;
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
