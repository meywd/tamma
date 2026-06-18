# Story 32-8 — Outcome Capture & Bug Taxonomy at Review/Gate (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Tests run via `sg docker -c "dotnet test ..."` for docker-bound suites
> (the session docker group is stale; build itself needs no wrapper).

**Goal:** Attribute development outcomes back to the agent(s) that produced the work. Capture
per-run/per-task outcome (`success | failure | partial`) and iteration-to-done count, and classify
defects found at review + quality gates into a fixed taxonomy (`visual | functional | regression |
security | performance | style`, catch-all `other`). Outcomes and defect records are **tenant-scoped
DCB events** tagged with the originating `agentId` + config version, so 32-10 (leaderboards) and
32-11 (learning) can score agents on real downstream quality — not just token/latency.

**Story file:** `docs/stories/epic-32/story-32-8/32-8-outcome-capture-and-bug-taxonomy-at-review-gate.md`
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§Tracking, §Ownership)

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (Elsa engine + control-plane API).
C# only — the TypeScript `packages/api` is **deleted**; do not reference it. Tests live in
`apps/tamma-elsa/tests/Tamma.Core.Tests/`, `Tamma.Api.Tests/`, `Tamma.Activities.Tests/` (xUnit).

---

## Non-goals (YAGNI guard)

- NO leaderboards / projections / aggregation — that is **32-10**. This story only *emits* the raw
  outcome + defect events (and the idempotent record store behind them).
- NO learning persistence / RAG feed — that is **32-11**. We align outcome wire strings to
  `knowledge.ts` so 32-11 consumes them with no translation, but we do not write `LearningCapture`.
- NO cost/latency/token emission — that is **32-9** (the other half of the performance dataset).
- NO new retry/iteration loop construct. `iterationsToDone` is *derived* by reading existing
  Epic-13 retry state (Testing `EvaluateResults→CommitFix→TriggerCI` + Review `WaitForFixes→
  ReRequestReview`). Adding a loop is out of scope and would duplicate Epic 13.
- NO new reviewer UI or webhook schema redesign — we read the categories the existing review webhook
  already carries (`ReviewResult.Comments` + an optional reviewer `category` field).
- NO platform-admin visibility into tenant outcome/defect data — by design (§Ownership) it is
  ALWAYS tenant-scoped; admin reads none of it.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Seam | File (verified) | State today |
|---|---|---|
| DCB event row | `src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId`, `IssueNumber`, `Tags`/`Metadata`/`Data` (JSONB strings), `SequenceNumber` (BIGSERIAL total order). |
| Durable append + tenant query | `src/Tamma.Data/Repositories/IEventRepository.cs` | `AppendAsync`, `QueryAsync(tenantId,...)`, `ListByTenantAsync`, `GetLastByTypeAsync(tenantId,type)` — tenant-scoped; ideal for query-before-append dedup. |
| Activity event accumulation | `src/Tamma.Activities/Core/TammaActivity.cs` | `TammaEventEmitter` writes `TammaEvent`s to **transient** `WorkflowExecutionContext.TransientProperties["tamma:events"]` + logs. **Not durable** — a central flush appends them as `DomainEvent`s. |
| Central flush precedent | `src/Tamma.Api/Endpoints/AgentEndpoints.cs` (~line 93) | `await events.AppendAsync(new DomainEvent { TenantId = tenantContext.TenantId, ... })` — the exact append-with-tenant pattern `AgentOutcomeService` follows. |
| Review hook | `src/Tamma.Activities/Review/MonitorReviewActivity.cs` | `OnReviewReceivedAsync` parses `ReviewResult` (Approved/ChangesRequested/TimedOut) + `ReviewCommentDetail` (FilePath/Body/Author). Bookmark `AutoBurn=true` (dedups the bookmark, NOT the defect rows). |
| Gate hook | `src/Tamma.Activities/Testing/GenerateQualityReportActivity.cs` | Builds `QualityReport.AllIssues: List<QualityIssue>` with `QualityIssueCategory` (Coverage/Test/Lint/Security/Build) + `QualityIssueSeverity`. `EvaluateResultsActivity.cs` routes AllPass/MinorIssues/MajorIssues/Critical. |
| Retry loop (iterations) | Testing `EvaluateResults→CommitFix→TriggerCI→WaitForCIResults`; Review `WaitForFixes→ReRequestReview` | `CIResultsPayload.RunId` + `ConsecutivePassCount` track Testing attempts; review re-request count tracks review attempts. No central iteration counter — must read off run state at the settling gate. |
| Taxonomy enum precedent | `src/Tamma.Core/Agents/{AgentRole,AgentAction,EnumWire}.cs` | `[Wire("...")]` attribute + `EnumWire<TEnum>` bidirectional frozen map; static-ctor validates distinct wires + exactly-one `[Wire]`; case-sensitive parse. Namespace kept `Tamma.Api.Services.Agents` (Story 27-19) though the type lives in `Tamma.Core`. |
| Outcome vocab precedent | `packages/shared/src/types/knowledge.ts` | `LearningCapture.outcome: 'success' | 'failure' | 'partial'` already exists — align `AgentOutcomeKind` wires to it. |
| Redaction | `src/Tamma.Core/Redaction/CredentialRedactor.cs` | Redact defect `message` before persistence. |
| Fire-and-forget precedent | `IMissingConfigRecorder` (missing-config plan) | Never-throw recorder contract — `AgentOutcomeService` mirrors it. |
| Sibling deps | `docs/stories/epic-32/story-32-6` (action trail), `32-7` (panels) | **Dirs exist, empty** — authored in the same wave. 32-6 establishes `agent_id`+version tagging in the tenant store; 32-7 the panel review verdict. Coordinate tag names before merge. |

**Key gap this story closes:** review/gate defects and run outcomes today exist only as transient
`tamma:events` + logs (or not at all). There is no durable, tenant-scoped, agent-attributed,
classified quality signal. `AgentOutcomeService` is the new durable seam; the activities are thin
capture hooks over it.

---

## Architecture

**classify → record (idempotent, tenant-scoped) → DCB event → (consumed by 32-10/32-11)**

1. **`BugCategory` + `AgentOutcomeKind`** enums in `Tamma.Core` (`[Wire]`/`EnumWire`). `BugCategory`
   adds a non-throwing `Classify(string?)` → `Other` + WARN (the one deliberate divergence from
   `AgentRole.Parse` which throws). `AgentOutcomeKind` wires align to `knowledge.ts`
   (`failure` not `fail`; `fail` accepted as an alias).
2. **`IAgentOutcomeService`** (new, `Tamma.Api/Services/Agents/`) — the single write-side seam.
   `RecordOutcomeAsync` (idempotent per `(runId, gateId)`) and `RecordDefectAsync` (idempotent per
   `(runId, defectKey)`). Both **never throw to the caller** (try/catch → WARN). Appends
   `DomainEvent`s via `IEventRepository.AppendAsync` with `TenantId` set — structural isolation.
3. **DCB events:** `AGENT.OUTCOME.RECORDED` (tags: `agentId`, `agentVersion`, `role`, `phase`,
   `outcome`, `runId`; data: `iterationsToDone`, `gateId`, `prNumber`) and `AGENT.DEFECT.RECORDED`
   (tags: `agentId`, `agentVersion`, `role`, `phase`, `category`, `source` review|gate|human,
   `runId`; data: `defectKey`, `filePath`, redacted `message`, `severity`, `rawCategory`).
4. **Idempotency:** query-before-append against the tenant event store (`GetLastByType`-style /
   `QueryAsync` filtered by `runId`+`gateId|defectKey`) PLUS a belt-and-suspenders partial unique
   index on the tenant `domain_events` projection of `(tenant_id, type, (tags->>'runId'),
   (data->>'defectKey'))`. `defectKey` = stable hash of `(runId, filePath, normalizedMessage,
   category, source)`.
5. **Capture hooks (thin):** `MonitorReviewActivity` (review + human defects), `GenerateQuality
   ReportActivity` (gate defects via `QualityIssueCategory→BugCategory`), `EvaluateResultsActivity`
   (settle outcome + `iterationsToDone`). 32-7 panel verdict capture is guarded on 32-7 presence.

### Per-mode ownership (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns outcome/defect data? | sole user (single tenant / `TenantId` null) | the originating tenant — ALWAYS tenant-scoped (§Ownership) |
| Platform admin read? | n/a | **never** — admin owns public agent *definitions*, reads no tenant performance data |
| Where stored? | the single tenant store | originating tenant `t_<hex>` event stream; `AgentOutcomeService` writes `TenantId` so per-tenant fan-out (28-1) only touches routing |
| agentId trust | from run context | from run context (pinned at start by 32-5/32-6) — NEVER from a reviewer payload |

---

## Task breakdown

### T1: Taxonomy + outcome enums (Tamma.Core) — core, no deps

**Scope:** `BugCategory` + `AgentOutcomeKind` enums + extensions; no service/activity wiring yet.

**Files:**
- New: `src/Tamma.Core/Agents/BugCategory.cs` (enum + `BugCategoryExtensions.ToWire`/`Classify`),
  `src/Tamma.Core/Agents/AgentOutcomeKind.cs` (enum + `ToWire`/`Parse` accepting `fail`/`failure`).
  Namespace `Tamma.Api.Services.Agents` to match `AgentRole`/`AgentAction` (Story 27-19 convention).

**Tests (first):** `tests/Tamma.Core.Tests/Agents/BugCategoryTests.cs`,
`AgentOutcomeKindTests.cs` — every member round-trips `EnumWire`; distinct wires; `Classify("xyz")`
→ `Other` (assert WARN at call sites, not the helper); `Classify(null/"")` → `Other`;
`AgentOutcomeKind` parses both `fail` and `failure`.

**Acceptance:**
- [ ] All taxonomy + outcome members round-trip; `EnumWire` static ctor validates distinctness.
- [ ] `Classify` is total (never throws) and maps unknown → `Other`.
- [ ] `dotnet build` clean; `Tamma.Core.Tests` green.

### T2: `AgentOutcomeService` + event schema + idempotency — depends T1

**Scope:** the durable seam. Record types, event-type constants, append-with-tenant, dedup,
never-throw contract, redaction. No activity wiring yet.

**Files:**
- New: `src/Tamma.Api/Services/Agents/IAgentOutcomeService.cs`, `AgentOutcomeService.cs`,
  `AgentOutcome.cs`/`AgentDefect.cs` (record types), `AgentOutcomeEventTypes.cs`
  (`AGENT.OUTCOME.RECORDED`, `AGENT.DEFECT.RECORDED`).
- New: idempotency migration under `src/Tamma.Data/Migrations/...` (additive partial unique index
  on the tenant `domain_events` projection of `(tenant_id, type, (tags->>'runId'),
  (data->>'defectKey'))` for `AGENT.DEFECT.RECORDED`, and `(... gateId)` for outcomes). Run
  `dotnet ef migrations add` then verify `has-pending-model-changes` reports none.
- Modify: `src/Tamma.Api/Program.cs` — register `IAgentOutcomeService` (mirror existing agent-service
  registration).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentOutcomeServiceTests.cs` — first record appends
exactly one event with correct tags/data; duplicate `(runId,gateId)` / `(runId,defectKey)` → no
second event (query-before-append + unique-index catch); recorder swallows a DB fault (WARN, no
throw); `message` redacted via `CredentialRedactor`; `rawCategory` preserved when classify→Other;
events carry `TenantId`.

**Acceptance:**
- [ ] One outcome event per `(runId,gateId)`; one defect event per `(runId,defectKey)` under concurrency.
- [ ] `RecordOutcomeAsync`/`RecordDefectAsync` never propagate an exception to the caller.
- [ ] Migration applies + rolls back cleanly; `has-pending-model-changes` → none.

### T3: Gate capture — defects + outcome + iterationsToDone — depends T1, T2

**Scope:** map `QualityReport.AllIssues` → `BugCategory`, emit gate defects; derive
`iterationsToDone` from Testing+Review loops; settle the outcome at the gate.

**Files:**
- Modify: `src/Tamma.Activities/Testing/GenerateQualityReportActivity.cs` — after building
  `AllIssues`, classify each `QualityIssueCategory` → `BugCategory`
  (Coverage/Test/Build→`functional`, Security→`security`, Lint→`style`; regression/performance via
  message heuristics) and call `RecordDefectAsync(source: gate)`.
- Modify: `src/Tamma.Activities/Testing/EvaluateResultsActivity.cs` — at the settling routing
  (AllPass = success; Critical/exhausted = failure; MajorIssues mid-loop = no terminal yet),
  read the attempt count off run state and `RecordOutcomeAsync(outcome, iterationsToDone)`.
- The activities take an injected `IAgentOutcomeService?` (null-tolerant so existing activity unit
  tests don't all need rework; a new test asserts it IS wired in Program.cs).

**Tests (first):** `tests/Tamma.Activities.Tests/Testing/QualityGateOutcomeCaptureTests.cs` —
QualityReport with Coverage+Security+Lint+Build issues emits 4 defects with mapped categories;
3-attempt loop (fail→fail→pass) → `iterationsToDone==3`, outcome `success`; max-iterations →
`failure`; zero-defect pass → success + no defect events; unknown heuristic → `functional` default.

**Acceptance:**
- [ ] Every `QualityIssue` produces exactly one classified defect event (gate source).
- [ ] `iterationsToDone` matches the attempt count to the passing gate; failure path counts attempts consumed.
- [ ] No change to gate routing outcomes / scoring.

### T4: Review capture — defects + human feedback — depends T1, T2

**Scope:** classify review comments + reviewer-supplied categories; emit review/human defects.

**Files:**
- Modify: `src/Tamma.Activities/Review/MonitorReviewActivity.cs` — in `OnReviewReceivedAsync`, for
  each `ReviewCommentDetail` classify `category` (reviewer field) or infer, call
  `RecordDefectAsync(source: review)`; an explicit human-flagged category → `source: human`.
  Injected `IAgentOutcomeService?` (null-tolerant).
- (Guarded) 32-7 panel aggregation review verdict → defects, behind a 32-7-present check.

**Tests (first):** `tests/Tamma.Activities.Tests/Review/MonitorReviewDefectCaptureTests.cs` —
ChangesRequested with N comments → N classified defect events; reviewer `category="visual"` →
`Visual`; unknown reviewer category → `Other` + WARN + `rawCategory` in data; Approved with no
comments → no defect events; TimedOut → no defect events; replayed webhook → defect dedup holds.

**Acceptance:**
- [ ] Each review comment becomes one classified defect (review/human source).
- [ ] Unknown reviewer category → `other` (never throws, never aborts review).
- [ ] No change to review outcomes (Approved/ChangesRequested/TimedOut).

### T5: Tenant isolation + end-to-end tests — depends T2–T4

**Scope:** prove cross-tenant invisibility and the full capture path; docker-bound.

**Files:** `tests/Tamma.Api.Tests/Agents/AgentOutcomeIsolationTests.cs` (+ reuse T2-T4 suites).

**Tests (first, docker-bound — `sg docker -c "dotnet test ..."`):** seed outcome+defect events for
tenant A; tenant B's `ListByTenantAsync(B,...)` returns none of A's; a platform-admin path reads
none of A's outcome/defect data; full path: run a simulated 2-iteration workflow with 3 gate defects
+ 2 review comments → 1 `AGENT.OUTCOME.RECORDED` (iterations=2) + 5 `AGENT.DEFECT.RECORDED`, all
tagged `agentId`+version, all `TenantId`=A.

**Acceptance:**
- [ ] Tenant A data invisible to B and to platform admin (structural + query tests).
- [ ] End-to-end run produces exactly the expected event counts (idempotent on re-run).
- [ ] Full suite green (`sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"`).

---

## Task order & dependencies

T1 → T2 → (T3 ∥ T4) → T5. T1 is the only hard prerequisite for everything; T3 and T4 are
parallel-safe once T2 lands; T5 closes the loop. 32-6/32-7 should land (or at least pin their
`agent_id`+version tag names) before T3/T4 merge — coordinate tag spelling.

## Risks

- **Cross-tenant leak (highest):** the whole value prop is per-tenant private datasets. Mitigation:
  `DomainEvent.TenantId` + per-tenant connection/role; `AgentOutcomeService` reads tenantId+agentId
  from run context, NEVER from a reviewer payload; explicit isolation tests in T5. Get this wrong and
  one tenant scores another's agents.
- **Double-counting on retry/replay:** hot retry loops + replayed webhooks re-surface the same
  defect. Mitigation: `(runId,gateId)`/`(runId,defectKey)` query-before-append + partial unique
  index. `defectKey` hash must be stable across attempts (normalize message, exclude volatile fields).
- **Capture aborting a passing workflow:** a DB blip on the capture path must not fail a gate.
  Mitigation: never-throw recorder (try/catch→WARN), matching `IMissingConfigRecorder`. The
  contract is load-bearing — assert it in T2.
- **iterationsToDone derivation drift:** Testing and Review loops count attempts differently;
  attribution must be the code/design-producing agent, not the reviewer. Mitigation: read the final
  attempt index off run state at the settling gate; pin attribution to the run-start `agentId`.
  T3 tests pin the count semantics.
- **32-6/32-7 tag-name mismatch:** if this story tags `agentId` but 32-6 tags `agent_id`, 32-10
  joins break. Mitigation: read the sibling stories before T3/T4; use the exact tag spelling 32-6
  establishes (the design spec writes `agent_id`).
- **Migration discipline:** the idempotency index is additive, but still verify
  `has-pending-model-changes` reports none and mirror any entity config in the established single
  source; a JSONB-expression unique index needs a raw-SQL migration step (EF won't infer it).
- **`fail` vs `failure` wire drift:** the spec says `fail`, `knowledge.ts` says `failure`. We persist
  `failure` and accept `fail` as an alias — document it so 32-11 isn't surprised. Pinned in T1 tests.

## Definition of done

- [ ] All five tasks' acceptance boxes checked; story ACs 1-10 satisfied.
- [ ] `BugCategory`/`AgentOutcomeKind` validated via `EnumWire`; unknown→`other` non-throwing.
- [ ] `AGENT.OUTCOME.RECORDED` + `AGENT.DEFECT.RECORDED` emitted, tenant-scoped, `agentId`+version
      tagged, idempotent per `(run, gate/defect)`.
- [ ] Tenant isolation proven; capture never aborts a workflow.
- [ ] Full `dotnet test` suite green (docker-bound via `sg docker -c`); no new lint/build warnings.
- [ ] No reference to the deleted `packages/api`; all touched code in `apps/tamma-elsa`.
