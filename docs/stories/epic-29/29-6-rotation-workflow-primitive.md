# Story 29-6: Rotation Workflow Primitive (Generic Elsa Activity Set)

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want a generic `RotateSecretWorkflow` in Elsa that orchestrates the saga shape from the research notes — mint → push → probe → activate → retire-old — with named compensation for each step and a `IRotationHandler` plug-in per consumer type,
so that DB-password rotation (Story 29-7), Cranl env-var rotation (Story 29-8), and future consumer types (OIDC client secret, OAuth app secret, SMTP password, custom webhook HMAC) all share one audited workflow rather than each inventing its own rollback logic.

## Acceptance Criteria

1. A new Elsa workflow `RotateSecretWorkflow` is registered in `Tamma.Activities/Secrets/`. Input: `{ secretId, scope, tenantId?, newPlaintext? | generateLength?, rotationCorrelationId }`. Output: `{ result: "activated" | "compensated" | "failed", oldVersion, newVersion, error? }`.
2. The workflow uses the following activity sequence, each with a compensation:
   - `MintPendingVersionActivity` — mint new version in store, status `Pending`. Compensation: `DeleteVersionActivity`.
   - `ResolveHandlerActivity` — select the `IRotationHandler` registered for the secret's `ConsumerRefs[0].System` (`postgres`, `cranl`, `hmac`, etc.).
   - `PushNewValueActivity` — invoke the handler's `PushAsync(secret, newPlaintext, ct)`. Handler-specific idempotency. Compensation: `RollbackPushActivity` → handler's `RollbackAsync(secret, newPlaintext, ct)`.
   - `ProbeActivity` — invoke handler's `ProbeAsync(secret, ct)`; retries 3× with exponential backoff (5s, 15s, 45s).
   - `ActivateNewVersionActivity` — flip new version to `Active`, flip old version to `RetiredGrace`. Compensation: revert statuses.
   - `ScheduleRetireOldActivity` — enqueue a `platform_queued_task` to fully retire the old version after the grace window (default 15 min; configurable per secret).
3. `IRotationHandler` port:
   ```csharp
   interface IRotationHandler {
     string System { get; }                // "postgres" | "cranl" | "hmac" | "generic-http"
     Task PushAsync(SecretMetadata secret, byte[] newPlaintext, RotationContext ctx, CancellationToken ct);
     Task<ProbeResult> ProbeAsync(SecretMetadata secret, RotationContext ctx, CancellationToken ct);
     Task RollbackAsync(SecretMetadata secret, byte[] newPlaintext, RotationContext ctx, CancellationToken ct);
   }
   ```
4. A fallback `GenericHttpRotationHandler` handles the case where a secret's consumer has no specialized handler yet: it POSTs the new value to an operator-configured webhook URL (HMAC-signed by the previous version) and probes a health-check URL. Covers "any consumer with an HTTP endpoint" without forcing a dedicated handler per system.
5. All activities emit `SECRET.ROTATE.<STEP>.<OUTCOME>` events via `ISecretAccessAuditor` with the rotation correlation id as a tag. Emits minimum: `STARTED`, `PUSH.SUCCESS|FAILED`, `PROBE.SUCCESS|FAILED`, `ACTIVATED`, `COMPENSATION.STARTED|SUCCESS|FAILED`, `COMPLETED`.
6. Retry / compensation semantics match the research notes §3: push retries 3× with exponential backoff; probe retries 3×; if compensation itself fails, emit `SECRET.ROTATION.COMPENSATION.FAILED` + alert (via 29-4's audit feed) and halt — operator must manually intervene.
7. A workflow-test-in-memory fixture proves the full success path (new version activated, old retired) and the compensation path (push succeeds, probe fails — push rolled back, new version deleted, old still `Active`).
8. Grace window sweeper runs on `TaskQueueProcessor` and retires `RetiredGrace` versions older than their grace window to `Revoked`. Emits `SECRET.VERSION.RETIRED`. Idempotent — running twice on the same row is a no-op.
9. `IRotationHandler` implementations live in the same `Services/Secrets/Handlers/` folder; the rotation workflow resolves them via DI keyed by `System`. Registering a new handler is one `.AddKeyedSingleton<IRotationHandler, ...>()` call.
10. Reuse check: Epic 1.5-30 `RotationCascadeWorkflow` and 1.5-29 `IRotationHandler` have overlapping scope. This story re-uses 1.5-29's handler contract (same method signatures) and names them identically so a future merge pass can consolidate. Note in the implementation plan: if 1.5-29 has already shipped when this story starts, import its interface instead of creating a duplicate.

## Technical Context

### Why Elsa not a .NET-native Polly saga

Rotation is long-running (push → probe with 3× backoff can take
minutes), needs durable state (crash recovery), and must be visible in
the same workflow UI operators already use. Elsa gives us all three
for free. .NET-native sagas (Polly + MediatR) would need custom
persistence.

### Grace window semantics

Old `Active` version becomes `RetiredGrace` immediately on rotation
success. It remains **readable** by the rotation handler's in-process
codepath (so an in-flight request using the old connection string
finishes), but is **not** returned as the current version by `ISecretStore.GetAsync`.

At grace window expiry (default 15 min, configurable per secret via
`RotationGraceWindowMinutes` on the metadata), the sweeper flips the
version to `Revoked` and (for handlers that support it) calls
`RevokeOldAsync` on the handler — e.g. Postgres `REVOKE` on the old
role password if the handler rotated it in-place.

### Event shape

```json
{
  "type": "SECRET.ROTATE.ACTIVATED",
  "tags": { "secretId": "...", "rotationCorrelationId": "rot_abc123", "tenantId": "..." },
  "data": { "oldVersion": 2, "newVersion": 3, "durationMs": 7543 }
}
```

## Estimated hours

16 — workflow + 6 activities + handler contract + generic HTTP handler
+ sweeper + tests.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Activities/Secrets/` (new folder)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/` (new folder)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/RotationSweeper.cs` (new)

## References

- Research notes §3 (saga pattern + compensation)
- Epic 1.5-29 `IRotationHandler` shape: `docs/stories/epic-1.5/story-1.5-29/` (when authored)
- Epic 1.5-30 `RotationCascadeWorkflow`: same
- [Temporal — Saga Pattern in Microservices](https://temporal.io/blog/mastering-saga-patterns-for-distributed-transactions-in-microservices)
