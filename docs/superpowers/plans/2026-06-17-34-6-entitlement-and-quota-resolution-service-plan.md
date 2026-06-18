# Story 34-6 — Entitlement & Quota Resolution Service

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. C# suites that touch Postgres run via `sg docker -c "dotnet test ..."`;
> the build itself needs no wrapper.

**Goal:** Ship the single read seam that turns a tenant's pinned plan assignment (34-4) into a
concrete, closed, cached `ResolvedEntitlements` map — one `{ limit, period, overageMode }` per
`EntitlementMetricKey` (34-1) — plus a non-enforcing `CheckHeadroom` calc that the sibling
Enforcement epic, Billing (Epic 35), and both dashboards all consume. The resolver answers "what is
this tenant allowed?", per-mode (SaaS tenant_id vs single-user user_id→tenant), with event-driven
cache invalidation. It does NOT meter consumption (Epic 35) and does NOT block (Enforcement).

**Story file:** `docs/stories/epic-34/story-34-6/34-6-entitlement-and-quota-resolution-service.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/` (xUnit). No new package; everything lands under the
existing `Tamma.Api/Services/Pricing/` directory (introduced by 34-1) + a new `Dtos/Pricing/`.

---

## Non-goals (YAGNI guard)

- NO usage **metering** writes (Epic 35). The resolver only *reads* gauge counts behind
  `IEntitlementUsageReader` and returns `null` for metering-only metrics (`LlmTokens`,
  `WorkflowRuns`) until Epic 35 supplies its reader.
- NO quota **enforcement** / blocking. `CheckHeadroom` computes over/remaining; nothing in this
  story blocks a workflow, a request, or a provider call (sibling Enforcement epic owns that).
- NO Stripe / billing / charging (Epic 35).
- NO catalog / assignment **mutation**. This story is read-only over 34-1 (`IPlanCatalogService`)
  and 34-4 (`IPlanAssignmentService.GetActiveAsync`). It never writes `Plan`/`PlanEntitlement`/
  `TenantPlanAssignment`.
- NO new EF migration. The resolver reads existing tables; the only new state is an in-memory cache.
- NO dashboard UI. The two dashboards consume this API in a later story.
- NO change to the metric enum or the catalog/assignment contracts — they are imported verbatim.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Prerequisites this story builds on (sibling stories, already drafted)

| Contract | Owner | Where |
|---|---|---|
| `EntitlementMetricKey` closed enum (`Agents, WorkflowRuns, LlmTokens, Seats, Repos, RagStorageMb, BenchmarkRetentionDays`), snake_case persisted | **34-1** | `Tamma.Core/Enums/EntitlementMetricKey.cs` (NEW in 34-1) |
| `PlanEntitlement { MetricKey, LimitValue long? (null=unlimited), Period, OverageMode }` | **34-1** | `Tamma.Data/Entities/PlanEntitlement.cs` (NEW in 34-1) |
| `IPlanCatalogService.GetByIdAsync(planId) → PlanSnapshot{ Version, Status, IsCustom, Entitlements }` | **34-1** | `Tamma.Api/Services/Pricing/IPlanCatalogService.cs` (NEW in 34-1) |
| `IPlanAssignmentService.GetActiveAsync(tenantId) → TenantPlanAssignment{ PlanId, PlanVersion }` (pinned) | **34-4** | `Tamma.Api/Services/Pricing/IPlanAssignmentService.cs` (NEW in 34-4) |
| `TENANT.PLAN.CHANGED` event (tags incl. `tenantId`) on assign/activation | **34-4** | `Tamma.Api/Services/Pricing/PlanAssignmentEventTypes.cs` (NEW in 34-4) |
| `PricingEndpoints.cs` file (tenant pricing surface) | **34-2** | `Tamma.Api/Endpoints/PricingEndpoints.cs` (NEW in 34-2) |
| `PricingServiceCollectionExtensions` DI seam | **34-1** | `Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` (NEW in 34-1) |
| `PLAN.CATALOG.UPDATED` (or equivalent catalog-edit event) | **34-2** | catalog admin CRUD; verify exact event name in `PlanCatalogEventTypes.cs` |

> **Sequencing reality:** 34-1, 34-2, 34-4 are *drafted* (story files exist) but not yet
> implemented. This plan's Task 0 verifies the live signatures before coding; if a sibling has not
> landed, stub the consumed interfaces behind the seam and pin against the story contracts above
> (the unit tests mock these interfaces, so this story can be built and tested ahead of full
> sibling implementation, then re-verified once they land).

### Existing seams this story reuses (verified live in repo)

| Seam | File (verified) | Use |
|---|---|---|
| `ITammaModeProvider.Mode` (SingleUser \| SaaS, process-stable) | `src/Tamma.Api/Services/PromptStore/TammaMode.cs:48` | per-mode principal resolution |
| Fail-loud resolution precedent (`tenant → system → throw`, no empty fallback) | `src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:145-173` | mirror for `NO_ASSIGNMENT` |
| `ITenantContext.TenantId` | `src/Tamma.Data/ITenantContext.cs:3` | caller tenant on member route |
| In-process event-bus subscriber (cache invalidation precedent) | `src/Tamma.Api/Services/TenantStatus/TenantStatusInvalidationListener.cs:42` | `EntitlementCacheInvalidationListener` model |
| `IPlatformEventBus.Subscribe(typePrefix, handler)` (prefix subscription, per-handler catch) | `src/Tamma.Api/Services/PlatformEvents/IPlatformEventBus.cs:52-74` | subscribe `TENANT.PLAN.` + catalog prefix |
| `IEventRepository.AppendAsync(DomainEvent)` (tenant-scope DCB) | `src/Tamma.Data/Repositories/IEventRepository.cs:7` | `ENTITLEMENT.RESOLVED.*` emit |
| `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)` | `src/Tamma.Api/Services/PlatformEvents/PlatformEventPublisher.cs:33` | platform-audit mirror |
| `IMemoryCache` cache precedent | `src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs`, `InMemoryBudgetConfigProvider.cs` | `EntitlementSnapshotCache` |
| `ITenantMembershipRepository.ListByTenantAsync(tenantId, limit, offset) → (Members, Total)` | `src/Tamma.Data/Repositories/ITenantMembershipRepository.cs:10` | `Seats` count |
| `AgentConfig { TenantId }` (Epic 32) | `src/Tamma.Data/Entities/AgentConfig.cs:13` | `Agents` count |
| `GitHubInstallationRepo { IsActive, InstallationEntityId }` | `src/Tamma.Data/Entities/GitHubInstallationRepo.cs` | `Repos` count |
| `TenantMembership { Role }` + `TenantRoleHierarchy` (owner/admin/member) | `src/Tamma.Data/Entities/TenantMembership.cs`, `src/Tamma.Api/Authorization/TenantRoleHierarchy.cs:16-21` | RBAC |
| Auth policies `MemberAccess` / `PlatformOwnerAccess` | `src/Tamma.Api/Program.cs:986-994` | endpoint gating |
| `TammaError(code, message, context, retryable, severity)` | `src/Tamma.Core/TammaError.cs:44` | fail-loud errors |
| `DomainEvent { Type, TenantId, Tags, Metadata, Data }` | `src/Tamma.Data/Entities/DomainEvent.cs` | event shape |

**Key reuse note:** the cache-invalidation listener is a near-clone of
`TenantStatusInvalidationListener` *but simpler* — it subscribes the in-process `IPlatformEventBus`
(no Postgres LISTEN/NOTIFY), so no Npgsql connection management, no shutdown drain. Lift the
subscribe/handler/swallow shape, drop the LISTEN machinery.

---

## Architecture

**resolve (cache-first) → snapshot (pinned) → closed map → headroom**, all read-only:

1. **`IEntitlementService.ResolveAsync(principal)`** — single read seam. Principal → tenantId
   (per-mode), cache-first, else `IPlanAssignmentService.GetActiveAsync` → `IPlanCatalogService.
   GetByIdAsync` → build a complete 7-key `ResolvedEntitlements` (backfill missing metrics with a
   documented default), cache it, emit `ENTITLEMENT.RESOLVED.SUCCESS` on the miss.
2. **`CheckHeadroom(resolved, metric, usage)`** — pure, shared, non-enforcing over/remaining calc.
3. **`IEntitlementSnapshotCache`** — `IMemoryCache`-backed, keyed by tenantId, TTL + explicit
   eviction.
4. **`EntitlementCacheInvalidationListener`** — `BackgroundService` subscribing `IPlatformEventBus`:
   `TENANT.PLAN.CHANGED` evicts one tenant; `PLAN.CATALOG.UPDATED` flushes all.
5. **`IEntitlementUsageReader`** — gauge-metric current-value seam; CP-backed default answers
   seats/agents/repos, returns `null` for metering-only metrics (Epic 35).
6. **Endpoints:** `GET /api/pricing/entitlements` (MemberAccess, tenant from `ITenantContext`),
   `GET /api/admin/tenants/{id}/entitlements` (PlatformOwnerAccess).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Principal | sole user → personal tenant (`ForUser`) | caller tenant (`ForTenant`, `ITenantContext`) |
| Read own entitlements | the sole user (authenticated) | any tenant member (`MemberAccess`) — read is unprivileged |
| Read arbitrary tenant | the sole user (same code path) | platform owner (`PlatformOwnerAccess`) only |
| Cross-tenant guard | n/a | member route ignores body/param tenant; uses `ITenantContext` |
| Cache key | personal tenant id | caller tenant id |
| Mode source | `ITammaModeProvider.Mode` | same |

---

## Task breakdown

### Task 0 — Verify sibling contracts + scaffold (no behaviour)

- [ ] **Verify** the live signatures of `EntitlementMetricKey`, `PlanSnapshot.Entitlements`,
  `IPlanCatalogService.GetByIdAsync`, `IPlanAssignmentService.GetActiveAsync`, the exact
  `TENANT.PLAN.CHANGED` + catalog-update event names, and the `PricingServiceCollectionExtensions`
  shape. If a sibling hasn't landed, record the pinned contract (story §Current-state) and proceed
  against the interface (unit tests mock it).
- [ ] Create the `Tamma.Api/Dtos/Pricing/` directory.
- [ ] Confirm `MemberAccess` + `PlatformOwnerAccess` policies (Program.cs:986-994) and the
  `sg docker -c "dotnet test ..."` runner contract.

**Files:** read-only verification + empty dir. **Tests:** none (scaffold).

### Task 1 — Models + headroom (pure, no I/O) [TDD]

- [ ] **Tests first** `CheckHeadroomTests.cs`: under / at-limit / over / unlimited(`null`) / zero-
  limit matrix; assert `Remaining`, `IsOver`, `OveragePercent` (unlimited ⇒ `Remaining=null,
  IsOver=false` even at huge usage; zero limit ⇒ `OveragePercent=null`, `IsOver` when usage>0).
- [ ] Implement `EntitlementModels.cs`: `EntitlementPrincipal` (ForTenant/ForUser),
  `ResolvedEntitlement`, `ResolvedEntitlements` (closed `IReadOnlyDictionary<EntitlementMetricKey,
  ResolvedEntitlement>` + non-throwing `Get`), `EntitlementHeadroom`.
- [ ] Implement the pure `CheckHeadroom` as a static helper (and the `IEntitlementService` member
  delegates to it) so it's testable without the service.

**Files:** `EntitlementModels.cs`, `IEntitlementService.cs` (signature only),
`CheckHeadroomTests.cs`. **Approach:** records + a pure static; no dependencies.

### Task 2 — Snapshot cache [TDD]

- [ ] **Tests first** `EntitlementSnapshotCacheTests.cs`: `Set`+`TryGet` round-trips per tenant;
  `Invalidate(tenantId)` evicts exactly one; `Flush()` clears all; TTL expiry evicts (inject
  `TimeProvider`/fake clock); two tenants never collide.
- [ ] Implement `IEntitlementSnapshotCache` + `EntitlementSnapshotCache` over `IMemoryCache`
  (precedent `DiagnosticsService`), key `"entitlements:{tenantId}"`, default 5-min TTL, explicit
  `Invalidate`/`Flush`.

**Files:** `IEntitlementSnapshotCache.cs`, `EntitlementSnapshotCache.cs`, test. **Approach:** thin
`IMemoryCache` wrapper; keep a `ConcurrentDictionary<Guid, byte>` key registry so `Flush` and
per-tenant eviction are O(1) (IMemoryCache has no enumerate-keys API).

### Task 3 — Gauge-metric usage reader [TDD]

- [ ] **Tests first** `ControlPlaneEntitlementUsageReaderTests.cs`: `Seats` = membership Total
  (`ITenantMembershipRepository.ListByTenantAsync(t,1,0).Total`); `Agents` = `AgentConfig` count for
  the tenant; `Repos` = active `GitHubInstallationRepo` count for the tenant's installation;
  `LlmTokens`/`WorkflowRuns`/`RagStorageMb`/`BenchmarkRetentionDays` return `null`; reader throwing
  for one metric is caught by the caller (assert at Task 4/5, here assert it returns the count).
- [ ] Implement `IEntitlementUsageReader` + `ControlPlaneEntitlementUsageReader` (inject
  `ITenantMembershipRepository`, `ControlPlaneDbContext` for `AgentConfig`/`GitHubInstallationRepo`,
  scoped via `IServiceScopeFactory` if needed for the listener path).

**Files:** `IEntitlementUsageReader.cs`, `ControlPlaneEntitlementUsageReader.cs`, test.
**Approach:** a `switch` on `EntitlementMetricKey`; `default → null`. Repo/agent/repo counts via the
existing repository + `CountAsync` on the DbSets.

### Task 4 — Core resolution service [TDD]

- [ ] **Tests first** `EntitlementServiceTests.cs`:
  - SaaS-by-tenantId and single-user-by-userId→tenant resolve the pinned assignment → complete map;
  - complete-7-keys invariant + per-metric default backfill (`limit=0, monthly, block`) when the
    snapshot has fewer rows;
  - unlimited (`null` limit) flows through resolve;
  - `NO_ASSIGNMENT` fail-loud (throws `ENTITLEMENT.RESOLVE.NO_ASSIGNMENT` severity High, emits
    `ENTITLEMENT.RESOLVED.FAILED{reason=no_assignment}`, NEVER returns empty);
  - catalog-unavailable for a pinned planId → `CATALOG_UNAVAILABLE` + FAILED event;
  - custom-plan override (IsCustom snapshot's bespoke/unlimited limits win);
  - cache hit (second resolve does NOT call `GetActiveAsync`/`GetByIdAsync`; only the miss emits
    `ENTITLEMENT.RESOLVED.SUCCESS`).
- [ ] Implement `EntitlementService` (algorithm in story §Resolution algorithm), `EntitlementEventTypes.cs`.
  Mode + principal→tenant via `ITammaModeProvider`; single-user user→personal-tenant lookup
  (reuse the personal-tenant resolution the `EnsurePersonalTenantMiddleware` invariant guarantees).
- [ ] Emit `ENTITLEMENT.RESOLVED.SUCCESS` (cache-miss) / `.FAILED` via `IEventRepository` +
  platform mirror via `IPlatformEventPublisher`.

**Files:** `EntitlementService.cs`, `EntitlementEventTypes.cs`, complete `IEntitlementService.cs`,
test. **Approach:** mock `IPlanAssignmentService`, `IPlanCatalogService`, `IEntitlementSnapshotCache`,
`IEventRepository`/`IPlatformEventPublisher` in unit tests.

### Task 5 — Cache invalidation listener [TDD]

- [ ] **Tests first** `EntitlementCacheInvalidationListenerTests.cs`: a published
  `TENANT.PLAN.CHANGED{tenantId=T}` invalidates exactly T; `PLAN.CATALOG.UPDATED` flushes all; a
  handler exception / malformed payload is swallowed (listener never throws back into the bus,
  cache untouched for unrelated tenants).
- [ ] Implement `EntitlementCacheInvalidationListener : BackgroundService` — on `StartAsync`
  `Subscribe("TENANT.PLAN.", handler)` + `Subscribe(<catalog-update-prefix>, handler)`; handler
  parses `tenantId` tag → `Invalidate`, catalog event → `Flush`; wrap in try/catch (precedent
  `TenantStatusInvalidationListener.OnNotification`). Dispose subscriptions on `StopAsync`.

**Files:** `EntitlementCacheInvalidationListener.cs`, test. **Approach:** lift the subscribe/handler
shape from `TenantStatusInvalidationListener` *minus* the Npgsql LISTEN/NOTIFY + shutdown-drain
machinery (in-process bus, no connection).

### Task 6 — DTOs + endpoints + DI wiring [TDD]

- [ ] **Tests first** `EntitlementEndpointsTests.cs` (integration, `sg docker`): member reads own
  tenant (`GET /api/pricing/entitlements`, `MemberAccess`, body includes live seat/agent/repo
  counts); admin reads any tenant (`GET /api/admin/tenants/{id}/entitlements`,
  `PlatformOwnerAccess` → 200; non-platform → 403; unknown tenant → 404; no assignment → 404);
  **tenant isolation** (tenant A member cannot read tenant B; `TENANT.PLAN.CHANGED` for A doesn't
  evict B); version-pinning end-to-end (assign v1, deprecate→v2, resolve still v1).
- [ ] Implement `EntitlementDtos.cs` (`ResolvedEntitlementsDto` with inline per-metric headroom
  fields).
- [ ] Add `GET /api/pricing/entitlements` to `PricingEndpoints.cs` (tenant from `ITenantContext` in
  SaaS / sole user in single-user) and `GET /api/admin/tenants/{id}/entitlements` to
  `AdminTenantsEndpoints.cs`. Each endpoint composes `ResolveAsync` + per-metric
  `IEntitlementUsageReader.GetCurrentAsync` + `CheckHeadroom`. Map `NO_ASSIGNMENT` → 404.
- [ ] `PricingServiceCollectionExtensions.AddEntitlementResolution(services)`: register
  `IEntitlementService`, `IEntitlementSnapshotCache` (singleton), `IEntitlementUsageReader`
  (scoped), and `AddHostedService<EntitlementCacheInvalidationListener>()`. Call from `Program.cs`;
  map the two routes.

**Files:** `EntitlementDtos.cs`, `PricingEndpoints.cs` (mod), `AdminTenantsEndpoints.cs` (mod),
`PricingServiceCollectionExtensions.cs` (mod), `Program.cs` (mod), test.

### Task 7 — Full-suite green + verification

- [ ] `dotnet build` clean (no wrapper).
- [ ] `sg docker -c "dotnet test apps/tamma-elsa/..."` — new Pricing tests + full suite green.
- [ ] Confirm no migration was generated (`has-pending-model-changes` reports none — this story adds
  no schema).
- [ ] Re-verify against the live sibling interfaces if 34-1/34-2/34-4 landed during implementation.

---

## Sequencing & dependencies

```
Task 0 (verify) → Task 1 (models+headroom) → Task 2 (cache) ┐
                                              Task 3 (reader) ┼→ Task 4 (service) → Task 5 (listener) → Task 6 (endpoints+DI) → Task 7 (verify)
```

- Task 1 is the only hard prerequisite for everything (models). Tasks 2 and 3 are independent and
  parallel-safe. Task 4 needs 1+2 (cache) and benefits from 3 (reader, but the service can be built
  with the reader mocked and wired fully in Task 6). Task 5 needs 2. Task 6 needs 4+5+3.
- **Cross-story:** depends on 34-1 (`IPlanCatalogService`, `EntitlementMetricKey`, `PlanEntitlement`,
  `PricingServiceCollectionExtensions`) and 34-4 (`IPlanAssignmentService.GetActiveAsync`,
  `TENANT.PLAN.CHANGED`); consumes the `PricingEndpoints.cs` file from 34-2. Because the service
  depends only on *interfaces* (mocked in unit tests), Tasks 1-5 can be built ahead of full sibling
  implementation; Task 6 integration tests + Task 0 re-verification need the siblings live (or their
  interfaces stubbed against the pinned contracts).

---

## Risks + mitigations

- **Sibling stories not yet implemented (34-1/34-2/34-4 are drafted, not built).** Mitigation: code
  against the pinned interface contracts (story §Current-state table), mock them in unit tests,
  gate the docker integration tests (Task 6) behind sibling availability, and re-verify signatures
  in Task 0 + Task 7. The seam-based design means this story is buildable and unit-testable ahead of
  the siblings.
- **Resolution must never fall back to empty/plain** (`feedback_resolution_no_empty_fallback`).
  Mitigation: the *assignment* missing is a hard `NO_ASSIGNMENT` throw; only a *missing metric row
  within a present assignment* backfills the documented default — Task 4 tests pin both paths
  explicitly so a future refactor can't soften the throw into an empty map.
- **Cache staleness.** Mitigation: pinned snapshots are immutable (deprecated plan versions never
  mutate, 34-1), so a stale cached snapshot can never be *wrong* — only outdated if a tenant was
  re-assigned, which `TENANT.PLAN.CHANGED` invalidation catches instantly; the TTL is a memory
  bound, not a correctness mechanism. Task 5 tests the invalidation path; Task 2 tests TTL.
- **Listener swallowing too much.** Mitigation: handler try/catch is per-event (precedent
  `TenantStatusInvalidationListener`), logged WARN; a malformed event evicts nothing rather than
  flushing everything — Task 5 asserts unrelated tenants are untouched on a bad event.
- **Gauge reader coupling to other epics.** Mitigation: the `IEntitlementUsageReader` seam keeps
  metering (Epic 35) out of the resolve path; the CP reader answers only seats/agents/repos and
  returns `null` elsewhere; a reader exception degrades one metric to `CurrentUsage=null`, never
  failing the whole read (Task 6 endpoint composition catches per-metric).
- **Event-topology shift (Story 28-1).** Mitigation: the invalidation listener subscribes the
  **in-process** `IPlatformEventBus`, not a DB poller, so the per-tenant event-store migration
  doesn't break invalidation; `ENTITLEMENT.RESOLVED.*` system-scope events keep appending via the CP
  `IEventRepository` explicitly.
- **No migration discipline trap.** This story adds no schema — confirm `has-pending-model-changes`
  reports none in Task 7 so no accidental migration sneaks in.

---

## Acceptance criteria (mirror of the story)

- [ ] `IEntitlementService.ResolveAsync(principal)` returns a complete, closed `ResolvedEntitlements`
  map (all 7 `EntitlementMetricKey` members) sourced from the pinned `(PlanId, PlanVersion)`
  snapshot via 34-1/34-4; missing metric rows backfill the documented default (`limit 0, monthly,
  block`).
- [ ] Per-mode principal: SaaS by `tenant_id`, single-user by `user_id`→personal tenant; resolution
  NEVER falls back to empty/plain — no active assignment throws
  `ENTITLEMENT.RESOLVE.NO_ASSIGNMENT`.
- [ ] `GET /api/pricing/entitlements` (MemberAccess, own tenant) and
  `GET /api/admin/tenants/{id}/entitlements` (PlatformOwnerAccess) return the resolved set; unknown
  tenant / no assignment → 404.
- [ ] Snapshots cached per tenant; invalidated on `TENANT.PLAN.CHANGED` (evict one) and
  `PLAN.CATALOG.UPDATED` (flush all) via an `IPlatformEventBus` subscriber.
- [ ] Custom enterprise plan entitlements (incl. unlimited) resolve and override public defaults.
- [ ] `CheckHeadroom(resolved, metric, usage)` returns remaining/over without enforcing — one shared
  calc for enforcement + dashboards; unlimited ⇒ `Remaining=null, IsOver=false`.
- [ ] Gauge metrics resolved via `IEntitlementUsageReader`: `Seats` = `TenantMembership` count,
  `Agents` = Epic-32 agent count, `Repos` = active `GitHubInstallationRepo` count; metering-only
  metrics return `null`.
- [ ] `ENTITLEMENT.RESOLVED.SUCCESS`/`.FAILED` DCB events emitted (cache-miss / failure, not every
  hit).
- [ ] Per-mode + per-tenant ownership honored; tenant isolation enforced (A never reads B; A's plan
  change never evicts B's cache).
- [ ] Unit + integration tests cover resolution per mode, complete-keys + default backfill, unlimited
  handling, cache invalidation on plan change, custom-plan override, headroom math, gauge counts,
  RBAC matrix, tenant isolation, and version pinning. Full suite green; no migration generated.
