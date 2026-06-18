# Story 37-5: Audit Retention Policies & Tamper-Aware Pruning

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **compliance owner** (platform owner in SaaS; the sole user in single-user mode),
I want **configurable per-category retention windows for the curated audit trail with a scheduled pruning job that deletes expired records without breaking the tamper-evident hash chain or violating an active legal hold**,
So that **Tamma satisfies SOC2/GDPR data-minimization (don't keep audit records longer than the policy) while keeping the audit chain verifiable and legally-held records untouchable**.

## Priority

P1 — Required for SOC2 retention controls and GDPR data-minimization; depends on the curated audit
projection (37-1) and the hash chain (37-2), and is gated by legal hold (37-6).

## Context & Architecture Boundary

This story targets the **C# port** in `apps/tamma-elsa`. **`packages/api` (the legacy TypeScript
API) is DELETED and is NEVER a target.** All work lands in:

- `Tamma.Data` — the `audit_records` read model + hash chain landed by 37-1/37-2 live in
  `TenantDbContext` (per-tenant schema) and `ControlPlaneDbContext` (platform scope). The new
  retention-policy entity and the pruner live here.
- `Tamma.ElsaServer/Workflows` — a scheduled workflow + a `BackgroundService` scheduler, modeled on
  the existing `HourlyAnalyticsRollupScheduler` / `HourlyAnalyticsRollupWorkflow` pair (Story 28-10).
- `Tamma.Activities` — the prune activity, modeled on the existing
  `Tamma.Activities/Analytics/PurgeStaleAnalyticsActivity.cs` (Story 28-10) but operating on
  `audit_records` with chain re-anchoring and legal-hold consultation.
- `Tamma.Api` — `AuditRetentionService`, plus retention endpoints on `OrgEndpoints.cs` (tenant
  scope) and `AdminEndpoints.cs` (platform scope).

**Distinct from existing analytics retention.** Story 28-10's `PurgeStaleAnalyticsActivity` already
prunes `platform_analytics_hourly` rollups on a fixed 13-month window. That code is the **pattern
exemplar**, NOT the thing being extended — analytics rollups have no hash chain and no legal hold.
Audit pruning is a separate, policy-driven, chain-aware sweep over `audit_records`.

**Dependency state (verified 2026-06-17):** 37-1/37-2/37-6 are not yet landed
(`Tamma.Data/Audit`, `Tamma.Core/Audit`, `Tamma.Api/Services/Audit` do not exist). This story
**consumes** the artifacts those stories create — `AuditRecord`, `AuditChainCheckpoint`,
`AuditChainVerifier`, `AuditProjector`, `IAuditRecordRepository`, and `ILegalHoldService` /
`legal_holds`. Sequence 37-1 → 37-2 → 37-6 → 37-5.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

Mirrors Epic 27 / `PromptOverride` (`user_id` XOR `tenant_id`):

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a retention policy? | The sole user — `user_id` set, `tenant_id` NULL. | The tenant — `tenant_id` set, `user_id` NULL (one policy set per tenant). |
| Who configures it (PUT)? | The user. | `tenant_owner` / `tenant_admin` only; `member` → 403. |
| Who sets **platform** defaults & minimums? | The user (it's their instance). | Platform owner only (`PlatformOwnerAccess` / `OwnerAccess`), via `/api/admin/audit/retention`. |
| What does the pruner scope to? | The user's audit chain (single chain). | Each tenant's chain (per-tenant schema) + the platform chain (control plane). |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

## Acceptance Criteria

1. **Retention-policy entity & table.** An `audit_retention_policies` table is added to
   `TenantDbContext` (per-tenant, `tenant_id`-keyed) AND `ControlPlaneDbContext` (platform /
   single-user, `user_id`-keyed) with an EF migration under `Tamma.Data/Migrations/Tenant` and
   `.../ControlPlane`. Columns: `id`, `user_id` (single-user) / `tenant_id` (SaaS),
   `category` (one of the 37-1 catalog control categories: CONFIG, RBAC, SECRET, BYOK, BILLING,
   EXPORT, AUTH, IMPERSONATION, TENANT, AGENT, PERSONA), `retention_days`, `created_at`,
   `updated_at`, `updated_by`. A `principal_xor` CHECK (`user_id` XOR `tenant_id`, mirroring
   `PromptOverride`) and `UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, category)`. A missing row
   for a category means "use the platform default for that category".

2. **Platform minimums with a compliance floor.** Platform-enforced minimum retention days per
   category are defined in code (a static `AuditRetentionDefaults` map in `Tamma.Core/Audit` or
   `Tamma.Data`), with the compliance-critical categories (SECRET, RBAC, BYOK, BILLING, AUTH,
   IMPERSONATION) floored at **>= 365 days** and informational categories at a lower default
   (e.g. AGENT/CONFIG 90 days). The floor is the hard lower bound a tenant/user policy may not go
   below; the default is the value used when no policy row exists.

3. **Read/configure retention API — tenant scope.**
   `GET /api/v1/orgs/{tenantId}/audit/retention` returns the effective policy per category
   (resolved row → platform default) plus the platform minimum per category, readable by any tenant
   member. `PUT /api/v1/orgs/{tenantId}/audit/retention` upserts per-category `retention_days`,
   gated `tenant_owner`/`tenant_admin` (member → 403). Single-user mode hits the same shape with the
   sole user as principal.

4. **Platform-default API — admin scope.** `GET` / `PUT /api/admin/audit/retention`
   (`PlatformOwnerAccess`) reads and sets the platform default retention per category. Defaults may
   be raised above the compliance floor but never set below it (422 if attempted, AC 8).

5. **Minimum-floor enforcement on write (422, no retroactive delete).** A PUT that sets any
   category below its platform minimum is rejected with **422** and a clear, actionable message
   (`"category SECRET requires >= 365 days; got 90"`). Lowering retention **does not** delete
   anything on save — only the scheduled job (AC 6) prunes, and only on its next run.

6. **Scheduled pruning workflow (Elsa, daily).** An `AuditRetentionWorkflow` + `AuditRetentionScheduler`
   `BackgroundService` (modeled on `HourlyAnalyticsRollupWorkflow` / `HourlyAnalyticsRollupScheduler`,
   incl. the `pg_try_advisory_lock` multi-pod leader election) dispatches a daily prune. For each
   scope (each tenant schema + the control plane), the `PruneExpiredAuditRecordsActivity` deletes
   `audit_records` whose `occurred_at` is older than `now - effective_retention_days(category)`,
   computed per category. Pruning **never** deletes below the platform minimum (AC 2) and **never**
   deletes records under an active legal hold (AC 7).

7. **Legal-hold exclusion (dep 37-6).** Before deleting any record, the pruner consults
   `ILegalHoldService` (37-6). Records matching an active hold scope (category / actor / subject /
   time-range / case_ref) are **excluded from the delete set and retained**, regardless of age. When
   the prune set is reduced by an active hold, the pruner emits `AUDIT.RETENTION.BLOCKED_BY_HOLD`
   (the event 37-6 expects) with the hold id and the count of records spared.

8. **Below-minimum rejection message.** (Companion to AC 5.) The 422 response body names the
   offending category, the requested value, and the enforced minimum, so the caller can correct it
   without guessing.

9. **Chain-preserving prune (re-anchor / boundary checkpoint).** Deleting a contiguous expired
   prefix of a chain would break `prev_hash` linkage for the first surviving record and trip a false
   tamper alarm in `AuditChainVerifier` (37-2). The pruner therefore **seals the pruned range with a
   retained boundary checkpoint**: before deleting, it writes (or updates) an
   `AuditChainCheckpoint` (37-2 entity) at the last-pruned sequence with `head_hash` = the hash of
   the last record being deleted, marked as a **prune boundary**. Verification (37-2) treats records
   at/after a prune-boundary checkpoint as chaining to the checkpoint, not to a now-deleted
   `prev_hash`. After a prune, `AuditChainVerifier.VerifyAsync(scope, from, to)` returns **OK** for
   the surviving range.

10. **Pruning is itself audited.** A successful prune emits `AUDIT.RETENTION.PRUNED` with per-scope,
    per-category counts (`{ scope, category, deleted, retainedByHold, cutoff }`), the new boundary
    checkpoint sequence, and the policy snapshot used. This event is one of the sensitive actions in
    the 37-1 catalog, so it is itself projected into `audit_records` (audit of the audit pruning).

11. **Best-effort, isolated, replayable.** A prune failure for one scope is logged at WARN, emits
    `AUDIT.RETENTION.FAILED`, and **does not** abort the other scopes or fail the workflow (same
    best-effort contract as `PurgeStaleAnalyticsActivity`). `OperationCanceledException` on host
    shutdown propagates cleanly (not swallowed as a failure). A missed daily run self-recovers on
    the next run because pruning is idempotent (deleting an already-deleted range is a no-op).

12. **Tests.** Policy CRUD + per-mode RBAC (owner/admin/member 403, single-user sole user);
    minimum-floor enforcement (422 below floor, accepts at/above); lowering retention does not
    delete on save; prune deletes only past-cutoff records per category; prune respects an active
    legal hold (held records survive, `AUDIT.RETENTION.BLOCKED_BY_HOLD` emitted); **post-prune chain
    verifies OK via `AuditChainVerifier`** (the load-bearing test); `AUDIT.RETENTION.PRUNED`
    emission with correct counts; prune below platform minimum is impossible (clamps).

## Technical Design

### Entity — `AuditRetentionPolicy`

`apps/tamma-elsa/src/Tamma.Data/Entities/AuditRetentionPolicy.cs` (mirrors the `PromptOverride`
per-mode XOR shape):

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-5 — per-category audit retention window. Per-mode owned:
/// single-user keys by UserId (TenantId NULL); SaaS keys by TenantId
/// (UserId NULL) — the same XOR invariant as <see cref="PromptOverride"/>.
/// A missing row for a category means "use the platform default".
/// </summary>
public class AuditRetentionPolicy
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }       // single-user mode
    public Guid? TenantId { get; set; }     // SaaS mode

    /// <summary>37-1 control category (CONFIG, RBAC, SECRET, ...).</summary>
    public string Category { get; set; } = null!;

    /// <summary>Retention window in days. CHECK > 0; enforced >= platform minimum at write time.</summary>
    public int RetentionDays { get; set; }

    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

Model config goes in `Tamma.Data/TammaModelConfiguration.cs` (the single source for entity config in
this codebase): the `principal_xor` CHECK, the `retention_days > 0` CHECK, and
`UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, category)`. EF migrations: additive new table, so a
normal `dotnet ef migrations add AddAuditRetentionPolicies` against both the Tenant and ControlPlane
contexts; verify `has-pending-model-changes` reports none afterward.

### Platform minimums & defaults — `AuditRetentionDefaults`

`apps/tamma-elsa/src/Tamma.Core/Audit/AuditRetentionDefaults.cs` (data, not branches):

```csharp
public static class AuditRetentionDefaults
{
    public const int ComplianceFloorDays = 365;   // SOC2 / GDPR floor

    // Per-category (Minimum, Default). Default >= Minimum always.
    public static readonly IReadOnlyDictionary<string, (int MinDays, int DefaultDays)> ByCategory =
        new Dictionary<string, (int, int)>
        {
            ["SECRET"]        = (365, 730),
            ["RBAC"]          = (365, 730),
            ["BYOK"]          = (365, 730),
            ["BILLING"]       = (365, 730),
            ["AUTH"]          = (365, 365),
            ["IMPERSONATION"] = (365, 730),
            ["TENANT"]        = (365, 730),
            ["EXPORT"]        = (365, 365),
            ["PERSONA"]       = (90,  365),
            ["CONFIG"]        = (90,  365),
            ["AGENT"]         = (30,  90),
        };

    public static (int MinDays, int DefaultDays) For(string category)
        => ByCategory.TryGetValue(category, out var v) ? v : (ComplianceFloorDays, ComplianceFloorDays);
}
```

The exact category list comes from the 37-1 `SensitiveActionCatalog` categories; this map MUST cover
every category the catalog defines (a completeness test pins that).

### Resolution order (effective retention)

For a given `(principal, category)`:
1. Principal's policy row for the category → if present, use `RetentionDays` (already validated
   `>= minimum` at write time).
2. Platform default for the category (`AuditRetentionDefaults.For(category).DefaultDays`).

Never falls below `AuditRetentionDefaults.For(category).MinDays` even if a stale row somehow holds a
smaller value — the pruner clamps `effective = max(resolved, minimum)` as a belt-and-suspenders guard
(AC 6).

### Service — `AuditRetentionService`

`apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditRetentionService.cs`:

- `GetEffectivePolicyAsync(principal)` → per-category `{ category, effectiveDays, minimumDays, source: row|default }`.
- `UpsertAsync(principal, category, retentionDays, updatedBy)` → validates `retentionDays >= minimum`
  (throws a `TammaError("AUDIT.RETENTION.BELOW_MINIMUM", ..., severity: Medium)` that the endpoint
  maps to **422**); upserts the row; emits `AUDIT.RETENTION.POLICY_CHANGED` (sensitive → projected).
- `GetPlatformDefaultsAsync()` / `SetPlatformDefaultAsync(category, days)` — admin path; same floor
  check.

### Activity — `PruneExpiredAuditRecordsActivity`

`apps/tamma-elsa/src/Tamma.Activities/Audit/PruneExpiredAuditRecordsActivity.cs`. Direct analog of
`PurgeStaleAnalyticsActivity` (best-effort, `ExecuteDeleteAsync`, pure-DI `PruneScopeAsync` entry
point callable from an admin force-prune endpoint, `ComputeCutoff`-style helper). Per scope:

```
for each category in catalog:
    effectiveDays = max(resolvedPolicy(category), AuditRetentionDefaults.For(category).MinDays)
    cutoff        = nowUtc.AddDays(-effectiveDays)

    candidates    = audit_records where category = @category and occurred_at < @cutoff   // (scope-filtered)
    held          = candidates intersect activeLegalHoldScopes (via ILegalHoldService)
    toDelete      = candidates except held

    if toDelete is empty: continue

    // CHAIN RE-ANCHOR (AC 9) — before deleting, seal the pruned prefix:
    lastPruned = max(source_sequence_number) in toDelete that is contiguous from the chain head
    write/update AuditChainCheckpoint { scope, head_sequence = lastPruned,
                                        head_hash = record_hash(lastPruned), is_prune_boundary = true,
                                        signed_at, signature }   // signature via 37-2 envelope key

    deleted = ExecuteDeleteAsync(toDelete)
    emit AUDIT.RETENTION.PRUNED { scope, category, deleted, retainedByHold = held.Count, cutoff, boundarySeq = lastPruned }
    if held.Any(): emit AUDIT.RETENTION.BLOCKED_BY_HOLD { scope, category, holdIds, spared = held.Count }
```

> **Chain re-anchor detail (AC 9).** The hash chain is per-scope and ordered by
> `source_sequence_number`. Pruning only ever removes a **contiguous prefix** (oldest records) — you
> never punch a hole in the middle, because retention is age-based and monotone. The boundary
> checkpoint records the hash of the last deleted record so the first **surviving** record's
> `prev_hash` still has a verifiable anchor. `AuditChainVerifier.VerifyAsync` (37-2) must, when it
> encounters a record whose `prev_hash` points at a row that no longer exists, look for a
> prune-boundary checkpoint at that sequence and treat a match as a valid link (no tamper). If a
> record under an active hold prevents a contiguous prefix delete (a held record sits in the
> middle of the expired range), the pruner deletes only up to the first held record this run — the
> rest is reconsidered on the next run after the hold releases. This keeps "contiguous prefix"
> invariant true and the chain always verifiable.

### Workflow + scheduler

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditRetentionWorkflow.cs` — single-step workflow
  running `PruneExpiredAuditRecordsActivity` across all scopes (iterate tenant schemas via the
  tenant registry; control plane as the platform scope). Mirror `HourlyAnalyticsRollupWorkflow`'s
  `DefinitionId` + `CronExpression` shape (daily, e.g. `0 30 3 * * *`).
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditRetentionScheduler.cs` — `BackgroundService`
  copy of `HourlyAnalyticsRollupScheduler`: `Enabled` gate (default true; tests/non-Elsa roots set
  false), `FireAtMinute`/poll, `pg_try_advisory_lock` leader election (reuse the
  `IRollupSchedulerLeaderLock` abstraction or a parallel `IAuditRetentionLeaderLock`), idempotent
  last-fired-day tracking, WARN-and-continue on dispatch failure. Options section
  `AuditRetention` with `Enabled`, `FireAtHourUtc`, `PollInterval`.

### Endpoints

Tenant (`OrgEndpoints.cs`, gated via `RequireTenantMembershipFilter` + `RoleAtLeast(..Admin)` for
writes, the same pattern OrgEndpoints already uses; reads any member):

```
GET /api/v1/orgs/{tenantId}/audit/retention          -> effective per-category policy + minimums (member+)
PUT /api/v1/orgs/{tenantId}/audit/retention          -> upsert (tenant_owner/tenant_admin; member 403); 422 below floor
```

Platform (`AdminEndpoints.cs`, `PlatformOwnerAccess`):

```
GET /api/admin/audit/retention                       -> platform default per-category retention + minimums
PUT /api/admin/audit/retention                        -> set platform defaults (>= floor; 422 otherwise)
```

## Dependencies

- **37-1 (Sensitive-Action Audit Taxonomy & Curated Projection)** — provides `audit_records`,
  `SensitiveActionCatalog` (the category list this story's policies key on), `AuditProjector`,
  `IAuditRecordRepository`. **Hard prerequisite.**
- **37-2 (Tamper-Evident Hash-Chain)** — provides `record_hash`/`prev_hash`,
  `AuditChainCheckpoint`, `AuditChainVerifier`, and the envelope signing key. The prune re-anchor
  (AC 9) writes a prune-boundary checkpoint and relies on the verifier honoring it. **Hard
  prerequisite.**
- **37-6 (Legal Hold)** — provides `legal_holds` + `ILegalHoldService`; the pruner consults active
  holds before deleting (AC 7) and emits the `AUDIT.RETENTION.BLOCKED_BY_HOLD` event 37-6 expects.
  **Hard prerequisite.**
- **Story 28-10 (analytics retention)** — `PurgeStaleAnalyticsActivity` /
  `HourlyAnalyticsRollupScheduler` are the **pattern exemplars** (best-effort prune + advisory-lock
  scheduler). Not modified.
- **Epic 27 / `PromptOverride`** — per-mode `user_id` XOR `tenant_id` ownership pattern.
- **Blocks**: 37-8 (right-to-erasure) shares the "consult legal hold before delete" path and may
  reuse `AuditRetentionDefaults`; the retention dashboard story surfaces these policies.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/`; docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale).

1. **Policy CRUD + RBAC (`tests/Tamma.Api.Tests/Audit/AuditRetentionEndpointsTests.cs`)** — per
   mode: SaaS tenant_owner & tenant_admin can PUT, member → 403, cross-tenant → 404; single-user
   sole user can PUT; admin platform-default route requires `PlatformOwnerAccess`. GET returns
   effective policy + minimums.
2. **Minimum-floor (`AuditRetentionServiceTests.cs`)** — PUT below floor → 422 with category +
   requested + minimum in the message; PUT at floor and above → accepted; defaults completeness:
   every `SensitiveActionCatalog` category has an `AuditRetentionDefaults` entry; lowering retention
   does NOT delete on save (no rows removed by `UpsertAsync`).
3. **Prune correctness (`PruneExpiredAuditRecordsActivityTests.cs`)** — seed records straddling the
   cutoff per category; assert only past-cutoff rows deleted, per-category cutoffs honored, clamp to
   minimum even with a stale sub-minimum row; `OperationCanceledException` propagates;
   per-scope failure isolated (`AUDIT.RETENTION.FAILED`, other scopes still run).
4. **Legal hold (`PruneRespectsLegalHoldTests.cs`)** — active hold covering some expired records →
   those records survive the prune, `AUDIT.RETENTION.BLOCKED_BY_HOLD` emitted with hold id + spared
   count; release the hold → next prune deletes them.
5. **Post-prune chain verifies (LOAD-BEARING, `PruneChainReanchorTests.cs`)** — build a chain via
   the 37-2 projector, prune the expired prefix, write the boundary checkpoint, then
   `AuditChainVerifier.VerifyAsync(scope, from, to)` returns **OK** for the surviving range (no false
   tamper). Negative control: prune WITHOUT writing the boundary checkpoint → verifier reports a
   broken link (proves the re-anchor is what keeps it valid). Held-record-in-the-middle → only the
   contiguous prefix up to it is pruned, chain still verifies.
6. **Event emission (`AuditRetentionEventsTests.cs`)** — successful prune emits
   `AUDIT.RETENTION.PRUNED` with `{ scope, category, deleted, retainedByHold, cutoff, boundarySeq }`;
   `AUDIT.RETENTION.POLICY_CHANGED` on PUT; both projected into `audit_records` by 37-1.
7. **Scheduler (`AuditRetentionSchedulerTests.cs`)** — mirror `HourlyAnalyticsRollupSchedulerTests`:
   fires once per day at the configured hour, advisory-lock leader election skips non-leaders,
   `Enabled=false` suppresses the loop, dispatch failure → WARN + continue.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditRetentionPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditRetentionDefaults.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config: XOR + unique + CHECK) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add `DbSet<AuditRetentionPolicy>`) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<AuditRetentionPolicy>`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*_AddAuditRetentionPolicies.cs` | Create (EF migration) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddAuditRetentionPolicies.cs` | Create (EF migration) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditRetentionService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/IAuditRetentionService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Audit/PruneExpiredAuditRecordsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Audit/AuditRetentionEventTypes.cs` | Create (PRUNED / BLOCKED_BY_HOLD / FAILED / POLICY_CHANGED) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditRetentionWorkflow.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditRetentionScheduler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (tenant retention GET/PUT) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (platform retention GET/PUT) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register service + scheduler + map endpoints) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditRetentionEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditRetentionServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Audit/PruneExpiredAuditRecordsActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Audit/PruneRespectsLegalHoldTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Audit/PruneChainReanchorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Audit/AuditRetentionSchedulerTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (esp. Epic 28-10 retention, Epic 29
   secret cabinet, Epic 27 per-mode ownership).
3. Confirmed 37-1, 37-2, 37-6 have landed (this story consumes `AuditRecord`,
   `AuditChainCheckpoint`, `AuditChainVerifier`, `ILegalHoldService`). If they have not, the
   activity/service interfaces must be coded against the contracts those stories define.
4. Planned a TDD approach — write AC 12 tests first, especially the post-prune chain verification.

### Why a boundary checkpoint instead of recomputing the chain

Re-hashing surviving records to "close the gap" would (a) mutate immutable audit rows, defeating the
tamper-evidence, and (b) be O(chain length) per prune. The boundary checkpoint is O(1): it records
the hash of the last-pruned record and lets the verifier treat that as the anchor for the first
survivor. This is the same envelope-signed checkpoint mechanism 37-2 already uses for periodic chain
anchoring — the prune path reuses it with `is_prune_boundary = true` so verification can distinguish
a routine periodic anchor from a "everything before here was lawfully pruned" anchor.

### Contiguous-prefix invariant

Age-based retention only ever expires the oldest records, so the delete set is always a prefix of the
chain ordered by `source_sequence_number` — never a hole. A legal hold in the middle of the expired
range caps this run's delete at the first held record; the prefix invariant (and thus chain
verifiability) is preserved. Document this clearly so a future "prune by actor/subject" feature
(which would punch holes) is consciously rejected or designed to re-anchor differently.

### Per-mode is non-negotiable

Do not ship the single-user model and assume it works for SaaS (CLAUDE.md universal rule). The
policy entity carries both `user_id` and `tenant_id` with an XOR CHECK; the service selects the
column from `ITammaModeProvider`; the pruner scopes per tenant schema in SaaS and to the sole
user/control-plane in single-user.

### Best-effort, never the point of the run

Copy `PurgeStaleAnalyticsActivity`'s failure contract verbatim: per-scope try/catch, WARN +
`AUDIT.RETENTION.FAILED` event, never rethrow (except `OperationCanceledException` on shutdown).
Retention housekeeping must never crash the scheduled workflow or starve other tenants' prunes.

## Logging Requirements

- **INFO**: prune completed per scope (`scope`, `category`, `deleted`, `retainedByHold`, `cutoff`,
  `boundarySeq`); policy upserted (`principal`, `category`, `retentionDays`); scheduler dispatched
  (day, instance, lockKey).
- **DEBUG**: per-category candidate count and cutoff; effective-policy resolution
  (row vs default); leader-lock acquire/skip.
- **WARN**: prune failed for a scope (`scope`, error) → continues; PUT below minimum (422); flapping
  legal-hold reducing prune sets repeatedly; scheduler dispatch failed (next-run recovery).
- **ERROR**: boundary-checkpoint write failed (prune is aborted for that scope to avoid breaking the
  chain — never delete without a valid re-anchor); chain verification fails immediately after a prune
  (regression guard).
- **Structured context**: `{ scope, tenantId|userId, category, deleted, retainedByHold, cutoff,
  boundarySeq, holdIds }` where applicable.
- **Credential / data safety**: never log audit `payload_json`, secret plaintext, or signing-key
  material; the prune logs counts and cutoffs only, never record bodies.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
