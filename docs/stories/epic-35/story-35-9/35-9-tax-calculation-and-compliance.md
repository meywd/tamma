# Story 35-9: Tax Calculation & Compliance (Stripe Tax / VAT)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), `.dev/` knowledge-base usage (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the test-first (TDD) mandate, and the build-success / coverage gates.

## User Story

As a **tenant_owner/tenant_admin** of a Tamma SaaS organization (and as the sole user of a single-user deployment that pays a platform fee),
I want to provide my billing address and VAT/GST tax id so Tamma applies the correct tax to my invoices via Stripe Tax — including EU B2B reverse-charge — and surfaces tax line items in my invoice mirror and portal,
So that my invoices are tax-compliant, reproducible for audit, and I am never silently billed the wrong tax because an unverifiable tax id was accepted.

## Priority

P1 - Required for compliant invoicing in tax-collecting jurisdictions (EU VAT, UK VAT, US sales tax). Builds on the customer mapping (35-1), the subscription that automatic-tax attaches to (35-4), and the invoice mirror + finalization path (35-8). Tamma uses Stripe Tax as the tax engine and only stores the minimal point-in-time tax metadata needed for compliance — it does **not** become a tax engine itself.

## Acceptance Criteria

1. The `BillingCustomer` entity (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs`, created in 35-1) gains tax fields: `BillingAddress` (JSONB — line1/line2/city/state/postal_code/country ISO-3166-1 alpha-2), `TaxIdType` (string, Stripe tax-id type e.g. `eu_vat`/`gb_vat`/`au_abn`, nullable), `TaxIdValue` (string, the raw id, nullable), and a `TaxExemptStatus` enum (`None`/`Exempt`/`ReverseCharge`) persisted as text with a CHECK constraint. The existing `TaxStatus` text column from 35-1 is repurposed/superseded by `TaxExemptStatus` (migration maps old values). An EF Core migration is added under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`; `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none after it.

2. A `TaxProfileService` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/TaxProfileService.cs`) exposes `GetProfileAsync(Guid tenantId)` and `UpdateProfileAsync(Guid tenantId, TaxProfileUpdate update, CancellationToken)` which validates the address + tax id, pushes them to the Stripe customer (`Customer.Address`, `Customer.TaxExempt`, and a Stripe `CustomerTaxId` via the tax-ids API), and updates the local `BillingCustomer` row atomically.

3. A `PUT /api/v1/billing/tax-profile` endpoint (`apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/TaxProfileEndpoints.cs`) updates the profile and is restricted to `tenant_owner`/`tenant_admin` (a `member` receives 403, mirroring prompt-store RBAC); a paired `GET /api/v1/billing/tax-profile` returns the current profile to any tenant member. Both run under the tenant-membership filter so the caller's tenant is resolved from context, never a body field.

4. Tax id format is validated **before** save: syntactic validation per `TaxIdType` (e.g. EU VAT country-prefix + checksum shape) and submission to Stripe whose async verification status is recorded; an invalid/unverifiable id is **rejected with a machine-readable `TammaError`** (code `BILLING.TAX_ID.INVALID`, severity `High`, context `{ taxIdType, country }`) and the row is **not** updated to a state that would silently bill incorrect tax. Resolution is never "accept and bill anyway" (no silent-degrade — aligns with the project's no-empty-fallback principle).

5. Stripe Tax `automatic_tax` is enabled on the subscriptions/invoices created by 35-4/35-8: `SubscriptionService` (35-4) and the invoice/finalization path (35-8) pass `AutomaticTax = { Enabled = true }`. This story owns the toggle helper `IAutomaticTaxPolicy` that 35-4/35-8 call; it does not re-implement subscription or invoice creation.

6. Tax line items are projected from the finalized Stripe invoice into `BillingInvoiceLine` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoiceLine.cs`, created in 35-8) with a `LineKind` of `tax` (alongside `base`/`metered-overage`/`credit` from 35-8), carrying `Amount`, `Currency`, `TaxRatePercent`, `TaxJurisdiction`, and `TaxType` (e.g. `vat`/`gst`/`sales_tax`). The portal invoice detail shows the tax lines via the existing `GET /api/v1/billing/invoices/{id}` (35-8) — this story extends the projection in `InvoiceService` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs`), not the endpoint.

7. EU B2B **reverse-charge**: a valid VAT id in a supported reverse-charge country sets the Stripe customer to the appropriate tax-exempt treatment, and the finalized invoice produces the expected **zero / reverse-charge** tax line; the local mirror records `TaxExemptStatus = ReverseCharge` and a `BillingInvoiceLine` of kind `tax` with `Amount = 0` and `TaxType = reverse_charge`. The reverse-charge determination is made by Stripe Tax, not by Tamma's own rules.

8. Tax-relevant fields are captured as a **point-in-time snapshot** on the invoice mirror at finalization: `BillingInvoice` (35-8) gains `TaxTotal`, `TaxCustomerAddressSnapshot` (JSONB), `TaxIdSnapshot` (type+value+verification status), and `TaxExemptSnapshot`, all written from the `invoice.finalized` projection so a later customer address/id change does **not** mutate historical invoices.

9. DCB events are emitted to `DomainEvent` via `IEventRepository.AppendAsync` following `AGGREGATE.ACTION.STATUS`: `BILLING.TAX.PROFILE_UPDATED` (tags `{ tenantId, taxIdType, country, taxExemptStatus, verification }`, `TenantId` set) on a successful profile update, and `tax_total` is added to the existing `BILLING.INVOICE.FINALIZED` event tags (35-8) `{ ..., taxTotal, currency }`. A `BILLING.TAX_ID.VALIDATION_FAILED` event (tags `{ tenantId, taxIdType, country }`) is emitted on a rejected id.

10. **Single-user mode** and **BYOK** both flow through the same tax path: tax applies to the platform/seat fee. In single-user mode there is no Stripe wiring (the `NullBillingProvider` from 35-1 governs), so the tax-profile endpoints/service no-op or are absent exactly as the other billing surfaces are; for a SaaS BYOK tenant (`BillingCustomer.BillingMode = Byok`), tax is still computed on the platform/seat fee invoice (BYOK only suppresses token markup per 35-3, never tax).

11. Per-mode + per-tenant ownership (CLAUDE.md two-scoping rule): in SaaS the tax profile is owned by the **tenant** (`tenant_owner`/`tenant_admin` edit, `member` read-only); the catalog/automatic-tax toggle is platform-global (`OwnerAccess` for any admin read). In single-user mode the sole user owns their profile (no RBAC). Tenant isolation: the tax profile is resolved by `BillingCustomer.TenantId` (unique per 35-1) and is never readable/writable cross-tenant.

12. Idempotency: tax-id and customer updates use deterministic Stripe idempotency keys (`billing-taxprofile-{tenantId}-{hash}`) so retries never create duplicate Stripe tax-id objects; updating a profile that already matches Stripe is a no-op.

13. Unit + integration tests (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`, Stripe mocked at the service-interface boundary; live Stripe with Tax enabled opt-in behind `STRIPE_SECRET_KEY_TEST`): address/tax-id save + validation (valid, invalid-syntax, unverifiable), `automatic_tax` enabled on invoice/subscription, the reverse-charge case (zero tax line + `ReverseCharge` status), and snapshot retention (change address after finalize → historical invoice tax snapshot unchanged). A tenant-isolation test asserts one tenant cannot read/update another tenant's tax profile, and the RBAC test asserts `member` → 403 on `PUT`.

14. Logging follows the project standard: INFO on profile updated (tenantId, taxIdType, country, verification status — never the raw id beyond a redacted suffix), WARN on tax-id validation failure (type, country, reason), ERROR on Stripe Tax API failure during finalization projection; **the raw tax id, billing address PII, and any payment details are NEVER logged in full** (log a redacted form only).

## Technical Design

### Namespace / file structure

```
apps/tamma-elsa/src/Tamma.Core/
  Billing/TaxExemptStatus.cs            # NEW — enum { None, Exempt, ReverseCharge } (shared)

apps/tamma-elsa/src/Tamma.Data/
  Entities/BillingCustomer.cs           # MODIFY (35-1) — add BillingAddress, TaxIdType, TaxIdValue, TaxExemptStatus
  Entities/BillingInvoice.cs            # MODIFY (35-8) — add TaxTotal + tax snapshot columns
  Entities/BillingInvoiceLine.cs        # MODIFY (35-8) — LineKind 'tax' + tax rate/jurisdiction/type fields
  ControlPlaneDbContext.cs              # (no new DbSet — entities already registered by 35-1/35-8)
  TammaModelConfiguration.cs            # MODIFY — CHECK on TaxExemptStatus / LineKind, jsonb columns
  Migrations/ControlPlane/
    <ts>_AddTaxProfileAndInvoiceTaxSnapshot.cs   # NEW (+ .Designer.cs + snapshot update)

apps/tamma-elsa/src/Tamma.Api/
  Services/Billing/
    TaxProfileService.cs                # NEW — get/update profile, validate, push to Stripe
    ITaxProfileService.cs               # NEW — seam
    ITaxIdValidator.cs                  # NEW — syntactic + Stripe verification
    TaxIdValidator.cs                   # NEW
    IAutomaticTaxPolicy.cs              # NEW — toggle helper consumed by 35-4/35-8
    AutomaticTaxPolicy.cs               # NEW
    TaxLineProjector.cs                 # NEW — maps Stripe invoice tax → BillingInvoiceLine + snapshot
    InvoiceService.cs                   # MODIFY (35-8) — call TaxLineProjector at invoice.finalized
    BillingEvents.cs                    # MODIFY (35-1) — add TaxProfileUpdated / TaxIdValidationFailed / taxTotal tag
  Endpoints/Billing/
    TaxProfileEndpoints.cs              # NEW — GET/PUT /api/v1/billing/tax-profile
  Extensions/
    BillingServiceCollectionExtensions.cs  # MODIFY (35-1) — register tax services (mode-aware)
  Program.cs                            # MODIFY — map TaxProfileEndpoints under the tenant-membership group
```

### Key entity changes

```csharp
// Tamma.Core/Billing/TaxExemptStatus.cs
namespace Tamma.Core.Billing;
public enum TaxExemptStatus { None, Exempt, ReverseCharge }

// Tamma.Data/Entities/BillingCustomer.cs  (additions to the 35-1 entity)
public string? BillingAddress { get; set; }          // jsonb: {line1,line2,city,state,postalCode,country}
public string? TaxIdType { get; set; }               // Stripe tax-id type e.g. "eu_vat"
public string? TaxIdValue { get; set; }              // raw id; never logged in full
public string TaxExemptStatus { get; set; } = "None"; // text domain; CHECK ('None','Exempt','ReverseCharge')
public string? TaxIdVerification { get; set; }        // "pending"|"verified"|"unverified" (Stripe async)

// Tamma.Data/Entities/BillingInvoice.cs  (additions to the 35-8 entity)
public long TaxTotal { get; set; }                    // minor units, point-in-time
public string? TaxCustomerAddressSnapshot { get; set; } // jsonb snapshot at finalize
public string? TaxIdSnapshot { get; set; }            // jsonb {type,valueRedacted,verification}
public string TaxExemptSnapshot { get; set; } = "None";

// Tamma.Data/Entities/BillingInvoiceLine.cs  (additions to the 35-8 entity)
// LineKind gains 'tax'; tax lines carry:
public decimal? TaxRatePercent { get; set; }
public string? TaxJurisdiction { get; set; }
public string? TaxType { get; set; }                  // "vat"|"gst"|"sales_tax"|"reverse_charge"
```

### EF model configuration sketch (`TammaModelConfiguration.ConfigureControlPlaneEntities`)

```csharp
modelBuilder.Entity<BillingCustomer>(entity =>
{
    entity.Property(e => e.BillingAddress).HasColumnType("jsonb");
    entity.ToTable("billing_customers", t =>
        t.HasCheckConstraint("ck_billing_customers_tax_exempt",
            "\"TaxExemptStatus\" IN ('None','Exempt','ReverseCharge')"));
});

modelBuilder.Entity<BillingInvoice>(entity =>
{
    entity.Property(e => e.TaxCustomerAddressSnapshot).HasColumnType("jsonb");
    entity.Property(e => e.TaxIdSnapshot).HasColumnType("jsonb");
    entity.ToTable("billing_invoices", t =>
        t.HasCheckConstraint("ck_billing_invoices_tax_exempt",
            "\"TaxExemptSnapshot\" IN ('None','Exempt','ReverseCharge')"));
});

// BillingInvoiceLine: extend the existing LineKind CHECK to include 'tax'.
```

### EF migration sketch

`dotnet ef migrations add AddTaxProfileAndInvoiceTaxSnapshot --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` — **additive** (new columns on existing 35-1/35-8 tables), with a data step mapping the old `BillingCustomer.TaxStatus` text into `TaxExemptStatus`:

```csharp
migrationBuilder.AddColumn<string>("BillingAddress", "billing_customers", type: "jsonb", nullable: true);
migrationBuilder.AddColumn<string>("TaxIdType", "billing_customers", nullable: true);
migrationBuilder.AddColumn<string>("TaxIdValue", "billing_customers", nullable: true);
migrationBuilder.AddColumn<string>("TaxExemptStatus", "billing_customers", nullable: false, defaultValue: "None");
migrationBuilder.AddColumn<string>("TaxIdVerification", "billing_customers", nullable: true);
migrationBuilder.Sql(@"UPDATE billing_customers
    SET ""TaxExemptStatus"" = CASE ""TaxStatus""
        WHEN 'reverse_charge' THEN 'ReverseCharge'
        WHEN 'exempt' THEN 'Exempt' ELSE 'None' END;");
// billing_invoices: TaxTotal, TaxCustomerAddressSnapshot (jsonb), TaxIdSnapshot (jsonb), TaxExemptSnapshot
// billing_invoice_lines: TaxRatePercent, TaxJurisdiction, TaxType; replace LineKind CHECK to add 'tax'
```

> CHECK *edits* on existing tables follow the project's migration discipline (mirror entity config only in `TammaModelConfiguration.cs`, the single source); verify `has-pending-model-changes` reports none after generating.

### Tax-profile service seam

```csharp
// Services/Billing/ITaxProfileService.cs
public interface ITaxProfileService
{
    Task<TaxProfile> GetProfileAsync(Guid tenantId, CancellationToken ct = default);
    Task<TaxProfile> UpdateProfileAsync(Guid tenantId, TaxProfileUpdate update, CancellationToken ct = default);
}

public sealed record TaxProfileUpdate(
    BillingAddressDto Address, string? TaxIdType, string? TaxIdValue, bool ClaimExempt);

public sealed record TaxProfile(
    BillingAddressDto Address, string? TaxIdType, string? TaxIdValueRedacted,
    TaxExemptStatus ExemptStatus, string? Verification);
```

`UpdateProfileAsync` flow: (1) `ITaxIdValidator.ValidateAsync(type, value, country)` — syntactic check, then create a Stripe `CustomerTaxId` and read its verification status; reject with `BILLING.TAX_ID.INVALID` + emit `BILLING.TAX_ID.VALIDATION_FAILED` on failure. (2) push `Customer.Address` + `Customer.TaxExempt` to Stripe with a deterministic idempotency key. (3) update `BillingCustomer` (address, type, redacted-store policy, `TaxExemptStatus`, `TaxIdVerification`) in one CP transaction. (4) emit `BILLING.TAX.PROFILE_UPDATED`. Stripe determines reverse-charge eligibility — Tamma maps Stripe's tax-exempt result onto `TaxExemptStatus`.

### Automatic-tax toggle (consumed by 35-4 / 35-8)

```csharp
// Services/Billing/IAutomaticTaxPolicy.cs
public interface IAutomaticTaxPolicy
{
    // Enabled whenever billing is enabled (SaaS) and the customer has a usable address.
    bool ShouldEnable(BillingCustomer customer);
}
```

35-4's `SubscriptionService` and 35-8's `InvoiceService` set `AutomaticTax = new() { Enabled = policy.ShouldEnable(customer) }` on the Stripe subscription/invoice. This story owns the policy; the call sites belong to those stories (boundary respected — this story only provides the helper and the projection).

### Tax-line projection (invoice.finalized)

`TaxLineProjector.Project(Stripe.Invoice invoice, BillingInvoice mirror)` runs inside 35-8's `InvoiceService` finalization projection: it reads `invoice.TotalTaxAmounts` / `invoice.AutomaticTax`, writes `BillingInvoice.TaxTotal` + the address/id/exempt **snapshots**, and appends one `BillingInvoiceLine { LineKind = "tax", ... }` per jurisdiction (reverse-charge → a single `Amount = 0`, `TaxType = "reverse_charge"` line). The snapshot makes historical invoices reproducible regardless of later profile edits.

### DCB event names

| Event | When | Tags | `TenantId` |
|---|---|---|---|
| `BILLING.TAX.PROFILE_UPDATED` | profile saved + pushed to Stripe | `{ tenantId, taxIdType, country, taxExemptStatus, verification }` | set |
| `BILLING.TAX_ID.VALIDATION_FAILED` | tax id rejected | `{ tenantId, taxIdType, country, reason }` | set |
| `BILLING.INVOICE.FINALIZED` (35-8) | finalize projection (extended) | `{ ..., taxTotal, currency }` | set |

Emitted via `IEventRepository.AppendAsync(new DomainEvent { ... })` in the `OrgEndpoints.EmitTenantEvent` shape (`Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`); the `BillingEvents` static helper (35-1) gains the new builders. These CP-resident events are observable by the Story 5.6 `AlertRuleEvaluator`.

### API shape

```
GET  /api/v1/billing/tax-profile      → 200 TaxProfile          (any tenant member; tax id redacted)
PUT  /api/v1/billing/tax-profile      body: TaxProfileUpdate     (tenant_owner/tenant_admin; member 403)
                                       → 200 TaxProfile | 422 {code:"BILLING.TAX_ID.INVALID", ...}
```

Both endpoints sit in the tenant-membership group (`RequireTenantMembershipFilter` resolves the tenant + role from context — `TenantRoleItemKey`); the caller's tenant is never read from a body field. RBAC mirrors the prompt-store precedent: read = any member, write = owner/admin, member write = 403.

### Per-mode + per-tenant handling

| Concern | single-user | SaaS |
|---|---|---|
| Provider | `NullBillingProvider` (35-1) — tax endpoints/service no-op or absent | `StripeBillingProvider` + Stripe Tax |
| Profile owner | the sole user (no RBAC) | the tenant; `tenant_owner`/`tenant_admin` edit, `member` read-only |
| Automatic-tax | n/a | on for platform/seat-fee invoices; BYOK tenants taxed on platform fee |
| Reverse-charge | n/a | Stripe Tax decides; mirror records `ReverseCharge` |
| Snapshot | n/a | written at finalize; immutable per invoice |
| Tenant isolation | single tenant | profile keyed by unique `BillingCustomer.TenantId`; never cross-tenant |

## Dependencies

**Internal (prerequisite):**
- **Story 35-1** — `BillingCustomer` entity + `IBillingProvider`/`NullBillingProvider` seam, `StripeClientFactory`, cabinet-resolved Stripe key, `BillingEvents` helper, mode-aware DI.
- **Story 35-4** — `SubscriptionService` (sets `automatic_tax` on the subscription) and `BillingSubscription` mirror.
- **Story 35-8** — `BillingInvoice` + `BillingInvoiceLine` mirrors, `InvoiceService` finalization projection, `BILLING.INVOICE.FINALIZED` event, the portal invoice endpoints.
- Epic 28 — control plane, `Tenant`, `ControlPlaneDbContext`, `RequireTenantMembershipFilter`, `ITammaModeProvider`.
- Epic 29 — secret cabinet (Stripe key resolution, via 35-1).
- Epic 4 — DCB events (`DomainEvent`, `IEventRepository.AppendAsync`).

**Internal (blocks):**
- Story 35-10/35-11 (billing portal / dashboard surfaces) — display the tax profile editor + tax lines this story produces.

**External:**
- `Stripe.net` (added by 35-1) with **Stripe Tax** enabled on the account; tax-ids API + `Customer.TaxExempt` + invoice `AutomaticTax` / `TotalTaxAmounts`. Research the latest Stripe.net Tax surface before coding — do not assume method shapes.
- A Stripe test account with Tax enabled and at least one origin tax registration configured (so test-mode tax is computed).

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `TaxIdValidatorTests` — valid EU VAT shape accepted; malformed prefix/checksum rejected (`BILLING.TAX_ID.INVALID`); Stripe "unverified" status → rejected, no silent accept.
2. `TaxProfileServiceTests` — update pushes `Customer.Address`/`TaxExempt`/tax-id to Stripe (mocked) with the deterministic idempotency key; valid VAT in a reverse-charge country → `TaxExemptStatus = ReverseCharge`; row updated atomically; `BILLING.TAX.PROFILE_UPDATED` emitted once; rejected id leaves the row unchanged and emits `BILLING.TAX_ID.VALIDATION_FAILED`; idempotent re-save of an identical profile = no Stripe write.
3. `AutomaticTaxPolicyTests` — enabled when billing on + address present; disabled with no address; disabled for `NullBillingProvider`.
4. `TaxLineProjectorTests` — Stripe invoice with one tax jurisdiction → one `tax` line with rate/jurisdiction/type; reverse-charge invoice → single `Amount = 0`, `TaxType = "reverse_charge"` line; snapshot fields written from the finalize payload; `BillingInvoice.TaxTotal` set; `taxTotal` added to the `BILLING.INVOICE.FINALIZED` tags.

**Integration (`apps/tamma-elsa/tests/Tamma.Api.Tests`, docker-bound via `sg docker -c "dotnet test ..."`):**
5. Migration applies + rolls back on a real Postgres CP DB; `has-pending-model-changes` reports none; the `TaxStatus → TaxExemptStatus` data step maps existing rows.
6. `PUT /api/v1/billing/tax-profile` end-to-end (Stripe mocked): owner/admin succeeds; **`member` → 403**; invalid id → 422 with the machine-readable code; `GET` returns the redacted profile.
7. **Tenant isolation** — tenant A cannot `GET`/`PUT` tenant B's tax profile (resolved by `BillingCustomer.TenantId`); cross-tenant attempt → 403/404.
8. **Snapshot retention** — finalize an invoice (mocked Stripe tax), then change the customer's address via `PUT`; re-read the historical `BillingInvoice` and assert its `TaxCustomerAddressSnapshot`/`TaxIdSnapshot`/`TaxTotal` are unchanged.

**Live (opt-in, behind `STRIPE_SECRET_KEY_TEST` with Tax enabled, excluded from default CI):**
9. Real Stripe test-mode: create an invoice with `automatic_tax` on for a taxable address → non-zero tax line; valid EU B2B VAT → reverse-charge zero line; verify the mirror matches.

**Mocks:** Stripe SDK mocked at the `CustomerService` / `CustomerTaxIdService` / `InvoiceService` interface boundary; `IRuntimeSecretResolver` stubbed. Live Stripe Tax is opt-in only.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Billing/TaxExemptStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs` | Modify (tax fields) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoice.cs` | Modify (tax total + snapshots) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoiceLine.cs` | Modify (tax line kind + fields) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (jsonb cols + CHECKs) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddTaxProfileAndInvoiceTaxSnapshot.cs` | Create (+ Designer + snapshot) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/ITaxProfileService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/TaxProfileService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/ITaxIdValidator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/TaxIdValidator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IAutomaticTaxPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/AutomaticTaxPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/TaxLineProjector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs` | Modify (call projector at finalize) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingEvents.cs` | Modify (tax events + taxTotal tag) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/TaxProfileEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` | Modify (register tax services) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map tax-profile endpoints) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/TaxIdValidatorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/TaxProfileServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/AutomaticTaxPolicyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/TaxLineProjectorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/TaxProfileEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/TaxSnapshotIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for billing/Stripe/tax/VAT spikes, bugs, findings, decisions.
3. Reviewed the 35-1 billing seam (`IBillingProvider`, `BillingCustomer`, `BillingEvents`, `StripeClientFactory`) and the 35-8 invoice mirror (`BillingInvoice`/`BillingInvoiceLine`, `InvoiceService` finalize path) — those are the entities/services this story extends.
4. **Researched the latest Stripe.net Tax surface** (`Customer.TaxExempt`, `CustomerTaxIdService`, invoice `AutomaticTax` / `TotalTaxAmounts`, tax-id verification statuses) via current docs before writing any SDK call.
5. Planned the TDD (Red-Green-Refactor) cycle for every new type.

### Key Design Decisions

- **Stripe Tax is the tax engine; Tamma stores minimal metadata.** We never compute rates ourselves — we collect the address/id, enable `automatic_tax`, and project Stripe's result. The validator only does *syntactic* pre-checks plus reading Stripe's verification status.
- **No silent mis-billing.** An unverifiable VAT id is a hard `TammaError` (`BILLING.TAX_ID.INVALID`), never "accept and bill standard tax" — consistent with the project's no-empty-fallback principle (resolution is correct-or-error).
- **Point-in-time snapshot at finalize.** Tax address/id/exempt and `TaxTotal` are copied onto `BillingInvoice` when Stripe finalizes, so a later profile edit cannot rewrite history (audit reproducibility).
- **Reuse the 35-1 seam and 35-8 projection.** Tax fields ride on the existing `BillingCustomer`/`BillingInvoice(Line)` entities; this story adds columns + a projector + a profile service, not new tables or a parallel billing path.
- **Automatic-tax toggle is a helper, not a re-implementation.** `IAutomaticTaxPolicy` is owned here but called by 35-4/35-8 — respects the subscription/invoice ownership boundaries.
- **BYOK still pays tax.** BYOK suppresses token markup (35-3) but the platform/seat fee invoice is taxable; the same path applies.

### Boundary Notes (do not implement sibling-story scope)

- Do **not** re-implement subscription creation (35-4) or invoice creation/finalization/dunning (35-8) — only add the `automatic_tax` toggle helper and the tax-line projection those stories call.
- Do **not** add webhook ingestion (35-5) — the finalize projection is invoked from the existing 35-8 path which 35-5 already drives.
- Do **not** build the portal/dashboard tax UI (35-10/35-11) — this story exposes the API + projection only.
- Do **not** add token-markup / BYOK suppression logic (35-3) — only assert tax applies to the platform/seat fee.

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Silently billing wrong tax on an unverifiable id | High | Hard reject (`BILLING.TAX_ID.INVALID`) + `VALIDATION_FAILED` event; row untouched; tested. |
| Historical invoice tax mutates after a profile edit | High | Point-in-time snapshot written at `invoice.finalized`; snapshot-retention integration test. |
| Stripe Tax not enabled / no origin registration in test account | Medium | Live tax tests opt-in behind `STRIPE_SECRET_KEY_TEST`; default CI mocks at the service boundary. |
| Stripe.net Tax API drift vs assumptions | Medium | Research current docs before coding; mock at the service-interface boundary. |
| PII / raw tax id leaking into logs | High | Redacted-only logging; tax id stored but never fully logged; credential-safety assertion in review. |
| Cross-tenant tax-profile access | High | Resolve by unique `BillingCustomer.TenantId` via the membership filter; tenant-isolation test. |

### Success Metrics

- [ ] Invoices in tax-collecting jurisdictions carry a Stripe-computed tax line in `BillingInvoiceLine` (kind `tax`).
- [ ] Valid EU B2B VAT id produces a reverse-charge zero line and `TaxExemptStatus = ReverseCharge`.
- [ ] No unverifiable tax id is ever accepted (0 silent mis-bills in tests).
- [ ] Historical invoice tax snapshot is immutable across profile edits.
- [ ] Migration applies + rolls back; `has-pending-model-changes` = none.

## Logging Requirements

- **INFO**: tax profile updated (`tenantId`, `taxIdType`, `country`, `verification` — tax id only as redacted suffix), tax lines projected at finalize (`invoiceId`, `taxTotal`, jurisdiction count).
- **DEBUG**: each Stripe SDK call issued (resource type, idempotency key — never the value), automatic-tax toggle decision (`enabled`, reason).
- **WARN**: tax-id validation failure (`taxIdType`, `country`, `reason`), Stripe verification returned `unverified`.
- **ERROR**: Stripe Tax API failure during the finalize projection (`invoiceId`, error class), production Stripe key absent (delegated to 35-1 boot path).
- **Structured context**: include `{ tenantId, invoiceId, taxIdType, country, taxExemptStatus }` where applicable.
- **Credential / PII safety**: NEVER log the raw tax id, full billing address, or any payment details; log redacted forms only.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
