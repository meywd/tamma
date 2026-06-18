# Story 35-6 — Plan Quota & Usage-Limit Enforcement (BYOK-Aware)

> Implementation plan · Epic 35 (Billing & Payments, C#) · drafted 2026-06-17 · target: `apps/tamma-elsa` (.NET 9 / EF Core 9 / Npgsql) · test-first (TDD), xUnit; docker-bound suites run via `sg docker -c "dotnet test ..."`.

> **For agentic workers:** REQUIRED SUB-SKILL — use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan phase-by-phase. Steps use checkbox (`- [ ]`) syntax for tracking. Every phase writes tests **before** implementation.

**Goal:** Enforce plan quotas (period platform-token allowance, seat caps, concurrent workflows, connected repos) at the right gates, resolved from the tenant's *live* subscription + plan catalog. Platform-provided token usage counts against the allowance; BYOK token volume is exempt (BYOK is gated only on seats/repos/features). Reuse and extend the existing `CheckBudgetActivity` fail-closed seam for the engine LLM path, add a pre-dispatch check for workflow runs, emit soft-warn / over-quota DCB events into the alert pipeline, and return a stable machine-readable over-quota response — all distinct from the existing absolute `BudgetConfig` USD cap, per-tenant-isolated, and a single-user no-op.

---

## Non-goals (YAGNI guard)

- **NO usage metering / rollup writes.** `BillingUsageRollup` is *written* by Story 35-3. This story only *reads* the current-period rollup. No new token-counting on the LLM response path.
- **NO subscription lifecycle / checkout / webhook ingestion.** `BillingSubscription` and the `SUBSCRIPTION.UPDATED` hook belong to Story 35-4. This story consumes the entity and exposes `IQuotaService.Invalidate(tenantId)` for 35-4 to call on change.
- **NO invoicing, overage line-item creation, dunning, tax, billing portal, or credits wallet.** This story emits the `AllowWithOverage` *decision* + the `BILLING.QUOTA.EXCEEDED` *event*; it does not bill.
- **NO synchronous Stripe call on the enforcement path.** All checks read CP / per-tenant tables. Stripe is the invoicing system of record, never the < 100ms decision source (mirrors Story 20-3's "query local, not Stripe").
- **NO seat/repo endpoint relocation.** Seat cap is checked at the existing member-invite gate, repo cap at the existing installation-repo add gate. This plan adds the `IQuotaService` call only.
- **NO alert-rule seeding for `BILLING.QUOTA.*`.** The events are CP-resident and `AlertRuleEvaluator`-visible; a `BuiltInAlertRules` entry is a trivial follow-up (mirrors the budget-exhausted rule).
- **NO change to the absolute `BudgetConfig` USD cap semantics.** The quota check is *additive* alongside the budget check; both can fire, with separate reasons/events.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What enforcement exists today, and where the seams are

| Site | Behaviour today | Relevance |
|---|---|---|
| `src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` (`ExecuteAsync` ~49) | Calls `TammaApiClient.GetBudgetAsync(budgetOwnerId, ...)` → `GET /api/providers/diagnostics/budget/{accountId}`. On `Spent >= Limit` completes outcome `"BudgetExhausted"`; otherwise `"WithinBudget"`. Any exception ⇒ **fail closed** to `"BudgetExhausted"` (`:157-165`). Emits `BUDGET.EXHAUSTED` via `IAlertEventEmitter`. | **The fail-closed seam to extend** (AC4). Add a quota check after the budget check; reuse the `"BudgetExhausted"` outcome so the flowchart wiring is untouched. |
| `src/Tamma.Activities/LlmCall/TammaApiClient.cs` (`GetBudgetAsync` ~139) | `GET {base}/api/providers/diagnostics/budget/{budgetOwnerId}` returning `BudgetStatus?`; engine-auth via shared secret. | Add `GetTokenQuotaAsync(tenantId)` calling the new `GET /api/v1/billing/quota/{tenantId}/token`. |
| `src/Tamma.Api/Endpoints/ProviderEndpoints.cs` (`GetBudget` ~393) → `DiagnosticsService.GetBudgetAsync` (~280) → `BudgetStatus` record (`Services/Diagnostics/Models/DiagnosticsModels.cs:115`) | Returns `{ Spent, Limit, Remaining, PercentUsed, ShouldAlert, IsOverBudget }` from `PostgresBudgetConfigProvider` (60s cache, `PostgresBudgetConfigProvider.cs:33`). | Pattern to mirror for the engine-callable token-quota route + the 60s cache. |
| `src/Tamma.Api/Services/SaaS/LlmProxyService.cs` (`ChatAsync`) → `/api/v1/llm/chat` (`Program.cs:1900`, handler `SaaSEndpoints.LlmChat:38`) | Platform-provided LLM proxy. On failure maps `response.ErrorReason switch`: `"budget_exceeded" => 402`, `"invalid_request" => 400`, else 502. | **The platform-token gate** (AC2/AC8). Add a `CheckTokenQuotaAsync` pre-call; add a `"quota_exceeded" => 402` arm (distinct from `budget_exceeded`). |
| `src/Tamma.Api/Endpoints/EngineEndpoints.cs` (`ExecuteTask` ~581, `TriggerCi` ~545) → `/api/engine/execute-task`, `/api/engine/trigger-ci` (`Program.cs:1853-1854`, `WorkflowsManage`) | Workflow-dispatch entry points; `ExecuteTask` already resolves `ITenantContext tc`. | **The pre-dispatch gate** (AC5): add `CheckConcurrentWorkflowQuotaAsync` before dispatch. |
| `src/Tamma.Data/Entities/Plan.cs:32` — `public string Quotas { get; set; } = "{}";` | Stored by `PlansSeeder`, **read by nothing**. Doc-comment says "opaque JSON consumed by the billing layer" — that layer doesn't exist yet. | **Dead config this story activates** (AC1): parse into `PlanQuota`. |
| `src/Tamma.Data/Entities/Tenant.cs:11` — `public string Plan { get; set; } = "free";` | The tenant's plan slug. Separate from any `BillingSubscription` (created by 35-4). | Fallback plan source when no subscription row exists (free-tier pre-checkout). |

### Inputs for the non-token quota dimensions

- **Seats** — count `TenantMembership` rows for the tenant (`Tamma.Data/Entities/TenantMembership.cs`: `TenantId`, `UserId`, `Role`).
- **Connected repos** — count active `GitHubInstallationRepo` rows (`IsActive == true`) for the tenant's installation (`Tamma.Data/Entities/GitHubInstallationRepo.cs`).
- **Concurrent workflows** — count in-flight `WorkflowInstance` rows for the tenant (`Tamma.Data/Entities/WorkflowInstance.cs`).

### Billing entities this story consumes (created by prerequisite stories — NOT created here)

- `BillingSubscription` — **Story 35-4** (active subscription → plan slug + overage flags).
- `BillingUsageRollup` — **Story 35-3** (current-period token usage, `billing_mode='platform'` split).
- `BillingCustomer.BillingMode` (`PlatformProvided|Byok`) + `ITenantProviderKeyResolver` + the `billing_mode` tag on `LLM.CALL.*` / `ProviderDiagnostic.BillingMode` — **Stories 35-1 / 35-2**.
- `Services/Billing/` directory + `IBillingProvider`/`NullBillingProvider` registration pattern — **Story 35-1**.

> If a prerequisite entity name differs at implementation time, adapt the read in `QuotaResolver`/`QuotaService` only — the public `IQuotaService` contract is stable.

### Infrastructure to reuse

- **Events:** `IEventRepository.AppendAsync(DomainEvent)` (`src/Tamma.Data/Repositories/IEventRepository.cs`); `DomainEvent { Type, TenantId, Tags(JSONB), Metadata, Data }`. CP-resident; the Story 5.6 `AlertRuleEvaluator` polls this exact table.
- **Mode:** `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`) — process-stable SingleUser | SaaS.
- **Tenant context:** `ITenantContext` (`src/Tamma.Data/ITenantContext.cs`) — ambient `TenantId`, also a global query filter (defence-in-depth isolation).
- **Auth policies (`Program.cs`):** `OwnerAccess` (`:971`, `users:manage`), `MemberAccess` (`:991`, authenticated tenant member), `PlatformOwnerAccess` (`:986`, platform admin). Tenant-scope endpoints mirror `AlertEndpoints.cs` (admin section + `/api/v1/orgs/{tenantId}/...` section).
- **Cache pattern:** `PostgresBudgetConfigProvider` — `ConcurrentDictionary` + 60s TTL, invalidated on write. Reuse the shape for `PlanQuota`.
- **DI extension pattern:** `BillingServiceCollectionExtensions` (35-1) / `AddDiagnosticsServices` — mode-aware registration of real-vs-null seam.

---

## Architecture

**Resolve → check → decide → emit → respond**, with a single `IQuotaService` seam and a null seam for single-user:

1. **`IQuotaService`** (new) — the one enforcement seam. `GetQuotaAsync`, `CheckTokenQuotaAsync`, `CheckConcurrentWorkflowQuotaAsync`, `CheckSeatQuotaAsync`, `CheckConnectedRepoQuotaAsync`, `Invalidate`. `QuotaService` (SaaS, EF + 60s cache) and `NullQuotaService` (single-user, always `Allowed`).
2. **`QuotaResolver`** — subscription (35-4) → plan slug → `Plan.Quotas` JSON → strongly-typed `PlanQuota` (tolerant parse; malformed ⇒ fail-closed to free quota). Cached per tenant.
3. **`QuotaDecision`** — `{ Allowed, WhichQuota, Limit, Used, ResetAt, HttpStatus }`; `402` for token HardBlock, `429` for caps.
4. **`QuotaNotifier`** — per-(tenant, dimension, period, level) dedup ledger (`quota_notifications` table) gating `BILLING.QUOTA.WARN` / `BILLING.QUOTA.EXCEEDED` DCB emission to exactly once.
5. **Gates wired:** platform-token → `LlmProxyService` + engine `CheckBudgetActivity`; concurrent-workflow → `EngineEndpoints` pre-dispatch; seat/repo → existing mutation gates.
6. **Surfaces:** `GET /api/v1/admin/quota/{tenantId}` (OwnerAccess), `GET /api/v1/orgs/{tenantId}/quota` (MemberAccess).

### Per-mode ownership (the mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Is there a quota dimension? | No — sole user owns all usage; `NullQuotaService` returns `Allowed` for every check. | Yes — the tenant is the principal; quota keyed by `tenantId`. |
| What still applies in single-user? | Only the existing local `BudgetConfig` USD cap (unchanged). | Quota *and* the USD cap (separate reasons/events). |
| Who reads the quota surfaces? | Route absent. | Platform owner (`/api/v1/admin/quota/{id}`); tenant members (`/api/v1/orgs/{id}/quota`). |
| Where do quota events fan out? | Not emitted. | Tenant-scoped (`TenantId` set) → tenant alert feed + notifications. |
| Mode source | `ITammaModeProvider` (process-stable). | same |

---

## Phased task breakdown

### Phase 1 — Core types + `quota_notifications` ledger + `IQuotaService`/`NullQuotaService` skeleton (no gate wiring yet)

**Files:**
- New: `src/Tamma.Core/Billing/PlanQuota.cs` (record + `OverageBehavior` enum), `QuotaDimension.cs`.
- New: `src/Tamma.Api/Services/Billing/IQuotaService.cs`, `QuotaDecision.cs`, `QuotaEvents.cs`, `QuotaOptions.cs`, `NullQuotaService.cs`.
- New: `src/Tamma.Data/Entities/QuotaNotification.cs`; DbSet + config in `ControlPlaneDbContext.cs` / `TammaModelConfiguration.cs` (CHECK on `Level IN ('warn','exceeded')`; `UNIQUE (TenantId, WhichQuota, PeriodStart, Level)`). Additive EF migration under `Migrations/ControlPlane/` (`dotnet ef migrations add AddQuotaNotifications --context ControlPlaneDbContext`).
- New: `src/Tamma.Api/Extensions/QuotaServiceCollectionExtensions.cs` (`AddTammaQuota` registers `NullQuotaService` in single-user, real `QuotaService` in SaaS); wire in `Program.cs` (mirror `BillingServiceCollectionExtensions`).

**Approach:** Define the stable contract first; register the null seam so the host boots; the real `QuotaService` body lands in Phase 2. `NullQuotaService` short-circuits every method to `QuotaDecision.Allow(d)` with zero DB access.

**Tests first (`tests/Tamma.Api.Tests/Billing/NullQuotaServiceTests.cs`):** every check returns `Allowed`, no DB read, no event; `AddTammaQuota` registers `NullQuotaService` under `TammaMode.SingleUser` and `QuotaService` under `TammaMode.SaaS`. Migration applies + rolls back; `has-pending-model-changes` reports none.

---

### Phase 2 — `QuotaResolver` + `QuotaService` token-quota decision (the hot path, BYOK-aware)

**Files:**
- New: `src/Tamma.Api/Services/Billing/QuotaResolver.cs` (subscription→plan→`PlanQuota`, tolerant JSON parse, fail-closed to free on malformed), `QuotaService.cs` (`GetQuotaAsync` cached 60s; `CheckTokenQuotaAsync`), `QuotaNotifier.cs` (dedup insert + DCB emit).
- Read deps: `BillingSubscription` (35-4) with `Tenant.Plan` fallback; `Plan` by slug; `BillingUsageRollup` (35-3) current-period `billing_mode='platform'` sum; `BillingCustomer.BillingMode` for the BYOK short-circuit.

**Approach (TDD):**
1. `QuotaResolver`: resolve active subscription → plan slug (fallback `Tenant.Plan`); load `Plan`; parse `Plan.Quotas` into `PlanQuota`; malformed JSON ⇒ free quota + WARN log. Cache `(PlanQuota, fetchedAt)` per tenant, 60s TTL.
2. `CheckTokenQuotaAsync(tenantId, billingMode, projectedTokens)`: `byok` ⇒ `Allow` (no rollup read, asserted by a never-called mock); `allowance == null` ⇒ `Allow`; read rollup `used`; compute `used + projected` vs `WarnThreshold*allowance` and `allowance`; emit WARN at ≥80% (once), EXCEEDED at ≥100% (once); HardBlock ⇒ deny 402 (`ResetAt`=period end), AllowWithOverage ⇒ allow.
3. `QuotaNotifier.NotifyAsync`: insert-if-absent on `(TenantId, WhichQuota, PeriodStart, Level)`; fresh insert ⇒ append DCB event; unique-violation ⇒ no-op.

**Tests first:**
- `QuotaResolverTests.cs` — free/team/enterprise parse; missing keys default; malformed ⇒ fail-closed free + WARN; no-subscription ⇒ `Tenant.Plan` fallback; cache hit avoids re-resolve.
- `QuotaServiceTokenTests.cs` — free at 100% ⇒ deny 402 (`reset_at`); paid at 100% ⇒ allow (overage); BYOK over allowance ⇒ allow (rollup mock never called); 80% ⇒ allow + one WARN; null allowance ⇒ allow.
- `QuotaNotifierTests.cs` — first WARN/EXCEEDED inserts + emits once; repeat in same period ⇒ no second event; new period ⇒ new event.

---

### Phase 3 — Cap checks (seats, concurrent workflows, connected repos) + invalidation

**Files:**
- Modify: `QuotaService.cs` — `CheckSeatQuotaAsync` (count `TenantMembership`), `CheckConcurrentWorkflowQuotaAsync` (count in-flight `WorkflowInstance`), `CheckConnectedRepoQuotaAsync` (count active `GitHubInstallationRepo`); each vs the `PlanQuota` cap (null ⇒ unlimited); `Invalidate(tenantId)` evicts the cache.

**Approach (TDD):** each cap check loads `PlanQuota` (cached), counts the relevant rows, returns `Allow` or deny 429 with `WhichQuota`/`Limit`/`Used`. `Invalidate` removes the per-tenant cache entry so the next `GetQuotaAsync` re-resolves.

**Tests first:**
- `QuotaServiceCapTests.cs` — seat cap (members == limit ⇒ deny 429; below ⇒ allow); concurrent cap (in-flight == limit ⇒ deny); repo cap; null cap ⇒ always allow.
- `QuotaInvalidationTests.cs` — `Invalidate` evicts; subsequent `GetQuotaAsync` re-resolves the upgraded plan; a previously-blocked token check now allows (AC9).

---

### Phase 4 — Wire the platform-token gate (LlmProxyService + SaaSEndpoints)

**Files:**
- Modify: `src/Tamma.Api/Services/SaaS/LlmProxyService.cs` — before the upstream call, resolve `billing_mode` (from `ITenantProviderKeyResolver`/the 35-2 path), call `CheckTokenQuotaAsync(tenantId, mode, projectedTokens)`; deny ⇒ set `ErrorReason = "quota_exceeded"` and surface the `QuotaDecision` numbers.
- Modify: `src/Tamma.Api/Endpoints/SaaSEndpoints.cs` — extend the `ErrorReason switch` with `"quota_exceeded" => Results.Json(QuotaResponse..., statusCode: 402)`, distinct from `"budget_exceeded"`.

**Approach (TDD):** `projectedTokens` uses the request `MaxTokens` (or a conservative default) as the pre-call projection; the actual spend is metered by 35-3. BYOK ⇒ the quota check short-circuits Allow, so a BYOK call over allowance still succeeds.

**Tests first (`LlmProxyServiceQuotaTests.cs`):** platform call over allowance (HardBlock) ⇒ `ErrorReason == "quota_exceeded"` ⇒ endpoint 402 with the `quota_exceeded` body; paid-tier over allowance ⇒ success (overage); BYOK over allowance ⇒ success; under allowance ⇒ success.

---

### Phase 5 — Wire the engine gate (CheckBudgetActivity) + the pre-dispatch gate (EngineEndpoints) + engine quota route

**Files:**
- Modify: `src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` — after the budget check passes, call `apiClient.GetTokenQuotaAsync(budgetOwnerId, ct)`; `!Allowed` ⇒ `"BudgetExhausted"`; the existing outer `catch` already fails closed to `"BudgetExhausted"` (AC4).
- Modify: `src/Tamma.Activities/LlmCall/TammaApiClient.cs` — add `GetTokenQuotaAsync(tenantId)` → `GET /api/v1/billing/quota/{tenantId}/token`.
- Modify: `src/Tamma.Api/Endpoints/ProviderEndpoints.cs` (+ `Program.cs` route map) — add the engine-callable `GET /api/v1/billing/quota/{tenantId}/token` returning `{ allowed, which_quota, limit, used, reset_at }` from `IQuotaService.CheckTokenQuotaAsync` (mode read server-side; engine-auth scheme like the budget route).
- Modify: `src/Tamma.Api/Endpoints/EngineEndpoints.cs` — in `ExecuteTask` and `TriggerCi`, before dispatch, `CheckConcurrentWorkflowQuotaAsync(tc.TenantId)`; deny ⇒ `Results.Json(QuotaResponse, statusCode: 429)`.

**Approach (TDD):** keep `CheckBudgetActivity`'s outcome alphabet (`WithinBudget`/`BudgetExhausted`) unchanged so the flowchart is untouched. The pre-dispatch check resolves the tenant from `ITenantContext`, never the request body (isolation).

**Tests first:**
- `tests/Tamma.Activities.Tests/LlmCall/CheckBudgetActivityQuotaTests.cs` — budget-ok + quota-ok ⇒ `WithinBudget`; budget-ok + quota-exhausted ⇒ `BudgetExhausted`; quota endpoint throws ⇒ fail-closed `BudgetExhausted`.
- Endpoint test (Phase 7 integration): `/api/engine/execute-task` at concurrent cap ⇒ 429 `concurrent_workflows`.

---

### Phase 6 — Quota observability endpoints (admin + tenant)

**Files:**
- New: `src/Tamma.Api/Endpoints/Billing/QuotaEndpoints.cs` (mirror `AlertEndpoints.cs`: admin section + `/api/v1/orgs/{tenantId}/...` section); map in `Program.cs`.
  - `GET /api/v1/admin/quota/{tenantId}` (`OwnerAccess`) ⇒ full resolved `PlanQuota` + usage per dimension.
  - `GET /api/v1/orgs/{tenantId}/quota` (`MemberAccess`) ⇒ same shape, tenant resolved from context; cross-tenant ⇒ 404.

**Approach (TDD):** compose the response from `GetQuotaAsync` + the per-dimension counts. Tenant route never accepts a client-supplied id for the read scope (isolation); the `{tenantId}` segment is validated against the authenticated tenant.

**Tests first (`QuotaEndpointsTests.cs`):** admin route owner ⇒ 200 full shape; member on admin route ⇒ 403; `/orgs/{id}/quota` member ⇒ 200; cross-tenant ⇒ 404; single-user ⇒ routes absent (NullQuotaService path).

---

### Phase 7 — Integration + isolation + perf (docker-bound, real Postgres)

**Files:** `tests/Tamma.Api.Tests/Billing/QuotaEnforcementIntegrationTests.cs` (run via `sg docker -c "dotnet test ..."`).

**Tests:**
- `/api/v1/llm/chat` free tenant at allowance ⇒ 402 `quota_exceeded`; after simulated upgrade + `Invalidate` ⇒ 200 (AC9).
- `/api/engine/execute-task` at concurrent cap ⇒ 429.
- **Tenant isolation**: seed A at 100%, B at 0%; A ⇒ 402, B ⇒ 200; A's rollup/subscription never read for B; `/orgs/{B}/quota` never returns A's numbers; cross-tenant `/orgs/{A}/quota` with B's token ⇒ 404.
- Migration applies + rolls back; `has-pending-model-changes` none.
- **Perf**: warm-cache quota check p95 < 100ms over N iterations (AC6) — assert no Stripe client is constructed on the path.

---

## Sequencing & dependencies

```
Phase 1 (core + ledger + null seam)
  └─ Phase 2 (resolver + token decision)        ← needs P1 contract
       ├─ Phase 3 (cap checks + invalidate)      ← needs P2 service
       ├─ Phase 4 (LlmProxy + SaaS endpoint)     ← needs P2 token decision
       └─ Phase 5 (engine activity + pre-dispatch)← needs P2/P3
            └─ Phase 6 (observability endpoints)  ← needs P2/P3
                 └─ Phase 7 (integration/isolation/perf)
```

- **Phase 1** is the only hard prerequisite for everything else.
- **Phases 3, 4, 5** are parallel-safe once Phase 2 lands (independent gates).
- **External story prerequisites:** 35-3 (`BillingUsageRollup`) and 35-4 (`BillingSubscription` + invalidation hook) must be merged before Phase 2; 35-1/35-2 (`BillingCustomer.BillingMode`, `Services/Billing/`) before Phase 1.

---

## Risks + mitigations

- **Latency on every LLM call** (High) — cached `PlanQuota` (60s) + a single indexed rollup read; BYOK short-circuits before any read; Phase 7 perf test asserts p95 < 100ms and no Stripe client construction.
- **Malformed `Plan.Quotas` ⇒ accidental unlimited** (High) — tolerant parser fails *closed* to the free-tier quota with a WARN; `QuotaResolverTests` pins this.
- **Event flood from a hot over-quota loop** (Medium) — per-(tenant, dimension, period, level) dedup ledger caps emission at one; alert evaluator `ThrottleSeconds` is the secondary guard.
- **Stale cache after upgrade keeps a tenant blocked** (High) — 35-4 calls `Invalidate(tenantId)` on `SUBSCRIPTION.UPDATED`; `QuotaInvalidationTests` + the Phase 7 upgrade test assert immediate lift.
- **BYOK tenant wrongly throttled** (High) — BYOK short-circuit is checked first and is the dedicated `QuotaServiceTokenTests` BYOK case; the rollup is never read for BYOK.
- **Engine quota endpoint unreachable** (Medium) — `CheckBudgetActivity`'s existing fail-closed `catch` denies; `CheckBudgetActivityQuotaTests` asserts it.
- **Prerequisite entity-name drift (35-3/35-4)** (Medium) — confine all reads of `BillingUsageRollup`/`BillingSubscription` to `QuotaResolver`/`QuotaService`; the public `IQuotaService` contract is stable so call-sites don't churn.
- **Migration discipline** (Low) — `quota_notifications` is additive (new table, not a baseline CHECK edit); still verify `has-pending-model-changes` reports none and mirror config in `TammaModelConfiguration.cs` only.

---

## Acceptance criteria (mirror the story)

- [ ] `QuotaService` resolves the effective quota from the active `BillingSubscription` + plan catalog, parsing `Plan.Quotas` (previously dead config) into `PlanQuota`.
- [ ] Platform-provided LLM calls are gated against the period token allowance; free ⇒ HardBlock (402), paid ⇒ AllowWithOverage — distinct from the `BudgetConfig` USD cap.
- [ ] BYOK tenants are never blocked on token volume; still gated on seats / connected repos / feature flags.
- [ ] Engine path reuses/extends `CheckBudgetActivity` (fail-closed) for the LLM check; a pre-dispatch concurrent-workflow check is added; all checks complete < 100ms p95 from the local rollup (no Stripe).
- [ ] Soft thresholds (80/100%) emit `BILLING.QUOTA.WARN` / `BILLING.QUOTA.EXCEEDED` DCB events feeding the notifications/alert path, deduped once per (tenant, dimension, period).
- [ ] Over-quota responses use a stable shape (402/429 with `code quota_exceeded`, `which_quota`, `reset_at`, `limit`, `used`) consumable by the dashboards.
- [ ] Quotas recompute immediately on subscription/seat change (`Invalidate`) so an upgrade lifts the block without a restart.
- [ ] Single-user mode enforces no quotas (`NullQuotaService`); SaaS mode keys quota by `tenantId`; tenant isolation holds (A's usage never gates B).
- [ ] Unit + integration tests cover: free-tier hard block, paid overage allow, BYOK exemption, seat-cap block, concurrent-cap block, warn/exceeded single-emission, sub-change recompute, engine fail-closed, tenant isolation — Stripe + provider HTTP mocked.
