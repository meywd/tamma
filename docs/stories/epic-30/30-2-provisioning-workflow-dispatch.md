# Story 30-2: Provisioning Workflow — Resumable, Per-Backend Dispatch

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want a single `ProvisionTenantWorkflow` in Elsa that resolves the correct backend from the tenant's `provider_key`, dispatches to that backend's `ProvisionAsync`, handles timeout / failure with a saga-shaped compensation, and persists intermediate state so it survives restarts,
so that Epic 28's `CreateTenantWorkflow` (which today calls Cranl directly) becomes provider-agnostic and new backends automatically get durable provisioning, resumption, and audit trail.

## Acceptance Criteria

1. `ProvisionTenantWorkflow` replaces `CreateTenantWorkflow` (Story 28-5) or wraps it. Input: `{ tenantId, provisioningRequest }` (where `provisioningRequest.ProviderKey` drives dispatch). Output: `ProvisioningResult`.
2. Workflow sequence (each step an Elsa activity with a compensation):
   - `ResolveProviderActivity` — resolves from `TenantProviderRegistry`; compensation: none (pure lookup).
   - `PreflightActivity` — asks the provider for capability match (topology supported? region valid?); fail fast if not. Compensation: none.
   - `ReserveResourcesActivity` — emits `TENANT.PROVISION.STARTED`, flips row to `provisioning_state = Pending`. Compensation: flip back to `New` + emit `CANCELLED`.
   - `ExecuteProvisionActivity` — calls `ITenantInfrastructureProvider.ProvisionAsync`. This is the long-running step (Cranl can take 2-5 min). Compensation: `ITenantInfrastructureProvider.DeprovisionAsync`.
   - `PersistEndpointsActivity` — writes `provider_resource_ids` JSONB + updates `tenants.provider_key` + stores endpoints in routing cache (30-8). Compensation: clear those columns.
   - `RegisterSecretsActivity` — creates the initial cabinet rows for the new tenant (DB connection, any API keys the provider surfaces) via `ISecretStore.CreateAsync` (Epic 29). Compensation: `ISecretStore.RetireVersionAsync` on each.
   - `InitialProbeActivity` — calls `ITenantInfrastructureProvider.GetStatusAsync` until `Ready` or timeout (30 min). Retries: none — this is a timeout, not a retry. Compensation: `DeprovisionAsync`.
   - `ActivateActivity` — flips `provisioning_state = Ready`, emits `TENANT.PROVISION.COMPLETED`.
3. Workflow is **resumable**: Elsa persists state per activity. A process restart between `ExecuteProvisionActivity` and `InitialProbeActivity` picks up on restart; `ResolveProviderActivity` is re-run (pure); `ExecuteProvisionActivity` is **not** re-run if its output was persisted (idempotency enforced by provider implementations — each one checks "do my resources already exist for this tenantId" and short-circuits).
4. Compensation fires in reverse order on any step's failure. If compensation itself fails, emit `TENANT.PROVISION.COMPENSATION.FAILED` with the orphan resource ids; operator alert via 29-4's audit feed; halt.
5. Workflow emits events at each step: `TENANT.PROVISION.<STEP>.<OUTCOME>` into `platform_events`. Correlation id = `tenantId`. Every step also records duration so 30-10's cost dashboard can attribute time.
6. `PreflightActivity` enforces per-provider quotas: `registry.GetProvider(k).GetCapabilities().MaxTenantsPerOrg?` — if the invoking org has reached the limit, fail fast before reserving resources. Quota check uses `TenantRepository.CountByOrgAndProvider(orgId, providerKey)`.
7. Timeout per step is configurable per provider: Cranl 5 min for `ExecuteProvisionActivity`, Hetzner 10 min, Cloudflare 60 s, BYO 30 s. Surfaced via `ProviderCapabilities.TimeoutSeconds` dictionary.
8. Unit tests cover: success path; provisioner throws during `ExecuteProvisionActivity` → compensation runs; probe times out → compensation runs; persistence layer rollback survives (wrap in test transaction).
9. Integration test with fake `ITenantInfrastructureProvider` implementing the `"test"` key: full workflow run end-to-end, verify all events emitted in order, verify tenant row reaches `Ready`.
10. A second workflow `DeprovisionTenantWorkflow` (Story 30-9) reuses the same dispatch pattern with reverse-order activities.

## Technical Context

### Why replace CreateTenantWorkflow

Epic 28's `CreateTenantWorkflow` (Story 28-5) hard-codes the Cranl
flow. We replace it rather than sit alongside to avoid two workflows
operating on the same `tenants` row with subtly different state
machines.

The migration:

1. Deploy 30-2 behind a feature flag `Tenants:AsyncProvisioning:UseV2 = false`.
2. Run 30-3 (Cranl refactor).
3. Flip the feature flag on staging — assert existing tenant create still works.
4. Flip the feature flag on production.
5. Delete the old workflow.

### Resumability semantics

Elsa's activity state persistence gives us resumability for free.
Provider calls are the only external side-effect, and each provider is
responsible for making `ProvisionAsync` idempotent (keyed by `tenantId`
on their side — e.g. Cranl provider checks if `CranlProjectId` is
already set on the tenant row before calling `POST /projects`).

### Compensation catalog

```
ResolveProvider         → (none)
Preflight               → (none)
ReserveResources        → flip state back to New
ExecuteProvision        → DeprovisionAsync
PersistEndpoints        → clear endpoints + provider_resource_ids
RegisterSecrets         → RetireVersionAsync on each
InitialProbe            → DeprovisionAsync
Activate                → flip state back to provisioning_failed
```

## Estimated hours

22 — workflow + 8 activities + compensations + resumability tests +
feature flag + migration from v1 workflow.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Activities/Provisioning/ProvisionTenantWorkflow.cs` (new; replaces CreateTenantWorkflow)
- `apps/tamma-elsa/src/Tamma.Activities/Provisioning/*Activity.cs` (8 new)
- Adjust Story 28-5 outputs: its workflow becomes a thin wrapper that enqueues this one.

## References

- Story 30-1 interface
- Story 28-5 current workflow: `docs/stories/epic-28/story-28-5/...`
- Research notes §2, §3
- [Temporal — Saga Pattern](https://temporal.io/blog/mastering-saga-patterns-for-distributed-transactions-in-microservices)
