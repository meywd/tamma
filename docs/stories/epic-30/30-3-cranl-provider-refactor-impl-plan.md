# Story 30-3 Implementation Plan — Cranl Provider Refactor to v2

**Status**: Planned (2026-04-20)
**Story brief**: [`30-3-cranl-provider-refactor.md`](./30-3-cranl-provider-refactor.md)
**Epic 30 phase**: Provider drivers — after 30-1 + 30-2.
**Branch**: `feat/story-30-3-cranl-provider-v2`

---

## 1. Objective

Mechanical refactor of `CranlTenantProvisioner` to implement the v2
`ITenantInfrastructureProvider` interface. Zero behaviour change —
same HTTP calls, same state machine, same polling timeouts. Also
ships the data-migration script that populates existing tenants'
`provider_resource_ids` JSONB from the legacy `cranl_*` columns.

## 2. Dependencies

Hard blockers:

- **Story 30-1** — v2 interface.
- **Story 30-2** — dispatch workflow consumes this provider.
- **Story 29-2** — secret store for Cabinet registration.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlTenantProvider.cs` | v2 impl. |
| `.../Provisioning/Cranl/CranlCapabilities.cs` | Capability constants. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260515100000_BackfillCranlProviderResourceIds.cs` | Data migration. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/CranlTenantProviderTests.cs` | Port of existing Cranl tests. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlTenantProvisioner.cs` | Delete (logic absorbed into v2 provider). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlProvisioningWorkflow.cs` | Delete (superseded by 30-2 workflow). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `AddKeyedSingleton<ITenantInfrastructureProvider, CranlTenantProvider>("cranl")`. |

## 5. Sequence of changes

### Step 1 — v2 provider skeleton (3h)

- Class + `ProviderKey` + capability map.
- **Commit**: `feat(cranl): v2 provider skeleton`.

### Step 2 — ProvisionAsync (3h)

- Port existing CranlTenantProvisioner Create flow:
  - `POST /projects` → `POST /databases` → poll → `POST /apps` →
    `PUT /environment` → `POST /deploy` → poll.
- Captures `provider_resource_ids` JSONB: `cranl_project_id`,
  `cranl_database_id`, `cranl_app_id`, `cranl_region`.
- Idempotency via `provider_resource_ids.cranl_project_id` existence check.
- **Commit**: `feat(cranl): provisionAsync v2`.

### Step 3 — DeprovisionAsync (2h)

- Walk teardown: delete app → db → project.
- Reads resource ids from `provider_resource_ids` JSONB; falls back
  to legacy `cranl_*` columns during transition; last step clears both.
- **Commit**: `feat(cranl): deprovisionAsync v2`.

### Step 4 — ResolveEndpointsAsync (2h)

- Builds `TenantEndpoints` from resource ids + `ISecretStore.GetAsync("tenant:db/cranl-connection", tenantId)`.
- No direct reads of `cranl_database_url_encrypted`.
- **Commit**: `feat(cranl): resolveEndpointsAsync v2`.

### Step 5 — RegisterInitialSecretsAsync helper (2h)

- Creates cabinet rows: `tenant:db/cranl-connection`, `tenant:cranl/app-env-hmac`.
- Called by 30-2's `RegisterSecretsActivity`.
- **Commit**: `feat(cranl): initial-secrets helper`.

### Step 6 — Data migration (2h)

- `BackfillCranlProviderResourceIds`:
  ```sql
  UPDATE tenants
  SET provider_key = 'cranl',
      provider_resource_ids = jsonb_build_object(
        'cranl_project_id', cranl_project_id,
        'cranl_database_id', cranl_database_id,
        'cranl_app_id', cranl_app_id,
        'cranl_region', cranl_region)
  WHERE cranl_project_id IS NOT NULL;
  ```
- Idempotent via `WHERE provider_resource_ids = '{}'::jsonb` guard.
- **Commit**: `migration(cranl): backfill provider_resource_ids`.

### Step 7 — Port tests (2h)

- Copy `CranlProvisioningWorkflowTests` to exercise v2 provider surface.
- New: `GetCapabilities` returns expected set.
- **Commit**: `test(cranl): port tests to v2 provider`.

### Step 8 — Delete old code (1h)

- Remove v1 `CranlTenantProvisioner` + `CranlProvisioningWorkflow`.
- v1 `ITenantProvisioner` shim from 30-1 continues working.
- **Commit**: `chore(cranl): delete v1 provisioner`.

## 6. Test strategy

### Unit

- Capability matrix.
- Resource-id serialization round-trip.

### Integration

- Testcontainers + faked Cranl API client: full provision → endpoints → deprovision.
- Backfill migration: seed tenants, run migration, assert JSONB matches cols.

### Regression

- Existing Cranl tests pass against v2 provider.

## 7. Rollback plan

- **Feature flag** in 30-2 reverts to v1 workflow which still works
  with the kept v1 `ITenantProvisioner` shim.
- **Migration rollback**: `Down` method retains legacy columns since
  they're not dropped by this story (29-10 drops `cranl_database_url_encrypted`).
- **Non-reversible**: deletion of `CranlTenantProvisioner.cs` is
  reversible via `git revert`.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Skeleton | 3 |
| 2. ProvisionAsync | 3 |
| 3. DeprovisionAsync | 2 |
| 4. ResolveEndpointsAsync | 2 |
| 5. Initial-secrets helper | 2 |
| 6. Data migration | 2 |
| 7. Port tests | 2 |
| 8. Delete old | 1 |
| **Total** | **17** (brief 14; +3 for migration testing). |

## 9. Open questions

- **Legacy columns drop timing**: `cranl_*` columns are dropped in
  29-10. This story keeps them during the migration window.
- **Cranl region list**: per brief — confirm "germany-1" etc. are
  current Cranl region IDs at implementation.
- **Cranl downtime behaviour**: `api.cranl.com` outages cause
  provisioning to fail. Retry semantics via 30-2's step timeouts.
- **Provider-resource-ids schema stability**: document the JSONB
  keys per provider; lock schema before 30-10 ingests.
- **30-2 v2 flag state**: provider is available whether or not v2
  workflow is live (v1 shim still resolves it via registry).
