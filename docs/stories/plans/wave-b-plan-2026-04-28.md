# Wave B Plan — 2026-04-29 (revised)

**Status**: active (rev 2)
**Branch**: `feat/wave-b`
**Base**: `main` at `5321316` (post Wave-A merge of PR #329)
**Predecessor**: [`layer-4-5-prioritization-2026-04-21.md`](./layer-4-5-prioritization-2026-04-21.md) — superseded.

## Revision note (2026-04-29)

The original wave-b plan (rev 1) listed Stories 19-6, 29-1, 18-5 as "must-ship" — but a git-log audit on 2026-04-29 revealed all three already shipped during Wave A (including a Wave A.5 follow-up I'd missed that *deleted* TammaAppDbContext entirely and replaced it with `ControlPlaneDbContext + ITenantDbContextFactory`). Plan re-anchored against actual repo state, not stale plan-file `Status:` markers.

## What's actually in `main` post Wave-A

- **Database-per-tenant foundation** — Stories 28-1 through 28-13 (entity move + migrations + LRU pool + JWT switch-org + KEK rotation + everything in between)
- **Wave A.5 architectural pivot** — `TammaDbContext` + `TammaAppDbContext` deleted; 20 repos routed through `ControlPlaneDbContext + ITenantDbContextFactory`. Closes review findings `orgs/002`, `orgs/004`, `admin-db/020`, `admin-db/021` via different mechanism than Story 19-6 originally specified.
- **Epic 29 secret management** — Stories 29-1 through 29-10 all shipped (interface + Postgres-backed envelope-encrypted store + reveal-once UX + admin/tenant UIs + rotation primitives + Cranl/postgres credential rotation + stopgap migration + delete).
- **Epic 18 dashboard + tenant admin** — Stories 18-3, 18-4, 18-5, 18-7, 18-8 all shipped.
- **Epic 17 tenant-scoped event store** — 17-3, 17-4 shipped.
- **Epic 27 prompt store (single-user mode)** — 27-1, 27-4, 27-5, 27-6, 27-7 shipped. **27-2 and 27-3 not yet shipped** (see below).
- **28 deferred-major dep batches** — chromadb, openai, pino, eslint, ts-eslint, zod, vite, react, typescript all bumped + chromadb server bump 0.6.3→1.5.8 + healthcheck migration + self-healing volume-reset workflow with backup + reviewer-approval gate.

## What's genuinely pending (verified by 0 substantive commits in `main`)

| Epic | Theme | Pending | Status |
|---|---|---|---|
| **27** | Prompt Store — SaaS-mode resolution | 27-2 (service tenant-scope), 27-3 (API endpoint admin path) | ❌ not shipped |
| **30** | Pluggable Tenant Infrastructure | 30-1, 30-2, 30-3, 30-4, 30-5, 30-6, 30-7, 30-8, 30-9, 30-10 | ❌ all 10 not shipped |
| **31** | Git Platform Expansion | 31-1, 31-2, 31-4, 31-5, 31-6, 31-7, 31-8, 31-9, 31-10 (31-3 already 2 commits, 31-11/12 explicitly deferred) | ❌ 9 not shipped |
| **19** | Per-request repos (post-Wave-A.5) | 19-4 (single small story) | ❌ not shipped |
| **1.5** | Foundation (mixed) | ~22 of 53 — needs deeper audit (some stale, some real, some superseded) | ⚠️ mixed |

## Why 27-2 + 27-3 reopen

The shipped C# `PromptStoreService` resolves prompts keyed on `userId`, not `tenantId`. That works for **single-user mode** (CLI / standalone — sole user owns their overrides). It does NOT work for **SaaS mode** where the tenant_admin should set team-shared prompts and member users consume them without edit access.

CLAUDE.md was updated 2026-04-29 (commit on this branch) to make the mode split explicit. Stories 27-2 and 27-3 implement the SaaS-mode resolution path:

- **27-2 (service)**: add tenant-scoped resolution in `ResolveRoleActionAsync(Guid? tenantId, ...)`, parallel to the existing `ResolveRoleActionAsync(Guid? userId, ...)`. Mode detection at startup picks one resolution function.
- **27-3 (API)**: gate PUT/DELETE on `SettingsManage` (`settings:manage` permission) in SaaS mode; reject member-role users with 403. The dashboard already consumes `/api/prompts/:role/:action` — the endpoint shape stays the same; only the override key changes.

Both remain valid stories per the original plans. The plan files don't need updating; the git-log status was wrong.

## Wave B — proposed scope

Three coherent chunks. User picks scope at dispatch time.

### Option α — small, focused (~30-40h)

Quick wins that close the SaaS-mode gap and tidy 19-4.

| # | Story | Hours | Why |
|---|---|---|---|
| 1 | **27-2** Prompt Store SaaS-mode resolution | ~10h | Closes the mode-confusion gap. Foundation for 27-3. |
| 2 | **27-3** Prompt Store API tenant-admin path | ~6h | Wires 27-2 to the dashboard. Tests the RBAC gate. |
| 3 | **19-4** Per-request repos follow-up | ~16h | Last loose thread from Epic 19. |
| 4 | **CLAUDE.md SaaS-mode audit follow-ups** | ~4h | Spot-check Agent/Sanitization/Budget endpoints for mode-aware behavior surfaced by the audit. |

### Option β — Epic 30 foundation (~60-80h)

Pluggable provisioning. Front-loads the `ITenantInfrastructureProvider` v2 that gates every other Epic 30 story.

| # | Story | Hours |
|---|---|---|
| 1 | **30-1** ITenantInfrastructureProvider v2 | ~16h |
| 2 | **30-2** Resumable Per-Backend Provisioning Workflow | ~18h |
| 3 | **30-3** Cranl Provider Refactor to v2 | ~14h |
| 4 | **30-8** Per-Tenant Routing Resolver | ~16h |

Defers 30-4/5/6 (provider impls — Hetzner Cloud, Cloudflare, BYO) + 30-7/9/10 (UI/ops) to later waves.

### Option γ — Epic 31 git platforms (~80-120h)

Git platform expansion. Foundation (31-1) gates the rest.

| # | Story | Hours |
|---|---|---|
| 1 | **31-1** Git Platform Abstraction + Capability Matrix | ~16h |
| 2 | **31-2** Platform Registry + Per-Tenant Routing Resolver | ~14h |
| 3-5 | **31-4/5/6** Gitea / Forgejo / GitLab Drivers | ~16h × 3 |
| 6-9 | **31-7/8/9/10** Webhook / CI Secrets / UI / Test Harness | ~10h × 4 |

### Option δ — Big bang (α + β + γ — ~170-240h)

Everything. Expensive but coherent: SaaS-mode prompts done first (a real product gap), then the foundational architectural work in Epic 30 + Epic 31. Treats Wave B as "every genuinely-pending epic-30+ story" instead of a focused chunk.

## Sequencing

Within any option:

```
27-2 → 27-3        (sequential: 27-3 needs 27-2's resolution model)
30-1 → 30-2/3/8    (30-1 unblocks the other Epic 30 work)
31-1 → 31-2 → 31-4/5/6      (31-1 then 31-2; drivers parallelize after)
                     ↘ 31-7/8/9/10 (parallelize once 31-2 lands)
```

α is fully sequential; β and γ have parallel branches after the foundation. δ runs all three concurrently — α + the β/γ foundation stories first, then β/γ branches in parallel.

## Parallel execution plan (Option δ — selected 2026-04-29)

Four batches across three concurrency layers. Each batch's agents run in their own worktree on a story-named branch (e.g. `story-27-2-saas-prompt-resolution`); branches merge into `feat/wave-b` directly via local merge + push (no PRs per the Wave-A pattern). Total work ~170-240h dispatched across ~14 agents with a critical-path of ~3 batches deep.

### Batch 1 — foundations + small wins (4 parallel agents)

Independent across all three options. No shared file domains.

| Agent | Story | Worktree branch | File domain | ~Hours |
|---|---|---|---|---|
| **B1.1** | 27-2 — Prompt Store SaaS-mode resolution | `story-27-2-saas-prompt-resolution` | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/` (C#) | ~10h |
| **B1.2** | 19-4 — Per-request repos follow-up | `story-19-4-per-request-followup` | `apps/tamma-elsa/src/Tamma.Data/Repositories/` (C#) | ~16h |
| **B1.3** | 30-1 — `ITenantInfrastructureProvider` v2 | `story-30-1-provisioner-interface-v2` | new package: `apps/tamma-elsa/src/Tamma.Provisioning/` (C#) | ~16h |
| **B1.4** | 31-1 — Git Platform Abstraction + Capability Matrix | `story-31-1-git-platform-abstraction` | new package: `apps/tamma-elsa/src/Tamma.GitPlatform/` (C#) | ~16h |

All 4 are C# but in different packages — no file overlap. Lockfile (`pnpm-lock.yaml`) untouched. Merge order: B1.2 → B1.1 → B1.3 → B1.4 (size ascending; arbitrary since no conflicts).

### Batch 2 — second-tier dependencies (5 parallel agents)

Dispatched after Batch 1 lands. Three of these depend on Batch 1; two are independent.

| Agent | Story | Depends on | File domain | ~Hours |
|---|---|---|---|---|
| **B2.1** | 27-3 — Prompt Store API tenant-admin path | B1.1 (27-2) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs` + RBAC plumbing | ~6h |
| **B2.2** | 30-2 — Resumable Per-Backend Provisioning Workflow | B1.3 (30-1) | `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/` (Elsa workflow) | ~18h |
| **B2.3** | 30-3 — Cranl Provider Refactor to v2 | B1.3 (30-1) | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs` | ~14h |
| **B2.4** | 30-8 — Per-Tenant Routing Resolver | B1.3 (30-1) | `apps/tamma-elsa/src/Tamma.Data/Pooling/` (extends LRU pool) | ~16h |
| **B2.5** | 31-2 — Platform Registry + Per-Tenant Routing Resolver | B1.4 (31-1) | new package: `apps/tamma-elsa/src/Tamma.GitPlatform.Registry/` | ~14h |

5 parallel C# agents in different files. No shared edit surface.

### Batch 3 — Epic 31 drivers (3 parallel agents — depends on B2.5)

Dispatched after Batch 2's 31-2 lands.

| Agent | Story | File domain | ~Hours |
|---|---|---|---|
| **B3.1** | 31-4 — Gitea Driver | new: `apps/tamma-elsa/src/Tamma.GitPlatform.Gitea/` | ~16h |
| **B3.2** | 31-5 — Forgejo Compat Shim + Test-Matrix Extension | extends 31-4 + adds shim layer | ~14h |
| **B3.3** | 31-6 — GitLab Driver | new: `apps/tamma-elsa/src/Tamma.GitPlatform.GitLab/` | ~16h |

3 parallel agents, each adds a new driver subpackage.

### Batch 4 — Epic 31 cross-cutting (4 parallel agents — depends on B2.5 + B3)

Some can dispatch in parallel with Batch 3 (only depend on 31-2, not the drivers).

| Agent | Story | Depends on | File domain | ~Hours |
|---|---|---|---|---|
| **B4.1** | 31-7 — Webhook Receiver Abstraction | B2.5 (31-2) | new: `apps/tamma-elsa/src/Tamma.GitPlatform.Webhooks/` — can run in parallel with B3 | ~10h |
| **B4.2** | 31-8 — CI Secrets Provisioner Abstraction | B2.5 (31-2) + Epic 29 (already shipped) | extends `IPlatformGit` interface — can run in parallel with B3 | ~10h |
| **B4.3** | 31-9 — Onboarding Platform Picker UI | B3.* (drivers exist for picker to list) | `packages/dashboard-user/src/pages/onboarding/` (TypeScript/React) | ~10h |
| **B4.4** | 31-10 — Integration Test Harness | B3.* (drivers exist to test) | `apps/tamma-elsa/tests/Tamma.GitPlatform.IntegrationTests/` + CI workflow | ~10h |

B4.1 + B4.2 can dispatch concurrently with Batch 3 (file-domain-isolated). B4.3 + B4.4 follow Batch 3.

### Layer summary

```
                          time →
Layer 1 (parallel):  [B1.1] [B1.2] [B1.3] [B1.4]
                       ↓     ↓     ↓     ↓
Layer 2 (parallel):  [B2.1] [B2.2] [B2.3] [B2.4] [B2.5] ← + B4.1 B4.2 (parallel-startable)
                                                  ↓
Layer 3 (parallel):                              [B3.1] [B3.2] [B3.3]
                                                  ↓
Layer 4 (parallel):                              [B4.3] [B4.4]
```

Critical path: B1.4 → B2.5 → B3.1 (or any driver) → B4.3 (or B4.4) — 4 stages × ~16h each ≈ 64h calendar at single-agent pace, but parallelism collapses it to whichever stage takes longest. Realistic Wave B duration: 1-2 weeks calendar.

### Conflict surface

- All Batch 1-3 work is C# in non-overlapping packages — pnpm lockfile only churns if a Batch adds a TS dep (B4.3 will). Predictable lockfile-reconciliation pattern from Wave A's deferred-majors merges applies.
- Single-PR contract: `feat/wave-b → main` is the ONE integration PR. Per-story branches are scratchpads, deleted after merge.

### Failure modes

| Failure | Recovery |
|---|---|
| B1 foundation surfaces a design gap (e.g. 30-1 interface needs revision after 30-2 starts) | Pause Layer 2 dependents, revise B1's contract, re-dispatch. Layer-2 agents that depend on the bad interface burn ~1-2h before catching the gap. |
| Two agents conflict on a shared utility file | Manual merge resolution (Wave A pattern). 5-10 min per conflict. |
| Critical-path agent fails (kill or timeout) | Re-dispatch with the original brief; the failed agent's worktree is the diagnostic, not data loss. |
| Story plan turns out to be obsolete (like the Wave-A plan-vs-reality mismatch) | Stop the agent, audit current state, revise the plan. Cheaper if caught in Batch 1 than Batch 4. |

## Acceptance — when is Wave B "done"?

Wave B closes when ALL of:

1. The chosen option's named stories merged to `feat/wave-b`
2. CI all green (matches Wave A's 23/23 baseline)
3. `feat/wave-b → main` PR opened — single integration PR per the wave-A pattern, ready for merge

Stretch: spot-check audit findings from CLAUDE.md's new "Operating Modes" rule. Any remaining SaaS-mode regressions surface in PR review.

## Working notes

- **Branch**: `feat/wave-b` (created 2026-04-28; revised plan 2026-04-29)
- **Base**: `main` at commit `5321316`
- **CI**: ci.yml + codeql.yml triggers don't include `feat/wave-b` directly; PR-into-main triggers them via the `pull_request: branches: [main]` rule. No CI trigger update needed (per user 2026-04-29).
- **PR ceremony**: per-story branches merge into `feat/wave-b` directly; only ONE PR exists at end (`feat/wave-b → main`). Wave-A's "no extra PRs" pattern preserved.
- **Backups (Story 1.5-7)**: still deferred per user 2026-04-29.
- **`production-destructive` GitHub environment**: created by user; reviewer-approval gate is configured for the volume-reset workflow.
