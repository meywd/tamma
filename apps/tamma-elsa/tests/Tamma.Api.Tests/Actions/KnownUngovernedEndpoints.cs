namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 (AC11, D7) — the RATCHET BASELINE for
/// <see cref="GovernedEndpointCoverageSweepTests"/>: every mutating endpoint of the
/// <c>Tamma.Api</c> host that carries no catalog binding today, each with a written
/// reason.
///
/// <para><b>This is a snapshot with a one-way valve, not an escape hatch.</b> Three
/// mechanisms, all asserted in <see cref="GovernedEndpointCoverageSweepTests"/>:</para>
/// <list type="number">
///   <item><b>Count pin</b> (<see cref="PinnedCount"/>) — an ADDITION fails the
///   build. This is the property that matters on day one: the backlog is large, but
///   it cannot GROW without a reviewer seeing a number change and asking "should
///   this new route be governed?".</item>
///   <item><b>Staleness</b> — an entry whose endpoint is now bound, or no longer
///   exists, fails until deleted. The baseline therefore drains as governance
///   lands, instead of rotting into a list nobody rereads.</item>
///   <item><b>Justification classification</b> — a placeholder ("TODO", "legacy",
///   "") cannot buy an entry.</item>
/// </list>
///
/// <para><b>Honesty about what this baseline means</b> (AC9). When 43-8's harnesses
/// first landed (2026-07-29) every in-scope endpoint was in here: ZERO routes were
/// bound. As of <b>2026-07-30</b> that is no longer true — 21 routes carry a real
/// binding (the 17 mediation routes plus <c>MentorshipController</c>'s four
/// <c>[HttpPost]</c> actions), so the numbers are <b>237 in scope, 216 baselined,
/// 21 bound</b>. The remaining honesty requirement is unchanged and is now
/// MACHINERY, not prose: <c>ActionEnforcementSites</c> computes the enforcement
/// sites per action from this same endpoint metadata and the admin API serialises
/// them as <c>enforcementSites</c>, so a catalog row with an EMPTY array must render
/// as "not enforced anywhere yet" rather than as governed. <b>Updated 2026-08-01
/// (Story 43-9 D15):</b> a binding is STILL metadata — <c>.Governs(key)</c> never
/// enforces. Enforcement is a separate per-route opt-in
/// (<c>.EnforcesGovernance()</c> / <c>[EnforcesGovernance]</c>), so "bound" and
/// "enforced" are two different populations and
/// <c>GovernedEndpointEnforcementSweepTests</c> pins the second EXACTLY. Today: 16
/// of the 21 bound routes enforce; <c>POST /api/v1/llm/call</c> is bound and
/// deliberately never enforces (Seam A); the four <c>MentorshipController</c>
/// actions are bound and not yet opted in.</para>
///
/// <para><b>TWO shrink-only collections live here, not one</b> (Story 43-9 D17):
/// <see cref="All"/> is the ungoverned BACKLOG — routes that should eventually be
/// governed — and <see cref="ReviewedUngovernedExceptions"/> is the set of routes
/// that CANNOT be governed without circularity. Keeping them apart is what lets the
/// backlog pin stay strictly-decreasing while a genuinely necessary new ungoverned
/// route is still representable, named and dated.</para>
///
/// <para><b>Justifications are grouped by route family, deliberately.</b> Writing
/// ~230 individually-reasoned sentences would produce ~230 paraphrases of the same
/// four facts; grouping by family states the actual reason once and keeps the review
/// tractable. The classifier's floor is that every entry names a family and a
/// reason.</para>
///
/// <para><b>…but grouping must not hide a risk class</b> (review F15, 2026-07-29).
/// Family grouping is a readability device, not a licence to file a high-stakes route
/// behind a generic label. The <c>no-catalog-member: agent / workflow / document
/// orchestration write</c> paraphrase originally covered 30+ routes including the
/// agent-provider CREDENTIAL writes (<c>POST|DELETE
/// /api/v1/agents/providers/{provider}/credential</c>, <c>…/credential/rotate</c>) and
/// ESCALATION RESOLUTION (<c>POST /api/documents/escalations/{escalationId}/resolve</c>)
/// — none of which is the same risk class as a role-selection <c>PUT</c>. Those four
/// now carry their own justification lines. THE RULE: when a family contains a member
/// whose consequence differs in KIND from the rest, split it out, even though the
/// count pin does not change. A reviewer scanning this file must be able to see the
/// dangerous routes without reading the route patterns themselves.</para>
/// </summary>
internal static class KnownUngovernedEndpoints
{
    /// <summary>One baselined endpoint.</summary>
    /// <param name="Method">Upper-case HTTP method.</param>
    /// <param name="Pattern">Raw route pattern.</param>
    /// <param name="Justification">Why it is not governed yet; must classify.</param>
    internal sealed record Entry(string Method, string Pattern, string Justification);

    /// <summary>
    /// The classification vocabulary. An entry must read as one of these — the point
    /// is that a reader can tell, per entry, WHICH KIND of ungoverned it is, and
    /// therefore what would have to happen to govern it.
    /// </summary>
    internal static readonly string[] JustificationKeywords =
    [
        // Reached by a person through a UI, never by an autonomous agent. Governing
        // it would gate a human on themselves.
        "human-operated",

        // Auth, webhooks, health, diagnostics, billing callbacks — infrastructure
        // the platform needs to function; not an agent capability.
        "platform-infrastructure",

        // An engine mediation route that Story 43-9 will bind with .Governs plus the
        // enforcement filter. These are the ones that DO get governed next.
        "engine-mediation",

        // A catalogued capability whose route exists but whose binding is another
        // story's (named in the text).
        "binding-owned-by",

        // The gate-evaluation endpoint itself.
        //
        // LIVE as of 2026-08-01 (Story 43-9). It was pre-provisioned with ZERO uses
        // on 2026-07-29 (review F18(b)) and its use count was pinned at 0 by
        // GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_
        // isStillUnused. 43-9 landed POST /api/v1/governance/evaluate, so that test
        // is DELETED rather than widened — an unused arm and a used arm are
        // different facts, and 43-8's own failure message said which way to resolve
        // it. The route it classifies is in ReviewedUngovernedExceptions, not in the
        // shrink-only baseline: see the D17 note there for why.
        "gate-evaluation-endpoint-cannot-gate-itself",

        // No catalog member exists for this capability at all; cataloguing it is a
        // governance decision nobody has taken yet (epic README open question 5).
        "no-catalog-member",
    ];

    /// <summary>Whether a justification reads as one of <see cref="JustificationKeywords"/>.</summary>
    internal static bool IsClassified(string justification) =>
        !string.IsNullOrWhiteSpace(justification)
        && JustificationKeywords.Any(k => justification.Contains(k, StringComparison.OrdinalIgnoreCase));

    // =======================================================================
    // The EXCEPTION classifier — strictly stronger than IsClassified.
    // Review finding F3 (2026-08-01), PROVED BY MUTATION.
    // =======================================================================

    /// <summary>
    /// The vocabulary an <see cref="ReviewedUngovernedExceptions"/> entry must use
    /// ON TOP OF <see cref="JustificationKeywords"/>.
    ///
    /// <para><b>Why a second vocabulary exists</b> (review F3). The D17 doc-comment
    /// below claims the exception set "cannot become a blanket escape hatch" because
    /// each entry "must pass the SAME classifier as the baseline". That sentence was
    /// the defect: passing the same classifier is exactly what every one of the 216
    /// backlog entries already does. A reviewer proved it by moving an ordinary
    /// backlog entry (<c>DELETE /api/acceptance-rules/{documentTypeKey}</c>) into
    /// the exception set with its justification COPIED VERBATIM, bumping
    /// <see cref="ExceptionPinHistory"/> to <c>[3]</c> and DROPPING
    /// <see cref="PinnedCount"/> 216 → 215 — so the laundering read as governance
    /// PROGRESS in the diff — and the whole fixture stayed green.</para>
    ///
    /// <para><b>What separates the two sets.</b> The backlog is "nobody has got to
    /// this yet". The exception set is "gating this is CIRCULAR — the route would
    /// have to pass the gate in order to decide whether it may run the gate". So an
    /// exception must actually make that argument, in words. This vocabulary occurs
    /// ZERO times across all 216 backlog justifications (measured, and asserted by
    /// <c>GovernedEndpointCoverageSweepTests.Discrimination_noBacklogJustification_
    /// wouldSatisfyTheExceptionClassifier</c>), so a copied backlog line cannot
    /// satisfy it and an author who wants to launder one must write a circularity
    /// claim that is false — a lie a reviewer can see, rather than a relabelling
    /// nobody can.</para>
    /// </summary>
    internal static readonly string[] ExceptionCircularityKeywords =
    [
        "circular",
        "circularity",
        "deadlock",
    ];

    /// <summary>
    /// Floor on an exception justification's length. An exception is an ARGUMENT,
    /// not a label: the two seeded entries run 344 and 566 characters, while the
    /// backlog's median is 154. This is a floor, not the discriminator — 22 backlog
    /// justifications clear it — the discriminator is
    /// <see cref="ExceptionCircularityKeywords"/>.
    /// </summary>
    internal const int MinExceptionJustificationLength = 200;

    /// <summary>
    /// Whether a justification is strong enough to buy an entry in
    /// <see cref="ReviewedUngovernedExceptions"/> — MATERIALLY stronger than
    /// <see cref="IsClassified"/>, which is all the baseline requires.
    /// </summary>
    internal static bool IsExceptionJustified(string justification)
    {
        // 1. It is still a baseline-classifiable justification — an exception is a
        //    baseline justification PLUS an argument, never something outside the
        //    vocabulary.
        if (!IsClassified(justification)) return false;

        var text = justification.Trim();

        // 2. It ARGUES the circularity that is the only reason this set exists.
        //    Zero of the 216 backlog justifications do; a copied backlog line is
        //    therefore rejected, and laundering one requires WRITING a circularity
        //    claim — which is either true (fine, that is the bar) or a visible lie.
        if (!ExceptionCircularityKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;

        // 3. It is an argument, not a label. Stops the bare word "circular" being
        //    appended to a one-line backlog reason as a magic token.
        if (text.Length < MinExceptionJustificationLength) return false;

        // 4. It is not a VERBATIM copy of a live backlog justification — the exact
        //    laundering the reviewer performed. Read at call time, not in a static
        //    initialiser, because `All` is declared below this method.
        return !All.Any(e => string.Equals(
            e.Justification.Trim(), text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The count pin (AC8(c)). SEEDED 2026-07-29 from the sweep itself. May only go
    /// DOWN, and every decrement must come with the deleted entry in the same diff.
    ///
    /// <para><b>237 → 216 (2026-07-30, Story 43-8 AC1 steps 2–3, carve-outs §A1 #1
    /// and #2).</b> The ratchet turning the right way for the first time: 21 entries
    /// were DELETED because their routes now carry a real binding — the 17 mediation
    /// routes bound with <c>.Governs</c> in <c>Program.cs</c>, plus
    /// <c>MentorshipController</c>'s four <c>[HttpPost]</c> actions bound with
    /// <c>[Governs]</c>. A route may never be both bound and baselined; the staleness
    /// arm of <c>EveryMutatingEndpoint_IsGovernedOrJustified</c> is what enforces
    /// that.</para>
    ///
    /// <para><b>The direction rule is ASSERTED, not only written</b> — the value is
    /// the last element of <see cref="PinHistory"/> and
    /// <c>GovernedEndpointCoverageSweepTests.TheRatchetPin_IsMechanicallyShrinkOnly</c>
    /// asserts that history is strictly decreasing (the
    /// <c>TemplateExampleConformanceTests</c> shape, adopted here for all four 43-8
    /// ratchets). Raising this pin now requires appending a value that makes the
    /// fixture RED.</para>
    /// </summary>
    /// <para><b>216 → 215 (Story 43-12).</b> POST /api/engine/command — a 200
    /// "Command accepted" no-op — was DELETED (route + handler + DTO); its stale
    /// baseline entry goes in the same diff and the ratchet turns the way it
    /// celebrates (strictly down).</para>
    /// <para><b>215 → 214 (Story 42-10).</b> GET /api/v1/secrets/reveal/{token} is
    /// no longer baselined — it now binds effect:secret.read and enforces (an LLM
    /// value-read gates at 90). The baseline entry is deleted in the same diff.</para>
    internal const int PinnedCount = 214;

    /// <summary>
    /// The pin's recorded high-water history, oldest first; every element must be
    /// strictly LESS than its predecessor. 237 (seeded 2026-07-29, zero routes bound)
    /// → 216 (2026-07-30, 21 routes bound).
    ///
    /// <para><b>Honest residual</b> (same as the precedent's): this defeats the
    /// ordinary laundering path — editing one literal — but not deliberate tampering,
    /// since an author could append an increase AND edit the assertion. It moves the
    /// shrink-only property from prose into something a reviewer can see in a diff,
    /// which is the defect <c>ContractBindingTests.cs:255-271</c> has and this epic's
    /// ratchets must not inherit.</para>
    /// </summary>
    internal static readonly int[] PinHistory = [237, 216, 215, 214];

    /// <summary>
    /// The pinned size of the in-scope mutating surface (Correction 4: derive it at
    /// runtime, then pin it, rather than restating a literal from a grep).
    ///
    /// <para><b>UNCHANGED at 237 by the 2026-07-30 binding work, deliberately.</b> A
    /// bound route leaves the BASELINE but does not leave the in-scope SURFACE — it
    /// is still a mutating endpoint the sweep must see, it is simply governed now. So
    /// <see cref="PinnedCount"/> and this number are equal only while zero routes are
    /// bound (story 43-8 §A3 step 2); from the first binding onward they move
    /// independently and <c>InScopeEndpointCount_isPinned</c> /
    /// <c>Baseline_countIsPinned</c> must be reconciled separately. Today:
    /// 237 in scope, 216 baselined, 21 bound.</para>
    /// </summary>
    // Story 43-12 — 239 → 238: POST /api/engine/command was DELETED from the host, so
    // the in-scope mutating surface has one fewer endpoint (215 baselined + 2
    // exceptions + 21 bound = 238).
    internal const int PinnedInScopeCount = 238;

    // =======================================================================
    // Story 43-9 DECISION D17 — the NAMED, DATED, REVIEWED exception set
    // =======================================================================

    /// <summary>
    /// One reviewed exception to the shrink-only baseline.
    /// </summary>
    /// <param name="Method">Upper-case HTTP method.</param>
    /// <param name="Pattern">Raw route pattern.</param>
    /// <param name="AddedOn">ISO date the exception was reviewed.</param>
    /// <param name="Story">The story that reviewed it.</param>
    /// <param name="Justification">Why it is not governed; must classify.</param>
    internal sealed record Exception(
        string Method, string Pattern, string AddedOn, string Story, string Justification);

    /// <summary>
    /// <b>Story 43-9 D17.</b> Routes that are ungoverned by NECESSITY, not by
    /// backlog, and that arrived AFTER <see cref="PinnedCount"/> became
    /// shrink-only.
    ///
    /// <para><b>Why this exists at all.</b> <see cref="PinnedCount"/> is the last
    /// element of a strictly-decreasing <see cref="PinHistory"/>, asserted twice
    /// (in <c>GovernedEndpointCoverageSweepTests</c> and again from the registry by
    /// <c>RatchetDisciplineTests</c>). That is exactly right for the ungoverned
    /// BACKLOG — "a new ungoverned route is not a reason to raise the pin, it is
    /// the signal the ratchet exists to produce". But it makes a route that CANNOT
    /// be governed unrepresentable: 216 → 217 is red by design, and editing 216 in
    /// place is the undeclared re-widening the ratchet exists to catch. The two
    /// alternatives were worse — bind a route that must not be bound, or move it
    /// somewhere the sweep does not look.</para>
    ///
    /// <para><b>Why it cannot become a blanket escape hatch.</b> It is keyed
    /// PER ROUTE, so a different new route still goes red; each entry carries a
    /// date, the reviewing story and a justification that must pass
    /// <see cref="IsExceptionJustified"/>; its MEMBERSHIP is pinned by route in
    /// <c>GovernedEndpointCoverageSweepTests.ExceptionSet_membershipIsPinnedByRoute</c>;
    /// the set is count-pinned by a history whose HEAD is bound to its seed and
    /// whose tail must strictly decrease; it is declared in
    /// <c>RatchetDisciplineTests.Ratchets()</c> so all three AC8 properties are
    /// asserted against it; and staleness applies both ways — an entry whose route
    /// no longer exists, or which becomes bound, fails until deleted. The rejected
    /// alternative was the count-level "name the index that may rise" precedent,
    /// which is ANONYMOUS: any future route could occupy the widened slot.</para>
    ///
    /// <para><b>CORRECTION 2026-08-01 (review F3) — the paragraph above used to
    /// say "a justification that must pass the SAME classifier as the baseline",
    /// and the set was pinned by COUNT ALONE with a one-element history. All three
    /// of those were the escape hatch, not the guard against it. A reviewer moved
    /// an ordinary backlog entry (<c>DELETE /api/acceptance-rules/{documentTypeKey}</c>)
    /// into this set with its justification COPIED VERBATIM and a made-up story id,
    /// set <see cref="ExceptionPinHistory"/> to <c>[3]</c>, and dropped
    /// <see cref="PinnedCount"/> 216 → 215 with history <c>[237, 216, 215]</c> — so
    /// the laundering read in the diff as governance PROGRESS — and 41 of 41 tests
    /// passed, this file's own <c>ExceptionSet_*</c> tests included. Every "why it
    /// cannot become an escape hatch" clause above is now backed by an assertion
    /// that has been watched to fail on that exact mutation.</b></para>
    ///
    /// <para><b>SEEDED AT 2, not 1.</b> Story 43-9's plan budgeted one exception
    /// (the gate-evaluation route). Implementing it produced TWO ungoverned
    /// routes that cannot be governed, both for the same circularity reason in
    /// different directions: the route that ASKS the gate, and the route a person
    /// uses to OVERRIDE it. Gating the second would mean an admin needs a grant in
    /// order to issue a grant — the exact deadlock the authorization ledger
    /// exists to prevent. Recorded here rather than smuggled into one entry.</para>
    /// </summary>
    internal static readonly IReadOnlyList<Exception> ReviewedUngovernedExceptions =
    [
        new("POST", "/api/v1/governance/evaluate", "2026-08-01", "Story 43-9",
            "gate-evaluation-endpoint-cannot-gate-itself: the engine's mediation route to the "
            + "autonomy gate (Tamma.ElsaServer registers no repository and cannot inject "
            + "IAutonomyGate, so CheckActionGateActivity asks over HTTP). It mints no "
            + "ExternalEffect member because it is a READ. Binding it would be circular — the "
            + "route would have to evaluate the gate to decide whether it may evaluate the gate."),

        new("POST", "/api/actions/authorizations/{id:guid}/decide", "2026-08-01", "Story 43-9",
            "human-operated: the authorization ledger's decision surface — a person grants or "
            + "denies one pending authorization from the dashboard, behind ActionsManage. It is "
            + "THE override for a gate denial, so gating it would require a grant in order to "
            + "issue a grant: an admin whose deploy was blocked could never unblock it. Same "
            + "circularity as the gate-evaluation route, in the opposite direction."),
    ];

    /// <summary>The exception set's own count pin — shrink-only, seeded 2026-08-01.</summary>
    internal static readonly int[] ExceptionPinHistory = [2];

    /// <summary>Exception entries as baseline entries, for the coverage rule's lookup.</summary>
    internal static IEnumerable<Entry> ExceptionsAsEntries =>
        ReviewedUngovernedExceptions.Select(e => new Entry(e.Method, e.Pattern, e.Justification));

    /// <summary>The baseline itself.</summary>
    internal static readonly IReadOnlyList<Entry> All =
    [
        new("*", "/health",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("*", "/health/live",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("*", "/health/ready",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("DELETE", "/api/acceptance-rules/{documentTypeKey}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/actions/policy/actions/{ns}/{key}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/actions/policy/groups/{group}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/admin/actions/ceiling/actions/{ns}/{key}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/actions/ceiling/groups/{group}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/api-keys/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/conventions/{role}/{action}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/pricing/plans/{slug}/versions/{version:int}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/providers/{key}/settings",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/scheduled-triggers/{id:guid}",
            "human-operated: platform-owner scheduled-trigger CONFIGURATION behind PlatformOwnerAccess. DECIDED 2026-08-01 by Story 43-9 (§C-bis), which this entry used to name as the binding owner: it is NOT bound, and the previous 'binding-owned-by Story 43-9' text was an expectation nobody had taken. Reasoning: the epic's general rule for /api/admin/* is that a surface reached by a person and never by an agent must not be gated, because gating it gates a human on themselves; and the catalogued effects this route family performs (effect:schedule.create|update|delete) are classified RouteOnly in MediationClientEffectSweepTests precisely because they are reached from the dashboard, not through the engine mediation client. What the schedule ARMS is governed at its own seams when it fires. A future story that binds this owns the behaviour change"),
        new("DELETE", "/api/admin/service-keys/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/tenant-databases/{databaseId:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/invites/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/{id}/keys/{keyId}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/agents/{agentId:guid}/enablement",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/conventions/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/engine/issue-labels/{repo}/{issueNumber}/{label}",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("DELETE", "/api/kb/index",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("DELETE", "/api/kb/vector-db/delete",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("DELETE", "/api/pricing/providers/{provider}/byok",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/projects/{projectId:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/prompts/system/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/prompts/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/providers/providers/{handle}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/tracker/preferences",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/v1/admin/alert-channels/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/v1/admin/alert-rules/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/v1/agents/providers/{provider}/credential",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — this route and the two POSTs below store, rotate and delete a provider secret for the tenant's agents. Split onto its own justification line 2026-07-29 (review F15): it was hidden inside the generic 'agent / workflow / document orchestration write' family, and a credential write is not the same risk class as a role-selection PUT. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("DELETE", "/api/v1/agents/providers/{provider}/model",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/v1/integrations/email/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/v1/integrations/jira/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/alert-channels/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/api-keys/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/invites/{inviteId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/members/{userId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/work-items/{id:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/workflows/instances/{id}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        // Story 42-10 — the reveal route is NO LONGER baselined: it now binds
        // effect:secret.read and .EnforcesGovernance() (an LLM value-read gates at
        // 90; an authenticated human passes). Its "deliberately never enforceable"
        // justification became false the moment the route enforced.
        new("PATCH", "/api/admin/providers/{key}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/admin/tenant-databases/{databaseId:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/admin/tenants/{tenantId:guid}/plan",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/projects/{projectId:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("PATCH", "/api/v1/admin/alert-channels/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/v1/admin/alert-rules/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/v1/onboarding/repos/{installationId:long}/{repoId:long}",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("PATCH", "/api/v1/orgs/{tenantId:guid}/alert-channels/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PATCH", "/api/work-items/{id:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/actions/policy/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/adl/blocker/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/clarify/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/deploy-approval/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/design/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/merge-approval/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/admin/api-keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/conventions/{role}/{action}/reset",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/kek/rotate/retry",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/kek/rotate/start",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pools/{tenantId:guid}/evict",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pricing/plans",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pricing/plans/custom",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/providers",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/scheduled-triggers/",
            "human-operated: platform-owner scheduled-trigger CONFIGURATION behind PlatformOwnerAccess. DECIDED 2026-08-01 by Story 43-9 (§C-bis), which this entry used to name as the binding owner: it is NOT bound, and the previous 'binding-owned-by Story 43-9' text was an expectation nobody had taken. Reasoning: the epic's general rule for /api/admin/* is that a surface reached by a person and never by an agent must not be gated, because gating it gates a human on themselves; and the catalogued effects this route family performs (effect:schedule.create|update|delete) are classified RouteOnly in MediationClientEffectSweepTests precisely because they are reached from the dashboard, not through the engine mediation client. What the schedule ARMS is governed at its own seams when it fires. A future story that binds this owns the behaviour change"),
        new("POST", "/api/admin/scheduled-triggers/{id:guid}/run-now",
            "human-operated: platform-owner MANUAL FIRE of an existing scheduled trigger behind PlatformOwnerAccess. SPLIT OUT from the schedule-CRUD family on 2026-08-01 (Story 43-9 §C-bis) under this file's own rule that a member whose consequence differs in KIND gets its own line: run-now EXECUTES where the other three CONFIGURE, so a reviewer scanning this file must see it without reading route patterns. It is still ungoverned for the same reason, and the reason is stronger here than for CRUD: a person is pressing the button, and the workflow the fire dispatches is itself governed at its own seams when it runs — gating this would gate the human on themselves AND double-gate the dispatch. A future story that binds it owns the behaviour change"),
        new("POST", "/api/admin/secrets",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/secrets/{id:guid}/retire-version/{versionNumber:int}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/secrets/{id:guid}/rotate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/service-keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/service-keys/{id}/rotate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenant-databases",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/migrate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/cancel-delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/force-delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/retry",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/cleanup",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/deprovision",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/impersonate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/move",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/plan/cancel",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/provision",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/users/invite",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/users/{id}/keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/agents/",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/archive",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/rollback",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/versions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/auth/impersonate/end",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/auth/logout",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/config/sanitize",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/conventions/resolve",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/documents/decisions/{sessionId}/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/documents/escalations/{escalationId}/resolve",
            "no-catalog-member: ESCALATION RESOLUTION — closes an escalated document review and releases the suspended lifecycle. Split onto its own justification line 2026-07-29 (review F15): resolving an escalation is the very act the escalation ring exists to require a decision for, so grouping it with the generic 'agent / workflow / document orchestration write' family hid the highest-stakes route in that family. Cataloguing it is epic README open question 5"),
        // Story 43-12 — DELETED the /api/engine/command baseline entry with the route
        // itself (POST /api/engine/command was a 200 "Command accepted" no-op; deleting
        // the route made this entry stale, so it goes in the same commit and the pins
        // decrement: PinnedCount 216 → 215, PinnedInScopeCount 239 → 238).
        new("POST", "/api/engine/create-issue",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/cycle-result",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/execute-task",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/issue-comment",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/issue-labels",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/query-context",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/store-context",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/trigger-ci",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/github/webhooks",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/kb/context/feedback",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/index/trigger",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        // WORDING CORRECTED 2026-07-29 (adversarial review F16). These two entries
        // previously claimed the start/stop pair was "the C# half of the catalogued
        // effect:mcp.tool.invoke member". That was never true in a bindable sense:
        // the member's SiteKey read "POST /api/kb/mcp/servers/{id}/start|stop", an
        // ALTERNATION, and GovernedEndpointBindingSweepTests compares a SiteKey's
        // route part to a single $"{method} {RawText}" ordinally — so it matched no
        // real route and neither start nor stop could ever have been bound to it.
        // Server start/stop is MCP-server LIFECYCLE, not tool invocation; it has no
        // catalog member of its own, and giving it one is a vocabulary decision.
        new("POST", "/api/kb/mcp/servers/{id}/start",
            "no-catalog-member: MCP-SERVER LIFECYCLE — starting a configured MCP server changes which tools exist for the model to call. It is NOT effect:mcp.tool.invoke (that member names the invocation route) and no catalog member covers server lifecycle; adding one is a vocabulary decision nobody has taken. Note that this route's real consequence — the tool SET changing — has no drift signal anywhere in this epic"),
        new("POST", "/api/kb/mcp/servers/{id}/stop",
            "no-catalog-member: MCP-SERVER LIFECYCLE — the stop half of the pair above; same absent catalog member, same absent drift signal"),
        new("POST", "/api/kb/mcp/tools/invoke",
            "binding-owned-by Story 43-9: the direct MCP tool-invocation proxy and the ONE route effect:mcp.tool.invoke actually names. DECISION REVERSED 2026-08-01 (Story 43-9 D16, §C): 43-9 deliberately does NOT bind or enforce this route, and this entry stays. Reason: on 2026-07-30 effect:mcp.tool.invoke was reversed to ship min: AutonomyDial.AlwaysHuman, because epic D2 tolerates an unclassified action at runtime only on the strength of the drift harnesses making it unmergeable in CI, and NO CI harness can enumerate a remote MCP server's tools — for MCP that half of the bargain does not exist and never will. So a binding here would NOT be behaviour-preserving: it would hard-block the route on day one, which is a behaviour change a story must argue for rather than inherit from a helper. Blast radius is independently empty today: no MCP tool executor is registered, so an mcp__* name already terminates as an unknown tool. A future story that binds it owns that behaviour change"),
        new("POST", "/api/kb/rag/query",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/vector-db/search",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/vector-db/upsert",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/onboarding/install",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/pricing/providers/{provider}/byok",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/pricing/subscribe",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/projects",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/prompts/{role}/{action}/render",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/prompts/{role}/{action}/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/chain/resolve",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/diagnostics",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/diagnostics/batch",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/failure",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/success",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/providers/create",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/providers/{handle}/execute",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/admin/alert-channels",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alert-rules",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alert-rules/{id:guid}/_test",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/_test",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/{id:guid}/acknowledge",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/{id:guid}/resolve",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/audit/checkpoint",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/agents/",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/config/validate",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/providers/{provider}/credential",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — stores a provider secret for the tenant's agents. Split onto its own justification line 2026-07-29 (review F15) so a reviewer scanning this baseline can SEE it instead of reading a generic 'agent / workflow / document orchestration write' paraphrase that also covers role-selection PUTs. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("POST", "/api/v1/agents/providers/{provider}/credential/rotate",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — rotates a stored provider secret. Split onto its own justification line 2026-07-29 (review F15); see the credential POST above. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("POST", "/api/v1/agents/resolve-for-phase",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/{id:guid}/archive",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/{id:guid}/versions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/auth/login",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/password-reset/confirm",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/password-reset/request",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/refresh",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/register",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/resend-verification",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/switch-org",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/verify-email",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/installations/{id}/rotate-key",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/integrations/email/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/integrations/jira/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/llm/chat",
            "no-catalog-member: the SaaS API-key LLM chat route (SaaSEndpoints.LlmChat), a separate surface from the engine mediation seam POST /api/v1/llm/call that effect:llm.call names and is now bound to. Justification corrected 2026-07-30 (43-8 AC1 step 3): it previously claimed a catalogued member exists for it, which was a family paraphrase and is false. Cataloguing it is epic README open question 5"),
        new("POST", "/api/v1/onboarding/complete",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/orgs/",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/invites/accept",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alert-channels",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alerts/{id:guid}/acknowledge",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alerts/{id:guid}/resolve",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/api-keys",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/invites",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/invites/{inviteId:guid}/resend",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/reprovision",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/retire-version/{versionNumber:int}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/rotate",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/rotate-workflow",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/transfer-ownership",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/secrets/{secretId:guid}/rotate",
            "human-operated: the secret cabinet's admin surface, driven by a person through the dashboard; no catalog member covers cabinet CRUD"),
        new("POST", "/api/v1/workflows/{id}/result",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/workflows/{id}/status",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/webhooks/{platform}",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/work-items",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/work-items/{id:guid}/assign",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/work-items/{id:guid}/status",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/workflows/definitions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/workflows/instances",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/workflows/instances/{id}/cancel",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/acceptance-rules/{documentTypeKey}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/enabled",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/enforce",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/roles",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/threshold",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/groups/{group}/threshold",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/admin/actions/ceiling/actions/{ns}/{key}/threshold",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/actions/ceiling/groups/{group}/threshold",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/conventions/{role}/{action}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/pricing/margins",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/pricing/plans/{slug}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/providers/{key}/prices",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/providers/{key}/settings",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/scheduled-triggers/{id:guid}",
            "human-operated: platform-owner scheduled-trigger CONFIGURATION behind PlatformOwnerAccess. DECIDED 2026-08-01 by Story 43-9 (§C-bis), which this entry used to name as the binding owner: it is NOT bound, and the previous 'binding-owned-by Story 43-9' text was an expectation nobody had taken. Reasoning: the epic's general rule for /api/admin/* is that a surface reached by a person and never by an agent must not be gated, because gating it gates a human on themselves; and the catalogued effects this route family performs (effect:schedule.create|update|delete) are classified RouteOnly in MediationClientEffectSweepTests precisely because they are reached from the dashboard, not through the engine mediation client. What the schedule ARMS is governed at its own seams when it fires. A future story that binds this owns the behaviour change"),
        new("PUT", "/api/admin/tenants/{tenantId:guid}/plan",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/users/{id}/role",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/agents/role-selections/{role}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/agents/{agentId:guid}/enablement",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/config/agents",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/prompts/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/providers",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/sanitize/rules",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/security",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/conventions/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/kb/index/config",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("PUT", "/api/kb/mcp/config",
            "no-catalog-member: MCP server configuration write — adding a server changes which tools exist, and that has NO drift signal anywhere in this epic (effect:mcp.tool.invoke is one coarse member by construction)"),
        new("PUT", "/api/kb/rag/config",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("PUT", "/api/prompts/system/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/prompts/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/providers/diagnostics/budget/{accountId}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/tracker/preferences",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("PUT", "/api/v1/agents/config",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/v1/agents/providers/{provider}/model",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/v1/orgs/{tenantId:guid}/members/{userId:guid}/role",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PUT", "/api/v1/orgs/{tenantId:guid}/settings",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PUT", "/api/workflows/instances/{id}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
    ];

    /// <summary>
    /// Indexed by <c>"{METHOD} {pattern}"</c> — the baseline UNIONED with the
    /// reviewed exception set (Story 43-9 D17(4)). The union is what makes an
    /// exception "accounted for" by the coverage rule; the COUNT PINS deliberately
    /// see the two collections separately, so unreviewed growth of the baseline is
    /// still impossible and growth of the exception set is its own visible number.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Entry> BySiteKey =
        All.Concat(ExceptionsAsEntries)
            .GroupBy(e => $"{e.Method} {e.Pattern}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}
