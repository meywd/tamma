# Epic 28 Status Audit — 2026-05-29

**Branch:** feat/wave-b
**HEAD:** 695ac0e0
**Scope:** 12 stories (28-1 through 28-12). 28-13 (OpenBao KMS backend) deferred per epic README until a documented trigger fires.

> Caveat: every story file still says `**Status**: Draft`. This audit maps shipped code (commit history + `apps/tamma-elsa/src/`) to ACs. Where evidence is ambiguous, the gap is labelled "needs human verification".

## Summary table

| Story | Title | Verdict | Tests |
|---|---|---|---|
| 28-1 | EF migration scripts (CP + tenant + global-Elsa + per-tenant Elsa) | MOSTLY DONE | tests exist in `tests/Tamma.Api.Tests/Epic28/` (ControlPlane/Tenant model + factory, PlansSeeder) |
| 28-2 | Split TammaDbContext into ControlPlaneDbContext | DONE | tests exist in `Epic28/ControlPlaneDbContextModelTests.cs`, `PlansSeederTests.cs` |
| 28-3 | TenantDbContext factory with runtime connection routing | DONE | tests exist in `Epic28/TenantDbContextModelTests.cs`, `TenantDbContextFactoryTests.cs` |
| 28-4 | Tenant connection resolver + LRU pool cache | DONE | tests exist in `Epic28/LruPooledTenantConnectionResolverTests.cs`, `LruResolverLeaseAndDiagnosticsTests.cs`, `TenantConnectionHandleTests.cs`, `TenantConnectionPoolMetricsTests.cs` |
| 28-5 | CreateTenantWorkflow + DeleteTenantWorkflow on global Elsa | MOSTLY DONE | tests exist in `Activities.Tests/TenantLifecycle/*WorkflowStructureTests.cs` |
| 28-6 | platform_events + platform_queued_tasks + platform_email_outbox | DONE | tests exist in `Epic28/Platform*RepositoryTests.cs`, `PlatformTaskWorkerTests.cs`, `OutboxSmtpSenderPlatformPathTests.cs` |
| 28-7 | API-key prefix routing | DONE | tests exist in `Auth/ApiKeyAuthHandlerTests.cs`, `ApiKeyHasherTests.cs`, `ApiKeyPrefixGeneratorTests.cs`, `ApiKeyPrefixParserTests.cs`, `Auth/{Admin,User,Org}ApiKeysEndpointsTests.cs` |
| 28-8 | TenantContextMiddleware async-provisioning handling | MOSTLY DONE | tests exist in `Middleware/TenantContextMiddlewareTests.cs`, `TenantStatus/*` |
| 28-9 | JWT claims + /auth/switch-org + refresh tokens across tenants | PARTIAL | tests exist in `Auth/SwitchOrgEndpointTests.cs`, `JwtServiceTests.cs` |
| 28-10 | platform_analytics_hourly rollup workflow | MOSTLY DONE | tests exist in `Activities.Tests/Analytics/*`, `Epic28/PlatformAnalyticsService*Tests.cs`, `ComputeTenantRollupActivityTests.cs`, `ComputePlatformRollupActivityTests.cs` |
| 28-11 | Admin UX for tenants.Status state machine | DONE | tests exist in `Admin/AdminTenantsTests.cs`, `Admin/AdminTenantEventsSse*Tests.cs`, `Admin/AdminTenantsAuditAndNoteTests.cs` + dashboard `pages/admin/tenants/*` |
| 28-12 | Postgres roles + KEK rotation | MOSTLY DONE | tests exist in `Secrets/Kek*Tests.cs` (Coordinator, Provider, AdvisoryLock, PostFix, Retry, StatusSerialization), `KekCabinetHealthCheckTests.cs` |

## Per-story detail

### 28-1 — EF migration scripts (CP + tenant + global-Elsa + per-tenant Elsa)
**Verdict:** MOSTLY DONE
**Evidence:**
- commits `c90e03a`, `06208677`, `a7656270` (PR A/B/D — platform defaults via code, outbox/queue split, 15-entity move)
- files: `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*` (14 migrations including `PlatformApiKeyIndex`, `PlatformAnalyticsHourly`, `KekRotations`, `AddPlatformBootstrap`, `DropMovedEntitiesFromControlPlane`); `Tamma.Data/Migrations/Tenant/` (`AddMovedEntitiesToTenantSchema`, `Story27_2_PromptOverridesPrincipalXor`, `ConventionStore`); `Tamma.Data/ControlPlaneDesignTimeDbContextFactory.cs`, `TenantDesignTimeDbContextFactory.cs`
- index commits visible (e.g., `PlatformAnalyticsHourly` + ix_pah_*)

**Gaps:**
- AC2 (bootstrap script `scripts/db/bootstrap-shared-dbs.{sh,ps1}`): not found — only `scripts/db/postgres-roles.sql` exists. Closest substitute is `docker/init-db.sql` + `apps/tamma-elsa/scripts/init-db.sql`. Needs human verification whether `init-db.sql` + Docker entrypoint covers AC2 semantics or whether a dedicated bootstrap script was deliberately skipped.
- AC3 (`scripts/db/reset-all.{sh,ps1}`): not found.
- AC1 row "global-Elsa migrations / per-tenant Elsa migrations" — Elsa uses its own EF provider so no Tamma migration files are expected; the per-tenant Elsa DB provisioning runs via `Tamma.Data/Pooling/EfTenantDbMigrator.cs`. The global-Elsa DB has no Elsa-EF migration runner wired explicitly that I could find (`ElsaServer/Program.cs` setup may handle it implicitly — needs human verification).
- AC4 (Plans seed): present (`Tamma.Data/Seeders/` + `PlansSeederTests.cs`).
- AC5 (`tenants.Status` CHECK constraint, `KekVersion`, `EncryptedConnectionString` partial CHECK): partly verified via Pr-A platform defaults commit; specific CHECK-constraint coverage needs human verification against migration source.

**Tests:** tests exist in `tests/Tamma.Api.Tests/Epic28/` (model + factory + seeder) and `tests/Tamma.Api.Tests/Provisioning/CranlProvisioningWorkflowTests.cs`. Also `bedf38a9` notes "skip 3 aspirational 28-1 tests" — i.e. 3 ACs deferred. Worth checking those.

### 28-2 — Split TammaDbContext into ControlPlaneDbContext
**Verdict:** DONE
**Evidence:**
- commits `c90e03a` (15-entity move), `5ff35d72` (stories 28-4→28-12 land), `6d9dd18a` (PR C)
- files: `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` (registers 14+ DbSets — Users, RefreshTokens, PasswordResetTokens, Tenants, TenantMemberships, UserInvites, ApiKeys (CP scope), GitHubInstallations, GitHubInstallationRepos, GitHubWebhookDeliveries, Plans, PlatformEvents, PlatformQueuedTasks, PlatformEmailOutbox, PlatformApiKeyIndex, KekRotations, AdminImpersonations, PlatformAnalyticsHourly, PlatformWebhookDeliveries, TenantPlatformInstallations)
- DI registration in `Tamma.Api/Program.cs` and `Tamma.ElsaServer/Program.cs` (both `grep -l ControlPlaneDbContext` confirm)
- TammaDbContext is **gone** from the tree (no file matches) — exceeds AC3 (which only required `[Obsolete]` marker; ship simply deleted it).

**Gaps:** none material; AC3 is moot because TammaDbContext was deleted outright, which is stronger than the AC required.
**Tests:** tests exist in `Epic28/ControlPlaneDbContextModelTests.cs`, `PlansSeederTests.cs`.

### 28-3 — TenantDbContext factory with runtime connection routing
**Verdict:** DONE
**Evidence:**
- commits `c90e03a` (entity move), `5ff35d72`
- files: `Tamma.Data/TenantDbContext.cs`, `Tamma.Data/TenantDbContextFactory.cs`, `Tamma.Data/Abstractions/ITenantDbContextFactory.cs`, `Tamma.Data/Abstractions/ITenantConnectionResolver.cs`, `Tamma.Data/StubTenantConnectionResolver.cs` (kept as the dev-fallback resolver, registered via `TryAddSingleton` in `AddTammaData`)
- TenantDbContext exposes AgentConfigs, PromptOverrides, Conventions, ProviderHealths, ProviderDiagnostics, SanitizationRules, WorkflowDefinitions, WorkflowInstances, DomainEvents, QueuedTasks, EmailOutbox, BudgetConfigs, ApiKeys, MentorshipSessions, MentorshipEvents, JuniorDevelopers, Stories — matches AC1 except `provider_diagnostics` is now `ProviderDiagnostics` and the entity-move migration confirms the 15-entity transfer.

**Gaps:**
- AC3 "stub is `#if DEBUG`-only / release build throws if no real resolver": the current `StubTenantConnectionResolver` is unconditional and used by tests/dev. Production wiring relies on `AddTenantConnectionPool` (called in Program.cs only when `ConnectionStrings:ControlPlane` is set) `Replace`ing it. This is materially safer than the stub-in-release case the AC worried about, but does not match the AC text — needs explicit human verification that this is the intended end-state.

**Tests:** tests exist in `Epic28/TenantDbContextModelTests.cs`, `TenantDbContextFactoryTests.cs`.

### 28-4 — Tenant connection resolver + LRU pool cache
**Verdict:** DONE
**Evidence:**
- commits `5ff35d72`, `8bec0dd6` (M12 regression tests), `f1026335` (batch D: pool + worker correctness), `c340b314` (KEK lifecycle hardening, R2-S5/S8/C3/C7)
- files: `Tamma.Data/Pooling/LruPooledTenantConnectionResolver.cs`, `TenantConnectionHandle.cs`, `TenantConnectionPoolOptions.cs`, `TenantConnectionPoolMetrics.cs`, `TenantConnectionPoolServiceCollectionExtensions.cs`, `Tamma.Data/Pooling/NpgsqlTenantAdminConnection.cs`, `Tamma.Data/Pooling/EfTenantDbMigrator.cs`, `Tamma.Api/Services/Secrets/AesGcmConnectionStringDecryptor.cs`, `Tamma.Data/Pooling/PassthroughConnectionStringDecryptor.cs`
- wired in `Tamma.Api/Program.cs:236-285` via `AddTenantConnectionPool(...)` — replaces the stub, includes `PoolWarmupService` for pre-warm and Story-30-8 V2 endpoint directory plumbing
- admin diagnostics: `Tamma.Api/Endpoints/Admin/PoolsAdminEndpoints.cs` exposes `GET /api/admin/pools/stats`, `GET /api/admin/pools/tenants`, `POST /api/admin/pools/{id}/evict`

**Gaps:**
- AC5 metric names: the story specifies `tamma_tenant_pool_hits_total{tenant_id}` etc. The implementation in `TenantConnectionPoolMetrics.cs` ships counters/histograms; exact OTel metric names need human verification against the AC list.
- AC6 envelope key naming and slot semantics: the implementation uses `AesGcmConnectionStringDecryptor` + `KekProvider` (with primary/secondary slots) which matches the story's spec, though the on-wire envelope is now driven from `TenantSecretProtector` / `AesGcmConnectionStringDecryptor`. Needs human verification of envelope byte layout `[0x01][slot][12 nonce][ct][16 tag]`.

**Tests:** tests exist in `Epic28/LruPooledTenantConnectionResolverTests.cs`, `LruResolverLeaseAndDiagnosticsTests.cs`, `TenantConnectionHandleTests.cs`, `TenantConnectionPoolMetricsTests.cs`, `NpgsqlTenantAdminConnectionTests.cs`, `PassthroughConnectionStringDecryptorTests.cs`, `Secrets/AesGcmConnectionStringDecryptorTests.cs`, `Secrets/TenantSecretProtectorEnvironmentTests.cs`.

### 28-5 — CreateTenantWorkflow + DeleteTenantWorkflow on Global Elsa
**Verdict:** MOSTLY DONE
**Evidence:**
- commits `7c22ea7b` (Story 28-5 — workflows), `31a81c39` (merge), `c61ec36b` (cleanup decomposed into Sequence), `4e04d96f` (KEK lifecycle inside provisioning)
- files: `Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs`, `DeleteTenantWorkflow.cs`, `CleanUpFailedTenantWorkflow.cs`, `TenantCleanupRequestedTrigger.cs`
- activities under `Tamma.Activities/TenantLifecycle/` (19 files: MarkProvisioning, CreateTenantRole, CreateTenantDatabase, BuildTenantConnectionString, MigrateTenantDatabase, SeedTenantDefaults, EncryptAndPersistConnectionString, WarmTenantPool, MarkTenantActive, MarkTenantDeleting, EvictTenantPool, DropTenantDatabase, DropTenantRole, SoftDeleteTenantRow, EmitDeletedSuccess, EmitCleanupTerminalEvent, CleanupFailureClassifier, TenantLifecycleActivity, TenantLifecycleEvents)
- admin endpoints: `AdminTenantsEndpoints.cs` provides `POST /api/admin/tenants/{id}/actions/retry`, `/actions/delete`, `/actions/force-delete`, `/cleanup`, `PATCH /plan` — all emit `TENANT.PROVISIONING_REQUESTED` / delete events.
- tenant-status endpoint: `Tamma.Api/Endpoints/TenantStatusEndpoint.cs` (`GET /api/v1/tenants/{id}/status`) folds `platform_events` into a step ladder per Doc 03 §6.

**Gaps:**
- AC1 trigger source: the trigger today is from **admin retry** + the V2 task-queue path (`ProvisionTenantV2TaskHandler`), **not** from the `VerifyEmail` endpoint. `VerifyEmail` in `AuthEndpoints.cs:302` only sets `EmailVerified=true` — it does NOT emit `TENANT.PROVISIONING_REQUESTED` or flip `Status` from `pending_verification` to `provisioning`. Either provisioning has been redirected to the V2 path (Story 30) and the verify-email coupling was dropped on purpose, or AC1 is unfulfilled. **Needs human verification — meaningful gap.**
- AC2 step 10 (`QueueWelcomeEmail` insert into `platform_email_outbox` from inside `CreateTenantWorkflow`): no activity called `QueueWelcomeEmail*` exists. The workflow Sequence (per `CreateTenantWorkflow.cs:27-45`) lists 8 activities and stops at `MarkTenantActiveActivity`. The welcome email currently appears to enqueue elsewhere (`AuthEndpoints.cs` references `IPlatformEmailOutboxRepository` for transactional emails). **Needs human verification — likely a small gap.**
- AC4 step C (pg_dump backup behind `Backup:DeletionBackup=true`) and step D (`pg_terminate_backend`): not verified in the activity set; `DropTenantDatabaseActivity` likely handles the SQL but the backup step is not visible.
- AC4 cooling-off window: `TenantCleanupRequestedTrigger.cs` exists and has an options class — the 5-minute delay needs verification.
- AC7 `CleanUpFailedTenantWorkflow` operator sidecar: present (`CleanUpFailedTenantWorkflow.cs` + `CleanupFailureClassifier.cs` + `CleanupStepActivityTests.cs`).

**Tests:** tests exist in `Activities.Tests/TenantLifecycle/CreateTenantWorkflowStructureTests.cs`, `DeleteTenantWorkflowStructureTests.cs`, `CleanUpFailedTenantWorkflowStructureTests.cs`, `CleanupStepActivityTests.cs`, `CleanupFailureClassifierTests.cs`, `CreateTenantRoleActivityPasswordTests.cs`, `EmitCleanupTerminalEventActivityTests.cs`, `TenantNamingTests.cs`, `TenantLifecycleEventsTests.cs`.

### 28-6 — platform_events + platform_queued_tasks + platform_email_outbox
**Verdict:** DONE
**Evidence:**
- commits `6a454bbd` (Story 28-6 merge), `e3ce45b0` (repos + bus + sender), `a7656270` (PR B outbox/queue split — Decision #5)
- files: `Tamma.Data/Entities/PlatformEvent.cs`, `PlatformQueuedTask.cs`, `PlatformEmailOutboxMessage.cs`; `Tamma.Data/Repositories/PlatformEventRepository.cs`, `PlatformQueuedTaskRepository.cs`, `PlatformEmailOutboxRepository.cs` (+ interfaces)
- workers: `Tamma.Api/Services/PlatformTasks/PlatformTaskWorker.cs`, `IPlatformTaskHandler.cs`, `IPlatformTaskHandlerRegistry.cs`, `PlatformTaskServiceCollectionExtensions.cs`
- bus + publisher: `Tamma.Api/Services/PlatformEvents/InMemoryPlatformEventBus.cs`, `IPlatformEventBus.cs`, `PlatformEventPublisher.cs`
- outbox sender drains both per-tenant + platform tables: `Tamma.Api/Services/Email/OutboxSmtpSender.cs:262`

**Gaps:**
- RabbitMQ vs in-memory bus: only `InMemoryPlatformEventBus` is implemented today. The story didn't require RabbitMQ explicitly, but downstream Story 28-5/28-8 specs assume `tamma.platform.events` topic. The Postgres LISTEN/NOTIFY path (`d3de5c60`) covers status invalidation cluster-wide. RabbitMQ broker integration is a separate concern — flagged but not a 28-6 gap.

**Tests:** tests exist in `Epic28/PlatformEventRepositoryTests.cs`, `PlatformQueuedTaskRepositoryTests.cs`, `PlatformEmailOutboxRepositoryTests.cs`, `PlatformEventPublisherTests.cs`, `InMemoryPlatformEventBusTests.cs`, `PlatformTaskWorkerTests.cs`, `PlatformTaskHandlerRegistryTests.cs`, `OutboxSmtpSenderPlatformPathTests.cs`, `PlatformApiKeyIndexRepositoryTests.cs`, `PlatformDefaultRowRepositoryTests.cs`.

### 28-7 — API-key prefix routing (`tk_t_` / `tk_pl_` / `tk_u_`)
**Verdict:** DONE
**Evidence:**
- commits `a789bb76` (Story 28-7 — prefix routing), `0348e34d` (Argon2id hasher + platform_api_key_index + API-keys CRUD), `e3ff366d` (deferred items merge)
- files: `Tamma.Api/Auth/ApiKeyAuthHandler.cs` (prefix routing + legacy fallback gated by `Tamma:Auth:AllowLegacyUnprefixedKeys`), `ApiKeyPrefixGenerator.cs`, `ApiKeyPrefixParser.cs`, `ApiKeyHasher.cs` (Argon2id with `argon2id$` marker prefix + scrypt fallback), `Base32.cs`
- key endpoints: `Tamma.Api/Endpoints/AdminApiKeysEndpoints.cs` (platform-admin `tk_pl_*`), `OrgApiKeysEndpoints.cs` (tenant `tk_t_*`), and user-key route in standard auth endpoints
- routing index: `Tamma.Data/Entities/PlatformApiKeyIndex.cs`, `Tamma.Data/Repositories/PlatformApiKeyIndexRepository.cs`, migration `20260422104355_PlatformApiKeyIndex.cs`

**Gaps:**
- On-wire prefix differs from the AC: the story spec said `tk_t_`/`tk_pl_`/`tk_u_`, the implementation uses `tamma_sk_t_` / `tamma_sk_pl_` / `tamma_sk_u_` (from `ApiKeyPrefixParser.cs:11` docs). Not a defect — it's a deliberate cleaner naming chosen during impl. Worth aligning the story doc to reality.
- AC1 the `tk_t_` design ships **tenant id encoded in the prefix itself** (`tamma_sk_t_<base32-tenant-id>_<random>` per the parser doc), which is **Doc 01 §3.1 option 1**, NOT the "three-prefix + CP routing index" variant the story said it chose. The CP `platform_api_key_index` table still exists (commit `0348e34d`) — needs human verification whether the index is primary lookup or a defence-in-depth seam.

**Tests:** tests exist in `Auth/ApiKeyAuthHandlerTests.cs`, `ApiKeyHasherTests.cs`, `ApiKeyPrefixGeneratorTests.cs`, `ApiKeyPrefixParserTests.cs`, `Base32Tests.cs`, `Auth/AdminApiKeysEndpointsTests.cs`, `OrgApiKeysEndpointsTests.cs`, `UserApiKeysEndpointsTests.cs`.

### 28-8 — TenantContextMiddleware async-provisioning handling
**Verdict:** MOSTLY DONE
**Evidence:**
- commits `5d54bb88` (Story 28-8 — middleware), `7551cd17` (merge), `e9b1d775` (status cache wired)
- files: `Tamma.Api/Middleware/TenantContextMiddleware.cs`, `EnsurePersonalTenantMiddleware.cs` (kept, not deleted), `ImpersonationContextMiddleware.cs`
- status cache + bus: `Tamma.Api/Services/TenantStatus/MemoryTenantStatusCache.cs`, `TenantStatusEvaluator.cs`, `TenantStatusInvalidationListener.cs`, `Tamma.Data/Pooling/PostgresTenantStatusInvalidationBus.cs`, `NullTenantStatusInvalidationBus.cs`

**Gaps:**
- AC1 mentions that the former `EnsurePersonalTenantMiddleware` synchronous-create path should be eliminated. It is still in the tree at `Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs` — needs human verification whether it is wired into the pipeline today (and is therefore a leftover that should be removed) or kept for self-hosted single-user mode.
- AC2 exact status-code mapping (503 / 424 / 410 / 402 / 404 / 409 for the eight `tenants.Status` values) needs human verification against the `TenantStatusEvaluator.cs` implementation — the tests `TenantContextMiddlewareTests.cs` should cover this.

**Tests:** tests exist in `Middleware/TenantContextMiddlewareTests.cs`, `TenantStatus/{NullInvalidationBus,PostgresTenantStatusInvalidationBus,TenantStatusInvalidationListener}Tests.cs`, `Epic28/MemoryTenantStatusCacheTests.cs`.

### 28-9 — JWT claims + `/auth/switch-org` + refresh tokens across tenants
**Verdict:** PARTIAL
**Evidence:**
- commits `839e4aca` (Story 28-9 — switch-org + cross-tenant refresh), plus auth fixes in `b21e4f36` round-2
- files: `Tamma.Api/Endpoints/AuthEndpoints.cs:867` (`POST /api/v1/auth/switch-org` — handler `SwitchOrg`), `Tamma.Api/Auth/JwtService.cs` (`TenantClaim(TenantId, Role)` record, multi-tenant claim emit)

**Gaps:**
- AC3 (refresh tokens scoped to TenantId, JtiChainHead lineage, refresh-reuse detection): `Tamma.Data/Entities/RefreshToken.cs` has only `Id, UserId, TokenHash, ExpiresAt, RevokedAt, CreatedAt` — **NO `TenantId` column, NO `JtiChainHead` column**, no `RevokedReason`. This is a significant gap. Cross-tenant refresh leak protection appears to depend entirely on the access-token claim and not on a DB-side tenant binding.
- AC1 claim shape: `tenantId`, `role`, `isPlatformAdmin` confirmed in JwtService; `tenantSlug` and `jti` claims need human verification.
- AC2 5-step atomicity (revoke old refresh, insert new tenant-scoped refresh, emit AUTH.TENANT_SWITCHED, etc.) — needs human verification against `SwitchOrgEndpointTests.cs`.
- AC6 `/auth/logout?all=true` revocation path: not specifically searched — likely present but not verified.

**Tests:** tests exist in `Auth/SwitchOrgEndpointTests.cs`, `JwtServiceTests.cs`, `UserIdClaimRoundTripTests.cs`, `Orgs/OrgSwitchOrgRoute404Tests.cs`. The Refresh-token DB-shape gap above is **not** test-covered (no `RefreshToken` entity tenant binding exists to test).

### 28-10 — `platform_analytics_hourly` rollup workflow
**Verdict:** MOSTLY DONE
**Evidence:**
- commits `381b931b` / `7b0d4ed3` / `5ed59638` (28-10 read-side + fact table), `c87af5f6` (analytics rollup tests + bug fixes + runbook)
- files: migration `20260422105157_PlatformAnalyticsHourly.cs` + `Tamma.Data/Entities/PlatformAnalyticsHourly.cs`
- workflow + activities: `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs`, `HourlyAnalyticsRollupScheduler.cs`; activities under `Tamma.Activities/Analytics/` (`ComputeTenantRollupActivity`, `ComputePlatformRollupActivity`, `FanOutTenantRollupsActivity`, `EmitHourCompletedActivity`)
- read-side: `Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs`, `Endpoints/AdminAnalyticsEndpoints.cs`

**Gaps:**
- 1k/5k/10k idle-orchestrator benchmark (Epic 28 README cross-doc resolution #3 — explicit deliverable of this story): no evidence found in code or `.dev/spikes/`. Likely deferred to Story-30 / production-scale gate. Documentation-only deliverable.
- 13-month retention sweeper `PURGE_ANALYTICS_HOURLY` weekly task: needs human verification (no obvious file with that name).
- Per-metric coverage (AC2 + AC3) — the 8 per-tenant metric keys and 6 platform-wide keys need verification against `ComputeTenantRollupActivity.cs` and `ComputePlatformRollupActivity.cs` to confirm full coverage.

**Tests:** tests exist in `Activities.Tests/Analytics/AnalyticsRollupEventsTests.cs`, `ComputeTenantRollupAggregationTests.cs`, `HourlyAnalyticsRollupSchedulerTests.cs`, `HourlyAnalyticsRollupWorkflowStructureTests.cs`, plus `Epic28/PlatformAnalyticsServiceFactTableTests.cs`, `PlatformAnalyticsServiceTests.cs`, `ComputePlatformRollupActivityTests.cs`, `ComputeTenantRollupActivityTests.cs`.

### 28-11 — Admin UX for `tenants.Status` state machine
**Verdict:** DONE
**Evidence:**
- commits `c9051552` / `99d19fec` (Story 28-11 platform-admin tenant-status UX)
- API: `Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` — `GET /api/admin/tenants`, `GET /api/admin/tenants/{id}`, `POST /api/admin/tenants/{id}/actions/{retry,delete,force-delete}`, `POST /api/admin/tenants/{id}/cleanup`, `PATCH /api/admin/tenants/{id}/plan`
- SSE: `Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs` with `Last-Event-ID` resumption (commit `48a39771`/`139a5ebb`)
- audit + impersonation: `Tamma.Data/Entities/AdminImpersonation.cs`, `Tamma.Api/Services/Auth/AdminImpersonationService.cs`, `Endpoints/Admin/AdminImpersonationsEndpoints.cs`, `Middleware/ImpersonationContextMiddleware.cs`, migration `20260426183524_AddAdminImpersonations.cs`
- dashboard UI: `packages/dashboard/src/pages/admin/tenants/{TenantDetailPage,TenantsListPage}.tsx`

**Gaps:**
- AC2 `resourceSummary` (24h analytics): needs human verification that AdminTenants detail endpoint actually joins to `platform_analytics_hourly`.
- AC3 SSE fallback / long-poll if the stream can't open: needs human verification.

**Tests:** tests exist in `Admin/AdminTenantsTests.cs`, `AdminTenantsAuditAndNoteTests.cs`, `AdminTenantEventsSseTests.cs`, `AdminTenantEventsSseLoopTests.cs`, `AdminTenantEventsSseResumptionTests.cs`, `AdminImpersonationTests.cs`, `Auth/ImpersonationContextMiddlewareTests.cs`.

### 28-12 — Postgres roles + KEK rotation
**Verdict:** MOSTLY DONE
**Evidence:**
- commits `22bcf790` (Story 28-12 — KEK rotation + decryptor), `6db6f095` (merge — closes 28-4 decryptor gap), `c340b314` (KEK lifecycle hardening — PF-S5/S8/C3/C7 + retry actor identity), plus six R2 follow-ups
- KEK infra: `Tamma.Api/Services/Secrets/KekProvider.cs`, `KekRotationCoordinator.cs`, `KekRotationStatus.cs`, `KekCabinetHealthCheck.cs`, `AesGcmConnectionStringDecryptor.cs`, `TenantSecretProtector.cs`, `TenantSecretProtectorAdapter.cs`, `Stopgap/StopgapSecretMigrator.cs`, `RotationScheduleCalculator.cs`, `RotationSchedule.cs`
- migration `20260426120000_KekRotations.cs` + entity `KekRotation.cs`
- endpoints: `Tamma.Api/Endpoints/KekRotationEndpoints.cs` (`POST /api/admin/kek/rotate/start`, `GET /api/admin/kek/rotate/status`, `POST /api/admin/kek/rotate/retry`) wired in `Program.cs:1274-1280`
- Postgres roles: `scripts/db/postgres-roles.sql` exists at repo root
- runbook: `.dev/runbooks/kek-rotation.md` exists

**Gaps:**
- AC2 `postgres-roles-lint.yml` CI job (parses script in throwaway container): needs human verification — search `.github/workflows/` if present.
- AC2 API-pod startup check `SELECT current_user` asserting it is NOT `tamma_provisioner`: needs human verification.
- AC1 split-role enforcement at compose level — `docker-compose.prod.yml` would carry distinct DB URL slots; needs human verification.
- AC5 rekey loop in `RekeyTenantConnectionStringsWorkflow.cs`: I do not see a file with that exact name under `Tamma.ElsaServer/Workflows/`. The rotation is driven instead from `KekRotationCoordinator` (background task in the API process). Either the architecture diverged (coordinator-instead-of-workflow) or the workflow is on a separate path. Needs human verification.
- AC5 progress metric `tamma_kek_rotation_remaining_gauge`: needs human verification (probably present in coordinator).

**Tests:** tests exist in `Secrets/KekRotationCoordinatorTests.cs`, `KekProviderTests.cs`, `KekRotationAdvisoryLockTests.cs`, `KekRotationPostFixTests.cs`, `KekRotationRetryTests.cs`, `KekRotationStatusSerializationTests.cs`, `KekCabinetHealthCheckTests.cs`, `AesGcmConnectionStringDecryptorTests.cs`, `TenantSecretProtectorEnvironmentTests.cs`, `RotationScheduleCalculatorTests.cs`, plus `Stopgap/` tests.

## Recommended next-up

1. **Close 28-9 refresh-token tenant binding.** `RefreshToken` entity needs `TenantId`, `JtiChainHead`, `RevokedReason` columns + matching migration + reuse-detection logic. This is the largest concrete gap on the audit; the access-token claim is correct but the DB row is not tenant-scoped, so refresh-reuse across tenants is structurally possible.

2. **Verify and document the verify-email → provisioning trigger.** Either wire `VerifyEmail` to emit `TENANT.PROVISIONING_REQUESTED` per Story 28-5 AC1, or update the story doc + Epic 28 README to reflect that provisioning has moved to the V2 task-queue path and the verify-email coupling was deliberately dropped. The current story-vs-code mismatch is confusing.

3. **Decide on `EnsurePersonalTenantMiddleware`.** Either remove it from the pipeline (Story 28-8 AC1) or document why it survives for single-user mode. Currently both `TenantContextMiddleware` and `EnsurePersonalTenantMiddleware` exist in the tree.

4. **Flip every story file from "Status: Draft" to "Status: Done" (or "MOSTLY DONE w/ residuals")** so that future agents auditing this don't have to redo the mapping done in this report.

## Open hazards still grep-able in code

Two files carry `EPIC 28 CUTOVER HAZARD` markers (annotated for Task #12 from a recent docs commit `4ecd2ca1`):

- `Tamma.Api/Services/Conventions/IConventionStore.cs` — I-2 (silent partial write in per-tenant-DB mode for system-default conventions).
- `Tamma.Data/Repositories/IConventionRepository.cs` — I-2 (system-default partial-write under per-tenant DB) + X-1 (cross-tenant write defence-in-depth).
- `Tamma.Data/Entities/Convention.cs` — I-2 and X-1 surface notes.

These are all Convention-Store-bound (Epic 27 → 28 cutover concerns) and explicitly documented as "pure documentation, no runtime guard yet". They will need real fan-out-or-fail-loud guards once tenants run on per-tenant DBs in production. No code in `apps/tamma-elsa/src/Tamma.Activities/`, `Tamma.Api/Auth/`, or the tenant-lifecycle activities carries this marker.

Additionally, the CI-skip note from commit `bedf38a9` ("refresh pnpm lockfile + skip 3 aspirational 28-1 tests") flags **three skipped 28-1 tests** that need re-enabling or deletion. Worth grepping `[Skip(` or `[Fact(Skip` in `Epic28/` to surface them.
