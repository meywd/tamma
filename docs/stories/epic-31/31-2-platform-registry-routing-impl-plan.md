# Story 31-2 Implementation Plan — Platform Registry + Per-Tenant Routing Resolver

**Status**: Planned (2026-04-21)
**Story brief**: [`31-2-platform-registry-routing.md`](./31-2-platform-registry-routing.md)
**Epic 31 phase**: Foundation — after 31-1; blocks 31-3..31-9.
**Branch**: `feat/story-31-2-platform-registry-routing`

---

## 1. Objective

Ship the `IPlatformResolver` service that hands a caller a
ready-to-use `IGitPlatformDriver` scoped to a tenant's chosen git
platform + decrypted installation credentials. Introduces the
`tenant_platform_installations` table (generalising
`github_installations`), the repository over it with RLS, and the
cache invalidation flow tied to credential-rotation and switch-org
events. After this story, every caller that needs to talk to a git
platform on behalf of a tenant goes through one audited seam.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — `IGitPlatformDriver` + capability matrix + enums.
- **Story 28-3** — tenant DbContext factory (for tenant-scoped
  reads).
- **Story 28-9** — switch-org flow (cache invalidation subscribes
  to JWT tenant-claim changes).
- **Story 29-2** — Postgres-backed secret store (`tenant_secrets`
  table exists; `credential_secret_id` FK requires it).

Soft:

- **Story 29-3** — reveal-once UX — used by onboarding but not
  directly here.

Blocks: 31-3, 31-4, 31-5, 31-6, 31-7, 31-8, 31-9, 31-11, 31-12.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/TenantPlatformInstallation.cs` | EF entity mapped to the new table. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260505000000_TenantPlatformInstallations.cs` | EF migration: table + index + RLS policy + existing-`github_installations` seed. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantPlatformInstallationRepository.cs` | Typed CRUD + RLS-aware list. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/TenantPlatformInstallationRepository.cs` | Impl — uses tenant DbContext per 28-3. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IPlatformResolver.cs` | Resolver interface with `ResolveForTenantAsync`, `ResolveForWebhookAsync`, `ListForTenantAsync`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms/Tamma.Platforms.csproj` | New project for the resolver + impl (not the abstraction). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms/PlatformResolver.cs` | Impl: keyed-DI lookup + secret load + cached driver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms/PlatformDriverCache.cs` | LRU cache of `(tenantId, kind) → IGitPlatformDriver` with 5-min TTL. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms/PlatformResolverCacheInvalidator.cs` | Background service subscribing to the event store tail for `PLATFORM.INSTALLATION.*` + `TENANT.SWITCH_ORG` events. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms/PlatformInstallationEvents.cs` | Typed event records: `PlatformInstallationConnectedEvent`, `PlatformInstallationDisconnectedEvent`, `PlatformInstallationCredentialRotatedEvent`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Tests/PlatformResolverTests.cs` | Happy path + null + cross-tenant + cache + invalidation tests. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Tests/TenantPlatformInstallationRepositoryTests.cs` | RLS policy + cross-tenant denial tests via Testcontainers Postgres. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Tests/PlatformResolverCacheInvalidatorTests.cs` | Event-subscription test. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add `Tamma.Platforms` + test project. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` | Register `TenantPlatformInstallation` entity. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `IPlatformResolver`, `PlatformDriverCache` (singleton), `PlatformResolverCacheInvalidator` (hosted service). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` | Not yet touched here — 31-3 migrates callers. Documented for the reader. |

## 5. Sequence of changes

### Step 1 — Migration + entity (4h)

- `20260505000000_TenantPlatformInstallations.cs`:
  - Create table per brief AC1.
  - Unique index on `(tenant_id, platform_kind, installation_external_id)`.
  - RLS policy: app-role can only read rows where `tenant_id =
    current_setting('app.current_tenant_id')::uuid`. Postgres
    superuser bypasses.
  - Backfill from `github_installations`: for each row, INSERT a
    `tenant_platform_installations` row with `platform_kind='github'`,
    `installation_external_id = github_installations.installation_id`,
    `credential_secret_id = <lookup against tenant_secrets where
    name='github_installation_token'>`.
  - Rollback path documented (non-reversible? see §7).
- `TenantPlatformInstallation` entity + `DbSet` registration.
- Unit test: entity mapping round-trips JSONB `metadata`.
- **Commit**: `feat(data): tenant_platform_installations migration`.

### Step 2 — Repository + RLS-scoped queries (3h)

- `ITenantPlatformInstallationRepository` with methods: `GetByTenantAsync`,
  `GetByIdAsync` (scoped), `GetByExternalIdAsync` (for webhook resolve),
  `CreateAsync`, `UpdateAsync`, `SoftDeleteAsync`, `ListByTenantAsync`.
- Each method sets `app.current_tenant_id` before the query via
  `ITenantContextAccessor` (from 28-3).
- Integration test with Postgres testcontainer: cross-tenant call
  returns 0 rows even when setting a spoofed tenant id.
- **Commit**: `feat(data): tenant_platform_installations repository`.

### Step 3 — `IPlatformResolver` interface (1h)

- Three methods per brief AC3.
- Placed in `Tamma.Platforms.Abstractions` so 31-3..31-6 drivers can
  reference without a runtime dep on the impl project.
- **Commit**: `feat(platforms): IPlatformResolver interface`.

### Step 4 — `PlatformDriverCache` (2h)

- Thin LRU via `Microsoft.Extensions.Caching.Memory.MemoryCache` with
  configurable size (default 128 entries) + 5-min sliding TTL.
- Keyed by `(tenantId, PlatformKind)` — tenant may eventually have
  multiple kinds.
- `InvalidateTenantAsync(Guid tenantId)` purges all entries for that
  tenant.
- Unit tests: hit, miss, TTL expiration, explicit invalidation.
- **Commit**: `feat(platforms): driver cache`.

### Step 5 — `PlatformResolver` impl (5h)

- Constructor: `IKeyedServiceProvider`, `ITenantPlatformInstallationRepository`,
  `ISecretStore`, `PlatformDriverCache`, `IEventRepository`,
  `ILogger<PlatformResolver>`.
- `ResolveForTenantAsync`:
  1. Cache check → hit returns.
  2. Repo `GetByTenantAsync(tenantId)` → if null, return null.
  3. Load credential via `_secrets.GetAsync(row.CredentialSecretId)`.
  4. Look up driver factory via
     `IKeyedServiceProvider.GetRequiredKeyedService<IGitPlatformDriverFactory>(row.PlatformKind)`.
  5. Factory builds a driver wired to `row.BaseUrl` + credential.
  6. Insert into cache. Return.
- `ResolveForWebhookAsync(installationId)`: repo
  `GetByIdAsync` → same path 3-6.
- `ListForTenantAsync`: enumerate all rows (multi-platform tenant
  supported; first cut UI ships single).
- Unit tests: null for missing install, caches across calls,
  cross-tenant spoof returns null (repository RLS enforces).
- **Commit**: `feat(platforms): PlatformResolver impl`.

### Step 6 — Event types + emission helpers (2h)

- Three typed event records in `PlatformInstallationEvents.cs`.
- Helper `IPlatformInstallationEventEmitter` with
  `EmitConnectedAsync`, `EmitDisconnectedAsync`, `EmitCredentialRotatedAsync`
  — wraps `_events.AppendAsync` with correct Tags.
- These events land in 31-3 (GitHub refactor) + 31-9 (onboarding)
  call sites; emitter plumbed in DI now so callers compile-time-link
  cleanly.
- **Commit**: `feat(platforms): installation event types + emitter`.

### Step 7 — Cache invalidator hosted service (3h)

- `PlatformResolverCacheInvalidator` extends
  `Microsoft.Extensions.Hosting.BackgroundService`.
- Subscribes to the event store's live tail for types
  `PLATFORM.INSTALLATION.CREDENTIAL_ROTATED.SUCCESS`,
  `PLATFORM.INSTALLATION.DISCONNECTED.SUCCESS`,
  `TENANT.SWITCH_ORG.SUCCESS`.
- On each matching event: `cache.InvalidateTenantAsync(tenantId)`.
- Tail consumer uses `IEventRepository.TailAsync(fromOffset,
  cancellationToken)` — existing primitive from Epic 28.
- Test: fake event store emits a rotation event; invalidator removes
  cache entry.
- **Commit**: `feat(platforms): cache invalidator hosted service`.

### Step 8 — DI registration + wiring (2h)

- `Program.cs`:
  - `services.AddScoped<IPlatformResolver, PlatformResolver>();`
  - `services.AddSingleton<PlatformDriverCache>();`
  - `services.AddHostedService<PlatformResolverCacheInvalidator>();`
  - `services.AddScoped<ITenantPlatformInstallationRepository, TenantPlatformInstallationRepository>();`
- **Commit**: `feat(api): wire platform resolver + cache invalidator`.

### Step 9 — Integration test with fake drivers (3h)

- Two fake `IGitPlatformDriver` implementations registered under
  `PlatformKind.Gitea` + `PlatformKind.GitLab` keys.
- Create two tenants: one with a Gitea install, one with GitLab.
- Assert resolver returns the correct driver per tenant.
- Rotate one tenant's credential; assert cache invalidated on next
  call.
- Assert tenant A calling with spoofed id of tenant B returns null
  (RLS path).
- **Commit**: `test(platforms): resolver integration`.

### Step 10 — Coverage + docs (1h)

- Coverage target ≥85%.
- Append a resolver architecture section to
  `Tamma.Platforms.Abstractions/README.md`.
- **Commit**: `docs(platforms): resolver architecture`.

## 6. Test strategy

### Unit

- `PlatformDriverCache`: LRU semantics, TTL, invalidation.
- Resolver: null paths, cache interaction, event-driven invalidation.
- Event emitter: serialises Tags correctly.
- Repository: happy path + cross-tenant denial.

### Integration (Postgres testcontainer)

- RLS policy enforces tenant isolation under `app_user` role.
- Migration backfill preserves existing GitHub installations.
- Full loop: create installation → resolve → rotate credential (via
  event emission) → resolve again → assert fresh driver instance.

### Defence-in-depth

- Connect as `app_user` with spoofed `SET app.current_tenant_id` —
  ensure RLS policy blocks reads for non-matching tenant even if
  the repository layer is bypassed.

## 7. Rollback plan

- **Revert commits**: removes resolver + cache + invalidator + repo
  + entity. `Program.cs` registrations drop. No downstream code
  references the resolver until 31-3 lands.
- **Migration rollback**: EF `Down()` drops the table. Any rows in
  `tenant_platform_installations` are lost. `github_installations`
  is **not** dropped (31-3 still reads it in pre-migration state).
  After 31-3 ships, the `github_installations` table is scheduled
  for deprecation in a later story; until then, dual-read is safe.
- **Non-reversible**: if a tenant has already connected a Gitea
  install (only possible post-31-4/31-9), revert loses that
  connection record. Document in the rollback runbook: operators
  must snapshot `tenant_platform_installations` before revert.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Migration + entity | 4 |
| 2. Repository | 3 |
| 3. `IPlatformResolver` interface | 1 |
| 4. Driver cache | 2 |
| 5. Resolver impl | 5 |
| 6. Event types + emitter | 2 |
| 7. Cache invalidator service | 3 |
| 8. DI wiring | 2 |
| 9. Integration test | 3 |
| 10. Coverage + docs | 1 |
| **Total** | **26** (brief: 18 — variance: brief under-estimated RLS + event-subscriber work; flagged). |

## 9. Open questions

- **Multi-platform tenant support**: brief AC3 says "a tenant may
  eventually connect more than one platform" — interface supports
  `ListForTenantAsync` but UI ships single-platform first. Plan:
  interface shipped now; UI defers multi-connect to a later story.
  Document how the resolver decides "which driver" when a tenant
  has two — first-matching on primary key `(tenantId, kind)` is
  fine if the caller provides `kind`; for caller-agnostic use
  cases, we need a "primary" flag. Add `IsPrimary bool NOT NULL
  DEFAULT true` to the table now — cheap, future-proof.
- **`IGitPlatformDriverFactory` keyed DI pattern**: C# keyed DI does
  not support factory shapes cleanly. Plan: register drivers directly
  via `AddKeyedSingleton<IGitPlatformDriver>` when the driver is
  stateless (has no installation context), or use a
  `IKeyedServiceProvider.GetRequiredKeyedService<IGitPlatformDriverFactory>`
  pattern that each driver project registers. Lean toward factory
  because each tenant's driver needs a different `baseUrl`. Document
  in README.
- **Cache size 128**: arbitrary. For a 1000-tenant deployment this
  means 88% of lookups miss cache. Plan: make `Platforms:DriverCache:
  MaxEntries` configurable; default 512. Monitor hit rate in
  telemetry.
- **Event-tail consumer catch-up on restart**: the invalidator is
  a hosted service; on process restart, it should begin tailing
  from "now" (not replay history) to avoid re-invalidating already-
  processed events. Plan: tail from `IEventRepository.GetMaxOffsetAsync()`
  at startup. Document in invalidator.
- **Migration backfill and existing `github_installations` rows**:
  some rows may have `installation_id` without a corresponding
  `tenant_secrets` row (legacy free-tenant installations). Plan:
  backfill creates `tenant_secrets` rows pointing to the existing
  private-key PEM mounted env var; operator runbook documents.
- **Webhook path resolution ordering**: `ResolveForWebhookAsync`
  takes the installation row id, not the platform's external id.
  31-7's webhook handler has only the external id (from the webhook
  payload). Plan: add `GetByExternalIdAsync(PlatformKind, string
  externalId)` for webhook enrichment.
