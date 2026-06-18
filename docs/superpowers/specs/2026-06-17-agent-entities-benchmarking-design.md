# Design Spec — Agent Entities, Personas, Benchmarking & Learning (Epic 32)

**Date:** 2026-06-17
**Status:** Approved (brainstorm converged with user)
**Epic:** 32
**Related epics:** 1 (providers), 4 (DCB event sourcing), 6 (RAG/context), 9 (unified agent API), 27 (prompt store), 28 (tenancy), 29 (secret cabinet), 34 (pricing), 35 (billing), 36 (analytics), 37 (audit)

## Problem

Tamma's workflows invoke a single agent per phase, keyed only by a static **role** (architect, reviewer, …). There is no first-class, persisted **agent entity** whose configuration, actions, and performance can be tracked and compared over time. We want:

- Multiple named dev-agents to participate in **design and review** steps of workflows.
- Agents with **dynamic config** but **stable identity**, so performance can be tracked and benchmarked by configuration (provider, prompt, model, …).
- **Learning and outcome tracking**: successes/failures, iterations-to-done, and bug classes (visual vs functional vs …).
- **Context management + RAG** wired into agent execution.
- A **customization layer above LLM API providers**, distinct from CLI agent providers.

## Core concept: the Agent is the entity

An **Agent** is a first-class, persisted entity — a saved configuration given a stable identity — tracked along two dimensions: **what it did** (actions) and **how well it did** (performance).

- **Identity**: immutable `agent_id` + stable `name` (e.g., `atlas`). The join key for all metrics, so history survives config edits.
- **Saved config (versioned)**: provider chain, prompt overrides, budget, allowed tools, temperature, RAG settings. Each version is pinned so any action/metric ties to the exact config that produced it.
- **Role attribute**: architect/reviewer/tester/… — retained so like-vs-like benchmarking is possible (reviewer A vs reviewer B), but the Agent, not the role, is the tracked entity.
- **Backing execution layer**: either the managed-LLM-agent layer (over `ILLMProvider`) or a CLI agent provider — but the *entity* is independent of what runs it.

## Ownership, visibility & data scoping (the key tenancy rule)

Definition ownership and **data** ownership are separate:

| Concern | Scope |
|---|---|
| **Agent definition** | **Public/system-wide** (owned & edited by platform admin; available to every tenant) **OR** **private/tenant-owned** (owned & edited by a tenant; available only to it). Shipped defaults are public. |
| **Performance + action data** | **ALWAYS tenant-scoped** — belongs to the tenant that generated it; never system-wide, never cross-tenant. |

→ A tenant's usable agent set = **all public agents ∪ its own private agents**.
→ **One-to-many**: one agent definition → many independent per-tenant performance/action datasets. Two tenants running public agent `atlas` build *separate, private* profiles; neither sees the other's; the platform admin who owns `atlas` sees **none** of it.
→ Leaderboards/benchmarks are computed **within a tenant's own data**.

This maps cleanly onto the unified schema-per-tenant model:
- **Public agent definitions** live in the **control-plane** (shared), referenced by `agent_id`.
- **Private agent definitions + all performance/action data** live in the **tenant's `t_<hex>` schema** — isolation is structural.

**Single-user mode:** "public" = shipped system agents; the sole user owns/creates private ones; performance is the user's.

**RBAC** (mirrors the Prompt Store): public agent CRUD → platform owner/admin (`OwnerAccess`); private agent CRUD → tenant owner/admin; members read. Performance/action read → tenant members (own tenant only); platform admin **cannot** read any tenant's performance.

## Provider credential & auth model

- **BYOK per tenant**: a tenant configures their own API key per provider, stored encrypted in the Epic 29 secret cabinet. BYOK usage hits the tenant's own provider account.
- **Platform-provided otherwise**: usage-metered and billed by us (Epic 34/35). Resolution order per tenant: **BYOK key (cabinet) → else platform-provided**.
- **SaaS = API-key auth only.** The managed-LLM-agent layer (`ILLMProvider`) is the sole execution path in SaaS. **Token-based / CLI agent providers (`ICLIAgentProvider`) are NOT available in SaaS** — they remain single-user/self-hosted only.
- Agent definitions are **credential-agnostic** (provider + model + prompt + settings, never raw keys); credentials resolve at execution from the principal's secret source. So a public agent run by a tenant executes with *that tenant's* key/budget → cost & performance are genuinely the tenant's.

## Managed-LLM-agent layer (above the LLM API, distinct from CLI)

`IManagedAgent` composed over `ILLMProvider`, adding: context assembly (RAG) → prompt rendering (agent config + Prompt Store) → tool loop (`executeToolLoop`) → sanitization (`SecureAgentProvider`) → instrumentation (cost/diagnostics) → outcome capture. The resolver returns an `IManagedAgent` whether backed by an LLM-API provider (this layer) or a CLI provider, so workflows treat them identically. SaaS exposes only the LLM-API path.

## Multi-agent design/review steps (C# Elsa + Epic 9 unified API)

- **`RunAgentPanelActivity`** fans a step out to N agents of the relevant role(s); **`AggregatePanelActivity`** combines results. Strategies: `single`, `consensus/vote`, `lead+critics`, `llm-judge-merge`. Defaults: `lead+critics` (design), `consensus` (review).
- **Design step**: architect agents propose → aggregate to a chosen/synthesized design. **Review step**: reviewer agents incl. specialized **security / performance / visual** reviewers → aggregate to verdict + classified findings.
- Workflow tracks `iteration_count` and loops until gates pass or max iterations; every iteration emits events.

## Tracking: actions + performance + learning

- **Action trail** (DCB events tagged `agent_id`, config version, role, provider, model, prompt key/version, issueId, iteration) in the **tenant** event store. Event families: `AGENT.TASK.SUCCEEDED/FAILED/PARTIAL`, `AGENT.ITERATION.COMPLETED`, `REVIEW.BUG.RECORDED` (`bugType: visual|functional|regression|security|perf|style`), `AGENT.PANEL.AGGREGATED`, plus usage/cost emission (tokens + provider/model cost basis) consumed by Epics 34/35/36.
- **Performance projections**: per-tenant leaderboards — success rate, avg iterations-to-done, bug counts by type, cost, latency — sliceable by agent / provider / prompt / version.
- **Learning**: persist `LearningCapture`/`KnowledgeEntry`; auto-generate learnings from outcomes; feed into the KB so RAG retrieves them in future runs.
- **Phase 2**: A/B experiment framework — cohorts, config-variant assignment, statistical significance, auto rollout/rollback on regression.

## Story breakdown (Epic 32, 14 stories)

1. `32-1` Agent entity model & versioned saved config (public/private)
2. `32-2` Agent registry, resolution & RBAC API
3. `32-3` Per-tenant provider credential resolution (BYOK → platform) — **canonical owner of cabinet key wiring**
4. `32-4` SaaS provider auth gating — API-key only (CLI/token providers single-user only)
5. `32-5` Managed agent execution layer (`IManagedAgent` over `ILLMProvider`)
6. `32-6` Agent action trail (DCB events tagged `agent_id`) in tenant store
7. `32-7` Multi-agent design/review panels in Elsa (strategy-driven)
8. `32-8` Outcome capture & bug taxonomy at review/gate
9. `32-9` Agent usage & cost emission (producer; consumed by Epics 34/35/36 — **markup engine is 34-5, not here**)
10. `32-10` Benchmark projections & leaderboards (per agent/provider/prompt, per-tenant)
11. `32-11` Learning persistence & auto-learning into RAG
12. `32-12` Agent personas & persona-aware benchmarking
13. `32-13` Agent management & benchmark dashboards (admin public + tenant private)
14. `32-14` A/B experiment framework (Phase 2: cohorts, significance, rollout/rollback)

## Non-goals
- Pricing/markup engine (Epic 34), invoicing/payment (Epic 35), business analytics (Epic 36), audit product features (Epic 37) — Epic 32 *produces* the data these consume.
- New provider implementations (Epic 1-10).
- Implementing the Epic 9 unified API itself (dependency).

## Risks
- **Global provider keys today**: cost isn't truly per-tenant until BYOK/platform per-tenant keys land (32-3 + Epic 29 wiring). Tracking works regardless; cost attribution is the gap.
- **Panel cost**: multi-agent panels multiply token spend — budget clamps + max-iteration caps required.
- **Cross-tenant leakage**: performance/action data must never escape the tenant schema — enforce via the per-tenant connection/role; covered by isolation tests.
