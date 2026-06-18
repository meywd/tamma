# Story 34-6: Entitlement & Quota Resolution Service

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide covers the 7-phase workflow (Read → Research → Break Down → TDD → Quality
Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging, test-first
development, 100% coverage on critical paths, and build-success enforcement. Failure to follow it
results in rework.

## User Story

As a **platform owner (and, in SaaS mode, a tenant member/admin)**,
I want a single read API that turns a tenant's pinned plan assignment (34-4) into a concrete,
resolved entitlement set — a `LimitValue`/`Period`/`OverageMode` per `EntitlementMetricKey`
(agents, workflow runs, LLM tokens, seats, repos, RAG storage, benchmark retention) — with a
cached snapshot and a non-enforcing headroom helper,
so that the sibling Enforcement epic, Billing (Epic 35), and both dashboards all answer "what is
this tenant allowed?" from one canonical calculation that never drifts, and a plan deprecation
never silently re-prices or re-limits an existing tenant.

## Priority

P0 — This is the read contract the Enforcement epic and the dashboards consume. Without a single
resolution seam, every consumer would re-derive limits from the catalog independently and they
would drift. It is the bridge between the assignment layer (34-4) and everything that asks "is the
tenant over their quota?".

## Acceptance Criteria

1. A new `IEntitlementService` (`apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementService.cs`)
   exposes `Task<ResolvedEntitlements> ResolveAsync(EntitlementPrincipal principal, CancellationToken ct)`
   that returns a `ResolvedEntitlements` map: **every** `EntitlementMetricKey` member (34-1) →
   `{ MetricKey, LimitValue (null = unlimited), Period, OverageMode }`, sourced from the
   `PlanEntitlement` rows of the tenant's active `TenantPlanAssignment` (34-4) pinned
   `(PlanId, PlanVersion)` snapshot resolved via 34-1's `IPlanCatalogService.GetByIdAsync`.

2. The resolved map is **complete and closed**: for each of the 7 `EntitlementMetricKey` members,
   if the pinned plan version has no `PlanEntitlement` row for that key, the service falls back to
   a documented, code-owned default (`LimitValue = 0`, `Period = monthly`, `OverageMode = block`)
   so consumers can index any metric without a null-check. The set of keys is exactly the closed
   enum — never a subset.

3. Per-mode principal resolution mirrors `ITammaModeProvider` + the PromptStore/Convention
   resolution order: in **SaaS** mode resolution keys by `tenant_id`; in **single-user** mode it
   keys by the sole user via `user_id` → that user's personal tenant. `EntitlementPrincipal` is a
   small discriminated record (`ForTenant(Guid tenantId)` / `ForUser(Guid userId)`). Resolution
   **never** falls back to an empty/plain entitlement set — a principal with no active assignment
   throws `TammaError("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT", ...)` (severity High); the only fallback
   tier is the per-metric documented default of AC2 within a *present* assignment.

4. `GET /api/pricing/entitlements`
   (`apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs`) returns the resolved set for the
   **current** principal (any authenticated tenant member; gated `MemberAccess`), resolving the
   tenant from `ITenantContext` (SaaS) or the sole user (single-user). The body is a
   `ResolvedEntitlementsDto` (`apps/tamma-elsa/src/Tamma.Api/Dtos/Pricing/EntitlementDtos.cs`).

5. `GET /api/admin/tenants/{tenantId}/entitlements` (gated `PlatformOwnerAccess`) returns the
   resolved set for an arbitrary tenant — the platform-owner read seam. An unknown tenant → 404; a
   tenant with no active assignment → 404 (the `NO_ASSIGNMENT` error mapped, not a 500).

6. Entitlement snapshots are **cached per principal** in an in-memory `IEntitlementSnapshotCache`
   (keyed by the resolved `tenant_id`), with TTL fallback, and are **invalidated on**
   `TENANT.PLAN.CHANGED` and `PLAN.CATALOG.UPDATED` DCB/platform events. Invalidation is wired by a
   new `EntitlementCacheInvalidationListener` that subscribes to the existing `IPlatformEventBus`
   (`Subscribe("TENANT.PLAN.")` + the catalog event prefix) — the same subscriber pattern as
   `TenantStatusInvalidationListener` / `NotificationDispatcher`. A `TENANT.PLAN.CHANGED` event for
   tenant T evicts exactly T's snapshot; a catalog-wide `PLAN.CATALOG.UPDATED` flushes the whole
   cache.

7. Custom enterprise plan entitlements resolve correctly and **override public-plan defaults**:
   when the tenant's active assignment points at an `IsCustom == true` plan version (34-2), the
   service reads that custom version's `PlanEntitlement` rows verbatim — including `LimitValue =
   NULL` (unlimited) — and the resolved set reflects the bespoke limits, not the public free/team
   defaults.

8. A typed, non-enforcing helper `EntitlementHeadroom CheckHeadroom(ResolvedEntitlements resolved,
   EntitlementMetricKey metric, long currentUsage)` returns
   `{ MetricKey, LimitValue, CurrentUsage, Remaining (null = unlimited), IsOver, OveragePercent }`
   **without blocking** — it is the single shared calc the Enforcement epic and both dashboards
   consume so over/remaining math never diverges. Unlimited (`LimitValue == null`) ⇒ `Remaining =
   null`, `IsOver = false`, regardless of usage.

9. Effort/estimate metric special cases are documented and resolved through an `IEntitlementUsageReader`
   seam so the resolver surfaces *current values* for the gauge-style metrics: `Seats` reads the
   `TenantMembership` count (`ITenantMembershipRepository.ListByTenantAsync` total), `Agents` reads
   the Epic-32 agent count (`AgentConfig` rows for the tenant), `Repos` reads the active
   `GitHubInstallationRepo` count for the tenant's installation. This story ships the reader
   interface + a control-plane-backed default implementation for these three count metrics;
   metering-backed metrics (`LlmTokens`, `WorkflowRuns`) return "unavailable" until Epic 35 supplies
   the metering reader (the headroom helper degrades to `CurrentUsage = null` for those).

10. A DCB event `ENTITLEMENT.RESOLVED.SUCCESS` is emitted (sampled / on cache-miss, not on every
    cache hit) via `IEventRepository.AppendAsync` for tenant-scope with tags `tenantId`, `planId`,
    `planVersion`, `mode`, `source` (`cache-miss|admin-read`); the failure path emits
    `ENTITLEMENT.RESOLVED.FAILED` with `reason` (`no_assignment|catalog_unavailable`). Event names
    follow the `AGGREGATE.ACTION.STATUS` convention.

11. Per-mode + per-tenant ownership is honored end-to-end. **single-user**: the sole user reads
    their own entitlements (no RBAC beyond authentication); `GET /api/pricing/entitlements` resolves
    the lone tenant. **SaaS**: any tenant member (`member`/`tenant_admin`/`tenant_owner`) may read
    `GET /api/pricing/entitlements` for their own tenant (read is not a privileged operation —
    mirrors the PromptStore "GET resolved = any member" RBAC); the admin route is
    `PlatformOwnerAccess` only; a tenant member can never read another tenant's entitlements
    (tenant is taken from `ITenantContext`, never a request body/param on the member route).

12. The resolver is **read-only and non-mutating**: it never writes `PlanEntitlement`/`Plan`/
    `TenantPlanAssignment` rows, never charges, and never blocks a workflow. It does not meter
    consumption (Epic 35) and does not enforce limits (sibling Enforcement epic) — it only resolves
    and computes headroom.

13. Unit + integration tests cover: resolution per mode (SaaS by tenant_id, single-user by
    user_id→tenant), the complete-7-keys invariant + per-metric default backfill, unlimited
    (`NULL` limit) handling through resolve + headroom, cache hit/miss + invalidation on
    `TENANT.PLAN.CHANGED` (evicts one) and `PLAN.CATALOG.UPDATED` (flush all), custom-plan override,
    headroom math (under / at / over / unlimited), the gauge-metric usage reader (seats/agents/repos
    counts), `NO_ASSIGNMENT` fail-loud (no empty fallback), RBAC matrix (member can read own,
    cross-tenant 404, admin route platform-owner-only), and **tenant isolation** (tenant A's resolve
    never returns tenant B's limits or counts).

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Services/Pricing/             # existing dir (34-1/34-4 land here)
    IEntitlementService.cs                # NEW — ResolveAsync + CheckHeadroom contract
    EntitlementService.cs                 # NEW — core resolution (catalog → resolved map)
    EntitlementModels.cs                  # NEW — EntitlementPrincipal, ResolvedEntitlements,
                                          #        ResolvedEntitlement, EntitlementHeadroom records
    IEntitlementSnapshotCache.cs          # NEW — per-tenant snapshot cache abstraction
    EntitlementSnapshotCache.cs           # NEW — IMemoryCache-backed, TTL + explicit eviction
    IEntitlementUsageReader.cs            # NEW — gauge-metric current-value seam (seats/agents/repos)
    ControlPlaneEntitlementUsageReader.cs # NEW — CP-backed default impl for the 3 count metrics
    EntitlementCacheInvalidationListener.cs # NEW — BackgroundService subscribing IPlatformEventBus
    EntitlementEventTypes.cs              # NEW — ENTITLEMENT.RESOLVED.SUCCESS / .FAILED constants
  Tamma.Api/Dtos/Pricing/                 # NEW directory
    EntitlementDtos.cs                    # NEW — ResolvedEntitlementsDto + ResolvedEntitlementDto +
                                          #        EntitlementHeadroomDto
  Tamma.Api/Endpoints/
    PricingEndpoints.cs                   # MODIFY (file introduced by 34-2) — add GET
                                          #   /api/pricing/entitlements
    Admin/AdminTenantsEndpoints.cs        # MODIFY — add GET /api/admin/tenants/{id}/entitlements
  Tamma.Api/Extensions/
    PricingServiceCollectionExtensions.cs # MODIFY (created by 34-1) — register entitlement services
  Tamma.Api/Program.cs                    # MODIFY — map the two read routes; register the listener
```

> **Boundary note (Epic 34 ↔ siblings):** this story owns *entitlement resolution + headroom* only.
> The versioned catalog (`Plan`/`PlanEntitlement`/`EntitlementMetricKey`/`IPlanCatalogService`)
> belongs to **34-1**; the active-assignment source of truth (`TenantPlanAssignment` /
> `IPlanAssignmentService.GetActiveAsync`) belongs to **34-4**; the `PricingEndpoints.cs` file
> belongs to **34-2** (this story adds one GET handler to it). Usage **metering** is **Epic 35** —
> this story reads gauge counts behind `IEntitlementUsageReader` and degrades to "unavailable" for
> metering-only metrics. **Enforcement** (blocking on a quota) is a sibling epic — this story
> resolves and computes headroom but never blocks.

### Core models (`EntitlementModels.cs`)

```csharp
namespace Tamma.Api.Services.Pricing;

using Tamma.Core.Enums;   // EntitlementMetricKey (34-1)

/// <summary>
/// Who we're resolving for. SaaS → ForTenant; single-user → ForUser (the
/// sole user, resolved to their personal tenant). Mirrors the (userId|tenantId)
/// XOR principal of the prompt store.
/// </summary>
public readonly record struct EntitlementPrincipal
{
    public Guid? TenantId { get; private init; }
    public Guid? UserId { get; private init; }
    public static EntitlementPrincipal ForTenant(Guid tenantId) => new() { TenantId = tenantId };
    public static EntitlementPrincipal ForUser(Guid userId) => new() { UserId = userId };
}

/// <summary>One resolved quota line. LimitValue null = unlimited.</summary>
public sealed record ResolvedEntitlement(
    EntitlementMetricKey MetricKey,
    long? LimitValue,
    string Period,        // monthly | total
    string OverageMode);  // block | allow | meter

/// <summary>
/// Complete, closed map: every EntitlementMetricKey member is present.
/// Carries the pinned plan coordinates so callers can audit the source.
/// </summary>
public sealed record ResolvedEntitlements(
    Guid TenantId,
    Guid PlanId,
    int PlanVersion,
    bool IsCustom,
    IReadOnlyDictionary<EntitlementMetricKey, ResolvedEntitlement> Limits)
{
    public ResolvedEntitlement Get(EntitlementMetricKey key) => Limits[key]; // never throws — closed
}

/// <summary>Non-enforcing headroom calc. Remaining null = unlimited.</summary>
public sealed record EntitlementHeadroom(
    EntitlementMetricKey MetricKey,
    long? LimitValue,
    long? CurrentUsage,
    long? Remaining,
    bool IsOver,
    double? OveragePercent);
```

### Service contract (`IEntitlementService.cs`)

```csharp
public interface IEntitlementService
{
    /// Resolve the complete entitlement set for a principal (cache-first).
    /// Throws TammaError("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT") if no active
    /// TenantPlanAssignment exists — never returns an empty/plain set.
    Task<ResolvedEntitlements> ResolveAsync(EntitlementPrincipal principal, CancellationToken ct = default);

    /// Pure, non-enforcing headroom calc shared by enforcement + dashboards.
    EntitlementHeadroom CheckHeadroom(
        ResolvedEntitlements resolved, EntitlementMetricKey metric, long currentUsage);
}
```

### Resolution algorithm (`EntitlementService.ResolveAsync`)

1. **Resolve principal → tenantId.** SaaS: `principal.TenantId` (required; from `ITenantContext`
   at the endpoint). single-user: `principal.UserId` → the user's personal tenant via the existing
   personal-tenant lookup (the `EnsurePersonalTenantMiddleware` invariant guarantees one). Mode read
   from `ITammaModeProvider`.
2. **Cache-first.** `IEntitlementSnapshotCache.TryGet(tenantId)` → return on hit.
3. **Active assignment.** `IPlanAssignmentService.GetActiveAsync(tenantId)` (34-4). `null` →
   emit `ENTITLEMENT.RESOLVED.FAILED{reason=no_assignment}`, throw
   `TammaError("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT", ..., severity: High)` — **no empty fallback**
   (mirrors prompt/convention fail-loud, `feedback_resolution_no_empty_fallback`).
4. **Pinned snapshot.** `IPlanCatalogService.GetByIdAsync(assignment.PlanId)` (34-1) — the pinned
   `(PlanId, PlanVersion)` row, immutable, so a later deprecation cannot retro-mutate. `null` →
   `ENTITLEMENT.RESOLVED.FAILED{reason=catalog_unavailable}`, throw
   `TammaError("ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE", ...)`.
5. **Build the closed map.** For each of the 7 `EntitlementMetricKey` members, take the matching
   `PlanSnapshot.Entitlements` row; if absent, backfill the documented default
   (`LimitValue=0, Period=monthly, OverageMode=block`). This guarantees AC2's complete map.
6. **Cache + event.** `cache.Set(tenantId, resolved)`; emit
   `ENTITLEMENT.RESOLVED.SUCCESS{source=cache-miss}` (cache-miss only — not on every hit).
7. **Return.**

`CheckHeadroom` is pure: `Remaining = LimitValue is null ? null : Math.Max(0, LimitValue - usage)`,
`IsOver = LimitValue is not null && usage > LimitValue`, `OveragePercent = LimitValue is > 0 ?
(double)usage / LimitValue * 100 : null`. Unlimited short-circuits to `Remaining=null, IsOver=false`.

### Gauge-metric usage reader (`IEntitlementUsageReader.cs`)

```csharp
/// Current value for gauge-style metrics, so headroom on the read API
/// reflects live counts without the caller wiring three repositories.
public interface IEntitlementUsageReader
{
    /// Returns null for metrics this reader can't answer (e.g. metering-only
    /// LlmTokens/WorkflowRuns until Epic 35 supplies its reader).
    Task<long?> GetCurrentAsync(Guid tenantId, EntitlementMetricKey metric, CancellationToken ct = default);
}
```

`ControlPlaneEntitlementUsageReader` answers the three CP-resident counts and returns `null` for
the rest:

| Metric | Source | Notes |
|---|---|---|
| `Seats` | `ITenantMembershipRepository.ListByTenantAsync(tenantId, 1, 0)` → `Total` | membership count |
| `Agents` | `AgentConfig` rows where `TenantId == tenantId` (Epic 32) | tenant agent count |
| `Repos` | `GitHubInstallationRepo` where `IsActive` for the tenant's installation | active connected repos |
| `LlmTokens`, `WorkflowRuns`, `RagStorageMb`, `BenchmarkRetentionDays` | `null` | metering-backed → Epic 35 reader |

### Cache + invalidation

`EntitlementSnapshotCache` wraps `IMemoryCache` (precedent: `DiagnosticsService`,
`InMemoryBudgetConfigProvider`) keyed `"entitlements:{tenantId}"` with a default 5-minute TTL
(belt-and-suspenders behind event invalidation) and `Invalidate(tenantId)` / `Flush()`.

`EntitlementCacheInvalidationListener : BackgroundService` subscribes the in-process
`IPlatformEventBus` (the same bus `TenantStatusInvalidationListener` and `NotificationDispatcher`
use):

- `Subscribe("TENANT.PLAN.")` — on `TENANT.PLAN.CHANGED` (34-4) read the `tenantId` tag and
  `cache.Invalidate(tenantId)`.
- `Subscribe("PLAN.CATALOG.UPDATED")` (or the 34-2 catalog-update event prefix) — `cache.Flush()`
  (a catalog edit may touch any tenant's pinned version only if re-assigned, but a flush is the safe
  cheap option). Handlers are best-effort and never throw back into the bus (matches the bus's
  per-handler catch contract).

> **Event-topology note (Story 28-1):** `ENTITLEMENT.RESOLVED.*` events append to the CP
> `DomainEvents`/`PlatformEvents` store today; when events move per-tenant the invalidation listener
> still works because it subscribes the in-process `IPlatformEventBus`, not a DB poller.

### DCB event names (`AGGREGATE.ACTION.STATUS`)

| Event | When | Tags |
|---|---|---|
| `ENTITLEMENT.RESOLVED.SUCCESS` | cache-miss resolve / admin read | `tenantId`, `planId`, `planVersion`, `mode`, `source` (`cache-miss\|admin-read`) |
| `ENTITLEMENT.RESOLVED.FAILED` | no active assignment / catalog gone | `tenantId`, `mode`, `reason` (`no_assignment\|catalog_unavailable`) |

Consumed events (not emitted here): `TENANT.PLAN.CHANGED` (34-4), `PLAN.CATALOG.UPDATED` (34-2) for
cache invalidation.

### API shape

```
# Tenant self-read — MemberAccess (any authenticated tenant member)
GET /api/pricing/entitlements
  → 200 {
      tenantId, planId, planVersion, isCustom,
      limits: [ { metricKey, limitValue|null, period, overageMode,
                  currentUsage|null, remaining|null, isOver, overagePercent|null } ]
    }
  → 404 no_active_assignment            # NO_ASSIGNMENT mapped

# Platform-owner read of any tenant — PlatformOwnerAccess
GET /api/admin/tenants/{tenantId}/entitlements
  → 200 (same body shape, tenant from route)
  → 404 unknown_tenant | no_active_assignment
```

The DTO embeds the headroom fields inline (per-metric `currentUsage/remaining/isOver`) so dashboards
get resolution + live headroom in one call. The endpoint composes `ResolveAsync` +
`IEntitlementUsageReader.GetCurrentAsync` per metric + `CheckHeadroom`.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Principal | the sole user → personal tenant (`EntitlementPrincipal.ForUser`) | the caller's tenant (`EntitlementPrincipal.ForTenant`, from `ITenantContext`) |
| Who may read `/api/pricing/entitlements` | the sole user (authenticated) | any tenant member (`MemberAccess`) — read is unprivileged, mirrors PromptStore "GET resolved = any member" |
| Who may read `/api/admin/tenants/{id}/entitlements` | the sole user (no RBAC gate, same code path) | platform owner (`PlatformOwnerAccess`) only |
| Cross-tenant guard | n/a (one tenant) | member route ignores any body/param tenant; tenant from `ITenantContext` |
| Cache key | personal tenant id | caller tenant id |
| Mode source | `ITammaModeProvider.Mode` | same |

### Integration points

- **34-4 `IPlanAssignmentService.GetActiveAsync(tenantId)`** — the pinned `(PlanId, PlanVersion)`
  source of truth.
- **34-1 `IPlanCatalogService.GetByIdAsync(planId)`** → `PlanSnapshot.Entitlements` +
  `EntitlementMetricKey` enum (reused verbatim) + `IsCustom` flag (custom-plan override, AC7).
- **`ITammaModeProvider`** (`TammaMode.cs`) — per-mode principal resolution.
- **`ITenantContext`** — caller tenant on the member route.
- **`ITenantMembershipRepository`**, **`AgentConfig`** (via `ControlPlaneDbContext`),
  **`GitHubInstallationRepo`** — gauge-metric counts.
- **`IPlatformEventBus`** — cache invalidation subscription (precedent
  `TenantStatusInvalidationListener`).
- **`IEventRepository` / `IPlatformEventPublisher`** — `ENTITLEMENT.RESOLVED.*` emission.
- **`PricingServiceCollectionExtensions.AddPlanCatalog`/`AddPricing`** — extend with
  `AddEntitlementResolution(services)` registering the service, cache, usage reader, and listener.

## Dependencies

**Internal — prerequisite:**
- Story 34-1 (Plan & Price-Book Catalog) — `EntitlementMetricKey` enum, `PlanEntitlement`,
  `IPlanCatalogService`/`PlanSnapshot`, `PricingServiceCollectionExtensions`.
- Story 34-4 (Per-Tenant Plan Assignment) — `TenantPlanAssignment`,
  `IPlanAssignmentService.GetActiveAsync`, the pinned `(PlanId, PlanVersion)` contract, and the
  `TENANT.PLAN.CHANGED` event the cache invalidates on.
- Epic 28 (control-plane tenancy) — `ControlPlaneDbContext`, `ITenantContext`,
  `EnsurePersonalTenantMiddleware` (single-user personal-tenant invariant), `IPlatformEventBus`.
- Epic 4 (DCB events) — `IEventRepository` / `IPlatformEventPublisher`.
- Epic 32 (agents) — `AgentConfig` for the `Agents` gauge count.

**Internal — blocks:**
- Epic 34 Enforcement story — consumes `IEntitlementService.ResolveAsync` + `CheckHeadroom` to gate.
- Both dashboards (`packages/dashboard`, `packages/dashboard-user`) — render limits + headroom.
- Epic 35 (Billing) — consumes resolved entitlements for overage attribution and supplies the
  metering-backed `IEntitlementUsageReader` for `LlmTokens`/`WorkflowRuns`.

**External:**
- None in this story. No Stripe — entitlements are read from the catalog, not charged. The
  `IEntitlementUsageReader` seam keeps metering (Epic 35) out of the resolve path; tests mock it.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/`), test-first:**

1. `EntitlementServiceTests.Resolve_SaaS_ByTenantId` — SaaS principal resolves the tenant's pinned
   assignment → snapshot → complete 7-key map; mock `IPlanAssignmentService` + `IPlanCatalogService`.
2. `Resolve_SingleUser_ByUserId` — single-user principal resolves the sole user's personal tenant
   (`ITammaModeProvider.Mode = SingleUser`); same closed map.
3. `Resolve_CompleteSevenKeys_AndDefaultBackfill` — a plan version with only 3 entitlement rows
   still returns all 7 keys; the 4 missing keys carry the documented default
   (`limit=0, monthly, block`).
4. `Resolve_Unlimited_NullLimit` — a `LimitValue = NULL` entitlement resolves to unlimited and flows
   through `CheckHeadroom` (`Remaining=null, IsOver=false` even with huge usage).
5. `Resolve_NoAssignment_FailsLoud` — no active assignment throws
   `ENTITLEMENT.RESOLVE.NO_ASSIGNMENT` (severity High) and emits `ENTITLEMENT.RESOLVED.FAILED`; the
   service NEVER returns an empty set (pins `feedback_resolution_no_empty_fallback`).
6. `Resolve_CustomPlan_Overrides` — an `IsCustom = true` plan version's bespoke (incl. unlimited)
   entitlements override the public free/team defaults.
7. `CheckHeadroomTests` — under / at-limit / over / unlimited matrix; `OveragePercent` math; zero
   limit edge.
8. `Cache_HitMiss` — second `ResolveAsync` for the same tenant is a cache hit (no second
   `GetActiveAsync`/`GetByIdAsync` call); only the miss emits `ENTITLEMENT.RESOLVED.SUCCESS`.
9. `CacheInvalidationListenerTests` — a `TENANT.PLAN.CHANGED` event for tenant T evicts T's
   snapshot (next resolve re-hits the catalog); a `PLAN.CATALOG.UPDATED` event flushes all entries;
   a malformed/handler-thrown event is swallowed (listener never breaks the bus).
10. `ControlPlaneEntitlementUsageReaderTests` — `Seats` returns membership count, `Agents` returns
    tenant `AgentConfig` count, `Repos` returns active `GitHubInstallationRepo` count;
    `LlmTokens`/`WorkflowRuns` return `null`.

**Integration (xUnit + Postgres via `sg docker -c "dotnet test ..."`):**

11. `EntitlementEndpointsTests.MemberRead_OwnTenant` — `GET /api/pricing/entitlements` with a
    `member` JWT returns the caller's resolved set (read is unprivileged); the body includes live
    seat/agent/repo counts.
12. `AdminRead_PlatformOwnerOnly` — `GET /api/admin/tenants/{id}/entitlements`: platform-owner JWT
    → 200; non-platform JWT → 403; unknown tenant → 404; tenant with no assignment → 404.
13. **Tenant-isolation test** — two tenants on different plan versions (one custom, one public) each
    resolve their own frozen entitlement set + their own gauge counts; tenant A's member can never
    read tenant B's entitlements (the member route uses `ITenantContext`, ignores any param); a
    `TENANT.PLAN.CHANGED` for A never evicts B's cached snapshot.
14. Version-pinning end-to-end — assign v1, deprecate (catalog creates v2), resolve again → still
    v1's entitlements (the pinned snapshot, never latest).

**Mocks:** `IPlanAssignmentService` (34-4) and `IPlanCatalogService` (34-1) faked for unit tests;
`IEntitlementUsageReader` mocked for the resolve/headroom tests and exercised for real in the CP
reader tests; `IEventRepository`/`IPlatformEventPublisher` faked to capture emitted events;
`IMemoryCache` real (in-memory) for cache tests; `TimeProvider` injected where TTL is asserted. **No
Stripe/provider mocks** — this story makes no external billing/provider calls.

## Estimated Effort

4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IEntitlementService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementModels.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IEntitlementSnapshotCache.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementSnapshotCache.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IEntitlementUsageReader.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/ControlPlaneEntitlementUsageReader.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementCacheInvalidationListener.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/EntitlementEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Pricing/EntitlementDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify (add GET /api/pricing/entitlements; file from 34-2) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` | Modify (add GET /api/admin/tenants/{id}/entitlements) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Modify (AddEntitlementResolution) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map 2 routes; register invalidation listener; DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/EntitlementServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/CheckHeadroomTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/EntitlementSnapshotCacheTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/EntitlementCacheInvalidationListenerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/ControlPlaneEntitlementUsageReaderTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/EntitlementEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (pricing, caching, resolution
   fail-loud).
3. Reviewed **34-1** and **34-4** stories — this story is strictly downstream of their catalog +
   assignment contracts; do NOT re-implement the catalog, the assignment, or the metric enum here.
4. Reviewed the cache-invalidation precedent (`TenantStatusInvalidationListener`,
   `NotificationDispatcher`) and the per-mode resolution precedent (`PromptStoreService`,
   `TammaMode.cs`).
5. Confirmed the C# test runner contract: `sg docker -c "dotnet test ..."` for docker-bound suites
   (build needs no wrapper) — see `reference_dotnet_test_docker`.
6. Planned a TDD (Red-Green-Refactor) approach — a failing test per AC before implementation.

### Key Design Decisions

- **One resolution seam, one headroom calc.** The whole point is that Enforcement and both
  dashboards never re-derive limits or over/remaining math independently. `CheckHeadroom` is pure
  and shared so a typo can't make the dashboard and the enforcer disagree about who's over quota.
- **Closed, complete map (never a subset).** Consumers index any `EntitlementMetricKey` without a
  null check. Missing catalog rows backfill a documented default (`limit 0, block`) — a missing
  entitlement is the *safest* (most restrictive) default, but resolution itself still fails loud if
  the *assignment* is absent.
- **Fail loud on no assignment, never on a missing metric row.** Mirrors the prompt/convention
  contract (`feedback_resolution_no_empty_fallback`): `tenant → catalog → error`, never an empty or
  "plain" entitlement set. A tenant with no active plan is a real bug Billing/Enforcement must see.
- **Pin via 34-4, snapshot via 34-1.** Resolve the *pinned* `(PlanId, PlanVersion)`, not "the
  latest" — a plan deprecation must never silently re-limit an existing tenant. The catalog snapshot
  is immutable for deprecated versions, so the pinned read is reproducible forever.
- **Event-driven cache, not DB poll.** Subscribe the in-process `IPlatformEventBus` rather than
  polling — invalidation is instant on `TENANT.PLAN.CHANGED` and survives the Story-28-1 event
  topology shift (the bus is in-process, the poller would not be).
- **Gauge counts behind a seam.** Seats/agents/repos are CP-resident counts answerable now;
  tokens/runs are metering-only and stay `null` until Epic 35. The headroom helper degrades
  gracefully (`CurrentUsage = null`) rather than blocking the read API on Epic 35.

### Boundary Notes (what this story does NOT do)

- No usage metering writes (Epic 35) — it only *reads* gauge counts behind `IEntitlementUsageReader`
  and returns `null` for metering-only metrics.
- No quota **enforcement** / blocking (sibling Enforcement epic) — it resolves + computes headroom;
  it never blocks a workflow.
- No Stripe / billing / charging (Epic 35).
- No catalog or assignment mutation (34-1 / 34-2 / 34-4) — strictly read-only.
- No dashboard UI (that consumes this API in a later story / the dashboards).

### Edge Cases

- Single-user mode where the personal tenant has the back-filled `plan_free` assignment (34-4
  migration) → resolves the free entitlements; never `NO_ASSIGNMENT` for a normally-provisioned
  instance.
- A `PLAN.CATALOG.UPDATED` flush during a burst of resolves → next resolve is a cache miss and
  re-reads the (still pinned) snapshot; correctness is unaffected, only one extra catalog read.
- Cache TTL expiry independent of events → a stale-but-pinned snapshot can never be *wrong* (pinned
  versions are immutable), so the TTL is purely a memory bound, not a correctness mechanism.
- An `IEntitlementUsageReader` throwing for one metric → headroom for that metric degrades to
  `CurrentUsage = null` (logged WARN); the rest of the set still resolves.

## Logging Requirements

- **INFO**: entitlements resolved on cache miss (`tenantId`, `planId`, `planVersion`, `mode`),
  admin entitlement read (`tenantId`, actor), cache flushed/invalidated (`tenantId` or `all`).
- **DEBUG**: cache hit (`tenantId`), per-metric default backfill (`metricKey`), usage-reader lookup
  result (`metricKey`, value), invalidation event received (`eventType`, `tenantId`).
- **WARN**: usage reader threw for a metric (degraded to null), resolve for a tenant whose snapshot
  had fewer than 7 entitlement rows (backfilled), invalidation handler swallowed an exception.
- **ERROR**: `ENTITLEMENT.RESOLVE.NO_ASSIGNMENT` before the throw, catalog unavailable for a pinned
  `planId` (should be impossible — log the unexpected gap), event-emission failure.
- **Structured context**: include `{ tenantId, planId, planVersion, metricKey, mode, source }`
  where applicable.
- **Credential safety**: entitlement data is not secret, but never log connection strings or tenant
  secrets if a resolution path ever touches a tenant row.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
