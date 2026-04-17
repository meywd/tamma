# Layer 4 + Epic 28 — consolidated execution plan

**Status**: Active
**Last updated**: 2026-04-17
**Scope**: combines the revised Layer 4 ([`layer-4-integration-ui.md`](./layer-4-integration-ui.md))
with Epic 28 ([`../epic-28/README.md`](../epic-28/README.md)) database-per-tenant
refactor. Epic 28 foundation is now a **prerequisite** for Layer 4 teams
that touch any tenant-scoped code path.

## Why combine

Layer 4 as written assumes the existing shared-DB + `TenantId` + EF
query-filter model. Epic 28 replaces that model mid-flight. Shipping them
independently means every Layer 4 team touches a moving target. This plan
inserts Epic 28 phases as gates before Layer 4 work that depends on them,
and parallelises what can run concurrently.

## Sequencing overview

```
Phase 0  (prereq: PR #328 merged to main)
  │
  ▼
Phase A — Epic 28 Foundation (SERIAL, 60h)
  28-1 EF migrations
  28-2 ControlPlaneDbContext split
  28-3 TenantDbContext factory
  │
  ▼
Phase B — Epic 28 Data Plane + Provisioning (~73h, some parallel)
  ┌─ 28-4 Connection resolver + pool          (22h)
  ├─ 28-6 platform_events/queue/outbox tables (18h)
  └─ (after 28-4 + 28-6) 28-5 Create/Delete workflows (45h, XL)
  │
  ▼
Phase C — Epic 28 Auth (SERIAL stream on top of B, 50h)
  28-7 API-key prefix routing
  28-8 TenantContextMiddleware async handling
  28-9 JWT + /auth/switch-org
  │
  ▼
Phase D — Layer 4 teams fan out (existing plan, 97h+34h+64h+156h)
  ┌─ Team A  Epic 9 completion          — blocked on 28-3, 28-4
  ├─ Team B  Prompt Store UIs           — blocked on 28-9 (needs switch-org)
  ├─ Team C  Epic 18 UI completion      — blocked on 28-5, 28-8, 28-9
  └─ Team D  Epic 12 context tools      — blocked on 28-3 only
  │
  ║ (parallel with Phase D, independent streams)
  ║
  ├─ Epic 28 Ops Stream — 28-10 analytics, 28-11 admin UX, 28-12 KEK
  └─ Hardening punch list (60h — see layer-2-3-status-post-epic-19.md)
```

## Phase A — Epic 28 Foundation (60h, serial)

**Stories**: 28-1 → 28-2 → 28-3

**Why serial**: 28-2 reads the migration set created by 28-1; 28-3 reuses
the entity split introduced by 28-2. No parallelism possible.

**Deploy gate**: four migration sets run clean on fresh Postgres 17;
existing API tests still green (no behaviour change yet — the stub
resolver from 28-3 routes everything to a configured default tenant).

**Blocks**: every subsequent phase, and all Layer 4 teams except Team D
which only needs the tenant DbContext contract (available after 28-3).

## Phase B — Epic 28 Data Plane + Provisioning (73h, partial parallel)

**Stories**: 28-4, 28-6 (parallel) → 28-5

**Parallel group**:
- 28-4 Connection resolver + LRU pool cache (22h) — replaces 28-3's stub
- 28-6 Platform events / queued tasks / email outbox tables (18h)

**Blocker**: 28-5 (`CreateTenantWorkflow` + `DeleteTenantWorkflow`, 45h)
needs both — resolver for tenant DB access + `platform_events` for
workflow audit.

**Deploy gate**: happy-path tenant create from email-verify click to
`Status=active` in <60s; `DropDatabase` compensation proven by
fault-injection test; `platform_queued_tasks` worker drains an enqueued
test task end-to-end.

**Blocks**: all Layer 4 teams that need real per-tenant routing. Unblocks
Team D (Epic 12 tools against tenant DBs).

## Phase C — Epic 28 Auth (50h, serial)

**Stories**: 28-7 → 28-8 → 28-9

- 28-7 API-key prefix routing (14h) — changes `ApiKeyAuthHandler`
- 28-8 TenantContextMiddleware async handling (12h) — 503/410/424/404
  responses during tenant provisioning/deletion
- 28-9 JWT tenantId claim + `/auth/switch-org` (24h) — multi-tenant user
  sessions

**Why serial**: 28-8 depends on 28-7's handler routing shape; 28-9
depends on 28-8 for middleware semantics.

**Deploy gate**: user registers → verifies email → polls status until
active → logs in → switches org → hits tenant-scoped endpoint → 200. All
paths auditable via `AUTH.*` events in `platform_events`.

**Blocks**: Team B (Prompt Store UIs — tenant UI needs switch-org),
Team C (Epic 18 dashboard — needs the full auth flow).

## Phase D — Layer 4 teams (existing 4-team plan, fan out)

Now the existing Layer 4 plan runs as originally scoped in
[`layer-4-integration-ui.md`](./layer-4-integration-ui.md), but with the
following added prerequisites per team:

| Team | Epic 28 prereq | Notes |
|------|----------------|-------|
| A — Epic 9 completion (97h) | 28-3, 28-4 | Provider chain resolver writes to tenant DB via factory |
| B — Prompt Store UIs (34h) | 28-9 | Tenant UI uses switch-org to preview as tenant |
| C — Epic 18 UI completion (64h) | 28-5, 28-8, 28-9 | Register flow + dashboard both rely on full tenant lifecycle |
| D — Epic 12 context tools (156h) | 28-3 only | Lowest dependency — can start right after Phase A |

**Wall-clock Phase D** unchanged from the original Layer 4 plan: ~156h
(Team D critical path).

### Parallel with Phase D — Epic 28 Ops Stream (70h)

Runs concurrently with Phase D teams, independent of them:

- 28-10 `platform_analytics_hourly` rollup (28h)
- 28-11 Admin UX for tenants.Status state machine (22h)
- 28-12 Roles + KEK rotation (20h)

Assign as a 5th team (Team E — Ops) if agent capacity allows.

### Parallel with Phase D — Hardening punch list (60h)

Not Epic 28 work but same era: see
[`layer-2-3-status-post-epic-19.md`](./layer-2-3-status-post-epic-19.md)
punch list for the ~60h of C# business-logic depth that is still shallow
after the Wave 1/Wave 2 hardening. Team B or a shared engineer can pick
these off between Layer 4 UI milestones.

## Totals

| Phase | Serial hours | Wall clock with parallelism |
|-------|-------------:|---------------------------:|
| A — Foundation | 60 | 60 |
| B — Data plane + provisioning | 73 | 45 (28-4 and 28-6 parallel, then 28-5) |
| C — Auth | 50 | 50 |
| D — Layer 4 teams + Ops stream + hardening | 97+34+64+156+70+60 = 481 | ~156 (Team D critical path) |
| **Total** | **664** | **~311** |

At 5 productive dev hours/day and 5 parallel streams peak, **~6 weeks
wall-clock**.

## Parallel-agent-safe groups

- **After Phase A merges**: 28-4, 28-6, and Team D (Epic 12) can all
  start.
- **After Phase B merges**: 28-7 (auth stream) + Team A (Epic 9) + Ops
  stream can run in parallel.
- **After Phase C merges**: all four Layer 4 teams are unblocked.

Never parallel:
- 28-1 → 28-2 → 28-3 (Phase A internal order)
- 28-7 → 28-8 → 28-9 (Phase C internal order)
- 28-4, 28-6 before 28-5 (workflow consumes both)

## Deploy gates (summary)

Each phase gates the next via a concrete pass/fail check:

- **Phase A**: four migration sets clean on fresh Postgres 17; stub
  resolver integration test green.
- **Phase B**: tenant create p95 < 60s (1 concurrent); DROP DATABASE
  compensation survives fault injection.
- **Phase C**: full register → verify → login → switch-org → tenant
  query path audited in `platform_events` and `AUTH.*` event stream.
- **Phase D**: Layer 4 acceptance criteria per team; ops stream's
  analytics rollup accurate within 1%; hardening punch list items each
  have passing integration tests.

## Rollback strategy per phase

- **Phase A**: pure additive migrations; revert commits if needed.
- **Phase B**: 28-5 workflow registered behind a feature flag
  (`Tenants:AsyncProvisioning=true`). Off = registration falls back to
  synchronous provisioning (Epic 19 era behaviour).
- **Phase C**: 28-9 switch-org gated by `Auth:MultiTenantSessions=true`.
  Off = single-tenant login only.
- **Phase D**: team-scoped rollbacks; each team's work is already
  isolated to its worktree/branch.

## Sources

- Epic 28 overview: [`../epic-28/README.md`](../epic-28/README.md)
- Epic 28 sequencing (phases within the epic):
  [`db-per-tenant/00-sequencing.md`](./db-per-tenant/00-sequencing.md)
- Layer 4 original: [`layer-4-integration-ui.md`](./layer-4-integration-ui.md)
- Layer 2/3 status + hardening punch list:
  [`layer-2-3-status-post-epic-19.md`](./layer-2-3-status-post-epic-19.md)
- Epic 28 story files: `docs/stories/epic-28/story-28-N/28-N-*.md`
  (stories 28-1 through 28-6 committed; 28-7 through 28-12 pending
  authoring — see task tracker)
