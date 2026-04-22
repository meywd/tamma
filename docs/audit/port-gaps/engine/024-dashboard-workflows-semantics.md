# Finding 024: Dashboard `/workflows` — semantics flipped from "definitions with instanceCount" to "instances"

**Scope**: engine (dashboard)
**Severity**: P2 (correctness — dashboard card renders the wrong thing)
**Status**: Semantic rewrite
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/dashboard/index.ts:64-84` (9e9a57c~1)

```typescript
// packages/api/src/routes/dashboard/index.ts:64-84 (9e9a57c~1)
fastify.get('/api/dashboard/workflows', async (_request, reply) => {
  const definitions = await workflowStore.listDefinitions();

  const result = await Promise.all(
    definitions.map(async (def) => {
      const instances = await workflowStore.listInstances({
        definitionId: def.id,
        page: 1,
        pageSize: 0, // we only need the total count
      });

      return {
        ...def,
        instanceCount: instances.total,
      };
    }),
  );

  return reply.send(result);
});
```

Response: array of **workflow definitions**, each annotated with an `instanceCount`. One row per definition. Drives the dashboard's "Workflows" tab where users see "TddRedGreenRefactor: 1,243 runs; IssueAnalysis: 892 runs" — a rollup view.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:26-30`

```csharp
// DashboardEndpoints.cs:26-30 (current)
public static async Task<IResult> GetWorkflows(IWorkflowRepository workflowRepo, ITenantContext tc)
{
    var (instances, total) = await workflowRepo.ListInstancesAsync(null, tc.TenantId, 1, 20);
    return Results.Ok(new { instances = instances.Select(i => new { i.Id, i.DefinitionId, i.Status, i.CreatedAt }), total });
}
```

Response: `{instances: [...20], total}` — the first page of 20 **instances**. One row per workflow run.

## 3. The gap

Completely different semantics under the same URL:

- TS: "definitions with rollup counts" — one card per workflow type.
- C#: "recent instances" — one row per execution.

For the dashboard's "Workflows" card:

- TS: "8 workflow definitions, TddRedGreenRefactor has 1,243 runs..."
- C#: "recent 20 runs, a mix of all types, no aggregation".

Field-name drift also exists: TS returned bare array (`[def1, def2, ...]`), C# returns `{instances, total}`. A frontend that did `summary.forEach(...)` on the TS output will `TypeError: summary.forEach is not a function` on the C# one.

Neither is strictly wrong — but they're two different views. The dashboard frontend was written against the TS shape.

Additional issue: the C# version has a **fixed** page size of 20 and no pagination query support. If the user wants page 2, they can't get it.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`.
- Story alignment:
  - [x] Matches TS behavior (C# diverged silently)
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (no explicit story contract on this URL — both views are reasonable but the original is the one deployed clients use)
  - [ ] No story

## 5. Status

- **Classification**: Semantic rewrite.
- **What's needed to finish**:
  1. Decide: restore TS semantics on this URL, OR split into two endpoints (`/workflows/definitions` rollup, `/workflows/instances` recent runs).
  2. Recommended: restore TS semantics on `/dashboard/workflows` (definitions with rollup), and add a separate `/dashboard/workflows/recent` for the instances view.
  3. Implement: for each definition, run a count query `COUNT(*) WHERE definitionId = ?`. N+1 risk — use a single `GROUP BY definitionId` query for scale.
- **Is it "just a stub" or is scope missing?** Semantic pivot during port. Mechanical to restore.
- **Blockers**: depends on finding 014 (definition id type) — the `DefinitionId` reference in the rollup query must be consistent.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:26-30`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IWorkflowRepository.cs` + impl — add `GetInstanceCountsByDefinitionAsync(tenantId)` → `Dictionary<DefinitionId, int>`.
- Files to create: possibly new endpoint for "recent instances" if split.
- Tests to add:
  - `GetWorkflows_ReturnsOneEntryPerDefinition`
  - `GetWorkflows_IncludesInstanceCount`
  - `GetWorkflows_NPlusOne_SingleQuery` (assert one SQL query via EF Core logging).
  - `GetWorkflows_TenantIsolated`
- Estimated effort: 3h — rollup query 1h, endpoint rewrite 30m, tests 1.5h.

## References

- TS source: `packages/api/src/routes/dashboard/index.ts:64-84`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:26-30`
- Story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`
- Related findings: `014-workflow-definition-id-guid-mismatch.md`, `022-dashboard-summary-shape-drift.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: a3d2e7e
- **Notes**: `DashboardEndpoints.GetWorkflows` restored to the TS semantics:
  one row per workflow DEFINITION, each annotated with an
  `instanceCount`. Implementation uses a single `GROUP BY DefinitionId`
  query (no N+1) scoped to the ambient tenant. The old "first 20
  instances under the same URL" semantic is dropped.
