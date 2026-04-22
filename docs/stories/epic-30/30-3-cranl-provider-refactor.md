# Story 30-3: Cranl Provider Refactor to v2 Interface

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want `CranlTenantProvisioner` refactored behind the v2 `ITenantInfrastructureProvider` interface from Story 30-1 with zero behavior change, so the existing Cranl flow runs under the new dispatch workflow from Story 30-2 and Cranl becomes "one of four backends" rather than "the only backend".

## Acceptance Criteria

1. `CranlTenantProvider : ITenantInfrastructureProvider` with `ProviderKey = "cranl"` lives in `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/`. It wraps (or absorbs) the existing `CranlTenantProvisioner` methods.
2. `GetCapabilities()` returns:
   - `SupportedTopologies = DedicatedCompute`
   - `Regions = ["germany-1", "us-east-1", "saudi-arabia-1", "egypt-1", "india-1"]` (plus "coming soon" list as metadata only)
   - `Features = CustomDomains` (yes) + `DedicatedDb` (yes) + `AutoscaleCompute` (no) + `BackupManagement` (via Cranl UI, not us)
3. `ProvisionAsync` walks the Cranl README flow (project → DB → poll → app → env → deploy → poll → domains) and returns `ProvisioningResult`:
   - `ProviderResourceIds = { "cranl_project_id": ..., "cranl_database_id": ..., "cranl_app_id": ..., "cranl_region": ... }`
   - `Endpoints = { DbUrl: ..., EngineHost: cranl_app_url, EngineUrl: https://... }`
4. `DeprovisionAsync` walks the teardown (delete app → DB → project). Reads resource ids from the tenant row's new `provider_resource_ids` JSONB (from Story 30-1) or falls back to the legacy `cranl_*` columns if they haven't been migrated yet. Last-step deletes both sources.
5. `ResolveEndpointsAsync` returns the Cranl endpoints from the resource ids + the secret store's `tenant:db/cranl-connection` value (via Epic 29). No longer reads `cranl_database_url_encrypted` directly.
6. Idempotency: if `provider_resource_ids.cranl_project_id` is set when `ProvisionAsync` is called, return the current status rather than creating a second project (unchanged behavior from today).
7. All existing tests from `CranlProvisioningWorkflowTests` pass against the v2 provider (port them to exercise the `ITenantInfrastructureProvider` surface). Add a test asserting `GetCapabilities()` returns the expected capability set.
8. Old `CranlTenantProvisioner : ITenantProvisioner` is kept as a deprecated shim (v1 interface → v2 provider) for the feature-flag rollout window per Story 30-2 AC. Marked with `[Obsolete("Use ITenantInfrastructureProvider via TenantProviderRegistry.GetProvider(\"cranl\")")]`.
9. The secrets Epic 29 knows how to rotate Cranl env vars — `ITenantInfrastructureProvider` in this story does **not** handle rotations; it delegates via Cabinet metadata. Story 30-3 adds a `RegisterInitialSecretsAsync` helper called by 30-2's `RegisterSecretsActivity` that creates the cabinet rows: `tenant:db/cranl-connection`, `tenant:cranl/app-env-hmac` (the `TAMMA_SHARED_SECRET` shadow per tenant if we split it).
10. DB migration (no schema change this story — 30-1 already added `provider_key` + `provider_resource_ids`) — but this story writes the data-migration script that populates existing tenants' `provider_resource_ids` from the legacy `cranl_*` columns: `UPDATE tenants SET provider_key = 'cranl', provider_resource_ids = jsonb_build_object('cranl_project_id', cranl_project_id, ...) WHERE cranl_project_id IS NOT NULL`.

## Technical Context

### Minimal-risk refactor

The Cranl flow is the only piece of production Tamma today (tests +
staging). A refactor that changes its behavior is a regression risk.
This story is explicitly a *mechanical* refactor:

- Same HTTP calls, same order.
- Same state machine on `tenants.provisioning_state`.
- Same polling timeouts.
- Same secret protection (via Epic 29 — not via `TenantSecretProtector`).

The only behavior change is "the data flows through 30-2's workflow
now" — which is itself gated behind `Tenants:AsyncProvisioning:UseV2`.

### Provider resource id shape

Today: `tenants.cranl_project_id`, `cranl_database_id`, `cranl_app_id`,
`cranl_region` as columns.

After 30-1 + 30-3: `tenants.provider_resource_ids` JSONB holding the
same keys. Each provider is free to add its own keys (Hetzner gets
`hetzner_server_id`, Cloudflare gets `cloudflare_d1_id`).

Query patterns change slightly:

```csharp
// before
var projectId = tenant.CranlProjectId;

// after
var projectId = tenant.ProviderResourceIds["cranl_project_id"]?.ToString();
```

## Estimated hours

14 — provider class + capability matrix + data migration + port Cranl
tests + deprecated shim + register-initial-secrets helper.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlTenantProvider.cs` (new, subsumes old `CranlTenantProvisioner` logic)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlTenantProvisioner.cs` (delete)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlProvisioningWorkflow.cs` (fold into provider or delete)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260515100000_BackfillCranlProviderResourceIds.cs` (new — data migration)

## References

- Story 30-1 interface
- Story 30-2 dispatch workflow
- Cranl README: `docs/vendors/cranl/README.md`
- Epic 28 tenant columns migration: `20260419204924_CranlProvisioningColumns.cs`
