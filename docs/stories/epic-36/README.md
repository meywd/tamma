# Epic 36: Analytics & Reporting Platform

## Overview

Epic 36 turns Tamma's DCB event stream and per-tenant operational data into a first-class,
multi-dimensional analytics product. Today Tamma has only a thin, single-grain substrate: an
hourly fact table (`platform_analytics_hourly`, Story 28-10) that carries fleet-wide and
per-`(hour, tenant)` totals consumed exclusively by platform-owner admin endpoints, plus a live
tenant dashboard that shows nothing but workflow counts and a success rate.

This epic builds the real analytics product on top of the DCB event stream (`LLM.CALL.SUCCESS`,
`AGENT.DISPATCH.*`, `WORKFLOW.*`) and per-call `ProviderDiagnostic` data. It delivers:

- **A dimensional projection pipeline** that materializes per-tenant usage / cost / performance
  rollups broken down by **provider, agent, workflow definition, repo, and a BYOK-vs-platform
  cost basis** — at hourly and daily grain, idempotent, resumable, and per-tenant isolated.
- **Tenant-facing analytics** — usage, cost/spend, and agent/tenant performance APIs and a
  dashboard, plus CSV/PDF exports and scheduled email reports. RBAC-gated (owner/admin edit,
  member read) and scoped per-mode (`user_id` in single-user, `tenant_id` in SaaS).
- **A strictly-separated platform-owner business-analytics surface** (MRR, churn, conversion,
  active/converting tenants) that is **NEVER** exposed per tenant — it lives on the control plane
  and is reachable only behind `PlatformOwnerAccess` / owner-only admin endpoints.

Epic 36 is built entirely in the C# `apps/tamma-elsa` solution. Per-tenant analytics live in the
tenant schema (`Tamma.Data` `TenantDbContext`); platform business analytics live on the
`ControlPlaneDbContext`. The two dashboards are `packages/dashboard-user` (tenant-facing) and
`packages/dashboard` (admin, owner-only). It is **distinct from Epic 5/23 operational
observability** (system health, ops metrics stay there) and re-targets the analytics slices of
Epics 20/32 onto a single dimensional source of truth.

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 36-1 | Dimensional Analytics Projection Schema & Store | P0 | drafted | 3-4 days |
| 36-2 | DCB-to-Analytics Projection Pipeline (Dimensional Rollup) | P0 | drafted | 4-5 days |
| 36-3 | Tenant Usage Analytics API | P0 | drafted | 3-4 days |
| 36-4 | Cost & Spend Analytics API (BYOK vs Platform) | P0 | drafted | 3-4 days |
| 36-5 | Agent & Tenant Performance Rollups API (consume Epic 32) | P1 | drafted | 3-4 days |
| 36-6 | Tenant Analytics Dashboard UI | P1 | drafted | 4-5 days |
| 36-7 | Pricing / Cost-Basis & Platform Margin Model | P0 | drafted | 3-4 days |
| 36-8 | Analytics Exports (CSV / PDF) | P1 | drafted | 3-4 days |
| 36-9 | Scheduled Reports & Delivery | P2 | drafted | 4-5 days |
| 36-10 | Platform Business Analytics (Owner-Only: MRR, Churn, Conversion) | P1 | drafted | 4-5 days |
| 36-11 | Analytics Event Catalog, Backfill & Reconciliation | P2 | drafted | 3-4 days |

## Architecture

```
+-----------------------------------------------------------------------------------+
|                  EPIC 36: ANALYTICS & REPORTING PLATFORM                           |
|                  (apps/tamma-elsa  — C# / Elsa / EF Core 9 / PG17)                 |
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  SOURCE OF TRUTH: DCB event stream (Tamma.Data DomainEvent) + ProviderDiagnostic  |
|    LLM.CALL.SUCCESS  AGENT.DISPATCH.*  WORKFLOW.*   (per-tenant schema)            |
|                                   |                                               |
|  +-- PROJECTION (36-1, 36-2) -----v-----------------------------------------+      |
|  |  ComputeTenantDimensionalRollupActivity  (SequenceNumber checkpoint)     |      |
|  |  CompactDailyAnalyticsActivity           PurgeStaleUsageAnalyticsActivity|      |
|  |  fan-out step ON the existing HourlyAnalyticsRollupWorkflow (28-10)      |      |
|  +-------------------------------|-----------------------------------------+      |
|                                  v                                                 |
|  PER-TENANT FACT STORE (tenant schema, no TenantId column — schema = isolation)    |
|    analytics_usage_hourly   analytics_usage_daily   analytics_projection_checkpoint|
|       dims: Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis(byok|platf) |
|                                  |                                                 |
|  +-- TENANT ANALYTICS APIs (36-3,4,5) ----------+   +-- PRICING (36-7) ---------+  |
|  |  AnalyticsService / AdminAnalyticsEndpoints  |<--| IAnalyticsPricingConfig   |  |
|  |  Usage | Cost/Spend (BYOK vs platform) | Perf|   | margin -> PlatformBilledUsd| |
|  |  RBAC: MemberAccess read / owner+admin edit  |   +---------------------------+  |
|  +----------------------|-----------------------+                                  |
|         |               |                  |                                       |
|   Exports (36-8)   Scheduled (36-9)  Tenant Dashboard UI (36-6)                    |
|   CSV / PDF        email delivery    packages/dashboard-user                       |
|                                                                                   |
|  ===============  OWNER-ONLY (NEVER per tenant) ================================   |
|  +-- PLATFORM BUSINESS ANALYTICS (36-10) -------------------------------------+    |
|  |  ControlPlaneDbContext: platform_analytics_hourly (28-10) + business facts |    |
|  |  MRR | churn | conversion | active/converting tenants                      |    |
|  |  PlatformOwnerAccess only -> admin dashboard (packages/dashboard)          |    |
|  +---------------------------------------------------------------------------+     |
|                                                                                   |
|  Catalog / Backfill / Reconciliation (36-11) — events, replay, totals tie-out     |
+-----------------------------------------------------------------------------------+
```

## Key Technical Decisions

### Dimensional store, mirrored on (not extended from) the platform fact table

The control-plane `platform_analytics_hourly` table (Story 28-10) stays the owner-only,
single-grain fleet store — one `SELECT` answers "platform this week". Epic 36 adds **separate**
per-tenant `analytics_usage_hourly` / `analytics_usage_daily` fact tables that carry the
dimensions the CP row deliberately omits (`Provider`, `AgentId`, `WorkflowDefinitionId`,
`RepoId`, `CostBasis`). The hourly and daily entities share their dimension + measure contract
**exactly**, so the daily roll-up is a lossless `GROUP BY date_trunc('day', Hour), <dims>`. Measure
types/precision (`long` counters, `decimal(20,4)` cost) match `PlatformAnalyticsHourly` so an
owner-side reconciliation join is lossless.

### Tenant isolation is the schema, not a column

The per-tenant fact tables carry **no `TenantId` column** — tenancy is implicit in the per-tenant
schema (`t_<hex>`) + connection string, matching the Doc 01 §1.4 target architecture
(`TenantDbContext` carries no query filters; the search-path schema is the isolation plane). A row
written to schema A is physically unreachable from schema B's context. The deliberate
`ApplyTenantFilter` no-op keeps the EF migration graph in parity. These tables are **NOT** added to
`ControlPlaneDbContext`.

### NULLS NOT DISTINCT business key for idempotent upsert

Nullable dimensions (`AgentId`, `WorkflowDefinitionId`, `RepoId`) would let duplicate
"unattributed" rows accumulate under a naive unique index. A unique business-key index over the
full dimension tuple with `.AreNullsDistinct(false)` (PG15+/PG17) collapses NULLs to one row per
bucket — the upsert target the projection relies on (same pattern as `prompt_overrides` /
`conventions`). Missing-dimension events bucket under that dimension `= NULL` and are **never**
coerced to a sentinel string, so per-dimension breakdowns and the grand total always reconcile.

### Projection is idempotent, resumable, and per-tenant-failure-tolerant

The dimensional rollup is an **additional fan-out step** on the existing
`HourlyAnalyticsRollupWorkflow` (one schedule, one advisory lock, one target hour) — it does not
fork the scheduler. The activity recomputes the whole `(tenant, hour)` bucket from source and
**overwrites** measures (read-then-upsert), so replay and backfill are naturally idempotent. An
`analytics_projection_checkpoint` row records the highest folded `DomainEvent.SequenceNumber`
(the monotonic `BIGSERIAL` total-order cursor — never `Id`/`CreatedAt`) for resumability. A single
tenant's failure emits `…_FAILED` and continues the fan-out (Story 28-10 AC5 tolerance shape).

### Cost basis from the Epic 35 `billing_mode` signal

`CostBasis` (`byok | platform`) is resolved per usage record from the `billing_mode` tag on
`LLM.CALL.*` events and the `ProviderDiagnostic.BillingMode` column (both produced by Story 35-2).
The analytics path performs **no secret reads** — it reuses the already-surfaced discriminator, so
provider-key plaintext is never on the analytics surface. Absent `billing_mode` (single-user mode /
legacy events) defaults to `platform`. `platform`-basis rows carry
`PlatformBilledUsd = CostUsd × (1 + margin)` from the Story 36-7 `IAnalyticsPricingConfig` seam;
`byok` rows carry `PlatformBilledUsd = 0` (Tamma never marks up a BYOK call).

### Per-tenant analytics vs. platform business analytics — a hard wall

Tenant-facing analytics (usage/cost/performance) read the tenant schema behind `MemberAccess`
(any member reads their tenant's data; member is read-only by default; owner/admin edit reports).
Platform business analytics (MRR, churn, conversion) live on `ControlPlaneDbContext` and are
reachable **only** behind `PlatformOwnerAccess` — they are never exposed on a tenant endpoint or
the tenant dashboard. The endpoint shape stays identical across modes; the auth middleware
resolves the override key (`user_id` single-user / `tenant_id` SaaS) per the prompt-store
precedent.

### Built in C#, never `packages/api`

All server code lands in `apps/tamma-elsa` (`Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer`,
`AnalyticsService` / `AdminAnalyticsEndpoints`). The legacy `packages/api` is deleted and must not
be referenced. UI is React: `packages/dashboard-user` (tenant) and `packages/dashboard` (admin).

## Dependencies

### On Other Epics

- **Epic 4 (DCB event sourcing)** — the per-tenant `DomainEvent` stream (`LLM.CALL.SUCCESS`,
  `AGENT.DISPATCH.*`, `WORKFLOW.*`) and its `SequenceNumber` total-order cursor that the projection
  reads. **Consumed.**
- **Epic 28 (per-tenant schema)** — `TenantDbContext`, `ITenantDbContextFactory`, the Tenant EF
  migration graph, and `EfTenantDbMigrator` the fact tables and checkpoint live in.
- **Story 28-10** — the `HourlyAnalyticsRollupWorkflow`, `FanOutTenantRollupsActivity`,
  `ComputeTenantRollupActivity.AggregateLlmUsage`, `PurgeStaleAnalyticsActivity`, and
  `AnalyticsRollupEvents` this epic extends/mirrors (and the CP `platform_analytics_hourly` table it
  reuses for owner analytics, left intact).
- **Epic 32 (agent action trail)** — the `agent_id` DCB tag and per-agent data the agent dimension
  and performance rollups (36-5) **consume**. Absent → rows bucket under `AgentId = NULL`.
- **Epic 34 (pricing / margin)** — pricing & platform-margin inputs feeding the cost-basis / margin
  model (36-7) and `PlatformBilledUsd`.
- **Epic 35 (billing)** — the `billing_mode` tag / `ProviderDiagnostic.BillingMode` (Story 35-2)
  that resolves `CostBasis`; soft/forward dependency (defaults to `platform` if not yet landed).

### Complements (does not duplicate)

- **Epic 5 / Epic 23 (operational observability)** — system-health and ops metrics stay there.
  Epic 36 owns **product analytics** (usage/cost/performance business facts), reconciling so the
  two surfaces never re-implement each other. Operational lag/SLO of the projection itself is
  emitted as analytics events (`ANALYTICS.ROLLUP.DIMENSIONAL_LAG`) and an OTel gauge, consumed by
  the ops alerting Epic 5/23 owns.

### External Dependencies

- **PostgreSQL 17** — NULLS NOT DISTINCT business key (PG15+), per-tenant `ExecuteDeleteAsync`.
- **EF Core 9 / Npgsql** — model-expressible `AreNullsDistinct(false)`, search-path schema routing.
- **Elsa** — `HourlyAnalyticsRollupWorkflow` host for the scheduled projection fan-out.
- **Testcontainers + Docker** — Postgres-17 integration suites (run via `sg docker -c "dotnet test …"`).
- **CSV/PDF rendering** (36-8) and **email delivery** (36-9) — export/report surfaces.

## Database Schema

All per-tenant analytics tables live in each tenant schema via the `TenantDbContext` migration
graph (no `TenantId` column — isolation is the schema). The control-plane
`platform_analytics_hourly` (Story 28-10) is left intact and gains owner-only business-fact tables.

```sql
-- ── Per-tenant dimensional fact store (Story 36-1) — tenant schema (t_<hex>) ──
CREATE TABLE analytics_usage_hourly (
  id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  hour                   TIMESTAMPTZ NOT NULL,          -- UTC top-of-hour bucket
  -- dimensions
  provider               TEXT NOT NULL,                 -- required
  agent_id               TEXT,                          -- nullable (Epic 32 agent trail)
  workflow_definition_id UUID,                          -- nullable
  repo_id                TEXT,                          -- nullable
  cost_basis             TEXT NOT NULL,                 -- 'byok' | 'platform' (HasConversion<string>)
  -- measures
  tokens_in              BIGINT  NOT NULL DEFAULT 0,
  tokens_out             BIGINT  NOT NULL DEFAULT 0,
  cost_usd               NUMERIC(20,4) NOT NULL DEFAULT 0,
  platform_billed_usd    NUMERIC(20,4) NOT NULL DEFAULT 0,  -- CostUsd*(1+margin) for platform; 0 for byok
  workflows_started      BIGINT  NOT NULL DEFAULT 0,
  workflows_completed    BIGINT  NOT NULL DEFAULT 0,
  workflows_failed       BIGINT  NOT NULL DEFAULT 0,
  agent_dispatches       BIGINT  NOT NULL DEFAULT 0,
  computed_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Breakdown index — drives "by provider / agent / workflow / cost-basis".
CREATE INDEX IX_analytics_usage_hourly_breakdown
  ON analytics_usage_hourly (hour, provider, agent_id, workflow_definition_id, cost_basis);

-- Idempotent business key — full dimension tuple, NULLS NOT DISTINCT (one row per bucket-tuple).
CREATE UNIQUE INDEX UX_analytics_usage_hourly_dims
  ON analytics_usage_hourly (hour, provider, agent_id, workflow_definition_id, repo_id, cost_basis)
  NULLS NOT DISTINCT;

-- analytics_usage_daily: byte-for-byte identical shape, 'day' (UTC midnight) instead of 'hour'.
-- Lossless GROUP BY date_trunc('day', hour), <dims>. Indexes: IX_/UX_analytics_usage_daily_*.

-- ── Projection cursor (Story 36-2) — tenant schema ──
CREATE TABLE analytics_projection_checkpoint (
  id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  stream               TEXT NOT NULL,        -- 'dimensional'
  last_sequence_number BIGINT NOT NULL,      -- highest DomainEvent.SequenceNumber folded in
  updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── Owner-only platform business analytics (Story 36-10) — control-plane DB ──
-- platform_analytics_hourly (Story 28-10) left INTACT.
-- + business-fact projections for MRR / churn / conversion / active+converting tenants,
--   mapped on ControlPlaneDbContext, reachable ONLY behind PlatformOwnerAccess.
```

## Implementation Phases

### Phase 1: Dimensional Foundation (36-1, 36-2, 36-7) — Weeks 1-2

- Per-tenant fact schema + `CostBasis` enum + NULLS-NOT-DISTINCT business keys (36-1).
- DCB→analytics projection pipeline: compute activity, checkpoint, daily compaction, retention
  purge, fan-out on the 28-10 workflow, lag SLO (36-2).
- Pricing / cost-basis & platform-margin model behind `IAnalyticsPricingConfig` (36-7).
- Estimated: 10-13 days

### Phase 2: Tenant Analytics APIs (36-3, 36-4, 36-5) — Weeks 3-4

- Tenant usage analytics API; cost & spend (BYOK vs platform) API; agent/tenant performance
  rollups API (consuming Epic 32). All `MemberAccess` read, owner/admin edit.
- Estimated: 9-12 days

### Phase 3: Surfaces & Delivery (36-6, 36-8, 36-9) — Weeks 5-6

- Tenant analytics dashboard UI (`packages/dashboard-user`); CSV/PDF exports; scheduled reports +
  email delivery.
- Estimated: 11-14 days

### Phase 4: Owner Business Analytics & Integrity (36-10, 36-11) — Week 7

- Platform business analytics (MRR/churn/conversion), owner-only, on the admin dashboard
  (`packages/dashboard`); analytics event catalog, backfill, and reconciliation tie-out.
- Estimated: 7-9 days

## Success Metrics

- Projection lag p95 < 2 hours bucket-to-materialized; `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` fires
  only past the configured SLO budget.
- 100% idempotent replay: re-running any hour leaves row counts and measures unchanged; backfill of
  an already-projected bucket is a no-op on measures.
- Reconciliation tie-out: `Σ(per-dimension rows)` equals the grand total for every tenant×bucket
  (NULL buckets included), and owner-side CP totals reconcile losslessly against the per-tenant
  dimensional sums (36-11).
- Zero cross-tenant leakage: a row in tenant A's schema is unreachable from tenant B's context
  (proven by Testcontainer isolation tests); a tenant A projection failure leaves tenant B intact.
- Tenant analytics API p95 < 500ms reading pre-aggregated facts (no raw event re-scan).
- Platform business analytics reachable **only** behind `PlatformOwnerAccess`; member-role SaaS
  users hit 403 on every owner endpoint and the surface is absent from the tenant dashboard.
- Cost basis correctly classified: 100% of `platform` rows carry `PlatformBilledUsd =
  CostUsd × (1+margin)`; 100% of `byok` rows carry `PlatformBilledUsd = 0`.

## Reference Documents

- [Epic 36 stories](.) — `docs/stories/epic-36/story-36-*/`
- [Story 36-1: Dimensional Analytics Projection Schema & Store](story-36-1/36-1-dimensional-analytics-projection-schema-and-store.md)
- [Story 36-2: DCB-to-Analytics Projection Pipeline](story-36-2/36-2-dcb-to-analytics-projection-pipeline.md)
- [Story 28-10: Platform Analytics Rollup](../epic-28/) — the `HourlyAnalyticsRollupWorkflow` +
  `platform_analytics_hourly` foundation this epic extends.
- [CLAUDE.md — Operating Modes & per-mode ownership](../../../CLAUDE.md) — single-user vs SaaS
  principal/RBAC and the prompt-store resolution precedent.
- DCB event sourcing (Epic 4); per-tenant schema (Epic 28); agent action trail (Epic 32);
  pricing/margin (Epic 34); billing & `billing_mode` (Epic 35).

---

**Last Updated**: 2026-06-17
**Epic Owner**: TBD
**Implementation Start**: TBD
**Total Estimated Effort**: 37-48 days
