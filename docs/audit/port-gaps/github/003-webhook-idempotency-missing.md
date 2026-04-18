# Finding 003: Webhook idempotency on X-GitHub-Delivery header not enforced

**Scope**: github
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented (stub) — absent on both sides, but C# explicitly missing a safeguard that prod needs
**Estimated port effort**: 6-8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts` (entire handler)
- Contract/behavior: TS did **not** implement idempotency either — no table, no header read, no cache check. Tasks were enqueued unconditionally on every `issues` / `pull_request` / `push` delivery. The `enqueueWebhookTask` helper did copy `payload['delivery']` onto the inner payload (line 245-247) when present, but never used it as an idempotency key.

```typescript
// packages/api/src/routes/github/github-webhook.ts:244-247 (9e9a57c~1)
// Include optional payload fields
if (payload['delivery'] !== undefined) {
  taskInput.payload['delivery'] = payload['delivery'];
}
```

Notably, `X-GitHub-Delivery` (the header GitHub sends on every webhook, a UUID unique to each delivery attempt) was not read from request headers anywhere in the TS codebase; neither `request.headers['x-github-delivery']` nor `request.headers['X-GitHub-Delivery']` is referenced in the deleted routes.

- Dependencies: none (gap exists in TS too).
- Tests that exercised this: none — the gap was pre-existing.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:100-187`; `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:144-187`
- Contract/behavior: The C# webhook handler does not read `X-GitHub-Delivery`, does not persist it, and does not look up prior deliveries before enqueueing. `EnqueueDeferredEventAsync` always invokes `_taskQueue.EnqueueAsync`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:175-179 (current)
var task = await _taskQueue.EnqueueAsync(
    type: taskType,
    payloadJson: payload.GetRawText(),
    installationId: installationId,
    tenantIdOverride: tenantId);
```

A grep across the C# solution confirms zero references to `X-GitHub-Delivery`, `GitHubDelivery`, `WebhookDelivery`, or a `github_webhook_events` / `webhook_deliveries` table. There is no entity, no repository, and no migration for storing delivery IDs.

- Dependencies: `ITaskQueue.EnqueueAsync` is the single side-effect on the deferred path — it has no dedup parameter.
- Tests: `GitHubWebhookTaskQueueIntegrationTests.cs` asserts enqueue-on-first-delivery but does not exercise a retry scenario.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: enqueue on every delivery (also a gap).
- C# does: enqueue on every delivery.
- For a caller (GitHub itself) that retries a webhook delivery because of a transient network failure or a non-2xx response, GitHub's documented retry policy is: up to **5 attempts** with exponential backoff over ~8 hours per delivery ID, plus automatic redelivery from the "Advanced" tab. The `X-GitHub-Delivery` header is stable across attempts of the same delivery; this is GitHub's explicit idempotency signal. Neither system uses it.
- In production with existing data / deployed clients, this means: any webhook retry path (slow first response, intermittent 5xx from our side, manual redelivery by a dev) produces duplicate task-queue entries. For `issues.opened` this duplicates the autonomous-work trigger (two agents starting the same issue). For `pull_request.synchronize` this duplicates gate runs. For `push` on default branch this duplicates whatever downstream job consumes the event.

Error paths:
- TS error path: duplicate work; no de-duplication.
- C# error path: duplicate work; no de-duplication. Additionally, because `TaskQueue` is a persistent Postgres queue (not an in-memory ephemeral one), duplicates are durable and survive restarts.

Observability:
- Neither system logs `X-GitHub-Delivery`, so operators cannot correlate task-queue rows to GitHub's webhook delivery log when debugging. Even independent of idempotency enforcement, capturing the delivery ID into log scope would be a significant observability win.

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (Task 3)
- Story's acceptance criteria for this behavior: Not explicitly called out. AC #10 ("Error handling") alludes to retry, but the focus is on the UI/onboarding retry flow, not webhook redelivery semantics.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior (neither TS nor C# matches the story)
  - [x] No story — spec gap; must be backfilled before remediation

Spec gap: idempotency policy for GitHub webhooks must be added to story 18-4 (or a standalone Epic-19 followup). The audit summary notes this explicitly: "Not found in audited files — may have lived outside scope or was planned."

## 5. Status

- **Classification**: Not-yet-implemented (stub) — absent on both sides, elevated to a P2 finding because the C# port had the opportunity to fix it and did not, and because the Postgres-backed task queue makes duplicates more damaging than the in-memory TS queue.
- **What's needed to finish**:
  1. Decide policy: reject-duplicate (return 200 with `skipped:true`) vs best-effort-dedup-via-task-queue (dedup key on insert). Prefer the first — safer and matches GitHub's expectation.
  2. Add `github_webhook_events` table: `(delivery_id uuid PK, received_at timestamptz, event_type text, action text, installation_id bigint nullable)`. Index on `(received_at)` for TTL cleanup.
  3. Read `X-GitHub-Delivery` header in `GitHubEndpoints.Webhooks` after signature verification succeeds. If header missing → log warn, proceed (legacy deliveries). If present and row already exists → return 200 with `{received:true, skipped:true, reason:"duplicate_delivery"}`.
  4. Write row before dispatching; on dispatch failure delete the row so the retry re-attempts. (Alternative: write after-dispatch with ON CONFLICT DO NOTHING — simpler, acceptable.)
  5. Add a background cleanup job deleting rows older than 30 days (GitHub's retry window is ~8h; 30 days gives ample forensic buffer).
  6. Attach delivery_id to logging scope for the entire request lifetime.
- **Is it "just a stub" or is scope missing?** Scope missing. The TS team never wrote this; the C# port inherited the gap. Should be spec'd and implemented as a distinct piece of work.
- **Blockers**: Requires a migration; should coordinate with Finding 019 (github_webhook_events idempotency table absent) — they're the same artifact from different angles.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` — read delivery header, short-circuit on duplicate.
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — register new entity.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — optionally thread delivery ID into logging scope.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubWebhookDelivery.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IGitHubWebhookDeliveryRepository.cs` + impl
  - A new EF Core migration for the table.
  - Optional: a background `IHostedService` for TTL cleanup (or a scheduled Elsa workflow).
- Tests to add:
  - `GitHubEndpointsIntegrationTests.Webhook_DuplicateDelivery_Returns200Skipped` — post same delivery ID twice, assert enqueue happens once.
  - `GitHubEndpointsIntegrationTests.Webhook_MissingDeliveryHeader_LogsWarnAndProceeds` — assert backward-compat with deliveries that lack the header.
  - `GitHubEndpointsIntegrationTests.Webhook_DispatchFails_DeliveryRowRolledBack` — force enqueue to throw; assert the delivery row is not persisted, so retry works.
- Estimated effort: 6-8h broken down as:
  - Schema + entity + repo + migration: 2h
  - Endpoint integration + header plumbing: 1.5h
  - Tests (3 scenarios): 1.5h
  - TTL cleanup job + logging scope: 1-3h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:244-247` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:100-187`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:144-187`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (AC lacks idempotency clause)
- Related findings: `docs/audit/port-gaps/github/019-github-webhook-events-table-missing.md` (same table from schema angle), `docs/audit/port-gaps/github/017-webhook-route-no-rate-limit-plugin.md`
