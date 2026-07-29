using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Template WORKED-EXAMPLE ↔ document-type conformance gate.
///
/// <para><b>Why this exists:</b> <see cref="ContractBindingTests"/> pins that a bound
/// cell's template literally CONTAINS the JSON field tokens its validator slices —
/// but a template can carry every token while its own worked example instructs the
/// WRONG document shape. That is not hypothetical: the shipped
/// <c>Prompts/architect/plan-system-design.md</c> instructed
/// <c>files: [{path, action}]</c> + <c>dependencies</c> while
/// <c>Tamma.Core/Documents/Types/Plan.cs</c> deserializes <c>files</c> as
/// <c>string[]</c> and reads <c>dependsOn</c> — every produce through the cell was a
/// guaranteed <c>MALFORMED_PAYLOAD</c>, and every existing test stayed green (the
/// tokens <c>"tasks"</c>/<c>"files"</c> were present). This fixture closes that gap:
/// for every DocumentType-bound cell it EXTRACTS the template's fenced JSON example
/// and runs it through the bound type's REAL <c>Validate()</c> (via
/// <see cref="DocumentTypeRegistry"/>).</para>
///
/// <para><b>Extraction mirrors the runtime ingest.</b> The shipped templates format
/// their instructed reply as a single fenced <c>```json</c> block. The extractor
/// takes the LAST such fence and then applies the exact carve the lifecycle applies
/// to a real reply — first <c>{</c> … last <c>}</c>, must parse
/// (<c>DocumentLifecycleWorkflow.ExtractJsonObject</c>). A bound template whose
/// fence holds no carvable JSON object is therefore a violation in itself: the
/// reply shape it instructs could never even be INGESTED, let alone validate.</para>
///
/// <para><b>Closed-set placeholders are normalized, not failed.</b> The repo's
/// template/RenderContract idiom writes closed vocabularies inline as
/// <c>"low|medium|high"</c> (or <c>"urgent | high | normal | low"</c>) string
/// values. Those are placeholder notation, not a wrong shape — and the 39-16
/// regeneration source (<c>IDocumentType.RenderContract</c>) uses the same idiom —
/// so before validation every string value of the form <c>a|b|c</c> is replaced by
/// its FIRST alternative. Structural drift (wrong field names, objects where
/// strings belong, missing required members, dangling ids) still fails.</para>
///
/// <para><b>The classification is EXHAUSTIVE, not an allowlist</b> (adversarial
/// review follow-up, 2026-07-29). Until this change the fixture only ever LOOKED at
/// cells someone had already written into a table: bound cells (test 1), the
/// <see cref="KnownNonConformingTemplates"/> ratchet (test 2), and a 5-entry
/// hand-maintained <see cref="ConformingUnboundCells"/> list (test 4). Nothing
/// enumerated the taxonomy, so a cell in NO table was simply never checked — which is
/// exactly how <c>(security, threat-model)</c> could sit outside every table while
/// instructing a shape its own registered validator rejects. Test 5
/// (<see cref="EveryTaxonomyCell_IsClassifiedExactlyOnce"/>) now derives the FULL cell
/// set from the real taxonomy and requires every cell to land in EXACTLY ONE of four
/// classifications:
/// <list type="number">
///   <item>a live binding (<c>ContractBindingTests.Bindings</c>) — test 1's job;</item>
///   <item><see cref="ConformingUnboundCells"/> — unbound, intended type registered,
///         example must validate TODAY (test 4);</item>
///   <item><see cref="KnownNonConformingTemplates"/> — unbound, intended type
///         registered, example does NOT validate; shrink-only ratchet debt (test 2);</item>
///   <item><see cref="IntentionallyUnboundCells"/> — no registered document type
///         claims the cell at all, with a written reason.</item>
/// </list>
/// A cell in none of them fails the build naming the three entries the author could
/// add and the evidence for choosing between them. A new taxonomy token (the way the
/// Epic 41 cells arrived) therefore cannot ship unclassified.</para>
///
/// <para><b>Known pre-existing non-conformance</b> is baselined in
/// <see cref="KnownNonConformingTemplates"/> — the same ratchet shape as
/// <c>ContractBindingTests.KnownContractViolations</c>: entries may only ever be
/// REMOVED (count-pinned), and a stale entry (one whose template now conforms)
/// fails the build. Every entry is an UNBOUND cell owned by an Epic 41 story; a
/// BOUND cell may never be baselined — binding a cell (the 39-12+ lifecycle
/// migration) requires rewriting its template to conform (the 39-15 D7 precedent)
/// and deleting the baseline entry in the same change.</para>
/// </summary>
[TestFixture]
public class TemplateExampleConformanceTests
{
    // ====================================================================
    // Known non-conforming templates — a RATCHET, not an escape hatch
    // ====================================================================

    /// <summary>
    /// One baselined cell: the document type its Epic 41 owner will bind it to
    /// (<paramref name="IntendedDocumentTypeKey"/> — a registered wire key; every
    /// planned Epic 41 key has now landed, so an unregistered key is a typo), the
    /// owning story, and why the shipped template does not conform today.
    /// </summary>
    private sealed record BaselineEntry(string IntendedDocumentTypeKey, string OwningStory, string Reason);

    /// <summary>
    /// Cells whose shipped template ALREADY fails example-conformance. All are
    /// UNBOUND today; each is owned by the Epic 41 story that will bind it.
    /// Baselining keeps the build green while making the debt explicit and
    /// un-growable: (a) any NEW violation on a bound cell still fails, (b) an entry
    /// whose template now conforms goes STALE and fails until deleted, (c) an entry
    /// whose cell gets BOUND fails until the binding story rewrites the template and
    /// deletes the entry.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), BaselineEntry> KnownNonConformingTemplates =
        new Dictionary<(string, string), BaselineEntry>
        {
            [("architect", "plan-migration-strategy")] = new("plan", "41-12",
                "example instructs the legacy plan wire — files as {path, action} objects plus a " +
                "\"dependencies\" key — while Plan requires per-task files: string[] + dependsOn + testing " +
                "(MALFORMED_PAYLOAD on deserialization)"),
            [("tester", "plan-test-strategy")] = new("test-plan", "41-13",
                "example instructs the legacy plan wire; 41-1b mints the TestPlan document type this cell " +
                "will produce, and 41-13 rewrites the cell when it binds"),
            [("tester", "exploratory-test")] = new("findings", "41-14",
                "no JSON example at all — the template instructs file-format test output; 41-14 rewrites " +
                "the cell as a Findings (exploratory charter) producer"),
            [("tester", "write-regression-test")] = new("test-spec", "41-16",
                "no JSON example at all — the template instructs file-format test output; 41-16 rewrites " +
                "the cell as a TestSpec (bound regression case) producer"),
            [("tester", "verify-acceptance")] = new("review", "41-15",
                "example instructs an {issues, summary: {decision, ...}} shape; Review requires root-level " +
                "subject/decision/summary (summary is a string, not an object) — MALFORMED_PAYLOAD"),
            // (product_owner, define-acceptance-criteria) REMOVED (Story 41-2, 2026-07-29):
            // AcceptanceCriteriaAuthoringWorkflow binds the cell, so it moved to
            // ContractBindingTests.Bindings and test 1 now owns it. Its template was rewritten
            // from the legacy task-breakdown (plan) wire to the AcceptanceCriteria wire in the
            // same change — a bound cell may never be baselined here (test 3). Pin 16 → 15.
            [("product_owner", "plan-roadmap")] = new("prose", "41-4",
                "example instructs the legacy plan wire; 41-4 produces prose (roadmap, audience=stakeholder) " +
                "once 41-1c lands the prose document type"),
            [("product_owner", "prioritize-backlog")] = new("backlog-ordering", "41-3",
                "example instructs the retired P0-P3 / severity / ownerRole triage vocabulary; 41-1b mints " +
                "the BacklogOrdering document type this cell will produce"),
            [("devops", "plan-incident-response")] = new("plan", "41-22",
                "example instructs the legacy plan wire — files as {path, action} objects plus a " +
                "\"dependencies\" key — MALFORMED_PAYLOAD against Plan"),
            [("devops", "write-postmortem")] = new("prose", "41-22",
                "no JSON example — the template instructs a markdown issue-comment format; becomes " +
                "prose (postmortem, audience=engineering) once 41-1c lands"),
            [("tech_writer", "update-changelog")] = new("prose", "41-24",
                "no JSON example — the template instructs a markdown issue-comment format; becomes " +
                "prose (release-notes/changelog) once 41-1c lands"),

            // ---- surfaced by the exhaustive classification (test 5), 2026-07-29 ----
            // The five remaining prose-family cells. Prose.cs's kind vocabulary names a
            // producing story for each of its ten kinds (adr → 41-9, release-notes and
            // changelog → 41-24, user-docs and api-docs → 41-25, runbook → 41-26 …), so
            // each of these cells HAS an intended registered type (prose landed with
            // 41-1c). None of them instructs it: every one is a markdown-format template
            // with no JSON fence at all, so a produce through the cell could not even be
            // ingested. This is PRE-EXISTING debt of exactly the same shape as the
            // (devops, write-postmortem) / (tech_writer, update-changelog) entries above —
            // it became visible only because test 5 started enumerating the taxonomy. The
            // templates are NOT rewritten here (each is owned by its story); they are
            // recorded so the debt is explicit and shrink-only from now on.
            // (architect, write-adr) REMOVED (Story 41-9, 2026-07-29): AdrAuthoringWorkflow binds
            // the cell, so it moved to ContractBindingTests.Bindings and test 1 now owns it. Its
            // template was rewritten from the markdown issue-comment report to the prose envelope
            // (kind=adr, audience=engineering) in the same change — a bound cell may never be
            // baselined here (test 3). Pin 15 → 14. This is the FIRST of the six prose-family
            // baseline entries to clear; 41-4/41-22/41-24/41-25/41-26 own the rest.
            [("tech_writer", "write-release-notes")] = new("prose", "41-24",
                "no JSON example — the template instructs markdown release notes; 41-24 rewrites the cell " +
                "as a prose (release-notes, audience=user) producer"),
            [("tech_writer", "write-user-docs")] = new("prose", "41-25",
                "no JSON example — the template instructs markdown user documentation; 41-25 rewrites the " +
                "cell as a prose (user-docs, audience=user) producer"),
            [("tech_writer", "write-api-docs")] = new("prose", "41-25",
                "no JSON example — the template instructs markdown API reference output; 41-25 rewrites " +
                "the cell as a prose (api-docs, audience=developer) producer"),
            [("tech_writer", "write-runbook")] = new("prose", "41-26",
                "no JSON example — the template instructs a markdown runbook; 41-26 rewrites the cell as a " +
                "prose (runbook, audience=ops) producer"),
        };

    /// <summary>
    /// The ratchet's count pin.
    ///
    /// <para><b>Direction rule:</b> this number goes DOWN. Delete the baseline entry
    /// and decrement the pin when the owning story rewrites its template. A template
    /// edit may NEVER be accompanied by an increment — that is the whole point of the
    /// ratchet, and every other gate in the fixture (tests 1–4) still fails loudly on
    /// new non-conformance.</para>
    ///
    /// <para><b>Pin history.</b> 11 → 16 (2026-07-29). The only increase this pin may
    /// ever record is the one in the change that WIDENED the gate: before test 5 the
    /// fixture never looked outside its own tables, so the five prose cells added above
    /// were invisible debt, not new drift. Widening the lens is allowed to reveal what
    /// was already there; nothing else may raise this number.
    /// 16 → 15 (2026-07-29, Story 41-2): the ratchet turning the right way for the first
    /// time — (product_owner, define-acceptance-criteria) was BOUND, its template
    /// rewritten to the AcceptanceCriteria wire, and its baseline entry deleted.
    /// 15 → 14 (2026-07-29, Story 41-9): (architect, write-adr) was BOUND, its template
    /// rewritten from the markdown issue-comment report to the prose envelope
    /// (kind=adr, audience=engineering), and its baseline entry deleted.</para>
    /// </summary>
    private const int KnownNonConformingTemplateCount = 14;

    // NOTE (2026-07-29): the PlannedFutureTypeKeys escape hatch is gone — 41-1b
    // registered test-plan / acceptance-criteria / backlog-ordering and 41-1c
    // registered prose, so every baseline entry's intended key now resolves in
    // DocumentTypeRegistry and is staleness-checked against the real validator.
    // An intended key that does NOT resolve is a typo, full stop.

    /// <summary>
    /// UNBOUND cells whose intended document type IS registered and whose shipped
    /// template must therefore instruct a CONFORMING example TODAY — the 41-1a
    /// cross-lane contract (implementation-plan-41-1a.md D5: "each template
    /// instructs the JSON shape its future consumer story will pin"). Test 1 only
    /// covers cells BOUND in ContractBindingTests and the ratchet only covers cells
    /// already known non-conforming, so an unbound cell whose type had landed sat
    /// in neither net — exactly how plan-sprint and author-ui-spec shipped examples
    /// that failed their own validators (adversarial review, 2026-07-29).
    ///
    /// <para><b>Admission evidence.</b> An entry is justified when a LANDED artifact
    /// ties the cell to a registered type. Two evidence classes are accepted, and every
    /// entry below names which one it rests on:
    /// <list type="bullet">
    ///   <item>a <c>// Producing cell</c> comment in <c>Tamma.Core/Documents/Types/*.cs</c>,
    ///         the <c>Prose.cs</c> kind→story seed, or a <c>RolePhaseMap</c> producer note
    ///         — a story has DECLARED the cell's intended type;</item>
    ///   <item>the shipped template demonstrably instructs a registered type's wire —
    ///         its example validates against a DISCRIMINATING validator (one with
    ///         required members / closed enums, so acceptance cannot be an accident).
    ///         Pinning it here keeps that true instead of leaving it to luck.</item>
    /// </list>
    /// An entry may never name a cell that is bound (test 1's job) or baselined in
    /// <see cref="KnownNonConformingTemplates"/> (the ratchet's job) — both are
    /// asserted. When the owning story binds the cell, its entry here is deleted in
    /// the same change (test 1 takes over).</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), string> ConformingUnboundCells =
        new Dictionary<(string, string), string>
        {
            // ---- declared intent: a "Producing cell" comment on the type ----------
            // SprintPlan.cs "Producing cell (41-1b D4)" — 41-6 binds it.
            [("scrum_master", "plan-sprint")] = "sprint-plan",
            // UxSpec.cs "Producing cell (41-1b D4)" — 41-27 binds it.
            [("ux_designer", "author-ui-spec")] = "ux-spec",
            // ThreatModel.cs "Producing cell (41-1b D4): (security, threat-model)" — the cell
            // sat in NO table until test 5 enumerated the taxonomy (it is the drift this
            // whole change exists to make impossible). Its template instructs the
            // ThreatModel wire and validates; 41-19 binds it.
            [("security", "threat-model")] = "threat-model",

            // ---- declared intent: RolePhaseMap producer notes ----------------------
            // RolePhaseMap: "41-10's Design producer". The template instructs the Design
            // wire (summary / recommendation / recommendedAlternativeId /
            // constraintEvaluation / alternatives[id,name,tradeoffs]) and validates.
            [("architect", "design-system")] = "design",
            // RolePhaseMap: "41-22's Diagnosis producer (diagnose-incident stays the
            // triage-panel review lens)". The template instructs the canonical Diagnosis
            // wire (analysisSummary / ranked hypotheses with confidence + affectedFiles).
            [("devops", "incident-rootcause")] = "diagnosis",

            // ---- declared intent: Prose.cs kind→story seed -------------------------
            // Prompts/project_manager/report-status.md instructs prose (kind status-update) — 41-5 binds it.
            [("project_manager", "report-status")] = "prose",
            // Prompts/scrum_master/write-retro-narrative.md instructs prose (kind retro-narrative) — 41-8 binds it.
            [("scrum_master", "write-retro-narrative")] = "prose",
            // Prompts/project_manager/coordinate-release.md instructs prose (kind status-update reused —
            // a conscious vocabulary decision, no dedicated kind; no Epic 41 story binds the cell yet).
            [("project_manager", "coordinate-release")] = "prose",

            // ---- demonstrated intent: the template instructs a discriminating wire --
            // The three scrum_master reporting cells instruct the Findings wire verbatim
            // (topic/summary/findings[title,summary,relevance,confidence,citations]/
            // overallConfidence). FindingsDocumentType requires all of it, so acceptance
            // is not an accident — these were written to the wire on purpose.
            [("scrum_master", "synthesize-standup")] = "findings",
            [("scrum_master", "facilitate-retro")] = "findings",
            [("scrum_master", "track-impediments")] = "findings",
            // The two ux_designer critique cells instruct the CANONICAL Review wire
            // (root-level subject{kind,documentId,documentType} / decision / summary /
            // issues[severity,category,description,suggestedFix]) rather than the legacy
            // {issues, verdict} cell shape ReviewProducerHelper also accepts. Pinned so the
            // 41-28 review stage cannot regress them to the legacy half.
            [("ux_designer", "review-design")] = "review",
            [("ux_designer", "audit-accessibility")] = "review",
            // The three Epic 41 triage cells instruct the 26-1 TriageDecision wire
            // (priority/type/complexity/automation closed enums + required reasoning) —
            // the same wire (product_owner, triage-intake) is BOUND to. Closed enums plus a
            // required field make acceptance discriminating. 41-11 / 41-17 / 41-16 bind them.
            [("architect", "triage-tech-debt")] = "triage-decision",
            [("senior_developer", "triage-pr")] = "triage-decision",
            [("tester", "manage-regression")] = "triage-decision",
        };

    // ====================================================================
    // Cells that produce no registered document at all
    // ====================================================================

    /// <summary>
    /// Taxonomy cells that NO registered document type claims: nothing names them as a
    /// producer — no <c>// Producing cell</c> comment in <c>Tamma.Core/Documents/Types/*.cs</c>,
    /// no <c>Prose.cs</c> kind→story seed, no <c>RolePhaseMap</c> producer note, no
    /// <c>ContractBindingTests.Bindings</c> entry — so there is no type to validate their
    /// worked example against and no conformance to assert. Each entry carries the reason.
    ///
    /// <para><b>This is NOT the same set as</b> <c>ContractBindingTests.IntentionallyUnbound</c>.
    /// That one answers "this DISPATCHED pair's caller slices no structured reply" and only
    /// ever covers pairs a compiled workflow emits. This one answers "this TAXONOMY cell
    /// mints no document", and covers every cell in the grid, dispatched or not.</para>
    ///
    /// <para><b>What an entry costs.</b> It is a claim that the cell is outside the document
    /// vocabulary — so it is the ONE classification that turns the gate off for a cell. If a
    /// story later declares an intended type for one of these, its entry moves to
    /// <see cref="ConformingUnboundCells"/> (rewriting the template in the same change) — it
    /// does not stay here with a "future work" note.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), string> IntentionallyUnboundCells =
        new Dictionary<(string, string), string>
        {
            // ---- context scans: cell-local scan shape, consumed as free text ---------
            // All eleven instruct the same cell-local {relevantFiles, …} shape and are read
            // verbatim by ContextGatheringWorkflow.Extract. The Findings-PRODUCING scan is the
            // separate (developer, triage-context-scan) cell minted by 39-15 D5 precisely so a
            // document producer never shares a cell with a free-text scan.
            [("architect", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract); the Findings producer is the split (developer, triage-context-scan) cell",
            [("developer", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract); the Findings producer is the split (developer, triage-context-scan) cell",
            [("devops", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract)",
            [("product_owner", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract)",
            [("project_manager", "context-scan")] = "free-text scan findings; no compiled dispatch site and no document type",
            [("scrum_master", "context-scan")] = "free-text scan findings; no compiled dispatch site and no document type",
            [("security", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract)",
            [("senior_developer", "context-scan")] = "free-text scan findings; no compiled dispatch site and no document type",
            [("tech_writer", "context-scan")] = "free-text scan findings; no compiled dispatch site and no document type",
            [("tester", "context-scan")] = "free-text scan findings (ContextGatheringWorkflow.Extract)",
            [("ux_designer", "context-scan")] = "free-text scan findings; no compiled dispatch site and no document type",

            // ---- planning-shaped cells on the legacy plan wire -----------------------
            // Each instructs tasks[] with files as {path, action} objects plus "dependencies" —
            // the legacy wire PlanDocumentType rejects (it wants files: string[] + dependsOn +
            // testing). NOTHING declares these as Plan producers, so there is no conformance to
            // assert; recorded here with the shape they carry so that if a story ever DOES bind
            // one to Plan, the rewrite is a known cost and the entry moves to the tables above.
            [("product_owner", "plan-scope")] = "scoping notes on the legacy plan wire; no story declares it a Plan producer",
            [("architect", "design-api-contract")] = "API-contract notes on the legacy plan wire; the declared Design producer is (architect, design-system)",
            [("architect", "design-data-model")] = "data-model notes on the legacy plan wire; the declared Design producer is (architect, design-system)",
            [("architect", "design-integration")] = "integration notes on the legacy plan wire; the declared Design producer is (architect, design-system)",
            [("developer", "plan-implementation")] = "implementation notes on the legacy plan wire; the bound Plan producers are (architect, plan-system-design) and (senior_developer, create-tasks)",
            [("developer", "plan-fix")] = "fix-planning notes on the legacy plan wire; no story declares it a Plan producer",
            [("developer", "plan-debugging")] = "debug-planning notes on the legacy plan wire; the Diagnosis producer is (senior_developer, debug-rootcause)",
            [("senior_developer", "plan-implementation")] = "implementation notes on the legacy plan wire; the bound Plan producer is (senior_developer, create-tasks)",
            [("senior_developer", "plan-refactor")] = "refactor notes on the legacy plan wire; no story declares it a Plan producer",
            [("devops", "plan-deployment")] = "deployment notes on the legacy plan wire; no story declares it a Plan producer",

            // ---- plan/task review lenses on the legacy verdict wire ------------------
            // Every one instructs the CURRENT reviewer cell shape — top-level issues[] plus a
            // verdict half — which ReviewProducerHelper.MapReviewerReply explicitly accepts and
            // folds onto Review (D4, path 2). The raw example is therefore not required to pass
            // ReviewDocumentType.Validate on its own, and pinning it as a Review producer would
            // fail a shape the runtime deliberately supports.
            [("architect", "plan-review")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("senior_developer", "plan-review")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("security", "plan-review-security")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("developer", "review-feasibility")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("tester", "review-testability")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("devops", "review-operability")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("product_owner", "review-scope")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("tech_writer", "review-docs")] = "plan-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("product_owner", "review-acceptance")] = "acceptance-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",
            [("architect", "assess-technical-risk")] = "risk-review lens on the legacy {issues, verdict} wire that ReviewProducerHelper maps onto Review",

            // ---- code-review lenses on the legacy code-review wire -------------------
            // All instruct issues[] of {file, line, severity, category, issue, fix} — the diff
            // review shape. CodeReviewWorkflow.StoreAnalysis keeps the raw text; where a review
            // document IS minted it goes through ReviewProducerHelper's legacy path.
            [("developer", "code-review")] = "diff-review lens: legacy code-review issue wire, raw text kept by CodeReviewWorkflow.StoreAnalysis",
            [("senior_developer", "code-review")] = "diff-review lens: legacy code-review issue wire, raw text kept by CodeReviewWorkflow.StoreAnalysis",
            [("architect", "code-review-architecture")] = "diff-review lens: legacy code-review issue wire",
            [("security", "code-review-security")] = "diff-review lens: legacy code-review issue wire",
            [("tester", "code-review-coverage")] = "diff-review lens: legacy code-review issue wire",
            [("developer", "self-review")] = "diff-review lens: legacy code-review issue wire, consumed before PR creation",
            [("security", "audit-dependencies")] = "audit lens on the legacy code-review issue wire; no document type claims it",
            [("security", "audit-secrets")] = "audit lens on the legacy code-review issue wire; no document type claims it",
            [("security", "review-compliance")] = "compliance lens on the legacy code-review issue wire; no document type claims it",
            [("senior_developer", "mentor-feedback")] = "free-text mentoring guidance posted verbatim; MentorshipWorkflow discards the structured half",

            // ---- triage-panel lenses -------------------------------------------------
            // RolePhaseMap.GetTriageActionForRole's panel arms. The panel's TriageDecision draft
            // is produced by the SEPARATE bound cell (product_owner, triage-intake); these replies
            // are critiques aggregated by ReviewPanelAggregation, not documents.
            [("developer", "triage-defect")] = "triage-panel lens (GetTriageActionForRole); the TriageDecision producer is the bound (product_owner, triage-intake) cell",
            [("tester", "triage-defect")] = "triage-panel lens (GetTriageActionForRole); the TriageDecision producer is the bound (product_owner, triage-intake) cell",
            [("security", "assess-vulnerability")] = "triage-panel lens (GetTriageActionForRole) on the legacy {issues, verdict} wire",
            [("devops", "diagnose-incident")] = "triage-panel lens (GetTriageActionForRole); RolePhaseMap keeps it the review lens while (devops, incident-rootcause) is the Diagnosis producer",
            [("architect", "triage-technical")] = "technical-triage lens on the retired P0-P3 / severity / ownerRole vocabulary; no document type claims it",
            [("senior_developer", "triage-technical")] = "technical-triage lens on the retired P0-P3 / severity / ownerRole vocabulary; no document type claims it",

            // ---- operational assessments on the retired triage vocabulary ------------
            [("devops", "monitor-health")] = "health report on the retired P0-P3 / severity / ownerRole vocabulary; no document type claims it",
            [("devops", "assess-capacity")] = "capacity report on the retired P0-P3 / severity / ownerRole vocabulary; no document type claims it",

            // ---- cell-local diagnosis-shaped analyses --------------------------------
            [("senior_developer", "resolve-blocker")] = "cell-local {diagnosis, …} shape; ClassifyBlockerActivity.ParseAIDiagnosis treats every field as optional and degrades to heuristics",
            [("security", "analyze-security-incident")] = "cell-local {diagnosis, …} incident shape; no document type claims it",

            // ---- code / file-format output, consumed via the success flag ------------
            [("developer", "implement-feature")] = "file-format code output; callers read only the llm-call success flag",
            [("developer", "implement-fix")] = "file-format code output; callers read only the llm-call success flag",
            [("developer", "refactor")] = "file-format code output; callers read only the llm-call success flag",
            [("developer", "debug")] = "file-format code output; DebuggingWorkflow.applyFix reads only the llm-call success flag",
            [("developer", "address-review-comments")] = "patch text; ReviewFixWorkflow.ExtractGenerateSuccess reads only the llm-call success flag",
            [("developer", "write-tests")] = "file-format test code; the TestSpec producer is the SEPARATE bound (tester, write-tests) cell",
            [("devops", "implement-infrastructure")] = "file-format infrastructure code; callers read only the llm-call success flag",
            [("devops", "configure-cicd")] = "file-format pipeline configuration; callers read only the llm-call success flag",

            // ---- free-text summaries --------------------------------------------------
            [("product_owner", "summarize-stakeholder")] = "free text; ContextGatheringWorkflow.ExtractPO falls back to the raw text as the summary",
            [("senior_developer", "summarize-technical")] = "free-text technical summary; no document type claims it",
            [("tech_writer", "summarize-changes")] = "free-text PR description; PullRequestWorkflow.CaptureDescription takes the raw text",

            // ---- bespoke cell-local shapes --------------------------------------------
            [("ux_designer", "draft-user-flow")] = "cell-local {summary, flows[screens…]} shape; the UxSpec producer is (ux_designer, author-ui-spec)",
            [("tester", "write-test-cases")] = "file-format test code (no JSON fence); the TestSpec producer is the bound (tester, write-tests) cell",
        };

    // ====================================================================
    // Extraction — mirrors the runtime ingest path
    // ====================================================================

    /// <summary>The template idiom: the instructed reply shape is a fenced ```json block.</summary>
    private static readonly Regex JsonFence = new("```json\\s*\\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// A closed-set placeholder value: <c>a|b|c</c> (optionally spaced, as in
    /// <c>urgent | high | normal | low</c>) — the RenderContract idiom for "one of".
    /// </summary>
    private static readonly Regex ClosedSetPlaceholder = new(@"^\s*[A-Za-z0-9_.\-]+(\s*\|\s*[A-Za-z0-9_.\-]+)+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Extract the worked example from a template body: take the LAST ```json fence
    /// (the instructed output shape), then apply the exact carve the runtime applies
    /// to a reply — first <c>{</c> … last <c>}</c>, must parse
    /// (<c>DocumentLifecycleWorkflow.ExtractJsonObject</c>). Returns the parsed
    /// object, or a failure reason naming what is missing.
    /// </summary>
    internal static (JsonElement? Example, string? FailureReason) ExtractExample(string template)
    {
        var matches = JsonFence.Matches(template);
        if (matches.Count == 0)
            return (null, "the template has no ```json fenced example block");

        var body = matches[^1].Groups[1].Value;
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start)
            return (null, "the ```json fence contains no {…} JSON object — the lifecycle's ExtractJsonObject " +
                          "carve (first '{' … last '}') would reject the instructed reply outright, so a " +
                          "conforming reply cannot even be ingested");

        var candidate = body[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException e)
        {
            return (null, $"the fenced example does not parse as JSON ({e.Message})");
        }
    }

    /// <summary>
    /// Replace every closed-set placeholder string value (<c>"low|medium|high"</c>)
    /// with its first alternative, recursively. Everything else passes through
    /// verbatim, so structural drift still fails validation.
    /// </summary>
    internal static JsonElement NormalizeClosedSetPlaceholders(JsonElement example)
    {
        var normalized = NormalizeNode(JsonNode.Parse(example.GetRawText()));
        using var doc = JsonDocument.Parse(normalized?.ToJsonString() ?? "null");
        return doc.RootElement.Clone();
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var newObj = new JsonObject();
                foreach (var (key, value) in obj)
                    newObj[key] = NormalizeNode(value);
                return newObj;
            case JsonArray arr:
                var newArr = new JsonArray();
                foreach (var item in arr)
                    newArr.Add(NormalizeNode(item));
                return newArr;
            case JsonValue value when value.TryGetValue<string>(out var s) && ClosedSetPlaceholder.IsMatch(s):
                return JsonValue.Create(s.Split('|')[0].Trim());
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }

    // ====================================================================
    // Evaluation core
    // ====================================================================

    /// <summary>
    /// Resolve the registered <see cref="IDocumentType"/> a ContractBindingTests
    /// parser-authority string (<c>"PlanDocumentType.Validate"</c>) names.
    /// </summary>
    private static IDocumentType ResolveByValidatorAuthority(string parserAuthority)
    {
        var typeName = parserAuthority[..^".Validate".Length];
        var match = DocumentTypeRegistry.All.SingleOrDefault(t => t.GetType().Name == typeName);
        match.Should().NotBeNull(
            $"the binding authority '{parserAuthority}' names no registered IDocumentType — " +
            "DocumentTypeRegistry.All has no implementation whose CLR type is " + typeName);
        return match!;
    }

    /// <summary>
    /// Evaluate one cell against a document type. Returns <c>null</c> when the
    /// template's worked example CONFORMS (extractable + valid), else a description
    /// of the non-conformance. Uses the context-free <c>Validate</c> — cross-document
    /// rules (e.g. TestSpec's CASE_UNKNOWN_TASK_ID) need a consumed document and are
    /// out of scope for a shipped static example.
    /// </summary>
    private static string? EvaluateNonConformance(string role, string action, IDocumentType type)
    {
        var template = SystemPrompts.GetRoleAction(role, action);
        if (template is null)
            return "no shipped template exists for the cell";

        var (example, reason) = ExtractExample(template.Template);
        if (example is null)
            return reason;

        var result = type.Validate(NormalizeClosedSetPlaceholders(example.Value));
        if (result.IsValid)
            return null;

        return string.Join("; ", result.Violations.Select(v => $"{v.Code}: {v.Message}"));
    }

    // ====================================================================
    // Test 1 — every DocumentType-bound cell's worked example validates
    // ====================================================================

    [Test]
    public void EveryDocumentTypeBoundCell_ShippedExampleValidatesAgainstItsBoundType()
    {
        var boundCells = ContractBindingTests.DocumentTypeValidatedCells;
        boundCells.Should().NotBeEmpty(
            "the DocumentType-backed subset of ContractBindingTests.Bindings came back empty — " +
            "this gate would be a no-op (ContractBindingTests' universal pin should also be failing)");

        var violations = new List<string>();
        foreach (var ((role, action), parserAuthority) in boundCells.OrderBy(kv => kv.Key))
        {
            var type = ResolveByValidatorAuthority(parserAuthority);
            var nonConformance = EvaluateNonConformance(role, action, type);
            if (nonConformance is not null)
            {
                violations.Add(
                    $"  ({role}, {action}) → {type.GetType().Name}: {nonConformance}" + Environment.NewLine +
                    $"      fix Prompts/{role}/{action}.md so its worked example is a VALID '{type.Key}' document " +
                    "(mirror the type's RenderContract; the 39-15 D7 rewrite precedent). A bound cell may NOT be " +
                    "baselined in KnownNonConformingTemplates.");
            }
        }

        violations.Should().BeEmpty(
            "every DocumentType-bound prompt cell's shipped worked example must actually validate against the " +
            "document type its callers validate with — a template that instructs the wrong shape makes every " +
            "produce through the cell a guaranteed validation failure at runtime while all token-presence tests " +
            "stay green. Non-conforming templates:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    // ====================================================================
    // Test 2 — the ratchet: entries must still be non-conforming, and may only shrink
    // ====================================================================

    [Test]
    public void KnownNonConformingTemplates_AreStillNonConforming_AndCountOnlyShrinks()
    {
        KnownNonConformingTemplates.Should().HaveCount(KnownNonConformingTemplateCount,
            "the baseline is count-pinned; the pin may only ever DECREASE (delete an entry + decrement " +
            "the pin when its owning Epic 41 story rewrites the template) — never add entries");

        var problems = new List<string>();
        foreach (var ((role, action), entry) in KnownNonConformingTemplates.OrderBy(kv => kv.Key))
        {
            entry.Reason.Should().NotBeNullOrWhiteSpace("every baseline entry must say why it does not conform");
            entry.OwningStory.Should().NotBeNullOrWhiteSpace("every baseline entry must name its owning Epic 41 story");

            if (SystemPrompts.GetRoleAction(role, action) is null)
            {
                problems.Add($"  ({role}, {action}): baselined but no shipped template exists — the cell left the " +
                             "taxonomy; delete the entry");
                continue;
            }

            if (!TryResolveRegisteredType(entry.IntendedDocumentTypeKey, out var type))
            {
                // Every planned Epic 41 key (test-plan, acceptance-criteria,
                // backlog-ordering via 41-1b; prose via 41-1c) is registered now,
                // so an unresolvable intended key can only be a typo.
                problems.Add($"  ({role}, {action}): intended document type '{entry.IntendedDocumentTypeKey}' is " +
                             "not registered in DocumentTypeRegistry — every planned Epic 41 key has landed, " +
                             "so this is a typo; fix the key");
                continue;
            }

            var nonConformance = EvaluateNonConformance(role, action, type!);
            if (nonConformance is null)
                problems.Add($"  ({role}, {action}): baselined as non-conforming but its example now VALIDATES " +
                             $"against '{type!.Key}' — delete its KnownNonConformingTemplates entry and decrement " +
                             "the count pin (the ratchet only turns one way)");
        }

        problems.Should().BeEmpty(
            "KnownNonConformingTemplates must list ONLY cells whose shipped template still fails " +
            "example-conformance:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static bool TryResolveRegisteredType(string key, out IDocumentType? type)
    {
        type = DocumentTypeRegistry.All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
        return type is not null;
    }

    // ====================================================================
    // Test 3 — baseline entries must be UNBOUND cells
    // ====================================================================

    [Test]
    public void KnownNonConformingTemplates_OnlyBaselineUnboundCells()
    {
        // A bound cell's example non-conformance is a live runtime defect (its produce
        // validates through the type TODAY) — it must be FIXED, never baselined. When an
        // Epic 41 story binds a baselined cell, the SAME change must rewrite the template
        // to conform (the thin-binding D7 rewrite precedent) and delete the entry here.
        var bound = ContractBindingTests.AllBoundCells.ToHashSet();
        var boundBaselined = KnownNonConformingTemplates.Keys
            .Where(bound.Contains)
            .Select(k => $"  ({k.Role}, {k.Action})")
            .ToList();

        boundBaselined.Should().BeEmpty(
            "every KnownNonConformingTemplates entry must be an UNBOUND cell — binding a cell requires " +
            "rewriting its template to conform and deleting its baseline entry in the same change:" +
            Environment.NewLine + string.Join(Environment.NewLine, boundBaselined));
    }

    // ====================================================================
    // Test 4 — unbound cells with a REGISTERED intended type must conform
    // ====================================================================

    [Test]
    public void EveryConformingUnboundCell_ShippedExampleValidatesAgainstItsIntendedType()
    {
        ConformingUnboundCells.Should().NotBeEmpty(
            "the unbound-cell gate must at least cover the two cells whose drift motivated it " +
            "(plan-sprint → sprint-plan, author-ui-spec → ux-spec; adversarial review 2026-07-29)");

        var bound = ContractBindingTests.AllBoundCells.ToHashSet();
        var violations = new List<string>();
        foreach (var ((role, action), typeKey) in ConformingUnboundCells.OrderBy(kv => kv.Key))
        {
            bound.Should().NotContain((role, action),
                $"({role}, {action}) is BOUND in ContractBindingTests — bound cells are test 1's job; " +
                "delete its ConformingUnboundCells entry in the same change that bound it");
            KnownNonConformingTemplates.Should().NotContainKey((role, action),
                $"({role}, {action}) cannot be both pinned conforming here and baselined non-conforming " +
                "in KnownNonConformingTemplates — the two sets are complements");

            TryResolveRegisteredType(typeKey, out var type).Should().BeTrue(
                $"({role}, {action}) declares intended document type '{typeKey}', which is not registered " +
                "in DocumentTypeRegistry — fix the key (or the entry is premature)");

            var nonConformance = EvaluateNonConformance(role, action, type!);
            if (nonConformance is not null)
            {
                violations.Add(
                    $"  ({role}, {action}) → {type!.GetType().Name}: {nonConformance}" + Environment.NewLine +
                    $"      fix Prompts/{role}/{action}.md so its worked example is a VALID '{type.Key}' document " +
                    "(mirror the type's RenderContract) — the 41-1a cross-lane contract promises the template " +
                    "instructs the shape its consumer story will pin, BEFORE the cell is bound.");
            }
        }

        violations.Should().BeEmpty(
            "an unbound cell whose intended document type is already registered must instruct a conforming " +
            "worked example — otherwise its consumer story binds against a template whose own example fails " +
            "the validator on day one, and no bound-cell gate catches it until then:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    // ====================================================================
    // Test 5 — EXHAUSTIVE classification: every taxonomy cell, exactly one bucket
    // ====================================================================

    /// <summary>
    /// The four classifications a taxonomy cell can carry, for the completeness sweep.
    /// </summary>
    private enum Classification
    {
        Bound,
        ConformingUnbound,
        Baselined,
        IntentionallyUnbound,
    }

    /// <summary>
    /// The authoritative cell set: <see cref="RolePhaseMap.EligibleActions"/> — the SPEC §4
    /// per-role eligibility matrix every resolver validates against. Cross-checked against
    /// the embedded prompt-file grid so neither source can drift silently behind the gate.
    /// </summary>
    private static IReadOnlyList<(string Role, string Action)> AllTaxonomyCells() =>
        RolePhaseMap.EligibleActions
            .SelectMany(kv => kv.Value.Select(a => (Role: kv.Key.ToWire(), Action: a.ToWire())))
            .OrderBy(c => c.Role, StringComparer.Ordinal)
            .ThenBy(c => c.Action, StringComparer.Ordinal)
            .ToList();

    [Test]
    public void TaxonomyCellSet_MatchesTheEmbeddedPromptFileGrid()
    {
        // Defence in depth for test 5's derivation. PromptFileLoader already fails loud at
        // static init on a taxonomy cell with no file (PROMPT.SEED.NO_BODY_FAMILY) or a file
        // outside the taxonomy (PROMPT.SEED.UNKNOWN_CELL); asserting it HERE makes the
        // completeness sweep's authority explicit rather than assumed, so "the gate enumerates
        // every cell" cannot quietly become "the gate enumerates every cell one source knows".
        var fromMatrix = AllTaxonomyCells().ToHashSet();
        var fromFiles = SystemPrompts.RoleActionTemplates
            .Select(t => (Role: t.Role!, t.Action))
            .ToHashSet();

        fromMatrix.Should().BeEquivalentTo(fromFiles,
            "the RolePhaseMap eligibility matrix and the embedded Prompts/{role}/{action}.md grid must " +
            "describe the SAME cell set — test 5 derives the universe it sweeps from the matrix");
    }

    [Test]
    public void EveryTaxonomyCell_IsClassifiedExactlyOnce()
    {
        var cells = AllTaxonomyCells();
        cells.Should().NotBeEmpty("the taxonomy came back empty — the completeness sweep would be a no-op");

        var bound = ContractBindingTests.AllBoundCells.ToHashSet();

        // ---- (a) no classification may name a cell outside the taxonomy -------------
        var known = cells.ToHashSet();
        var strays = new List<string>();
        strays.AddRange(ConformingUnboundCells.Keys.Where(k => !known.Contains(k))
            .Select(k => $"  ConformingUnbound: ({k.Role}, {k.Action})"));
        strays.AddRange(KnownNonConformingTemplates.Keys.Where(k => !known.Contains(k))
            .Select(k => $"  KnownNonConformingTemplates: ({k.Role}, {k.Action})"));
        strays.AddRange(IntentionallyUnboundCells.Keys.Where(k => !known.Contains(k))
            .Select(k => $"  IntentionallyUnboundCells: ({k.Role}, {k.Action})"));

        strays.Should().BeEmpty(
            "a classification entry naming a (role, action) that is not in the taxonomy is dead weight — the " +
            "cell left RolePhaseMap; delete the entry:" + Environment.NewLine + string.Join(Environment.NewLine, strays));

        // ---- (b) every entry that claims a reason must carry one --------------------
        var blank = IntentionallyUnboundCells
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"  ({kv.Key.Role}, {kv.Key.Action})")
            .ToList();
        blank.Should().BeEmpty(
            "IntentionallyUnboundCells is the one classification that switches the conformance gate OFF for a " +
            "cell — every entry must say WHY no registered document type claims it:" + Environment.NewLine +
            string.Join(Environment.NewLine, blank));

        // ---- (c) the sweep: exactly one classification per cell ---------------------
        var unclassified = new List<string>();
        var overlapping = new List<string>();

        foreach (var cell in cells)
        {
            var buckets = new List<Classification>();
            if (bound.Contains(cell)) buckets.Add(Classification.Bound);
            if (ConformingUnboundCells.ContainsKey(cell)) buckets.Add(Classification.ConformingUnbound);
            if (KnownNonConformingTemplates.ContainsKey(cell)) buckets.Add(Classification.Baselined);
            if (IntentionallyUnboundCells.ContainsKey(cell)) buckets.Add(Classification.IntentionallyUnbound);

            if (buckets.Count > 1)
            {
                overlapping.Add(
                    $"  ({cell.Role}, {cell.Action}): classified {buckets.Count} times — " +
                    string.Join(" + ", buckets) + ". The four classifications are mutually exclusive; " +
                    "a cell has exactly one status. Delete all but the true one.");
                continue;
            }

            if (buckets.Count == 0)
                unclassified.Add(DescribeUnclassified(cell));
        }

        overlapping.Should().BeEmpty(
            "a taxonomy cell may hold only ONE classification:" + Environment.NewLine +
            string.Join(Environment.NewLine, overlapping));

        unclassified.Should().BeEmpty(
            "EVERY cell in the taxonomy must be accounted for by exactly one classification. An unclassified " +
            "cell is a cell this fixture never looks at — which is precisely how a template can drift into " +
            "instructing a shape its own registered validator rejects while every test stays green " +
            "((security, threat-model) did exactly that under the old 5-entry allowlist). Classify each cell " +
            "below — do NOT reach for the ratchet unless the example genuinely fails its intended type:" +
            Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    /// <summary>
    /// Build the actionable failure text for one unclassified cell: what its template
    /// actually instructs, which registered validators accept it, and the exact entry to
    /// add for each of the three author-owned classifications.
    /// </summary>
    private static string DescribeUnclassified((string Role, string Action) cell)
    {
        var (role, action) = cell;
        var template = SystemPrompts.GetRoleAction(role, action);

        string evidence;
        var accepting = new List<string>();
        if (template is null)
        {
            evidence = "no shipped template — PromptFileLoader should have refused to start; investigate first";
        }
        else
        {
            var (example, reason) = ExtractExample(template.Template);
            if (example is null)
            {
                evidence = $"the template instructs NO ingestible JSON example ({reason})";
            }
            else
            {
                var normalized = NormalizeClosedSetPlaceholders(example.Value);
                accepting = DocumentTypeRegistry.All
                    .Where(t => t.Validate(normalized).IsValid)
                    .Select(t => t.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                evidence = accepting.Count == 0
                    ? "the template's fenced example validates against NO registered document type"
                    : "the template's fenced example validates against: " + string.Join(", ", accepting);
            }
        }

        var caveat = accepting.Contains("diagnosis")
            ? Environment.NewLine +
              "      NOTE: 'diagnosis' declares no required members, so it accepts almost any JSON object — " +
              "its presence above is NOT evidence of intent."
            : "";

        return
            $"  ({role}, {action}) — UNCLASSIFIED. {evidence}{caveat}" + Environment.NewLine +
            "      Add EXACTLY ONE of:" + Environment.NewLine +
            $"        1. ConformingUnboundCells[(\"{role}\", \"{action}\")] = \"<registered key>\"  — when a landed " +
            "artifact ties the cell to a registered type (a `// Producing cell` comment in " +
            "Tamma.Core/Documents/Types/*.cs, the Prose.cs kind→story seed, a RolePhaseMap producer note) OR the " +
            "template demonstrably instructs that type's wire, AND the example validates today." + Environment.NewLine +
            $"        2. KnownNonConformingTemplates[(\"{role}\", \"{action}\")] = new(\"<key>\", \"<story>\", \"<why>\") " +
            "— same intent, but the example does NOT validate. Ratchet debt: it must name the story that will fix " +
            "it, and the count pin moves with it. Never use this to silence a cell you have not checked." + Environment.NewLine +
            $"        3. IntentionallyUnboundCells[(\"{role}\", \"{action}\")] = \"<why nothing claims it>\"  — when " +
            "NOTHING names the cell as a document producer (free text, code/file-format output, a review or triage " +
            "lens whose reply the runtime maps itself). This switches the gate off for the cell, so it is a claim, " +
            "not a default." + Environment.NewLine +
            $"      If the cell IS bound, add it to ContractBindingTests.Bindings instead — test 1 then owns it.";
    }

    // ====================================================================
    // Test 6 — extractor/normalizer behavior pins
    // ====================================================================

    [Test]
    public void Extractor_CarvesTheLastJsonFence_AndFailsLoudWithoutOne()
    {
        var template = """
            Some instructions.
            ```json
            {"first": true}
            ```
            More prose, then the instructed output shape:
            ```json
            {"tasks": [{"id": "T1"}]}
            ```
            """;
        var (example, reason) = ExtractExample(template);
        reason.Should().BeNull();
        example!.Value.TryGetProperty("tasks", out _).Should().BeTrue("the LAST fence is the instructed output shape");

        var (none, noneReason) = ExtractExample("no fenced example here");
        none.Should().BeNull();
        noneReason.Should().Contain("no ```json fenced example block");

        var (bareArray, arrayReason) = ExtractExample("```json\n[\"just\", \"strings\"]\n```");
        bareArray.Should().BeNull(
            "a bare array instructs a reply the lifecycle's first-'{'-to-last-'}' carve can never ingest");
        arrayReason.Should().Contain("no {…} JSON object");
    }

    [Test]
    public void Normalizer_ReplacesClosedSetPlaceholders_AndOnlyThose()
    {
        using var doc = JsonDocument.Parse("""
            {
              "severity": "low|medium|high",
              "priority": "urgent | high | normal | low",
              "url": "https://example.com/a",
              "path": "src/Foo.cs",
              "text": "either this or that",
              "nested": [{"type": "bug|feature"}],
              "count": 3
            }
            """);
        var normalized = NormalizeClosedSetPlaceholders(doc.RootElement);

        normalized.GetProperty("severity").GetString().Should().Be("low");
        normalized.GetProperty("priority").GetString().Should().Be("urgent");
        normalized.GetProperty("url").GetString().Should().Be("https://example.com/a", "URLs are not alternations");
        normalized.GetProperty("path").GetString().Should().Be("src/Foo.cs");
        normalized.GetProperty("text").GetString().Should().Be("either this or that", "prose with spaces is not an alternation");
        normalized.GetProperty("nested")[0].GetProperty("type").GetString().Should().Be("bug");
        normalized.GetProperty("count").GetInt32().Should().Be(3);
    }
}
