# Implementation Plan — Story 41-3: Backlog Prioritization & Grooming Workflow

> **Amended 2026-08-01 (scoping round).** This plan was written before 41-1a/41-1b/41-1c/41-2/41-9 landed
> and before the evidence-anchor defect was found. Six claims are corrected **in place** below, each marked
> `[CORRECTED 2026-08-01]` with the struck original kept: D2/step 3 (there is no existing segment
> normaliser to reuse), D3/Correction 4 (evidence needs **two** findings anchors, and its producers already
> exist), D8 (41-1b landed the acceptance arm), step 5 (the edge pin moved 16 → 18), step 7 (the cell's
> graduation is **five** fixture edits, not two), and the Dependencies block (41-1b and 41-2 are `done`).
> The full evidence trail is in the story file's `## Amendment — 2026-08-01`, which also adds AC5/AC6/AC7.
> Where this plan and the story now disagree, **the story's amended ACs win.**

## Scope & Deliverable

When this story is done a new Elsa workflow `BacklogPrioritizationWorkflow` (DefinitionId
`backlog-prioritization`) is a **thin binding** over `document-lifecycle` in the landed producer shape
(`TaskCreationWorkflow` is the reference): it assembles the candidate item set plus whatever ranking
evidence the caller supplies, dispatches `document-lifecycle` with `documentType = "backlog-ordering"` and
the producer cell `(product_owner, prioritize-backlog)`, routes the typed exit, and exposes the accepted
ordering. Zero `Finish`, zero `llm-call` dispatch, zero validate/retry plumbing, exactly one
`DispatchWorkflow` targeting `document-lifecycle`.

Alongside the binding: the **rewritten** `prioritize-backlog` prompt template (the shipped one ranks a
*single* item, not a set — see Corrections); a `BACKLOG.*` DCB event family; a **set-scoped lineage anchor**
(the store is issue-anchored and a backlog ordering is not); the `WorkflowDocumentInterface` edge + its
three pin edits; the `ContractBindingTests` `Bindings` entry; and the structure/execution suites. The
`BacklogOrdering` **type is 41-1b's**, not this story's.

## Pre-Reading

- `docs/stories/epic-41/story-41-3/41-3-backlog-prioritization-and-grooming.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — rules 1–5; the new-types table row for `BacklogOrdering`
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the type (`total order over the referenced item set; rationale + value/effort per item; no ties`)
- `docs/stories/epic-41/story-41-2/implementation-plan.md` — the sibling this plan reuses wholesale: **D7's shared `EmitDomainLifecycleEventActivity`**, the rule-1 clause (f) two-edit lockstep (its Correction 4), and the `[ResumeBehavior]` correction (its Correction 1)
- **THE RECIPE:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` (esp. its **D2 producer-scoped issue id**, `:51` `ProducerScope` + `:112` `CreationBindingHelper.ScopeIssueId` — the mechanism this story generalises) and `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs` (`DeriveIssueId`, `ScopeIssueId`, `BuildFailureDetail`) and `LifecycleBindingHelper.cs` (`ReadLifecycleResult`, `IsAccepted`)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IDocumentInstanceRepository.cs:40-50` — **every read is `(tenantId, issueId)`-anchored**; there is no by-type, by-set or by-repository query. This is the constraint D2/D3 are built around.
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — the single-issue read seam (fail-closed)
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/prioritize-backlog.md` — the cell being rewritten (front matter `variables: role, issueJson, repoContext`, `enableTools: false`, `maxTokens: 2048`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs` — the type whose wire the shipped template currently emits (see Correction 2)
- **The gates this story must move** *[line-numbers refreshed 2026-08-01 against commit `6429691`; the
  originals — `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`, `reconciled` `:102-123`,
  `ContractBindingTests.cs:82` — predate 41-1b/41-1c/41-2/41-9]***:**
  `WorkflowInterfaceGraphTests.cs:52` `HaveCount(18)` + the `reconciled` array `:109-138`;
  `ContractBindingTests.cs:94` `Bindings`, `:754-763` `PendingProducerCells` (delete), `:824`
  graduation guard; `TemplateExampleConformanceTests.cs:130-132` (delete), `:207` pin `14`, `:224`
  `PinHistory`, `:609` shrink-only assertion, `:796` classify-exactly-once;
  `TaxonomyDriftBuildTests.cs:125` `ExpectedContributingWorkflows`, `:462`
  `ScanLifecycleBindingDispatches`; `ResumableStandardStructuralTests.cs:108/:159/:203`

## Corrections to the story

1. **AC4's `[ResumeBehavior(Both)]` is wrong and would fail the build.** Identical to 41-2's Correction 1:
   `Both` requires a canonical suspend node (`LifecycleBookmarks.CanonicalSuspendActivities`) in the
   binding's **own** graph (`ResumableStandardStructuralTests.cs:159`, plus the inverse honesty check at
   `:205`), and a thin binding owns none — the accept gate suspends inside the dispatched child. **Declare
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`**, as every landed producer does.
2. **The shipped `prioritize-backlog.md` ranks ONE item and emits a `TriageDecision`-shaped payload.** Its
   body says "prioritizing a backlog item — deciding where the issue below ranks", takes `{{issueJson}}`
   (singular) and outputs `{type, severity, priority: P0|P1|P2|P3, ownerRole, estimatedEffort, labels,
   reasoning}` — which is very nearly `TriageDecision`'s wire, **not** a total order over a set. The
   story's `BacklogOrdering` ("total order over the referenced item set; every item has a rationale +
   value/effort estimate; no ties") is a different document entirely. **The template must be rewritten**
   (39-15 D7 precedent, where `(product_owner, triage-intake)` was rewritten from exactly this P0–P3 /
   `ownerRole` vocabulary to the `TriageDecision` wire). This is in scope and moves the estimate.
3. **The cell's front matter has no feedback carrier.** `variables: role, issueJson, repoContext` — there is
   no `contextFindings`. A producer variable a cell does not declare is **silently dropped at render** (the
   39-15 render-drop lesson), so `feedbackVariableName` must name a *declared* variable. D4 resolves it.
4. **AC2 ("consumes upstream `TriageDecision`/`Findings` as ranking evidence") is not reachable through the
   store as written.** Every document read is `(tenantId, issueId)`-scoped
   (`IDocumentInstanceRepository.cs:40-50`; `FetchLatestAcceptedDocumentActivity` wraps exactly that), and
   `ListByIssueAsync`/`GetLatestAcceptedAsync` take **one** issue id. There is no "all accepted
   `triage-decision` rows for a repository" query, so evidence for an N-item backlog cannot be gathered by
   one activity. D3 resolves this without extending the store: the caller supplies the item set and the
   binding performs **N bounded per-item reads**. ~~Additionally, the upstream producers the story names
   (**41-11, 41-16, 41-17**) do not exist yet — the evidence path degrades gracefully to "issue text only"
   and must not hard-fail.~~ *[CORRECTED 2026-08-01 — graceful degradation stands, the premise does not.
   `triage-decision` and `findings` have LANDED producers today (`TriagePODecisionWorkflow`,
   `TriageContextGatheringWorkflow`, `ResearchWorkflow`), and the real defect is the ANCHOR: triage-context
   findings live at `{issueId}#triage-context`, so a bare-id read misses them and returns
   `ResearchWorkflow`'s findings instead. Read BOTH anchors — story file Amendment A1 / AC2.]*
5. **AC3 ("consumable by 41-6 via the 39-11 store") needs a lineage anchor the store can serve.** A
   `BacklogOrdering` is not about one issue, but `DocumentInstance.IssueId` is a required non-null string
   (`DocumentInstance.cs:37`) and it is the only read key. D2 defines a synthetic anchor —
   generalising `TaskCreationWorkflow`'s producer-scoped id (`{issueId}#task-creation`, its D2) — so 41-6
   can read the accepted ordering with the existing seam and no new repository method.
6. **Rule-1 clause (f) is a two-edit lockstep and the epic README names only one.** Besides
   `WorkflowInterfaceGraphTests.cs:52` `HaveCount(18)` *[was `:45` `HaveCount(16)`; refreshed 2026-08-01]*,
   the same file's
   `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:103`) asserts **bidirectionally**
   against a hardcoded `reconciled` array (`:109-138`): every listed id must be `!Provisional` and every
   unlisted one must be `Provisional`. A new non-provisional row omitted from that array fails the build.
   *(The epic README still prints the stale `:45` / `HaveCount(16)` figure at
   `docs/stories/epic-41/README.md:42` — recorded, not edited; the README is not this story's file.)*
7. **Rule-3/rule-4 reachability.** The accept gate publishes and suspends, but 39-17/39-19/39-20 are
   fail-closed stubs in tree, so "PO accepts" has no surface. Tests inject the decision through the 39-8
   `DocumentDecisionResumeEndpoint.Resume` statics. This story claims the workflow half only.

## Design Decisions

- **D1 — New DefinitionId `backlog-prioritization`; greenfield, no call site moves.** Nothing dispatches
  `(product_owner, prioritize-backlog)` today (repo-wide grep: zero `.cs` references outside
  `AgentAction.cs:26` / `RolePhaseMap.cs:53`), so there is no compat surface and no `IntentionallyUnbound`
  entry to move — the `Bindings` entry is purely additive. Registration is by assembly scan. Inputs:
  `repository`, `tenantId`, `backlogScope` (a caller-chosen grouping token, e.g. a board or milestone name),
  `itemsJson` (the candidate set: `[{issueId, issueNumber, title, summary}]`), `acceptanceRulesJson?`.
  Outputs: `status`, `outcome`, `documentId`, `orderingJson`, `backlogAnchor`.
- **D2 — Set-scoped lineage anchor: `backlog:{repository}:{backlogScope}`,** ~~folded through
  `CreationBindingHelper.ScopeIssueId`'s normalisation~~ *[CORRECTED 2026-08-01 — `ScopeIssueId` performs
  NO normalisation; it is `$"{baseIssueId ?? string.Empty}#{producer}"` and nothing else
  (`CreationBindingHelper.cs:95-96`). There is no existing segment transform to fold through. This story
  AUTHORS the normaliser and exposes it publicly — story file Amendment A3 / AC6.]* **normalised by this
  story's own public segment normaliser.** The store's only read key is `issueId` (Correction
  5), so the ordering is written under a deterministic synthetic anchor rather than a real issue. This is
  the *same* mechanism `TaskCreationWorkflow` already ships (`{issueId}#task-creation`, its D2) — promoted
  from "isolate two producers of one type" to "anchor a non-issue-scoped document", so it is a reuse, not a
  new concept. It is deterministic, so 39-10 re-entry and 41-6's consumer read both recompute it from inputs
  alone. **Filed to 39-11, not patched here:** the honest fix is a by-type/by-repository read; this plan
  records the anchor convention so 41-6 and a future 39-11 revision agree on one string.
- **D3 — Ranking evidence is gathered by N bounded per-item reads behind the `FreshRun` gate, and its
  absence is never fatal.** For each item in `itemsJson` (capped at a configured `MaxEvidenceReads`, default
  50) the binding performs one `FetchLatestAcceptedDocumentActivity` read for
  `(item.issueId, "triage-decision")` and ~~one for `(item.issueId, "findings")`~~ *[CORRECTED 2026-08-01 —
  **two** findings reads, not one: `(item.issueId, "findings")` for `ResearchWorkflow`'s anchor AND
  `(CreationBindingHelper.ScopeIssueId(item.issueId, "triage-context"), "findings")` for
  `TriageContextGatheringWorkflow`'s. Story file Amendment A1 / AC2. Three reads per item; the cap and the
  `ForEach` shape are unchanged. The composed evidence value must also stay under
  `PromptStoreService.MaxVariableValueLength` (100 000, `PromptStoreService.cs:96`) or the renderer drops it
  as unresolved.]*, inside an Elsa `ForEach`
  over a collection variable — **not** N compiled dispatch nodes (that would be unmaintainable and would
  distort the drift-gate pair count). Each read is fail-closed (`Found=false` ⇒ skipped), so a backlog whose
  items have never been triaged still produces an ordering from titles and summaries. This keeps AC2
  satisfiable **before** 41-11/41-16/41-17 exist (Correction 4) and makes their arrival a data change, not a
  code change.
- **D4 — The feedback carrier is a new declared variable `evidence`, added to the cell's front matter.**
  Correction 3: `feedbackVariableName` must name a declared variable or repair/revise notes are dropped.
  Reusing `repoContext` would conflate ranking evidence with revision notes in one blob. Instead the rewrite
  (D5) declares `variables: role, itemsJson, repoContext, evidence` and the dispatch sets
  `["feedbackVariableName"] = "evidence"`. Front-matter *variable-list* edits are safe against every keyset
  gate — `ConventionSeedDriftTests`, `SystemPromptsTests` and `PromptFileLoaderTests` key on `(role,
  action)` and on the four **required front-matter keys** (`variables`/`enableTools`/`maxTokens`/`version`),
  never on the variable list's contents (`PromptFileLoader.cs:122`). No taxonomy pin moves.
- **D5 — The template is rewritten to the `BacklogOrdering` wire; `maxTokens` raised.** Precedent 39-15 D7.
  The body instructs: rank the supplied set into a **total order** (rank 1..N, no ties), one rationale +
  value estimate + effort estimate per item, referencing only items in the supplied set. `enableTools` stays
  `false` (ranking is a judgement over supplied context, not a tool-using task). `maxTokens: 2048` is raised
  to `8192` — 2048 cannot emit N rationales for a real backlog; that key *is* required front matter and its
  value is free, so the change is local. The exact JSON shape is `BacklogOrderingDocumentType`'s —
  **41-1b owns the wire**, so this edit is a lockstep with its `Contract` const, enforced by
  `ContractBindingTests.EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` (`:361`).
- **D6 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + `ComputeReEntryPositionActivity`, keyed on the
  D2 anchor, no allowlist entry.** Per Correction 1. The re-entry position gates the whole evidence-gather
  region and the `BACKLOG.GROOMING.STARTED` emission — a re-entry must not re-read N documents or re-announce
  the run.
- **D7 — The `BACKLOG.*` family rides 41-2's shared `EmitDomainLifecycleEventActivity`.** This story ships
  only `BacklogEvents.cs` (`BACKLOG.GROOMING.STARTED` / `.ORDERED` / `.ACCEPTED` / `.FAILED`). If 41-2 has
  not landed, carry a local copy of the activity and delete it when 41-2 merges — recorded here so the
  duplication is deliberate and time-boxed.
- **D8 — Acceptance posture is 41-1b's arm, asserted here.** ~~`AcceptanceDefaults.For` ends in `_ => Rules`
  (`AcceptanceDefaults.cs:128-133`) — the single-`architect` unanimous row, which is wrong for a backlog
  ordering.~~ *[CORRECTED 2026-08-01 — 41-1b landed the arm:
  `DocumentTypeKey.BacklogOrdering => s_productOwnerRules` at
  `Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:215` (note the file moved to `Documents/Policy/`), and
  `s_productOwnerRules` is a `SingleReviewer` / `ProductOwner` / `Unanimous` row built at `:129-139`. This
  type never reaches `_ => Rules`. Test (f) survives as a regression guard, not as a gap to close.]*
  The required row is a **`product_owner` single reviewer** with the acceptor per autonomy, and
  the story's "reordering above a churn threshold escalates" is expressed as a per-document-type
  always-escalate class in the resolved rules, **not** as a branch in the binding (rule 3). Both selector
  arms needed already exist (`GetReviewActionForRole(ProductOwner) => ReviewScope`) — **no 41-1a
  dependency**.

## Implementation Steps

1. **Precondition check (no code).** 41-1b merged: `DocumentTypeKey.BacklogOrdering` parses,
   `DocumentTypeRegistry.Resolve("backlog-ordering")` returns the type, its `Contract` const is final (D5
   depends on it), and `AcceptanceDefaults.For` carries the D8 arm. Confirm 41-2's
   `EmitDomainLifecycleEventActivity` is in tree (else D7's fallback).

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/BacklogEvents.cs`** — the four constants, tags
   `repository` / `tenantId` / `correlationId` / `backlogScope` (no `issueId`: this document is not
   issue-scoped — the anchor rides `correlationId`).

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/BacklogBindingHelper.cs`** — a
   `public static class` (as all 18 helpers in that folder are), pure,
   Elsa-free, total, fail-closed: `public static BuildAnchor(repository, backlogScope)` (D2 —
   deterministic) plus its **separately callable `public static` segment normaliser**
   ~~normalised through the same segment transform `ScopeIssueId` uses~~ *[CORRECTED 2026-08-01 — no such
   transform exists; see D2. 41-4 and 41-6 both delegate to this normaliser, so it may not be `private` or
   an inline lambda. Story file AC6.]*, `ParseItems(itemsJson)` → a bounded list (D3's
   cap, malformed ⇒ empty, never throws), `AppendEvidence(evidenceSoFar, itemIssueId, docJson)` (the D3
   accumulator), `ProjectOrdering(documentJson)` (the accepted `items` array raw text, `"[]"` on
   unreadable), `BuildFailureDetail` via `CreationBindingHelper`. Reuse `LifecycleBindingHelper` verbatim.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BacklogPrioritizationWorkflow.cs`** (D1/D2/D3/
   D6) — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Graph: `ReadInputs` →
   `ComputeReEntryPosition` (on the D2 anchor) → `ReadPositionStage` → `FreshRun` `FlowDecision` →
   `EmitGroomingStarted` → `GatherEvidence` (`ForEach` over the parsed items, body =
   `FetchLatestAcceptedDocumentActivity` ×2 + `AppendEvidence`) → `DispatchLifecycle` →
   `ReadLifecycleExit` → `LifecycleAccepted` `FlowDecision` → `EmitOrdered`/`EmitAccepted` |
   `EmitFailed` → `ExposeOutput` (the single terminal region; **no `Finish`**). Dispatch input:

   ```csharp
   ["documentType"]          = "backlog-ordering",
   ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
   ["producerAction"]        = AgentAction.PrioritizeBacklog.ToWire(),
   ["producerVariablesJson"] = /* { itemsJson, repoContext, evidence } */,
   ["feedbackVariableName"]  = "evidence",           // D4 — a DECLARED carrier
   ["issueId"]               = anchor,               // D2
   ["correlationId"]         = anchor,
   ["tenantId"] / ["acceptanceRulesJson"]
   ```

   `WaitForCompletion = new(true)`. `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`) — add
   `("backlog-prioritization", [TriageDecision, Findings], BacklogOrdering, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — ~~`:45`
   `HaveCount(16)` → `HaveCount(17)`~~ *[CORRECTED 2026-08-01 — the pin is now **`:52` `HaveCount(18)`**
   (41-2 and 41-9 landed since this plan was written); take it to `19`, or +1 on whatever a concurrent
   story leaves it at.]* with the reason in the
   comment, **and** add `"backlog-prioritization"` to the `reconciled` array ~~`:102-123`~~ *[now
   `:109-138`, 15 ids]* (Correction 6).

6. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/prioritize-backlog.md`** (D5, Correction
   2) — body rewritten to the `BacklogOrdering` wire; front matter gains `itemsJson` + `evidence` (D4) and
   `maxTokens: 8192`.

7. **MODIFY the drift gates — the FIVE-edit graduation, all in one commit** *(expanded 2026-08-01: this
   step used to name two edits; it is five, and a partial set is a red build with a message that names
   neither this story nor the template. Story file Amendment A2 / AC5.)*
   a. `ContractBindingTests.cs:94` `Bindings` — add
      `[("product_owner","prioritize-backlog")] = new("BacklogOrderingDocumentType.Validate", [ … the six
      41-1b token groups … ])`, taken **verbatim** from the pending entry, with a comment naming
      `Tamma.Core/Documents/Types/BacklogOrdering.cs` as the shape authority.
   b. `ContractBindingTests.cs:754-763` — **delete** the `PendingProducerCells` entry (no count pin on that
      table; a plain delete). Leave the 41-2 graduation comment above it as the precedent trail and add
      this story's.
   c. `TemplateExampleConformanceTests.cs:130-132` — **delete** the `KnownNonConformingTemplates` entry.
   d. `TemplateExampleConformanceTests.cs:207` — `KnownNonConformingTemplateCount` **14 → 13**.
   e. `TemplateExampleConformanceTests.cs:224` — `PinHistory` `[11, 16, 15, 14]` → `[11, 16, 15, 14, 13]`.
      `TheRatchetPin_IsMechanicallyShrinkOnly` (`:609`) binds the pin to `PinHistory[^1]`, so (d) and (e)
      move together or not at all.

   Then `TaxonomyDriftBuildTests.cs`: add `"BacklogPrioritizationWorkflow"` to
   `ExpectedContributingWorkflows` (`:125`). Verify (do not pre-edit) `MinExpectedDispatchPairs` (`:110`, a
   floor, currently `21`) and `EveryConcreteWorkflow_IsIntrospectableOrAllowListed`.

8. **CREATE the test suites** — `BacklogPrioritizationWorkflowStructureTests.cs`,
   `BacklogBindingHelperTests.cs`, `BacklogPrioritizationLifecycleExecutionTests.cs` (all under
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/`). See Test Plan.

9. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean.

## Data & Migrations

None. `BacklogOrdering` rows are `document_instances` (39-11's table, 41-1b's registration); `BACKLOG.*` and
`DOCUMENT.*` ride the existing emitter → drain → `domain_events` path. `has-pending-model-changes` stays
clean. **Note:** the D2 anchor is written into the existing required `IssueId` column — no schema change,
and the "this is not really an issue id" caveat is recorded in D2 and filed to 39-11.

## Events

- **Emits (new constants, this story):** `BACKLOG.GROOMING.STARTED` (fresh runs only, data
  `itemCount`), `.ORDERED` (data `itemCount`, `evidenceHits`), `.ACCEPTED` (data `documentId`), `.FAILED`
  (detail names the typed outcome wire). Tags `repository` / `tenantId` / `correlationId` (= the D2 anchor)
  / `backlogScope`.
- **Emitted by the machinery this binding wires in:** the `DOCUMENT.*` family, `APPROVAL.*`,
  `ESCALATION.TRIGGERED`.
- **Consumes (as evidence, via the store — not the event stream):** accepted `triage-decision` and
  `findings` documents, one bounded read per backlog item (D3).

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution suite (the shared 39-6/39-10 fixture).

- **`BacklogPrioritizationWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, `TaskCreation`-shaped:
  builds; DefinitionId `backlog-prioritization`; threads `TenantId`; **zero** `Finish`; **exactly one**
  `DispatchWorkflow`, literal id `document-lifecycle`; **zero** targeting `llm-call`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables;
  `ScanLifecycleBindingDispatches()` contains `(product_owner, prioritize-backlog)` attributed to this
  workflow; `MaterializeDispatchInput` yields `documentType == "backlog-ordering"` and
  `feedbackVariableName == "evidence"`; one `ComputeReEntryPositionActivity`; the evidence `ForEach` present
  with `FetchLatestAcceptedDocumentActivity` in its body (and **not** N unrolled fetch nodes — D3);
  `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`; `[ResumeBehavior(LatestStateReEntry)]`; **no
  `Wait*` activity** (Correction 1). **Covers AC1, AC4.**
- **`BacklogBindingHelperTests`** — `BuildAnchor` determinism + tenant/scope folding + hostile-character
  normalisation (same-inputs-twice ⇒ byte-identical, the 41-6 consumer contract); `ParseItems` on valid /
  malformed / oversized input (cap honoured, never throws); `AppendEvidence` accumulation and skip-on-absent;
  `ProjectOrdering` on a valid body and on unreadable JSON (`"[]"`); `BuildFailureDetail` names each
  reachable outcome wire. **Covers AC2 (evidence half), AC3 (anchor half).**
- **`BacklogPrioritizationLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path with evidence:** *[expanded 2026-08-01 for the two-anchor read — story AC2.]* three
  backlog items — item 1 with an accepted `triage-decision` at its bare id, item 2 with an accepted
  `findings` **only** at `ScopeIssueId(itemId, "triage-context")`, item 3 with an accepted `findings` at
  the bare id (the `ResearchWorkflow` anchor) — → scripted valid draft → review approve → `Accept` resume
  → `status=completed`; asserts **all three** evidence documents reached the producer variables, each
  labelled with the anchor it came from, and that a fourth item with nothing still appears in the ordering
  (D3's graceful degradation). A single-anchor implementation fails on item 2. **Covers AC1, AC2.**
  (a2) **evidence size bound:** an item set large enough to exceed
  `PromptStoreService.MaxVariableValueLength` (100 000) yields a composed evidence value **under** the cap
  — otherwise the renderer marks it unresolved and ships a literal `{{evidence}}` in the prompt
  (`PromptStoreService.Render`, `:559-589`). **Covers AC2(iv).**
  (b) **domain-rule rejection:** a draft with two items at the same rank is rejected by the type's
  validator with the named violation code, loops repair/revise (notes arriving through `evidence` — D4), and
  accepts on the second round. **Covers AC1 (validation half).**
  (c) **validation exhaustion:** always-tied stub → typed `ValidationExhausted` escalation with lineage,
  `BACKLOG.GROOMING.FAILED` naming the outcome, `status=escalated`, no error terminal.
  (d) **41-6 consumer read:** after (a), `FetchLatestAcceptedDocumentActivity` for
  `(BuildAnchor(repository, backlogScope), "backlog-ordering")` returns the accepted body — the exact seam
  41-6 will use, proving the D2 anchor is recomputable from inputs alone. **Covers AC3.**
  (e) **re-entry:** crash after acceptance → short-circuits with the SAME `documentId`, exactly one
  `DOCUMENT.ACCEPTED` and one `BACKLOG.GROOMING.ACCEPTED`, and **zero** extra evidence reads; crash
  mid-review → resumes at review of the same revision. **Covers AC4.**
  (f) **acceptance posture (D8):** `AcceptanceDefaults.For(DocumentTypeKey.BacklogOrdering)` returns the
  documented row — a `_ => Rules` fall-through fails here as well as in 41-1b.
- **Drift gates (self-verifying, steps 5/7)** — `ContractBindingTests`, `TemplateExampleConformanceTests`
  (**all five graduation edits**, story AC5 — including
  `EveryDocumentTypeBoundCell_ShippedExampleValidatesAgainstItsBoundType` on the rewritten template),
  `TaxonomyDriftBuildTests`, `WorkflowInterfaceGraphTests` (count **and** `reconciled` bidirectional) and
  `ResumableStandardStructuralTests` (declares, **no** allowlist entry) all green in the same commit.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `BacklogOrdering` validated (total order, rationale, no ties) | 4, 6, 7 (D1/D5) | StructureTests clause (a)–(f); ExecutionTests (b) named violation code |
| 2 — evidence read at BOTH findings anchors, absence non-fatal | 4 (D3) | ExecutionTests (a) three-anchor fixture + (a2) size bound; HelperTests accumulation |
| 3 — consumable by 41-6 via the 39-11 store | 4, 5 (D2) | ExecutionTests (d); HelperTests anchor determinism |
| 4 — resumable per the standard, no allowlist entry | 4 (D6) | StructureTests declaration + no-`Wait*`; ExecutionTests (e); `ResumableStandardStructuralTests` |
| 5 — five-edit graduation in one commit | 7 (a)–(e) | `TheRatchetPin_IsMechanicallyShrinkOnly`, `EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse`, `KnownNonConformingTemplates_OnlyBaselineUnboundCells`, `EveryTaxonomyCell_IsClassifiedExactlyOnce` |
| 6 — `BuildAnchor` + segment normaliser PUBLIC (shared with 41-4/41-6) | 3 (D2) | `BacklogBindingHelperTests` calls both from a different assembly — a non-public member does not compile |
| 7 — template rewritten to the `backlog-ordering` wire | 6 (D5) | `EveryDocumentTypeBoundCell_ShippedExampleValidatesAgainstItsBoundType`; `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` |

## Dependencies & Sequencing

- ~~**Blocked by:** **41-1b** — hard. `DocumentTypeKey` has exactly 10 members today (verified) and
  `backlog-ordering` is not one, so the document is unparsable (`DOCUMENT.TYPE.UNKNOWN`) and unpersistable on
  the **human path too**.~~ *[CORRECTED 2026-08-01 — **41-1b is `done`** (`docs/sprint-status.yaml:630`).
  `DocumentTypeKey` now has **17** members including `[Wire("backlog-ordering")] BacklogOrdering`
  (`DocumentTypeKey.cs:40`); `BacklogOrderingDocumentType` is registered at `DocumentTypeRegistry.cs:44`.
  Not a blocker — a shipped input. Step 1's precondition check is a five-minute confirmation, not a wait.]*
  **Epic 39** (39-6/39-7/39-8/39-10/39-11) — all landed and verified.
- ~~**Soft-blocked by 41-2** — only for D7's shared `EmitDomainLifecycleEventActivity`. A local copy unblocks
  it at the cost of a later mechanical merge.~~ *[CORRECTED 2026-08-01 — 41-2 is `done`
  (`docs/sprint-status.yaml:632`) and
  `Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs` is in tree. D7's local-copy fallback is
  moot; use the shared activity.]*
- **NOT blocked by 41-1a.** `(product_owner, prioritize-backlog)` exists (`AgentAction.cs:26`,
  `RolePhaseMap.cs:53`, prompt file present) and `GetReviewActionForRole(ProductOwner)` already resolves.
- **NOT blocked by 41-11 / 41-16 / 41-17** despite the story's `consumes:` line naming them. D3 makes their
  output *optional* evidence read through the existing store seam; when they land, the same code picks up
  richer input with no change (Correction 4).
- **Blocks:** **41-6** (sprint planning reads the accepted `BacklogOrdering` — AC2 there is "hard-fails loud
  if none exists", which depends on this story's D2 anchor being the agreed string). Also feeds **41-4**
  (roadmap `consumes: BacklogOrdering`).
- **Lockstep:** 41-1b's `BacklogOrdering` `Contract` const ↔ step 6's template rewrite ↔ step 7's `Bindings`
  token groups (one wire, agreed once). **The D2 anchor string is a shared contract with 41-6 and 41-4** —
  it must be defined in `BacklogBindingHelper.BuildAnchor` and consumed, never re-derived, by them.
- **Stubbed, not pulled in:** 39-17, 39-19, 39-20 (Correction 7).
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5/6/7 (parallel) → 8 → 9.

## Risks & Mitigations

- **The D2 anchor is a workaround for an issue-anchored store.** If 39-11 later grows a by-type read, two
  ways to find a `BacklogOrdering` exist. Mitigation: the anchor is computed in exactly one place
  (`BuildAnchor`) and consumed by 41-6/41-4 through it; the "file to 39-11" note in D2 makes the migration a
  helper-body change, not a workflow change.
- **N evidence reads on a large backlog.** 200 items ⇒ 400 store reads inside one workflow instance.
  Mitigation: the D3 cap (default 50, configurable) plus fail-closed per-read semantics; the reads are
  in-process repository calls, not HTTP; the `ForEach` shape keeps them out of the compiled dispatch count so
  the drift gates are unaffected.
- **Template rewrite regresses output quality, and this one is a bigger rewrite than 41-2's** (single-item →
  set-ranking is a change of task, not of format). Mitigation: 41-1b AC2's accepting/rejecting fixtures are
  the contract; the no-ties rule is machine-checked, so a drifting draft drives a repair turn rather than a
  silent bad ordering.
- **41-1b slips or the wire churns.** Mitigation: steps 2–3 and the helper tests are 41-1b-independent;
  agree the `items[]` wire in one review before step 6.
- **"Done" is narrower than the story's prose** (39-17/39-19/39-20 unlanded). Mitigation: Correction 7
  states the boundary; no AC above depends on the orchestrator.
- **Story-vs-code tensions:** Corrections 1–6 are resolved in favour of the code. Corrections 2 and 4 change
  the work (and the estimate); the rest are mechanical.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1b precondition verification + wire agreement | 0.25 |
| 2 | `BacklogEvents` (+ 41-2 emitter reuse or local copy) | 0.25 |
| 3 | `BacklogBindingHelper` (anchor, item parse, evidence accumulator) | 0.75 |
| 4 | The binding workflow incl. the evidence `ForEach` region | 1.25 |
| 5 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 6 | Prompt-template rewrite: single-item triage → set ordering (Correction 2) | 0.75 |
| 7 | `ContractBindingTests` + `TaxonomyDriftBuildTests` edits | 0.5 |
| 8 | Structure + helper + Testcontainers suites (a)–(f) | 1.25 |
| 9 | Full-suite green, review polish | 0.25 |
| **Total** | | **5.5** |

**Est. Effort: 5.5 days.** The story file says 3–4 days; that predates three facts this plan verified — the
template is a from-scratch rewrite of a *different task* (Correction 2, +0.75 d), the evidence path needs a
bounded `ForEach` because the store has no set query (Correction 4, +0.75 d), and the document needs a
synthetic lineage anchor (Correction 5, +0.5 d). The story's `## Estimated Effort` section is left at 3–4
days and this plan is the record of the delta.

## Blocks / Blocked by

- **Blocked by:** 41-1b (hard — the `BacklogOrdering` document type); Epic 39 stories 39-6, 39-7, 39-8,
  39-10, 39-11 (all landed); 41-2 (soft — D7's shared emitter only).
- **Blocks:** 41-6 (sprint planning consumes the accepted `BacklogOrdering` and shares the D2 anchor
  contract); 41-4 (roadmap consumes it).
- **Not blocked by:** 41-1a (cell and reviewer arm already exist); 41-1c (typed document, not prose);
  41-11 / 41-16 / 41-17 (their `TriageDecision` output is optional evidence — D3); the tenant-aware
  scheduled-trigger seam (this workflow is caller-triggered, not scheduled).
