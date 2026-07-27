# Implementation Plan — Story 41-23: Capacity & Health Review Workflow

> ## ⛔ THIS PLAN IS BLOCKED. Do not schedule it.
>
> Story 41-23's AC1 — *"Scheduled, tenant-scoped, idempotent per window"* — depends on a **tenant-aware
> scheduled-trigger seam that does not exist and that no story owns**. It is the one Wave-0 enabler in
> Epic 41 with no owner (`epic-41/README.md:297`, `:454-472`). Everything below Phase 0 is written on the
> assumption that seam lands first; **Phase 0 is the work of writing it**, and it is explicitly *not*
> inside this story's 4–5 day estimate. Nothing in this plan works around the gap: there is no "poll from
> the workflow", no "fire it from a cron in CI", no "reuse `HourlyAnalyticsRollupScheduler`". Each of
> those was considered and rejected in **D0**.
>
> The **producing half** (Phases 1–3: the `Findings` binding, the prompt rewrites, the analytics read
> activity) is seam-independent and can ship on-demand-triggered, satisfying AC2 and AC3 but **not AC1**.
> That is the only honest partial delivery, and it is called out per-AC in the Definition of Done.

## Scope & Deliverable

When this story is done, a **scheduled, tenant-scoped, per-window-idempotent** sweep produces a typed
`Findings` health/capacity report on the Epic 39 spine:

| New artefact | What |
|---|---|
| `capacity-health-review` | a thin binding over `document-lifecycle`, `produces: findings`, cell `(devops, assess-capacity)` |
| `FetchHealthSignalsActivity` | the in-process read of analytics rollups + DCB deploy/health events for a window (**not** a tool — see D3) |
| `HealthReviewEvents` + `EmitHealthReviewEventActivity` | `HEALTH_REVIEW.*` alongside `DOCUMENT.*` |
| the scheduled-trigger seam | **Phase 0 — story 41-30, not yet built** — see the block notice |

## Pre-Reading

- `docs/stories/epic-41/story-41-23/41-23-capacity-and-health-review.md` — the story (ACs are source of truth, modulo **Corrections** below)
- `docs/stories/epic-41/README.md:454-472` — the scheduler dependency, in full; `:297` — the seam's Wave-0 row
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — the thin-binding recipe
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — the resume standard
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — the non-reusable precedent, in full (366 lines; three types: options `:15-42`, `BackgroundService` `:70-256`, `IRollupSchedulerLeaderLock` + `PostgresAdvisoryLeaderLock` `:265-366`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditChainCheckpointScheduler.cs` — **the second copy of the same pattern** (Story 37-2): `FireAtMinute=15` `:23`, `RunOnStartup=false` `:20`, `_lastFired` tuple `:55`, advisory-lock base `:48`. Its own doc-comment (`:29-43`) says it copies the rollup scheduler. Two copies = the extraction argument
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` — `DefinitionId` `:51`, `CronExpression = "0 5 * * * *"` **documentation-only** `:59`, "inert absent the scheduler" `:40-46`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `DebugDiagnosisWorkflow.cs` — the thin-binding templates
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — the wire (`topic`/`summary`/`findings[{title,summary,relevance,confidence,citations,rank}]`/`overallConfidence`), codes `:49-76`, validator `:85-151` (**`EMPTY_FINDINGS` at `:111-117`**), contract `:190`
- `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/monitor-health.md` + `assess-capacity.md` — **both currently instruct a triage-decision-shaped JSON**
- `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` — `Hour` `:46`, `TenantId` (null = platform row) `:53`, `WorkflowsStarted/Completed/Failed` `:56-62`, `AgentDispatches` `:69`, `TokensIn/Out` `:72-75`, `CostUsd` `:83`, `ActiveTenantsAtHourEnd` `:90`
- `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IPlatformAnalyticsService.cs` + `ITenantAnalyticsService.cs`; `Tamma.Data/Entities/AnalyticsUsageHourly.cs` / `AnalyticsUsageDaily.cs` / `ProviderHealth.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:200-206` — `QueryEventsAsync(tenantId, type, typeIsPrefix, correlationId, actor, from, to, cursor, limit, includeTotal)`; the half-open window semantics `:177-179`, tenant-isolation throw `:181-187`
- `apps/tamma-elsa/src/Tamma.Activities/ADL/DeployEvents.cs:30-84` — the deploy signal family
- `apps/tamma-elsa/src/Tamma.Activities/Decomposition/EmitDecompositionEventActivity.cs` — the event-activity shape
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the structure-test shape
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`Bindings` `:82`, universal pins `:626`/`:655`), `TaxonomyDriftBuildTests.cs` (`ScanLifecycleBindingDispatches` `:460`, `ExpectedContributingWorkflows` `:125`), `Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44` (`HaveCount(16)`)
- **NOT FOUND:** any tenant-aware scheduled-trigger seam; any `HEALTH_REVIEW.*` constant; any `IToolExecutor` that reads a metric. Everything else above exists and was read.

## Corrections to the story

- **C1 — AC2's "empty ⇒ valid empty report" is false in code.** `FindingsDocumentType.Validate`
  (`Findings.cs:111-117`) adds `EMPTY_FINDINGS` for a zero-length findings list, with the verbatim
  rationale *"an empty list is a violation, not a valid 'nothing found'"*. A clean sweep therefore cannot
  produce an empty document — it would loop through repair/revise and escalate as
  `validation-exhausted`. **D4** records the resolution: a healthy window produces exactly one finding
  ("no saturation or degradation detected", citing the metrics checked), which is both truthful and
  valid. AC2's second clause must be restated as *"a healthy window produces a single
  all-clear finding citing the signals checked — never an empty document."*
- **C2 — the `Findings` schema has no `severity`, no `recommendedAction`, no projected-breach flag.**
  `Finding` is `{title, summary, relevance, confidence, citations, rank?}` (`Findings.cs:12-22`). The
  story's "Produced document" paragraph — *"each finding cites a metric/trend as evidence, with severity
  + recommended action; ranked; projected-breach items flagged"* — is not expressible as typed fields.
  Only `citations` (evidence) and `rank` (ranking) map. **D5**: severity / recommended action /
  projected-breach live *inside* `summary` under a prompt-enforced convention, and `relevance` carries
  the severity ordering. Extending `Finding` is a 41-1b-class vocabulary change and is **out of scope**;
  it is filed, not done here.
- **C3 — "two produce cells as lenses aggregating into one report" is not expressible.**
  `DocumentLifecycleWorkflow` reads exactly one `producerRole` / `producerAction` / `documentType`
  (`:169-172`). One lifecycle = one produce cell. **D2** picks one cell and folds the other lens into the
  prompt, rather than shipping two `Findings` documents that then need an un-typed merge.
- **C4 — neither produce prompt emits `Findings` today.** `Prompts/devops/monitor-health.md` and
  `assess-capacity.md` both declare `variables: role, issueJson, repoContext` and instruct a
  **triage-decision-shaped** reply — `{type, severity, priority, ownerRole, estimatedEffort, labels,
  relatedIssues, reasoning}`. Neither declares a variable that can serve as the lifecycle's
  `feedbackVariableName` carrier. The chosen cell's template must be rewritten to the `Findings` wire
  (the 39-13/39-15 precedent), and its declared variables changed.
- **C5 — the story's `HourlyAnalyticsRollupScheduler` line cites are correct.** All six were re-verified:
  hardcoded target at `:198-199`; options section at `:17`; single `FireAtMinute` int at `:34`; advisory
  lock key `ComputeAdvisoryLockKey(year, dayOfYear, hour)` with **no tenant component** at `:241`
  (`0x524C5550` = "RLUP", body `:249-254`, call site `:179`); dispatch threads no `tenantId` at
  `:200-204`/`:211` (`new DispatchWorkflowOptions()`, zero input variables); `_lastFired` in-process at
  `:83`. *(The epic README's `:197-198` / `:201-202` are each one line early; the story's `:198-199` /
  `:202-203` are right.)* Two further facts the story does not state: the leader lock **fails open** —
  with no `DefaultConnection` configured, `PostgresAdvisoryLeaderLock` returns a `NoOpLease` and declares
  itself leader (`:294-301`); and the advisory lock is released at the end of dispatch, so a mid-hour
  restart **can** double-dispatch, mitigated only by the target workflow's own UPSERT idempotency
  (`:51-55`) — a property a document-producing lifecycle does not have.
- **C6 — the pattern has already been copy-pasted once.** `AuditChainCheckpointScheduler` (Story 37-2) is
  a second, near-identical `BackgroundService` with its own `FireAtMinute`, its own `_lastFired`
  `(Year, DayOfYear, Hour)` tuple and its own advisory-lock namespace, and its doc-comment says so
  outright. Whoever writes the seam is extracting a duplicated pattern, not inventing one — which
  materially lowers its risk and should be said when the seam is scoped.
- **C7 — `Elsa.Scheduling` is referenced and enabled, but has no cron trigger in use.**
  `Elsa.Scheduling` 3.5.3 is referenced by `Tamma.Activities.csproj:31` and `Tamma.ElsaServer.csproj:29`
  and `elsa.UseScheduling()` is called (`ElsaServer/Program.cs:100`) — but **only** for `Delay`/`DelayFor`
  SLA bookmarks inside running workflows (9 call sites). No `Cron` or `Timer` trigger activity exists
  anywhere. The seam is therefore not "wire up an already-present Elsa cron trigger"; it is either a
  fourth `BackgroundService` done right, or the first real use of Elsa's cron triggers. **D0** names the
  choice as the seam-writer's, not this story's.
- **C8 — the Epic 42 caveat is right about tools and misleading about reach.** There is indeed no
  `IToolExecutor` that reads a metric (exactly six are registered, `Tamma.Api/Program.cs:753-764`). But
  the analytics rollups, the usage tables, `ProviderHealth`, and the whole DCB stream are readable
  **in-process** through `IPlatformAnalyticsService` / `ITenantAnalyticsService` /
  `IEventRepository.QueryEventsAsync`. An Elsa **activity** can read them today with no Epic 42
  dependency (D3). What Epic 42 gates is (a) reading an *external* monitoring system's metrics and (b)
  *acting* on capacity — neither of which this story's ACs require.

## Design Decisions

- **D0 — Phase 0 (the seam) is named, not smuggled in.** Four workarounds were considered and rejected:
  (i) *reuse `HourlyAnalyticsRollupScheduler`* — one tenant's leader suppresses every other tenant's fire
  (C5), so a multi-tenant deployment would produce one report total; (ii) *a fourth copy of the
  `BackgroundService` pattern* — ships the same four defects and makes C6 a third copy; (iii) *a
  self-rescheduling workflow using `Delay`* — durable, but a dropped/faulted instance silently ends the
  schedule with nothing to detect it, and there is no per-tenant fan-out; (iv) *an external cron hitting
  an endpoint* — moves tenancy and idempotency outside the product with no audit trail. The seam's
  minimum contract, restated so whoever owns it can size it: a **tenant component in the advisory-lock
  key**, a **`tenantId` threaded into the dispatch**, a **persisted last-fired window** (a row, not a
  process field), and a **window/cron shape** rather than one `FireAtMinute` — plus a registry so a second
  scheduled workflow is a registration, not a new class. C6's duplicate is the extraction target.
- **D1 — one thin binding, `capacity-health-review`, on the `TaskCreationWorkflow`/`DebugDiagnosisWorkflow`
  skeleton.** `ReadInputs → ComputeReEntryPosition → FetchHealthSignals → DispatchLifecycle → ReadLifecycleExit
  → ExposeOutput`; exactly one `DispatchWorkflow` with literal id `document-lifecycle`; zero `llm-call`;
  zero `Finish`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`; a declared
  `feedbackVariableName`; one `WorkflowDocumentInterface` row (edge pin 16 → 17).
- **D2 — the produce cell is `(devops, assess-capacity)`; `monitor-health` stays the alert lens (per C3).**
  Both cells exist and are eligible (`RolePhaseMap.cs:146`, `:150`). Picking `assess-capacity` as the
  document producer and leaving `monitor-health` where it is keeps `monitor-health`'s existing
  alert-classification semantics intact (it is reachable today as an intake lens) and avoids two
  overlapping `Findings` documents per window with no typed merge. The health lens becomes a **section of
  the one report**, instructed in the rewritten template — the same "facets become sections of one
  document" move 41-10 makes for the three `design-*` cells. `(devops, monitor-health)` is left
  **unbound and unmodified** by this story.
- **D3 — signals are read by an ACTIVITY, not a tool (per C8).** `FetchHealthSignalsActivity` (in
  `Tamma.Activities/Health/`) resolves its dependencies via `context.GetService<T>()` (the
  `EventPersistenceMiddleware` / `ComputeReEntryPositionActivity` pattern) and produces one
  `signalsJson` payload for the window from three landed sources: (a) `PlatformAnalyticsHourly` rows for
  `[windowStart, windowEnd)` scoped to the tenant (`TenantId` non-null; the platform row is `null`),
  (b) `IEventRepository.QueryEventsAsync(tenantId, type: "DEPLOY.", typeIsPrefix: true, from, to, …)`
  — the 4-7 surface, prefix + half-open window, exactly as documented at `IEventRepository.cs:177-206` —
  and the same for `GATE.`/`TEST.`, (c) `ProviderHealth`. Fail-loud: an unresolvable service is a
  `TammaError`, never an empty payload that would silently produce an all-clear. This keeps the agent
  path reachable **today**, with no Epic 42 dependency; Epic 42's 42-9/42-7 only extend it to external
  monitoring and to acting.
- **D4 — the all-clear finding (per C1).** The rewritten template instructs: *if no signal breaches a
  threshold, return exactly one finding titled "No saturation or degradation detected", whose
  `citations` list the metric series actually examined and whose `relevance`/`confidence` reflect the
  coverage of the window.* This is truthful, satisfies `MISSING_EVIDENCE`/`EMPTY_FINDINGS`/rank rules,
  and gives the reviewer something to check. A test pins it.
- **D5 — severity / recommended action / projected breach are prompt conventions inside `summary`
  (per C2), with `relevance` as the severity ordering.** The template instructs each finding's `summary`
  to open `severity=<critical|high|medium|low>; action=<recommended action>;` and to append
  `projected-breach=<ISO date>` when a trend crosses a threshold inside the horizon; `relevance` is set
  from severity (critical 1.0 / high 0.8 / medium 0.5 / low 0.2) so `rank`-less list order and relevance
  agree. **Filed, not done:** a typed `severity`/`action` on `Finding` → 41-1b / the `Findings` type owner.
- **D6 — window identity is the idempotency key, and it is producer-scoped.** The binding takes
  `windowStart`/`windowEnd` inputs (RFC3339, half-open, matching `QueryEventsAsync`'s semantics) and keys
  its lifecycle on `issueId = "health#{repositoryOrTenant}#{windowStartIso}"` via
  `CreationBindingHelper.ScopeIssueId`-style composition. Because the 39-11 latest-accepted / re-entry
  read scopes by `(issueId, documentType)` with no producer filter (`CreationBindingHelper.cs:84-94`), a
  window-scoped id is what makes a **re-dispatch of the same window a no-op re-entry** (`Complete` →
  short-circuit, `DOCUMENT.REENTERED`, no second `DOCUMENT.ACCEPTED`). That is AC1's "idempotent per
  window" — and note it is *document-level* idempotency, independent of whatever the seam does. Two
  layers, and the story only gets one of them for free.
- **D7 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, as the story says.** Correct as written (the
  accept-gate suspend is inside the dispatched child). Requires a `ComputeReEntryPositionActivity` node —
  39-10 clause (c), `ResumableStandardStructuralTests.cs:240-261`.
- **D8 — "each lens fail-closed" (AC1) means the signal read, not the LLM.** `FetchHealthSignalsActivity`
  fails loud on an unreadable source; a partial read is reported as an explicit `unavailable` entry in
  `signalsJson` so the produced report can cite it, never as silence. The document's own quality is the
  review stage's job, per the story's closing note.
- **D9 — autonomy and escalation are policy passthrough.** `acceptanceRulesJson` rides as an input
  (39-12 D8). "A breach above a configured threshold always escalates" is an `AcceptanceRules` row with
  `AcceptorRequirement.Human`, supplied by the caller — never an if-else in the binding.
  `AcceptanceDefaults.For(Findings)` is the single-`architect` unanimous base row today
  (`AcceptanceDefaults.cs`, the `_ => Rules` arm), which is wrong for an ops report; the caller overrides.

## Implementation Steps

### Phase 0 — the scheduled-trigger seam (story 41-30; not this story's estimate)

0. **Blocked.** See the notice at the top and **D0**. Nothing here can be built by 41-23 without
   expanding its scope by the seam's full cost. If this story is scheduled before the seam exists, the
   only honest delivery is Phases 1–3 with AC1 explicitly unmet.

### Phase 1 — the signal read (seam-independent)

1. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Health/HealthSignalWindow.cs`** — a pure record set +
   a pure `HealthSignalProjector` that folds analytics rows + event rows into the `signalsJson` shape.
   Elsa-free, no I/O, unit-tested standalone (the `LifecycleResumeCalculator` posture).
2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Health/FetchHealthSignalsActivity.cs`** (D3/D8) —
   inputs `TenantId`, `Repository`, `WindowStart`, `WindowEnd`; output `SignalsJson`; resolves
   `IEventRepository` + the analytics read services via `context.GetService<T>()`; fail-loud
   `TammaError HEALTH.SIGNALS.SERVICE_UNREGISTERED` / `HEALTH.SIGNALS.READ_FAILED`.
3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Health/HealthReviewEvents.cs` +
   `EmitHealthReviewEventActivity.cs`** — copy `EmitDecompositionEventActivity` shape.
   Constants: `HEALTH_REVIEW.STARTED`, `HEALTH_REVIEW.SIGNALS_READ`, `HEALTH_REVIEW.REPORT`,
   `HEALTH_REVIEW.FAILED`. Tags `tenantId`, `repository`, `windowStart`, `windowEnd`, `documentId`,
   `correlationId`.

### Phase 2 — the prompt (seam-independent)

4. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/assess-capacity.md`** (C4/D2/D4/D5) to the
   `Findings` wire, front matter `variables: role, signalsJson, contextFindings, conventions` /
   `enableTools: false` / `maxTokens: 4096` / `version: 2`, body covering **both** lenses (saturation +
   degradation), the D4 all-clear rule, and the D5 `summary` convention. `contextFindings` is the
   declared `feedbackVariableName` carrier (the `TaskCreationWorkflow.cs:190` pattern).
   **`Prompts/devops/monitor-health.md` is NOT touched** (D2).
5. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`** — add
   `[("devops","assess-capacity")] = new("FindingsDocumentType.Validate", [One("\"summary\""),
   One("\"findings\""), One("\"title\""), One("\"relevance\""), One("\"confidence\""),
   One("\"citations\""), One("\"overallConfidence\"")])` — token-for-token the landed
   `(developer, triage-context-scan)` entry (`:202-207`).

### Phase 3 — the binding (seam-independent)

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/HealthReviewBindingHelper.cs`** —
   pure: `ComposeWindowIssueId(tenantOrRepo, windowStart)` (D6), `BuildFailureDetail(exit)`,
   `ProjectBreachItems(findingsDocumentJson)` (fail-closed `"[]"`, for the routing output).
7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CapacityHealthReviewWorkflow.cs`**
   (`capacity-health-review`, D1/D6/D7) — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`; graph
   `ReadInputs → ComputeReEntryPosition → FreshRun? → FetchHealthSignals → DispatchLifecycle →
   ReadLifecycleExit → ExposeOutput`; dispatch input
   `documentType="findings"`, `producerRole=devops`, `producerAction=assess-capacity`,
   `feedbackVariableName="contextFindings"`, `producerVariablesJson={signalsJson, contextFindings:"", conventions:""}`,
   `issueId`/`correlationId` = the D6 window id, `tenantId`, `acceptanceRulesJson`.
8. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (`BuildSeed`)** — add
   `new WorkflowDocumentInterface("capacity-health-review", empty, DocumentTypeKey.Findings, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`** —
   `HaveCount(16)` → `HaveCount(17)`, with the reason.
9. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125`** —
   add `CapacityHealthReviewWorkflow` to `ExpectedContributingWorkflows` with a one-line comment.

### Phase 4 — trigger wiring (BLOCKED on Phase 0)

10. Register `capacity-health-review` with the seam: a per-tenant window schedule, a persisted last-fired
    window row, and a dispatch threading `tenantId` + `windowStart`/`windowEnd`. **Cannot be written
    until Phase 0 exists.** Until then the workflow is dispatchable on demand (by definition id) and by
    a 41-22 incident's follow-up.

### Phase 5 — tests

11. Structure/unit/execution suites per the Test Plan; finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes` (clean).

## Data & Migrations

**This story: none.** It reads landed tables (`PlatformAnalyticsHourly`, `AnalyticsUsageHourly`/`Daily`,
`ProviderHealth`, `domain_events`) and writes documents through 39-11's existing
`document_instances` path. `dotnet ef migrations has-pending-model-changes` stays clean.

**Phase 0 (the seam) will need one**: a durable `scheduled_trigger_fires` (or equivalent) table keyed
`(tenantId, triggerKey, windowStart)` — the persisted last-fired window that replaces
`HourlyAnalyticsRollupScheduler._lastFired` (`:83`). That migration belongs to the seam's story, not this
one, and is called out here so it is not discovered late.

## Events

- **Emits (new):** `HEALTH_REVIEW.STARTED`, `HEALTH_REVIEW.SIGNALS_READ`, `HEALTH_REVIEW.REPORT`,
  `HEALTH_REVIEW.FAILED` — tags `tenantId`, `repository`, `windowStart`, `windowEnd`, `documentId`,
  `correlationId`. (Nothing named `HEALTH_REVIEW.*` exists today.)
- **Emitted by the machinery this story wires in:** `DOCUMENT.*` (`DocumentEvents.cs:28-53`),
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes (reads, does not emit):** `DEPLOY.*` (`DeployEvents.cs:30-84`), `GATE.*`/`TEST.*`
  (`TestingEvents.cs`), and the analytics rollup tables — all via D3's activity.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`HealthSignalProjectorTests`** (pure) — folds analytics rows + event rows into `signalsJson`; an
  unavailable source produces an explicit `unavailable` entry, never silence (D8); window boundaries are
  half-open (a row at `windowEnd` is excluded), matching `QueryEventsAsync`'s documented semantics.
- **`FetchHealthSignalsActivityTests`** (Moq'd `IEventRepository` + analytics services) — reads with the
  right `(tenantId, typePrefix, from, to)`; unresolvable service ⇒ typed `TammaError`, never an empty
  payload. **Covers AC1's "each lens fail-closed" (D8).**
- **`CapacityHealthReviewWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set
  verbatim: builds; `DefinitionId == "capacity-health-review"`; threads `TenantId`; no retry plumbing
  variables; **exactly one** `DispatchWorkflow`, literal id `document-lifecycle`; **zero** `llm-call`;
  `ScanLifecycleBindingDispatches()` contains `(devops, assess-capacity)`; `MaterializeDispatchInput`
  shows `documentType == "findings"` and `feedbackVariableName == "contextFindings"`; **zero** `Finish`;
  one `ComputeReEntryPositionActivity`; one `FetchHealthSignalsActivity`;
  `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*` node. **Covers AC3.**
- **`HealthReviewBindingHelperTests`** — `ComposeWindowIssueId` determinism + distinctness across windows
  and tenants (the D6 idempotency key); `ProjectBreachItems` fail-closed `"[]"`.
- **`FindingsDocumentType` fixtures (`Tamma.Core.Tests`)** — AC2: a report whose findings each cite a
  concrete metric series validates; a finding with no `citations` ⇒ `MISSING_EVIDENCE`; **the D4
  all-clear report (one finding, citations = the series checked) validates**; and the standing
  **negative** pin — an empty `findings` array ⇒ `EMPTY_FINDINGS`, so C1's correction cannot silently
  regress into "empty is fine". **Covers AC2, as corrected.**
- **Contract/drift guards (self-verifying)** — `ContractBindingTests` green with the new `Bindings` entry
  and both universal pins; `LifecycleBindingWalk_FindsPairs_NotANoOp` finds the new binding;
  `WorkflowInterfaceGraphTests` `HaveCount(17)`; **and a negative assertion that
  `(devops, monitor-health)` gains no `Bindings` entry** (D2 — it stays the alert lens).
- **`ResumableStandardStructuralTests`** — green with **no** allowlist entry for
  `CapacityHealthReviewWorkflow`. **Covers AC3.**
- **`CapacityHealthReviewExecutionTests`** (Testcontainers, on the 39-6/39-10 shared fixture) —
  (a) happy path: seeded analytics + `DEPLOY.*` rows → valid `Findings` → review → accept; the accepted
  `document_instances` row carries the D6 window issue id. (b) **Window idempotency (the half AC1 gets
  without the seam):** re-dispatch the same `(tenant, window)` → re-entry short-circuits to `Complete`,
  emits `DOCUMENT.REENTERED`, and the stream carries exactly **one** `DOCUMENT.ACCEPTED` and one
  `HEALTH_REVIEW.REPORT`. (c) Tenant isolation: two tenants, same window → two distinct documents,
  neither visible to the other's read. (d) All-clear: a quiet window produces the D4 single-finding
  report and accepts. (e) Escalate-on-breach: rules with `AcceptorRequirement.Human` → the accept gate
  suspends, no self-accept.
- **NOT TESTABLE without Phase 0:** that the workflow *fires* on a schedule, per tenant, once per window,
  durably across restart. No test in this suite claims it. **AC1's scheduling half is unmet.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — scheduled, tenant-scoped, idempotent per window; each lens fail-closed | **partially** — 2, 7 (D6/D8); scheduling half is **Phase 0 — story 41-30** | Execution (b)(c) prove *window* + *tenant* idempotency at the document layer; `FetchHealthSignalsActivityTests` proves fail-closed. **"Scheduled" is NOT satisfied and no test claims it** |
| 2 — findings cite concrete metric evidence; empty ⇒ valid empty report | 4 (D4/D5), with **C1's restatement** | `FindingsDocumentType` fixtures incl. the all-clear case and the standing `EMPTY_FINDINGS` negative. **As written the AC is unsatisfiable (C1)** |
| 3 — `[ResumeBehavior(LatestStateReEntry)]`; 39-10 gate green without allowlist | 7 (D7) | `CapacityHealthReviewWorkflowStructureTests`; `ResumableStandardStructuralTests` |

## Blocks / Blocked by

- **Blocked by — hard:**
  - **The tenant-aware scheduled-trigger seam.** Only story 41-30 builds it (`epic-41/README.md:297`, `:454-472`).
    AC1's scheduling half cannot be met. **This is the reason the plan is marked blocked.** Its minimum
    contract is in D0; its migration is flagged under Data & Migrations; its extraction target (the
    `HourlyAnalyticsRollupScheduler` / `AuditChainCheckpointScheduler` duplicate) is C6.
- **Blocked by — hard, in tree (satisfied):**
  - **Epic 39** — 39-2/39-4 (`Findings` type + registry), 39-6 (`document-lifecycle`), 39-7
    (`document-review`), 39-8 (accept gate), 39-10 (resume standard + gate), 39-11 (store + persist
    wiring). All landed.
  - **28-10** (`HourlyAnalyticsRollupWorkflow` + `PlatformAnalyticsHourly`) and **4-7**
    (`IEventRepository.QueryEventsAsync`) — both landed; D3 reads through them.
- **NOT blocked by:**
  - **41-1a** — `(devops, assess-capacity)` and `(devops, monitor-health)` both already exist and are
    eligible (`AgentAction.cs:103,107`; `RolePhaseMap.cs:146,150`), with prompt files on disk. *(This
    story appears in no row of the epic README's 41-1a/41-1b/41-1c gating table, which is correct.)*
  - **41-1b / 41-1c** — reuses `Findings`; produces no prose.
  - **Epic 42** — D3/C8: the signals this story's ACs need are readable in-process by an activity today.
    42-9 (authenticated HTTP) and 42-7 (cloud/VPS) extend it to *external* monitoring and to *acting* on
    capacity; neither is on an AC.
- **Blocked by — partial (AC-level, named):** **39-17/39-19/39-20** — the accept gate publishes and
  parks; the "assigned to the devops role's Task View" behaviour in Orchestrator/user interaction is
  unreachable (`InitiatorOnlyTaskAudienceResolver`, `Program.cs:445-447`). Not on an AC, so not a
  delivery blocker.
- **Blocks:** **41-22** — a projected-capacity breach or degraded-health finding is one of the incident
  workflow's reactive triggers ("consumes 41-23 escalations"). That edge lands in 41-22, not here.
- **Sibling consumers of the same seam:** **41-5**, **41-7**, **41-11**, **41-16**, **41-17**
  (PR-triage half), **41-20**. Whoever writes the seam should size it against all seven, not against
  this story alone.
- **Files, does not fix:** typed `severity`/`recommendedAction`/`projectedBreach` on `Finding` → the
  `Findings` type owner / 41-1b (C2/D5). A producer/kind filter on the 39-11 latest-accepted read → 39-11
  (already filed at `CreationBindingHelper.cs:84-94`).

## Risks & Mitigations

- **The seam never gets written and this story ships "scheduled" in name only.** Highest risk by far.
  Mitigation: the block notice, D0's rejected-workarounds list, and a Definition-of-Done row that refuses
  to mark AC1 satisfied. If the seam slips, ship Phases 1–3 with AC1 openly unmet rather than adding a
  fourth copy of the `BackgroundService` pattern.
- **`EMPTY_FINDINGS` turns a healthy system into an escalation loop (C1).** A sweep on a quiet window
  that returns `findings: []` fails validation, repairs, fails, and escalates — a false alarm generated
  by the platform itself. Mitigation: D4's all-clear rule in the template **and** the positive fixture;
  the standing `EMPTY_FINDINGS` negative test keeps the intent visible.
- **Severity/action smuggled into `summary` (D5) is unqueryable and unenforceable.** A dashboard cannot
  filter on it and no validator checks it. Mitigation: the convention is prompt-enforced and
  review-checked, and the typed-field extension is filed rather than silently forgotten. Accept the
  limitation explicitly rather than forking `Findings` inside this story.
- **The signal read is broad and could be slow or heavy.** `QueryEventsAsync` pages keyset-descending on
  `SequenceNumber` (not `CreatedAt`) and `Total` is opt-in (`IEventRepository.cs:189-198`). Mitigation:
  bounded `limit` per prefix, no `includeTotal`, and the projector folds to counters rather than carrying
  raw events into the prompt.
- **Window idempotency is document-level only.** D6 makes a re-dispatch a no-op, but nothing prevents a
  *missed* window — that is the seam's persisted-last-fired job. Mitigation: state it, do not paper over
  it; Execution (b) tests only what is actually guaranteed.
- **Story-vs-canon tensions:** C1 and C3 are genuine contradictions, resolved in favour of the code. C2
  and C4 are gaps between the story's prose and the shipped schema/templates.

## Est. Effort

**Producing half only (Phases 1–3 + 5). Phase 0 and Phase 4 are excluded and unestimable here.**

| Step(s) | Work | Days |
|---|---|---|
| 1 | `HealthSignalWindow` + pure projector | 0.5 |
| 2 | `FetchHealthSignalsActivity` (three sources, fail-loud) | 0.75 |
| 3 | `HealthReviewEvents` + emit activity | 0.25 |
| 4–5 | `assess-capacity.md` rewrite to the `Findings` wire + binding entry | 0.5 |
| 6–7 | `HealthReviewBindingHelper` + `CapacityHealthReviewWorkflow` | 0.75 |
| 8–9 | Registry seed + edge pin + drift contributor entry | 0.25 |
| 11 | Structure/unit tests + `Findings` fixtures | 0.75 |
| 11 | Testcontainers scenarios (a)–(e) | 0.75 |
| — | Full-suite green, review polish | 0.25 |
| **Total (Phases 1–3 + 5)** | | **4.75** (story estimate: 4–5 days) |
| **Phase 0 — the seam** | **Story 41-30's work. Not estimated here.** Extraction of a twice-duplicated pattern (C6) plus a persisted-window table, a tenant-keyed lock, a cron/window shape and a trigger registry. Size it against all seven consumers. | **—** |
| **Phase 4 — trigger wiring** | Blocked on Phase 0; ~0.5 d once the seam's registration API exists | **0.5\*** |

\* Not included in the total; unreachable today.
