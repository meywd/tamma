# Story 32-6 — Agent Action Trail (DCB events tagged agent_id) in Tenant Store

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Docker-bound C# suites run via `sg docker -c "dotnet test ..."`; the build
> needs no wrapper.

**Goal:** Capture a complete, queryable **action trail** for every managed agent run as DCB events
written to the **tenant-scoped** `domain_events` stream — tagged with `agentId` + `agentVersion` +
`role` + `provider` + `model` + `promptRef` + `issueId` + `iteration` + `correlationId` +
`credentialSource` — and expose a per-agent, tenant-isolated, member-readable action-history query
API. This is the audit/analytics substrate that 32-8/32-9/32-10/32-11 consume. Action data is
**ALWAYS tenant-scoped**: a platform admin cannot read another tenant's trail, and one public agent
definition produces N independent per-tenant trails.

**Story file:** `docs/stories/epic-32/story-32-6/32-6-agent-action-trail-in-tenant-store.md`

**Design source:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
(§"Ownership, visibility & data scoping" + §"Tracking: actions + performance + learning").

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (xUnit). **`packages/api` is deleted** — all
work is C#.

---

## Non-goals (YAGNI guard)

- **NO new table.** The trail reuses the existing tenant `domain_events` (`DomainEvent` entity).
  No projection table, no read-model materialization — that is 32-10's job (leaderboards). This
  story only writes events and exposes a thin read over them.
- **NO new event infrastructure.** `IEventRepository.AppendAsync` already routes tenant-scoped
  events to `t_<hex>` via `ITenantDbContextFactory`; `SequenceNumber` already exists as the
  `BIGSERIAL` cursor. Reuse both.
- **NO cross-tenant or platform-admin read path.** Not "guarded" — *unbuilt*. The repository throws
  `NotSupportedException` on null-tenant paginated reads; no `OwnerAccess` route is added. A platform
  owner who owns a public agent sees none of any tenant's runs of it.
- **NO inlined blobs.** Prompt bodies, tool args/results, RAG context → `promptRef`/`blobRef` +
  sanitized values only. Reuse the existing `SecureAgentProvider` sanitization seam; do not write a
  new sanitizer.
- **NO benchmarking/leaderboard/learning logic.** 32-9 (cost emission), 32-10 (projections), 32-11
  (learning) consume this trail; they are out of scope. This story only guarantees the data is
  captured, isolated, and queryable.
- **NO `ProviderDiagnostic` schema change.** Link via the existing `CorrelationId` + `AgentType`
  columns. A dedicated `agentId` column is a later additive migration if ever needed.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Asset | Location | What it gives us |
|---|---|---|
| `DomainEvent` (DCB row) | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Id`, `Type`, `TenantId`, `IssueNumber`, JSONB `Tags`/`Metadata`/`Data`, `CreatedAt`, **`SequenceNumber` (`BIGSERIAL`)** — the cursor. Reused verbatim, no change. |
| `IEventRepository` / `EventRepository` | `apps/tamma-elsa/src/Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs` | `AppendAsync` resolves `evt.TenantId ?? tenantContext.TenantId` → writes through `ITenantDbContextFactory.CreateAsync(tid)` (the `t_<hex>` schema). `QueryWithPaginationAsync`/`ListByTenantAsync` are tenant-scoped and **throw `NotSupportedException` on null tenant** — the isolation backbone. We add `QueryAgentTrailAsync` alongside. |
| Tenant context + factory | `apps/tamma-elsa/src/Tamma.Data/{ITenantContext,TenantContext}.cs`, `ITenantDbContextFactory` | Schema-per-tenant routing — the structural isolation plane. No new plumbing. |
| Existing DCB emission precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` (`UpdateConfig`, ~93–113) | Shows the canonical `DomainEvent { Type, TenantId, Tags=flat strings, Metadata=envelope, Data }` shape to mirror. |
| `ProviderDiagnostic` | `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Already has `CorrelationId`, `AgentType`, `TenantId`, tokens, cost. Link key for AC 8 — no schema change. |
| `DiagnosticsService` | `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs` | `RecordEventAsync(ProviderDiagnostic)` — thread the run's `correlationId` + `AgentType` here. |
| Tenant-scope endpoint precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AlertEndpoints.cs` (`ListTenantAlerts`, ~576) | Exact pattern for a member-readable `/api/v1/orgs/{tenantId}/...` list filtering `tenant_id` + paging. |
| Path-tenant gate | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (~350, ~1512 `orgs` group, `RequireTenantMembershipFilter`) | `/api/v1/orgs/{tenantId}/*` under `MemberAccess` + `RequireTenantMembershipFilter`. Map the trail endpoints here. |
| Agent resolver (32-2) | `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Source of `provider`/`model`/`role`/version for tags until 32-1's entity lands. |
| Sanitization seam | `SecureAgentProvider` (Content Sanitization plan; `packages/shared/src/security/` design + C# equivalent) | Redaction before persistence. Reuse. |

**Not-yet-existing (mark NEW / prerequisite):**
- `Agent`/`AgentVersion` entity (`Tamma.Data/Entities/Agent.cs`) — **NEW in story 32-1** (not present
  today). Supplies `agentId`/`agentVersion`. Hard prerequisite; until it lands, derive identity from
  the resolved config.
- `IManagedAgent` execution layer — **story 32-5**. The producer that calls the emitter. Hard
  prerequisite for Task 2 wiring.
- Panel/bug seams — **stories 32-7/32-8**. Emitter API ships now; those call sites stub forward.

---

## Architecture

**Producer → emitter → tenant event store → tenant-scoped read.** No new storage layer.

```
32-5 IManagedAgent run ──┐
32-7 panel aggregate ────┤── IAgentTrailEmitter ──► IEventRepository.AppendAsync(DomainEvent)
32-8 review/gate bug  ────┘     (sanitize + tag)          │ TenantId = resolving tenant
                                                          ▼
                                          t_<hex>.domain_events  (schema-per-tenant)
                                                          ▲
GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs ───────┤  IEventRepository.QueryAgentTrailAsync
GET /api/v1/orgs/{tenantId}/agents/{agentId}/trail ──────┘  (agentId Tags predicate, SequenceNumber cursor)

ProviderDiagnostic { CorrelationId, AgentType } ── linked by (correlationId, agentId) ── 32-9/32-10
```

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the **action trail** of a run? | The sole user (their instance, their schema). Trail is `TenantId`-scoped to the personal tenant. | The tenant that ran the agent. Visible to its members via `/api/v1/orgs/{tenantId}/agents/...`. |
| Can a platform admin read it? | N/A (no platform/tenant split). | **No.** No `OwnerAccess` route exists and `IEventRepository` cannot return cross-tenant tenant-scoped events. Even the platform admin who owns the *public agent definition* sees none of any tenant's runs. |
| Public/system agent run? | Trail belongs to the sole user. | One public definition → N independent per-tenant trails; each tenant sees only its own. |
| Who can read via API? | The user. | Any tenant **member** (read-only; no mutation surface). |
| Mode source | `ITammaModeProvider` (process-stable) | same |

The isolation guarantee is **structural** (schema-per-tenant + repository null-tenant guards), not an
app filter — that is the single most important property to preserve and test.

---

## Task breakdown

### T1: Trail emission core (no producer wiring yet)

**Scope:** The emitter seam, tag builder, context/record types, event-type constants, redaction,
non-blocking contract, DI. No call sites changed yet.

**Files (all NEW under `src/Tamma.Api/Services/Agents/`):**
- `AgentTrailContext.cs` — `AgentTrailContext` (identity/tags fields) + records `AgentRunOutcome`,
  `ToolCallRecord`, `IterationRecord`, `PanelRecord`, `BugRecord`.
- `AgentTrailTags.cs` — `Build(AgentTrailContext)` → flat-string JSONB (the shared tag contract).
- `AgentTrailEventTypes.cs` — constants: `AGENT.TASK.SUCCESS/FAILED/PARTIAL`,
  `AGENT.TOOL_CALL.SUCCESS/FAILED`, `AGENT.ITERATION.COMPLETED`, `AGENT.PANEL.AGGREGATED`,
  `REVIEW.BUG.RECORDED`, `AGENT.TRAIL.WRITE_FAILED`.
- `IAgentTrailEmitter.cs` + `AgentTrailEmitter.cs` — `RunCompletedAsync`, `ToolCallAsync`,
  `IterationCompletedAsync`, `PanelAggregatedAsync`, `BugRecordedAsync`. Each builds a `DomainEvent`
  with `TenantId = ctx.TenantId`, sanitized `Data`, full `Tags`; appends via `IEventRepository`;
  **never throws into the run** (catch → WARN → best-effort `AGENT.TRAIL.WRITE_FAILED` breadcrumb).
- Wire DI in `Program.cs` (mirror existing `Services/Agents/` registrations).

**Tests first (`tests/Tamma.Api.Tests/Agents/AgentTrailEmitterTests.cs`):**
- [ ] Each family emits the right `Type` and full required tag set; `REVIEW.BUG.RECORDED` carries `bugType`.
- [ ] `Data` contains only refs/sanitized values for secret-shaped input (redaction).
- [ ] `AppendAsync` failure does NOT propagate; WARN logged; `AGENT.TRAIL.WRITE_FAILED` attempted.
- [ ] `TenantId` on every emitted event = the context's resolving tenant.

**Acceptance:**
- [ ] Emitter is the single seam; no hand-built `DomainEvent` outside it.
- [ ] Non-blocking contract holds under induced failure.
- [ ] Full C# suite stays green.

### T2: Wire emission into the managed run, panels, diagnostics link

**Scope:** Call the emitter from the producers; thread one `correlationId` through trail + diagnostics.

**Files (modify):**
- 32-5 `IManagedAgent` execution path — call `RunCompletedAsync` (terminal), `ToolCallAsync` (per
  tool loop step), `IterationCompletedAsync` (per design/review iteration). Depends on 32-5.
- 32-7/32-8 seams — `PanelAggregatedAsync` (`AggregatePanelActivity`), `BugRecordedAsync`
  (review/gate). Forward-compatible: if those land later, ship the calls behind their seam and stub.
- `Services/Diagnostics/DiagnosticsService.cs` (or the run that calls `RecordEventAsync`) — set
  `ProviderDiagnostic.CorrelationId` = run `correlationId` and `AgentType` = role so AC 8's link
  holds.

**Tests first:**
- [ ] `tests/Tamma.Api.Tests/Agents/AgentTrailDiagnosticsLinkTests.cs` — a run's `AGENT.TASK.*`
  event and its `ProviderDiagnostic` rows share `correlationId` + `agentId`; re-key by agent yields
  expected rollup.
- [ ] Run-completion emits exactly one terminal `AGENT.TASK.*`; tool steps and iterations each emit.

**Acceptance:**
- [ ] One `correlationId` spans trail + diagnostics for a run.
- [ ] Producer wiring does not change run semantics (run still completes on trail failure — T1 contract).

### T3: Tenant-scoped query API

**Scope:** `QueryAgentTrailAsync` + two member-readable endpoints under `/api/v1/orgs/{tenantId}`.

**Files:**
- Modify `Tamma.Data/Repositories/IEventRepository.cs` — add
  `QueryAgentTrailAsync(Guid tenantId, Guid agentId, string? typePrefix, DateTimeOffset? from,
  DateTimeOffset? to, string? role, string? provider, string? outcome, long? cursor, int limit)`.
- Modify `Tamma.Data/Repositories/EventRepository.cs` — implement it: `tenantDbFactory.CreateAsync`,
  `Where(e => e.TenantId == tid)`, `agentId` Tags JSONB predicate, optional `type` prefix +
  date/role/provider/outcome filters, `WHERE SequenceNumber < cursor` (or `>` per order),
  `OrderByDescending(SequenceNumber)`, `Take(limit)`. **Throw `NotSupportedException` on empty
  tenant** — match the existing paginated-read guard.
- New `Tamma.Api/Endpoints/AgentTrailEndpoints.cs` — `ListRuns` (typePrefix `AGENT.TASK`) and
  `ListTrail` (all `AGENT.`/`REVIEW.BUG.`). `tenantId`/`agentId` route-bound; `nextCursor`/`hasMore`
  wire shape; paging defaults 50 / max 500 (mirror `AlertEndpoints`).
- New `Tamma.Api/Dtos/Agents/AgentTrailDtos.cs` — run + trail-event DTOs.
- Modify `Program.cs` — map both under the `orgs` group with `RequireTenantMembershipFilter`.

**Tests first (`tests/Tamma.Api.Tests/Agents/AgentTrailEndpointsTests.cs`):**
- [ ] `runs` returns only `AGENT.TASK.*` for the agent + tenant; filters (date/role/provider/outcome) apply.
- [ ] `trail` returns the full family stream for the agent + tenant.
- [ ] Cursor: page through >page-size events incl. same-millisecond `CreatedAt`; stable order via
  `SequenceNumber`; no dup/skip at boundaries; `nextCursor`/`hasMore` correct.
- [ ] Member can read; no mutation endpoint exists.

**Acceptance:**
- [ ] Both endpoints page on `SequenceNumber`, filterable per AC 5.
- [ ] Endpoint shape identical between modes; the path-tenant gate + repository scoping enforce isolation.

### T4: Tenant-isolation test suite (the load-bearing AC)

**Scope:** Prove AC 4 across every path. Highest priority — write alongside T3, do not defer.

**Files:** `tests/Tamma.Api.Tests/Agents/AgentTrailIsolationTests.cs`.

**Tests:**
- [ ] Seed trail events for tenant A and B; `GET /orgs/{B}/agents/{agentId}/trail` returns only B's
  rows, never A's.
- [ ] A member of A hitting B's path → `RequireTenantMembershipFilter` rejects (403/404).
- [ ] Platform owner has **no route** to a tenant's trail; `IEventRepository` null-tenant paginated /
  trail read throws `NotSupportedException`.
- [ ] A run of a public/system agent by tenant A leaves a trail only in A's schema — none on CP, none
  visible to platform owner, none to tenant B.

**Acceptance:**
- [ ] Zero cross-tenant trail reads possible via any code path.

---

## Task order & dependencies

T1 → T2 (needs 32-5 producer; soft on 32-7/32-8) ; T3 → T4 (write T4 with T3). T1 is the only hard
prerequisite within the story; T3/T4 only need T1 + the seeded `DomainEvent` shape. Story-level
hard prerequisites: **32-1** (agent identity) and **32-5** (the producing run). 32-3 (credential
source) and 32-7/32-8 (panel/bug producers) are soft — default/stub if not yet landed.

## Risks

- **Cross-tenant leakage (the #1 invariant):** mitigated by schema-per-tenant routing +
  `NotSupportedException` null-tenant guards + `RequireTenantMembershipFilter` + the T4 suite.
  Watch: any future "admin view all agent runs" request must fan out per-tenant explicitly — do NOT
  relax the repository guard. The platform admin owning a public agent definition must still see zero
  tenant data; pin this in T4.
- **Trail on the hot run path:** every step appends a row. Tool-heavy runs can be chatty — keep `Data`
  blob-referenced and lean; the non-blocking emitter must use a short append timeout so a slow tenant
  DB never stalls a run. If volume becomes a problem, batch emission per run (cheap follow-up) — not
  in scope now.
- **Identity before 32-1:** `agentId`/`agentVersion` come from resolved-config identity until the
  entity lands; ensure the tag value is stable (the join key for all of 32-10) — a churning id breaks
  history. Treat 32-1 as a hard gate before merge.
- **Cursor correctness on bursts:** `SequenceNumber` (not `CreatedAt`) is the cursor — the existing
  `QueryWithPaginationAsync` already tie-breaks on it; match exactly. Same-millisecond burst test in
  T3 is the guard.
- **Diagnostics link drift:** if the run forgets to thread `correlationId`/`AgentType` into
  `RecordEventAsync`, 32-9/32-10 can't re-key by agent. AC 8's link-integrity test is the tripwire.
- **Redaction completeness:** a missed sanitization path leaks prompt/secret content into an immutable
  event stream (un-deletable audit). Route ALL `Data` through the sanitizer in the emitter (one
  choke point), and assert it in T1's redaction test.

## Verification

- `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~Agents.AgentTrail"`
  green (isolation, emitter, endpoints, diagnostics-link).
- Full `apps/tamma-elsa` suite stays green (no regression).
- Manual: run a managed agent in a dev tenant, hit `GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs`
  and `/trail`, confirm events land in the tenant schema only and a second tenant sees nothing.
