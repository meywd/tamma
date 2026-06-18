# Story 32-11: Learning Persistence & Auto-Learning into RAG

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant running Tamma agents over many issues**,
I want durable learnings (what worked / what failed for a given agent + role + task pattern) to be captured automatically from run outcomes and fed back into the RAG knowledge base,
So that future runs of the same agent retrieve those lessons and steadily improve — without any learning ever leaking across tenants.

## Priority

P1 - Closes the Epic 32 learning loop; turns the action/outcome trail (32-8) and benchmark projections (32-10) into a feedback mechanism that changes future agent behaviour.

## Context

The Epic 32 design spec calls for a closed learning loop: *"persist `LearningCapture`/`KnowledgeEntry`; auto-generate learnings from outcomes; feed into the KB so RAG retrieves them in future runs."* The TypeScript type vocabulary for this already exists — `LearningCapture`, `KnowledgeEntry`, `PendingLearning`, and the review/approval state machine — in `packages/shared/src/types/knowledge.ts`, and `KnowledgeService` (`packages/intelligence/src/knowledge-base/knowledge-service.ts`) already implements `captureLearning` → `getPendingLearnings` → `approveLearning` (creating a `KnowledgeEntry` with embedding) and a RAG retrieval pipeline lives in `packages/intelligence/src/rag/`.

What is **missing** is the durable, tenant-scoped backend and the C# auto-learning hook:

1. The captured learning has **no persistent, tenant-scoped backend** — `LearningCapture`/`PendingLearning` resolve through whatever store `KnowledgeService` is constructed with; nothing ties a learning to an `agentId` + tenant in the per-tenant database, and nothing prevents a public agent's learnings from one tenant being served to another.
2. There is **no auto-learning hook**: nothing listens for run/outcome events (`AGENT.OUTCOME.RECORDED`, `AGENT.DEFECT.RECORDED` from 32-8) to derive learnings from `whatWorked` / `whatFailed` / `rootCause` and recurring defect categories.
3. There is **no ingestion into RAG** of approved learnings tagged so retrieval is scoped to `(agentId, tenant)`, and managed-agent context assembly (32-5) does not yet retrieve agent-scoped learnings.

This story adds: (a) a tenant-scoped `AgentLearning` entity persisted in the per-tenant `TenantDbContext` (schema-per-tenant — isolation is structural; the row never carries another tenant's data), (b) an `AgentLearningService` (C#) that runs after `AGENT.OUTCOME.RECORDED`/`AGENT.DEFECT.RECORDED` to auto-generate learnings, deduplicate them, route them through a review/approval flow (`PendingLearning`), and ingest **approved** learnings into the RAG KB via `IIntelligenceHttpClient` tagged with `agentId` + tenant, and (c) wiring in `ManagedAgent` (32-5) context assembly to retrieve agent-scoped learnings and inject them into the prompt.

**Tenancy rule (from the design spec).** Definition ownership and *data* ownership are separate: a public/system agent's *definition* is shared, but every learning it accumulates is **ALWAYS tenant-scoped**. Two tenants running public agent `atlas` build separate, private learning sets; neither sees the other's; the platform admin who owns `atlas` sees none of it. Because the TypeScript intelligence sidecar (`@tamma/intelligence-server`, reached via the shared `IIntelligenceHttpClient`) is a process-shared service, **the C# side is the source of truth for tenancy**: every KB upsert and every RAG query is tagged/filtered by `(agentId, tenantId)`, and retrieval that omits the caller's tenant filter returns nothing.

## Acceptance Criteria

1. An **`AgentLearning`** entity (tenant-scoped, persisted via `TenantDbContext`) captures `{ agentId, agentVersion, role, taskPattern, signal: 'success' | 'failure' | 'defect-category', lesson, sourceRunCorrelationId, status, createdAt }`. The row lives in the tenant's `t_<hex>` schema with no cross-tenant column (isolation is structural, per the Epic 28 model), and an additive EF migration adds the table under `src/Tamma.Data/Migrations/` with `has-pending-model-changes` reporting none afterwards.

2. An **auto-learning hook** in `AgentLearningService` runs after `AGENT.OUTCOME.RECORDED` and `AGENT.DEFECT.RECORDED` (the 32-8 outcome/defect events): a **success** outcome with a non-empty `whatWorked` derives a `signal: 'success'` learning; a **failure/partial** outcome with `whatFailed`/`rootCause` derives a `signal: 'failure'` learning; a **recurring defect category** (same `category` for the same agent crossing a configurable count threshold within a window) derives a `signal: 'defect-category'` learning. The hook is **fire-and-forget-safe** — a capture fault is logged (WARN) and never aborts the workflow or masks the originating event (matching the `IMissingConfigRecorder` / `AgentOutcomeService` precedent).

3. Auto-generated learnings are **deduplicated by similarity** before persistence: a candidate learning whose `(agentId, role, taskPattern, signal)` matches an existing open learning AND whose `lesson` is within a similarity threshold of the existing `lesson` is collapsed (bump `hitCount`/`lastSeen`, no new row, no new event) so a hot retry loop or recurring pattern produces one durable learning, not thousands.

4. Captured learnings enter a **review/approval flow** modelled on `PendingLearning` (`status: 'pending' | 'approved' | 'rejected'`): auto-captured learnings start `pending`; an approval action promotes a learning to `approved` (and triggers RAG ingestion, AC5); a rejection sets `rejected` with a reason and never ingests. Auto-approval for high-confidence signals is gated behind a configurable `AgentLearning:AutoApprove` flag (default off) so unbounded auto-ingestion is opt-in.

5. **Approved** learnings are written into the RAG knowledge base via the existing `IIntelligenceHttpClient` (KB/vector ingestion path → `KnowledgeService.approveLearning` / KB upsert), creating a `KnowledgeEntry` of `type: 'learning'`, **tagged with `agentId` + `tenantId`** so retrieval is scoped. The ingest payload carries the agent and tenant identifiers in the entry's tags/metadata; ingestion of a `rejected` or still-`pending` learning is impossible.

6. **Managed-agent context assembly** (32-5 `ManagedAgent` RAG step) retrieves agent-scoped learnings from RAG — querying with a filter that includes the current `(agentId, tenantId)` — and injects the retrieved lessons into the rendered prompt. An integration test demonstrates that a follow-up run of the same agent receives a **demonstrably different assembled context** (the injected lesson) versus a run before the learning was ingested.

7. **Tenant isolation:** a tenant's learnings for a **public** agent are NEVER retrieved by another tenant. A cross-tenant RAG retrieval test runs the same public agent under tenant A (ingesting a learning) and tenant B, and asserts tenant B's context assembly does NOT contain tenant A's lesson. A RAG query that omits the caller's tenant filter returns no tenant-scoped learnings (fail-closed).

8. **Learning capture respects content sanitization**: the `lesson`/`whatWorked`/`whatFailed`/`rootCause` text is sanitized (reusing the `ContentSanitizer` seam from the managed-agent path) before persistence and ingestion so no secrets/PII are durably stored, and capture is **rate-limited per agent** (configurable `AgentLearning:MaxCapturesPerWindow`) on top of similarity dedup to prevent unbounded growth.

9. **DCB events** `AGENT.LEARNING.CAPTURED` (on first persistence of a new learning) and `AGENT.LEARNING.APPLIED` (when an agent-scoped learning is retrieved and injected into a run's context) are emitted via the tenant `IEventRepository`, tagged `{ agentId, signal, lessonId, tenantId }`. A deduped (collapsed) capture and a rejected learning emit no `CAPTURED` event.

10. **Reconciliation / lifecycle:** approving a learning that was already ingested is idempotent (no duplicate KB entry); deleting/rejecting a previously-approved learning removes (or disables) the corresponding `KnowledgeEntry` from the KB so retrieval stops surfacing it.

11. **Tests** cover: auto-capture from a **success** outcome; auto-capture from a **recurring defect category**; **similarity dedup** (N identical candidates → one durable learning, one `CAPTURED` event); the **RAG round-trip** (capture → approve → ingest → retrieve → inject, asserting context changes); and **tenant isolation** (cross-tenant public-agent retrieval test). C# service tests verify the hook is fire-and-forget-safe (DB fault → WARN, no throw) and that sanitization strips a seeded secret.

## Technical Design

### Component map (all paths verified against the C# `apps/tamma-elsa` stack; `packages/api` is deleted and never referenced)

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  IAgentLearningService.cs        # NEW — capture + approve/reject + retrieve contract
  AgentLearningService.cs         # NEW — auto-learning hook, dedup, sanitize, ingest, events
  AgentLearningEventTypes.cs      # NEW — AGENT.LEARNING.CAPTURED / .APPLIED constants
  AgentLearningOptions.cs         # NEW — AutoApprove, MaxCapturesPerWindow, defect-recurrence threshold, similarity threshold
  ManagedAgent.cs                 # MODIFY (32-5) — context assembly retrieves + injects agent-scoped learnings

apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/
  IIntelligenceHttpClient.cs      # EXISTS — KB/vector/RAG sidecar client (verified); reuse for ingest + query
  IntelligenceHttpClient.cs       # EXISTS — verified

apps/tamma-elsa/src/Tamma.Data/
  Entities/AgentLearning.cs       # NEW — tenant-scoped entity
  TenantDbContext.cs              # MODIFY — add DbSet<AgentLearning>
  TammaModelConfiguration.cs      # MODIFY — ConfigureTenantEntities: AgentLearning model config + CHECK constraints
  Migrations/                     # NEW — additive EF migration (agent_learnings table)

packages/intelligence/src/knowledge-base/
  knowledge-service.ts            # EXISTS — captureLearning / getPendingLearnings / approveLearning (verified); ingest target
packages/intelligence/src/rag/
  retriever.ts / rag-pipeline.ts  # EXISTS — RAG retrieval (verified); query path with agent/tenant tag filter

packages/shared/src/types/
  knowledge.ts                    # EXISTS — LearningCapture / KnowledgeEntry / PendingLearning (verified); shared vocabulary
```

> **Source-of-truth note.** The TypeScript `KnowledgeService` + RAG pipeline are reached through the process-shared `@tamma/intelligence-server` sidecar via the existing `IIntelligenceHttpClient` (a plain `HttpClient` against `/kb/*` routes — verified). Because the sidecar is shared across tenants, **tenancy is enforced on the C# side**: every ingest and query carries `(agentId, tenantId)` tags and a tenant filter. No new TypeScript package is introduced — the existing `LearningCapture`/`KnowledgeEntry`/`PendingLearning` vocabulary and `captureLearning`/`approveLearning` flow are reused as-is.

### `AgentLearning` entity (tenant-scoped)

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/AgentLearning.cs
public class AgentLearning
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }            // join key — survives config edits (32-1)
    public int AgentVersion { get; set; }        // config version that produced the outcome
    public string Role { get; set; } = null!;    // architect/reviewer/... — like-vs-like
    public string TaskPattern { get; set; } = null!; // normalized task/phase descriptor
    public string Signal { get; set; } = null!;  // 'success' | 'failure' | 'defect-category' (CHECK)
    public string Lesson { get; set; } = null!;  // sanitized, deduped lesson text
    public string? DefectCategory { get; set; }  // set when Signal = 'defect-category'
    public Guid SourceRunCorrelationId { get; set; } // ties learning to the originating run (32-5/32-8)
    public string Status { get; set; } = "pending"; // 'pending' | 'approved' | 'rejected' (CHECK)
    public string? KnowledgeEntryId { get; set; }    // KB entry id once ingested (AC5/AC10)
    public int HitCount { get; set; } = 1;       // bumped on similarity dedup (AC3)
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
}
```

No `TenantId` column on the entity (Epic 28 target architecture — the row lives in the tenant's own schema; isolation is structural via `TenantDbContext`). `TammaModelConfiguration.ConfigureTenantEntities` registers the model with CHECK constraints on `Signal`/`Status` and a unique index on `(AgentId, Role, TaskPattern, Signal)` to anchor dedup (similarity collapse updates the matching open row rather than inserting).

### Auto-learning hook

```csharp
// AgentLearningService.RecordFromOutcomeAsync — invoked after the 32-8 outcome/defect flush
//   (AGENT.OUTCOME.RECORDED / AGENT.DEFECT.RECORDED), e.g. from AgentOutcomeService or the
//   review/gate activity's post-flush hook, never on the hot resolve path.
//
// 1. Derive candidate(s):
//    - outcome = success  + whatWorked   → signal 'success'
//    - outcome = failure/partial + whatFailed/rootCause → signal 'failure'
//    - defect category crossing recurrence threshold for this agent → signal 'defect-category'
// 2. Sanitize the lesson text via ContentSanitizer (AC8) — no secrets/PII persisted.
// 3. Rate-limit per agent (MaxCapturesPerWindow) and dedup by similarity against open learnings
//    matching (agentId, role, taskPattern, signal) (AC3): collapse → bump HitCount/LastSeen, return.
// 4. Persist a new AgentLearning (status 'pending'); emit AGENT.LEARNING.CAPTURED via tenant
//    IEventRepository tagged { agentId, signal, lessonId, tenantId } (AC9).
// 5. If AgentLearning:AutoApprove and high-confidence → ApproveAsync immediately (AC4/AC5).
//
// The whole method is try/catch-wrapped: a fault logs WARN and returns — never throws to the
// caller (AC2), mirroring AgentOutcomeService / IMissingConfigRecorder.
```

`AGENT.OUTCOME.RECORDED` (32-8) carries `outcome` + `iterationsToDone` tagged with `agentId`/`agentVersion`; the outcome's `whatWorked`/`whatFailed`/`rootCause` fields ride in the event `data` (the `LearningCapture` vocabulary). `AGENT.DEFECT.RECORDED` carries `category` + agent attribution; the recurrence check queries the tenant event store for prior same-category defects for the agent (the 32-8 trail) before opening a `defect-category` learning.

### RAG ingestion (approved learnings)

```csharp
// AgentLearningService.ApproveAsync(lessonId, edits?)
// 1. Flip status -> 'approved', stamp ReviewedAt.
// 2. Build a KnowledgeEntry (type 'learning') from the AgentLearning, tags/metadata carrying
//    agentId + tenantId (AC5). taskPattern/role become keywords for matching.
// 3. Ingest via IIntelligenceHttpClient (KB upsert / approveLearning path on the sidecar) — the
//    sidecar generates the embedding and persists the vector. Store the returned entry id in
//    AgentLearning.KnowledgeEntryId (idempotent: re-approve is a no-op when already set, AC10).
// RejectAsync(lessonId, reason): status -> 'rejected', no ingest; if previously approved, also
//    disable/remove the KnowledgeEntry from the KB so retrieval stops surfacing it (AC10).
```

The ingest carries the agent + tenant identifiers so a later RAG query filtered by `(agentId, tenantId)` retrieves them. The shared sidecar never serves a learning to a query lacking the matching tenant tag (fail-closed, AC7).

### Context assembly hook (32-5)

```csharp
// In ManagedAgent.RunAsync, during the "assemble context + RAG (Epic 6)" step:
//   var learnings = await _intelligence.QueryRagAsync(new RagQueryRequest {
//       Query = request.PhaseDescription,
//       Filter = { AgentId = agentId, TenantId = tenantId, Type = "learning" }
//   }, ct);
//   contextBuilder.AppendLearnings(learnings);   // injected into the rendered prompt
//   if (learnings.Any()) emit AGENT.LEARNING.APPLIED { agentId, signal, lessonId, tenantId };
```

This makes the loop closed: outcome → learning → KB → retrieval → next run's prompt. The integration test (AC6) asserts the assembled context for run #2 contains the lesson absent from run #1.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a learning generated by a run? | The sole user — it's their instance; the learning is theirs (the user is the tenant). | The **tenant** that ran the agent — always tenant-scoped, even for a public agent definition. Never the platform admin who owns the public agent. |
| Where is it stored? | The (single) tenant schema. | The generating tenant's `t_<hex>` schema (structural isolation). |
| Who can approve/reject? | The user. | `tenant_owner` / `tenant_admin` (mirrors the Prompt Store RBAC for tenant data; members read). |
| Who can retrieve it via RAG at run time? | The user's runs. | Only that tenant's runs — filtered by `(agentId, tenantId)`; cross-tenant retrieval returns nothing (AC7). |
| Can the platform admin (owner of a public agent) see the learnings? | n/a (no platform/tenant split). | **No** — performance/learning data is never visible to the public-agent owner (design-spec rule). |

## Dependencies

- **Prerequisite — Story 32-8** (Outcome capture & bug taxonomy): emits `AGENT.OUTCOME.RECORDED` (with `outcome`/`iterationsToDone`) and `AGENT.DEFECT.RECORDED` (with `category`) tagged `agentId`+`agentVersion` in the tenant store. This story's auto-learning hook consumes those events and reuses the same fire-and-forget-safe, idempotent capture discipline.
- **Prerequisite — Story 32-10** (Benchmark projections & leaderboards): the per-tenant outcome/defect dataset and recurrence counts the defect-category signal builds on; learnings complement the leaderboard with prescriptive lessons.
- **Prerequisite — Story 32-5** (Managed agent execution layer): provides `ManagedAgent`'s context-assembly/RAG step and the `ContentSanitizer` seam this story hooks into; provides `correlationId` on the run for `sourceRunCorrelationId`.
- **Prerequisite — Epic 6 (RAG/KB)**: the RAG retrieval pipeline (`packages/intelligence/src/rag/`) and the KB ingestion path (`KnowledgeService`, `packages/intelligence/src/knowledge-base/`) reached via `IIntelligenceHttpClient`.
- **Related — Epic 9 (KnowledgeService / unified agent API)**: the `KnowledgeService` capture/approve flow and shared `LearningCapture`/`KnowledgeEntry`/`PendingLearning` vocabulary in `packages/shared/src/types/knowledge.ts`.
- **Related — Story 32-1/32-2** (agent identity + resolver): `agentId` + config version are the join keys learnings are tagged with.

## Testing Strategy

1. **Auto-capture from success** (`Tamma.Api.Tests/Agents/AgentLearningServiceTests.cs`): feed a synthetic `AGENT.OUTCOME.RECORDED` (success + `whatWorked`) → exactly one `AgentLearning` (signal `success`, status `pending`) persisted + one `AGENT.LEARNING.CAPTURED` event tagged `{agentId, signal, lessonId, tenantId}`.
2. **Auto-capture from recurring defect**: feed N `AGENT.DEFECT.RECORDED` of the same `category` for one agent crossing the threshold → one `defect-category` learning; sub-threshold → none.
3. **Similarity dedup**: feed M near-identical success candidates → one durable learning, `HitCount = M`, exactly one `CAPTURED` event.
4. **Sanitization**: an outcome whose `whatFailed` embeds a fake secret → persisted/ingested `lesson` has it redacted (no secret durably stored).
5. **Fire-and-forget-safe**: simulate a `TenantDbContext` fault during capture → WARN logged, no throw, originating outcome event unaffected.
6. **RAG round-trip (integration)**: capture → approve → ingest via `IIntelligenceHttpClient` → run `ManagedAgent` context assembly for the same agent → assert injected lesson present AND assembled context differs from a pre-ingest run; `AGENT.LEARNING.APPLIED` emitted.
7. **Tenant isolation (integration)**: same public agent under tenant A (ingest lesson) and tenant B → tenant B's context assembly excludes A's lesson; a query without the tenant filter returns nothing (fail-closed).
8. **Lifecycle/idempotency**: re-approve an already-ingested learning → no duplicate KB entry; reject a previously-approved learning → KB entry removed/disabled, subsequent retrieval excludes it.
9. **RBAC (SaaS)**: member-role approve/reject → 403; `tenant_owner`/`tenant_admin` → allowed (mirrors Prompt Store RBAC).

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentLearning.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add `DbSet<AgentLearning>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (AgentLearning model config + CHECK/unique constraints in `ConfigureTenantEntities`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/` (new EF migration) | Create (NEW — additive `agent_learnings` table) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentLearningService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentLearningService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentLearningEventTypes.cs` | Create (NEW — `AGENT.LEARNING.CAPTURED`, `AGENT.LEARNING.APPLIED`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentLearningOptions.cs` | Create (NEW — AutoApprove, MaxCapturesPerWindow, recurrence + similarity thresholds) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Modify (retrieve + inject agent-scoped learnings; emit `AGENT.LEARNING.APPLIED`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register `IAgentLearningService` + options) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentLearningServiceTests.cs` | Create (NEW — unit: capture, dedup, sanitize, fire-and-forget) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentLearningRagIntegrationTests.cs` | Create (NEW — RAG round-trip + tenant isolation) |

> **NEW file note.** `AgentLearningService.cs`, `IAgentLearningService.cs`, `AgentLearningEventTypes.cs`, `AgentLearningOptions.cs`, and `Entities/AgentLearning.cs` do **not** exist yet — they are created by this story. `IIntelligenceHttpClient.cs`/`IntelligenceHttpClient.cs`, `TenantDbContext.cs`, `TammaModelConfiguration.cs`, `ManagedAgent.cs` (from 32-5), `packages/shared/src/types/knowledge.ts`, and the `packages/intelligence` RAG/KB modules all exist (verified).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed Stories 32-5 (managed agent + RAG step), 32-8 (outcome/defect events), and 32-10 (projections) — this story sits directly downstream of all three
4. Reviewed the existing `LearningCapture`/`PendingLearning` flow in `packages/intelligence/src/knowledge-base/knowledge-service.ts` so the C# side mirrors, not forks, its semantics
5. Planned a TDD approach (Red-Green-Refactor) — tests first, per project convention

### Tenancy is the load-bearing invariant

The shared `@tamma/intelligence-server` sidecar means a forgotten tenant tag silently cross-contaminates learnings between tenants for a shared public agent. Treat the `(agentId, tenantId)` tag on **both** ingest and query as mandatory and fail-closed: a query without a tenant filter must return zero tenant-scoped learnings. The cross-tenant isolation test (AC7) is the regression guard — do not weaken it.

### Reuse, do not fork

Reuse `ContentSanitizer` (32-5 managed-agent path) for lesson sanitization, the tenant `IEventRepository` for events, the `IIntelligenceHttpClient` KB/RAG methods for ingest/query, and the existing `LearningCapture`/`KnowledgeEntry`/`PendingLearning` vocabulary. Do not introduce a second sanitizer, a second event store, or a new TypeScript package.

### Fire-and-forget on the cold path only

Auto-capture runs **after** the 32-8 outcome/defect flush — never on the hot agent-resolve or LLM-call path. Capture is fire-and-forget-safe: a fault is a logged WARN, never a throw. The hook must not delay or alter the run's terminal outcome.

### Dedup before persistence, before events

Apply rate-limit + similarity dedup **before** inserting the row and **before** emitting `AGENT.LEARNING.CAPTURED`, so a collapsed candidate produces neither a new row nor a new event (AC3/AC9). Anchor the cheap exact-match dedup on the `(AgentId, Role, TaskPattern, Signal)` unique index; layer text-similarity on top for near-duplicates.

### Migration discipline

`agent_learnings` is an additive tenant-scoped table — normal `dotnet ef migrations add`. Mirror the entity config in `TammaModelConfiguration.ConfigureTenantEntities` only (the single source), verify `has-pending-model-changes` reports none, and confirm the migration applies + rolls back cleanly in the per-tenant migrator path. C# tests run via `sg docker -c "dotnet test ..."`.

## Logging Requirements

- **INFO**: Learning auto-captured (`agentId`, `signal`, `lessonId`), learning approved + ingested into KB (`agentId`, `lessonId`, `knowledgeEntryId`), learning applied to a run's context (`agentId`, `lessonId`, `correlationId`).
- **DEBUG**: Candidate derived from outcome/defect (signal, taskPattern), similarity dedup collapse (matched `lessonId`, new `hitCount`), RAG query for agent-scoped learnings (filter, result count).
- **WARN**: Capture fault swallowed (fire-and-forget — `agentId`, error), rate-limit hit for an agent (`agentId`, window), ingest failure (sidecar unreachable — `lessonId`, error).
- **ERROR**: KB ingestion/upsert persistently failing after retry, tenant filter missing on a RAG query (must never happen — alert on it), migration/model mismatch.
- **Structured context**: include `{ agentId, tenantId, signal, lessonId, correlationId }` where applicable.
- **Credential safety**: NEVER log raw lesson text containing un-sanitized content; log only the post-sanitization lesson or its id. NEVER log provider keys or tenant secrets.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
