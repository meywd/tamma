# Finding 016: Installation router has no 60-second TTL cache

**Scope**: github
**Severity**: P2 (correctness/observability) — currently P3 from a pure-correctness lens, but the underlying Postgres load argues for P2
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2-3h

## 1. What's in TS

Pre-delete snapshot reference: `InstallationRouter` class lived under `packages/api/src/services/installation-router.ts` (not captured in the summary but referenced from `github-webhook.ts`).

- File: `packages/api/src/routes/github/github-webhook.ts:33-34,166-178` (how it was wired); `packages/api/src/services/installation-router.ts` (the class itself)
- Contract/behavior: TS ran a singleton `InstallationRouter` service that cached installation lookups by `installationId` with a 60-second TTL. Every webhook path that needed installation context (e.g., tenant link for task enqueue) went through `installationRouter.resolve(id)`. First call hit Postgres; subsequent calls within 60s hit the in-process map. Mutations (`installation.deleted` / `suspend` / `unsuspend`) invalidated the entry — see Finding 005.

The webhook handler wired the router as an optional dependency:

```typescript
// packages/api/src/routes/github/github-webhook.ts:33-34 (9e9a57c~1)
/** Installation router for resolving/caching installations. */
installationRouter?: InstallationRouter;
```

And invalidated on mutation (lines 166-178 quoted in Finding 005).

The actual TTL cache implementation was a map with insertion timestamps; entries expired passively (checked on read) or were purged on explicit invalidation.

- Dependencies: none external — internal to the service.
- Tests that exercised this: TS integration tests asserted:
  - First lookup queries store, second lookup (within 60s) does not.
  - After TTL expiry, store is re-queried.
  - `invalidate(id)` forces re-query on next lookup.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` (entire file)
- Contract/behavior: No cache. Every operation on the router service goes directly through `IInstallationRepository`.

Concrete examples of repeated DB calls within a single webhook:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:166-168 (current)
if (installationId is not null)
{
    var install = await _installations.GetByInstallationIdAsync(installationId.Value);
    tenantId = install?.TenantId;
}
```

This call happens on every deferred-event enqueue. Under heavy webhook volume (a popular repo's `push` event rate, or a PR with many `pull_request.synchronize` deliveries), each webhook generates one repository read per delivery, even though the installation entity changes minutely (suspend/unsuspend/delete are rare).

- Dependencies: `IInstallationRepository`, `TammaDbContext` — every call is a round-trip + EF Core translation.
- Tests: `InstallationRouterServiceTests` does not test caching (there is none).

## 3. The gap

- TS did: cache installation lookups for 60s, producing near-zero DB load for hot installations.
- C# does: query Postgres on every lookup.
- For a caller sending 1000 `push` webhooks per minute for a single active installation, TS hit Postgres for the installation lookup once; C# hits Postgres 1000 times. Each call is cheap (indexed `(InstallationId)` lookup on a small table with `.Include(i => i.Repos)`) but 1000 unnecessary round-trips per minute per installation is not free.
- In production with existing data / deployed clients, this means:
  - **Postgres load scales linearly with webhook volume**: for a tenant with 100 active installations, hot traffic can dominate connection-pool time.
  - **p99 latency on webhook response increases**: the DB call is 2-5ms on a warm pool, 20-100ms if the pool is saturated. Under concurrent load this compounds.
  - **Connection pool pressure**: each webhook holds a connection for the duration of the lookup + dispatch. Unnecessary lookups reduce the pool's effective capacity for legitimate work (event emission, task enqueue).
  - **No concurrency dedup**: 50 simultaneous webhooks for the same installation each issue a separate query. A cache would coalesce these to one + 49 cache hits.

Error paths:
- TS error path: cache never returns errors (passive expiry); store errors propagate through the first lookup.
- C# error path: every lookup can fail independently (connection issue, timeout); 50× more surface area for transient DB faults to affect webhook response.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: none. Caching is an implementation concern.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [x] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

Performance concern; architecture.md (if it discussed caching) would be the governing doc, but no such section is cited.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Straightforward add.
- **What's needed to finish**:
  1. Choose cache primitive. `IMemoryCache` (built into ASP.NET Core) is fine — it supports absolute expiration and sliding expiration. `HybridCache` (new in .NET 9) would also work but is overkill.
  2. Inject `IMemoryCache` into `InstallationRouterService`.
  3. Wrap `_installations.GetByInstallationIdAsync(id)` calls inside the service with a cache-get-or-set using key `$"installation:{id}"` and `AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)`.
  4. Expose `Invalidate(long installationId)` method on the interface so Finding 005's webhook mutation paths can bust the entry.
  5. Optionally: partition by tenant as well (`$"installation:{tenantId}:{id}"`) so per-tenant invalidation is possible, but this complicates the lookup paths — simpler to key on installation id alone.
- **Is it "just a stub" or is scope missing?** Stub. Easy to add; the architecture already supports it.
- **Blockers**: None. Pairs tightly with Finding 005 (which depends on this).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — inject `IMemoryCache`, wrap `GetByInstallationIdAsync` calls.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IInstallationRouterService.cs` — add `void Invalidate(long installationId)`.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register `AddMemoryCache()` (may already be registered for other services; verify).
- Files to create: none.
- Tests to add:
  - `InstallationRouterServiceTests.Resolve_HitsRepoOnce_WithinTTL` — call twice within a second, assert the spy repository saw only one call.
  - `InstallationRouterServiceTests.Resolve_AfterTTLExpiry_HitsRepoAgain` — use a fake `IMemoryCache` or `TimeProvider` to advance time; assert second call hits repo.
  - `InstallationRouterServiceTests.Invalidate_EvictsCachedEntry` — warm cache, call Invalidate, next Resolve hits repo.
  - `InstallationRouterServiceTests.ConcurrentResolves_CoalesceToSingleRepoCall` — 10 concurrent `Resolve(id)` calls, assert repo called only once. Uses a semaphore inside the cache get-or-set to be meaningful.
- Estimated effort: 2-3h broken down as:
  - Cache wiring + Invalidate surface: 1h
  - Tests (4 cases): 1-2h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:33-34,166-178` (commit `9e9a57c~1`); class lived in `packages/api/src/services/installation-router.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`
- Story: no story; performance concern
- Related findings: `005-no-cache-invalidation-hook.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed
- **Commit**: `a3d2e7e` (engine scope, finding 029)
- **Notes**: Engine scope added `IMemoryCache`-backed 60s TTL via `GetInstallationCachedAsync` keyed by `install:{installationId}`. All mutate branches (created, deleted, suspend, unsuspend) call `InvalidateInstallationCache(installationId)`. The callback path also invalidates after upsert (added in this commit alongside finding 007). No additional work required.
