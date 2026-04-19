# Finding 009: Diagnostics report groups by time only, not by provider/model/agentType

**Scope**: providers
**Severity**: P1 (cost-attribution dashboards impossible)
**Status**: Incomplete (different aggregation axis was ported)
**Estimated port effort**: 6–10h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/diagnostics-routes.ts` and
`git show 9e9a57c~1:packages/api/src/services/pg-diagnostics-store.ts`.

- File: `packages/api/src/routes/settings/diagnostics-routes.ts:137-162`
- Contract/behavior: `GET /diagnostics/report?groupBy=provider|model|agentType` aggregated cost/tokens/latency/error-rate **by dimension** (not by time). Returns `DiagnosticsReportGroup[]` where each group is `{key, totalCost, totalTokens, avgLatency, errorRate, count}`.

```typescript
// packages/api/src/routes/settings/diagnostics-routes.ts (9e9a57c~1) — lines 137-162
app.get('/diagnostics/report', async (request, reply) => {
  const query = request.query as { from?: string; to?: string; groupBy?: string };
  const accountId = getAccountId(request);
  const groupBy = query.groupBy ?? 'provider';
  if (!VALID_GROUP_BY.has(groupBy)) {
    return reply.status(400).send({ error: `Invalid groupBy value: ${groupBy}. Must be one of: provider, model, agentType` });
  }
  const options: DiagnosticsReportOptions = {
    accountId, groupBy: groupBy as 'provider' | 'model' | 'agentType',
  };
  if (query.from) options.from = query.from;
  if (query.to) options.to = query.to;
  const groups = await store.report(options);
  return reply.send({ groups });
});
```

- SQL query produced: `SELECT provider_name, SUM(cost_usd), SUM(input_tokens + output_tokens), AVG(latency_ms), error_rate, COUNT(*) FROM provider_diagnostics WHERE ... GROUP BY provider_name ORDER BY count DESC` (see `pg-diagnostics-store.ts:192-207`).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:238-251`, `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs:84-134`
- Contract/behavior: `GET /diagnostics/report?from&to&bucketSize=5m|hour|day` returns a **time-bucketed** aggregation across the whole tenant. There is no `groupBy` axis at all.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs — lines 238-251
public static async Task<IResult> GetReport(
    [FromServices] IDiagnosticsService service,
    [FromServices] ITenantContext tc,
    DateTime? from,
    DateTime? to,
    string? bucketSize)
{
    var fromDt = from ?? DateTime.UtcNow.AddDays(-1);
    var toDt = to ?? DateTime.UtcNow;
    var parsedBucket = ParseBucketSize(bucketSize, BucketSize.Hour);
    var report = await service.GetReportAsync(tc.TenantId, fromDt, toDt, parsedBucket);
    return Results.Ok(report);
}
```

- `DiagnosticsService.GetReportAsync` at lines 84-134 returns `DiagnosticsReport(From, To, BucketSize, Buckets[], TotalCalls, TotalCost, OverallSuccessRate)` where each `DiagnosticsBucket` is `(BucketStart, TotalCalls, SuccessCount, SuccessRate, TotalCost, AvgLatencyMs)`. All buckets are homogeneous across providers — you cannot tell which provider contributed the cost in a given 5-minute window.

## 3. The gap

- TS answered "which provider cost the most this month": `groupBy=provider`.
- TS answered "which model cost the most": `groupBy=model`.
- TS answered "which role consumed the most tokens": `groupBy=agentType`.
- C# answers only: "how much was spent each hour/day" — no attribution.
- For a dashboard that wants "top 5 providers by cost this week":
  - TS: one call, `?groupBy=provider&from=...&to=...`.
  - C#: has to page through `/diagnostics/query?provider=X` once per known provider and sum client-side. And it can't even do that for agentType/model because the columns are missing (see finding 008).
- **Both axes are valuable**: time-buckets for trend graphs, dimension-buckets for breakdown tables. TS only had dimension buckets; C# only has time buckets. The correct answer is both.

Error paths:
- TS: `400 {error: 'Invalid groupBy value: X. Must be one of: provider, model, agentType'}`.
- C#: no validation error since there's no `groupBy` param; unknown `bucketSize` falls through to the `Hour` default silently.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 AC 2: "`GET /api/v1/diagnostics/report` — generate aggregated cost/usage report." Does not specify the aggregation axis. The implementation is spec-ambiguous — both time-bucketing and dimension-grouping satisfy the literal AC.
- TS precedent + archived SQL `014_provider_diagnostics.sql` with `idx_diagnostics_provider (provider_name, created_at DESC)` + `idx_diagnostics_model (model, created_at DESC)` clearly anticipated the dimension-grouped query pattern.
- Story alignment:
  - [x] Describes a third behavior — the story underspecifies; both axes meet AC but neither alone covers the TS precedent.
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.
  - [ ] No story — there is a story, but it's ambiguous.

## 5. Status

- **Classification**: Incomplete port (different aggregation model chosen).
- **What's needed to finish**:
  1. Extend `IDiagnosticsRepository.AggregateAsync` / add a second `AggregateByDimensionAsync(groupBy, from, to, tenantId)` that returns per-dimension buckets.
  2. Extend `DiagnosticsService.GetReportAsync` signature: `GetReportAsync(tenantId, from, to, bucketSize, groupBy?)`.
  3. Extend `DiagnosticsReport` to optionally include a `Dimensions: DimensionBucket[]` field.
  4. Accept `groupBy` query param in `GetReport` endpoint; validate against `{provider, model, agentType}`.
  5. Depends on finding 008 for `agent_type` + `model` columns to exist.
- **Is it "just a stub" or is scope missing?** Incomplete — the story didn't pin the aggregation axis, and the C# engineer picked the other one. Both are useful; neither alone replaces TS.
- **Blockers**: Finding 008 (missing columns).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/IDiagnosticsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/Models/DiagnosticsReport.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/DiagnosticsRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:238-251`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/Models/DimensionBucket.cs`
- Tests to add:
  - `DiagnosticsReport_GroupByProvider_ReturnsOneBucketPerProvider`
  - `DiagnosticsReport_GroupByModel_RespectsNullModelAsUnknown`
  - `DiagnosticsReport_GroupByAgentType_WhenColumnMissing_Returns400`
  - `DiagnosticsReport_InvalidGroupBy_Returns400WithValidValues`
- Estimated effort: 8h broken down as:
  - Repo SQL / LINQ aggregation: 2.5h
  - Service + DTO extension: 1.5h
  - Endpoint + validation: 1h
  - Tests: 3h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (both axes available)
- **Commit**: `0dbccf9` `fix(providers): land P1/P2 diagnostics/health/validation/user-providers fixes [findings 008, 009, 010, 012, 013, 014, 018, 019]`
- **Notes**: Added `IDiagnosticsService.GetDimensionReportAsync(provider|model|agentType)` with EF GroupBy aggregation (`ProviderKey`, `Model`, `AgentType` columns — `AgentType` exists thanks to finding 008 schema work). Returns a `DimensionReport { Groups: DimensionBucket[] }` with `{Key, TotalCalls, SuccessCount, ErrorRate, TotalCost, TotalTokens, AvgLatencyMs}` per bucket, ordered by `TotalCalls DESC`. `GET /api/providers/diagnostics/report` now accepts `?groupBy=provider|model|agentType` (TS shape) AND keeps the existing `?bucketSize=5m|hour|day` (time-bucketed) — both axes supported, neither deletes the other. Invalid `groupBy` returns `400` with the valid values listed.

## References

- TS source: `packages/api/src/routes/settings/diagnostics-routes.ts:137-162`, `packages/api/src/services/pg-diagnostics-store.ts:155-217` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs:84-134`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:238-251`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md` AC 2
- Related findings: `008-diagnostics-taxonomy-collapsed.md`, `023-diagnostics-missing-composite-indexes.md`
- Archived SQL migration: `database/archived-sql-migrations/014_provider_diagnostics.sql:33-35` (indices that anticipate dimension-grouped queries)
