# DB-per-Tenant — Implementation Sequencing Plan

> **Superseded/extended by the unified schema-per-tenant model** — see `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (complete 2026-06-10).

**Status**: Active
**Scope**: Execution plan for the 12 Epic 28 stories. Derived from the
four design documents in this directory.
**Companion**: [`../../epic-28/README.md`](../../epic-28/README.md) — epic
overview, dependency graph, and conflict resolutions.

This plan sequences the 12 stories into three phases. Phases 1 and 2 are
serial (each story depends on the previous). Phase 3 fans out into three
independent parallel streams once the foundation and provisioning
plumbing are in place.

| Phase | Stories | Effort (h) | Wall-clock (h) | Parallelism |
|---|---|---|---|---|
| **Phase 1 — Foundation** | 28-1 → 28-2 → 28-3 | 60 | 60 | Serial |
| **Phase 2 — Provisioning plumbing** | 28-5 → 28-9 → 28-12 | 89 | 89 | Serial |
| **Phase 3 — Parallel streams** | (28-4 + 28-6) ‖ (28-7 + 28-8) ‖ (28-10 + 28-11) | 116 | ~50 | 3 streams |
| **Total** | 12 stories | **265** | **~200** | 1.3× speedup |

---

## Phase 1 — Foundation (serial)

**Goal**: Create every schema and the DbContext plumbing that every later
story depends on. Nothing can be parallelised here — each story mutates
the artefacts the next story reads.

### Ordered stories

| Step | Story | Effort | Why in this order |
|---|---|---|---|
| 1 | 28-1 — EF migration scripts | L (30h) | All four schemas (CP + tenant + global-Elsa + per-tenant Elsa) land first. Every subsequent story assumes these tables exist and compiles against them. |
| 2 | 28-2 — Split `TammaDbContext` into `ControlPlaneDbContext` | M (16h) | The CP DbContext must exist before anything that writes `platform_events`, reads `tenants`, or touches `users`. Done second so tests can exercise against the new CP schema. |
| 3 | 28-3 — `TenantDbContext` factory with runtime connection routing | M (14h) | The factory is the only new knob most handlers need. It takes a `NpgsqlDataSource` injected at request time — the actual resolver that *provides* the data source is 28-4 in phase 3. Phase 1 ships the factory with a stub resolver (always returns the CP data source, used only in integration tests) so 28-4 can replace it cleanly. |

**Total**: 60 hours.

### Deploy gate

Before merging Phase 1 to `feat/auth-foundation`:

- All four migration sets run idempotently on a fresh Postgres 17 (twice-run-clean test).
- `ControlPlaneDbContext` builds and the existing CP-only endpoints
  (`/auth/login`, `/auth/register`, `/auth/me`, `/tenants` listing) still
  pass their current test suite.
- `TenantDbContext` builds against the stub resolver and a canary
  tenant-scoped endpoint round-trips a row.
- `dotnet ef migrations script` output reviewed for unintentional DDL.
- No change to runtime behaviour in production — the new DbContexts are
  registered but unused by live traffic until Phase 3 lights up the
  real resolver.

### Rollback strategy

- Migrations are forward-only with a documented **down script** per
  migration (EF `Down()` methods). If Phase 1 is reverted, run the down
  scripts in reverse order. The four schemas are designed to coexist
  with the current shared `public` schema — no data is moved in Phase 1,
  so rollback is schema-drop only.
- Git revert of the DbContext split is mechanical (one commit per story
  keeps reverts atomic).
- **No data loss on rollback** — existing data stays in the shared DB.

### Risks and mitigations

| Risk | Mitigation |
|---|---|
| Migration 28-1 collides with an unrelated in-flight migration number | Coordinate migration numbers with any open PR before merging 28-1. Reserve a contiguous block (e.g. 050–053) for the four migration sets. |
| `TammaDbContext` has implicit consumers via reflection / DI naming | Before 28-2, grep for `TammaDbContext` usage and confirm every caller is migrated or still compiles. The CI build fails closed on missing references. |
| Stub resolver leaks into production | Gate the stub behind an `#if DEBUG` compile flag, and add an integration test that asserts `TenantDbContext` construction in a non-DEBUG build requires a real `ITenantConnectionResolver` registration. |

---

## Phase 2 — Provisioning plumbing (serial)

**Goal**: Stand up the tenant-lifecycle workflow, the auth surface that
handles async-provisioned tenants, and the Postgres role + KEK machinery
the workflow uses. Serial because each story depends on the previous.

### Ordered stories

| Step | Story | Effort | Why in this order |
|---|---|---|---|
| 1 | 28-5 — `CreateTenantWorkflow` + `DeleteTenantWorkflow` on global Elsa | XL (45h) | The workflow is the central lifecycle artefact. It creates tenant roles, DBs, runs migrations, and flips `tenants.Status`. Everything else in Phase 2 either produces tokens the workflow honours (28-9) or the secrets the workflow uses (28-12). **Depends on 28-2 (CP DbContext) + 28-6 (platform tables)** — so 28-6 is pulled into Phase 1 as a prerequisite for 28-5, and lands before Phase 2 begins. See "Parallel-agent-safe story groups" below. |
| 2 | 28-9 — JWT claims + `/auth/switch-org` + refresh tokens across tenants | L (24h) | Once tenants can transition `pending_verification → provisioning → active`, the JWT model must carry the active `tid`, handle rootless JWTs for users with no active tenant, and support `/auth/switch-org`. Refresh must re-read role from CP so a newly-provisioned tenant's membership role shows up on next refresh. Depends on 28-2 + 28-4 + 28-8. |
| 3 | 28-12 — Roles (`admin`/`provisioner`/`app`) + KEK rotation | L (20h) | With the workflow running, the three Postgres roles must be provisioned out-of-band and the KEK + secondary-KEK must be wired into the API pod's environment. Rotation tests exercise the full decrypt-with-secondary fallback. Depends on 28-1 (tenants table has `EncryptedConnectionString` + `KekVersion`) and 28-4 (resolver consumes the decrypted string). |

**Prerequisite pulled forward**: 28-6 (`platform_events` + `platform_queued_tasks` + `platform_email_outbox`, M 18h) must land between Phase 1 and Phase 2 so 28-5 has somewhere to emit `TENANT.PROVISIONING_REQUESTED / STEP_* / PROVISIONED.SUCCESS`. See "Parallel-agent-safe story groups" for the rationale.

**Total**: 89 hours (Phase 2 itself); 107h including the 28-6 pull-forward.

### Deploy gate

Before merging Phase 2 to `feat/auth-foundation`:

- End-to-end provisioning test passes: a fresh registration, verify-email
  click, workflow run, and tenant-active flip all complete under the 60s
  p95 target on a local dev Postgres.
- Compensation test passes: inject a permanent failure at migration step 3
  and verify the compensation ladder drops the half-built DB, role, and
  any stray artefacts; `tenants.Status='failed'`, `requires_manual_cleanup=false`.
- `/auth/switch-org` round-trip produces a new access token with the
  right `tid` + `role` claim, and refresh honours a mid-session role
  change inside the active tenant.
- KEK rotation integration test: provision a tenant with `KekVersion=1`,
  rotate to `KekVersion=2`, verify live requests mid-rotation succeed
  via the secondary-KEK fallback, and verify the background re-encrypt
  loop migrates the tenant to `KekVersion=2`.
- 12-scenario cross-tenant leak suite (from the epic success metrics)
  runs green.

### Rollback strategy

- Feature-flag every new endpoint behind `Tamma:Features:DbPerTenant=false`.
  On rollback, flip the flag: `/auth/switch-org` returns 404, the
  middleware stops honouring rootless JWTs, and `TenantContextMiddleware`
  falls back to the Phase 1 stub path.
- `CreateTenantWorkflow` lives in global Elsa — reverting its deployment
  means new registrations sit at `Status='pending_verification'` until
  the workflow is re-deployed. Acceptable for a short rollback window;
  registrations are written to CP and can be retried.
- KEK rotation is append-only — the secondary slot stays populated until
  an explicit drop. Rollback leaves the rotation state harmless.

### Risks and mitigations

| Risk | Mitigation |
|---|---|
| `CreateTenantWorkflow` partial-failure strands DBs | The compensation ladder + `CleanUpFailedTenantWorkflow` (Doc 03 §4.3) is shipped in the same story and covered by T6 and T7 integration tests. Manual-cleanup quarantine state (`requires_manual_cleanup=true`) is surfaced in 28-11's admin UX. |
| Global-Elsa scale-out with many `OrchestratorWorkflow` instances | Benchmark is run in Phase 3 (28-10) at 1k / 5k / 10k idle instances. If any threshold trips, add a tenant-fanout singleton as a follow-up before crossing 500 production tenants. Not blocking for Phase 2. |
| KEK rotation corrupts a tenant row | Re-encrypt loop is idempotent: on failure it re-reads `KekVersion` and retries. A consistency check at end-of-loop asserts every `tenants` row has a decryptable envelope. |
| Switch-org issues JWT for a `provisioning` tenant | 28-9 adds a Status check to `/auth/switch-org` — if target `Status != 'active'` the server returns 503 with `X-Tenant-Status: provisioning` rather than issuing the token. |

---

## Phase 3 — Parallel streams (3 streams)

**Goal**: Ship the remaining six stories in three independent streams
once Phase 1 and Phase 2 have landed. Each stream touches different code
paths and can be worked by a different agent / engineer.

### Stream A — Runtime data plane (28-4 + 28-6)

**Stories**: 28-4 (Tenant connection resolver + LRU pool cache, L 22h),
28-6 (platform_* tables, M 18h).

**Effort**: 40 hours.

**Why together**:
- 28-4 and 28-6 touch non-overlapping code paths (resolver lives in
  `packages/api/src/services/tenant-connection-resolver/`; platform
  tables add entities under `Entities/Platform*`).
- 28-6 is a prerequisite for 28-5 in Phase 2 and therefore must land
  before Phase 2 opens — see "Parallel-agent-safe story groups" below.
  28-4 can land at any point in Phase 3 because Phase 1/2 use the stub
  resolver.

**Files owned**: `services/tenant-connection-resolver/*`, `Entities/PlatformEvent.cs`, `Entities/PlatformQueuedTask.cs`, `Entities/PlatformEmailOutbox.cs`, `Migrations/04x_platform_tables.cs`.

### Stream B — Auth plane (28-7 + 28-8)

**Stories**: 28-7 (API-key prefix routing `tk_t_/tk_pl_/tk_u_`, M 14h),
28-8 (`TenantContextMiddleware` async-provisioning handling, M 12h).

**Effort**: 26 hours.

**Why together**:
- Both stories change the authentication pipeline but in different
  handlers — 28-7 modifies `ApiKeyAuthHandler`, 28-8 modifies
  `TenantContextMiddleware`. The two files are edited by the same
  stream to avoid merge churn on the DI registration site.
- 28-7 depends only on 28-6 (CP tables exist); 28-8 depends on 28-4
  (resolver exists) and 28-5 (workflow drives state machine). Both are
  available by Phase 3 start.

**Files owned**: `Auth/ApiKeyAuthHandler.cs`, `Middleware/TenantContextMiddleware.cs`, `Services/TenantResolutionService.cs`.

### Stream C — Observability + ops (28-10 + 28-11)

**Stories**: 28-10 (`platform_analytics_hourly` rollup workflow, L 28h),
28-11 (Admin UX for `tenants.Status` state machine, L 22h).

**Effort**: 50 hours (the critical path of Phase 3).

**Why together**:
- 28-10 (nightly Elsa workflow reading `platform_events` + per-tenant
  events) is the cross-tenant analytics rollup. 28-11 is the admin
  dashboard that reads the rolled-up data and the raw `tenants.Status`
  column. They form a single observability feature unit.
- Neither conflicts with streams A or B; 28-11 reads the CP DbContext
  (available since Phase 1) and the workflow state written by 28-5
  (available since Phase 2).
- 28-10 owns the global-Elsa scale benchmark (see conflict resolution
  #3 in the Epic 28 README): measure idle orchestrator instances at
  1k / 5k / 10k and file a follow-up if any threshold trips.

**Files owned**: `Workflows/PlatformAnalyticsRollupWorkflow.cs`, `Migrations/04x_platform_analytics_hourly.cs`, dashboard pages under `packages/dashboard/src/pages/admin/` + admin components under `packages/dashboard/src/components/admin/`, admin API routes under `Endpoints/Admin/TenantsEndpoints.cs`.

### Wall-clock arithmetic

| Stream | Effort | Wall-clock (1 agent per stream) |
|---|---|---|
| A | 40h | 40h |
| B | 26h | 26h |
| C | 50h | 50h |
| **Critical path (Stream C)** | — | **50h** |

Total effort across streams: 116 hours. Critical path: 50 hours (Stream C).
The 26h Stream B has 24h of slack; pair it with Stream A's 40h if an
engineer frees up.

### Deploy gate

Before merging Phase 3 (full epic complete) to `main`:

- All 12 success-metric integration tests (12 cross-tenant leak
  scenarios) still pass end-to-end.
- Tenant-create p95 < 60s at 10 concurrent provisioning (measured in
  a dedicated load test).
- Tenant-delete test with 1M events finishes in < 30s.
- Admin UX shows the full state machine live against an integration
  environment with a couple of tenants in each state
  (`pending_verification`, `provisioning`, `active`, `delete_requested`,
  `deleting`, `deleted`, `failed`).
- Nightly rollup workflow runs against a seeded 30-day dataset and
  produces `platform_analytics_hourly` rows with correct aggregates.
- Orchestrator-scale benchmark report is attached to 28-10's story
  file: 1k idle instances p95 bookmark-scan latency, RAM, and DB-pool
  usage all under documented thresholds.

### Rollback strategy

- Each stream merges behind its own sub-flag
  (`Tamma:Features:DbPerTenant:Resolver`, `:ApiKeyRouting`,
  `:AdminTenantUx`, `:PlatformAnalytics`). Any one stream can be
  disabled in production without affecting the others.
- The `TenantContextMiddleware` changes are additive — the pre-Phase-3
  middleware code path stays behind the flag so reverting one PR
  doesn't break unrelated endpoints.
- Analytics rollup workflow has a cron trigger — disable the trigger
  in Elsa Studio to pause without redeploying.

### Risks and mitigations

| Risk | Mitigation |
|---|---|
| Stream A's resolver caching interacts badly with Stream B's API-key routing under load | Shared load-test harness runs both streams together before merge (not after). A pre-merge synthetic test with 500 RPS across a mix of JWT and API-key auth flows guards against eviction-thrash interactions. |
| Stream C's analytics rollup contends with live per-tenant traffic | Rollup runs at 02:00 UTC, opens short-lived read connections per tenant (no write path), and respects the pool cache's max size. A circuit-breaker skips a tenant whose recent error rate exceeds 5% — don't add provisioning pressure during an outage. |
| Three streams land on the same week and pile up review backlog | Phase 3 is designed to take ~50h wall-clock on one agent per stream. Reviews can be staggered: Stream B (26h, smallest) merges first, then Stream A (40h), then Stream C (50h). This sequence matches natural completion order. |
| Admin UX (28-11) reveals race conditions in `tenants.Status` transitions | Admin UX is read-only on status except for the cancel-delete button, which is already a `POST` with optimistic-lock on `tenants.Status='delete_requested'`. No new race surface. |

---

## Parallel-agent-safe story groups

Some stories can run concurrently once their prerequisites have merged.
This section gives explicit go-ahead lists for the "can X run in
parallel with Y?" question.

### After Phase 1 merges

Phase 1 (28-1, 28-2, 28-3) must complete serially. Once all three are on
`feat/auth-foundation`:

- **28-6** (platform_* tables) becomes available. It depends only on
  28-1 and is the prerequisite for 28-5. Pull it forward to bridge
  Phase 1 → Phase 2.
- **28-4** (resolver) becomes available. Depends on 28-3. Can be run
  in parallel with 28-6 by a second agent — the two stories touch
  non-overlapping files (resolver service vs platform entities).

### After Phase 2 merges

Phase 2 (28-5, 28-9, 28-12) lands serially. Once all three are on
`feat/auth-foundation`, the following can be worked in parallel by up to
three agents:

- **28-4** (if not already landed) and **28-6** (if not already landed)
  — Stream A.
- **28-7** and **28-8** — Stream B. 28-7 may land first because it only
  depends on 28-6; 28-8 depends on 28-4 + 28-5.
- **28-10** and **28-11** — Stream C. Both depend on 28-5 + 28-6, which
  are by this point shipped.

### Never parallel (hard sequential)

- 28-1 → 28-2 (DbContext split consumes the new schema)
- 28-2 → 28-3 (tenant DbContext factory extends patterns from CP)
- 28-3 → 28-4 (resolver implements the interface 28-3 declares)
- 28-6 → 28-5 (workflow writes to tables 28-6 creates)
- 28-5 → 28-8 (middleware honours the Status state machine 28-5 drives)
- 28-5 → 28-11 (admin UX reflects the Status state machine)

### Safe with caveats

- 28-10 and 28-11 share a small migration footprint (both add platform
  admin tables under `apps/tamma-elsa/src/Tamma.Data/Entities/Platform*`).
  Coordinate migration numbers so both agents don't claim the same
  `04x` slot. Use a pre-PR hook to reserve numbers, or alphabetise
  (28-10 takes the lower number).
- 28-7 and 28-8 both register handlers in `Program.cs`. Merge order
  doesn't matter but the DI lines land in the same file — rebase is
  mechanical.

---

## Summary

- **Serial effort**: 265 hours.
- **Wall-clock with parallelism**: ~200 hours (60h Phase 1 + 89h Phase 2
  + 50h Phase 3 critical path; pull-forward of 28-6 between phases adds
  ~18h shared with Stream A's parallel budget in Phase 3).
- **Parallelism factor**: 1.3×. Not higher because the foundation
  (Phase 1) and provisioning lifecycle (Phase 2) are inherently serial —
  each story strictly needs the artefact the previous one produced.
- **Critical path**: 28-1 → 28-2 → 28-3 → 28-6 → 28-5 → 28-9 → 28-12 →
  Stream C (28-10 + 28-11).
- **Safest merge order**: Phase 1 on `feat/auth-foundation`; Phase 2 on
  top; Phase 3 streams merge in order B → A → C as each completes.
- **Deploy gates** at the end of each phase gate the next phase from
  opening — no concurrent work across phase boundaries.
