# Story 32-8: Outcome Capture & Bug Taxonomy at Review/Gate

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../../BEFORE_YOU_CODE.md)

This story targets the C# **`apps/tamma-elsa`** stack (Tamma.Activities + Tamma.Api/Tamma.ElsaServer + Tamma.Core + Tamma.Data). The TypeScript `packages/api` is **deleted** — do not reference it. DCB event sourcing, prompt/convention stores, agent registry, and provisioning all live in `apps/tamma-elsa`.

## User Story

As a **tenant evaluating which agents to trust with design and review work**,
I want every workflow run/task to record its **outcome** (`success` | `fail` | `partial`), the **number of iterations it took to reach a passing gate**, and a **classified bug record** for each defect found at review and quality-gate steps (`visual` | `functional` | `regression` | `security` | `performance` | `style`), all attributed to the **originating agent (id + config version)** and stored only in my tenant's data,
So that the benchmarking (32-10) and learning (32-11) stories can score agents on real downstream quality — not just token/latency — and I can compare reviewer A vs reviewer B on the defects they actually catch and the bugs their code actually ships.

## Priority

P1 — the quality-signal half of the agent performance dataset. 32-9 supplies cost/latency; this story supplies outcome + defect quality. Both feed 32-10 leaderboards and 32-11 learning.

## Acceptance Criteria

1. A versioned, tenant-scoped **`AgentOutcome`** record links a workflow run / task to its originating `agentId` + config `version` with an outcome enum `{success, fail, partial}` and an `iterationsToDone` count. The record lives only in the tenant's data (tenant `t_<hex>` schema / `DomainEvent.TenantId` scoping) and is never readable across tenants or by platform admins for another tenant.
2. A fixed **bug taxonomy enum** `{visual, functional, regression, security, performance, style}` is defined in `Tamma.Core` using the established `[Wire]` / `EnumWire<TEnum>` pattern (mirrors `AgentRole`/`AgentAction`). Free-text categories are rejected on the wire; an unknown/unmappable category maps to a catch-all `other` and logs a WARN (it does NOT throw, so a malformed reviewer payload never aborts a workflow).
3. Review steps (`Tamma.Activities/Review/MonitorReviewActivity` and the review-panel aggregation from 32-7) classify each found defect into the taxonomy and emit one **`AGENT.DEFECT.RECORDED`** DCB event per defect, tagged with `category`, `agentId`, `agentVersion`, `role`, `phase`, `source: "review"`, `issueId`, and `prNumber`.
4. Quality-gate steps (`Tamma.Activities/Testing/GenerateQualityReportActivity` and `EvaluateResultsActivity`) map each `QualityIssue` in the report to the taxonomy (Coverage/Test→`functional`, Security→`security`, Lint→`style`, Build→`functional`, plus heuristics for regression/performance) and emit `AGENT.DEFECT.RECORDED` events tagged `source: "gate"` with the same agent attribution.
5. Human review feedback (the `ReviewResult.Comments` carried by `MonitorReviewActivity` and reviewer-supplied categories on the review webhook) is captured as `AGENT.DEFECT.RECORDED` events tagged `source: "human"`, so manually-flagged defects join the same dataset.
6. `iterationsToDone` is **derived** from the existing debug/CI retry loops (Epic 13 TDD/CI debug-retry; the `EvaluateResults → CommitFix → TriggerCI` cycle and the `WaitForFixes → ReRequestReview` review loop) by counting attempts until a passing gate / approval, attributed to the originating agent. No new loop construct is introduced — the count is read off existing per-run iteration state.
7. On run/task completion, exactly one of **`AGENT.OUTCOME.RECORDED`** (carrying `outcome` and `iterationsToDone`) is emitted, tagged with `agentId` + `agentVersion`. (The spec's `AGENT.TASK.SUCCEEDED/FAILED/PARTIAL` family from 32-6 is reused as the outcome-typed alias; this story adds `iterationsToDone` and the `bugType`-classified defect events on top of that trail rather than inventing a parallel event family.)
8. Capture hooks are **non-blocking and idempotent per `(runId, gateId)`** (and per `(runId, defectKey)` for defects): a re-run, a retried gate, or a replayed review webhook does NOT double-count outcomes or duplicate defect events. Capture failures never abort the workflow (fire-and-forget-safe, matching the `IMissingConfigRecorder` precedent).
9. A taxonomy-validation unit suite proves: every taxonomy member round-trips through `EnumWire`; unknown wire strings map to `other` with a WARN (not a throw); `[Wire]` strings are distinct.
10. Tests cover: outcome derivation across a multi-iteration retry loop (iterationsToDone = attempts-to-pass), taxonomy classification at both review and gate, the unknown→`other` fallback, idempotency on re-run/replay, and **tenant isolation** (tenant A's outcome/defect rows are invisible to tenant B and to a platform admin).

## Technical Design

### Where this fits (verified against repo @ main 98cfb1c2, 2026-06-17)

| Component | File (verified) | Role in this story |
|---|---|---|
| DCB event entity | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId`, `IssueNumber`, `Tags`(JSONB), `Metadata`(JSONB), `Data`(JSONB), `SequenceNumber`. The outcome/defect events are `DomainEvent` rows. |
| Durable event seam | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` (`AppendAsync`, tenant-scoped `QueryAsync`/`ListByTenantAsync`) | Persists events with `TenantId` set — structural tenant isolation. |
| Activity event accumulation | `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` (`TammaEventEmitter` → transient `tamma:events`) | Activities accumulate `TammaEvent`s; a central flush appends them as `DomainEvent`s (the `AgentEndpoints`/engine pattern). **Activity-side `tamma:events` are transient** — durability comes from the central `AgentOutcomeService` flush, NOT the activity. |
| Review activity | `apps/tamma-elsa/src/Tamma.Activities/Review/MonitorReviewActivity.cs` (carries `ReviewResult` + `ReviewCommentDetail`) | Defect-capture hook point at review. |
| Gate activities | `apps/tamma-elsa/src/Tamma.Activities/Testing/GenerateQualityReportActivity.cs` (produces `QualityReport.AllIssues: List<QualityIssue>`), `EvaluateResultsActivity.cs` (AllPass/Minor/Major/Critical routing) | Defect-capture + outcome hook points at gate. |
| Retry loop | `EvaluateResults → CommitFix → TriggerCI → WaitForCIResults` (Testing) + `WaitForFixes → ReRequestReview` (Review) | Source of `iterationsToDone`. |
| Taxonomy enum precedent | `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs`, `AgentAction.cs`, `EnumWire.cs` | `[Wire]`/`EnumWire<TEnum>` is the canonical validated-enum pattern — `BugCategory` follows it exactly. |
| Outcome enum precedent | `packages/shared/src/types/knowledge.ts` (`LearningCapture.outcome: 'success' | 'failure' | 'partial'`) | The TS learning layer already uses this triple; `AgentOutcomeKind` aligns wire strings to it. |
| Action-trail base (32-6) | `docs/stories/epic-32/story-32-6` (sibling, in flight) | Establishes `AGENT.TASK.*` events tagged `agent_id` + version in the tenant store — this story extends that trail. |
| Panels (32-7) | `docs/stories/epic-32/story-32-7` (sibling, in flight) | `RunAgentPanelActivity`/`AggregatePanelActivity` review verdicts are an additional defect source. |

> **NOTE — NEW files** are marked NEW in the Files table. `AgentOutcomeService.cs` does **not** exist yet (the spec's path is aspirational); it is created by this story. All other cited C# files exist.

### Taxonomy enum (Tamma.Core)

`BugCategory` follows the verified `[Wire]`/`EnumWire<TEnum>` pattern so persisted wire strings are decoupled from C# identifiers and case-sensitively validated. Unlike `AgentRole.Parse` (which throws on unknown), the bug-classification path must be **non-fatal** — a bad reviewer payload cannot abort a run — so a `Classify(string)` helper maps unknown → `Other` + WARN.

```csharp
// apps/tamma-elsa/src/Tamma.Core/Agents/BugCategory.cs  (NEW)
// Namespace kept as Tamma.Api.Services.Agents to match AgentRole/AgentAction
// (Story 27-19 convention — taxonomy lives in Tamma.Core, namespace preserved).
namespace Tamma.Api.Services.Agents;

/// <summary>
/// Fixed defect taxonomy recorded at review + quality gates (SPEC §Tracking).
/// Wire strings are the stable DCB contract. <see cref="Other"/> is the
/// catch-all for unknown/unmappable categories — never thrown, always logged.
/// </summary>
public enum BugCategory
{
    [Wire("visual")]      Visual,
    [Wire("functional")]  Functional,
    [Wire("regression")]  Regression,
    [Wire("security")]    Security,
    [Wire("performance")] Performance,
    [Wire("style")]       Style,
    [Wire("other")]       Other,   // catch-all — unknown categories map here
}

public static class BugCategoryExtensions
{
    public static string ToWire(this BugCategory c) => EnumWire<BugCategory>.ToWire(c);

    /// <summary>
    /// Non-throwing classification: returns the matching category, or
    /// <see cref="BugCategory.Other"/> for null/empty/unknown input. Callers
    /// MUST log a WARN with the offending raw string when Other is returned
    /// for a non-empty input (AC2). Never throws — a malformed reviewer
    /// payload must not abort a workflow.
    /// </summary>
    public static BugCategory Classify(string? raw)
        => raw is not null && EnumWire<BugCategory>.TryParse(raw.Trim(), out var c) ? c : BugCategory.Other;
}
```

### Outcome kind (Tamma.Core)

```csharp
// apps/tamma-elsa/src/Tamma.Core/Agents/AgentOutcomeKind.cs  (NEW)
namespace Tamma.Api.Services.Agents;

/// <summary>
/// Run/task outcome. Wire strings align with the TS learning layer
/// (knowledge.ts LearningCapture.outcome: success|failure|partial) so the
/// learning loop (32-11) consumes one vocabulary.
/// </summary>
public enum AgentOutcomeKind
{
    [Wire("success")] Success,
    [Wire("failure")] Failure,   // wire = "failure" (matches knowledge.ts), spec's "fail" is the short alias
    [Wire("partial")] Partial,
}
```

> **Wire alignment decision:** the spec writes `fail`; `knowledge.ts` (the consuming learning layer) already uses `failure`. We persist `failure` as the wire string to avoid a translation seam at the learning boundary, and document `fail` as the human-facing short alias. `Classify`-style parsing accepts both.

### Event schema (DCB)

All events are `DomainEvent` rows appended via `IEventRepository.AppendAsync` with `TenantId` set (structural isolation). Pattern `AGGREGATE.ACTION.STATUS`.

**`AGENT.OUTCOME.RECORDED`** — one per completed run/task:
```jsonc
{
  "type": "AGENT.OUTCOME.RECORDED",
  "tenantId": "<tenant-guid>",
  "issueNumber": 482,
  "tags": {
    "agentId": "<agent-guid>", "agentVersion": "7",
    "role": "reviewer", "phase": "review",
    "outcome": "success",            // success | failure | partial
    "runId": "<workflow-instance-id>"
  },
  "data": {
    "iterationsToDone": 3,           // attempts until passing gate / approval
    "outcomeKind": "success",
    "gateId": "quality-report",      // the gate that settled the outcome
    "prNumber": 311
  },
  "metadata": { "workflowVersion": "1.0.0", "eventSource": "system" }
}
```

**`AGENT.DEFECT.RECORDED`** — one per classified defect (review, gate, or human):
```jsonc
{
  "type": "AGENT.DEFECT.RECORDED",
  "tenantId": "<tenant-guid>",
  "issueNumber": 482,
  "tags": {
    "agentId": "<agent-guid>", "agentVersion": "7",
    "role": "reviewer", "phase": "review",
    "category": "functional",        // BugCategory wire string (or "other")
    "source": "review",              // review | gate | human
    "runId": "<workflow-instance-id>"
  },
  "data": {
    "defectKey": "<stable hash for idempotency>",
    "filePath": "src/foo.ts", "message": "<redacted summary>",
    "severity": "error", "rawCategory": "<original string if mapped to other>"
  },
  "metadata": { "workflowVersion": "1.0.0", "eventSource": "system" }
}
```

> `AGENT.TASK.SUCCEEDED/FAILED/PARTIAL` (32-6 action trail) remain the per-task lifecycle events; `AGENT.OUTCOME.RECORDED` is the **scored** projection carrying `iterationsToDone`. The `AgentOutcomeService` emits the outcome event in the same flush as the corresponding 32-6 task event so the two never disagree.

### AgentOutcomeService (the central durable seam)

Activities accumulate defect/outcome intents in the transient `tamma:events` bag (per `TammaEventEmitter`); the **`AgentOutcomeService`** in `Tamma.Api` is the single write-side seam that flushes them to the DCB store with tenant scoping, dedup, and agent attribution — exactly the pattern `AgentEndpoints` uses today (`IEventRepository events ... await events.AppendAsync(new DomainEvent { TenantId = ... })`).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentOutcomeService.cs  (NEW)
public interface IAgentOutcomeService
{
    /// Records the terminal outcome for a run/task. Idempotent per (runId, gateId).
    Task RecordOutcomeAsync(AgentOutcome outcome, CancellationToken ct = default);

    /// Records one classified defect. Idempotent per (runId, defectKey).
    Task RecordDefectAsync(AgentDefect defect, CancellationToken ct = default);
}

// AgentOutcome: tenantId, agentId, agentVersion, role, phase, runId, gateId,
//               outcome (AgentOutcomeKind), iterationsToDone, issueNumber, prNumber
// AgentDefect:  tenantId, agentId, agentVersion, role, phase, runId, defectKey,
//               category (BugCategory), source (review|gate|human), filePath,
//               message (redacted via Tamma.Core CredentialRedactor), severity, rawCategory
```

**Idempotency:** dedup is enforced by querying the tenant event store for an existing `AGENT.OUTCOME.RECORDED` with the same `(runId, gateId)` tag (resp. `AGENT.DEFECT.RECORDED` with the same `(runId, defectKey)`) before append. `defectKey` = stable hash of `(runId, filePath, normalizedMessage, category, source)` so the same defect surfaced twice in a retry loop collapses to one row. Belt-and-suspenders: a partial unique index on the tenant `domain_events` projection of `(tenant_id, type, (tags->>'runId'), (data->>'defectKey'))`.

**Non-blocking:** `RecordOutcomeAsync`/`RecordDefectAsync` never throw to the caller (try/catch → WARN), matching the `IMissingConfigRecorder` fire-and-forget contract — a CP/tenant DB blip must not turn a passing gate into a workflow abort.

### iterationsToDone derivation

No new loop. The Testing retry loop (`EvaluateResults → CommitFix → TriggerCI → WaitForCIResults`) and the Review loop (`WaitForFixes → ReRequestReview`) already advance a per-run attempt counter (the `ConsecutivePassCount` / CI run sequence on `CIResultsPayload.RunId`, and the review re-request count). `AgentOutcomeService` reads the final attempt index off the run state at the settling gate:
- **success:** `iterationsToDone` = number of attempts up to and including the passing attempt.
- **failure/partial:** `iterationsToDone` = attempts consumed before the terminal (max-iteration / escalation / timeout) state.
Attribution: the agent that produced the code/design under iteration (the originating `agentId`+version pinned at run start, carried on the run context), not the reviewer.

### Per-mode / per-tenant ownership (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the outcome/defect data? | The sole user — their instance, their dataset (`TenantId` null / single tenant). | The tenant that ran the agent. ALWAYS tenant-scoped; one agent definition → many independent per-tenant datasets (design spec §Ownership). |
| Can a platform admin read it? | N/A (sole user). | **No.** Platform admin owns *public agent definitions* but reads **no** tenant's performance/defect data — enforced structurally by `DomainEvent.TenantId` + per-tenant connection/role. |
| Where do events land? | The single tenant store. | The originating tenant's `t_<hex>` schema event stream (per-tenant fan-out per Story 28-1 trajectory; `AgentOutcomeService` writes with `TenantId` set so the migration only touches routing). |

## Tasks / Subtasks

- [ ] Task 1: Taxonomy + outcome enums in Tamma.Core (AC: 2, 9)
  - [ ] Subtask 1.1: Add `BugCategory` enum + `BugCategoryExtensions.Classify` (non-throwing → `Other` + WARN) using `[Wire]`/`EnumWire`
  - [ ] Subtask 1.2: Add `AgentOutcomeKind` enum (`success`/`failure`/`partial`, wire-aligned to knowledge.ts)
  - [ ] Subtask 1.3: Unit tests — round-trip, distinct wires, unknown→Other, `fail`/`failure` alias parsing
- [ ] Task 2: `AgentOutcomeService` + event schema (AC: 1, 7, 8)
  - [ ] Subtask 2.1: `IAgentOutcomeService` + `AgentOutcome`/`AgentDefect` records, DI wiring in Program.cs
  - [ ] Subtask 2.2: `AGENT.OUTCOME.RECORDED` / `AGENT.DEFECT.RECORDED` event types + tag/data builders (agentId+version, tenant-scoped)
  - [ ] Subtask 2.3: Idempotency (query-before-append on `(runId, gateId)` / `(runId, defectKey)`) + partial unique index migration
  - [ ] Subtask 2.4: Non-throwing contract (try/catch → WARN); message redaction via `CredentialRedactor`
- [ ] Task 3: Defect capture at review (AC: 3, 5)
  - [ ] Subtask 3.1: Hook `MonitorReviewActivity` resume path — classify `ReviewResult.Comments` + reviewer category → `RecordDefectAsync(source: review|human)`
  - [ ] Subtask 3.2: Hook the 32-7 panel aggregation review verdict (guarded on 32-7 presence)
- [ ] Task 4: Defect + outcome capture at gate (AC: 4, 6, 7)
  - [ ] Subtask 4.1: Map `QualityReport.AllIssues` (`QualityIssueCategory`) → `BugCategory` and emit `RecordDefectAsync(source: gate)` from `GenerateQualityReportActivity`
  - [ ] Subtask 4.2: Derive `iterationsToDone` from the Testing + Review retry loops; emit `RecordOutcomeAsync` at the settling gate/approval
- [ ] Task 5: Tests (AC: 10)
  - [ ] Subtask 5.1: Outcome derivation across a 3-iteration retry loop
  - [ ] Subtask 5.2: Classification at review + gate; unknown→other WARN
  - [ ] Subtask 5.3: Idempotency on re-run/replayed webhook
  - [ ] Subtask 5.4: Tenant isolation (A invisible to B + platform admin)

## Dependencies

- **Prerequisite — Story 32-6** (Agent action trail: `AGENT.TASK.*` events tagged `agent_id`+version in the tenant store). This story extends that trail with outcome scoring + defect classification; it reuses 32-6's agent-attribution tagging and tenant-event-store wiring.
- **Prerequisite — Story 32-7** (Multi-agent design/review panels): the panel review verdict is an additional defect source (AC3). Gracefully degrades if a deployment runs panels off — single-reviewer `MonitorReviewActivity` capture (AC3/AC5) is independent of 32-7.
- **Prerequisite — Epic 4** (DCB event sourcing): `DomainEvent` + `IEventRepository` tenant-scoped append/query are the storage substrate.
- **Related — Epic 13** (TDD/CI debug-retry loops): source of `iterationsToDone`; this story reads existing iteration state, adds no loop.
- **Related — Epic 3** (quality gates): `GenerateQualityReportActivity`/`EvaluateResultsActivity` are the gate-side capture points.
- **Consumed by — Story 32-10** (benchmark projections/leaderboards: success rate, avg iterations-to-done, bug counts by type) and **Story 32-11** (learning persistence — `LearningCapture.outcome` aligns to `AgentOutcomeKind`).

## Testing Strategy

1. **Enum unit tests** (`Tamma.Core.Tests`, xUnit): `BugCategory`/`AgentOutcomeKind` round-trip through `EnumWire`; distinct wire strings; `Classify("frobnicate")` → `Other` + WARN asserted; `Classify(null/"")` → `Other` silently; `fail`/`failure` both parse.
2. **Service unit tests** (`Tamma.Api.Tests/Agents/AgentOutcomeServiceTests.cs`): `RecordOutcomeAsync` appends one `AGENT.OUTCOME.RECORDED` with correct tags+`iterationsToDone`; `RecordDefectAsync` appends one `AGENT.DEFECT.RECORDED`; **idempotency** — same `(runId, gateId)` / `(runId, defectKey)` twice → one row, no second event; recorder swallows a DB fault (WARN, no throw); message redaction applied.
3. **Iteration derivation tests**: simulate a 3-attempt Testing loop (fail→fail→pass) → `iterationsToDone == 3`, outcome `success`; max-iterations-exhausted → outcome `failure` with attempts consumed; review re-request loop counted.
4. **Classification mapping tests**: `QualityReport.AllIssues` with Coverage/Security/Lint/Build issues map to `functional/security/style/functional`; review `ReviewResult.Comments` with reviewer categories classify correctly; an unknown reviewer category → `other` + WARN + `rawCategory` preserved in `data`.
5. **Tenant isolation tests** (docker-bound, `sg docker -c "dotnet test ..."`): seed outcome+defect events for tenant A; assert tenant B's `IEventRepository.ListByTenantAsync(B, ...)` returns none of A's rows and a platform-admin path cannot read A's outcome/defect data.
6. **Edge cases**: zero-defect passing run (outcome `success`, no defect events); replayed review webhook (AutoBurn already covers bookmark, but defect dedup must still hold); concurrent defect records for the same `defectKey` (unique-index catch → single row).

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Agents/BugCategory.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Core/Agents/AgentOutcomeKind.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentOutcomeService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentOutcomeService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentOutcomeEventTypes.cs` | Create (NEW — `AGENT.OUTCOME.RECORDED`, `AGENT.DEFECT.RECORDED`) |
| `apps/tamma-elsa/src/Tamma.Activities/Review/MonitorReviewActivity.cs` | Modify (defect/human capture hook on resume) |
| `apps/tamma-elsa/src/Tamma.Activities/Testing/GenerateQualityReportActivity.cs` | Modify (QualityIssue→BugCategory mapping + gate defect capture) |
| `apps/tamma-elsa/src/Tamma.Activities/Testing/EvaluateResultsActivity.cs` | Modify (settle outcome + iterationsToDone at gate) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI: register `IAgentOutcomeService`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/...` | Create (additive: partial unique index for defect/outcome idempotency on tenant event projection) |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Agents/BugCategoryTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentOutcomeServiceTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Review/MonitorReviewDefectCaptureTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Testing/QualityGateOutcomeCaptureTests.cs` | Create (NEW) |

> Final test project paths follow the repo's existing `apps/tamma-elsa/tests/*` layout; confirm exact project names against the solution before adding (mirror the nearest existing `Tamma.Activities.Tests`/`Tamma.Api.Tests` project).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (esp. DCB tagging + tenant isolation)
3. Read sibling stories 32-6 (action trail) and 32-7 (panels) — this story depends on their `agent_id`+version tagging and review-verdict shape; align tag names exactly
4. Read the design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` §Tracking
5. Planned the TDD cycle (enums + service tests first, then activity hooks)

### Key design decisions

- **`[Wire]`/`EnumWire` for the taxonomy** — reuses the verified `AgentRole`/`AgentAction` pattern: stable persisted contract, case-sensitive validation, no free-text. The one deliberate divergence is `BugCategory.Classify` is non-throwing (→ `Other` + WARN) because a malformed reviewer payload must never abort a workflow (AC2).
- **`failure` wire string, `fail` alias** — aligns with `knowledge.ts` `LearningCapture.outcome` so 32-11 consumes one vocabulary; documented so the spec's `fail` is not lost.
- **Central service is the durable seam, not the activity** — activity `tamma:events` are transient (per `TammaActivity.cs`); durability + tenant scoping + dedup live in `AgentOutcomeService` (the `AgentEndpoints` flush pattern). This also keeps capture out of the workflow's failure path.
- **iterationsToDone is derived, not stored by a new loop** — read off existing Epic-13 retry state; attributed to the code/design-producing agent, not the reviewer.
- **Idempotency is query-before-append + partial unique index** — survives re-runs, retried gates, and replayed webhooks without double-counting (AC8).

### Integration points

- **Review:** `MonitorReviewActivity.OnReviewReceivedAsync` already parses `ReviewResult` + `ReviewCommentDetail`; the capture hook classifies each comment's category (or reviewer-supplied `category` field on the webhook) and calls `RecordDefectAsync`.
- **Gate:** `GenerateQualityReportActivity` already aggregates `QualityReport.AllIssues` with `QualityIssueCategory`; map that enum to `BugCategory` and emit defects; `EvaluateResultsActivity`'s AllPass/Major/Critical routing settles the outcome.
- **Tenant context:** the originating `agentId`+version and `tenantId` are pinned on the run context at start (32-5/32-6); the service reads them — it never trusts a reviewer-supplied agentId.

### Risks and mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Cross-tenant data leak | High | `DomainEvent.TenantId` + per-tenant connection/role; explicit isolation tests (AC10); service never accepts a caller-supplied tenantId from a reviewer payload |
| Double-counting on retry/replay | Medium | `(runId, gateId)` / `(runId, defectKey)` dedup + partial unique index (AC8) |
| Capture aborting a passing workflow | High | Fire-and-forget-safe service (try/catch → WARN), never throws to the activity |
| Free-text category pollution | Medium | `EnumWire` validation + `Classify`→`Other` with `rawCategory` preserved (AC2) |
| 32-6/32-7 not yet merged | Medium | Single-reviewer + gate capture work without 32-7; tag names mirror 32-6 — coordinate before merge |

## Logging Requirements

- **INFO:** outcome recorded (`agentId`, `outcome`, `iterationsToDone`, `runId`); defect recorded (`agentId`, `category`, `source`, `runId`)
- **DEBUG:** classification decision (`rawCategory` → `BugCategory`); idempotency hit (duplicate `(runId, gateId|defectKey)` skipped); iteration count derivation
- **WARN:** unknown category mapped to `other` (include `rawCategory`); capture swallowed a DB fault (`runId`, error); reviewer-supplied agentId mismatch ignored
- **ERROR:** none on the capture path (failures are WARN — capture never blocks); reserve ERROR for the migration/idempotency-index creation path
- **Structured context:** `{ tenantId, agentId, agentVersion, runId, gateId|defectKey, category, outcome, iterationsToDone }`
- **Credential safety:** defect `message` is redacted via `Tamma.Core/Redaction/CredentialRedactor` before persistence; never log raw reviewer payloads or secrets

## Related

- Design spec: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-17-32-8-outcome-capture-and-bug-taxonomy-at-review-gate-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-6` (action trail), `docs/stories/epic-32/story-32-7` (panels)
- Consumers: Story 32-10 (leaderboards), Story 32-11 (learning → RAG)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
