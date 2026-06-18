# Story 34-8: Pricing Audit, Events & Reproducibility

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide covers the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging, test-first development, 100% coverage on critical paths, and build-success enforcement.

## User Story

As a **platform owner (and a compliance/finance auditor acting on their behalf)**,
I want every pricing decision — a plan version published, a tenant's plan changed, a margin policy edited, a credit grant, a promo applied, a BYOK-mode flip — captured as a canonical DCB event with consistent tags, and a query API that reconstructs exactly "what plan / price / entitlements / BYOK-mode applied to tenant X at timestamp T" by replaying those events,
so that the whole Epic 34 pricing stack satisfies Tamma's 100%-audit-trail goal, no priced amount is ever un-explainable, and a historical invoice line can be re-derived deterministically (time-travel debugging of money).

## Priority

P1 — This is the cross-cutting consistency layer for the whole epic. Stories 34-1..34-7 each emit their own events; without one canonical taxonomy, one emitter that writes them transactionally with the state change, and one reconstruction API, the audit trail is fragmented and the "reproducible pricing" promise of 34-1/34-5 cannot be verified.

## Acceptance Criteria

1. A documented **pricing event catalog** (file `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` + a markdown table in this story) defines every pricing event as `AGGREGATE.ACTION.STATUS`: `PLAN.VERSION.CREATED`, `PLAN.DEPRECATED` (34-1), `TENANT.PLAN.CHANGED`, `TENANT.PLAN.CANCELLED` (34-4), `PRICING.MARGIN.UPDATED` (34-5), `CREDIT.GRANTED` / `CREDIT.CONSUMED` / `CREDIT.EXPIRED` (34-6), `PROMO.APPLIED` / `PROMO.REVOKED` (34-7), and `BYOK.MODE.CHANGED` (34-3) — each as a `const string` so producers reference the same symbol, never a string literal.
2. A **canonical tag contract** is enforced by `PricingEventEmitter`: every pricing event carries the required tags `tenantId` (or empty for catalog-global events such as `PLAN.VERSION.CREATED`), `planId`, `planVersion`, `actorUserId`, `pricingMode` (`platform_provided`|`byok`), and `source` (`admin`|`system`|`tenant`). A `PricingEventTagValidator` rejects (in tests and in a DEBUG assertion) any emit missing a required tag for that event type.
3. `PricingEventEmitter` (`apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventEmitter.cs`) is the **single write seam** that stories 34-1..34-7 call. It routes each event to the correct plane exactly like `AlertEventEmitter`: tenant-scoped events (`TENANT.PLAN.*`, `CREDIT.*`, `PROMO.*`, `BYOK.MODE.CHANGED`) → tenant `DomainEvent` store via `IEventRepository.AppendAsync`; control-plane / catalog-global events (`PLAN.VERSION.CREATED`, `PLAN.DEPRECATED`, `PRICING.MARGIN.UPDATED`) → control-plane `PlatformEvent` store via `IPlatformEventPublisher.AppendAndPublishAsync`.
4. Every priced state change is **emitted transactionally with the entity write** — the event append and the entity mutation share one DB transaction (the same `DbContext.SaveChangesAsync` for control-plane writes; tenant-scope appends join the producer's tenant `DbContext` transaction). An integration test asserts the invariant: a forced failure after the entity write but before commit leaves NEITHER the row NOR the event (no state change exists without its audit event, and no orphan audit event exists without its state change).
5. `GET /api/admin/pricing/audit?tenantId={guid}&at={iso8601}` (`PlatformOwnerAccess`) reconstructs the **effective pricing snapshot** for the tenant at timestamp `T` by replaying/filtering the pricing event streams: effective `planId` + `planVersion` (last `TENANT.PLAN.CHANGED`/`CANCELLED` ≤ T), effective `MarginPolicy` (last `PRICING.MARGIN.UPDATED` ≤ T resolved by scope order provider→plan→global per 34-5), resolved entitlements (from the plan version snapshot via `IPlanCatalogService`), BYOK mode (last `BYOK.MODE.CHANGED` ≤ T), and any active credits/promos at T. Response is a `PricingAuditSnapshot` DTO.
6. **Price replay is reproducible:** `GET /api/admin/pricing/audit/replay?tenantId={guid}&usageEventId={guid}` re-runs `IUsagePricingEngine.PriceUsage` (34-5) against the historical usage line (read from `ProviderDiagnostic`/the 34-3 usage event) **and the `MarginPolicy` effective at that usage event's timestamp**, and returns the recomputed `{ costBasisUsd, marginUsd, sellPriceUsd, pricingMode }`. A replay regression test asserts the recomputed `sellPriceUsd` is byte-stable against a golden value for a synthetic usage+policy history.
7. A **point-in-time-equals-live invariant test** builds a synthetic plan-change history (assign v1 → upgrade to v2 → change margin → flip BYOK) and asserts that `GET /api/admin/pricing/audit?at=now` returns the same effective snapshot as the live read paths (`IPlanCatalogService.GetForTenantAsync` + the live `MarginPolicy` resolution + live BYOK mode) — the replay reconstruction never drifts from current state.
8. A **configuration-audit feed** powers the admin dashboard: `GET /api/admin/pricing/audit/log?tenantId=&domain=&from=&to=` (`PlatformOwnerAccess`) returns a paginated, time-ordered list of pricing config changes (plan/margin/credit/promo/byok) each with `eventType`, `actorUserId`, `occurredAt`, and a `diff` (old→new) field; a new `packages/dashboard` admin tab (`PricingAuditTab.tsx`) renders it with actor + diff columns.
9. **Sensitive-data redaction:** BYOK secret references in `BYOK.MODE.CHANGED` events (and any event `data` field that could carry a key) are emitted as opaque refs only (the secret-cabinet `SecretRow` id / handle), never plaintext keys; every free-text error/detail field passes through `CredentialRedactor.Clean` (`apps/tamma-elsa/src/Tamma.Core/Redaction/`) before serialisation, mirroring `AlertEventEmitter`.
10. **Per-mode handling:** the audit/reconstruction API is platform-owned in both modes. In single-user mode the sole user is the platform owner and reads the audit for their own (single) tenant; in SaaS mode `PlatformOwnerAccess` gates the admin audit endpoints and a tenant-scoped read (`GET /api/v1/orgs/{tenantId}/pricing/audit`, `MemberAccess`) lets a tenant see their OWN pricing history (no margin-policy internals — margin/cost-basis fields are stripped from the tenant projection, since markup is a platform secret).
11. **Per-tenant isolation:** the tenant-scoped audit read never returns another tenant's events; the reconstruction reads tenant-scope events via the per-tenant `DomainEvent` store (per-tenant connection is the isolation plane) and only catalog-global events (which carry no other tenant's data) from the control plane. A cross-tenant isolation test (tenant A's owner requesting tenant B's audit) returns 404/403.
12. **Idempotent / dedup-safe emission:** `PricingEventEmitter` tolerates the `IPlatformEventPublisher` dedup no-op (null return = "already recorded" on a retried write) and never double-counts an event; the emitter itself never throws into the producer's transaction in a way that would silently drop the audit row — a failed append rolls back the state change (AC 4), it does not swallow.
13. Unit + integration tests cover: tag-contract validation per event type, plane-routing per event type, the emitted-with-state transactional invariant, point-in-time reconstruction == live state, price-replay determinism (golden file), redaction of BYOK refs, per-mode RBAC matrix (owner / tenant_owner / member / cross-tenant), and the config-audit log diff shaping.

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Services/Pricing/              # directory created by 34-1; this story adds the audit seam
    PricingEventTypes.cs                   # NEW — const string catalog (PLAN.*, TENANT.PLAN.*, PRICING.MARGIN.*, CREDIT.*, PROMO.*, BYOK.*)
    IPricingEventEmitter.cs                # NEW (Tamma.Api.Services.Pricing)
    PricingEventEmitter.cs                 # NEW — single write seam; dual-plane routing + redaction
    PricingEventTags.cs                    # NEW — required-tag contract + PricingEventTagValidator
    IPricingAuditService.cs                # NEW — reconstruction + replay read seam
    PricingAuditService.cs                 # NEW — replays event streams into a PricingAuditSnapshot
    PricingAuditSnapshot.cs                # NEW — immutable reconstruction DTO (record)
    PricingAuditDtos.cs                    # NEW — audit-log row + replay response DTOs
  Tamma.Api/Endpoints/Admin/
    AdminPricingEndpoints.cs               # MODIFIED (created by 34-5) — add /audit, /audit/replay, /audit/log handlers
  Tamma.Api/Endpoints/
    PricingEndpoints.cs                    # MODIFIED (created by 34-4) — add tenant-scoped /orgs/{id}/pricing/audit
  Tamma.Api/Extensions/
    PricingServiceCollectionExtensions.cs  # MODIFIED (created by 34-1) — register emitter + audit service
  Tamma.Data/Repositories/
    IEventRepository.cs / EventRepository.cs           # REUSED — tenant DomainEvent append/query
    IPlatformEventRepository.cs / PlatformEventRepository.cs  # REUSED — control-plane PlatformEvent query
  Tamma.Core/Redaction/
    CredentialRedactor.cs                  # REUSED — scrub free-text fields before serialisation

packages/dashboard/src/
  services/admin/pricing-audit-client.ts   # NEW — typed client (mirrors admin-tenants-client.ts)
  pages/admin/PricingAuditTab.tsx          # NEW — config-audit feed (actor + diff columns)
  pages/admin/AdminLayout.tsx              # MODIFIED — add 'pricing-audit' to AdminTab union + TABS
```

### Event catalog — `PricingEventTypes` (the canonical taxonomy)

```csharp
namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Canonical pricing event taxonomy (AGGREGATE.ACTION.STATUS). Every Epic 34
/// producer references THESE constants — never a string literal — so the
/// audit reconstruction (PricingAuditService) and the alert evaluator match
/// a stable set. Plane assignment is data, not a producer concern: see
/// <see cref="PricingEventTags.PlaneFor"/>.
/// </summary>
public static class PricingEventTypes
{
    // Catalog-global (control plane → PlatformEvent)
    public const string PlanVersionCreated = "PLAN.VERSION.CREATED";   // 34-1
    public const string PlanDeprecated     = "PLAN.DEPRECATED";        // 34-1
    public const string MarginUpdated      = "PRICING.MARGIN.UPDATED"; // 34-5

    // Tenant-scoped (tenant DomainEvent store)
    public const string TenantPlanChanged   = "TENANT.PLAN.CHANGED";   // 34-4
    public const string TenantPlanCancelled = "TENANT.PLAN.CANCELLED"; // 34-4
    public const string CreditGranted       = "CREDIT.GRANTED";        // 34-6
    public const string CreditConsumed      = "CREDIT.CONSUMED";       // 34-6
    public const string CreditExpired       = "CREDIT.EXPIRED";        // 34-6
    public const string PromoApplied        = "PROMO.APPLIED";         // 34-7
    public const string PromoRevoked        = "PROMO.REVOKED";         // 34-7
    public const string ByokModeChanged     = "BYOK.MODE.CHANGED";     // 34-3
}
```

> **Boundary note:** this story does NOT define the *business semantics* of credits, promos, or BYOK mode (those are owned by 34-6, 34-7, 34-3). It owns the canonical event *names + tag contract + emission + reconstruction*. The producer stories call `IPricingEventEmitter` with their domain data; this story guarantees the tags and the audit replay.

### Required-tag contract — `PricingEventTags`

```csharp
namespace Tamma.Api.Services.Pricing;

public sealed record PricingEventTagSet(
    Guid? TenantId,        // null/empty for catalog-global events
    Guid? PlanId,
    int? PlanVersion,
    Guid? ActorUserId,
    string PricingMode,    // "platform_provided" | "byok"
    string Source);        // "admin" | "system" | "tenant"

public static class PricingEventTags
{
    /// <summary>Which plane an event type writes to.</summary>
    public static EventPlane PlaneFor(string eventType) => eventType switch
    {
        PricingEventTypes.PlanVersionCreated
            or PricingEventTypes.PlanDeprecated
            or PricingEventTypes.MarginUpdated => EventPlane.ControlPlane,
        _ => EventPlane.Tenant,
    };

    /// <summary>Required tag keys per event type — drives PricingEventTagValidator.</summary>
    public static IReadOnlyList<string> RequiredFor(string eventType);
}

public enum EventPlane { Tenant, ControlPlane }
```

`PricingEventTagValidator.Validate(eventType, tags)` returns the missing-key list. `PricingEventEmitter` calls it; in DEBUG a non-empty result is a `Debug.Assert` (loud in tests), in RELEASE it is a WARN log + the event is still emitted with the tags present (never drop an audit row over a tag bug).

### Emitter — `PricingEventEmitter` (single write seam)

```csharp
public interface IPricingEventEmitter
{
    /// <summary>
    /// Append a pricing event to the correct plane, with the canonical tag
    /// set, transactionally with the producer's state change. The producer
    /// passes its open tenant/control-plane DbContext so the append joins
    /// the SAME transaction — no state change without its audit event.
    /// </summary>
    Task EmitAsync(
        string eventType,
        PricingEventTagSet tags,
        IReadOnlyDictionary<string, object?> data,
        DbContext producerContext,
        CancellationToken ct = default);
}
```

Implementation mirrors `AlertEventEmitter`:
- Serialise `tags` (string-typed JSONB) and `data` (after running every string value through `CredentialRedactor.Clean`).
- `Metadata` = `{"eventSource":"system","workflowVersion":"1.0.0"}`.
- Route by `PricingEventTags.PlaneFor(eventType)`:
  - **Tenant** → add a `DomainEvent` row to the producer's tenant `DbContext` (`db.DomainEvents.Add(...)`); the producer's single `SaveChangesAsync`/transaction commits both. (For producers that aren't already in a tenant `DbContext` — e.g. an Elsa activity — fall back to `IEventRepository.AppendAsync`.)
  - **Control plane** → add a `PlatformEvent` to the producer's `ControlPlaneDbContext`; or, when emitted standalone, `IPlatformEventPublisher.AppendAndPublishAsync` (tolerate the null dedup no-op return).

The transactional contract (AC 4) is the load-bearing piece: the emitter does **not** open its own transaction — it enlists in the producer's. The producer pattern (documented for 34-1..34-7 to follow) is:

```csharp
// inside e.g. PlanAssignmentService.AssignAsync (34-4)
db.TenantPlanAssignments.Add(assignment);
await emitter.EmitAsync(PricingEventTypes.TenantPlanChanged, tags, data, db, ct);
await db.SaveChangesAsync(ct);   // one commit → row + event, atomically
```

### Reconstruction — `PricingAuditService`

```csharp
public interface IPricingAuditService
{
    /// <summary>Effective pricing snapshot for a tenant at timestamp T.</summary>
    Task<PricingAuditSnapshot> ReconstructAsync(Guid tenantId, DateTime at, CancellationToken ct);

    /// <summary>Re-price a historical usage line against the policy effective then.</summary>
    Task<PricingReplayResult> ReplayUsageAsync(Guid tenantId, Guid usageEventId, CancellationToken ct);

    /// <summary>Paginated, time-ordered config-change log (admin feed).</summary>
    Task<(IReadOnlyList<PricingAuditLogRow> Rows, int Total)> QueryLogAsync(
        Guid? tenantId, string? domain, DateTime? from, DateTime? to, int limit, int offset, CancellationToken ct);
}
```

`ReconstructAsync` algorithm:
1. Tenant-scope stream: `IEventRepository.QueryWithPaginationAsync(tenantId, type: null, ...)` filtered to the tenant pricing event types, `CreatedAt <= at`, newest-first → take last `TENANT.PLAN.CHANGED`/`CANCELLED` (effective plan+version), last `BYOK.MODE.CHANGED` (effective mode), and fold `CREDIT.*` / `PROMO.*` into the set active at T.
2. Control-plane stream: `IPlatformEventRepository.QueryAsync(typePrefix: "PRICING.MARGIN", ...)` filtered `<= at` → resolve the effective `MarginPolicy` per 34-5's scope order (provider→plan→global). `PLAN.VERSION.CREATED`/`DEPRECATED` are consulted to confirm the effective version was active at T.
3. Entitlements: hand the resolved `(planId, planVersion)` to `IPlanCatalogService.GetByIdAsync` (34-1) for the entitlement/feature/price snapshot.
4. Assemble `PricingAuditSnapshot { TenantId, At, PlanId, PlanVersion, PlanSlug, PricingMode, MarginPolicySnapshot, Entitlements, ActiveCredits, ActivePromos }`.

`ReplayUsageAsync` reads the usage line (`ProviderDiagnostic` row / 34-3 usage event by id), resolves the `MarginPolicy` effective at `usageEvent.CreatedAt` (NOT now), and calls `IUsagePricingEngine.PriceUsage(...)` (34-5) — the engine is pure, so feeding it the historical policy + historical tokens reproduces the original `sellPriceUsd` exactly.

### DCB event names emitted/consumed (this story)

| Event | Plane | Producer (story) | This story's role |
|---|---|---|---|
| `PLAN.VERSION.CREATED`, `PLAN.DEPRECATED` | control plane | 34-1 | catalog + emitter + audit reads |
| `TENANT.PLAN.CHANGED`, `TENANT.PLAN.CANCELLED` | tenant | 34-4 | emitter + reconstruction |
| `PRICING.MARGIN.UPDATED` | control plane | 34-5 | emitter + margin replay |
| `CREDIT.GRANTED` / `CONSUMED` / `EXPIRED` | tenant | 34-6 | emitter + active-credit fold |
| `PROMO.APPLIED` / `PROMO.REVOKED` | tenant | 34-7 | emitter + active-promo fold |
| `BYOK.MODE.CHANGED` | tenant | 34-3 | emitter + redaction + mode reconstruction |

This story emits no *new* event type of its own — it is the consistency layer. (The audit *read* endpoints are pure reads; they do not emit.)

### API shape

```
# Admin (PlatformOwnerAccess) — under /api/admin/pricing (group created by 34-5)
GET /api/admin/pricing/audit?tenantId={guid}&at={iso8601}
      → 200 PricingAuditSnapshot
GET /api/admin/pricing/audit/replay?tenantId={guid}&usageEventId={guid}
      → 200 { costBasisUsd, marginUsd, sellPriceUsd, pricingMode, marginPolicyEffectiveAt }
GET /api/admin/pricing/audit/log?tenantId=&domain=&from=&to=&limit=50&offset=0
      → 200 { rows: PricingAuditLogRow[], total } ; PricingAuditLogRow = { eventType, actorUserId, occurredAt, domain, diff }

# Tenant-scoped (MemberAccess) — under /api/v1/orgs/{tenantId} (group created by 34-4)
GET /api/v1/orgs/{tenantId}/pricing/audit?at={iso8601}
      → 200 TenantPricingAuditSnapshot  (margin/cost-basis fields STRIPPED — platform secret)
```

`at` defaults to `now` when omitted. All admin endpoints sit behind the existing `PlatformOwnerAccess` policy (Program.cs ~986); the tenant endpoint behind `MemberAccess` and explicitly scopes to the caller's tenant.

### Per-mode + per-tenant handling (CLAUDE.md two-scoping-model rule)

| Question | single-user | SaaS |
|---|---|---|
| Who reads the full pricing audit (incl. margin)? | The sole user (= platform owner); their one tenant. | `PlatformOwnerAccess` (platform owner) only. |
| Who reads the tenant-scoped audit (no margin internals)? | The sole user. | `tenant_owner`/`tenant_admin`/`member` for their OWN tenant via `/orgs/{id}/pricing/audit`. |
| Where do tenant-scope events live? | The sole user's tenant `DomainEvent` store. | The tenant's per-tenant `DomainEvent` store (isolation plane). |
| Where do catalog/margin events live? | Control-plane `PlatformEvent` store. | Same — global, never per-tenant. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Migration discipline

This story adds **no new tables** — it reuses `domain_events` (tenant) and `platform_events` (control plane). No EF migration is required. If the audit-log diff needs an index hint, it is a read-only query against existing `Type`/`CreatedAt`/`SequenceNumber` columns already indexed for `AlertRuleEvaluator`. Verify `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none after wiring (it must — no model change).

## Dependencies

**Internal Dependencies:**
- **Prerequisite — Story 34-1** (Plan & Price-Book Catalog): provides `Plan` versioning, `IPlanCatalogService.GetByIdAsync` (entitlement snapshot for reconstruction), and the `PLAN.VERSION.CREATED`/`PLAN.DEPRECATED` event producers that route through this story's emitter.
- **Prerequisite — Story 34-4** (Per-Tenant Plan Assignment): provides `TenantPlanAssignment`, `IPlanCatalogService.GetForTenantAsync` (live-state oracle for AC 7), the `PricingEndpoints` `/orgs/{id}` group, and the `TENANT.PLAN.CHANGED`/`CANCELLED` producers.
- **Prerequisite — Story 34-5** (Cost→Price Markup Engine): provides `IUsagePricingEngine.PriceUsage` (replayed by AC 6), `MarginPolicy` (timestamp-effective resolution), `AdminPricingEndpoints` (the `/api/admin/pricing` group this story extends), and `PRICING.MARGIN.UPDATED`.
- **Prerequisite — Epic 4 (DCB event sourcing)**: `DomainEvent`/`PlatformEvent` entities, `IEventRepository`, `IPlatformEventRepository`/`IPlatformEventPublisher` — the stores this story replays.
- **Soft-coupled — Stories 34-3 (BYOK mode), 34-6 (credits), 34-7 (promos)**: this story defines `BYOK.MODE.CHANGED` / `CREDIT.*` / `PROMO.*` in the canonical taxonomy and folds them into reconstruction; those stories produce the events via `IPricingEventEmitter`. If a producer story is not yet merged, its events simply don't appear in the stream — reconstruction degrades gracefully (the snapshot omits that dimension, never errors).
- **Reuses — `Tamma.Core/Redaction/CredentialRedactor`** (already exists) and the secret cabinet (`SecretRow`, Epic 29) for opaque BYOK refs.

**External Dependencies:**
- None at runtime. (No Stripe — billing/charging is Epic 35. This story re-derives priced amounts for *audit*, it never moves money.) Tests mock `IUsagePricingEngine`, `IPlanCatalogService`, and the event repositories.

**Blocks:**
- **Epic 35 (Billing)**: invoice line items reference the reconstructed pricing for dispute/audit; Epic 35 reads (does not re-implement) this audit API.
- **Epic 36-7 (Analytics view)**: the config-audit feed and replay are the source for the pricing analytics surface.

## Testing Strategy

1. **Tag-contract unit tests** (`PricingEventTagValidatorTests`): for each event type, a tag set missing a required key is reported; a complete set validates; catalog-global events do not require `tenantId`.
2. **Plane-routing unit tests** (`PricingEventEmitterTests`): mock `IEventRepository` + `IPlatformEventPublisher`; assert `TENANT.PLAN.CHANGED` lands on the tenant store and `PRICING.MARGIN.UPDATED` on the control plane; assert `CredentialRedactor.Clean` ran on every string `data` value; assert a BYOK ref is an opaque handle (no `tamma_sk_`/`sk_live_` pattern survives — reuse the redactor's prefix set).
3. **Transactional-invariant integration test** (`PricingAuditTransactionTests`, docker-bound): drive a producer-style write that adds the entity + calls `EmitAsync`, then force a failure before commit → assert NEITHER row NOR event persisted; happy path → assert BOTH persisted. Run via `sg docker -c "dotnet test ..."`.
4. **Point-in-time == live test** (`PricingAuditReconstructionTests`): build a synthetic history (assign v1 → upgrade v2 → margin edit → BYOK flip) by appending events; assert `ReconstructAsync(now)` matches `IPlanCatalogService.GetForTenantAsync` + live margin + live BYOK; assert `ReconstructAsync(t_before_upgrade)` returns v1.
5. **Price-replay determinism test** (`PricingReplayTests`, golden file): a synthetic usage line + a `MarginPolicy` history; `ReplayUsageAsync` recomputes `sellPriceUsd` byte-stable against a checked-in golden value; changing the policy *after* the usage timestamp does not change the replayed price.
6. **Tenant-isolation tests** (`PricingAuditIsolationTests`): tenant A's owner requesting tenant B's `/orgs/{B}/pricing/audit` → 403/404; the tenant projection strips `marginUsd`/`costBasisUsd`/`MarginPolicySnapshot`; reconstruction reads only the requesting tenant's `DomainEvent` store.
7. **RBAC matrix tests** (`AdminPricingAuditEndpointsTests`): admin endpoints reject non-platform-owner with 403; tenant endpoint allows `member` read but the response omits margin internals; `at` defaulting to now.
8. **Config-audit-log shaping test**: `QueryLogAsync` orders newest-first, filters by `domain` (plan/margin/credit/promo/byok derived from event type), and the `diff` field carries old→new from the event `data`.
9. **Dashboard tests** (Vitest + Testing Library, colocated): `PricingAuditTab` renders rows with actor + diff columns; empty state; `pnpm test --filter @tamma/dashboard` green.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPricingEventEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTags.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPricingAuditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingAuditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingAuditSnapshot.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingAuditDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` | Modify (created by 34-5 — add audit/replay/log handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify (created by 34-4 — add tenant `/orgs/{id}/pricing/audit`) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Modify (created by 34-1 — register emitter + audit service) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map new audit routes) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingEventEmitterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingEventTagValidatorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingAuditReconstructionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingReplayTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingAuditTransactionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingAuditIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/AdminPricingAuditEndpointsTests.cs` | Create |
| `packages/dashboard/src/services/admin/pricing-audit-client.ts` | Create |
| `packages/dashboard/src/pages/admin/PricingAuditTab.tsx` | Create |
| `packages/dashboard/src/pages/admin/__tests__/PricingAuditTab.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Modify (add `pricing-audit` tab) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `.dev/decisions/story-28-1-design-calls.md` for the tenant/control-plane event routing matrix this story depends on)
3. Read stories 34-1, 34-4, 34-5 — this story is the consistency layer over their producers and must reference their real types (`IPlanCatalogService`, `TenantPlanAssignment`, `IUsagePricingEngine`, `MarginPolicy`)
4. Studied `AlertEventEmitter.cs` — it is the exact pattern for dual-plane routing + redaction this story replicates for pricing
5. Planned TDD approach (Red-Green-Refactor); the transactional invariant and replay-determinism tests are the highest-value reds to write first

### Key Design Decisions

- **One emitter, enlisted in the producer's transaction.** The "no state change without its audit event" invariant (AC 4) is impossible if the emitter opens its own transaction or fires-and-forgets. The emitter adds the event row to the *producer's* `DbContext` so a single `SaveChangesAsync` commits both or neither. This is the inverse of `AlertEventEmitter`'s fire-and-forget posture — alerts are best-effort observability; pricing audit is a hard correctness invariant. Document this difference loudly in the emitter XML doc.
- **Reconstruction by event replay, not a snapshot table.** Per Epic 4 DCB philosophy, the event stream is the source of truth; a derived snapshot table would be a second thing to keep consistent. Reconstruction filters the existing `domain_events`/`platform_events` — no new table, no migration.
- **The replay engine is pure (34-5).** `IUsagePricingEngine.PriceUsage` takes a usage line + a margin policy and is deterministic. Feeding it the *historical* policy is the whole trick to reproducibility — never let replay reach for the *current* policy.
- **Margin is a platform secret.** The tenant-scoped projection strips `marginUsd`, `costBasisUsd`, and the `MarginPolicySnapshot`. A tenant can see their plan/version/entitlements/credits history, never the markup math. Enforced in the DTO mapping, not just the UI.
- **Catalog-global events stay control-plane resident** even after Story 28-1/Epic 30 moves tenant events fully per-tenant — reconstruction always reads margin/plan-catalog events from `IPlatformEventRepository`, so the topology shift only touches tenant-scope routing (already abstracted behind `IEventRepository`).

### Integration Points

- **34-1..34-7 producers** call `IPricingEventEmitter.EmitAsync` in their write transaction (the canonical producer pattern shown above) — this story ships the emitter and the tests that pin the contract; the producer stories adopt it.
- **`IPlanCatalogService` (34-1)** is the entitlement oracle for both reconstruction and the AC-7 live-state comparison.
- **`IUsagePricingEngine` + `MarginPolicy` (34-5)** are the replay engine + policy source.
- **`AlertRuleEvaluator` (Story 5.6)** already polls `domain_events`/`platform_events`; because pricing events now land there consistently, a future built-in rule (e.g. on `PRICING.MARGIN.UPDATED`) is free — out of scope here, but the consistent emission makes it possible.

### Risks and Mitigations

| Risk | Severity | Mitigation |
| --- | --- | --- |
| A producer story emits an event NOT through the emitter (string literal, own transaction) → audit gap | High | The canonical producer pattern + `PricingEventTypes` constants + a grep-guard test that fails if a pricing event string literal appears outside `PricingEventTypes`; document in each producer story's Dev Notes. |
| Emitter opening its own transaction defeats the atomicity invariant | High | Emitter takes the producer's `DbContext`; the transactional integration test (AC 4) is the guard. |
| Reconstruction drifts from live state as new dimensions are added (e.g. a future entitlement field) | Medium | The point-in-time-==-live test (AC 7) runs against `IPlanCatalogService.GetForTenantAsync`, so any new live dimension that isn't reconstructed fails the test. |
| BYOK key leaks into an event `data` field | Critical | Opaque-ref-only contract (AC 9) + `CredentialRedactor.Clean` on every string + a redaction test asserting no secret-prefix survives. |
| Event-store topology shift (Story 28-1 / Epic 30) | Medium | Tenant reads go through `IEventRepository` (already abstracted per-tenant); catalog reads pinned to `IPlatformEventRepository`. |

### Success Metrics

- [ ] 100% of Epic 34 priced state changes have a matching audit event (verified by the grep-guard + transactional invariant tests).
- [ ] `ReconstructAsync(now)` == live state for the synthetic history fixture (zero drift).
- [ ] Replayed `sellPriceUsd` matches the golden value byte-for-byte across runs.

## Logging Requirements

- **INFO**: audit snapshot served (`tenantId`, `at`, resolved `planId`/`planVersion`), price replay served (`usageEventId`, recomputed `sellPriceUsd`), config-audit log queried (`tenantId`, `domain`, row count).
- **DEBUG**: pricing event emitted (`eventType`, plane, `tenantId`), reconstruction stream fold (events scanned, effective plan/margin/byok chosen), replay margin-policy-effective-at resolution.
- **WARN**: emitted event missing a required tag (RELEASE path — event still written), reconstruction found a usage event with an unknown provider/model (replay surfaces `PricingUnknownModel` from 34-5), tenant requested an `at` before any pricing history (empty snapshot returned).
- **ERROR**: producer transaction rolled back because the audit append failed (the atomicity invariant firing — this is the *correct* loud failure), control-plane event store unreachable during reconstruction.
- **Structured context**: include `{ tenantId, eventType, planId, planVersion, pricingMode, usageEventId, at }` where applicable.
- **Credential safety**: NEVER log BYOK plaintext keys, encrypted connection strings, or secret-cabinet values; BYOK is logged as the opaque `SecretRow` handle only; all free-text error fields pass through `CredentialRedactor.Clean`.

## References

- **MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../../.dev/README.md)
- Sibling stories: `docs/stories/epic-34/story-34-1/`, `story-34-4/`, `story-34-5/`
- Routing precedent: `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/AlertEventEmitter.cs`
- Event stores: `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`, `PlatformEventRepository.cs`
- Redaction: `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs`
- Implementation plan: `docs/superpowers/plans/2026-06-17-34-8-pricing-audit-events-and-reproducibility-plan.md`

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
