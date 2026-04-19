# Finding 029: Installation router has no 60s-TTL cache — webhook latency regression

**Scope**: engine (GitHub App)
**Severity**: P1 (feature broken — webhook throughput / p99 latency regression)
**Status**: Incomplete (production optimisation dropped)
**Estimated port effort**: 4h

## 1. What's in TS

- File: `packages/api/src/services/installation-router.ts` (9e9a57c~1)

The TS router maintained a 60-second TTL in-memory cache keyed by `installationId`. Webhook dispatch looked up the target tenant + installation row from cache first, DB on miss. For a repository receiving 100 pushes/hour:

- Cache hit: <10ms dispatch (memory lookup, enqueue).
- Cache miss: ~20ms (DB roundtrip + cache populate + enqueue).

Cache miss happened at most once per 60s per installation, so steady-state was dominated by hits.

Headers in the TS source explained the TTL choice: 60s is "short enough to reflect installation state changes (install/uninstall/suspend) without a restart; long enough to smooth over the 95%+ cache-hit workload of a busy tenant."

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`

No cache. Every webhook touches the DB:

```csharp
// InstallationRouterService.cs:164-170 (current)
Guid? tenantId = null;
if (installationId is not null)
{
    var install = await _installations.GetByInstallationIdAsync(installationId.Value);
    tenantId = install?.TenantId;
}
```

For every webhook event (push / issues / pull_request, etc.), the handler calls `GetByInstallationIdAsync` which does `db.GitHubInstallations.Include(i => i.Repos).FirstOrDefaultAsync(...)`. The `.Include(i => i.Repos)` makes this more expensive than a simple indexed lookup — it fetches every repo row associated with the installation.

### Deployment impact

A busy SaaS customer with 200 repos firing ~500 webhooks/hour will:

- TS: 500 cache hits (<10ms each), 1 DB lookup every 60s per installation.
- C#: 500 DB lookups, each ~20-50ms including the `Include` fan-out.

Net increase of 10+ seconds per hour of blocking DB time per installation. With concurrent webhook spikes (e.g. post-merge CI-completion bursts), this cascades into connection-pool exhaustion and webhook timeouts from GitHub (GitHub retries unresponsive endpoints, then gives up after ~3 attempts).

## 3. The gap

- TS did: 60s TTL in-memory cache. Dispatch under 10ms on hit.
- C# does: DB hit on every webhook. No cache. `Include(i => i.Repos)` makes each lookup heavier than necessary.

For dashboard observability (p95 webhook dispatch latency): TS ~10ms, C# ~30-50ms. Minor at low traffic, compounding under load.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (GitHub App install flow). Doesn't explicitly call out caching as a requirement, but the TS design document (`docs/stories/plans/c-sharp-port-audit-findings.md`) mentions it.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression on latency / throughput)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — performance requirement implicit

## 5. Status

- **Classification**: Incomplete — production optimisation was dropped during port.
- **What's needed to finish**:
  1. Add `IMemoryCache` dependency (already available in ASP.NET Core).
  2. Wrap `GetByInstallationIdAsync` in a cache lookup: `_cache.GetOrCreateAsync($"install:{installationId}", entry => { entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60); return _installations.GetByInstallationIdAsync(installationId); })`.
  3. Drop `.Include(i => i.Repos)` — the router doesn't need repos; they're loaded lazily if needed. Or add a lightweight DTO path that skips the include.
  4. Invalidate cache on install/uninstall/suspend events: `_cache.Remove($"install:{installationId}")` at the end of each handler.
  5. Expose cache-hit-rate metric via OpenTelemetry / Prometheus.
- **Is it "just a stub" or is scope missing?** Performance optimisation dropped; correctness is intact.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — add cache.
  - `Program.cs` — register `IMemoryCache` (typically already done by `AddMemoryCache()`).
- Tests to add:
  - `HandleWebhook_CacheHit_AvoidsDbRoundtrip` (counted via `Mock<IInstallationRepository>.Verify`).
  - `HandleWebhook_CacheMiss_PopulatesAndDispatches`.
  - `HandleWebhook_AfterInstallationCreated_CacheInvalidated`.
  - `HandleWebhook_AfterInstallationSuspended_CacheInvalidated`.
- Estimated effort: 4h
  - Cache wrapper + invalidation hooks: 2h
  - Drop unneeded `Include`: 30m
  - Tests: 1.5h

## References

- TS source: `packages/api/src/services/installation-router.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `030-installation-soft-delete-vs-hard.md`, cross-ref `docs/audit/port-gaps/github/` on webhook handling

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: a3d2e7e
- **Notes**: `InstallationRouterService` now wraps
  `GetByInstallationIdAsync` in an `IMemoryCache` lookup with a 60-second
  TTL on the webhook hot path (`EnqueueDeferredEventAsync`). Cache is
  invalidated on every `installation.created`/`deleted`/`suspend`/`unsuspend`
  webhook so state changes propagate within one webhook tick. Drop of
  `.Include(i => i.Repos)` deferred — the deferred-event path only needs
  `TenantId` so the cache hit is cheap, and other callers still want
  the includes; left as a stretch optimisation.
