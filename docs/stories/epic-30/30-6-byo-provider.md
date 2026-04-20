# Story 30-6: BYO Provider — External DB + External Engine Registration

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform administrator** onboarding an enterprise tenant,
I want a `BringYourOwnTenantProvider` where the tenant supplies their own Postgres URL + their own Elsa runner URL, Tamma validates both, stores the endpoints, and registers routing without provisioning anything itself,
so that compliance-heavy customers (customer-owned RDS, hardware HSM, on-prem deploys) can use Tamma without their data ever crossing into our infrastructure — matching the Northflank BYOC pattern from the research notes.

## Acceptance Criteria

1. `BringYourOwnTenantProvider : ITenantInfrastructureProvider` with `ProviderKey = "byo"`. `GetCapabilities`:
   - `SupportedTopologies = Managed` only.
   - `Regions = []` (N/A).
   - `Features = DedicatedDb | BackupManagement (customer's responsibility, not ours)`.
2. `ProvisionAsync` **validates but does not create** infrastructure:
   - `ExistingDbUrl` (from `ProvisioningRequest`) — open a connection; assert the database is reachable and that the connecting user has `CREATE`, `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the public schema; assert the Postgres version is 15+; **refuse** if the connection requires `sslmode=disable` unless an explicit `--allow-plaintext-db` flag is passed in the request.
   - `ExistingEngineUrl` — HTTP `GET /health` must return 200 with the expected `{ engine: "tamma-engine", version: "...", status: "ready" }` body shape.
   - Runs Tamma's EF migrations (`dotnet ef database update`) against the tenant DB to ensure schema parity. Fails with a clear error if any migration cannot apply (e.g. extensions missing).
   - Writes the validated endpoints into `tenants.provider_resource_ids`: `{ "byo_db_url_ref": "tenant:db/byo-connection", "byo_engine_url": "..." }` (the actual DB URL is stored in Epic 29's cabinet; the resource-ids blob references it).
3. `ProvisionAsync` probes continuously for the first 30 min post-provision (via 30-2's `InitialProbeActivity`) to confirm the tenant's own engine is actually reachable under normal load. Flags transient failures to 30-4's alerting channel so a flaky enterprise deploy surfaces early.
4. `DeprovisionAsync` does **not** delete anything externally — it removes the tenant row + purges Epic 29 cabinet rows related to this tenant + removes routing config. The customer's DB and engine remain untouched. Documented explicitly in the runbook so an enterprise offboard doesn't accidentally wipe production data.
5. `ResolveEndpointsAsync` returns the stored endpoints from `provider_resource_ids` + a fresh lookup of `tenant:db/byo-connection` in Epic 29's cabinet (so rotation changes are picked up without requiring a re-provision).
6. A **rotation bridge**: when a BYO tenant admin wants to rotate their DB password, they rotate it out-of-band (in their own systems) and then push the new value into Tamma's cabinet via the tenant-admin UI (Story 29-5) — this is a "Managed" secret, no push handler. A specialised `ManagedSecretRotationHandler` for `System = "managed"` is added to Epic 29's registry and is a no-op on push (just updates the cabinet); all probe/rollback logic is run but compensation has no side-effects on the external system.
7. Validation step emits `TENANT.BYO.VALIDATION.<OUTCOME>` events with breakdown of which checks passed/failed. Customer-supplied URLs are treated as PII (hashed before log lines; only the hostname is visible without decrypting).
8. `GetStatusAsync` probes the engine and runs `SELECT 1` on the DB. Both green = Ready. One failure = Degraded. Both failing for 5 min = Unhealthy with alert.
9. Onboarding UI (Story 30-7) exposes a separate BYO flow with clear "you are responsible for the uptime, backups, and cost" language + a validation preview that surfaces the AC 2 check results before committing.
10. A `BYO` certification test suite — a fixture that deploys a pure-Postgres + tamma-engine container pair on the test host, registers it via the BYO provider, runs a smoke workflow end-to-end, and asserts everything the ordinary Cranl test asserts (agent dispatch, monitoring, event emission). Proves the BYO path is functionally complete.

## Technical Context

### What "bring your own" means concretely

Two decoupled inputs from the tenant:

- A Postgres URL (they operate it).
- An Elsa engine HTTP base URL (they operate it — probably by
  deploying Tamma's engine container pointed at their DB).

We validate the shape, register the endpoints, route traffic, and
leave operational responsibility with the customer.

### Why a separate "Managed" topology

BYO cannot use `DatabaseOnly` or `DedicatedCompute` — we're not
doing the compute. `Managed` captures "platform does not own this
infra; we only orchestrate". Hetzner / Cloudflare / Cranl are
non-Managed (platform operates); BYO is the only Managed at epic
close.

### Enterprise escape hatch

Some customers want "run Tamma entirely in our VPC". This story
covers the "DB + engine already exist" variant. A future epic could
add a "platform-operated in customer's VPC" variant (essentially
Hetzner provider but pointed at customer-owned Hetzner / AWS
account). That's out of scope here.

### Rotation semantics

A BYO tenant's DB password rotation sequence:

1. Customer rotates the password in their own systems.
2. Customer logs into `dash.tamma.dev/secrets`.
3. Opens the `db/byo-connection` secret, clicks Rotate.
4. The rotation dialog has a "paste new value" option rather than
   "auto-generate" (because the customer already rotated).
5. `ManagedSecretRotationHandler.PushAsync` is a no-op.
6. `ProbeAsync` opens a fresh connection with the new value — fails if
   the customer pasted the wrong value.
7. `ActivateAsync` flips cabinet versions.

This puts the right error ("wrong value pasted") in front of the
customer immediately rather than breaking background workflows later.

## Estimated hours

18 — provider + validation harness + managed rotation handler +
onboarding UI hook + certification test fixture + runbook.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Byo/BringYourOwnTenantProvider.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Byo/ByoValidationHarness.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/ManagedSecretRotationHandler.cs` (new)

## References

- Research notes §2 (Northflank BYOC shape)
- [Northflank — Multi-tenant cloud deployment 2026](https://northflank.com/blog/multi-tenant-cloud-deployment)
- Story 29-6 rotation workflow
- Story 30-1 interface
