# Finding 011: `AnalyticsService` returns zero state for all three routes

**Scope**: kb
**Severity**: P2 (observability only; honest zero state rather than lies)
**Status**: Not-yet-implemented (cost tracker never constructed)
**Estimated port effort**: 3-4h (depends on cost-tracker wiring + dashboard integration)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/AnalyticsService.ts`.

The deleted TS `AnalyticsService` had **identical** null-fallback behavior to the sidecar:

```typescript
// packages/api/src/services/knowledge-base/AnalyticsService.ts (9e9a57c~1)
async getUsageAnalytics(period: AnalyticsPeriodFilter): Promise<UsageAnalytics> {
  if (!this.costTracker) {
    return {
      period: { start: period.start, end: period.end },
      totalQueries: 0,
      totalTokensRetrieved: 0,
      avgLatencyMs: 0,
      sourceBreakdown: {},
    };
  }
  const usageRows = await this.costTracker.getUsage({ start: period.start, end: period.end });
  const totalTokens = usageRows.reduce((sum, row) => sum + row.tokens, 0);
  return {
    period: { start: period.start, end: period.end },
    totalQueries: usageRows.length,
    totalTokensRetrieved: totalTokens,
    avgLatencyMs: 0,
    sourceBreakdown: {},
  };
}

async getQualityAnalytics(period: AnalyticsPeriodFilter): Promise<QualityAnalytics> {
  // Quality analytics require feedback data which the CostTracker does not track.
  // Return zero state; this can be enhanced when a dedicated feedback store is available.
  return {
    period: { start: period.start, end: period.end },
    totalFeedback: 0,
    relevanceRate: 0,
    avgRelevanceScore: 0,
    topPerformingSources: [],
    improvementTrend: 0,
  };
}

async getCostAnalytics(period: AnalyticsPeriodFilter): Promise<CostAnalytics> {
  if (!this.costTracker) {
    return {
      period: { start: period.start, end: period.end },
      totalCostUsd: 0,
      embeddingCostUsd: 0,
      indexingCostUsd: 0,
      breakdown: [],
    };
  }
  // ...
}
```

Two observations about the TS version:
- It also returned zero state when no `costTracker` was injected — so user-visible behavior does not regress from TS → sidecar.
- The `getQualityAnalytics` path explicitly documented "zero state until feedback store is available" — a pre-existing TODO.

- Dependencies: `ICostTracker` from `@tamma/providers` (LLM cost metering).
- Tests: `packages/api/src/__tests__/services/knowledge-base/AnalyticsService.test.ts` — tested with injected fake trackers.

## 2. What's in C#

### C# side
Three endpoints, forwarded verbatim:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:184-203 (current)
public static async Task<IResult> GetKbAnalytics(
    [FromServices] IIntelligenceHttpClient client,
    [FromQuery(Name = "start")] string? start,
    [FromQuery(Name = "end")] string? end,
    CancellationToken ct)
    => Results.Ok(await client.GetAnalyticsAsync(start, end, ct));

public static async Task<IResult> GetKbUsage( /* ... */ ) => /* ... */;
public static async Task<IResult> GetKbCosts( /* ... */ ) => /* ... */;
```

### Sidecar side

```typescript
// packages/intelligence-server/src/services/AnalyticsService.ts:48-61 (current)
async getAnalytics(period?: AnalyticsPeriod): Promise<KbAnalyticsResponse> {
  if (!this.costTracker) {
    return { queries: 0, indexedDocs: 0, hitRate: 0, totalTokens: 0 };
  }
  const bound = this.toPeriod(period);
  const usage = await this.costTracker.getUsage(bound);
  const totalTokens = usage.reduce((sum, row) => sum + row.tokens, 0);
  return {
    queries: usage.length,
    indexedDocs: 0,
    hitRate: 0,
    totalTokens,
  };
}
```

```typescript
// packages/intelligence-server/src/services/AnalyticsService.ts:63-85 (current)
async getUsage(period?: AnalyticsPeriod): Promise<KbUsageResponse> {
  if (!this.costTracker) {
    return { daily: [] };
  }
  // ...
}

// lines 87-104
async getCosts(period?: AnalyticsPeriod): Promise<KbCostsResponse> {
  if (!this.costTracker) {
    return { totalCost: 0, breakdown: [] };
  }
  // ...
}
```

- Dependencies: `ICostTrackerAdapter` (narrow type).
- Tests: `packages/intelligence-server/src/__tests__/services/AnalyticsService.test.ts`.

Schema drift between TS and sidecar:
- TS `UsageAnalytics` had `avgLatencyMs`, `sourceBreakdown`. Sidecar `KbAnalyticsResponse` has `hitRate`, `indexedDocs`, `totalTokens`.
- TS had distinct `getUsageAnalytics` / `getCostAnalytics` / `getQualityAnalytics`. Sidecar collapsed to `getAnalytics` / `getUsage` / `getCosts`.

## 3. The gap

- TS did: zero-state when no tracker; delegate to real tracker when injected.
- C# + sidecar does: same zero-state fallback; never injects a tracker.

For a dashboard user viewing "Knowledge Base Analytics":
- TS: empty dashboard, "No data yet" state.
- C# + sidecar: identical empty dashboard.

The user-visible regression is zero. The observability regression is: no cost / usage / hit-rate metrics ever flow, so the team can't:
- Budget LLM spend.
- Detect a runaway embedder cost (Story 6-7 "LLM Cost Monitoring").
- Measure RAG effectiveness (hit rate).

Error paths:
- TS: zero-state JSON.
- C# + sidecar: zero-state JSON (same).

In production this means the `/dashboard` Analytics tab is permanently empty — but this was also true in TS prod since the cost tracker was never wired there either.

## 4. Gap from stories

`docs/stories/epic-6/story-6-7/6-7-llm-cost-monitoring.md` (LLM cost monitoring) is the covering story. It calls for per-model, per-user, per-period cost breakdowns — all of which require the cost tracker to be running AND the analytics surface to read from it.

Story alignment:
- [x] Matches TS behavior (both ship zero-state; story not met in either)
- [x] Matches C# behavior (same)
- [ ] Describes a third behavior
- [x] Partial — the story exists but schema drift between TS and sidecar (`avgLatencyMs` vs `hitRate`) isn't adjudicated.

## 5. Status

- **Classification**: Not-yet-implemented. Also: data-model drift between TS and sidecar.
- **What's needed to finish**:
  1. Construct an `ICostTrackerAdapter` in the sidecar composition root. Source of truth: likely `@tamma/providers`'s cost-tracker singleton, or a new Postgres-backed cost_events table.
  2. Pass to `AnalyticsService`.
  3. Fill `indexedDocs` by querying the indexer's stats (currently hard-coded to 0).
  4. Fill `hitRate` by querying the RAG pipeline cache stats (already available via `IRagPipeline.getCacheStats()`).
  5. Reconcile schema: decide whether to carry the TS fields (`avgLatencyMs`, `sourceBreakdown`) or keep the sidecar fields. Update dashboard accordingly.
- **Is it "just a stub" or is scope missing?** Scope is spec'd in Story 6-7; implementation stopped at the interface layer.
- **Blockers**:
  - #001 (composition root).
  - Story 6-7 has no clear owner; cost-tracker plumbing is a shared concern.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/adapters.ts` — add `createCostTrackerFromEnv()`.
  - `packages/intelligence-server/src/server.ts` — wire.
  - `packages/intelligence-server/src/services/AnalyticsService.ts` — query indexer for `indexedDocs`, RAG cache for `hitRate`.
- Files to create:
  - `packages/intelligence-server/src/cost-tracker-bridge.ts` — bridges `@tamma/providers` `ICostTracker` to `ICostTrackerAdapter`.
- Tests to add:
  - `GET /kb/analytics` with a populated cost tracker returns non-zero `queries`, `totalTokens`.
  - `GET /kb/analytics/usage?start=...&end=...` returns per-day breakdown.
  - `GET /kb/analytics/costs` returns positive `totalCost` and non-empty `breakdown` when events exist.
- Estimated effort: 3-4h
  - Adapter + composition: 1h
  - Non-zero-state tests: 1h
  - Dashboard schema reconciliation: 1-2h

## References

- TS source: `packages/api/src/services/knowledge-base/AnalyticsService.ts` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/AnalyticsService.ts`
- Story: `docs/stories/epic-6/story-6-7/6-7-llm-cost-monitoring.md`
- Related findings: #001, #014

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

`AnalyticsService` is in `packages/intelligence-server/src/services/`. The
zero-state fallback, the missing `ICostTracker` wiring, and the
`indexedDocs` / `hitRate` plumbing all live in TypeScript and depend on
either `@tamma/providers` (cost tracker) or `IRagPipeline.getCacheStats()`.
The schema drift between TS (`avgLatencyMs`, `sourceBreakdown`) and the
sidecar (`hitRate`, `indexedDocs`, `totalTokens`) is also a sidecar
contract decision — once made, the C# DTOs would be regenerated from the
sidecar response shape (currently the C# client returns `JsonElement`
unchanged so the dashboard sees the sidecar shape directly).

The user-visible behavior is unchanged from TS, so this is honest zero
state — not a port regression.

**To unblock:** sidecar work — wire cost-tracker bridge, query indexer for
docs, query RAG cache for hit rate. 3-4h.
