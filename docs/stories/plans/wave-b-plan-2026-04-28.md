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

**Continuous flow**: each story dispatches the moment its dependencies clear. No discrete batch boundaries — when an agent lands a foundation, every story that was waiting on it dispatches immediately. The orchestrator (this session) tracks per-story unblock state and fans out as agents complete.

Each agent runs in its own worktree on a story-named branch (`story-NN-N-...`); branches merge into `feat/wave-b` directly via local merge + push. Single-PR contract preserved: `feat/wave-b → main` (PR #343) is the only PR.

### Dispatch graph

Stories grouped by their dependency tier (NOT execution batch — they fan out as their predecessors land):

```
Tier 0 — no deps, dispatch immediately:
  27-2 (Prompt SaaS resolution)            apps/.../PromptStore (C#)
  19-4 (Per-request repos follow-up)       apps/.../Repositories (C#)
  30-1 (ITenantInfrastructureProvider v2)  new pkg Tamma.Provisioning (C#)
  31-1 (Git Platform Abstraction)          new pkg Tamma.GitPlatform (C#)

Tier 1 — fan out as Tier 0 lands:
  27-2 lands → dispatch 27-3 (API + RBAC)
  30-1 lands → dispatch 30-2, 30-3, 30-8 in parallel
  31-1 lands → dispatch 31-2

Tier 2 — fan out as Tier 1 lands:
  31-2 lands → dispatch 31-4, 31-5, 31-6, 31-7, 31-8 in parallel
                (drivers + webhook + CI-secrets all only need 31-2)

Tier 3 — fan out as Tier 2 lands:
  any of 31-4/5/6 lands → dispatch 31-9 (picker UI can list whatever
                          drivers exist) and 31-10 (test harness can
                          test whatever drivers exist)
```

### Per-story file domains (conflict surface check)

| Story | File domain | Conflict risk |
|---|---|---|
| 27-2 | `apps/.../Tamma.Api/Services/PromptStore/` | none |
| 27-3 | `apps/.../Tamma.Api/Endpoints/PromptEndpoints.cs` + RBAC plumbing | minor (one file edited by 27-2 too — 27-3 starts AFTER 27-2 lands so sequenced) |
| 19-4 | `apps/.../Tamma.Data/Repositories/` | none (different repos than 30/31 work) |
| 30-1 | new `Tamma.Provisioning/` package | none (greenfield) |
| 30-2 | `apps/.../Tamma.Activities/TenantLifecycle/` | none |
| 30-3 | `apps/.../CranlTenantProvisioner.cs` | none |
| 30-8 | `apps/.../Tamma.Data/Pooling/` (extends LRU pool) | minor (touches existing LRU pool — overlap with 19-4 if 19-4 expands repository plumbing) — sequenced via 30-1 dep |
| 31-1 | new `Tamma.GitPlatform/` package | none |
| 31-2 | new `Tamma.GitPlatform.Registry/` | none |
| 31-4/5/6 | new `Tamma.GitPlatform.{Gitea,Forgejo,GitLab}/` | none (separate driver packages) |
| 31-7 | new `Tamma.GitPlatform.Webhooks/` | none |
| 31-8 | extends `IPlatformGit` interface (in 31-1's package) | minor — overlaps with 31-1's interface definition; sequenced via 31-1 → 31-2 → 31-8 |
| 31-9 | `packages/dashboard-user/src/pages/onboarding/` (TS) | none, but **only TS-side work in the wave** — pnpm-lock churn |
| 31-10 | new `tests/Tamma.GitPlatform.IntegrationTests/` + CI workflow | none |

Lockfile (pnpm-lock.yaml) only churns on 31-9 (TS dashboard work). Wave-A's lockfile-reconciliation pattern handles it if needed.

### Orchestrator responsibilities (this session)

After each agent completion notification:
1. Verify the agent's branch pushed cleanly (`gh run view` if CI fired, else `git log origin/<branch>`)
2. Merge the branch into `feat/wave-b` locally with `git merge --no-ff`; resolve conflicts via Wave-A's `git checkout --theirs pnpm-lock.yaml && pnpm install --no-frozen-lockfile` recipe (if lockfile)
3. Push `feat/wave-b`
4. Identify any newly-unblocked stories from the dispatch graph above
5. Dispatch those agents IMMEDIATELY — do not wait for other in-flight agents

### Failure modes

| Failure | Recovery |
|---|---|
| Tier 0 foundation surfaces a design gap (e.g. 30-1 interface needs revision after 30-2 starts) | Pause Tier 1 dependents on that foundation, revise the foundation's contract, re-dispatch. Tier-1 agents already in flight burn ~1-2h before catching the gap. |
| Two agents conflict on a shared utility file | Manual merge resolution (Wave A pattern). 5-10 min per conflict. |
| Agent fails (kill or timeout) | Re-dispatch with the original brief; the failed agent's worktree is the diagnostic, not data loss. |
| Story plan obsolete (like Wave-A's plan-vs-reality mismatch) | Stop the agent, git-log audit the current state, revise the plan, re-dispatch. Cheaper if caught at Tier 0 than Tier 3. |

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
