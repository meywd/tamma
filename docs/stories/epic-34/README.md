# Epic 34: Pricing, Plans & Entitlements

## Overview

Epic 34 is the **pricing model layer** of Tamma SaaS: a production catalog of plans, features,
per-plan entitlements/quotas, and the rules that turn measured usage into a priced amount. It is the
monetization foundation that Billing (Epic 35) charges against and a sibling Enforcement epic
consumes — but it deliberately stops short of invoicing/payment capture and runtime gate
enforcement.

The catalog is a **tenant-aware price-book** built on the C# `apps/tamma-elsa` stack. Plans, features,
typed entitlements, and prices are immutable, versioned, control-plane rows
(`ControlPlaneDbContext`); a tenant assigned `team v1` keeps reproducible `team v1` pricing and
quotas forever, even after `team v2` re-prices. The epic distinguishes **BYOK** (tenant brings its
own provider API key, stored in the Epic 29 secret cabinet → flat platform/seat fee, no token markup)
from **platform-provided** usage (our cost basis from `ProviderPricingService` + a configurable
margin), assigns plans (including custom enterprise plans) to tenants, and applies trials, prepaid
credits, and promo codes. It exposes a small set of read APIs — resolve plan, resolve entitlements,
price a usage line, price net of credits — that downstream epics consume through a single published
contract (`IPricingContract`, Story 34-10).

Per CLAUDE.md's "two scoping models" rule, every pricing feature answers ownership in **both** modes:
the plan/margin catalog is **platform-owned and global** in single-user (sole user reads it) and SaaS
(platform owner authors it; tenants get read-only resolved snapshots); per-tenant state (plan
assignment, BYOK mode, credits) is keyed by `user_id` in single-user mode and `tenant_id` in SaaS
mode.

### Supersedes

Epic 34 re-targets and retires the stale TypeScript monetization surface (everything below is on the
deleted `packages/api`; this epic re-implements it on `apps/tamma-elsa`):

- **TS Epic 20 plan/pricing stories** — `20-1` (Stripe integration & **plan model**) and `20-4`
  (**usage-limits enforcement** plan/quota model). Their plan/quota scope is now owned by 34-1
  (catalog), 34-4 (assignment), and 34-6 (entitlements) + the sibling Enforcement epic. Story 34-10
  marks `20-1` / `20-4` `superseded` in `docs/sprint-status.yaml`. (Stripe-specific subscription,
  checkout, portal, webhook, metering, and billing-dashboard scope — `20-2`, `20-3`, `20-5` — is
  **re-homed to Epic 35 (Billing)**, not retired by this epic.)
- **The legacy `Plan.Quotas` opaque-JSON model** — replaced by typed `PlanFeature` /
  `PlanEntitlement` / `PlanPrice` rows keyed off the closed `EntitlementMetricKey` enum. Story 34-10
  back-fills every legacy `Plan.Quotas` blob and loose `Tenant.Plan` string into structured rows.
- **The Epic 21-2 pricing page** — the public/admin pricing surface is now driven by the catalog +
  the dashboards in Story 34-9.

## Plans & Entitlements

The seeded baseline catalog (`PlansSeeder` + structured child rows, insert-missing-only). Prices are
split by **pricing mode**: BYOK rows carry a platform/seat fee with **no token markup**;
platform-provided rows are billed cost-plus-margin (default global margin `1.3×`, Story 34-5).

| Plan | Pricing mode | Recurring (USD/mo) | Workflow Runs | LLM Tokens | Seats | Repos | Agents | Custom |
|------|--------------|--------------------|---------------|------------|-------|-------|--------|--------|
| **Free** | platform-provided | $0 | metered/block | metered/block | 1 | 3 | limited | no |
| **Team** | platform-provided · BYOK | paid · seat fee | metered | cost+margin · BYOK 0 markup | multi | multi | multi | no |
| **Enterprise** | platform-provided · BYOK | custom | unlimited (`NULL`) | custom | custom | custom | custom | `IsCustom = true` |

Typed entitlement metrics (closed `EntitlementMetricKey` enum, persisted snake_case, shared by
entitlement limits, metered pricing, metering, and enforcement so a quota key never drifts):
`agents`, `workflow_runs`, `llm_tokens`, `seats`, `repos`, `rag_storage_mb`,
`benchmark_retention_days`. Each `PlanEntitlement` row carries a `LimitValue` (`NULL` = unlimited), a
`Period` (`monthly | total`), and an `OverageMode` (`block | allow | meter`). Trials, prepaid/granted
USD credits, and promo codes (Story 34-7) layer on top of the resolved plan price.

> Concrete numeric limits per plan are seeded as `PlanEntitlement` rows and are admin-editable via the
> catalog CRUD (Story 34-2); the table above is the structural shape, not a frozen price sheet.

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 34-1 | Plan & Price-Book Catalog Data Model | P0 | drafted | 4-5 days |
| 34-2 | Plan Catalog Admin API & Custom Enterprise Plans | P0 | drafted | 3-4 days |
| 34-3 | BYOK vs Platform-Provided Pricing Mode (per-provider, secret-cabinet wired) | P0 | drafted | 4-5 days |
| 34-4 | Per-Tenant Plan Assignment & Lifecycle | P0 | drafted | 3-4 days |
| 34-5 | Cost→Price Markup Engine (platform-provided usage) | P0 | drafted | 3-4 days |
| 34-6 | Entitlement & Quota Resolution Service | P0 | drafted | 3-4 days |
| 34-7 | Trials, Credits & Promo Codes | P1 | drafted | 4-5 days |
| 34-8 | Pricing Audit, Events & Reproducibility | P1 | drafted | 3-4 days |
| 34-9 | Pricing & Plan Management Dashboards | P1 | drafted | 4-5 days |
| 34-10 | Epic 20 Decommission & Pricing Contract Migration | P0 | drafted | 3-4 days |
| 34-11 | Provider Cost Price-Book (`Provider` + `ProviderModelPrice` cost entities behind `IProviderPricingService`; before 34-5) *(2026-06-21)* | P0 | drafted | 3-4 days |

## Architecture

```
+-----------------------------------------------------------------------------+
|              EPIC 34: PRICING, PLANS & ENTITLEMENTS                          |
|              (control-plane resident; apps/tamma-elsa C#)                    |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-- LAYER 1: Catalog Foundation (34-1, 34-2) -------------------------+    |
|  |                                                                     |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  |  | Plan (versioned| | PlanFeature /    | | Plan Catalog Admin   |   |    |
|  |  | immutable rows)| | Entitlement /    | | API + Custom         |   |    |
|  |  | EntitlementKey | | Price (by mode)  | | Enterprise Plans     |   |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  +---------------------------------------------------------------------+    |
|                              |                                              |
|  +-- LAYER 2: Pricing Mode & Assignment (34-3, 34-4) ------------------+    |
|  |                              |                                       |    |
|  |  +-------------------------+ +-----------------------------------+   |    |
|  |  | BYOK vs Platform mode   | | Per-Tenant Plan Assignment &      |   |    |
|  |  | per (tenant, provider)  | | Lifecycle (pin PlanId+Version)    |   |    |
|  |  | -> Epic 29 secret       | | TenantPlanAssignment (active row) |   |    |
|  |  |    cabinet (ISecretStore)| |                                  |   |    |
|  |  +-------------------------+ +-----------------------------------+   |    |
|  +---------------------------------------------------------------------+    |
|                              |                                              |
|  +-- LAYER 3: Pricing & Entitlement Resolution (34-5, 34-6, 34-7) ----+    |
|  |                              |                                       |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  |  | Cost->Price    | | Entitlement &    | | Trials / Credits /   |   |    |
|  |  | Markup Engine  | | Quota Resolution | | Promo Codes          |   |    |
|  |  | (pure; margin  | | (closed 7-metric | | (net price = sell -  |   |    |
|  |  |  policy)       | |  map per tenant) | |  promo - credits)    |   |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  +---------------------------------------------------------------------+    |
|                              |                                              |
|  +-- LAYER 4: Audit, UI & Published Contract (34-8, 34-9, 34-10) -----+    |
|  |                              |                                       |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  |  | Pricing Audit, | | Admin + Tenant   | | IPricingContract     |   |    |
|  |  | Events & Time- | | Dashboards       | | (sole surface for    |   |    |
|  |  | Travel Replay  | | (catalog + plan) | |  Epic 35 / Enforce)  |   |    |
|  |  +----------------+ +------------------+ +----------------------+   |    |
|  +---------------------------------------------------------------------+    |
|                              |                                              |
|        consumed by ->  Epic 35 (Billing)   +   Enforcement epic            |
+-----------------------------------------------------------------------------+
```

## Key Technical Decisions

### Immutable, Versioned Catalog Rows (No In-Place Plan Edits)

A `Plan` (and its `PlanFeature` / `PlanEntitlement` / `PlanPrice` children) is immutable once
`Status = active | deprecated`. Editing produces a **new** `Plan` row with `Version = prior + 1` and
`SupersedesPlanId = priorId`, and flips the prior row to `deprecated`. A partial unique index
`UX_plans_OneActivePerSlug` (filtered on `Status = 'active'`) enforces exactly one active version per
slug at the database level. Mutating an active/deprecated row throws
`TammaError("PLAN.VERSION.IMMUTABLE", …)`. This is the reproducibility guarantee: a tenant pinned to
`team v1` keeps `team v1` pricing/quotas forever, so billing and historical invoices re-derive
identically.

### `EntitlementMetricKey` — Single Source of Quota-Key Truth

A closed enum (`Agents`, `WorkflowRuns`, `LlmTokens`, `Seats`, `Repos`, `RagStorageMb`,
`BenchmarkRetentionDays`) in `Tamma.Core/Enums/`, persisted as snake_case text (never the numeric
ordinal) via an EF `HasConversion` value converter. The same key is shared by entitlement limits,
metered pricing components, usage metering (Epic 35), and enforcement — so a typo can never split
`llm_tokens` from `llmTokens` across layers.

### BYOK vs Platform-Provided as the Pricing-Mode Axis

`PlanPrice` is split by `PricingMode` (`platform_provided | byok`) at the plan level; a
per-`(tenant, provider)` override lives in `TenantProviderBilling` (Story 34-3). The
`IProviderKeyResolver` resolves the effective key + mode: **BYOK** reads the tenant's secret from the
Epic 29 cabinet (`ISecretStore`, `SecretScope.Tenant`); **platform** falls back to the global config
key. There is **no silent empty fallback** — a BYOK row with a missing cabinet secret throws
`TammaError("PROVIDER.KEY.RESOLVE.BYOK_MISSING", …)` (mirrors the no-empty-fallback rule), never
degrading to the platform key (which would mis-bill and leak the platform quota). This story also
closes a real production gap: `CallLlmActivity` currently only ever reads the global platform key.

### Pure, Deterministic Markup Engine

`IUsagePricingEngine.PriceUsage(UsageLine)` is **side-effect-free**: it takes a resolved
`MarginPolicy` + `UsageLine` and returns `PricedUsage { CostBasisUsd, MarginUsd, SellPriceUsd,
PricingMode }`. Cost basis comes from `ProviderPricingService` (input/output tokens billed at
different rates). For `platform_provided`, `SellPrice = CostBasis × MarkupMultiplier (+ FixedUsdPer1M
× tokens/1M)`; for `byok`, the **token component is 0** (the seat/plan fee is Billing's concern).
Margin resolution order is **provider → plan → global → error** (`PRICING.MARGIN.NO_POLICY` if none —
never a silent zero margin). Pricing is byte-stable: 6dp internal arithmetic
(`MidpointRounding.ToEven`), 2dp invoice-facing, pinned by a golden-file test.

### One Published Contract for Downstream Epics

`IPricingContract` (Story 34-10) is the **only** surface Billing (Epic 35) and Enforcement call into,
exposing `ResolvePlanAsync`, `ResolveEntitlementsAsync`, `PriceUsageAsync`, and `PriceNetAsync`. No
other epic may take a direct dependency on `Plan`, `PlanEntitlement`, `PlanPrice`,
`TenantPlanAssignment`, `IUsagePricingEngine`, `IEntitlementService`, or
`ICreditAwarePricingEngine` — the façade freezes the surface and the contract conformance tests pin
its shapes.

### Event Sourcing & Time-Travel Pricing Audit

Every pricing decision emits a canonical DCB event to the control-plane `platform_events` /
`DomainEvents` store via `IPlatformEventPublisher` / `IEventRepository`:
`PLAN.VERSION.CREATED`, `PLAN.DEPRECATED`, `PRICING.BYOK.ENABLED` / `.DISABLED`,
`PRICING.MARGIN.UPDATED`, plan-assignment / credit / promo events, and the back-fill events
(`PRICING.BACKFILL.*`). Story 34-8 adds a replay query that reconstructs exactly "what plan / price /
entitlements / BYOK-mode applied to tenant X at timestamp T" — time-travel debugging of money.

### Per-Mode Ownership

| Concern | single-user | SaaS |
|---|---|---|
| Plan / margin catalog | platform-global rows; sole user reads | platform owner (`OwnerAccess`) authors; tenants read-only |
| Plan assignment | the user's instance | per-tenant `TenantPlanAssignment` (owner/admin) |
| BYOK mode + key | the user (no RBAC); global config key | per-`(tenant, provider)`; `PricingManage` gate (owner+admin); member → 403 |
| Credits / trials / promos | the user | per-tenant; owner/admin manage |

## Dependencies

### On Other Epics

- **Epic 4** (DCB events): `platform_events` / `DomainEvents` store, `PlatformEvent` entity,
  `IPlatformEventPublisher.AppendAndPublishAsync` / `IEventRepository.AppendAsync` for the audit trail.
- **Epic 28** (control plane / tenancy): `ControlPlaneDbContext`, `TammaModelConfiguration`, the
  `Tenant.PlanId` shadow column + FK to `plans`, `PlansSeeder`, and the
  `Migrations/ControlPlane/` pipeline.
- **Epic 29** (secret cabinet): `ISecretStore`, `SecretRef.ForTenant`, `SecretScope.Tenant`,
  `SecretPurpose.ApiKey` for BYOK key storage/retrieval (consumed, not modified).
- **Epic 32** (agents / provider chain): defines the provider chain BYOK applies to; Story 32-3 owns
  the cabinet read-path mechanics; per-call cost basis comes from the provider chain's diagnostics.
- **`ProviderPricingService`** (existing): the per-call cost-basis source the markup engine reads.

### Consumers (downstream — depend on this epic)

- **Epic 35 (Billing)**: charges against `PlanEntitlement` / `PlanPrice`; metering keys off
  `EntitlementMetricKey`; calls `IPricingContract` only.
- **Sibling Enforcement epic**: consumes `EntitlementMetricKey` + resolved entitlements through
  `IPricingContract` to gate runtime usage. (Enforcement is **not** in Epic 34 scope.)

### External Dependencies

- **PostgreSQL 17**: partial unique indexes (`WHERE Status = 'active'`), jsonb metered components,
  CHECK constraints pinning closed enums.
- **EF Core 9 / Npgsql**: value converters (`EntitlementMetricKey`), additive control-plane migrations.
- **No Stripe / payment dependency in this epic** — pricing data is computed and stored, not charged.
  Charging, checkout, invoices, and webhooks are Epic 35.

## Database Schema

All Epic 34 tables are **control-plane resident** (global rows in `ControlPlaneDbContext`) — there is
**no per-tenant schema (`t_<hex>`) pricing table.

```sql
-- 34-1: Plan catalog (versioned, immutable). Plan gains versioning columns; legacy
-- Slug/DisplayName/PlacementPolicy and the opaque Quotas JSON are kept for one deprecation window.
ALTER TABLE plans ADD COLUMN "Version"          INT  NOT NULL DEFAULT 1;
ALTER TABLE plans ADD COLUMN "Status"           TEXT NOT NULL DEFAULT 'active'
  CHECK ("Status" IN ('active','deprecated','draft'));
ALTER TABLE plans ADD COLUMN "IsCustom"         BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE plans ADD COLUMN "BillingInterval"  TEXT NOT NULL DEFAULT 'monthly'
  CHECK ("BillingInterval" IN ('monthly','annual'));
ALTER TABLE plans ADD COLUMN "SupersedesPlanId" UUID REFERENCES plans("Id");

CREATE UNIQUE INDEX UX_plans_Slug_Version       ON plans ("Slug", "Version");
CREATE UNIQUE INDEX UX_plans_OneActivePerSlug   ON plans ("Slug") WHERE "Status" = 'active';

CREATE TABLE plan_features (
  "Id"          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "PlanId"      UUID NOT NULL REFERENCES plans("Id") ON DELETE RESTRICT,
  "FeatureKey"  TEXT NOT NULL,            -- e.g. byok_allowed, support_tier
  "BoolValue"   BOOLEAN,
  "StringValue" TEXT,
  UNIQUE ("PlanId", "FeatureKey")
);

CREATE TABLE plan_entitlements (
  "Id"          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "PlanId"      UUID NOT NULL REFERENCES plans("Id") ON DELETE RESTRICT,
  "MetricKey"   TEXT NOT NULL,            -- EntitlementMetricKey snake_case
  "LimitValue"  BIGINT,                   -- NULL = unlimited
  "Period"      TEXT NOT NULL DEFAULT 'monthly' CHECK ("Period" IN ('monthly','total')),
  "OverageMode" TEXT NOT NULL DEFAULT 'block' CHECK ("OverageMode" IN ('block','allow','meter')),
  UNIQUE ("PlanId", "MetricKey")
);

CREATE TABLE plan_prices (
  "Id"               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "PlanId"           UUID NOT NULL REFERENCES plans("Id") ON DELETE RESTRICT,
  "PricingMode"      TEXT NOT NULL CHECK ("PricingMode" IN ('platform_provided','byok')),
  "RecurringUsd"     NUMERIC(20,4) NOT NULL DEFAULT 0,
  "SeatUsd"          NUMERIC(20,4) NOT NULL DEFAULT 0,
  "MeteredComponent" JSONB NOT NULL DEFAULT '{}',
  UNIQUE ("PlanId", "PricingMode")
);

-- 34-3: Per-(tenant, provider) BYOK vs platform mode. BYOK rows carry a cabinet secret name.
CREATE TABLE tenant_provider_billing (
  "Id"          UUID PRIMARY KEY DEFAULT gen_random_uuid(),  -- UUIDv7
  "TenantId"    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  "ProviderKey" TEXT NOT NULL,            -- "anthropic" | "openai" | "openrouter"
  "Mode"        TEXT NOT NULL DEFAULT 'platform' CHECK ("Mode" IN ('platform','byok')),
  "SecretName"  TEXT,                     -- cabinet name when byok; NULL for platform
  "Status"      TEXT NOT NULL DEFAULT 'active' CHECK ("Status" IN ('active','disabled')),
  CONSTRAINT ck_tpb_secret_xor CHECK (
    ("Mode" = 'byok'     AND "SecretName" IS NOT NULL) OR
    ("Mode" = 'platform' AND "SecretName" IS NULL)),
  "CreatedAt"   TIMESTAMPTZ NOT NULL, "UpdatedAt" TIMESTAMPTZ NOT NULL,
  "CreatedBy"   UUID, "UpdatedBy" UUID
);
CREATE UNIQUE INDEX ux_tpb_active_provider
  ON tenant_provider_billing ("TenantId", "ProviderKey") WHERE "Status" = 'active';

-- 34-3: per-call cost record gains the mode the markup engine keys off.
ALTER TABLE provider_diagnostics ADD COLUMN "BillingMode" TEXT NOT NULL DEFAULT 'platform';

-- 34-4: Pins each tenant to a specific (PlanId, PlanVersion) — exactly one active per tenant.
CREATE TABLE tenant_plan_assignments (
  "Id"          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"    UUID NOT NULL REFERENCES tenants(id),
  "PlanId"      UUID NOT NULL REFERENCES plans("Id"),
  "PlanVersion" INT  NOT NULL,
  "Status"      TEXT NOT NULL DEFAULT 'active' CHECK ("Status" IN ('active','superseded')),
  "EffectiveFrom" TIMESTAMPTZ NOT NULL,
  "CreatedAt"   TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX ux_tpa_one_active_per_tenant
  ON tenant_plan_assignments ("TenantId") WHERE "Status" = 'active';

-- 34-5: Margin policy (provider > plan > global resolution). At least one of multiplier / fixed.
CREATE TABLE margin_policies (
  "Id"               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "Scope"            TEXT NOT NULL CHECK ("Scope" IN ('global','plan','provider')),
  "RefKey"           TEXT,                 -- NULL=global, plan slug, or provider key
  "MarkupMultiplier" NUMERIC(20,6),
  "FixedUsdPer1M"    NUMERIC(20,6),
  "EffectiveFrom"    TIMESTAMPTZ NOT NULL,
  "Status"           TEXT NOT NULL DEFAULT 'active' CHECK ("Status" IN ('active','superseded')),
  CONSTRAINT ck_margin_nonempty CHECK (
    "MarkupMultiplier" IS NOT NULL OR "FixedUsdPer1M" IS NOT NULL),
  "CreatedAt"        TIMESTAMPTZ NOT NULL, "UpdatedAt" TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX ux_margin_one_active
  ON margin_policies ("Scope", "RefKey") NULLS NOT DISTINCT WHERE "Status" = 'active';

-- 34-7: trials, prepaid/granted USD credits, promo codes (control-plane, per-tenant).
-- tenant_trials, tenant_credits (granted/consumed USD ledger), promo_codes + redemptions.
```

## Implementation Phases

### Phase 1: Catalog Foundation (34-1, 34-2) — Week 1

- Versioned/immutable `Plan` + `PlanFeature` / `PlanEntitlement` / `PlanPrice` rows;
  `EntitlementMetricKey` enum; `IPlanCatalogService` + `PlanSnapshot`; immutability + supersede
  guard; insert-missing-only seeder.
- Admin CRUD / version-management API + custom enterprise plans.
- Estimated: 7-9 days

### Phase 2: Pricing Mode & Assignment (34-3, 34-4) — Week 2

- `TenantProviderBilling` + `IProviderKeyResolver`; close the BYOK gap in `CallLlmActivity`;
  enable/disable flows wired to the Epic 29 cabinet; `ProviderDiagnostic.BillingMode`.
- `TenantPlanAssignment` (pin `PlanId` + `PlanVersion`); audited assign/change lifecycle.
- Estimated: 7-9 days

### Phase 3: Pricing & Entitlement Resolution (34-5, 34-6, 34-7) — Week 3

- Pure `IUsagePricingEngine` + `MarginPolicy` (provider→plan→global); `IEntitlementService` (closed
  7-metric resolved map per tenant); trials/credits/promos + net-price engine.
- Estimated: 10-13 days

### Phase 4: Audit, UI & Published Contract (34-8, 34-9, 34-10) — Week 4

- Pricing audit events + time-travel replay query; admin + tenant dashboards; `IPricingContract`
  façade, Epic 20 decommission, and the legacy `Plan.Quotas` / `Tenant.Plan` back-fill.
- Estimated: 10-13 days

## Success Metrics

- A tenant pinned to a plan version re-derives identical pricing/quotas after that version is
  deprecated (100% reproducibility; golden-file + contract conformance tests green).
- 0% reads of the platform key for a BYOK `(tenant, provider)` row; 100% of BYOK-miss cases throw
  `PROVIDER.KEY.RESOLVE.BYOK_MISSING` (no silent empty / no platform fallback in any test).
- After back-fill, **no non-deleted tenant lacks an active plan assignment** (verification query
  returns zero rows); the back-fill is idempotent (second run inserts zero rows).
- Exactly one `active` plan version per slug and one `active` assignment per tenant at all times
  (DB-enforced partial unique indexes; no race leaves two active rows).
- `ResolveEntitlementsAsync` always returns all 7 `EntitlementMetricKey` members (closed map).
- Every pricing decision is replayable: "plan/price/entitlements/BYOK-mode for tenant X at T" is
  reconstructable from `platform_events` with zero un-explainable priced amounts.
- No downstream epic takes a direct dependency on pricing entities — all cross-epic reads go through
  `IPricingContract` (enforced by the contract conformance tests).

## Reference Documents

- [Epic 34 Stories](./) — `story-34-1/` … `story-34-10/`
- [Pricing Contract (Story 34-10)](./pricing-contract.md) — the stable `IPricingContract` surface
- [CLAUDE.md — Operating Modes & Prompt Store RBAC](../../../CLAUDE.md) — per-mode ownership rules
- [CLAUDE.md — Multi-tenant provisioning (Cranl)](../../../CLAUDE.md) — control-plane / tenancy model
- [Epic 20 README](../epic-20/README.md) — superseded TS billing/plan plan (see Supersedes above)
- [Migration Ordering Note](../migration-ordering.md) — control-plane vs tenant-schema ordering
- [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md) — mandatory 7-phase development workflow

---

**Last Updated**: 2026-06-17
**Epic Owner**: TBD
**Implementation Start**: TBD
**Total Estimated Effort**: 34-44 days
