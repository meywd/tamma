# Finding 005: Budget enforcement is a no-op — `InMemoryBudgetConfigProvider` returns zero

**Scope**: providers
**Severity**: P0 (cost-runaway control plane non-functional)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 4–6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/diagnostics-store.ts` and
`git show 9e9a57c~1:packages/api/src/routes/settings/diagnostics-routes.ts`.

- TS exposed budget *status* but not budget *config* via this surface. The budget limit was supplied per-call as a query parameter: `GET /diagnostics/budget/:accountId?limit=100` (see `diagnostics-routes.ts:168-183`). The TS store computed `spent = SUM(cost_usd)` (PgDiagnosticsStore.getBudget) and returned `{spent, limit, remaining, percentUsed}`. Budget *enforcement* (cutting off runaway spend) was handled upstream at the cost-monitor tier inside the TS engine, which maintained per-account thresholds in a long-lived in-process config object seeded from a config file, and halted workflow continuation when `percentUsed >= 100`.

```typescript
// packages/api/src/services/pg-diagnostics-store.ts (9e9a57c~1) — lines 219-232
async getBudget(accountId: string, limitUsd: number): Promise<BudgetStatus> {
  const result = await this.pool.query<{ total_spent: string }>(
    `SELECT COALESCE(SUM(cost_usd), 0)::NUMERIC(12,6) as total_spent
     FROM provider_diagnostics
     WHERE account_id = $1`,
    [accountId],
  );

  const spent = parseFloat(result.rows[0]?.total_spent ?? '0');
  const remaining = Math.max(0, limitUsd - spent);
  const percentUsed = limitUsd > 0 ? (spent / limitUsd) * 100 : 0;
  return { spent, limit: limitUsd, remaining, percentUsed };
}
```

```typescript
// packages/api/src/routes/settings/diagnostics-routes.ts (9e9a57c~1) — lines 168-183
app.get('/diagnostics/budget/:accountId', async (request, reply) => {
  const { accountId } = request.params as { accountId: string };
  const query = request.query as { limit?: string };
  ...
  const limitUsd = query.limit ? parseFloat(query.limit) : 100;
  ...
  const budget = await store.getBudget(accountId, limitUsd);
  return reply.send(budget);
});
```

- TS workflow halt: the TS engine (`packages/orchestrator/` at the pre-delete snapshot) consulted `budget.percentUsed` before each step.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/InMemoryBudgetConfigProvider.cs:18-30`
- Contract/behavior: Budget config is stored in a `ConcurrentDictionary<Guid, BudgetConfig>` that is never populated. No endpoint writes to it. Every `GetConfig` call for an unknown account returns `LimitUsd=0m, AlertThreshold=0.8`, effectively disabling budget enforcement.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/InMemoryBudgetConfigProvider.cs — lines 18-30
public BudgetConfig GetConfig(Guid accountId)
{
    if (_configs.TryGetValue(accountId, out var cfg))
        return cfg;

    // Sensible fallback — zero cap, effectively no budget enforcement.
    var now = DateTime.UtcNow;
    return new BudgetConfig(
        LimitUsd: 0m,
        AlertThreshold: 0.8,
        PeriodStart: now - DefaultPeriod,
        PeriodEnd: now + DefaultPeriod);
}
```

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs:137-164`
- `GetBudgetAsync` fetches config from `_budgetProvider`, then computes `isOver = spent > cfg.LimitUsd && cfg.LimitUsd > 0`. Because `LimitUsd` is always `0` for every tenant, `isOver` is always `false` and `ShouldAlert` is always `false` (unless `spent > 0 && LimitUsd > 0` — which cannot hold).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs — lines 145-151
var remaining = Math.Max(0m, cfg.LimitUsd - spent);
var percentUsed = cfg.LimitUsd > 0
    ? (double)(spent / cfg.LimitUsd) * 100.0
    : 0.0;
...
var isOver = spent > cfg.LimitUsd && cfg.LimitUsd > 0;
var shouldAlert = isOver || (cfg.LimitUsd > 0 && fraction >= cfg.AlertThreshold);
```

- No `PUT /api/providers/diagnostics/budget/{accountId}` endpoint exists. No `budget_config` table, no EF entity. The `SetConfig(Guid, BudgetConfig)` method on the provider is unreachable from HTTP callers.
- Dependencies: `IBudgetConfigProvider` registered as singleton, `DiagnosticsService` injects it.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Diagnostics/DiagnosticsServiceTests.cs` uses a test-only `IBudgetConfigProvider` impl that returns a configured `LimitUsd > 0`. Production path has no such impl.

## 3. The gap

- TS: budget limit supplied by caller per request; spend is real (non-zero — see finding 004); workflow halt at `100%`.
- C#: budget limit always `0` in prod; spend is always `0` (finding 004); alerts and "IsOverBudget" are always `false`. `GET /diagnostics/budget/{accountId}` returns `{limit:0, spent:0, remaining:0, percentUsed:0, shouldAlert:false, isOverBudget:false}` for every tenant.
- For a caller sending `GET /api/providers/diagnostics/budget/<tenantId>`:
  - TS: a 422 if `limit` query missing, otherwise the computed status.
  - C#: a 200 with all-zero status, regardless of actual LLM spend.
- In production with existing data / deployed clients, this means: a runaway tenant could burn through unlimited provider spend with zero warning. The observable control-plane surface says "everything fine".

Error paths:
- TS error path: missing `limit` param → `400 {error: 'limit must be a positive number'}`.
- C# error path: bad UUID → `400 {error: 'accountId must be a GUID.'}`. Non-existent tenant → `200` with zeros.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 AC 2: "`GET /api/v1/diagnostics/budget/:accountId` — check current budget status against limits." Says **limits** (plural, persisted), not "caller-supplied limit via query string". The TS impl actually did the latter; the C# scaffold tried to do the former but forgot to persist.
- Story 9-2 AC 4: "`DiagnosticsProcessor` is updated to write to the diagnostics service in addition to (or instead of) the cost tracker." The cost tracker is where enforcement lived in TS; moving it to the C# diagnostics service means persisting the budget config too.
- Story alignment:
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.
  - [x] Describes a third behavior — story describes a persisted-budget-limit model that matches C#'s **shape** but not its **fill**. C# has the interface, the service, the DTO; it has no endpoint, no table, no default seed.
  - [x] No story — spec gap for the budget-config CRUD endpoints.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Scope was understood (the interface + service exist) but the persistence, seed, and endpoint layers were never added.
- **What's needed to finish**:
  1. Add `budget_configs` table: `(tenantId UUID PK, limit_usd DECIMAL(12,6), alert_threshold DOUBLE, period_days INT, created_at, updated_at)`.
  2. Write EF entity + repository + migration.
  3. Add endpoints:
     - `GET /api/providers/budget/{tenantId}` (resolve config only)
     - `PUT /api/providers/budget/{tenantId}` — `SettingsManage` gated
     - Keep `GET /api/providers/diagnostics/budget/{accountId}` (status lookup)
  4. Replace `InMemoryBudgetConfigProvider` with `PgBudgetConfigProvider` reading/writing the table.
  5. Publish a `BUDGET.THRESHOLD_CROSSED.WARNING` domain event when `percentUsed >= alert_threshold * 100`.
  6. Wire workflow halt: before Elsa's `LlmCall` activity invokes the provider, it fetches budget status; if `IsOverBudget`, the activity short-circuits with a terminal error.
- **Is it "just a stub" or is scope missing?** Both. The scaffold is a stub (`InMemoryBudgetConfigProvider.SetConfig` is unreachable from HTTP), and the persistence/endpoint layer is missing scope.
- **Blockers**: Depends on finding 004 — even with budgets persisted, all costs are $0, so enforcement never triggers.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs` (no logic change, just swap provider)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (DI + route registration)
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs` (new endpoints)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/BudgetConfig.cs` (entity, distinct from the in-memory DTO)
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/BudgetConfigRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IBudgetConfigRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_BudgetConfigs.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/PgBudgetConfigProvider.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Settings/UpdateBudgetConfigRequest.cs`
- Tests to add:
  - `BudgetConfigRepository_UpsertAndRead_RoundTrips`
  - `DiagnosticsService_OverBudget_ReturnsShouldAlert`
  - `Program_PutBudget_RequiresSettingsManage`
  - `DiagnosticsService_AlertThresholdCrossed_EmitsDomainEvent`
- Estimated effort: 5h broken down as:
  - Entity + migration + repo: 1.5h
  - Endpoints + DTO + DI rewire: 1h
  - Event emission + workflow halt hook: 1h
  - Tests: 1.5h

## References

- TS source: `packages/api/src/services/pg-diagnostics-store.ts:219-232`, `packages/api/src/routes/settings/diagnostics-routes.ts:168-183` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/InMemoryBudgetConfigProvider.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs:137-164`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:258-265`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`
- Related findings: `004-cost-accounting-hardcoded-zero.md`, `008-diagnostics-taxonomy-collapsed.md`
- CLAUDE.md section: "Self-Maintenance Goal" — cost runaway directly threatens the autonomous-operation guarantee.
