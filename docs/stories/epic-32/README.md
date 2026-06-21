# Epic 32: Agents — First-Class Agent Entities, Managed Execution, Benchmarking & Learning

## Overview

Today agents are an **implicit** concept in Tamma. `agent_configs` is a tenant-scoped JSONB blob keyed by role (`Tamma.Data.Entities.AgentConfig`) merged into a `ResolvedAgentConfig`; provider API keys are global environment variables (`CallLlmInlineActivity` reads `_configuration["Anthropic:ApiKey"]`); performance lives in an undifferentiated `ProviderDiagnostic` table; and multi-agent review is a hardcoded 4-role sequence (`TriagePanelReviewWorkflow`).

Epic 32 promotes agents into **first-class, versioned, identity-bearing entities**. An **Agent** is a saved configuration given a stable identity — tracked along two dimensions: **what it did** (an `agent_id`-tagged action trail) and **how well it did** (a per-tenant performance dataset). Each agent has an immutable `agent_id` + stable `name` + `role`, plus a monotonically-versioned saved config (provider chain / prompt / budget / tools / temperature / RAG). Visibility is **public** (system, platform-admin-owned, available to all tenants) or **private** (tenant-owned, available only to it); shipped defaults are public.

The epic adds: a **managed-LLM-agent execution layer** (`IManagedAgent` over the existing `IAIProvider`/inline tool-loop HTTP path) — distinct from CLI/token agent providers (`ICLIAgentProvider`, which stay single-user/self-hosted only because **SaaS = API-key auth only**); **strategy-driven multi-agent panels** in Elsa; an `agent_id`-tagged **DCB action trail** in the tenant store; **outcome + bug-taxonomy capture** at review/gate; **benchmark projections + leaderboards** (per agent / provider / prompt, per-tenant); **BYOK-then-platform** credential resolution from the Epic 29 secret cabinet; a per-tenant **cost-basis-plus-margin metering** model that re-targets Epic 20 billing; **learning persistence + auto-learning into RAG**; and a Phase-2 **A/B experiment framework**.

### The key tenancy rule (definition ownership ≠ data ownership)

| Concern | Scope |
|---|---|
| **Agent definition** | **Public/system-wide** (platform-admin-owned, available to every tenant) **OR** **private/tenant-owned** (tenant-owned, available only to it). Shipped defaults are public. |
| **Performance + action data** | **ALWAYS tenant-scoped** — belongs to the tenant that generated it; never system-wide, never cross-tenant. |

- A tenant's usable agent set = **all public agents ∪ its own private agents**.
- **One-to-many**: one agent definition → many independent per-tenant performance/action datasets. Two tenants running public agent `atlas` build *separate, private* profiles; neither sees the other's; the platform admin who owns `atlas` sees **none** of it.
- Leaderboards/benchmarks are computed **within a tenant's own data**.

This maps onto the unified schema-per-tenant model: **public agent definitions** live in the **control-plane** (`ControlPlaneDbContext`, shared, referenced by `agent_id`); **private agent definitions + all performance/action data** live in the **tenant's `t_<hex>` schema** (`TenantDbContext`) — isolation is structural. In **single-user mode**, "public" = shipped system agents, the sole user owns/creates private ones, and performance is the user's.

### Supersedes

Epic 32 is the canonical owner of the agent entity, execution, and tracking model. It **supersedes** the following earlier, now-replaced approaches:

- **Story 1-13** (agent customization) — superseded by the first-class `Agent` + versioned `AgentVersion` entity (32-1) and the registry/RBAC API (32-2).
- **Story 1-14** (performance analysis) — superseded by the `agent_id`-tagged action trail (32-6), benchmark projections + leaderboards (32-10), and the per-tenant performance dataset model.
- **Epic 20 stories 20-3 / 20-4 / 20-5 (in part)** — usage metering, limit-enforcement injection, and the billing-dashboard data source are re-targeted by the cost-basis-plus-margin metering producer (32-9) and the agent/benchmark dashboards (32-13); Epic 32 *produces* the per-tenant cost data those billing surfaces consume.
- **The `AgentConfig` role-keyed JSONB blob + `AgentSeeder`** — superseded by `Agent`/`AgentVersion` (CP-resident definitions) and the insert-missing-only `AgentEntitySeeder`. The legacy blob coexists during cutover, then retires.
- **The hardcoded `TriagePanelReviewWorkflow`** — superseded by strategy-driven multi-agent panels (`RunAgentPanelActivity` / `AggregatePanelActivity`, 32-7).

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 32-1 | Agent Entity Model & Versioned Saved Config (public/private) | P0 | drafted | 4-5 days |
| 32-2 | Agent Registry, Resolution & RBAC API | P0 | drafted | 4-5 days |
| 32-3 | Per-Tenant Provider Credential Resolution (BYOK → platform) | P0 | drafted | 4-5 days |
| 32-4 | SaaS Provider Gate — gate STAGE of the call-LLM endpoint *(reframed 2026-06-21)* | P0 | drafted | 2-3 days |
| 32-5 | Call-LLM Endpoint + Managed Execution (`POST /api/v1/llm/call`; lynchpin) *(reframed 2026-06-21)* | P0 | drafted | 6-8 days |
| 32-6 | Agent Action Trail (DCB events tagged `agent_id`) in Tenant Store | P0 | drafted | 4-5 days |
| 32-7 | Multi-Agent Design/Review Panels in Elsa (strategy-driven) | P1 | drafted | 5-6 days |
| 32-8 | Outcome Capture & Bug Taxonomy at Review/Gate | P1 | drafted | 3-4 days |
| 32-9 | Cost-Basis-Plus-Margin Metering & BYOK Pricing Model (re-targets Epic 20) | P1 | drafted | 4-5 days |
| 32-10 | Benchmark Projections & Leaderboards (per agent/provider/prompt, per-tenant) | P1 | drafted | 4-5 days |
| 32-11 | Learning Persistence & Auto-Learning into RAG | P1 | drafted | 4-5 days |
| 32-12 | Persona-Aware Benchmarking *(reframed 2026-06-21: persona = named system agent)* | P2 | drafted | 3-4 days |
| 32-13 | Agent Management & Benchmark Dashboards (admin public + tenant private) | P2 | drafted | 4-5 days |
| 32-14 | A/B Experiment Framework for Agents (Phase 2: cohorts, significance, rollout/rollback) | P2 | drafted | 5-6 days |
| 32-15 | Persona Reframe & Seeding (named cross-role personas; amends 32-1) *(2026-06-21)* | P0 | drafted | 3-4 days |
| 32-16 | Per-Tenant Agent/Persona Enablement (`TenantAgentEnablement`) *(2026-06-21)* | P0 | drafted | 3-4 days |
| 32-17 | Custom-Agent Prompts (`ConfigJson.prompts`; custom prompts ⇔ custom agent) *(2026-06-21)* | P0 | drafted | 2-3 days |
| 32-18 | Registry Enablement Gate & Epic-27 Prompt Source (amends 32-2) *(2026-06-21)* | P0 | drafted | 3-4 days |
| 32-19 | Agent Style/Voice Variants (optional overlay; split from 32-12) *(2026-06-21)* | P2 | drafted | 3-4 days |
| 32-20 | Interactive Question-Back (`request_input` + `IQuestionRouter`) *(2026-06-21)* | P1 | drafted | 5-6 days |
| 32-21 | MCP & Plugin Tool Sourcing (C#) *(2026-06-21)* | P1 | drafted | 5-6 days |
| 32-22 | Prompt & Response Cache (server-side) *(2026-06-21)* | P2 | drafted | 3-4 days |
| 32-23 | Streaming Run Tap (SSE for dashboard/CLI) *(2026-06-21)* | P1 | drafted | 3-4 days |
| 32-24 | C# Harness/CLI Agent Adapter (single-user local, **DEFERRED**) *(2026-06-21)* | P3 | drafted | 5-6 days |

## Architecture

```
+-----------------------------------------------------------------------------+
|        EPIC 32: AGENTS — ENTITIES, EXECUTION, BENCHMARKING, LEARNING         |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-- LAYER 1: Agent Entity & Registry (32-1, 32-2) -------------------+     |
|  |   CONTROL PLANE (shared)                | TENANT t_<hex> (isolated)|     |
|  |   +--------------------+ +-----------+  | +--------------------+   |     |
|  |   | Agent (identity)   | | Agent     |  | | Private agent defs |   |     |
|  |   | id/name/role/      | | Version   |  | | (tenant-owned)     |   |     |
|  |   | visibility (pub)   | | (config   |  | +--------------------+   |     |
|  |   +--------------------+ |  snapshot)|  |                          |     |
|  |   Registry + RBAC API: all public U caller's own private          |     |
|  +------------------------------------------------------------------+      |
|                              |                                              |
|  +-- LAYER 2: Credential & Auth (32-3, 32-4) ------------------------+     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  |   | BYOK -> platform |  | Epic 29 secret   |  | SaaS gate:       | |     |
|  |   | resolution       |  | cabinet (BYOK)   |  | API-key ONLY;    | |     |
|  |   | (per tenant)     |  |                  |  | CLI = single-user| |     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  +------------------------------------------------------------------+      |
|                              |                                              |
|  +-- LAYER 3: Managed Execution (32-5) over IAIProvider --------------+    |
|  |   IManagedAgent.RunAsync:                                          |    |
|  |   resolve agent -> resolve credential -> SaaS gate ->             |    |
|  |   context+RAG -> prompt render -> [reused inline tool loop] ->    |    |
|  |   sanitize -> instrument (cost basis) -> AgentRunResult           |    |
|  |   (distinct from ICLIAgentProvider; converge on AgentRunResult)  |    |
|  +------------------------------------------------------------------+      |
|                              |                                              |
|  +-- LAYER 4: Panels (32-7) -----------------------------------------+     |
|  |   RunAgentPanelActivity (fan-out N agents) ->                     |    |
|  |   AggregatePanelActivity (single|consensus|lead+critics|         |    |
|  |   llm-judge-merge). Design: lead+critics. Review: consensus.     |    |
|  +------------------------------------------------------------------+      |
|                              |                                              |
|  +-- LAYER 5: Tracking — TENANT-SCOPED ALWAYS (32-6, 32-8, 32-9) ----+     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  |   | Action trail     |  | Outcome + bug    |  | Usage & cost     | |     |
|  |   | DCB tagged       |  | taxonomy at      |  | emission         | |     |
|  |   | agent_id (t_hex) |  | review/gate      |  | (producer ->     | |     |
|  |   |                  |  | (bugType)        |  |  Epics 34/35/36) | |     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  +------------------------------------------------------------------+      |
|                              |                                              |
|  +-- LAYER 6: Benchmarking, Learning, Personas (32-10..32-14) -------+     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  |   | Leaderboards     |  | Learning persist |  | Personas +       | |     |
|  |   | (per-tenant,     |  | + auto-learn     |  | persona-aware    | |     |
|  |   | per agent/prov/  |  | into RAG         |  | benchmarking;    | |     |
|  |   | prompt/version)  |  | (Epic 6 KB)      |  | A/B (Phase 2)    | |     |
|  |   +------------------+  +------------------+  +------------------+ |     |
|  |   Dashboards (32-13): admin = public defs; tenant = own private  |     |
|  +------------------------------------------------------------------+      |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## Key Technical Decisions

### Identity is the entity, not the role

`Agent.Id` is the immutable join key for every metric, action, and benchmark — history survives config edits. `Role` (architect / reviewer / tester / …) is retained as a **benchmarking attribute** (so like-vs-like comparison is possible: reviewer A vs reviewer B), but the **Agent**, not the role, is the tracked entity. This replaces the anonymous, role-keyed `agent_configs` blob.

### Versions are immutable; archive, never delete

Each saved config is captured as an immutable, monotonically-versioned `AgentVersion` snapshot pinned to the run that produced it. Rollback = repoint `Agent.CurrentVersionId`, never delete-and-recreate. `AgentVersion` FK uses `OnDelete(DeleteBehavior.Restrict)` so audit history cannot cascade away.

### Definitions in the control plane, data in the tenant schema

Public agent definitions are shared cross-tenant → **`ControlPlaneDbContext`** holds public defs. **`TenantDbContext` (`t_<hex>`)** holds private defs **and ALL performance/action data**. No performance column ever appears on `Agent`/`AgentVersion`. Cross-tenant isolation is structural (schema-per-tenant + `ITenantDbContextFactory`), not an app-level filter — the platform admin who owns a public agent sees **zero** of any tenant's runs of it.

### Managed-LLM-agent layer, distinct from CLI providers

`IManagedAgent` (in `packages/providers`' managed-LLM-agent layer / `apps/tamma-elsa` `Tamma.Api.Services.Agents`) is the customization layer **above** the LLM API: context assembly (RAG, Epic 6) → prompt render (Epic 27 prompt/convention store, tenant → system → error) → the **reused** inline tool loop (`CallLlmInlineActivity` seam, *not forked*) → sanitization (`SecureAgentProvider`) → instrumentation (cost via `IProviderPricingService`) → outcome capture → a structured `AgentRunResult`. It is **not** an `ICLIAgentProvider`; both backends converge on `AgentRunResult` so workflows never branch on backend.

### SaaS = API-key auth only

The managed-LLM-agent path (`IAIProvider`) is the **sole** execution path in SaaS. **Token-based / CLI agent providers (`ICLIAgentProvider`) are NOT available in SaaS** — they remain single-user/self-hosted only. A CLI-backed agent resolved in SaaS yields a typed `GATE_DENIED` `AgentRunResult`, never an uncaught exception.

### Credential-agnostic definitions; BYOK → platform resolution

Agent definitions carry provider + model + prompt + settings, **never raw keys**. Credentials resolve at execution from the principal's secret source: **BYOK key (Epic 29 cabinet) → else platform-provided** (usage-metered, billed via Epic 34/35). Resolution stamps `CredentialSource` (`byok` | `platform`) onto `AgentRunResult` and the DCB tags, so a public agent run by a tenant executes with *that tenant's* key/budget → cost & performance are genuinely the tenant's.

### Typed failures, never lost runs

Expected failures (provider error, budget-exceeded, gate denial, missing-credential, loop exhaustion) are **data**, not exceptions: each produces a typed `AgentRunResult { Success = false, FailureCode, FailureReason }` with whatever cost accrued, and emits exactly one terminal DCB event. Only contract violations (e.g., null request) may throw.

### Strategy-driven panels replace the hardcoded sequence

`RunAgentPanelActivity` fans a step out to N agents of the relevant role(s); `AggregatePanelActivity` combines results via `single`, `consensus/vote`, `lead+critics`, or `llm-judge-merge`. Defaults: **`lead+critics`** for design, **`consensus`** for review. Specialized **security / performance / visual** reviewers participate in review panels. Budget clamps + max-iteration caps are mandatory (panels multiply token spend).

### Action trail = DCB events, not a new table

Every run, tool call, iteration, panel aggregation, and recorded bug is a DCB `DomainEvent` in the **tenant's own `domain_events` stream** (`AGGREGATE.ACTION.STATUS`), tagged with `agentId` + config version + role + provider + model + prompt key/version. Paginate on `SequenceNumber` (server-side `BIGSERIAL`), never `CreatedAt`. Trail capture is best-effort-non-blocking — a write failure never aborts a real agent run.

### Cost is a producer, markup is downstream

Epic 32 emits usage/cost events at **provider cost basis** (`IProviderPricingService.Compute`). The **markup engine is Epic 34 (Story 34-5), NOT here**; invoicing/payment (Epic 35), business analytics (Epic 36), and audit product features (Epic 37) all *consume* Epic 32's data. Epic 32 produces; it does not bill, invoice, or analyze.

## Dependencies

### On Other Epics

- **Epic 1** (provider abstraction): `IAIProvider`, `IProviderPricingService`, provider config/allowlist, normalized LLM responses.
- **Epic 4** (DCB event sourcing): `DomainEvent`, `IEventRepository`, the `SequenceNumber` cursor — reused for the action trail; no new event infrastructure.
- **Epic 6** (RAG / context): `AssembleContextActivity` + the RAG pipeline supply assembled context; the learning loop (32-11) feeds learnings back into the KB.
- **Epic 9** (unified agent API): engine ↔ central-API round-trips for agent resolution / credential / prompt; the panel + dispatch call-site convention.
- **Epic 27** (prompt / convention store): prompt + convention resolution (tenant → system → error; NEVER empty/plain fallback); `AgentRole` / `AgentAction` taxonomy.
- **Epic 28** (tenancy): `ControlPlaneDbContext`, `TenantDbContext`, schema-per-tenant, the CP migration pipeline, `ITenantDbContextFactory` / `ITenantContext`.
- **Epic 29** (secret cabinet): encrypted per-tenant BYOK provider keys; canonical wiring in 32-3.
- **Epics 34 / 35 / 36 / 37** (downstream consumers): pricing/markup, billing, analytics, audit consume Epic 32's usage/cost/action data. Epic 32 *produces*, never bills/invoices/analyzes.

### External Dependencies

- None new. All work is in the C# `apps/tamma-elsa` stack (`Tamma.Api`, `Tamma.Data` EF Core, `Tamma.Activities`, `Tamma.ElsaServer`), the `packages/providers` managed-LLM-agent layer, and `packages/intelligence` (RAG). **`packages/api` is deleted — never referenced.** Reuses EF Core 9 / Npgsql, the existing event store, `DiagnosticsService`, the inline tool loop, and the `SecureAgentProvider` sanitization seam.

## Database Schema

Definitions live in the **control plane**; all performance/action data lives in each **tenant's `t_<hex>` schema**.

```sql
-- ===== CONTROL PLANE (ControlPlaneDbContext) — agent DEFINITIONS =====

-- Agent identity (public/system OR private/tenant-owned). NO performance columns.
CREATE TABLE agents (
  id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name               TEXT NOT NULL,                  -- stable handle, e.g. 'tamma-architect'
  role               TEXT NOT NULL,                  -- AgentRole wire string
  visibility         INT  NOT NULL,                  -- 0 = Public (system), 1 = Private
  owner_tenant_id    UUID,                           -- set iff Private + SaaS
  owner_user_id      UUID,                           -- set iff Private + single-user
  status             INT  NOT NULL DEFAULT 0,        -- 0 = Active, 1 = Archived
  current_version_id UUID,                           -- pointer to active agent_versions row
  created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_by         UUID,
  updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_by         UUID,
  -- Visibility <-> ownership invariant (mirrors ck_prompt_overrides_principal_xor)
  CONSTRAINT ck_agents_visibility_ownership CHECK (
       (visibility = 0 AND owner_tenant_id IS NULL AND owner_user_id IS NULL)        -- public
    OR (visibility = 1 AND owner_tenant_id IS NOT NULL AND owner_user_id IS NULL)    -- private/SaaS
    OR (visibility = 1 AND owner_user_id IS NOT NULL AND owner_tenant_id IS NULL)    -- private/single-user
  )
);
-- Public handles unique on (name, role); private handles unique per owner.
CREATE UNIQUE INDEX IX_agents_public_name_role
  ON agents (name, role) WHERE visibility = 0;
CREATE UNIQUE INDEX IX_agents_private_tenant_name
  ON agents (owner_tenant_id, name) WHERE visibility = 1 AND owner_tenant_id IS NOT NULL;
CREATE UNIQUE INDEX IX_agents_private_user_name
  ON agents (owner_user_id, name) WHERE visibility = 1 AND owner_user_id IS NOT NULL;

-- Immutable, monotonically-versioned saved-config snapshots. Insert-only.
CREATE TABLE agent_versions (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
  version     INT  NOT NULL,                         -- 1-based, monotonic per agent_id
  config_json JSONB NOT NULL DEFAULT '{}'::jsonb,    -- provider chain / model / prompt / budget / tools / temperature / rag
  notes       TEXT,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_by  UUID
);
CREATE UNIQUE INDEX IX_agent_versions_agent_version ON agent_versions (agent_id, version);

-- ===== TENANT SCHEMA t_<hex> (TenantDbContext) — DATA, ALWAYS tenant-scoped =====

-- Private agent definitions (tenant-owned) — same shape as agents, in the tenant schema.

-- Action trail: NO new table. Reuses the tenant's DCB event stream.
--   domain_events (Id, Type, TenantId, IssueNumber, Tags JSONB, Metadata JSONB,
--                  Data JSONB, CreatedAt, SequenceNumber BIGSERIAL)
--   Types: AGENT.TASK.SUCCESS/FAILED/PARTIAL, AGENT.TOOL_CALL.SUCCESS/FAILED,
--          AGENT.ITERATION.COMPLETED, AGENT.PANEL.AGGREGATED, REVIEW.BUG.RECORDED,
--          AGENT.RUN.STARTED/SUCCESS/FAILED.
--   Tags: { agentId, agentVersion, role, provider, model, promptRef, issueId,
--           iteration, correlationId, credentialSource }; bug events add bugType.
--   Cursor: SequenceNumber (BIGSERIAL total order), never CreatedAt.

-- Benchmark / leaderboard projections + learning captures (32-10 / 32-11) — tenant-scoped.
-- ProviderDiagnostic rows (cost/latency/tokens) linked to the trail by
-- (agentId, correlationId); existing entity reused, no schema change required.
```

DCB event families (`AGGREGATE.ACTION.STATUS`): `AGENT.CREATED.SUCCESS`, `AGENT.VERSION_PUBLISHED.SUCCESS`, `AGENT.ARCHIVED.SUCCESS` (control-plane, definition lifecycle); `AGENT.RUN.STARTED/SUCCESS/FAILED`, `AGENT.TASK.SUCCESS/FAILED/PARTIAL`, `AGENT.TOOL_CALL.SUCCESS/FAILED`, `AGENT.ITERATION.COMPLETED`, `AGENT.PANEL.AGGREGATED`, `REVIEW.BUG.RECORDED` (tenant, action trail).

## Implementation Phases

### Phase 1: Entity, Credential & Execution Foundation (32-1 … 32-6) — P0

The first-class `Agent` + versioned `AgentVersion` model, registry + RBAC API, BYOK → platform credential resolution, the SaaS API-key-only gate, the `IManagedAgent` managed execution layer (reusing the inline tool loop), and the `agent_id`-tagged tenant action trail. Nothing downstream can be built until the entity + execution + trail substrate exists.
Estimated: 23-29 days

### Phase 2: Panels, Tracking & Benchmarking (32-7 … 32-11) — P1

Strategy-driven multi-agent design/review panels (replacing `TriagePanelReviewWorkflow`), outcome + bug-taxonomy capture at review/gate, cost-basis-plus-margin usage/cost emission (re-targeting Epic 20 billing as a producer), per-tenant benchmark projections + leaderboards, and learning persistence + auto-learning into RAG.
Estimated: 20-25 days

### Phase 3: Personas, Dashboards & Experiments (32-12 … 32-14) — P2

Agent personas + persona-aware benchmarking, agent-management + benchmark dashboards (admin public defs + tenant private data), and the Phase-2 A/B experiment framework (cohorts, statistical significance, auto rollout/rollback on regression).
Estimated: 12-15 days

## Success Metrics

- 100% of agent-driven workflow steps route through `RunManagedAgentActivity` → `IManagedAgent` (zero remaining ad-hoc role→llm-call dispatch for managed agents).
- 100% of managed runs produce an `AgentRunResult` and exactly one terminal `AGENT.RUN.*` / `AGENT.TASK.*` event in the correct tenant schema.
- Zero forks of the tool loop / sanitizer / compactor (single source confirmed by grep).
- Zero cross-tenant action/performance reads possible (verified by the isolation test suite); a platform admin sees **none** of any tenant's run data, even for public agents.
- 100% of agent runs resolve a credential via BYOK → platform, with `CredentialSource` (`byok` | `platform`) tagged on every run and cost event.
- Every trail event carries all required tags; `REVIEW.BUG.RECORDED` always carries a valid `bugType` (`visual` | `functional` | `regression` | `security` | `perf` | `style`).
- Trail-write failures never fail an agent run (non-blocking contract verified).
- Per-tenant leaderboards compute success rate, avg iterations-to-done, bug counts by type, cost, and latency — sliceable by agent / provider / prompt / version.
- Auto-generated learnings from outcomes are retrievable by RAG in subsequent runs.

## Reference Documents

- **[Epic 32 Revised Agent Architecture (Design of Record, 2026-06-20)](../../superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md)** — supersedes the persona/agent + credential portions below: steps never call providers (call-LLM endpoint), persona = named cross-role system agent, provider cost entity, per-tenant enablement, BYOK-per-provider. See also the [managed-LLM execution deep dive](../../superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md) and the [re-plan](../../superpowers/plans/2026-06-20-epic-32-37-replan.md).
- [Epic 32 Design of Record — Agent Entities, Personas, Benchmarking & Learning (2026-06-17, partially superseded)](../../superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md)
- [Story 32-1 — Agent Entity Model & Versioned Saved Config](./story-32-1/32-1-agent-entity-model-and-versioned-saved-config.md)
- [Story 32-2 — Agent Registry, Resolution & RBAC API](./story-32-2/32-2-agent-registry-resolution-and-rbac-api.md)
- [Story 32-3 — Per-Tenant Provider Credential Resolution (BYOK → platform)](./story-32-3/32-3-per-tenant-provider-credential-resolution.md)
- [Story 32-4 — SaaS Provider Auth Gating (API-key only)](./story-32-4/32-4-saas-provider-auth-gating-api-key-only.md)
- [Story 32-5 — Managed Agent Execution Layer (`IManagedAgent`)](./story-32-5/32-5-managed-agent-execution-layer.md)
- [Story 32-6 — Agent Action Trail (DCB events tagged `agent_id`) in Tenant Store](./story-32-6/32-6-agent-action-trail-in-tenant-store.md)
- [Story 32-7 — Multi-Agent Design/Review Panels in Elsa](./story-32-7/32-7-multi-agent-design-review-panels-in-elsa.md)
- [Story 32-8 — Outcome Capture & Bug Taxonomy at Review/Gate](./story-32-8/32-8-outcome-capture-and-bug-taxonomy-at-review-gate.md)
- [Story 32-9 — Cost-Basis-Plus-Margin Metering & BYOK Pricing Model](./story-32-9/32-9-cost-basis-plus-margin-metering-and-byok-pricing-model.md)
- [Story 32-10 — Benchmark Projections & Leaderboards](./story-32-10/32-10-benchmark-projections-and-leaderboards.md)
- [Story 32-11 — Learning Persistence & Auto-Learning into RAG](./story-32-11/32-11-learning-persistence-and-auto-learning-into-rag.md)
- [Story 32-12 — Agent Personas & Persona-Aware Benchmarking](./story-32-12/32-12-agent-personas-and-persona-aware-benchmarking.md)
- [Story 32-13 — Agent Management & Benchmark Dashboards](./story-32-13/32-13-agent-management-and-benchmark-dashboards.md)
- [Story 32-14 — A/B Experiment Framework for Agents (Phase 2)](./story-32-14/32-14-a-b-experiment-framework-for-agents.md)
- [Prompt Store Architecture](../../../CLAUDE.md) — the RBAC + per-mode ownership pattern Epic 32 mirrors
- [Unified Schema-per-Tenant Tenancy](../../../CLAUDE.md) — control-plane vs `t_<hex>` placement model

---

**Last Updated**: 2026-06-17
**Epic Owner**: TBD
**Implementation Start**: TBD
**Total Estimated Effort**: 55-69 days
