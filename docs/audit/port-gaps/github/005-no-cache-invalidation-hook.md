# Finding 005: Installation lifecycle — no cache invalidation hook on mutate events

**Scope**: github
**Severity**: P3 (drift/contract) — pending Finding 016 resolution this could rise to P2
**Status**: Not-yet-implemented (stub) — the cache itself is missing, so the hook has nothing to invalidate
**Estimated port effort**: 1-2h (couples with Finding 016's 2-3h cache implementation)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:163-180`
- Contract/behavior: On every mutate action (`deleted`, `suspend`, `unsuspend`) the TS handler called `options.installationRouter.invalidate(id)` if the router was wired. This busted an in-process cache inside the `InstallationRouter` service so the next lookup re-read from Postgres rather than returning the stale (e.g., unsuspended, or missing) value.

```typescript
// packages/api/src/routes/github/github-webhook.ts:163-180 (9e9a57c~1)
} else if (action === 'deleted') {
  await options.installationStore.removeInstallation(id);
  // Invalidate the cache when an installation is deleted
  if (options.installationRouter) {
    options.installationRouter.invalidate(id);
  }
} else if (action === 'suspend') {
  await options.installationStore.suspendInstallation(id);
  // Invalidate cache so the suspended state is picked up
  if (options.installationRouter) {
    options.installationRouter.invalidate(id);
  }
} else if (action === 'unsuspend') {
  await options.installationStore.unsuspendInstallation(id);
  if (options.installationRouter) {
    options.installationRouter.invalidate(id);
  }
}
```

The `InstallationRouter` class (not quoted here — lived under `packages/api/src/services/installation-router.ts`) held a 60-second TTL cache keyed by `installationId`. `invalidate(id)` deleted the entry. The webhook was the only explicit invalidator; the TTL was the only other path to cache refresh.

- Dependencies: `InstallationRouter.invalidate(installationId)`; TS-side cache implementation.
- Tests that exercised this: router unit tests covered invalidate behavior; webhook integration tests covered that `suspend` followed immediately by a lookup returned the suspended state.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` (entire file)
- Contract/behavior: The C# `InstallationRouterService` has **no cache at all**. Every call to `_installations.GetByInstallationIdAsync` hits Postgres directly. Consequently there is nothing to invalidate, and the webhook handler does not invoke an `Invalidate` method (there is none).

Search result: zero references to `invalidate`, `cache`, `MemoryCache`, `IMemoryCache`, or any TTL primitive in `InstallationRouterService.cs`. The class is a thin delegator over the repository.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:281-295 (current)
case "suspend":
    await _installations.SetSuspendedAsync(installationId.Value, true);
    await EmitEventAsync(
        "INSTALLATION.SUSPENDED.SUCCESS",
        null,
        new Dictionary<string, object?> { ["installationId"] = installationId });
    return new WebhookResult("installation", action, Skipped: false);

case "unsuspend":
    await _installations.SetSuspendedAsync(installationId.Value, false);
    await EmitEventAsync(
        "INSTALLATION.UNSUSPENDED.SUCCESS",
        null,
        new Dictionary<string, object?> { ["installationId"] = installationId });
    return new WebhookResult("installation", action, Skipped: false);
```

No cache invalidation because no cache. See Finding 016 for the missing cache itself.

- Dependencies: `IInstallationRepository` — fresh-read every time.
- Tests: `InstallationRouterServiceTests` does not test cache behavior (there is nothing to test).

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: bust the 60s cache immediately on mutate so subsequent lookups saw the new state within milliseconds.
- C# does: every read hits Postgres — the cache-invalidation hook is not missed because the cache is not there, but Postgres load increases linearly with webhook volume.
- For a caller invoking `HandleCallbackAsync` or `EnqueueDeferredEventAsync` immediately after a `suspend` webhook, TS would have served the pre-mutation cached value for up to 60 seconds if `invalidate` had not run; because it did, the next lookup was correct. C# is always correct (no cache) but also always pays the DB round-trip.
- In production with existing data / deployed clients, this means: correctness is not affected today because there's no cache. But if anyone re-introduces caching (per Finding 016) without also wiring invalidation here, a subtle bug opens: a suspended installation continues to receive task enqueues for up to TTL seconds because the router thinks it's active.

Error paths:
- No new error paths; current behavior is correct-and-slow rather than fast-and-stale.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: Not referenced. Caching is an implementation concern, not an AC; the story does not mandate or prohibit caching.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [x] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

This is a performance / architecture concern that the story correctly leaves to implementation.

## 5. Status

- **Classification**: Not-yet-implemented (stub) — on the cache side. The invalidation hook is dependent on the cache itself being present (Finding 016).
- **What's needed to finish**:
  1. Resolve Finding 016: add a 60-second TTL cache in `InstallationRouterService` (or introduce `IInstallationCache`).
  2. After (1): in each of the three mutate branches (`deleted`, `suspend`, `unsuspend`) invoke `cache.Invalidate(installationId)` before returning.
  3. Also invalidate on the `installation.created` branch — if a (re-)installation for the same external `installation_id` appears, the prior cached miss must be evicted.
  4. Consider invalidating on `installation_repositories.added` / `removed` if the cached value carries repo metadata; the TS router exposed repos on the cached object so TS did bust on these too. Confirm intended shape before wiring.
- **Is it "just a stub" or is scope missing?** Stub. Paired with Finding 016. The two should be remediated together.
- **Blockers**: Finding 016 must land first (or alongside).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — inject cache, invalidate in mutate branches.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IInstallationRouterService.cs` — optionally surface an `Invalidate` method if callers outside the webhook path need to invalidate (e.g., admin-triggered refresh).
- Files to create: see Finding 016 (the cache implementation itself).
- Tests to add:
  - `InstallationRouterServiceTests.HandleWebhook_Suspend_InvalidatesCache` — call once to warm, post suspend webhook, call again, assert DB was re-queried. Use a spy repository.
  - `InstallationRouterServiceTests.HandleWebhook_Deleted_InvalidatesCache` — same pattern.
  - `InstallationRouterServiceTests.HandleWebhook_Unsuspend_InvalidatesCache` — same.
- Estimated effort: 1-2h on top of Finding 016's 2-3h (because the hook itself is three one-line calls plus the three tests).

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:163-180` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:268-295`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `016-installation-router-no-60s-ttl-cache.md` (the cache itself), `004-installation-deleted-soft-vs-hard.md`
