# Story 28-1 entity split — design decisions

**Date**: 2026-04-27
**Context**: a Story 28-1 scoping agent surfaced 5 substantive design calls that would have to be answered before the entity-move could land cleanly. This ADR records the answers and the 4-PR execution sequence.

The original Story 28-1 brief said "move 11 POCOs from `ControlPlaneDbContext` to `TenantDbContext`, drop `TenantId` columns, fix consumers, generate migrations, re-enable 3 [Ignore]'d tests." Direct execution wasn't safe because:

1. Five repos use a `TenantId IS NULL` row in CP for the platform-wide default — that row has no home after the entity move.
2. Several repos have legitimate cross-tenant scan paths (`EventRepository.QueryAsync(tenantId: null)`, sender/processor scans) that break the moment data moves to per-tenant DBs.
3. The Cranl test (`Tenants_Cranl_Columns_Are_Ignored_On_NewContext`) cannot be re-enabled today — the Cranl columns are intentionally load-bearing per Story 29-10 stopgap.
4. The CP-model test (`Model_Has_ExpectedControlPlaneEntities`) lists only 16 expected tables; CP actually has ~24 (alerts, kek_rotations, admin_impersonations, platform_bootstrap added since the test was written).
5. `EmailOutboxRepository` and `QueuedTaskRepository` straddle two planes (per-tenant enqueue + cross-tenant scan).

## Decisions

### #1 — Platform-default-row pattern: code defaults

**Context**: `AgentConfig`, `PromptOverride`, `SanitizationRule`, `BudgetConfig`, `ProviderHealth` repositories each fall back to a `TenantId IS NULL` row in CP for platform-wide defaults.

**Decision**: **Option A — code defaults**, matching CLAUDE.md's prompt-store pattern ("System defaults remain in code (`default-prompts.ts`)"). Each repository defines a `DEFAULTS` constant for its kind; lookups fall back to the constant when no per-tenant row exists.

**Rationale**:
- Simplest. No new CP tables, no dual-store reads, no platform-default migration to plan.
- Consistent with existing prompt-store pattern, which is the most prominent precedent in the codebase.
- Behavior change for ops who edited the platform-default row directly: they'll lose those edits. **Mitigation**: dump existing platform-default rows pre-migration as a one-shot diff for ops review.

**Alternatives considered**:
- (B) CP defaults table per kind: ships 5 new CP tables. Preserves API but adds five new migrations + five new admin endpoints to manage them.
- (C) Hybrid: code defaults primary, optional CP override row. Requires dual-store reads on the hot path.

### #2 — Cross-tenant admin queries: per-call decision

**Context**: A few callers query across tenants today via `_cp.DomainEvents.Where(e => e.TenantId == null OR criteria)`-style scans.

**Decision**: per-call answer.
- **Cross-tenant lifecycle events** (admin dashboard "recent events", SSE event stream): use `platform_events` (already exists, already CP-resident; tenant lifecycle events already get written there per the Doc-01 §5.1-5.2 split).
- **Per-tenant queue draining** (`OutboxSmtpSender`, `TaskQueueProcessor`): switch to per-tenant pollers OR move ALL platform work to `IPlatformEmailOutboxRepository` / `IPlatformQueuedTaskRepository` (the platform-scoped versions already exist for some flows).
- **Tenant-scoped events for an admin viewing one tenant**: use `ITenantDbContextFactory.CreateAsync(tid).DomainEvents` — it's already a per-tenant query.
- **Cross-tenant tenant-scoped events** (admin "search across all tenants"): fan-out per-tenant queries with pagination + aggregation in API layer. Not a default path; only build when an explicit user story demands it.

**Rationale**: matches the Doc-01 §5 split. Lifecycle events are platform; per-tenant business events stay per-tenant.

### #3 — Cranl test stays `[Ignore]`'d, point reason at Epic 30

**Context**: `Tenants_Cranl_Columns_Are_Ignored_On_NewContext` assumes Cranl columns leave with Story 28-1. They don't — `LruPooledTenantConnectionResolver` is wired in production today and reads `tenants.CranlDatabaseUrlEncrypted`. Removing the columns now breaks routing.

**Decision**: keep the test `[Ignore]`'d but rewrite its reason from "until Story 28-1 lands" to "until Epic 30 ships pluggable infra backends and an alternative routing column."

**Rationale**: Story 29-10 stopgap (load-bearing today) blocks this re-enablement. Epic 30 is the right unblock signal.

### #4 — Extend `Model_Has_ExpectedControlPlaneEntities` test list

**Context**: The test's expected-table list has 16 entries. Today CP has ~24: the original 16 + alert tables (5) + `kek_rotations` + `admin_impersonations` + `platform_bootstrap`.

**Decision**: extend the expected list to enumerate all current CP-resident tables. Re-enable the test as part of Story 28-1's execution.

**Rationale**: the test exists to catch accidental tenant-resident-entity drift onto CP. It's correct in intent but stale in expected values. Updating the list AND re-enabling captures both the intent and the current state.

### #5 — Outbox + QueuedTask: tenant-scope = tenant DB, platform-scope = platform_* tables

**Context**: Both repos have an "enqueue knows tenant → would go to tenant DB" path AND a "sender/processor cross-tenant scan → currently CP DB" path.

**Decision**: split cleanly along the existing `IPlatformEmailOutboxRepository` / `IPlatformQueuedTaskRepository` abstraction.
- Tenant-scoped operations (enqueue an email for tenant X, queue a task for tenant Y) write to that tenant's DB outbox/queue.
- Platform-scoped operations (welcome email, billing email, cross-tenant maintenance task) write to `platform_email_outbox` / `platform_queued_tasks` (already CP-resident).
- The senders/processors are split: per-tenant pollers walk the LRU pool's known-warm tenants for tenant-scope drain; the existing platform sender keeps draining `platform_*` tables.

**Rationale**: matches the Doc-01 §4.3 step-10 resolution (welcome email goes to platform outbox), and matches the existing `IPlatformX` interface presence in the codebase.

## Execution sequence — 4 PRs

The agent's recommended sequence converts a single 15-20h keystone refactor into 4 reviewable PRs, three of which can ship in parallel:

### PR A — platform-default-row repos → code defaults (~4h)
Files: `AgentConfigRepository`, `PromptRepository`, `SanitizationRepository`, `BudgetConfigRepository`, `ProviderHealthRepository`, plus 5 small `DEFAULTS` constant files. Tests stay green; no entity moves yet.

### PR B — outbox + queued-task scope split (~3h)
Files: `EmailOutboxRepository`, `QueuedTaskRepository`, `OutboxSmtpSender`, `TaskQueueProcessor`, plus updates to call sites that enqueue platform-scope work.

### PR C — cross-tenant admin queries (~2h)
Files: `EventRepository.QueryAsync`, `UserDashboardEndpoints`, `DashboardEndpoints`, `AlertRuleEvaluator`, `DiagnosticsService`. Switches each cross-tenant scan to either `platform_events` or per-tenant fan-out per the matrix above.

### PR D — the actual entity split (~6h, AFTER A/B/C land)
Now mechanical:
- Drop the 11 + 4 mentorship `DbSet`s from `ControlPlaneDbContext`
- Add to `TenantDbContext` (already has them at `TenantDbContext.cs:52-75`)
- Set `omitTenantIdColumn: true` in tenant-side `OnModelCreating`
- Generate CP-drop migration + tenant-create migration via `dotnet ef`
- Update test fixtures (~30 files) to seed via `ITenantDbContextFactory` instead of `_cp.X`
- Re-enable `Model_Has_ExpectedControlPlaneEntities` (with extended list per #4) and `Tenant_Resident_Entities_Have_No_TenantId_Column`
- Cranl test stays `[Ignore]`'d with updated reason per #3

## Out of scope

- **Cross-tenant tenant-scoped event search** (admin "show me all events across all tenants"): not built today, no current user story. Build when a story demands it.
- **Migrating existing platform-default-row data** to code defaults: dev/test data is disposable, prod has no users yet — accept the data loss with a one-shot `pg_dump` of those rows for ops review.
- **Removing Cranl columns from `Tenant`**: blocked by Story 29-10 stopgap; will leave with Epic 30.
