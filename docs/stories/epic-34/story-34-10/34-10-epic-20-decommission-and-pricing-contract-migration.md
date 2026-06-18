# Story 34-10: Epic 20 Decommission & Pricing Contract Migration

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide covers the 7-phase workflow (Read → Research → Break Down → TDD → Quality
Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging, test-first
development, 100% coverage on critical paths, and build-success enforcement. Failure to follow it
results in rework.

## User Story

As a **platform owner (and, downstream, the Billing and Enforcement epic authors)**,
I want the stale TypeScript Epic 20 plan/pricing surface formally retired, every existing tenant
and plan back-filled from the legacy opaque `Plan.Quotas` JSON and loose `Tenant.Plan` string onto
the new structured catalog (34-1 `PlanEntitlement`) and assignment model (34-4
`TenantPlanAssignment`), and a single stable read-contract published as the ONLY surface
Billing (Epic 35) and the Enforcement epic call into,
so that no consumer reaches into pricing entities directly, no tenant is ever left without an
active plan assignment, and the cut-over from the JSON era is re-runnable, reproducible, and
verifiable on a seeded environment with zero errors.

## Priority

P0 — This is the capstone that makes Epic 34 a stable, consumable foundation. Until the legacy
`Plan.Quotas` JSON and `Tenant.Plan` string are back-filled into structured rows AND a single
read-contract is published, Billing (Epic 35) and Enforcement would either re-derive limits from
the old JSON (drift) or wire directly into 34-1/34-4/34-5/34-6/34-7 entities (coupling). This story
freezes a façade and migrates the data so every downstream epic builds on one surface.

## Acceptance Criteria

1. A re-runnable, idempotent **back-fill** (EF migration data step + a guarded
   `PricingBackfillService` invoked at startup, insert-missing-only) converts every populated legacy
   `Plan.Quotas` JSON blob into typed `PlanEntitlement` rows (34-1) keyed by `EntitlementMetricKey`,
   mapping the historic JSON keys (`llmTokensPerMonth → llm_tokens`, `concurrentWorkflows →`
   *(dropped — not an entitlement metric, logged WARN)*, `seats → seats`, where a legacy `-1`
   sentinel becomes `LimitValue = NULL` = unlimited). Plans already carrying structured
   `PlanEntitlement` rows from the 34-1 seeder are left untouched (no double-insert).

2. The same back-fill creates exactly one `active` `TenantPlanAssignment` (34-4) for every existing
   non-deleted tenant, derived from its current `Tenant.PlanId` shadow column (preferred) or, when
   NULL, from the `Plan` whose `Slug` matches the legacy `Tenant.Plan` string, falling back to
   `plan_free`; `PlanVersion` is pinned from the resolved `Plan.Version`. After the back-fill **no
   non-deleted tenant lacks an active assignment** (asserted by a verification query that returns
   zero rows).

3. The Epic 20 stories whose plan/pricing scope is now owned by Epic 34 are marked **`superseded`**
   in `docs/sprint-status.yaml` with an inline note pointing at the Epic 34 owner story: `20-1`
   (plan model → 34-1/34-4) and `20-4` (usage-limits-enforcement plan/quota model → 34-1/34-6 +
   sibling Enforcement epic). Stripe-specific subscription/checkout/portal/webhook scope
   (`20-2`), usage metering (`20-3`), and billing dashboard (`20-5`) are explicitly **re-homed to
   Epic 35 (Billing), NOT Epic 34** — their status notes are updated to say so but they are NOT
   marked superseded by this story (Epic 35 owns their retirement).

4. A published `IPricingContract` façade
   (`apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingContract.cs`) is the ONLY surface
   Billing (Epic 35) and Enforcement call into. It exposes four read seams that delegate to the
   already-built Epic 34 services — `ResolvePlanAsync` (→ 34-1 `IPlanCatalogService` +
   34-4 `IPlanAssignmentService.GetActiveAsync`), `ResolveEntitlementsAsync` (→ 34-6
   `IEntitlementService.ResolveAsync`), `PriceUsageAsync` (→ 34-5 `IUsagePricingEngine.PriceUsage`
   + 34-5 `IMarginPolicyResolver`), and `PriceNetAsync` (→ 34-7
   `ICreditAwarePricingEngine.PriceNetAsync`) — returning the published contract DTOs in
   `Tamma.Api/Dtos/Pricing/PricingContractDtos.cs`.

5. The contract is documented as a stable surface in a new
   `docs/stories/epic-34/pricing-contract.md` that states, in one place, the four operations, their
   input/output shapes, the `PricingMode` (`platform_provided` | `byok`, 34-1/34-3) semantics, and
   the explicit rule that **no other epic may take a direct dependency on `Plan`, `PlanEntitlement`,
   `PlanPrice`, `TenantPlanAssignment`, `IUsagePricingEngine`, `IEntitlementService`, or
   `ICreditAwarePricingEngine`** — every cross-epic read goes through `IPricingContract`.

6. **Contract conformance tests** (`tests/Tamma.Api.Tests/Pricing/PricingContractTests.cs`) assert
   the façade returns stable, complete shapes: `ResolveEntitlementsAsync` always returns all 7
   `EntitlementMetricKey` members (closed map, mirrors 34-6 AC2); `ResolvePlanAsync` returns the
   pinned `(PlanId, PlanVersion)` not "latest" after a deprecation; `PriceUsageAsync` honors
   `PricingMode` end-to-end (a `byok` line returns `SellPriceUsd == CostBasisUsd` token markup of
   zero per 34-5, a `platform_provided` line applies the markup); `PriceNetAsync` returns a
   `NetPriceResult` whose `NetPriceUsd == SellPriceUsd − PromoDiscountUsd − CreditsAppliedUsd`
   (34-7 invariant).

7. A **migration-ordering note** mirroring `docs/stories/migration-ordering.md` records the
   control-plane vs tenant-schema ordering for the new pricing tables (`plans` column adds,
   `plan_features`, `plan_entitlements`, `plan_prices` from 34-1; `tenant_plan_assignments` from
   34-4; plus this story's back-fill data step). All pricing tables are **control-plane resident**
   (catalog + assignment are global/CP rows) — the note explicitly states there is NO per-tenant
   schema (`t_<hex>`) pricing table, and that the back-fill data step runs **after** the 34-1 and
   34-4 schema migrations have applied.

8. A **smoke test** on a seeded environment (the docker integration suite) provisions a `free`, a
   `team`, and an `enterprise` tenant, runs the full back-fill, then calls all four contract
   operations for each tenant and asserts **zero errors**: each resolves a plan, a complete
   entitlement set, a priced usage line, and a net price post-migration.

9. **Rollback / re-run safety** is documented and tested: per CLAUDE.md "no-migration-anxiety", the
   app is pre-production, so the contract is that the back-fill is **idempotent and re-runnable** —
   a second invocation is a verified no-op (`PricingBackfillService` second run inserts zero rows,
   emits no events, returns a `BackfillReport` with all-zero insert counts). The story documents
   that "rollback" = drop-and-reseed (no production data to preserve), not a reversible down-migration
   of data.

10. The back-fill emits DCB / platform-audit events: `PRICING.BACKFILL.STARTED` (once per run, tags
    `runId`, `mode`, `source=startup|admin`), `PRICING.BACKFILL.COMPLETED` (tags `runId`,
    `plansConverted`, `tenantsAssigned`, `entitlementsCreated`, `durationMs`), and
    `PRICING.BACKFILL.SKIPPED` when a prior completed run is detected (idempotent no-op path). Events
    append via `IPlatformEventPublisher.AppendAndPublishAsync` to `platform_events` (CP-resident).

11. The legacy `Plan.Quotas` string column and `Tenant.Plan` string remain on the row (per 34-1's
    one-deprecation-window decision) but are **no longer read by any pricing path** after this story:
    a `PricingContract`-only read rule is enforced by the conformance tests, and the legacy
    `AdminTenantsEndpoints.UpdateTenantPlan` path (kept working by 34-1/34-4) is verified to flow
    through `IPlanAssignmentService` (34-4), never re-deriving from `Quotas`.

12. Per-mode + per-tenant ownership is honored: the catalog/contract is **platform-owned and global
    in both modes** (single-user and SaaS) — there is no tenant-scoped pricing table. The back-fill
    runs once per deployment regardless of mode (it operates on CP rows). `IPricingContract` reads
    resolve per-mode via the underlying services' existing `ITammaModeProvider` handling (SaaS by
    `tenant_id`; single-user by the sole user → personal tenant); the façade adds no new RBAC of its
    own — callers (Epic 35/Enforcement) are already gated. The admin re-run trigger (AC10
    `source=admin`) is `PlatformOwnerAccess` only.

13. Unit + integration tests cover: legacy-JSON → `PlanEntitlement` mapping (incl. `-1 → NULL`
    unlimited and the dropped `concurrentWorkflows` WARN), tenant back-fill from `PlanId` /
    `Slug` / `plan_free` fallback chain, the zero-tenant-without-assignment invariant, second-run
    no-op idempotency, the four contract operations' shape stability + `PricingMode` honoring,
    pinned-version-survives-deprecation through the façade, the `BackfillReport` counts + events, and
    **tenant isolation** (the back-fill assigns each tenant only its own plan; no cross-tenant row).

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Services/Pricing/                 # dir created by 34-1; this story adds the façade + back-fill
    IPricingContract.cs                        # NEW — the published 4-op read façade (Tamma.Api.Services.Pricing)
    PricingContract.cs                         # NEW — delegates to 34-1/34-4/34-5/34-6/34-7 services
    IPricingBackfillService.cs                 # NEW — re-runnable back-fill orchestration
    PricingBackfillService.cs                  # NEW — JSON→entitlements + tenant→assignment back-fill
    PricingBackfillModels.cs                   # NEW — BackfillReport, LegacyQuotaMap records
    PricingBackfillEventTypes.cs               # NEW — PRICING.BACKFILL.STARTED/.COMPLETED/.SKIPPED
  Tamma.Api/Dtos/Pricing/
    PricingContractDtos.cs                     # NEW — PlanContractView, EntitlementContractView,
                                               #        PricedUsageView, NetPriceView (stable wire shapes)
  Tamma.Api/Endpoints/
    Admin/AdminPricingBackfillEndpoint.cs      # NEW — POST /api/admin/pricing/backfill (re-run; PlatformOwnerAccess)
  Tamma.Api/Extensions/
    PricingServiceCollectionExtensions.cs      # MODIFY (created by 34-1) — AddPricingContract + AddPricingBackfill
  Tamma.Api/Program.cs                          # MODIFY — register façade + back-fill hosted-run; map admin re-run route
  Tamma.Data/Migrations/ControlPlane/
    <ts>_PricingBackfillDataStep.cs            # NEW — additive data-only migration (JSON→entitlements, tenant→assignment)
docs/stories/epic-34/
  pricing-contract.md                          # NEW — published stable-contract doc (AC5)
  pricing-migration-ordering.md                # NEW — CP-vs-tenant ordering note (AC7), mirrors docs/stories/migration-ordering.md
docs/sprint-status.yaml                         # MODIFY — Epic 20 supersede/re-home notes (AC3); add epic-34 block
```

> **Boundary note (Epic 34 ↔ siblings):** this story **owns the façade + the data back-fill + the
> Epic 20 retirement bookkeeping only**. It does NOT re-implement any pricing logic — the catalog
> (34-1), assignment (34-4), markup engine (34-5), entitlement resolution (34-6), and credit-aware
> net price (34-7) are *consumed*, never re-built. It does NOT touch Stripe / subscriptions /
> checkout / metering / dashboards — that scope is **re-homed to Epic 35 (Billing)** and only its
> *status note* is updated here. It does NOT implement quota enforcement (sibling Enforcement epic).

### The published façade (`IPricingContract.cs`)

The whole point of the story: one surface, four operations, delegating to services that already
exist after 34-1/34-4/34-5/34-6/34-7.

```csharp
namespace Tamma.Api.Services.Pricing;

using Tamma.Api.Dtos.Pricing;

/// <summary>
/// THE stable read-contract for pricing. Epic 35 (Billing) and the Enforcement
/// epic call ONLY these four methods — never the underlying catalog/assignment/
/// engine services directly. Adding a method here is a deliberate contract
/// change; reaching past it is a layering violation (see pricing-contract.md).
/// </summary>
public interface IPricingContract
{
    /// Resolve the tenant's pinned, effective plan (NOT "latest" — survives deprecation).
    Task<PlanContractView> ResolvePlanAsync(Guid tenantId, CancellationToken ct = default);

    /// Resolve the complete, closed 7-key entitlement set for the tenant.
    Task<EntitlementContractView> ResolveEntitlementsAsync(Guid tenantId, CancellationToken ct = default);

    /// Price one usage line at LIST price (cost basis × markup), honoring PricingMode.
    Task<PricedUsageView> PriceUsageAsync(Guid tenantId, UsageLine line, CancellationToken ct = default);

    /// Price one usage line NET of promos + credits + trial (the charge Billing issues).
    Task<NetPriceView> PriceNetAsync(Guid tenantId, UsageLine line, CancellationToken ct = default);
}
```

`PricingContract` implementation (delegation only — no business logic):

```csharp
public sealed class PricingContract(
    IPlanCatalogService catalog,            // 34-1
    IPlanAssignmentService assignments,     // 34-4
    IEntitlementService entitlements,       // 34-6
    IUsagePricingEngine usageEngine,        // 34-5 (pure)
    IMarginPolicyResolver marginResolver,   // 34-5 (scoped, reads CP)
    ICreditAwarePricingEngine creditEngine, // 34-7
    ILogger<PricingContract> logger) : IPricingContract
{
    public async Task<PlanContractView> ResolvePlanAsync(Guid tenantId, CancellationToken ct)
    {
        var active = await assignments.GetActiveAsync(tenantId, ct)
            ?? throw new TammaError("PRICING.CONTRACT.NO_ASSIGNMENT", $"tenant {tenantId} has no active plan", severity: ErrorSeverity.High);
        var snapshot = await catalog.GetByIdAsync(active.PlanId, ct)  // pinned (PlanId, PlanVersion)
            ?? throw new TammaError("PRICING.CONTRACT.CATALOG_UNAVAILABLE", $"plan {active.PlanId} not found", severity: ErrorSeverity.High);
        return PlanContractView.From(snapshot, active);
    }

    public async Task<EntitlementContractView> ResolveEntitlementsAsync(Guid tenantId, CancellationToken ct)
        => EntitlementContractView.From(await entitlements.ResolveAsync(EntitlementPrincipal.ForTenant(tenantId), ct));

    public async Task<PricedUsageView> PriceUsageAsync(Guid tenantId, UsageLine line, CancellationToken ct)
    {
        var policy = await marginResolver.ResolveAsync(tenantId, line.Provider, ct);   // 34-5 — tenant/provider margin + PricingMode
        return PricedUsageView.From(usageEngine.PriceUsage(line, policy));             // 34-5 — pure
    }

    public async Task<NetPriceView> PriceNetAsync(Guid tenantId, UsageLine line, CancellationToken ct)
        => NetPriceView.From(await creditEngine.PriceNetAsync(line, tenantId, ct));     // 34-7
}
```

> The façade is a thin, total adapter. It does NOT cache (34-6 already caches entitlement
> snapshots), does NOT meter (Epic 35), and does NOT enforce (Enforcement). Resolution stays
> fail-loud — a tenant with no active assignment throws `PRICING.CONTRACT.NO_ASSIGNMENT`
> (never an empty/plain result, per `feedback_resolution_no_empty_fallback`).

### Contract DTOs (`PricingContractDtos.cs`)

Stable wire shapes so a future internal refactor of the underlying services cannot break Billing:

```csharp
namespace Tamma.Api.Dtos.Pricing;

public sealed record PlanContractView(
    Guid TenantId, Guid PlanId, string Slug, string DisplayName,
    int PlanVersion, bool IsCustom, string BillingInterval, string Status);

public sealed record EntitlementContractView(
    Guid TenantId, Guid PlanId, int PlanVersion, bool IsCustom,
    IReadOnlyList<EntitlementLineView> Limits);   // always all 7 EntitlementMetricKey members

public sealed record EntitlementLineView(
    string MetricKey, long? LimitValue, string Period, string OverageMode);

public sealed record PricedUsageView(
    decimal CostBasisUsd, decimal MarginUsd, decimal SellPriceUsd, string PricingMode);

public sealed record NetPriceView(
    decimal SellPriceUsd, decimal PromoDiscountUsd, decimal CreditsAppliedUsd,
    decimal NetPriceUsd, bool TrialWaived);
```

### Back-fill orchestration (`PricingBackfillService.cs`)

```csharp
public interface IPricingBackfillService
{
    /// Idempotent. Converts legacy Plan.Quotas → PlanEntitlement rows and creates
    /// one active TenantPlanAssignment per existing tenant. Re-runnable: a second
    /// run inserts nothing and emits PRICING.BACKFILL.SKIPPED.
    Task<BackfillReport> RunAsync(BackfillTrigger trigger, CancellationToken ct = default);
}

public sealed record BackfillReport(
    Guid RunId, int PlansConverted, int EntitlementsCreated, int TenantsAssigned,
    int SkippedTenants, bool WasNoOp, long DurationMs);

public enum BackfillTrigger { Startup, Admin }
```

Algorithm:

1. **Detect prior completion.** If a `PRICING.BACKFILL.COMPLETED` platform event exists AND every
   non-deleted tenant already has an active `TenantPlanAssignment` AND every active plan already has
   `PlanEntitlement` rows → emit `PRICING.BACKFILL.SKIPPED`, return `WasNoOp = true`. (Belt: the
   service is insert-missing-only anyway, so a partial prior run self-heals on re-run.)
2. **Plan quotas → entitlements** (insert-missing-only): for each active `Plan` with **no**
   `PlanEntitlement` rows, parse `Plan.Quotas` JSON via `LegacyQuotaMap`, emit one `PlanEntitlement`
   per recognized key (`llmTokensPerMonth → LlmTokens`, `seats → Seats`), `-1 → LimitValue = null`,
   `Period = monthly`, `OverageMode = block`; log WARN for unmapped keys
   (`concurrentWorkflows` is not an `EntitlementMetricKey`).
3. **Tenant → assignment** (insert-missing-only): for each non-deleted `Tenant` with no active
   `TenantPlanAssignment`, resolve plan via `Tenant.PlanId` shadow → else `Plan.Slug == Tenant.Plan`
   → else `plan_free`; insert one `active` row with pinned `PlanVersion`, delegating to
   `IPlanAssignmentService.AssignAsync` (so the tenant `PlanId`/`Plan` columns stay in lockstep and
   `TENANT.PLAN.CHANGED` audit is consistent) **or** a direct insert with `Reason = "backfill: 34-10"`
   when delegating would mis-classify `direction` for a first assignment (the migration data step
   uses raw SQL; the service path is used for the startup `PricingBackfillService` so audit events
   fire — see Edge Cases).
4. **Verify invariant.** Run the AC2 check query; if any non-deleted tenant lacks an active
   assignment, throw `PRICING.BACKFILL.INCOMPLETE` (severity Critical) — fail the run loudly.
5. **Emit** `PRICING.BACKFILL.COMPLETED` with counts + duration; return the `BackfillReport`.

`PricingBackfillService.RunAsync(Startup)` is invoked once at API startup after `PlansSeeder.SeedAsync`
and the 34-4 migration back-fill (defense-in-depth: the EF migration data step does the bulk; the
startup service heals any tenant created between migration and a later code deploy, and emits the
audit events the raw-SQL migration cannot).

### EF migration sketch (data-only, additive)

```csharp
// <ts>_PricingBackfillDataStep.cs  (Tamma.Data.Migrations.ControlPlane)
public override void Up(MigrationBuilder migrationBuilder)
{
    // 1. Legacy Quotas JSON → plan_entitlements (only for plans with no rows yet).
    //    Uses Postgres jsonb extraction; -1 sentinel → NULL (unlimited).
    migrationBuilder.Sql(@"
      INSERT INTO plan_entitlements (""Id"",""PlanId"",""MetricKey"",""LimitValue"",""Period"",""OverageMode"")
      SELECT gen_random_uuid(), p.""Id"", 'llm_tokens',
             NULLIF((p.""Quotas""::jsonb->>'llmTokensPerMonth')::bigint, -1),
             'monthly','block'
      FROM plans p
      WHERE p.""Quotas"" IS NOT NULL AND p.""Quotas"" <> '{}'
        AND (p.""Quotas""::jsonb ? 'llmTokensPerMonth')
        AND NOT EXISTS (SELECT 1 FROM plan_entitlements e
                        WHERE e.""PlanId"" = p.""Id"" AND e.""MetricKey"" = 'llm_tokens');
      -- (repeat for seats → 'seats'; concurrentWorkflows intentionally NOT mapped)
    ");

    // 2. One active tenant_plan_assignment per non-deleted tenant (NOT EXISTS guard = idempotent).
    migrationBuilder.Sql(@"
      INSERT INTO tenant_plan_assignments
        (""Id"",""TenantId"",""PlanId"",""PlanVersion"",""Status"",""EffectiveFrom"",
         ""AssignedByUserId"",""Reason"",""CreatedAt"",""UpdatedAt"")
      SELECT gen_random_uuid(), t.""Id"",
             COALESCE(t.""PlanId"", s.""Id"", f.""Id""),
             COALESCE(p.""Version"", s.""Version"", f.""Version"", 1),
             'active', now(), NULL, 'backfill: 34-10', now(), now()
      FROM tenants t
      LEFT JOIN plans p ON p.""Id"" = t.""PlanId""
      LEFT JOIN plans s ON s.""Slug"" = t.""Plan"" AND s.""Status"" = 'active'
      LEFT JOIN plans f ON f.""Slug"" = 'free' AND f.""Status"" = 'active'
      WHERE t.""DeletedAt"" IS NULL
        AND NOT EXISTS (SELECT 1 FROM tenant_plan_assignments a
                        WHERE a.""TenantId"" = t.""Id"" AND a.""Status"" = 'active');
    ");
}

public override void Down(MigrationBuilder migrationBuilder)
{
    // Pre-production, no-migration-anxiety: data Down deletes only backfill-stamped rows.
    migrationBuilder.Sql("DELETE FROM tenant_plan_assignments WHERE \"Reason\" = 'backfill: 34-10';");
    // plan_entitlements created by backfill are left (the 34-1 seeder owns canonical rows).
}
```

> This is a **data-only** migration — no DDL, no CHECK edits — so `has-pending-model-changes` must
> report none after it is added. It depends on the 34-1 (`plan_entitlements`) and 34-4
> (`tenant_plan_assignments`) schema migrations already being applied (ordering is recorded in
> `pricing-migration-ordering.md`, AC7). The `NOT EXISTS` guards make a re-apply on a partially
> migrated DB a no-op (AC9).

### DCB / platform-audit event names (`AGGREGATE.ACTION.STATUS`)

| Event | When | Tags / data |
|---|---|---|
| `PRICING.BACKFILL.STARTED` | run begins | tags `runId`, `mode`, `source` (`startup`\|`admin`) |
| `PRICING.BACKFILL.COMPLETED` | run finishes (rows possibly inserted) | tags `runId`; data `plansConverted`, `tenantsAssigned`, `entitlementsCreated`, `durationMs` |
| `PRICING.BACKFILL.SKIPPED` | prior completed run detected → no-op | tags `runId`, `reason=already_complete` |

All append via `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)` to the CP-resident
`platform_events` log (the `AlertRuleEvaluator` already polls it, so a later story can alert on a
failed/incomplete back-fill with no new plumbing). Consumed (not emitted) by this story:
`TENANT.PLAN.CHANGED` (34-4) when the startup service path delegates to `AssignAsync`.

### API shape (admin re-run only — reads go through the in-process façade)

```
# Platform-owner re-run trigger (PlatformOwnerAccess) — for ops after a manual seed/restore
POST /api/admin/pricing/backfill
  → 200 { runId, plansConverted, entitlementsCreated, tenantsAssigned, skippedTenants, wasNoOp, durationMs }
  → 409 backfill_in_progress       # a single-flight lock prevents concurrent runs
```

`IPricingContract` itself is an **in-process service**, not an HTTP endpoint — Billing/Enforcement
consume it via DI. (34-6 already ships the tenant-facing `GET /api/pricing/entitlements` read API;
this story does not duplicate it.)

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Catalog / contract ownership | platform-global rows; sole user reads via the façade | platform-global rows; tenants read via the underlying 34-6 API |
| Back-fill scope | runs once on CP rows (the lone tenant gets one assignment) | runs once on CP rows (every tenant gets one assignment) |
| Façade per-mode resolution | delegates to 34-6/34-4 which key by sole-user → personal tenant | delegates which key by `tenant_id` |
| Re-run trigger RBAC | the sole user (authenticated; same code path) | `PlatformOwnerAccess` (platform owner) only |
| Tenant-scoped pricing table? | none — global catalog | none — global catalog |
| Mode source | `ITammaModeProvider` (via underlying services) | same |

### Integration points

- **34-1 `IPlanCatalogService` / `PlanSnapshot` / `EntitlementMetricKey`** — pinned catalog reads,
  the quota-key enum the back-fill maps legacy JSON onto.
- **34-4 `IPlanAssignmentService.GetActiveAsync` / `AssignAsync` / `TenantPlanAssignment`** — the
  active-assignment source of truth; the startup back-fill path delegates to `AssignAsync`.
- **34-5 `IUsagePricingEngine.PriceUsage` / `IMarginPolicyResolver` / `UsageLine` / `PricedUsage`** —
  list-price computation with `PricingMode`.
- **34-6 `IEntitlementService.ResolveAsync` / `ResolvedEntitlements`** — complete entitlement set.
- **34-7 `ICreditAwarePricingEngine.PriceNetAsync` / `NetPriceResult`** — credit-aware net price.
- **`IPlatformEventPublisher` / `PlatformEvent`** — back-fill audit events.
- **`PlansSeeder`** — runs first; back-fill is insert-missing-only so it never conflicts with seeded
  structured rows (34-1 extends the seeder).
- **`PricingServiceCollectionExtensions`** — `AddPricingContract` + `AddPricingBackfill` extend the
  34-1 DI module; `Program.cs` composition root wires the startup run + admin route.

## Dependencies

**Internal — prerequisite (hard):**
- Story 34-1 (Plan & Price-Book Catalog) — `Plan` versioning, `PlanEntitlement`, `PlanPrice`,
  `EntitlementMetricKey`, `IPlanCatalogService`, `PlanSnapshot`, `PricingServiceCollectionExtensions`,
  the `Services/Pricing/` directory.
- Story 34-3 (BYOK vs Platform-Provided Pricing Mode) — the `PricingMode` (`byok`|`platform`)
  semantics the contract's `PriceUsageAsync` must honor end-to-end.
- Story 34-4 (Per-Tenant Plan Assignment) — `TenantPlanAssignment`, `IPlanAssignmentService`, the
  back-fill-one-active-per-tenant pattern (this story extends/heals it).
- Story 34-5 (Cost→Price Markup Engine) — `IUsagePricingEngine`, `IMarginPolicyResolver`,
  `UsageLine`, `PricedUsage`.
- Story 34-6 (Entitlement & Quota Resolution) — `IEntitlementService.ResolveAsync`,
  `ResolvedEntitlements`, the closed 7-key map.
- Epic 28 (control-plane tenancy) — `ControlPlaneDbContext`, `Tenant` shadow `PlanId`,
  `PlansSeeder`, the `Migrations/ControlPlane/` pipeline.
- Epic 4 (DCB events) — `PlatformEvent`, `IPlatformEventPublisher`.

**Internal — soft prerequisite:**
- Story 34-7 (Trials, Credits & Promo Codes) — `ICreditAwarePricingEngine` / `NetPriceResult`. If
  34-7 has not landed, `PriceNetAsync` is registered against a pass-through default
  (`NetPriceUsd == SellPriceUsd`, all discounts zero) and the façade method is feature-flagged; the
  conformance test for `PriceNetAsync` is skipped until 34-7 ships. (Documented in
  `pricing-contract.md`.)

**Internal — blocks:**
- Epic 35 (Billing) — charges against `IPricingContract.PriceNetAsync` + `ResolveEntitlementsAsync`;
  this story is its only sanctioned entry point.
- Epic 34 Enforcement story — gates on `IPricingContract.ResolveEntitlementsAsync`.

**External:**
- PostgreSQL 17 (jsonb extraction in the back-fill SQL, partial unique indexes from 34-1/34-4).
- EF Core 9 / Npgsql (data-only migration).
- **No Stripe dependency** — pricing data is migrated and read, never charged (Epic 35 owns Stripe).
  Conformance/smoke tests mock no Stripe; provider cost basis comes from `ProviderPricingService`
  (existing) which the 34-5 engine consumes.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/`), test-first:**

1. `LegacyQuotaMapTests` — `{"llmTokensPerMonth":2000000,"seats":10}` → two `PlanEntitlement`
   specs (`llm_tokens=2000000`, `seats=10`); `seats: -1` → `LimitValue = null` (unlimited);
   `concurrentWorkflows` present → not mapped + WARN logged; empty/`{}` Quotas → no rows.
2. `PricingBackfillServiceTests.Run_ConvertsQuotas_InsertMissingOnly` — a plan with legacy JSON and
   no structured rows gets entitlements; a plan already carrying 34-1-seeded rows is untouched.
3. `Run_AssignsEveryTenant_FallbackChain` — tenant with `PlanId` → that plan; tenant with NULL
   `PlanId` but `Plan="team"` → team plan; tenant with neither → `plan_free`; all pin `PlanVersion`.
4. `Run_ZeroTenantWithoutAssignment_Invariant` — after a run, the AC2 verification query returns
   zero; a deliberately-skipped tenant makes the run throw `PRICING.BACKFILL.INCOMPLETE`.
5. `Run_SecondRun_IsNoOp` — second `RunAsync` inserts zero rows, returns `WasNoOp = true`, emits
   `PRICING.BACKFILL.SKIPPED`, emits no `COMPLETED` (idempotency, AC9).
6. `Run_EmitsEvents` — first run emits `STARTED` then `COMPLETED` with correct counts/duration tags
   (assert against a fake `IPlatformEventPublisher`).
7. `PricingContractTests.ResolveEntitlements_ReturnsAllSevenKeys` — façade always returns the closed
   7-key map (delegates to a faked `IEntitlementService`).
8. `PricingContract_ResolvePlan_PinnedNotLatest` — assign v1, deprecate (catalog at v2), façade
   `ResolvePlanAsync` still returns v1 (mock 34-1/34-4).
9. `PricingContract_PriceUsage_HonorsPricingMode` — a `byok` margin policy → `SellPriceUsd ==
   CostBasisUsd` (zero token markup, 34-5 rule); a `platform_provided` policy applies the multiplier.
10. `PricingContract_PriceNet_Invariant` — `NetPriceUsd == SellPriceUsd − PromoDiscountUsd −
    CreditsAppliedUsd` (mock 34-7 `ICreditAwarePricingEngine`); pass-through default when 34-7 absent.
11. `PricingContract_NoAssignment_FailsLoud` — a tenant with no active assignment throws
    `PRICING.CONTRACT.NO_ASSIGNMENT`, never an empty result (`feedback_resolution_no_empty_fallback`).

**Integration (xUnit + Postgres via `sg docker -c "dotnet test ..."`):**

12. `PricingBackfillMigrationTests` — apply 34-1 + 34-4 + this data migration to a clean DB seeded
    with the three legacy plans; assert structured `plan_entitlements` rows + one `active`
    `tenant_plan_assignments` per tenant; re-apply the data step → no new rows (idempotent).
13. `PricingContractSmokeTests` (AC8) — seed `free`/`team`/`enterprise` tenants, run the back-fill,
    then call all four contract ops for each tenant → zero errors; each returns a plan, a complete
    entitlement set, a priced usage line, and a net price.
14. **Tenant-isolation test** — two tenants on different plans: the back-fill assigns each only its
    own plan (no cross-tenant assignment row); `ResolveEntitlementsAsync(A)` never returns B's limits.
15. `LegacyPathFlowsThroughContract` — `AdminTenantsEndpoints.UpdateTenantPlan` (kept by 34-4)
    routes through `IPlanAssignmentService`, never re-deriving from `Quotas`; a contract read after
    the update reflects the new pinned assignment.

**Mocks:** the five underlying services (`IPlanCatalogService`, `IPlanAssignmentService`,
`IEntitlementService`, `IUsagePricingEngine`+`IMarginPolicyResolver`, `ICreditAwarePricingEngine`)
are faked in façade unit tests; `IPlatformEventPublisher` is faked to capture back-fill events;
`TimeProvider` injected where `durationMs` is asserted. DB-bound tests use the real Npgsql provider
against docker Postgres. **No Stripe/provider HTTP mocks** — no external billing/provider call in
this story (provider cost basis is the existing `ProviderPricingService` table, exercised in 34-5).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPricingContract.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingContract.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPricingBackfillService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingBackfillService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingBackfillModels.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingBackfillEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Pricing/PricingContractDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingBackfillEndpoint.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Modify (AddPricingContract + AddPricingBackfill) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register façade + startup back-fill; map admin re-run route) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_PricingBackfillDataStep.cs` | Create (data-only migration) |
| `docs/stories/epic-34/pricing-contract.md` | Create (published stable-contract doc) |
| `docs/stories/epic-34/pricing-migration-ordering.md` | Create (CP-vs-tenant ordering note) |
| `docs/sprint-status.yaml` | Modify (Epic 20 supersede/re-home notes; add epic-34 block) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/LegacyQuotaMapTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingBackfillServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingContractTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingBackfillMigrationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingContractSmokeTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (pricing, migrations, resolution
   fail-loud, the no-migration-anxiety note).
3. Reviewed **34-1, 34-3, 34-4, 34-5, 34-6, 34-7** — this story is the capstone; it *consumes* every
   one of them and must NOT re-implement the catalog, assignment, engines, or credit logic.
4. Reviewed `docs/stories/migration-ordering.md` (the format the new ordering note mirrors) and
   `PlansSeeder.cs` (insert-missing-only ownership rule).
5. Confirmed the C# test runner contract: `sg docker -c "dotnet test ..."` for docker-bound suites
   (build needs no wrapper) — see `reference_dotnet_test_docker`.
6. Planned a TDD (Red-Green-Refactor) approach — a failing test per AC before implementation.

### Key Design Decisions

- **The façade is the product; the back-fill is the one-time cost.** The lasting deliverable is
  `IPricingContract` — one surface that freezes Epic 34's read shape so Billing/Enforcement never
  couple to internals. Everything else (the migration, the supersede bookkeeping) is the work needed
  to make that surface trustworthy.
- **Delegate, never re-implement.** `PricingContract` contains zero pricing math — every method is a
  one-liner over an already-tested service. A line of business logic in the façade is a smell that
  means scope leaked from a sibling story.
- **Insert-missing-only, re-runnable.** Both the migration data step and the startup service use
  `NOT EXISTS` guards. Per CLAUDE.md no-migration-anxiety the app is pre-production, so "rollback" is
  drop-and-reseed; the value we guarantee is *idempotency* (second run = no-op), not a reversible
  data down-migration.
- **Belt-and-suspenders migration + service.** The EF data step does the bulk back-fill fast and
  in-transaction; the startup `PricingBackfillService` heals tenants created after the migration and
  emits the audit events raw SQL cannot. Both are idempotent so running both is safe.
- **Re-home Stripe scope, don't retire it here.** Only `20-1`/`20-4`'s *plan/quota model* scope is
  owned by Epic 34 → `superseded`. Subscriptions/checkout/metering/dashboard (`20-2`/`20-3`/`20-5`)
  are Billing's (Epic 35) to retire — touching their status beyond a re-home note would overstep the
  Epic 34 ↔ Epic 35 boundary.

### Boundary Notes (what this story does NOT do)

- No Stripe / subscriptions / checkout / portal / webhooks (Epic 35) — only re-homes the status note.
- No usage metering writes (Epic 35) — the contract reads gauge counts via 34-6's reader seam.
- No quota **enforcement** / blocking (sibling Enforcement epic) — the contract resolves; callers
  enforce.
- No new pricing logic — catalog (34-1), assignment (34-4), markup (34-5), entitlements (34-6),
  credits (34-7) are consumed verbatim.
- No DDL change — the legacy `Plan.Quotas` / `Tenant.Plan` columns are dropped by a *later* story
  once Epic 35 is off the JSON (34-1's deprecation-window decision).
- No tenant-scoped pricing table — the catalog is platform-global by design.

### Edge Cases

- A tenant created between the migration data step and a later code deploy → healed by the startup
  `PricingBackfillService` on next boot (idempotent NOT EXISTS guard).
- A plan with malformed `Quotas` JSON → the back-fill logs WARN and skips that plan's quota
  conversion (its 34-1-seeded structured rows, if present, are authoritative anyway); it does NOT
  fail the whole run.
- The startup service path delegating to `AssignAsync` for a tenant whose `direction` would be
  mis-classified (no prior assignment) → use `Reason="backfill: 34-10"` and treat as `lateral`;
  the migration raw-SQL path inserts directly with the same reason for speed.
- 34-7 not yet merged → `PriceNetAsync` registered as a pass-through (`NetPriceUsd == SellPriceUsd`);
  the conformance test for net price is `[Fact(Skip="awaiting 34-7")]` until it lands.
- Concurrent admin re-run + startup run → a single-flight lock (named advisory lock or an in-process
  semaphore) makes the second caller observe `WasNoOp`/`409 backfill_in_progress`.

## Logging Requirements

- **INFO**: back-fill run started (`runId`, `source`), back-fill completed (`plansConverted`,
  `tenantsAssigned`, `entitlementsCreated`, `durationMs`), back-fill skipped (already complete),
  contract op served on the slow/admin path (`tenantId`, op).
- **DEBUG**: per-plan quota conversion (`slug`, mapped metric keys), per-tenant assignment resolution
  (`tenantId`, source of plan = `planId|slug|free`), façade delegation entry/exit (`tenantId`, op).
- **WARN**: unmapped legacy quota key encountered (`slug`, key — e.g. `concurrentWorkflows`),
  malformed `Quotas` JSON skipped (`slug`), 34-7 absent → net-price pass-through engaged,
  back-fill re-run requested while one is in progress.
- **ERROR**: `PRICING.BACKFILL.INCOMPLETE` (a tenant left without an assignment — before the throw),
  `PRICING.CONTRACT.NO_ASSIGNMENT` / `PRICING.CONTRACT.CATALOG_UNAVAILABLE` from the façade,
  back-fill transaction rollback, event-emission failure.
- **Structured context**: include `{ runId, tenantId, slug, planId, planVersion, metricKey, source,
  op }` where applicable.
- **Credential safety**: pricing data is not secret, but never log encrypted connection strings or
  tenant secrets if a back-fill/contract path ever touches a tenant row.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
