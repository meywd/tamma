# Finding 026: `engine_events` → `domain_events` rename with lost timestamp BIGINT, partial issue-number index

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 4h

## 1. What's in TS

Archived at `database/archived-sql-migrations/011_tenant_scoped_stores.sql`.

- File: `packages/api/database/migrations/011_tenant_scoped_stores.sql:10-42`
- Contract/behavior: DCB event store with **two** timestamp fields — `timestamp BIGINT` (millisecond epoch, used for ms-precision replay per CLAUDE.md's "Date/Time Handling" section) and `created_at TIMESTAMPTZ` (Postgres-native, for RLS timestamp comparison and index ordering). The `issue_number` column is indexed via a **partial index** `WHERE issue_number IS NOT NULL` so per-issue lookups skip the many events without one. RLS enabled, tenant_id NOT NULL with sentinel default.
- Key code (verbatim quote, annotated):

```sql
-- 011_tenant_scoped_stores.sql
CREATE TABLE IF NOT EXISTS engine_events (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  type          TEXT NOT NULL,
  timestamp     BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT,  -- ← ms-precision
  tenant_id     UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
                REFERENCES tenants(id),
  issue_number  INTEGER,
  data          JSONB NOT NULL DEFAULT '{}',
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_id ON engine_events (tenant_id);
CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_issue
  ON engine_events (tenant_id, issue_number)
  WHERE issue_number IS NOT NULL;                                 -- ← partial, compound
CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_type
  ON engine_events (tenant_id, type);

ALTER TABLE engine_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE engine_events FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON engine_events
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

CREATE TRIGGER trg_prevent_tenant_change_engine_events
  BEFORE UPDATE ON engine_events FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

- Dependencies: `tenants`, `prevent_tenant_id_change()` function.
- Tests that exercised this: DCB event replay tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:34-49, 496-504`
- Contract/behavior: renamed to `domain_events` (which better reflects CLAUDE.md's event schema). Drops `timestamp BIGINT` entirely — only `CreatedAt TIMESTAMPTZ` remains. Adds `Tags jsonb` and `Metadata jsonb` (CLAUDE.md compliance). RLS absent (finding 020). The compound partial index on `(tenant_id, issue_number) WHERE issue_number IS NOT NULL` is missing; instead there's an unfiltered index on `(Type, CreatedAt)`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "domain_events",   // ← renamed from engine_events
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),                    // ← was NOT NULL
        IssueNumber = table.Column<int>(type: "integer", nullable: true),
        Tags = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),       // ← new
        Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),   // ← new
        Data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
        // NO timestamp BIGINT
    },
    constraints: table => { table.PrimaryKey("PK_domain_events", x => x.Id); });

migrationBuilder.CreateIndex(name: "IX_domain_events_TenantId", table: "domain_events", column: "TenantId");
migrationBuilder.CreateIndex(name: "IX_domain_events_Type_CreatedAt",
    table: "domain_events", columns: new[] { "Type", "CreatedAt" });
// Missing partial index on (TenantId, IssueNumber) WHERE IssueNumber IS NOT NULL
// No RLS policy, no trigger.
```

- Dependencies: no FK to tenants visible; no RLS.
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| Name | `engine_events` | `domain_events` | Any external consumer (ELSA workflows, ingestion scripts) querying the old name breaks silently (Program.cs DROP list includes both, so fresh installs are OK) |
| `timestamp BIGINT` | present (ms epoch) | **absent** | CLAUDE.md says "ISO 8601 with millisecond precision" — `timestamp with time zone` achieves that, so the replacement is arguably fine. However, ms-epoch integer arithmetic (sorting, window bucketing) is cheaper than timestamptz; query patterns that did `WHERE timestamp BETWEEN ? AND ?` with plain ints must rewrite |
| `tags jsonb`, `metadata jsonb` | absent in 011 (added in app code) | **present** (net positive, matches CLAUDE.md schema) | Good — C# is the canonical DCB shape |
| `tenant_id` nullability | NOT NULL with sentinel default | nullable, no default | Inserts without tenant context don't fail; they write a row with `TenantId=null` that will be visible to all tenants once RLS is restored |
| Partial issue_number index | `(tenant_id, issue_number) WHERE issue_number IS NOT NULL` | **absent** | Per-issue event replay (dominant query on the engine replay path) falls back to seq scan or the `TenantId`-only index |
| RLS + trigger | present | **absent** | See findings 020, 022 |

- For a caller running a per-issue replay query `SELECT * FROM domain_events WHERE tenant_id = ? AND issue_number = ? ORDER BY created_at`, TS hits the compound partial index; C# scans the `IX_domain_events_TenantId` range and filters.
- In production with existing data / deployed clients, this means: event replay latency grows linearly with total events per tenant instead of with per-issue events.

Error paths: none at write time; performance + isolation at read time.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-3-tenant-scoped-event-store.md`, CLAUDE.md "Event Sourcing (DCB Pattern)".
- Story alignment:
  - [x] Matches TS behavior (index + RLS regressions)
  - [x] Matches C# behavior (Tags/Metadata additions, rename)
  - [ ] Describes a third behavior
  - [ ] No story

Both are partially right — C# picked up CLAUDE.md DCB fields but dropped the perf/isolation scaffolding.

## 5. Status

- **Classification**: Data-model regression (RLS + indexes) + semantic rewrite (naming + fields — net positive).
- **What's needed to finish**:
  1. Add partial index `IX_domain_events_tenant_issue` on `(TenantId, IssueNumber) WHERE "IssueNumber" IS NOT NULL`.
  2. Make `TenantId` NOT NULL (decide: sentinel default or middleware enforcement).
  3. Add RLS policy + trigger (finding 020, 022).
- **Is it "just a stub" or is scope missing?** Partial port.
- **Blockers**: coordinated with finding 020/022.

## Remediation

- Files to modify: `TammaDbContext` event mapping.
- Files to create: `20260418000014_DomainEventsIssueIndex.cs`.
- Tests to add: per-issue replay `EXPLAIN`; null-tenant insert behavior.
- Estimated effort: 4h.

## References

- TS source: `database/archived-sql-migrations/011_tenant_scoped_stores.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-17/17-3-tenant-scoped-event-store.md`
- Related findings: `020-schema-rls-policies-missing.md`, `022-schema-prevent-tenant-id-change-trigger.md`
- CLAUDE.md section: "Event Sourcing (DCB Pattern)"
