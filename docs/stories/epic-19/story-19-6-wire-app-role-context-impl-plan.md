# Story 19-6 Implementation Plan — Wire Per-Request Repositories to App-Role Context

**Status**: Planned (2026-04-20)
**Story brief**: [`story-19-6-wire-app-role-context.md`](./story-19-6-wire-app-role-context.md)
**Team**: Layer 4 Team A (post-Epic-19 hardening)
**Branch**: `feat/story-19-6-wire-app-role-context`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-a-19-6-app-role`

---

## 1. Objective

Activate the RLS scaffold that Phase 3 landed but did not switch on. The
Phase-2 migration created the `tamma_app` Postgres role and tenant
isolation policies on 14 tables; `TammaAppDbContext` + `TenantContextInterceptor`
compile and run — but **no** per-request code paths inject the app
context. This story swaps `TammaDbContext` → `TammaAppDbContext` across
21 repositories and 5 endpoint handlers, adds a fail-closed regression
test that inserts a NULL-tenant row as superuser and proves the app-role
connection does not return it, and ships the runbook for rotating the
`tamma_app` password and flipping the `TammaAppDb` connection string.

Closes review findings: `orgs/002`, `orgs/004`, `admin-db/020`,
`admin-db/021` (currently downgraded to "Partial — scaffold only"). No
functional regression expected; the migration + background-service
paths intentionally stay on the superuser connection.

## 2. Dependencies

Hard blockers:

- **Phase-3 scaffold commits** (`e53c5a1`, `9e20e05`, `159f12a`) — merged.
- **Phase-2 migration** `20260419021119_Phase2RlsAndTriggers.cs` — applied.
- `TammaAppDbContext` + `TenantContextInterceptor` — already in the repo.

Soft:

- **Story 29-7** (DB credential rotation) — this story's manual
  runbook becomes automated once 29-7 ships.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/AppRoleRegressionTests.cs` | Fail-closed regression test — AC 3. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/AppRoleInjectionAuditTests.cs` | CI guard: asserts no production file (outside the approved cross-tenant list) injects `TammaDbContext` directly. |
| `/home/meywd/tamma/docs/runbooks/enable-app-role-rls.md` | Operator rollout runbook — AC 4. |
| `/home/meywd/tamma/.github/workflows/db-context-guard.yml` | GitHub Action that runs `AppRoleInjectionAuditTests` on every PR to block regression. |

## 4. Files to modify

### Production — 21 repositories (Per-request; swap `TammaDbContext` → `TammaAppDbContext`)

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/UserRepository.cs` | constructor param: `TammaAppDbContext` |
| `.../Repositories/TenantRepository.cs` | same |
| `.../Repositories/TenantMembershipRepository.cs` | same |
| `.../Repositories/UserInviteRepository.cs` | same |
| `.../Repositories/ApiKeyRepository.cs` | same |
| `.../Repositories/PasswordResetTokenRepository.cs` | same |
| `.../Repositories/RefreshTokenRepository.cs` | same |
| `.../Repositories/GitHubInstallationRepository.cs` | same |
| `.../Repositories/GitHubInstallationRepoRepository.cs` | same |
| `.../Repositories/AgentConfigRepository.cs` | same |
| `.../Repositories/PromptOverrideRepository.cs` | same |
| `.../Repositories/ProviderHealthRepository.cs` | same |
| `.../Repositories/ProviderDiagnosticsRepository.cs` | same |
| `.../Repositories/SanitizationRuleRepository.cs` | same |
| `.../Repositories/WorkflowInstanceRepository.cs` | same |
| `.../Repositories/DomainEventRepository.cs` | same |
| `.../Repositories/MentorshipSessionRepository.cs` | same |
| `.../Repositories/JuniorDeveloperRepository.cs` | same |
| `.../Repositories/StoryRepository.cs` | same |
| `.../Repositories/SecretRepository.cs` (once 29-2 lands) | same |
| `.../Repositories/BudgetRepository.cs` | same |

### Production — keep `TammaDbContext` with explicit comment

| Absolute path | Change |
|---|---|
| `.../Services/Background/TaskQueueProcessor.cs` | Add `// cross-tenant by design — background service dequeues for any tenant`. |
| `.../Services/Background/OutboxSmtpSender.cs` | Same comment. |
| `.../Services/Background/WorkflowSyncService.cs` | Same. |
| `.../Middleware/EnsurePersonalTenantMiddleware.cs` | Same. |
| `.../Program.cs` | Migration bootstrap — already uses `TammaDbContext` via EF; no change except comment. |

### Production — endpoints taking a DbContext directly

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs` (lines 21, 81) | `[FromServices] TammaAppDbContext` |
| `.../Endpoints/OrgEndpoints.cs` (lines 512, 570) | same |
| `.../Services/Provisioning/CranlTenantProvisioner.cs` (lines 47, 52) | same |
| `.../Services/Provisioning/CranlProvisioningWorkflow.cs` (lines 31, 39) | same |
| `.../Services/Provisioning/NullTenantProvisioner.cs` (lines 19, 22) | same |

### Audit docs

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/docs/audit/port-gaps/orgs/002-ef-filter-permissive-null-tenant.md` | Re-promote `Outcome: Fixed`. |
| `.../port-gaps/orgs/004-with-tenant-context-set-local-gone.md` | Same. |
| `.../port-gaps/admin-db/020-schema-rls-policies-missing.md` | Same. |
| `.../port-gaps/admin-db/021-schema-tamma-app-role-missing.md` | Same. |

## 5. Sequence of changes

### Step 1 — CI guard + baseline test (2h)

- Write `AppRoleInjectionAuditTests` that:
  - Scans `apps/tamma-elsa/src/**/*.cs`.
  - Allow-lists the 5 background/middleware/migration files.
  - Fails CI if `TammaDbContext` appears as a constructor parameter
    (not `TammaAppDbContext`) anywhere else.
- Run locally — expected to fail for 21 repos and 5 endpoints, proving
  the guard catches the current state.
- Wire into `.github/workflows/db-context-guard.yml`.
- **Commit**: `test(tenancy): CI guard — block direct TammaDbContext injection`.

### Step 2 — Swap repositories in alphabetical order, one commit per 5 (6h × 4 commits = 6h, see breakdown)

Break the swap into four commits so review + revert stay tractable.

- Commit A: repos A-F (`AgentConfigRepository` → `DomainEventRepository`).
- Commit B: repos G-L (`GitHubInstallationRepository` → `JuniorDeveloperRepository`).
- Commit C: repos M-R (`MentorshipSessionRepository` → `RefreshTokenRepository`).
- Commit D: repos S-Z (`SanitizationRuleRepository` → `WorkflowInstanceRepository`).

Per-commit checklist:
- `TammaAppDbContext` ctor injection.
- Run existing repo unit tests — they should all pass.
- Run `AppRoleInjectionAuditTests` — expect fewer failures.

- **Commits**: `fix(tenancy): route {group} repos through TammaAppDbContext`.

### Step 3 — Swap endpoints (2h)

- 5 endpoint handlers updated as listed above.
- Rerun all API integration tests; all should pass.
- `AppRoleInjectionAuditTests` now passes cleanly.
- **Commit**: `fix(tenancy): route endpoint DbContext params through TammaAppDbContext`.

### Step 4 — Cross-tenant comments (1h)

- Annotate the 5 intentional cross-tenant sites with a single-line
  `// cross-tenant by design — <reason>` comment.
- Update `AppRoleInjectionAuditTests.AllowList` to reference the exact
  file+line rather than file-only (sharper enforcement).
- **Commit**: `docs(tenancy): annotate intentional cross-tenant DbContext use`.

### Step 5 — Regression integration test (3h)

- `AppRoleRegressionTests` — Testcontainers Postgres + the full
  migration set:
  1. Open an **admin** (`TammaDbContext`) connection.
  2. Insert a row with `TenantId = NULL` into each of the 14 tenant-scoped tables.
  3. Open a **second** connection as `tamma_app` with
     `SET app.current_tenant_id = <random new guid>`.
  4. `SELECT COUNT(*)` each table — assert result is `0` for every one.
  5. Also assert: flipping the tenant context mid-session to the
     inserted row's (missing) tenant still returns 0, proving the
     NULL-tenant case is closed.
- Also verify fail-closed when `app.current_tenant_id` is NOT set
  (empty string) — returns 0 rows, no error.
- **Commit**: `test(tenancy): RLS fail-closed regression`.

### Step 6 — Runbook + audit updates (2h)

- Author `docs/runbooks/enable-app-role-rls.md`:
  1. Rotate `tamma_app` password: `ALTER ROLE tamma_app WITH PASSWORD :new_pw`.
  2. Update `TammaAppDb` connection string in deploy env.
  3. Startup log line to watch for: `"TammaAppDb connection configured"`.
  4. Probe: hit `/api/v1/tenancy/probe` after deploy; expects
     `{ rlsActive: true }` when a tenant is scoped.
  5. Rollback: revert the env change; `TammaDbContext` fallback kicks
     in automatically (the `AddTammaData` warning docs this).
- Re-promote the 4 audit findings from "Partial" → "Fixed" with a link
  back to the PR.
- **Commit**: `docs(runbooks): enable-app-role-rls + re-promote audit findings`.

### Step 7 — Deploy gate (manual, 0.5h in-hours)

- Staging: rotate `tamma_app` password; set `TammaAppDb` env var; deploy.
- Run integration smoke; confirm `/tenancy/probe` returns `rlsActive=true`.
- Observe `MaxPoolSize` — tune via env if the second Npgsql pool
  exceeds the default 100 conns.
- **Commit**: none — deploy only.

## 6. Test strategy

### Unit tests

- `QueryFilterAndInterceptorTests` (pre-existing) must continue to
  pass. This story extends coverage by running the same tests with a
  `TammaAppDbContext` fixture instead of `TammaDbContext`, asserting
  the query filter fires correctly.

### Integration tests

- `AppRoleRegressionTests` as described in step 5.
- `AppRoleInjectionAuditTests` as described in step 1.

### Manual

- Staging smoke: kick off a single-tenant workflow; inspect logs for
  `"connection: tamma_app"`; query `pg_stat_activity` to confirm the
  app-role connection is active.

### Performance

- Before/after `pgbench`-style comparison of a typical tenant-scoped
  query — RLS adds a policy predicate to every query plan. Acceptable
  overhead: < 10% p99. Record in runbook.

## 7. Rollback plan

- **Graceful rollback**: unset `TammaAppDb` env var on the API pod;
  `AddTammaData` fallback routes `TammaAppDbContext` resolution to
  the superuser connection. RLS becomes dormant; behaviour reverts
  to pre-story.
- **Migration state**: no migration changes. Phase-2 RLS policies
  stay live but harmlessly enforce against a superuser that bypasses
  them.
- **Password rotation non-reversible**: once `tamma_app` password is
  rotated, any older env-var values are invalid. Keep the previous
  password in Hetzner sealed secrets for 24h as break-glass.
- **Data safety**: no data touched. All changes are injection-site swaps.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. CI guard + baseline | 2 |
| 2. Repository swaps (4 commits) | 6 |
| 3. Endpoint swaps | 2 |
| 4. Cross-tenant comments | 1 |
| 5. Regression integration test | 3 |
| 6. Runbook + audit updates | 2 |
| 7. Deploy gate | 0.5 |
| **Total** | **16.5** |

## 9. Open questions

- **Npgsql pool tuning**: second connection pool (app role) increases
  total connections. Current `MaxPoolSize` default is 100 per
  connection string → 200 total after this story. Postgres
  `max_connections` default is 100. Two actions:
  1. Set `MaxPoolSize=50` on each connection string to stay within
     `max_connections=100`.
  2. Or raise `max_connections=300` on the Postgres instance.
  Plan: option 1. Document in runbook.
- **Roslyn analyzer vs. grep-in-test**: the test-based guard is
  simpler to maintain than a Roslyn analyzer. Analyzer gives better
  IDE feedback. Plan: test-based for now; upgrade to analyzer in a
  follow-up if regressions occur.
- **Missing repositories**: if more repositories land between story
  brief date and implementation, the allow-list needs updating.
  Propose adding a `[AllowCrossTenantDbContext]` attribute to make
  the intent explicit and searchable.
- **Test parallelism risk**: `AppRoleRegressionTests` changes session
  config (`SET app.current_tenant_id`). Must run in a non-shared
  test connection. Use `TestContainers.PostgreSQL` per-test container
  or a dedicated connection per test.
- **Rotating the `tamma_app` password post-migration**: today the
  password is `changeme`. First rotation can happen during deploy.
  29-7 will automate this. Between this story and 29-7 merging,
  documented as a manual ops step (runbook step 1).
