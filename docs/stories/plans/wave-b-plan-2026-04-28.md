# Wave B Plan — 2026-04-28

**Status**: active
**Branch**: `feat/wave-b`
**Base**: `main` at `5321316` (post Wave-A merge of PR #329)
**Predecessor**: [`layer-4-5-prioritization-2026-04-21.md`](./layer-4-5-prioritization-2026-04-21.md) — superseded for items shipped in Wave A.

## Wave-A summary (now in `main`)

PR #329 squashed 100 commits onto main, landing:

- **Database-per-tenant foundation** — Story 28-1 (entity move + 2 EF migrations) + 28-2/3/4/5/6/7/9/12 (CP split, tenant factory, LRU pool, platform_events, JWT switch-org, etc.)
- **Wave-4 deferred backlog** — 6 PRs (#335–#340) closing nested-lockfile alerts, vitest 4 migration, H6 flake doc, Story 28-1 PR A/B/C
- **28 deferred-major dep batches** — chromadb 1→3, openai 4→6, pino 9→10, eslint 9→10, ts-eslint 8.18→8.59, zod 3→4, vite 6→8, react 18→19 (codemod across 132 files), typescript 5.7→6.0, plus chromadb server bump 0.6.3→1.5.8
- **Infrastructure** — chromadb healthcheck migration (curl → bash /dev/tcp), self-healing volume-reset workflow with backup + reviewer-approval gate, CI trigger expansion to integration-branch PRs, force-resolve transitive CVEs

## Wave B — top 3 must-ship

These are the three highest-priority items that survived Wave A and are now unblocked.

### #1 — Story 19-6: Wire per-request repos onto `TammaAppDbContext` (P0)

**Plan**: [`epic-19/story-19-6-wire-app-role-context-impl-plan.md`](../epic-19/story-19-6-wire-app-role-context-impl-plan.md)
**Effort**: 16.5h
**Why first**: closes the only remaining P0 review finding from 2026-04-20. Phase-3 RLS shipped as scaffold-only — `tamma_app` role + tenant policies exist but no request paths use the app-role connection. Until 19-6 lands, RLS is dead code and every tenant story inherits the same false-remediation.

**Scope**:

- Swap `TammaDbContext` → `TammaAppDbContext` across 21 repositories + 5 endpoint handlers
- Fail-closed regression test: insert NULL-tenant row as superuser, prove the app-role connection doesn't return it
- Runbook: rotate `tamma_app` password, flip `TammaAppDb` connection string

**Closes**: review findings `orgs/002`, `orgs/004`, `admin-db/020`, `admin-db/021`.
**Out of scope**: migration + background-service paths intentionally stay on the superuser connection.

### #2 — Story 29-1: Secret Store Abstraction

**Plan**: [`epic-29/29-1-secret-store-abstraction-impl-plan.md`](../epic-29/29-1-secret-store-abstraction-impl-plan.md)
**Effort**: 16h
**Why second**: gates the entire Epic 29 (29-2 through 29-10). Writing this interface wrong costs ~40h of rework downstream. Foundation work — interfaces, records, validators, xUnit mocks — no real data movement.

**Ships**: `ISecretStore`, metadata/version record types, `ISecretStoreBackend` driver port, `ISecretAccessAuditor` event port.

**Unblocks**: 29-2 (Postgres-backed envelope), 29-3 (reveal-once), 29-4 (platform-admin UI), 29-5 (tenant-admin UI), 29-6 (rotation primitive), 29-7 (DB credential rotation), 29-8 (Cranl env rotation), 29-9 (migrate stopgap secrets), 29-10 (delete stopgaps), plus 31-2's credential store.

### #3 — Story 18-8: Tenant-Admin User Management UI

**Plan**: [`epic-18/18-8-tenant-admin-user-mgmt-ui-impl-plan.md`](../epic-18/18-8-tenant-admin-user-mgmt-ui-impl-plan.md)
**Effort**: 32h
**Why third**: largest first-customer-visible gap. Backend is 90% done; 18-7 ships 14h of thin completions, then 18-8 ships the full UI surface.

**Hard blockers** (dependency chain):

1. **Story 18-5** (dashboard-user shell + sidebar + settings layout) — Status: Planned. Must land before 18-8.
2. **Story 18-7** (tenant-admin user mgmt API completion) — Status: Planned. Adds resend-invite, tenant audit, role-change-event handlers.

So Wave B's #3 is actually a 3-story chain: **18-5 → 18-7 → 18-8**.

**Ships in 18-8**: members table, invite drawer, change-role dialog, remove-member confirm, pending-invites list (resend + revoke), transfer-ownership flow, tenant-scoped audit log. Gated by `tenant_owner` / `tenant_admin` RBAC.

## Wave B — second tier (post-must-ships)

Order by dependency unlock:

| # | Story | Epic | Hours | Unblocks |
|---|---|---|---:|---|
| 4 | **9-5** Provider chain API (C#) | 9 | 14 | Team A chain (9-9 → 9-12) |
| 5 | **27-4** | 27 | TBD | needs status re-audit |
| 6 | **27-5** | 27 | TBD | post-18-5/18-8 dashboard wire-up |
| 7 | **18-4** GitHub App installation onboarding | 18 | TBD | tenant onboarding flow |
| 8 | **29-2** Postgres-backed envelope-encrypted secret store | 29 | — | depends on 29-1 |
| 9 | **29-3** Reveal-once-on-create UX | 29 | — | depends on 29-1 + 29-2 |
| 10 | **17-3** Postgres-backed event store (in-progress) | 17 | — | event audit completeness |

## Out of scope for Wave B (deferred to Wave C+)

- **Epic 30** (pluggable provisioning — Hetzner Cloud, Cloudflare, BYO) — large surface, no urgent customer demand. Sequence: after Epic 29's secret store lands (gates 30-1's `ITenantInfrastructureProvider` v2 since it depends on `ISecretStoreBackend`).
- **Epic 31 git-platform expansion** — Forgejo (31-5) + GitLab (31-3) + integration test harness (31-10). Bitbucket (31-11) + Azure DevOps (31-12) explicitly deferred per planning blockers.
- **Epic 1.5 backup-and-recovery (Story 1.5-7)** — designed only; first hot-path implementation (Approach A: GitHub Actions cron daily backup to object storage) is unscheduled. Per user: backups not now.
- **`@types/node` 22 → 25** bump — incompatible with Node 22 LTS runtime; revisit at Node 24 LTS (~2026-Q3).

## Sequencing constraints

```
must-ship #1: 19-6           (independent — start immediately)
must-ship #2: 29-1           (independent — start in parallel with 19-6)
must-ship #3: 18-5 → 18-7 → 18-8     (sequential chain; start 18-5 in parallel with 19-6 + 29-1)

post-must-ships:
  9-5 (independent)           → enables 9-9 → 9-12 (Team A)
  29-2 (after 29-1)           → enables 29-3 + 29-4 + ...
  17-3 (in-progress)          → ship when ready
```

Three parallel tracks at start (19-6, 29-1, 18-5). When 18-5 lands, branch: 18-7 picks up the sequential chain while 19-6 / 29-1 continue independently.

## Acceptance — when is Wave B "done"?

Wave B closes when ALL of:

1. **Story 19-6 merged to feat/wave-b** — RLS plane is live, fail-closed regression test green
2. **Story 29-1 merged to feat/wave-b** — `ISecretStore` + driver port shipped, mocks + tests landed
3. **Story 18-8 merged to feat/wave-b** (with 18-5 + 18-7 as predecessors) — tenant-admin UI surface live; tenant_owner/admin can manage members end-to-end
4. **Wave B → main PR opened** — single integration PR per the wave-A pattern, CI all green, ready for merge

Stretch goal: ship 9-5 (Provider chain API) in Wave B if any of the must-ships finish ahead of estimate. It's independent and 14h, so a single agent can pick it up between blockers.

## Risk register

| Risk | Mitigation |
|---|---|
| 19-6's repository swap surfaces RLS policy gaps not caught in Phase-2 migration | Fail-closed regression test catches a NULL-tenant leak; runbook for rolling back to superuser connection if a critical regression appears |
| 29-1's interface design needs revision after 29-2 surfaces a real backend constraint | Keep 29-1 narrow to the verified-needed surface; resist over-design. 40h of rework cost is the explicit warning. |
| 18-8 depends on a 3-story chain (18-5 → 18-7 → 18-8) — chain delay propagates | Parallel-start 18-5 with 19-6/29-1 so the chain isn't waiting at start. If 18-5 slips, 18-7 + 18-8 can pre-author against stub backends. |
| Wave B doesn't have an obvious 4th must-ship — danger of premature scope expansion | Hold to the named 3 + stretch 9-5. Defer everything else to Wave C explicitly. |

## Working notes

- **Branch**: `feat/wave-b` (created 2026-04-28, pushed to origin)
- **Base**: `main` at commit `5321316` (Wave A integration merge)
- **CI**: ci.yml + codeql.yml already include `feat/wave-a` in their pull_request triggers. Add `feat/wave-b` in the same shape (one-line update).
