# Finding 022: Dashboard `/summary` shape drift — missing `recentEvents`, different field names

**Scope**: engine (dashboard)
**Severity**: P2 (correctness — dashboard tiles lost the live activity feed)
**Status**: Behavioral drift (simpler, incomplete)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/dashboard/index.ts:27-55` (9e9a57c~1)

```typescript
// packages/api/src/routes/dashboard/index.ts:27-55 (9e9a57c~1)
fastify.get('/api/dashboard/summary', async (request, reply) => {
  const engines = engineRegistry.list();
  const definitions = await workflowStore.listDefinitions();

  const tenantId = (request as FastifyRequest & { tenantId?: string }).tenantId ?? DEFAULT_TENANT_ID;
  const recentEvents: unknown[] = [];
  for (const info of engines) {
    const engine = engineRegistry.get(info.id);
    if (engine === undefined) continue;
    const store = engine.getEventStore();
    if (store === undefined) continue;
    const events = await store.getEvents(tenantId);
    recentEvents.push(...events.slice(-10).map((e) => ({ ...e, engineId: info.id })));
  }

  recentEvents.sort((a: any, b: any) => (b.timestamp ?? 0) - (a.timestamp ?? 0));

  return reply.send({
    engineCount: engines.length,
    workflowDefinitions: definitions.length,
    recentEvents: recentEvents.slice(0, 20),
  });
});
```

Response shape: `{engineCount, workflowDefinitions, recentEvents[20]}`. The `recentEvents` array is the backbone of the dashboard's "live activity feed" tile — user sees "workflow 5 started", "agent task completed", etc. in near-real-time.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:8-21`

```csharp
// DashboardEndpoints.cs:8-21 (current)
public static async Task<IResult> GetSummary(
    IEventRepository eventRepo,
    IWorkflowRepository workflowRepo,
    ITenantContext tc)
{
    var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 1000);
    var (instances, total) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 1);
    return Results.Ok(new
    {
        totalEvents = events.Count,
        totalWorkflows = total,
        timestamp = DateTime.UtcNow
    });
}
```

Response shape: `{totalEvents, totalWorkflows, timestamp}`. No `recentEvents`, no `engineCount`, no `workflowDefinitions` count. The field names are completely different — `totalEvents` (C#) vs `engineCount` + ad-hoc event count (TS).

## 3. The gap

- TS did: three-part summary with live activity feed.
- C# does: totals only, no activity feed.

For the dashboard's `SummaryTile` component:

- TS: showed "3 engines running, 47 workflow definitions, last event: 'Code generated for issue #42' (3s ago)".
- C#: shows "1000 total events, N total workflows, 2026-04-17T...". Cannot reconstruct the activity feed.

The field-name drift also means any existing frontend code referencing `summary.engineCount` / `summary.recentEvents` reads `undefined`. Migration is a breaking change for anything that was written against TS.

Note: `totalEvents` caps at 1000 due to the `QueryAsync(..., 1000)` limit. It's not a true count — it's "the count of the first 1000 events" — but presented as if it were a total.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift — response was simplified and renamed.
- **What's needed to finish**:
  1. Add `recentEvents` — pull the 20 most recent events from `IEventRepository` for the ambient tenant (use `OrderByDescending(CreatedAt).Take(20)`), include `{id, type, createdAt, issueNumber, tenantId, engineId?}` per event.
  2. Rename `totalEvents` → keep (it's useful) but fix the 1000 cap (use `.CountAsync()` not `.Take(1000).Count`).
  3. Add `engineCount` — hard-coded 0 until finding 013 lands, then pull from `IEngineRegistry.Count`.
  4. Rename / add `workflowDefinitions` count — pull from `IWorkflowRepository.ListDefinitionsAsync().Count`.
- **Is it "just a stub" or is scope missing?** Scope was reduced; field names diverged. Recovery is mechanical.
- **Blockers**: `engineCount` accuracy depends on finding 013 (engine registry). `recentEvents` can land independently.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:8-21`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Dashboard/DashboardSummaryDto.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Dashboard/DashboardEventDto.cs`
- Tests to add:
  - `GetSummary_IncludesRecentEvents_Top20`
  - `GetSummary_RecentEventsSortedDescendingByTimestamp`
  - `GetSummary_TotalEvents_UsesCount_NotLimitedQuery`
  - `GetSummary_TenantIsolated` — asserts tenant-B events not visible to tenant-A.
- Estimated effort: 3h
  - Dashboard DTO + recentEvents query: 1.5h
  - Engine-count wiring (or stub placeholder): 30m
  - Tests including tenant isolation: 1h

## References

- TS source: `packages/api/src/routes/dashboard/index.ts:27-55`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:8-21`
- Story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`
- Related findings: `013-engine-registry-missing.md`, `023-dashboard-engines-empty.md`, `024-dashboard-workflows-semantics.md`, `028-eventrepo-rls-bypass.md`
