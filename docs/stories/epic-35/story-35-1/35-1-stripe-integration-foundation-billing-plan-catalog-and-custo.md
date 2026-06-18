# Story 35-1: Stripe Integration Foundation, Billing Plan Catalog & Customer Mapping (C#)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), `.dev/` knowledge-base usage (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the test-first (TDD) mandate, and the build-success / coverage gates.

## User Story

As a **platform owner** running Tamma in SaaS mode,
I want each tenant mapped to a Stripe customer and a billing plan catalog that maps every `Plan.Slug` to Stripe Product/Price/Meter ids — wired through the Epic 29 secret cabinet, never raw env,
So that downstream billing stories (subscriptions, metering, invoicing, dunning) have a stable customer-to-Stripe binding and a single source of truth for prices, while single-user deployments incur zero Stripe coupling.

## Priority

P0 - Foundational. Every other Epic 35 story (subscriptions 35-2, BYOK-aware metering 35-3, invoicing, dunning, portal, credits) depends on the `BillingCustomer` mapping and the `billing_plan_prices` catalog created here.

## Acceptance Criteria

1. A new control-plane entity `BillingCustomer` is added at `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs` with: `Id` (Guid PK), `TenantId` (Guid, **unique** FK to `tenants.Id`), `StripeCustomerId` (string, nullable until Stripe ack), `BillingMode` (enum `BillingMode { PlatformProvided, Byok }` persisted as text), `DefaultCurrency` (string, ISO-4217, default `usd`), `TaxStatus` (string, e.g. `none`/`taxable`/`reverse_charge`), `CreatedAt`, `UpdatedAt`. Registered as `DbSet<BillingCustomer> BillingCustomers` on `ControlPlaneDbContext` and configured in `TammaModelConfiguration.ConfigureControlPlaneEntities` (table `billing_customers`, unique index on `TenantId`, FK to `tenants`, CHECK constraint on `BillingMode` text domain).

2. A new control-plane entity `BillingPlanPrice` is added at `apps/tamma-elsa/src/Tamma.Data/Entities/BillingPlanPrice.cs` mapping a `PlanSlug` (`free`/`team`/`enterprise`) to `StripeProductId`, base `StripePriceId`, and per-meter metered price ids (`TokensInputPriceId`, `TokensOutputPriceId`, `SeatsPriceId`) plus the three meter ids (`TokensInputMeterId`, `TokensOutputMeterId`, `SeatsMeterId`). Stored in a `billing_plan_prices` table — it does **NOT** overload `Plan.Quotas` or `Plan.PlacementPolicy` (those remain tenancy-placement concerns per `Plan.cs`). Unique index on `PlanSlug`.

3. An EF Core migration is added under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` creating both tables (plus snapshot update). `dotnet ef migrations has-pending-model-changes` reports none after the migration; `Update` then `Remove`/down applies and rolls back cleanly.

4. An `IBillingProvider` seam (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingProvider.cs`) exposes `CreateCustomerAsync(Guid tenantId, CustomerDescriptor)`, `SyncCatalogAsync(CancellationToken)`, and `IsEnabled` — implemented by `StripeBillingProvider` (SaaS) and `NullBillingProvider` (single-user no-op).

5. The Stripe **secret key** and **webhook signing secret** resolve via the Epic 29 cabinet — `ISecretStore` / `ISecretStoreBackend` reads of platform-scoped rows (`SecretScope.Platform`, `SecretPurpose.ApiKey` for the API key, `SecretPurpose.Webhook` for the signing secret), accessed at runtime through the established `IRuntimeSecretResolver` pattern (cabinet name e.g. `billing/stripe-secret-key`). In production (`!IsDevelopment`) billing **refuses to boot** if the Stripe key is present only as a raw `IConfiguration`/env value with no cabinet row (fail-fast, mirroring `RuntimeSecretResolver` Story 29-10 semantics).

6. On tenant creation (the `OrgEndpoints.CreateOrg` path at `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:57`, and the registration path that creates a tenant in `AuthEndpoints`), a Stripe Customer is created and the `BillingCustomer` row is persisted **within the same control-plane transaction** as the `TENANT.CREATED.SUCCESS` emission. If the Stripe API call fails, tenant creation is **not** blocked: a `billing.customer.create` retry is enqueued as a `PlatformQueuedTask` (handled by a new `IPlatformTaskHandler`) and the `BillingCustomer` row persists with `StripeCustomerId = null`.

7. A CLI seed command (`dotnet run --project Tamma.Api -- seed-billing`, dispatched in `Program.cs` alongside the existing `migrate-secrets` command at `Program.cs:1143`) idempotently creates/updates Stripe Products, base Prices, three metered Prices, and **three Billing Meters** — `tamma.platform_tokens_input` (SUM), `tamma.platform_tokens_output` (SUM), `tamma.seats` (LAST/gauge) — and writes their ids into `billing_plan_prices`. Re-running the command is a no-op (existing ids are reused via Stripe idempotency keys + lookup-by-stored-id).

8. The seed command and the customer-create path both emit DCB events to `DomainEvent` via `IEventRepository.AppendAsync`: `BILLING.PLAN_CATALOG.SYNCED` (tags `{ planSlug, source: "seed" }`) on a successful catalog sync, and `BILLING.CUSTOMER.CREATED` (tags `{ tenantId, stripeCustomerId, billingMode }`, `TenantId` set) on customer creation. Event types follow the `AGGREGATE.ACTION.STATUS` convention.

9. In **single-user mode** (`ITammaModeProvider.Mode == TammaMode.SingleUser` — no `Tamma:TenantSharedSecret` / `ConnectionStrings:ControlPlane`), `NullBillingProvider` is registered: zero Stripe SDK calls are made, the tenant-create hook is a no-op (no `BillingCustomer` row, no event), and the billing endpoints / seed command short-circuit with a clear "billing is SaaS-only" message.

10. `BillingMode` defaults to `PlatformProvided`; a tenant flagged BYOK (provider keys supplied by the tenant) is recorded as `Byok` so 35-3 metering can suppress token markup. This story only stores the flag and defaults it — it does **not** implement metering or markup suppression (Story 35-3 boundary).

11. RBAC is enforced per CLAUDE.md per-mode ownership: in SaaS, the seed command and any admin billing-catalog read are `OwnerAccess` (platform-owner only); the customer-create hook runs as a system operation inside the tenant-create transaction (no extra caller permission). No tenant-facing endpoint is added in this story.

12. Tenant isolation: `BillingCustomer` is keyed by `TenantId` with a unique constraint; a second create attempt for the same tenant resolves the existing row (no duplicate Stripe customer). The catalog (`billing_plan_prices`) is platform-global (keyed by `PlanSlug`, not tenant) and is never exposed cross-tenant.

13. Stripe SDK calls use idempotency keys (`Stripe.RequestOptions.IdempotencyKey`) derived deterministically (`billing-customer-{tenantId}`, `billing-catalog-{planSlug}-{resource}`) so retries never mint duplicate Stripe objects.

14. Unit tests (xUnit, `tests/Tamma.Api.Tests/Billing/`) cover: catalog slug→price mapping, idempotent seed (second run = no new Stripe calls / no row churn), customer-create-on-tenant-create with the Stripe SDK mocked, the `PlatformQueuedTask` retry enqueue on Stripe failure, and the single-user `NullBillingProvider` no-op seam. Tenant-isolation test asserts one `BillingCustomer` per tenant.

15. Logging follows the project standard: INFO on customer created / catalog synced (counts), WARN on Stripe failure → retry enqueued, ERROR on production boot with no cabinet key; **Stripe secret key, webhook secret, and customer payment details are NEVER logged**.

## Technical Design

### Namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/
  Entities/
    BillingCustomer.cs              # NEW — control-plane entity (TenantId-unique)
    BillingPlanPrice.cs             # NEW — control-plane catalog row (PlanSlug-keyed)
  ControlPlaneDbContext.cs          # MODIFY — add DbSet<BillingCustomer>, DbSet<BillingPlanPrice>
  TammaModelConfiguration.cs        # MODIFY — ConfigureControlPlaneEntities: tables, indexes, CHECKs, FK
  Migrations/ControlPlane/
    <ts>_AddBillingCustomerAndPlanPrices.cs        # NEW (+ .Designer.cs + snapshot update)
  Seeders/
    BillingSeeder.cs                # NEW — idempotent Stripe Product/Price/Meter sync + row upsert

apps/tamma-elsa/src/Tamma.Core/
  Billing/BillingMode.cs            # NEW — enum { PlatformProvided, Byok } (Core: shared enum)

apps/tamma-elsa/src/Tamma.Api/
  Services/Billing/
    IBillingProvider.cs             # NEW — create-customer / sync-catalog / IsEnabled seam
    StripeBillingProvider.cs        # NEW — Stripe.net implementation (SaaS)
    NullBillingProvider.cs          # NEW — single-user no-op
    IBillingCatalog.cs              # NEW — read-side: resolve BillingPlanPrice by slug (cached)
    BillingCatalog.cs               # NEW — EF-backed catalog reader
    StripeClientFactory.cs          # NEW — builds Stripe.StripeClient from cabinet-resolved key
    BillingOptions.cs               # NEW — bound config (cabinet names, default currency)
    SeedBillingCommand.cs           # NEW — CLI dispatch (mirrors MigrateSecretsCommand)
  Services/Billing/Tasks/
    CreateBillingCustomerTaskHandler.cs  # NEW — IPlatformTaskHandler retry seam
    CreateBillingCustomerTaskPayload.cs  # NEW
  Extensions/
    BillingServiceCollectionExtensions.cs  # NEW — AddTammaBilling(mode-aware DI)
  Endpoints/OrgEndpoints.cs         # MODIFY — invoke IBillingProvider.CreateCustomerAsync in tenant-create txn
  Endpoints/AuthEndpoints.cs        # MODIFY — same hook on registration tenant-create path
  Program.cs                        # MODIFY — AddTammaBilling(); seed-billing CLI dispatch
```

> **Why `AdminTenantsEndpoints.cs` is NOT the create path.** The spec listed `AdminTenantsEndpoints.cs`, but inspection shows it is the *lifecycle* manager (list/detail/retry/delete/change-plan, `OwnerAccess`) — it does not run `new Tenant`. The actual create sites are `OrgEndpoints.CreateOrg` (`OrgEndpoints.cs:57`) and the registration path in `AuthEndpoints` (`AuthEndpoints.cs:275`). The hook lands there. If a future admin-initiated tenant-create lands in `AdminTenantsEndpoints`, the same `IBillingProvider.CreateCustomerAsync` call is added there too.

### Key entity signatures

```csharp
// Tamma.Core/Billing/BillingMode.cs
namespace Tamma.Core.Billing;
public enum BillingMode { PlatformProvided, Byok }

// Tamma.Data/Entities/BillingCustomer.cs
namespace Tamma.Data.Entities;
public class BillingCustomer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }                 // unique FK -> tenants.Id
    public string? StripeCustomerId { get; set; }      // null until Stripe acks (retry path)
    public string BillingMode { get; set; } = "PlatformProvided"; // text domain; CHECK-constrained
    public string DefaultCurrency { get; set; } = "usd";
    public string TaxStatus { get; set; } = "none";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Tenant? Tenant { get; set; }
}

// Tamma.Data/Entities/BillingPlanPrice.cs
namespace Tamma.Data.Entities;
public class BillingPlanPrice
{
    public Guid Id { get; set; }
    public string PlanSlug { get; set; } = null!;      // free | team | enterprise (unique)
    public string? StripeProductId { get; set; }
    public string? StripePriceId { get; set; }         // base (flat seat/platform) price
    public string? TokensInputMeterId { get; set; }
    public string? TokensInputPriceId { get; set; }
    public string? TokensOutputMeterId { get; set; }
    public string? TokensOutputPriceId { get; set; }
    public string? SeatsMeterId { get; set; }
    public string? SeatsPriceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### EF model configuration sketch (`TammaModelConfiguration.ConfigureControlPlaneEntities`)

```csharp
modelBuilder.Entity<BillingCustomer>(entity =>
{
    entity.ToTable("billing_customers", t =>
        t.HasCheckConstraint("ck_billing_customers_mode",
            "\"BillingMode\" IN ('PlatformProvided','Byok')"));
    entity.HasKey(e => e.Id);
    entity.HasIndex(e => e.TenantId).IsUnique();
    entity.HasIndex(e => e.StripeCustomerId).IsUnique()
        .HasFilter("\"StripeCustomerId\" IS NOT NULL");
    entity.HasOne(e => e.Tenant).WithMany()
        .HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<BillingPlanPrice>(entity =>
{
    entity.ToTable("billing_plan_prices");
    entity.HasKey(e => e.Id);
    entity.HasIndex(e => e.PlanSlug).IsUnique();
});
```

### EF migration sketch

`dotnet ef migrations add AddBillingCustomerAndPlanPrices --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` produces an additive migration:

```csharp
migrationBuilder.CreateTable(name: "billing_customers", columns: table => new {
    Id = table.Column<Guid>(nullable: false),
    TenantId = table.Column<Guid>(nullable: false),
    StripeCustomerId = table.Column<string>(nullable: true),
    BillingMode = table.Column<string>(nullable: false, defaultValue: "PlatformProvided"),
    DefaultCurrency = table.Column<string>(nullable: false, defaultValue: "usd"),
    TaxStatus = table.Column<string>(nullable: false, defaultValue: "none"),
    CreatedAt = table.Column<DateTime>(nullable: false),
    UpdatedAt = table.Column<DateTime>(nullable: false),
}, constraints: table => {
    table.PrimaryKey("PK_billing_customers", x => x.Id);
    table.CheckConstraint("ck_billing_customers_mode",
        "\"BillingMode\" IN ('PlatformProvided','Byok')");
    table.ForeignKey("FK_billing_customers_tenants_TenantId",
        x => x.TenantId, "tenants", "Id", onDelete: ReferentialAction.Cascade);
});
migrationBuilder.CreateIndex("IX_billing_customers_TenantId", "billing_customers",
    "TenantId", unique: true);
// + billing_plan_prices table, unique IX on PlanSlug
```

### Billing provider seam

```csharp
// Services/Billing/IBillingProvider.cs
public interface IBillingProvider
{
    bool IsEnabled { get; }                                  // false for NullBillingProvider
    Task<BillingCustomer> CreateCustomerAsync(
        Guid tenantId, CustomerDescriptor descriptor, CancellationToken ct = default);
    Task<CatalogSyncResult> SyncCatalogAsync(CancellationToken ct = default);
}

public sealed record CustomerDescriptor(
    string TenantName, string TenantSlug, string? OwnerEmail, BillingMode Mode);
```

`StripeBillingProvider` resolves the Stripe key via `IRuntimeSecretResolver.GetAsync("billing/stripe-secret-key")`, builds a `Stripe.StripeClient`, and uses `Stripe.CustomerService` / `ProductService` / `PriceService` / `Billing.MeterService`. Every mutating call passes a deterministic `RequestOptions.IdempotencyKey`. `NullBillingProvider` returns `IsEnabled = false` and throws/`no-ops` on the mutating calls.

### Tenant-create hook (same transaction as `TENANT.CREATED.SUCCESS`)

In `OrgEndpoints.CreateOrg`, after `provisioning.ProvisionAsync(tenant.Id)` and before/with `EmitTenantEvent(...,"TENANT.CREATED.SUCCESS",...)`:

```csharp
if (billing.IsEnabled)
{
    try
    {
        var bc = await billing.CreateCustomerAsync(
            tenant.Id,
            new CustomerDescriptor(tenant.Name, tenant.Slug, ownerEmail, BillingMode.PlatformProvided),
            ct);
        await events.AppendAsync(BillingEvents.CustomerCreated(tenant.Id, bc.StripeCustomerId, bc.BillingMode));
    }
    catch (Exception ex) // Stripe unreachable / rate-limited
    {
        // Persist row with null StripeCustomerId + enqueue retry; DO NOT block tenant creation.
        await platformTasks.EnqueueAsync(new PlatformQueuedTask {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName, // "billing.customer.create"
            TenantId = tenant.Id,
            Payload = JsonSerializer.Serialize(new CreateBillingCustomerTaskPayload(tenant.Id)),
        });
        logger.LogWarning(ex, "Stripe customer create failed for tenant {TenantId}; enqueued retry", tenant.Id);
    }
}
```

`CreateBillingCustomerTaskHandler : IPlatformTaskHandler` (`TaskType = "billing.customer.create"`) re-drives `CreateCustomerAsync`, fills `StripeCustomerId`, and emits `BILLING.CUSTOMER.CREATED`. A malformed payload throws `PlatformTaskTerminalException`; a transient Stripe error throws normally so the worker retries per its budget.

### DCB event names

| Event | When | Tags | `TenantId` |
|---|---|---|---|
| `BILLING.CUSTOMER.CREATED` | Stripe customer created + row persisted | `{ tenantId, stripeCustomerId, billingMode }` | set |
| `BILLING.PLAN_CATALOG.SYNCED` | `seed-billing` finishes a slug's catalog sync | `{ planSlug, source: "seed" }` | null (platform) |

Emitted via `IEventRepository.AppendAsync(new DomainEvent { Type=..., Tags=..., Metadata="""{"workflowVersion":"1.0.0","eventSource":"system"}""", Data=... })`, matching the `OrgEndpoints.EmitTenantEvent` shape. A `BillingEvents` static helper builds the rows. (These CP-resident events are what the Story 5.6 `AlertRuleEvaluator` can later observe; no alert rule is added in this story.)

### API / CLI shape

- No new HTTP endpoints for tenants in this story. The customer mapping is a side effect of the existing tenant-create endpoints.
- `seed-billing` CLI: `dotnet run --project apps/tamma-elsa/src/Tamma.Api -- seed-billing` → runs `SeedBillingCommand.RunAsync(app.Services)` before the HTTP pipeline binds, prints a per-slug report (`product/price/meter ids created or reused`), exits with code 0/1. In single-user mode it prints "billing is SaaS-only" and exits 0.

### Per-mode + per-tenant handling

| Concern | single-user | SaaS |
|---|---|---|
| Provider registered | `NullBillingProvider` (`IsEnabled=false`) | `StripeBillingProvider` |
| Tenant-create hook | no-op (no row, no event) | creates customer + row + event in the create txn |
| Stripe key source | n/a | cabinet `billing/stripe-secret-key` (`SecretScope.Platform`, `SecretPurpose.ApiKey`); prod fail-fast if absent |
| Seed command | prints SaaS-only, exits 0 | runs catalog sync (`OwnerAccess` when triggered via any future admin route) |
| Catalog ownership | n/a | platform-global, `OwnerAccess` |
| `BillingCustomer` ownership | n/a | one row per tenant (unique `TenantId`); never cross-tenant |

## Dependencies

**Internal (prerequisite):**
- Epic 28 — control plane, `Tenant`/`Plan` entities, `ControlPlaneDbContext`, `PlatformQueuedTask` + `IPlatformTaskHandler` worker, `OrgEndpoints`/`AuthEndpoints` tenant-create paths, `ITammaModeProvider`.
- Epic 29 — secret cabinet (`ISecretStore`, `ISecretStoreBackend`, `IRuntimeSecretResolver`, `SecretScope.Platform`, `SecretPurpose.ApiKey`/`Webhook`).
- Epic 4 — DCB events (`DomainEvent`, `IEventRepository.AppendAsync`).

**Internal (blocks):**
- Story 35-2 (subscription lifecycle — needs `BillingCustomer` + base price ids).
- Story 35-3 (BYOK-aware metering — needs the three meters + `BillingMode`).
- Invoicing / dunning / portal / credits stories (need the customer mapping + catalog).

**External:**
- `Stripe.net` NuGet package (latest stable; add to `Tamma.Api.csproj`). Research latest API surface (`Billing.MeterService`, `RequestOptions.IdempotencyKey`) before coding — do not assume method shapes.
- A Stripe account with test + live keys; Billing Meters enabled (on by default).

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `BillingCatalogTests` — slug→`BillingPlanPrice` resolution, unknown slug throws, cache hit.
2. `BillingSeederTests` — first run creates products/prices/meters (mocked Stripe service interfaces) and writes ids; second run finds existing ids and makes zero create calls, no row churn; emits one `BILLING.PLAN_CATALOG.SYNCED` per slug.
3. `StripeBillingProviderTests` — `CreateCustomerAsync` calls Stripe `CustomerService.CreateAsync` with the deterministic idempotency key, persists `BillingCustomer`, returns mapped row; duplicate tenant returns existing row (no second Stripe call).
4. `CreateBillingCustomerTaskHandlerTests` — handler re-drives create on a `PlatformQueuedTask`, fills `StripeCustomerId`, emits `BILLING.CUSTOMER.CREATED`; malformed payload → `PlatformTaskTerminalException`; transient Stripe error rethrows (retry).
5. `NullBillingProviderTests` — `IsEnabled=false`; tenant-create hook makes no Stripe calls and writes no row.
6. `BillingSecretBootTests` — production env + cabinet key absent → boot throws; cabinet key present → boots.

**Integration (`apps/tamma-elsa/tests/Tamma.Api.Tests`, docker-bound via `sg docker -c "dotnet test ..."`):**
7. Migration applies + rolls back on a real Postgres CP DB; `has-pending-model-changes` reports none.
8. Tenant-create through `OrgEndpoints.CreateOrg` (Stripe mocked) yields exactly one `BillingCustomer` row and one `BILLING.CUSTOMER.CREATED` event in `DomainEvents`.
9. **Tenant isolation** — creating two tenants yields two distinct `BillingCustomer` rows; re-running create for the same tenant does not insert a second row (unique `TenantId`); a tenant's row is never returned to a different tenant context.

**Mocks:** Stripe SDK is mocked at the `CustomerService`/`ProductService`/`PriceService`/`Billing.MeterService` interface boundary (no live Stripe in CI). `IRuntimeSecretResolver` stubbed to return a fake key. Live-Stripe integration is opt-in behind `STRIPE_SECRET_KEY_TEST` and excluded from default CI.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Billing/BillingMode.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingPlanPrice.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add 2 DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingCustomerAndPlanPrices.cs` | Create (+ Designer + snapshot) |
| `apps/tamma-elsa/src/Tamma.Data/Seeders/BillingSeeder.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingProvider.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/StripeBillingProvider.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/NullBillingProvider.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/StripeClientFactory.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingEvents.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SeedBillingCommand.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Tasks/CreateBillingCustomerTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Tasks/CreateBillingCustomerTaskPayload.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (customer-create hook) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` | Modify (customer-create hook) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (AddTammaBilling + seed-billing CLI dispatch) |
| `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` | Modify (add Stripe.net) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingCatalogTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingSeederTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/StripeBillingProviderTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/CreateBillingCustomerTaskHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/NullBillingProviderTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingTenantCreateIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for billing/Stripe/secret-cabinet spikes, bugs, findings, decisions.
3. Reviewed the Epic 29 secret cabinet contracts (`ISecretStore`, `IRuntimeSecretResolver`, `SecretScope`, `SecretPurpose`) and the `PlatformQueuedTask` worker.
4. **Researched the latest Stripe.net API** (Products, Prices, `Billing.MeterService`, `RequestOptions.IdempotencyKey`) via current docs before writing any SDK call — do not assume method names.
5. Planned the TDD (Red-Green-Refactor) cycle for every new type.

### Key Design Decisions

- **Catalog is a separate table, not `Plan` columns.** `Plan.Quotas`/`Plan.PlacementPolicy` are tenancy-placement concerns (see `Plan.cs` doc-comment); Stripe ids are a billing concern keyed by `PlanSlug`. Overloading `Plan` would couple placement to Stripe.
- **`BillingMode` lives in `Tamma.Core`** so both Data (text column) and Api (provider) reference one enum.
- **Secret resolution reuses the Epic 29 cabinet seam, not a bespoke reader.** `IRuntimeSecretResolver` already implements cabinet-first + dev-only-fallback + prod fail-fast — exactly the AC5 requirement. Do not re-implement.
- **The create path is `OrgEndpoints`/`AuthEndpoints`, not `AdminTenantsEndpoints`** (the latter is lifecycle-only — verified). See the callout in Technical Design.
- **Non-blocking customer create** uses the existing `PlatformQueuedTask` + `IPlatformTaskHandler` worker for retries — no new queue infrastructure.
- **Idempotency everywhere**: deterministic Stripe idempotency keys + lookup-by-stored-id make the seed and the customer-create safe to re-run.

### Boundary Notes (do not implement sibling-story scope)

- No subscriptions, no checkout, no webhook *ingestion* endpoint (35-2 / later — this story only stores the webhook signing secret reference).
- No usage metering, no token markup, no BYOK suppression logic (35-3) — only the `BillingMode` flag + default.
- No invoicing, dunning, tax computation, billing portal, or credits wallet (later stories).
- No tenant-facing billing UI (later stories).

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Stripe outage blocks tenant creation | High | Non-blocking hook → `PlatformQueuedTask` retry; row persists with null `StripeCustomerId`. |
| Duplicate Stripe customers/products on retry | Medium | Deterministic idempotency keys + unique `TenantId`/`PlanSlug` constraints + lookup-by-stored-id. |
| Stripe.net API drift vs assumptions | Medium | Research latest docs before coding; mock at service-interface boundary. |
| Raw env key in production | High | AC5 prod fail-fast via `IRuntimeSecretResolver` (Story 29-10 semantics). |
| Single-user accidental Stripe coupling | Medium | `NullBillingProvider` registered by mode; tests assert zero SDK calls. |

### Success Metrics

- [ ] Every SaaS tenant has exactly one `BillingCustomer` row (unique `TenantId`).
- [ ] `seed-billing` is idempotent: second run = 0 Stripe create calls.
- [ ] Single-user boot makes 0 Stripe calls (asserted in tests).
- [ ] Migration applies + rolls back; `has-pending-model-changes` = none.

## Logging Requirements

- **INFO**: `BillingCustomer` created (`tenantId`, `stripeCustomerId` presence boolean), catalog synced (`planSlug`, counts of created/reused resources), seed command summary.
- **DEBUG**: each Stripe SDK call issued (resource type, idempotency key — never the value), cabinet key resolved (boolean found, never the value).
- **WARN**: Stripe customer-create failure → retry enqueued (`tenantId`, error class), seed reused-existing on re-run.
- **ERROR**: production boot with no cabinet Stripe key, unrecoverable seed failure, `PlatformQueuedTask` dead-lettered.
- **Structured context**: include `{ tenantId, planSlug, billingMode, idempotencyKey }` where applicable.
- **Credential safety**: NEVER log the Stripe secret key, webhook signing secret, or any customer payment details.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
