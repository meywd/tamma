# Finding 002: Webhook event dispatch parity across 5 event types (positive finding)

**Scope**: github
**Severity**: None (positive finding — ported correctly)
**Status**: Ported faithfully
**Estimated port effort**: 0h (no remediation)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:113-121`
- Contract/behavior: Branch on `X-GitHub-Event` header. Five event types are actively handled: `installation` → `handleInstallationEvent`; `installation_repositories` → `handleInstallationRepositoriesEvent`; `issues`, `pull_request`, `push` → `enqueueWebhookTask` (deferred via task queue). All other event types are silently ignored (no 400, no error — they fall through the `if/else if` chain and the handler returns `200 {ok:true}`).

```typescript
// packages/api/src/routes/github/github-webhook.ts:113-121 (9e9a57c~1)
try {
  if (event === 'installation') {
    await handleInstallationEvent(payload, options, installationId);
  } else if (event === 'installation_repositories') {
    await handleInstallationRepositoriesEvent(payload, options, installationId);
  } else if (event === 'issues' || event === 'pull_request' || event === 'push') {
    // Enqueue a task for actionable webhook events
    await enqueueWebhookTask(event, payload, options, installationId);
  }
} catch (err) {
  app.log.error({ msg: 'Failed to process webhook', event, installationId, error: err });
  return reply.status(500).send({ error: 'Internal error processing webhook' });
}
```

For the deferred events the TS form of the task-type slug is `github.${event}.${action}` (see `enqueueWebhookTask` at `:229-236`) with the `installationId` attached both as the task envelope's `installationId` and as a key on the inner payload.

- Dependencies: `IGitHubInstallationStore` (for the installation-lifecycle branch), `ITaskQueue` (for the deferred branch), `InstallationRouter` (only consulted on `installation.deleted` / suspend / unsuspend for cache invalidation).
- Tests that exercised this: integration-level webhook tests covered all five event types; the task queue payload shape was asserted against `type: 'github.issues.opened'` etc.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:111-136`
- Contract/behavior: The top-level dispatch in `HandleWebhookAsync` branches on `eventType` with the identical five cases. The `default` arm logs a debug message and returns `WebhookResult(... Skipped: true)`, which is semantically equivalent to the TS "silently ignore" (the HTTP response is `200 {received:true,skipped:true}` instead of `200 {ok:true}` — a minor shape drift — but the dispatch topology matches).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:111-136 (current)
public async Task<WebhookResult> HandleWebhookAsync(string eventType, JsonElement payload)
{
    var action = TryGetString(payload, "action");

    switch (eventType)
    {
        case "installation":
            return await HandleInstallationEventAsync(payload, action);

        case "installation_repositories":
            return await HandleInstallationRepositoriesEventAsync(payload, action);

        // Deferred events — enqueue for async processing so the webhook
        // handler can return quickly. Ported from the TS queueing path.
        case "push":
        case "issues":
        case "pull_request":
            return await EnqueueDeferredEventAsync(eventType, action, payload);

        default:
            _logger.LogDebug(
                "Webhook event {Event} (action={Action}) skipped (not handled)",
                Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action));
            return new WebhookResult(eventType, action, Skipped: true);
    }
}
```

Task-type slug construction is at `InstallationRouterService.cs:171-173` and matches the TS form `github.${eventType}.${action}` with a fallback to `github.${eventType}` when action is empty (TS used the literal string `'unknown'` — a small drift worth calling out but not a severity).

- Dependencies: `ITaskQueue?` (nullable, `EnqueueDeferredEventAsync` degrades to `skipped=true` when not registered — line 147-153); `IInstallationRepository`; `IEventRepository`; `ITenantRepository`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/GitHub/InstallationRouterServiceTests.cs` covers all five event types with representative payloads. `GitHubWebhookTaskQueueIntegrationTests.cs` asserts the enqueue path end-to-end.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: handle 5 event types (`installation`, `installation_repositories`, `issues`, `pull_request`, `push`); silently drop unknown events.
- C# does: handle the same 5 event types; log at debug and return `skipped:true` for unknown events.
- For a caller sending a webhook with `X-GitHub-Event: installation` and a payload containing `action: created`, TS calls `installationStore.upsertInstallation` + seeds repos, emits no domain events. C# calls `_installations.UpsertAsync` + seeds repos, **and additionally emits `INSTALLATION.CREATED.SUCCESS`** (see Finding 006 for the gap on what's still missing in the `created` path — repo fetching, API key generation).
- In production with existing data / deployed clients, this means: the dispatch topology is reliable, but the per-branch semantics differ (documented in Findings 004-006 for the installation branch, Finding 003 for idempotency).

Error paths:
- TS error path: handler throws → 500 `{"error":"Internal error processing webhook"}`.
- C# error path: handler throws → 500 via `Results.Problem("Internal error processing webhook", ...)` at `GitHubEndpoints.cs:183-185`.

Response shape drift:
- TS success: `200 {ok:true}`.
- C# success: `200 {received:true, event, action, skipped, queued?, taskId?}` — richer, backward-compatible superset for any consumer that only reads 2xx status.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: Task 3 "Update webhook handler for org-scoped installations" specifies handling `installation.created`, `installation.deleted`, and the `installation_repositories` variants. Issues/PR/push enqueue behavior predates Epic 18 and is inherited from the original architecture.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — actually C# matches
  - [ ] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

The dispatch surface itself is ported cleanly. File this finding as documentation-positive: future refactors should preserve this 5-event contract.

## 5. Status

- **Classification**: Ported faithfully (positive finding).
- **What's needed to finish**: Nothing — this documents parity. However, two follow-ups that should NOT be rolled into remediation of this finding but are adjacent:
  1. Align success response shape (Finding 018 could track response-contract drift if desired).
  2. Align unknown-event telemetry: TS logged only at trace level; C# logs at debug plus returns `skipped:true` in the response body, which may surface to GitHub's webhook delivery log.
- **Is it "just a stub" or is scope missing?** Neither — this is a correctly ported surface, and the finding exists so auditors can confirm parity rather than re-investigate.
- **Blockers**: None.

## Remediation

- Files to modify: none (intentionally).
- Files to create: none.
- Tests to add: regression tests that pin the 5-event set so accidental removal during future refactors is caught:
  - `InstallationRouterServiceTests.HandleWebhook_UnknownEvent_ReturnsSkipped` — post `X-GitHub-Event: star` and assert `Skipped=true, TaskId=null`.
  - Five dedicated tests (one per known event type) asserting the branch taken — several already exist in `InstallationRouterServiceTests.cs`; audit and ensure each covers the `action` routing.
- Estimated effort: 0h (no remediation required). Optional test hardening: 1h.

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:113-121` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:111-136`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (Task 3)
- Related findings:
  - `docs/audit/port-gaps/github/003-webhook-idempotency-missing.md`
  - `docs/audit/port-gaps/github/004-installation-deleted-soft-vs-hard.md`
  - `docs/audit/port-gaps/github/005-no-cache-invalidation-hook.md`
  - `docs/audit/port-gaps/github/006-installation-created-no-provisioning.md`
