# Story 37-5 — Audit Retention Policies & Tamper-Aware Pruning (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** Give the curated audit trail configurable, per-mode, per-category **retention windows**
with platform-enforced compliance **minimums**, and a **daily scheduled pruning job** that deletes
expired `audit_records` WITHOUT (a) breaking the 37-2 tamper-evident hash chain, (b) deleting records
under an active 37-6 legal hold, or (c) ever dropping below the platform minimum. Pruning re-anchors
the chain via a signed boundary checkpoint so `AuditChainVerifier` still returns OK afterward, and the
prune itself is an audited sensitive action.

**Seed note (story spec):** `/tmp/pab_stories/37-5.json` — P1, est 4-5 days, epic 37 (Audit,
Compliance & Data Governance). boundaryNote empty. Distinct from the existing Story 28-10
`PurgeStaleAnalyticsActivity` (which only prunes analytics rollups, no chain, no holds) — that code
is the **pattern exemplar**, not the thing extended.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
**`packages/api` is DELETED — never a target.** Tests in `apps/tamma-elsa/tests/` (xUnit;
docker-bound suites run via `sg docker -c "dotnet test ..."`; build needs no wrapper).

---

## Non-goals (YAGNI guard)

- **NO** change to how `audit_records` are created — that's 37-1's projector. This story only
  *deletes* expired rows and writes a re-anchor checkpoint.
- **NO** new hash-chain or checkpoint mechanism — reuse 37-2's `AuditChainCheckpoint` + envelope
  signing key; the prune path just sets `is_prune_boundary = true`.
- **NO** legal-hold UI or hold CRUD — that's 37-6; this story *consults* `ILegalHoldService`.
- **NO** right-to-erasure — that's 37-8 (shares the consult-hold path; do not implement here).
- **NO** "prune by actor/subject" (would punch holes mid-chain). Retention is strictly age-based →
  always a contiguous prefix → trivially re-anchorable. Hole-punching is explicitly out of scope.
- **NO** second scheduler infra — clone the `HourlyAnalyticsRollupScheduler` advisory-lock pattern;
  do not invent a new leader-election primitive.
- **NO** per-user override layer in SaaS (CLAUDE.md prompt-store rule analog): one policy set per
  tenant, configured by tenant_owner/admin.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Dependency artifacts NOT yet present (this story consumes them)

37-1 / 37-2 / 37-6 have not landed. Confirmed absent:

| Expected (from dep) | Path | Status |
|---|---|---|
| `Tamma.Data/Audit/` (AuditProjector) | `apps/tamma-elsa/src/Tamma.Data/Audit/` | **does not exist** (37-1) |
| `Tamma.Core/Audit/` (catalog, verifier) | `apps/tamma-elsa/src/Tamma.Core/Audit/` | **does not exist** (37-1/37-2) |
| `AuditRecord` / `AuditChainCheckpoint` / `LegalHold` entities | `Tamma.Data/Entities/Audit*.cs` | **none** (37-1/37-2/37-6) |
| `Tamma.Api/Services/Audit/` | `apps/tamma-elsa/src/Tamma.Api/Services/Audit/` | **does not exist** (37-2/37-6) |

⇒ Sequence is **37-1 → 37-2 → 37-6 → 37-5**. If implementing 37-5 against in-flight deps, code to
the contracts in those specs (`IAuditRecordRepository`, `AuditChainVerifier.VerifyAsync`,
`AuditChainCheckpoint`, `ILegalHoldService`).

### Reusable patterns confirmed present (the load-bearing exemplars)

| Concern | File | What to copy |
|---|---|---|
| Best-effort retention prune | `src/Tamma.Activities/Analytics/PurgeStaleAnalyticsActivity.cs` (Story 28-10) | `[Activity]` shape; `RunAsync` try/catch (rethrow only `OperationCanceledException` on shutdown); static pure-DI `PruneAsync(...)` entry point; `ComputeCutoff(nowUtc, window)` clamp helper; `ExecuteDeleteAsync`; terminal event emit via `IPlatformEventPublisher`. |
| Daily scheduler + multi-pod safety | `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` | `BackgroundService`; `Options` with `Enabled` gate (tests set false); `FireAtMinute`/poll loop; `IRollupSchedulerLeaderLock` + `pg_try_advisory_lock`; idempotent last-fired tracking; WARN-and-continue. |
| Recurring workflow definition | `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` | `DefinitionId` + `CronExpression` constants; single-activity workflow body. |
| Per-mode `user_id` XOR `tenant_id` entity | `src/Tamma.Data/Entities/PromptOverride.cs` | nullable `UserId`/`TenantId`; XOR + unique-nulls-not-distinct config goes in `TammaModelConfiguration.cs`. |
| Tenant RBAC (owner/admin/member 403) | `src/Tamma.Api/Endpoints/OrgEndpoints.cs` | `RequireTenantMembershipFilter.TenantRoleItemKey`; `TenantRoleHierarchy.IsAtLeast(role, Admin)`; `Results.Json({error}, 403)`; cross-tenant → 404. |
| Platform-owner gate | `src/Tamma.Api/Program.cs` (`PlatformOwnerAccess` / `OwnerAccess` policies, ~966-996) | `.RequireAuthorization("PlatformOwnerAccess")` on admin routes. |
| Tenant-vs-owner write gate analog | `PromptManage` / `ConventionManage` policies (Program.cs ~1012/1024) | the prompt/convention precedent: owner-only `settings:manage` would 403 tenant_admin, so dedicated gates exist — retention writes use the OrgEndpoints `RoleAtLeast(Admin)` inline check (matches member-role-write pattern). |
| Mode source | `src/Tamma.Api/Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`) | process-stable SingleUser/SaaS. |
| Migration split | `src/Tamma.Data/Migrations/{ControlPlane,Tenant}/` | additive new-table migration both contexts; verify `has-pending-model-changes` reports none. |
| Endpoints to extend | `src/Tamma.Api/Endpoints/OrgEndpoints.cs`, `AdminEndpoints.cs` | both exist. |

**Key gap this story closes:** age-based pruning of an append-only hash chain naively breaks
verification (the first survivor's `prev_hash` points at a deleted row). The re-anchor boundary
checkpoint is the novel piece; everything else is composition of existing patterns.

---

## Architecture

**Policy → schedule → prune (hold-aware) → re-anchor → audit**, reusing the analytics-retention and
hash-chain machinery end-to-end:

1. **`AuditRetentionPolicy`** (new entity, `Tamma.Data/Entities/`) — per-mode (`user_id` XOR
   `tenant_id`), per-category `retention_days`. Lives in BOTH `TenantDbContext` (per-tenant) and
   `ControlPlaneDbContext` (platform / single-user). Missing row ⇒ platform default.
2. **`AuditRetentionDefaults`** (new, `Tamma.Core/Audit/`) — static `(MinDays, DefaultDays)` per
   37-1 category; compliance floor 365d for SECRET/RBAC/BYOK/BILLING/AUTH/IMPERSONATION/TENANT.
   The minimum is the hard write-time floor; the default is used when no row exists.
3. **`AuditRetentionService`** (new, `Tamma.Api/Services/Audit/`) — resolve effective policy
   (row → default, clamped to min); upsert with 422-below-floor; platform-default admin path;
   emits `AUDIT.RETENTION.POLICY_CHANGED` (sensitive → projected by 37-1).
4. **`PruneExpiredAuditRecordsActivity`** (new, `Tamma.Activities/Audit/`) — per scope, per
   category: cutoff = now − effectiveDays; candidates = `occurred_at < cutoff`; **subtract active
   legal-hold matches** (`ILegalHoldService`); **write a signed prune-boundary
   `AuditChainCheckpoint`** at the last contiguous pruned sequence; `ExecuteDeleteAsync`; emit
   `AUDIT.RETENTION.PRUNED` (+ `BLOCKED_BY_HOLD` when a hold spared records). Best-effort per scope.
5. **`AuditRetentionWorkflow` + `AuditRetentionScheduler`** (new, `Tamma.ElsaServer/Workflows/`) —
   daily, advisory-lock leader-elected clone of the analytics scheduler; iterates tenant schemas +
   control plane.
6. **Endpoints** — tenant `GET/PUT /api/v1/orgs/{tenantId}/audit/retention`; platform
   `GET/PUT /api/admin/audit/retention`.

### Chain re-anchor (the novel piece)

Pruning removes a **contiguous prefix** of the per-scope chain (age-based ⇒ always oldest first ⇒
never a hole). Before deleting, write/update an `AuditChainCheckpoint { scope, head_sequence =
lastPrunedSeq, head_hash = hash(lastPrunedRecord), is_prune_boundary = true, signed_at, signature }`
(signature via 37-2 envelope key). `AuditChainVerifier.VerifyAsync` (37-2) must, when a record's
`prev_hash` references a missing row, accept a matching prune-boundary checkpoint at that sequence as
a valid anchor. ⇒ surviving range verifies OK. If a held record sits inside the expired range, prune
only up to it this run (preserves the contiguous-prefix invariant); the rest waits for hold release.
**If the boundary checkpoint write fails, ABORT the delete for that scope** (never delete without a
valid re-anchor) → ERROR log.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Owns the retention policy? | sole user (`user_id`, `tenant_id` NULL) | tenant (`tenant_id`, `user_id` NULL) |
| Configures (PUT)? | the user | `tenant_owner`/`tenant_admin`; `member` → 403 |
| Sets platform defaults/minimums? | the user (their instance) | platform owner only (`PlatformOwnerAccess`) |
| Prune scopes over? | the single user/CP chain | each tenant schema chain + the CP platform chain |
| Mode source | `ITammaModeProvider` | same |

---

## Task breakdown

### T1 — `AuditRetentionPolicy` entity + `AuditRetentionDefaults` + migrations

**Scope:** Entity (per-mode XOR), the static minimum/default map, EF config, additive migrations for
both contexts. No service/endpoint/prune wiring yet.

**Files:**
- New: `src/Tamma.Data/Entities/AuditRetentionPolicy.cs` (mirror `PromptOverride` XOR shape).
- New: `src/Tamma.Core/Audit/AuditRetentionDefaults.cs` (`ComplianceFloorDays = 365`; per-category
  `(MinDays, DefaultDays)`; `For(category)`).
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` (principal XOR CHECK, `retention_days > 0`
  CHECK, `UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, category)`).
- Modify: `src/Tamma.Data/TenantDbContext.cs` + `ControlPlaneDbContext.cs` (`DbSet<AuditRetentionPolicy>`).
- New: migrations `src/Tamma.Data/Migrations/Tenant/*_AddAuditRetentionPolicies.cs` and
  `.../ControlPlane/*_AddAuditRetentionPolicies.cs` (`dotnet ef migrations add`).

**Tests first:** `tests/Tamma.Data.Tests/Audit/AuditRetentionPolicyEntityTests.cs` (or
Api.Tests) — XOR CHECK rejects both-set/both-null; unique constraint; `retention_days > 0` CHECK;
`AuditRetentionDefaults` covers every 37-1 `SensitiveActionCatalog` category; every default
`>= minimum`; floor categories `>= 365`.

**Acceptance criteria:**
- [ ] Migration applies + rolls back cleanly on both contexts; `has-pending-model-changes` → none.
- [ ] XOR + unique + `> 0` CHECKs enforced at DB level.
- [ ] `AuditRetentionDefaults` is complete vs the 37-1 catalog and never sets a default below its minimum.

### T2 — `AuditRetentionService` (resolve + upsert + platform defaults)

**Scope:** Effective-policy resolution (row → default, clamp to min), validated upsert (422 below
floor), platform-default read/set, `AUDIT.RETENTION.POLICY_CHANGED` emission. Mode-aware principal
selection via `ITammaModeProvider`.

**Files:** new `src/Tamma.Api/Services/Audit/IAuditRetentionService.cs` +
`AuditRetentionService.cs`; new `src/Tamma.Activities/Audit/AuditRetentionEventTypes.cs`
(`AUDIT.RETENTION.PRUNED|BLOCKED_BY_HOLD|FAILED|POLICY_CHANGED`); register in `Program.cs`.

**Tests first:** `tests/Tamma.Api.Tests/Audit/AuditRetentionServiceTests.cs` — resolution order
(row beats default), clamp to min for a stale sub-min row, upsert below floor throws
`TammaError("AUDIT.RETENTION.BELOW_MINIMUM")` carrying category/requested/min, upsert at/above floor
persists + emits POLICY_CHANGED, single-user vs SaaS principal selection, completeness vs catalog.

**Acceptance criteria:**
- [ ] `GetEffectivePolicyAsync` returns per-category `{ effectiveDays, minimumDays, source }`.
- [ ] Upsert below minimum throws the 422-mapped error with an actionable message; no DB write.
- [ ] Lowering retention writes the row only; deletes nothing.

### T3 — `PruneExpiredAuditRecordsActivity` (hold-aware, chain re-anchoring, best-effort)

**Scope:** The core sweep. Per scope, per category: cutoff, candidate select, legal-hold subtraction,
boundary-checkpoint write, `ExecuteDeleteAsync`, event emission. Best-effort isolation copied from
`PurgeStaleAnalyticsActivity`. Static pure-DI `PruneScopeAsync(...)` entry point for an admin
force-prune + tests.

**Files:** new `src/Tamma.Activities/Audit/PruneExpiredAuditRecordsActivity.cs`. Consumes
`IAuditRecordRepository` (37-1), `AuditChainVerifier`/`AuditChainCheckpoint`/envelope key (37-2),
`ILegalHoldService` (37-6).

**Tests first (the load-bearing suite):**
- `tests/Tamma.Activities.Tests/Audit/PruneExpiredAuditRecordsActivityTests.cs` — per-category
  cutoffs honored; only past-cutoff rows deleted; clamp to minimum even with a stale sub-min policy;
  `OperationCanceledException` propagates on shutdown; one scope failing → `AUDIT.RETENTION.FAILED`,
  others still run.
- `.../PruneRespectsLegalHoldTests.cs` — active hold spares in-scope expired records,
  `AUDIT.RETENTION.BLOCKED_BY_HOLD` emitted with hold id + spared count; release → next run deletes.
- `.../PruneChainReanchorTests.cs` — **after a prefix prune + boundary checkpoint,
  `AuditChainVerifier.VerifyAsync` returns OK**; negative control: prune WITHOUT the boundary
  checkpoint → verifier reports a broken link; held-record-in-the-middle → only contiguous prefix
  pruned, chain still verifies; boundary-checkpoint write failure → delete ABORTED for that scope.
- `.../AuditRetentionEventsTests.cs` — `AUDIT.RETENTION.PRUNED` carries
  `{ scope, category, deleted, retainedByHold, cutoff, boundarySeq }`.

**Acceptance criteria:**
- [ ] Prune never deletes below the platform minimum or an active hold.
- [ ] Post-prune `AuditChainVerifier.VerifyAsync(scope, ...)` = OK (re-anchor works); no-checkpoint negative control fails.
- [ ] Per-scope failure isolated; `OperationCanceledException` not swallowed.
- [ ] `AUDIT.RETENTION.PRUNED` emitted with correct per-category counts; projected into `audit_records` by 37-1.

### T4 — `AuditRetentionWorkflow` + `AuditRetentionScheduler` (daily, advisory-locked)

**Scope:** Clone the analytics scheduler+workflow pair for a daily audit prune across all scopes
(tenant schemas via the tenant registry + control plane).

**Files:** new `src/Tamma.ElsaServer/Workflows/AuditRetentionWorkflow.cs` (DefinitionId +
`CronExpression` e.g. `0 30 3 * * *`) and `AuditRetentionScheduler.cs` (`BackgroundService` with
`AuditRetention` options section: `Enabled`, `FireAtHourUtc`, `PollInterval`; reuse
`IRollupSchedulerLeaderLock` or a parallel `pg_try_advisory_lock` lease); wire in
`Tamma.ElsaServer/Program.cs`.

**Tests first:** `tests/Tamma.Activities.Tests/Audit/AuditRetentionSchedulerTests.cs` (mirror
`HourlyAnalyticsRollupSchedulerTests`) — fires once/day at configured hour; advisory lock skips
non-leaders; `Enabled=false` suppresses the loop; dispatch failure → WARN + continue + next-day
recovery.

**Acceptance criteria:**
- [ ] Daily dispatch; multi-pod safe (one leader/day); `Enabled=false` for non-Elsa/test roots.
- [ ] Iterates every tenant schema + the control plane; a failing scope doesn't abort the rest.

### T5 — Retention endpoints (tenant + platform) + Program wiring

**Scope:** Read/configure surfaces with per-mode RBAC.

```
GET  /api/v1/orgs/{tenantId}/audit/retention   (any member)              — effective policy + minimums
PUT  /api/v1/orgs/{tenantId}/audit/retention   (tenant_owner/admin; 403 member; 422 below floor)
GET  /api/admin/audit/retention                (PlatformOwnerAccess)     — platform defaults + minimums
PUT  /api/admin/audit/retention                (PlatformOwnerAccess; 422 below floor)
```

**Files:** modify `src/Tamma.Api/Endpoints/OrgEndpoints.cs` (tenant pair; reuse
`RequireTenantMembershipFilter` + `RoleAtLeast(Admin)`), `AdminEndpoints.cs` (admin pair), and
`Program.cs` (map + register service/scheduler).

**Tests first:** `tests/Tamma.Api.Tests/Audit/AuditRetentionEndpointsTests.cs` — RBAC matrix
(tenant_owner ✓, tenant_admin ✓, member 403, cross-tenant 404; admin route requires
`PlatformOwnerAccess`); single-user sole user can PUT; PUT below floor → 422 with category/
requested/min; GET returns effective + minimums.

**Acceptance criteria:**
- [ ] Endpoint shape identical between modes; auth middleware/mode picks the principal.
- [ ] Member → 403 on PUT; below-floor → 422; cross-tenant → 404.

---

## Task order & dependencies

T1 → T2 → T3 → T4 (parallel-safe with T5 once T3 lands) → T5. T1 is the only hard intra-story
prerequisite. External hard prerequisites: 37-1, 37-2, 37-6 must be merged (or their contracts
stable) before T3.

## Risks

- **False tamper alarm after prune (the headline risk).** Mitigation: the prune-boundary checkpoint
  + verifier honoring it (AC 9 / T3). The `PruneChainReanchorTests` negative control (prune without
  checkpoint → verifier fails) is the regression guard. If 37-2's `AuditChainVerifier` lands without
  prune-boundary awareness, T3 is blocked until 37-2 is amended — flag early.
- **Hole-punching creep.** A future "prune by subject/actor" would break the contiguous-prefix
  invariant and the O(1) re-anchor. Documented as a non-goal; if it's ever wanted it needs a
  different (per-segment) re-anchor design. Don't let it sneak in via 37-8 erasure reuse.
- **Legal-hold race (flapping).** A hold placed mid-prune must spare records; a hold released between
  candidate-select and delete must not cause an under-retain. Consult `ILegalHoldService` inside the
  same logical pass and re-check immediately before `ExecuteDeleteAsync`; on doubt, retain (next run
  reconsiders). A held record in the middle caps this run at the prefix before it.
- **Deleting without a valid re-anchor.** If the boundary-checkpoint write fails, ABORT the scope's
  delete (ERROR log) — never delete first and re-anchor after.
- **Best-effort contract drift.** Copy `PurgeStaleAnalyticsActivity` exactly: per-scope try/catch,
  WARN + `AUDIT.RETENTION.FAILED`, rethrow only `OperationCanceledException` on shutdown. A retention
  hiccup must never crash the daily workflow or starve other tenants.
- **Migration discipline.** Additive new table both contexts; still verify `has-pending-model-changes`
  → none and mirror entity config only in `TammaModelConfiguration.cs` (the single source).
- **Per-mode regressions.** Pin the SaaS-tenant vs single-user-user principal selection in T2/T5
  tests; the wrong default (ship single-user, assume SaaS works) is the CLAUDE.md anti-pattern.
- **Event-store topology (Story 28-1 / Epic 30).** `AUDIT.RETENTION.*` events follow 37-1's
  projection routing — system/platform-scope prune events stay control-plane-resident; tenant-scope
  events route per-tenant. Emit through the same publisher 37-1/37-2 use so a later per-tenant
  fan-out needs no change here.

## Out of scope / deferred

- Right-to-erasure (37-8) — shares the consult-hold path; not built here.
- Retention/audit dashboard surfaces — separate Epic 37 dashboard story.
- Admin "force prune now" endpoint — the static `PruneScopeAsync` entry point makes it a trivial
  follow-up, but it is not in this story's ACs.
- OpenBao-backed signing key — 37-2 owns the signing key source (envelope key / TenantSecretProtector);
  the OpenBao migration is Story 28-13.
