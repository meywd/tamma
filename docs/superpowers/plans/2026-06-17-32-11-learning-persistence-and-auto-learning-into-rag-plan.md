# Story 32-11 — Learning Persistence & Auto-Learning into RAG (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Close the Epic 32 learning loop. Persist durable, **tenant-scoped** learnings derived
from agent run outcomes (what worked / what failed / recurring defect categories), route them
through a `PendingLearning`-style review/approval flow, ingest **approved** learnings into the RAG
knowledge base tagged by `(agentId, tenantId)`, and wire managed-agent context assembly (32-5) to
retrieve and inject those learnings so future runs of the same agent improve — with **zero
cross-tenant leakage**, even for shared public agents.

**Story file:** `docs/stories/epic-32/story-32-11/32-11-learning-persistence-and-auto-learning-into-rag.md`
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (Epic 32 §"Tracking: ... learning")

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine;
per-tenant `TenantDbContext`); TypeScript `@tamma/intelligence` RAG/KB pipeline reached through the
process-shared `@tamma/intelligence-server` sidecar via the existing C# `IIntelligenceHttpClient`.
C# tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`).

---

## Non-goals (YAGNI guard)

- **NO new TypeScript package.** The `LearningCapture` / `KnowledgeEntry` / `PendingLearning`
  vocabulary (`packages/shared/src/types/knowledge.ts`) and the `KnowledgeService`
  capture/approve flow (`packages/intelligence/src/knowledge-base/knowledge-service.ts`) already
  exist — reuse them via `IIntelligenceHttpClient`, do not fork.
- **NO `packages/api` anything.** It is deleted. Persistence and the auto-learning hook are
  C# (`apps/tamma-elsa`).
- **NO new sanitizer / event store / RAG pipeline.** Reuse `ContentSanitizer` (32-5 path), the
  tenant `IEventRepository`, and the Epic 6 RAG retriever.
- **NO change to 32-8 outcome/defect events.** This story *consumes* `AGENT.OUTCOME.RECORDED` /
  `AGENT.DEFECT.RECORDED`; it adds learnings on top, it does not re-shape the outcome trail.
- **NO unbounded auto-ingestion.** Auto-approve is opt-in (default off); everything else flows
  through the review/approval flow. Similarity dedup + per-agent rate-limit cap growth.
- **NO cross-tenant learning sharing.** A public agent's learnings are ALWAYS tenant-scoped.
  Platform admins who own a public agent see none of any tenant's learnings.

---

## Current-state findings (verified 2026-06-17, repo @ main)

| Seam | State today |
|---|---|
| `packages/shared/src/types/knowledge.ts` | **EXISTS.** Defines `LearningCapture` (`taskId`, `outcome`, `whatWorked`, `whatFailed`, `rootCause`, suggested*), `PendingLearning` (extends + `status: pending\|approved\|rejected`, review fields), `KnowledgeEntry` (`type: 'learning'`, keywords, embedding, stats), `KnowledgeQuery`/`KnowledgeResult`. **No `agentId` / `tenantId` field — tagging is this story's job (carried in tags/metadata).** |
| `packages/intelligence/src/knowledge-base/knowledge-service.ts` | **EXISTS.** `captureLearning()` → `learningCapture.captureExplicit`; `getPendingLearnings()`; `approveLearning(id, edits?)` builds a `KnowledgeEntry` (`type 'learning'`, generates embedding) + persists. `getRelevantKnowledge()` for retrieval. This is the ingest/approve target reached via the sidecar. |
| `packages/intelligence/src/rag/` | **EXISTS** — `retriever.ts`, `rag-pipeline.ts`, `ranker.ts`, `query-processor.ts`, `assembler.ts`. The retrieval path managed-agent context assembly queries. |
| `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IIntelligenceHttpClient.cs` | **EXISTS.** Typed `HttpClient` against the sidecar `/kb/*` routes: `QueryRagAsync`, `SearchVectorsAsync`, `UpsertVectorsAsync`, KB index methods. Plain HTTP — **carries no tenant scoping by itself; tenancy must be tagged on payloads (C#-side responsibility).** Degraded fallback on 5xx is built in. |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | **EXISTS.** Per-tenant `DbContext` (one per request+tenant via `ITenantDbContextFactory`). Target arch: **no `TenantId` column** on tenant tables — isolation is structural (schema-per-tenant). `OnModelCreating` → `TammaModelConfiguration.ConfigureTenantEntities`. Add `DbSet<AgentLearning>` here. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | **EXISTS.** `{ Id, Type, TenantId?, IssueNumber?, Tags(json), Metadata(json), Data(json), CreatedAt, SequenceNumber }`. Appended via `IEventRepository.AppendAsync(DomainEvent)` (`Repositories/IEventRepository.cs`). |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/` | **EXISTS:** `AgentResolverService`, `ResolvedAgentConfig`, `DefaultAgentConfig`. **`AgentLearningService` / `IAgentLearningService` / event-types / options do NOT exist — created here.** `ManagedAgent.cs` lands in **32-5** (prerequisite) and owns the context-assembly/RAG step + `ContentSanitizer` seam this story hooks. |
| 32-8 events | `AGENT.OUTCOME.RECORDED` (carries `outcome`, `iterationsToDone`; the `LearningCapture` fields ride in `data`) and `AGENT.DEFECT.RECORDED` (carries `category`), tagged `agentId`+`agentVersion`, idempotent per `(runId, gateId)` / `(runId, defectKey)`, fire-and-forget-safe (the precedent this story matches). Emitted by `AgentOutcomeService` (32-8). |

**Key gap closed by this story:** captured learnings have no durable tenant-scoped backend, no
auto-learning hook, and no scoped RAG ingestion/retrieval. We add all three on the C# stack,
reusing the existing TS vocabulary + KB/RAG pipeline via `IIntelligenceHttpClient`.

---

## Architecture

**Outcome event → derive → sanitize → dedup → persist → (approve) → ingest → retrieve → inject.**

1. **`AgentLearning` entity** (tenant-scoped, `TenantDbContext`) — the durable registry. No
   `TenantId` column (structural isolation). Unique index `(AgentId, Role, TaskPattern, Signal)`
   anchors exact-match dedup; similarity collapse updates the matching open row.
2. **`AgentLearningService`** (new C#) — the single write-side seam:
   - `RecordFromOutcomeAsync(outcome/defect)` — the **auto-learning hook**, invoked after the
     32-8 flush (never on the hot path). Derives candidate(s) by signal, sanitizes via
     `ContentSanitizer`, rate-limits + similarity-dedups, persists `pending`, emits
     `AGENT.LEARNING.CAPTURED`. Fire-and-forget-safe (fault → WARN, no throw).
   - `ApproveAsync` / `RejectAsync` — the review/approval flow. Approve flips status + ingests the
     learning into the KB via `IIntelligenceHttpClient` (KB upsert / `approveLearning` path),
     tagged `(agentId, tenantId)`, storing the returned `KnowledgeEntryId` (idempotent).
   - `RetrieveForRunAsync(agentId, tenantId, query)` — queries RAG filtered by `(agentId,
     tenantId)` for `ManagedAgent` context assembly; emits `AGENT.LEARNING.APPLIED`.
3. **DCB events** `AGENT.LEARNING.CAPTURED` / `AGENT.LEARNING.APPLIED` via tenant
   `IEventRepository`, tagged `{ agentId, signal, lessonId, tenantId }`.
4. **`ManagedAgent` (32-5) context hook** — during RAG assembly, retrieve agent-scoped learnings
   and inject them into the rendered prompt; emit `APPLIED`.
5. **Tenancy = C#-side, fail-closed.** Every ingest + query carries the `(agentId, tenantId)` tag;
   a query without the tenant filter returns zero tenant-scoped learnings. The cross-tenant
   isolation test is the regression guard.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a learning generated by a run? | The sole user (the user is the tenant). | The **tenant** that ran the agent — always, even for a public agent. |
| Where stored? | The single tenant schema. | The generating tenant's `t_<hex>` schema (structural isolation). |
| Who approves/rejects? | The user. | `tenant_owner`/`tenant_admin` (members read) — mirrors Prompt Store RBAC. |
| Who retrieves it at run time? | The user's runs. | Only that tenant's runs (`(agentId, tenantId)` filter); cross-tenant → nothing. |
| Can the public-agent owner (platform admin) see it? | n/a | **No** — performance/learning data never visible to the definition owner. |

---

## Task breakdown

### T1: `AgentLearning` entity + migration + model config (core persistence)

**Scope:** New tenant-scoped entity, `DbSet`, EF model config with CHECK + unique constraints, and
an additive migration. No service logic yet.

**Files:**
- New: `apps/tamma-elsa/src/Tamma.Data/Entities/AgentLearning.cs` (fields per story Technical Design:
  `Id, AgentId, AgentVersion, Role, TaskPattern, Signal, Lesson, DefectCategory?, SourceRunCorrelationId,
  Status, KnowledgeEntryId?, HitCount, CreatedAt, LastSeen, ReviewedAt?, RejectionReason?`).
- Modify: `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` — add `DbSet<AgentLearning> AgentLearnings`.
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` — in `ConfigureTenantEntities`:
  CHECK on `Signal IN ('success','failure','defect-category')` and `Status IN ('pending','approved','rejected')`;
  unique index `(AgentId, Role, TaskPattern, Signal)`; no `TenantId` column (structural isolation).
- New: additive EF migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/` (`dotnet ef migrations add`).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentLearningEntityTests.cs` (or fold into the
service tests) — entity persists + round-trips through `TenantDbContext`; CHECK rejects an invalid
`Signal`/`Status`; unique index rejects a second open row for the same `(AgentId, Role, TaskPattern,
Signal)`; migration applies + rolls back cleanly; `has-pending-model-changes` reports none.

**Acceptance criteria:**
- [ ] `AgentLearning` persists and round-trips through `TenantDbContext`.
- [ ] CHECK + unique constraints enforced at the DB level.
- [ ] Migration is additive, applies + rolls back, no pending model changes.

### T2: `AgentLearningService` core — capture, sanitize, dedup, events (no RAG yet)

**Scope:** The write-side seam: derive candidate(s), sanitize, rate-limit + similarity-dedup,
persist `pending`, emit `AGENT.LEARNING.CAPTURED`. Fire-and-forget-safe. No ingest/retrieve yet.

**Files:**
- New: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentLearningService.cs`,
  `AgentLearningService.cs`, `AgentLearningEventTypes.cs`
  (`AGENT.LEARNING.CAPTURED`, `AGENT.LEARNING.APPLIED`),
  `AgentLearningOptions.cs` (`AutoApprove` default false, `MaxCapturesPerWindow`, `Window`,
  `DefectRecurrenceThreshold`, `SimilarityThreshold`).
- Reuse: `ContentSanitizer` (32-5 path), tenant `IEventRepository`, `TenantDbContext`.
- Modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register `IAgentLearningService` + options.

**Derivation rules (`RecordFromOutcomeAsync`):**
- success + non-empty `whatWorked` → `signal 'success'`.
- failure/partial + `whatFailed`/`rootCause` → `signal 'failure'`.
- defect category crossing `DefectRecurrenceThreshold` for this agent (query tenant event store
  for prior same-category `AGENT.DEFECT.RECORDED` for the agent) → `signal 'defect-category'`.

**Order (load-bearing):** sanitize → rate-limit → similarity dedup → persist → emit `CAPTURED`.
A collapsed candidate yields no new row and no event.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentLearningServiceTests.cs` —
auto-capture from success (one pending row + one `CAPTURED` event tagged
`{agentId, signal, lessonId, tenantId}`); auto-capture from recurring defect (threshold gates it);
similarity dedup (M near-identical → 1 row, `HitCount=M`, 1 event); sanitization strips a seeded
secret from `lesson`; rate-limit caps captures per window; **fire-and-forget-safe** (DB fault →
WARN, no throw, originating event unaffected).

**Acceptance criteria:**
- [ ] Each signal type derives correctly; sub-threshold defect → no learning.
- [ ] Dedup + rate-limit cap growth; collapsed candidate emits no event.
- [ ] Sanitization applied before persistence/ingestion.
- [ ] `RecordFromOutcomeAsync` never throws to its caller.

### T3: Approval flow + RAG ingestion via `IIntelligenceHttpClient`

**Scope:** `ApproveAsync` / `RejectAsync`. Approve flips status, builds a `KnowledgeEntry`
(`type 'learning'`, tags/metadata = `agentId` + `tenantId`, keywords = role/taskPattern), ingests
via `IIntelligenceHttpClient` (KB upsert / `approveLearning` sidecar path), stores
`KnowledgeEntryId` (idempotent). Reject sets `rejected` + reason, no ingest; reject-of-approved
removes/disables the KB entry. `AutoApprove` opt-in auto-promotes high-confidence captures from T2.

**Files:** modify `AgentLearningService.cs`; reuse `IIntelligenceHttpClient` (verified) for ingest.

**Tests (first):** extend `AgentLearningServiceTests.cs` — approve → KB upsert called once with
`(agentId, tenantId)` tags + `KnowledgeEntryId` stored; re-approve idempotent (no duplicate);
reject → no ingest; reject-of-approved → KB entry removed/disabled; `AutoApprove=true` →
high-confidence capture auto-ingests; `AutoApprove=false` (default) → stays `pending`.

**Acceptance criteria:**
- [ ] Only `approved` learnings ingest; pending/rejected never reach the KB.
- [ ] Ingest payload carries `agentId` + `tenantId`.
- [ ] Approve is idempotent; reject-of-approved cleans up the KB entry.

### T4: `ManagedAgent` (32-5) retrieval + injection hook

**Scope:** In `ManagedAgent.RunAsync`'s context-assembly/RAG step, call
`RetrieveForRunAsync(agentId, tenantId, phaseDescription)` → query RAG filtered by
`(agentId, tenantId, type 'learning')`, inject retrieved lessons into the rendered prompt, emit
`AGENT.LEARNING.APPLIED` when any are injected.

**Files:** modify `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` (32-5);
add `RetrieveForRunAsync` to `AgentLearningService`.

**Tests (first):** integration `tests/Tamma.Api.Tests/Agents/AgentLearningRagIntegrationTests.cs` —
**RAG round-trip**: capture → approve → ingest → run context assembly for the same agent → injected
lesson present AND assembled context **differs** from a pre-ingest run; `AGENT.LEARNING.APPLIED`
emitted with `{agentId, signal, lessonId, tenantId}`.

**Acceptance criteria:**
- [ ] A follow-up run's assembled context demonstrably changes after a learning is ingested.
- [ ] `AGENT.LEARNING.APPLIED` emitted on retrieval+injection.
- [ ] No learnings ingested → context unchanged, no `APPLIED` event.

### T5: Tenant isolation hardening + lifecycle/idempotency

**Scope:** Make tenancy fail-closed and verified; finish the lifecycle edges.

**Files:** harden `AgentLearningService` query path (tenant filter mandatory; missing filter →
zero results + ERROR log); ensure reject-of-approved + re-approve idempotency from T3 is covered
end-to-end.

**Tests (first):** extend `AgentLearningRagIntegrationTests.cs` — **cross-tenant isolation**: same
public agent under tenant A (ingest lesson) and tenant B → tenant B's context assembly excludes
A's lesson; a RAG query omitting the tenant filter returns nothing (fail-closed); SaaS RBAC —
member approve/reject → 403, `tenant_owner`/`tenant_admin` allowed.

**Acceptance criteria:**
- [ ] Tenant B never retrieves tenant A's lesson for a shared public agent.
- [ ] Query without tenant filter returns zero tenant-scoped learnings (fail-closed, ERROR-logged).
- [ ] SaaS RBAC enforced on approve/reject.
- [ ] Full C# suite green via `sg docker -c "dotnet test ..."`.

---

## Task order & dependencies

T1 → T2 → T3 → T4 → T5. T1 is the only hard prerequisite for the rest; T3 needs T2; T4 needs T3 +
the 32-5 `ManagedAgent`; T5 needs T3/T4.

**Story prerequisites (must be merged first):** 32-5 (`ManagedAgent` + RAG step + `ContentSanitizer`),
32-8 (`AGENT.OUTCOME.RECORDED` / `AGENT.DEFECT.RECORDED`), 32-10 (per-tenant outcome dataset +
recurrence). Epic 6 (RAG/KB pipeline) + Epic 9 (`KnowledgeService` vocabulary) are standing deps.

## Risks

- **Cross-tenant leakage (primary).** The shared intelligence sidecar means a forgotten tenant tag
  silently mixes learnings across tenants for a shared public agent. Mitigation: tenant tag
  mandatory on ingest AND query, fail-closed on a missing query filter, cross-tenant isolation
  test as the regression guard (T5). Do not weaken AC7.
- **Learning noise / unbounded growth.** Mitigation order: similarity dedup (one durable learning
  per `(agent, role, taskPattern, signal)`), per-agent rate-limit, approval gate (auto-approve off
  by default). Watch a flapping signal (approve → reject → re-derive) — if observed, add a
  per-lesson cooldown (cheap follow-up).
- **Auto-capture on the wrong path.** It must run only **after** the 32-8 flush, never on the hot
  resolve/LLM path, and be fire-and-forget-safe. A capture fault must never delay or alter a run's
  terminal outcome — the never-throw contract in T2 is load-bearing.
- **Sidecar tenancy mismatch.** `IIntelligenceHttpClient` is a plain shared `HttpClient` with no
  tenant scoping of its own; ALL scoping rides on payload tags. If the sidecar's KB/RAG schema
  cannot store/filter arbitrary tags, verify and, if needed, namespace by tenant in the collection
  key — take the simpler option that keeps isolation structural.
- **Migration discipline.** `agent_learnings` is additive (normal `migrations add`); still mirror
  config in `TammaModelConfiguration.ConfigureTenantEntities` only, verify no pending model
  changes, and confirm apply+rollback in the per-tenant migrator path.
- **Event-store topology (Story 28-1 / Epic 30).** `AGENT.LEARNING.*` events append to the tenant
  store via `IEventRepository`, consistent with the 32-8 outcome trail; keep emission going through
  the tenant repository so any future per-tenant fan-out is transparent.
