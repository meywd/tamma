# Story 41-3: Backlog Prioritization & Grooming Workflow

Status: drafted

## User Story

As a **product owner** (or eligible role-holder), I want a workflow that ranks a set of backlog items into
a typed `BacklogOrdering` on the lifecycle — with value/effort rationale per item — so that prioritisation
is explicit, reviewed, accepted, and consumable by sprint planning, instead of an ad-hoc reorder.

## Priority

P2 / Wave 3 — feeds 41-6 sprint planning and 41-4 roadmap.

## Scope

Thin binding over `document-lifecycle`. `consumes: [backlog items (issues), TriageDecisions ~~from
41-11/41-16/41-17~~, Findings]` / `produces: BacklogOrdering`. Produce cell
`(product_owner, prioritize-backlog)`. *[AMENDED 2026-08-01 — 41-11/41-16/41-17 will add producers, but
they are not the source today: `triage-decision` and `findings` already have landed producers
(`TriagePODecisionWorkflow`, `TriageContextGatheringWorkflow`, `ResearchWorkflow`). See Amendment A1.]*

The cell exists and nothing dispatches it — no 41-1a work here. What IS in scope is a **template
rewrite**: the shipped `Prompts/product_owner/prioritize-backlog.md` ranks ONE item and emits a
`TriageDecision`-shaped payload (P0–P3, `ownerRole`), not a total order over a set. It is rewritten to the
`BacklogOrdering` contract (39-15 D7 precedent; the exact target is pinned in **Amendment A4**).

Evidence gathering is caller-supplied item set + bounded per-item store reads (the store has no
repository-wide query), ~~degrading gracefully to issue text when the upstream producers (41-11/41-16/41-17
— not yet built) have written nothing~~ *[AMENDED 2026-08-01 — graceful degradation still holds; the
premise did not. Evidence exists in tree today and is reachable only at the right anchors, which is what
Amendment A1 / AC2 fixes.]*, **read at both findings anchors per item**, and degrading gracefully to issue
text when an item genuinely has none.

## Produced document

`BacklogOrdering` (41-1): total order over the referenced item set; every item has a rationale +
value/effort estimate; no ties. `tenantId`/`repository` lineage.

## Events

`BACKLOG.GROOMING.STARTED` → `.ORDERED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted ordering is the input 41-6 reads. Large reprioritisations
affecting committed work can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent proposes an ordering; PO accepts.
- **85–100:** agent orders and self-accepts within policy; reordering above a churn threshold escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `BacklogOrdering` validated (total order, rationale per item, no ties).
   Includes the `prioritize-backlog` template rewrite (see Scope, and **AC7** for what the rewrite must
   produce).
2. ~~Consumes upstream `TriageDecision`/`Findings` as ranking evidence via bounded per-item reads over the
   caller-supplied item set; absent evidence degrades to issue text and never hard-fails (41-11/41-16/41-17
   do not exist yet).~~ *[AMENDED 2026-08-01 — the old text could not fail: "consumes Findings" was
   satisfiable by a read that structurally cannot return the Findings that exist. See Amendment A1.]*

   **Ranking evidence is read at BOTH findings anchors, per item, and absence is never fatal.** For each
   item of the caller-supplied set (capped, see D3) the binding performs bounded fail-closed reads through
   `FetchLatestAcceptedDocumentActivity` (`Found=false` ⇒ skip, never throw):
   - `("triage-decision", itemIssueId)` — the bare item id;
   - `("findings", itemIssueId)` — the bare item id (**`ResearchWorkflow`'s** anchor);
   - `("findings", CreationBindingHelper.ScopeIssueId(itemIssueId, "triage-context"))` —
     **`TriageContextGatheringWorkflow`'s** anchor.

   `itemIssueId` is required to be in `CreationBindingHelper.DeriveIssueId` form
   (`"{repository}#{issueNumber}"`) — that is the id the landed triage producers write under; an item
   supplied in any other form is recorded as an evidence miss, not silently treated as "no evidence".

   Failable tests: (i) a fixture seeding an accepted `findings` **only** at the `#triage-context` anchor
   must see it reach the producer variables — a single-anchor implementation fails; (ii) a fixture seeding
   a research `findings` at the bare id **and** a triage `findings` at the scoped id must surface both,
   each labelled with the anchor it came from — an implementation that reads one and calls it "the
   findings" fails; (iii) an item with neither still appears in the accepted ordering; (iv) the composed
   evidence value handed to the producer is asserted `< PromptStoreService.MaxVariableValueLength`
   (`100_000`, `Tamma.Api/Services/PromptStore/PromptStoreService.cs:96`) — a longer value is treated as
   UNRESOLVED and the literal `{{evidence}}` is left in the rendered prompt (`Render`, `:559-589`), so an
   unbounded accumulator silently ships a broken prompt.
3. Consumable by 41-6 via the 39-11 store, under the synthetic backlog anchor
   (`BacklogOrdering` is not issue-scoped; `DocumentInstance.IssueId` is the only read key). **The anchor
   and its segment normaliser are this story's public shared contract — see AC6.**
4. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.
5. **The cell GRADUATES with all five coordinated test-fixture edits in the same commit** (Amendment A2).
   Partial completion is a red build with a misleading message, so each edit is named here:
   (a) add `[("product_owner", "prioritize-backlog")]` to `ContractBindingTests.Bindings`
   (`tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:94`), carrying the six token groups
   verbatim from the pending entry;
   (b) **delete** its `PendingProducerCells` entry (same file, `:754-763`);
   (c) **delete** its `KnownNonConformingTemplates` entry
   (`tests/Tamma.Activities.Tests/Workflows/TemplateExampleConformanceTests.cs:130-132`);
   (d) decrement the ratchet pin `KnownNonConformingTemplateCount` **14 → 13** (`:207`);
   (e) **append `13` to `PinHistory`** — `[11, 16, 15, 14]` → `[11, 16, 15, 14, 13]` (`:224`).
   Failable: `TheRatchetPin_IsMechanicallyShrinkOnly` (`:609`) asserts
   `KnownNonConformingTemplateCount == PinHistory[^1]`, so doing (d) without (e) — or (e) without (d) —
   fails on a message about "an undeclared re-widening" that names neither this story nor the template.
   Doing (b) without (a) fails `EveryTaxonomyCell_IsClassifiedExactlyOnce` (`:796`); doing (a) without (b)
   fails `EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse`
   (`ContractBindingTests.cs:824`); doing (a) without (c) fails
   `KnownNonConformingTemplates_OnlyBaselineUnboundCells` (`:688`).
6. **`BacklogBindingHelper.BuildAnchor` and its segment normaliser are PUBLIC, and this story owns that
   contract** (Amendment A3). `BacklogBindingHelper` is a `public static class` in
   `Tamma.ElsaServer/Workflows/Helpers/` (every one of the 18 helper files there declares
   `public static class`) exposing at minimum
   `public static string BuildAnchor(string? repository, string? backlogScope)` and the segment normaliser
   it composes from, as a separately callable `public static` member — **not** a `private static` or an
   inline lambda. 41-6 calls `BuildAnchor` by name for its upstream read
   (`docs/stories/epic-41/story-41-6/implementation-plan.md:90,:349,:524-528`) and 41-4 does the same
   (`docs/stories/epic-41/story-41-4/implementation-plan.md:51,:93-96,:150`); both additionally build their
   own anchors "delegating to the same segment transform", which is only possible if the normaliser is
   callable. Failable: `BacklogBindingHelperTests` lives in `Tamma.Activities.Tests` (a different
   assembly) and calls both members directly — a `private` or inline-lambda normaliser does not compile.
   (Note the test does **not** catch `internal`: `Tamma.ElsaServer.csproj:15` has
   `<InternalsVisibleTo Include="Tamma.Activities.Tests" />`. `public` is required by the *sibling-story*
   contract, not by the compiler here — 41-4 and 41-6 happen to sit in the same assembly, so `internal`
   would compile for them too and quietly make the member unusable to anything else. Assert the modifier
   directly: `typeof(BacklogBindingHelper).GetMethod(nameof(BuildAnchor))!.IsPublic.Should().BeTrue()`,
   same for the normaliser.) Plus:
   `BuildAnchor` is deterministic (same inputs twice ⇒ byte-identical), total (null/empty/hostile
   characters ⇒ no throw), and its normaliser must guarantee no segment can contain the anchor delimiter,
   so a 3-segment `backlog:` anchor can never be forged from a 2-segment item key —
   `TriageItemCycleHelper.DeriveItemKey` (`:88-95`) emits `{repo}:{source}:{title}` and `{repo}:{source}`
   in the same colon-delimited shape, so the namespace is *not* naturally disjoint.
7. **The `prioritize-backlog` template rewrite produces the `backlog-ordering` wire** (Amendment A4).
   Failable: (a) the rewritten file's LAST fenced `json` block, carved as the runtime carves a reply
   (first `{` … last `}`, must parse), validates
   through `DocumentTypeRegistry.Resolve("backlog-ordering")` →
   `BacklogOrderingDocumentType.Validate` with **zero** violations — asserted by
   `TemplateExampleConformanceTests.EveryDocumentTypeBoundCell_ShippedExampleValidatesAgainstItsBoundType`
   (`:574`) the moment AC5(a) lands; (b) the body carries all six pinned tokens (`"items"`, `"itemId"`,
   `"rank"`, `"rationale"`, `"value"`, `"effort"`) —
   `ContractBindingTests.EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` (`:443`); (c) the
   body contains a literal `{{evidence}}` placeholder and the front matter declares `evidence` — the
   renderer substitutes on the **body's** `{{…}}` occurrences, not on the front-matter list
   (`PromptStoreService.Render`, `:559-589`), so a carrier declared but not placed is a silent no-op and a
   carrier placed but not declared is a lie in the front matter; (d) `maxTokens` is raised from `2048`
   (2048 cannot emit N rationales); (e) `version` is bumped `1 → 2`.

## Dependencies

- ~~**Blocking:** **41-1b** (`BacklogOrdering` type), Epic 39 (lifecycle, store, accept).~~
  *[AMENDED 2026-08-01 — 41-1b has landed; it is a shipped input, not a blocker.]*
- **Done, no longer blocking — 41-1b.** `DocumentTypeKey.BacklogOrdering`
  (`Tamma.Core/Documents/DocumentTypeKey.cs:40`), `BacklogOrdering` + `BacklogOrderingDocumentType`
  (`Documents/Types/BacklogOrdering.cs:38`), registry row (`Documents/DocumentTypeRegistry.cs:44`), and the
  acceptance row `DocumentTypeKey.BacklogOrdering => s_productOwnerRules`
  (`Documents/Policy/AcceptanceDefaults.cs:215`, a **single `product_owner` reviewer** — `:129-139`).
  Status `done`, `docs/sprint-status.yaml:630`.
- **Done, no longer blocking — 41-2's shared emitter.** `EmitDomainLifecycleEventActivity` is in tree
  (`Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs`), so the implementation plan's D7
  fallback ("carry a local copy") is moot.
- **Epic 39** — lifecycle / store / accept: landed (39-6/39-7/39-8/39-10/39-11).
- **NOT blocked by 41-1a** — the cell exists (`Tamma.Core/Agents/AgentAction.cs:26`,
  `Agents/RolePhaseMap.cs:53`, `Tamma.Api/Prompts/product_owner/prioritize-backlog.md`) and nothing
  dispatches it (repo-wide, the only `.cs` references are those two taxonomy rows plus the two test
  fixtures AC5 edits).
- **NOT blocked by 41-11 / 41-16 / 41-17** — and, per Amendment A1, not waiting on them for evidence
  either: `triage-decision` and `findings` have landed producers today.
- **Unblocks:** 41-6 (calls `BuildAnchor` by name), 41-4 (same), 44-3 (consumes the produced document).

## Estimated Effort

3–4 days *(the story header figure; the implementation plan costs 5.5 d and is the record of the delta.
The 2026-08-01 amendments do not move either number — AC5/AC6/AC7 write down work the plan already
scoped; AC2's second findings anchor is one extra read in an existing loop.)*

## Amendment — 2026-08-01 (scoping round: story vs. tree)

Every claim below was checked against the working tree at commit `6429691`. Where the story was wrong the
original text is struck through in place rather than removed.

**A1 — AC2's "consumes upstream `Findings`" was an empty claim: the read it described cannot return the
Findings that exist, and can return a different workflow's.**

The story used to say: *"Consumes upstream `TriageDecision`/`Findings` as ranking evidence via bounded
per-item reads over the caller-supplied item set; absent evidence degrades to issue text and never
hard-fails (41-11/41-16/41-17 do not exist yet)."* That is unfalsifiable — any single-anchor read passes
it, including one that never returns a Findings document. What is true:

- `findings` has **two** landed producers writing under **two different anchors**.
  `TriageContextGatheringWorkflow` sets its lifecycle `issueId` to
  `CreationBindingHelper.ScopeIssueId(baseId, "triage-context")`
  (`Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs:96`), i.e. `"{baseIssueId}#triage-context"`
  (`Helpers/CreationBindingHelper.cs:95-96`). `ResearchWorkflow` writes `findings` under the **bare**
  caller-supplied `issueId` (`Workflows/ResearchWorkflow.cs:91`, dispatch `:209-210`).
- The store has exactly one read key. `IDocumentInstanceRepository` exposes `GetByIdAsync`,
  `ListByIssueAsync(tenantId, issueId, audience, ct)` and
  `GetLatestAcceptedAsync(tenantId, issueId, ct)` (`Tamma.Data/Repositories/IDocumentInstanceRepository.cs:40,49-50,57`)
  — no by-type, by-repository or by-producer query, and `GetLatestAcceptedAsync` returns "the single latest
  accepted instance **per document type**" with no producer filter (`:52-56`).
- Therefore a read at `(item.issueId, "findings")` **never** returns the triage-context findings, and when
  a research findings exists for the same issue it returns **that** — a different workflow's document under
  the same type key. This is precisely the collision `ScopeIssueId`'s own doc comment says the suffix
  exists to prevent (`CreationBindingHelper.cs:85-94`), and the same lesson 41-9 recorded for
  `{issueId}#adr` (`docs/sprint-status.yaml:639`).
- **`triage-decision` is *not* affected.** `TriagePODecisionWorkflow` anchors on the bare
  `CreationBindingHelper.DeriveIssueId(repository, itemNumber)` when no explicit id is supplied
  (`Workflows/TriagePODecisionWorkflow.cs:105-108`), and `TriageItemCycleWorkflow` passes
  `TriageItemCycleHelper.DeriveItemKey` — which is `"{repo}#{number}"` for an issue
  (`Helpers/TriageItemCycleHelper.cs:85-86`) — as that `issueId` (`TriageItemCycleWorkflow.cs:174,:225`).
  One read at the item's own id is correct there, **provided** the id is in that exact form. AC2 now
  requires it.

**Resolution chosen: read BOTH anchors** (AC2), not "amend the claim down". The evidence exists in tree
today; reading one anchor is a bug, not a scoping limit.

**A2 — the graduation checklist is FIVE coordinated edits, and the story recorded none of them.**

The story used to say only "Includes the `prioritize-backlog` template rewrite (see Scope)". Binding the
cell moves it between four exhaustive classification tables plus a mechanically-asserted count pin.
Verified current values, all at commit `6429691`:

| # | Edit | Where | Current value |
|---|---|---|---|
| a | add the cell to the binding list | `ContractBindingTests.Bindings` (`ContractBindingTests.cs:94`) | absent |
| b | delete its pending-producer entry | `ContractBindingTests.PendingProducerCells` (`:754-763`) | present, 5 entries |
| c | delete its known-non-conforming template entry | `TemplateExampleConformanceTests.KnownNonConformingTemplates` (`:130-132`) | present |
| d | decrement the conformance pin | `KnownNonConformingTemplateCount` (`:207`) | `14` → `13` |
| e | append to the pin history | `PinHistory` (`:224`) | `[11, 16, 15, 14]` → `[11, 16, 15, 14, 13]` |

`TheRatchetPin_IsMechanicallyShrinkOnly` (`:609-631`) asserts
`KnownNonConformingTemplateCount.Should().Be(PinHistory[^1])` and that every history element from index 2
on is strictly smaller than its predecessor — so (d) without (e), or (e) without (d), is an automatic
failure whose message talks about "an undeclared re-widening" and names neither this story nor the
template. The precedents are in the fixture comments: 41-2 (16 → 15) and 41-9 (15 → 14) each performed all
five (`TemplateExampleConformanceTests.cs:124-128,:155-160,:194-199`;
`ContractBindingTests.cs:742-749`). The pending-producer table carries **no** count pin, so (b) is a plain
delete. Recorded as **AC5**.

**A3 — the anchor helper and its segment normaliser must be PUBLIC; this story owns the shared contract,
and one thing the implementation plan says about it is false.**

Two sibling stories already call the helper **by name**: 41-6 (`implementation-plan.md:90` "**41-3's**
`BacklogBindingHelper.BuildAnchor(repository, backlogScope)` — called, never re-derived"; `:349` "call — do
not copy"; `:524-528`) and 41-4 (`implementation-plan.md:51,:96,:150`). Both additionally state that their
*own* anchor builders (`SprintBindingHelper.BuildAnchor`, `RoadmapBindingHelper.BuildAnchor`) delegate to
"the same segment transform" (41-6 `:187-188`, 41-4 `:93-94`) and that their helper tests assert agreement
with it (41-6 `:435-436`, 41-4 `:231-232`). Neither sibling *names* that transform — so this story must
name it and expose it, or both siblings will copy it and the "provably consistent" assertion becomes two
divergent copies.

**Correction to this story's own implementation plan.** D2 (`implementation-plan.md:83-90`) says the anchor
is "folded through `CreationBindingHelper.ScopeIssueId`'s normalisation", and step 3 (`:144-145`) says
"normalised through the same segment transform `ScopeIssueId` uses". **There is no such transform.**
`ScopeIssueId` is pure concatenation:
`public static string ScopeIssueId(string? baseIssueId, string producer) => $"{baseIssueId ?? string.Empty}#{producer}";`
(`CreationBindingHelper.cs:95-96`). Nothing in that file trims, lowercases, or escapes a segment. 44-3
found the same thing independently
(`docs/stories/epic-44/story-44-3/44-3-hierarchy-ranking-and-the-backlogordering-apply-seam.md:112`). So
this story **authors** the normaliser rather than reusing one. All 18 helpers in
`Tamma.ElsaServer/Workflows/Helpers/` are `public static class`, and `Tamma.Activities.Tests` is a separate
assembly, so a public member is both the convention and what makes the helper tests compile. Recorded as
**AC6**.

**A4 — the template rewrite: what is shipped, and what it must become.**

Shipped `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/prioritize-backlog.md` (verified verbatim):
front matter `variables: role, issueJson, repoContext` / `enableTools: false` / `maxTokens: 2048` /
`version: 1`; body opens "You are a {{role}} prioritizing a backlog **item** — deciding where the issue
below ranks relative to comparable backlog work"; it takes a single `{{issueJson}}`; and its JSON fence
instructs `{type, severity, priority: "P0|P1|P2|P3", ownerRole, estimatedEffort, labels, relatedIssues,
reasoning}` — very nearly `TriageDecision`'s wire (`Tamma.Core/Documents/Types/TriageDecision.cs`), and a
**different document in a different shape** from what this story produces. It is a classification of one
item, not a total order over a set.

The rewrite target, confirmed against the real type:
- Document type: **`BacklogOrderingDocumentType`** (`Tamma.Core/Documents/Types/BacklogOrdering.cs:38`),
  wire key `backlog-ordering` (`Documents/DocumentTypeKey.cs:40`), registered at
  `Documents/DocumentTypeRegistry.cs:44`.
- Validator: `Validate(JsonElement)` → `ValidateCore` (`BacklogOrdering.cs:72-76`), violation codes
  `NO_ITEMS` (`:44`), `ITEM_ID_MISSING` (`:47`), `ITEM_ID_DUPLICATED` (`:54`), `RANK_DUPLICATED` (`:57`),
  `RANK_NOT_TOTAL_ORDER` (`:60`), `ITEM_MISSING_RATIONALE` (`:63`), `ITEM_MISSING_ESTIMATE` (`:66`).
- Wire: `{ "items": [ { "itemId", "rank", "rationale", "value", "effort" } ] }` with ranks the unique
  gap-free `1..N` sequence — pinned by the type's `Contract` const (`:196-213`) and Core-side by
  `RenderContractTokenTests.BacklogOrderingTokens`
  (`tests/Tamma.Core.Tests/Documents/Types/RenderContractTokenTests.cs:64-68`, 6 tokens). The template's
  token groups in `Bindings` must be those six, verbatim from the pending entry
  (`ContractBindingTests.cs:754-763`).

Recorded as **AC7**, including the two failure modes that are easy to get half-right: the `{{evidence}}`
carrier must appear **in the body** (the renderer scans the body's `{{…}}`, not the front-matter list —
`PromptStoreService.Render`, `:559-589`), and the composed evidence value must stay under
`MaxVariableValueLength` (`:96`, 100 000) or it is treated as unresolved and the literal `{{evidence}}`
ships in the prompt.

**A5 — stale literals in this story's implementation plan (do not edit the numbers it names).** The plan
was written before 41-1b/41-1c/41-2/41-9 landed. Corrected here so an implementer edits the right
literals; the plan's own text is annotated in place.
- `WorkflowInterfaceGraphTests` edge pin is **`:52` `HaveCount(18)`**, not "`:45` `HaveCount(16)`"; the
  bidirectional `reconciled` array is **`:109-138`** and now carries 15 ids (41-2's
  `acceptance-criteria-authoring` at `:134`, 41-9's `adr-authoring` at `:137`). Both edits still required.
  (The epic README's rule-1 clause (f) also still says "`:45`, `HaveCount(16)` today",
  `docs/stories/epic-41/README.md:42` — README is not this story's file; recorded, not edited.)
- `DocumentTypeKey` has **17** members, not 10 (`DocumentTypeKey.cs:24-49`). The plan's "exactly 10
  members today (verified)" was true when written and is not now.
- D8's premise is discharged: `AcceptanceDefaults.For` no longer falls through to `_ => Rules` for this
  type — 41-1b landed `DocumentTypeKey.BacklogOrdering => s_productOwnerRules`
  (`Documents/Policy/AcceptanceDefaults.cs:215`; the rules are a `SingleReviewer` `product_owner` row,
  `:129-139`). The plan's test (f) survives as a regression guard; the plan's "which is wrong for a backlog
  ordering" framing is stale.
- `TaxonomyDriftBuildTests` pins verified current: `MinExpectedDispatchPairs = 21` (`:110`, a floor — no
  edit), `ExpectedContributingWorkflows` (`:125`, subset floor — add
  `"BacklogPrioritizationWorkflow"`).

## Open items (not resolved by this amendment)

- **What `BacklogItem.itemId` MEANS is still unpinned, and 44-3 is waiting on this story to say.** 44-3's
  Cross-Story Contract C2
  (`docs/stories/epic-44/story-44-3/44-3-hierarchy-ranking-and-the-backlogordering-apply-seam.md:116`):
  `BacklogItem.ItemId` is a `string` validated only as non-blank (`Types/BacklogOrdering.cs:15`, `:105-110`),
  the shipped contract and both examples use `"issue-7"` (`:202,:222-227`), and 44-3's apply seam resolves
  each entry "by its `itemId` string to a work item in the project" — so if this story emits git issue
  numbers, **every entry resolves not-found** while both stories' tests stay green against their own
  fixtures. Either this story pins `itemId = work_items."Key"` (and feeds the workflow work-item keys), or
  44-3 resolves through `ExternalRefJson` too. **Not decided here** — it is a two-story product decision
  about what a backlog item *is*, and picking one unilaterally in a docs pass would hard-code a guess into
  both. Note it is a **different field** from AC2's `itemIssueId`: that one is the store read key and
  *must* be `"{repository}#{issueNumber}"` to hit the landed triage anchors. Fixing one does not fix the
  other, and they may legitimately differ.
- **The D2 anchor is not reconstructible from a tracker project id.** 44-3's C1 (`:114`): the anchor is
  keyed on `{repository}` + `{backlogScope}`, while `ProjectEntity` carries `RepositoryId` as a `Guid?`
  (`Tamma.Data/Entities/ProjectEntity.cs:37`) and has no `backlogScope` concept — so
  `POST /api/projects/{projectId}/apply-ordering` cannot derive it. 44-3 ships apply-by-`documentId` only
  unless this changes. Left open deliberately: the honest fix is a by-type/by-repository read on 39-11
  (already filed in this story's D2), not a second anchor convention invented here.
