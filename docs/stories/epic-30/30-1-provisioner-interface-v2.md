# Story 30-1: `ITenantInfrastructureProvider` v2 Interface + `ProvisioningTopology`

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want a v2 provisioning interface that separates "what kind of infrastructure" (topology) from "which backend operates it" (provider), with a capability matrix that tells the onboarding UI which (backend, topology) pairs are supported,
so that Epic 28's Cranl-only assumptions stop leaking into every layer and I can add Hetzner / Cloudflare / BYO backends as drivers rather than forks.

## Acceptance Criteria

1. `ITenantInfrastructureProvider` replaces `ITenantProvisioner`. Methods:
   - `ProviderKey` (string) — `"cranl"`, `"hetzner"`, `"cloudflare"`, `"byo"`, `"null"`.
   - `GetCapabilities()` → `ProviderCapabilities` (which topologies supported; region list; optional features like `SupportsCustomDomains`).
   - `ProvisionAsync(TenantId, ProvisioningRequest, CancellationToken)` — creates infra, returns `ProvisioningResult`.
   - `DeprovisionAsync(TenantId, CancellationToken)` — compensating; idempotent.
   - `GetStatusAsync(TenantId, CancellationToken)` — probe / status.
   - `ResolveEndpointsAsync(TenantId, CancellationToken)` → `TenantEndpoints` (DB connection info, engine host, engine URL, optional custom domain).
2. `ProvisioningTopology` enum:
   - `DatabaseOnly` — provision only a DB; engine runs on shared infrastructure.
   - `DedicatedCompute` — provision a VM / Worker + engine + DB.
   - `Managed` — tenant owns the infra; the platform only registers endpoints.
3. `ProvisioningRequest` fields: `Topology`, `Region?`, `Tier?` (starter / pro / enterprise — maps to resource sizing), `CustomName?`, `ExistingDbUrl?` (for Managed / BYO), `ExistingEngineUrl?` (for Managed / BYO), `ExtraTags` (dictionary of operator-supplied metadata).
4. `ProvisioningResult`: `Status` (Pending / Ready / Failed), `ProviderResourceIds` (dictionary — e.g. `{"cranl_project_id": "...", "cranl_app_id": "..."}` for Cranl; `{"hetzner_server_id": "..."}` for Hetzner), `Endpoints` (same shape as `TenantEndpoints`), `ProvisioningDurationSeconds`, `FailureReason?`.
5. `ProviderCapabilities` fields: `SupportedTopologies` (bit-flags), `Regions` (list of region ids), `MaxTenantsPerOrg?`, `CostUnitsPerMonth?` (for 30-10's dashboard), `Features` (bit-flags: `CustomDomains`, `AutoscaleCompute`, `DedicatedDb`, `BackupManagement`).
6. A new DB column `tenants.provider_key` (text) records which backend owns the row. Existing Cranl-provisioned tenants get `provider_key = "cranl"` on migration. Deprecate `tenants.cranl_*` columns as provider-specific: move them to a new JSONB column `provider_resource_ids` via migration `20260515000000_TenantProviderColumns.cs`.
7. `IProviderRegistry` DI service resolves providers by key: `registry.GetProvider("cranl") → ITenantInfrastructureProvider`. Registration uses keyed singletons. A default "null" provider is always registered for dev.
8. `ITenantProvisioner` (v1, Cranl-only) is kept as a deprecated shim that delegates to the v2 provider keyed `"cranl"`. The shim's tests still pass; new callers use v2. Deletion tracked as a final task in Epic 30 closeout.
9. xUnit: a single-capabilities test iterates every registered provider + every topology and asserts that `ProvisionAsync` returns `Failed` with a clear error when a provider is asked for an unsupported topology (rather than e.g. silently doing the wrong thing).
10. The workflow from Story 30-2 calls `registry.GetProvider(request.ProviderKey).ProvisionAsync(...)` — **no switch statement anywhere** on provider key outside the registry itself. New providers plug in via DI only.

## Technical Context

### Why topology not coupled to provider

Some backends can run more than one topology: Hetzner can do
`DatabaseOnly` (Postgres container on shared VM) or
`DedicatedCompute` (full VM per tenant). Cranl is more rigid — today
it's essentially `DedicatedCompute` (project + DB + app). The
topology enum keeps the dimension orthogonal so the onboarding UI
filters by (backend, topology) pairs.

### Capability matrix (initial)

| Provider | DatabaseOnly | DedicatedCompute | Managed |
|---|---|---|---|
| cranl | no | yes | no |
| hetzner | yes (stretch) | yes | no |
| cloudflare | yes | yes | no |
| byo | no | no | yes |
| null | yes (shared in-process) | no | no |

Matrix drives the onboarding UI (Story 30-7) filter.

### Interface sketch

```csharp
public interface ITenantInfrastructureProvider
{
    string ProviderKey { get; }
    ProviderCapabilities GetCapabilities();
    Task<ProvisioningResult> ProvisionAsync(Guid tenantId, ProvisioningRequest request, CancellationToken ct);
    Task DeprovisionAsync(Guid tenantId, CancellationToken ct);
    Task<ProvisioningStatus> GetStatusAsync(Guid tenantId, CancellationToken ct);
    Task<TenantEndpoints> ResolveEndpointsAsync(Guid tenantId, CancellationToken ct);
}
```

### Out-of-scope

- Provider implementations (30-3..30-6).
- Onboarding UI (30-7).
- Per-request routing consumer (30-8).

## Estimated hours

18 — interface + types + capability matrix + registry + migration to
`provider_key` + shim + tests.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantInfrastructureProvider.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ProvisioningModels.cs` (extend)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProviderRegistry.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260515000000_TenantProviderColumns.cs` (new)

## References

- User design intent: 2026-04-20
- Research notes §2
- Today's `ITenantProvisioner`: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantProvisioner.cs`
