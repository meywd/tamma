# Story 35-9 — Tax Calculation & Compliance (Stripe Tax / VAT)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for every
> task — write the failing test first, then the implementation. This story is part of Epic 35
> (Billing & Payments, C# control plane). Steps use checkbox (`- [ ]`) syntax for tracking.
> SaaS-only at runtime: every Stripe Tax touch-point must no-op under the single-user
> `NullBillingProvider` from 35-1.

**Goal:** Apply correct tax to Tamma SaaS invoices using **Stripe Tax** — collect/validate the
tenant's billing address + VAT/GST tax id, set the Stripe customer's tax-exempt status, enable
`automatic_tax` on the subscriptions/invoices that 35-4/35-8 already create, project Stripe's tax
line items into the local invoice mirror, handle **EU B2B reverse-charge**, and store a
**point-in-time tax snapshot** on each finalized invoice for compliance/audit. Tamma uses Stripe as
the tax engine and stores only the minimal metadata — it never computes rates itself, and it never
silently bills incorrect tax on an unverifiable id.

**Non-goals (YAGNI guard):**
- NO building of subscription creation (35-4) or invoice creation/finalization/dunning (35-8). This
  story only adds the `automatic_tax` toggle helper those stories call and the tax-line projection
  invoked from 35-8's finalize path.
- NO webhook ingestion (35-5). The finalize projection runs inside the existing 35-8 path that 35-5
  drives.
- NO portal / dashboard tax UI (35-10/35-11). This story ships the API + projection only.
- NO token-markup or BYOK suppression (35-3). We only assert tax applies to the platform/seat fee.
- NO bespoke tax engine. Rates, jurisdictions, and reverse-charge eligibility are decided by Stripe
  Tax; Tamma validates id syntax + reads Stripe's verification result, then projects.
- NO silent-degrade. An unverifiable tax id is a hard `TammaError`, never "accept and bill standard
  tax" (consistent with the project's no-empty-fallback principle).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`, builds need no wrapper). Dep `Stripe.net` is added by 35-1; this
story uses its Tax surface (`Customer.TaxExempt`, `CustomerTaxIdService`, invoice `AutomaticTax` /
`TotalTaxAmounts`).

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Sibling-story foundations this story extends (all NEW, authored ahead of this in Epic 35)
- **35-1** (`docs/stories/epic-35/story-35-1/35-1-...md`, authored): introduces
  `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs` (unique `TenantId`, `BillingMode`
  text, a `TaxStatus` text column we repurpose), the `IBillingProvider` /
  `StripeBillingProvider` / `NullBillingProvider` seam, `StripeClientFactory` (cabinet-resolved
  key), `BillingEvents` static DCB helper, `Tamma.Core/Billing/BillingMode.cs`, and the mode-aware
  `BillingServiceCollectionExtensions.AddTammaBilling`. **These are prerequisites — do not
  re-create them.**
- **35-4** (`/tmp` spec): `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SubscriptionService.cs`
  creates Stripe subscriptions — the call site that sets `AutomaticTax` on the subscription.
- **35-8** (`/tmp` spec): `apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoice.cs` +
  `BillingInvoiceLine.cs` (line split base/metered-overage/credit, which we extend with `tax`),
  `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs` (the `invoice.finalized`
  projection we hook), and the `BILLING.INVOICE.FINALIZED` DCB event we extend with `taxTotal`.

> At implementation time 35-1/35-4/35-8 must be merged. The billing `Entities` / `Services/Billing`
> directories do **not** exist on `main` yet (verified: `Tamma.Api/Services/Billing/` absent, no
> `Stripe` ref in `Tamma.Api.csproj`) — they are created by the prerequisite stories.

### Control-plane data layer (verified present on main)
- `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` — DbSets declared
  `public DbSet<X> Xs => Set<X>();`. `BillingCustomer`/`BillingInvoice`/`BillingInvoiceLine` are
  registered by 35-1/35-8; this story adds **columns only**, no new DbSet.
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` — `ConfigureControlPlaneEntities` is
  the single place entity mapping lives (`ToTable`, `HasCheckConstraint`, `HasColumnType("jsonb")`,
  `HasIndex`). Mirror 35-1's `billing_customers` block; add jsonb columns + CHECKs there.
- `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — additive migration; verify
  `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none
  after generating. CHECK *edits* (extending `LineKind` to allow `tax`, the new `TaxExemptStatus`
  CHECK) follow the project's migration discipline — mirror config only in
  `TammaModelConfiguration.cs`.
- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — `Type`, `TenantId`, `Tags`,
  `Metadata`, `Data`, `CreatedAt`, server-side `SequenceNumber`. DCB shape set by
  `OrgEndpoints.EmitTenantEvent` (`OrgEndpoints.cs:1036-1061`):
  `Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`; `Tags`/`Data` JSON. `BillingEvents`
  (35-1) reuses this shape — add the tax builders.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync(DomainEvent)`
  (line 7) is the append seam.

### Endpoint + RBAC seams (verified present on main)
- `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` — resolves the
  path/context tenant + role into `http.Items[TenantRoleItemKey]` (line 30) /
  `PathTenantIdItemKey` (line 31). The tax-profile endpoints run under this group so the tenant is
  resolved from context, never a body field. RBAC precedent: read = any member, write =
  owner/admin, member-write = 403 (prompt-store / `OrgEndpoints` role-gate pattern,
  `TenantRoleHierarchy.IsAtLeast(role, Admin)`).
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `EmitTenantEvent` helper
  (line 1036), `TenantRoleHierarchy` role checks (e.g. lines 106, 254). Mirror for the tax
  endpoints.
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — auth policies `OwnerAccess` (971), `MemberAccess`
  (991), `PromptManage` (1012); path-tenant routes attach the membership filter (~1505/1596). Map
  `TaxProfileEndpoints` into that `/api/v1/billing/*` (tenant-membership) group.

### Mode + secret seams (verified present on main)
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider.Mode`
  (`SingleUser`/`SaaS`). Drives whether tax services are wired at all (single-user →
  `NullBillingProvider` from 35-1 governs; tax endpoints no-op/absent).
- Stripe key resolution is owned by 35-1 (`StripeClientFactory` + `IRuntimeSecretResolver`); this
  story reuses the resolved client, no new secret reader.

### Not present yet (NEW in this story)
- No tax-profile entity columns, service, validator, automatic-tax policy, tax-line projector, or
  tax-profile endpoints. No `TaxExemptStatus` enum. All tax code is greenfield on top of the 35-1/8
  entities.

---

## Architecture

**Collect + validate profile → push to Stripe → enable automatic_tax → project Stripe's tax → snapshot at finalize**, reusing the 35-1 seam and the 35-8 invoice projection end-to-end:

1. **`ITaxProfileService`** is the single profile write seam: validate (syntactic + Stripe
   verification), push `Customer.Address`/`TaxExempt`/tax-id to Stripe, update `BillingCustomer`
   atomically, emit `BILLING.TAX.PROFILE_UPDATED`. An unverifiable id → hard `TammaError`
   (`BILLING.TAX_ID.INVALID`) + `BILLING.TAX_ID.VALIDATION_FAILED`, row untouched.
2. **`IAutomaticTaxPolicy`** is a tiny helper that 35-4's `SubscriptionService` and 35-8's
   `InvoiceService` call to set `AutomaticTax = { Enabled = ... }`. Owned here, called there
   (boundary respected).
3. **`TaxLineProjector`** runs inside 35-8's finalize projection: reads Stripe's
   `TotalTaxAmounts`/`AutomaticTax`, writes `BillingInvoice.TaxTotal` + the address/id/exempt
   **snapshot**, and appends one `BillingInvoiceLine { LineKind="tax", ... }` per jurisdiction
   (reverse-charge → a single `Amount=0` line). Snapshot = audit reproducibility.
4. **DCB events** `BILLING.TAX.PROFILE_UPDATED` / `BILLING.TAX_ID.VALIDATION_FAILED` and an extended
   `BILLING.INVOICE.FINALIZED` (`taxTotal` tag) append to the CP `DomainEvents` store via
   `IEventRepository` (the store the alert evaluator polls).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Tax provider | `NullBillingProvider` (35-1) — tax endpoints/service no-op or absent | `StripeBillingProvider` + Stripe Tax |
| Who owns the tax profile? | the sole user (no RBAC) | the tenant; `tenant_owner`/`tenant_admin` edit, `member` read-only |
| Automatic-tax | n/a | on for platform/seat-fee invoices; BYOK tenants taxed on the platform fee |
| Reverse-charge decision | n/a | Stripe Tax decides; mirror records `ReverseCharge` |
| Snapshot ownership | n/a | per-invoice, immutable, written at finalize |
| Mode source | `ITammaModeProvider` (process-stable) | same |
| Tenant isolation | single tenant | profile keyed by unique `BillingCustomer.TenantId`; never cross-tenant |

---

## Phased task breakdown

### Phase 1 — Data layer: tax columns + migration (foundation)

- [ ] **T1.1 — `TaxExemptStatus` enum.** New `apps/tamma-elsa/src/Tamma.Core/Billing/TaxExemptStatus.cs`
  (`{ None, Exempt, ReverseCharge }`). *Test first:* covered transitively by T1.3 column tests.
- [ ] **T1.2 — Extend billing entities.** Add tax fields to `BillingCustomer.cs` (BillingAddress
  jsonb, TaxIdType, TaxIdValue, TaxExemptStatus text default `None`, TaxIdVerification), to
  `BillingInvoice.cs` (TaxTotal, TaxCustomerAddressSnapshot jsonb, TaxIdSnapshot jsonb,
  TaxExemptSnapshot text), and to `BillingInvoiceLine.cs` (TaxRatePercent, TaxJurisdiction,
  TaxType; `LineKind` gains `tax`). *Tests first:* `BillingTaxEntityModelTests` — defaults
  (`TaxExemptStatus="None"`, `TaxExemptSnapshot="None"`).
- [ ] **T1.3 — Model config.** In `TammaModelConfiguration.ConfigureControlPlaneEntities`: jsonb
  column types for the address/snapshot columns; CHECK `ck_billing_customers_tax_exempt` and
  `ck_billing_invoices_tax_exempt` on the enum domain; extend the `BillingInvoiceLine.LineKind`
  CHECK (from 35-8) to include `tax`. *Tests first:* EnsureCreated/snapshot test asserting the
  CHECKs + jsonb columns exist.
- [ ] **T1.4 — EF migration.** `dotnet ef migrations add AddTaxProfileAndInvoiceTaxSnapshot
  --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` (build, no wrapper). Add the
  data step mapping `BillingCustomer.TaxStatus` → `TaxExemptStatus`. Verify
  `has-pending-model-changes` → none. *Tests first (integration, docker):* `TaxMigrationTests`
  applies `Update` then down/`Remove`-equivalent on a real Postgres CP DB and asserts the data step
  maps existing rows.

### Phase 2 — Tax-id validation

- [ ] **T2.1 — `ITaxIdValidator` + `TaxIdValidator`.** New `Services/Billing/ITaxIdValidator.cs` /
  `TaxIdValidator.cs`: syntactic check per `TaxIdType` (EU VAT country prefix + length/shape; GB/AU
  variants), then create a Stripe `CustomerTaxId` (mocked in tests) and read its verification
  status. Returns a `TaxIdValidationResult { Ok, Verification, Reason }`; unverifiable → not Ok.
  **Research the latest Stripe.net tax-id verification statuses before coding.** *Tests first:*
  `TaxIdValidatorTests` — valid EU VAT shape accepted; malformed prefix/checksum rejected; Stripe
  `unverified` → rejected (no silent accept); each branch maps to `BILLING.TAX_ID.INVALID` context.

### Phase 3 — Tax-profile service + events

- [ ] **T3.1 — `BillingEvents` tax builders.** Modify `Services/Billing/BillingEvents.cs` (35-1):
  add `TaxProfileUpdated(tenantId, type, country, exempt, verification)`,
  `TaxIdValidationFailed(tenantId, type, country, reason)`, and a `taxTotal`/`currency` tag on the
  `InvoiceFinalized` builder (35-8). *Tests first:* assert Type, Tags JSON, `TenantId` set.
- [ ] **T3.2 — `ITaxProfileService` + `TaxProfileService`.** New seam + impl: `GetProfileAsync`
  (returns redacted id), `UpdateProfileAsync` — validate via T2.1, push `Customer.Address` +
  `Customer.TaxExempt` + tax-id to Stripe with deterministic idempotency key
  `billing-taxprofile-{tenantId}-{hash}`, map Stripe's exempt result onto `TaxExemptStatus`
  (reverse-charge in a supported country → `ReverseCharge`), update `BillingCustomer` atomically,
  emit `BILLING.TAX.PROFILE_UPDATED`. Rejected id → `TammaError("BILLING.TAX_ID.INVALID")` +
  `BILLING.TAX_ID.VALIDATION_FAILED`, row unchanged. Identical re-save → no Stripe write. *Tests
  first:* `TaxProfileServiceTests` — happy path, reverse-charge mapping, rejected-id leaves row +
  emits failure, idempotent re-save, atomic update, single event.

### Phase 4 — Automatic-tax policy + tax-line projection

- [ ] **T4.1 — `IAutomaticTaxPolicy` + `AutomaticTaxPolicy`.** New helper: `ShouldEnable(customer)`
  = billing enabled (SaaS) AND a usable address present. *Tests first:* `AutomaticTaxPolicyTests` —
  on with address, off without, off under `NullBillingProvider`. (Call sites in 35-4/35-8 are out of
  scope — only the helper ships here; a thin integration test asserts those services *would* read
  it, but the wiring lands in those stories.)
- [ ] **T4.2 — `TaxLineProjector`.** New `Services/Billing/TaxLineProjector.cs`:
  `Project(Stripe.Invoice, BillingInvoice mirror)` reads `TotalTaxAmounts`/`AutomaticTax`, writes
  `TaxTotal` + the address/id/exempt **snapshot** onto the mirror, and appends `BillingInvoiceLine`
  rows of kind `tax` (one per jurisdiction; reverse-charge → single `Amount=0`,
  `TaxType="reverse_charge"`). *Tests first:* `TaxLineProjectorTests` — one-jurisdiction →
  one line with rate/jurisdiction/type; reverse-charge → zero line; snapshot written; `TaxTotal`
  set; `taxTotal` added to the `BILLING.INVOICE.FINALIZED` tags.
- [ ] **T4.3 — Hook the projector into 35-8's `InvoiceService` finalize path.** Modify
  `Services/Billing/InvoiceService.cs` to call `TaxLineProjector.Project(...)` inside the existing
  `invoice.finalized` projection (the single touch into 35-8 code, kept minimal). *Tests first
  (integration):* finalize an invoice (Stripe tax mocked) → mirror has the tax line + snapshot +
  `TaxTotal`, event carries `taxTotal`.

### Phase 5 — Endpoints + DI wiring

- [ ] **T5.1 — `TaxProfileEndpoints`.** New `Endpoints/Billing/TaxProfileEndpoints.cs`:
  `GET /api/v1/billing/tax-profile` (any tenant member; returns redacted profile) and
  `PUT /api/v1/billing/tax-profile` (owner/admin; member → 403; invalid id → 422 with the
  machine-readable code). Resolve tenant + role from `RequireTenantMembershipFilter` context. *Tests
  first:* `TaxProfileEndpointsTests` — owner/admin PUT succeeds, member 403, invalid id 422, GET
  redacted, cross-tenant 403/404.
- [ ] **T5.2 — DI + route mapping.** Modify `Extensions/BillingServiceCollectionExtensions.cs`
  (35-1) to register `ITaxProfileService`, `ITaxIdValidator`, `IAutomaticTaxPolicy`,
  `TaxLineProjector` (only when `Mode==SaaS`; single-user keeps the no-op seam). Map
  `TaxProfileEndpoints` in `Program.cs` under the `/api/v1/billing/*` tenant-membership group.
  *Tests first:* `AddTammaTaxTests` — SaaS resolves the real services; single-user resolves nothing
  / no-op.

### Phase 6 — Verification

- [ ] **T6.1** Run `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` — full suite
  green (no regressions).
- [ ] **T6.2** `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` →
  none.
- [ ] **T6.3** Snapshot-retention integration test (`TaxSnapshotIntegrationTests`): finalize →
  change address → historical invoice tax snapshot unchanged.
- [ ] **T6.4** (opt-in) Live Stripe test mode with Tax enabled behind `STRIPE_SECRET_KEY_TEST`:
  taxable address → non-zero tax line; valid EU B2B VAT → reverse-charge zero line.

---

## Sequencing & dependencies

```
T1.1 → T1.2 → T1.3 → T1.4            (data layer — must land first)
              ↓
T2.1                                 (validator; needs entities for store shape)
              ↓
T3.1 → T3.2                          (events + profile service; needs validator + Stripe client)
              ↓
T4.1 → T4.2 → T4.3                   (policy + projector + 35-8 hook; needs entities + events)
              ↓
T5.1 → T5.2                          (endpoints + DI; needs profile service)
              ↓
T6.x                                 (verification)
```

- **Hard prerequisite:** Stories **35-1, 35-4, 35-8 merged** (entities, billing seam, invoice
  mirror + finalize path, `BillingEvents`). Phase 1 (data) before everything else here.
- **External:** `Stripe.net` (from 35-1) with **Stripe Tax** enabled on the account; research the
  current Tax surface (`Customer.TaxExempt`, `CustomerTaxIdService`, invoice `AutomaticTax` /
  `TotalTaxAmounts`, verification statuses) before T2.1/T3.2/T4.2.
- **Story prerequisites present at main:** Epic 28 (CP, tenants, membership filter, mode), Epic 29
  (cabinet via 35-1), Epic 4 (DCB events).

## Risks + mitigations

- **Silently billing wrong tax on an unverifiable id.** *Mitigation:* T2.1/T3.2 hard-reject with
  `BILLING.TAX_ID.INVALID` + `VALIDATION_FAILED` event; the row is never updated to a bill-anyway
  state. Tested across valid/invalid-syntax/unverified branches.
- **Historical invoice tax mutates after a profile edit.** *Mitigation:* T4.2 writes a point-in-time
  snapshot (address/id/exempt/total) at `invoice.finalized`; T6.3 snapshot-retention test pins
  immutability.
- **Stripe Tax not enabled / no origin registration in the test account.** *Mitigation:* live tax
  tests are opt-in behind `STRIPE_SECRET_KEY_TEST`; default CI mocks at the
  `CustomerService`/`CustomerTaxIdService`/`InvoiceService` interface boundary.
- **Stripe.net Tax API drift vs assumed signatures.** *Mitigation:* research current docs before
  coding; all tests mock at the service-interface boundary so SDK shape is isolated to validator +
  profile service + projector.
- **Boundary creep into 35-4/35-8.** *Mitigation:* this story ships only the `IAutomaticTaxPolicy`
  helper + `TaxLineProjector` + one minimal call from `InvoiceService`; subscription/invoice
  creation stays owned by 35-4/35-8. The story's Boundary Notes pin this.
- **PII / raw tax id leaking into logs.** *Mitigation:* redacted-only logging; tax id stored but
  never fully logged; credential-safety asserted in review and in the logging requirements.
- **Cross-tenant tax-profile access.** *Mitigation:* resolve by unique `BillingCustomer.TenantId`
  via `RequireTenantMembershipFilter`; tenant-isolation + RBAC (member 403) tests.
- **Migration discipline.** *Mitigation:* additive columns + CHECK edits mirrored only in
  `TammaModelConfiguration.cs`; T1.4 verifies `has-pending-model-changes` reports none and the
  `TaxStatus → TaxExemptStatus` data step runs.

## Acceptance criteria (mirror the story)

- [ ] `BillingCustomer` gains `{BillingAddress, TaxIdType, TaxIdValue, TaxExemptStatus,
  TaxIdVerification}`; `BillingInvoice` gains `{TaxTotal, TaxCustomerAddressSnapshot, TaxIdSnapshot,
  TaxExemptSnapshot}`; `BillingInvoiceLine` gains tax fields + `tax` kind; additive migration applies
  + rolls back; `has-pending-model-changes` = none; `TaxStatus → TaxExemptStatus` mapped.
- [ ] `PUT /api/v1/billing/tax-profile` (owner/admin; member 403) validates + pushes address/id/
  exempt to Stripe and updates the local row; `GET` returns the redacted profile to any member.
- [ ] Tax id is validated before save; an invalid/unverifiable id is rejected with the
  machine-readable `BILLING.TAX_ID.INVALID` and never silently bills wrong tax.
- [ ] `automatic_tax` is enabled on 35-4/35-8 subscriptions/invoices via `IAutomaticTaxPolicy`; tax
  lines are projected into `BillingInvoiceLine` (kind `tax`) and shown via the 35-8 portal endpoint.
- [ ] Valid EU B2B VAT in a supported country → reverse-charge zero/`reverse_charge` tax line and
  `TaxExemptStatus = ReverseCharge` in the mirror.
- [ ] Tax fields snapshot onto `BillingInvoice` at finalize so historical invoices are reproducible
  after a later profile change.
- [ ] `BILLING.TAX.PROFILE_UPDATED` emitted on update; `taxTotal` added to
  `BILLING.INVOICE.FINALIZED` tags; `BILLING.TAX_ID.VALIDATION_FAILED` on rejection.
- [ ] Single-user mode and BYOK both flow through the same tax path (tax applies to the platform/seat
  fee; single-user no-ops via `NullBillingProvider`).
- [ ] Unit + integration tests (Stripe mocked; live opt-in behind `STRIPE_SECRET_KEY_TEST`): save +
  validation, `automatic_tax` on invoice, reverse-charge, snapshot retention, tenant-isolation, RBAC.
- [ ] Full xUnit suite green; raw tax id / billing PII / payment details never logged in full.
