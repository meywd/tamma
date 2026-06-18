# Story 34-10: Epic 20 Decommission & Pricing Contract Migration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. C# docker-bound suites run via `sg docker -c "dotnet test ..."`; the build
> needs no wrapper (`reference_dotnet_test_docker`).

**Goal:** Formally retire the stale TypeScript Epic 20 plan/pricing surface, back-fill every
existing plan and tenant from the legacy opaque `Plan.Quotas` JSON and loose `Tenant.Plan` string
onto Epic 34's structured catalog (34-1 `PlanEntitlement`) and assignment model (34-4
`TenantPlanAssignment`), and publish a single stable `IPricingContract` façade as the ONLY surface
Billing (Epic 35) and the Enforcement epic call into. The migration is idempotent/re-runnable; the
façade delegates to already-built services and adds zero pricing logic.

**Story file:** `docs/stories/epic-34/story-34-10/34-10-epic-20-decommission-and-pricing-contract-migration.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/` (xUnit). Docs in `docs/stories/` and
`docs/sprint-status.yaml`.

---

## Non-goals (YAGNI guard)

- NO new pricing logic. The catalog (34-1), assignment (34-4), markup engine (34-5), entitlement
  resolution (34-6), and credit-aware net price (34-7) are *consumed*. A single line of pricing math
  in the façade means scope leaked from a sibling story.
- NO Stripe / subscriptions / checkout / portal / webhooks / metering / billing dashboard. That
  scope is **re-homed to Epic 35 (Billing)** — this story only updates the Epic 20 status *notes*.
- NO quota enforcement / blocking. The contract resolves and prices; the sibling Enforcement epic
  gates on it.
- NO DDL change to drop `Plan.Quotas` / `Tenant.Plan` — those columns stay one deprecation window
  (34-1's decision); a later story drops them once Epic 35 is off the JSON.
- NO tenant-scoped pricing table. The catalog/assignment are platform-global CP rows in both modes.
- NO reversible data down-migration. Per CLAUDE.md no-migration-anxiety (pre-production), "rollback"
  = drop-and-reseed; the guarantee is idempotency (second run = no-op), not data reversal.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists today (the legacy surface to migrate off)

| Site | State today |
|---|---|
| `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` | Thin skeleton: `Id`, `Slug`, `DisplayName`, `MonthlyPriceUsd`, **`Quotas` (opaque JSON string, default `"{}"`)**, `IsActive`, `PlacementPolicy`. 34-1 extends it with `Version`/`Status`/nav collections. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` (line 11) | `public string Plan { get; set; } = "free";` — the loose slug string. `Tenant.PlanId` is an Epic-28 **shadow** column (`EF.Property<Guid?>`), not a CLR property on the entity. |
| `apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs` | Seeds 3 plans with `Quotas` JSON: free `{"llmTokensPerMonth":100000,"concurrentWorkflows":1,"seats":1}`, team `…2000000…seats:10`, enterprise `…50000000…"seats":-1` (line 84 — **`-1` = unlimited sentinel**). `SeedAsync` short-circuits if **any** plan exists (line 44-48) — 34-1 changes this to per-table insert-missing-only. Stable IDs: `FreePlanId`/`TeamPlanId`/`EnterprisePlanId` (lines 23-32). |
| `docs/sprint-status.yaml` (lines 273-279) | `epic-20: contexted`; `20-1`..`20-5` all `drafted` with "spec only" notes. The `20-1` note literally says "Plan.cs catalogue exists but is tenancy placement (Epic 28), not the Stripe plan/customer model"; `20-4` note: "Plan.Quotas JSON defined but unused by any billing/orchestrator check". **`epic-34` is NOT yet a block in this file** — this story adds it. |
| `docs/stories/migration-ordering.md` | The canonical cross-epic migration-number ledger (001-018 listed; rules: never reuse/reorder a number, idempotency required). The format the new pricing ordering note mirrors. Note: this ledger tracks the legacy `database/migrations/*.sql` numbering; the C# CP migrations live under `Tamma.Data/Migrations/ControlPlane/` (EF, timestamp-named) — the new note records the EF ordering. |

### What 34-1..34-7 will have built (this story's hard inputs)

> Verified by reading the drafted sibling stories under `docs/stories/epic-34/story-34-*/`. All land
> in `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/` (a NEW directory created by 34-1 — confirmed
> absent today: `ls apps/tamma-elsa/src/Tamma.Api/Services/Pricing/` → no such directory).

| From | Surface this story consumes |
|---|---|
| 34-1 | `EntitlementMetricKey` (closed enum: `Agents`,`WorkflowRuns`,`LlmTokens`,`Seats`,`Repos`,`RagStorageMb`,`BenchmarkRetentionDays`; snake_case persisted), `PlanEntitlement` (`MetricKey`,`LimitValue long?`=NULL unlimited,`Period`,`OverageMode`), `IPlanCatalogService.GetByIdAsync`, `PlanSnapshot`, `PricingServiceCollectionExtensions`, the `Services/Pricing/` dir. |
| 34-3 | `PricingMode` (`byok`\|`platform`) semantics — `TenantProviderBilling` per-(tenant,provider) override; the contract's `PriceUsageAsync` must honor mode end-to-end. |
| 34-4 | `TenantPlanAssignment` (`PlanVersion` pinned, partial-unique `Status='active'`), `IPlanAssignmentService.GetActiveAsync`/`AssignAsync`, the back-fill-one-active-per-tenant migration pattern (raw SQL with `COALESCE(PlanId, slug-match, plan_free)`). |
| 34-5 | `IUsagePricingEngine.PriceUsage(UsageLine, MarginPolicy)` → `PricedUsage(CostBasisUsd, MarginUsd, SellPriceUsd, PricingMode)` (pure; BYOK → zero token markup), `IMarginPolicyResolver.ResolveAsync(tenantId, provider)`, `UsageLine`. Cost basis from existing `IProviderPricingService` (verified at `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService.cs` — `Compute`/`IsKnown`). |
| 34-6 | `IEntitlementService.ResolveAsync(EntitlementPrincipal)` → `ResolvedEntitlements` (closed 7-key map), `EntitlementPrincipal.ForTenant`. Fail-loud `ENTITLEMENT.RESOLVE.NO_ASSIGNMENT` (no empty fallback). |
| 34-7 | `ICreditAwarePricingEngine.PriceNetAsync(UsageLine, tenantId)` → `NetPriceResult(SellPriceUsd, PromoDiscountUsd, CreditsAppliedUsd, NetPriceUsd, TrialWaived)`. **Soft dep** — pass-through default if not yet merged. |

### Infrastructure seams (verified present today)

- `apps/tamma-elsa/src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs` — `AppendAndPublishAsync(PlatformEvent, ct)` → CP `platform_events`, idempotent (null on dedup collision). The back-fill audit path.
- `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs` + `DomainEvent.cs` — event rows.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — DCB append (tenant-scope; not needed for the CP-resident back-fill events but available).
- Authorization policies confirmed in use (`apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs`, endpoints): `OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`, `PromptManage`. The admin re-run uses `PlatformOwnerAccess`.
- `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — EF CP migration dir (`20260609205701_InitialControlPlane` is the baseline). New data-only migration lands here.

---

## Architecture

**Migrate the data → freeze the surface → record the retirement.** Three pillars:

1. **`IPricingContract`** (`Tamma.Api/Services/Pricing/`) — a thin, total façade with four read ops
   (`ResolvePlanAsync`, `ResolveEntitlementsAsync`, `PriceUsageAsync`, `PriceNetAsync`) that each
   delegate one-to-one to a 34-x service. Zero pricing math. Returns published DTOs
   (`Tamma.Api/Dtos/Pricing/PricingContractDtos.cs`) so a later refactor of internals can't break
   Billing. Fail-loud on no assignment (`PRICING.CONTRACT.NO_ASSIGNMENT`).
2. **`PricingBackfillService` + data-only EF migration** — idempotent, insert-missing-only.
   Migration does the bulk (jsonb→entitlements, tenant→assignment via `NOT EXISTS` guards); the
   startup service heals stragglers and emits `PRICING.BACKFILL.*` audit events the raw SQL can't.
3. **Retirement bookkeeping** — `docs/sprint-status.yaml` Epic 20 supersede/re-home notes + a new
   `epic-34` block; `docs/stories/epic-34/pricing-contract.md` (the published contract) +
   `pricing-migration-ordering.md` (CP-vs-tenant ordering, mirrors `migration-ordering.md`).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the catalog/contract? | Platform-global CP rows; the sole user reads via the façade/underlying API. | Platform-global CP rows; tenants read via 34-6's `GET /api/pricing/entitlements`. Façade is in-process for Billing/Enforcement. |
| Who/what does the back-fill scope? | Runs once on CP rows; the lone tenant gets one assignment. | Runs once on CP rows; every non-deleted tenant gets exactly one `active` assignment. |
| Façade per-mode resolution | Delegates to 34-6/34-4 which key sole-user → personal tenant. | Delegates which key by `tenant_id`. The façade adds NO new RBAC. |
| Who may trigger the admin re-run? | The sole user (authenticated; same code path). | `PlatformOwnerAccess` (platform owner) only. |
| Tenant-scoped pricing table? | None — global catalog. | None — global catalog. |
| Mode source | `ITammaModeProvider` (via the underlying services). | same |

---

## Phased task breakdown

### Phase 0: Prerequisite gate + research (no code)

- [ ] Confirm 34-1, 34-3, 34-4, 34-5, 34-6 are merged (their entities/services exist in
      `Services/Pricing/`). 34-7 is a soft dep — note whether it is present (drives the
      `PriceNetAsync` pass-through decision).
- [ ] Read `.dev/` for pricing/migration/resolution-fail-loud findings and the no-migration-anxiety
      note; read `docs/stories/migration-ordering.md` (format) and `PlansSeeder.cs` (insert-missing
      ownership rule).
- [ ] Verify the C# test runner contract (`sg docker -c "dotnet test ..."`).

### Phase 1: Published contract DTOs + façade (TDD)

**Files:** `Tamma.Api/Dtos/Pricing/PricingContractDtos.cs` (new), `Services/Pricing/IPricingContract.cs`
(new), `Services/Pricing/PricingContract.cs` (new), `Extensions/PricingServiceCollectionExtensions.cs`
(modify — `AddPricingContract`), `Program.cs` (modify — DI).

- [ ] **Test first** `tests/Tamma.Api.Tests/Pricing/PricingContractTests.cs`:
  - `ResolveEntitlements_ReturnsAllSevenKeys` (façade returns the closed 7-key map; fake `IEntitlementService`).
  - `ResolvePlan_PinnedNotLatest` (assign v1, deprecate to v2; façade still returns v1; mock 34-1/34-4).
  - `PriceUsage_HonorsPricingMode` (byok → `SellPriceUsd == CostBasisUsd`; platform → markup applied; mock 34-5).
  - `PriceNet_Invariant` (`NetPriceUsd == SellPriceUsd − PromoDiscountUsd − CreditsAppliedUsd`; mock 34-7; pass-through when 34-7 absent).
  - `NoAssignment_FailsLoud` (`PRICING.CONTRACT.NO_ASSIGNMENT`; never empty — pins `feedback_resolution_no_empty_fallback`).
- [ ] Define `PricingContractDtos.cs`: `PlanContractView`, `EntitlementContractView`+`EntitlementLineView`, `PricedUsageView`, `NetPriceView` with `From(...)` mappers off the underlying records.
- [ ] Implement `IPricingContract` + `PricingContract` (delegation only — each method ≤3 lines + the fail-loud guard, as sketched in the story Technical Design).
- [ ] `AddPricingContract(services)` registers `IPricingContract` scoped; call from `Program.cs`. Register `PriceNetAsync`'s engine as a pass-through default when 34-7 is absent (feature flag / null-object).
- [ ] Green the Phase-1 tests.

### Phase 2: Legacy quota mapper + back-fill service (TDD)

**Files:** `Services/Pricing/PricingBackfillModels.cs` (new — `BackfillReport`, `LegacyQuotaMap`,
`BackfillTrigger`), `Services/Pricing/IPricingBackfillService.cs` (new),
`Services/Pricing/PricingBackfillService.cs` (new), `Services/Pricing/PricingBackfillEventTypes.cs`
(new — `PRICING.BACKFILL.STARTED/.COMPLETED/.SKIPPED`).

- [ ] **Test first** `tests/Tamma.Api.Tests/Pricing/LegacyQuotaMapTests.cs`:
  - `{"llmTokensPerMonth":2000000,"seats":10}` → 2 `PlanEntitlement` specs.
  - `seats:-1` → `LimitValue = null` (unlimited sentinel).
  - `concurrentWorkflows` present → NOT mapped + WARN logged (it is not an `EntitlementMetricKey`).
  - empty/`{}`/malformed JSON → no rows, no throw.
- [ ] **Test first** `tests/Tamma.Api.Tests/Pricing/PricingBackfillServiceTests.cs`:
  - `Run_ConvertsQuotas_InsertMissingOnly` (plan with legacy JSON + no rows → entitlements; plan already seeded by 34-1 → untouched).
  - `Run_AssignsEveryTenant_FallbackChain` (`PlanId` → that plan; NULL `PlanId` + `Plan="team"` → team; neither → `plan_free`; pin `PlanVersion`).
  - `Run_ZeroTenantWithoutAssignment_Invariant` (AC2 query returns zero; a skipped tenant → `PRICING.BACKFILL.INCOMPLETE` thrown).
  - `Run_SecondRun_IsNoOp` (zero inserts, `WasNoOp=true`, emits `SKIPPED`, no `COMPLETED`).
  - `Run_EmitsEvents` (`STARTED` then `COMPLETED` with counts/duration; fake `IPlatformEventPublisher`).
- [ ] Implement `LegacyQuotaMap` (pure JSON→`PlanEntitlement`-spec; the `-1→NULL` + unmapped-key WARN rules).
- [ ] Implement `PricingBackfillService.RunAsync` per the story algorithm: detect-prior-completion → quotas→entitlements (insert-missing) → tenant→assignment (insert-missing, delegate to `AssignAsync` with `Reason="backfill: 34-10"`) → verify invariant (throw on miss) → emit `COMPLETED`. Single-flight lock (in-process semaphore + DB advisory lock).
- [ ] `AddPricingBackfill(services)` + invoke `RunAsync(Startup)` once at API startup **after**
      `PlansSeeder.SeedAsync` (mirror its startup wiring in `Program.cs`).
- [ ] Green the Phase-2 tests.

### Phase 3: Data-only EF migration (TDD via integration)

**Files:** `Tamma.Data/Migrations/ControlPlane/<ts>_PricingBackfillDataStep.cs` (new),
`docs/stories/epic-34/pricing-migration-ordering.md` (new).

- [ ] **Test first** `tests/Tamma.Api.Tests/Pricing/PricingBackfillMigrationTests.cs` (docker):
  - apply 34-1 + 34-4 + this data step to a clean DB seeded with the 3 legacy plans → assert
    structured `plan_entitlements` + one `active` `tenant_plan_assignments` per tenant.
  - re-apply the data step → zero new rows (idempotent `NOT EXISTS`).
- [ ] Write the data-only migration (jsonb extraction for entitlements; `COALESCE(PlanId, slug, free)`
      tenant assignment; both `NOT EXISTS`-guarded), as sketched in the story. `Down()` deletes only
      `Reason='backfill: 34-10'` rows.
- [ ] Run `dotnet ef migrations add PricingBackfillDataStep --context ControlPlaneDbContext`, then
      `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → MUST report
      none (data-only, no DDL).
- [ ] Write `pricing-migration-ordering.md`: list the EF CP migration order (34-1 schema → 34-4
      schema → this data step), state all pricing tables are CP-resident (NO `t_<hex>` pricing
      table), and that the data step runs after both schema migrations. Mirror the rules section of
      `docs/stories/migration-ordering.md`.
- [ ] Green the Phase-3 docker tests.

### Phase 4: Admin re-run endpoint + smoke + isolation tests (TDD)

**Files:** `Endpoints/Admin/AdminPricingBackfillEndpoint.cs` (new), `Program.cs` (modify — map route),
`tests/Tamma.Api.Tests/Pricing/PricingContractSmokeTests.cs` (new).

- [ ] **Test first** in `PricingContractSmokeTests` (docker, AC8): seed `free`/`team`/`enterprise`
      tenants, run the back-fill, call all four contract ops per tenant → zero errors (plan +
      complete entitlements + priced usage + net price).
- [ ] **Test first** tenant-isolation (AC13): two tenants on different plans — back-fill assigns each
      only its own plan (no cross-tenant assignment); `ResolveEntitlementsAsync(A)` never returns B's
      limits.
- [ ] **Test first** `LegacyPathFlowsThroughContract`: `AdminTenantsEndpoints.UpdateTenantPlan`
      routes through `IPlanAssignmentService`, never re-deriving from `Quotas`; a contract read after
      reflects the new pinned assignment.
- [ ] Implement `POST /api/admin/pricing/backfill` (`PlatformOwnerAccess`) → `RunAsync(Admin)`,
      returning the `BackfillReport`; 409 on `backfill_in_progress` (single-flight).
- [ ] Green Phase-4 tests.

### Phase 5: Published contract doc + Epic 20 retirement bookkeeping (no code)

**Files:** `docs/stories/epic-34/pricing-contract.md` (new), `docs/sprint-status.yaml` (modify).

- [ ] Write `pricing-contract.md` (AC5): the four operations + I/O shapes, `PricingMode`
      (`platform_provided`|`byok`) semantics, and the explicit rule that no epic may take a direct
      dependency on `Plan`/`PlanEntitlement`/`PlanPrice`/`TenantPlanAssignment`/`IUsagePricingEngine`/
      `IEntitlementService`/`ICreditAwarePricingEngine` — every cross-epic read goes through
      `IPricingContract`.
- [ ] Edit `docs/sprint-status.yaml`:
  - `20-1-stripe-integration-plan-model: superseded` — note "Plan/quota model owned by Epic 34 (34-1/34-4); Stripe subscription/customer scope re-homed to Epic 35".
  - `20-4-usage-limits-enforcement: superseded` — note "Quota model owned by Epic 34 (34-1/34-6); enforcement re-homed to the Epic 34 Enforcement story".
  - `20-2`/`20-3`/`20-5` — leave status `drafted`, update notes to "re-homed to Epic 35 (Billing); retired there, not here".
  - Add an `epic-34:` block (mirror the existing `epic-NN:` blocks' style) with `34-10-…: drafted`
    plus the sibling story statuses if not already present.
- [ ] Final full-suite run: `sg docker -c "dotnet test apps/tamma-elsa"` green; build green.

---

## Sequencing & dependencies

```
Phase 0 (gate) → Phase 1 (façade) ─┐
                                    ├→ Phase 4 (admin re-run + smoke/isolation)
Phase 0 → Phase 2 (back-fill svc) ─┤
              → Phase 3 (migration)┘
Phase 5 (docs/bookkeeping) — after Phase 4 (so the contract doc matches the shipped façade)
```

- Phase 1 (façade) and Phase 2 (back-fill) are independent and can be built in parallel; both need
  Phase 0's prereq gate.
- Phase 3 (migration) depends on Phase 2's `LegacyQuotaMap`/assignment logic for parity (the SQL
  mirrors the service's mapping rules) but can be drafted alongside.
- Phase 4 needs both the façade (Phase 1) and the back-fill (Phase 2/3) to run the smoke test.
- Phase 5 is documentation; write the contract doc last so it describes the actually-shipped surface.
- **Hard external prereq:** 34-1, 34-3, 34-4, 34-5, 34-6 merged. **Soft:** 34-7 (pass-through if absent).

---

## Risks + mitigations

- **Scope leak into the façade.** The biggest risk is re-implementing pricing logic in
  `PricingContract`. *Mitigation:* the conformance tests mock every underlying service and assert the
  façade only *delegates*; any business logic would fail "delegates to faked service exactly once".
  Code-review rule: each façade method ≤3 lines + the fail-loud guard.
- **Back-fill leaves a tenant without an assignment.** A silent gap would break every downstream
  read. *Mitigation:* AC2 verification query runs at the end of `RunAsync` and throws
  `PRICING.BACKFILL.INCOMPLETE` (Critical) — fail loud, never partial-silent. Tested explicitly.
- **Non-idempotent re-run double-inserts.** *Mitigation:* `NOT EXISTS` guards in both the SQL and the
  service; second-run-is-no-op test pins it; `Reason='backfill: 34-10'` stamps backfill rows so
  `Down()` is surgical.
- **`-1` sentinel mis-mapped to a real (huge) limit instead of unlimited.** *Mitigation:*
  `NULLIF(..., -1)` in SQL and the `LegacyQuotaMap` `-1 → null` rule, both tested (enterprise
  `seats:-1` → unlimited).
- **Migration topology shift (Story 28-1 / Epic 30).** Pricing tables + back-fill events are
  CP-resident. *Mitigation:* `pricing-migration-ordering.md` states this explicitly; events append
  via the CP `IPlatformEventPublisher` so a later per-tenant event move only touches tenant-scope
  routing, not pricing.
- **34-7 not merged when this lands.** *Mitigation:* `PriceNetAsync` registered as a pass-through
  (`NetPriceUsd == SellPriceUsd`), feature-flagged; its conformance test `[Fact(Skip="awaiting 34-7")]`
  until 34-7 ships. Documented in `pricing-contract.md`.
- **Over-retiring Epic 20.** Marking `20-2`/`20-3`/`20-5` superseded would overstep the Epic 34 ↔
  Epic 35 boundary. *Mitigation:* AC3 pins exactly which stories are `superseded` here (`20-1`,
  `20-4`) vs only re-homed (the Stripe/metering/dashboard trio). Boundary note in the story.
- **`has-pending-model-changes` noise.** The data-only migration must add no DDL. *Mitigation:* no
  entity/`OnModelCreating` change in this story; verify `has-pending-model-changes` reports none.

---

## Acceptance criteria (mirror the story)

1. Re-runnable, idempotent back-fill converts legacy `Plan.Quotas` JSON → typed `PlanEntitlement`
   rows (`-1 → NULL` unlimited; `concurrentWorkflows` dropped + WARN); plans already carrying
   structured rows are untouched.
2. Exactly one `active` `TenantPlanAssignment` per non-deleted tenant (`PlanId` → `Slug` →
   `plan_free` fallback; `PlanVersion` pinned); no tenant left without an assignment (verified query
   returns zero).
3. Epic 20 `20-1`/`20-4` marked `superseded` in `docs/sprint-status.yaml` pointing at Epic 34;
   `20-2`/`20-3`/`20-5` re-homed to Epic 35 (note only, NOT superseded here).
4. `IPricingContract` façade (4 ops: resolve-plan, resolve-entitlements, price-usage,
   net-price-with-credits) is the ONLY surface Epic 35/Enforcement call into; it delegates to
   34-1/34-4/34-5/34-6/34-7 with no direct entity access from other epics.
5. `docs/stories/epic-34/pricing-contract.md` documents the contract + the no-direct-dependency rule.
6. Conformance tests assert stable shapes + BYOK-vs-platform pricing honored end-to-end + the
   net-price invariant.
7. `pricing-migration-ordering.md` records CP-vs-tenant ordering (all pricing tables CP-resident; data
   step after 34-1/34-4 schema migrations).
8. Smoke test on a seeded env: free/team/enterprise tenants all resolve plan + entitlements + price +
   net price with zero errors post-migration.
9. Rollback story documented: pre-production drop-and-reseed; back-fill re-runnable/idempotent;
   second-run no-op verified.
10. Back-fill emits `PRICING.BACKFILL.STARTED/.COMPLETED/.SKIPPED` to CP `platform_events`.
11. Legacy `Plan.Quotas`/`Tenant.Plan` columns retained but no longer read by any pricing path;
    `UpdateTenantPlan` verified to flow through `IPlanAssignmentService`.
12. Per-mode + per-tenant ownership honored (platform-global catalog; no tenant pricing table;
    underlying services key per-mode; admin re-run `PlatformOwnerAccess`).
13. Tests cover JSON mapping, fallback chain, the zero-without-assignment invariant, second-run
    no-op, the four ops' shape + `PricingMode`, pinned-version-survives-deprecation, the
    `BackfillReport`/events, and tenant isolation.
