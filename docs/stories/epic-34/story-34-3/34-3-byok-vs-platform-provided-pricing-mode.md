# Story 34-3: BYOK vs Platform-Provided Pricing Mode (per-provider mode selection)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down →
TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules, TRACE/DEBUG
logging requirements, the mandatory Test-Driven-Development workflow, and the build/coverage gates.

**Failure to follow this process will result in rework.**

> **Boundary note (canonical ownership):** This story owns **ONLY the pricing-MODE selection per
> `(tenant, provider)`** — the `TenantProviderBilling` entity and the BYOK/platform toggle endpoints.
> It does **NOT** own provider-key resolution or the LLM call path. **Story 32-3** is the canonical
> owner of BYOK provider-key resolution from the Epic 29 secret cabinet into the LLM call path (the
> `IProviderCredentialResolver` seam, exposing the resolved key AND a `ProviderCredential.Source`
> discriminator of `byok | platform`), and **Story 32-4** owns SaaS provider-auth eligibility
> (`IProviderAuthRegistry.IsSaaSEligible`). This story **CONSUMES** both seams and never re-wires the
> cabinet, the resolver, or `CallLlmActivity`.

## User Story

As a **tenant owner running Tamma in SaaS mode**,
I want to choose, per provider, whether Tamma calls the LLM with **my own API key (BYOK)** — billed a
flat platform/seat fee with no token markup — or with **the platform's key (platform-provided)** — billed
at cost-plus-margin,
so that I control my LLM spend and the pricing engine charges me the right way for each provider.

(And, in single-user mode, the sole user keeps full control of the same per-provider mode for their
self-hosted instance.)

## Priority

P0 - The BYOK/platform mode is the input the cost→price markup engine (Story 34-5) keys off. The mode
selected here also tells Story 32-3's `IProviderCredentialResolver` which credential source to use for
the LLM call (32-3 owns the actual cabinet read + `CallLlmActivity` wiring); this story records the
per-`(tenant, provider)` choice that drives both.

## Acceptance Criteria

1. A new control-plane entity **`TenantProviderBilling`** (`apps/tamma-elsa/src/Tamma.Data/Entities/TenantProviderBilling.cs`)
   is added to `ControlPlaneDbContext` with columns: `Id` (UUIDv7), `TenantId` (Guid, FK → `Tenant`),
   `ProviderKey` (string, e.g. `"anthropic"`), `Mode` (string enum `byok|platform`), `SecretName`
   (nullable cabinet name, e.g. `"provider/anthropic/api-key"` — the name 32-3's resolver reads),
   `Status` (`active|disabled`),
   `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` audit columns. A partial unique index enforces **one
   `active` row per `(TenantId, ProviderKey)`**. An additive EF migration is added under
   `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` and `has-pending-model-changes` reports
   none after it is created.
2. A new **`MetricBillingMode` enum** lives in `apps/tamma-elsa/src/Tamma.Core/Enums/MetricBillingMode.cs`
   (`Platform`, `Byok`) and is the single shared token used by this story, the pricing engine (34-5),
   and metering — so the mode never drifts between layers (mirrors the `EntitlementMetricKey`
   single-source rule in Story 34-1).
3. This story **CONSUMES Story 32-3's credential seam** — it does NOT define its own key resolver and
   does NOT read the Epic 29 cabinet directly. The mode persisted here (`TenantProviderBilling.Mode`) is
   the per-`(tenant, provider)` selection that 32-3's `IProviderCredentialResolver` honours when it
   resolves the effective key; the resulting `ProviderCredential.Source` (`byok | platform`) is the
   single discriminator both layers share. No `IProviderKeyResolver`/`ProviderKeyResolver`/
   `ProviderKeyResolution` type is introduced by this story.
4. This story does **NOT** modify `CallLlmActivity` or any LLM call path — that wiring (the direct
   `_configuration["…:ApiKey"]` removal and the cabinet read) is owned entirely by Story 32-3. The mode
   row created here is what 32-3's resolver reads to decide `byok` vs `platform`; the no-empty /
   no-silent-platform-fallback guarantee on key resolution is 32-3's acceptance criterion, not this
   story's.
5. The **platform-provided mode is the default**: single-user mode and any `(tenant, provider)` with no
   `active` BYOK row mean 32-3 resolves `Source=platform` (the global config key), so existing
   platform-key deployments are unaffected. This story only persists the absence/presence of an `active`
   BYOK row; the actual key resolution stays in 32-3.
6. A **BYOK enable flow** (`POST /api/v1/orgs/{tenantId}/pricing/providers/{providerKey}/byok`) stores
   the supplied provider key into the secret cabinet via `ISecretStore.CreateAsync` with typed
   `SecretPurpose.ApiKey`, `SecretScope.Tenant`, then inserts/updates the `TenantProviderBilling` row to
   `Mode=byok`, `Status=active`, `SecretName=<cabinet name>`. The cabinet `SecretName` convention
   (`provider/{providerKey}/api-key`, scope `Tenant`) MUST match the lookup name 32-3's resolver expects,
   so the key written here is the key 32-3 later reads. A **disable flow** (`DELETE …/byok`) flips the
   row to `Mode=platform`, tombstones the secret ref (`SecretName=null`, retire the cabinet secret via
   `ISecretStore.RetireVersionAsync`), and invalidates 32-3's cached credential for `(tenant, provider)`,
   keeping the row for audit.
7. **SaaS provider-auth guard**: attempting to register a CLI/token agent provider as a BYOK provider
   returns **HTTP 422** with body `{ "error": "CLI providers are single-user only" }`. Eligibility is
   decided by calling **Story 32-4's `IProviderAuthRegistry.IsSaaSEligible(providerKey)`** — this story
   does NOT reinvent the allowlist or the rejection logic. A provider for which `IsSaaSEligible` returns
   `false` (CLI/token providers from `packages/providers`, e.g. `claude-code`/`opencode`, and unknown
   providers — fail-closed) is rejected with the 422.
8. The pricing mode is surfaced on the **per-call cost record**: `ProviderDiagnostic`
   (`apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`) gains a `BillingMode` string column
   (`byok|platform`, default `platform`) populated from 32-3's `ProviderCredential.Source` on the
   diagnostic ingest path so the markup engine (34-5) knows whether to apply a margin; the same value is
   tagged on the emitted DCB event. (32-3 already propagates `CredentialSource` into the diagnostic; this
   story persists it as the `BillingMode` column the markup engine keys off.)
9. **DCB events** are emitted via `IEventRepository.AppendAsync`: `PRICING.BYOK.ENABLED` and
   `PRICING.BYOK.DISABLED` (on the enable/disable flows) — each tagged with `tenantId`, `provider`,
   `mode`. Tag/Data JSON follows the existing `DomainEvent` shape (`Type`, `TenantId`, `Tags`, `Data`).
   (Key-resolution events are owned by 32-3, not this story.)
10. **RBAC (per-mode)**: in SaaS, BYOK mode management (enable/disable, GET reveal-status) is reachable by
    `tenant_owner` OR `tenant_admin` (via the `PricingManage` gate — see Dev Notes on policy choice); a
    `member`-role caller hits **403**. In single-user mode the sole user has full control (no RBAC). Reads
    of the current mode are available to any tenant member.
11. **Per-tenant isolation**: the endpoints only ever read/write the `TenantProviderBilling` row and
    cabinet secret for the caller's own tenant; a cross-tenant `tenantId` in the route resolves to 404
    (mirrors prompt-store/secret-cabinet behaviour), and the cabinet `SecretRef` is built
    `ForTenant(tenantId, …)` so the Epic 29 store's authorisation filter applies.
12. **Mode-change idempotency**: re-enabling BYOK for a `(tenant, provider)` that already has an `active`
    BYOK row updates the existing row (and rotates the cabinet secret) rather than creating a duplicate —
    the partial unique index (one `active` row per `(TenantId, ProviderKey)`) is enforced.
13. **Unit + integration tests** prove: enable writes the cabinet row + DB row + `PRICING.BYOK.ENABLED`
    event and never echoes the key; disable retires the secret, flips to platform, invalidates 32-3's
    cache, and emits `PRICING.BYOK.DISABLED`; the `member`-role caller is 403 on a BYOK mutation; a
    provider for which `IProviderAuthRegistry.IsSaaSEligible` is `false` is 422; `ProviderDiagnostic.BillingMode`
    is persisted from `ProviderCredential.Source`; a tenant cannot read/mutate another tenant's BYOK
    config; the one-active-row invariant holds on re-enable.

## Tasks / Subtasks

- [ ] Task 1: Catalog + enum foundation (AC: 1, 2)
  - [ ] Subtask 1.1: Add `MetricBillingMode` enum to `Tamma.Core/Enums/`.
  - [ ] Subtask 1.2: Add `TenantProviderBilling` entity + DbSet + model config (partial unique index,
        CHECK on `mode`/`status`) in `ControlPlaneDbContext` / `TammaModelConfiguration`.
  - [ ] Subtask 1.3: `dotnet ef migrations add AddTenantProviderBilling` under `Migrations/ControlPlane/`;
        verify `has-pending-model-changes` reports none.

- [ ] Task 2: BYOK enable/disable/get-mode endpoints + CLI guard (AC: 6, 7, 9, 10, 11, 12)
  - [ ] Subtask 2.1: New `TenantProviderBillingService.cs` — enable/disable/get-mode over
        `TenantProviderBilling`; one-active-row upsert.
  - [ ] Subtask 2.2: New `PricingEndpoints.cs` — enable (POST), disable (DELETE), get-mode (GET).
  - [ ] Subtask 2.3: Store/retire the cabinet secret via `ISecretStore` (purpose `ApiKey`, scope
        `Tenant`, name `provider/{providerKey}/api-key` to match 32-3's lookup); on disable, call
        32-3's `IProviderCredentialResolver.Invalidate(tenantId, providerKey)`; emit `PRICING.BYOK.*` events.
  - [ ] Subtask 2.4: CLI-provider guard → call `IProviderAuthRegistry.IsSaaSEligible` (Story 32-4);
        non-eligible → 422 `"CLI providers are single-user only"`.
  - [ ] Subtask 2.5: RBAC wiring (`PricingManage` gate; member → 403; cross-tenant → 404).

- [ ] Task 3: Cost-record tagging (AC: 8)
  - [ ] Subtask 3.1: Add `BillingMode` column to `ProviderDiagnostic` + migration.
  - [ ] Subtask 3.2: Populate it from 32-3's `ProviderCredential.Source` on the diagnostic ingest path
        and tag the emitted DCB cost event with `mode`.

- [ ] Task 4: Tests (AC: 13) — written FIRST per TDD.

## Technical Design

### Namespaces / file structure

```
apps/tamma-elsa/src/
  Tamma.Core/Enums/
    MetricBillingMode.cs                         # NEW — enum { Platform, Byok }
  Tamma.Data/Entities/
    TenantProviderBilling.cs                     # NEW — control-plane row
    ProviderDiagnostic.cs                        # MODIFY — add BillingMode column
  Tamma.Data/
    ControlPlaneDbContext.cs                     # MODIFY — DbSet<TenantProviderBilling>
    TammaModelConfiguration.cs                   # MODIFY — indexes + CHECK constraints
    Migrations/ControlPlane/
      <ts>_AddTenantProviderBilling.cs           # NEW — additive migration
    Migrations/ControlPlane/
      <ts>_AddProviderDiagnosticBillingMode.cs   # NEW — additive column migration
  Tamma.Api/Services/Pricing/
    TenantProviderBillingService.cs              # NEW — enable/disable/read mode
    PricingEventTypes.cs                         # NEW — PRICING.BYOK.* constants
  Tamma.Api/Endpoints/
    PricingEndpoints.cs                          # NEW — tenant mode-toggle endpoints
  Tamma.Api/Endpoints/
    ProviderEndpoints.cs                         # MODIFY — set BillingMode on ingest
```

> **NOT NEW but CONSUMED (owned by other stories — do NOT modify):**
> - **Story 32-3** — `Tamma.Api/Services/Providers/IProviderCredentialResolver.cs` (the
>   `ProviderCredential { ApiKey, Source, … }` seam + `CredentialSource` enum). This story calls
>   `Invalidate(tenantId, providerKey)` on disable and relies on its `ResolveAsync` for all key reads;
>   it does NOT define a key resolver and does NOT touch `CallLlmActivity`/`CallLlmInlineActivity`.
> - **Story 32-4** — `Tamma.Activities/Security/IProviderAuthRegistry.cs` (`IsSaaSEligible`) — the
>   single source for the CLI-provider 422 gate.
> - **Epic 29 cabinet** — `Tamma.Api/Services/Secrets/ISecretStore.cs`, `SecretRef.cs`, `SecretScope.cs`,
>   `SecretPurpose.cs`, `SecretRequests.cs`. This story uses `ISecretStore.CreateAsync`/`RetireVersionAsync`
>   only to write/retire the BYOK secret on the enable/disable flows (so 32-3 can later read it); it
>   never performs the runtime read.

### Entity sketch — `TenantProviderBilling`

```csharp
namespace Tamma.Data.Entities;

public class TenantProviderBilling
{
    public Guid Id { get; set; }                  // UUIDv7
    public Guid TenantId { get; set; }            // FK -> Tenant
    public string ProviderKey { get; set; } = null!;   // "anthropic", "openai", "openrouter"
    public string Mode { get; set; } = "platform";     // MetricBillingMode token
    public string? SecretName { get; set; }       // cabinet name when Mode=byok; null otherwise
    public string Status { get; set; } = "active";     // "active" | "disabled"
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public Tenant? Tenant { get; set; }
}
```

EF model configuration (in `TammaModelConfiguration`, single source per Epic 28 convention):

```csharp
b.Entity<TenantProviderBilling>(e =>
{
    e.ToTable("tenant_provider_billing", t =>
    {
        t.HasCheckConstraint("ck_tpb_mode", "mode IN ('platform','byok')");
        t.HasCheckConstraint("ck_tpb_status", "status IN ('active','disabled')");
        // BYOK rows MUST carry a secret name; platform rows MUST NOT.
        t.HasCheckConstraint("ck_tpb_secret_xor",
            "(mode = 'byok' AND secret_name IS NOT NULL) OR (mode = 'platform' AND secret_name IS NULL)");
    });
    // One ACTIVE row per (tenant, provider) — partial index, plain NULL semantics safe.
    e.HasIndex(x => new { x.TenantId, x.ProviderKey })
     .HasFilter("status = 'active'")
     .IsUnique();
    e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
});
```

### Consuming Story 32-3 (credential source) — NOT re-implemented here

Provider-key resolution is **owned by Story 32-3**. Its seam (do NOT duplicate):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/  — OWNED BY 32-3, consumed here
namespace Tamma.Api.Services.Providers;

public enum CredentialSource { Byok, Platform }

public sealed record ProviderCredential(
    string ApiKey, CredentialSource Source, string? SecretRefStorageKey, int? VersionNumber);

public interface IProviderCredentialResolver
{
    Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default);
    void Invalidate(Guid? tenantId, string providerName);   // called by THIS story on enable/disable
}
```

How this story consumes it:

- **Mode is the input, not the resolver.** The `active` `TenantProviderBilling` row for
  `(tenant, provider)` is what 32-3's resolver reads to decide `Source=Byok` vs `Source=Platform`. This
  story persists the row; 32-3 reads the key. There is **no** `IProviderKeyResolver`,
  `ProviderKeyResolver`, or `ProviderKeyResolution` in this story.
- **No empty / no platform fallback.** The fail-loud guarantee (a BYOK row whose cabinet secret is
  missing throws rather than degrading to the platform key) is **32-3's** acceptance criterion — this
  story neither implements nor re-tests it here.
- **Cache invalidation.** On a BYOK enable/disable mutation, this story calls
  `IProviderCredentialResolver.Invalidate(tenantId, providerKey)` so 32-3's cache reflects the new mode
  on the next LLM call.
- **No LLM-call-path changes.** `CallLlmActivity` / `CallLlmInlineActivity` are untouched by this story.

### API shape

```
# Tenant-facing (SaaS: tenant_owner/tenant_admin via PricingManage; single-user: the user)
GET    /api/v1/orgs/{tenantId}/pricing/providers                 # list modes per provider (member: read)
GET    /api/v1/orgs/{tenantId}/pricing/providers/{providerKey}   # current mode + whether a key is set
POST   /api/v1/orgs/{tenantId}/pricing/providers/{providerKey}/byok   # body { apiKey }; -> 200 mode=byok
DELETE /api/v1/orgs/{tenantId}/pricing/providers/{providerKey}/byok   # -> 200 mode=platform
```

POST `…/byok` body: `{ "apiKey": "sk-ant-…" }`. Response: `{ "provider": "anthropic", "mode": "byok",
"keySet": true }`. The raw key is NEVER echoed back (reveal-once cabinet rule, Epic 29); GET returns
`keySet: true/false`, never the value.

`SecretName` convention: `provider/{providerKey}/api-key`, scope `Tenant`, purpose `ApiKey` — this MUST
match the name 32-3's `IProviderCredentialResolver` reads (`SecretRef.ForTenant(tenantId,
"provider/<name>/api-key")`) so the secret written on enable is the one 32-3 later resolves.

### Per-mode + per-tenant handling

| Concern | single-user | SaaS |
|---|---|---|
| Principal | the sole user (`tenantId` null) | the tenant (`tenant_id`) |
| Default mode | platform (no BYOK row) | platform until a BYOK row is created |
| Who manages BYOK | the user (no RBAC) | `tenant_owner` / `tenant_admin` via `PricingManage`; `member` → 403 |
| Cabinet scope | no per-tenant cabinet row needed | `SecretRef.ForTenant(tenantId, …)` |
| Cross-tenant | n/a | route `tenantId` ≠ caller's tenant → 404 |
| Key resolution at call time | owned by 32-3 | owned by 32-3 |

### DCB event names (AGGREGATE.ACTION.STATUS)

| Event | When | Tags |
|---|---|---|
| `PRICING.BYOK.ENABLED` | BYOK enable flow succeeds | `tenantId`, `provider`, `mode=byok` |
| `PRICING.BYOK.DISABLED` | BYOK disable flow succeeds | `tenantId`, `provider`, `mode=platform` |

> Key-resolution events (`PRICING.PROVIDER_KEY.RESOLVED` / `…FAILED`) are **NOT** owned by this story —
> Story 32-3 emits the credential-resolution audit (`credentialSource` tag) on the LLM call path.

### Integration points

- **Story 32-3 (canonical credential owner)** — `IProviderCredentialResolver` resolves the key + emits
  `ProviderCredential.Source` (`byok|platform`). This story persists the per-`(tenant, provider)` mode
  the resolver reads and calls `Invalidate(tenantId, providerKey)` on mutation. This story performs NO
  runtime key reads and makes NO LLM-call-path changes.
- **Story 32-4 (SaaS auth gating)** — `IProviderAuthRegistry.IsSaaSEligible(providerKey)` is the single
  source for the CLI-provider 422 gate on BYOK registration.
- **Epic 29 secret cabinet** — `ISecretStore` for create/retire of the per-tenant BYOK secret on the
  enable/disable flows only (`SecretPurpose.ApiKey`, `SecretScope.Tenant`, `SecretRef.ForTenant`). The
  cabinet **read** at call time is 32-3's, not this story's — do NOT re-wire the cabinet read path.
- **Story 34-1** — `MetricBillingMode` lives beside `EntitlementMetricKey`; `PlanPrice.PricingMode`
  (`platform_provided|byok`) from 34-1 is the plan-level default this per-(tenant,provider) override
  layers on top of.
- **Story 34-5 (markup engine)** — reads `ProviderDiagnostic.BillingMode` to decide markup vs flat.
- **Epic 32** — agents define the provider chain (`AgentConfig`) BYOK applies to; this story does not
  change the chain, only the per-`(tenant, provider)` mode for whichever provider the chain selects.

### EF migration sketch

```csharp
migrationBuilder.CreateTable(
    name: "tenant_provider_billing",
    columns: t => new {
        Id = t.Column<Guid>(nullable: false),
        TenantId = t.Column<Guid>(nullable: false),
        ProviderKey = t.Column<string>(nullable: false),
        Mode = t.Column<string>(nullable: false, defaultValue: "platform"),
        SecretName = t.Column<string>(nullable: true),
        Status = t.Column<string>(nullable: false, defaultValue: "active"),
        CreatedAt = t.Column<DateTime>(nullable: false),
        UpdatedAt = t.Column<DateTime>(nullable: false),
        CreatedBy = t.Column<Guid>(nullable: true),
        UpdatedBy = t.Column<Guid>(nullable: true),
    },
    constraints: t => {
        t.PrimaryKey("pk_tenant_provider_billing", x => x.Id);
        t.ForeignKey("fk_tpb_tenant", x => x.TenantId, "tenants", "id", onDelete: Cascade);
        t.CheckConstraint("ck_tpb_mode", "mode IN ('platform','byok')");
        t.CheckConstraint("ck_tpb_status", "status IN ('active','disabled')");
        t.CheckConstraint("ck_tpb_secret_xor",
            "(mode='byok' AND secret_name IS NOT NULL) OR (mode='platform' AND secret_name IS NULL)");
    });
migrationBuilder.CreateIndex(
    "ux_tpb_active_provider", "tenant_provider_billing",
    new[] { "TenantId", "ProviderKey" }, unique: true, filter: "status = 'active'");

// Second additive migration: ProviderDiagnostic.BillingMode
migrationBuilder.AddColumn<string>(
    "BillingMode", "provider_diagnostics", nullable: false, defaultValue: "platform");
```

## Dependencies

**Internal:**

- **Prerequisite** Story **32-3** (Per-Tenant Provider Credential Resolution) — the **canonical owner**
  of provider-key resolution. Supplies `IProviderCredentialResolver` (the key) and
  `ProviderCredential.Source` (`byok|platform`, the mode discriminator) that this story's
  `TenantProviderBilling` row drives and that the markup engine reads. This story CONSUMES it for
  everything key-related and never re-wires the cabinet or `CallLlmActivity`.
- **Prerequisite** Story **32-4** (SaaS Provider Auth Gating) — supplies `IProviderAuthRegistry.IsSaaSEligible`,
  the single source for the CLI-provider 422 rejection on BYOK registration.
- **Prerequisite** Story 34-1 (Plan & Price-Book Catalog) — `MetricBillingMode` / `EntitlementMetricKey`
  shared enums and `PlanPrice.PricingMode` plan-level default.
- **Prerequisite** Epic 29 (secret cabinet) — `ISecretStore`, `SecretRef`, `SecretPurpose`, `SecretScope`
  for writing/retiring the BYOK secret on enable/disable (NOT for runtime reads — that is 32-3's).
- **Blocks** Story 34-5 (cost→price markup engine reads `ProviderDiagnostic.BillingMode`).
- **Related** Epic 28 (control-plane / `ControlPlaneDbContext`, `TammaModelConfiguration`), Epic 4
  (DCB events / `IEventRepository`).

**External:**

- No new third-party packages; uses the existing EF Core 9 / Npgsql stack.

## Testing Strategy

1. **Unit — `TenantProviderBillingService`** (`tests/Tamma.Api.Tests/Pricing/TenantProviderBillingServiceTests.cs`):
   enable writes cabinet secret (`SecretPurpose.ApiKey`, name `provider/{p}/api-key`) + flips row to byok
   + emits `PRICING.BYOK.ENABLED` + calls `IProviderCredentialResolver.Invalidate`; disable retires the
   secret + flips to platform + emits `PRICING.BYOK.DISABLED` + invalidates; one-active-row invariant
   (second enable updates, not duplicates).
2. **Integration — endpoints** (`tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs`, docker-bound
   via `sg docker -c "dotnet test …"`): POST byok stores a real cabinet row + DB row + DCB event; GET
   returns `keySet:true` and never the value; DELETE reverts; the migration applies + rolls back.
3. **RBAC / isolation tests**: SaaS `member` → 403 on POST/DELETE byok; `tenant_admin`/`tenant_owner` →
   200; cross-tenant `tenantId` in route → 404; tenant A cannot read tenant B's mode; single-user mode
   the sole user passes all gates.
4. **CLI-provider guard test**: registering a provider for which `IProviderAuthRegistry.IsSaaSEligible`
   returns `false` (e.g. `claude-code`) as BYOK → 422 `"CLI providers are single-user only"`. The
   registry itself is unit-tested under Story 32-4; this story asserts the endpoint calls it and maps
   the result to the 422.
5. **`ProviderDiagnostic` test**: ingest path persists `BillingMode` from 32-3's `ProviderCredential.Source`;
   DCB cost event carries the `mode` tag.
6. **Mocks**: `ISecretStore` faked in-memory (no real KMS); `IProviderCredentialResolver` and
   `IProviderAuthRegistry` stubbed; no Stripe in this story (billing/charging is Epic 35).
   Tenant-isolation asserted via the cabinet `SecretRef.ForTenant` scope and the partial unique index.

> Key-resolution correctness (BYOK→cabinet vs platform→global, the no-empty/no-platform-fallback throw,
> and the `CallLlmActivity` wiring) is tested under **Story 32-3**, not here.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Enums/MetricBillingMode.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/TenantProviderBilling.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Modify (add `BillingMode`) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (indexes + CHECKs) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddTenantProviderBilling.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddProviderDiagnosticBillingMode.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/TenantProviderBillingService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs` | Modify (set `BillingMode` on ingest from 32-3's `ProviderCredential.Source`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI + map endpoints + `PricingManage` policy) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/TenantProviderBillingServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs` | Create |

> **Consumed (NOT modified by this story):** `Tamma.Api/Services/Providers/IProviderCredentialResolver.cs`
> (Story 32-3), `Tamma.Activities/Security/IProviderAuthRegistry.cs` (Story 32-4),
> `Tamma.Activities/LlmCall/CallLlmActivity.cs` (Story 32-3 wires it — this story does not).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (esp. Epic 29 secret-cabinet notes and
   the no-empty-fallback feedback).
3. Reviewed `CLAUDE.md` "Operating Modes", "Prompt Store Architecture / RBAC", and "Multi-tenant
   provisioning" — the per-mode ownership rules are mandatory here.
4. Read **Story 32-3** (`IProviderCredentialResolver` / `ProviderCredential.Source`) and **Story 32-4**
   (`IProviderAuthRegistry.IsSaaSEligible`) — this story consumes both seams and must not duplicate them.
5. Planned the TDD Red-Green-Refactor cycle (tests first).

### Key design decisions

- **Key resolution is NOT this story's seam.** Story 32-3 owns `IProviderCredentialResolver` and the
  `CallLlmActivity` wiring. This story owns only the per-`(tenant, provider)` mode selection
  (`TenantProviderBilling`) that 32-3 reads, plus writing/retiring the BYOK secret on the toggle flows
  so 32-3's resolver has something to read. On every mode mutation, call
  `IProviderCredentialResolver.Invalidate(tenantId, providerKey)`.
- **No empty / no platform fallback for BYOK** is a guarantee owned and tested by 32-3, not duplicated
  here. This mirrors `feedback_resolution_no_empty_fallback` (tenant → system → error, never empty).
- **CLI-provider gating is 32-4's `IsSaaSEligible`.** The 422 on BYOK registration of a CLI/token
  provider calls `IProviderAuthRegistry.IsSaaSEligible(providerKey)` — this story does NOT reinvent the
  allowlist, the auth-model classification, or the rejection logic; it only maps a `false` result to the
  422 `"CLI providers are single-user only"` response.
- **RBAC policy choice.** The spec names `SettingsManage` for BYOK management, but the codebase's
  `SettingsManage` policy is **owner-only** (`settings:manage`) and 403s every `tenant_admin`
  (see `Program.cs` ~1001 and the Story 27-3 comment ~1006). Per CLAUDE.md the prompt/convention
  precedent is tenant_owner **OR** tenant_admin via the dedicated `PromptManage`/`ConventionManage`
  gates. To honour the spec's intent (tenant admins manage BYOK) without re-defining `SettingsManage`,
  add a `PricingManage` policy (permission `pricing:manage`, granted to owner+admin exactly like
  `PromptManage`); document `SettingsManage` as the spec label and `PricingManage` as the concrete gate.
- **`ProviderKey` overload.** Note the term `ProviderKey` here means the provider identifier
  (`"anthropic"`) on `ProviderDiagnostic`/`TenantProviderBilling` — distinct from the Cranl tenancy
  "ProviderKey backend label". Keep the column name `ProviderKey` to match `ProviderDiagnostic`.

### Security requirements

- The BYOK key is written once into the cabinet and never echoed back (reveal-once, Epic 29). GET
  endpoints return `keySet` only.
- NEVER log the supplied API key on the enable flow; log `provider`, `mode`, `tenantId` — not the value.
  The POST body is never logged.
- Cabinet `SecretRef.ForTenant(tenantId, …)` ensures the Epic 29 authorisation filter blocks
  cross-tenant reads even if the route guard is bypassed (defence in depth).

### Risks and mitigations

| Risk | Severity | Mitigation |
| --- | --- | --- |
| Duplicating 32-3's key resolver / re-wiring `CallLlmActivity` (boundary violation) | High | This story persists the mode row + writes the cabinet secret only; key reads + LLM-call wiring stay in 32-3; reviewer checklist item |
| Stale 32-3 credential cache after a mode toggle | Medium | Call `IProviderCredentialResolver.Invalidate(tenantId, providerKey)` on every enable/disable |
| `SecretName` mismatch between this story's write and 32-3's read | Medium | Pin the shared name convention `provider/{providerKey}/api-key`, scope `Tenant`, purpose `ApiKey`; integration test asserts 32-3 can resolve the written secret |
| Migration drift on the collapsed control-plane baseline | Medium | Additive table/column only; verify `has-pending-model-changes` reports none; mirror entity config solely in `TammaModelConfiguration` |
| `SettingsManage` owner-only would 403 tenant admins | Medium | Use dedicated `PricingManage` (owner+admin) gate, documented above |
| Logging the supplied key | High | Explicit no-secret logging rule + reviewer checklist |

### Success metrics

- [ ] A BYOK enable writes a cabinet secret that 32-3's `IProviderCredentialResolver` resolves to
      `Source=Byok` on the next call.
- [ ] Every mode mutation invalidates 32-3's credential cache for `(tenant, provider)`.
- [ ] `ProviderDiagnostic.BillingMode` populated from `ProviderCredential.Source` on 100% of
      post-migration cost records.

## Logging Requirements

- **INFO**: BYOK enabled/disabled (`tenantId`, `provider`, new mode) — never the key.
- **DEBUG**: cabinet write/retire attempted (`secretName`, no value); 32-3 cache invalidation issued.
- **WARN**: a BYOK registration rejected by `IsSaaSEligible` (`tenantId`, `provider`).
- **ERROR**: cabinet store unreachable on enable/disable; mode upsert conflict.
- **Structured context**: `{ tenantId, provider, mode, secretName }` where applicable.
- **Credential safety**: NEVER log the supplied API key or the POST body.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
| 2026-06-17 | 1.1.0 | Scope-corrected: consume 32-3 credential resolver + 32-4 gating; removed duplicate key resolver | Claude |
