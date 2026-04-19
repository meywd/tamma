# Finding 029: `workflow_instances` diff — definition_id text→uuid, created_at BIGINT→timestamptz, nullable TenantId, RLS gone

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (partial — compound index added, RLS deferred)
- **Notes**: `Phase1` migration adds `IX_workflow_instances_TenantId_DefinitionId` and `IX_workflow_instances_TenantId_Status` compound indexes matching TS. **Not done**: revert `DefinitionId uuid → text` — the C# port treats workflow definitions as first-class entities with an FK; supporting external string IDs would require a parallel `ExternalDefinitionId TEXT` column. Documented as intentional design divergence. **Not done**: RLS — Phase-2 work.

## 1. What's in TS

Archived at `database/archived-sql-migrations/011_tenant_scoped_stores.sql`.

- File: `packages/api/database/migrations/011_tenant_scoped_stores.sql:47-79`
- Contract/behavior: workflow instance table. `definition_id` is a free-form TEXT (external workflow ID, possibly from ELSA or a third-party engine — not necessarily a UUID). Timestamps are `BIGINT` ms-epoch for cheap integer arithmetic. NOT NULL tenant_id with sentinel default. RLS enabled + tenant-change trigger.
- Key code (verbatim quote, annotated):

```sql
-- 011_tenant_scoped_stores.sql
CREATE TABLE IF NOT EXISTS workflow_instances (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  definition_id   TEXT NOT NULL,                                           -- ← TEXT, not UUID
  tenant_id       UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
                  REFERENCES tenants(id),
  status          TEXT NOT NULL DEFAULT 'pending',
  current_activity TEXT,
  variables       JSONB NOT NULL DEFAULT '{}',
  created_at      BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT,   -- ← ms-epoch BIGINT
  updated_at      BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT
);

CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_id ON workflow_instances (tenant_id);
CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_definition
  ON workflow_instances (tenant_id, definition_id);
CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_status
  ON workflow_instances (tenant_id, status);

ALTER TABLE workflow_instances ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow_instances FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON workflow_instances ...;

CREATE TRIGGER trg_prevent_tenant_change_workflow_instances
  BEFORE UPDATE ON workflow_instances FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

- Dependencies: `tenants`, `prevent_tenant_id_change()`.
- Tests that exercised this: workflow lifecycle tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:270-294, 692-700`
- Contract/behavior: `DefinitionId` is now `uuid` with an FK to `workflow_definitions(Id)`. Timestamps are `timestamptz`. TenantId is nullable. No RLS, no trigger.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "workflow_instances",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),                     // ← UUID, was TEXT
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),                          // ← nullable
        Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "pending"),
        CurrentActivity = table.Column<string>(type: "text", nullable: true),
        Variables = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),  // ← timestamptz
        UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
        StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),     // ← new
        CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)    // ← new
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_workflow_instances", x => x.Id);
        table.ForeignKey(
            name: "FK_workflow_instances_workflow_definitions_DefinitionId",
            column: x => x.DefinitionId, principalTable: "workflow_definitions", principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    });

migrationBuilder.CreateIndex(name: "IX_workflow_instances_DefinitionId_Status",
    table: "workflow_instances", columns: new[] { "DefinitionId", "Status" });
migrationBuilder.CreateIndex(name: "IX_workflow_instances_TenantId",
    table: "workflow_instances", column: "TenantId");
// Missing: (TenantId, DefinitionId) compound; RLS; trigger
```

There's also a later migration `20260417010406_WorkflowInstanceResult.cs` adding a `Result` field.

- Dependencies: `workflow_definitions` FK (new, enforced).
- Tests: none on RLS.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `definition_id` type | `TEXT` | `uuid` + FK to local `workflow_definitions` | External workflow IDs (from ELSA/Temporal) that aren't UUIDs cannot be stored; loss of "adapter" flexibility |
| `created_at`/`updated_at` | `BIGINT` ms-epoch | `timestamptz` | Integer arithmetic queries rewrite to timestamp; dashboards consuming ms-epoch break |
| `tenant_id` | NOT NULL with sentinel | nullable | Same issue as finding 026 |
| Compound index `(tenant_id, definition_id)` | present | absent — only `(DefinitionId, Status)` and `(TenantId)` | Per-tenant-per-definition listings don't hit a compound index |
| RLS + trigger | present | absent | See 020, 022 |
| `StartedAt`, `CompletedAt` | absent | **present** (net positive) | Useful for duration analytics |

- For a caller registering a workflow from ELSA with external id `"pr-review-workflow-v3"` (a string), TS stores it; C# rejects — it expects a UUID. The FK to `workflow_definitions` forces a two-step registration (first insert into `workflow_definitions`, then reference).
- For a caller at scale querying `WHERE tenant_id = ? AND definition_id = ?`, TS uses the compound partial index; C# uses the `TenantId` index and filters.

Error paths:
- TS: insert succeeds for any TEXT `definition_id`.
- C#: `22P02 invalid_text_representation` if `definition_id` isn't a UUID; `23503 foreign_key_violation` if no corresponding `workflow_definitions` row.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md`.
- Story alignment:
  - [x] Matches TS behavior (RLS, index)
  - [ ] Matches C# behavior (UUID FK breaks external-engine use case)
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (RLS, index) + semantic rewrite (UUID FK, timestamptz).
- **What's needed to finish**:
  1. Add RLS + trigger (findings 020, 022).
  2. Add `(TenantId, DefinitionId)` compound index.
  3. Decide whether external-engine integration requires a second free-form `ExternalDefinitionId TEXT` column alongside the UUID FK.
- **Is it "just a stub" or is scope missing?** Partial port; rewrote to EF idioms.
- **Blockers**: if external engine IDs need support, column must be added.

## Remediation

- Files to modify: `WorkflowInstance.cs` entity.
- Files to create: `20260418000015_WorkflowInstancesHardening.cs`.
- Tests to add: cross-tenant isolation; compound index usage.
- Estimated effort: 3h.

## References

- TS source: `database/archived-sql-migrations/011_tenant_scoped_stores.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`, `20260417010406_WorkflowInstanceResult.cs`
- Story: `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md`
- Related findings: `020`, `022`, `026-schema-engine-events-domain-events-rename.md`
