# Finding 004: Cost accounting hardcoded to $0 — no token-to-dollar enrichment

**Scope**: providers
**Severity**: P0 (silent billing regression; every diagnostics row has `cost_usd=0`)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 4–6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/provider-session.ts`.

- TS relied on the `InstrumentedAgentProvider` decorator (in `@tamma/providers`) and the `DiagnosticsProcessor` (in `@tamma/shared/src/telemetry/`) to compute `costUsd` from `{provider, model, inputTokens, outputTokens}` at event-emission time. A provider-level pricing table keyed on `provider:model` provided `$/input_token` and `$/output_token`. The store received a non-zero `costUsd` on every `provider:complete` record.
- The diagnostics store contract was unchanged: `DiagnosticsRecordInput.costUsd?: number` (see `packages/api/src/services/diagnostics-store.ts:39-57`), with cost being supplied **by the emitter**, not recomputed at store-insert time.
- Code locus: `packages/providers/src/instrumented-agent-provider.ts`, `packages/providers/src/instrumented-llm-provider.ts`, `packages/shared/src/telemetry/diagnostics-processor.ts`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:143-146`, `:167-169`
- Contract/behavior: Both the Anthropic and the OpenAI-style response parsers hardcode `cost = 0m`. The XML-doc comment explicitly says "cost-monitor is responsible for enrichment (Epic 9)" but no such enrichment path exists in the C# code.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs — lines 143-146
// Anthropic doesn't return cost; leave at 0 — the cost-monitor
// service is responsible for enrichment (Epic 9).
return new ProviderInvocationResult(content, tokens, 0m, durationMs);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs — lines 167-169
int totalTokens = 0;
...
return new ProviderInvocationResult(choicesContent, totalTokens, 0m, durationMs);
```

- `ProviderSessionService.ExecuteAsync` writes a `ProviderDiagnostic` with `Cost = invocationResult.CostUsd` — i.e., always `0`.
- No pricing table exists anywhere under `apps/tamma-elsa/src/Tamma.Api/Services/`.
- Dependencies: `ProviderDiagnostic.Cost` column (entity, `decimal`), `DiagnosticsRepository.GetCostSumAsync` (used by `DiagnosticsService.GetBudgetAsync`).
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Diagnostics/*` verify that rows round-trip `Cost`; none verify cost is computed.

## 3. The gap

- TS: `costUsd = inputTokens * $/input_token + outputTokens * $/output_token` from a pricing table; persisted as a positive decimal.
- C#: `costUsd = 0m` on every invocation — every diagnostics row is written with `Cost = 0`.
- For a caller sending `{provider: "anthropic", model: "claude-sonnet-4"}` and consuming 1000/1000 tokens:
  - TS inserts `{cost_usd: 0.012}` (0.003/1k input + 0.015/1k output, example).
  - C# inserts `{cost_usd: 0}`.
- In production with existing data / deployed clients, this means:
  - `GET /api/providers/diagnostics/budget/{accountId}` always returns `spent: 0` (see `DiagnosticsService.cs:143` which sums `cost_usd`). Budget alerts never fire. See finding 005.
  - `GET /api/providers/diagnostics/report` always returns `totalCost: 0` per bucket.
  - Dashboards and any billing pipeline that consume this table silently show $0 spend.

Error paths:
- Neither TS nor C# error on zero cost; this is a silent-failure mode.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 AC 1: "`provider_diagnostics` table stores per-call diagnostics records" with column `cost_usd NUMERIC(12,6)` — i.e., expects a non-zero value.
- Story 9-2 AC 3: "`DiagnosticsProcessor` is updated to write to the diagnostics service in addition to (or instead of) the cost tracker" — the "cost tracker" is where the token-to-dollar computation lived in TS. That tracker was not ported.
- Story alignment:
  - [ ] Matches TS behavior.
  - [x] Matches C# behavior (stub).
  - [ ] Describes a third behavior.
  - [x] No story — spec gap. There is no story titled "Port provider pricing table to C#" or "Port `CostTracker`".

If no story: `CLAUDE.md` § "Key Architectural Decisions" does not mention the cost tracker; `docs/architecture.md` does. This is under-documented in the port plan.

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Port the pricing table from `@tamma/providers` (look in `packages/providers/src/pricing.ts` or similar at `9e9a57c~1`). Store it as a JSON resource or a frozen dictionary.
  2. Introduce `IProviderPricingService` with a single method `decimal Compute(string provider, string model, int inputTokens, int outputTokens)`.
  3. Call it inside `HttpProviderClient.ParseResponse` (or even better, inside a new `InstrumentedProviderClient` decorator, so the same pattern covers future adapters).
  4. Persist separate input/output token counts on `ProviderDiagnostic` (see finding 008) so replay/audit can recompute cost if pricing changes.
  5. Expose a read-only `GET /api/providers/pricing` so the dashboard can display the active rates.
- **Is it "just a stub" or is scope missing?** Just a stub — the code itself admits this in the XML-doc. The deletion PR did not port the pricing table.
- **Blockers**: Partially blocked by finding 008 (token column collapse) — recomputing cost per input/output split requires separate columns. Not blocked on finding 003 (provider adapters) — the enrichment can run for the 4 HTTP providers today.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs` (replace `0m` with call into pricing service)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderSessionService.cs` (call pricing before record)
  - `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` (add `InputTokens`, `OutputTokens`)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderPricingService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/pricing.json` (or embedded resource)
- Tests to add:
  - `ProviderPricingService_ComputesCorrectUsdForKnownModels`
  - `HttpProviderClient_PopulatesCostFromPricingTable`
  - `DiagnosticsService_BudgetStatus_ReflectsNonZeroCostSum`
- Estimated effort: 5h broken down as:
  - Pricing table JSON + service: 1h
  - Column split on diagnostics entity + migration: 1.5h
  - HttpProviderClient wiring + regression: 1h
  - Tests: 1.5h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed
- **Commit**: `498889b` `fix(providers): land P0 pricing/budget/role/CLI-stub fixes [findings 001, 003, 004, 005]`
- **Notes**: Ported the TS pricing table from `packages/cost-monitor/src/pricing-config.ts` (`9e9a57c~1`) into `Tamma.Api.Services.Providers.ProviderPricingService`. Per-token rates stored as `decimal` (1M-token TS rate ÷ 1,000,000) so EF round-trips don't lose precision. Six providers covered: anthropic, openai, google (`gemini` alias), openrouter, claude-code (uses anthropic rates), local (zero). Provider alias map normalises `anthropic-claude` / `gemini` / `claude-code` etc. Wired into `HttpProviderClient.ParseResponse` for both Anthropic and OpenAI-style branches; OpenAI-style now reads `prompt_tokens` + `completion_tokens` separately so the `(input, output)` token split also reaches the diagnostics row. `ProviderInvocationResult` extended with `InputTokens` / `OutputTokens`; `ProviderSessionService` persists them onto `ProviderDiagnostic.InputTokens` / `OutputTokens` columns (already present from the schema-hardening migration). Unknown `(provider, model)` tuples cleanly return `0m` — same as the TS happy path. 10 unit tests added covering known rates, alias resolution, prefix matching, negative-clamp.

## References

- TS source: `packages/providers/src/instrumented-agent-provider.ts`, `packages/providers/src/pricing.ts`, `packages/shared/src/telemetry/diagnostics-processor.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:143-146`, `:167-169`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`
- Related findings: `005-budget-enforcement-no-op.md`, `008-diagnostics-taxonomy-collapsed.md`
- CLAUDE.md section: "Multi-Provider AI Abstraction" implicitly expects cost tracking for the "70%+ autonomous issue completion rate" goal.
- Archived SQL migration: `database/archived-sql-migrations/014_provider_diagnostics.sql:24` (`cost_usd NUMERIC(12,6) DEFAULT 0`)
