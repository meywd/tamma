# Story 35-6: Plan Quota & Usage-Limit Enforcement (BYOK-Aware)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the test-first (TDD) mandate, the 100% critical-path coverage requirement, and build-success enforcement.

## User Story

As a **platform owner running Tamma in SaaS mode**,
I want plan quotas (period token allowance, seat caps, concurrent workflows, connected repos) enforced at the right gates from the tenant's live subscription and usage state — with platform-provided tokens counted against the allowance and BYOK token volume exempt,
so that free-tier tenants are correctly hard-blocked when they exhaust their allowance, paid tenants can run over with overage billing, BYOK tenants are never throttled on token volume, and an upgrade lifts the block instantly without a restart.

## Priority

P0 — Without quota enforcement the billing plans created in 35-1/35-4 are unenforced: a free tenant could consume unlimited platform tokens, and seat/repo caps would be advisory only. This story is the enforcement layer that makes plan tiers real.

## Acceptance Criteria

1. A new `QuotaService` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaService.cs`, contract `IQuotaService`) resolves the **effective quota** for a tenant from the active `BillingSubscription` (Story 35-4) joined to the `BillingPlanPrice` / `Plan` catalog (Story 35-1), parsing the `Plan.Quotas` JSON into a strongly-typed `PlanQuota` record. This replaces the current dead-config state where `Plan.Quotas` (`apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs:32`) is stored but never read by any enforcement path.

2. The **platform-provided LLM path** (`LlmProxyService.ChatAsync`, `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs`, and `/api/v1/llm/chat`) consults `IQuotaService.CheckTokenQuotaAsync(tenantId, ...)` **before** issuing the upstream call. When the period token allowance is exhausted the behaviour is plan-configurable: `OverageBehavior.HardBlock` (free tier) returns a denial; `OverageBehavior.AllowWithOverage` (paid tiers) permits the call and the spend is billed as overage by the metering path (35-3). This is **distinct** from the existing absolute `BudgetConfig` USD cap enforced by `CheckBudgetActivity` / `DiagnosticsService.GetBudgetAsync`.

3. **BYOK tenants are never blocked on token volume.** When the resolved billing mode is `byok` (from `BillingCustomer.BillingMode` per Story 35-2, surfaced on the call via `ITenantProviderKeyResolver`/the `billing_mode` tag), `CheckTokenQuotaAsync` short-circuits to `QuotaDecision.Allowed` regardless of token usage. BYOK tenants are still gated on **seat count**, **connected-repo count**, and **feature flags** via the non-token quota checks.

4. The engine LLM path reuses and extends the existing fail-closed seam: `CheckBudgetActivity` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs`) gains a **quota check** alongside its budget check by calling a new central endpoint `GET /api/v1/billing/quota/{tenantId}/token` through `TammaApiClient`. Quota exhaustion completes the activity with the existing `"BudgetExhausted"` outcome (so the flowchart wiring is unchanged); any error in the quota check **fails closed** (denies), matching the activity's current `catch` semantics (`CheckBudgetActivity.cs:157-165`).

5. A **pre-dispatch workflow-run check** is added to the workflow-dispatch entry points (`EngineEndpoints.ExecuteTask` and `EngineEndpoints.TriggerCi`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs`, mounted under `/api/engine/*` with `WorkflowsManage`): before dispatch, `IQuotaService.CheckConcurrentWorkflowQuotaAsync(tenantId)` rejects the run when the tenant is at its `MaxConcurrentWorkflows` cap (counting in-flight `WorkflowInstance` rows). Seat and connected-repo caps are enforced at their own mutation gates (member-invite path, GitHub installation-repo add path) — this story adds the `IQuotaService` checks; it does not relocate those endpoints.

6. All quota checks complete **< 100ms p95** using a **local rollup**, never a synchronous Stripe call: token usage is read from the `BillingUsageRollup` table (Story 35-3) for the current period; seat count from `TenantMembership`; connected repos from active `GitHubInstallationRepo` rows. `QuotaService` caches the resolved `PlanQuota` per tenant with a short TTL (mirroring `PostgresBudgetConfigProvider.CacheTtl` = 60s), invalidated on subscription change (AC9).

7. **Soft thresholds** are configurable per quota dimension (default 80% warn, 100% exceeded). Crossing 80% emits a `BILLING.QUOTA.WARN` DCB event (once per period per dimension); crossing 100% emits `BILLING.QUOTA.EXCEEDED`. Both append via `IEventRepository.AppendAsync` to the control-plane `DomainEvents` store with `TenantId` set and `Tags = { tenantId, which_quota, plan, mode, period_start }`, so the Story 5.6 `AlertRuleEvaluator` and the metering/notifications paths can react without a join.

8. **Over-quota API responses** use a stable machine-readable shape consumable by both dashboards: `402 Payment Required` for token-allowance exhaustion under `HardBlock` and `429 Too Many Requests` for concurrent-workflow / seat / repo caps, with body `{ "error": "quota_exceeded", "which_quota": "platform_tokens|concurrent_workflows|seats|connected_repos", "reset_at": "<ISO-8601 | null>", "limit": <n>, "used": <n> }`. The `/api/v1/llm/chat` denial maps the existing `LlmProxyResult` error-reason switch (`SaaSEndpoints.cs`) to a new `"quota_exceeded"` reason → 402, kept distinct from the existing `"budget_exceeded"` → 402.

9. Quotas are **recomputed immediately on subscription / seat change**: Story 35-4's subscription-lifecycle webhook handler (and the `BillingModeService` mode-switch from 35-2) call `IQuotaService.InvalidateAsync(tenantId)` so an upgrade (free → team) lifts a `HardBlock` on the **next** request with no process restart. A `SUBSCRIPTION.UPDATED`-driven invalidation is asserted by test.

10. **Per-mode ownership** (CLAUDE.md "Operating Modes"): in **single-user mode** (`ITammaModeProvider.Mode == TammaMode.SingleUser`) quotas are not enforced — `NullQuotaService` is registered (every check returns `QuotaDecision.Allowed`), no `BillingUsageRollup` is required, and the engine/proxy paths fall back to the existing local `BudgetConfig` behaviour only. In **SaaS mode** the real `QuotaService` is wired; the tenant is the principal and quota state is keyed by `tenantId`.

11. **Tenant isolation**: every quota read is keyed by the authenticated `tenantId` resolved from `ITenantContext` / the engine shared-secret tenant claim — never a client-supplied id. Tenant A's usage rollup, seat count, and subscription are never read for tenant B. The `BillingUsageRollup` query carries the ambient tenant filter as defence-in-depth.

12. **Admin observability**: a read-only `GET /api/v1/admin/quota/{tenantId}` (`OwnerAccess`/platform-owner) returns the resolved `PlanQuota` + current usage for every dimension (`{ plan, mode, token_allowance, tokens_used, overage_behavior, seats_limit, seats_used, concurrent_limit, concurrent_used, repos_limit, repos_used, reset_at }`); a tenant-facing `GET /api/v1/orgs/{tenantId}/quota` (`MemberAccess`, any tenant member may read) returns the same shape scoped to the caller's tenant. No tenant-facing mutation endpoint is added (quota is derived, not set).

13. **DCB event hygiene**: `BILLING.QUOTA.WARN` and `BILLING.QUOTA.EXCEEDED` are emitted **at most once per (tenant, dimension, period)** — a hot LLM loop that crosses 100% a thousand times in a minute produces exactly one `EXCEEDED` event for that period (deduplicated via a `quota_notifications` ledger row keyed `(tenant_id, which_quota, period_start)`), so the alert pipeline is not flooded.

14. Unit + integration tests (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`) cover: free-tier platform token **hard-block** at 100%; paid-tier **overage allow** at 100%; **BYOK token exemption** (over allowance ⇒ allowed); **seat-cap block** at the invite gate; concurrent-workflow cap at pre-dispatch; `WARN`/`EXCEEDED` **single-emission** dedup; **subscription-change recompute** lifting a block; the engine `CheckBudgetActivity` quota-check fail-closed path; and **tenant-isolation** (A's usage never gates B). Stripe SDK and provider HTTP are mocked.

## Technical Design

### Namespace / file structure

```
apps/tamma-elsa/src/Tamma.Core/
  Billing/
    PlanQuota.cs                    # NEW — strongly-typed quota record + OverageBehavior enum (Core: shared)
    QuotaDimension.cs               # NEW — enum { PlatformTokens, ConcurrentWorkflows, Seats, ConnectedRepos }

apps/tamma-elsa/src/Tamma.Api/
  Services/Billing/
    IQuotaService.cs                # NEW — resolve quota + per-dimension checks + invalidate
    QuotaService.cs                 # NEW — EF-backed, cached, SaaS implementation
    NullQuotaService.cs             # NEW — single-user no-op (always Allowed)
    QuotaResolver.cs                # NEW — subscription→plan→PlanQuota resolution + JSON parse
    QuotaDecision.cs                # NEW — record { Allowed, WhichQuota, Limit, Used, ResetAt, HttpStatus }
    QuotaNotifier.cs                # NEW — dedup ledger + WARN/EXCEEDED DCB emission
    QuotaEvents.cs                  # NEW — BILLING.QUOTA.WARN / BILLING.QUOTA.EXCEEDED constants
    QuotaOptions.cs                 # NEW — bound config (warn threshold, cache TTL)
  Endpoints/Billing/
    QuotaEndpoints.cs               # NEW — GET admin/quota + orgs/{id}/quota
  Extensions/
    QuotaServiceCollectionExtensions.cs  # NEW — AddTammaQuota(mode-aware DI)
  Services/SaaS/
    LlmProxyService.cs              # MODIFY — call IQuotaService.CheckTokenQuotaAsync pre-call; quota_exceeded reason
  Endpoints/
    SaaSEndpoints.cs                # MODIFY — map "quota_exceeded" reason → 402 body
    EngineEndpoints.cs              # MODIFY — pre-dispatch CheckConcurrentWorkflowQuotaAsync in ExecuteTask/TriggerCi
    ProviderEndpoints.cs            # MODIFY — add GET /api/v1/billing/quota/{tenantId}/token (engine-callable)
  Program.cs                        # MODIFY — AddTammaQuota(); map QuotaEndpoints; engine quota route

apps/tamma-elsa/src/Tamma.Data/
  Entities/
    QuotaNotification.cs            # NEW — dedup ledger (tenant_id, which_quota, period_start, level)
  ControlPlaneDbContext.cs          # MODIFY — add DbSet<QuotaNotification>
  TammaModelConfiguration.cs        # MODIFY — table, unique index, CHECKs
  Migrations/ControlPlane/
    <ts>_AddQuotaNotifications.cs    # NEW (+ Designer + snapshot)

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  CheckBudgetActivity.cs            # MODIFY — add quota check via TammaApiClient (fail-closed)
  TammaApiClient.cs                 # MODIFY — add GetTokenQuotaAsync(tenantId)
```

> **Verified against the current tree.** `Plan` (with the unread `Quotas` JSON, `Plan.cs:32`), `LlmProxyService`/`ILlmProxyService`, `/api/v1/llm/chat` (`Program.cs:1900`), `CheckBudgetActivity` + its `TammaApiClient.GetBudgetAsync` → `GET /api/providers/diagnostics/budget/{accountId}` → `DiagnosticsService.GetBudgetAsync` → `BudgetStatus`, `EngineEndpoints.ExecuteTask`/`TriggerCi` (`/api/engine/*`, `Program.cs:1853-1854`), `IEventRepository.AppendAsync`/`DomainEvent`, `ITenantContext`, `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`), `TenantMembership`, `GitHubInstallationRepo`, `WorkflowInstance`, `BudgetConfig`/`PostgresBudgetConfigProvider` (60s cache), and the `OwnerAccess`/`MemberAccess`/`PlatformOwnerAccess` policies (`Program.cs:971,991,986`) all exist today. **`BillingSubscription` (35-4), `BillingUsageRollup` (35-3), `BillingCustomer.BillingMode` + `ITenantProviderKeyResolver` (35-1/35-2) are created by prerequisite stories — this story consumes them.**

### Key types

```csharp
// Tamma.Core/Billing/PlanQuota.cs
namespace Tamma.Core.Billing;

public enum OverageBehavior { HardBlock, AllowWithOverage }

/// <summary>Effective per-period quota for a tenant, parsed from Plan.Quotas
/// JSON and overlaid with any subscription-level override. A null limit means
/// "unlimited" for that dimension (e.g. enterprise tokens).</summary>
public sealed record PlanQuota(
    string PlanSlug,
    long? PlatformTokenAllowance,     // tokens/period; null = unlimited
    OverageBehavior TokenOverage,
    int? MaxSeats,
    int? MaxConcurrentWorkflows,
    int? MaxConnectedRepos,
    double WarnThreshold);            // 0..1, default 0.8
```

```csharp
// Tamma.Api/Services/Billing/QuotaDecision.cs
namespace Tamma.Api.Services.Billing;

public sealed record QuotaDecision(
    bool Allowed,
    QuotaDimension WhichQuota,
    long Limit,
    long Used,
    DateTimeOffset? ResetAt,
    int HttpStatus)                   // 402 tokens / 429 caps; 0 when Allowed
{
    public static QuotaDecision Allow(QuotaDimension d) =>
        new(true, d, 0, 0, null, 0);
}
```

```csharp
// Tamma.Api/Services/Billing/IQuotaService.cs
namespace Tamma.Api.Services.Billing;

public interface IQuotaService
{
    /// <summary>Resolve the effective quota for a tenant (cached, ≤100ms).</summary>
    Task<PlanQuota> GetQuotaAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Gate a platform-provided LLM call. BYOK ⇒ always Allow.
    /// HardBlock plan over allowance ⇒ deny (402). AllowWithOverage ⇒ allow,
    /// metering bills the overage. Emits WARN/EXCEEDED via QuotaNotifier.</summary>
    Task<QuotaDecision> CheckTokenQuotaAsync(
        Guid tenantId, string billingMode, long projectedTokens, CancellationToken ct = default);

    /// <summary>Gate a workflow dispatch on the concurrent-workflow cap.</summary>
    Task<QuotaDecision> CheckConcurrentWorkflowQuotaAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Gate a member-invite on the seat cap.</summary>
    Task<QuotaDecision> CheckSeatQuotaAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Gate a repo-add on the connected-repo cap.</summary>
    Task<QuotaDecision> CheckConnectedRepoQuotaAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Drop the cached PlanQuota for a tenant (sub/seat/mode change).</summary>
    void Invalidate(Guid tenantId);
}
```

### Quota resolution (`QuotaResolver`)

`GetQuotaAsync(tenantId)`:
1. Cache hit (per-tenant `ConcurrentDictionary<Guid, (PlanQuota, DateTime fetchedAt)>`, TTL `QuotaOptions.CacheTtl` default 60s) → return.
2. Load the active `BillingSubscription` for the tenant (Story 35-4); fall back to `Tenant.Plan` slug (`Tenant.cs:11`) when no subscription row exists (free-tier tenants pre-checkout).
3. Resolve the `Plan` row by slug; parse `Plan.Quotas` JSON (`Plan.cs:32`) into `PlanQuota` with a tolerant parser (missing keys ⇒ unlimited/default; malformed JSON ⇒ **fail closed** to the free-tier quota, logged WARN — never silently unlimited).
4. Cache + return.

### Token-quota check (the hot path)

`CheckTokenQuotaAsync(tenantId, billingMode, projectedTokens)`:
- `billingMode == "byok"` ⇒ `QuotaDecision.Allow(PlatformTokens)` (AC3) — no rollup read.
- Load `PlanQuota`; `PlatformTokenAllowance == null` ⇒ allow (unlimited).
- Read current-period platform-provided token usage from `BillingUsageRollup` (Story 35-3), filtered to `billing_mode = 'platform'` so BYOK usage already excluded at the rollup.
- `used + projectedTokens` vs allowance:
  - `< WarnThreshold * allowance` ⇒ allow.
  - `>= WarnThreshold` and `< allowance` ⇒ allow + `QuotaNotifier.NotifyAsync(WARN)`.
  - `>= allowance` ⇒ `QuotaNotifier.NotifyAsync(EXCEEDED)`; then `HardBlock` ⇒ deny (402, `ResetAt = period_end`), `AllowWithOverage` ⇒ allow (overage billed by 35-3).

### Notifier dedup ledger

```csharp
// Tamma.Data/Entities/QuotaNotification.cs
public class QuotaNotification
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string WhichQuota { get; set; } = null!;   // platform_tokens | seats | ...
    public DateTime PeriodStart { get; set; }
    public string Level { get; set; } = null!;        // warn | exceeded
    public DateTime CreatedAt { get; set; }
}
```

`QuotaNotifier.NotifyAsync` inserts-if-absent on the unique key `(TenantId, WhichQuota, PeriodStart, Level)` (NULLS NOT DISTINCT not needed — no nullable key parts); on a fresh insert it appends the DCB event, on a unique-violation it no-ops (AC13). Mirrors the missing-config-notifications dedup pattern.

### EF migration sketch

Additive only (new table; no baseline CHECK edits):

```csharp
migrationBuilder.CreateTable(name: "quota_notifications", columns: table => new {
    Id = table.Column<Guid>(nullable: false),
    TenantId = table.Column<Guid>(nullable: false),
    WhichQuota = table.Column<string>(nullable: false),
    PeriodStart = table.Column<DateTime>(nullable: false),
    Level = table.Column<string>(nullable: false),
    CreatedAt = table.Column<DateTime>(nullable: false),
}, constraints: table => {
    table.PrimaryKey("PK_quota_notifications", x => x.Id);
    table.CheckConstraint("ck_quota_notifications_level", "\"Level\" IN ('warn','exceeded')");
});
migrationBuilder.CreateIndex("IX_quota_notifications_dedup", "quota_notifications",
    new[] { "TenantId", "WhichQuota", "PeriodStart", "Level" }, unique: true);
```

Run `dotnet ef migrations add AddQuotaNotifications --context ControlPlaneDbContext`; verify `has-pending-model-changes` reports none; mirror entity config in `TammaModelConfiguration.cs` (the single source).

### DCB event names

| Event | When | TenantId | Tags |
|---|---|---|---|
| `BILLING.QUOTA.WARN` | first crossing of `WarnThreshold` for a (tenant, dimension, period) | set | `{ tenantId, which_quota, plan, mode, period_start, used, limit }` |
| `BILLING.QUOTA.EXCEEDED` | first crossing of 100% for a (tenant, dimension, period) | set | `{ tenantId, which_quota, plan, mode, period_start, used, limit, overage_behavior }` |

Both follow `AGGREGATE.ACTION.STATUS` and append via `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags, Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}", Data })`. They are CP-resident exactly like `BUDGET.EXHAUSTED`, so the existing `AlertRuleEvaluator` can match them with no rule-engine change (alert-rule seeding for these is a follow-up, mirroring the budget-exhausted rule).

### Engine path (CheckBudgetActivity extension)

`CheckBudgetActivity.ExecuteAsync` already calls `TammaApiClient.GetBudgetAsync` and fails closed on any exception (`CheckBudgetActivity.cs:68-114, 157-165`). Add, after the budget check passes, a quota check:

```csharp
var quota = await apiClient.GetTokenQuotaAsync(budgetOwnerId, context.CancellationToken);
if (quota is { Allowed: false })
{
    // reuse the existing exhausted outcome so the flowchart is unchanged
    await context.CompleteActivityWithOutcomesAsync("BudgetExhausted");
    return;
}
```

`TammaApiClient.GetTokenQuotaAsync` calls `GET /api/v1/billing/quota/{tenantId}/token` (new in `ProviderEndpoints`/a billing endpoint, authenticated by the engine shared-secret scheme like the budget route). Any failure ⇒ the activity's outer `catch` already returns `"BudgetExhausted"` (fail-closed, AC4).

### Pre-dispatch workflow check

In `EngineEndpoints.ExecuteTask` and `TriggerCi`, after request validation and before dispatch:

```csharp
var decision = await quota.CheckConcurrentWorkflowQuotaAsync(tc.TenantId ?? Guid.Empty, ct);
if (!decision.Allowed)
    return Results.Json(QuotaResponse.From(decision), statusCode: decision.HttpStatus); // 429
```

`CheckConcurrentWorkflowQuotaAsync` counts in-flight `WorkflowInstance` rows for the tenant against `PlanQuota.MaxConcurrentWorkflows` (null ⇒ unlimited; single-user ⇒ `NullQuotaService` ⇒ always allowed).

### Over-quota response shape (`QuotaResponse`)

```jsonc
// 402 (tokens, HardBlock) or 429 (concurrent / seats / repos)
{ "error": "quota_exceeded", "which_quota": "platform_tokens",
  "reset_at": "2026-07-01T00:00:00Z", "limit": 1000000, "used": 1000000 }
```

`SaaSEndpoints.LlmChat` extends its existing `response.ErrorReason switch` with `"quota_exceeded" => Results.Json(QuotaResponse..., statusCode: 402)`, kept separate from the existing `"budget_exceeded"` arm (different cause: allowance vs absolute USD cap).

### Per-mode + per-tenant handling

| Concern | single-user (`TammaMode.SingleUser`) | SaaS (`TammaMode.SaaS`) |
|---|---|---|
| Service registered | `NullQuotaService` (every check `Allowed`) | `QuotaService` (EF + cache) |
| Quota source | n/a (only local `BudgetConfig` USD cap applies) | active `BillingSubscription` → `Plan.Quotas` |
| Token usage source | n/a | `BillingUsageRollup` (`billing_mode='platform'`, current period) |
| BYOK exemption | n/a (sole user owns all usage) | `byok` mode ⇒ token checks short-circuit Allow |
| Principal / key | the user | the tenant (`tenantId`) |
| WARN/EXCEEDED events | not emitted | emitted (deduped per period) |
| Admin/tenant read endpoints | route absent | `/api/v1/admin/quota/{id}` (owner), `/api/v1/orgs/{id}/quota` (member) |

## Dependencies

**Internal (prerequisite):**
- **Story 35-3** (BYOK-aware usage metering) — supplies the `BillingUsageRollup` table + `billing_mode` split this story reads for token usage. **Hard blocker.**
- **Story 35-4** (subscription lifecycle) — supplies the `BillingSubscription` entity + the `SUBSCRIPTION.UPDATED` invalidation hook. **Hard blocker.**
- **Story 35-1** — `BillingCustomer`, `BillingPlanPrice`, `Services/Billing/` directory, `Plan` catalog mapping.
- **Story 35-2** — `BillingCustomer.BillingMode` + `ITenantProviderKeyResolver` + the `billing_mode` tag on `LLM.CALL.*` / `ProviderDiagnostic`.
- **Epic 28** — `Tenant`/`Plan`, `ControlPlaneDbContext`, `ITenantContext`, `ITammaModeProvider`, `TenantMembership`, `WorkflowInstance`, `GitHubInstallationRepo`.
- **Epic 4** — DCB events (`DomainEvent`, `IEventRepository.AppendAsync`); Story 5.6 `AlertRuleEvaluator`.
- **Epic 9** — `CheckBudgetActivity` / `TammaApiClient` fail-closed budget seam (extended here).

**Internal (blocks):**
- Billing dashboard / usage-display stories (consume `/api/v1/orgs/{id}/quota`).
- Invoicing / overage stories (rely on the `AllowWithOverage` decision + `EXCEEDED` events).

**External:**
- Stripe.net (via 35-1) — only mocked here; quota checks never call Stripe synchronously (AC6).
- PostgreSQL 17 control-plane DbContext (existing) for integration tests.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `QuotaResolverTests` — subscription→plan→`PlanQuota` parse (free/team/enterprise), missing JSON keys default sensibly, malformed `Plan.Quotas` ⇒ fail-closed to free quota + WARN, no-subscription falls back to `Tenant.Plan` slug.
2. `QuotaServiceTokenTests` — free-tier at 100% ⇒ `HardBlock` deny (402, `reset_at` = period end); paid-tier at 100% ⇒ `AllowWithOverage` allow; BYOK over allowance ⇒ allow (no rollup read — asserted via mock never-called); 80% crossing ⇒ allow + one WARN; unlimited (null allowance) ⇒ allow.
3. `QuotaServiceCapTests` — seat cap block at `CheckSeatQuotaAsync` (members == limit ⇒ deny 429); concurrent-workflow cap (in-flight == limit ⇒ deny); connected-repo cap.
4. `QuotaNotifierTests` — first WARN/EXCEEDED inserts ledger row + appends DCB event once; repeat crossings in the same period ⇒ no second event (unique-violation no-op); new period ⇒ new event.
5. `QuotaInvalidationTests` — `Invalidate(tenantId)` evicts cache; a subsequent `GetQuotaAsync` re-resolves the upgraded plan (free→team) and a previously-blocked token check now allows (AC9).
6. `NullQuotaServiceTests` — single-user: every check returns `Allowed`, no DB read, no event.
7. `CheckBudgetActivityQuotaTests` — quota-allowed ⇒ `"WithinBudget"`; quota-exhausted ⇒ `"BudgetExhausted"`; quota endpoint throws ⇒ fail-closed `"BudgetExhausted"` (AC4).
8. `LlmProxyServiceQuotaTests` — platform call over allowance (HardBlock) ⇒ `ErrorReason == "quota_exceeded"`; BYOK call over allowance ⇒ succeeds.

**Integration (`Tamma.Api.Tests`, docker-bound real Postgres, `sg docker -c "dotnet test ..."`):**
9. Migration applies + rolls back; `has-pending-model-changes` reports none.
10. `/api/v1/llm/chat` for a free tenant at allowance ⇒ HTTP 402 `quota_exceeded` body; same tenant after a simulated upgrade + `Invalidate` ⇒ 200.
11. `/api/engine/execute-task` at the concurrent cap ⇒ HTTP 429 `quota_exceeded`/`concurrent_workflows`.
12. **Tenant isolation** — seed tenant A at 100% usage, tenant B at 0%; A's `/llm/chat` ⇒ 402, B's ⇒ 200; assert A's rollup/subscription never read for B; `/api/v1/orgs/{B}/quota` never returns A's numbers; cross-tenant `/api/v1/orgs/{A}/quota` with B's token ⇒ 404.
13. `GET /api/v1/admin/quota/{id}` (`OwnerAccess`) returns the full resolved shape; member-role caller on the admin route ⇒ 403; `GET /api/v1/orgs/{id}/quota` member ⇒ 200.

**Mocks:** Stripe SDK mocked (no live calls); upstream provider via `HttpMessageHandler` stub; `BillingUsageRollup`/`BillingSubscription` repositories faked for unit tests, real for isolation integration tests.

Coverage targets per `CLAUDE.md`: 80% line / 75% branch / 85% function; token-quota decision logic, BYOK exemption, fail-closed engine path, and notifier dedup are **critical paths → 100%**.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Billing/PlanQuota.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Billing/QuotaDimension.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IQuotaService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/NullQuotaService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaDecision.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaNotifier.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaEvents.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/QuotaOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/QuotaEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/QuotaServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/QuotaNotification.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddQuotaNotifications.cs` | Create (+ Designer + snapshot) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config) |
| `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs` | Modify (token quota check + quota_exceeded reason) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs` | Modify (map quota_exceeded → 402) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` | Modify (pre-dispatch concurrent check) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs` | Modify (engine token-quota route) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (AddTammaQuota, map endpoints) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` | Modify (quota check, fail-closed) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` | Modify (GetTokenQuotaAsync) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaResolverTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaServiceTokenTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaServiceCapTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaNotifierTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaInvalidationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/NullQuotaServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/LlmProxyServiceQuotaTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/CheckBudgetActivityQuotaTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/QuotaEnforcementIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for billing/quota/budget spikes, bugs, findings, decisions (especially the `CheckBudgetActivity` fail-closed history and the Story 5.6 alert-evaluator findings).
3. Confirmed Stories 35-1/35-2/35-3/35-4 are merged so `BillingCustomer`/`BillingMode`/`BillingUsageRollup`/`BillingSubscription` exist.
4. Reviewed `CheckBudgetActivity.cs` (the fail-closed pattern this story extends) and `PostgresBudgetConfigProvider.cs` (the 60s-cache pattern this story mirrors).
5. Planned the TDD (Red-Green-Refactor) cycle for every new type.

### Key Design Decisions

- **Quota is derived, never set.** The effective `PlanQuota` is resolved from the live subscription + plan catalog at read time; there is no per-tenant "quota override" table to drift. `Plan.Quotas` becomes a *read* path (it is dead config today, `Plan.cs:32`).
- **Quota is distinct from the absolute USD budget cap.** `BudgetConfig`/`CheckBudgetActivity` enforce a hard USD ceiling (a safety brake); plan quotas enforce the *allowance* a tenant paid for. Both can fire; the response reasons (`budget_exceeded` vs `quota_exceeded`) and the events (`BUDGET.EXHAUSTED` vs `BILLING.QUOTA.EXCEEDED`) stay separate so dashboards can tell them apart.
- **Fail-closed in the engine, fail-closed on malformed config.** The engine quota check inherits `CheckBudgetActivity`'s deny-on-error contract; a malformed `Plan.Quotas` JSON degrades to the *free* quota (most restrictive), never to unlimited.
- **BYOK exemption is checked first and cheaply.** `CheckTokenQuotaAsync` returns `Allow` on `byok` before any rollup read — BYOK tenants pay a seat fee, not a token markup (epic theme), so token volume must never gate them.
- **Local rollup, never synchronous Stripe.** All checks read CP/per-tenant tables; Stripe is the system of record for invoicing, not for the < 100ms enforcement decision (AC6) — same principle as Story 20-3's "query local `usage_records`, not Stripe".
- **Dedup the events, throttle the alerts.** One ledger row per (tenant, dimension, period, level) caps event volume at the source (AC13), belt-and-suspenders with the alert evaluator's `ThrottleSeconds`.
- **Single-user is a registration-time no-op.** `NullQuotaService` keeps request handlers free of mode branching, mirroring 35-1's `NullBillingProvider` and 35-2's `NullBillingModeService`.

### Boundary Notes (do not implement sibling-story scope)

- **No usage metering or rollup writes** — `BillingUsageRollup` is written by Story 35-3; this story only *reads* it. No token-counting on the LLM response path beyond reading the existing rollup.
- **No subscription lifecycle, checkout, or webhook ingestion** — Story 35-4 owns `BillingSubscription` and the `SUBSCRIPTION.UPDATED` hook; this story only consumes the entity and exposes `Invalidate` for 35-4 to call.
- **No invoicing, overage line-item creation, dunning, tax, portal, or credits** — later stories. This story emits the `AllowWithOverage` *decision* and the `EXCEEDED` *event*; it does not bill.
- **No seat/repo endpoint relocation** — the seat cap is checked at the existing member-invite gate and the repo cap at the existing installation-repo add gate; this story adds the `IQuotaService` call, it does not move those endpoints.
- **No alert-rule seeding for quota events in this story** — the events are CP-resident and evaluator-visible; a `BuiltInAlertRules` entry for `BILLING.QUOTA.*` is a trivial follow-up (mirrors the budget-exhausted rule).

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Quota check adds latency to every LLM call | High | Cached `PlanQuota` (60s TTL) + single indexed rollup read; AC6 p95 < 100ms; load test in CI. |
| Malformed `Plan.Quotas` JSON ⇒ accidental unlimited | High | Tolerant parser fails *closed* to free-tier quota + WARN; unit-tested. |
| Event flood from a hot over-quota loop | Medium | Per-period dedup ledger (AC13) + alert ThrottleSeconds. |
| Stale cache after upgrade keeps a tenant blocked | High | 35-4 calls `Invalidate(tenantId)` on `SUBSCRIPTION.UPDATED`; AC9 test asserts immediate lift. |
| BYOK tenant wrongly throttled | High | BYOK short-circuit checked first; dedicated exemption test; never reads rollup for BYOK. |
| Engine quota endpoint unreachable | Medium | `CheckBudgetActivity` fail-closed `catch` already denies; AC4 test. |

### Success Metrics

- [ ] Free-tier platform tokens are hard-blocked at 100%; paid-tier runs over with overage; BYOK never blocked on tokens.
- [ ] Quota checks p95 < 100ms (no synchronous Stripe call).
- [ ] One WARN + one EXCEEDED event per (tenant, dimension, period).
- [ ] Upgrade lifts a block on the next request (no restart).
- [ ] Single-user boot enforces no quotas (NullQuotaService); 0 billing reads asserted.

## Logging Requirements

- **INFO**: quota resolved (`tenantId`, `plan`, `mode`); quota decision denied (`tenantId`, `which_quota`, `limit`, `used`); WARN/EXCEEDED event emitted (`tenantId`, `which_quota`, `level`); cache invalidated (`tenantId`).
- **DEBUG**: quota check started (`tenantId`, `dimension`, `projectedTokens`); cache hit/miss; rollup read (`tenantId`, `used`).
- **WARN**: malformed `Plan.Quotas` ⇒ fail-closed to free quota (`tenantId`, `plan`); quota threshold crossed (`tenantId`, `which_quota`); engine quota endpoint fallback.
- **ERROR**: control-plane read failure on quota resolution; DCB append failure; rollup query failure.
- **Structured context**: include `{ tenantId, plan, mode, which_quota, limit, used }` where applicable.
- **Credential safety**: NEVER log Stripe keys, provider API keys (BYOK or platform), or any secret plaintext. Quota logs carry counts and slugs only.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
