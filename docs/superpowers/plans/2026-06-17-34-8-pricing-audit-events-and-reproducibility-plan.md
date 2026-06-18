# Story 34-8 — Pricing Audit, Events & Reproducibility (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes its
> tests before implementation. Story: `docs/stories/epic-34/story-34-8/34-8-pricing-audit-events-and-reproducibility.md`.

**Goal:** Make every Epic 34 pricing decision auditable and time-travel-reproducible through the
DCB event stream. Ship (a) the canonical pricing event taxonomy as code constants
(`PLAN.*`, `TENANT.PLAN.*`, `PRICING.MARGIN.*`, `CREDIT.*`, `PROMO.*`, `BYOK.*`), (b) a single
`PricingEventEmitter` write-seam that 34-1..34-7 route through, emitting events **transactionally
with the state change** and routing tenant vs control-plane planes exactly like `AlertEventEmitter`,
(c) a reconstruction API that replays the streams into "what plan/price/entitlements/BYOK applied
to tenant X at time T", (d) a deterministic price-replay that re-runs `IUsagePricingEngine` against
the margin policy effective at a historical usage event, and (e) an admin config-audit dashboard
feed with actor + diff.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (Tamma.Api minimal-API +
Tamma.Data EF + Tamma.Core), React/Vite dashboard in `packages/dashboard` (Vitest). C# tests live
in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). Build needs no docker wrapper.

---

## Non-goals (YAGNI guard)

- **NO new event type of this story's own.** 34-8 is the consistency layer; it names + emits +
  reconstructs the producers' events. It does not invent a `PRICING.AUDIT.*` event.
- **NO new table / EF migration.** Reconstruction replays the existing `domain_events`
  (tenant) and `platform_events` (control-plane) stores. A derived snapshot table would be a second
  source of truth to keep consistent — rejected. `has-pending-model-changes` must report none.
- **NO money movement.** Re-deriving a priced amount for *audit* is in scope; charging, invoices,
  Stripe are Epic 35. Replay calls the pure `IUsagePricingEngine`, never a billing API.
- **NO re-implementation of the markup engine, plan catalog, credit/promo/BYOK semantics.** Those
  are owned by 34-5 / 34-1 / 34-6 / 34-7 / 34-3 respectively (34-5 carries the CANONICAL-owner
  boundary note for markup). This story *consumes* `IUsagePricingEngine`, `IPlanCatalogService`,
  `MarginPolicy`, and the producers' domain data.
- **NO alert rules for pricing events.** Consistent emission makes a future `PRICING.MARGIN.UPDATED`
  alert rule trivial, but wiring it is out of scope (Story 5.6 / a later story).
- **NO mutation of resolution/pricing semantics.** Replay must reproduce the original number; it
  must never "fix" or re-price with current policy.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Event stores + dual-plane routing (the pattern to copy)

- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — tenant-scope event row:
  `Id, Type, TenantId?, IssueNumber?, Tags(jsonb), Metadata(jsonb), Data(jsonb), CreatedAt,
  SequenceNumber(BIGSERIAL)`.
- `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs` — control-plane event row, same column
  shape minus `IssueNumber`, plus `UserId?`. Doc 01 §5.1–5.2: cross-tenant / catalog-global events
  (`TENANT.*`, etc.) write here.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` —
  `AppendAsync` routes a null-tenant event to `IPlatformEventRepository`; a tenant event to the
  per-tenant `DbContext` via `ITenantDbContextFactory.CreateAsync(tid)` then `SaveChangesAsync`
  (lines ~49–87). `QueryWithPaginationAsync` (tenant-scoped, paginated, exact type, ordered by
  `CreatedAt` then `SequenceNumber`, lines ~176–209) is the reconstruction read seam.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformEventRepository.cs` /
  `IPlatformEventRepository.cs` — control-plane query (`QueryAsync(typePrefix, limit)`).
- `apps/tamma-elsa/src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs` +
  `Tamma.Api/Services/PlatformEvents/PlatformEventPublisher.cs` — append+publish; **returns null on
  the partial-unique dedup no-op** (idempotent retry — treat null as "already recorded").
- **Reference emitter — `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/AlertEventEmitter.cs`:**
  tenant-scope events → `IEventRepository.AppendAsync(new DomainEvent{...})`; platform-scope →
  `IPlatformEventPublisher.AppendAndPublishAsync(new PlatformEvent{...}, ct)`; **every `lastError` /
  `finalError` string runs through `CredentialRedactor.Clean` before serialisation** (lines ~96,
  135, 211); `Tags`/`Data` serialised with `JsonSerializerOptions{WriteIndented=false}`;
  `Metadata = {"eventSource":"system","workflowVersion":"1.0.0"}`. **Difference to flag:**
  `AlertEventEmitter` is fire-and-forget (`try/catch → LogWarning`, never throws) — 34-8's emitter
  must be the opposite (a failed append must roll back the producer's state change).

### Redaction (reuse, do not reinvent)

- `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs` — `Clean(string?)` scrubs Bearer
  tokens, `key=value` credential assignments, URL basic-auth, and known secret prefixes
  (`tamma_sk_`, `sk_live_`, `sk_test_`, `ghp_`, `github_pat_`, `xoxb-`, …), strips control chars,
  caps at 1024 chars. The redaction test for BYOK refs asserts none of these prefixes survive.

### Authorization policies (Program.cs, verified)

- `apps/tamma-elsa/src/Tamma.Api/Program.cs` defines policies (~966–1082):
  `OwnerAccess` (~971), **`PlatformOwnerAccess` (~986)** — platform-admin gate used by all
  `/api/admin/*` admin CRUD, `MemberAccess` (~991). Admin group: `app.MapGroup("/api/admin")`
  (~1244). Admin endpoint exemplar:
  `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantDatabasesEndpoints.cs` (static
  minimal-API handlers, `[FromServices]` DI, DTOs in `Tamma.Api/Dtos/Admin`).

### Dependency-story artifacts this story references (created by 34-1/34-4/34-5)

- **34-1** (`docs/stories/epic-34/story-34-1/...`): `Plan` gains `Version`, `Status`,
  `IsCustom`, `BillingInterval`, `SupersedesPlanId`; new `IPlanCatalogService` (in
  `Tamma.Api/Services/Pricing/` — **directory created by 34-1**) with `GetByIdAsync(planId)`,
  `GetForTenantAsync(tenantId)`, returning an immutable `PlanSnapshot`; producers emit
  `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` to `platform_events`. `EntitlementMetricKey` enum in
  `Tamma.Core/Enums`.
- **34-4** (`story-34-4/...`): `TenantPlanAssignment` entity (TenantId, PlanId+Version, Status,
  EffectiveFrom/To); `IPlanAssignmentService.AssignAsync`; emits `TENANT.PLAN.CHANGED`; adds
  `PricingEndpoints.cs` (the `/orgs/{id}` tenant group + `/api/pricing/*`).
- **34-5** (`story-34-5/...`): `IUsagePricingEngine.PriceUsage(usageLine) →
  {costBasisUsd, marginUsd, sellPriceUsd, pricingMode}` (pure/deterministic); `MarginPolicy`
  entity (Scope plan|provider|global, RefKey, MarkupMultiplier/FixedUsdPer1M, EffectiveFrom);
  `AdminPricingEndpoints.cs` (**the `/api/admin/pricing` group this story extends**); emits
  `PRICING.MARGIN.UPDATED`. Cost basis from existing
  `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService.cs`
  (`Compute(provider, model, inputTokens, outputTokens)`).
- Usage line source: `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` — carries
  `ProviderKey, Model, InputTokens, OutputTokens, Cost, TenantId?, CorrelationId?, CreatedAt`
  (34-3 enriches with BillingMode). `Tenant.Plan` is today a **string** (`free`) — 34-4 introduces
  the `PlanId`/assignment as source of truth, which this story's reconstruction relies on.

### Dashboard surface

- `packages/dashboard/src/pages/admin/AdminLayout.tsx` — `AdminTab` union (~17:
  `'users'|'api-keys'|'health'|'links'|'audit-log'|'tenants'`) + `TABS` array (~24); tabs render by
  `activeTab` switch (~82). Service clients in `packages/dashboard/src/services/admin/`
  (`admin-tenants-client.ts` is the closest pattern). `AuditLogTab.tsx` is the closest existing
  tab to mirror for a time-ordered list.

### Gotcha worth pinning

- `EventRepository.AppendAsync` opens **its own** per-tenant `DbContext` + `SaveChangesAsync`. For
  the transactional invariant (AC 4) the emitter must instead enlist in the **producer's** open
  `DbContext` for the common producer path; `IEventRepository.AppendAsync` is the standalone
  fallback only (e.g. Elsa activities with no ambient producer transaction).

---

## Architecture

**taxonomy (constants) → emitter (one seam, transactional, dual-plane, redacted) → reconstruction
(replay streams) → replay (pure engine + historical policy) → surfaces (admin endpoints + tenant
endpoint + dashboard tab).** No new table; reuse `domain_events` + `platform_events`.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Full audit incl. margin internals | sole user (= platform owner) | `PlatformOwnerAccess` only |
| Tenant audit (no margin) | sole user | tenant_owner/admin/member for own tenant (`/orgs/{id}/pricing/audit`) |
| Tenant-scope event store | sole user's tenant `domain_events` | per-tenant `domain_events` (isolation plane) |
| Catalog/margin event store | control-plane `platform_events` | same — global |
| Mode source | `ITammaModeProvider` | same |

---

## Task breakdown (test-first)

### Task 1 — Canonical taxonomy + tag contract (`PricingEventTypes`, `PricingEventTags`)

**Files:**
- New `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` — `const string` per
  event (see story event-catalog table).
- New `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTags.cs` — `PricingEventTagSet`
  record, `EventPlane` enum, `PlaneFor(eventType)`, `RequiredFor(eventType)`,
  `PricingEventTagValidator.Validate(eventType, tagSet) → IReadOnlyList<string>` (missing keys).

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PricingEventTagValidatorTests.cs`):
- each event type's required-tag list; missing-key detection; catalog-global events don't require
  `tenantId`; `PlaneFor` returns ControlPlane for `PLAN.*`/`PRICING.MARGIN.*`, Tenant otherwise.

**Approach:** pure static — no DI, no DB. Fast unit suite, no docker.

### Task 2 — `PricingEventEmitter` (single write seam, transactional, redacted)

**Files:**
- New `IPricingEventEmitter.cs` (`EmitAsync(eventType, tags, data, producerContext, ct)`).
- New `PricingEventEmitter.cs` — mirror `AlertEventEmitter` for serialisation + redaction
  (`CredentialRedactor.Clean` on every string `data` value; `Metadata` constant; `JsonOpts`),
  but enlist in the producer's `DbContext`: route by `PlaneFor` → add `DomainEvent` (tenant) or
  `PlatformEvent` (control-plane) to `producerContext`; provide a standalone fallback using
  `IEventRepository` / `IPlatformEventPublisher` when `producerContext` is null. **Do not catch +
  swallow** — let an append failure propagate so the producer's transaction rolls back.

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PricingEventEmitterTests.cs`):
- plane routing per event type (mock repos / in-memory `DbContext`); redaction ran on every string
  `data` value; BYOK ref opaque (no secret prefix survives); validator wired (missing tag →
  Debug.Assert in DEBUG); standalone fallback path when `producerContext` null.

**Approach:** keep the emitter dependency-light (logger + optional `IEventRepository` +
`IPlatformEventPublisher` for the fallback). The producer-context overload is the primary path.

### Task 3 — Transactional invariant (atomicity) integration test + producer pattern doc

**Files:**
- New `tests/Tamma.Api.Tests/Pricing/PricingAuditTransactionTests.cs` (docker-bound).
- Doc the canonical producer pattern in the story Dev Notes (already written) + a short XML-doc on
  `IPricingEventEmitter.EmitAsync` so 34-1..34-7 adopt: `db.Add(entity);
  await emitter.EmitAsync(..., db, ct); await db.SaveChangesAsync(ct);`.

**Tests first:** drive a producer-style write that adds an entity + calls `EmitAsync` against a real
`DbContext`; force a failure before commit → assert NEITHER row NOR event; happy path → assert
BOTH. Run `sg docker -c "dotnet test --filter PricingAuditTransactionTests ..."`.

**Approach:** this test is the load-bearing guard for AC 4 — write it red first; it drives the
"enlist in producer transaction" design from Task 2.

### Task 4 — `PricingAuditService.ReconstructAsync` (point-in-time replay)

**Files:**
- New `IPricingAuditService.cs`, `PricingAuditService.cs`, `PricingAuditSnapshot.cs`.

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PricingAuditReconstructionTests.cs`):
- synthetic history (assign v1 → upgrade v2 → margin edit → BYOK flip) appended to the stores;
  `ReconstructAsync(now)` == live (`IPlanCatalogService.GetForTenantAsync` + live margin + live
  BYOK); `ReconstructAsync(t_before_upgrade)` == v1; empty-history → empty snapshot (no throw).

**Approach:** fold the two streams (tenant via `IEventRepository.QueryWithPaginationAsync`,
control-plane via `IPlatformEventRepository.QueryAsync`) filtered `CreatedAt <= at`; pick last
plan-change / BYOK-change; resolve margin by 34-5 scope order; snapshot entitlements via
`IPlanCatalogService.GetByIdAsync`. Mock `IPlanCatalogService` + repos in unit tests; one
docker-bound integration test exercises the real stores.

### Task 5 — `PricingAuditService.ReplayUsageAsync` (deterministic re-pricing)

**Files:** extend `PricingAuditService.cs`; `PricingReplayResult` in `PricingAuditDtos.cs`.

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PricingReplayTests.cs`, golden file):
- synthetic usage line (`ProviderDiagnostic`) + a `MarginPolicy` history; `ReplayUsageAsync`
  recomputes `sellPriceUsd` byte-stable against a checked-in golden value; a policy edit AFTER the
  usage timestamp does not change the replayed price; unknown provider/model → surfaces
  `PricingUnknownModel` (from 34-5), logged WARN.

**Approach:** read the usage line by id; resolve the `MarginPolicy` effective at
`usageEvent.CreatedAt` (NOT now) via the reconstruction's margin fold; call the pure
`IUsagePricingEngine.PriceUsage`. Determinism comes from the engine + historical policy — assert,
don't recompute, in the test.

### Task 6 — Config-audit log query + diff shaping

**Files:** extend `PricingAuditService.QueryLogAsync`; `PricingAuditLogRow` in `PricingAuditDtos.cs`.

**Tests first:** newest-first ordering; `domain` filter (plan/margin/credit/promo/byok derived from
event type prefix); `diff` carries old→new from event `data`; pagination (`limit`/`offset`/`total`).

**Approach:** union tenant + control-plane pricing events in the requested window, map to rows,
derive `domain` from the event type, derive `diff` from the producer's `data` (producers should
include `before`/`after` keys — note this in the producer-pattern doc).

### Task 7 — Admin + tenant endpoints

**Files:**
- Modify `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` (created by 34-5):
  add `GetAudit`, `ReplayUsage`, `GetAuditLog` handlers under `/api/admin/pricing/audit*`
  (`PlatformOwnerAccess`).
- Modify `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` (created by 34-4): add
  `GET /api/v1/orgs/{tenantId}/pricing/audit` (`MemberAccess`, scoped to caller tenant, margin
  fields stripped).
- Modify `Program.cs` to map the routes (mirror existing admin route registration ~1244+).
- Modify `Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` (created by 34-1): register
  `IPricingEventEmitter` + `IPricingAuditService`.

**Tests first** (`tests/Tamma.Api.Tests/Pricing/AdminPricingAuditEndpointsTests.cs` +
`PricingAuditIsolationTests.cs`):
- RBAC matrix (owner 200 / non-owner 403); tenant endpoint allows `member` read but response omits
  `marginUsd`/`costBasisUsd`/`MarginPolicySnapshot`; cross-tenant (A → B) 403/404; `at` defaults to
  now; tenant reconstruction reads only the requesting tenant's store.

**Approach:** static minimal-API handlers + DTOs (mirror `AdminTenantDatabasesEndpoints`). The
tenant projection is a separate `TenantPricingAuditSnapshot` mapping that drops margin fields — pin
that in the isolation test, not just the UI.

### Task 8 — Dashboard config-audit tab

**Files:**
- New `packages/dashboard/src/services/admin/pricing-audit-client.ts` (mirror
  `admin-tenants-client.ts`).
- New `packages/dashboard/src/pages/admin/PricingAuditTab.tsx` (columns: eventType, actor,
  occurredAt, domain, diff; filters by tenant/domain/date).
- Modify `packages/dashboard/src/pages/admin/AdminLayout.tsx` — add `'pricing-audit'` to `AdminTab`
  + `TABS` + render switch.
- New `packages/dashboard/src/pages/admin/__tests__/PricingAuditTab.test.tsx`.

**Tests first** (Vitest + Testing Library): renders rows with actor + diff; empty state; filter
calls client. `pnpm test --filter @tamma/dashboard` green; no new lint errors.

---

## Sequencing & dependencies

```
Task 1 (taxonomy/tags) ─┬─→ Task 2 (emitter) ─→ Task 3 (atomicity test)
                        │
                        └─→ Task 4 (reconstruct) ─→ Task 5 (replay) ─→ Task 6 (log)
                                                                          │
                                          Tasks 2/4/5/6 ─→ Task 7 (endpoints) ─→ Task 8 (dashboard)
```

- **Hard prerequisites (must be merged first):** Story 34-1 (creates the `Services/Pricing/`
  directory, `IPlanCatalogService`, `EntitlementMetricKey`), 34-4 (`PricingEndpoints`,
  `TenantPlanAssignment`, `GetForTenantAsync`), 34-5 (`AdminPricingEndpoints`,
  `IUsagePricingEngine`, `MarginPolicy`). Epic 4 DCB stores already exist.
- **Soft prerequisites (graceful-degradation if absent):** 34-3 (BYOK mode), 34-6 (credits),
  34-7 (promos) — their events are folded when present; reconstruction omits the dimension when not.
- Task 1 has no dependency and can start immediately. Task 3 (atomicity) gates the merge — it is the
  invariant the whole story exists to guarantee. Task 8 is independent of the C# tasks once the
  endpoints (Task 7) exist or are stubbed in the client.

---

## Risks + mitigations

- **A producer emits an event NOT through the emitter (string literal / own transaction) → audit
  gap.** *Mitigation:* `PricingEventTypes` constants + a grep-guard test that fails if a pricing
  event string literal appears outside `PricingEventTypes.cs`; document the canonical producer
  pattern in each producer story's Dev Notes; the atomicity test (Task 3) proves the pattern.
- **Emitter opening its own transaction defeats atomicity.** *Mitigation:* emitter enlists in the
  producer's `DbContext`; Task 3 integration test is the guard (write it red first).
- **Reconstruction drifts from live state as new dimensions are added.** *Mitigation:* the
  point-in-time-==-live test (Task 4) compares against `IPlanCatalogService.GetForTenantAsync`, so
  any new live dimension that isn't reconstructed fails the test.
- **BYOK key leaks into an event `data` field.** *Severity: critical.* *Mitigation:* opaque-ref-only
  contract + `CredentialRedactor.Clean` on every string + a redaction test asserting no secret
  prefix survives.
- **Replay reaches for the current policy instead of the historical one → wrong audit number.**
  *Mitigation:* `ReplayUsageAsync` resolves the policy at `usageEvent.CreatedAt`; the
  "policy edited after usage doesn't change replay" test (Task 5) pins it.
- **Event-store topology shift (Story 28-1 / Epic 30 moves tenant events fully per-tenant).**
  *Mitigation:* tenant reads go through `IEventRepository` (already per-tenant-abstracted);
  catalog/margin reads pinned to `IPlatformEventRepository` so the shift only touches tenant
  routing.
- **`Tenant.Plan` is still a string today.** *Mitigation:* reconstruction relies on 34-4's
  `TenantPlanAssignment` / `PlanId` source-of-truth — 34-4 is a hard prerequisite; do not start
  Task 4 against the legacy string field.
- **Migration discipline.** No new table; after wiring run
  `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` and confirm none.

---

## Acceptance criteria (mirrors the story)

- [ ] Canonical taxonomy as `const string`s in `PricingEventTypes.cs` covering `PLAN.*`,
      `TENANT.PLAN.*`, `PRICING.MARGIN.*`, `CREDIT.*`, `PROMO.*`, `BYOK.MODE.CHANGED`; no producer
      uses a string literal (grep-guard test green).
- [ ] Required tag contract (`tenantId`/`planId`/`planVersion`/`actorUserId`/`pricingMode`/`source`)
      enforced by `PricingEventTagValidator`; missing tag detected in tests.
- [ ] `PricingEventEmitter` routes tenant events → `DomainEvent` and catalog/margin → `PlatformEvent`
      (verified per event type), redacts every string `data` value, BYOK ref opaque-only.
- [ ] Emitted-with-state transactional invariant: forced pre-commit failure leaves NEITHER row NOR
      event; happy path commits BOTH (docker-bound integration test green).
- [ ] `GET /api/admin/pricing/audit?tenantId=&at=` reconstructs effective plan version + margin +
      entitlements + BYOK mode + active credits/promos at T (`PlatformOwnerAccess`).
- [ ] `GET /api/admin/pricing/audit/replay` re-prices a historical usage line against the policy
      effective then; recomputed `sellPriceUsd` byte-stable vs golden (regression test green).
- [ ] Point-in-time reconstruction == live state for the synthetic history fixture.
- [ ] Config-audit log feed (`/api/admin/pricing/audit/log`) + `PricingAuditTab` render
      plan/margin/credit/promo/byok changes with actor + diff.
- [ ] Tenant-scoped audit (`/api/v1/orgs/{id}/pricing/audit`) returns the tenant's own history with
      margin/cost-basis stripped; cross-tenant request 403/404; reads only the tenant's store.
- [ ] Per-mode RBAC matrix (owner / tenant_owner / member / cross-tenant) covered by tests.
- [ ] Full C# suite + `pnpm test --filter @tamma/dashboard` green; `has-pending-model-changes`
      reports none.
