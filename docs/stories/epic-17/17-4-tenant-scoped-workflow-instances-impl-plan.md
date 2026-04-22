# Story 17-4: Tenant-Scoped Workflow Instances — Implementation Plan

## Overview

Scope ELSA workflow instances per tenant. Add `tenant_id` to the `workflow_instances` table, update `WorkflowInstance` / `IWorkflowStore` types, filter all query methods by tenant, and define the boundary where the C# ELSA side receives the tenant context. `WorkflowDefinition` is **not** tenant-scoped (definitions are global/shared — only instances are isolated).

Migration `011_tenant_scoped_stores.sql` is shared with Story 17-3 (event store). Story 17-3 owns the `engine_events` half of the file; Story 17-4 owns the `workflow_instances` half. Land both in a single PR.

Depends on: 17-1 (`tenants` table), 17-2 (RLS infrastructure, `prevent_tenant_id_change()` function, `tamma_app` role), 17-3 (shares migration 011). Blocks: 9-11 (Elsa diagnostics interceptors), 27-6 (ELSA prompt resolution).

---

## Step-by-Step Implementation Tasks

### Task 1: Migration SQL — `workflow_instances` half of 011 (1.5 hours)

**File to modify**: `database/migrations/011_tenant_scoped_stores.sql`

Append the workflow section. Must be idempotent (`IF NOT EXISTS`, `DROP POLICY IF EXISTS`) because 17-3 and 17-4 share the file and tests replay it.

```sql
-- =========================================================================
-- 2. Workflow Instances table (Story 17-4)
-- =========================================================================
CREATE TABLE IF NOT EXISTS workflow_instances (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  definition_id    TEXT NOT NULL,
  tenant_id        UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
                   REFERENCES tenants(id),
  status           TEXT NOT NULL DEFAULT 'pending',
  current_activity TEXT,
  variables        JSONB NOT NULL DEFAULT '{}',
  created_at       BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT,
  updated_at       BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT
);

CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_id
  ON workflow_instances (tenant_id);
CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_definition
  ON workflow_instances (tenant_id, definition_id);
CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_status
  ON workflow_instances (tenant_id, status);

ALTER TABLE workflow_instances ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow_instances FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_policy ON workflow_instances;
CREATE POLICY tenant_isolation_policy ON workflow_instances
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

DROP TRIGGER IF EXISTS trg_prevent_tenant_change_workflow_instances ON workflow_instances;
CREATE TRIGGER trg_prevent_tenant_change_workflow_instances
  BEFORE UPDATE ON workflow_instances
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

-- Grant to tamma_app role (from 17-2)
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON workflow_instances TO tamma_app;
  END IF;
END $$;
```

**Note — `workflow_definitions` is deliberately omitted.** Per AC-10 definitions are global. If a future PR persists definitions to PG, they go in their own un-RLS'd table.

**Commands**:
```bash
psql -f database/migrations/011_tenant_scoped_stores.sql  # idempotent replay
pnpm --filter @tamma/api test -- --run persistence/workflow-store
```

---

### Task 2: Update `WorkflowInstance` type + `IWorkflowStore` interface (1 hour)

**File to modify**: `packages/api/src/persistence/workflow-store.ts`

The in-memory store already carries `tenantId` (added by 17-5 scaffolding). This task formalises the interface contract so every query method requires or accepts tenant context.

Interface diff:

```typescript
export interface WorkflowInstance {
  id: string;
  definitionId: string;
  tenantId: string;              // required, no default at type level
  status: string;
  currentActivity?: string;
  variables: Record<string, unknown>;
  createdAt: number;
  updatedAt: number;
}

export interface ListInstancesOptions {
  page?: number;
  pageSize?: number;
  definitionId?: string;
  tenantId: string;              // REQUIRED — no cross-tenant listing
}

export interface IWorkflowStore {
  // Definitions are global — unchanged.
  upsertDefinition(def: WorkflowDefinition): Promise<WorkflowDefinition>;
  listDefinitions(): Promise<WorkflowDefinition[]>;
  getDefinition(id: string): Promise<WorkflowDefinition | null>;

  // Instances are tenant-scoped.
  createInstance(instance: WorkflowInstance): Promise<WorkflowInstance>;
  updateInstance(
    id: string,
    tenantId: string,              // NEW — caller must assert ownership
    update: Partial<Omit<WorkflowInstance, 'id' | 'tenantId'>>,
  ): Promise<WorkflowInstance | null>;
  getInstance(id: string, tenantId: string): Promise<WorkflowInstance | null>; // NEW param
  deleteInstance(id: string, tenantId: string): Promise<boolean>;              // NEW param
  listInstances(options: ListInstancesOptions): Promise<PaginatedResult<WorkflowInstance>>;
}
```

**Rationale**: `getInstance`, `updateInstance`, `deleteInstance` now take `tenantId` so application-level checks are enforced even if RLS is bypassed (defence-in-depth). The existing `InMemoryWorkflowStore.getInstance(id)` is the only signature break — every caller must be updated.

---

### Task 3: Update `InMemoryWorkflowStore` + add `PgWorkflowStore` skeleton (2 hours)

**File to modify**: `packages/api/src/persistence/workflow-store.ts`

`InMemoryWorkflowStore` changes:
- `createInstance()`: reject if `instance.tenantId` is missing or empty (no more silent `DEFAULT_TENANT_ID` fallback — the caller must supply it via `request.tenantId`).
- `getInstance(id, tenantId)`: return `null` if stored instance's `tenantId !== tenantId`.
- `updateInstance(id, tenantId, update)`: same ownership check; reject any attempt to mutate `tenantId` in `update`.
- `deleteInstance(id, tenantId)`: same ownership check.
- `listInstances(options)`: `tenantId` is now required; throw if absent (helps catch missing tenant context in tests).

**File to create**: `packages/api/src/persistence/pg-workflow-store.ts`

Skeleton only — RLS does the heavy lifting. All queries run under a connection where `SET LOCAL app.current_tenant_id = ...` has been applied by the tenant-context middleware (17-5). The store still passes `tenant_id` in `INSERT` / `WHERE` clauses as defence-in-depth.

```typescript
export class PgWorkflowStore implements IWorkflowStore {
  constructor(private readonly pool: pg.Pool) {}

  async createInstance(instance: WorkflowInstance): Promise<WorkflowInstance> {
    const row = await this.pool.query(
      `INSERT INTO workflow_instances
        (id, definition_id, tenant_id, status, current_activity, variables, created_at, updated_at)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
       RETURNING *`,
      [/* ... */],
    );
    return this._mapRow(row.rows[0]);
  }

  async getInstance(id: string, tenantId: string): Promise<WorkflowInstance | null> {
    // RLS already filters; explicit tenant_id check is defence-in-depth.
    const res = await this.pool.query(
      `SELECT * FROM workflow_instances WHERE id = $1 AND tenant_id = $2`,
      [id, tenantId],
    );
    return res.rows[0] ? this._mapRow(res.rows[0]) : null;
  }

  async listInstances(opts: ListInstancesOptions): Promise<PaginatedResult<WorkflowInstance>> {
    // tenantId required — RLS also enforces but we pass it explicitly.
    // SELECT ... WHERE tenant_id = $1 [AND definition_id = $2] ORDER BY created_at DESC LIMIT $3 OFFSET $4
  }
  // updateInstance, deleteInstance likewise tenant-filtered.
}
```

**Commands**:
```bash
pnpm --filter @tamma/api test -- --run workflow-store
pnpm --filter @tamma/api build
```

---

### Task 4: Wire tenant context into API workflow routes (1.5 hours)

**Files to modify**:
- `packages/api/src/routes/workflows/index.ts` (or equivalent route file — search for `workflowStore.listInstances`)

Every route handler that touches workflow instances must read `request.tenantId` (set by the middleware from 17-5) and pass it to the store:

```typescript
app.get('/api/workflows/instances', async (request, reply) => {
  const tenantId = request.tenantId;
  if (tenantId === undefined) {
    return reply.status(403).send({ error: 'Tenant context required' });
  }
  const { page, pageSize, definitionId } = request.query as ListInstancesQuery;
  const result = await workflowStore.listInstances({ tenantId, page, pageSize, definitionId });
  return reply.send(result);
});

app.post('/api/workflows/instances', async (request, reply) => {
  const tenantId = request.tenantId!;
  const body = request.body as CreateInstanceBody;
  const instance = await workflowStore.createInstance({
    ...body,
    tenantId,                                  // injected from request context
    variables: { ...body.variables, TenantId: tenantId },  // also inside variables for ELSA activities
  });
  // Then dispatch to ELSA with tenantId…
  await elsaClient.startWorkflow(body.definitionName, { ...body.variables, TenantId: tenantId });
  return reply.send(instance);
});

app.get('/api/workflows/instances/:id', async (request, reply) => {
  const tenantId = request.tenantId!;
  const { id } = request.params as { id: string };
  const instance = await workflowStore.getInstance(id, tenantId);
  if (instance === null) return reply.status(404).send({ error: 'Not found' });
  return reply.send(instance);
});
```

Pause / resume / cancel / signal endpoints follow the same pattern: look up the instance via `getInstance(id, tenantId)`, 404 if not found, then forward the call to the ELSA service. Cross-tenant leakage is blocked at the application layer regardless of what ELSA's own persistence does.

**Commands**:
```bash
pnpm --filter @tamma/api test -- --run routes/workflows
```

---

### Task 5: C# ELSA boundary — document the contract (1 hour)

This story does **not** modify C#. It defines the contract the C# side must honour, which Story 9-11 and Story 27-6 will implement.

**File to modify**: `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md` — append an "ELSA Boundary" section documenting:

1. **Dispatch contract**: The TypeScript API, when calling ELSA's HTTP dispatch endpoint, always injects `TenantId` into the workflow input variables. Every workflow activity that queries Tamma APIs or the DB reads `workflow.Variables["TenantId"]` and threads it into outbound calls.
2. **Callback contract**: When ELSA calls back into Tamma APIs (e.g., `POST /api/v1/diagnostics`), the C# code **must** include `TenantId` in the request (either as a header `X-Tenant-Id` or in the request body). The API's tenant-context middleware will resolve it via the inbound JWT / service token path — the C# side must have a JWT that carries the correct `tenantId` claim.
3. **ELSA's native tenant module is out of scope.** Investigated in the story text; the decision is to use workflow variables rather than Elsa.Tenants to avoid coupling to ELSA 3.x internals. The finding will be logged in `.dev/findings/elsa-tenants-evaluation.md` as part of Story 9-11.
4. **ELSA's own persistence** (`Elsa_WorkflowInstances` table in the ELSA schema) remains un-scoped — operational queries against it should be considered cross-tenant and restricted to platform operators. Tenant isolation of user-visible workflow data happens in Tamma's `workflow_instances` table and the API layer.

Do **not** edit `apps/tamma-elsa/` as part of this story. File a follow-up referenced from 9-11.

---

## Test Strategy

**File to create**: `packages/api/src/persistence/__tests__/workflow-store-tenant.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `createInstance` with `tenantId: 'A'` stores correctly | Row has `tenantId === 'A'` |
| 2 | `createInstance` without `tenantId` throws | Caller error — no silent default |
| 3 | `listInstances({ tenantId: 'A' })` returns only A's instances | Zero B instances in result |
| 4 | `listInstances({ tenantId: 'B' })` after seeding both tenants | Only B |
| 5 | `listInstances({ tenantId: 'A', definitionId: 'def1' })` | Filters by both |
| 6 | `getInstance(id, 'A')` on an A-owned instance | Returns instance |
| 7 | `getInstance(id, 'B')` on an A-owned instance | Returns `null` (cross-tenant denied) |
| 8 | `updateInstance(id, 'B', ...)` on A-owned instance | Returns `null`, no mutation |
| 9 | `updateInstance(id, 'A', { tenantId: 'B' })` | Rejects — tenantId immutable |
| 10 | `deleteInstance(id, 'B')` on A-owned instance | Returns `false`, row still present |
| 11 | Concurrent `createInstance` for tenants A and B | Both succeed, listing each is isolated |
| 12 | Pagination across mixed tenants | `total` reflects only the requested tenant |

**Integration test — RLS end-to-end** (in `packages/api/src/persistence/__tests__/pg-workflow-store.integration.test.ts`, guarded by `TAMMA_TEST_PG_URL`):

| # | Test | Assertion |
|---|------|-----------|
| 13 | Two tenants, each starts 3 workflows, list under tenant A's session | 3 results, all A |
| 14 | With `app.current_tenant_id` unset | RLS returns 0 rows even without explicit filter |
| 15 | Attempt `UPDATE workflow_instances SET tenant_id = ...` | Trigger `prevent_tenant_id_change()` rejects |
| 16 | API route: authenticated as tenant A, `GET /api/workflows/instances` | Body contains only A instances, never B |

**Commands**:
```bash
pnpm --filter @tamma/api test -- --run workflow-store-tenant
pnpm --filter @tamma/api test:integration -- --run pg-workflow-store
```

---

## Rollout

**Existing DB state**: fresh install — no production rows. If a dev environment has `workflow_instances` rows from before migration 011 (unlikely; table did not exist prior), the migration's `DEFAULT '00000000-0000-0000-0000-000000000000'` assigns them to the `DEFAULT_TENANT_ID` tenant automatically. No backfill script needed.

**Decision**: no legacy-tenant backfill. Per the CLAUDE.md note ("No migration anxiety: App is not in production with users"), we take the simple path — any pre-existing rows get `DEFAULT_TENANT_ID`, readable only under the default tenant's session context. CLI / self-hosted mode runs under `DEFAULT_TENANT_ID` (see 17-5), so existing local workflow data stays visible.

**Deploy order**:
1. Merge 17-3 + 17-4 PR (migration 011)
2. Deploy API — migration runs on startup
3. Verify `GET /api/workflows/instances` under a test tenant returns zero cross-tenant rows
4. Story 9-11 unblocks (ELSA interceptors can now call the tenant-scoped API)

**Rollback**: if RLS breaks production queries, the emergency escape hatch is to connect as `postgres` (superuser bypasses RLS). Not tenant-scoping at the application layer is safer than a broken deploy. Document this in the story's "operational notes".

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/persistence/pg-workflow-store.ts` | Postgres-backed `IWorkflowStore` skeleton |
| 2 | `packages/api/src/persistence/__tests__/workflow-store-tenant.test.ts` | Unit tests for tenant filtering |
| 3 | `packages/api/src/persistence/__tests__/pg-workflow-store.integration.test.ts` | RLS end-to-end tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `database/migrations/011_tenant_scoped_stores.sql` | Append `workflow_instances` section (coordinated with 17-3) |
| 2 | `packages/api/src/persistence/workflow-store.ts` | Tighten `IWorkflowStore` signatures: `getInstance`/`updateInstance`/`deleteInstance` require `tenantId`; `ListInstancesOptions.tenantId` required |
| 3 | `packages/api/src/routes/workflows/index.ts` | Read `request.tenantId`, pass to every store call, inject `TenantId` into dispatched ELSA variables |
| 4 | `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md` | Append "ELSA Boundary" section documenting C# contract |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| 1. Migration SQL (`workflow_instances` half of 011) | 1.5 |
| 2. `IWorkflowStore` interface tightening | 1 |
| 3. `InMemoryWorkflowStore` + `PgWorkflowStore` skeleton | 2 |
| 4. API route wiring (tenant context → store + ELSA dispatch) | 1.5 |
| 5. C# boundary documentation | 1 |
| 6. Unit + integration tests | 1 |
| **Total** | **8 hours** |

Matches the Layer-2 Team A estimate of ~8 hours for this story.

---

## Dependencies

- **Story 17-1** — `tenants` table and `DEFAULT_TENANT_ID` constant
- **Story 17-2** — `tamma_app` role, `prevent_tenant_id_change()` function, RLS infrastructure
- **Story 17-3** — shares migration 011; merge together
- **Story 17-5** — `request.tenantId` populated by tenant-context middleware

## Blocks

- **Story 9-11** — ELSA diagnostics interceptors (needs tenant-scoped workflow data)
- **Story 27-6** — ELSA prompt resolution (needs tenant resolution inside workflow activities)

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-15 | 1.0 | Initial implementation plan | Architecture Team |
