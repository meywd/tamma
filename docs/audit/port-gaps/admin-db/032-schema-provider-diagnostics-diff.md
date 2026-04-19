# Finding 032: `provider_diagnostics` column collapse — input/output tokens conflated, 8 columns dropped, 6 indexes missing (billing accuracy regression)

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (columns + indexes; FK deferred)
- **Notes**: `Phase1` migration adds 8 columns: `InputTokens`, `OutputTokens`, `CorrelationId`, `EngineId`, `TaskId`, `TaskType`, `AgentType`, `ProjectId`, `ErrorCode`. `TokensUsed` retained for back-compat. Five new indexes added matching TS: `(TenantId, CreatedAt)`, `(EngineId, CreatedAt)`, `(Model, CreatedAt)`, `(RequestType, CreatedAt)`, partial on `CorrelationId WHERE NOT NULL`. Plus `ix_provider_diagnostics_budget` partial on `(TenantId, CreatedAt) WHERE Success = true`. **Not done**: FK on `TenantId → tenants(Id)` — diagnostics is a write-once event sink that may receive payloads referencing tenants not yet materialised; firehose ingestion model. `BudgetServiceTests` and `DiagnosticsAggregationTests` were updated to seed tenants regardless (defensive; future-proofs if FK is added later).

## 1. What's in TS

Archived at `database/archived-sql-migrations/014_provider_diagnostics.sql`.

- File: `packages/api/database/migrations/014_provider_diagnostics.sql`
- Contract/behavior: rich per-call diagnostics with 18 columns supporting billing, cross-step tracing (via `correlation_id`), cost reporting (`cost_usd NUMERIC(12,6)`), per-engine/per-task attribution, separate success/failure error context, and 7 indexes — including a partial budget index `WHERE success = true` for fast "successful spend this month" queries.
- Key code (verbatim quote, annotated):

```sql
-- 014_provider_diagnostics.sql
CREATE TABLE IF NOT EXISTS provider_diagnostics (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id),
  event_type TEXT NOT NULL,
  provider_name TEXT NOT NULL,
  model TEXT,
  agent_type TEXT,
  project_id TEXT,
  engine_id TEXT,
  task_id TEXT,
  task_type TEXT,
  input_tokens INTEGER DEFAULT 0,                   -- ← separate
  output_tokens INTEGER DEFAULT 0,                  -- ← separate
  latency_ms INTEGER DEFAULT 0,
  cost_usd NUMERIC(12, 6) DEFAULT 0,                -- ← 6 decimal places
  success BOOLEAN NOT NULL DEFAULT false,
  error_code TEXT,
  error_message TEXT,
  correlation_id UUID,                              -- ← cross-step tracing
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_diagnostics_account_created ON provider_diagnostics (account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_provider ON provider_diagnostics (provider_name, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_model ON provider_diagnostics (model, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_event_type ON provider_diagnostics (event_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_engine ON provider_diagnostics (engine_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_correlation ON provider_diagnostics (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_diagnostics_budget ON provider_diagnostics (account_id, created_at) WHERE success = true;
```

- Dependencies: `tenants(id)` FK.
- Tests that exercised this: diagnostics ingestion + budget query tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:117-136, 595-598`
- Contract/behavior: narrowed to 11 columns. `ProviderKey` replaces `provider_name`. `TokensUsed` single int conflates input+output. `Cost` is `numeric(18,6)`. Lost `agent_type`, `project_id`, `engine_id`, `task_id`, `task_type`, `event_type`, `error_code`, `correlation_id`. Only one index: `(ProviderKey, CreatedAt)`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "provider_diagnostics",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        ProviderKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),  // ← was provider_name
        RequestDurationMs = table.Column<double>(type: "double precision", nullable: false),                   // ← was latency_ms (int)
        TokensUsed = table.Column<int>(type: "integer", nullable: false),                                      // ← was input_tokens + output_tokens
        Cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),         // ← precision widened
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),
        Model = table.Column<string>(type: "text", nullable: true),
        RequestType = table.Column<string>(type: "text", nullable: true),                                      // ← was event_type
        Success = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
        ErrorMessage = table.Column<string>(type: "text", nullable: true),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
        // NO agent_type, project_id, engine_id, task_id, task_type, error_code, correlation_id
    },
    constraints: table => { table.PrimaryKey("PK_provider_diagnostics", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_provider_diagnostics_ProviderKey_CreatedAt",
    table: "provider_diagnostics",
    columns: new[] { "ProviderKey", "CreatedAt" });
// Missing: account_created, model, event_type, engine, correlation, budget(partial)
```

- Dependencies: no FK.
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `input_tokens` + `output_tokens` separate | two INTEGER columns | **one `TokensUsed`** | **Billing accuracy regression** — Anthropic/OpenAI price input ≠ output tokens (e.g. Claude Sonnet input $3/MTok vs output $15/MTok). Collapsing them makes exact cost reconstruction impossible. `cost_usd` is recorded at ingest time, so this works for summary billing but breaks post-hoc reconciliation |
| `correlation_id` | present with partial index | **absent** | Cross-step tracing (context-scan → plan → implement correlation) is impossible; cannot answer "show me every API call for this PR" |
| `engine_id`, `task_id`, `task_type` | present, indexed | **absent** | Per-engine usage reports break |
| `agent_type`, `project_id` | present | **absent** | Per-role usage reports (developer vs tester spend) break |
| `event_type` vs `RequestType` | `TEXT`, indexed with `created_at` | `RequestType` text, not indexed | Slower event-type breakdown queries |
| `error_code` | present | **absent** | Structured error classification gone |
| Partial budget index `WHERE success = true` | present | **absent** | "How much did tenant X spend this month?" scans failed attempts too |
| FK on `account_id`/`TenantId` | present | absent | Orphaned diagnostics when tenant deleted |

- For a caller running `SELECT SUM(cost_usd) FROM provider_diagnostics WHERE account_id = ? AND created_at > NOW() - INTERVAL '30 days' AND success = true`, TS hits the partial `idx_diagnostics_budget` — O(log n) on active rows; C# does a full-table scan filtered in memory.
- For a caller correlating a workflow span, TS uses `correlation_id` to stitch together context-scan → plan → implement → review; C# cannot.
- In production with existing data / deployed clients, this means:
  - Billing reports show only total cost, not input-vs-output-cost split.
  - Per-engine/per-task cost attribution is gone.
  - Cross-step traces rely on application logs only (OpenSearch, not DB).

Error paths: none at write time — the regressions surface as "columns missing" downstream.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/9-2-provider-diagnostics.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (the biggest single-table loss in this audit after RLS)
- **What's needed to finish**:
  1. Split `TokensUsed` into `InputTokens` + `OutputTokens`.
  2. Add `CorrelationId uuid`, `EngineId`, `TaskId`, `TaskType`, `AgentType`, `ProjectId`, `ErrorCode` columns.
  3. Add six indexes matching TS.
  4. Add partial budget index.
  5. Add FK on `TenantId` with `ON DELETE SET NULL` (preserve billing history).
- **Is it "just a stub" or is scope missing?** Partial port; structural depth dropped.
- **Blockers**: existing `TokensUsed` rows need either split (default 0 for output) or deprecation.

## Remediation

- Files to modify: `ProviderDiagnostic.cs` entity, `DiagnosticsService` ingestion.
- Files to create: `20260418000018_ProviderDiagnosticsRestore.cs`.
- Tests to add: budget query uses partial index; correlation trace stitches multi-step; input+output token sum matches `TokensUsed` for migrated rows.
- Estimated effort: 3h.

## References

- TS source: `database/archived-sql-migrations/014_provider_diagnostics.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-9/9-2-provider-diagnostics.md`
- Related findings: `033-schema-provider-health-diff.md`
