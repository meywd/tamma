# Implementation Plan — Story 41-12: Dependency & Upgrade Planning Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `dependency-upgrade-planning` exists as a **thin binding over
`document-lifecycle`** (the 39-12/39-15 recipe), producing a typed `Plan` from the
`(architect, plan-migration-strategy)` producer cell. It assembles the consumed side (an optional
41-20 dependency `Findings` read through `FetchLatestAcceptedDocumentActivity`, plus the manifest /
advisory context handed in as inputs), dispatches `document-lifecycle` once with `documentType = "plan"`,
a declared `feedbackVariableName` carrier, and a producer-scoped issue id; and routes the typed exit into
a single `SetOutput` terminal region. Zero `Finish`, zero `llm-call`, zero parsing, no retry plumbing.

The story also **rewrites `Prompts/architect/plan-migration-strategy.md` to the canonical `Plan` wire**
(this is not optional — see Correction C1: the shipped template instructs a shape
`PlanDocumentType.Validate` cannot deserialize), adds one `ContractBindingTests.Bindings` entry with
authority `PlanDocumentType.Validate`, declares one `WorkflowDocumentInterface` row and bumps
`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` 16 → 17, declares
`[ResumeBehavior(ResumeMode.LatestStateReEntry)]` and passes `ResumableStandardStructuralTests` with no
allowlist entry, and emits the `DEP_UPGRADE.PLAN.*` family alongside the lifecycle's `DOCUMENT.*`.

## Pre-Reading

- `docs/stories/epic-41/story-41-12/41-12-dependency-and-upgrade-planning.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f) (the checkable "thin" definition) and the Wave-3 placement
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — **THE reference binding.** Copy its skeleton verbatim: `ReadInputs` → `ComputeReEntryPositionActivity` → `ReadPositionStage` → `FreshRun` `FlowDecision` → `FetchLatestAcceptedDocumentActivity` → `DispatchLifecycle` → `ReadLifecycleExit` → `ExposeOutput`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs` — the same recipe without the consumed-document fetch
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — **the reference structure-test shape** (the epic README names it); clauses (a)–(e) are its eight tests
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` (`ReadLifecycleResult`, `IsAccepted`, `LifecycleExit`) and `Helpers/CreationBindingHelper.cs` (`DeriveIssueId`, `ScopeIssueId`, `BuildFailureDetail`, `ProjectTasksArray`) — the shared fail-closed cores; **do not fork them**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs` — `PlanTask` wire (`id`/`description`/`files: string[]`/`dependsOn: string[]`/`testing`) + the six violation codes AC2 pins (`EMPTY_PLAN` `:50`, `TASK_MISSING_FILE_MAP` `:53`, `TASK_MISSING_TESTING` `:56`, `DANGLING_DEPENDS_ON` `:62`, `CYCLIC_DEPENDS_ON` `:68`, `NO_TOPOLOGICAL_ORDER` `:71`) — story cite `Plan.cs:50-71` verified accurate
- `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-migration-strategy.md` — the cell being rewritten
- `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-system-design.md` — the *bound* sibling `plan` producer; note it carries the SAME defect (C1)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` (`:82`), the `(architect, plan-system-design)` entry (`:160-164`) this story clones, the universal DocumentType-authority pin (`:626`), and the clause-(c) staleness guard (`:725-737`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — `ScanLifecycleBindingDispatches` (`:460`), `MaterializeDispatchInput` (`:507`), `ExpectedContributingWorkflows` (`:125`), `MinExpectedDispatchPairs = 21` (`:110`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` `BuildSeed` (`:134-174`) + `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` (`HaveCount(16)`) and its `reconciled` list (`:102-123`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:127-132` — `For(plan)` → the 7-role **majority panel** (`s_panelRules`)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` (`GetReviewActionForRole`), `:430-433` (`GetPanelActionForRole`) — the review-lens selector (see C2)
- `.dev/findings/39-15-remaining-producers-migration.md` — the distilled binding recipe, the two-plans-per-issue scoping decision (D2 there), and the filed 39-11 producer-filter gap
- **All story-referenced paths exist and were verified in tree.** Nothing this story consumes is plan-only.

## Corrections to the story

- **C1 — the `plan-migration-strategy` template does NOT instruct the `Plan` wire; rewriting it is
  in scope and the story does not say so.** The shipped cell emits
  `"files": [{"path": "...", "action": "create|modify"}]` and `"dependencies": []`. `PlanTask.Files` is
  `IReadOnlyList<string>` and the prerequisite property is `dependsOn`
  (`Plan.cs:16-17`). So an obedient model's reply throws `JsonException` inside
  `PlanDocumentType.Validate` → `MALFORMED_PAYLOAD` on **every** produce, and `DANGLING_DEPENDS_ON` /
  `CYCLIC_DEPENDS_ON` / `NO_TOPOLOGICAL_ORDER` can never fire because `dependsOn` is always empty. AC2 is
  unreachable without the rewrite. Note that `ContractBindingTests` would **not** catch this — it checks
  only that the literal tokens `"tasks"` and `"files"` appear, which they do.
- **C1b — the same defect exists TODAY in the landed `(architect, plan-system-design)` binding**
  (`plan-system-design.md` has the identical `files`/`dependencies` shape and IS in `Bindings` at
  `ContractBindingTests.cs:160-164`). This story does **not** fix it (out of scope, different owner) but
  **files it** to 39-14/39-16 with a `.dev/findings/` entry, because 41-12 must not silently inherit a
  known-broken template by copy-paste.
- **C2 — "with a `(security, audit-dependencies)` review lens" is not reachable and is not an AC.**
  The review stage's per-role action is derived, never supplied: the lifecycle's `DispatchReview`
  (`DocumentLifecycleWorkflow.cs:452-466`) hands `document-review` only
  `documentJson`/`documentType`/`issueId`/`correlationId`/`tenantId`/`acceptanceRulesJson` — **no
  `reviewerAction`** — and `DocumentReviewWorkflow` (`:113-155`) passes none to either the panel or the
  single-reviewer branch. `ReviewerSelectionHelper.Resolve` therefore falls to
  `RolePhaseMap.GetPanelActionForRole(role, "plan")` → `GetReviewActionForRole` → security =
  `plan-review-security`. Getting `audit-dependencies` as a review lens would need a new doc-type arm in
  `GetPanelActionForRole` (a 41-1a-class lockstep taxonomy edit) **plus** a per-binding reviewer-action
  input the lifecycle does not have. This plan keeps the landed `plan-review-security` lens and records
  the desire as a filed gap; the Scope sentence is downgraded to intent.
- **C3 — the story's "Both cells exist today … and are unbound — this story binds the first" is only
  half right.** `(architect, plan-migration-strategy)` and `(security, audit-dependencies)` both exist
  (`AgentAction.cs:43`, `:92`; `RolePhaseMap.cs:72`, `:133`) and neither is in `Bindings` or
  `IntentionallyUnbound` — correct. But `(security, audit-dependencies)` is *also* not reachable as a
  review lens (C2), so after this story it remains unbound, waiting on 41-20.
- **C4 — `[ResumeBehavior]` mode.** AC5 says `Both`. It must be `LatestStateReEntry`. The binding never
  suspends on a bookmark of its own — the accept-gate suspend happens inside the dispatched
  `document-lifecycle` child while the parent waits on `WaitForCompletion = true`. Declaring `Both`
  would fail `ResumableStandardStructuralTests` clause (b), which requires a node whose type is in the
  declaration's `SuspendActivities` **and** in `LifecycleBookmarks.CanonicalSuspendActivities`. Every
  landed producer binding declares `LatestStateReEntry` (39-12 D7; `TaskCreationWorkflow.cs:47`,
  `TestCaseCreationWorkflow.cs:37`). AC5's *intent* — "39-10 structural test green without an allowlist
  entry" — is preserved exactly.
- **C5 — two-plans-per-issue scoping is mandatory here too, and the story omits it.** The 39-11
  latest-accepted read scopes by `(issueId, documentType)` with **no producer filter** (recorded in
  `.dev/findings/39-15-remaining-producers-migration.md`). A third `plan` producer on the same issue
  would `ComputeReEntryPosition("plan", issueId)` onto the accepted *system* plan and short-circuit to
  `Complete` on every run. The binding must key on `CreationBindingHelper.ScopeIssueId(issueId,
  "dependency-upgrade-planning")`.
- **C6 — AC3's "fails loud if a referenced id is unreadable" needs a mechanism the read seam does not
  have.** `FetchLatestAcceptedDocumentActivity` is deliberately **fail-closed**: any read failure yields
  `Found = false` and it never throws (`:127-134`). "Fails loud" is realised as a typed *routing* outcome
  (D5), not an exception.

## Design Decisions

- **D1 — New workflow class + new `DefinitionId`, no incumbent rewired.** `DependencyUpgradePlanningWorkflow`
  (`DefinitionId = "dependency-upgrade-planning"`, `builder.Version = WorkflowVersions.ComputedVersion`).
  Nothing today dispatches a dependency-upgrade plan, so unlike 39-12/39-15 there is no public surface to
  keep byte-stable. Inputs: `repository` (the lineage anchor — this is a **repository**-scoped activity,
  not issue-scoped), `issueId?`, `findingsDocumentId?`, `manifestJson`, `advisoriesJson`, `tenantId`,
  `acceptanceRulesJson?`. Outputs: `status`, `outcome`, `documentId`, `planJson`, `consumedFindingsId`,
  `error`.
- **D2 — Lineage anchor: a repository-scoped, producer-scoped synthetic issue id.** The 39-11 store and
  `ComputeReEntryPositionActivity` key on `issueId`; there is no repository-only read. So the binding
  derives `anchorId = CreationBindingHelper.ScopeIssueId(issueId ?? $"{repository}#deps",
  "dependency-upgrade-planning")` and threads it as both `issueId` and `correlationId` into the
  lifecycle. AC4's "retrievable by `repository`" is then satisfied because the anchor **contains** the
  repository and the `DOCUMENT.*` events carry `repository` as a tag. *Trade-off recorded (same as 39-15
  D2): the accepted plan's events carry the scoped anchor, not a bare issue id. The clean fix is the
  filed 39-11 producer-filter gap.*
- **D3 — Producer variables use the DECLARED carriers only (the render-drop lesson).** The rewritten
  cell (D6) declares `role, workItemJson, contextFindings, conventions`. The consumed 41-20 `Findings`
  body + the manifest + the advisories are folded into `contextFindings`; the upgrade request itself into
  `workItemJson`. `feedbackVariableName = "contextFindings"` so repair/revise notes land in a declared
  variable — identical to `TaskCreationWorkflow.cs:190`. **No new undeclared key is ever added to
  `producerVariablesJson`.**
- **D4 — Consumed 41-20 `Findings` is read through the existing seam, not a new one.**
  `FetchLatestAcceptedDocumentActivity` with `DocumentTypeKey = "findings"` on the *repository* anchor,
  gated behind the `FreshRun` `FlowDecision` (position == `produce`), exactly as `TaskCreationWorkflow`
  reads the system plan (`:155-166`). `Found` + `DocumentId` are surfaced as the `consumedFindingsId`
  output (AC3's "records the `documentId` … or `null`").
- **D5 — AC3's "fail loud" is a typed exit, not a throw, and only when an id was *explicitly asked for*.**
  Two cases, kept distinct: (i) **no `findingsDocumentId` input and nothing in the store** → legal
  schedule-triggered run, `consumedFindingsId` output is `""`, the plan proceeds against manifest +
  advisories only; (ii) **an explicit `findingsDocumentId` was supplied and the read returns
  `Found = false`** → the `ConsumedInputResolved` `FlowDecision` routes straight to `ExposeOutput` with
  `status = "escalated"`, `outcome = "consumed-input-unreadable"`, `error` naming the id, and a
  `DEP_UPGRADE.PLAN.FAILED` emit. No `Finish`, no dispatch, no silent plan-against-nothing. This keeps
  clause (a) (exactly one `DispatchWorkflow`) and clause (c) (zero `Finish`) intact and follows 39-12 D2's
  "typed-value `FlowDecision`s are allowed; a branch that impersonates the quality decision is not".
- **D6 — The prompt cell is REWRITTEN to `PlanDocumentType.RenderContract()`'s shape (C1), by hand.**
  No prompt file in the tree carries a 39-16 generated-region marker (verified — same finding as
  41-29's plan step 1.4), so the rewrite is a hand edit that reproduces `Plan.cs`'s `Contract` block:
  `{"tasks":[{"id","description","files":["path"],"dependsOn":["T1"],"testing"}]}`. Front matter keeps
  `variables: role, workItemJson, contextFindings, conventions`, `enableTools: true`, `maxTokens: 8192`,
  and **bumps `version: 1 → 2`** (the `write-tests.md` v2 precedent). The `ContractBindingTests` token
  groups are cloned from the `plan-system-design` entry unchanged (`AnyOf("\"tasks\"","\"steps\"")` +
  `AnyOf("\"fileMap\"","\"files\"","\"filesToModify\"")`) — both are satisfied by the rewritten body.
- **D7 — Acceptance policy: inherit the shipped `plan` default, override per-run via
  `acceptanceRulesJson`.** `AcceptanceDefaults.For(Plan)` is the 7-role majority panel
  (`AcceptanceDefaults.cs:129`); that is a *reasonable* default for an upgrade plan (architect + security
  are both on the roster) and this story does **not** edit the shared per-type default — doing so would
  silently change `plan-generation` and `task-creation`. The story's "a major-version upgrade of a
  load-bearing dependency can be an always-escalate class" is realised as an
  `EscalationClass(DocumentType, "plan")` supplied through the binding's `acceptanceRulesJson`
  passthrough — **policy, not code** (39-5 posture). One integration test proves the passthrough reaches
  the accept gate.
- **D8 — Pure helper: `DependencyPlanBindingHelper` in `Workflows/Helpers/`, Elsa-free, total,
  fail-closed.** Only two functions are genuinely new (`BuildUpgradeContext(findingsJson, manifestJson,
  advisoriesJson) → string` and `BuildConsumedInputDetail(...)`); `ReadLifecycleResult` / `IsAccepted`
  come from `LifecycleBindingHelper`, `BuildFailureDetail` / `ProjectTasksArray` / `ScopeIssueId` /
  `DeriveIssueId` from `CreationBindingHelper`. Nothing is forked.
- **D9 — New event family `DEP_UPGRADE.*` gets its own emitter activity, following the house pattern.**
  `Tamma.Activities/Dependencies/DependencyUpgradeEvents.cs` (`STARTED` / `DRAFTED` / `ACCEPTED` /
  `FAILED`) + `EmitDependencyUpgradeEventActivity` — the shape of
  `Tamma.Activities/Decomposition/EmitDecompositionEventActivity.cs`. Emissions are gated on the re-entry
  position exactly as 39-12 D3 gates `DECOMPOSITION.*` (a re-entry is not a new plan), so re-entry cannot
  double-emit.
- **D10 — Drift-gate bookkeeping is a single conscious edit set, enumerated (rule 1 clause (f)).** Adding
  `"DependencyUpgradePlanningWorkflow"` to `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`
  (`:125`); one `Bindings` entry; one `BuildSeed` row; `WorkflowInterfaceGraphTests.cs:45`
  `HaveCount(16) → HaveCount(17)` with the reason in the comment; the workflow added to
  `WorkflowInterfaceGraphTests`'s `reconciled` list (`:102-123`) so its row is asserted
  **non-provisional**. `MinExpectedDispatchPairs` (`:110`) is a floor and moves **up** only, so a new
  pair needs no edit there. No `AgentAction`/`RolePhaseMap`/`DocumentTypeKey` count pin moves — this
  story mints **no** new cell and **no** new document type.

## Implementation Steps

1. **Precondition check (no code).** Confirm in tree and compiling: `document-lifecycle`,
   `ComputeReEntryPositionActivity`, `FetchLatestAcceptedDocumentActivity`, `LifecycleBindingHelper`,
   `CreationBindingHelper`, `PlanDocumentType`, `ResumableStandardStructuralTests`. All verified present
   at plan time. Also confirm 41-20 has **not** landed — if it has, its `Findings` document type key and
   anchor convention must be matched exactly rather than assumed.

2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-migration-strategy.md`** (D6, C1) to
   the canonical `Plan` wire; bump `version` to 2. Keep the migration-specific instruction prose (ordered
   phases, rollback points, compatibility windows, no big-bang) — only the JSON block and the
   `dependencies`→`dependsOn` / `files`-as-strings shape change.

3. **CREATE `.dev/findings/41-12-plan-cell-wire-mismatch.md`** (C1b) recording that
   `plan-system-design.md` carries the same defect, that `ContractBindingTests`'s token check cannot see
   it, and that the fix belongs to 39-14/39-16. One page; no code.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Dependencies/DependencyUpgradeEvents.cs` +
   `EmitDependencyUpgradeEventActivity.cs`** (D9), cloned from the `Decomposition/` pair. Constants:
   `DEP_UPGRADE.PLAN.STARTED`, `.DRAFTED`, `.ACCEPTED`, `.FAILED`; tags `repository`, `issueId`,
   `tenantId`, `correlationId`; data `consumedFindingsId`, `taskCount`, `detail`.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DependencyPlanBindingHelper.cs`**
   (D8) — pure, Elsa-free, no throws out of routing lambdas:

   ```csharp
   public static class DependencyPlanBindingHelper
   {
       // Folds consumed Findings + manifest + advisories into ONE declared-carrier string.
       public static string BuildUpgradeContext(string? findingsJson, string? manifestJson, string? advisoriesJson);
       // AC3 detail: names the requested id and why it was unreadable.
       public static string BuildConsumedInputDetail(string requestedId);
       // Counts tasks in an accepted Plan body (0 on unreadable) — for the .ACCEPTED emit.
       public static int CountUpgradeTasks(string? planDocumentJson);
   }
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DependencyUpgradePlanningWorkflow.cs`** (D1–D5),
   copying `TaskCreationWorkflow`'s skeleton. Graph:
   `ReadInputs` → `ComputeReEntryPosition` (`DocumentType = "plan"`, `IssueId = anchorId`) →
   `ReadPositionStage` → `FreshRun` `FlowDecision`
   — True → `EmitStarted` → `FetchConsumedFindings` → `ConsumedInputResolved` `FlowDecision`
     — False → `EmitFailed` → `ExposeOutput` (D5 case ii)
     — True → join
   — False → join
   → `DispatchLifecycle` (the single `DispatchWorkflow`, `WorkflowDefinitionId = "document-lifecycle"`,
   `WaitForCompletion = true`) → `ReadLifecycleExit` → `LifecycleAccepted` `FlowDecision`
   → `EmitAccepted` / `EmitFailed` → `ExposeOutput` (the single terminal `Sequence` of `SetOutput`s).
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` on the class (C4). Dispatch input mirrors
   `TaskCreationWorkflow.cs:173-196` with `documentType = "plan"`,
   `producerRole = AgentRole.Architect.ToWire()`,
   `producerAction = AgentAction.PlanMigrationStrategy.ToWire()`,
   `feedbackVariableName = "contextFindings"`, `issueId`/`correlationId` = the anchor,
   `acceptanceRulesJson` passthrough.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — add
   `new WorkflowDocumentInterface("dependency-upgrade-planning", new[] { DocumentTypeKey.Findings },
   DocumentTypeKey.Plan, false)` to `BuildSeed` with a Story-41-12 comment.

8. **MODIFY the drift/pin gates in ONE commit** (D10, rule 1 clause (f)):
   `tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` `HaveCount(16) → HaveCount(17)`
   + comment; its `reconciled` array `:102-123` gains `"dependency-upgrade-planning"`;
   `tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125`
   `ExpectedContributingWorkflows` gains `"DependencyUpgradePlanningWorkflow"` with a note that the pair
   rides the lifecycle-binding walk; `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`
   `Bindings` (`:82`) gains the `(architect, plan-migration-strategy)` entry with authority
   `"PlanDocumentType.Validate"` and the `plan-system-design` token groups.

9. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DependencyUpgradePlanningWorkflowStructureTests.cs`
   + `DependencyPlanBindingHelperTests.cs`** — see Test Plan.

10. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DependencyUpgradeLifecycleExecutionTests.cs`**
    on the shared 39-6/39-10 Testcontainers fixture. Scenarios in Test Plan. Finish with full
    `dotnet test` + `dotnet ef migrations has-pending-model-changes` (must stay clean).

## Data & Migrations

None. Documents persist to 39-11's `document_instances` (its migration); `DEP_UPGRADE.*` and `DOCUMENT.*`
ride the existing drain → `EventRepository` → `domain_events` path.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new family, D9):** `DEP_UPGRADE.PLAN.STARTED` (fresh runs only), `.DRAFTED` (on the lifecycle's
  first accepted draft — emitted from the exit read, not from inside the lifecycle), `.ACCEPTED`
  (data `taskCount`, `consumedFindingsId`), `.FAILED` (on `rejected`/`escalated`, `detail` naming the
  typed outcome wire; also the D5 case-ii consumed-input escalation).
- **Emitted by the machinery this story wires in (not by this story's code):** the whole `DOCUMENT.*`
  family, `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes:** none at runtime. The consumed 41-20 `Findings` arrives through the 39-11 store read, not
  the event stream.

## Test Plan

All NUnit + FluentAssertions (Moq fakes; Testcontainers for step 10).

- **`DependencyUpgradePlanningWorkflowStructureTests`** — the eight clauses, cloned from
  `TaskCreationWorkflowStructureTests`: builds without error; `DefinitionId == "dependency-upgrade-planning"`;
  threads `TenantId`; **no** `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables (clause d);
  **exactly one** `DispatchWorkflow`, literal def id `document-lifecycle`, **zero** targeting `llm-call`
  (clauses a+b); `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(DependencyUpgradePlanningWorkflow, DispatchLifecycle, architect, plan-migration-strategy)` and
  `MaterializeDispatchInput` shows `documentType == "plan"` and
  `feedbackVariableName == "contextFindings"` (clause e); **zero `Finish`** and every graph leaf inside
  the single `ExposeOutput` region (clause c); `ComputeReEntryPositionActivity` and
  `FetchLatestAcceptedDocumentActivity` each present exactly once; class carries
  `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*` activity. **Covers AC1 (structure), AC5.**
- **`DependencyPlanBindingHelperTests`** — `BuildUpgradeContext` across all-present / findings-absent /
  unreadable-JSON inputs (never throws, never fabricates); `CountUpgradeTasks` on a valid `Plan` body and
  on garbage → 0; `BuildConsumedInputDetail` names the requested id. **Covers AC3 (helper half).**
- **`PlanDocumentTypeTests` additions (`Tamma.Core.Tests`)** — one fixture per AC2 rule against the
  **rewritten** template's shape: no tasks ⇒ `EMPTY_PLAN`; task with `files: []` ⇒ `TASK_MISSING_FILE_MAP`;
  task with `testing: ""` ⇒ `TASK_MISSING_TESTING`; `dependsOn` naming an absent id ⇒ `DANGLING_DEPENDS_ON`;
  mutually-blocking pair ⇒ `CYCLIC_DEPENDS_ON`; unorderable set ⇒ `NO_TOPOLOGICAL_ORDER`. Plus a
  **template-conformance test**: the JSON example embedded in the rewritten
  `plan-migration-strategy.md` deserializes to `Plan` and validates clean (this is the test that would
  have caught C1). **Covers AC2.**
- **Drift-gate modifications (step 8, self-verifying)** — `ContractBindingTests` green with the new entry
  (non-stale, because the lifecycle-binding walk discovers the pair); the universal DocumentType-authority
  pin (`:626`) green; `TaxonomyDriftBuildTests` contributor subset holds;
  `WorkflowInterfaceGraphTests` `HaveCount(17)` + non-provisional assertion green.
  **Covers AC1 (gate half), AC5 (pin half).**
- **`ResumableStandardStructuralTests`** — passes with **no** `LegacyResumeAllowlist` entry for the new
  workflow. **Covers AC5.**
- **`DependencyUpgradeLifecycleExecutionTests` (Testcontainers)** —
  (a) **happy path:** scripted valid upgrade `Plan` draft → panel review approve → orchestrator-side
  `Accept` resume → outputs `status=completed`, `planJson` with the expected task count,
  `consumedFindingsId` = the seeded `Findings` id; store asserts the accepted `Plan` is readable by the
  repository-bearing anchor through the 39-11 read (**AC4** producer half).
  (b) **consumed-plan hand-off:** a `test-case-creation`-shaped coding-step dispatch reads the accepted
  `Plan` back and sees the same task ids (**AC4** consumer half; a stubbed coding step, since
  `.github/workflows/tamma-agent.yml` does not exist — see Blocked by).
  (c) **explicit-id-unreadable:** `findingsDocumentId` supplied, store empty → `status=escalated`,
  `outcome=consumed-input-unreadable`, `DEP_UPGRADE.PLAN.FAILED` emitted, **zero `DOCUMENT.PRODUCED.*`**
  (**AC3**).
  (d) **schedule-triggered, no findings:** no id, empty store → the run proceeds, `consumedFindingsId`
  output `""`, plan produced and accepted (**AC3** null branch).
  (e) **validation exhaustion:** always-invalid stub → typed `ValidationExhausted` escalation with
  lineage; `DEP_UPGRADE.PLAN.FAILED` detail names the outcome wire; no error terminal reached.
  (f) **always-escalate passthrough (D7):** `acceptanceRulesJson` carrying
  `EscalationClass(document-type, plan)` → the run escalates before any acceptor decides.
  (g) **crash re-entry:** kill mid-review (39-10 D8 shape), fresh dispatch for the same anchor → resumes
  at review of the same revision, **exactly one** `DEP_UPGRADE.PLAN.STARTED` and one `DOCUMENT.ACCEPTED`
  on the whole stream.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding on `(architect, plan-migration-strategy)`, one `Bindings` entry, authority `PlanDocumentType.Validate` | 2, 6, 8 | StructureTests clauses (a)–(e); `ContractBindingTests` green incl. the universal authority pin |
| 2 — one fixture per `Plan` rule | 2, 9 | `PlanDocumentTypeTests` additions + the template-conformance test (C1) |
| 3 — records the consumed `documentId` or `null`; fails loud on an unreadable referenced id | 5, 6 (D5) | `DependencyPlanBindingHelperTests`; ExecutionTests (c) + (d) |
| 4 — accepted `Plan` retrievable by `repository` via 39-11; read by a coding-step dispatch | 6 (D2), 10 | ExecutionTests (a) + (b) |
| 5 — resume declaration + 39-10 gate green without allowlist; one new `WorkflowDocumentInterface` row; edge pin bumped | 6, 7, 8 | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` `HaveCount(17)` |

## Risks & Mitigations

- **The rewritten template drifts back (C1).** A future prompt edit can silently reintroduce the
  object-shaped `files`. Mitigation: the template-conformance test (Test Plan) parses the example out of
  the shipped `.md` and runs `PlanDocumentType.Validate` on it — a shape regression fails the build,
  which the token-only `ContractBindingTests` cannot do.
- **Third `plan` producer on one issue collides in the 39-11 read (C5).** Mitigation: producer-scoped
  anchor (D2), the landed 39-15 workaround; an ExecutionTests scenario seeds an accepted *system* plan on
  the bare issue id and asserts this binding still produces (does not short-circuit to `Complete`).
- **The 7-role majority panel is heavy for a routine patch-bump plan (D7).** Mitigation: it is the shipped
  `plan` default and is not changed here; per-run tuning rides `acceptanceRulesJson`. Filed as a policy
  question for 41-20's scheduled caller, not a code change.
- **`(security, audit-dependencies)` reads as delivered but is not (C2).** Mitigation: the correction is
  stated in the plan and the review-lens gap is filed against 41-1a's selector work; no AC claims it.
- **Copy-paste inheritance of the sibling's defect (C1b).** Mitigation: step 3's findings entry plus the
  conformance test make the divergence deliberate and visible.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check | 0.1 |
| 2–3 | Template rewrite to the `Plan` wire + findings entry (C1/C1b) | 0.5 |
| 4 | `DEP_UPGRADE.*` events + emitter activity | 0.4 |
| 5 | `DependencyPlanBindingHelper` | 0.3 |
| 6 | The binding workflow | 0.9 |
| 7–8 | Registry row + the four drift/pin edits | 0.4 |
| 9 | Structure tests + helper tests + `PlanDocumentType` fixtures + conformance test | 0.8 |
| 10 | Testcontainers scenarios (a)–(g) + full-suite green | 0.6 |
| **Total** | | **4.0** (story estimate: 3–4 days) |

## Blocks / Blocked by

- **Blocked by — hard:** none that are unlanded. Epic 39 (39-2/39-4/39-6/39-7/39-8/39-10/39-11) is in
  tree and verified; `(architect, plan-migration-strategy)` exists today
  (`AgentAction.cs:43`, `RolePhaseMap.cs:72`). **This story needs no part of 41-1a, 41-1b or 41-1c** — it
  mints no role, no cell and no document type. It is one of the few Wave-3 stories that is genuinely
  Wave-0-independent, and the story file does not say so.
- **Blocked by — for AC4's downstream hand-off only:** **Epic 40**. `.github/workflows/tamma-agent.yml`
  does not exist in this repo (verified: `.github/workflows/` contains `tamma-worker.yml`, not
  `tamma-agent.yml`), so the coding step's dispatch fails loud with `WorkflowNotFound`. ExecutionTests (b)
  therefore stubs the coding step; the real hand-off lands with Epic 40.
- **Blocked by — for the *input* side only:** **41-20** (scheduled dependency audit) produces the
  `Findings` this workflow consumes. Not a hard block — D5 case (i) is the no-findings path, tested.
- **Related, NOT blocking:** the tenant-aware scheduled-trigger seam. The story's "or scheduled" trigger
  is out of scope here; this workflow is dispatched by 41-20 or by hand. Do **not** import the Wave-0
  scheduler dependency into this story.
- **Blocks:** nothing directly. Feeds Epic 40's coding step (an accepted upgrade `Plan` is a valid work
  input) and is a consumer of 41-20.
- **Shared-edit register (coordinate before merging):** `ContractBindingTests.Bindings`,
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`, `DocumentTypeRegistry.BuildSeed`, and
  `WorkflowInterfaceGraphTests.cs:45` are each touched by **every** Epic 41 producer story
  (41-13, 41-14, 41-15, 41-16, 41-2, 41-3, …). The edge pin is a single integer — two producer stories
  merging in the same window will conflict. Sequence the pin bump last in each branch and rebase.
