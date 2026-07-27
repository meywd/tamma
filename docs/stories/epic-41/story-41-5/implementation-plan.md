# Implementation Plan — Story 41-5: Stakeholder & Status Reporting Workflow

> ## Scheduler note — NOT scheduler-blocked (2026-07-25 decision); still gated on 41-1a + 41-1c
>
> **Per the product owner's 2026-07-25 decision ("scheduling is needed for audits, NOT for ceremonies"),
> status reporting is user-initiated** — Part A ships complete on a manual/API trigger and this plan is
> no longer blocked on a scheduled-trigger seam. The seam itself is now **owned by 41-30**; a cron
> cadence for this workflow is a later opt-in through 41-30, and Part B below remains
> requirements-only until then.
>
> The original finding stands and is why 41-30 exists: `HourlyAnalyticsRollupScheduler` is the only
> cron-shaped artifact in the Elsa host and is **not reusable** — it hardcodes its target
> (`HourlyAnalyticsRollupWorkflow.DefinitionId`, `:197-198`) and its options section (`:17`), offers a
> single `FireAtMinute` int (`:34`) rather than a window or cron shape, threads **no `tenantId`** into
> the dispatch (`:198-204`), keys its advisory lock on `(year, dayOfYear, hour)` with **no tenant
> component** (`ComputeAdvisoryLockKey`, `:241-255`), and rests idempotency on `_lastFired`
> **in-process memory** (`:83`).
>
> Part A **remains hard-blocked on 41-1c** (prose) **and 41-1a** (see Correction 2 — the story's named
> cell is already taken by a live consumer; the real cell `(project_manager, report-status)` does not
> exist until 41-1a mints it). Neither is this story's to build.

## Scope & Deliverable

**Part A (plannable now, gated on 41-1a + 41-1c).** A new Elsa workflow `StatusReportWorkflow` (DefinitionId
`status-report`) is a **thin binding** over `document-lifecycle` in the landed producer shape: it reads the
accepted `SprintPlan` for the period plus a **period-scoped DCB evidence slice**, dispatches
`document-lifecycle` with `documentType = "prose"`, `kind = status-update`, `audience = stakeholder` and the
producer cell `(project_manager, report-status)`, routes the typed exit, and exposes the accepted report
text. Zero `Finish`, zero `llm-call` dispatch, zero validate/retry plumbing, exactly one `DispatchWorkflow`
targeting `document-lifecycle`. Alongside it: a new **engine-side DCB evidence-read activity** (shared with
41-7); a `STATUS_REPORT.*` event family; the period lineage anchor; the `WorkflowDocumentInterface` edge +
its three pin edits; the `ContractBindingTests` `Bindings` entry; and the structure/execution suites.

**Part B (blocked).** The tenant-scoped, per-window, durably-idempotent scheduled trigger that dispatches
`status-report` on a cadence.

## Pre-Reading

- `docs/stories/epic-41/story-41-5/41-5-stakeholder-and-status-reporting.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — the **scheduler bullet** under Dependencies (the definitive statement that no story then built this seam) and the Epic 42 table row for 41-5
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2 mints `(project_manager, report-status)`, the cell Correction 2 forces this story onto
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type, `Audience` on envelope **and** `DocumentInstance` + migration, the `status-update` kind and `stakeholder` audience (its Scope 3 seeds both **from this story**)
- `docs/stories/epic-41/story-41-4/implementation-plan.md` — the sibling prose binding; its D5 (reviewer choice that avoids a 41-1a review-arm dependency), D6 (template rewrite posture), D9 (kind/audience from vocabularies) apply here verbatim
- `docs/stories/epic-41/story-41-2/implementation-plan.md` — D7's shared `EmitDomainLifecycleEventActivity`; the `[ResumeBehavior]` correction; the rule-1 clause (f) two-edit lockstep
- **THE RECIPE:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:200-212` — `QueryEventsAsync(tenantId, type, typeIsPrefix, correlationId, actor, from, to, cursor, limit, includeTotal)`: **the tenant-scoped, time-windowed, type-prefixed DCB read D3's evidence activity wraps.** Note its hard guard — an empty `tenantId` throws `NotSupportedException`.
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs` — the only existing engine-side `IEventRepository` consumer; the `context.GetService<T>()` resolution + fail-closed posture D3 copies
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — **read it to understand why it is NOT the pattern** (the four defects enumerated in the banner above)
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/summarize-stakeholder.md` — the cell the story names; front matter `variables: role, workItemJson, findings, audience`, `enableTools: false`, `maxTokens: 2048`. **Already declares an `audience` variable** — the prompt layer anticipated a concept the document layer never got.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs:161` — the **live consumer** of `(product_owner, summarize-stakeholder)` that Correction 2 turns on
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:286-300` — `IntentionallyUnbound`, incl. the `(product_owner, summarize-stakeholder)` entry at `:299`; `:681` classify-or-fail; `:718` the contradiction check that makes Bindings ∩ IntentionallyUnbound a build failure
- **The gates this story must move:** `WorkflowInterfaceGraphTests.cs:45` + the `reconciled` array `:102-123`; `ContractBindingTests.cs:82`; `TaxonomyDriftBuildTests.cs:125`, `:460`; `ResumableStandardStructuralTests.cs:108/:159/:238/:266`

## Corrections to the story

1. **AC3's `[ResumeBehavior(Both)]` ("scheduled + accept-gate") is wrong twice over.** (i) `Both` requires a
   canonical suspend node from `LifecycleBookmarks.CanonicalSuspendActivities` in the binding's **own**
   graph (`ResumableStandardStructuralTests.cs:159`, plus the inverse honesty check at `:205`); a thin
   binding owns none — the accept gate suspends inside the dispatched child. (ii) "scheduled" is not a
   resume mode at all: `ResumeMode` has exactly three members (`BookmarkSuspend`, `LatestStateReEntry`,
   `Both`) and a cron trigger is a *dispatcher*, not a bookmark. **Declare
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`**, as every landed producer does.
2. **The story's produce cell `(product_owner, summarize-stakeholder)` is ALREADY BOUND to a live consumer
   and cannot be re-bound as a document producer.** `ContextGatheringWorkflow.cs:161` dispatches it via
   `llm-call` for a lenient free-text summary, and it is classified in
   `ContractBindingTests.IntentionallyUnbound` (`:299`) with the justification *"ContextGatheringWorkflow.
   ExtractPO opportunistically lifts summary/links from JSON but falls back to the raw text as the summary —
   lenient, never fails closed."* `Bindings` and `IntentionallyUnbound` are **mutually exclusive** — the
   contradiction check at `:718` fails the build on any pair in both. Worse, `RenderContract` is per document
   **type** (`IDocumentType.cs:47-50`), so binding the cell to `prose` would inject a prose envelope contract
   into the prompt that `ContextGatheringWorkflow`'s lenient consumer also renders. This is the same hazard
   the epic README already flagged for 41-22's `(devops, rollback)`.
   **Resolution: use the story's own parenthetical.** The story line 19 already names *"PM variant
   `(project_manager, report-status)` via 41-1"*. That becomes the **primary and only** produce cell, and
   `(product_owner, summarize-stakeholder)` is left untouched for `ContextGatheringWorkflow`.
   **Consequence: this story hard-depends on 41-1a, which its `Blocking:` line does not name.** Verified:
   `report-status` / `ReportStatus` returns **zero** repo-wide hits, and `project_manager` is not an
   `AgentRole` (the enum has exactly 8 members).
3. **AC1's "every claim cites DCB evidence" has no engine-side read seam.** The only `IEventRepository`
   consumer in `Tamma.Activities` is `LifecycleReEntryService`, and its read is narrowly issue-scoped for
   re-entry. There is **no activity** a workflow can use to pull a tenant-scoped, time-windowed DCB slice.
   `IEventRepository.QueryEventsAsync` (`:200-212`) is the right surface — tenant + type prefix + half-open
   `from`/`to` window + keyset cursor — but a `Documents/`-resident activity must be written to wrap it.
   That is in Part A's scope (D3) and is shared with **41-7**, which needs exactly the same read.
4. **A status report is not issue-scoped, so it needs its own lineage anchor.**
   `DocumentInstance.IssueId` is a required non-null string (`:37`) and the only store read key
   (`IDocumentInstanceRepository.cs:40-50`). D2 defines `status:{repository}:{periodKey}` — deterministic,
   so re-entry and Part B's idempotency both recompute it from inputs alone.
5. **The story's `consumes: [SprintPlan (41-6)]` inherits 41-6's blockers.** `SprintPlan` is a 41-1b type
   produced by a workflow that needs 41-1a's `scrum_master` role. D3 makes the read **optional and
   fail-closed**, so Part A does not take a hard 41-6 dependency — the report degrades to "DCB evidence
   only" when no accepted `SprintPlan` exists.
6. **Rule-1 clause (f) is a two-edit lockstep and the epic README names only one.** Besides
   `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`, the same file's
   `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:96`) asserts bidirectionally against
   the hardcoded `reconciled` array (`:102-123`).
7. **The Epic 42 caveat in the story is correct and is restated as a scope boundary, not a risk.** Only six
   `IToolExecutor`s are registered (`Tamma.Api/Program.cs:753-764`: `FileRead`, `FileWrite`, `SearchCode`,
   `ShellExecute`, `GitOperations`, `RunTests`), all coding-oriented. **Delivery ("Accepted report delivered
   via the orchestrator to the stakeholder audience") is out of Part A's scope entirely** — this story
   produces and accepts a document; publishing it is 42-9's tool plus 39-19's surface.

## Design Decisions — Part A (the lifecycle binding)

- **D1 — New DefinitionId `status-report`; produce cell `(project_manager, report-status)`.** Per Correction
  2. Greenfield, no call site moves, `Bindings` entry purely additive, `(product_owner,
  summarize-stakeholder)`'s `IntentionallyUnbound` entry **untouched**. Inputs: `repository`, `tenantId`,
  `periodKey` (e.g. `2026-W30`), `periodStart`/`periodEnd` (ISO-8601), `sprintScope?`,
  `acceptanceRulesJson?`. Outputs: `status`, `outcome`, `documentId`, `reportMarkdown`, `periodAnchor`.
- **D2 — Lineage anchor `status:{repository}:{periodKey}`.** Correction 4. Same normalisation as 41-3's
  `BacklogBindingHelper.BuildAnchor` and 41-4's `RoadmapBindingHelper.BuildAnchor`, so the anchor families
  are provably consistent. Written into the existing required `IssueId` column — no schema change here (the
  `Audience` migration is 41-1c's). **It is also the natural idempotency key for Part B**, which is why it is
  defined in Part A: the seam, whoever writes it, can key its persisted last-fired window on this string.
- **D3 — A new `QueryDcbEvidenceActivity`, shared with 41-7.** Correction 3. Placed in
  `Tamma.Activities/Documents/` beside `FetchLatestAcceptedDocumentActivity`. Inputs `TenantId`,
  `TypePrefixesJson` (the families to sweep — `DOCUMENT.`, `APPROVAL.`, `ESCALATION.`, `DEPLOY.`,
  `BLOCKER.`), `From`, `To`, `MaxEvents` (default 500). Outputs `Found`, `EvidenceJson` (a compact
  projection: `[{type, at, tags, detail}]`), `EventCount`. Resolves `IEventRepository` via
  `context.GetService<T>()` (the `LifecycleReEntryService` pattern — an injected repository is inert in the
  Elsa engine), pages on the keyset cursor, and is **fail-closed**: a missing service, an empty tenant (which
  `QueryEventsAsync` throws `NotSupportedException` on), or any exception yields `Found=false` with an empty
  projection — it never throws out of the binding graph. The `SprintPlan` read is a separate, likewise
  fail-closed `FetchLatestAcceptedDocumentActivity` (Correction 5).
- **D4 — Every claim is traceable because the evidence projection carries event ids, and the template is
  instructed to cite them.** This is the honest reading of AC1: the workflow cannot *verify* citation, and a
  validator over prose is forbidden by 41-1c's "no forced structure". So the projection includes each
  event's id/type/timestamp, the rewritten cell instructs one bracketed citation per claim, and the
  **review stage** is where uncited claims are caught. Stated plainly so "every claim cites DCB evidence" is
  not read as a machine-checked property.
- **D5 — `feedbackVariableName` is a DECLARED carrier on the new cell.** 41-1a mints
  `Prompts/project_manager/report-status.md`; this plan specifies its front matter as
  `variables: role, sprintPlanJson, evidence, audience, periodKey` / `enableTools: false` /
  `maxTokens: 8192` / `version: 1`, and the dispatch sets `["feedbackVariableName"] = "evidence"`. An
  undeclared producer variable is silently dropped at render (the 39-15 render-drop lesson). **Lockstep with
  41-1a:** the file is created *there*, its contents are specified *here*.
- **D6 — Reviewer pinned to `product_owner`, not `tech_writer`.** Identical reasoning to 41-4's D5:
  41-1c D2's prose default is a `tech_writer` reviewer and
  `RolePhaseMap.GetReviewActionForRole` **throws** for `TechWriter` (`:376-387`), called unguarded at
  `DocumentLifecycleWorkflow.cs:1199`. A `product_owner` reviewer (`ProductOwner => ReviewScope`) already
  resolves and is the right lens for a stakeholder report. Note: this story already depends on 41-1a for the
  *role and cell* (Correction 2), so unlike 41-4 the choice is not about avoiding the dependency — it is
  about not depending on 41-1a's *review-selector arm* as well, which is a separate deliverable inside that
  story.
- **D7 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + `ComputeReEntryPositionActivity` keyed on the
  D2 anchor, no allowlist entry.** Correction 1. The position gates the evidence sweep, the `SprintPlan`
  read and the `STATUS_REPORT.STARTED` emission — a re-entry must not re-sweep 500 events or re-announce.
- **D8 — The `STATUS_REPORT.*` family rides 41-2's shared `EmitDomainLifecycleEventActivity`.** This story
  ships only `StatusReportEvents.cs`.
- **D9 — Kind and audience from 41-1c's vocabularies, never string literals** (41-4 D9): `kind =
  status-update`, `audience = stakeholder`, both via `ToWire()`, so an out-of-vocabulary value is a compile
  error.
- **D10 — Delivery is explicitly out of scope.** Correction 7. The workflow's terminal is `ExposeOutput`;
  nothing posts. When 42-9 lands a governed HTTP/external-API executor and 39-19 lands a surface, delivery is
  a *downstream consumer* of the accepted document, not an edit to this binding.

## Design Notes — Part B (BLOCKED; requirements only, no steps)

Recorded so whoever owns the seam has this story's requirements in one place. **Do not implement against
this section as if it were a design** — it is a requirements list for a component with no owner.

- Tenant-scoped: the advisory-lock key must include a tenant component (today's
  `ComputeAdvisoryLockKey(year, dayOfYear, hour)` does not, so one tenant's leader suppresses all others).
- `tenantId` threaded into the dispatch (today's `DispatchWorkflowDefinitionRequest` at `:198-204` carries
  no input variables at all).
- **Durably** persisted last-fired window per `(tenantId, workflowDefinitionId, windowKey)` — not
  `_lastFired` in process memory (`:83`). D2's anchor is the natural `windowKey`.
- A window/cron shape, not a single `FireAtMinute` int.
- Not hardcoded to one target workflow, and its options section not hardcoded to one config key.
- Its idempotency must not assume the target is UPSERT-idempotent: a document-producing lifecycle workflow
  is not, which is precisely why the analytics rollup's design does not transfer.
- Consumers to satisfy simultaneously: **41-5, 41-7, 41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23**.

## Implementation Steps — Part A

1. **Precondition check (no code). Three hard gates.** (i) **41-1a**: `AgentRole.ProjectManager` parses,
   `AgentAction.ReportStatus` exists and is eligible in `RolePhaseMap`, and
   `Prompts/project_manager/report-status.md` exists with D5's front matter (`PromptFileLoader` refuses to
   start otherwise — `PROMPT.SEED.NO_BODY_FAMILY`). (ii) **41-1c**: `Parse("prose")` succeeds,
   `Resolve("prose")` returns `ProseDocumentType`, `DocumentEnvelope.Audience` + `DocumentInstance.Audience`
   exist with the migration applied, the vocabularies carry `status-update` and `stakeholder`, and
   `AcceptanceDefaults.For(Prose)` returns its D2 row. (iii) **41-2**'s shared emitter (else D8's local-copy
   fallback). Any gap blocks steps 4–7 — file it, do not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/QueryDcbEvidenceActivity.cs`** (D3) — the
   engine-side DCB evidence read over `IEventRepository.QueryEventsAsync`, fail-closed, keyset-paged, with
   its own unit test under `Tamma.Activities.Tests/Documents/`. **Coordinate with 41-7** — it needs the same
   activity at a standup altitude; one component, two consumers.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/StatusReportEvents.cs`** — `STATUS_REPORT.STARTED`
   / `.DRAFTED` / `.ACCEPTED` / `.FAILED`; tags `repository` / `tenantId` / `correlationId` (= the D2
   anchor) / `periodKey` / `audience`.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/StatusReportBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed: `BuildAnchor(repository, periodKey)` (D2, shared normalisation),
   `BuildProducerVariables(sprintPlanJson, evidenceJson, periodKey)` (D5's carrier set),
   `ProjectReportMarkdown(documentJson)` (the prose `body`, `""` on unreadable). Reuse
   `LifecycleBindingHelper.ReadLifecycleResult`/`IsAccepted` and `CreationBindingHelper.BuildFailureDetail`
   verbatim.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/StatusReportWorkflow.cs`** (D1/D2/D7/D9) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Graph: `ReadInputs` → `ComputeReEntryPosition` →
   `ReadPositionStage` → `FreshRun` → `EmitStatusReportStarted` → `FetchSprintPlan` → `QueryDcbEvidence` →
   `DispatchLifecycle` → `ReadLifecycleExit` → `LifecycleAccepted` → `EmitDrafted`/`EmitAccepted` |
   `EmitFailed` → `ExposeOutput` (single terminal region; **no `Finish`**). Dispatch input:

   ```csharp
   ["documentType"]          = "prose",
   ["producerRole"]          = AgentRole.ProjectManager.ToWire(),   // 41-1a
   ["producerAction"]        = AgentAction.ReportStatus.ToWire(),   // 41-1a
   ["producerVariablesJson"] = /* { sprintPlanJson, evidence, audience, periodKey } */,
   ["feedbackVariableName"]  = "evidence",                          // D5 — a DECLARED carrier
   ["documentKind"]          = ProseKind.StatusUpdate.ToWire(),     // D9, 41-1c vocabulary
   ["audience"]              = ProseAudience.Stakeholder.ToWire(),  // D9
   ["issueId"] = anchor, ["correlationId"] = anchor,                // D2
   ["tenantId"] / ["acceptanceRulesJson"]                           // D6's default rules
   ```

   `WaitForCompletion = new(true)`. `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`.
   **Lockstep:** the `documentKind`/`audience` input-key names are 41-1c's to define on
   `DocumentLifecycleWorkflow` (`:169-202` reads neither today) — agree them once, with 41-4.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`) — add
   `("status-report", [SprintPlan], Prose, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — bump `:45`
   by one with the reason in the comment, **and** add `"status-report"` to the `reconciled` array
   `:102-123` (Correction 6). *(If 41-6 has not landed, `SprintPlan` is not in the vocabulary and the
   consumes list is `[]` until it is — a follow-up one-line edit, recorded here so it is not forgotten.)*

7. **MODIFY the drift gates.** `ContractBindingTests.cs`: add
   `[("project_manager","report-status")] = new("ProseDocumentType.Validate", [ … 41-1c envelope token
   groups … ])`; **do not touch** the `(product_owner, summarize-stakeholder)` `IntentionallyUnbound` entry
   at `:299` (Correction 2) — add a cross-reference comment there instead, naming this story and why the
   cell was *not* reused. `TaxonomyDriftBuildTests.cs`: add `"StatusReportWorkflow"` to
   `ExpectedContributingWorkflows` (`:125`). Verify (do not pre-edit) `MinExpectedDispatchPairs` (`:110`)
   and `EveryConcreteWorkflow_IsIntrospectableOrAllowListed` (`:397`).

8. **CREATE the test suites** — `StatusReportWorkflowStructureTests.cs`, `StatusReportBindingHelperTests.cs`,
   `QueryDcbEvidenceActivityTests.cs`, `StatusReportLifecycleExecutionTests.cs`. See Test Plan.

9. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean (the `Audience`
   migration is 41-1c's and must already be applied).

**Part B has no steps.** It starts when the seam is owned.

## Data & Migrations

None **in this story**. Prose rows are `document_instances` with 41-1c's `Audience` column (its migration);
`STATUS_REPORT.*` and `DOCUMENT.*` ride the existing emitter → drain → `domain_events` path.
`has-pending-model-changes` must be clean. **Part B will need a table** (the persisted last-fired window,
per its requirements above) — owned by the seam, not by this story.

## Events

- **Emits (new constants, Part A):** `STATUS_REPORT.STARTED` (fresh runs only, data `periodStart`/`periodEnd`),
  `.DRAFTED` (data `evidenceCount`, `consumedSprintPlanId`), `.ACCEPTED` (data `documentId`, `audience`),
  `.FAILED` (detail names the typed outcome wire). Tags `repository` / `tenantId` / `correlationId` (= the
  D2 anchor) / `periodKey` / `audience`.
- **Emitted by the machinery this binding wires in:** the `DOCUMENT.*` family, `APPROVAL.*`,
  `ESCALATION.TRIGGERED`.
- **Consumes (as evidence, D3):** a tenant-scoped, time-windowed read of the `DOCUMENT.*`, `APPROVAL.*`,
  `ESCALATION.*`, `DEPLOY.*` and `BLOCKER.*` prefixes over `[periodStart, periodEnd)` via
  `IEventRepository.QueryEventsAsync`. **This is the first workflow in the codebase that reads the DCB
  stream as input rather than emitting to it** — noted because it makes the stream a load-bearing read path,
  not only an audit trail.

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution and evidence suites.

- **`StatusReportWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, `TaskCreation`-shaped: builds;
  DefinitionId `status-report`; threads `TenantId`; **zero** `Finish`; **exactly one** `DispatchWorkflow`,
  literal id `document-lifecycle`; **zero** targeting `llm-call`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables; `ScanLifecycleBindingDispatches()`
  contains `(project_manager, report-status)` attributed to this workflow; `MaterializeDispatchInput` yields
  `documentType == "prose"`, the `status-update` kind, the `stakeholder` audience and
  `feedbackVariableName == "evidence"`; one `ComputeReEntryPositionActivity`, one
  `FetchLatestAcceptedDocumentActivity`, one `QueryDcbEvidenceActivity`; `FlowDecision` id set exactly
  `{FreshRun, LifecycleAccepted}`; `[ResumeBehavior(LatestStateReEntry)]`; **no `Wait*` activity**
  (Correction 1). Plus a **negative pin**: the graph never dispatches
  `(product_owner, summarize-stakeholder)` (Correction 2 — so a future "simplification" that re-binds the
  taken cell fails loudly). **Covers AC2, AC3.**
- **`QueryDcbEvidenceActivityTests`** (Testcontainers, real `EventRepository`) — a seeded tenant stream:
  the half-open window is honoured (`from <= t < to`, boundary rows included/excluded correctly); type-prefix
  filtering returns only the requested families; keyset paging returns every row exactly once up to
  `MaxEvents`; **cross-tenant isolation** — tenant B's events never appear; **fail-closed** on an
  unregistered service, on an empty tenant (`QueryEventsAsync` throws `NotSupportedException`) and on a
  repository exception, in every case `Found=false` and no throw out of the activity. **Covers AC1 (evidence
  half).**
- **`StatusReportBindingHelperTests`** — `BuildAnchor` determinism, folding, hostile-character
  normalisation, and agreement with `BacklogBindingHelper`/`RoadmapBindingHelper`'s transform;
  `BuildProducerVariables` with SprintPlan present / absent / malformed; `ProjectReportMarkdown` on a valid
  prose body and on unreadable JSON (`""`); `BuildFailureDetail` names each reachable outcome wire.
- **`StatusReportLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path:** a seeded accepted `SprintPlan` + a seeded period event slice → scripted valid prose
  draft → review approve → `Accept` resume → `status=completed`, markdown projected; store asserts the
  accepted prose instance with `Audience = stakeholder`; replay asserts both event families. **Covers AC2.**
  (b) **degraded consumes (Correction 5):** no accepted `SprintPlan` → the report is still produced from DCB
  evidence alone, `STATUS_REPORT.DRAFTED` records `consumedSprintPlanId = null`. **Covers AC1's degradation
  posture.**
  (c) **prose is not schema-checked:** an unusually structured non-empty body validates; a whitespace-only
  body is rejected with 41-1c's named violation code and loops repair/revise, notes arriving through
  `evidence` (D5). **Covers AC2.**
  (d) **review over prose:** the review stage produces a `Review` whose `ParentDocumentId` is the prose
  document, with the D6 `product_owner` reviewer; control case pins that a `tech_writer` reviewer still
  throws at `GetReviewActionForRole` until 41-1a's arm lands. **Covers AC2.**
  (e) **validation exhaustion:** typed `ValidationExhausted` escalation with lineage, `STATUS_REPORT.FAILED`
  naming the outcome, `status=escalated`, no error terminal.
  (f) **re-entry:** crash after acceptance → short-circuits with the SAME `documentId`, exactly one
  `DOCUMENT.ACCEPTED` and one `STATUS_REPORT.ACCEPTED`, and **zero** extra evidence sweeps; crash mid-review
  → resumes at review of the same revision. **Covers AC3.**
  (g) **manual per-period idempotency (the honest partial of AC1):** dispatching `status-report` **twice**
  for the same `(repository, periodKey)` yields ONE accepted document — because both runs compute the same
  D2 anchor and the second re-enters via 39-10. This proves the *document* half of "idempotent per period"
  without the scheduler; the *firing* half stays unproven and is Part B's.
- **Drift gates (self-verifying, steps 6/7)** — `ContractBindingTests` (incl. the untouched
  `IntentionallyUnbound` entry and both universal pins), `TaxonomyDriftBuildTests`,
  `WorkflowInterfaceGraphTests` (count **and** `reconciled`) and `ResumableStandardStructuralTests`
  (declares, **no** allowlist entry) green in the same commit.

## Definition of Done

| AC | Part | Satisfied by step(s) | Verified by |
|---|---|---|---|
| 1 — scheduled, tenant-scoped, idempotent per period | **B — BLOCKED** | — | **Not achievable.** No seam exists; no story owns one. |
| 1 — every claim cites DCB evidence | A (partial) | 2, 5 (D3/D4) | `QueryDcbEvidenceActivityTests`; ExecutionTests (a). **Citation is instructed + review-caught, not machine-checked** (D4). |
| 1 — idempotent per period (document half) | A | 5 (D2/D7) | ExecutionTests (g) |
| 2 — thin lifecycle binding; prose reviewed by a `Review` | A | 5, 7 | StructureTests clause (a)–(f); ExecutionTests (c)(d) |
| 3 — resumable, 39-10 green without allowlist | A | 5 (D7) | StructureTests declaration + no-`Wait*`; ExecutionTests (f) |

**A story shipped as Part A only must say so in its own ACs** (epic README, Dependencies): it delivers a
manually/API-triggered status-report producer, not a scheduled one.

## Dependencies & Sequencing

- **Blocked by (hard, Part B):** **the tenant-aware scheduled-trigger seam (story 41-30, not yet built).** This is the one thing
  in Epic 41 that only that story builds. Writing it is a prerequisite for this story's AC1 and for all of Wave 2.
- **Blocked by (hard, Part A):**
  - **41-1a** — for `AgentRole.ProjectManager`, `AgentAction.ReportStatus` and
    `Prompts/project_manager/report-status.md`. **This is new relative to the story file**, which names only
    41-1c and the scheduler; it follows from Correction 2 (the story's stated cell is already taken by
    `ContextGatheringWorkflow`).
  - **41-1c** — for the `prose` type, the `Audience` field on envelope **and** `DocumentInstance` + its
    migration, and the `status-update`/`stakeholder` vocabulary entries.
  - **Epic 39** — 39-6/39-7/39-8/39-10/39-11 (all landed and verified in tree).
- **Soft-blocked by:** **41-2** (D7/D8's shared emitter); **41-6** (the consumed `SprintPlan` — optional per
  D3/Correction 5; the registry `consumes` list is `[]` until it lands).
- **Coordinated with:** **41-7** — D3's `QueryDcbEvidenceActivity` is one component with two consumers; both
  stories must not build it twice. Whichever lands first ships it.
- **Blocks:** nothing hard.
- **Out of scope, not blocking:** **42-9** (governed HTTP/external-API executor) — needed to *deliver* the
  accepted report; **39-19** — needed for a human to see it. Per Correction 7 both are downstream consumers.
- **Lockstep:** 41-1a's `report-status.md` front matter ↔ D5's variable set; 41-1c's `ProseDocumentType`
  contract + the `documentKind`/`audience` dispatch keys ↔ step 5 (agreed once, with 41-4); the anchor
  normalisation ↔ 41-3/41-4's helpers.
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5 → 6/7 (parallel) → 8 → 9. **Part B follows the
  seam, not this story.**

## Risks & Mitigations

- **The blocking risk is structural, not schedule.** Part B cannot be planned, estimated or de-risked
  against a component with no contract. Mitigation: ship Part A standalone with an explicit AC carve-out;
  the D2 anchor is designed to be the seam's idempotency key so the eventual integration is a dispatch, not
  a redesign.
- **Correction 2 changes the story's produce cell, and therefore its dependency set.** If the 41-1a owner
  declines `report-status`, the fallbacks are worse: repointing `ContextGatheringWorkflow` off
  `summarize-stakeholder` (a live, lenient consumer — a regression risk in a workflow this story has no
  business touching), or forking a second PO cell. Mitigation: 41-1a already lists `report-status` in its
  Scope 2; this plan only makes the dependency explicit.
- **D3's activity is new engine-side infrastructure inside a "thin binding" story.** It is the largest
  single piece of Part A and is shared with 41-7. Mitigation: it is a read-only, fail-closed wrapper over an
  existing, tenant-isolated repository method with a hard cross-tenant guard; its test suite is the
  isolation proof; building it once for two stories is cheaper than two narrow reads.
- **D4's honesty gap.** "Every claim cites DCB evidence" reads as a checked invariant and is not one.
  Mitigation: stated in D4, in the DoD table and in the test plan, so nobody ships believing it is enforced.
- **Reading the DCB stream as workflow input is a new load-bearing use of the audit trail.** A retention
  policy or a schema change to `domain_events` now breaks a *product* feature, not only forensics.
  Mitigation: named in Events; the read is bounded (`MaxEvents`) and fail-closed, so degradation is a thinner
  report, never a failed workflow.
- **Story-vs-code tensions:** Corrections 1–7 all resolve in favour of the code. Correction 2 changes the
  dependency set (the material one); Corrections 3–5 change the design; 1, 6 and 7 are mechanical or
  restatements.

## Est. Effort

**Part A (plannable):**

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a/41-1c precondition verification + dispatch-key and front-matter agreement | 0.5 |
| 2 | `QueryDcbEvidenceActivity` + its Testcontainers suite (shared with 41-7) | 1.25 |
| 3 | `StatusReportEvents` (+ 41-2 emitter reuse or local copy) | 0.25 |
| 4 | `StatusReportBindingHelper` | 0.5 |
| 5 | The binding workflow | 1.0 |
| 6 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 7 | `ContractBindingTests` (new entry + the untouched-entry cross-reference) + `TaxonomyDriftBuildTests` | 0.5 |
| 8 | Structure + helper + Testcontainers suites (a)–(g) | 1.25 |
| 9 | Full-suite green, review polish | 0.25 |
| **Part A total** | | **5.75** |

**Part B: not estimable.** The seam has no contract, no owner and seven consumers. Any number here would be
invented. When it is owned, this story's incremental cost on top of it is roughly **0.5–1 day** (register the
target, key the window on the D2 anchor, one idempotency integration test).

**Est. Effort: 5.75 days for Part A; Part B not estimable until the scheduler seam is owned.** The story
file says 3–4 days for the whole thing; that predates four verified facts — the named cell is taken
(Correction 2), there is no engine-side DCB read seam (Correction 3, +1.25 d), the document needs its own
lineage anchor (Correction 4), and the scheduled half has no component to build against. The story's
`## Estimated Effort` section is left at 3–4 days and this plan is the record of the delta.

## Blocks / Blocked by

- **Blocked by (hard):** the **tenant-aware scheduled-trigger seam** — story 41-30, blocks AC1 and all of Part
  B; **41-1a** (`project_manager` role + `report-status` cell + its prompt file — a dependency the story
  file does not name, forced by Correction 2); **41-1c** (the `prose` type, the `Audience`
  envelope/store field + migration, the `status-update`/`stakeholder` vocabulary); Epic 39 stories 39-6,
  39-7, 39-8, 39-10, 39-11 (all landed).
- **Blocked by (soft):** 41-2 (shared emitter); 41-6 (the consumed `SprintPlan` — optional, D3).
- **Coordinated with:** 41-7 (shares `QueryDcbEvidenceActivity`; also blocked on the same seam).
- **Blocks:** nothing hard.
- **Downstream, not blocking:** 42-9 (publish capability) and 39-19 (the surface a human reads it on) — both
  needed before the report is *delivered*, neither before it is produced and accepted.
