# Story 30-2 Implementation Plan — Resumable Per-Backend Provisioning Workflow

**Status**: Planned (2026-04-20)
**Story brief**: [`30-2-provisioning-workflow-dispatch.md`](./30-2-provisioning-workflow-dispatch.md)
**Epic 30 phase**: Foundation — after 30-1.
**Branch**: `feat/story-30-2-provision-workflow-dispatch`

---

## 1. Objective

Replace (behind a feature flag) Epic 28's Cranl-specific
`CreateTenantWorkflow` with a provider-agnostic `ProvisionTenantWorkflow`
that dispatches to the backend in `provisioningRequest.ProviderKey`,
survives restart, handles step timeout/failure with reverse-order
compensation, and emits per-step events for the admin UI's SSE feed.

## 2. Dependencies

Hard blockers:

- **Story 30-1** — registry + interface.
- **Story 28-5** — `platform_events` + `platform_queued_tasks`
  infrastructure.
- **Story 29-2** — secret store (for `RegisterSecretsActivity`).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/Provisioning/ProvisionTenantWorkflow.cs` | Master workflow. |
| `.../Provisioning/ResolveProviderActivity.cs` | Step 1. |
| `.../Provisioning/PreflightActivity.cs` | Step 2 (quota + topology). |
| `.../Provisioning/ReserveResourcesActivity.cs` | Step 3. |
| `.../Provisioning/ExecuteProvisionActivity.cs` | Step 4 — long-running. |
| `.../Provisioning/PersistEndpointsActivity.cs` | Step 5. |
| `.../Provisioning/RegisterSecretsActivity.cs` | Step 6. |
| `.../Provisioning/InitialProbeActivity.cs` | Step 7. |
| `.../Provisioning/ActivateActivity.cs` | Step 8. |
| `.../Provisioning/CompensationCatalog.cs` | Named compensators per step. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/ProvisionTenantWorkflowTests.cs` | Happy + compensation + resumability. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/CreateTenantWorkflow.cs` | When flag `Tenants:AsyncProvisioning:UseV2=true`, becomes a thin wrapper that enqueues `ProvisionTenantWorkflow`. Old path preserved behind flag. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Flag. |

## 5. Sequence of changes

### Step 1 — Activity skeletons (3h)

- All 8 activity classes, each accepting `WorkflowContext` and
  emitting a step event.
- **Commit**: `feat(provisioning): workflow activity skeletons`.

### Step 2 — Resolve + preflight (3h)

- `ResolveProviderActivity` → registry lookup.
- `PreflightActivity`:
  - Topology supported? Region in capability list?
  - Org quota: `TenantRepository.CountByOrgAndProvider(orgId, providerKey)` vs `MaxTenantsPerOrg?`.
- **Commit**: `feat(provisioning): resolve + preflight activities`.

### Step 3 — Reserve + execute (4h)

- `ReserveResourcesActivity` flips state to `Pending`; emits
  `TENANT.PROVISION.STARTED`.
- `ExecuteProvisionActivity` calls `provider.ProvisionAsync`. Long-
  running; timeout per provider's `Capabilities.TimeoutSeconds["execute"]`.
- **Commit**: `feat(provisioning): reserve + execute activities`.

### Step 4 — Persist + register secrets (3h)

- `PersistEndpointsActivity`: updates `provider_resource_ids` +
  emits `TENANT.ROUTING.CHANGED` (consumed by 30-8).
- `RegisterSecretsActivity`: calls `ISecretStore.CreateAsync` per
  provider-surfaced secret.
- **Commit**: `feat(provisioning): persist + secrets activities`.

### Step 5 — Probe + activate (3h)

- `InitialProbeActivity`: polls `provider.GetStatusAsync` until
  `Ready` or 30-min timeout.
- `ActivateActivity`: flips state to `Ready`, emits
  `TENANT.PROVISION.COMPLETED`.
- **Commit**: `feat(provisioning): probe + activate activities`.

### Step 6 — Compensation catalog (4h)

- `CompensationCatalog` records executed steps; on failure runs
  reverse-order compensators:
  - `ReserveResources` → flip back to `New`.
  - `ExecuteProvision` → `DeprovisionAsync`.
  - `PersistEndpoints` → clear cols.
  - `RegisterSecrets` → `RetireVersionAsync`.
  - `InitialProbe` → `DeprovisionAsync`.
- If compensation itself fails, emit
  `TENANT.PROVISION.COMPENSATION.FAILED` with orphan ids; halt.
- **Commit**: `feat(provisioning): reverse-order compensation`.

### Step 7 — Workflow + feature flag (3h)

- `ProvisionTenantWorkflow.cs` composes activities with compensation.
- Feature flag gates v1 vs. v2; rollout per brief.
- Integration test with `test` provider: happy path, mid-step
  failure, probe timeout.
- **Commit**: `feat(provisioning): ProvisionTenantWorkflow`.

## 6. Test strategy

### Unit

- Each activity's happy + failure.
- Compensation catalog reverse-order execution.

### Integration

- Fake `ITenantInfrastructureProvider` (`ProviderKey="test"`) with
  scriptable outcomes.
- Full success; mid-step failure; probe timeout; resumption after
  simulated Elsa restart.

### Observability

- Every activity emits one `TENANT.PROVISION.<STEP>.<OUTCOME>` event;
  verified by event-stream assertion.

## 7. Rollback plan

- **Feature flag** flips back to v1 workflow (Cranl-only).
- **State machine**: `tenants.provisioning_state` rows created by v2
  are compatible with v1 reads.
- **Non-reversible**: compensation of `ExecuteProvisionActivity`
  invokes provider's `DeprovisionAsync` — some cloud resources may
  have been partly created and may need manual cleanup per provider.
  Runbook documents.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Activity skeletons | 3 |
| 2. Resolve + preflight | 3 |
| 3. Reserve + execute | 4 |
| 4. Persist + secrets | 3 |
| 5. Probe + activate | 3 |
| 6. Compensation catalog | 4 |
| 7. Workflow + flag | 3 |
| **Total** | **23** (brief 22). |

## 9. Open questions

- **Timeout per provider**: surfaced via
  `Capabilities.TimeoutSeconds` dictionary. Needs default values:
  Cranl 300s, Hetzner 600s, Cloudflare 60s, BYO 30s.
- **Resumability after Elsa restart**: Elsa's own activity state
  persistence handles this. Provider idempotency (each `ProvisionAsync`
  checks for existing resources) guarantees no double-create.
- **Compensation failure alert**: emits event; UI surfaces; operator
  intervenes. No automatic retry of compensation.
- **`TENANT.ROUTING.CHANGED` ordering**: `PersistEndpointsActivity`
  emits this before `RegisterSecretsActivity` completes. If the
  routing cache refreshes mid-activation, it might route to a
  not-yet-active tenant. 30-8 handles via `TenantUnavailable`
  response.
- **v1-to-v2 data parity**: v1 tenants have Cranl columns populated.
  v2 expects `provider_resource_ids`. 30-1's backfill migration
  covers; this story inherits.
