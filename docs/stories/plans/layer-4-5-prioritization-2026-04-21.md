# Layer 4 + Layer 5 — Prioritization Across All Epics in Scope

**Status**: active, written 2026-04-21
**Branch**: `feat/auth-foundation`
**Scope**: every story currently ranked for Layer 4 or Layer 5 — Epics 9, 12, 16, 17, 18 (+ 18-7/18-8), 19 (19-6 follow-up), 27, 28, 29, 30, 31, plus Epic 33 deferred stub.
**Companion docs**:

- [`layer-4-integration-ui.md`](./layer-4-integration-ui.md)
- [`layer-4-with-epic-28.md`](./layer-4-with-epic-28.md)
- [`layer-5-validation.md`](./layer-5-validation.md)
- [`layer-2-3-status-post-epic-19.md`](./layer-2-3-status-post-epic-19.md)
- [`epic-29-30-placement.md`](./epic-29-30-placement.md)
- [`epic-31-33-placement.md`](./epic-31-33-placement.md)
- [`tenant-user-mgmt-audit.md`](./tenant-user-mgmt-audit.md)
- [`wave-2-impl-plan-inventory.md`](./wave-2-impl-plan-inventory.md)
- [`wave-3-impl-plan-inventory.md`](./wave-3-impl-plan-inventory.md)

This document is the **ordering contract** for the Layer 4 + Layer 5 stories across all epics in scope. It is not a new plan — the execution plans already exist. It is the single answer to "which story do I schedule next, and why?" given the 2026-04-20 review and the product-state reality.

---

## 1. Executive summary — top 10 stories for the next ~6 weeks

Ordered by rank. Each story is "do this before the one below it, unless a team-parallelism exception applies (documented in §4 waves)".

| # | Story | Epic | Hours | Why it ranks here |
|---|---|---|---:|---|
| 1 | **19-6** Wire per-request repos onto `TammaAppDbContext` | 19 | 16.5 | Closes P0 review finding #1 (scaffold-only RLS). Without it, the entire Phase-3 RLS plane is dead code — every Layer 4 tenant story inherits the same false-remediation. |
| 2 | **28-1** EF migration scripts (CP + tenant + global-Elsa + per-tenant Elsa) | 28 | 20 | Nothing in Epic 28 runs until these compile. Epic 28 is the gate for every multi-tenant SaaS feature; 28-1 is the first line of that gate. |
| 3 | **28-2** Split `TammaDbContext` → `ControlPlaneDbContext` | 28 | 16 | Unblocks 28-3, 28-5, 28-9. Pure serial on 28-1. |
| 4 | **28-3** `TenantDbContext` factory with runtime routing | 28 | 18 | Team D (Epic 12) and every tenant-scoped repository await this stub. |
| 5 | **9-5** Provider chain API (C#) | 9 | 14 | Unblocks Team A's whole chain (9-9 → 9-10 → 9-11 → 9-12). Cheap, foundational, co-requires nothing outside the punch list (already closed). |
| 6 | **28-6** `platform_events` + queue + outbox tables | 28 | 18 | Hard blocker for 28-5 (provisioning workflow emits events) and 28-7 (key-index routing). Parallel with 28-4. |
| 7 | **28-4** Tenant connection resolver + LRU pool cache | 28 | 21 | Replaces 28-3 stub. Every tenant request pays through this; without it, 28-5/28-7/28-8/28-9/29-2/30-8 all stay stubbed. |
| 8 | **29-1** Secret store abstraction + typed data model | 29 | 16 | Shape contract for the entire secrets cabinet. Unblocks 29-2..29-10 and 31-2's credential store. Writing this interface wrong costs 40h of rework. |
| 9 | **18-8** Tenant-admin user management UI | 18 | 32 | Closes the P1 "user can't add users to their tenant" review-sweep finding. Backend is 90% done (18-7 adds 14h of thin completions). Largest visible-to-customers gap for first tenant. |
| 10 | **28-9** JWT claims + `/auth/switch-org` + cross-tenant refresh | 28 | 21 | Unblocks Team B (27-5) and Team C (18-5 dashboard shell, 18-8 member UI). Every multi-tenant user session needs this. |

The next 10 stories (27-4, 27-5, 18-4, 18-5, 29-2, 29-3, 28-5, 28-7, 28-8, 28-12) are ranked in §3 and §4.

---

## 2. Scoring methodology

Each story is scored across five weighted criteria, total 100 points:

| Criterion | Weight | What scores high |
|---|---:|---|
| **Security / correctness** | 30% | Closes a P0/P1 review finding. Closes a known tenant-leak, auth-bypass, or data-loss surface. |
| **Product-blocking** | 25% | On the path from "installed" to "first tenant successfully runs an agent". First-customer blocker. |
| **Dependencies** | 20% | Hard blocker for many other high-priority stories. Unblock-many = high. |
| **Value per hour** | 15% | Impact ÷ estimated hours. High-leverage quick wins. |
| **Risk early** | 10% | Unknown-unknowns, external API dependencies (Cranl, Hetzner, Cloudflare, GitLab), cross-language bridges. Ship risky work early so integration pain lands before launch. |

Per-criterion scale is 1–5 (5 = strong match). Final score = Σ (score × weight). Maximum 5.00; effective bottom ~1.0.

### Scoring notes

- **Security** weighted highest because the 2026-04-20 review has an open P0 plus four P1s. Shipping customer-visible features on top of an insecure substrate is worse than shipping less, later, on a sound one.
- **Value per hour** is deliberately capped at 15%: we don't want "cheap pretty UI" to sort above "hard plumbing that unlocks the whole plane". Still meaningful — a 6h story that closes a P2 outranks a 35h story that does the same.
- **Risk early** is small (10%) but tie-breaking: between two similar-scored stories, prefer the one with external-API exposure so CI/integration surprises bite early.
- **Dependencies** uses the dependency graph in §5 — a story is scored "5" only if two or more high-priority stories block on it.

### Judgment calls baked into the scoring

1. **Single-tenant-SaaS launch is implicit "product priority"** — so Epic 28 phase A/B ranks as "Product-blocking: 4" even though Tamma could technically launch as a single-tenant service with Epic 28 deferred. If product reverses this (launch single-tenant, add multi-tenant post-launch), ~12 stories shift one wave later. Flagged in §6.
2. **Cranl stays the sole provisioning backend for launch** — Epic 30 scores low on Product-blocking (1–2) because Cranl already works. If a Hetzner-first or Cloudflare-first enterprise deal lands, 30-4 / 30-5 jump two waves. Flagged in §6.
3. **Secrets and rotation are a Year-1 compliance ask, not a launch gate** — Epic 29 ranks mid-wave, closing review findings 4/15/16 which are P1/P2, not P0. If a SOC 2 audit is near-term, 29-9/29-10 move into Wave A.
4. **Enterprise SSO is explicitly deferred** — Epic 33 is not scored; it enters the roadmap when a trigger condition fires.

---

## 3. Ranked scoring table

Every story in Layer 4 and Layer 5 scope, ranked by weighted score (desc). Ties broken by dependency count, then by hours (smaller wins). Wave assignment in §4.

Legend for criterion columns: S=Security, P=Product-blocking, D=Dependencies, V=Value/hr, R=Risk-early.

| Rank | Story | Epic | Hours | S (30%) | P (25%) | D (20%) | V (15%) | R (10%) | Score | Wave |
|---:|---|---|---:|:---:|:---:|:---:|:---:|:---:|---:|:---:|
| 1 | 19-6 wire app-role DbContext | 19 | 16.5 | 5 | 3 | 5 | 5 | 2 | **4.20** | A |
| 2 | 28-1 EF migration scripts | 28 | 20 | 3 | 5 | 5 | 3 | 3 | **3.95** | A |
| 3 | 28-2 ControlPlaneDbContext split | 28 | 16 | 3 | 4 | 5 | 4 | 2 | **3.70** | A |
| 4 | 28-3 TenantDbContext factory | 28 | 18 | 3 | 4 | 5 | 4 | 2 | **3.70** | A |
| 5 | 9-5 Provider chain API | 9 | 14 | 2 | 4 | 5 | 5 | 2 | **3.55** | A |
| 6 | 28-6 platform events + queue + outbox | 28 | 18 | 3 | 4 | 5 | 4 | 2 | **3.70** | A |
| 7 | 28-4 Tenant connection resolver + pool | 28 | 21 | 4 | 4 | 5 | 3 | 3 | **3.95** | A |
| 8 | 29-1 Secret store abstraction | 29 | 16 | 4 | 3 | 5 | 4 | 3 | **3.85** | A |
| 9 | 28-9 JWT claims + switch-org | 28 | 21 | 4 | 5 | 4 | 3 | 2 | **3.90** | A |
| 10 | 28-8 TenantContextMiddleware | 28 | 12 | 4 | 4 | 4 | 5 | 2 | **3.95** | A |
| 11 | 28-7 API-key prefix routing | 28 | 24 | 4 | 4 | 3 | 3 | 2 | **3.45** | A |
| 12 | 28-5 CreateTenant / DeleteTenant workflows | 28 | 33 | 3 | 5 | 4 | 3 | 4 | **3.70** | A |
| 13 | 18-8 Tenant-admin user mgmt UI | 18 | 32 | 4 | 5 | 2 | 3 | 2 | **3.50** | A |
| 14 | 18-7 Tenant-admin user mgmt API completion | 18 | 14 | 3 | 4 | 3 | 5 | 2 | **3.45** | A |
| 15 | 28-12 Postgres roles + KEK rotation | 28 | 19 | 5 | 3 | 3 | 4 | 3 | **3.75** | A |
| 16 | 29-2 Postgres-backed secret store | 29 | 20 | 4 | 3 | 4 | 3 | 3 | **3.35** | A |
| 17 | 29-3 Reveal-once UX | 29 | 10 | 3 | 3 | 4 | 5 | 2 | **3.40** | A |
| 18 | 18-5 User-facing dashboard shell | 18 | 40 | 2 | 5 | 3 | 2 | 3 | **3.05** | A |
| 19 | 18-4 GitHub App installation onboarding | 18 | 24 | 3 | 5 | 2 | 3 | 4 | **3.40** | A |
| 20 | 27-4 Prompt Store admin UI | 27 | 18 | 2 | 3 | 2 | 4 | 2 | **2.55** | B |
| 21 | 27-5 Prompt Store tenant UI | 27 | 16 | 2 | 3 | 2 | 4 | 2 | **2.55** | B |
| 22 | 9-9 Engine integration (TS → C# API) | 9 | 18 | 2 | 4 | 3 | 4 | 3 | **3.10** | B |
| 23 | 9-11 Diagnostics queue + Elsa integration | 9 | 24 | 2 | 4 | 3 | 3 | 3 | **2.95** | B |
| 24 | 9-10 CLI wiring + fallback | 9 | 14 | 1 | 3 | 2 | 4 | 3 | **2.35** | B |
| 25 | 9-12 Cross-epic integration test | 9 | 17 | 3 | 3 | 2 | 3 | 3 | **2.80** | B |
| 26 | 29-4 Platform-admin secret UI | 29 | 24 | 3 | 3 | 2 | 3 | 2 | **2.65** | B |
| 27 | 29-5 Tenant-admin secret UI | 29 | 20 | 3 | 3 | 2 | 3 | 2 | **2.65** | B |
| 28 | 29-6 Rotation workflow primitive | 29 | 16 | 4 | 2 | 4 | 3 | 3 | **3.25** | B |
| 29 | 29-7 Postgres role-password rotation | 29 | 15 | 4 | 2 | 2 | 3 | 3 | **2.85** | B |
| 30 | 29-8 Cranl env-var rotation | 29 | 16 | 3 | 2 | 2 | 3 | 3 | **2.55** | B |
| 31 | 29-9 Migrate stopgap secrets | 29 | 20 | 4 | 2 | 3 | 3 | 3 | **3.05** | B |
| 32 | 29-10 Delete stopgaps | 29 | 13 | 4 | 2 | 2 | 4 | 2 | **2.90** | B |
| 33 | 28-11 Admin UX for `tenants.Status` | 28 | 25 | 2 | 3 | 2 | 3 | 2 | **2.40** | B |
| 34 | 28-10 Platform analytics rollup | 28 | 24 | 1 | 2 | 2 | 2 | 3 | **1.80** | C |
| 35 | 16-5 RBAC enforcement | 16 | 16 | 4 | 3 | 2 | 4 | 2 | **3.15** | A→B (see note below) |
| 36 | 17-3 Tenant-scoped event store helpers | 17 | 10 | 3 | 2 | 2 | 4 | 1 | **2.50** | B |
| 37 | 12-5a Context priority-based truncation | 12 | 16 | 1 | 3 | 2 | 3 | 2 | **2.15** | C |
| 38 | 12-5b Few-shot example injection | 12 | 20 | 1 | 2 | 2 | 2 | 2 | **1.80** | C |
| 39 | 12-5d A/B testing hooks | 12 | 12 | 1 | 2 | 2 | 3 | 2 | **2.05** | C |
| 40 | 12-7a Vector DB search tools | 12 | 24 | 1 | 3 | 4 | 3 | 3 | **2.65** | C |
| 41 | 12-7b Convention & history tools | 12 | 16 | 1 | 3 | 4 | 3 | 2 | **2.55** | C |
| 42 | 12-7c Context budget manager | 12 | 20 | 1 | 3 | 3 | 3 | 2 | **2.35** | C |
| 43 | 12-7d Tool access config per role | 12 | 12 | 2 | 3 | 2 | 4 | 2 | **2.55** | C |
| 44 | 12-7e Elsa tool loop integration | 12 | 28 | 2 | 4 | 2 | 3 | 4 | **2.85** | C |
| 45 | 31-1 IGitPlatformClient abstraction | 31 | 22 | 2 | 2 | 5 | 3 | 3 | **2.85** | C |
| 46 | 31-2 Platform registry + routing | 31 | 26 | 3 | 2 | 5 | 2 | 3 | **2.90** | C |
| 47 | 31-3 GitHub driver refactor | 31 | 24 | 3 | 2 | 4 | 2 | 2 | **2.60** | C |
| 48 | 31-4 Gitea driver | 31 | 28 | 2 | 2 | 3 | 2 | 4 | **2.40** | C |
| 49 | 31-5 Forgejo compat matrix | 31 | 9 | 1 | 1 | 2 | 4 | 3 | **1.85** | C |
| 50 | 31-7 Webhook receiver abstraction | 31 | 23 | 4 | 2 | 3 | 2 | 3 | **2.90** | C |
| 51 | 31-8 CI secrets provisioner abstraction | 31 | 20 | 3 | 2 | 3 | 2 | 3 | **2.60** | C |
| 52 | 31-9 Onboarding platform picker UI | 31 | 37 | 2 | 3 | 2 | 2 | 3 | **2.35** | C |
| 53 | 31-10 Integration test harness | 31 | 27 | 2 | 2 | 3 | 2 | 4 | **2.40** | C |
| 54 | 30-1 Provisioner v2 interface | 30 | 18 | 2 | 1 | 4 | 3 | 3 | **2.40** | C/D |
| 55 | 30-2 Provisioning workflow dispatch | 30 | 23 | 2 | 1 | 3 | 2 | 3 | **2.05** | C/D |
| 56 | 30-3 Cranl provider refactor to v2 | 30 | 17 | 2 | 1 | 3 | 3 | 2 | **2.10** | C/D |
| 57 | 31-6 GitLab driver | 31 | 37 | 2 | 2 | 2 | 2 | 5 | **2.40** | D |
| 58 | 30-4 Hetzner cloud provider | 30 | 30 | 2 | 2 | 2 | 2 | 5 | **2.40** | D |
| 59 | 30-5 Cloudflare provider | 30 | 30 | 2 | 2 | 2 | 2 | 5 | **2.40** | D |
| 60 | 30-6 BYO provider | 30 | 18 | 2 | 2 | 2 | 3 | 3 | **2.25** | D |
| 61 | 30-7 Onboarding UI (backend + topology picker) | 30 | 26 | 2 | 2 | 2 | 2 | 3 | **2.10** | D |
| 62 | 30-8 Per-tenant routing resolver | 30 | 20 | 4 | 2 | 3 | 3 | 2 | **2.85** | D |
| 63 | 30-9 Deprovisioning workflow | 30 | 21 | 2 | 1 | 2 | 2 | 3 | **1.95** | D |
| 64 | 30-10 Cost + quota dashboard | 30 | 26 | 1 | 1 | 2 | 2 | 3 | **1.65** | D |
| 65 | 31-11 Bitbucket driver | 31 | ~28 | — | — | — | — | — | — | Deferred |
| 66 | 31-12 Azure DevOps driver | 31 | ~36 | — | — | — | — | — | — | Deferred |
| 67 | 28-13 OpenBao KMS backend | 28 | ~35 | — | — | — | — | — | — | Deferred |
| 68 | Epic 33 stories | 33 | n/a | — | — | — | — | — | — | Deferred |

**Totals scored**: 64 active stories + 4 deferred. Combined active hours: ≈1,440 (excluding bridge overhead, punch list, deferred).

**Note on 16-5 RBAC**: scored 3.15 which places it in Wave A territory, but every design doc describes the RBAC permission matrix as already enforced at the handler level (hierarchy guards in `OrgEndpoints.cs`). The remaining work is nginx-level gating + dashboard route guards + 403 pages. If that subset is already done under the punch list (verify), demote 16-5 to Wave B. If not, keep in Wave A — it closes "defense in depth for every tenant-scoped endpoint" and synchronises with 28-9/28-8 pipeline order.

---

## 4. Waves

Waves are **logical** 4–6 week chunks. Actual wall-clock depends on team parallelism (§7). A story "belongs to Wave X" means "don't schedule it before Wave X is underway and don't let it block Wave X+1".

### Wave A — Launch-critical (first-customer blockers + P0/P1 security)

**Theme**: Ship a working multi-tenant SaaS instance with real RLS, real tenant lifecycle, and real secret plumbing. Close P0.

**Total hours**: ≈ 370h (does not include Team D's unblocked-by-28-3 parallel stream, which can run alongside starting at ~week 2 once 28-3 merges).

| Order | Story | Hours | Rationale |
|---:|---|---:|---|
| 1 | 19-6 wire app-role DbContext | 16.5 | P0 review finding #1. Make the RLS scaffold live. Do this in week 1 because everything downstream inherits the RLS assumption. |
| 2 | 28-1 EF migration scripts | 20 | Nothing in Epic 28 runs without schemas. |
| 3 | 28-2 ControlPlaneDbContext split | 16 | Serial on 28-1. |
| 4 | 28-3 TenantDbContext factory | 18 | Serial on 28-2. **Unblocks Team D.** |
| 5 | 28-6 platform_events + queue + outbox | 18 | Parallel with 28-4. Blocks 28-5 and 28-7. |
| 6 | 28-4 Tenant connection resolver + pool | 21 | Parallel with 28-6. Replaces 28-3 stub — blocks 28-5/28-8/28-9/29-2/30-8. |
| 7 | 9-5 Provider chain API | 14 | Can start in week 1 in parallel (Team A) — doesn't depend on Epic 28. Unblocks 9-9/9-11/9-12. |
| 8 | 29-1 Secret store abstraction | 16 | Parallel with Epic 28 Phase A (no Epic 28 dep until 29-2). Unblocks all of Epic 29 and 31-2. |
| 9 | 28-5 CreateTenant / DeleteTenant workflows | 33 | Serial on 28-4 + 28-6. Enables the full register → verify → provisioning loop. |
| 10 | 28-7 API-key prefix routing | 24 | Serial on 28-6. |
| 11 | 28-8 TenantContextMiddleware | 12 | Serial on 28-4 + 28-5. Async-provisioning status handling. |
| 12 | 28-9 JWT claims + switch-org | 21 | Serial on 28-2/28-4/28-8. **Unblocks Team B (27-5) + Team C (18-5/18-8).** |
| 13 | 28-12 Postgres roles + KEK rotation | 19 | Parallel with auth-plane stream (28-7/28-8/28-9). Closes the `changeme` password smell and the hard-coded role grants. |
| 14 | 29-2 Postgres-backed secret store | 20 | Serial on 29-1 + 28-3. Ships the real cabinet. |
| 15 | 29-3 Reveal-once UX | 10 | Serial on 29-2. Small, powers the admin + tenant UIs. Closes review finding part of #15 / #16. |
| 16 | 18-7 Tenant-admin user mgmt API completion | 14 | Serial on Epic 28 Phase B (for RLS defence on the audit view). Three thin gaps. |
| 17 | 18-8 Tenant-admin user mgmt UI | 32 | Serial on 18-7 + 18-5 (shell). P1 "user can't add users" finding. |
| 18 | 18-4 GitHub App installation onboarding | 24 | Serial on 18-3 (done) + 28-5 (async provisioning). First-customer onboarding. |
| 19 | 18-5 User-facing dashboard shell | 40 | Serial on 28-5/28-8/28-9. Canonical shell for every tenant UI (18-8, 27-5, 29-5, 30-7, 31-9). |
| 20 | 16-5 RBAC enforcement (nginx + dashboard route guards gap-fill) | 4–16 | Scope depends on what the punch list absorbed. Run in parallel with 28-7/28-8/28-9 (same auth-plane mental model). |

**Exit criterion for Wave A**:

1. New user can register → verify email → async provisioning completes → log in → see their tenant → add members → view audit log.
2. 19-6 integration test proves RLS fail-closed: forgetting a tenant filter returns zero rows, not a leak.
3. No P0 open in the 2026-04-20 review.
4. Wave A cross-epic smoke test (the narrower version of Layer 5 §5.1) passes.

### Wave B — Core SaaS features (secret mgmt, prompt UIs, Epic 9 completion, tenant user mgmt tail)

**Theme**: Finish the core "Tamma as a SaaS" experience. Real secrets cabinet + rotation, Prompt Store tenant/admin UIs, Epic 9 end-to-end (cross-language bridge for Layer 4 Team A).

**Total hours**: ≈ 290h.

| Order | Story | Hours | Rationale |
|---:|---|---:|---|
| 1 | 9-9 Engine integration | 18 | Serial on 9-5 (Wave A) + 28-9. |
| 2 | 9-11 Diagnostics queue + Elsa | 24 | Serial on 9-5 + 9-3 hardening (done). |
| 3 | 9-10 CLI wiring + fallback | 14 | Serial on 9-9. CLI mode completeness. |
| 4 | 9-12 Cross-epic integration test | 17 | Serial on 9-10 + 9-11. Closes Epic 9. |
| 5 | 27-4 Prompt Store admin UI | 18 | Parallel with 9-* stream. Depends only on punch list (done). |
| 6 | 27-5 Prompt Store tenant UI | 16 | Serial on 27-4 + 28-9 + 18-5. |
| 7 | 29-4 Platform-admin secret UI | 24 | Serial on 29-3. Parallel with 27-4. |
| 8 | 29-5 Tenant-admin secret UI | 20 | Serial on 29-4 + 18-5 + 28-9. |
| 9 | 29-6 Rotation workflow primitive | 16 | Serial on 29-2. Parallel with 29-4/29-5. |
| 10 | 29-7 Postgres role-password rotation | 15 | Serial on 29-6 + 19-6 (for pool drain). |
| 11 | 29-8 Cranl env-var rotation | 16 | Serial on 29-6. |
| 12 | 29-9 Migrate stopgap secrets | 20 | Serial on 29-7 + 29-8 + 29-4. Closes review findings 15/16. |
| 13 | 29-10 Delete stopgaps | 13 | Serial on 29-9 (one release cycle later — can slip into Wave C if needed). |
| 14 | 28-11 Admin UX for tenants.Status | 25 | Serial on 28-5. Operational observability. |
| 15 | 17-3 Event store helpers (tenant-scoped tx scope) | 10 | Small; useful for 12-7b + 27-7 Layer-2 follow-up. |

**Exit criterion for Wave B**:

1. Full tenant self-service loop: register → onboard → install GitHub App → add members → configure prompts → rotate a secret → run a workflow.
2. All Epic 9 stories merged; Layer 5 §5.1 test #3 (provider failover) passes.
3. No stopgap secrets in code; `TenantSecretProtector` deleted.
4. Review findings 4, 15, 16 closed.

### Wave C — Scale enablement (Epic 12 agent effectiveness + Epic 31 multi-platform)

**Theme**: Make the product better (Epic 12 prompt engineering + context tools) and wider (Epic 31 Gitea/Forgejo drivers). Epic 30 interface foundation lands here so Layer 5's multi-backend work is unblocked.

**Total hours**: ≈ 325h.

| Order | Story | Hours | Rationale |
|---:|---|---:|---|
| 1 | 12-7a Vector DB search tools | 24 | Serial on Epic 6 (done). Unblocks 12-7c/12-7e. |
| 2 | 12-7b Convention + history tools | 16 | Parallel with 12-7a. Unblocks 12-7c/12-7e. |
| 3 | 12-7c Context budget manager | 20 | Serial on 12-7a + 12-7b. |
| 4 | 12-7d Tool access config per role | 12 | Parallel with 12-7c. |
| 5 | 12-7e Elsa tool loop integration (+ bridge) | 28 + 16 | Serial on 12-7c + 12-7d. Heaviest single Layer-4 item. |
| 6 | 12-5a Context priority-based truncation | 16 | Parallel with 12-7 tracks. |
| 7 | 12-5b Few-shot injection | 20 | Parallel with 12-7 tracks. |
| 8 | 12-5d A/B testing hooks | 12 | Parallel. |
| 9 | 31-1 IGitPlatformClient abstraction | 22 | Foundation for every other Epic 31 story. Start in parallel with Epic 12 (different team). |
| 10 | 31-2 Platform registry + routing | 26 | Serial on 31-1 + 29-2 (for credentials) + 28-9. |
| 11 | 31-3 GitHub driver refactor | 24 | Serial on 31-1 + 31-2. Closes review finding "GitHub hard-coding on call sites". |
| 12 | 31-7 Webhook receiver abstraction | 23 | Serial on 31-3. Closes review finding "webhook HMAC hard-coded". Security-positive. |
| 13 | 31-4 Gitea driver | 28 | Serial on 31-1 + 31-2 + 31-3. Parallel with 31-7. |
| 14 | 31-5 Forgejo compat matrix | 9 | Serial on 31-4. Trivial. |
| 15 | 31-8 CI secrets provisioner abstraction | 20 | Serial on 31-3/31-4. |
| 16 | 31-9 Onboarding platform picker UI | 37 | Serial on 31-2/31-3/31-4/31-6(not yet) + 29-3/29-5 + 18-5. Gated on Cut-line: ship GitHub + Gitea only; add Forgejo trivially; GitLab lands in Wave D. |
| 17 | 31-10 Integration test harness (Gitea + Forgejo containers) | 27 | Parallel throughout Wave C. |
| 18 | 30-1 Provisioner v2 interface | 18 | Parallel. Unblocks Wave D (Hetzner, Cloudflare, BYO). |
| 19 | 30-2 Provisioning workflow dispatch | 23 | Serial on 30-1 + 28-5. |
| 20 | 30-3 Cranl provider refactor to v2 | 17 | Serial on 30-1 + 30-2. |

**Exit criterion for Wave C**:

1. Epic 12 agentic tool loop live behind `EnableToolLoop` flag on staging.
2. Tenant on Gitea can onboard end-to-end.
3. Review findings "webhook HMAC hard-coded" and "GitHub hard-coded call sites" closed.
4. Epic 30 interface compiled; Cranl running through v2; ready to drop in Hetzner + Cloudflare in Wave D.

### Wave D — Operational quality (multi-backend drivers, Layer 5 validation, review P2 cleanup)

**Theme**: Ship the remaining provisioning backends, run the full cross-epic harness, validate perf + security at scale. This is "Layer 5" in the layered plan plus the tail of Epic 30/31 drivers.

**Total hours**: ≈ 260h story work + ≈ 72h Layer 5 validation.

| Order | Story | Hours | Rationale |
|---:|---|---:|---|
| 1 | 31-6 GitLab driver | 37 | Serial on 31-1/31-2. External API risk — run early in Wave D. |
| 2 | 30-4 Hetzner cloud provider | 30 | Serial on 30-1 + 29-7. External API risk. |
| 3 | 30-5 Cloudflare provider | 30 | Serial on 30-1. External API risk. Parallel with 30-4. |
| 4 | 30-6 BYO provider | 18 | Serial on 30-1. |
| 5 | 30-8 Per-tenant routing resolver | 20 | Serial on 30-3/30-4/30-5/30-6 + 19-6. Closes review finding #1 (full close). |
| 6 | 30-7 Onboarding backend + topology picker UI | 26 | Serial on 30-3..30-6. |
| 7 | 30-9 Deprovisioning workflow | 21 | Serial on 30-3..30-6. |
| 8 | 30-10 Cost + quota dashboard | 26 | Serial on 30-8. |
| 9 | 28-10 Platform analytics rollup | 24 | Serial on 28-5 + 28-6. Admin observability; also carries the orchestrator-scale benchmark. Can slip into Wave D if not critical earlier. |
| 10 | Layer 5 §5.1 cross-epic harness | 16 | Extends 9-12. |
| 11 | Layer 5 §5.2 perf benchmarks | 12 | Artillery + k6 runs against staging. |
| 12 | Layer 5 §5.3 security audit | 16 | Cover the OWASP Top 10 + RLS + sanitization + secret surface. |
| 13 | Layer 5 §5.4 staging rehearsal | 8 | Full from-scratch deploy. |
| 14 | Layer 5 §5.5 wiki/docs refresh | 12 | Multi-tenant architecture diagrams. |
| 15 | Layer 5 §5.6 release notes + PR org | 8 | |
| 16 | Review P2 backlog cleanup | ~24 | 2026-04-20 findings 7–14, 17–18. Sequence: 14 (hex collision), 5 (webhook cross-tenant key), 6 (artifact size cap), 11 (401 cache eviction), 18 (TenantNotFound → 404), then the rest. |

**Exit criterion for Wave D**:

1. All Layer 5 success criteria in `layer-5-validation.md` green.
2. Four provisioning backends live (Cranl, Hetzner, Cloudflare, BYO).
3. Review P2 cleanup complete.
4. Staging rehearsal clean.
5. Release candidate tagged.

### Deferred — trigger-gated

Each of these does not land on the roadmap until an explicit trigger fires. Scope and estimate unchanged; re-evaluate when the trigger hits.

| Story / Epic | Trigger condition(s) |
|---|---|
| **28-13** OpenBao KMS backend | (a) first paying tenant with breach clause; (b) compliance finding against env-var KEK; (c) 10+ tenants; (d) OpenBao LF graduation **and** operator commitment. Until one fires, env-var KEK stays per `project_epic28_kek_decision.md`. |
| **31-11** Bitbucket driver | Paying customer with Bitbucket Cloud repos; or sales objection in ≥3 deals; or explicit product decision. Auth strategy decision (app-password deprecation) must resolve first. |
| **31-12** Azure DevOps driver | Enterprise customer with Azure DevOps; or specific partnership. Microsoft's PAT-less migration timeline guides auth strategy (Entra-first). |
| **Epic 33** Per-Tenant IdP | Triggers in `epic-33/README.md`: first enterprise with SSO contract, compliance auditor finding, ≥5 tenants ask in 60 days, SCIM objection, "Tamma Enterprise" plan launch. Tier selection (Lean/Full/Full+LDAP) at activation. |

---

## 5. Dependency graph — critical path and cross-epic tension

### 5.1 Textual critical path (Wave A)

```
19-6 ─┐
      ├─► "Real RLS live" invariant — everything downstream honours it
      │
28-1 ─► 28-2 ─► 28-3 ─┬─► 28-4 ─┬─► 28-5 ─┬─► 28-8 ─► 28-9 ─┬─► 27-5
                      │         │         │                 ├─► 18-5 ─┬─► 18-8
                      │         │         │                 │         ├─► 18-4 ─► 18-8 UI
                      │         │         │                 │         ├─► 27-5
                      │         │         │                 │         └─► 29-5
                      │         │         │                 └─► 29-5
                      │         │         └─► 28-11, 28-10
                      │         └─► 29-2 ─► 29-3 ─► 29-4 ─► 29-9 ─► 29-10
                      └─► Team D (Epic 12 tracks start)

28-6 ─┬─► 28-5 (same node above)
      └─► 28-7

29-1 ─► 29-2 (above)
29-1 ─► 31-2 (Wave C)

9-5 ─► 9-9 ─► 9-10 ─► 9-12
9-5 ─► 9-11 ─► 9-12
```

### 5.2 Cross-epic dependencies (the ones that bite)

1. **28-1 → 29-2 → 31-2** — secrets depend on CP schema; Git platform registry depends on secret store. If 28-1 slips, both waves behind slip.
2. **19-6 → 29-7 pool drain** — rotation can mint and push a new DB password, but old-pool drain only works once every per-request DbContext goes through `TammaAppDbContext`. Document as co-requirement in both plans (already done in Wave 2 inventory §2).
3. **19-6 + 30-8 → close review finding #1 fully** — 19-6 closes half (per-request app-role wiring), 30-8 closes the other half (per-tenant endpoint resolution). A closure note should appear when 30-8 merges, referring back to 19-6.
4. **28-9 → 27-5 + 29-5 + 18-5 + 18-8** — four UIs block on switch-org. 28-9 is the single most unblocking Wave-A story after the Epic 28 foundation.
5. **29-1 → 31-2** — the git-platform registry caches installation tokens + webhook secrets through `ISecretStore`. If 29-1 gets the interface wrong, 31-2 pays the rework.
6. **28-5 and 28-7 on API-key table location** — documented in Wave 2 inventory: 28-5 writes a tenant API key in the tenant DB; 28-7 adds a CP-side `platform_api_key_index` routing table. Two-phase write creates a potential orphan window; background-reconciliation follow-up tracked.
7. **28-11 vs 30-8 on LISTEN/NOTIFY channel** — both consume `TENANT.ROUTING.CHANGED`. Documented in 30-8 impl plan; no actual conflict.
8. **Epic 30 drivers each ship their own 29-6 rotation handler** — flow: Epic 29 cabinet first, then Epic 30 drivers each add a rotation handler when implemented.
9. **31-3 depends on Epic 28 `ctx.GetTenantIdOrThrow()` helper** — Wave 3 inventory §6 notes; if helper not yet plumbed, 31-3 adds it (+2h).
10. **31-9 depends on 29-3 `RevealModal` + 29-5 Component extraction** — if 29-5 hasn't extracted the modal into `packages/dashboard-ui/`, 31-9 duplicates it temporarily.

### 5.3 Biggest cross-epic tension

**Epic 28 Phase A/B is on the critical path of three otherwise-independent streams: Epic 9 completion, Epic 29 secrets, and Epic 12 context tools.** All three need 28-3 before they can ship real tenant routing. Team D (Epic 12) is unblocked earliest (only needs 28-3); Team A (Epic 9) needs 28-3 + 28-4 for real chain-resolver tenant scope; Epic 29 needs 28-3 + 28-4 + 29-1 concurrently.

If Epic 28 Phase A slips by a week, every downstream team idles or pivots to non-tenant-scoped sub-tasks. Mitigation: 28-1..28-3 must ship in Wave A week 1–2 with no distractions; 28-4 and 28-6 in parallel after that.

### 5.4 Dependency sanity check

Validated: no story in Wave B depends on a story in Wave C. Spot checks:

- Wave B 9-9 depends on 9-5 (Wave A) + 28-9 (Wave A). ✅
- Wave B 29-6 depends on 29-2 (Wave A). ✅
- Wave B 27-5 depends on 27-4 + 28-9 + 18-5 (all Wave A). ✅
- Wave C 12-7e depends on 12-7a/b/c/d (all Wave C). Bridge budget in Wave C. ✅
- Wave C 31-2 depends on 29-1 (Wave A), 28-9 (Wave A), 31-1 (Wave C). ✅
- Wave D 30-4 depends on 29-7 (Wave B), 30-1 (Wave C). ✅

No Wave-A security story depends on a Wave-B story. Spot checks:

- 19-6 has no Wave-B dependency. ✅
- 28-12 depends on 28-1 (same wave) + 28-4 (same wave). ✅
- 29-3 depends on 29-2 (same wave). ✅

**No cycles detected**. One near-inversion flagged: **16-5 RBAC** is scored 3.15 and would sit in Wave A, but effectively depends on the punch list's RBAC hierarchy wiring (already done). Keep it in Wave A as a gap-fill on the nginx + dashboard side only; if the gap is zero, drop it.

---

## 6. Open questions for product owner

These are **decisions that re-order waves materially**. Not answered here — surface only. Items are roughly ordered by blast radius (biggest reorder first).

1. **Is multi-tenant SaaS the launch product, or is single-tenant SaaS acceptable for v1?**
   - If multi-tenant-first (current assumption): Epic 28 Phase A/B blocks everything; 370h Wave A is correct.
   - If single-tenant-first: Epic 28 drops to Wave C/D; Wave A shrinks to ~180h (Epic 9 completion + prompt UIs + hardening). 12 stories flip two waves earlier. Demo-ready in ~3 weeks instead of ~6.

2. **Is Cranl the only Year-1 provisioning backend, or is Hetzner/Cloudflare near-term?**
   - If Cranl-only Year 1: Epic 30 stays in Waves C/D. Current assumption.
   - If Hetzner/Cloudflare near-term (say, a known enterprise lead): 30-1, 30-4 (Hetzner) jump into Wave B; 30-5 follows. External-API integration risk lands earlier.

3. **Is SOC 2 Type II audit a 2026-H2 commitment or a 2027 commitment?**
   - If 2026-H2: 29-9 (migrate stopgaps) + 29-10 (delete stopgaps) + 19-6 full closure + review P2 backlog ALL move into Wave A. No `changeme` password, no `TAMMA_SHARED_SECRET` in env. Adds ~40h to Wave A.
   - If 2027: current placement (Wave B for 29-9/29-10, Wave D for review P2 backlog) holds.

4. **Is enterprise SSO (Epic 33) expected within 12 months, or is it speculative?**
   - If 12 months: scope Epic 33 Lean tier (~100h) now so we don't re-architect the user model twice. Landing zone: Wave D or post-launch.
   - If speculative: Epic 33 stays a stub. Current assumption.

5. **Do we need GitLab support at launch, or can it wait for a paying GitLab customer?**
   - If launch: 31-6 moves into Wave C; 37h added, external-API risk lands in Wave C (when the GitLab container is still fresh in integration tests).
   - If wait-for-customer: current placement in Wave D holds.

6. **Is the CLI-only / self-hosted deploy mode a supported product, or a best-effort developer facility?**
   - If supported: 9-10 (CLI wiring) is elevated to Wave A; compatibility testing adds weight.
   - If best-effort: current placement in Wave B holds.

7. **Does the first customer deal gate on SCIM / directory sync?**
   - If yes: Epic 33 fires trigger #4; activates a Lean-tier scoping sweep in Wave B, which reshuffles Wave C.
   - If no: Epic 33 stays deferred.

8. **Is Elsa Studio access (admin UI for workflow debugging) part of the Wave A UI, or a developer tool?**
   - If Wave A UI: Story 16-6 (Elsa Studio auto-login) re-enters scope; adds ~8h. 16-5 RBAC on elsa.tamma.dev becomes mandatory.
   - If developer-only: current (not scored) holds.

9. **Do we ship per-tenant-KEK now or cluster-KEK now?**
   - Per-tenant-KEK (Doc 01 §8.2 full shape): 28-12 doubles to ~38h. Strong tenant-isolation story for SOC 2.
   - Cluster-KEK (current 28-12 shape): 19h; fine for launch; upgrade path to per-tenant KEK is additive.
   - Current assumption: cluster-KEK for launch, per-tenant-KEK in Wave D or with OpenBao adoption.

10. **Is the "first customer" target an internal dogfood tenant or an external paying account?**
    - Dogfood: Wave A can focus on correctness over polish; 18-4 onboarding UI polish can slip.
    - External paying: Wave A must include 18-4 in full polish + the 18-8 UI + 29-3 reveal-once UX. Currently assumed external-paying.

---

## 7. Pacing notes — wall-clock estimate at current team shape

### 7.1 Assumed team shape

Current model (from `layer-4-integration-ui.md` + `layer-4-with-epic-28.md`):

- **Team A** — backend / API (Epic 9 + Epic 28 auth plane + Epic 29 API surface)
- **Team B** — UI tracks (Prompt Store, Dashboard shells, secret UIs, tenant user mgmt UI)
- **Team C** — UI tracks partial (18-4 onboarding, 18-5 shell); often merges with Team B
- **Team D** — Epic 12 (prompt engineering + context tools) + cross-language bridge
- **Team E / shared coordinator** — Epic 28 ops stream (28-10, 28-11, 28-12), hardening, review P2 cleanup

Productive output assumed: 5 dev-hours/day/engineer, ≥ 4 parallel streams at peak.

### 7.2 Wave-by-wave wall-clock estimate

| Wave | Stories | Serial hours on critical path | Parallel streams | Wall-clock weeks |
|---|---|---:|---:|---:|
| A | 20 | ≈ 170 (19-6 → 28-1 → 28-2 → 28-3 → 28-4 → 28-5 → 28-8 → 28-9 → 18-5 → 18-8) | 4 | **~6 weeks** |
| B | 15 | ≈ 110 (9-5 → 9-9 → 9-10 → 9-12 + 29-2 → 29-3 → 29-4 → 29-9) | 4 | **~4 weeks** |
| C | 20 | ≈ 150 (12-7a → 12-7c → 12-7e + 31-1 → 31-2 → 31-3 → 31-7) | 4 | **~5 weeks** |
| D | 16 | ≈ 120 (30-4 → 30-8 → 30-10 + 31-6 + Layer 5 harness + security audit) | 3 | **~4 weeks** |

**Grand total**: ~19 weeks wall-clock at 4 parallel streams peak. Calendar-wise, with buffer and integration, target is **~22 weeks (5 months)** to Layer 5 completion from the start of Wave A.

### 7.3 What compresses or expands the schedule

- **5th stream (Team E on ops)** — cuts Wave A by ~1 week (28-12 + 28-11 in parallel with auth stream).
- **3 streams instead of 4** — Wave A extends to ~8 weeks; Wave B to ~5 weeks. Total ~25 weeks.
- **Dropping single-tenant launch (see Open Question 1)** — Wave A shrinks to ~3 weeks (Epic 9 completion only); total drops to ~11 weeks to Layer 5 complete, at the cost of deferred multi-tenant.
- **Hetzner/Cloudflare early (Open Question 2)** — adds ~3 weeks to Wave B; external-API integration risk lands earlier (good for launch confidence).
- **Full SOC 2 Type II prep now (Open Question 3)** — adds ~4 weeks to Wave A. Worth it only if auditor is scheduled.

### 7.4 Week-0 checklist

Before Wave A kicks off:

- [ ] Merge PR #328 (`feat/auth-foundation`) to `main`. Confirmed green on 2026-04-18 per punch-list completion.
- [ ] Confirm Wave 2 + Wave 3 impl plans are current and merged. ✅ (per `wave-2-impl-plan-inventory.md` + `wave-3-impl-plan-inventory.md`).
- [ ] Product-owner sign-off on this doc's waves. **Pending** (see §6 Open Questions).
- [ ] Confirm Epic 33 deferral. ✅ (per `epic-33/README.md`).
- [ ] Confirm 28-13 deferral. ✅ (per `project_epic28_kek_decision.md`).
- [ ] Confirm 31-11 + 31-12 deferral. ✅ (per `epic-31-33-placement.md`).
- [ ] Team A worktree prepped for Epic 28 Phase A (branch `feat/epic-28-foundation`).
- [ ] Team D worktree prepped for 12-7 track (branch `feat/epic-12-context-tools`).

---

## 8. Appendix — top-line counts

- Stories scored: **64 active** + 4 deferred = 68 total in scope.
- Wave breakdown: **A=20 · B=15 · C=20 · D=16 · Deferred=4** (plus Layer 5 validation activities which are coordinator-owned, not stories).
- Top 5 highest-scored stories:
  1. 19-6 wire app-role DbContext (4.20)
  2. 28-1 EF migration scripts (3.95)
  3. 28-4 Tenant connection resolver (3.95)
  4. 28-8 TenantContextMiddleware (3.95)
  5. 28-9 JWT + switch-org (3.90)
- Biggest cross-epic dependency tension: **Epic 28 Phase A/B blocks Epic 9 completion, Epic 29 secrets, and Epic 12 context tools simultaneously**. See §5.3.

## 9. Change log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-21 | 1.0 | Initial prioritization doc — covers every Layer 4 / Layer 5 story across Epics 9, 12, 16, 17, 18, 19, 27, 28, 29, 30, 31, 33. | Prioritization pass |
