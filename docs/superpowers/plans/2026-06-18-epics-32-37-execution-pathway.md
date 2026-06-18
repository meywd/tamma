# Epics 32/34/35/36/37 — Parallel Multi-Agent Execution Pathway

**Date:** 2026-06-18  ·  **Status:** ready to execute (start after `/clear`)  ·  **Stories:** 59 across 5 epics  ·  **Waves:** 12

> Dependency-ordered into parallel waves (computed from each story's Dependencies). A wave's stories have **all their new-story prerequisites satisfied by earlier waves**, so every story in a wave can be implemented concurrently. Per-story detail: `docs/stories/epic-N/story-N-M/` + its plan in `docs/superpowers/plans/2026-06-17-<key>-*-plan.md`.

## Execution model

- **Dispatch:** one agent per story, in **Agent-tool batches** (≈8–12 concurrent). DO NOT use the background Workflow tool for this — it stalled repeatedly on this host (low concurrency + pauses across sleeps); the Agent tool was reliable. (See MEMORY.)
- **Per story (TDD):** the agent reads the story file + its plan doc, then Red→Green→Refactor against the **C# `apps/tamma-elsa`** stack (or `packages/*` for TS dashboard/provider stories). Tests colocated; follow `BEFORE_YOU_CODE.md` (docs/guides/).
- **Branch & PR:** one branch + PR **per wave** (`feat/exec-wave-NN`), reviewable in isolation. Docs-only changes never deploy; **code changes deploy ONLY via a `qa-*` tag** (deploy gate from #348), so merging a wave PR to main will NOT auto-deploy — cut a `qa-*` tag when you want the VPS to take a wave.
- **Verify gate between waves:** build green + tests green before starting the next wave.
  - C#: `cd apps/tamma-elsa && dotnet build Tamma.sln` then `sg docker -c "dotnet test Tamma.sln -v minimal --no-build"` (sg docker wrapper required for tests; ~4,500+ tests must stay green).
  - TS: `pnpm build` + `pnpm vitest run` (root) + affected dashboard suites.
- **Cross-epic boundaries (already verified in specs):** 32-3 owns BYOK key resolution · 34-3 owns pricing-mode · 34-5 owns markup engine · 32-4 owns SaaS gating · 32-9 produces usage (35/36 consume). Do not re-implement across stories.
- **Adversarial verify each wave:** after a wave's stories land, run a verification batch (reviewer agents) for correctness + boundary adherence before the wave PR.


## External prerequisites (status)

- **Epic 3** — gates DONE
- **Epic 4** — DONE (DCB event store)
- **Epic 5** — alerts/notification shipped (5-6)
- **Epic 6** — ⚠ FOUNDATION ONLY (RAG not wired into engine) — 32-5/32-11 integrate it
- **Epic 9** — ⚠ PARTIAL (unified agent API not finished) — build against C# CallLlmActivity seam; full integration later
- **Epic 13** — DONE
- **Epic 16** — SSO DONE
- **Epic 17** — SUPERSEDED (tenancy)
- **Epic 18** — PARTIAL (auth/admin)
- **Epic 20** — SUPERSEDED by Epics 34/35 (refs historical)
- **Epic 21** — partly superseded
- **Epic 27** — DONE (prompt/convention store)
- **Epic 28** — DONE (schema-per-tenant)
- **Epic 29** — CABINET DONE; provider-key wiring is 32-3 itself
- **Epic 32** — (check status)

> Hard external blockers to resolve/decide before the dependent stories: **Epic 9** (unified agent API) and **Epic 6** (RAG wiring). The story specs were written to build against the existing C# `CallLlmActivity`/interfaces so most can proceed; full runtime integration of 32-5/32-7/32-11 may need 9/6 first — decide per story at Wave 4–7.

## The waves

### Wave 1 — 5 stories (epics 32, 34, 35, 36, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-1 | [P0] Agent Entity Model & Versioned Saved Config (public/private) |(no new-story deps) |Epic 27, Epic 28 |
| 34-1 | [P0] Plan & Price-Book Catalog Data Model |(no new-story deps) |Epic 28, Epic 4 |
| 35-1 | [P0] Stripe Integration Foundation, Billing Plan Catalog & Customer Mapping (C#) |(no new-story deps) |Epic 28, Epic 29, Epic 4 |
| 36-1 | [P0] Dimensional Analytics Projection Schema & Store |(no new-story deps) |Epic 28, Epic 4 |
| 37-1 | [P0] Sensitive-Action Audit Taxonomy & Curated Audit-Record Projection |(no new-story deps) |Epic 27, Epic 28, Epic 4 |

### Wave 2 — 8 stories (epics 32, 34, 35, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-2 | [P0] Agent Registry, Resolution & RBAC API |after 32-1 | |
| 32-3 | [P0] Per-Tenant Provider Credential Resolution (BYOK → platform) |after 32-1 |Epic 29 |
| 34-2 | [P0] Plan Catalog Admin API & Custom Enterprise Plans |after 34-1 | |
| 35-5 | [P0] Stripe Webhook Ingestion, Idempotency & Billing Event Projection |after 35-1 | |
| 37-2 | [P0] Tamper-Evident Hash-Chain over Audit Records |after 37-1 |Epic 29, Epic 5 |
| 37-3 | [P0] Audit Query, Search & Filter API |after 37-1 |Epic 28 |
| 37-6 | [P1] Legal Hold |after 37-1 | |
| 37-10 | [P0] Sensitive-Action Audit Emission Coverage (BYOK, Billing/Plan, Auth/Login, Agent Actions) |after 37-1 |Epic 20, Epic 29, Epic 32 |

### Wave 3 — 7 stories (epics 32, 34, 35, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-4 | [P0] SaaS Provider Auth Gating — API-key only (CLI/token providers single-user only) |after 32-2, 32-3 | |
| 34-4 | [P0] Per-Tenant Plan Assignment & Lifecycle |after 34-1, 34-2 | |
| 35-4 | [P0] Subscription Lifecycle — Create, Upgrade/Downgrade, Cancel, Trial & Proration |after 35-1, 35-5 | |
| 35-7 | [P1] Payment Methods & Self-Service Stripe Billing Portal |after 35-1, 35-5 | |
| 37-4 | [P1] Signed Audit Export (JSON/CSV) with Integrity Manifest |after 37-2, 37-3 |Epic 29 |
| 37-5 | [P1] Audit Retention Policies & Tamper-Aware Pruning |after 37-1, 37-2, 37-6 | |
| 37-9 | [P2] Consent & Data-Processing Logging |after 37-1, 37-2 | |

### Wave 4 — 5 stories (epics 32, 34, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-5 | [P0] Managed Agent Execution Layer (IManagedAgent over IAIProvider) |after 32-2, 32-3, 32-4 |Epic 27, Epic 6 |
| 34-3 | [P0] BYOK vs Platform-Provided Pricing Mode (per-provider, secret-cabinet wired) |after 32-3, 32-4, 34-1 |Epic 29, Epic 32 |
| 34-6 | [P0] Entitlement & Quota Resolution Service |after 34-1, 34-4 | |
| 37-7 | [P1] GDPR DSAR — Data Subject Access Export |after 37-1, 37-4 |Epic 28 |
| 37-11 | [P2] SOC2-Aligned Control Mapping & Evidence Pack |after 37-10, 37-2, 37-3, 37-4, 37-5, 37-6 | |

### Wave 5 — 4 stories (epics 32, 34, 35, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-6 | [P0] Agent Action Trail (DCB events tagged agent_id) in Tenant Store |after 32-5 |Epic 17, Epic 4 |
| 34-5 | [P0] Cost->Price Markup Engine (platform-provided usage) |after 34-1, 34-3 | |
| 35-2 | [P0] BYOK vs Platform-Provided Billing Mode & Per-Tenant Provider Key Cabinet Integration |after 32-3, 34-3, 35-1 |Epic 27, Epic 29 |
| 37-8 | [P1] GDPR Right-to-Erasure with Crypto-Shredding & Audit Preservation |after 37-2, 37-6, 37-7 |Epic 29 |

### Wave 6 — 8 stories (epics 32, 34, 35, 37)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-7 | [P1] Multi-Agent Design/Review Panels in Elsa (strategy-driven) |after 32-5, 32-6 | |
| 32-8 | [P1] Outcome Capture & Bug Taxonomy at Review/Gate |after 32-6 |Epic 13, Epic 3 |
| 32-9 | [P1] Cost-Basis-Plus-Margin Metering & BYOK Pricing Model (re-targets Epic 20) |after 32-3, 32-5, 32-6 |Epic 20 |
| 34-7 | [P1] Trials, Credits & Promo Codes |after 34-4, 34-5 | |
| 34-8 | [P1] Pricing Audit, Events & Reproducibility |after 34-1, 34-4, 34-5 |Epic 4 |
| 34-10 | [P0] Epic 20 Decommission & Pricing Contract Migration |after 34-1, 34-3, 34-4, 34-5, 34-6 | |
| 35-3 | [P0] BYOK-Aware Usage Metering & Stripe Meter Event Reporting |after 34-5, 35-1, 35-2 |Epic 5, Epic 9 |
| 37-12 | [P1] Admin & Tenant Audit Dashboard UI |after 37-11, 37-2, 37-3, 37-4, 37-5, 37-6, 37-7, 37-8, 37-9 | |

### Wave 7 — 5 stories (epics 32, 34, 35, 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-10 | [P1] Benchmark Projections & Leaderboards (per agent/provider/prompt, per-tenant) |after 32-6, 32-8, 32-9 | |
| 32-11 | [P1] Learning Persistence & Auto-Learning into RAG |after 32-5, 32-8 |Epic 6, Epic 9 |
| 34-9 | [P1] Pricing & Plan Management Dashboards |after 34-2, 34-5, 34-6, 34-7 | |
| 35-6 | [P0] Plan Quota & Usage-Limit Enforcement (BYOK-Aware) |after 35-3, 35-4 | |
| 36-7 | [P0] Pricing / Cost-Basis & Platform Margin Model |after 32-9, 34-5 |Epic 20, Epic 29, Epic 9 |

### Wave 8 — 4 stories (epics 32, 35, 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-12 | [P2] Agent Personas & Persona-Aware Benchmarking |after 32-1, 32-10, 32-2, 32-5 |Epic 27 |
| 35-8 | [P0] Invoicing, Failed-Payment Dunning & Recovery |after 35-3, 35-5, 35-6 | |
| 36-2 | [P0] DCB-to-Analytics Projection Pipeline (Dimensional Rollup) |after 36-1, 36-7 |Epic 29, Epic 32 |
| 36-10 | [P1] Platform Business Analytics (Owner-Only: MRR, Churn, Conversion) |after 36-7 |Epic 16, Epic 20 |

### Wave 9 — 6 stories (epics 32, 35, 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-13 | [P2] Agent Management & Benchmark Dashboards (admin public + tenant private) |after 32-10, 32-12, 32-2, 32-3, 32-9 | |
| 35-9 | [P1] Tax Calculation & Compliance (Stripe Tax / VAT) |after 35-1, 35-4, 35-8 | |
| 35-10 | [P2] Credits & Prepaid Wallet Ledger |after 35-1, 35-5, 35-8 | |
| 36-3 | [P0] Tenant Usage Analytics API |after 36-1, 36-2 |Epic 18, Epic 28 |
| 36-4 | [P0] Cost & Spend Analytics API (BYOK vs Platform) |after 34-5, 36-1, 36-2, 36-7 |Epic 20, Epic 3 |
| 36-11 | [P2] Analytics Event Catalog, Backfill & Reconciliation |after 36-1, 36-2 |Epic 4 |

### Wave 10 — 4 stories (epics 32, 35, 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 32-14 | [P2] A/B Experiment Framework for Agents (Phase 2: cohorts, significance, rollout/rollback) |after 32-10, 32-13, 32-2, 32-5, 32-9 | |
| 35-11 | [P1] Tenant Billing Dashboard (dashboard-user) & Admin Billing Console (dashboard) |after 35-10, 35-2, 35-3, 35-4, 35-7, 35-8 | |
| 35-12 | [P1] Billing Audit, Reconciliation & Revenue Analytics |after 35-10, 35-3, 35-4, 35-5, 35-8 |Epic 5 |
| 36-5 | [P1] Agent & Tenant Performance Rollups API (consume Epic 32) |after 36-1, 36-2, 36-3 |Epic 32, Epic 9 |

### Wave 11 — 2 stories (epics 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 36-6 | [P1] Tenant Analytics Dashboard UI |after 36-3, 36-4, 36-5 |Epic 18, Epic 21 |
| 36-8 | [P1] Analytics Exports (CSV / PDF) |after 36-3, 36-4, 36-5 |Epic 28 |

### Wave 12 — 1 stories (epics 36)

| Story | Title | Depends on (new) | External |
|---|---|---|---|
| 36-9 | [P2] Scheduled Reports & Delivery |after 36-10, 36-8 |Epic 18, Epic 27 |

## Suggested sequencing notes

- **Wave 1 is the foundation** (32-1 agent entity, 34-1 price-book, 35-1 Stripe foundation, 36-1 analytics schema, 37-1 audit taxonomy) — everything else builds on these; do this wave first and verify hard.
- Waves 1–6 are the **P0 backbone** (entities, credentials, metering, enforcement, audit substrate). Waves 7–12 are dashboards, reports, A/B, exports (mostly P1/P2) — can be deferred or parallelized more loosely.
- **Dashboards/UI stories** (32-13, 35-11, 36-6, 37-12) are TS/React (`packages/dashboard`, `packages/dashboard-user`) — can run on a separate track from the C# backend stories in the same wave.
- Keep `docs/sprint-status.yaml` updated as stories move drafted → in-progress → done.

