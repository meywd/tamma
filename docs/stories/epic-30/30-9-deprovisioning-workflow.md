# Story 30-9: Deprovisioning Workflow — Reverse Each Backend

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform operator**,
I want a `DeprovisionTenantWorkflow` that reverses the provisioning saga per-backend — deletes cloud resources via the provider's `DeprovisionAsync`, purges Epic 29 cabinet rows, clears routing cache, and archives audit events without deleting them,
so that tenant offboarding is one workflow regardless of which backend owns the tenant, with the same saga-shape guarantees as the provisioning side.

## Acceptance Criteria

1. `DeprovisionTenantWorkflow` registered in Elsa. Input: `{ tenantId, requestedByUserId, reason (string) }`. Output: `{ outcome: "Deprovisioned" | "Failed", orphanResources? }`.
2. Workflow sequence (forward only — this workflow **is** the compensation for 30-2; its own failures produce alerts not recursive compensation):
   - `FreezeTenantActivity` — flips `provisioning_state = Deprovisioning`; evicts routing cache; any new request for this tenant returns 410 Gone via Story 28-8's middleware.
   - `DrainInFlightActivity` — waits up to 60 s for in-flight requests to finish (observed via the connection pool + active SSE subscriptions).
   - `PurgeCabinetActivity` — for every secret with `Scope = tenant && TenantId = this`, retire to `Revoked`. Does not delete rows — audit retention keeps them for 90 days minimum.
   - `ExecuteDeprovisionActivity` — calls `ITenantInfrastructureProvider.DeprovisionAsync`. On failure, log `orphanResources` from the provider's response and fail the workflow (operator intervention).
   - `ClearRoutingActivity` — emits `TENANT.ROUTING.CHANGED { reason: "deprovision" }` so every node invalidates.
   - `ArchiveTenantRowActivity` — flips `tenants.deleted_at = now()` but leaves the row; clears `provider_resource_ids` and `provider_key`. Keeps `platform_events` audit rows untouched.
   - `NotifyActivity` — emails tenant owner (via `platform_email_outbox`) confirming offboarding + summary of what was deleted vs what was retained.
3. A **confirmation token** is required to invoke this workflow: operator generates a token via `POST /api/v1/admin/tenants/{id}/deprovision-token` (returns a 6-digit code expiring in 10 min); pastes the code into the UI; token is verified server-side before the workflow is enqueued. Prevents accidental deprovisioning.
4. BYO tenants' deprovisioning is **non-destructive** on the customer side (Story 30-6 AC 4) — the `ExecuteDeprovisionActivity` for BYO only clears platform-side state. Workflow explicitly checks `ProviderKey == "byo"` and surfaces a "customer's resources retained" summary.
5. Idempotency: re-running the workflow on a fully-deprovisioned tenant is a no-op — each activity checks the current state and short-circuits if already complete.
6. Confirmation screen in admin UI shows a preview of what will be deleted (cloud resources by provider) vs retained (audit events, billing records, cabinet audit rows). Tenant-admin cannot self-deprovision; must contact platform admin (feature flagged for future self-service).
7. `orphanResources` log: if `ExecuteDeprovisionActivity` returns orphans, a `TENANT.DEPROVISION.ORPHANS` event is emitted with the list of resource ids per backend. Operators get an alert via 29-4's feed and can manually clean up through each provider's native UI (Hetzner Cloud console, Cloudflare dashboard, etc.).
8. Integration test with fake providers: full happy path on each of 4 backends; partial failure scenario (Hetzner server delete 500s) → workflow fails, orphan reported, tenant row remains in `Deprovisioning` state pending operator retry.
9. Data-retention policy: `platform_events` rows for the tenant are kept 90 days then purged by a scheduled workflow (out of scope for this story — flagged for Epic 17 follow-up). `domain_events` for the tenant go away when the tenant DB is dropped (the provider's `DeprovisionAsync` drops the DB for Cranl/Hetzner/Cloudflare; for BYO, the customer keeps them).
10. Closes the "deprovisioning workflow reverse each provider's create; SecretStore purge; DNS cleanup" user requirement from the Epic 30 planning task spec.

## Technical Context

### Why non-recursive compensation

The provisioning workflow (30-2) uses compensation on step failures
to undo what it did. The deprovisioning workflow is *itself* the
compensation for provisioning — if it fails mid-way, its failure is
an orphan-resource alert, not another compensation. Recursive
compensation would try to "unprovision the deprovisioning" which is
incoherent. The admin workflow when deprovision fails is:

1. Read the orphan list.
2. Delete resources manually in each cloud.
3. Re-run `DeprovisionTenantWorkflow` — it's idempotent; it picks
   up where it left off and completes.

### 60-second drain window

`DrainInFlightActivity` counts active tenant-scoped requests
(via a simple in-memory counter keyed by `tenantId` incremented by
`TenantContextMiddleware` on entry and decremented on completion).
60 s is enough for most long-running HTTP calls; forced proceed after
timeout with a warning event.

### Data retention vs deletion

Three classes of data:

- **Cloud resources** (VMs, DBs, Workers, D1 databases) — deleted in
  `ExecuteDeprovisionActivity`.
- **Epic 29 cabinet rows** — retired to `Revoked` (soft-delete) with
  audit retention.
- **`platform_events` for the tenant** — retained 90 days then hard-
  deleted (scheduled purge).
- **`tenants` row** — soft-deleted (`deleted_at = now()`), retained
  indefinitely for billing audit.

Documented in the runbook.

## Estimated hours

16 — workflow + 7 activities + confirmation token + orphan alerting +
runbook + integration tests.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Activities/Provisioning/DeprovisionTenantWorkflow.cs` (new)
- `apps/tamma-elsa/src/Tamma.Activities/Provisioning/*DeprovisionActivity.cs` (7 new)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminTenantEndpoints.cs` (extend — token endpoint + deprovision trigger)
- `docs/runbooks/tenant-offboarding.md` (new)

## References

- Story 30-1, 30-2, 30-3..30-6
- Epic 28 Story 28-5 (create/delete workflows)
- Story 29-1..29-10 (cabinet purge path)
- Research notes §3 (saga shape)
