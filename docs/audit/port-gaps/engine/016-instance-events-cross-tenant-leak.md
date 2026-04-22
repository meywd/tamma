# Finding 016: `GetInstanceEvents` leaks cross-tenant events

**Scope**: engine
**Severity**: P0 (cutover-blocking — multi-tenant isolation break)
**Status**: Behavioral drift (security bug)
**Estimated port effort**: 4h (including regression-test coverage)

## 1. What's in TS

- File: `packages/api/src/routes/workflows/index.ts:253-296` (9e9a57c~1)
- Contract: `GET /api/workflows/instances/:id/events` — SSE stream bound to a specific instance, scoped to the caller's tenant via the surrounding RBAC middleware (`requirePermission('workflows:view')`) and the `withTenantContext` session variable used by `PgEventStore`.

```typescript
// packages/api/src/routes/workflows/index.ts:253-268 (9e9a57c~1)
fastify.get<{ Params: { id: string } }>(
  '/api/workflows/instances/:id/events',
  {
    preHandler: [requirePermission('workflows:view')],
  },
  async (request, reply) => {
    const { id } = request.params;
    const instance = await store.getInstance(id);
    if (instance === null) {
      return reply.status(404).send({ error: 'Instance not found' });
    }
    reply.hijack();
    // SSE headers + state polling, scoped to the instance's tenant via store
  },
);
```

The `store.getInstance(id)` call goes through `withTenantContext` which sets Postgres session variable `app.current_tenant_id`. RLS enforces the per-tenant boundary at the database level. No cross-tenant data can leak.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/WorkflowEndpoints.cs:101-105`

```csharp
// WorkflowEndpoints.cs:101-105 (current)
public static async Task<IResult> GetInstanceEvents(
    Guid id, IEventRepository eventRepo, int? limit)
{
    var events = await eventRepo.QueryAsync(null, null, null, limit ?? 50);
    return Results.Ok(events.Select(e => new { e.Id, e.Type, e.Data, e.CreatedAt }));
}
```

Two critical problems:

1. `tenantId` parameter is **`null`** — no tenant filtering at all.
2. `EventRepository.QueryAsync` implementation uses `IgnoreQueryFilters()` then conditionally filters by tenant only when the tenant arg is non-null:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs:19-29 (current)
public async Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
{
    var query = db.DomainEvents.IgnoreQueryFilters().AsQueryable();
    if (tenantId.HasValue)
        query = query.Where(e => e.TenantId == tenantId.Value);
    // ...
}
```

With `tenantId = null`, every tenant's events are returned. The `IgnoreQueryFilters()` defeats any EF Core global filter that might have redundantly enforced tenant scoping.

Neither the route parameter `id` nor the instance's tenant is used. The endpoint returns the most recent 50 events from the entire engine_events table regardless of ownership.

- Tests: no tenant-isolation test on this endpoint. The existing smoke test passes because the test fixture has only one tenant seeded.

## 3. The gap

- TS did: RBAC + tenant-scoped SSE stream for the given instance.
- C# does: globally un-scoped fetch of the 50 most recent events, returned as a JSON array (also see finding 012 — this is one-shot JSON, not SSE).

For a tenant-A user calling `GET /api/workflows/instances/<tenant-B-instance-guid>/events`:

- TS: 404 (the instance isn't visible to tenant A; SSE never opens).
- C#: 200 with cross-tenant events.

For a tenant-A user calling `GET /api/workflows/instances/<tenant-A-instance-guid>/events`:

- TS: SSE stream scoped to tenant A's events.
- C#: 200 with the 50 most recent events from **all tenants**, not filtered by instance id at all.

This is a straightforward cross-tenant data leak. A customer in tenant A can observe workflow execution of tenant B (what workflows they're running, what data they're passing, what errors). In a SaaS deployment this is a breach.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (tenant isolation spec).
- Also `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md` for the workflow-instance-specific RLS.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift with security impact.
- **What's needed to finish**:
  1. Look up the instance via `IWorkflowRepository.GetInstanceAsync(id)`, pulling its `TenantId`.
  2. 404 when not found (or when `tenantId` mismatches the ambient `ITenantContext.TenantId`).
  3. Pass the instance's tenant id to `eventRepo.QueryAsync`.
  4. Restrict further by instance id — the TS version streamed events keyed by the instance, not every event in the tenant. The C# query has no way to filter by instance id without schema changes (see 028).
  5. Convert to real SSE (cross-ref finding 012).
- **Is it "just a stub" or is scope missing?** Scope was implemented carelessly — the author knew `tenantId` existed on `QueryAsync` but passed `null`. Quick fix in the endpoint, with a deeper fix in `EventRepository` (finding 028).
- **Blockers**: cross-ref finding 028 (RLS bypass in `EventRepository`).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/WorkflowEndpoints.cs:101-105`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` — stop using `IgnoreQueryFilters()` as the default path (finding 028).
- Files to create:
  - integration test file `Tamma.Api.Tests/WorkflowEndpoints/CrossTenantLeakTests.cs`.
- Tests to add:
  - `GetInstanceEvents_Returns404_ForOtherTenantsInstance`
  - `GetInstanceEvents_OnlyReturnsEvents_ForRequestedInstance`
  - `GetInstanceEvents_Honours_AmbientTenantContext`
  - `GetInstanceEvents_NoTenant_Returns401` (auth guard)
- Estimated effort: 4h
  - Endpoint rewrite: 30m
  - Pass tenant to repo + fix IgnoreQueryFilters default: 1h
  - Integration tests (cross-tenant fixture): 2.5h

## References

- TS source: `packages/api/src/routes/workflows/index.ts:253-296`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/WorkflowEndpoints.cs:101-105`, `Tamma.Data/Repositories/EventRepository.cs:19-29`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`, `17-4-tenant-scoped-workflow-instances.md`
- Related findings: `028-eventrepo-rls-bypass.md`, cross-ref `docs/audit/port-gaps/orgs/` findings on tenant isolation

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: c9dd51e
- **Notes**: `WorkflowEndpoints.GetInstanceEvents` now (a) loads the
  instance via `IWorkflowRepository.GetInstanceAsync`, 404s when not
  found; (b) compares the instance's tenant against the ambient
  `ITenantContext.TenantId` and 404s on mismatch (the TS RBAC
  middleware-equivalent); (c) passes the instance's tenant to
  `IEventRepository.QueryAsync` so the query is scoped even if the
  global EF filter is bypassed downstream. Cross-tenant data leak
  closed.
