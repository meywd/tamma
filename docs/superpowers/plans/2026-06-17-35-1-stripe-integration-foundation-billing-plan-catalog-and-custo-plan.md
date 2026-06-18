# Story 35-1 — Stripe Integration Foundation, Billing Plan Catalog & Customer Mapping (C#)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for every
> task — write the failing test first, then the implementation. This story is part of Epic 35
> (Billing & Payments, C# control plane). Steps use checkbox (`- [ ]`) syntax for tracking.
> SaaS-only: every Stripe touch-point must no-op in single-user mode.

**Goal:** Introduce the `Stripe.net` SDK into `Tamma.Api`, a mode-aware billing seam
(`IBillingProvider` → `StripeBillingProvider` / `NullBillingProvider`), a control-plane
`BillingCustomer` mapping (one per tenant) created in the tenant-create transaction, and a
`billing_plan_prices` catalog (slug → Stripe Product/Price/Meter ids) populated by an idempotent
`seed-billing` CLI command. Stripe credentials resolve through the Epic 29 secret cabinet (never raw
env in prod). All actions audited via DCB events. This is the foundation every other Epic 35 story
builds on.

**Non-goals (YAGNI guard):**
- NO subscription lifecycle, checkout, or webhook *ingestion* endpoint (Story 35-2 / later). This
  story only stores the webhook signing-secret cabinet reference.
- NO usage metering, token markup, or BYOK suppression (Story 35-3). Only the `BillingMode` flag +
  default `PlatformProvided` is stored here; the three meters are *created* but never *read*.
- NO invoicing, dunning, tax computation, billing portal, or credits wallet (later stories).
- NO tenant-facing billing UI or endpoints. The customer mapping is a side effect of existing
  tenant-create endpoints; the only operator surface is the `seed-billing` CLI.
- NO change to tenancy placement — `Plan.Quotas`/`Plan.PlacementPolicy` are untouched; Stripe ids
  live in a new `billing_plan_prices` table keyed by slug.
- NO new alert rules (Story 5.6 scope). Events are emitted CP-resident so a future rule can observe
  them.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`, builds need no wrapper). New dep: `Stripe.net`.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Control-plane data layer
- `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` — DbSets declared as
  `public DbSet<X> Xs => Set<X>();` (e.g. `Plans` line 76, `DomainEvents` line 199,
  `PlatformQueuedTasks` line 78). Add `BillingCustomers` + `BillingPlanPrices` here.
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` — `ConfigureControlPlaneEntities`
  (line 47) is the *single* place entity mapping lives: `entity.ToTable(...)`, `HasCheckConstraint`
  (e.g. line 61), `HasIndex(...).IsUnique().HasFilter(...)` (e.g. line 86–88, tenants 298–299).
  Mirror this exactly for the two new entities.
- `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` — `Plan.Slug` (`free`/`team`/`enterprise`),
  `Quotas`/`PlacementPolicy` are **placement** concerns (doc-comment line 39–43). Do NOT add Stripe
  ids here.
- `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` — tenant entity; FK target.
- `apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs` — idempotent seed pattern: `AnyAsync`
  short-circuit then `AddRangeAsync` (lines 44–93). `BillingSeeder` follows the same
  re-run-is-no-op contract but with Stripe upserts.
- `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — existing baseline
  `20260609205701_InitialControlPlane.cs` + snapshot. New migration is **additive** (two new tables)
  — normal `dotnet ef migrations add`, then verify `has-pending-model-changes` reports none.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync(DomainEvent)`
  (line 7). `DomainEvent` (`Tamma.Data/Entities/DomainEvent.cs`) has `Type`, `TenantId`, `Tags`,
  `Metadata`, `Data`, `CreatedAt`. DCB shape is set by `OrgEndpoints.EmitTenantEvent`
  (`OrgEndpoints.cs:1036–1066`): `Metadata` = `{"workflowVersion":"1.0.0","eventSource":"system"}`,
  `Tags`/`Data` JSON-serialized. Reuse that shape for `BillingEvents`.

### Tenant-create paths (the real hook sites)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:30–89` — `CreateOrg`: `new Tenant` via
  `tenantRepo.CreateAsync` (line 57), `provisioning.ProvisionAsync(tenant.Id)` (line 78), then
  `EmitTenantEvent(..., "TENANT.CREATED.SUCCESS", ...)` (line 81). **This is the primary hook.**
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:275` — registration path also runs
  `new Tenant`/`CreateAsync`. Add the same hook.
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` — **NOT a create path.**
  It is the Story 28-11 lifecycle manager (list/detail/retry/delete/change-plan, `OwnerAccess`),
  no `new Tenant`. The spec named it; correct target is OrgEndpoints/AuthEndpoints. (Documented in
  the story's Technical Design callout.)

### Secret cabinet (Epic 29) — reuse, don't reinvent
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` — write/rotate surface;
  **never returns plaintext** through public signatures (doc-comment lines 9–16).
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretScope.cs` — `Platform` (TenantId null,
  platform-admin only) vs `Tenant`. Stripe key = `Platform`.
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretPurpose.cs` — `ApiKey` (line 29, "OpenAI
  key, GitHub App credentials"), `Webhook` (line 50). Stripe key → `ApiKey`; webhook signing
  secret → `Webhook`.
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Stopgap/RuntimeSecretResolver.cs` — the
  runtime-read pattern: cabinet-first, dev-only env fallback, **Story 29-10 prod fail-fast** (line
  ~88 onward "no fallback. Fail-fast"). `IRuntimeSecretResolver.GetAsync(cabinetName)` is exactly
  AC5. Resolve `billing/stripe-secret-key` through it.
- `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/IAlertChannelSecretReader.cs` — precedent for a
  minimal read-only secret seam (`DefaultAlertChannelSecretReader` reads active version via
  `ISecretStoreBackend.GetVersionPlaintextAsync`). Use as a model if a dedicated reader is needed,
  but prefer `IRuntimeSecretResolver`.

### Platform task queue (retry seam)
- `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformQueuedTask.cs` — `Type`, `TenantId`, `Payload`,
  `Status`, `RetryCount`, dead-letter on ceiling.
- `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` — `TaskType` +
  `HandleAsync(task, ct)`. Normal throw = retryable; `PlatformTaskTerminalException` = dead-letter.
  Registered via `services.AddPlatformTaskHandler<T>()`. `CreateBillingCustomerTaskHandler` plugs in
  here.

### Mode + CLI dispatch
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider.Mode`
  (`SingleUser`/`SaaS`); detection from `Tamma:Mode` explicit, else `Tamma:TenantSharedSecret` /
  `ConnectionStrings:ControlPlane` presence (lines 67–96). Drives `NullBillingProvider` vs
  `StripeBillingProvider` registration.
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1140–1148` — CLI dispatch precedent: `migrate-secrets`
  runs `MigrateSecretsCommand.RunAsync(app.Services)` *before* the HTTP pipeline and returns an exit
  code. `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Stopgap/MigrateSecretsCommand.cs` —
  `ShouldRun(args)` (line 21, `args[0] == "migrate-secrets"`) + `RunAsync`. `SeedBillingCommand`
  mirrors this exactly with `seed-billing`.
- Seeders run at startup post-migration in `Program.cs` (`PlansSeeder.SeedAsync` at line 2030,
  `TenantDatabasesSeeder` at line 2064). `BillingSeeder` is NOT auto-run at startup (Stripe calls
  must be explicit/operator-triggered) — it runs only via `seed-billing`.
- Auth policies (`Program.cs:971–1012`): `OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`,
  `PromptManage`. Any future admin billing route uses `OwnerAccess`.

### Not present yet (all NEW)
- No `Stripe` reference in any `.csproj` (grep clean). No `Services/Billing/` directory. No billing
  entity. All billing code is greenfield.

---

## Architecture

**Mode-gated seam → cabinet-resolved Stripe client → CP entities → DCB events**, reusing the secret
cabinet and platform task queue end-to-end:

1. **`IBillingProvider`** is the single billing seam. `StripeBillingProvider` (SaaS) resolves the
   key from the cabinet and calls Stripe; `NullBillingProvider` (single-user) reports
   `IsEnabled=false` and no-ops. DI picks one based on `ITammaModeProvider.Mode`.
2. **`BillingCustomer`** (CP table, unique `TenantId`) is the tenant→Stripe mapping, created inside
   the tenant-create transaction; Stripe failure falls back to a `PlatformQueuedTask` retry so
   tenant creation is never blocked.
3. **`BillingPlanPrice`** (CP table, unique `PlanSlug`) is the slug→Stripe-ids catalog, populated by
   the idempotent `seed-billing` command which also mints the three Billing Meters.
4. **DCB events** `BILLING.CUSTOMER.CREATED` and `BILLING.PLAN_CATALOG.SYNCED` append to the CP
   `DomainEvents` store via `IEventRepository` (same store the alert evaluator polls).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Billing provider | `NullBillingProvider` (`IsEnabled=false`) | `StripeBillingProvider` |
| Who owns the catalog? | n/a (no Stripe) | Platform owner (`OwnerAccess`); platform-global, slug-keyed |
| Who owns a customer mapping? | n/a | The tenant — exactly one `BillingCustomer` per `TenantId`, never cross-tenant |
| Stripe key source | n/a | cabinet `billing/stripe-secret-key` (`SecretScope.Platform`, `SecretPurpose.ApiKey`); prod fail-fast if absent |
| Seed command | prints "billing is SaaS-only", exit 0 | runs catalog sync |
| Mode source | `ITammaModeProvider` (process-stable) | same |

---

## Phased task breakdown

### Phase 1 — Data layer: entities, DbContext, migration (foundation)

- [ ] **T1.1 — `BillingMode` enum.** New `apps/tamma-elsa/src/Tamma.Core/Billing/BillingMode.cs`
  (`{ PlatformProvided, Byok }`). *Test first:* trivial — covered transitively by T1.2 column tests.
- [ ] **T1.2 — `BillingCustomer` + `BillingPlanPrice` entities.** New
  `Tamma.Data/Entities/BillingCustomer.cs` (Id, TenantId, StripeCustomerId?, BillingMode text,
  DefaultCurrency, TaxStatus, timestamps, Tenant nav) and
  `Tamma.Data/Entities/BillingPlanPrice.cs` (Id, PlanSlug, product/base-price + 3×(meter,price)
  ids, timestamps). *Tests first:* `BillingEntityModelTests` — assert column defaults
  (`BillingMode="PlatformProvided"`, `DefaultCurrency="usd"`).
- [ ] **T1.3 — DbContext + model config.** Add `DbSet<BillingCustomer> BillingCustomers` and
  `DbSet<BillingPlanPrice> BillingPlanPrices` to `ControlPlaneDbContext.cs`; configure in
  `TammaModelConfiguration.ConfigureControlPlaneEntities`: tables `billing_customers` /
  `billing_plan_prices`, unique index on `BillingCustomer.TenantId`, filtered-unique on
  `StripeCustomerId`, unique on `BillingPlanPrice.PlanSlug`, CHECK on `BillingMode`, FK to `tenants`
  (cascade). *Tests first:* model-snapshot/EnsureCreated test asserting the indexes + CHECK exist.
- [ ] **T1.4 — EF migration.** `dotnet ef migrations add AddBillingCustomerAndPlanPrices --context
  ControlPlaneDbContext --output-dir Migrations/ControlPlane` (build, no wrapper). Verify
  `dotnet ef migrations has-pending-model-changes` → none. *Tests first (integration, docker):*
  `BillingMigrationTests` applies `Update` then down/`Remove`-equivalent on a real Postgres CP DB.

### Phase 2 — Billing seam + Stripe client + secret resolution

- [ ] **T2.1 — Add `Stripe.net` to `Tamma.Api.csproj`.** Research the latest stable version + the
  `Billing.MeterService`, `CustomerService`, `ProductService`, `PriceService`, and
  `RequestOptions.IdempotencyKey` surfaces **before** writing calls (do not assume signatures).
- [ ] **T2.2 — `BillingOptions` + `StripeClientFactory`.** New `Services/Billing/BillingOptions.cs`
  (cabinet names `billing/stripe-secret-key`, `billing/stripe-webhook-secret`; `DefaultCurrency`)
  and `StripeClientFactory.cs` building `Stripe.StripeClient` from
  `IRuntimeSecretResolver.GetAsync(...)`. *Tests first:* `StripeClientFactoryTests` — resolves key
  from a stubbed resolver; production env + null key → throws (AC5 fail-fast); dev env + null →
  documented behaviour.
- [ ] **T2.3 — `IBillingProvider` + `NullBillingProvider`.** New
  `Services/Billing/IBillingProvider.cs` (`IsEnabled`, `CreateCustomerAsync`, `SyncCatalogAsync`)
  and `NullBillingProvider.cs` (`IsEnabled=false`, no-op/throw). *Tests first:*
  `NullBillingProviderTests` — `IsEnabled=false`, no Stripe calls possible.
- [ ] **T2.4 — `StripeBillingProvider.CreateCustomerAsync`.** New `StripeBillingProvider.cs` —
  create Stripe `Customer` with deterministic idempotency key `billing-customer-{tenantId}`, persist
  `BillingCustomer`, return mapped row; duplicate tenant returns the existing row (lookup before
  create). *Tests first:* `StripeBillingProviderTests` — correct idempotency key, row persisted,
  duplicate-tenant short-circuit, mocked `CustomerService`.

### Phase 3 — Catalog reader + idempotent seed

- [ ] **T3.1 — `IBillingCatalog` + `BillingCatalog`.** New read-side seam resolving
  `BillingPlanPrice` by slug (EF-backed, cached). *Tests first:* `BillingCatalogTests` — known slug
  returns row, unknown slug throws, cache hit.
- [ ] **T3.2 — `BillingSeeder` + `StripeBillingProvider.SyncCatalogAsync`.** New
  `Tamma.Data/Seeders/BillingSeeder.cs` orchestrating: for each `Plan.Slug`, upsert Stripe Product,
  base Price, three metered Prices, and three Billing Meters (`tamma.platform_tokens_input` SUM,
  `tamma.platform_tokens_output` SUM, `tamma.seats` LAST), then write ids into `billing_plan_prices`
  (insert-if-absent / update-existing). Deterministic idempotency keys
  `billing-catalog-{slug}-{resource}`. *Tests first:* `BillingSeederTests` — first run creates +
  writes ids; second run reuses existing ids (0 create calls, no row churn); emits one
  `BILLING.PLAN_CATALOG.SYNCED` per slug; meters created with correct aggregation
  (SUM/SUM/LAST), all Stripe services mocked.
- [ ] **T3.3 — `BillingEvents` helper.** New `Services/Billing/BillingEvents.cs` building
  `DomainEvent` rows for `BILLING.CUSTOMER.CREATED` / `BILLING.PLAN_CATALOG.SYNCED` in the
  `EmitTenantEvent` shape. *Tests first:* assert Type, Tags JSON, TenantId set/null per event.

### Phase 4 — Tenant-create hook + retry handler

- [ ] **T4.1 — `CreateBillingCustomerTaskPayload` + handler.** New
  `Services/Billing/Tasks/CreateBillingCustomerTaskPayload.cs` and
  `CreateBillingCustomerTaskHandler.cs` (`IPlatformTaskHandler`, `TaskType="billing.customer.create"`)
  re-driving `CreateCustomerAsync`, filling `StripeCustomerId`, emitting `BILLING.CUSTOMER.CREATED`.
  Malformed payload → `PlatformTaskTerminalException`; transient Stripe error rethrows (retry).
  *Tests first:* `CreateBillingCustomerTaskHandlerTests` — happy path fills id + emits; terminal vs
  retryable failure paths.
- [ ] **T4.2 — Wire the hook into `OrgEndpoints.CreateOrg`.** After `ProvisionAsync`, if
  `billing.IsEnabled`: try `CreateCustomerAsync` + emit event; on failure enqueue the
  `PlatformQueuedTask` (do NOT block creation). Inject `IBillingProvider` +
  `IPlatformQueuedTaskRepository` into the endpoint. *Tests first (integration):*
  `BillingTenantCreateIntegrationTests` — create org (Stripe mocked) → one `BillingCustomer` + one
  `BILLING.CUSTOMER.CREATED`; Stripe-fail → row with null id + one queued task, tenant still created.
- [ ] **T4.3 — Wire the same hook into `AuthEndpoints` registration tenant-create path
  (`AuthEndpoints.cs:275`).** *Tests:* extend integration coverage for the registration path.

### Phase 5 — DI wiring + CLI command + single-user seam

- [ ] **T5.1 — `BillingServiceCollectionExtensions.AddTammaBilling`.** New extension: register
  `IBillingProvider` as `StripeBillingProvider` when `Mode==SaaS` else `NullBillingProvider`;
  register `IBillingCatalog`, `StripeClientFactory`, `BillingOptions`, and the task handler via
  `AddPlatformTaskHandler<CreateBillingCustomerTaskHandler>()`. *Tests first:*
  `AddTammaBillingTests` — SaaS config resolves `StripeBillingProvider`; single-user config resolves
  `NullBillingProvider`.
- [ ] **T5.2 — `SeedBillingCommand` + `Program.cs` dispatch.** New `Services/Billing/
  SeedBillingCommand.cs` (`ShouldRun(args) => args[0]=="seed-billing"`, `RunAsync(services)`):
  single-user → print SaaS-only + exit 0; SaaS → run `BillingSeeder`, print per-slug report, exit
  0/1. Add `AddTammaBilling()` call + the `seed-billing` dispatch block in `Program.cs` next to the
  `migrate-secrets` block (line ~1143). *Tests first:* `SeedBillingCommandTests` — `ShouldRun`
  arg matching; single-user no-op exit 0.

### Phase 6 — Verification

- [ ] **T6.1** Run `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` — full suite
  green (no regressions in the existing 4575).
- [ ] **T6.2** `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` →
  none.
- [ ] **T6.3** Manually run `dotnet run --project apps/tamma-elsa/src/Tamma.Api -- seed-billing`
  twice against a Stripe test account (opt-in, behind `STRIPE_SECRET_KEY_TEST`); confirm second run
  is a no-op (0 created).

---

## Sequencing & dependencies

```
T1.1 → T1.2 → T1.3 → T1.4            (data layer — must land first)
              ↓
T2.1 → T2.2 → T2.3 → T2.4            (seam + client; T2.4 needs T1.2)
              ↓
T3.1 → T3.2 → T3.3                   (catalog + seed; needs T1 + T2)
              ↓
T4.1 → T4.2 → T4.3                   (hook + retry; needs T2.4 + T3.3 + queue)
              ↓
T5.1 → T5.2                          (DI + CLI; needs all providers/handlers)
              ↓
T6.x                                 (verification)
```

- **Hard prerequisite:** Phase 1 (data) before everything. Phase 2 before Phase 3/4.
- **External:** `Stripe.net` (T2.1) gates all Stripe SDK calls — research latest API first.
- **Story prerequisites:** Epic 28 (CP, tenants, plan, queue, mode), Epic 29 (cabinet +
  `IRuntimeSecretResolver`), Epic 4 (DCB events) — all present at main.

## Risks + mitigations

- **Stripe outage blocks tenant creation.** *Mitigation:* T4.2 non-blocking try/catch →
  `PlatformQueuedTask` retry (the established worker), row persists with null `StripeCustomerId`. The
  integration test pins this.
- **Duplicate Stripe objects on retry/re-seed.** *Mitigation:* deterministic idempotency keys
  (`billing-customer-{tenantId}`, `billing-catalog-{slug}-{resource}`) + unique `TenantId`/`PlanSlug`
  DB constraints + lookup-by-stored-id in the seeder. Tests assert second seed run = 0 creates.
- **Stripe.net API drift vs assumed signatures.** *Mitigation:* T2.1 mandates researching current
  docs before coding; all tests mock at the Stripe *service-interface* boundary, so the SDK shape is
  isolated to a few factory/provider files.
- **Raw env Stripe key in production.** *Mitigation:* AC5 + T2.2 reuse `IRuntimeSecretResolver`'s
  Story 29-10 prod fail-fast; a `BillingSecretBootTests` case proves boot throws when the cabinet row
  is absent in production.
- **Accidental Stripe coupling in single-user.** *Mitigation:* mode-gated DI (T5.1) +
  `NullBillingProvider`; tests assert zero SDK calls and zero rows in single-user.
- **Migration discipline.** *Mitigation:* `billing_*` tables are additive (not a baseline CHECK
  edit), but T1.4 still verifies `has-pending-model-changes` reports none and mirrors entity config
  only in `TammaModelConfiguration.cs` (the single source).
- **Wrong create-path hook.** *Mitigation:* finding above documents that `AdminTenantsEndpoints` is
  lifecycle-only; the hook lands in `OrgEndpoints.CreateOrg` + `AuthEndpoints` (verified `new
  Tenant` sites).

## Acceptance criteria (mirror the story)

- [ ] `BillingCustomer` entity + table exist (unique `TenantId`, FK to `tenants`, CHECK on
  `BillingMode`); `BillingPlanPrice` entity + table exist (unique `PlanSlug`); both wired into
  `ControlPlaneDbContext` + `TammaModelConfiguration`; additive migration applies + rolls back;
  `has-pending-model-changes` = none.
- [ ] `IBillingProvider` resolves to `StripeBillingProvider` in SaaS, `NullBillingProvider` in
  single-user.
- [ ] Stripe key + webhook secret resolve via the Epic 29 cabinet (`SecretScope.Platform`,
  `SecretPurpose.ApiKey`/`Webhook`); production refuses to boot billing with only a raw env key.
- [ ] Tenant creation in `OrgEndpoints.CreateOrg` / `AuthEndpoints` creates a Stripe customer +
  `BillingCustomer` row in the create transaction; Stripe failure enqueues a `PlatformQueuedTask`
  retry instead of blocking creation.
- [ ] `seed-billing` CLI idempotently creates/updates Stripe Products, Prices, and the three Billing
  Meters (`tamma.platform_tokens_input` SUM, `tamma.platform_tokens_output` SUM, `tamma.seats` LAST)
  and writes ids into `billing_plan_prices`; re-run is a no-op.
- [ ] `BILLING.CUSTOMER.CREATED` (tags `{tenantId, stripeCustomerId, billingMode}`) and
  `BILLING.PLAN_CATALOG.SYNCED` (tags `{planSlug, source}`) appended to `DomainEvents`.
- [ ] Single-user mode registers `NullBillingProvider`: no Stripe calls, no `BillingCustomer` row,
  no billing endpoints/command effect.
- [ ] Unit tests cover catalog mapping, idempotent seed, customer-create-on-tenant-create
  (Stripe mocked), retry enqueue, and the single-user no-op seam; tenant-isolation test asserts one
  `BillingCustomer` per tenant.
- [ ] Full xUnit suite green; Stripe secret/webhook/payment details never logged.
