# Story 32-9: Agent Usage & Cost-Basis Emission (Producer for Pricing/Billing/Analytics)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

> **Boundary note (read first — this story is the PRODUCER, not the pricing engine):**
> 32-9 makes agents **emit** a per-LLM-call usage + **cost-basis** event, tagged with
> `agentId` / `tenant` / `provider` / `model` / `credentialSource`, into the tenant's DCB stream
> and aggregate it per billing period. It is the data **source** consumed by **Epic 34 (pricing /
> markup)**, **Epic 35 (billing / invoicing)**, and **Epic 36 (analytics)**.
> It does **NOT** implement any markup, margin, or billable-amount computation. The
> **margin/markup engine is owned by Epic 34-5** — branching cost-basis into a billable amount,
> applying BYOK-vs-platform pricing rules, and re-pricing periods all live there. This story
> emits `costBasisUsd` (the provider's own cost, already computed by `IProviderPricingService`)
> and the `credentialSource` discriminator so 34-5 can branch; it never computes `billableUsd`,
> a margin, or a seat/platform fee. (The spec key/title carries legacy "+margin / billable" framing
> from when this story was scoped against Epic 20; the Epic 32 design spec §"Story breakdown" item 9
> and §"Tracking" are canonical — **producer only, markup engine is 34-5**.)

## User Story

As **Epic 34 (pricing), Epic 35 (billing), and Epic 36 (analytics)** — and the tenant owner who
will eventually see a usage/cost view —
I want every LLM call made on behalf of an agent to emit a per-call **usage + cost-basis** event
into the calling tenant's own DCB stream, tagged with the agent's identity, provider, model, token
split, the provider's cost basis in USD, and whether the call ran on a **BYOK** or **platform**
credential, and rolled up per billing period,
So that the downstream pricing/billing/analytics epics have a complete, tenant-isolated, per-agent
cost-basis substrate to apply markup, invoice, and report against — without this story making any
pricing decision.

## Priority

P1 — The usage/cost producer the Epic 32 benchmarking + the Epic 34/35/36 consumers depend on. Cost
basis is already computed on every diagnostic; this story turns it into a first-class, agent-keyed,
per-period DCB usage signal. Depends on 32-3 (`credentialSource`), 32-5 (the managed run that owns the
call), and 32-6 (the action-trail substrate and `correlationId` linkage this re-keys against).

## Acceptance Criteria

1. Every completed LLM call attributable to an agent run emits exactly one **`AGENT.USAGE.RECORDED`**
   DCB `DomainEvent` into the **resolving tenant's** schema (`t_<hex>.domain_events` via
   `IEventRepository.AppendAsync`), never on the control plane. `TenantId` = the resolving tenant.
   One call → one usage event (a multi-turn tool loop is one logical call = one event carrying the
   loop's accumulated tokens, mirroring how `CallLlmInlineActivity` accumulates `ToolLoopTokens`).
2. The event's `Tags` JSONB carries (flat string values, shared builder): `agentId`, `agentVersion`,
   `role`, `provider`, `model`, `credentialSource` (`byok` | `platform`), `correlationId`, `issueId`,
   and `mode` (`single-user` | `saas`). These are the dimensions Epics 34/35/36 slice on.
3. The event's `Data` carries the **cost basis only**: `{ inputTokens, outputTokens, totalTokens,
   costBasisUsd, model, provider, credentialSource, durationMs }`. `costBasisUsd` is the provider's
   own USD cost as computed today by `IProviderPricingService.Compute(provider, model, in, out)` —
   the value already written to `ProviderDiagnostic.Cost`. **No `billableUsd`, no margin, no markup,
   no seat/platform fee** is computed or stored here (those are Epic 34-5).
4. The cost basis is **never recomputed** in this story — it reuses the single pricing seam
   (`IProviderPricingService`) so there is one cost-basis source of truth. For a BYOK call the same
   provider rate-sheet cost basis is recorded (BYOK changes *who pays the provider*, captured by
   `credentialSource`, not the per-token rate); 34-5 decides what, if anything, a BYOK call bills.
5. Each usage event is **linked to the action trail (32-6) and its diagnostic row** by sharing one
   `correlationId` + `agentId`: a run's `AGENT.TASK.*` trail event, its `ProviderDiagnostic` row(s),
   and its `AGENT.USAGE.RECORDED` event(s) all carry the same `correlationId` and resolve to the same
   `agentId`. Re-keying usage by agent for benchmarking (32-10) is then a join on `(correlationId,
   agentId)`.
6. Per-call usage is **aggregated per billing period per (agent, provider, credentialSource)** into a
   tenant-resident projection (`agent_usage_rollup` in the tenant schema), maintained from the
   `AGENT.USAGE.RECORDED` stream. The rollup is **idempotent on the event id** (replaying an event
   does not double-count). It stores summed `inputTokens`, `outputTokens`, `totalTokens`,
   `costBasisUsd`, and `callCount` — **cost basis only, no billable column**.
7. A tenant-scoped read endpoint **`GET /api/v1/orgs/{tenantId}/agents/usage`** returns the
   current-period usage broken down by **agent** and **provider** (and `credentialSource`):
   `{ periodStart, periodEnd, byAgent: [{ agentId, provider, credentialSource, inputTokens,
   outputTokens, totalTokens, costBasisUsd, callCount }], totals: {…} }`. Read is **member-level**
   (any tenant member); there is **no `billableUsd` field** on the wire — the billable view is an
   Epic 34/35 concern over this data.
8. The endpoint and rollup are **strictly tenant-isolated**: reads go through the tenant-scoped store
   only; a platform owner calling another tenant's usage path gets 403/404, never that tenant's
   numbers — even when the run used a public/system agent definition (one public agent → N
   independent per-tenant usage datasets, per the Epic 32 tenancy rule). Explicitly tested.
9. Usage emission is **best-effort-non-blocking** for the agent run: a usage-write failure does **not**
   abort or fail the run, is retried via the durable append path, and on terminal failure surfaces an
   **`AGENT.USAGE.EMIT_FAILED`** breadcrumb (or WARN log + metric) so the gap is observable, never
   silent. (Mirrors the 32-6 trail non-blocking contract.)
10. DCB events are emitted via `IEventRepository.AppendAsync` and follow `AGGREGATE.ACTION.STATUS`:
    `AGENT.USAGE.RECORDED` (per call) and `AGENT.USAGE.EMIT_FAILED` (terminal write failure). Tokens
    and `costBasisUsd` are in `Data`; identity dimensions are in `Tags`. No secret/raw-prompt content
    is persisted (reuse the sanitization/redaction seam).
11. **No pricing leakage:** there is **no code in this story** that reads/writes a margin, computes a
    billable amount, branches the *price* on `credentialSource`, or touches `Plan.Quotas`/`Plan.
    MonthlyPriceUsd` for pricing. A test asserts the emitted `Data` schema contains `costBasisUsd` and
    **not** `billableUsd`/`marginPct`/`feeUsd`, guarding the 34-5 boundary.
12. **Re-targets Epic 20 (documentation only):** a short note records that Epic 20's 20-3 (usage
    metering), 20-4 (limit enforcement), and 20-5 (billing dashboard) — written against the deleted
    TS `packages/api` flat per-token meter — are **superseded** by this agent-aware, cost-basis,
    DCB-native producer on the C# `apps/tamma-elsa` stack. The note states 20-3's `workflow_runs` /
    `llm_tokens` equivalents map to `callCount` / `totalTokens` in `agent_usage_rollup`; no Epic 20
    code is created or modified (it does not exist in this stack).
13. **Tests** cover: one-call-one-event (single-turn and tool-loop), tag/data completeness,
    cost-basis-equals-pricing-seam parity, `(correlationId, agentId)` link integrity across trail +
    diagnostic + usage, rollup idempotency on replay, BYOK vs platform `credentialSource` recorded
    correctly at both branches, tenant isolation (B cannot read A; platform admin cannot read either),
    non-blocking emit failure, and the no-pricing-leakage schema assertion (AC 11).

## Technical Design

### Where this sits (architecture)

The cost **basis** already exists: every provider invocation records a `ProviderDiagnostic` whose
`Cost` is `IProviderPricingService.Compute(provider, model, inputTokens, outputTokens)` — verified in
`ProviderSessionService.ExecuteAsync` (`new ProviderDiagnostic { … Cost = invocation.CostUsd … }`,
lines ~122–135) and in the SaaS `LlmProxyService` path. This story does **not** add a meter or a price;
it **promotes that cost basis into an agent-keyed DCB usage event** in the tenant stream and rolls it
up per period, so 34/35/36 can consume a clean per-agent signal instead of scraping diagnostics.

```
Managed agent run (32-5 IManagedAgent)  ── one logical LLM call ──┐
  │ provider invoked; ProviderDiagnostic { Cost = pricing.Compute(...), CorrelationId, AgentType }
  │
  ├─ AgentUsageEmitter.RecordCallAsync(ctx, usage)            // PRODUCER (this story)
  │     → DomainEvent { Type="AGENT.USAGE.RECORDED",
  │                     TenantId = resolving tenant,           // structural isolation
  │                     Tags  = {agentId, provider, model, credentialSource, correlationId, mode,…},
  │                     Data  = {inputTokens, outputTokens, costBasisUsd, …} }  // cost BASIS only
  │     → IEventRepository.AppendAsync(evt)                    // t_<hex>.domain_events
  │
  └─ AgentUsageRollupProjector  (consumes AGENT.USAGE.RECORDED, idempotent on event id)
        → upsert agent_usage_rollup (per period, per agent/provider/credentialSource)

   Downstream (NOT this story):
     Epic 34-5  markup/margin engine → billableUsd = f(costBasisUsd, credentialSource, plan)
     Epic 35    invoicing/billing dashboard
     Epic 36    analytics / leaderboards
```

The usage event reuses the existing `Tamma.Data.Entities.DomainEvent` row shape (no event-schema
change) and the existing tenant-scoped `IEventRepository` — isolation is inherited from the unified
schema-per-tenant model (32-6 / Epic 28), not re-implemented.

### Emitter seam (single producer site)

A thin injectable emitter is the one seam the 32-5 managed run calls per LLM call. It owns tag/data
shape, cost-basis sourcing (from the diagnostic / pricing seam — never a fresh computation), and the
non-blocking contract.

```csharp
// src/Tamma.Api/Services/Agents/AgentUsageContext.cs
public sealed record AgentUsageContext(
    Guid    TenantId,
    Guid    AgentId,
    int     AgentVersion,
    string  Role,
    string  Provider,
    string? Model,
    string  CredentialSource,   // "byok" | "platform" — from 32-3 ProviderCredential.Source
    Guid    CorrelationId,      // shared with 32-6 trail + ProviderDiagnostic
    string? IssueId,
    int?    IssueNumber,
    string  Mode);              // "single-user" | "saas"

// The measured facts of one logical call (single-turn or accumulated tool loop).
public sealed record AgentCallUsage(
    int     InputTokens,
    int     OutputTokens,
    decimal CostBasisUsd,       // == IProviderPricingService.Compute(...) / ProviderDiagnostic.Cost
    double  DurationMs)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

// src/Tamma.Api/Services/Agents/IAgentUsageEmitter.cs
public interface IAgentUsageEmitter
{
    /// <summary>
    /// Emit one AGENT.USAGE.RECORDED event for a completed LLM call. Non-blocking:
    /// never throws into the agent run (AC 9). Records cost BASIS only — no markup.
    /// </summary>
    Task RecordCallAsync(AgentUsageContext ctx, AgentCallUsage usage, CancellationToken ct = default);
}
```

```csharp
// src/Tamma.Api/Services/Agents/AgentUsageEmitter.cs
public sealed class AgentUsageEmitter(
    IEventRepository events,
    ILogger<AgentUsageEmitter> logger) : IAgentUsageEmitter
{
    public async Task RecordCallAsync(AgentUsageContext c, AgentCallUsage u, CancellationToken ct = default)
    {
        try
        {
            await events.AppendAsync(new DomainEvent
            {
                Id       = Guid.NewGuid(),
                Type     = AgentUsageEventTypes.UsageRecorded,   // "AGENT.USAGE.RECORDED"
                TenantId = c.TenantId,                            // resolving tenant — structural isolation
                IssueNumber = c.IssueNumber,
                Tags     = AgentUsageTags.Build(c),               // flat string dims (AC 2)
                Metadata = StandardMetadata(),                    // { workflowVersion, eventSource = "system" }
                Data     = JsonSerializer.Serialize(new
                {
                    inputTokens      = u.InputTokens,
                    outputTokens     = u.OutputTokens,
                    totalTokens      = u.TotalTokens,
                    costBasisUsd     = u.CostBasisUsd,            // BASIS ONLY — no billableUsd (AC 3, 11)
                    model            = c.Model,
                    provider         = c.Provider,
                    credentialSource = c.CredentialSource,
                    durationMs       = u.DurationMs,
                }),
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            // Non-blocking (AC 9): log + best-effort breadcrumb; never throw into the run.
            logger.LogWarning(ex,
                "Agent usage emit failed agent={AgentId} provider={Provider} corr={Corr}",
                c.AgentId, c.Provider, c.CorrelationId);
            await TryEmitFailedBreadcrumbAsync(c, ex, ct);        // AGENT.USAGE.EMIT_FAILED, swallows
        }
    }
}
```

`AgentUsageTags.Build` mirrors `AgentTrailTags.Build` (32-6) and the `Tags` convention already used by
`AgentEndpoints.UpdateConfig` (flat string dict, serialized). `AgentUsageEventTypes` holds the two
type constants.

### Cost-basis sourcing (one source of truth — AC 4)

The emitter is handed `CostBasisUsd` from the same value the diagnostic uses; it does **not** call
pricing itself differently:

```csharp
// In the 32-5 managed run, after the provider call (single-turn or tool-loop):
var usage = new AgentCallUsage(
    InputTokens:  diag.InputTokens,      // ProviderDiagnostic / ProviderAttemptTokens
    OutputTokens: diag.OutputTokens,
    CostBasisUsd: diag.Cost,             // == IProviderPricingService.Compute(provider, model, in, out)
    DurationMs:   diag.RequestDurationMs);
await _usageEmitter.RecordCallAsync(usageCtx, usage, ct);
```

For BYOK, `diag.Cost` is still the provider rate-sheet cost basis (what the call *would* cost at list
price); `credentialSource = byok` records that the tenant's own account was billed by the provider.
Whether a BYOK call is charged anything by us is **entirely Epic 34-5's call** — this story only
records the basis + the discriminator.

### Per-period rollup projection (AC 6)

A tenant-resident projection keeps a current/period rollup so the read endpoint (AC 7) is O(1) and
34/35/36 can read aggregates without scanning the event stream. New tenant-schema table; no control-
plane row.

```sql
-- tenant schema t_<hex>; created by the Tenant EF migration for this story
CREATE TABLE IF NOT EXISTS agent_usage_rollup (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_id          UUID        NOT NULL,
  provider          TEXT        NOT NULL,
  credential_source TEXT        NOT NULL,           -- 'byok' | 'platform'
  period_start      TIMESTAMPTZ NOT NULL,
  period_end        TIMESTAMPTZ NOT NULL,
  input_tokens      BIGINT      NOT NULL DEFAULT 0,
  output_tokens     BIGINT      NOT NULL DEFAULT 0,
  total_tokens      BIGINT      NOT NULL DEFAULT 0,
  cost_basis_usd    NUMERIC(18,6) NOT NULL DEFAULT 0,  -- BASIS ONLY; no billable column (AC 6, 11)
  call_count        BIGINT      NOT NULL DEFAULT 0,
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (agent_id, provider, credential_source, period_start)
);

-- Idempotency ledger so an event id is never folded twice (AC 6).
CREATE TABLE IF NOT EXISTS agent_usage_applied (
  event_id   UUID PRIMARY KEY,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

```csharp
// src/Tamma.Api/Services/Agents/AgentUsageRollupProjector.cs
public sealed class AgentUsageRollupProjector(ITenantDbContextFactory tenantDb, ITenantContext tenant)
{
    /// <summary>
    /// Fold one AGENT.USAGE.RECORDED event into agent_usage_rollup. Idempotent on event id:
    /// inserts into agent_usage_applied first; a duplicate (already-applied) event is a no-op.
    /// </summary>
    public Task ApplyAsync(DomainEvent usageEvent, CancellationToken ct = default);
}
```

The projector runs in-process from the same emission path (apply-on-append) and is replay-safe, so a
future stream rebuild reproduces the rollup exactly. Period boundaries default to calendar-month
(`date_trunc('month', …)`), matching the 20-3 convention; the period model is intentionally simple —
34/35 own real billing cycles.

### Read endpoint (AC 7, 8)

```csharp
// src/Tamma.Api/Endpoints/AgentUsageEndpoints.cs
// GET /api/v1/orgs/{tenantId}/agents/usage?period=current
public static async Task<IResult> GetCurrentUsage(
    HttpContext http, ITenantDbContextFactory tenantDb, ITenantContext tenantContext,
    Guid tenantId, string? period = "current")
{
    // tenantId is route-bound and validated by RequireTenantMembershipFilter (MemberAccess);
    // the read is physically scoped to that tenant's schema via ITenantDbContextFactory.
    var (start, end) = ResolvePeriod(period);                 // current calendar month by default
    var rows = await QueryRollupAsync(tenantId, start, end);  // tenant-scoped only

    return Results.Ok(new
    {
        periodStart = start, periodEnd = end,
        byAgent = rows.Select(r => new {
            r.AgentId, r.Provider, r.CredentialSource,
            r.InputTokens, r.OutputTokens, r.TotalTokens,
            costBasisUsd = r.CostBasisUsd,                     // NO billableUsd on the wire (AC 7, 11)
            r.CallCount }),
        totals = Totals(rows),
    });
}
```

Wired under the existing `/api/v1/orgs/{tenantId}` group with `RequireTenantMembershipFilter` — exactly
like 32-6's trail endpoints. There is **no `OwnerAccess` cross-tenant usage route**; a platform owner
cannot read any tenant's usage (AC 8).

### Diagnostics linkage (AC 5)

No `ProviderDiagnostic` schema change. The managed run threads one `correlationId` through the
`ProviderDiagnostic` (already has `CorrelationId` + `AgentType`), the 32-6 trail event, and the
`AGENT.USAGE.RECORDED` tag — so usage, trail, and cost/latency diagnostics for the same call join on
`(correlationId, agentId)`. This is the seam 32-10's leaderboards re-key off.

### Route + DI wiring (`Program.cs`)

```csharp
builder.Services.AddScoped<IAgentUsageEmitter, AgentUsageEmitter>();
builder.Services.AddScoped<AgentUsageRollupProjector>();

orgs.MapGet("/{tenantId}/agents/usage", AgentUsageEndpoints.GetCurrentUsage)
    .AddEndpointFilter<RequireTenantMembershipFilter>();   // MemberAccess
```

## Tasks / Subtasks

- [ ] Task 1: Usage emitter core (AC 1, 2, 3, 4, 9, 10, 11)
  - [ ] Subtask 1.1: Add `AgentUsageContext`, `AgentCallUsage`, `AgentUsageTags.Build`, `AgentUsageEventTypes` (`AGENT.USAGE.RECORDED`, `AGENT.USAGE.EMIT_FAILED`)
  - [ ] Subtask 1.2: Add `IAgentUsageEmitter` + `AgentUsageEmitter`; `Data` = cost basis only, `Tags` = identity dims; assert no `billableUsd`/`margin` field in serialization
  - [ ] Subtask 1.3: Enforce non-blocking contract — `AGENT.USAGE.EMIT_FAILED` breadcrumb on terminal failure; never throw into the run
  - [ ] Subtask 1.4: Register `IAgentUsageEmitter` in `Program.cs` DI
- [ ] Task 2: Wire emission into the managed run (AC 1, 4, 5)
  - [ ] Subtask 2.1: Call `RecordCallAsync` once per logical LLM call from the 32-5 `IManagedAgent` path (single-turn and accumulated tool-loop), sourcing `CostBasisUsd` from the diagnostic (`IProviderPricingService.Compute` value — no recompute)
  - [ ] Subtask 2.2: Thread the run's single `correlationId` + `agentId` through the diagnostic, the 32-6 trail event, and the usage tag; set `credentialSource` from 32-3's `ProviderCredential.Source` (default `platform` until 32-3 wired)
- [ ] Task 3: Per-period rollup projection (AC 6)
  - [ ] Subtask 3.1: Tenant EF migration: `agent_usage_rollup` + `agent_usage_applied` in the tenant schema
  - [ ] Subtask 3.2: `AgentUsageRollupProjector.ApplyAsync` — idempotent on event id, upsert per `(agentId, provider, credentialSource, periodStart)`; cost-basis-only columns
  - [ ] Subtask 3.3: Replay-safety test (apply same event twice → counted once)
- [ ] Task 4: Tenant-scoped read API (AC 7, 8)
  - [ ] Subtask 4.1: `AgentUsageEndpoints.GetCurrentUsage` + DTOs (byAgent/totals; no `billableUsd`)
  - [ ] Subtask 4.2: Map under `/api/v1/orgs/{tenantId}` with `RequireTenantMembershipFilter`; period resolver (current calendar month default)
- [ ] Task 5: Epic 20 re-target note (AC 12)
  - [ ] Subtask 5.1: Add the supersession note (this file + a short pointer in the Epic 20 stories' "Status"/notes) mapping `workflow_runs`/`llm_tokens` → `callCount`/`totalTokens`; create/modify no Epic 20 code
- [ ] Task 6: Tests (AC 13)
  - [ ] Subtask 6.1: One-call-one-event (single-turn + tool-loop); tag/data completeness; cost-basis == pricing-seam value
  - [ ] Subtask 6.2: Link integrity — trail + diagnostic + usage share `(correlationId, agentId)`
  - [ ] Subtask 6.3: Rollup idempotency on replay; BYOK vs platform `credentialSource` at both branches
  - [ ] Subtask 6.4: Tenant isolation (B≠A; platform admin denied), including a public-agent run
  - [ ] Subtask 6.5: Non-blocking emit failure → run completes + breadcrumb; no-pricing-leakage schema assertion (AC 11)

## Dependencies

**Internal Dependencies:**

- **Story 32-3** (per-tenant provider credential resolution) — supplies `credentialSource`
  (`byok` | `platform`) via `ProviderCredential.Source` / the `credentialSource` diagnostic tag. Hard
  for AC 2/AC 4 correctness; soft-default to `platform` if 32-3 not yet wired.
- **Story 32-5** (managed agent execution layer) — the `IManagedAgent` run is the producer that calls
  `IAgentUsageEmitter` once per LLM call and owns the `correlationId`. Hard prerequisite for Task 2.
- **Story 32-6** (agent action trail) — provides the `correlationId` linkage, the tenant-scoped
  `IEventRepository` emission pattern, the `AgentTrailTags` precedent, and the non-blocking emitter
  contract this story mirrors. Hard for AC 5.
- **Story 32-1** (agent entity model) — supplies the immutable `agentId` + `agentVersion` every usage
  event is tagged with (`AgentConfig.cs` exists today; `Agent`/`AgentVersion` is NEW in 32-1). Source
  from resolved config identity until it lands.
- **Epic 4 / Epic 28** (DCB event store + tenant-scoped store) — `DomainEvent`, `IEventRepository`,
  `ITenantDbContextFactory`, `SequenceNumber`. All reused; no new event infrastructure.
- **`IProviderPricingService`** (`Tamma.Api.Services.Providers`) — the cost-basis source of truth
  (`Compute(provider, model, in, out)`). Referenced, not modified.

**Consumers (downstream — NOT built here):**

- **Epic 34 (pricing / markup) — owner of 34-5 margin/markup engine**: branches `costBasisUsd` ×
  `credentialSource` × plan into `billableUsd`. This story emits the inputs it consumes.
- **Epic 35 (billing / invoicing)** and **Epic 36 (analytics)**: consume `AGENT.USAGE.RECORDED` +
  `agent_usage_rollup`.

**External Dependencies:** None new. EF Core 9 / Npgsql, existing event store, existing pricing seam.

## Testing Strategy

1. **Producer correctness (AC 1, 2, 3, 4):** drive a managed run (mocked HTTP) for single-turn and a
   multi-turn tool loop; assert exactly one `AGENT.USAGE.RECORDED` per logical call, all required
   `Tags` present, `Data.costBasisUsd == IProviderPricingService.Compute(provider, model, in, out)`,
   token split correct, and `Data` contains **no** `billableUsd`/`marginPct`/`feeUsd` (AC 11).
2. **Link integrity (AC 5):** one run produces a 32-6 `AGENT.TASK.*` trail event, a `ProviderDiagnostic`
   row, and an `AGENT.USAGE.RECORDED` event all sharing `correlationId` + `agentId`; assert the join.
3. **Rollup idempotency (AC 6):** apply the same usage event twice; assert `call_count`/tokens/
   `cost_basis_usd` counted once; assert per-`(agent, provider, credentialSource, period)` bucketing.
4. **BYOK vs platform (AC 2, 4):** run with `credentialSource=byok` and with `platform`; assert both
   record the same provider rate-sheet `costBasisUsd` and the correct discriminator, and that the
   rollup keeps them in separate buckets.
5. **Tenant isolation (AC 8) — highest priority:** seed usage for tenants A and B; assert
   `GET /api/v1/orgs/{B}/agents/usage` returns only B's numbers; assert a member of A hitting B's path
   is rejected by `RequireTenantMembershipFilter` (403/404); assert a platform owner has no read path
   to either; assert a **public/system agent** run by A produces a rollup only in A's schema, none on
   the CP, none visible to the platform owner or to B.
6. **Non-blocking (AC 9):** inject an `IEventRepository.AppendAsync` failure; assert the agent run
   still completes (no exception propagates) and an `AGENT.USAGE.EMIT_FAILED` breadcrumb / WARN + metric
   is produced.
7. **Epic 20 mapping sanity (AC 12):** assert `callCount`/`totalTokens` in `agent_usage_rollup` are the
   agent-aware equivalents of 20-3's `workflow_runs`/`llm_tokens` (documentation-backed parity check;
   no Epic 20 code exists to exercise).

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`; docker-bound suites run via
`sg docker -c "dotnet test ..."`. TDD: write the isolation + producer-schema (no-leakage) tests first.

## Estimated Effort

4-5 days (emitter + types + non-blocking ~1d; managed-run wiring + correlation/credentialSource ~1d;
rollup migration + idempotent projector ~1.25d; read endpoint + DTOs ~0.5d; tests incl. isolation /
idempotency / no-leakage / non-blocking ~1.25d).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentUsageEmitter.cs` | Create (NEW — emitter seam) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentUsageEmitter.cs` | Create (NEW — `AGENT.USAGE.RECORDED`, cost-basis only, non-blocking) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentUsageContext.cs` | Create (NEW — `AgentUsageContext` + `AgentCallUsage` records) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentUsageTags.cs` | Create (NEW — shared flat-tag builder) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentUsageEventTypes.cs` | Create (NEW — type constants) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentUsageRollupProjector.cs` | Create (NEW — idempotent per-period projector) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentUsageEndpoints.cs` | Create (NEW — tenant-scoped GET usage) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/AgentUsageDtos.cs` | Create (NEW — byAgent/totals; no billableUsd) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentUsageRollup.cs` | Create (NEW — tenant rollup entity) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentUsageApplied.cs` | Create (NEW — idempotency ledger entity) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (DbSets + model config for the two tenant tables) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*_AddAgentUsageRollup.cs` | Create (NEW — tenant EF migration) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI for emitter/projector; map `/orgs/{tenantId}/agents/usage`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentUsageEmitterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentUsageRollupTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentUsageIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentUsageEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentUsageNoPricingLeakageTests.cs` | Create |

> Note: `DomainEvent.cs`, `ProviderDiagnostic.cs`, `IProviderPricingService.cs`/`ProviderPricingService.cs`,
> and `Plan.cs` are **referenced, not modified** — this story reuses the existing DCB row shape,
> diagnostic entity, and cost-basis pricing seam, and does not touch `Plan` (no pricing here).
> (`packages/api` is deleted; all work is in the C# `apps/tamma-elsa` stack.)

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions — especially the cost-monitor /
   pricing origin (`ProviderPricingService` was ported from the TS `packages/cost-monitor`) and the
   28-1 tenant-scope routing decision (`.dev/decisions/story-28-1-design-calls.md`)
3. Re-read the Epic 32 design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
   — §"Story breakdown" item 9 (**producer; markup engine is 34-5, not here**) and §"Tracking" (usage/
   cost emission consumed by 34/35/36) are canonical over the legacy "+margin" framing in the spec key
4. Read 32-6 (`AgentTrailEmitter`/`AgentTrailTags`) — this story mirrors its tenant-scoped emission,
   tag builder, `correlationId` linkage, and non-blocking contract
5. Planned TDD approach (Red-Green-Refactor) — isolation + no-pricing-leakage tests first

### Key Design Decisions

- **Producer only — the markup boundary is sacred.** This story emits `costBasisUsd` (provider cost,
  already computed) + `credentialSource`. It computes **no** `billableUsd`, margin, fee, or plan
  branch. 34-5 owns that. AC 11 has a test that fails if a pricing field sneaks into `Data`.
- **Reuse the cost-basis source of truth.** `costBasisUsd` is the existing `ProviderDiagnostic.Cost`
  (= `IProviderPricingService.Compute`). Do **not** recompute or add a second pricing path.
- **No new event infrastructure.** `AGENT.USAGE.RECORDED` is a `DomainEvent` in the tenant's
  `domain_events`; isolation is structural (schema-per-tenant + `IEventRepository`), inherited from
  Epic 4/28 — same property 32-6 relies on for its trail.
- **`correlationId` is the join key**, shared across trail (32-6), diagnostic, and usage — so usage
  re-keys by agent for 32-10 without a new column on `ProviderDiagnostic`.
- **Rollup is idempotent on event id**, via an applied-ledger, so a future stream replay rebuilds it
  exactly — DCB hygiene.
- **One logical call → one usage event.** A multi-turn tool loop accumulates tokens (as
  `CallLlmInlineActivity` already does via `ToolLoopTokens`) and emits once; benchmarking counts a
  call, not a turn.
- **Platform admin has no usage read path by construction** — there is no `OwnerAccess` route, and the
  tenant-scoped store cannot return cross-tenant rows. A platform owner who owns a public agent sees
  zero of any tenant's usage of it (Epic 32 tenancy rule).

### Integration Points

- **32-5 managed run** is the sole producer; the emitter is injected into the run loop and called once
  per LLM call.
- **`IProviderPricingService` / `ProviderDiagnostic.Cost`** is the cost-basis source.
- **32-3 `ProviderCredential.Source`** supplies `credentialSource`.
- **32-6 trail + `correlationId`** is the linkage substrate; **32-10** consumes the agent-keyed
  rollup; **Epic 34-5/35/36** consume `AGENT.USAGE.RECORDED` + `agent_usage_rollup`.
- **`RequireTenantMembershipFilter` + `/api/v1/orgs/{tenantId}`** is the existing tenant path-gate the
  usage read rides, like the 32-6 trail endpoints.

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | --------- | ---------- |
| Pricing/markup logic leaks into the producer (34-5 boundary breach) | High | Cost-basis-only `Data`; no `Plan`/margin reads; AC 11 schema-assertion test fails on any pricing field |
| Cross-tenant usage leakage | Critical | Tenant-scoped store only; route behind `RequireTenantMembershipFilter`; no `OwnerAccess` route; isolation tests incl. public-agent + platform-admin-denied |
| Double-counted usage on replay/retry | High | `agent_usage_applied` idempotency ledger keyed by event id; replay test |
| Usage write aborts a real agent run | High | Non-blocking emitter (swallow + retry + `AGENT.USAGE.EMIT_FAILED`); failure-injection test asserts run completes |
| Cost basis drifts from the rest of the system | Medium | Single pricing seam reused; `costBasisUsd == Compute(...)` parity test |
| `credentialSource` unavailable before 32-3 lands | Medium | Default to `platform`; correctness test once 32-3 wired |
| `agentId`/`agentVersion` unavailable before 32-1 lands | Medium | Source from resolved config identity; treat 32-1 as prerequisite |

### Success Metrics

- [ ] 100% of agent LLM calls emit exactly one `AGENT.USAGE.RECORDED` in the correct tenant schema
- [ ] `costBasisUsd` on every event equals the pricing-seam value (parity test green)
- [ ] 0 pricing/markup fields present in emitted `Data` (AC 11 test green)
- [ ] 0 cross-tenant usage reads possible (isolation suite green)
- [ ] Rollup is replay-idempotent (idempotency test green)

## Logging Requirements

- **INFO**: usage recorded (`tenantId`, `agentId`, `provider`, `model`, `credentialSource`,
  `totalTokens`, `costBasisUsd`, `correlationId`); usage view served (`tenantId`, period, agent count)
- **DEBUG**: usage event appended (`type`, `agentId`, `sequenceNumber`); rollup folded (`agentId`,
  `provider`, period, new totals); idempotent skip (event already applied)
- **WARN**: usage emit failed/retried (`agentId`, `provider`, `correlationId`, error);
  `AGENT.USAGE.EMIT_FAILED` breadcrumb emitted
- **ERROR**: terminal usage-emit failure after retries (run still completes); rollup write failure;
  usage query repository failure
- **Structured context**: include `{ tenantId, agentId, agentVersion, provider, model,
  credentialSource, correlationId, costBasisUsd }` where applicable
- **Credential safety**: NEVER log API keys, raw prompts, or tool content; cost basis, token counts,
  and identity dimensions only. `credentialSource` is the discriminator — never the key

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
</content>
</invoke>
