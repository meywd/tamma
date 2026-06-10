# Epic 28 Residual Verification — 2026-05-30

**Branch:** feat/wave-b
**HEAD:** 842ed1a4
**Scope:** read-only verification of audit residuals from `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md`. Sibling agents are concurrently closing code gaps for 28-1 skipped tests, 28-5 verify-email trigger + 28-9 follow-ups, and 28-8 middleware decision; this report verifies everything else.

## Summary table

| Story | Residual | Verdict | One-line note |
|---|---|---|---|
| 28-1 | AC2 bootstrap script for shared DBs | REAL GAP | `scripts/db/docker-entrypoint-bootstrap.sh` only runs `postgres-roles.sql`; no `bootstrap-shared-dbs.{sh,ps1}` creates databases / runs migrations |
| 28-1 | AC3 `reset-all.{sh,ps1}` wipe-and-replay script | REAL GAP | does not exist in `scripts/db/`; closest is docker-compose down -v |
| 28-1 | AC5 `tenants.Status` CHECK constraint | REAL GAP | `Status` column is `character varying(32) NULL`; no CHECK enumerates the 8 valid states |
| 28-1 | AC5 `KekVersion` `smallint NOT NULL DEFAULT 1` | SPEC DIVERGENCE | shipped as `integer NULL` (no default, no smallint, nullable) |
| 28-1 | AC5 `EncryptedConnectionString` partial CHECK | REAL GAP | column is `bytea NULL`; no partial CHECK guarding `Status='pending_verification' OR EncryptedConnectionString IS NOT NULL` |
| 28-3 | AC3 release-build-throws on stub resolver | REAL GAP | `StubTenantConnectionResolver` is always registered via `TryAddSingleton`; production override depends entirely on operator setting `ConnectionStrings:ControlPlane`. A misconfigured prod build silently uses the stub. |
| 28-4 | AC5 metric names | SPEC DIVERGENCE | shipped names (`tamma.tenant_pools.opened_total`, `…evicted_total`, `…warm`, `…cache_hit_ratio`) differ from spec (`tamma_tenant_pool_hits_total{tenant_id}`); no `tenant_id` tag |
| 28-4 | AC6 envelope byte layout `[0x01][slot][12 nonce][ct][16 tag]` | SPEC DIVERGENCE | shipped layout is `[12 nonce][ct][16 tag]` — no `[0x01]` version byte, no `[slot]` byte. Slot routing is performed via the separate `KekVersion` int column on the row. |
| 28-5 | AC4 step C `pg_dump` backup behind `Backup:DeletionBackup=true` | REAL GAP | not implemented in `DropTenantDatabaseActivity` or anywhere else |
| 28-5 | AC4 step D explicit `pg_terminate_backend` | SPEC DIVERGENCE | shipped uses `DROP DATABASE … WITH (FORCE)` which kicks lingering backends; no separate `pg_terminate_backend` step |
| 28-5 | AC4 5-minute cooling-off window | REAL GAP | `TenantCleanupRequestedTriggerOptions` has `Enabled` + `PollInterval` (2s default) — no cooling-off window concept |
| 28-7 | `platform_api_key_index` primary lookup | VERIFIED DONE | `ApiKeyAuthHandler.ResolveApiKeyForPrefixedAsync` uses index as **fast path**, falls back to legacy SHA-256 `KeyHash` lookup; consistent with story doc |
| 28-9 | AC1 `tenantSlug` claim emit | REAL GAP | not emitted; `JwtService` emits `tenantId` + `active_tenant_id` but no slug claim |
| 28-9 | AC1 `jti` claim emit | VERIFIED DONE | emitted at `JwtService.cs:142` as `JwtRegisteredClaimNames.Jti` |
| 28-9 | AC2 5-step atomicity (transaction) | REAL GAP | `SwitchOrg` handler in `AuthEndpoints.cs:969` runs 5 sequential async calls with NO surrounding transaction; no `SELECT … FOR UPDATE`; tests don't assert atomicity |
| 28-9 | AC6 `/auth/logout?all=true` revocation | VERIFIED DONE | implemented in `AuthEndpoints.cs:692-712` with per-user rate limit + `RevokeAllForUserAsync(LogoutAll)` + audit event |
| 28-10 | Per-tenant metric coverage (AC2: 8 keys) | SPEC DIVERGENCE | shipped 7 fact-table columns (workflowsStarted/Completed/Failed, agentDispatches, tokensIn, tokensOut, costUsd) instead of 8 MetricKey/Tags rows. Missing: `issues.created`, `api.requests{class}`, `api.errors_5xx{class}`. Different data model (columns vs rows). |
| 28-10 | Platform-wide metric coverage (AC3: 6 keys) | SPEC DIVERGENCE | shipped 2 metrics (agentDispatches, activeTenantsAtHourEnd); missing `tenants.provisioned.success/failed`, `tenants.deleted`, `tenants.active{plan}`, `auth.logins.success/failed{reason}` |
| 28-10 | 13-month retention sweeper `PURGE_ANALYTICS_HOURLY` | REAL GAP | no file matches; no retention workflow exists in `Tamma.ElsaServer/` or `Tamma.Activities/` |
| 28-10 | 1k/5k/10k idle-orchestrator benchmark | NEEDS USER DECISION | `Tamma.Benchmarks/OrchestratorIdleTenantBench.cs` does not exist; benchmark deferred (no spike doc found) |
| 28-11 | AC2 `resourceSummary` 24h analytics join | REAL GAP | `GetTenantDetail` at `AdminTenantsEndpoints.cs:230` returns item + 100 events + actions; no `resourceSummary` field, no join to `platform_analytics_hourly` |
| 28-11 | AC3 SSE fallback / long-poll | REAL GAP | `AdminTenantEventsSseEndpoint` has no `?fallback=poll` query param; pure SSE only |
| 28-12 | AC2 `postgres-roles-lint.yml` CI workflow | REAL GAP | `.github/workflows/` has 15 workflows; none is roles-lint |
| 28-12 | AC2 API startup `SELECT current_user` assertion | REAL GAP | no occurrence of `current_user` assertion or `tamma_provisioner` rejection in `Tamma.Api/Program.cs` or any health-check |
| 28-12 | AC1 split-role enforcement at compose level | REAL GAP | `docker-compose.prod.yml` adds only resource limits; the single Username slot in `docker-compose.yml` defaults to `${POSTGRES_USER:-tamma}` for both `TammaDb` (admin/migrations) and the rest. The `TammaAppDb` slot exists but is the only role separation; no `tamma_admin` / `tamma_provisioner` / `tamma_app` three-way split is wired |
| 28-12 | AC5 `RekeyTenantConnectionStringsWorkflow.cs` | SPEC DIVERGENCE | file does not exist; rotation is driven entirely by `KekRotationCoordinator` (background HostedService) — coordinator-instead-of-workflow architecture |
| 28-12 | AC5 `tamma_kek_rotation_remaining_gauge` metric | REAL GAP | grep finds no matching meter/gauge name in `Tamma.Api/Services/Secrets/` |

## Per-residual detail

### 28-1 AC2 — bootstrap script for shared DBs
**Verdict:** REAL GAP
**Evidence:** `scripts/db/docker-entrypoint-bootstrap.sh` exists (Story 28-12 commit) but only runs `postgres-roles.sql` (creates 3 roles). It does NOT create `tamma_control` + `tamma_global_elsa` databases, does NOT run CP migrations, does NOT run global-Elsa migrations. The AC requires all three. `docker/init-db.sql` only creates `elsa` + `tamma` schemas (lines 16-17) and grants. `apps/tamma-elsa/scripts/init-db.sql` is the legacy TS engine schema. No `bootstrap-shared-dbs.{sh,ps1}` exists.
**Recommendation:** Ship a `scripts/db/bootstrap-shared-dbs.sh` that: (a) creates the two DBs idempotently, (b) shells into `dotnet ef database update` for CP context, (c) shells into the global-Elsa migrations entrypoint. Update `docker-compose.yml` to invoke as a one-shot `db-bootstrap` service before `api` + `elsa-server`.

### 28-1 AC3 — reset-all script
**Verdict:** REAL GAP
**Evidence:** `ls /home/meywd/tamma/scripts/db/` shows `docker-entrypoint-bootstrap.sh`, `postgres-roles.sql`, `test-no-argv-leak.sh`. No `reset-all.{sh,ps1}`.
**Recommendation:** Either ship the script (DROP DATABASE x2 + invoke bootstrap) or update story AC3 to point at `docker compose down -v && docker compose up -d` as the canonical reset.

### 28-1 AC5 — tenants.Status CHECK constraint
**Verdict:** REAL GAP
**Evidence:** `apps/tamma-elsa/src/Tamma.Data/Migrations/20260422100000_AddPlansAndPlatformTables.cs:148-152` adds `Status` column as `character varying(32) NULL`. No `AddCheckConstraint` exists anywhere across CP migrations restricting the 8 valid values from Doc 01 §10.2. (`grep -rn "AddCheckConstraint" Migrations/ControlPlane/` returns only the Story 28-9 refresh-token binding.)
**Recommendation:** Add a CP migration with `ALTER TABLE tenants ADD CONSTRAINT ck_tenants_status CHECK ("Status" IN ('pending_verification','provisioning','active','delete_requested','deleting','deleted','failed','suspended'))`.

### 28-1 AC5 — KekVersion column shape
**Verdict:** SPEC DIVERGENCE
**Evidence:** `20260422100000_AddPlansAndPlatformTables.cs` adds `KekVersion` as `integer nullable`. `TammaModelConfiguration.cs:206` declares `Property<int?>("KekVersion")`. Spec says `smallint NOT NULL DEFAULT 1`. Storage is wider + nullable; functionally compatible because the decryptor treats null as "legacy heuristic" (see `AesGcmConnectionStringDecryptor.cs:65-117`).
**Recommendation:** Update story AC to match implementation (`int? NULL` with legacy-row tolerance) OR migrate to `smallint NOT NULL DEFAULT 1` + backfill nulls if the smaller column matters.

### 28-1 AC5 — EncryptedConnectionString partial CHECK
**Verdict:** REAL GAP
**Evidence:** `20260422100000_AddPlansAndPlatformTables.cs` adds `EncryptedConnectionString` as `bytea NULL`. No partial CHECK exists. AC required `CHECK (Status='pending_verification' OR EncryptedConnectionString IS NOT NULL)`.
**Recommendation:** Add migration with the partial CHECK; pairs naturally with the Status CHECK.

### 28-3 AC3 — release-build-throws on stub resolver
**Verdict:** REAL GAP
**Evidence:** `Tamma.Data/DependencyInjection.cs:100-104` registers `StubTenantConnectionResolver` via `TryAddSingleton` unconditionally — no `#if DEBUG` guard, no environment check. `Tamma.Api/Program.cs:251` only calls `AddTenantConnectionPool` when `ConnectionStrings:ControlPlane` is non-empty; the else branch (line 277-284) logs Info and continues. A production deployment that forgets to set the CP connection string silently runs on the stub.
**Recommendation:** Add a release-build assertion in `Program.cs` (or `AddTammaData`) that throws if the resolver is still `StubTenantConnectionResolver` after composition completes AND `IHostEnvironment.IsProduction()`. Alternatively, gate the stub registration on `IsDevelopment()`.

### 28-4 AC5 — metric names
**Verdict:** SPEC DIVERGENCE
**Evidence:** `Tamma.Data/Pooling/TenantConnectionPoolMetrics.cs` exposes 4 OTel metrics:
- `tamma.tenant_pools.opened_total` (counter)
- `tamma.tenant_pools.evicted_total` (counter, tagged by `reason`)
- `tamma.tenant_pools.warm` (gauge)
- `tamma.tenant_pools.cache_hit_ratio` (gauge)

Spec mentioned `tamma_tenant_pool_hits_total{tenant_id}` etc. (underscored, per-tenant tag). Shipped names use dots (OTel convention) and have NO per-tenant tag (deliberately — high cardinality with hundreds of tenants would blow up the meter).
**Recommendation:** Update story AC to match — dot-style names + ratio gauge (no per-tenant tag) is the right shape for a multi-tenant pool. Document the cardinality reason.

### 28-4 AC6 — AES-GCM envelope byte layout
**Verdict:** SPEC DIVERGENCE
**Evidence:** `Tamma.Api/Services/Provisioning/TenantSecretProtector.cs:154-175` writes envelope as `[12-byte nonce] ‖ [ciphertext] ‖ [16-byte tag]`. There is NO `[0x01][slot]` prefix. Slot routing is performed on the **row** via the separate `KekVersion` int column, looked up by `KekProvider.GetByVersion` in `AesGcmConnectionStringDecryptor.cs:82-107`. The R2-H13 fix doc-block explicitly explains the post-multi-rotation design.
**Recommendation:** Update story AC to match. The row-column approach is operationally simpler (KekVersion bumps don't require envelope rewrites for migration tooling). Document this decision in the Doc 04 §4.3 update.

### 28-5 AC4 step C — pg_dump backup behind `Backup:DeletionBackup=true`
**Verdict:** REAL GAP
**Evidence:** `DropTenantDatabaseActivity.cs` (full file) probes existence then issues `DROP DATABASE … WITH (FORCE)`. No `pg_dump` invocation, no `Backup:` config section anywhere in this activity.
**Recommendation:** Either implement (shell out to `pg_dump`, write to `Backup:Destination` per config) or document the deferral as "not in scope until first paying tenant requests soft-delete recovery".

### 28-5 AC4 step D — explicit pg_terminate_backend
**Verdict:** SPEC DIVERGENCE
**Evidence:** `DropTenantDatabaseActivity.cs:54-58` deliberately relies on Postgres 17's `WITH (FORCE)` clause to kick lingering backends. Doc-comment line 13-14 explicitly notes this "saves us a separate `pg_terminate_backend` step on the happy path".
**Recommendation:** Update story AC to match. `WITH (FORCE)` is the modern equivalent and atomically guarantees no race between terminate and drop.

### 28-5 AC4 cooling-off window
**Verdict:** REAL GAP
**Evidence:** `TenantCleanupRequestedTriggerOptions` in `TenantCleanupRequestedTrigger.cs:16-34` only carries `Enabled` (bool, default true) + `PollInterval` (TimeSpan, default 2 seconds). No `CoolingOffWindow`, no 5-minute delay anywhere in the workflow chain. The trigger fires as soon as the event row is visible.
**Recommendation:** Decide if the 5-minute window is required (it gives the operator a chance to cancel an accidental delete). If yes, add a `Delay` activity at the head of `DeleteTenantWorkflow` (or before publishing the trigger event) controlled by a `TenantCleanupTrigger:CoolingOffWindow` option, default 5 minutes. If no, update story AC.

### 28-7 — platform_api_key_index role
**Verdict:** VERIFIED DONE
**Evidence:** `ApiKeyAuthHandler.cs:397-434` (`ResolveApiKeyForPrefixedAsync`):
- Line 403-419: **fast path** queries `IPlatformApiKeyIndexRepository.GetByPrefixAndSuffixAsync(prefix, suffixHash)` first
- Line 423-431: **fallback** to legacy SHA-256 `KeyHash` lookup for pre-Epic-28 rows

Tenant id is also encoded in the prefix (`tamma_sk_t_<base32-tid>_<random>`) per `ApiKeyPrefixParser.cs:11` for routing purposes; the index is what resolves the actual key row. Both seams co-exist as designed.
**Recommendation:** No action. Story doc audit note (line 122-123) can be closed.

### 28-9 AC1 — tenantSlug claim
**Verdict:** REAL GAP
**Evidence:** `JwtService.cs` lines 121-191 — full claim list contains `sub`, `tenantId`, `active_tenant_id`, `role`, `platformRole`, `email`, `name`, `authMethod`, `jti`, `iat`, `tenants` (JSON), plus impersonation extras. No `tenantSlug` or `active_tenant_slug`. Confirmed by `grep -n "Slug\|slug"` returning zero matches in JwtService.
**Recommendation:** Either add `new("tenantSlug", tenant?.Slug ?? "")` to the claim list (requires plumbing the slug into `GenerateAccessToken`) or update story AC to drop the claim. The `tenants` JSON claim already carries `{tenantId, role}`; could be widened to include slug.

### 28-9 AC1 — jti claim
**Verdict:** VERIFIED DONE
**Evidence:** `JwtService.cs:142` — `new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())`.
**Recommendation:** No action; verified.

### 28-9 AC2 — 5-step atomicity
**Verdict:** REAL GAP
**Evidence:** `AuthEndpoints.cs:969-1089` `SwitchOrg` runs:
1. `PersistActiveTenantAsync` (line 1013)
2. `RevokeAsync` OR `RevokeAllForUserAsync` (lines 1033, 1046)
3. `CreateAsync` new refresh token (line 1058)
4. `GenerateAccessToken` (line 1066)
5. `WriteSession` cookie + `PublishOrgSwitchedEventAsync` (lines 1074, 1079)

NO `BeginTransaction` / `UseTransaction` wraps these. NO `SELECT … FOR UPDATE` on the user's current refresh_tokens row (AC2 line 89-93 required it for concurrent-call serialisation). `grep -n Transaction AuthEndpoints.cs` returns zero matches. `SwitchOrgEndpointTests.cs` has no `Transaction|atomic` test.
**Recommendation:** Wrap the CP mutations in a single `await db.Database.BeginTransactionAsync()` so a mid-call crash doesn't leave a half-rotated session. Add the `FOR UPDATE` lock on the user's last active refresh token to serialise concurrent switch-org calls. Add a regression test asserting atomicity.

### 28-9 AC6 — /auth/logout?all=true
**Verdict:** VERIFIED DONE
**Evidence:** `AuthEndpoints.cs:656` `Logout` handler:
- Lines 672-712: detects `?all=true`, applies per-user rate limit (3/hour), calls `RevokeAllForUserAsync(userId, RefreshTokenRevokedReasons.LogoutAll)`, records audit event via `PublishLogoutAllEventAsync` (line 712).
- Falls back to per-token revocation when `all=true` absent (line 736).
**Recommendation:** No action; verified. NB: the AC also wanted `token_revocations` rows for in-flight admin token invalidation within 1 minute — that piece (separate from refresh revocation) is worth a follow-up scan but is sibling-agent scope (AC3 follow-ups).

### 28-10 — per-tenant metric coverage (AC2: 8 keys)
**Verdict:** SPEC DIVERGENCE
**Evidence:** `ComputeTenantRollupActivity.cs:160-188` writes to a **wide-row fact table** `PlatformAnalyticsHourly` with named columns:
- `WorkflowsStarted` ← `WorkflowInstances` count
- `WorkflowsCompleted` ← count where Status='completed'
- `WorkflowsFailed` ← count where Status='failed'
- `AgentDispatches` ← `DomainEvents` where Type LIKE 'AGENT.DISPATCH.%'
- `TokensIn`, `TokensOut`, `CostUsd` ← parsed from `LLM.CALL.SUCCESS` data JSON

Spec AC2 listed **8 metric keys** with tag breakouts in a long-narrow MetricKey/Tags table (`issues.created`, `workflows.executed{workflowName}`, `workflows.failed{workflowName}`, `llm.tokens.input{provider,model}`, `llm.tokens.output{provider,model}`, `llm.cost_usd{provider,model}`, `api.requests{endpoint_class}`, `api.errors_5xx{endpoint_class}`).

Coverage delta:
- Implemented: workflows.* (3 of 3), llm tokens + cost (3 of 3 — no provider/model tag)
- Missing entirely: `issues.created`, `api.requests{class}`, `api.errors_5xx{class}`
- Missing tag dimensions: `workflowName`, `provider`, `model`, `endpoint_class`

The data model itself diverged: spec was MetricKey-per-row, ship is fact-table columns. This is a foundational architecture choice; the column shape can never express the tag breakouts spec required.
**Recommendation:** Decide: (a) accept the column shape, drop the tag breakouts, update spec to match the simpler model + add `issues.created`, `api.requests`, `api.errors_5xx` columns; or (b) migrate to a long-narrow MetricKey table to honour the original spec. Option (a) is much smaller and probably right given the cardinality risk of (provider,model) tag combinations.

### 28-10 — platform-wide metric coverage (AC3: 6 keys)
**Verdict:** SPEC DIVERGENCE
**Evidence:** `ComputePlatformRollupActivity.cs:107-142` writes a single fact-table row per hour with `TenantId=NULL` carrying:
- `AgentDispatches` ← `PlatformEvents` LIKE 'AGENT.DISPATCH.%'
- `ActiveTenantsAtHourEnd` ← `Tenants.Count(DeletedAt=null AND CreatedAt < hourEnd)`
- All other fact-table columns zeroed

Spec AC3 listed 6 metrics: `tenants.provisioned.success`, `tenants.provisioned.failed{failure_reason}`, `tenants.deleted`, `tenants.active{plan}`, `auth.logins.success`, `auth.logins.failed{reason}`. Only `tenants.active` partially shipped (no per-plan tag). The other 5 metrics are **missing entirely**.
**Recommendation:** Same architecture decision as AC2. If sticking with fact-table shape, add `TenantsProvisionedSuccess`, `TenantsProvisionedFailed`, `TenantsDeleted`, `LoginsSuccess`, `LoginsFailed` columns. If returning to MetricKey shape, redesign.

### 28-10 — 13-month retention sweeper
**Verdict:** REAL GAP
**Evidence:** `grep -rn "PURGE_ANALYTICS_HOURLY|13.month|AnalyticsRetention|RetentionWorkflow" Tamma.ElsaServer/ Tamma.Activities/` returns zero. No workflow, no scheduled task, no SQL purge command.
**Recommendation:** Add a `PurgeStaleAnalyticsWorkflow` cron weekly that does `DELETE FROM platform_analytics_hourly WHERE Hour < now() - interval '13 months'`. Cheap, low-risk.

### 28-10 — 1k/5k/10k idle-orchestrator benchmark
**Verdict:** NEEDS USER DECISION
**Evidence:** `Tamma.Benchmarks/OrchestratorIdleTenantBench.cs` does not exist. No spike doc in `.dev/spikes/` matches the bench. The impl plan (28-10 impl-plan §37) named it explicitly; the implementation deferred it.
**Recommendation:** USER DECISION — was this benchmark intentionally deferred to a production-scale gate (Story 30), or should it ship as part of 28-10 closure? If deferred, capture the deferral in the story doc.

### 28-11 AC2 — resourceSummary 24h analytics
**Verdict:** REAL GAP
**Evidence:** `AdminTenantsEndpoints.cs:230-255` `GetTenantDetail` returns `AdminTenantDetailResponse(item, events, actions)`. Zero matches for `resourceSummary` or `ResourceSummary` across the entire `apps/tamma-elsa/src/` tree. Dashboard page also has no consumer for it. Story AC explicitly requires keys `workflowsLast24h`, `llmCostLast24h` joined from `platform_analytics_hourly`.
**Recommendation:** Add a `ResourceSummary` record + project from `PlatformAnalyticsHourly` filtered to last 24 hours for the tenant; include in the `GetTenantDetail` response. Dashboard page should render the panel (audit row at AC5 §164 confirms).

### 28-11 AC3 — SSE fallback / long-poll
**Verdict:** REAL GAP
**Evidence:** `AdminTenantEventsSseEndpoint.cs` (526 lines) implements pure SSE with `text/event-stream` content-type, keepalive comments, Last-Event-ID resume. NO `?fallback=poll` query param handling; `grep -in "fallback|long.poll|polling"` returns only one hit (line 169 `X-Accel-Buffering: no` — nginx hint, unrelated).
**Recommendation:** Either implement `?fallback=poll` mode returning a snapshot batch (probably reusing the AC2 events list) or update story AC to drop the fallback. The dashboard's TenantDetailPage would need a corresponding feature flag.

### 28-12 AC2 — postgres-roles-lint.yml CI workflow
**Verdict:** REAL GAP
**Evidence:** `ls .github/workflows/` shows 15 workflows; none is `postgres-roles-lint.yml`. No CI lints the `postgres-roles.sql` script for syntax/grant correctness in a throwaway container.
**Recommendation:** Add a `.github/workflows/postgres-roles-lint.yml` that spins up a transient postgres:17 container, applies `postgres-roles.sql`, queries `pg_roles` + `information_schema.role_table_grants` to assert each role has the expected privilege set.

### 28-12 AC2 — API startup current_user assertion
**Verdict:** REAL GAP
**Evidence:** `grep -rn "current_user|tamma_provisioner" Tamma.Api/Program.cs` returns nothing relevant. No startup health-check asserts the API pod is connecting as `tamma_app` (not `tamma_provisioner`). The only place `tamma_provisioner` is referenced for role-aware logic is `Tamma.Api/Services/Secrets/Handlers/RoleWhitelist.cs:30` — that's the whitelist for self-rotation, not a runtime current_user check.
**Recommendation:** Add a `IHostedService` (or extend `KekCabinetHealthCheck`) that on startup runs `SELECT current_user` against the app-DB connection and throws if the result is in `{ 'tamma_provisioner', 'postgres', 'tamma' }` (i.e. anything more privileged than `tamma_app`).

### 28-12 AC1 — split-role enforcement at compose level
**Verdict:** REAL GAP
**Evidence:** `docker-compose.yml:120,174,184` configures `ConnectionStrings__DefaultConnection` and `__TammaDb` both as `Username=${POSTGRES_USER:-tamma}` (admin role); only `__TammaAppDb` (line 185) uses `Username=tamma_app`. The three-way split (`tamma_admin` for DDL/migrations, `tamma_provisioner` for tenant CRUD on the CP, `tamma_app` for per-request) is NOT enforced at compose level — the API pod has all three URLs slotted into the **same default tamma user** for two of them. `docker-compose.prod.yml` adds only resource limits.
**Recommendation:** Update `docker-compose.yml` / `docker-compose.prod.yml` to slot three distinct URLs: `__TammaAdminDb` → tamma_admin, `__TammaProvisionerDb` → tamma_provisioner, `__TammaAppDb` → tamma_app. Add the env vars to `.env.example`. Coordinate with `AddTammaData` to plumb the new connection-string keys.

### 28-12 AC5 — RekeyTenantConnectionStringsWorkflow location
**Verdict:** SPEC DIVERGENCE
**Evidence:** `find / -name "RekeyTenantConnectionStringsWorkflow*"` returns nothing. The rotation is owned by `KekRotationCoordinator` (background HostedService) in `Tamma.Api/Services/Secrets/`; the design diverged from a workflow-orchestrated rekey to an in-process coordinator. Per the audit's residual note, "coordinator-instead-of-workflow may be the new architecture".
**Recommendation:** Confirm coordinator-only is the intended end-state (it makes sense for an operator-driven, idempotent, advisory-lock-protected rotation that doesn't need durable workflow semantics). Update story AC5 + Doc 04 §4 to point at `KekRotationCoordinator.cs` and `KekRotationEndpoints.cs` (`/api/admin/kek/rotate/*`).

### 28-12 AC5 — tamma_kek_rotation_remaining_gauge metric
**Verdict:** REAL GAP
**Evidence:** `grep -rn "tamma_kek_rotation_remaining|RotationRemaining|Meter|Gauge|Observable" Tamma.Api/Services/Secrets/` returns nothing. `KekRotationCoordinator.cs` has no Meter wiring. The coordinator exposes status via `GET /api/admin/kek/rotate/status` (REST) but does not emit an OTel gauge for "remaining tenants to rekey".
**Recommendation:** Add a `Meter("Tamma.KekRotation").CreateObservableGauge("tamma.kek_rotation.remaining")` reading from coordinator state. Useful for the operator dashboard to track rotation progress without polling the REST endpoint.

## Recommended follow-ups

### Update story docs to match implementation (SPEC DIVERGENCE — preferred)
- **28-1 AC5** KekVersion column shape — accept `integer NULL`, drop smallint requirement; document legacy-row tolerance via `KekVersion=null` heuristic path
- **28-4 AC5** metric names — update spec to dot-style + no per-tenant tag (cardinality reasoning)
- **28-4 AC6** envelope layout — update Doc 04 §4.3 to row-column slot routing (no `[0x01][slot]` envelope prefix)
- **28-5 AC4 step D** — accept `DROP DATABASE WITH (FORCE)` as the equivalent of `pg_terminate_backend` + DROP
- **28-12 AC5** RekeyTenantConnectionStringsWorkflow — update story + Doc 04 §4 to coordinator-instead-of-workflow; point at `KekRotationCoordinator` + REST endpoints

### Real gaps requiring code work (REAL GAP — prioritised)
1. **28-9 AC2** transaction wrap of SwitchOrg + `FOR UPDATE` lock — security-relevant (refresh-token leak between concurrent calls); ~50 LoC + 1 regression test
2. **28-3 AC3** release-build assertion against `StubTenantConnectionResolver` — silent prod misconfig is a high-severity ops risk; ~20 LoC in Program.cs + 1 startup test
3. **28-12 AC1** docker-compose split-role enforcement — fix matches the AC1 design intent and unlocks the AC2 startup assertion; ~30 lines compose + .env.example + minor `AddTammaData` plumbing
4. **28-1 AC5** Status + EncryptedConnectionString CHECK constraints — defends against type-erasure bugs; ~20 LoC migration
5. **28-11 AC2** resourceSummary join — surfaces 24h analytics in admin UI as designed; ~40 LoC + dashboard panel
6. **28-12 AC2** postgres-roles-lint.yml CI + startup current_user check — together they keep the three-way role split honest; ~60 LoC workflow + ~20 LoC startup check
7. **28-1 AC2/AC3** bootstrap + reset scripts — devex / CI gap; ~100 LoC of shell
8. **28-12 AC5** kek_rotation_remaining gauge — operator visibility; ~10 LoC meter wiring
9. **28-10 retention sweeper** — prevents unbounded growth of `platform_analytics_hourly`; ~30 LoC workflow + 1 cron registration
10. **28-9 AC1** tenantSlug claim — UI display nicety; ~5 LoC if slug is in scope, ~20 LoC if it requires plumbing
11. **28-5 AC4 backup + cooling-off** — only needed if/when SLA promises soft-delete recovery
12. **28-11 AC3** SSE fallback `?fallback=poll` — only needed if a dashboard env can't open SSE

### User decisions needed
1. **28-10 metric model** — accept the wide-row fact table (faster, simpler) OR migrate to the long-narrow MetricKey/Tags table the spec required (full coverage of `issues.created`, `api.requests{class}`, `api.errors_5xx{class}`, `tenants.provisioned.*`, `auth.logins.*` with tag breakouts)? This is the single biggest architectural divergence in the audit and shapes 28-10 AC2/AC3 closure.
2. **28-10 benchmark** — was the 1k/5k/10k idle-orchestrator benchmark intentionally deferred to a Story 30 production-scale gate, or is it required for 28-10 closure?
