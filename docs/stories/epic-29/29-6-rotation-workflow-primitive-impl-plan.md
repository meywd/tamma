# Story 29-6 Implementation Plan — Rotation Workflow Primitive

**Status**: Planned (2026-04-20)
**Story brief**: [`29-6-rotation-workflow-primitive.md`](./29-6-rotation-workflow-primitive.md)
**Epic 29 phase**: Rotation — after 29-2; parallel with 29-3.
**Branch**: `feat/story-29-6-rotation-workflow`

---

## 1. Objective

Ship a generic `RotateSecretWorkflow` in Elsa that orchestrates the
saga: mint → push → probe → activate → retire-old. Each step has
named compensation. An `IRotationHandler` plug-in per consumer type
encapsulates the system-specific push/probe/rollback. Ships a
fallback `GenericHttpRotationHandler` for consumers without a
dedicated handler.

## 2. Dependencies

Hard blockers:

- **Story 29-2** — store + backend.
- **Story 28-5 / 28-6** — `platform_queued_tasks` for grace-window scheduling.

Soft:

- **Epic 1.5-29** — if shipped, import `IRotationHandler` to avoid
  duplicate contract.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/Secrets/RotateSecretWorkflow.cs` | Master workflow. |
| `.../Secrets/Activities/MintPendingVersionActivity.cs` | Step 1. |
| `.../Secrets/Activities/ResolveHandlerActivity.cs` | Step 2. |
| `.../Secrets/Activities/PushNewValueActivity.cs` | Step 3. |
| `.../Secrets/Activities/ProbeActivity.cs` | Step 4. |
| `.../Secrets/Activities/ActivateNewVersionActivity.cs` | Step 5. |
| `.../Secrets/Activities/ScheduleRetireOldActivity.cs` | Step 6. |
| `.../Secrets/Activities/DeleteVersionActivity.cs` | Compensation for step 1. |
| `.../Secrets/Activities/RollbackPushActivity.cs` | Compensation for step 3. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/IRotationHandler.cs` | Contract (shared with Epic 1.5 if exists). |
| `.../Services/Secrets/Handlers/GenericHttpRotationHandler.cs` | Fallback handler. |
| `.../Services/Secrets/RotationSweeper.cs` | Grace-window retire sweeper. |
| `.../Services/Secrets/RotationContext.cs` | Context passed to handlers. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/RotateSecretWorkflowTests.cs` | Happy + compensation + probe-retry. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/RotationSweeperTests.cs` | Grace window. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Register workflow + activities + keyed handlers. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs` | `POST /secrets/{id}/rotate` dispatches the workflow. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretVersion.cs` | Add `Status` transitions: `Pending → Active → RetiredGrace → Revoked`. |

## 5. Sequence of changes

### Step 1 — Handler contract + context (2h)

- `IRotationHandler` with `System`, `PushAsync`, `ProbeAsync`,
  `RollbackAsync`.
- `RotationContext { SecretId, TenantId?, RotationCorrelationId, CranlApiKey?, AdminConnectionString? }`.
- `ProbeResult` discriminated: `Healthy` | `Unhealthy(reason)`.
- **Commit**: `feat(secrets): rotation handler contract`.

### Step 2 — Mint + delete activities (2h)

- `MintPendingVersionActivity` — adds new version status=Pending.
- `DeleteVersionActivity` — compensation; hard-deletes row.
- **Commit**: `feat(secrets): mint/delete version activities`.

### Step 3 — Resolve + push + rollback (3h)

- `ResolveHandlerActivity` — keyed DI resolution by `secret.ConsumerRefs[0].System`.
- `PushNewValueActivity` — invokes handler.PushAsync; 3× retry with
  5s/15s/45s backoff.
- `RollbackPushActivity` — invokes handler.RollbackAsync.
- **Commit**: `feat(secrets): resolve + push + rollback activities`.

### Step 4 — Probe + activate (2h)

- `ProbeActivity` — 3× retry.
- `ActivateNewVersionActivity` — Pending → Active, old Active → RetiredGrace.
- **Commit**: `feat(secrets): probe + activate activities`.

### Step 5 — Schedule retire (2h)

- `ScheduleRetireOldActivity` — enqueues
  `platform_queued_tasks` row with `RunAfter=now + grace` and
  `TaskType='RETIRE_SECRET_VERSION'`.
- **Commit**: `feat(secrets): schedule retire activity`.

### Step 6 — Sweeper (1h)

- Task-queue consumer for `RETIRE_SECRET_VERSION`: flips
  `RetiredGrace` → `Revoked`; calls `handler.RevokeOldAsync` if
  supported.
- Idempotent on status check.
- **Commit**: `feat(secrets): rotation sweeper`.

### Step 7 — Generic HTTP handler (2h)

- `GenericHttpRotationHandler`: POSTs to operator-configured
  webhook URL, HMAC-signed with previous version; probe
  health-check URL; rollback by POSTing the old value.
- **Commit**: `feat(secrets): generic HTTP rotation handler`.

### Step 8 — Workflow + events (2h)

- `RotateSecretWorkflow` composes activities with compensation.
- Emits `SECRET.ROTATE.*` per brief AC5.
- **Commit**: `feat(secrets): RotateSecretWorkflow`.

## 6. Test strategy

### Unit

- Each activity's happy + failure + compensation.
- `GenericHttpRotationHandler` with mocked HTTP.

### Integration

- Full workflow happy path: state transitions correct, events fire.
- Compensation path: probe fails → rollback, new version deleted,
  old still Active.
- Sweeper retires after grace window.

## 7. Rollback plan

- **Feature flag**: `Secrets:RotationEnabled=true`. Off disables
  rotate endpoint.
- **Compensation failure**: emits alert event; operator intervenes.
  Runbook documents recovery.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Handler contract | 2 |
| 2. Mint/delete | 2 |
| 3. Resolve/push/rollback | 3 |
| 4. Probe/activate | 2 |
| 5. Schedule retire | 2 |
| 6. Sweeper | 1 |
| 7. Generic HTTP handler | 2 |
| 8. Workflow + events | 2 |
| **Total** | **16** (matches brief). |

## 9. Open questions

- **1.5-29 contract reuse**: if Epic 1.5-29 has shipped by the time
  29-6 starts, import the interface. If not, ship one here with
  identical method signatures so consolidation is trivial.
- **Backoff constants**: 5/15/45s for push; 3× retries. Configurable
  per-handler.
- **Retry on probe vs. rollback**: current design retries probe 3×
  before rolling back. Some consumers (slow DBs) may need longer;
  add `ProbeRetryCount` to `RotationOptions` per-secret.
- **RotationContext tenant resolution**: activity resolves tenantId
  from the workflow input, not from the client — prevents tenant
  tampering.
- **Sweeper concurrency**: one worker per tenant? Plan: global
  concurrency=4 (reuses 28-6 dispatcher).
