# Implementation Plan — Story 41-7: Standup Synthesis Workflow

> ## ⛔ BLOCKED — this story cannot start, and the blocker has no owner
>
> 41-7 is a **scheduled, tenant-scoped, per-window idempotent** workflow (its AC1). The seam that makes
> that possible **does not exist in the codebase and no story in Epic 41 builds it.** The epic README
> names it as the fourth Wave-0 enabler with owner *"none — must be written"*
> (`epic-41/README.md:297`). Verified against `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs`,
> which the story's Scope line 19 cites as "the cron pattern" — it is not reusable as one:
>
> | 41-7 AC1 needs | `HourlyAnalyticsRollupScheduler` has | Line |
> |---|---|---|
> | dispatch any workflow | `HourlyAnalyticsRollupWorkflow.DefinitionId` hardcoded | `:197-198` |
> | a window / cron shape | a single `int FireAtMinute` | `:34` |
> | `tenantId` threaded into the dispatch | `DispatchWorkflowDefinitionRequest` with **no** input variables | `:199-203` |
> | a **persisted** last-fired window | `private (int,int,int) _lastFired` — in-process, reset on restart | `:83` |
> | a tenant component in the advisory-lock key | `ComputeAdvisoryLockKey(year, dayOfYear, hour)` — **one tenant's leader suppresses every other tenant's fire** | `:241` |
>
> **This plan therefore plans everything EXCEPT the seam, and does not invent it.** Steps 1–9 are the
> workflow; the trigger is step 10 and is `TODO(scheduler-seam)`. Writing the seam is a prerequisite for
> Wave 2 in its entirety (41-5, 41-7, 41-11, 41-16, 41-17's PR sweep, 41-20, 41-23) and should be a
> separate, owned story before any of them is scheduled.

## Scope & Deliverable

When this story is done (and the seam exists), a scrum master's daily status assembly is a **document the
platform produces from the audit trail**. A new `StandupSynthesisWorkflow`
(`DefinitionId = "standup-synthesis"` — free today) is a THIN BINDING over `document-lifecycle`: a
tenant-scoped scheduled trigger fires it per `(tenant, repository, window)`, it reads the DCB event window
plus the open `Decomposition`/`Plan`/PR and blocker signals through a new `FetchEventWindowActivity`,
dispatches `document-lifecycle` with `documentType = "findings"` and the `(scrum_master,
synthesize-standup)` producer cell (41-1a), and routes the typed exit. Zero `Finish`, zero `llm-call`,
zero parsing. Every digest item cites concrete DCB evidence (enforced by `FindingsDocumentType`'s
`MISSING_EVIDENCE`, not by story-local code). A new `STANDUP.*` family rides alongside `DOCUMENT.*`,
tagged `repository`/`tenantId`/window.

## Pre-Reading

- `docs/stories/epic-41/story-41-7/41-7-standup-synthesis.md` — the story (ACs are source of truth modulo
  Corrections)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); the Dependencies section's scheduler bullet
  and the Epic 42 caveat table (41-7 is in it: "authenticated HTTP / external API (42-9) — no executor")
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — mints `AgentRole.ScrumMaster`,
  `synthesize-standup`, `Prompts/scrum_master/_system.md` + `Prompts/scrum_master/synthesize-standup.md`,
  removes the `scrum_master → product_owner` alias (`RolePhaseMap.cs:239`), and moves the count pins
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — read it to
  understand why it is NOT the pattern (the table above), not to copy it
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the binding skeleton to copy
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — **read `Validate` in full**: the type
  hard-rejects an empty findings list (`EMPTY_FINDINGS`), a finding with no citations
  (`MISSING_EVIDENCE`), and relevance/confidence outside [0,1] (`RELEVANCE_OUT_OF_RANGE` /
  `CONFIDENCE_OUT_OF_RANGE`, rejected, never clamped). This is why Correction 1 exists.
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs` — **the only in-engine
  precedent for reading `IEventRepository` from activity code**; `FetchEventWindowActivity` copies its
  service-resolution + tenant-resolution posture
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the 4-7 query surface
  (`QueryAsync`, `QueryWithPaginationAsync`, `ListByTenantAsync(tenantId, typePrefix, limit, offset)`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs` — the **empty-input
  SKIPPED short-circuit** precedent (emitted before any dispatch), which Correction 1 reuses
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs` — `ScopeIssueId`, the
  producer-scoped lifecycle key D3 generalises to a window key
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:445-451` — `InitiatorOnlyTaskAudienceResolver` (fail-closed)
  and `AgentOfflineChatRelay` (refuses every message): why AC4 is not deliverable
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`;
  `TaxonomyDriftBuildTests.cs:110`, `:125-150`, `:460`; `ContractBindingTests.cs`;
  `ResumableStandardStructuralTests.cs`

## Corrections to the story

1. **AC2's "empty window ⇒ valid empty digest" is IMPOSSIBLE against the shipped type.**
   `FindingsDocumentType.Validate` (`Findings.cs`) emits `EMPTY_FINDINGS` for a zero-length findings list
   with the explicit rationale *"an empty list is a violation, not a valid 'nothing found'"*. An empty
   digest would loop the repair/revise ring to exhaustion and exit `escalated` — the exact "false noise"
   the AC is trying to avoid, inverted. Two options: (a) relax `EMPTY_FINDINGS` — **rejected**, it would
   weaken `research`, `triage-context-gathering` and every future `Findings` producer; (b) **short-circuit
   before dispatch**: when the window read returns no material events, emit `STANDUP.SYNTHESIS.SKIPPED`
   and expose `status = "skipped"` with **no document produced and no lifecycle dispatch** — the landed
   `TriagePODecisionWorkflow` empty-input precedent. **This plan takes (b)** and AC2 should read: *"an
   empty window produces no document and a `STANDUP.SYNTHESIS.SKIPPED` audit row — never an empty
   `Findings` and never a false digest."*
2. **AC2's "every finding cites concrete DCB evidence; confidence/relevance ∈ [0,1]" is already enforced
   by the type** (`MISSING_EVIDENCE`, `RELEVANCE_OUT_OF_RANGE`, `CONFIDENCE_OUT_OF_RANGE`). It costs this
   story a fixture, not a validator. The **new** work is making the citations *resolvable* — a citation
   string that names no real event is not caught by `Findings.Validate`. D4 adds that as a
   `validationContextJson` ring, which is the honest reading of "concrete DCB evidence".
3. **AC4 is not deliverable and must be re-scoped.** "Blocker follow-ups land in the correct role's Task
   View via the 39-20 audience resolver" — 39-20 has not landed; `ITaskAudienceResolver` is stubbed
   fail-closed by `InitiatorOnlyTaskAudienceResolver` (`Program.cs:445-447`), which admits only the issue
   initiator, and 39-19 ships no Task View at all (`AgentOfflineChatRelay` refuses every chat message,
   `:448-451`). The epic README names 41-7:49 as one of three ACs that fail *at the AC level*. Claim the
   half that exists: *"each flagged blocker is emitted as a `STANDUP.BLOCKER_FLAGGED` row carrying the
   owning role, and the accepted digest publishes an `AcceptanceRequest` on the orchestrator channel;
   role-scoped delivery is unreachable until 39-19/39-20."*
4. **Publication is not reachable on the agent path.** Per the story's own Epic 42 caveat and the README
   table, none of the six registered `IToolExecutor`s (`Program.cs:753-764`: `FileRead`, `FileWrite`,
   `SearchCode`, `ShellExecute`, `GitOperations`, `RunTests`) can post to a chat or tracker. Synthesis is
   agent-reachable; **broadcast is human-assigned until 42-9.** This is stated in the story and is
   correct — it is repeated here because it changes what "delivered to the team" means in the
   Orchestrator/user-interaction section.
5. **Scope line 19's "`HourlyAnalyticsRollupScheduler` cron pattern" is not a pattern.** See the blocked
   banner. The story's Dependencies line already carries the corrected wording; Scope does not, and
   should.
6. **AC3's `[ResumeBehavior(LatestStateReEntry)]` is correct as written** — unlike 41-8/41-9/41-10, whose
   `Both` declarations would fail the 39-10 gate. No change.

## Design Decisions

- **D1 — three components, one story: a trigger seam consumer, a window-read activity, and a thin
  binding.** The workflow itself is a standard binding; the two genuinely new pieces are (i)
  `FetchEventWindowActivity` — the first in-engine read of a DCB *window* (as opposed to 39-10's
  per-issue slice) — and (ii) the trigger, which this story **consumes but does not build**. Keeping them
  separate means steps 1–9 are shippable and testable by manual/API dispatch the day 41-1a lands, and only
  step 10 waits on the seam.
- **D2 — `FetchEventWindowActivity` is a new activity in `Tamma.Activities/Documents/`, modelled on
  `LifecycleReEntryService`.** Inputs: `TenantId`, `Repository`, `WindowStartUtc`, `WindowEndUtc`,
  `TypePrefixesJson` (default `["DOCUMENT.","DECOMPOSITION.","PLAN.","PR.","BLOCKER.","CYCLE.","DEPLOY."]`),
  `MaxEvents` (bounded, default 2000 — `LifecycleReEntryService.MaxEventsPerFamily`'s posture).
  Outputs: `EventsJson` (a neutral, Core-visible DTO list: `{eventId, type, createdAtUtc, issueId,
  repository, status, summary}`), `EventCount`, `EvidenceIndexJson` (the id→type map D4's validation ring
  reads). It resolves `IEventRepository` + `ITenantContext` via `context.GetService<T>()` (the
  `EventPersistenceMiddleware` pattern) and reads through `ListByTenantAsync(tenantId, typePrefix, limit,
  offset)` per prefix, filtered to the window in memory. **A missing service is a fail-loud
  `TammaError STANDUP.WINDOW.SERVICE_UNREGISTERED`, never an empty window** — an empty window is a
  business outcome (D5) and must not be indistinguishable from a wiring bug.
- **D3 — the window IS the lifecycle issue id, and that is what makes AC1's idempotency real.** A standup
  digest has no `issueId`, but `ComputeReEntryPositionActivity`, `GetLatestAcceptedAsync` and the 39-11
  read are all keyed on one. Generalise 39-15's `CreationBindingHelper.ScopeIssueId`: the binding computes
  `issueId = "standup:{repository}:{windowStartUtc:yyyy-MM-dd}"` (normalised through the same segment
  transform). Consequence: **a duplicate fire for the same window re-enters at `Complete` and
  short-circuits** — emitting `DOCUMENT.REENTERED` and no second `DOCUMENT.ACCEPTED` — so AC1's "re-running
  the same window is a no-op re-read" is delivered by the existing 39-10 machinery rather than by new
  code. The scheduler seam still needs its own durable fire-once record (a lost fire is a different
  failure from a duplicate one), but the *document* side is idempotent for free.
- **D4 — "cites concrete DCB evidence" is enforced with a `validationContextJson` ring, not a prose
  hope.** `FetchEventWindowActivity` emits `EvidenceIndexJson` (the set of event ids the window contains);
  the binding forwards it as `validationContextJson`; `FindingsDocumentType` gains a
  `ValidateWithContext` override (the 39-15 D3 seam, `IDocumentType.cs:35-43`) that — **only when the
  context is non-empty** — asserts every citation string resolves to an id in the index, with a new
  violation `CITATION_UNKNOWN_EVENT`. Empty context ⇒ identical to today ⇒ `research` and
  `triage-context-gathering` are byte-behaviour-stable. This is the same conditional-rule shape 41-10 uses
  for design facets; the two stories should land the pattern consistently.
- **D5 — the empty window short-circuits before the dispatch (Correction 1).** Graph node
  `WindowHasMaterial` `FlowDecision` on `EventCount > 0`: False → `EmitStandupSkipped` → `ExposeOutput`
  with `status = "skipped"`; True → `DispatchLifecycle`. This is a typed-value branch (39-12 D2's
  sanctioned kind), not a quality decision, and the structure test pins the `FlowDecision` id set so a
  parse gate cannot reappear.
- **D6 — `STANDUP.*` is a five-member family.** `SYNTHESIS.STARTED` / `.DIGEST` (the story's two) plus
  `.SKIPPED` (D5), `.BLOCKER_FLAGGED` (per flagged item, carrying the owning role — Correction 3's
  claimable half) and `.FAILED` (LOUD, on `rejected`/`escalated`). New
  `Tamma.Activities/Standup/StandupEvents.cs` + `EmitStandupEventActivity.cs`. All tagged `repository`,
  `tenantId`, `windowStartUtc`, `windowEndUtc`.
- **D7 — acceptance policy is passed through.** `AcceptanceDefaults.For(DocumentTypeKey.Findings)` falls
  to the `_ => Rules` catch-all (single `architect`, unanimous) — wrong for a standup digest, and
  **`AcceptanceDefaults.cs` is not this story's file to edit** (it is per document type, shared with
  `research` and `triage-context-gathering`). The binding forwards a caller-supplied
  `acceptanceRulesJson`, and the story ships a documented default (scrum-master reviewer at autonomy
  70–84, self-accept at 85–100) as configuration, not code.
- **D8 — the lockstep set, enumerated.** (i) `DocumentTypeRegistry.BuildSeed` +=
  `new WorkflowDocumentInterface("standup-synthesis", empty, DocumentTypeKey.Findings, false)`;
  (ii) `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` `HaveCount(16)` → `+1`;
  (iii) that test's `reconciled` array += `"standup-synthesis"`; (iv) `ContractBindingTests.Bindings` +=
  `[("scrum_master", "synthesize-standup")] = new("FindingsDocumentType.Validate", [...the seven Findings
  token groups...])` — the same groups as `(product_owner, research)`; (v)
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += `"StandupSynthesisWorkflow"`; (vi) NO
  `ResumableStandardStructuralTests` allowlist entry. **The taxonomy count pins
  (`AgentRoleTests.cs:12` `Be(8)`, `AgentActionTests.cs:38` `Be(80)`, `RolePhaseMapTests.cs:64`
  `HaveCount(80)`, `SystemPromptsTests.cs:61` `HaveCount(8)`, `ConventionStoreEndpointsTests.cs:720/:744`)
  are 41-1a's, moved once for all three roles and fifteen cells — this story must not touch them.**

## Implementation Steps

1. **Precondition gate (no code).** Verify `AgentRole.ScrumMaster` exists, `(scrum_master,
   synthesize-standup)` passes `RolePhaseMap.IsRoleEligibleForPhase`, and
   `Prompts/scrum_master/synthesize-standup.md` + `Prompts/scrum_master/_system.md` exist with the
   `Findings` token groups and a declared `contextFindings` carrier (D8(iv) / the render-drop lesson).
   Any gap is a 41-1a defect — file it there.
2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/EventWindowRow.cs`** — the neutral, Core-visible
   window DTO (`Tamma.Core` cannot see `Tamma.Data`, exactly as `LifecycleResumeCalculator`'s
   `ResumeEventRow` cannot).
3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchEventWindowActivity.cs`** per D2.
4. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs`** per D4 — `CitationUnknownEvent`
   constant + `ValidateWithContext` override + a context-bearing example. `Validate` untouched.
5. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Standup/StandupEvents.cs` +
   `EmitStandupEventActivity.cs`** (D6).
6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/StandupBindingHelper.cs`** — pure:
   `BuildWindowIssueId(repository, windowStartUtc)` (D3), `BuildEvidenceContext(evidenceIndexJson)`,
   `ProjectDigest(documentJson)`, `ExtractFlaggedBlockers(documentJson)` (→ the `.BLOCKER_FLAGGED` rows),
   `BuildFailureDetail(exit)`. `ReadLifecycleResult`/`IsAccepted` from `LifecycleBindingHelper`.
7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/StandupSynthesisWorkflow.cs`** —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`; graph `ReadInputs` → `ComputeReEntryPosition`
   (`documentType = "findings"`, `IssueId = WindowIssueId`) → `ReadPositionStage` → `FreshRun` → (True)
   `EmitStandupStarted` + `FetchEventWindow` → `WindowHasMaterial` (D5) → `DispatchLifecycle` →
   `ReadLifecycleExit` → `DigestAccepted` → `EmitStandupDigest` + `EmitBlockerFlagged` /
   `EmitStandupFailed` → `ExposeOutput`. Exactly ONE `DispatchWorkflow`, target `document-lifecycle`.
8. **MODIFY `DocumentTypeRegistry.cs` + the four pins** — D8(i)–(v).
9. **CREATE the tests** — see Test Plan. At this point the story is shippable and dispatchable by API;
   only the cadence is missing.
10. **`TODO(scheduler-seam)` — the trigger.** NOT buildable today. When the seam exists it must supply:
    a tenant component in the advisory-lock key; `tenantId` + `repository` + `windowStartUtc`/`EndUtc`
    threaded into the dispatch input; a **persisted** last-fired window per `(tenant, workflow, window)`;
    and a window/cron shape rather than one `FireAtMinute`. This story's consumption surface is exactly
    those four dispatch inputs — record them as the seam's required contract so whoever writes it has a
    concrete consumer.

## Data & Migrations

None **for this story**. `Findings` payloads are JSONB in 39-11's tables; `STANDUP.*`/`DOCUMENT.*` ride
the existing drain. `has-pending-model-changes` stays clean. *The scheduler seam will need its own
persisted last-fired table — that is the seam story's migration, not this one's.*

## Events

- **Emits:** `STANDUP.SYNTHESIS.STARTED` (fresh runs only), `.DIGEST` (on lifecycle `accepted`, data:
  `itemCount`, `blockedCount`, `documentId`), `.SKIPPED` (empty window, D5), `.BLOCKER_FLAGGED` (one per
  flagged item, data: `owningRole`, `issueId`, `evidence`), `.FAILED` (LOUD). Tags `repository`,
  `tenantId`, `windowStartUtc`, `windowEndUtc`, `correlationId`.
- **Consumes (the window read):** the configured type prefixes — `DOCUMENT.`, `DECOMPOSITION.`, `PLAN.`,
  `PR.`, `BLOCKER.`, `CYCLE.`, `DEPLOY.` — via `IEventRepository.ListByTenantAsync`. **Read-only; this
  story adds no consumer to any family.**
- **Emitted by the machinery this story wires in:** `DOCUMENT.*`, `APPROVAL.*`, `ESCALATION.TRIGGERED`.

## Test Plan

- **`FetchEventWindowActivityTests` (Moq'd `IEventRepository` + `ITenantContext`)** — window bounds are
  respected (an event one second outside is excluded); the prefix set is honoured; `MaxEvents` bounds the
  read; a missing `IEventRepository` throws `STANDUP.WINDOW.SERVICE_UNREGISTERED` (**never** an empty
  window — D2); zero matching events yields `EventCount == 0` with a well-formed empty `EventsJson`;
  cross-tenant rows are never returned (the repository is tenant-scoped, asserted on the call arguments).
- **`FindingsCitationContextTests` (Tamma.Core.Tests, pure)** — with a non-empty evidence index, a finding
  citing an unknown event id ⇒ `CITATION_UNKNOWN_EVENT`; citing a known id ⇒ valid. **Regression pin: the
  SAME payload validates clean with an EMPTY context** (`research` / `triage-context-gathering`
  unaffected). Plus the inherited matrix: empty findings list ⇒ `EMPTY_FINDINGS` (the fixture that makes
  Correction 1 concrete); a finding with no citations ⇒ `MISSING_EVIDENCE`; relevance 1.5 ⇒
  `RELEVANCE_OUT_OF_RANGE`. **Covers AC2.**
- **`StandupSynthesisWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set:
  `DefinitionId == "standup-synthesis"`; threads `TenantId`; no retry-plumbing variables; **exactly one
  `DispatchWorkflow`, literal id `document-lifecycle`**; zero `llm-call`; **zero `Finish`**;
  `ComputeReEntryPositionActivity` + `FetchEventWindowActivity` present; declares `LatestStateReEntry`;
  no `Wait*` node; `FlowDecision` id set pinned to exactly `{FreshRun, WindowHasMaterial, DigestAccepted}`;
  `ScanLifecycleBindingDispatches()` contains `(StandupSynthesisWorkflow, DispatchLifecycle, scrum_master,
  synthesize-standup)`; `MaterializeDispatchInput` yields `documentType == "findings"` and the declared
  `feedbackVariableName`. **Covers AC3 (structure half), rule-1 clauses (a)–(e).**
- **`StandupBindingHelperTests`** — `BuildWindowIssueId` is deterministic and tenant/repo/window-folded
  (two repos or two windows never collide); `ProjectDigest`/`ExtractFlaggedBlockers` on valid/unreadable
  JSON; `BuildFailureDetail` names each reachable outcome wire.
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (bumped, `standup-synthesis` in
  `reconciled`); `ContractBindingTests` (new entry satisfied by 41-1a's template);
  `TaxonomyDriftBuildTests`; `ResumableStandardStructuralTests` green with **no** allowlist entry.
  **Covers AC3 (gate half).**
- **`StandupSynthesisExecutionTests` (Testcontainers, shared 39-6/39-10 fixture)** — (a) happy path: seed
  a window of `DOCUMENT.*`/`BLOCKER.*` rows → valid digest draft → review → accept → accepted `Findings`
  readable by the window issue id, `.DIGEST` + `.BLOCKER_FLAGGED` rows present with the owning role.
  (b) **AC1 idempotency (D3):** dispatch the SAME window twice → the second run re-enters at `Complete`,
  emits `DOCUMENT.REENTERED`, produces no second document, and the stream carries exactly ONE
  `DOCUMENT.ACCEPTED` and ONE `STANDUP.SYNTHESIS.DIGEST`. (c) **empty window (Correction 1):** no seeded
  events → `STANDUP.SYNTHESIS.SKIPPED`, `status = "skipped"`, **zero** `document-lifecycle` instances
  started, zero `Findings` rows. (d) evidence ring: a draft citing a fabricated event id →
  `CITATION_UNKNOWN_EVENT` → repair/revise → accept. (e) tenant isolation: two tenants' windows produce
  two independent documents and neither reads the other's events. **Covers AC1, AC2, AC3 (re-entry half).**
- **Not tested, by design:** AC4's role-scoped Task View delivery (Correction 3 — the resolver is
  fail-closed) and the broadcast half (Correction 4 — no executor). Both are asserted **absent**: a test
  pins that the workflow performs no publication side effect, so the gap is visible rather than implied.

## Risks & Mitigations

- **The scheduler seam is unowned — this is the top risk and it is a programme risk, not a technical
  one.** Mitigation: steps 1–9 are seam-independent and dispatchable by API, so the story delivers value
  as a manually/orchestrator-triggered digest; step 10's four required inputs are written down as the
  seam's consumer contract. Do not build a 41-7-local scheduler: six other stories need the same seam and
  a local copy would be the second non-reusable one.
- **41-1a is a hard gate on both paths.** A human assignee still needs a `(scrum_master,
  synthesize-standup)` cell to bind, and `PromptFileLoader` refuses to boot on a taxonomy cell with no
  file. Mitigation: step 1 is a real gate.
- **41-1a's `scrum_master` alias removal is a live behaviour change** (`RolePhaseMap.cs:239` maps
  `scrum_master → product_owner` today, so stored configs silently re-point). Mitigation: not this story's
  to solve — 41-1a AC5 owns the migration; this story's execution tests must run **after** that lands so
  the resolved provider chain is the intended one.
- **Touching `Findings.cs` risks regressing `research` / `triage-context-gathering`.** Mitigation: D4's
  rule is an override that no-ops on empty context; the regression pin asserts the sibling behaviour
  explicitly in both the unit and execution suites (the same guard 41-10 uses for `Design`).
- **The window read is unbounded in principle.** A busy tenant's day could return far more than 2000 rows
  and silently truncate the digest's evidence base. Mitigation: `MaxEvents` is an explicit input, a
  truncated read sets a `Truncated` output flag, and the flag is carried into the digest's summary and the
  `.DIGEST` event data — visible truncation, never a silently partial digest.
- **Two `Findings` producers per window key.** 41-11's risk `Findings` and this digest must not share a
  lifecycle key; D3's `standup:` prefix and 41-11's own scope prefix keep them disjoint. Assert it in (e).

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a precondition + template verification | 0.25 |
| 2–3 | `EventWindowRow` + `FetchEventWindowActivity` | 1.0 |
| 4 | `Findings.ValidateWithContext` evidence ring | 0.5 |
| 5 | `StandupEvents` + emitter | 0.25 |
| 6 | `StandupBindingHelper` (pure) | 0.5 |
| 7 | `StandupSynthesisWorkflow` binding | 0.75 |
| 8 | Registry edge + four pin bumps | 0.25 |
| 9 | Core + activity + structure + helper + Testcontainers suites | 1.5 |
| 10 | Scheduler trigger | **not estimable — the seam does not exist** |
| **Total (steps 1–9)** | | **5.0** (story estimate: 4–5 days, which did not include the seam either) |

## Blocks / Blocked by

- **Blocked by — hard, no owner, cannot be worked around:**
  - **The tenant-aware scheduled-trigger seam.** No story in Epic 41 builds it
    (`epic-41/README.md:297`, `:454-472`). AC1 is unreachable without it. Needs: a tenant component in the
    advisory-lock key, `tenantId` threaded into the dispatch, a **persisted** last-fired window, and a
    window/cron shape. Shared with **41-5**, **41-11**, **41-16**, **41-17** (PR sweep), **41-20**,
    **41-23**.
- **Blocked by — hard, owned:**
  - **41-1a** — `AgentRole.ScrumMaster`, the `synthesize-standup` cell, its two prompt files, and the
    `scrum_master` alias removal. Blocking on **both** execution paths.
  - **Epic 39: 39-2/39-3** (`Findings` registered), **39-6**, **39-7**, **39-8**, **39-10**, **39-11**,
    **39-15** (the `ValidateWithContext` seam D4 rides) — **all landed**, verified in tree.
- **Blocked by — for AC-level claimability:** **39-19** + **39-20** (AC4 — Correction 3), **42-9**
  (broadcast — Correction 4). The story must state which half it claims.
- **NOT blocked by:** **41-1b** (reuses `Findings`) and **41-1c** (produces a typed document, not prose).
  41-7 appears in neither the README's 41-1b nor its 41-1c table — correctly.
- **Blocks / feeds:** **41-8** (the retro consumes accepted standup digests — the `Findings` edge is a
  store read, so 41-8 needs 41-7 *landed*, not *scheduled*); **41-11**, **41-16**, **41-20**, **41-23**
  inherit D2's `FetchEventWindowActivity` and D3's window-as-issue-id idempotency trick.
- **Shared edits:** `FetchEventWindowActivity` (this story and **41-11** both need it — whoever lands
  first builds it, the other consumes; register it before both start);
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)` today) — moved by
  41-9 (+1), 41-10 (+1), 41-11 (+2), this story (+1) and every other Epic 41 producer;
  `Findings.cs`'s `ValidateWithContext` override (this story) versus `Design.cs`'s (41-10) — the same
  conditional-rule pattern, land them consistently.
