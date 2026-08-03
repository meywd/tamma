# Implementation Plan — Story 40-8: Triage Outcome Dead Ends — Build `create-issues`

Written 2026-08-03 against the working tree. Every file:line below was re-verified on that date.
Paths are under `apps/tamma-elsa/` unless they start with `docs/` or `.dev/`.

## Scope & Deliverable

When this story is done, a reviewer choosing `defer` or `split` gets real platform issues and a
finished cycle instead of a permanent silent hang. Concretely: a new `CreateIssuesWorkflow`
(`DefinitionId = "create-issues"`, the id the two dead dispatch sites already use), backed by a
new `CreateIssuesActivity` that calls the live mediated route `POST /api/engine/create-issue`
once per item with a platform-side dedupe so a crash/re-run never double-creates; a structural
test that pins EVERY `DispatchWorkflow` target in `Tamma.ElsaServer/Workflows/` against declared
`DefinitionId`s (the class of this bug, closed); and the second live instance of the same bug
(`MentorshipController.cs:89`) fixed in passing.

## Pre-Reading

| Reference | Why it matters |
|---|---|
| `docs/stories/epic-40/story-40-8/40-8-triage-outcome-dead-ends-and-the-create-issues-workflow.md` | The story; ACs are source of truth. |
| `.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md` | The defect record this story fixes. |
| `src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:261-274` | `ReviewOutcome` FlowSwitch — `Defer`/`Split` cases. Verified. |
| `SingleIssueCycleWorkflow.cs:279-291` / `:296-308` | `CreateDeferredIssues` / `CreateSplitIssues` — dispatch `new("create-issues")` at `:283` and `:300`, `WaitForCompletion = true`, inputs `repository` + `issuesJson` (defaulting `"[]"` at `:287`/`:304`). All verified. |
| `SingleIssueCycleWorkflow.cs:1095-1105` | Defer/Split connections: dispatch → `reportDeferred`/`reportSplit` → `finish`. No failure edge and no `Result` binding on either dispatch — the child's outputs are ignored, so the child must ALWAYS complete. |
| `src/Tamma.Api/Endpoints/EngineEndpoints.cs:777-795` | `CreateIssue` — validates repo/title, calls `IGitHubEngineCallbackService.CreateIssueAsync`, returns `201` with `{number, htmlUrl, title}`. Route: `Program.cs:3127` (`WorkflowsManage`). |
| `EngineEndpoints.cs:689-707` | `GetIssues` — `GET /api/engine/issues?repo=&state=&labels=&per_page=&page=` (route `Program.cs:3122`). The dedupe read. |
| `src/Tamma.Api/Services/Engine/OctokitGitHubEngineCallbackService.cs:265-282` | The create implementation. **Emits no events.** Load-bearing for the AC5 contradiction below. |
| `src/Tamma.Api/Services/Git/GitEventTypes.cs:41-42` | The mediation plane's issue family is `GIT.ISSUE_UPDATED.*` only — **no issue-created family exists anywhere**. |
| `src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:281-323` | `ITriageApplyClient` + `HttpTriageApplyClient.CreateIssueAsync` (`:314-319`) — the existing activity-side HTTP client for THIS EXACT route, and the ctor-or-`context.GetService` client-resolution idiom (`:125-149`). The template for the new client seam. |
| `src/Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs` | The side-effect-leaf workflow template (ReadInputs → activity → success/failure outputs → Finish; `SetOutput` result surface). Copy this shape. |
| `src/Tamma.Activities/ADL/IssueStatusEvents.cs:28` | Activity-side event-constant precedent (`ISSUE_STATUS.UPDATED.SUCCESS/FAILED`). |
| `src/Tamma.Activities/Core/EventPersistenceMiddleware.cs:311-316` | `ResolveTenantId` reads the workflow variable literally named `TenantId` (or `AccountId`) — this is HOW drained events get tenant-tagged. The new workflow must declare that variable; the cycle must pass it. |
| `src/Tamma.ElsaServer/Program.cs:141` | `elsa.AddWorkflowsFrom<LlmCallWorkflow>()` — assembly scan; a new `WorkflowBase` in `Tamma.ElsaServer` registers with **no Program.cs edit**. |
| `tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:43-96` | `LegacyResumeAllowlist` (30 entries today) + discovery (`:97-99`: every concrete `WorkflowBase` in the ElsaServer assembly). Clause (a) declare-XOR-allowlist `:107-131`. |
| `ResumableStandardStructuralTests.cs:240-261` | Clause (c): a `LatestStateReEntry`/`Both` declaration REQUIRES the document-coupled `ComputeReEntryPositionActivity` in the built graph — exact type identity. Why the new workflow cannot honestly declare today (see Blocked #2). |
| `ResumableStandardStructuralTests.cs:311-328` | `BuiltGraphNodeTypes` — the deep stack graph-walk the new structural test reuses (the shallow `WorkflowStructureTests.GetAllActivities` at `:167-180` only expands one `Sequence` level). |
| `tests/Tamma.Activities.Tests/Workflows/WorkflowStructureTests.cs:186-199, 201+` | `GetDispatchedWorkflowIds` + `ExtractLiteralValue` — the existing reflection idiom for pulling the literal string out of `DispatchWorkflow.WorkflowDefinitionId`. |
| `tests/Tamma.Activities.Tests/Workflows/WorkflowTestHelper.cs:22-70` | Mock-builder harness; `builder.DefinitionId` is captured and readable — how the declared-id set is collected. |
| `tests/Tamma.Activities.Tests/Workflows/TriageItemCycleApplyFaultExecutionTests.cs:238-268` | The WORKING real-`IWorkflowRunner` execution harness (AddElsa + activities + injected client seam + capturing `TammaApiClient`). The execution-test template. |
| `tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleExecutionTests.cs:55-62` | The full-runtime dispatch harness is `[Explicit]` and diagnosed broken (2026-07-29). Why AC2 cannot be pinned as a literal full-cycle execution test (Blocked #3). |
| `src/Tamma.Api/Controllers/MentorshipController.cs:89` | `StartWorkflowAsync("tamma-autonomous-mentorship", …)` — the second live instance. **The story cites `:79`; the literal is at `:89` today.** |
| `src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs:47` | The real id: `"mentorship"`. |
| `src/Tamma.Api/Services/ElsaWorkflowService.cs:132-168` | `StartWorkflowAsync` POSTs `/elsa/api/workflow-definitions/{name}/execute` and throws on non-success — so the mentorship bug fails loudly at runtime (unlike the silent dispatch suspend), but nothing pins it. |
| `tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:231` (19-entry baseline), `:595-618` (classifier), `:664` (surface pin = 37) | Why this story must NOT add a `TammaApiClient` method — see D2. |
| `src/Tamma.Activities/LlmCall/TammaApiClient.cs` | Verified: **no `CreateIssueAsync` exists** (git methods `:287-338`; no issue-create). |
| `src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:465, :657` | The only two NON-literal dispatch ids in `Workflows/` (`new(ctx => reviewDefId.Get(ctx))` / `deliveryDefId`). The structural test's dynamic-site allowlist, seeded at exactly these two. |
| `docs/stories/epic-31/31-13-full-pr-operations.md` (Scope 4, AC2-3) | 31-13 catalogs `git.issue.create` (zone 35) at the ROUTE and binds/enforces per 43-9's opt-in. Governance lands there, covering every caller of the route — including this workflow — regardless of HTTP client. |
| `docs/stories/epic-43/story-43-11/…` — Missing-actions row `:1422`, Amendment 4, zone table | The ruling model: `git.issue.create` is an LLM-path action at 35; the dial governs the LLM only; the plan-review acceptance is the step whose approver the dial picks — the defer/split creates are the accepted outcome's tail. |
| `docs/stories/epic-43/story-43-12/…:36,:55` | 43-12 explicitly does NOT mint issue keys (31-13 owns them; double-mint fails the catalog duplicate-key guard). No collision. |
| `docs/stories/epic-41/README.md:592-597` + `docs/stories/epic-39/story-39-24/39-24-acceptance-step-coverage.md:494-500` | The two audits that found the bug. Both verified at those lines. |
| `docs/stories/epic-39/resumable-workflow-standard.md` + `docs/stories/epic-40/story-40-5/…` | The resume standard and the allowlist-coordination story (40-5 also edits `ResumableStandardStructuralTests.cs`). |

## Design Decisions

- **D1 — Build the workflow; the id matches the dispatch sites; the cycle needs no wiring edit.**
  `CreateIssuesWorkflow` declares `DefinitionId = "create-issues"` so `SingleIssueCycleWorkflow.cs:283/:300`
  resolve as-is. Registration is automatic (`Tamma.ElsaServer/Program.cs:141` assembly scan).
  The reroute alternative was already rejected in the story (drops the reviewer's decision or
  converts two automated outcomes into permanent manual work); nothing found in the tree reopens it.

- **D2 — The issue-create call rides a NEW activity-side client seam, NOT a new `TammaApiClient`
  method. This deviates from the story's "via `TammaApiClient`" wording, deliberately.**
  A new public mutating method on `TammaApiClient` must either be mapped to an `ExternalEffect`
  member (`MediationClientEffectSweepTests.Classify` fails any unmapped method: "a new mediation
  method with no governance decision") or sit in `KnownNonEffectClientMethods` — which is pinned
  shrink-only at 19, keyword-classified (`read-only` / `internal-session-lifecycle-no-external-effect`),
  and would be a lie for a method that creates GitHub issues. `ExternalEffect.GitIssueCreate` does
  not exist (full-tree grep, 2026-08-03); **Story 31-13 owns minting it and this story is forbidden
  to** (story Out of Scope; 43-12 `:36` confirms the ownership split and that double-minting fails
  the catalog's duplicate-key guard). A `TammaApiClient` method therefore hard-orders 31-13 before
  40-8 — contradicting the story's own "land in either order".
  **Resolution:** a small `IIssueCreateClient` (+ HTTP impl) in the new activity's file, exactly the
  `ITriageApplyClient`/`HttpTriageApplyClient` idiom that already calls this same route
  (`ApplyTriageResultActivity.cs:314-319`). Governance is unchanged: the route is live-and-ungoverned
  today, and when 31-13 binds `effect:git.issue.create` at the route (Seam C), every caller —
  this client included — is governed. This is NOT the "second client" dodge 43-9 D17 warns about:
  the governance decision is explicitly owned and scheduled (31-13 Scope 4 + its AC3 drift sweep
  "no engine git route is uncatalogued"), and the precedent client for the route already exists.
  *Rejected:* `TammaApiClient.CreateIssueAsync` + `[PerformsEffect]` (orders 31-13 first);
  minting the effect here (out of scope, double-mint guard); raw `HttpClient` inline in the
  workflow class (ElsaServer workflows do no I/O; activities do).

- **D3 — Idempotency is dedupe-against-the-platform, not Elsa instance state.** Before creating,
  the activity lists the repo's issues (`GET /api/engine/issues`, `state=all`, paginated) and
  skips any input item whose exact title already exists; a per-run created-set guards within-run
  duplicates. The platform IS the durable record of what was created — a crash at any point
  (including instance loss) followed by a re-run of the same input produces exactly the input set
  once. This is the epic's own doctrine ("Elsa instance state is an optimization, not the truth").
  *Rejected:* progress in workflow variables only (lost with the instance; mid-burst persistence
  is exactly what Epic 40 says cannot be assumed); a dedupe key on the route (the route has no
  such parameter; changing the route's shape belongs to 31-13's mediation work).
  *Stated limitations (pinned by tests, listed in Risks):* two input items with the SAME title
  collapse to one issue (warning event); an unrelated pre-existing issue with a matching title
  suppresses creation (recorded as skipped).

- **D4 — One activity, never a fault, result always surfaced.** `CreateIssuesActivity`
  (`TammaOutcomeActivity`, `[FlowNode("Success", "Failure")]`, the `ApplyTriageResultActivity`
  fail-loud-as-outcome precedent — a faulted node's outbound edges never fire in Elsa 3.5, and the
  cycle has no failure edge from the dispatch, so the child MUST complete). Malformed/empty
  `issuesJson` → `Success` with 0 created + a warning in the batch event (AC1: never a fault,
  never a hang). A per-item HTTP failure emits a loud per-item FAILED event and continues; if any
  item failed the activity completes the `Failure` outcome — both outcomes route to the output
  surface → `Finish`, so the parent always resumes. Outputs: `createdCount`, `failedCount`,
  `skippedCount`, `issueNumbersJson` (AC1's "result carrying the created issue numbers"), via
  `SetOutput` per the `UpdateIssueStatusWorkflow` template. A static, Elsa-free
  `CreateIssuesCoreAsync` seam (the `ApplyCoreAsync` pattern) carries the parse/dedupe/create
  logic for unit tests. Mock short-circuit when `Engine:CallbackUrl` is absent, per the
  `ApplyTriageResultActivity.cs:139-149` precedent.

- **D5 — Events: one new minimal family through the EXISTING drain, because the "existing
  engine-side events for the create route" do not exist (Blocked #1).** Verified: the route emits
  nothing (`EngineEndpoints.cs:777-795`, `OctokitGitHubEngineCallbackService.cs:265-282`) and no
  issue-created family exists anywhere (`GitEventTypes.cs:41-42` has only `GIT.ISSUE_UPDATED.*`).
  The activity emits per item through `TammaEventEmitter` → the DCB drain (existing machinery,
  existing `AGGREGATE.ACTION.STATUS` grammar): `ISSUES.CREATE_ITEM.SUCCESS|FAILED|SKIPPED` (one
  per item, AC5) plus batch `ISSUES.CREATE.STARTED|COMPLETED`, constants in a new
  `IssuesCreateEvents` class mirroring `IssueStatusEvents`. *Rejected:* reusing `GIT.*` — that
  family is emitted by the Story 38 `GitMediationService` plane; forging its provenance from the
  workflow drain would misattribute the emitter. *Rejected:* adding emission to the route itself —
  that is 31-13's mediation/governance surface.

- **D6 — Tenant tagging needs the `TenantId` variable, so the two dispatch sites gain ONE input
  each.** The drain tenant-tags from the workflow variable literally named `TenantId`
  (`EventPersistenceMiddleware.cs:311-316`). The workflow declares it from an optional `tenantId`
  input; `SingleIssueCycleWorkflow`'s two dispatch dictionaries (`:284-288`, `:301-305`) gain
  `["tenantId"] = tenantId.Get(ctx)`. The story's "no cycle edit for the happy path" claim stays
  true — the path completes without this — the edit exists solely for AC5's tenant-tagged events.

- **D7 — Resume declaration: allowlist now, declare when 40-4 lands (Blocked #2).** Clause (c)
  of the shipped gate (`ResumableStandardStructuralTests.cs:240-261`) requires the document-coupled
  `ComputeReEntryPositionActivity` — exact type — in any `LatestStateReEntry`/`Both` graph.
  `CreateIssuesWorkflow` is not a document workflow; wiring that activity would be dishonest, and
  the representability fix (`CanonicalReEntryActivities`, 40-4 AC10) is drafted, unlanded, and not
  in this batch. So: a justified `LegacyResumeAllowlist` entry (the exact class of the existing
  `BranchCreationWorkflow`/`UpdateIssueStatusWorkflow` "side-effect leaf" entries), with the
  burn-down named: *"issue-create side-effect leaf; idempotent re-run via platform dedupe (40-8
  D3); declares LatestStateReEntry the moment 40-4's clause-(c) registry seam lands."* The
  idempotency SUBSTANCE of AC3 ships in full; only the attribute line is deferred. *Rejected:*
  declaring anyway (red build, or a false document-re-entry node); blocking this P1 fix on 40-4.

- **D8 — The structural test pins literal dispatch ids against declared ids, with a named 2-entry
  allowlist for the only two dynamic sites.** Fixture in `Tamma.Activities.Tests/Workflows/`
  (that project references `Tamma.ElsaServer` AND `Tamma.Api`, so ONE fixture covers both halves
  of AC4). Declared set: instantiate every concrete `WorkflowBase` (discovery per
  `ResumableStandardStructuralTests.cs:97-99`), build via `WorkflowTestHelper`, read
  `builder.DefinitionId`. Dispatched set: deep graph walk (the `:311-328` stack pattern —
  the shallow existing walk misses nested dispatches), collect `DispatchWorkflow` nodes, extract
  literals via the `ExtractLiteralValue` idiom. Constant-based ids
  (`new(DebugDiagnosisWorkflow.DebugDiagnosisDefinitionId)` etc.) resolve to literals at
  construction and participate normally. The two delegate-valued sites
  (`DocumentLifecycleWorkflow.cs:465, :657`) go on a NAMED allowlist keyed
  (workflow, activity id) with justification + staleness check (an entry whose site vanishes or
  becomes literal fails until deleted) — the house ratchet idiom. Anti-no-op floor: assert the
  sweep sees ≥ 75 literal dispatch sites and ≥ 25 distinct ids (actual today: ~80 sites, 29
  distinct — read the exact numbers from the sweep when seeding, pin just below), so a broken
  extractor cannot pass silently. **Mentorship half:** a capture test — `MentorshipController`
  with a Moq `IElsaWorkflowService`, call `StartMentorship`, assert the captured definition id is
  in the SAME declared set. Red today (`"tamma-autonomous-mentorship"`), green after the one-word
  fix at `MentorshipController.cs:89`. This deliberately does NOT widen the directory-sweep scope
  (AC4's instruction); the controller is pinned by capture, not by source scan.

- **D9 — Correlation seam, provided but not wired (43-14's job).** 43-11 Amendment 2-B/C: the
  defer/split creates are the post-acceptance tail of the plan-review chain; once 31-13 enforces
  `git.issue.create` (zone 35), a dial below 35 must not 409 a tail whose review a human approved —
  43-14's grant minting + correlation threading solves that generally. Cheap future-proofing here:
  `IIssueCreateClient`'s HTTP impl accepts an optional correlation id and sends it as
  `X-Tamma-Correlation-Id` (the header Seam C reads, per 43-11 Amendment 2-B). Left unbound (no
  caller passes it) until 43-14 decides the threading; the seam exists so 43-14's change is
  one input, not a client rewrite.

## Implementation Steps

Write the failing tests of steps 1-2 BEFORE their implementation steps where marked (fail-first).

1. **CREATE `tests/Tamma.Activities.Tests/Workflows/DispatchTargetStructuralTests.cs`** (AC4, D8) —
   the sweep + the dynamic-site allowlist (seeded: the two `DocumentLifecycleWorkflow` sites) +
   the anti-no-op floors + the Mentorship capture test. **Run it: it must fail exactly twice —
   `create-issues` unresolved (CreateDeferredIssues, CreateSplitIssues) and the mentorship id.**
   This is the bug's reproduction and the class guard, red before any fix exists. *Effort: 0.4d.*

2. **CREATE `src/Tamma.Activities/ADL/CreateIssuesActivity.cs`** (AC1/3/5, D2/D3/D4/D5/D9) — one
   file, the `ApplyTriageResultActivity.cs` layout: `IssuesCreateEvents` constants,
   `IIssueCreateClient` (`CreateIssueAsync` returning `(success, statusCode, issueNumber)`,
   `ListIssuesAsync` returning `(number, title, state)` pages), `HttpIssueCreateClient` over
   `POST /api/engine/create-issue` + `GET /api/engine/issues` with the optional
   `X-Tamma-Correlation-Id`, `CreateIssuesActivity` (`TammaOutcomeActivity`,
   `Success`/`Failure`, outputs, mock short-circuit), and the static `CreateIssuesCoreAsync`
   seam (parse-tolerant, dedupe, per-item create + events). Unit tests in step 5 are written
   against the core FIRST where the fail-first sequencing demands (see Test Plan). *Effort: 0.5d.*

3. **CREATE `src/Tamma.ElsaServer/Workflows/CreateIssuesWorkflow.cs`** (AC1, D1) — the
   `UpdateIssueStatusWorkflow` template: `DefinitionId = "create-issues"`,
   `Version = WorkflowVersions.ComputedVersion`, variables (`Repository`, `IssuesJson`,
   `TenantId`, result vars), ReadInputs → `CreateIssuesActivity` → (Success|Failure) → output
   `SetOutput`s (`createdCount`, `failedCount`, `skippedCount`, `issueNumbersJson`, `success`) →
   `Finish`. No `DispatchWorkflow` inside; no Program.cs edit (auto-scan). Step 1's structural
   test loses its `create-issues` failures here. *Effort: 0.25d.*

4. **MODIFY `tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs`** (D7) —
   add the `["CreateIssuesWorkflow"]` allowlist entry with the D7 justification (30 → 31 entries).
   Without it, clause (a) is red the moment step 3 lands — that red is the gate working; this step
   rides in the same commit as step 3. *Effort: 0.1d.*

5. **CREATE `tests/Tamma.Activities.Tests/ADL/CreateIssuesActivityTests.cs`** (AC1/AC3 core) —
   unit tests over `CreateIssuesCoreAsync` with a scripted `IIssueCreateClient` (see Test Plan;
   the double-create pin is written against the dedupe-less first cut and must be RED before the
   list-read lands). *Effort: 0.3d.*

6. **CREATE `tests/Tamma.Activities.Tests/Workflows/CreateIssuesWorkflowExecutionTests.cs`**
   (AC1/2/3/5, execution layer) — the `TriageItemCycleApplyFaultExecutionTests.cs:238-268` harness
   (real `IWorkflowRunner`, injected client, capturing `TammaApiClient` drain): completes with
   counts + numbers; malformed input reaches `Finish` (no incident, no hang); per-item failure
   emits the loud FAILED event and still completes; re-run-after-partial-failure creates the input
   set exactly once; `TenantId` input tenant-tags the drained events. *Effort: 0.4d.*

7. **MODIFY `src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** (AC5, D6) — add
   `["tenantId"] = tenantId.Get(ctx)` to the two dispatch input dictionaries (`:284-288`,
   `:301-305`). Nothing else in the cycle changes. Pinned by the step-8 routing additions,
   written first (red). *Effort: 0.1d.*

8. **MODIFY `tests/Tamma.Activities.Tests/Workflows/SingleIssueCycleRoutingTests.cs`** (AC2
   structural half) — `DeferDispatch_CarriesTenantIdInput` / `SplitDispatch_CarriesTenantIdInput`
   (materialize the dispatch `Input` dictionary the way `TaxonomyDriftBuildTests`'
   `ScanLlmCallDispatches` materializes dispatch inputs; red before step 7). The existing pins
   (`DeferPath_ConnectsTo_CreateDeferredIssues`, `DeferPath_ReportDeferred_ConnectsTo_Finish`,
   `SplitPath_*`) + step 1's resolution sweep + step 6's execution tests are, together, AC2's
   "bug's reproduction, inverted" — see Blocked #3 for why the literal full-cycle execution pin
   is not writable today. *Effort: 0.15d.*

9. **MODIFY `src/Tamma.Api/Controllers/MentorshipController.cs:89`** (AC4 second half) —
   `"tamma-autonomous-mentorship"` → `"mentorship"`. Step 1's capture test goes green. Do NOT
   touch the descriptor prose that mentions the old name (`ActionCatalog.Descriptors.cs:491`,
   `ExternalEffect.cs:231,246` — doc text on 43-x-owned files; noted, not edited). *Effort: 0.05d.*

10. **Full `dotnet test`** (AC6) + **MODIFY `.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md`**
    — status OPEN → RESOLVED with the fix summary and the structural-guard pointer. *Effort: 0.15d.*

Total ≈ 2.4 days (story budgeted 2; the overage is step 1's two-plane sweep — flagged, not hidden).

Suggested commit grouping: [1] → [2,5] → [3,4] → [6] → [8,7] → [9] → [10].

## Test Plan (fail-first, per test)

A test that cannot go red against today's code is not evidence. Red states, explicitly:

| Test | Red state (what fails, against what) |
|---|---|
| `DispatchTargetStructuralTests.EveryDispatchedDefinitionId_ResolvesToADeclaredWorkflow` | **Red against today's tree**: reports exactly `SingleIssueCycleWorkflow/CreateDeferredIssues → "create-issues"` and `…/CreateSplitIssues → "create-issues"`. Green after step 3. This failure IS the bug; the test would have caught it at introduction. |
| `DispatchTargetStructuralTests.MentorshipController_DispatchesADeclaredDefinitionId` | **Red against today's tree**: captured `"tamma-autonomous-mentorship"` ∉ declared set. Green after step 9. |
| `DispatchTargetStructuralTests.DynamicDispatchAllowlist_HasNoStaleEntries` | Green at birth; goes red if either `DocumentLifecycleWorkflow` delegate site is removed/becomes literal without deleting its entry (staleness both ways, house idiom). |
| `DispatchTargetStructuralTests.Sweep_SeesTheDispatchSurface` (floors) | Green at birth; red if the extractor or walk silently breaks (sites < floor). The anti-no-op tripwire — without it the resolution test is satisfiable by an extractor that extracts nothing. |
| `CreateIssuesActivityTests.ReRunAfterPartialFailure_DoesNotDoubleCreate` | **Written against the dedupe-less first cut of `CreateIssuesCoreAsync` and run red**: scripted client fails at item 3/5 on run 1; run 2 re-sends all 5 while `ListIssuesAsync` returns run 1's 2 creations; without the pre-list the client records 7 creates for 5 titles. Green when D3's dedupe lands. The sequencing (naive loop → red → dedupe → green) is mandatory, not optional. |
| `CreateIssuesActivityTests.MalformedJson_CompletesWithZero_AndWarns` / `EmptyArray_CompletesWithZero` | Red against a first cut that lets `JsonException` escape; green when the tolerant parse lands. (`"[]"`, `""`, `"not json"`, `"{}"`, and an array of non-objects all end at 0-created + warning, no throw.) |
| `CreateIssuesActivityTests.CreatesOneIssuePerItem_AndReturnsNumbers` | Red = does not compile until the core exists (weak evidence — stated honestly); its real teeth are the per-item client-call count + returned-number assertions, which go red on any later regression. |
| `CreateIssuesActivityTests.DuplicateTitlesInInput_CollapseWithWarning` | Pins the D3 limitation so it is a documented behavior, not a surprise. |
| `CreateIssuesWorkflowExecutionTests.Completes_WithCounts_AndIssueNumbersOutput` | Real-runner run of the built workflow; red until step 3 wires outputs correctly. |
| `CreateIssuesWorkflowExecutionTests.MalformedInput_StillReachesFinish` | Asserts Finished status, zero incidents, zero pending bookmarks — the "never a fault, never a hang" half of AC1 at the workflow level. |
| `CreateIssuesWorkflowExecutionTests.PerItemFailure_EmitsLoudFailedEvent_AndStillCompletes` | Captures the drain; red if a failure path swallows the event or faults the instance. |
| `CreateIssuesWorkflowExecutionTests.TenantId_Input_TagsTheDrainedEvents` | **Red if the variable is named anything but `TenantId`** — pins the `ResolveTenantId` contract (`EventPersistenceMiddleware.cs:313`), which is invisible at compile time. |
| `SingleIssueCycleRoutingTests.DeferDispatch_CarriesTenantIdInput` / `SplitDispatch_CarriesTenantIdInput` | **Red against today's tree** (dictionaries at `:284-288`/`:301-305` have no `tenantId` key); green after step 7. |
| `ResumableStandardStructuralTests` (existing) | Goes red the moment step 3 lands without step 4's allowlist entry — the declare-XOR-allowlist gate working; the commit pairing makes the suite green again with the justification on record. |

Existing pins that must stay green (run explicitly): `SingleIssueCycleRoutingTests` (all),
`WorkflowStructureTests.SingleIssueCycleWorkflow_ActivityCountIsReasonable` (20–80 bound —
unchanged, the cycle gains no activities), the full `MediationClientEffectSweepTests` fixture
(untouched by design — D2), `TaxonomyDriftBuildTests` (the new workflow has a parameterless ctor
and no llm-call dispatches), `KnownUngovernedEndpoints` (no new routes).

## Count pins (read from the tree 2026-08-03)

| Pin | Before | After | Why |
|---|---|---|---|
| `ResumableStandardStructuralTests.LegacyResumeAllowlist` entries | 30 | 31 | D7's `CreateIssuesWorkflow` entry. **No numeric pin exists on this list** (the ratchet is staleness-based); the addition cuts against the burn-down direction and is therefore justified in-line with a named landing condition (40-4). Recorded here so the reviewer sees it as a decision, not drift. |
| `MediationClientEffectSweepTests` — baseline 19, `NonEffectPinHistory [19]`, exceptions pin 1, surface pin **37** (`:664`) | — | **unchanged** | D2 exists precisely so none of these move. |
| `KnownUngovernedEndpoints` — `PinnedCount` **216**, `PinnedInScopeCount` **239** | — | **unchanged** | No route added or bound here (31-13 owns the route's binding). |
| `DispatchTargetStructuralTests` dynamic-site allowlist (NEW) | — | seeded at **2** (`DocumentLifecycleWorkflow.cs:465, :657`), shrink-only message | New pin, born with its floor tests. |
| `DispatchTargetStructuralTests` sweep floors (NEW) | — | seeded from the tree at implementation (~80 literal sites / 29 distinct ids today; pin just below the observed values) | Anti-no-op. |

No EF migrations, no `Program.cs` edits, no catalog descriptor changes — nothing else pinned moves.

## Risks

- **Title-match dedupe is heuristic.** Duplicate titles inside one input list collapse (warned,
  pinned); an unrelated pre-existing same-title issue suppresses a create (recorded as skipped,
  visible in `skippedCount` + the SKIPPED event). Accepted: the failure mode is under-creation
  with a loud record — strictly better than the double-creation it prevents. A platform-side
  dedupe key is 31-13-shaped future work.
- **Pagination cost on big repos.** The dedupe list pages `GET /api/engine/issues` (100/page,
  `state=all`). Defer/split fires at most once per cycle run; bounded page cap (e.g. 10 pages)
  with a warning event when truncated — beyond the cap, dedupe degrades to within-run only.
  Stated in the activity's doc comment.
- **The mock short-circuit reports success without creating** (no `Engine:CallbackUrl`).
  Inherited from the `ApplyTriageResultActivity` precedent, kept for parity, logged loudly.
- **When 31-13 enforces the route at zone 35, defer/split gains a gate mid-chain.** A dial < 35
  then 409s the tail of an approved review — the exact Amendment 2-B shape. That is 43-14's
  grant-minting to solve; D9's correlation-header seam is this story's contribution. Until 31-13
  lands, behavior is unchanged (route ungoverned).
- **Two stories edit `ResumableStandardStructuralTests.cs`** (this one adds an entry; 40-5 —
  outside this batch — deletes one). Different lines; trivial merge, named here for wave planning.
- **The event family is new vocabulary** (AC5 deviation, Blocked #1). Contained: two constants
  classes' worth, existing grammar, existing drain; the honest alternative (emit nothing) fails
  AC5's audit intent outright.

## Blocked / contradictions

1. **AC5's premise is false against the tree.** "Issue creation emits the *existing* engine-side
   events for the create route" — the create route emits NO events (verified:
   `EngineEndpoints.cs:777-795`, `OctokitGitHubEngineCallbackService.cs:265-282`), and no
   issue-created event family exists anywhere (`GitEventTypes.cs:41-42` is `ISSUE_UPDATED` only).
   "No new event vocabulary" is therefore unsatisfiable alongside "emits events, one per item".
   **Resolution (deviation, recorded):** D5 — a minimal new family through the existing drain and
   grammar. If the story owner prefers zero new event types, the only alternative is emitting
   nothing until 31-13 puts the route on the mediation event plane — say so and AC5 drops here.
2. **AC3's "`[ResumeBehavior]` declared" cannot pass the shipped gate.** Clause (c)
   (`ResumableStandardStructuralTests.cs:240-261`) requires the document-coupled
   `ComputeReEntryPositionActivity` — exact type — for any `LatestStateReEntry`/`Both`
   declaration; the representability fix is 40-4 AC10 (`CanonicalReEntryActivities`), drafted,
   unlanded, outside this batch. **Resolution (deviation, recorded):** D7 — justified allowlist
   entry now with the declaration explicitly deferred to 40-4's wake; AC3's substantive guarantee
   (no double-create on kill/resume) ships and is pinned regardless.
3. **AC2's literal pin — a full `SingleIssueCycleWorkflow` execution driven to Defer/Split
   through a real awaited dispatch — is not writable today.** The only full-runtime dispatch
   harness in the tree is `[Explicit]` and diagnosed non-functional
   (`DocumentLifecycleExecutionTests.cs:55-62`: no CI selects `[Explicit]`, and the bare harness
   suspends forever on the first `Kind=Task` activity); `TriageItemCycleApplyFaultExecutionTests`
   records the same constraint and pins the branch instead. **Resolution (layered substitute,
   recorded):** existing Defer/Split routing pins + step 1's dispatch-resolution sweep (the
   inverted bug) + step 6's real-runner execution of the child + step 8's input pins. If a working
   dispatcher harness lands later (40-x), the literal end-to-end test is a follow-up, not a gap
   in the guarantee chain.
4. **Stale citation:** the mentorship literal is at `MentorshipController.cs:89`, not `:79` as the
   story (and the epic-41 README it quotes) say. Same defect, moved line. Fixed at `:89`.
5. **The story's "via `TammaApiClient`" wording contradicts the mediation-sweep ratchet given the
   no-minting rule** (no `ExternalEffect.GitIssueCreate` exists to attribute; a non-effect entry
   is pinned shut and would be false). **Resolution (deviation, recorded):** D2. If the owner
   insists on `TammaApiClient`, the story's "31-13: land in either order" flips to "31-13's key
   first" — one or the other must give; this plan gives the client wording.

## Dependencies on the batch (43-12..16, 42-10, 39-25, 31-13) and neighbors

- **31-13** — no ordering constraint either way under D2 (the plan's design preserves the story's
  "land in either order"). Cross-links to honor: 31-13's drift sweep must count this workflow's
  route usage among `git.issue.create` callers; if 31-13 lands FIRST and enforces the route, this
  workflow's calls are governed from birth (correct, and the 43-14 note below applies).
- **43-14** — coordination, not blocking: once the route is enforced, the plan-review approval
  that yields defer/split must cover the create tail (correlation-standing grant, Amendment 2-B/C).
  D9's header seam on `IIssueCreateClient` is the hook; 43-14 owns threading the cycle correlation.
  43-14 should add the defer/split → `create-issues` chain to its grant-scope inventory.
- **43-13** — no file overlap; the caller-kind classification of these creates (LLM path — the
  tail of an LLM review outcome) rides the route binding, which is 31-13's, not this story's.
- **43-12** — no overlap by construction (`43-12…md:36,:55`: issue keys are 31-13's; double-mint
  fails the duplicate-key guard).
- **43-15, 43-16, 42-10, 39-25** — no shared files, no ordering constraints found.
- **Outside the batch:** **40-4** gates the deferred `[ResumeBehavior]` declaration (D7/Blocked #2);
  **40-5** edits the same test file as step 4 (different lines — merge note only); **40-2/40-7**
  edit `SingleIssueCycleWorkflow.cs` (step 7's two lines are in the dispatch dictionaries at
  `:284-288`/`:301-305`, away from the agent-loop region those stories touch).

## Files touched (for wave planning)

Production: `src/Tamma.Activities/ADL/CreateIssuesActivity.cs` (new),
`src/Tamma.ElsaServer/Workflows/CreateIssuesWorkflow.cs` (new),
`src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` (2 lines),
`src/Tamma.Api/Controllers/MentorshipController.cs` (1 literal).
Tests: `tests/Tamma.Activities.Tests/Workflows/DispatchTargetStructuralTests.cs` (new),
`tests/Tamma.Activities.Tests/Workflows/CreateIssuesWorkflowExecutionTests.cs` (new),
`tests/Tamma.Activities.Tests/ADL/CreateIssuesActivityTests.cs` (new),
`tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` (1 entry),
`tests/Tamma.Activities.Tests/Workflows/SingleIssueCycleRoutingTests.cs` (2 tests).
Docs: `.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md` (status),
`docs/stories/epic-40/story-40-8/implementation-plan.md` (this file).

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-03 | 1.0.0   | Initial plan. Verified every story citation against the tree (one stale: MentorshipController `:79` → `:89`). Recorded three AC contradictions (AC5's nonexistent route events; AC3's declaration vs clause (c); AC2's unrunnable full-dispatch harness) and one deliberate deviation (activity client seam instead of `TammaApiClient`, forced by the mediation-sweep ratchet + the no-minting rule). | Claude |
