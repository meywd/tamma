# Story 32-9 — Agent Usage & Cost-Basis Emission (Producer) — Implementation Plan

**Date:** 2026-06-17
**Story:** [32-9](../../stories/epic-32/story-32-9/32-9-cost-basis-plus-margin-metering-and-byok-pricing-model.md)
**Epic:** 32 (Agents — first-class entities, managed execution, benchmarking & learning)
**Design spec:** [2026-06-17-agent-entities-benchmarking-design.md](../specs/2026-06-17-agent-entities-benchmarking-design.md)
**Stack:** C# `apps/tamma-elsa` (Tamma.Api / Tamma.Data / Tamma.Activities). `packages/api` is deleted — never target it.

## Goal

Make every LLM call attributable to an agent run **emit a per-call usage + cost-basis DCB event**
(`AGENT.USAGE.RECORDED`) into the calling **tenant's own** schema, tagged with
`agentId` / `tenant` / `provider` / `model` / `credentialSource`, and **roll it up per billing
period** (`agent_usage_rollup`) behind a tenant-scoped read endpoint. This is the **producer** that
Epic 34 (pricing/markup), Epic 35 (billing), and Epic 36 (analytics) consume.

## Non-goals (YAGNI / boundary guard)

- **No markup / margin / billable computation.** `billableUsd`, `marginPct`, seat/platform fee, plan
  branching on `credentialSource` → **all Epic 34-5**. This story emits `costBasisUsd` (provider cost,
  already computed) + the `credentialSource` discriminator and stops there. An automated test
  (Phase 1) fails if a pricing field appears in the event `Data`.
- **No re-pricing of past periods**, no invoicing, no dashboards — Epic 34/35/36.
- **No new pricing path.** Reuse `IProviderPricingService` / `ProviderDiagnostic.Cost` as the single
  cost-basis source of truth; do not recompute.
- **No new event infrastructure or new tenancy plumbing.** Reuse `DomainEvent`, `IEventRepository`,
  `ITenantDbContextFactory`, `SequenceNumber`.
- **No Epic 20 code.** 20-3/20-4/20-5 live in the deleted TS `packages/api`; the re-target is a
  **documentation note** only.
- **No `Plan.cs` / quota changes** for pricing here. (Limit-enforcement against budgets is 32-5's
  fail-closed gate; this story only produces the cost-basis signal it can read.)

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### The cost basis already exists — promote it, don't compute it

- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService.cs` —
  `decimal Compute(string provider, string? model, int inputTokens, int outputTokens)`; unknown
  `(provider, model)` → `0m`. `ProviderPricingService` is a frozen rate table ported from the TS
  `packages/cost-monitor`.
- `ProviderSessionService.ExecuteAsync` (`src/Tamma.Api/Services/Providers/ProviderSessionService.cs`,
  ~lines 122–135) already writes `new ProviderDiagnostic { … InputTokens, OutputTokens, Cost =
  invocation.CostUsd, TenantId, CorrelationId?, AgentType? … }` via `IDiagnosticsService.RecordEventAsync`.
  The SaaS path (`Services/SaaS/LlmProxyService.cs`) does the same with `EstimateCost`.
  → **`ProviderDiagnostic.Cost` IS the cost basis.** 32-9 reads it; it does not re-derive a price.
- `src/Tamma.Data/Entities/ProviderDiagnostic.cs` carries `InputTokens`, `OutputTokens`, `Cost`,
  `Model`, `TenantId`, `CorrelationId`, `AgentType` — every dimension this story needs already lands
  on the diagnostic. No `ProviderDiagnostic` schema change required.

### DCB emission + tenant isolation are reusable as-is

- `src/Tamma.Data/Entities/DomainEvent.cs` — `Id, Type, TenantId, IssueNumber, Tags(JSONB),
  Metadata(JSONB), Data(JSONB), CreatedAt, SequenceNumber(BIGSERIAL cursor)`. Reuse unchanged.
- `src/Tamma.Data/Repositories/EventRepository.cs` — `AppendAsync` resolves
  `evt.TenantId ?? tenantContext.TenantId` and writes through `ITenantDbContextFactory` into the
  `t_<hex>` schema; tenant-scope isolation is **structural**. A tenant-scoped (`TenantId != null`)
  event physically cannot land anywhere but that tenant's schema. This is the isolation backbone.
- Emission precedent: `src/Tamma.Api/Endpoints/AgentEndpoints.cs` `UpdateConfig` (~lines 93–113)
  appends `AGENT_CONFIG.UPDATED.SUCCESS` with flat-string `Tags`, standard `Metadata`
  (`workflowVersion`, `eventSource = "system"`), `Data` payload — copy this shape.

### 32-6 is the sibling to mirror (already drafted)

- `docs/stories/epic-32/story-32-6/…` ships `AgentTrailEmitter` / `AgentTrailTags` /
  `IEventRepository` tenant-scoped emission, the `correlationId` linkage to `ProviderDiagnostic`, and
  the **non-blocking** contract (`AGENT.TRAIL.WRITE_FAILED` breadcrumb). 32-9's emitter is a near-twin
  (`AgentUsageEmitter` / `AgentUsageTags` / `AGENT.USAGE.EMIT_FAILED`). Match its conventions.

### `credentialSource` comes from 32-3

- `docs/stories/epic-32/story-32-3/…` introduces `IProviderCredentialResolver` →
  `ProviderCredential { Source ∈ {Byok, Platform} }`, surfaced as `ProviderAttemptDiagnostic.
  CredentialSource` and the `credentialSource` diagnostic/trail tag. 32-9 reads that tag; default
  `platform` until 32-3 is wired. Today only `BudgetConfig.cs` mentions BYOK — no `credentialSource`
  in the call path yet (confirms the 32-3 dependency).

### The 32-5 managed run is the producer host

- 32-5 (`IManagedAgent` over `ILLMProvider`) owns the LLM call and the `correlationId`. It is where
  `IAgentUsageEmitter.RecordCallAsync` is called once per logical call. Until 32-5 lands, the emitter
  + rollup + endpoint are independently testable; the run-loop wiring is the only piece gated on it.
- `CallLlmInlineActivity` already accumulates tool-loop tokens (`ToolLoopTokens` / `PromptTokens` +
  `CompletionTokens`) → "one logical call = one usage event" is natural (emit once with accumulated
  totals).

### Agent identity

- `src/Tamma.Data/Entities/AgentConfig.cs` exists (tenant-nullable config). The immutable
  `Agent`/`AgentVersion` entity is NEW in 32-1; until then, source `agentId`/`agentVersion` from the
  resolved config identity.

### Tenant migrations

- Tenant EF model: `src/Tamma.Data/TenantDbContext.cs` (+ `Migrations/Tenant/…`,
  `TenantDbContextModelSnapshot.cs`). New tenant tables (`agent_usage_rollup`, `agent_usage_applied`)
  go here, scaffolded into the Tenant migration set.

## Architecture (what we add)

```
32-5 managed run ── one logical LLM call ─────────────────────────────────────────┐
  ProviderDiagnostic { Cost = pricing.Compute(...), InputTokens, OutputTokens,     │  (exists)
                       CorrelationId, AgentType }                                   │
                                                                                    ▼
  IAgentUsageEmitter.RecordCallAsync(AgentUsageContext, AgentCallUsage)   ◄── NEW (producer)
     → DomainEvent "AGENT.USAGE.RECORDED" { TenantId, Tags{agentId,provider,model,
              credentialSource,correlationId,mode,…}, Data{inputTokens,outputTokens,
              costBasisUsd,…}  ── COST BASIS ONLY }
     → IEventRepository.AppendAsync  → t_<hex>.domain_events     (structural isolation)
                                                                                    │
  AgentUsageRollupProjector.ApplyAsync(evt)  ◄── NEW (idempotent on event id)       │
     → upsert agent_usage_rollup (per period × agent × provider × credentialSource) │
                                                                                    ▼
  GET /api/v1/orgs/{tenantId}/agents/usage  ◄── NEW (MemberAccess, tenant-scoped)
     → { byAgent[], totals }   ── NO billableUsd on the wire

Downstream (NOT here): Epic 34-5 markup → billableUsd; Epic 35 billing; Epic 36 analytics.
```

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md "Operating Modes")

- **single-user:** the sole user owns everything; `credentialSource` is `platform` (local/platform key);
  events tag `mode = single-user`; the tenant is the user's personal tenant; usage rollup is the user's.
- **saas:** usage data is **ALWAYS tenant-scoped** (Epic 32 tenancy rule); `credentialSource` is
  `byok | platform` from 32-3; events tag `mode = saas`; a public/system agent run by a tenant produces
  a rollup only in **that tenant's** schema; platform admin has **no** read path to any tenant's usage.

## Phased TDD tasks

Each phase: write the failing tests first (Red), implement (Green), refactor. Docker-bound C# suites
run via `sg docker -c "dotnet test ..."`; the build itself needs no wrapper.

### Phase 1 — Emitter + types + non-blocking contract (the producer core)

**Files (NEW):** `Services/Agents/IAgentUsageEmitter.cs`, `AgentUsageEmitter.cs`,
`AgentUsageContext.cs` (`AgentUsageContext` + `AgentCallUsage` records), `AgentUsageTags.cs`,
`AgentUsageEventTypes.cs` (`AGENT.USAGE.RECORDED`, `AGENT.USAGE.EMIT_FAILED`).
**Modify:** `Program.cs` (DI: `AddScoped<IAgentUsageEmitter, AgentUsageEmitter>`).

**Approach:** `RecordCallAsync` builds one `DomainEvent` (Tags = identity dims via `AgentUsageTags.
Build`; `Data` = `{inputTokens, outputTokens, totalTokens, costBasisUsd, model, provider,
credentialSource, durationMs}`) and `AppendAsync`. Wrap in try/catch that logs + emits
`AGENT.USAGE.EMIT_FAILED` and **never rethrows** (mirror `AgentTrailEmitter`).

**Tests** (`tests/Tamma.Api.Tests/Agents/AgentUsageEmitterTests.cs`,
`AgentUsageNoPricingLeakageTests.cs`, fake `IEventRepository`):
- Happy path → exactly one `AGENT.USAGE.RECORDED`; all required `Tags` keys present (AC 2);
  `Data` token split + `costBasisUsd` correct (AC 3).
- **No-pricing-leakage (AC 11):** deserialize emitted `Data` → assert `costBasisUsd` present and
  `billableUsd`/`marginPct`/`feeUsd` **absent**. (Write this test first.)
- Non-blocking (AC 9): `AppendAsync` throws → `RecordCallAsync` does not throw; `AGENT.USAGE.
  EMIT_FAILED` breadcrumb attempted.

### Phase 2 — Per-period rollup projection (idempotent)

**Files (NEW):** `Data/Entities/AgentUsageRollup.cs`, `Data/Entities/AgentUsageApplied.cs`,
`Services/Agents/AgentUsageRollupProjector.cs`, `Migrations/Tenant/*_AddAgentUsageRollup.cs`.
**Modify:** `Data/TenantDbContext.cs` (DbSets + model config; unique
`(agent_id, provider, credential_source, period_start)`).

**Approach:** `ApplyAsync(DomainEvent)` — insert event id into `agent_usage_applied` first; if it
already exists, no-op (idempotent, AC 6). Else parse Tags/Data, `date_trunc('month')` the period,
upsert the per-`(agent, provider, credentialSource, period)` bucket adding tokens / `costBasisUsd` /
`callCount`. Cost-basis columns only — **no billable column**. Run the projector from the emission
path (apply-on-append) so it is replay-safe.

**Tests** (`AgentUsageRollupTests.cs`, in-memory/test tenant DB):
- Apply N distinct events → summed correctly, bucketed by `(agent, provider, credentialSource, period)`.
- **Idempotency:** apply the same event id twice → counted once (AC 6).
- BYOK vs platform kept in separate buckets, same provider rate-sheet `costBasisUsd` (AC 4).

### Phase 3 — Tenant-scoped read endpoint

**Files (NEW):** `Endpoints/AgentUsageEndpoints.cs`, `Dtos/Agents/AgentUsageDtos.cs`.
**Modify:** `Program.cs` (map `GET /api/v1/orgs/{tenantId}/agents/usage` under the orgs group with
`RequireTenantMembershipFilter` / `MemberAccess`).

**Approach:** read `agent_usage_rollup` for the route tenant + current calendar month; project to
`{ periodStart, periodEnd, byAgent[], totals }`. **No `billableUsd` field** on the wire (AC 7).
Isolation is inherited (tenant-scoped store + path-tenant gate).

**Tests** (`AgentUsageEndpointsTests.cs`, `AgentUsageIsolationTests.cs`):
- Returns current-period breakdown by agent + provider + credentialSource; totals correct.
- **Isolation (AC 8, highest priority):** B's path returns only B; member of A on B's path → 403/404;
  platform owner → no read path to either; public-agent run by A → rollup only in A's schema.

### Phase 4 — Wire emission into the 32-5 managed run + correlation linkage

**Modify:** the 32-5 `IManagedAgent` execution path (gated on 32-5) — call `RecordCallAsync` once per
logical LLM call (single-turn and accumulated tool-loop), sourcing `CostBasisUsd` from the
diagnostic's `Cost` (the `IProviderPricingService.Compute` value — **no recompute**), threading the
run's single `correlationId` + `agentId` through the diagnostic, the 32-6 trail event, and the usage
tag; set `credentialSource` from 32-3's `ProviderCredential.Source` (default `platform`).

**Tests** (`AgentUsageEmitterTests.cs` integration slice):
- One-call-one-event for single-turn and tool-loop (AC 1).
- **Link integrity (AC 5):** trail event + `ProviderDiagnostic` + `AGENT.USAGE.RECORDED` share
  `(correlationId, agentId)`.
- **Cost-basis parity (AC 4):** `Data.costBasisUsd == IProviderPricingService.Compute(provider, model,
  in, out)`.

### Phase 5 — Epic 20 re-target note (docs only)

**Modify:** this story's AC 12 note + a one-line pointer in the Epic 20 20-3/20-4/20-5 story files
("Superseded by 32-9 agent-aware cost-basis producer on the C# stack; `workflow_runs`/`llm_tokens` →
`callCount`/`totalTokens` in `agent_usage_rollup`"). **No Epic 20 code** is created/modified — it does
not exist in this stack.

**Tests:** documentation parity check only (no Epic 20 code to exercise).

## Sequencing & dependencies

1. **Phase 1 → Phase 2 → Phase 3** are independently shippable against the existing event store and a
   test tenant DB — **not gated on 32-5**. Build and test these first.
2. **Phase 4** (run-loop wiring) is gated on **32-5** (the managed run) and reads **32-3**'s
   `credentialSource` + **32-6**'s `correlationId`. If 32-5/32-3 lag, ship Phases 1-3 and stub the
   call site (default `credentialSource = platform`, `correlationId` from the run).
2. **32-1** supplies `agentId`/`agentVersion`; source from resolved config identity until it lands.
3. **Phase 5** is doc-only, do any time.
4. **Downstream:** Epic 34-5 / 35 / 36 consume the output; nothing in this story waits on them.

## Risks

| Risk | Severity | Mitigation |
| ---- | --------- | ---------- |
| Pricing/markup logic leaks into the producer (34-5 boundary breach) | High | Cost-basis-only `Data`; no `Plan`/margin reads; AC 11 no-leakage test written **first** (Phase 1) |
| Cross-tenant usage leakage | Critical | Tenant-scoped store only; `RequireTenantMembershipFilter`; no `OwnerAccess` route; Phase 3 isolation suite (incl. public-agent + platform-admin-denied) |
| Double-counted usage on replay/retry | High | `agent_usage_applied` idempotency ledger keyed by event id; Phase 2 replay test |
| Usage write aborts a real agent run | High | Non-blocking emitter (swallow + retry + `AGENT.USAGE.EMIT_FAILED`); Phase 1 failure-injection test |
| Cost basis drifts from the rest of the system | Medium | Single pricing seam reused (`ProviderDiagnostic.Cost`); Phase 4 parity test |
| `credentialSource` / `agentId` unavailable before 32-3 / 32-1 | Medium | Default `credentialSource=platform`; source identity from resolved config; correctness tests once deps wire |
| 32-5 not yet landed | Medium | Phases 1-3 ship + test standalone; Phase 4 is the only gated piece |

## Acceptance criteria (definition of done)

- [ ] Every agent LLM call emits exactly one `AGENT.USAGE.RECORDED` in the resolving tenant's schema
      (single-turn + tool-loop), `Tags` + `Data` complete, `Data.costBasisUsd == IProviderPricingService.
      Compute(...)`, and **no** `billableUsd`/margin/fee field anywhere (AC 1-4, 11 tests green).
- [ ] `AGENT.USAGE.RECORDED`, its `ProviderDiagnostic`, and the 32-6 `AGENT.TASK.*` trail event share
      `(correlationId, agentId)` (AC 5 link test green).
- [ ] `agent_usage_rollup` aggregates per period × agent × provider × credentialSource, **idempotent on
      event id** (AC 6 replay test green).
- [ ] `GET /api/v1/orgs/{tenantId}/agents/usage` returns current-period byAgent/provider/credentialSource
      breakdown (cost basis only, no `billableUsd`), member-readable, strictly tenant-isolated incl.
      platform-admin-denied and public-agent run (AC 7, 8 isolation suite green).
- [ ] Usage emission is non-blocking: induced append failure does not fail the run; `AGENT.USAGE.
      EMIT_FAILED` breadcrumb emitted (AC 9 test green).
- [ ] Both DCB types follow `AGGREGATE.ACTION.STATUS`; no secret/raw-prompt content persisted (AC 10).
- [ ] Epic 20 re-target note present; **no Epic 20 code** created/modified (AC 12).
- [ ] Full `apps/tamma-elsa` suite green via `sg docker -c "dotnet test ..."`; no regression in existing
      diagnostics / `CallLlmInlineActivity` tests.
</content>
