# Implementation Plan — Story 41-26: Runbook & Ops-Docs Authoring Workflow

## Scope & Deliverable

When this story is done, an operational runbook is authored as an audience-tagged prose document on the
Epic 39 spine, through one thin lifecycle binding:

| New workflow | DefinitionId | produces | producer cell |
|---|---|---|---|
| Runbook authoring | `runbook-authoring` | `prose` (kind `runbook`, audience `ops`) | **`(tech_writer, write-runbook)`** — see **C2**; the story says `(devops, …)`, which is not a legal cell |

It is the `TaskCreationWorkflow`/`DebugDiagnosisWorkflow` skeleton: exactly one `DispatchWorkflow` with
literal id `document-lifecycle`, zero `llm-call`, zero `Finish`, no retry plumbing, a declared
`feedbackVariableName`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, one
`ComputeReEntryPositionActivity`, one `WorkflowDocumentInterface` row (edge pin +1). It consumes
service/infra context (via the landed `context-gathering` workflow), the accepted incident `Diagnosis`
and postmortem prose from 41-22 when dispatched as a follow-up, and the previous accepted runbook
revision. `write-runbook.md` is rewritten to 41-1c's prose envelope with a real runbook body shape
(**C3**/**D4**). A new `RunbookEvents` family rides alongside `DOCUMENT.*`.

**This is the smallest of the three docs stories** — one document, one binding — and it is the natural
place to land last, inheriting 41-24 D6's `review-docs` fix and 41-25 D3's revision scheme.

## Pre-Reading

- `docs/stories/epic-41/story-41-26/41-26-runbook-and-ops-docs-authoring.md` — the story (ACs are source of truth, modulo **Corrections** below)
- `docs/stories/epic-41/README.md` — rules 1–5; the 41-1a review-selector gap (`:476-483`); the tech-writer coverage row (`:251`)
- **`docs/stories/epic-41/story-41-24/implementation-plan.md`** — **D6 (the `(tech_writer, review-docs)` rewrite) and D4 (the prose-envelope template pattern) are shared assets, inherited here.**
- **`docs/stories/epic-41/story-41-25/implementation-plan.md`** — **D3 (revision-scoped prose lifecycles, "update not duplicate") is a shared asset, inherited here.**
- **`docs/stories/epic-41/story-41-22/implementation-plan.md`** — **D3 (the producer-scoped incident id) and D10 (the postmortem action-item hand-off): this story is the consumer of both.**
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type, `Audience`, the kind (`runbook`) and audience (`ops`) vocabularies
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2's **fifteen-cell list (which does NOT contain `(devops, write-runbook)` — C2)**; Scope 3 / D1's `TechWriter` selector arm
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — the thin-binding recipe; `story-39-10/implementation-plan.md` — the resume standard
- **`apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:110-117`** — `WriteRunbook` is declared under the **tech_writer** block
- **`apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:139-151`** (devops set — **no `WriteRunbook`**) and **`:153-162`** (tech_writer set — `AgentAction.WriteRunbook` at `:160`)
- **`apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-runbook.md`** — the only runbook template on disk; **there is no `Prompts/devops/write-runbook.md`**
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs:93-129` (`taxonomy` set + `PROMPT.SEED.UNKNOWN_CELL`) and `:149-180` (`PROMPT.SEED.NO_BODY_FAMILY`) — the fail-loud loader, both directions
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the template; consumed-document read `:155-165`, producer-scoped issue id `:112`, feedback carrier `:190`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs:36` — `DefinitionId = "context-gathering"`; the dispatch pattern is `IssueDecompositionWorkflow`'s `GatherContext` node
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs:37-64`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` — Init reads `:169-202`; `IngestDraft` carves the first JSON object `:1170-1197`; `BuildReviewEnvelope` → unguarded `GetReviewActionForRole` at **`:1212`**; persist `:765-777`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs:239-259` — `ResolveSupersedes`: a `Produce` origin supersedes nothing (the cross-run gap 41-25 C4 files)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentReviewWorkflow.cs:256-265` — `BuildReviewerVariables` supplies exactly `planJson` + `documentJson`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProducerHelper.cs:168-203` — `DefaultFeedbackVariable = "workItemJson"`; undeclared feedback variables are dropped at render
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — the selector; `Helpers/ReviewerSelectionHelper.cs:60-70` — the 7-role roster
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`; `ContractBindingTests.cs` (`Bindings` `:82`, `ReviewProducerDispatchablePairs` `:505`, roster pin `:598`, universal pins `:626`/`:655`); `TaxonomyDriftBuildTests.cs:125`/`:460`; `Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`; `Tamma.Api.Tests/PromptStore/PromptFileLoaderTests.cs:20` (`ExpectedCellCount` is **derived**, never hand-edited); `Tamma.Api.Tests/Agents/RolePhaseMapTests.cs:64`
- **NOT FOUND:** `DocumentTypeKey.Prose` / `ProseDocumentType` / `DocumentEnvelope.Audience` / `DocumentInstance.Audience` (41-1c); the `TechWriter` selector arm (41-1a); `AgentAction.WriteRunbook` in the devops eligible set **anywhere**; `Prompts/devops/write-runbook.md`; any `RUNBOOK.*` event constant. Everything else above exists and was read.

## Corrections to the story

- **C1 — AC3's `[ResumeBehavior(Both)]` fails the 39-10 gate.** Identical to 41-24 C1 / 41-25 C1: `Both`
  requires a canonical suspend node in *this* workflow's graph
  (`ResumableStandardStructuralTests.cs:158-198` + the honesty inverse `:202-236`); a thin binding has
  none, because the accept gate lives inside the dispatched `document-lifecycle` child (39-12 D7; landed
  precedents `TaskCreationWorkflow.cs:47`, `DebugDiagnosisWorkflow.cs:38`). **Correct declaration:
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
- **C2 — `(devops, write-runbook)` IS NOT A LEGAL CELL, and no story mints it.** This is the significant
  finding for 41-26 and it is not recorded anywhere in the epic. Verified three ways:
  1. `AgentAction.WriteRunbook` is declared under the **tech_writer** block (`AgentAction.cs:110-117`).
  2. `RolePhaseMap.s_eligibleActions` puts it in the **tech_writer** set only (`:160`). The **devops** set
     (`:139-151`) contains `ContextScan, PlanDeployment, ImplementInfrastructure, ConfigureCicd, Deploy,
     Rollback, MonitorHealth, DiagnoseIncident, PlanIncidentResponse, WritePostmortem, AssessCapacity,
     ReviewOperability` — **no `WriteRunbook`**. So `RolePhaseMap.IsRoleEligibleForPhase("write-runbook",
     "devops")` returns `false`, and `ReviewerSelectionHelper.Resolve`-style eligibility asserts and the
     `TaxonomyDriftBuildTests.EveryDispatchSitePair_IsEligibleInTaxonomy` build gate would both fail.
  3. The only template on disk is `Prompts/tech_writer/write-runbook.md`; there is **no**
     `Prompts/devops/write-runbook.md`.
  Furthermore, **41-1a's fifteen-cell list does not include it** (`41-1a…:25-32`: `plan-sprint`,
  `synthesize-standup`, `facilitate-retro`, `track-impediments`, `report-status`, `coordinate-release`,
  `draft-user-flow`, `author-ui-spec`, `review-design`, `audit-accessibility`, `triage-tech-debt`,
  `design-system`, `triage-pr`, `manage-regression`, `incident-rootcause`) — and the epic README's 41-1a
  gating table lists 41-26 only under the *review-action selector* row, not under "a new action cell".
  **Resolution (D2): use `(tech_writer, write-runbook)`, which is legal today.** The alternative —
  adding `WriteRunbook` to the devops eligible set — is a lockstep taxonomy change owned by 41-1a, not by
  this story, and is enumerated in **D3** so the choice is informed rather than defaulted.
- **C3 — the produce prompt instructs raw markdown in a generic skeleton.** `write-runbook.md` carries
  front matter `variables: role, workItemJson, findings, audience` / `enableTools: false` /
  `maxTokens: 2048` / `version: 1` and the same `## Summary / ### Key Findings / ### Action Items /
  ### Details` block as the other four tech_writer authoring cells; only line 18 differs ("phrase Action
  Items as the operator's ordered procedure, including verification and rollback steps where relevant").
  Two consequences: (i) `DocumentLifecycleWorkflow.IngestDraft` (`:1177-1180`) carves the **first JSON
  object** out of the reply and fails the produce turn if there is none — raw markdown cannot be a
  lifecycle payload, so it must move inside 41-1c's `{kind, audience, title, body}` envelope; (ii) the
  story's own "symptoms → checks → remediation → escalation" shape is **not** what the template
  instructs — Summary/Key Findings is a research skeleton, not a runbook. Rewritten in D4. It does
  already declare `audience`, so the 41-1c tag threads cleanly.
- **C4 — `(tech_writer, review-docs)` is a PR-diff cell, and 41-1a does not fix that.** Same finding as
  41-24 C5 / 41-25 C5: the cell declares `variables: role, prDescription, diff, conventions` while
  `DocumentReviewWorkflow.BuildReviewerVariables` supplies only `planJson` + `documentJson`
  (`:256-265`); it does not declare `workItemJson`, which is `ReviewProducerHelper.DefaultFeedbackVariable`
  (`:203`), so repair notes drop at render; and it instructs a diff-review JSON, not the `Review` wire.
  **41-24 D6 owns the rewrite; this story inherits it** (or carries it if it ships first).
- **C5 — the story's "or an ops peer" review alternative already works.** `(devops, review-operability)`
  is eligible (`RolePhaseMap.cs:151`), is what `GetReviewActionForRole(Devops)` returns (`:383`), and is
  already classified in `ContractBindingTests.ReviewProducerDispatchablePairs` (`:513-514`). So a
  devops-reviewed runbook is reachable **today**, with no 41-1a dependency at all — which makes this the
  one docs story whose review stage has a working fallback. **D6** makes that the default and treats
  `(tech_writer, review-docs)` as the upgrade.
- **C6 — AC2's "with the incident `Diagnosis` as input" needs 41-22's producer-scoped id.** The 39-11
  latest-accepted read scopes by `(issueId, documentType)` with **no producer filter** — stated verbatim
  at `CreationBindingHelper.cs:84-94` and filed to 39-11. 41-22 D3 keys its incident documents on
  `ScopeIssueId(incidentId, "incident-diagnosis")`. A read on the bare issue id would find the *wrong*
  `diagnosis` (e.g. a `debug-diagnosis` one from `SingleIssueCycle`) or none. So the dispatch contract is
  an explicit `incidentDiagnosisScope` input, not a guess (**D5**).
- **C7 — `.dev/findings/document-lifecycle-persist-not-wired.md` is STALE.** Persistence *is* wired
  (`DocumentLifecycleWorkflow.cs:770-777`). Do not plan around it.
- **C8 — the story's cite of the unguarded selector call is 13 lines early.** It is
  `DocumentLifecycleWorkflow.cs:1212`, inside `BuildReviewEnvelope` (`:1200`), not `:1199`.

## Design Decisions

- **D1 — one producing binding, `runbook-authoring`, on the `TaskCreationWorkflow` skeleton.**
  `ReadInputs → ComputeReEntryPosition → FreshRun? → GatherContext ("context-gathering") →
  FetchIncidentDiagnosis? → FetchPreviousRunbook → DispatchLifecycle → ReadLifecycleExit → ExposeOutput`.
  **Note the declared deviation from rule 1(a)**: like `IssueDecompositionWorkflow` (39-12), this binding
  carries **two** `DispatchWorkflow`s — `context-gathering` (the `consumes` side) and
  `document-lifecycle` (the produce side) — and **zero** `llm-call`, **zero** `Finish`. That is the
  landed pilot's own shape, and the structure test pins the exact two-element set rather than "exactly
  one", exactly as `IssueDecompositionWorkflowStructureTests` does. One `WorkflowDocumentInterface` row;
  edge pin **+1**.
- **D2 — the produce cell is `(tech_writer, write-runbook)` (per C2).** It is legal today, its template is
  on disk, and it needs no taxonomy change from anyone. The story's user-story framing ("as a devops
  engineer / tech writer") is unaffected: rule 4's human-or-agent assignment is the orchestrator's
  routing decision, not the cell's role half. What the role half *does* determine is which `_system.md`
  identity preamble and which provider chain the produce turn uses — and a runbook authored under the
  tech-writer preamble, reviewed by a devops peer (D6), is the better division of labour anyway.
- **D3 — if `(devops, write-runbook)` is wanted instead, here is the exact lockstep, and it belongs to
  41-1a.** Enumerated so the choice is informed:
  1. `RolePhaseMap.s_eligibleActions[AgentRole.Devops]` gains `AgentAction.WriteRunbook` (`:139-151`) —
     the eligibility map. *(The `AgentAction` enum member already exists; no enum change, no
     `AgentActionTests.cs:38` `Be(80)` bump, no `RolePhaseMapTests.cs:64` `ValidActions` bump — those pin
     the **action** count, not the cell count.)*
  2. A new **`apps/tamma-elsa/src/Tamma.Api/Prompts/devops/write-runbook.md`** — mandatory. The loader is
     fail-loud **both ways**: a taxonomy cell with no file throws `PROMPT.SEED.NO_BODY_FAMILY` at static
     init (`PromptFileLoader.cs:160-168`), and a file outside the taxonomy throws
     `PROMPT.SEED.UNKNOWN_CELL` (`:114-119`, `:296-302`). Either refuses to start the process.
  3. `PromptFileLoaderTests.cs:20` `ExpectedCellCount` is **derived** from `RolePhaseMap.EligibleActions`
     and must **NOT** be hand-edited (41-1a AC7 says so explicitly) — it moves 93 → 94 by itself.
  4. Any cell-count assertion in `RolePhaseMapTests` moves by one, as a conscious edit with a reason.
  5. `ContractBindingTests`: a second `Bindings` entry for the devops cell at `ProseDocumentType.Validate`
     — two cells producing one document type is fine (`plan-generation` + `task-creation` both produce
     `plan`), but each needs its own compiled dispatch site or it goes stale under the clause-(c) guard
     (`:722-737`).
  6. The two templates then diverge and must be kept in sync by hand — which is the real cost, and the
     reason **D2 is the recommendation**.
- **D4 — the template is rewritten to the prose envelope with a REAL runbook body (per C3).** Front
  matter becomes
  `variables: role, workItemJson, findings, audience, serviceContext, diagnosisJson, conventions`;
  `findings` stays the declared `feedbackVariableName` carrier and also carries D5's previous revision.
  Body instructs `{"kind": "runbook", "audience": "ops", "title": …, "body": "<markdown>"}`, where the
  markdown follows the story's own shape: **Symptoms** (what an operator observes) → **Checks** (ordered,
  each with the exact command/dashboard and the expected result) → **Remediation** (ordered, each with a
  verification step and a rollback note) → **Escalation** (who, when, with what context). The generic
  Summary/Key-Findings/Action-Items skeleton is deleted. Inherits 41-24 D4's pattern.
- **D5 — inputs are explicit, never guessed (per C6).** The binding takes `repository`, `serviceKey`,
  optional `incidentDiagnosisScope` and optional `postmortemDocumentId`. When
  `incidentDiagnosisScope` is supplied (the 41-22 follow-up path), `FetchLatestAcceptedDocumentActivity`
  reads the `diagnosis` on **that** scope; when it is absent (the on-demand path), the read is skipped
  and `Found=false` is a legitimate answer. No bare-issue-id fallback — that would silently pick up an
  unrelated `debug-diagnosis` document. **AC2 is a contract between 41-22 and 41-26, and this states it.**
- **D6 — review defaults to the ops peer, upgrades to the tech writer (per C5).** The binding names no
  reviewer (39-12 D8 — reviewer selection is `AcceptanceRules` policy, forwarded verbatim as
  `acceptanceRulesJson`). The **documented default** for a runbook is `ReviewerRole = "devops"` →
  `(devops, review-operability)`, which works today with **no 41-1a dependency**. `ReviewerRole =
  "tech_writer"` → `(tech_writer, review-docs)` becomes available once 41-1a's selector arm **and**
  41-24 D6's template rewrite both land. This makes 41-26 the only one of the three docs stories that can
  ship a fully working review path before either lands.
- **D7 — revision scoping inherits 41-25 D3.** `runbookId = "runbook#{repository}#{serviceKey}"`; the
  lifecycle is keyed on `ScopeIssueId(runbookId, "runbook") + "#" + revisionKey`, where `revisionKey` is
  the incident id on the 41-22 follow-up path and a caller-supplied token (or the current date) on the
  on-demand path. The previous accepted runbook is read and folded into the DECLARED `findings` carrier
  as *"the current runbook — revise it, do not restate it"*, with its id exposed as `parentDocumentId`.
  Same caveat as 41-25 C4: there is **no cross-run `supersedes_document_id`** edge, because
  `DocumentLifecycleWorkflow` takes no `supersedesDocumentId` input and
  `DocumentLifecycleHelper.ResolveSupersedes` returns `null` for a `Produce` origin (`:255-259`). Filed
  to 39-6/39-11 by 41-25; not re-filed here.
- **D8 — `enableTools` stays `false`; service context comes from `context-gathering`.** The landed
  `context-gathering` workflow already runs a tool-enabled `(role, context-scan)` scan and returns
  findings; the binding dispatches it and folds the result into `serviceContext`. So the produce turn
  needs **no** tool of its own and therefore **no Epic 42 dependency**. *(This refines the story's Epic 42
  caveat: drafting is fully agent-reachable today; only publishing the accepted runbook to an ops-docs
  host needs 42-9.)*
- **D9 — a new `RunbookEvents` family; nothing named `RUNBOOK.*` exists.** Constants:
  `RUNBOOK.STARTED`, `RUNBOOK.CONTEXT_GATHERED`, `RUNBOOK.DRAFTED`, `RUNBOOK.ACCEPTED`, `RUNBOOK.FAILED`.
  `StatusForEvent`: `FAILED` → `"error"`, `STARTED` → `"started"`, else `"success"`. Tags `repository`,
  `serviceKey`, `incidentId`, `documentId`, `parentDocumentId`, `audience`, `correlationId`, `tenantId`.
  One `EmitRunbookEventActivity`, copying `EmitDecompositionEventActivity`'s shape.
  `RUNBOOK.CONTEXT_GATHERED` is emitted on fresh runs only — the 39-12 D3 rule (a re-entry is not a new
  authoring run).

## Implementation Steps

1. **Precondition gate (no code).** Verify in tree and compiling: **41-1c** (`DocumentTypeKey.Prose`,
   `ProseDocumentType`, `DocumentEnvelope.Audience`, `DocumentInstance.Audience` + migration, kind
   `runbook`, audience `ops`). Confirm whether **41-24 or 41-25** has landed (if so, inherit the
   `review-docs.md` rewrite and its classification entry; if not, decide whether this story carries it or
   defers to D6's ops-peer default). Confirm whether **41-1a** has landed (needed only for the
   `tech_writer` review upgrade, not for anything else in this plan). **Record the C2/D2 decision
   explicitly in the story before starting**: `(tech_writer, write-runbook)` (recommended, zero taxonomy
   change) vs. D3's `(devops, write-runbook)` lockstep, which must be re-assigned to 41-1a.

2. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/RunbookBindingHelper.cs`** (D5/D7) —
   pure, Elsa-free, total: `ComposeRunbookId(repository, serviceKey)`,
   `ScopeFor(runbookId, revisionKey)`, `FoldServiceContext(contextResultJson)` (fail-closed `""`),
   `BuildFailureDetail(exit)`. Determinism pinned by test.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Runbook/RunbookEvents.cs` +
   `EmitRunbookEventActivity.cs`** (D9) — copy `Decomposition/DecompositionEvents.cs` +
   `EmitDecompositionEventActivity.cs` shape exactly (pure static `BuildTammaEvent`, fail-closed default
   `EventType`, `TammaEventEmitter.Emit`).

4. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-runbook.md`** (C3/D4) — prose
   envelope, `version: 2`, front matter and Symptoms/Checks/Remediation/Escalation body per D4.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**: add
   `[("tech_writer","write-runbook")] = new("ProseDocumentType.Validate", [One("\"kind\""),
   One("\"audience\""), One("\"title\""), One("\"body\"")])` to **`Bindings`** — **not**
   `IntentionallyUnbound` (a document producer must be bound, `:655-674`).

5. **(Conditional) `review-docs.md` rewrite + `ReviewProducerDispatchablePairs` classification** — only if
   neither 41-24 nor 41-25 has done it and the `tech_writer` review path is wanted (C4 / 41-24 D6). **The
   D6 ops-peer default means this story can ship without it.**

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RunbookAuthoringWorkflow.cs`
   (`runbook-authoring`)** (D1/D5/D7) — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C1); graph per
   D1; the `GatherContext` node copies `IssueDecompositionWorkflow`'s `DispatchWorkflow("context-gathering")`
   + `StoreContextResult` pair verbatim; **zero `Finish`**, single `ExposeOutput` terminal region.
   Dispatch inputs:
   ```csharp
   ["documentType"]          = "prose",
   ["producerRole"]          = AgentRole.TechWriter.ToWire(),      // D2
   ["producerAction"]        = AgentAction.WriteRunbook.ToWire(),
   ["producerVariablesJson"] = { workItemJson, serviceContext, diagnosisJson,
                                 findings = previousRunbookBody,   // D7: revise, do not restate
                                 audience = "ops", conventions = "" },
   ["feedbackVariableName"]  = "findings",
   ["issueId"]               = ScopeFor(runbookId, revisionKey),
   ["correlationId"]         = same, ["tenantId"], ["acceptanceRulesJson"],
   ```
   Outputs: `status`, `outcome`, `documentId`, `documentJson`, `parentDocumentId`, `audience`,
   `serviceKey`, `runbookId`.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (`BuildSeed`)** — one row,
   `Provisional=false`:
   `new WorkflowDocumentInterface("runbook-authoring", new[]{ DocumentTypeKey.Diagnosis }, DocumentTypeKey.Prose, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`** — bump
   the pin by **+1** from whatever it is at merge time (16 today), with the one-line reason.

8. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125`** —
   add `RunbookAuthoringWorkflow` to `ExpectedContributingWorkflows` with a one-line comment.

9. **CREATE the test suites** (see Test Plan).

10. **Finish** with full `dotnet test` and `dotnet ef migrations has-pending-model-changes` (clean — no
    schema here; `Audience` is 41-1c's migration).

## Data & Migrations

None. The runbook persists through 39-11's `document_instances` via the lifecycle's own persist nodes
(`DocumentLifecycleWorkflow.cs:770-777` — C7); `RUNBOOK.*` rides the existing `TammaEventEmitter` drain →
`EventRepository` → `domain_events`. The `Audience` column is **41-1c's**.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new):** `RUNBOOK.STARTED`, `RUNBOOK.CONTEXT_GATHERED` (fresh runs only),
  `RUNBOOK.DRAFTED`, `RUNBOOK.ACCEPTED`, `RUNBOOK.FAILED` — tags `repository`, `serviceKey`,
  `incidentId`, `documentId`, `parentDocumentId`, `audience`, `correlationId`, `tenantId`.
- **Emitted by the machinery this story wires in:** the `DOCUMENT.*` family (`DocumentEvents.cs:28-53`),
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes (reads, does not emit):** the accepted incident `Diagnosis` (via 41-22's producer-scoped id,
  D5) and its own `RUNBOOK.ACCEPTED` history for the D7 previous-revision lookup.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`RunbookBindingHelperTests`** (pure) — `ComposeRunbookId` / `ScopeFor` determinism (same inputs twice
  → byte-identical); distinct services and distinct revision keys yield distinct scopes (**the prose
  collision guard**, D7); `FoldServiceContext` fail-closed on unreadable input; `BuildFailureDetail`
  names each reachable outcome wire.
- **`RunbookAuthoringWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set,
  adapted to D1's declared two-dispatch shape (the `IssueDecompositionWorkflowStructureTests` variant):
  builds; `DefinitionId == "runbook-authoring"`; threads `TenantId`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`; **exactly two** `DispatchWorkflow`s whose literal
  def ids are exactly `{context-gathering, document-lifecycle}`; **zero** `llm-call`;
  `ScanLifecycleBindingDispatches()` contains `(tech_writer, write-runbook)`;
  `MaterializeDispatchInput` shows `documentType == "prose"` and `feedbackVariableName == "findings"`;
  **zero** `Finish`; every graph leaf inside the single `ExposeOutput` region; one
  `ComputeReEntryPositionActivity`; the `FetchLatestAcceptedDocumentActivity` nodes;
  `[ResumeBehavior(LatestStateReEntry)]` (C1); no `Wait*` node. **Covers AC1, AC3.**
- **`ProseDocumentType` fixtures (`Tamma.Core.Tests`, against 41-1c's type)** — a runbook payload
  (`kind=runbook, audience=ops`) validates; unknown `kind`/`audience` fail with 41-1c's distinct codes;
  an empty `body` fails; a body with no headings at all still validates (41-1c AC2 — prose is not
  schema-checked, and a runbook is no exception).
- **Taxonomy guard (the C2 regression pin)** — a standing assertion that
  `RolePhaseMap.IsRoleEligibleForPhase("write-runbook", "tech_writer")` is `true` **and**
  `IsRoleEligibleForPhase("write-runbook", "devops")` is `false` unless D3's lockstep has been performed
  by 41-1a. This is what stops the story's `(devops, …)` phrasing from being re-introduced by hand and
  silently failing `EveryDispatchSitePair_IsEligibleInTaxonomy` at build time.
- **Contract/drift guards (self-verifying, steps 4, 7–8)** — `ContractBindingTests` green with the new
  `Bindings` entry at `ProseDocumentType.Validate`; both universal pins green;
  `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` clause (c) non-stale;
  `LifecycleBindingWalk_FindsPairs_NotANoOp` finds the new binding; `WorkflowInterfaceGraphTests` pin +1.
- **`ResumableStandardStructuralTests`** — green with **no** allowlist entry for
  `RunbookAuthoringWorkflow`. **Covers AC3.**
- **`RunbookAuthoringExecutionTests`** (Testcontainers, on the 39-6/39-10/39-12 shared fixture: real
  `DocumentLifecycleWorkflow` + `DocumentReviewWorkflow` + the new binding, stub `llm-call` and stub
  `context-gathering`, real Elsa EF persistence + event drain + `IDocumentInstanceRepository`, decisions
  via `DocumentDecisionResumeEndpoint.Resume`) —
  (a) **On-demand happy path:** no incident inputs → context gathered → prose produced → reviewed by the
  **devops ops peer** (D6, works with no 41-1a) → accepted; one `document_instances` row, type `prose`,
  `Audience = "ops"`; `RUNBOOK.*` present alongside `DOCUMENT.*`; `RUNBOOK.CONTEXT_GATHERED` exactly once.
  (b) **AC2 — postmortem follow-up:** seed an accepted incident `Diagnosis` under 41-22's producer-scoped
  incident id, dispatch with `incidentDiagnosisScope` → the diagnosis body appears in the produce turn's
  variables and the accepted runbook's lineage names it. **Plus the negative that makes D5/C6 real:**
  seed an *unrelated* `debug-diagnosis` document under the bare issue id and assert it is **not** picked
  up when `incidentDiagnosisScope` is absent.
  (c) **The prose collision guard:** run for a service whose *other* prose document (e.g. a 41-25
  user-docs doc) is already accepted under the same repository → the runbook still **produces** (no
  short-circuit on the sibling prose document). Written to fail without D7's scoping.
  (d) **Idempotent per revision:** re-dispatch the same `(service, revisionKey)` → re-entry
  short-circuits to `Complete`, `DOCUMENT.REENTERED`, exactly one `DOCUMENT.ACCEPTED` and one
  `RUNBOOK.ACCEPTED`; **no second `RUNBOOK.CONTEXT_GATHERED`** (the 39-12 D3 rule).
  (e) **Revision:** a second `revisionKey` produces a new document whose variables carry the previous
  body and whose output `parentDocumentId` is the previous document id; assert
  `document_instances.SupersedesDocumentId` is **null** across runs, pinning the known storage gap
  (41-25 C4) rather than assuming it fixed.
  (f) **`tech_writer` review upgrade (needs 41-1a + 41-24 D6):** rules naming `tech_writer` → the review
  stage completes and produces a `Review` whose `ParentDocumentId` is the runbook. **Asserted to THROW
  today** (`RolePhaseMap.cs:385-386`), flipped to the positive assertion when both land — so the block is
  visible in CI, not only in prose.
  (g) **Critical-path escalation:** rules with `AcceptorRequirement.Human` at autonomy 100 → the accept
  gate suspends and publishes an `AcceptanceRequest`. **Policy, not code (D6).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin lifecycle binding; prose reviewed by a `Review` | 4, 6, 7 (D1/D2/D4/D6) | `RunbookAuthoringWorkflowStructureTests` (two-dispatch declared deviation, zero `llm-call`, zero `Finish`); `ProseDocumentType` fixtures; `ContractBindingTests`; Execution (a). **Producer cell is `(tech_writer, write-runbook)`, NOT `(devops, …)` — C2/D2. Review works today via the devops ops peer (C5/D6); the `tech_writer` path additionally needs 41-1a + 41-24 D6** |
| 2 — dispatchable as a postmortem follow-up with the incident `Diagnosis` as input | 6 (D5) | Execution (b), including the negative that a bare-issue-id read must NOT pick up an unrelated diagnosis. **Requires 41-22 to pass its producer-scoped incident id — a stated contract, C6** |
| 3 — `[ResumeBehavior]`; 39-10 gate green without allowlist | 6 (C1) | `ResumableStandardStructuralTests`; `RunbookAuthoringWorkflowStructureTests`. **Declaration is `LatestStateReEntry`, not `Both` (C1)** |

## Blocks / Blocked by

- **Blocked by — hard:**
  - **41-1c** — `prose` `DocumentTypeKey` + `ProseDocumentType` + `DocumentEnvelope.Audience` +
    `DocumentInstance.Audience` (+ migration) + kind `runbook` + audience `ops`. Nothing in this story
    produces a document without it.
  - **Epic 39** — 39-2 (registry/envelope), 39-6 (`document-lifecycle`), 39-7 (`document-review`), 39-8
    (accept gate + resume endpoint), 39-10 (resume standard + gate), 39-11 (store + persist wiring —
    landed, C7). All in tree.
  - **`context-gathering`** — landed (`ContextGatheringWorkflow.cs:36`), dispatched as-is.
- **Blocked by — REVIEW STAGE ONLY, and only for the UPGRADE path:**
  - **41-1a** — the `(tech_writer, review-docs)` selector arm (`RolePhaseMap.cs:376-387` throws for
    `TechWriter`; `DocumentLifecycleWorkflow` calls it unguarded at **`:1212`**). **This is a review-stage
    block, not a produce-stage one — and for 41-26 it is not even a hard one**: D6/C5 make
    `(devops, review-operability)` the documented default, which is eligible, selector-reachable and
    already classified today (`ContractBindingTests.cs:513-514`). 41-26 is therefore the one docs story
    that can ship a complete produce→review→accept path before 41-1a lands. Execution (f) asserts the
    throw until it does; the plan does not work around it.
  - **41-24 D6** — the `review-docs` *prompt* rewrite, the half 41-1a does not cover (C4). Same
    conditional: only the upgrade path needs it.
  - **41-1a is NOT needed for the produce cell.** Per C2, `(devops, write-runbook)` — which the story
    names — is not a legal cell and 41-1a does not mint it; D2 uses the legal `(tech_writer, write-runbook)`
    instead, so **no 41-1a produce-side dependency exists**. If the devops cell is wanted after all, D3's
    lockstep must be **added to 41-1a's scope**, which is a change to 41-1a, not to this story.
- **Blocked by — partial (AC-level, named):** **39-17/39-19** (the accept gate publishes and parks),
  **39-20** (no role-addressed delivery — `InitiatorOnlyTaskAudienceResolver`, `Program.cs:445-447`).
  Neither is on an AC.
- **Contract dependency (not a block):** **41-22** must pass `incidentDiagnosisScope` for AC2's
  follow-up path (C6/D5). On-demand dispatch needs nothing from 41-22.
- **NOT blocked by:** the **scheduled-trigger seam** (on-demand / event-triggered, not cron — 41-26 is
  not in the seam's seven-consumer list); **41-1b** (no new document type); **Epic 42** for drafting
  (D8 — `context-gathering` supplies service context and the produce turn keeps `enableTools: false`;
  only publishing to an ops-docs host needs 42-9).
- **Blocks:** nothing. It is a leaf, and the cheapest of the three docs stories.
- **Shares with 41-24 and 41-25:** the rewritten `(tech_writer, review-docs)` cell + its classification
  entry + `ReviewDocsCellTests` (41-24 D6), and the prose revision-scoping convention (41-25 D3 / D7
  here). Exactly one of the three authors each; the others inherit.
- **Files, does not fix:** `(devops, write-runbook)` as a taxonomy addition → **41-1a**, with D3's exact
  lockstep (C2). Cross-run `supersedesDocumentId` on `document-lifecycle` → 39-6/39-11 (filed by 41-25).
  A producer/kind filter on the 39-11 latest-accepted read → 39-11 (already filed at
  `CreationBindingHelper.cs:84-94`).

## Risks & Mitigations

- **The story names a cell that does not exist and that no enabler mints (C2).** Highest-value finding
  here: taken literally, 41-26 fails `TaxonomyDriftBuildTests.EveryDispatchSitePair_IsEligibleInTaxonomy`
  at build time and `PromptFileLoader` at startup. Mitigation: D2 switches to the legal cell (zero cost),
  D3 enumerates the alternative in full so the decision is informed, and the Test Plan's taxonomy guard
  pins both directions so the wrong cell cannot be reintroduced silently.
- **Prose is one `DocumentTypeKey` for ten kinds; the 39-11 read has no kind filter.** A runbook for a
  repository that already has an accepted user-docs or postmortem prose document would short-circuit and
  produce nothing. Mitigation: D7's scoping + Execution (c), written to fail without it.
- **AC2 silently reading the wrong `Diagnosis`.** A bare-issue-id read would find a `debug-diagnosis`
  document from `SingleIssueCycle` and produce a runbook about the wrong thing — a *plausible-looking*
  wrong answer, the worst failure mode. Mitigation: D5's explicit scope input, no fallback, and Execution
  (b)'s negative assertion.
- **Runbook quality is not machine-checkable.** Prose is deliberately unvalidated (41-1c AC2), so
  "symptoms → checks → remediation → escalation" is a prompt convention and a review concern only.
  Mitigation: D4 makes the shape the *format block* rather than a line-18 aside, and D6's ops-peer review
  is the check. Stated as a limitation rather than papered over.
- **Two `DispatchWorkflow`s reads as a rule-1(a) violation.** Mitigation: D1 declares the deviation and
  points at the landed precedent (`IssueDecompositionWorkflow`, 39-12), and the structure test pins the
  exact two-element set — which is a stronger assertion than "exactly one", not a weaker one.
- **Story-vs-canon tensions:** C1 (resume mode), C2 (illegal cell) and C3 (markdown vs JSON payload) are
  genuine contradictions, all resolved in favour of the code. C4, C5 and C6 are gaps the story does not
  mention.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + the C2/D2 cell decision + 41-22/41-24 lockstep | 0.25 |
| 2 | `RunbookBindingHelper` | 0.25 |
| 3 | `RunbookEvents` + emit activity | 0.25 |
| 4 | `write-runbook.md` → prose envelope + real runbook body + binding entry | 0.5 |
| 5 | `review-docs.md` rewrite — **0 if inherited or if the D6 ops-peer default is used** | 0.0–0.5 |
| 6 | `RunbookAuthoringWorkflow` (two dispatches, three consumed reads) | 0.75 |
| 7–8 | Registry seed + edge pin + drift contributor entry | 0.25 |
| 9 | Structure test, helper/event unit tests, prose fixtures, the C2 taxonomy guard | 0.5 |
| 9 | Testcontainers scenarios (a)–(g) | 0.75 |
| 10 | Full-suite green, migration check, review polish | 0.25 |
| **Total (inheriting / ops-peer review)** | | **3.75** (story estimate: 2–3 days) |
| **Total (carrying 41-24 D6)** | | **4.25** |

The overrun is C3 (a template rewrite the story did not anticipate) and D5/D7 (AC2's diagnosis contract
and the revision scheme). If 41-24 and 41-25 land first, this story is the cheapest in the epic's docs
family and reuses four of their assets unchanged.
