# Story 37-6: Legal Hold

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **compliance/legal operator** (platform owner) and a **tenant administrator**,
I want to place a legal hold that freezes audit records (and optionally related per-tenant data) matching a defined scope — time range, actor, subject, or case reference,
So that retention pruning (Story 37-5) and right-to-erasure (Story 37-8) cannot delete those records while litigation, investigation, or regulatory inquiry is active, and so the act of placing/releasing a hold is itself an immutable, auditable event.

## Priority

P1 - Required for litigation-readiness and to make destructive compliance operations (retention pruning, GDPR erasure) safe. A hold is a hard "do not delete" gate that overrides every automated and operator-initiated deletion path.

## Context & Architecture Alignment

This story targets the **C# `apps/tamma-elsa`** application — the legacy TypeScript `packages/api` is deleted and is NOT a target.

- **`Tamma.Data`** — owns the audit read-model (created in Story 37-1), the per-tenant `TenantDbContext`, and the platform `ControlPlaneDbContext`. The `legal_holds` registry and the `LegalHold` entity live here.
- **`Tamma.Api`** — owns the HTTP surface (`OrgEndpoints` for tenant scope, `AdminEndpoints` for platform scope) and the hold service.
- **DCB substrate** — every lifecycle and enforcement event is appended via `IEventRepository.AppendAsync(DomainEvent)` (`Tamma.Data/Repositories/EventRepository.cs`), exactly as `AlertEndpoints` already does.

A legal hold sits **upstream** of the two destructive operations in this epic:

```
                  ┌──────────────────────────┐
                  │  legal_holds (registry)  │   ← this story
                  └────────────┬─────────────┘
                               │  ILegalHoldGuard.IsHeld(scope)
              ┌────────────────┴─────────────────┐
              ▼                                   ▼
   Retention pruner (37-5)              Right-to-erasure (37-8)
   skips in-scope records              refuses in-scope records
   emits RETENTION.BLOCKED_BY_HOLD     emits ERASURE.BLOCKED_BY_HOLD
```

Per CLAUDE.md "Operating Modes", ownership is answered for **both** modes (the two-scoping-model rule):

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who places/releases a **tenant-scope** hold? | The sole user (no RBAC). | `tenant_owner` only (route + role gate); `tenant_admin` MAY be granted by configuration but default is owner-only; `member` → 403. |
| Who places/releases a **platform-wide** hold (spans tenants / system audit)? | The sole user (it is their instance). | Platform owner ONLY (`PlatformOwnerAccess`). Never exposed on the tenant surface. |
| Where is hold ownership stored? | `user_id` set, `tenant_id` NULL (principal XOR, mirroring `prompt_overrides`). | `tenant_id` set, `user_id` NULL; platform-wide holds have BOTH NULL (`is_platform_wide = true`). |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

## Acceptance Criteria

1. A **`legal_holds`** table (per-mode owned) stores at minimum: `id` (UUID), `tenant_id` (NULL for platform-wide / single-user-platform holds), `user_id` (set only in single-user mode), `is_platform_wide` (bool), `scope_category` (e.g. `audit` | `tenant-data`), `scope_actor_id`, `scope_subject_id`, `scope_time_from`, `scope_time_to`, `case_ref`, `reason`, `status` (`active` | `released`), `placed_by`, `placed_at`, `released_by`, `released_at`, `created_at`, `updated_at`. A `LegalHold` entity in `Tamma.Data/Entities/LegalHold.cs` maps it; EF config (CHECK + indexes) lives in `TammaModelConfiguration.cs`.

2. A **principal-XOR CHECK constraint** enforces the per-mode ownership invariant: a hold is either tenant-scope (`tenant_id` set, `user_id` NULL, `is_platform_wide=false`), single-user-scope (`user_id` set, `tenant_id` NULL), or platform-wide (`tenant_id` NULL, `user_id` NULL, `is_platform_wide=true`) — never an ambiguous mix. A CHECK also restricts `status` to `{active, released}` and `scope_category` to the allowed set.

3. **Place hold** — tenant scope: `POST /api/v1/orgs/{tenantId}/audit/legal-holds`. Requires `tenant_owner` in SaaS (member → **403**); any user in single-user mode. Body carries the scope (`scope_category`, optional `actor`, `subject`, `time_from`, `time_to`, `case_ref`) + `reason` (required). Forces `tenant_id = {tenantId}` server-side (body `tenantId` mismatch → 400, mirroring `CreateTenantChannel`). Returns `201 Created` with the hold DTO.

4. **Place hold** — platform scope: `POST /api/v1/admin/audit/legal-holds`. Requires `PlatformOwnerAccess`. May target a specific `tenant_id` (cross-tenant hold) or set `is_platform_wide=true` (spans all tenants + system audit). Returns `201`.

5. **Release hold** — `DELETE /api/v1/orgs/{tenantId}/audit/legal-holds/{id}` (tenant, `tenant_owner`; cross-tenant `id` → 404) and `DELETE /api/v1/admin/audit/legal-holds/{id}` (platform, `PlatformOwnerAccess`). Release is a **status flip to `released`** (stamps `released_by`/`released_at`) — rows are NEVER hard-deleted, so the hold history survives for compliance. Releasing an already-released hold → `409 Conflict`.

6. An active hold sets a queryable predicate that 37-5 and 37-8 MUST consult before deleting. This story ships **`ILegalHoldGuard`** with `Task<HoldDecision> EvaluateAsync(LegalHoldQuery candidate, CancellationToken)` returning whether a candidate record (tenant, actor, subject, timestamp, case) is covered by ANY active hold, plus the matching `hold_id`(s). The guard is the single seam both destructive stories depend on.

7. While a hold is active, any prune attempt against in-scope records is **blocked** (the pruner skips them and continues with out-of-scope rows) and emits **`AUDIT.RETENTION.BLOCKED_BY_HOLD`** (tags: `tenantId`, `holdId`, `caseRef`, count of records skipped). The 37-5 pruner calls `ILegalHoldGuard` before each delete batch; this story provides the guard + the event type + the integration test, and adds the call site as a NEW hook in the 37-5 pruner.

8. While a hold is active, any right-to-erasure attempt against in-scope records is **refused** and emits **`AUDIT.ERASURE.BLOCKED_BY_HOLD`** (tags: `tenantId`, `holdId`, `subjectId`, `caseRef`). Erasure of a held subject does not partially delete — it fails closed (the whole erasure request is rejected with a 409-style result surfaced to 37-8's caller) so no in-scope record is removed while held. This story provides the guard + event type + integration test, and adds the call site as a NEW hook in the 37-8 erasure flow.

9. Placing a hold emits **`AUDIT.LEGAL_HOLD.PLACED`** and releasing emits **`AUDIT.LEGAL_HOLD.RELEASED`** — both sensitive, appended to the audit read-model via `IEventRepository.AppendAsync`, immutable (no update/delete path), with tags `tenantId`, `holdId`, `caseRef`, `placedBy`/`releasedBy`, `scopeCategory`, and `isPlatformWide`.

10. **List holds** — `GET /api/v1/orgs/{tenantId}/audit/legal-holds` (tenant members; tenant-scope rows ONLY — never platform-wide or other-tenant rows) and `GET /api/v1/admin/audit/legal-holds` (`PlatformOwnerAccess`; all holds, filterable by `tenantId`, `status`, `caseRef`). Lists return active + historical (released) holds with scope, `placed_by`, `placed_at`, `released_by`, `released_at`, and `status`. Tenant member sees the list read-only and gets **403** on place/release in SaaS.

11. **Overlapping holds are handled additively** — a record covered by ≥1 active hold is held; releasing ONE overlapping hold does NOT unfreeze a record still covered by another. `ILegalHoldGuard.EvaluateAsync` returns `IsHeld=true` while any active hold matches, and the prune/erasure paths re-evaluate per operation (no caching of a stale "not held" decision across a hold placement).

12. **Release re-enables deletion** — once the last covering hold is released, a subsequent prune/erasure of the (now out-of-scope) record proceeds normally and emits the standard 37-5/37-8 success events (no `BLOCKED_BY_HOLD`).

13. Unit + integration tests cover: hold blocks retention prune, hold blocks erasure, release re-enables prune AND erasure, RBAC per mode (single-user any-user; SaaS owner-only place/release, member 403, cross-tenant 404), hold place/release audited (PLACED/RELEASED events appended exactly once), overlapping holds (two holds, release one, still held), and scope matching (actor-only, subject-only, time-range, case-ref, and combined predicates).

## Technical Design

### Entity — `Tamma.Data/Entities/LegalHold.cs` (NEW)

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-6 — a legal hold freezes audit records (and optionally related
/// per-tenant data) matching its scope so retention pruning (37-5) and
/// right-to-erasure (37-8) cannot delete them while <see cref="Status"/> is
/// <c>active</c>. Per-mode owned (CLAUDE.md "Operating Modes"):
/// exactly one of tenant-scope / single-user-scope / platform-wide.
/// </summary>
public class LegalHold
{
    public Guid Id { get; set; }

    // ── Ownership (principal XOR, mirrors prompt_overrides) ──
    public Guid? TenantId { get; set; }       // tenant-scope hold
    public Guid? UserId { get; set; }         // single-user-mode hold
    public bool IsPlatformWide { get; set; }  // spans all tenants + system audit

    // ── Scope predicate (any subset; NULL field = unconstrained) ──
    public string ScopeCategory { get; set; } = "audit"; // audit | tenant-data
    public Guid? ScopeActorId { get; set; }    // the acting user/agent
    public string? ScopeSubjectId { get; set; } // data subject (GDPR subject ref)
    public DateTime? ScopeTimeFrom { get; set; }
    public DateTime? ScopeTimeTo { get; set; }
    public string? CaseRef { get; set; }        // matter / case identifier

    public string Reason { get; set; } = null!;

    // ── Lifecycle ──
    public string Status { get; set; } = LegalHoldStatus.Active; // active | released
    public Guid? PlacedBy { get; set; }
    public DateTime PlacedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTime? ReleasedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class LegalHoldStatus
{
    public const string Active = "active";
    public const string Released = "released";
    public static readonly string[] All = { Active, Released };
}

public static class LegalHoldScopeCategory
{
    public const string Audit = "audit";
    public const string TenantData = "tenant-data";
    public static readonly string[] All = { Audit, TenantData };
}
```

### EF model config — `Tamma.Data/TammaModelConfiguration.cs` (MODIFY)

Mirror the existing `users`/`tenants` style (`ToTable` + `HasCheckConstraint` + `HasIndex`):

```csharp
modelBuilder.Entity<LegalHold>(entity =>
{
    entity.ToTable("legal_holds", t =>
    {
        // Per-mode ownership XOR: tenant-scope OR single-user-scope OR platform-wide.
        t.HasCheckConstraint("ck_legal_holds_principal_xor",
            "(\"TenantId\" IS NOT NULL AND \"UserId\" IS NULL AND \"IsPlatformWide\" = false) " +
            "OR (\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL AND \"IsPlatformWide\" = false) " +
            "OR (\"TenantId\" IS NULL AND \"UserId\" IS NULL AND \"IsPlatformWide\" = true)");
        t.HasCheckConstraint("ck_legal_holds_status",
            "\"Status\" IN ('active','released')");
        t.HasCheckConstraint("ck_legal_holds_scope_category",
            "\"ScopeCategory\" IN ('audit','tenant-data')");
    });

    entity.HasKey(e => e.Id);
    entity.Property(e => e.Reason).IsRequired();

    // Hot path: "is this candidate covered by an active hold?" — filtered
    // partial index keeps the guard fast even with thousands of released rows.
    entity.HasIndex(e => new { e.TenantId, e.Status })
        .HasFilter("\"Status\" = 'active'");
    entity.HasIndex(e => e.CaseRef);
    entity.HasIndex(e => e.ScopeSubjectId);
});
```

Add `public DbSet<LegalHold> LegalHolds => Set<LegalHold>();` to `ControlPlaneDbContext.cs`. (The registry is platform-resident so platform-wide and cross-tenant holds are evaluable in one query; tenant-scope rows carry `tenant_id`. This mirrors how `Alerts`/`AlertChannels` are CP-resident yet carry an optional `TenantId`.) Add an additive EF migration under `Tamma.Data/Migrations/ControlPlane/` (`dotnet ef migrations add AddLegalHolds`); confirm `has-pending-model-changes` reports none afterwards.

### Guard — `Tamma.Api/Services/Audit/ILegalHoldGuard.cs` + `LegalHoldGuard.cs` (NEW)

The single seam 37-5 and 37-8 depend on. Pure read against `legal_holds` where `Status = active`.

```csharp
namespace Tamma.Api.Services.Audit;

/// <summary>Candidate record being considered for deletion.</summary>
public sealed record LegalHoldQuery(
    Guid? TenantId,
    string ScopeCategory,
    Guid? ActorId,
    string? SubjectId,
    DateTime Timestamp,
    string? CaseRef);

/// <summary>Decision returned to the pruner / erasure flow.</summary>
public sealed record HoldDecision(bool IsHeld, IReadOnlyList<Guid> MatchingHoldIds);

public interface ILegalHoldGuard
{
    /// <summary>
    /// Returns <see cref="HoldDecision.IsHeld"/> = true when ANY active hold
    /// covers the candidate. Coverage = ownership match (platform-wide, OR
    /// same tenant, OR single-user) AND every non-null scope predicate
    /// matches (actor, subject, [time_from, time_to], case_ref). NULL scope
    /// fields are wildcards. Overlapping holds are additive — true while one
    /// matches. Never throws "blocked" itself; callers act on the decision.
    /// </summary>
    Task<HoldDecision> EvaluateAsync(LegalHoldQuery candidate, CancellationToken ct);
}
```

`LegalHoldGuard` filters in SQL on ownership + `Status = active` (using the partial index) and applies the scope predicates. Fail-closed contract: if the guard query throws (CP DB unavailable), the pruner/erasure MUST treat the decision as **held** (do not delete) — destructive operations fail safe, not open. This is documented on the interface and asserted by a test.

### Service — `Tamma.Api/Services/Audit/LegalHoldService.cs` (NEW)

Owns place/release/list with per-mode scope derivation + lifecycle event emission (`AUDIT.LEGAL_HOLD.PLACED` / `RELEASED` via `IEventRepository`). Endpoints stay thin (validation + RBAC + DTO), delegating to the service — the established `AlertEndpoints` → repo pattern, but with a service layer because the scope-derivation + event-emission logic is shared across the four route variants.

```csharp
public sealed class LegalHoldService
{
    public LegalHoldService(
        ControlPlaneDbContext db,
        IEventRepository events,
        ITammaModeProvider mode,
        TimeProvider clock,
        ILogger<LegalHoldService> logger) { /* ... */ }

    public Task<LegalHold> PlaceAsync(PlaceHoldCommand cmd, CancellationToken ct);
    public Task<LegalHold> ReleaseAsync(Guid id, Guid? releasedBy, /* scope */ Guid? tenantId, bool platform, CancellationToken ct);
    public Task<IReadOnlyList<LegalHold>> ListAsync(LegalHoldListFilter filter, CancellationToken ct);
}
```

### Event types — `Tamma.Api/Services/Audit/LegalHoldEventTypes.cs` (NEW)

```csharp
public static class LegalHoldEventTypes
{
    public const string Placed   = "AUDIT.LEGAL_HOLD.PLACED";
    public const string Released = "AUDIT.LEGAL_HOLD.RELEASED";
}

public static class AuditDeletionEventTypes
{
    public const string RetentionBlockedByHold = "AUDIT.RETENTION.BLOCKED_BY_HOLD";
    public const string ErasureBlockedByHold   = "AUDIT.ERASURE.BLOCKED_BY_HOLD";
}
```

### Endpoints — `Tamma.Api/Endpoints/OrgEndpoints.cs` + `AdminEndpoints.cs` (MODIFY)

Tenant-scope handlers added to `OrgEndpoints` (alongside the existing `ListTenantAudit`), platform-scope handlers added to `AdminEndpoints`. Both mirror `AlertEndpoints`' tenant/admin split and its `RequireTenantAdmin`-style inline role gate.

```
# tenant scope (RequireTenantMembershipFilter; place/release require owner in SaaS)
GET    /api/v1/orgs/{tenantId}/audit/legal-holds
POST   /api/v1/orgs/{tenantId}/audit/legal-holds
DELETE /api/v1/orgs/{tenantId}/audit/legal-holds/{id}

# platform scope (PlatformOwnerAccess)
GET    /api/v1/admin/audit/legal-holds
POST   /api/v1/admin/audit/legal-holds
DELETE /api/v1/admin/audit/legal-holds/{id}
```

Route registration in `Program.cs`: tenant routes on the `orgs` group with `RequireTenantMembershipFilter` (matching the existing `/{tenantId}/alerts` block); admin routes with `.RequireAuthorization("PlatformOwnerAccess")` (matching the platform-admin block).

### Enforcement hooks (NEW call sites in sibling stories)

This story owns the guard + event types + the integration tests that prove blocking; the **call sites** are added into the 37-5 pruner and the 37-8 erasure flow as part of this story's diff (they are NEW because those stories are siblings in the same epic):

- **37-5 pruner** — before deleting an audit batch, call `ILegalHoldGuard.EvaluateAsync` per candidate; skip held rows, append `AUDIT.RETENTION.BLOCKED_BY_HOLD` once per affected hold with the skipped count, continue with unheld rows.
- **37-8 erasure** — before erasing a subject's records, evaluate the hold guard; if held, append `AUDIT.ERASURE.BLOCKED_BY_HOLD` and reject the whole erasure request (fail closed — no partial delete).

If 37-5 / 37-8 land after this story, the hooks land here as guarded no-ops against the (yet-absent) delete loop and are wired fully when those stories merge; the guard + events + registry are complete and independently testable regardless of merge order.

## Dependencies

- **Hard prerequisite — Story 37-1** (audit read-model + `IEventRepository` audit-event path). The `legal_holds` registry and the PLACED/RELEASED events build on the read-model 37-1 establishes.
- **Consumer — Story 37-5 (Retention)**: the pruner MUST call `ILegalHoldGuard` before deleting; emits `AUDIT.RETENTION.BLOCKED_BY_HOLD`. This story adds that call site.
- **Consumer — Story 37-8 (Right-to-Erasure)**: erasure MUST call `ILegalHoldGuard` before deleting; emits `AUDIT.ERASURE.BLOCKED_BY_HOLD`. This story adds that call site.
- **Reuses (no change)**: `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`), `RequireTenantMembershipFilter` + `TenantRoleHierarchy` (`Authorization/`), `PlatformOwnerAccess` policy (`Program.cs`), `IEventRepository` (`Tamma.Data/Repositories/`).

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/` (xUnit; docker-bound suites run via `sg docker -c "dotnet test ..."`). TDD — write tests first.

1. **Guard unit tests** (`LegalHoldGuardTests.cs`):
   - No active hold → `IsHeld=false`.
   - Tenant-scope hold matches same-tenant candidate; does NOT match other-tenant candidate.
   - Platform-wide hold matches every tenant + system (tenant_id null) candidate.
   - Scope matching: actor-only, subject-only, time-range (inclusive bounds, before/after misses), case-ref, and combined-predicate (all must match); NULL scope field = wildcard.
   - **Overlapping holds**: two active holds cover one record; releasing one → still `IsHeld=true`; releasing both → `IsHeld=false`.
   - Released hold never matches.
   - **Fail-closed**: guard query failure → decision treated as held (no delete).

2. **Hold-blocks-prune integration** (`LegalHoldRetentionTests.cs`): seed audit rows past the retention window; place a hold covering a subset; run the 37-5 pruner; assert in-scope rows survive, out-of-scope rows are deleted, and exactly one `AUDIT.RETENTION.BLOCKED_BY_HOLD` event per affected hold.

3. **Hold-blocks-erasure integration** (`LegalHoldErasureTests.cs`): place a hold covering a data subject; run the 37-8 erasure for that subject; assert no records are deleted (fail closed), the request is rejected, and one `AUDIT.ERASURE.BLOCKED_BY_HOLD` event is emitted.

4. **Release re-enables** (both integration files): release the covering hold; re-run prune and erasure; assert the now-unheld records are deleted and the standard 37-5/37-8 success events fire (no `BLOCKED_BY_HOLD`).

5. **Lifecycle audit** (`LegalHoldServiceTests.cs`): place emits exactly one `AUDIT.LEGAL_HOLD.PLACED`; release emits exactly one `AUDIT.LEGAL_HOLD.RELEASED`; releasing an already-released hold → 409 and NO second event.

6. **RBAC + per-mode matrix** (`LegalHoldEndpointsTests.cs`):
   - single-user mode: sole user can place/release/list; rows keyed `user_id`.
   - SaaS tenant scope: `tenant_owner` can place/release; `member` → 403 on place/release, read-only list allowed; cross-tenant `id` → 404; body `tenantId` mismatch → 400.
   - SaaS platform scope: `PlatformOwnerAccess` required; a tenant member hitting `/api/v1/admin/audit/legal-holds` → 403; platform-wide list never leaks onto the tenant surface.

7. **CHECK constraint** (`LegalHoldServiceTests.cs`): inserting a row violating the principal-XOR (e.g. both `tenant_id` and `user_id` set) is rejected by Postgres; invalid `status` / `scope_category` rejected.

8. **Migration discipline**: `has-pending-model-changes` reports none after `AddLegalHolds`; migration applies and rolls back cleanly; full suite stays green.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/LegalHold.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (add `LegalHold` entity config — CHECK + indexes) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<LegalHold>`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddLegalHolds.cs` | Create (additive EF migration) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/ILegalHoldGuard.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/LegalHoldGuard.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/LegalHoldService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/LegalHoldEventTypes.cs` | Create (`AUDIT.LEGAL_HOLD.*`, `AUDIT.*.BLOCKED_BY_HOLD`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (tenant legal-hold handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (platform legal-hold handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (route registration; DI for service + guard) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAuditRecordRepository.cs` | Modify (37-1 read-model: add hold-aware delete-candidate query if 37-1 exposes the prune surface here) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/LegalHoldGuardTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/LegalHoldServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/LegalHoldEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/LegalHoldRetentionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/LegalHoldErasureTests.cs` | Create |

> `IAuditRecordRepository.cs` is a **37-1 artifact** — verify it exists at implementation time. If 37-1 names the prune surface elsewhere, attach the hold-aware candidate query to whatever 37-1 actually exposes; do NOT invent a second read-model.

## Dev Notes

### Development Process Reminder

Before implementing this story:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md).
2. Search `.dev/` for related spikes/bugs/findings/decisions.
3. Confirm Story 37-1 has landed and note the exact name/shape of its audit read-model + prune surface (`IAuditRecordRepository` is the spec's assumption — verify before wiring).
4. Confirm whether 37-5 / 37-8 have landed; if so, add the guard call sites into their real delete loops; if not, land the hooks as guarded no-ops with the integration tests pending those loops.
5. Plan the TDD cycle (Red-Green-Refactor) — guard + service tests first.

### Fail-Closed Is Load-Bearing

The single most important invariant: **a hold-guard failure must never let a deletion through.** If the registry query throws, the candidate is treated as held. A destructive compliance operation failing safe (refusing to delete) is always preferable to deleting a record that a court ordered preserved. This is the opposite of the alert pipeline's `TryEmitAsync` swallow-and-continue posture — do not copy that pattern into the guard.

### Per-Mode Scope Derivation

Derive ownership from `ITammaModeProvider.Mode` + the route, not from request body fields:
- single-user mode → `user_id` = caller, `tenant_id` NULL.
- SaaS tenant route → `tenant_id` = path tenant (forced), `user_id` NULL.
- SaaS admin route → `tenant_id` = body target tenant OR `is_platform_wide = true`; `user_id` NULL.

Getting this wrong leaks a tenant's hold onto the platform surface or vice-versa — pin it in `LegalHoldEndpointsTests`.

### Overlapping Holds = Additive, Re-Evaluated Per Operation

Never cache a "not held" decision across a hold placement. The pruner/erasure call `EvaluateAsync` per operation so a hold placed mid-run still protects records the run hasn't reached. Releasing one of several overlapping holds must not unfreeze a still-covered record — the guard returns `IsHeld=true` while any active hold matches.

### Release Is a Status Flip, Not a Delete

`legal_holds` rows are append-then-flip. A released hold stays in the table (status `released`) forever so "who placed/released this hold and when" is permanently auditable. There is no hard-delete endpoint.

### Time-Range Bounds

`scope_time_from`/`scope_time_to` are inclusive; a NULL bound is open-ended (NULL `from` = "since the beginning", NULL `to` = "until forever"). Store and compare in UTC (`DateTime` UTC, consistent with `DomainEvent.CreatedAt`).

## Logging Requirements

- **INFO**: hold placed (`holdId`, `scopeCategory`, `caseRef`, `tenantId`/`isPlatformWide`, `placedBy`), hold released (`holdId`, `releasedBy`), prune blocked by hold (`holdId`, skipped count), erasure blocked by hold (`holdId`, `subjectId`).
- **DEBUG**: guard evaluation (candidate scope, matched hold ids, decision), list-holds query (filter, row count).
- **WARN**: release attempt on an already-released hold; member-role place/release attempt rejected (403); cross-tenant hold access attempt rejected (404).
- **ERROR**: guard query failure (then **treated as held** — log loudly because deletions are now being suppressed and the operator must know the registry is unreachable), migration/CHECK violation on insert.
- **Structured context**: include `{ holdId, tenantId, scopeCategory, caseRef, decision, mode }` where applicable.
- **Credential / PII safety**: `case_ref`, `reason`, `scope_subject_id` may reference sensitive matters — log identifiers, never free-text `reason` at INFO+; redact `scope_subject_id` if it can be a natural-person identifier.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
