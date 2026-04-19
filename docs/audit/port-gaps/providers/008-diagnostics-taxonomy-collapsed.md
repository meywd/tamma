# Finding 008: Diagnostics taxonomy collapsed — seven columns dropped, tokens merged

**Scope**: providers
**Severity**: P1 (cross-request tracing broken; billing accuracy regression)
**Status**: Data-model regression
**Estimated port effort**: 6–8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/diagnostics-store.ts` and
archived SQL `database/archived-sql-migrations/014_provider_diagnostics.sql`.

- File: `packages/api/src/services/diagnostics-store.ts:16-36` (`DiagnosticsRecord`)
- The TS `DiagnosticsRecord` carried **17 fields**:

```typescript
// packages/api/src/services/diagnostics-store.ts (9e9a57c~1) — lines 16-36
export interface DiagnosticsRecord {
  id: string;
  accountId: string | null;
  eventType: string;           // 'provider:call' | 'provider:complete' | 'provider:error' | 'tool:invoke' | 'tool:complete' | 'tool:error'
  providerName: string;
  model: string | null;
  agentType: string | null;    // 'implementer' | 'reviewer' | ...
  projectId: string | null;
  engineId: string | null;
  taskId: string | null;
  taskType: string | null;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  costUsd: number;
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  correlationId: string | null;
  createdAt: string;
}
```

- The matching SQL columns from archived migration 014 (17 fields):

```sql
-- database/archived-sql-migrations/014_provider_diagnostics.sql — lines 10-30
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
  input_tokens INTEGER DEFAULT 0,
  output_tokens INTEGER DEFAULT 0,
  latency_ms INTEGER DEFAULT 0,
  cost_usd NUMERIC(12, 6) DEFAULT 0,
  success BOOLEAN NOT NULL DEFAULT false,
  error_code TEXT,
  error_message TEXT,
  correlation_id UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`
- Contract/behavior: The C# entity has **11 fields** — 7 are missing vs TS, plus a token-count collapse.

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs (current)
public class ProviderDiagnostic
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;   // was provider_name in TS
    public double RequestDurationMs { get; set; }       // was latency_ms
    public int TokensUsed { get; set; }                 // COLLAPSED from input_tokens + output_tokens
    public decimal Cost { get; set; }
    public Guid? TenantId { get; set; }                 // was account_id
    public string? Model { get; set; }
    public string? RequestType { get; set; }            // ≈ task_type but semantically ambiguous
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

- Missing columns / fields: `event_type`, `agent_type`, `project_id`, `engine_id`, `task_id`, `correlation_id`, `error_code`.
- `input_tokens` + `output_tokens` → a single `TokensUsed` scalar. Pricing differs by split (see finding 004), so this is a billing-accuracy regression.
- The ingest DTO `IngestDiagnosticRequest` (from `ProviderEndpoints.cs:272-290`) accepts `{ProviderKey, DurationMs, TokensUsed, Cost, Model, Success, Error}` — only 7 fields.

## 3. The gap

- **Correlation tracing**: TS `correlationId` let a single user-facing request produce multiple provider calls that could be joined by correlation ID. C# has no column; multi-attempt fallback chains cannot be reassembled post-hoc.
- **Project / engine attribution**: TS `project_id` + `engine_id` let the dashboard scope reports to a single project or a single Elsa engine instance. C# can only filter by tenant.
- **Task context**: TS `task_id` + `task_type` let reports distinguish "code-generation tasks cost more than context-scan tasks". C# has `RequestType` (ambiguous — not clear if it's task or event type) and no `task_id`.
- **Event type**: TS distinguished `tool:*` events from `provider:*` events. C# cannot tell a tool invocation from a provider call apart in the diagnostics stream.
- **Agent type**: TS carried the role (`implementer`, `reviewer`). C# dropped it — the report endpoint can no longer group by agent type (see finding 009).
- **Error codes**: TS `error_code` was a stable enum (`RATE_LIMITED`, `CONTEXT_TOO_LONG`, `AUTH_FAILED`, `NETWORK_ERROR`, etc.). C# has only a free-form `ErrorMessage` — grouping by error class is impossible.
- **Token accuracy**: TS `inputTokens + outputTokens` is critical for cost recomputation (different $/token rates). C# stores only the sum.

Error paths:
- TS: POST ingest rejected records missing `eventType` (one of 6 enum values) or `providerName` — see `diagnostics-store.ts:129-139`.
- C#: POST ingest (`IngestDiagnosticRequest`) has no `eventType` at all; requires only `ProviderKey`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 AC 1 includes the full TS schema verbatim (columns listed include `event_type`, `agent_type`, `project_id`, `engine_id`, `task_id`, `task_type`, `input_tokens`, `output_tokens`, `error_code`, `correlation_id`).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior.
  - [ ] No story — there is a story, and it is directly contradicted by the EF entity.

## 5. Status

- **Classification**: Data-model regression.
- **What's needed to finish**:
  1. Extend `ProviderDiagnostic` entity with the 7 missing columns.
  2. Split `TokensUsed` into `InputTokens`, `OutputTokens`. Compute `TotalTokens` as a `[NotMapped]` convenience.
  3. Write EF migration `AddMissingProviderDiagnosticColumns`.
  4. Extend `IngestDiagnosticRequest` DTO + `ProviderEndpoints.IngestDiagnostic`.
  5. Extend `DiagnosticsRepository.QueryAsync` / `AggregateAsync` to expose/filter on the new columns.
  6. Update `DiagnosticsService.GetReportAsync` to accept `GroupBy` enum (see finding 009).
- **Is it "just a stub" or is scope missing?** Both. The EF entity is a minimal scaffold; the scope was understood (the archived SQL was in the repo at time of port) and explicitly contracted by Story 9-2.
- **Blockers**: Depends on finding 023 (missing composite indexes); these should be added in the same migration.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:288-303`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/DiagnosticsRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:272-290`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Providers/IngestDiagnosticRequest.cs`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_AddDiagnosticsColumns.cs`
- Tests to add:
  - `IngestDiagnostic_PersistsAllColumns_RoundTrips`
  - `Diagnostics_InputOutputTokenSplit_SurvivesSerialization`
  - `Diagnostics_CorrelationIdIndex_EnablesTraceQuery`
- Estimated effort: 7h broken down as:
  - Entity + migration + repo: 3h
  - DTO + endpoint: 1h
  - Report groupBy wiring: 1.5h
  - Tests: 1.5h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (schema already done; DTO + endpoint mapping added now)
- **Commit**: `0dbccf9` `fix(providers): land P1/P2 diagnostics/health/validation/user-providers fixes [findings 008, 009, 010, 012, 013, 014, 018, 019]`
- **Notes**: The `ProviderDiagnostic` entity and `provider_diagnostics` table already carry the seven restored columns + `InputTokens`/`OutputTokens` split (added by the schema-hardening migration `20260419015726_SchemaHardeningPhase1`). The remediation here was the application-layer plumbing: `IngestDiagnosticRequest` extended with all seven optional fields plus `InputTokens`/`OutputTokens`/`ErrorCode`/`CorrelationId`. `MapDiagnostic` helper routes the new fields onto the entity columns; back-compat default attributes legacy `TokensUsed`-only payloads to `InputTokens` so per-token cost recomputation still works. Combined with finding 004 the cost pipeline now writes accurate per-input/per-output billing rows.

## References

- TS source: `packages/api/src/services/diagnostics-store.ts:16-36`, `packages/api/src/services/pg-diagnostics-store.ts:65-99` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:288-303`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md` AC 1
- Related findings: `004-cost-accounting-hardcoded-zero.md`, `009-diagnostics-report-groupby-dropped.md`, `010-diagnostics-batch-ingest-missing.md`, `023-diagnostics-missing-composite-indexes.md`
- Archived SQL migration: `database/archived-sql-migrations/014_provider_diagnostics.sql`
