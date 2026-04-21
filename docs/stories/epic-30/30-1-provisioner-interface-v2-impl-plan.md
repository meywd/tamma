# Story 30-1 Implementation Plan — `ITenantInfrastructureProvider` v2

**Status**: Planned (2026-04-20)
**Story brief**: [`30-1-provisioner-interface-v2.md`](./30-1-provisioner-interface-v2.md)
**Epic 30 phase**: Foundation — land first.
**Branch**: `feat/story-30-1-provisioner-interface-v2`

---

## 1. Objective

Ship the v2 provisioning interface that separates "what kind of
infrastructure" (topology) from "which backend operates it" (provider),
with a capability matrix that the onboarding UI uses to filter
(backend, topology) pairs. Introduces `tenants.provider_key` column
and `provider_resource_ids` JSONB; keeps the v1 `ITenantProvisioner`
as a deprecated shim so existing Cranl callers compile through the
feature-flag rollout.

## 2. Dependencies

Hard blockers:

- **Story 28-3** — `TenantDbContextFactory` exists.
- **Story 29-2** — secret store exists (capability matrix may
  reference secrets).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantInfrastructureProvider.cs` | v2 interface. |
| `.../Services/Provisioning/ProviderCapabilities.cs` | Capability record. |
| `.../Services/Provisioning/ProvisioningTopology.cs` | `DatabaseOnly | DedicatedCompute | Managed` flags. |
| `.../Services/Provisioning/ProvisioningRequest.cs` | Request record. |
| `.../Services/Provisioning/ProvisioningResult.cs` | Result record. |
| `.../Services/Provisioning/TenantEndpoints.cs` | `{ DbUrl, EngineHost, EngineUrl, CustomDomain? }`. |
| `.../Services/Provisioning/TenantProviderRegistry.cs` | Keyed DI resolver `GetProvider(string key)`. |
| `.../Services/Provisioning/NullTenantProvider.cs` | Always-available no-op (dev / tests). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260515000000_TenantProviderColumns.cs` | Adds `provider_key`, `provider_resource_ids`, backfills existing Cranl rows. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/ProviderRegistryTests.cs` | Capability-matrix tests. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/TopologyCompatibilityTests.cs` | Every provider × topology pair. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantProvisioner.cs` | Mark `[Obsolete]`; keep as shim. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs` | Temporary shim wraps the v2 provider once 30-3 lands. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Add `ProviderKey` + `ProviderResourceIds` props. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register keyed singletons + `TenantProviderRegistry`. |

## 5. Sequence of changes

### Step 1 — Types + interface (3h)

- `ProvisioningTopology`, `ProvisioningRequest`, `ProvisioningResult`,
  `TenantEndpoints`, `ProviderCapabilities`.
- `ITenantInfrastructureProvider` with 5 methods.
- Unit test capability-matrix rendering.
- **Commit**: `feat(provisioning): v2 interface + types`.

### Step 2 — Registry (2h)

- `TenantProviderRegistry` uses `IServiceProvider.GetKeyedService<ITenantInfrastructureProvider>(key)`.
- Throws on missing key.
- Unit test registration + lookup.
- **Commit**: `feat(provisioning): TenantProviderRegistry`.

### Step 3 — Null provider (2h)

- `NullTenantProvider` with `ProviderKey="null"`; capability shows
  `DatabaseOnly` only; Provision is no-op returning Ready.
- **Commit**: `feat(provisioning): NullTenantProvider (dev)`.

### Step 4 — Migration + entity (3h)

- EF migration adds `provider_key TEXT NOT NULL DEFAULT 'cranl'`,
  `provider_resource_ids JSONB NOT NULL DEFAULT '{}'::jsonb`.
- Backfills existing rows: `UPDATE tenants SET provider_key='cranl',
  provider_resource_ids=jsonb_build_object('cranl_project_id', cranl_project_id, ...)`.
- `Tenant.cs` adds props.
- **Commit**: `feat(db): tenants.provider_key + provider_resource_ids`.

### Step 5 — v1 shim (2h)

- Mark `ITenantProvisioner` + `CranlTenantProvisioner` `[Obsolete]`.
- Shim delegates to `TenantProviderRegistry.GetProvider("cranl")`.
- Tests: old callers still compile; behaviour unchanged.
- **Commit**: `chore(provisioning): deprecate v1 shim`.

### Step 6 — Topology compatibility tests (3h)

- For every registered provider × every topology:
  - If unsupported, `ProvisionAsync` returns `Failed` with clear
    error.
- Asserts AC9.
- **Commit**: `test(provisioning): topology compatibility matrix`.

### Step 7 — DI registration + docs (3h)

- Keyed singletons: `"null"`, `"cranl"` (placeholder until 30-3).
- Architectural docs in `docs/stories/epic-30/architecture-notes.md`.
- **Commit**: `docs(provisioning): v2 architecture notes`.

## 6. Test strategy

### Unit

- Types serialise correctly.
- Registry keyed lookup; missing key throws.
- Topology enum flags composition.

### Integration

- Migration applies cleanly on a fresh + pre-existing DB.
- Shim preserves v1 caller behaviour.

## 7. Rollback plan

- **Migration rollback**: migration has a `Down` that drops the two
  columns; v1 shim continues working.
- **Revert**: commits ordered; reverse revert restores pre-v2 state.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Types + interface | 3 |
| 2. Registry | 2 |
| 3. Null provider | 2 |
| 4. Migration + entity | 3 |
| 5. v1 shim | 2 |
| 6. Compatibility tests | 3 |
| 7. Docs + DI | 3 |
| **Total** | **18** (matches brief). |

## 9. Open questions

- **Cost hint schema**: brief says `CostUnitsPerMonth?` — number or
  structured record? Plan: structured (see 30-10). Update at that
  story.
- **Regions as strings**: free-form or enum? Plan: free-form strings
  per provider to avoid enum maintenance pain.
- **Feature-flag coexistence**: during 30-3 rollout, both v1 and v2
  cohabit for Cranl. Registry returns v2; shim delegates.
- **`MaxTenantsPerOrg`**: nullable int — per-tier. Revisit for
  Cloudflare (50k) vs Cranl (TBD) once providers ship.
- **Backfill performance**: `UPDATE ... SET provider_resource_ids = ...`
  on the full `tenants` table runs in <1s for current volumes.
  Documented in migration.
