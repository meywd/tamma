# PR #329 — Merge Readiness + Remaining Backlog

**Date**: 2026-04-23
**Branch**: `feat/wave-a` → `main`
**Current tip**: `1cc215e`
**Test matrix**: 2956 passed / 3 skipped (aspirational Story 28-1) / 0 failed
**CI**: all 4 workflows green (CI, CodeQL, Docker builds, forbidden-symbols)

---

## Part A — Items I claimed to review but skipped

Verify these before merge or acknowledge the risk explicitly.

1. **`PostgresAlertSink.ResolveMatchingChannelsAsync`** — does a platform-scoped alert (`tenantId == null`) correctly NOT fan out to tenant-specific channels? Does a tenant-scoped alert fan out to platform channels too? Docstring claims yes; method body never read.
2. **`NotificationDispatcher` retry math** — docstring claims exponential backoff `[30s, 2m, 5m, 15m, 30m]`. Never verified the computed delays match; no test asserts the delay schedule.
3. **`TokenBucketAlertRateLimiter` refill correctness** — token bucket needs a tick or timestamp-based refill. Only read the class name + DI registration, not the impl.
4. **Rule predicate `count_gte` semantics** — does the 5min window use wall-clock or event timestamps? Does `group_by: ["tenantId"]` handle missing tenantId gracefully (count as platform-wide, or ignore the event)?
5. **`agent-dispatch-failed-3x-5min` test coverage** — is there a test that seeds 3 events and asserts the rule fires exactly once (not 3 times, not 0 times)?
6. **CodeQL `cs/cleartext-storage` future blast radius** — count other `_logger.Log*(... {Tenant}", tenantId)` call sites that will re-trigger on every future push. Decide: query-filter exclusion, wholesale migration to DCB events, or ongoing dismissal workflow.
7. **Migration `Down()` reversibility** for Wave C tables (`alerts`, `alert_channels`, `alert_delivery_attempts`, `alert_rules`, `alert_evaluator_cursor`). Do they drop cleanly or leave orphan FK constraints?
8. **Test suite runtime regression** — `Tamma.Api.Tests` went from ~2m → 4m39s over the PR. Investigate: is it C.4's `TammaApiHealthMonitorTests` timing-based waits, E2E flow's 10s waitForAsync, or something worse?

---

## Part B — Deferred from prior waves (not started)

### Wave D (originally scoped, never dispatched)

- **Epic 30** — pluggable tenant-infra provisioning (Cranl/Hetzner/Cloudflare/BYO backends)
- **Epic 31** — multi Git-platform support: GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git (GitHub already done)
- **14 P2 code-review findings** from the earlier audit pile

### Epic 12 — context tools

Originally on the Wave C list; substituted out when we pivoted Wave C to the alert system. No work started.

### Epic 28 end-state

- **Story 28-1** — finish db-per-tenant cutover: move legacy-shared DbSets off `ControlPlaneDbContext` onto per-tenant `TenantDbContext` via `ITenantDbContextFactory`.
  - Affected POCOs: `AgentConfigs`, `PromptOverrides`, `WorkflowInstances`, `DomainEvents`, `QueuedTasks`, `EmailOutbox`, `BudgetConfigs`, `MentorshipSessions`, `MentorshipEvents`, `JuniorDevelopers`, `Stories`
  - Re-enables the 3 `[Ignore]`'d aspirational tests (`Model_Has_ExpectedControlPlaneEntities`, `Tenants_Cranl_Columns_Are_Ignored_On_NewContext`, `Tenant_Resident_Entities_Have_No_TenantId_Column`)
- **Story 28-13** — migrate KEK from env-var (`Cranl:EncryptionKey`) to OpenBao. Currently env-var; migration trigger deferred indefinitely.

### Story 18-5 follow-ups (shipped as "shell foundation" only)

- Onboarding wizard (step 6 in plan)
- Repo/run detail pages (steps 7–8)
- Settings pages + `OrgSwitcher` (steps 5 + 9)
- Docker + nginx `dash.tamma.dev` deployment (step 10)
- Playwright E2E tests (step 11)

### Story 29-10 intentionally-NOT-deleted stopgaps

These are load-bearing today; scheduled for removal after other stories land.

- `TenantSecretProtector.cs` + `TenantSecretProtectorAdapter.cs` — still used by Story 28-5 for per-tenant DB-URL encryption
- `tenants.CranlDatabaseUrlEncrypted` column — tenant-scoped cabinet routing not yet wired

---

## Part C — Cross-cutting debt / housekeeping

- **196 npm Dependabot alerts** on TS packages (deferred by earlier decision)
- **40 unrelated package test failures** under root `pnpm vitest run` — pre-existing missing-module errors in `@tamma/shared` / `nanoid` dependents. Not introduced by this PR; worth a separate cleanup PR.
- **`changeme` literal** in Phase-2 migration — retire when Phase-2 RLS is actually removed (superseded by Epic 28 db-per-tenant anyway)
- **Postgres 17 vs 16 version drift** — dev/prod mismatch not reconciled
- **CodeQL `cs/cleartext-storage` rule policy decision** — outstanding (see Part A item 6)

---

## Part D — In-progress on this branch

- **Task #12 (CodeQL alerts clean-up)** — fix pushed for alert #92 (`permissions: contents: read` on forbidden-symbols workflow). Alerts #93–96 waiting on CodeQL re-analysis post the Wave-C.4 rename → drop-log change. Likely all auto-closed on next CodeQL run; not yet verified by `gh api`.

---

## Part E — Recommended ordering for shipping

1. Verify the 8 Part-A items (30–60 min surface read of actual code + test assertions)
2. Confirm CodeQL re-analysis closed #93–96 (one `gh api` call after next CodeQL run)
3. Merge PR #329 into `main`
4. Schedule Part-B items as separate PRs:
   - Epic 28-1 (unlocks 3 skipped tests; architectural keystone)
   - Story 18-5 follow-ups (complete the user dashboard)
   - Wave D (Epic 30 + Epic 31 + 14 P2 findings)
   - Epic 12 (context tools)

If deferring Part A and taking the large-branch rollup risk explicitly is the preference, the revert-merge is the safety net.
