# Completeness Audit — `RotateSecretWorkflow` (rotate-secret)

**Date:** 2026-06-22
**Auditor mode:** completeness / maturity (NOT bug-hunting)
**Verdict:** **PARTIAL** — the saga body is genuinely production-grade; the operational edges (workflow trigger/dispatch, retire-sweeper wiring, dry-run preview, alert-channel completeness) are missing, so end-to-end the feature does not yet complete a rotation on its own.

---

## Purpose & owner

- **Purpose:** Generic, audited secret-rotation saga — `mint → push → probe → activate → schedule-retire` — with named compensation per step and a per-consumer `IRotationHandler` plug-in (postgres / cranl / hmac / generic-http). One audited workflow so DB-password (29-7), Cranl env-var (29-8), and future consumer types (OIDC/OAuth/SMTP/HMAC webhook) all share one rollback path.
- **Owning epic/story:** Epic 29 (Secret Store), **Story 29-6 "Rotation Workflow Primitive"**. Consumers: 29-7 (postgres handler), 29-8 (cranl handler), 29-4 (admin UI / timeline), Wave-C alerts.
- **Files:**
  - `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RotateSecretWorkflow.cs` (the Elsa workflow shell)
  - `apps/tamma-elsa/src/Tamma.Activities/SecretsRotation/Activities/RotateSecretSagaActivity.cs` (the real engine — `SagaRunner`)
  - `apps/tamma-elsa/src/Tamma.Activities/SecretsRotation/Activities/{RotationActivityBase,RotationWorkflowState,ScheduleRetireOldActivity}.cs`
  - `apps/tamma-elsa/src/Tamma.Activities/SecretsRotation/Contracts/*` (ports: gateway, handler, audit emitter, registry, retire scheduler, context, target)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Rotation/*` (concrete: `SecretStoreRotationGateway`, `RetireScheduler`, `RotationAuditEmitter`, `KeyedRotationHandlerRegistry`, DI ext)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/*` (postgres / cranl / generic-http handlers)

---

## Maturity: **PARTIAL**

This is NOT the user's "thin happy-path skeleton" complaint (cf. PullRequest). The saga is one of the more complete workflows in the repo:

- All 5 saga steps present, **each with the correct compensation edge**.
- Push + probe **retry with exponential backoff (5s/15s/45s)** exactly per AC2/AC6.
- A full **compensation path** (rollback push while old version still Active → delete pending version) with its own `COMPENSATION.STARTED/SUCCESS/FAILED` events and a non-rethrowing terminal `compensation-failed` outcome (AC6).
- ~16 **DCB audit event types** emitted via `IRotationAuditEmitter` (STARTED, STAGED, PUSH/PROBE SUCCESS/FAILED, SWITCHED, ACTIVATED, COMPLETED, FAILED, COMPENSATION.*, RETIRE_SCHEDULED, VERSION.RETIRED).
- **Tenant scoping** flows through (`Snapshot.TenantId` on every event; null ⇒ platform scope).
- **No silent-failure / no false-success:** secret-not-found and handler-not-registered short-circuit to `failed`; the terminal label is honest (`activated | compensated | failed`).
- Idempotency by design (`rotationCorrelationId` threaded through gateway + handler ports).
- Real unit coverage: `SagaRunnerTests`, `SagaRunnerAlertEmissionTests`, `RotationContractTests`, `RotateSecretWorkflowStructureTests`.

It is **partial, not complete**, because the rotation cannot run end-to-end in production: nothing *starts* the workflow, and the *retire* half of the saga (AC8) is enqueued but never drained.

---

## Current capabilities (what it actually does today)

1. **Input bind/validate** (`InitInputs` SetVariable): coerces `secretId` (Guid|string), requires `rotationCorrelationId`, accepts `newPlaintext? | generateLength?`, `operatorUserId?`, `graceWindowSeconds?`. Hard-throws on missing `secretId`/`rotationCorrelationId` (correct fail-fast).
2. **Step 1 — mint:** `GetSnapshotAsync` (→ `secret_not_found` ⇒ failed); resolve handler by `ConsumerSystem` with `generic-http` fallback (→ `handler_not_registered` ⇒ failed); generate CSPRNG plaintext when none supplied (16–256 byte clamp, base64url); `MintPendingVersionAsync` (→ `mint_failed` ⇒ failed). Emits STARTED + STAGED.
3. **Step 3 — push (retry):** `handler.PushAsync` up to 4 attempts (5/15/45s). On exhaustion ⇒ `CompensateAsync` ⇒ outcome `compensated`. Emits PUSH.SUCCESS/PUSH.FAILED.
4. **Step 4 — probe (retry):** `handler.ProbeAsync` up to 4 attempts; unhealthy on exhaustion ⇒ `CompensateAsync` ⇒ `compensated`. Emits PROBE.SUCCESS/PROBE.FAILED.
5. **Step 5 — activate:** `ActivateVersionAsync` (Pending→Active, prev Active→RetiredGrace). Throw ⇒ compensate. Emits SWITCHED + ACTIVATED.
6. **Step 6 — schedule retire:** if a previous version exists, `IRetireScheduler.ScheduleRetireAsync` enqueues a `RETIRE_SECRET_VERSION` `platform_queued_tasks` row with `runAfter = now + grace` (default 900s). Scheduling failure is logged + RETIRE_SCHEDULED(`schedule_failed`) but does **not** unwind (correct — activation already happened). First rotation ⇒ RETIRE_SCHEDULED(`no_previous_version`). Emits COMPLETED.
7. **Compensation:** rollback push (only if `Pushed`), delete pending version (if minted); terminal `compensation-failed` does not rethrow.
8. **Alert fan-out (optional):** when `IAlertEventEmitter` is wired, emits Wave-C `SECRET.ROTATION.FAILED` with `{targetKind, cabinetName, handlerType, failureStage, compensationApplied, lastError}` on every terminal failure stage.
9. **Outputs:** `Result`, `NewVersionNumber`, `OldVersionNumber`, `Error`.
10. **Registration:** discoverable via Elsa assembly sweep (`AddWorkflowsFrom<LlmCallWorkflow>` — same `Tamma.ElsaServer` assembly). DI ports + 3 handlers registered via `AddTammaSecretRotation`.

---

## Intended full scope (with citations)

From **`docs/stories/epic-29/29-6-rotation-workflow-primitive.md`**:

- **AC1** — Elsa workflow with input `{secretId, scope, tenantId?, newPlaintext?|generateLength?, rotationCorrelationId}`, output `{result, oldVersion, newVersion, error?}`. *(Note: spec lists `scope`/`tenantId` as inputs; impl derives tenant from the snapshot — defensible, but `scope` is unmodelled.)*
- **AC2** — mint → resolve-handler → push → probe → activate → schedule-retire-old, **each with compensation**; grace default 15 min, per-secret configurable. ✔ present.
- **AC3** — `IRotationHandler { System; PushAsync; ProbeAsync; RollbackAsync }`. ✔ present (+ optional `RevokeOldAsync`).
- **AC4** — fallback `GenericHttpRotationHandler` (HMAC-signed webhook POST + health probe). ✔ present.
- **AC5** — emit `SECRET.ROTATE.<STEP>.<OUTCOME>` events with correlation-id tag; min set STARTED/PUSH/PROBE/ACTIVATED/COMPENSATION/COMPLETED. ✔ present (event names use `SECRET.ROTATION.*` — internally consistent, but **diverge from the AC5/AC-event-shape `SECRET.ROTATE.*` spelling** in §"Event shape").
- **AC6** — push 3× backoff, probe 3×, compensation-failed ⇒ emit `COMPENSATION.FAILED` + alert + halt for manual intervention. ✔ present.
- **AC7** — in-memory workflow test proves success path (activated + old retired) **and** compensation path. *Partial:* `SagaRunner` success/compensation paths are unit-tested, but **"old retired" is not exercised end-to-end** because the sweeper is unwired (see Missing).
- **AC8** — **grace-window sweeper runs on `TaskQueueProcessor`**, retires `RetiredGrace`→`Revoked`, emits `SECRET.VERSION.RETIRED`, idempotent. **❌ NOT wired** — see Missing #1.
- **AC9** — handlers resolved via keyed DI; new handler = one `.AddKeyedSingleton`. ✔ present.
- **AC10** — reuse 1.5-29 `IRotationHandler` shape. ✔ (matching contract).

**Domain best-practice (production-complete rotation saga):** a trigger surface (operator endpoint + scheduled auto-rotation), a working retire/revoke tail, dry-run "preview rotation" (AC9 of 29-4/29-7 references `RotationContext.DryRun`), concurrency guard (no two rotations for the same secret in flight), and durable resumption on crash mid-saga.

---

## Missing capabilities (gap to complete)

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | **Retire-sweeper not wired to any worker.** `RETIRE_SECRET_VERSION` rows are enqueued by `ScheduleRetireAsync` but there is **no `IPlatformTaskHandler` registered for that type** (only MoveTenant / ProvisionTenantV2 / CreateBillingCustomer exist) **and** `IRetireScheduler.SweepDueRetireTasksAsync` is **never called** by any hosted service/scheduler. Old versions stay in `RetiredGrace` forever; AC8 unmet; `SECRET.VERSION.RETIRED` never fires in prod. | **P0** | Story 29-6 (AC8) |
| 2 | **No trigger / dispatch surface.** Nothing in the codebase starts the `rotate-secret` workflow — no admin endpoint, no scheduled auto-rotation, no service `DispatchWorkflow("rotate-secret")`. The saga is dead unless invoked manually via Elsa Studio. | **P0** | Story 29-4 (admin UI) / 29-6 |
| 3 | **No per-secret concurrency guard.** Two overlapping rotations on the same `secretId` (e.g. operator click + scheduled run) both mint/push — version-number races and double-push. Idempotency keys are per-correlation, not per-secret. | P1 | none |
| 4 | **Dry-run "preview rotation" path not surfaced.** `RotationContext.DryRun` exists and all handlers honor it, but the saga hard-codes `DryRun: false` and no caller can request a preview (referenced by 29-4/29-7 admin UI AC9). | P1 | Story 29-4 |
| 5 | **`scope` input unmodelled.** AC1 lists `scope` (platform vs tenant) as an input; the workflow derives tenant from the snapshot only. Acceptable today, but platform/tenant scope is not an explicit, validated input. | P2 | Story 29-6 (AC1) |
| 6 | **Event-name spelling divergence.** AC5 + the §"Event shape" example specify `SECRET.ROTATE.*`; impl emits `SECRET.ROTATION.*`. Internally consistent (alerts/dashboards match the constants) but breaks any consumer keyed off the spec'd `SECRET.ROTATE.*` names. | P2 | Story 29-6 (AC5) — decide canonical |
| 7 | **`durationMs` not on terminal events.** Spec event-shape carries `durationMs`; only probe emits a duration. ACTIVATED/COMPLETED omit total saga duration (analytics/SLO signal). | P3 | none |
| 8 | **No alert on success-with-degraded-retire.** When `ScheduleRetireAsync` fails post-activation, the saga emits RETIRE_SCHEDULED(`schedule_failed`) and warns, but fires **no alert** — an old credential silently never retires. | P2 | Epic 38 (alert mediation) / Wave-C |
| 9 | **`RevertActivationAsync` is dead/un-exercised.** Gateway + state flag exist for "compensate after a successful activate," but no post-activate step can fail into it (retire-schedule failure is non-fatal). Either remove or wire a post-activate verification step that can trigger it. | P3 | none |
| 10 | **No durable end-to-end resumption test.** AC7's "old retired" leg is untested end-to-end (blocked by #1). Once the sweeper is wired, add an integration test that activates then drains the retire task. | P1 | depends on #1 |

*Note: this audit is read-only on code (per instructions) — no `.cs` files were modified.*

---

## Ordered build-out spec (to reach complete & robust)

> Honor project rules: tenant→system→error (never empty/plain fallback), no silent-failure/false-success, steps never call external providers directly, emit DCB audit events.

1. **[P0] Wire the retire tail (AC8).** Implement `RetireSecretVersionTaskHandler : IPlatformTaskHandler` with `Type = "RETIRE_SECRET_VERSION"`, registered in `PlatformTaskServiceCollectionExtensions`. On `HandleAsync`: parse the `RetireTaskPayload`; if `runAfter > now` throw a *retryable* failure (re-queues, not dead-letter); else fetch old plaintext → `gateway.RetireVersionAsync` → resolve handler → `handler.RevokeOldAsync` (best-effort, log+continue on throw) → emit `SECRET.VERSION.RETIRED` (`{taskId, versionNumber}`). Reuse the existing `RetireScheduler.SweepDueRetireTasksAsync` body. **Failure edges:** malformed payload ⇒ dead-letter (`malformed_payload`); retire throw ⇒ `FailAsync(maxRetries:3)`; idempotent on already-`Revoked`. *(Alternative: register `SweepDueRetireTasksAsync` as a periodic hosted service — but the per-task `IPlatformTaskHandler` route is the AC8-specified `TaskQueueProcessor` path.)*

2. **[P0] Add a trigger surface.**
   - **Operator endpoint:** `POST /api/v1/secrets/{secretId}/rotate` (platform-owner / tenant-admin per scope) → mint a fresh `rotationCorrelationId`, dispatch `rotate-secret` with `{secretId, operatorUserId, generateLength|newPlaintext, graceWindowSeconds}`. Return `202` + correlation id; emit `SECRET.ROTATION.REQUESTED`.
   - **Scheduled auto-rotation:** a cron-style hosted service (mirror `HourlyAnalyticsRollupScheduler`) that selects secrets whose `RotationIntervalDays` has elapsed and dispatches the workflow with `operatorUserId = Guid.Empty`. **No empty/plain fallback** — if a secret has no consumer/handler, emit `SECRET.ROTATION.FAILED(handler_not_registered)` (already handled by the saga) rather than skipping silently.

3. **[P1] Per-secret concurrency guard.** Before mint, take an advisory lock / status check on the secret (`gateway.TryBeginRotationAsync(secretId, correlationId)`): if a rotation is already in flight, short-circuit with terminal `failed`/`rotation_in_progress` + emit `SECRET.ROTATION.REJECTED(rotation_in_progress)` — do NOT proceed. Release on terminal outcome.

4. **[P1] Dry-run preview path.** Add `bool DryRun` workflow input → thread into `BuildRotationContext`. In dry-run: run mint-generate + handler `PushAsync`/`ProbeAsync` (handlers already early-return on `ctx.DryRun`) but **do not** `ActivateVersionAsync` / `ScheduleRetireAsync`; emit `SECRET.ROTATION.PREVIEW.{OK|FAILED}` and return without mutating active state. Surface via `?dryRun=true` on the rotate endpoint (29-4 admin "preview" button).

5. **[P1] End-to-end retire test (AC7 completion).** Integration test: dispatch real workflow against an in-memory gateway + a due retire task, assert old version → `Revoked` and `SECRET.VERSION.RETIRED` emitted. Add a crash-mid-push resumption test to prove durable replay converges (handler idempotency).

6. **[P2] Reconcile event naming (AC5).** Decide canonical spelling. Either rename constants to `SECRET.ROTATE.*` to match the spec, or amend 29-6 AC5/§Event-shape to `SECRET.ROTATION.*` (the impl's choice) — and update the 3 specs (29-4 timeline, Wave-C alert rules) to the single chosen form. Add an ADR note so future consumers key off one set.

7. **[P2] Degraded-retire alert.** On `ScheduleRetireAsync` failure post-activation, in addition to the warn + RETIRE_SCHEDULED(`schedule_failed`), call `_alertEmitter.EmitSecretRotationFailedAsync(..., FailureStage:"retire", CompensationApplied:false)` (or a dedicated `SECRET.ROTATION.RETIRE.DEGRADED` alert) so operators are paged that an old credential will not auto-retire.

8. **[P2] Model `scope` input (AC1).** Add explicit `scope: "platform" | "tenant"` workflow input; validate it against `Snapshot.TenantId` (tenant scope ⇒ TenantId non-null; platform ⇒ null) and fail fast on mismatch (`scope_mismatch`) — closes the AC1 input contract.

9. **[P3] Total-duration telemetry.** Capture `started = now` at saga entry; include `durationMs` on ACTIVATED/COMPLETED/FAILED `data` to match the spec event-shape and feed SLO dashboards.

10. **[P3] Resolve `RevertActivationAsync` dead code.** Either delete the gateway method + `Activated` flag, OR add a post-activate verification step (re-probe / consumer health re-check) whose failure invokes `RevertActivationAsync` + `RollbackAsync` — making the activate-compensation edge real rather than latent.

---

## Bottom line

The `RotateSecretWorkflow` **saga core is complete and robust** (proper compensation, retries, honest terminal states, full DCB audit trail, tenant scoping). It is rated **PARTIAL** only because two **P0** operational gaps stop it completing a rotation in production: **(1)** the retire-sweeper is enqueued but never drained (AC8 unmet — old versions never reach `Revoked`), and **(2)** there is **no trigger** that starts the workflow. Close those two and the remaining items are polish/hardening.
