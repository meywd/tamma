# Story 30-9 Implementation Plan — Deprovisioning Workflow

**Status**: Planned (2026-04-20)
**Story brief**: [`30-9-deprovisioning-workflow.md`](./30-9-deprovisioning-workflow.md)
**Epic 30 phase**: Runtime — after 30-2..30-6, 30-8.
**Branch**: `feat/story-30-9-deprovision-workflow`

---

## 1. Objective

Ship `DeprovisionTenantWorkflow` — reverse saga that deletes per-
backend cloud resources via provider's `DeprovisionAsync`, purges
Epic 29 cabinet rows, clears routing cache, archives audit events
without deleting them. Confirmation-token gating prevents accidental
deprovisioning; BYO is non-destructive on the customer side. Closes
the offboarding story for all 4 backends.

## 2. Dependencies

Hard blockers:

- **Story 30-2** — provisioning workflow patterns.
- **Stories 30-3..30-6** — each provider's `DeprovisionAsync`.
- **Story 30-8** — routing cache to invalidate.
- **Story 29-1..29-2** — cabinet for secret purge.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/Provisioning/DeprovisionTenantWorkflow.cs` | Master workflow. |
| `.../Provisioning/Deprovisioning/FreezeTenantActivity.cs` | Step 1. |
| `.../Provisioning/Deprovisioning/DrainInFlightActivity.cs` | Step 2. |
| `.../Provisioning/Deprovisioning/PurgeCabinetActivity.cs` | Step 3. |
| `.../Provisioning/Deprovisioning/ExecuteDeprovisionActivity.cs` | Step 4. |
| `.../Provisioning/Deprovisioning/ClearRoutingActivity.cs` | Step 5. |
| `.../Provisioning/Deprovisioning/ArchiveTenantRowActivity.cs` | Step 6. |
| `.../Provisioning/Deprovisioning/NotifyActivity.cs` | Step 7 email. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Admin/DeprovisionConfirmationTokens.cs` | 6-digit token issuer. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/DeprovisionTenantWorkflowTests.cs` | Per-backend tests. |
| `/home/meywd/tamma/docs/runbooks/tenant-offboarding.md` | Operator runbook. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminTenantActionsEndpoints.cs` | Add `POST /admin/tenants/:id/deprovision-token` + enhance delete action with token. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` | When state=`Deprovisioning`, return 410 Gone. |

## 5. Sequence of changes

### Step 1 — Confirmation tokens (2h)

- `DeprovisionConfirmationTokens.IssueAsync(tenantId, operatorId)`
  → 6-digit OTP; stored in `platform_queued_tasks` with
  `RunAfter=now+10min` + unique on `(tenantId, purpose='deprovision')`.
- `VerifyAndConsumeAsync(tenantId, code)` → flips consumed.
- Rate-limited endpoint: 3 token requests/tenant/hour.
- **Commit**: `feat(admin): deprovision confirmation tokens`.

### Step 2 — Freeze + drain activities (3h)

- `FreezeTenantActivity`: state=`Deprovisioning`, emits
  `TENANT.ROUTING.CHANGED`.
- `DrainInFlightActivity`: in-memory counter (keyed by tenantId)
  incremented by `TenantContextMiddleware`; wait 60s or counter=0.
- **Commit**: `feat(deprovision): freeze + drain`.

### Step 3 — Purge cabinet (2h)

- `PurgeCabinetActivity`: every tenant-scoped secret version flipped
  to `Revoked`. Rows kept for 90-day audit retention.
- **Commit**: `feat(deprovision): cabinet purge`.

### Step 4 — Execute deprovision (3h)

- Calls provider's `DeprovisionAsync`.
- On failure: captures `orphanResources`; emits
  `TENANT.DEPROVISION.ORPHANS` event; workflow fails (not compensated).
- **Commit**: `feat(deprovision): execute per provider`.

### Step 5 — Clear routing + archive (2h)

- `ClearRoutingActivity`: emits `TENANT.ROUTING.CHANGED { reason:
  "deprovision" }` → all nodes evict cache.
- `ArchiveTenantRowActivity`: `tenants.deleted_at = now()`; clears
  `provider_resource_ids` + `provider_key`.
- **Commit**: `feat(deprovision): clear routing + archive`.

### Step 6 — Notify (2h)

- `NotifyActivity`: inserts `platform_email_outbox` row with
  offboarding-summary template (deleted vs. retained).
- Template differs for BYO ("your data is retained on your side").
- **Commit**: `feat(deprovision): notify owner`.

### Step 7 — Workflow + idempotency (3h)

- `DeprovisionTenantWorkflow` composes activities.
- Each activity checks state + short-circuits if already complete.
- Re-run is a no-op once tenant is in `deleted` state.
- **Commit**: `feat(deprovision): workflow + idempotency`.

### Step 8 — Tests + runbook (4h)

- Per-backend tests with fake providers.
- Partial-fail test: Hetzner delete 500s → workflow fails,
  `orphanResources` logged, tenant in `Deprovisioning`.
- BYO test: customer resources untouched.
- Runbook.
- **Commit**: `test(deprovision): per-backend E2E + runbook`.

## 6. Test strategy

### Unit

- Confirmation tokens: issuance, expiry, replay protection.
- Each activity's idempotency check.

### Integration

- Per-backend happy path (fake providers).
- Partial-fail: orphans surfaced.
- BYO non-destructive verified.

## 7. Rollback plan

- **Feature flag**: not needed — deprovision is operator-triggered.
- **Failure mode**: orphans surfaced; operator manually cleans up
  via vendor UI, then re-runs workflow.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Confirmation tokens | 2 |
| 2. Freeze + drain | 3 |
| 3. Purge cabinet | 2 |
| 4. Execute | 3 |
| 5. Clear + archive | 2 |
| 6. Notify | 2 |
| 7. Workflow | 3 |
| 8. Tests + runbook | 4 |
| **Total** | **21** (brief 16; +5 for tests + confirmation tokens). |

## 9. Open questions

- **6-digit vs. UUID token**: 6-digit with 10-min expiry + 3/h rate
  limit gives sufficient entropy against brute force. Operators
  prefer 6-digit for readability.
- **Drain window 60s**: deterministic but may cut off very-long
  requests. Add "force proceed after 60s" warning event.
- **Self-service tenant delete**: not in this story. Operator only.
- **`deleted_at` retention**: indefinite for billing audit. Purge
  out of scope.
- **BYO "contact customer" flow**: the notify email for BYO explains
  customer's data is retained; does not prompt customer action.
