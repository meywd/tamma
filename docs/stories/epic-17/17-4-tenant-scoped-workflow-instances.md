# Story 17.4: Tenant-Scoped Workflow Instances

Status: ready-for-dev

## Story

As a **platform engineer**,
I want ELSA workflow instances isolated per tenant,
so that one organization's running workflows, variables, and execution history are invisible to every other organization.

## Acceptance Criteria

1. `WorkflowInstance` interface (TypeScript side) gains a `tenantId: string` field
2. `IWorkflowStore.createInstance()` requires `tenantId` in the input
3. `IWorkflowStore.listInstances()` accepts a `tenantId` filter and returns only that tenant's instances
4. `IWorkflowStore.getInstance()` verifies the instance belongs to the requested tenant (or relies on RLS)
5. `InMemoryWorkflowStore` filters all query methods by `tenantId`
6. On the C# ELSA side, `StartWorkflowAsync` accepts a `tenantId` parameter and stores it in workflow variables
7. ELSA workflow variables include `TenantId` as a required variable set at workflow dispatch time
8. The ELSA `WorkflowSyncService` passes `tenantId` through when syncing workflow definitions and dispatching instances
9. Workflow status queries (`GetWorkflowStatusAsync`) are scoped to the requesting tenant
10. `WorkflowDefinition` is NOT tenant-scoped (definitions are global/shared across tenants; instances are tenant-scoped)
11. CLI/self-hosted mode passes `DEFAULT_TENANT_ID` for all workflow instances
12. The API workflow routes (`/api/workflows/*`) filter results by the request's tenant context
13. No cross-tenant workflow leakage: listing workflows for tenant A returns zero results from tenant B

## Technical Context

### Current Workflow Store

From `packages/api/src/persistence/workflow-store.ts`:

```typescript
export interface WorkflowInstance {
  id: string;
  definitionId: string;
  status: string;
  currentActivity?: string;
  variables: Record<string, unknown>;
  createdAt: number;
  updatedAt: number;
}

export interface IWorkflowStore {
  createInstance(instance: WorkflowInstance): Promise<WorkflowInstance>;
  updateInstance(id: string, update: Partial<WorkflowInstance>): Promise<WorkflowInstance | null>;
  getInstance(id: string): Promise<WorkflowInstance | null>;
  listInstances(options?: ListInstancesOptions): Promise<PaginatedResult<WorkflowInstance>>;
}
```

No tenant scoping exists. All instances are in one flat collection.

### ELSA Workflow Service (C# Side)

From `apps/tamma-elsa/src/Tamma.Api/Services/IElsaWorkflowService.cs`:

```csharp
public interface IElsaWorkflowService
{
    Task<string> StartWorkflowAsync(string workflowName, Dictionary<string, object> input);
    Task PauseWorkflowAsync(string instanceId);
    Task ResumeWorkflowAsync(string instanceId);
    Task CancelWorkflowAsync(string instanceId);
    Task<WorkflowStatus> GetWorkflowStatusAsync(string instanceId);
    Task SendSignalAsync(string instanceId, string signalName, object? payload = null);
}
```

The `input` dictionary is the entry point for passing tenant context into ELSA workflows.

### ELSA's Built-in Tenant Support

ELSA Workflows 3.x has a concept of "Tenants" via the `Elsa.Tenants` module. Before implementing custom tenant scoping, investigate whether ELSA's built-in tenant module can be leveraged:

- ELSA tenants use a `TenantId` property on workflow instances
- ELSA has `ITenantResolutionStrategy` for resolving the current tenant
- ELSA can filter workflow instances by tenant automatically

If ELSA's built-in module is sufficient, the C# side can use it directly. The TypeScript side still needs its own scoping in `IWorkflowStore`.

### Two-Layer Architecture

| Layer | Store | Tenant Scoping |
|-------|-------|----------------|
| TypeScript (Node.js) | `IWorkflowStore` (in-memory or PG) | `tenantId` field on `WorkflowInstance`, filtered in store methods |
| C# (ELSA Server) | ELSA's internal persistence | `TenantId` in workflow variables OR ELSA's built-in tenant module |
| Sync bridge | `WorkflowSyncService` | Passes `tenantId` from API request to ELSA dispatch |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/workflow-store.ts` | Add `tenantId` to `WorkflowInstance`, update `ListInstancesOptions`, update `InMemoryWorkflowStore` |
| `packages/api/src/routes/workflows/index.ts` | Filter workflow queries by tenant context from request |
| `apps/tamma-elsa/src/Tamma.Api/Services/IElsaWorkflowService.cs` | Add `tenantId` parameter to `StartWorkflowAsync` |
| `apps/tamma-elsa/src/Tamma.Api/Services/ElsaWorkflowService.cs` | Pass `TenantId` into workflow variables on dispatch |
| `apps/tamma-elsa/src/Tamma.Api/Services/WorkflowSyncService.cs` | Include tenant context in sync operations |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/persistence/__tests__/workflow-store-tenant.test.ts` | Tests for tenant-scoped workflow store |

## Implementation Plan

### Step 1: Update WorkflowInstance Interface

```typescript
export interface WorkflowInstance {
  id: string;
  definitionId: string;
  tenantId: string;  // NEW
  status: string;
  currentActivity?: string;
  variables: Record<string, unknown>;
  createdAt: number;
  updatedAt: number;
}
```

### Step 2: Update ListInstancesOptions

```typescript
export interface ListInstancesOptions {
  page?: number;
  pageSize?: number;
  definitionId?: string;
  tenantId?: string;  // NEW: filter by tenant
}
```

### Step 3: Update InMemoryWorkflowStore

```typescript
async createInstance(instance: WorkflowInstance): Promise<WorkflowInstance> {
  const record: WorkflowInstance = {
    ...instance,
    id: instance.id || randomUUID(),
    createdAt: instance.createdAt || Date.now(),
    updatedAt: instance.updatedAt || Date.now(),
  };
  this.instances.set(record.id, record);
  return record;
}

async getInstance(id: string): Promise<WorkflowInstance | null> {
  return this.instances.get(id) ?? null;
}

async listInstances(options?: ListInstancesOptions): Promise<PaginatedResult<WorkflowInstance>> {
  let items = [...this.instances.values()];

  // Filter by tenantId when provided
  if (options?.tenantId !== undefined) {
    items = items.filter((i) => i.tenantId === options.tenantId);
  }

  // Filter by definitionId when provided
  if (options?.definitionId !== undefined) {
    items = items.filter((i) => i.definitionId === options.definitionId);
  }

  const total = items.length;
  const page = options?.page ?? 1;
  const pageSize = options?.pageSize ?? 50;
  const start = (page - 1) * pageSize;
  const data = items.slice(start, start + pageSize);

  return { data, total };
}
```

### Step 4: Update Workflow API Routes

```typescript
// In packages/api/src/routes/workflows/index.ts
app.get('/api/workflows/instances', async (request, reply) => {
  const tenantId = getTenantIdFromRequest(request); // From middleware (Story 17.5)
  const instances = await workflowStore.listInstances({
    tenantId,
    ...request.query,
  });
  return reply.send(instances);
});
```

### Step 5: Update ELSA Workflow Service (C# Side)

```csharp
public interface IElsaWorkflowService
{
    Task<string> StartWorkflowAsync(
        string workflowName,
        Dictionary<string, object> input,
        string? tenantId = null);  // NEW

    // ... other methods unchanged, they operate on instanceId
    // which is already unique globally
}
```

Implementation passes `TenantId` into the workflow input variables:

```csharp
public async Task<string> StartWorkflowAsync(
    string workflowName,
    Dictionary<string, object> input,
    string? tenantId = null)
{
    // Inject TenantId into workflow variables
    if (tenantId != null)
    {
        input["TenantId"] = tenantId;
    }

    // Dispatch workflow with input variables
    // ...
}
```

### Step 6: ELSA Built-in Tenant Module (Investigation)

Before implementing custom scoping, check if ELSA's `Elsa.Tenants` NuGet package provides:

1. Automatic `TenantId` on workflow instances
2. `ITenantResolutionStrategy` that can read from HTTP headers or workflow variables
3. Built-in filtering on workflow instance queries by tenant

If available, wire it up via `Program.cs`:

```csharp
services.AddElsa(elsa =>
{
    elsa.UseTenants(tenants =>
    {
        tenants.UseResolutionStrategy<HeaderTenantResolutionStrategy>();
    });
});
```

Document findings in `.dev/findings/` regardless of the outcome.

## Implementation Notes

1. `WorkflowDefinition` remains global (not tenant-scoped). All tenants share the same workflow definitions. Only instances are isolated.
2. The `variables` field on `WorkflowInstance` already stores arbitrary data. `TenantId` is stored both as a top-level field (for efficient indexing/filtering) and inside `variables` (for ELSA activities to read).
3. For `getInstance(id)`, tenant scoping can be enforced two ways:
   - **Application-level**: Fetch the instance, check `tenantId` matches the request tenant, return null if mismatch
   - **Database-level**: RLS on a future PG workflow_instances table
   - Both should be used (defense-in-depth)
4. The `updateInstance()` and `getInstance()` methods take only `id` (not `tenantId`). For now, the caller must verify tenant ownership. In a PG-backed store with RLS, the database enforces this automatically.
5. ELSA's `PauseWorkflowAsync`, `ResumeWorkflowAsync`, `CancelWorkflowAsync` take `instanceId`. These should verify the instance belongs to the requesting tenant before acting. This verification happens in the API route handler, not in the ELSA service itself.

## Testing Strategy

### Unit Tests

Create `packages/api/src/persistence/__tests__/workflow-store-tenant.test.ts`:

1. `createInstance` stores `tenantId` correctly
2. `listInstances({ tenantId: 'A' })` returns only tenant A's instances
3. `listInstances({ tenantId: 'B' })` returns only tenant B's instances
4. `listInstances()` without `tenantId` returns all instances (backward compat)
5. `listInstances({ tenantId: 'A', definitionId: 'def1' })` filters by both
6. `getInstance(id)` returns instance regardless of tenant (no implicit filter — caller must verify)
7. Mixed tenant instances with pagination: correct page/total counts

### Integration Tests

8. Dispatch a workflow via API with tenant A context, list workflows with tenant B context => zero results
9. Dispatch workflows for multiple tenants, verify each tenant sees only their own
10. ELSA workflow variables contain `TenantId` after dispatch

### Backward Compatibility

11. Existing workflow tests pass without specifying `tenantId` (field is present with default value)
12. CLI mode dispatches workflows with `DEFAULT_TENANT_ID` in variables

## Dependencies

- **Story 17.1** (Tenant Model + Database Schema) — `tenants` table and `DEFAULT_TENANT_ID` constant
- Internal: `packages/api/src/persistence/workflow-store.ts`
- Internal: `apps/tamma-elsa/src/Tamma.Api/Services/`
- Internal: `packages/api/src/routes/workflows/`

## Estimated Effort

| Task | Hours |
|------|-------|
| Update WorkflowInstance + IWorkflowStore interfaces | 1 |
| Update InMemoryWorkflowStore | 1 |
| Update workflow API routes | 1.5 |
| Update IElsaWorkflowService (C#) | 1 |
| Update ElsaWorkflowService implementation (C#) | 1.5 |
| Investigate ELSA Tenants module | 1 |
| Unit tests (TypeScript) | 1.5 |
| Integration tests | 1.5 |
| **Total** | **10 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
