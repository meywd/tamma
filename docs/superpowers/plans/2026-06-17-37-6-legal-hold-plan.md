# Story 37-6 — Legal Hold (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Ship a **legal-hold** capability that freezes audit records (and optionally related
per-tenant data) matching a hold scope (category / actor / subject / time-range / case-ref) so that
retention pruning (Story 37-5) and right-to-erasure (Story 37-8) cannot delete them while a hold is
active. Holds are owned per mode with elevated RBAC; placing/releasing a hold is itself a sensitive,
immutable, audited action. The single enforcement seam is `ILegalHoldGuard`, which 37-5 and 37-8 must
consult before any delete.

**Story file:** `docs/stories/epic-37/story-37-6/37-6-legal-hold.md`
**Spec:** `/tmp/pab_stories/37-6.json` (boundaryNote: empty).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."` — session docker group is stale, build needs no wrapper).

**Target architecture (verified 2026-06-17, repo @ main 98cfb1c2):**
- `Tamma.Data` — audit read-model (Story 37-1), per-tenant `TenantDbContext`, platform
  `ControlPlaneDbContext`. The `legal_holds` registry + `LegalHold` entity live here.
- `Tamma.Api` — HTTP surface (`OrgEndpoints` tenant scope, `AdminEndpoints` platform scope) + hold
  service/guard.
- The legacy TypeScript `packages/api` is **DELETED** — never a target.

---

## Non-goals (YAGNI guard)

- NO new audit read-model. 37-1 owns it; this story attaches a hold-aware candidate query and the
  guard call sites to whatever 37-1 actually exposes. Do not invent a parallel read-model.
- NO hard-delete of holds. Release is a status flip (`active` → `released`); rows persist forever for
  compliance. No purge endpoint.
- NO per-record "hold flag" denormalization. Coverage is computed by the guard against the registry
  at evaluation time — a record is held iff an active hold's scope matches it. This keeps holds
  retroactive (a hold placed today protects records written last year) with zero backfill.
- NO new delivery/notification channel. PLACED/RELEASED/BLOCKED_BY_HOLD are DCB events; if an
  operator wants alerts on them, the existing Story 5.6 alert pipeline picks them up via an
  `alert_rules` row — out of scope here.
- NO change to 37-5/37-8 deletion semantics beyond the guard gate. The guard says held/not-held;
  those stories own everything else about pruning and erasure.

---

## Current-state findings (verified 2026-06-17)

| Seam | Location | Note |
|---|---|---|
| Audit read-model | created in **Story 37-1** | `story-37-5` dir is empty → Epic 37 is being authored fresh; 37-1/37-5/37-8 are siblings. `IAuditRecordRepository.cs` is the spec's assumed name — **verify** at impl time. |
| DCB event append | `Tamma.Data/Repositories/EventRepository.cs` → `IEventRepository.AppendAsync(DomainEvent)` | `DomainEvent { Id, Type, TenantId?, Tags, Metadata, Data, CreatedAt, SequenceNumber }`. `AlertEndpoints` already appends lifecycle events this way. |
| Mode | `Tamma.Api/Services/PromptStore/TammaMode.cs` → `ITammaModeProvider.Mode` (`SingleUser`/`SaaS`) | process-stable singleton. |
| Tenant RBAC | `Authorization/RequireTenantMembershipFilter.cs` (+ `TenantRoleHierarchy.cs`) | filter stashes role in `HttpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`; mutations inline-gate via the `AlertEndpoints.RequireTenantAdmin` pattern. |
| Platform RBAC | `PlatformOwnerAccess` policy (`Program.cs` ~986) | platform-owner only — distinct from `OwnerAccess` (which admits every personal-tenant owner). Admin audit routes MUST use `PlatformOwnerAccess`. |
| Existing tenant-audit endpoint precedent | `OrgEndpoints.ListTenantAudit` (~527, Story 18-7) + `IEventRepository.ListByTenantAsync` | confirms `/api/v1/orgs/{tenantId}/audit*` is the right mount + cross-tenant 404 discipline. |
| Endpoint tenant/admin split + cross-tenant 404 + body-tenant-mismatch 400 | `Endpoints/AlertEndpoints.cs` | THE pattern to copy (`ListTenantAlerts`/`CreateTenantChannel`/`RequireTenantAdmin`). |
| EF model config conventions | `Tamma.Data/TammaModelConfiguration.cs` | `ToTable(... t => t.HasCheckConstraint(...))`, `HasIndex(...).HasFilter(...)`. Migrations under `Migrations/ControlPlane/`. |

**Key gap closed by this story:** there is currently NO legal-hold / retention / erasure code in
`Tamma.Api` or `Tamma.Data` (grep confirms only unrelated "prune" usages in rate-limit/KEK/alert
windows). This story introduces the hold registry + guard + lifecycle, and the enforcement hooks for
the two destructive sibling stories.

---

## Architecture: registry → guard → enforcement

1. **`legal_holds` table** (CP-resident; `LegalHold` entity) — the registry. One of three ownership
   shapes (principal XOR CHECK): tenant-scope (`tenant_id` set), single-user (`user_id` set),
   platform-wide (`is_platform_wide = true`, both null). Scope predicate columns: `scope_category`,
   `scope_actor_id`, `scope_subject_id`, `scope_time_from`, `scope_time_to`, `case_ref`. Lifecycle:
   `status` (`active`/`released`), `placed_by/at`, `released_by/at`. Partial index on
   `(tenant_id, status) WHERE status='active'` for the hot guard query.
2. **`ILegalHoldGuard.EvaluateAsync(LegalHoldQuery)` → `HoldDecision(IsHeld, MatchingHoldIds)`** —
   the single seam. Coverage = ownership match AND every non-null scope predicate matches (NULL =
   wildcard). Overlapping holds additive. **Fail-closed**: query failure ⇒ treated as held.
3. **Lifecycle service** (`LegalHoldService`) — place/release/list with per-mode scope derivation,
   emitting `AUDIT.LEGAL_HOLD.PLACED` / `AUDIT.LEGAL_HOLD.RELEASED` via `IEventRepository`.
4. **Endpoints** — tenant (`/api/v1/orgs/{tenantId}/audit/legal-holds`, owner-only mutate in SaaS,
   member 403) + platform (`/api/v1/admin/audit/legal-holds`, `PlatformOwnerAccess`).
5. **Enforcement hooks** — guard call sites in the 37-5 pruner (skip + `AUDIT.RETENTION.BLOCKED_BY_HOLD`)
   and 37-8 erasure (fail closed + `AUDIT.ERASURE.BLOCKED_BY_HOLD`).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Place/release tenant-scope hold | sole user (no RBAC) | `tenant_owner` (member → 403) |
| Place/release platform-wide hold | sole user | `PlatformOwnerAccess` only |
| Ownership storage | `user_id` set, `tenant_id` NULL | `tenant_id` set (tenant) or both NULL + `is_platform_wide` (platform) |
| List visibility | sole user sees all their holds | tenant sees tenant-scope rows only; platform owner sees all |
| Mode source | `ITammaModeProvider` | same |

---

## Task breakdown

### LH-1: `legal_holds` registry + `LegalHold` entity + migration (core, no behaviour yet)

**Scope:** entity, EF config, DbSet, additive migration. No service/endpoints.

**Files:**
- New: `src/Tamma.Data/Entities/LegalHold.cs` (+ `LegalHoldStatus`, `LegalHoldScopeCategory` consts).
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` — `legal_holds` table: principal-XOR CHECK,
  status CHECK, scope-category CHECK; partial index `(TenantId, Status) WHERE Status='active'`;
  indexes on `CaseRef`, `ScopeSubjectId`.
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` — `DbSet<LegalHold> LegalHolds`.
- New: `src/Tamma.Data/Migrations/ControlPlane/<ts>_AddLegalHolds.cs` (`dotnet ef migrations add AddLegalHolds`).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/LegalHoldEntityTests.cs` — insert tenant/single-user/
platform-wide rows succeed; principal-XOR violation (both tenant_id + user_id) rejected by Postgres;
invalid status / scope_category rejected; `has-pending-model-changes` reports none.

**Acceptance:**
- [ ] Migration applies + rolls back cleanly; snapshot updated; no pending model changes.
- [ ] All three ownership shapes insert; every XOR/CHECK violation is rejected at the DB.

### LH-2: `ILegalHoldGuard` + `LegalHoldGuard` (the enforcement seam)

**Scope:** read-only coverage evaluation against `legal_holds WHERE status='active'`. Fail-closed.

**Files:**
- New: `src/Tamma.Api/Services/Audit/ILegalHoldGuard.cs` (+ `LegalHoldQuery`, `HoldDecision`).
- New: `src/Tamma.Api/Services/Audit/LegalHoldGuard.cs`.
- Wire DI in `Program.cs` (or a small `AddTammaAudit` extension mirroring the alert wiring).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/LegalHoldGuardTests.cs` —
no active hold → not held; tenant-scope matches own tenant, not other tenant; platform-wide matches
all; scope matching (actor / subject / time-range inclusive bounds / case-ref / combined; NULL =
wildcard); released hold never matches; **overlapping**: two holds cover one record, release one →
still held, release both → not held; **fail-closed**: guard query throws → decision = held.

**Acceptance:**
- [ ] `EvaluateAsync` returns `IsHeld=true` + matching ids while any active hold covers the candidate.
- [ ] Overlapping holds additive; releasing one does not unfreeze a still-covered record.
- [ ] Registry-query failure yields a held decision (never delete-open) and logs ERROR.

### LH-3: `LegalHoldService` + lifecycle events

**Scope:** place/release/list; per-mode scope derivation; emit `AUDIT.LEGAL_HOLD.PLACED`/`RELEASED`.

**Files:**
- New: `src/Tamma.Api/Services/Audit/LegalHoldService.cs`, `LegalHoldEventTypes.cs`
  (`AUDIT.LEGAL_HOLD.PLACED`/`RELEASED`, `AUDIT.RETENTION.BLOCKED_BY_HOLD`,
  `AUDIT.ERASURE.BLOCKED_BY_HOLD`), command/filter records.
- Wire DI in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/LegalHoldServiceTests.cs` —
place emits exactly one PLACED event with correct tags; release flips status + stamps
`released_by/at` + emits exactly one RELEASED; release of already-released → conflict + NO second
event; per-mode scope derivation (single-user → user_id; SaaS tenant → tenant_id; SaaS platform →
tenant_id-or-platform-wide); list filters by tenant/status/caseRef.

**Acceptance:**
- [ ] PLACED/RELEASED appended via `IEventRepository` exactly once each; immutable (no update path).
- [ ] Released rows persist (status flip, never hard delete).

### LH-4: Endpoints (tenant + platform) + RBAC

**Scope:** the four route variants, per-mode RBAC, cross-tenant 404, body-tenant-mismatch 400.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `ListTenantLegalHolds`, `PlaceTenantLegalHold`,
  `ReleaseTenantLegalHold` (owner gate via the `AlertEndpoints.RequireTenantAdmin` pattern, tightened
  to owner for place/release per AC3; member → 403).
- Modify: `src/Tamma.Api/Endpoints/AdminEndpoints.cs` — `ListLegalHolds`, `PlaceLegalHold`,
  `ReleaseLegalHold`.
- Modify: `src/Tamma.Api/Program.cs` — tenant routes on the `orgs` group with
  `RequireTenantMembershipFilter` (mirror the `/{tenantId}/alerts` block); admin routes with
  `.RequireAuthorization("PlatformOwnerAccess")`.

```
GET    /api/v1/orgs/{tenantId}/audit/legal-holds          (members; tenant rows only)
POST   /api/v1/orgs/{tenantId}/audit/legal-holds          (tenant_owner; member 403)
DELETE /api/v1/orgs/{tenantId}/audit/legal-holds/{id}     (tenant_owner; cross-tenant 404)
GET    /api/v1/admin/audit/legal-holds                    (PlatformOwnerAccess)
POST   /api/v1/admin/audit/legal-holds                    (PlatformOwnerAccess)
DELETE /api/v1/admin/audit/legal-holds/{id}               (PlatformOwnerAccess)
```

**Tests (first):** `tests/Tamma.Api.Tests/Audit/LegalHoldEndpointsTests.cs` —
single-user: any user place/release/list. SaaS tenant: owner OK; member 403 on place/release,
read-only list OK; cross-tenant id → 404; body `tenantId` ≠ path → 400; tenant list never leaks
platform-wide/other-tenant rows. SaaS platform: `PlatformOwnerAccess` enforced; tenant member on
admin route → 403. Place→list→release→list round-trip reflects status.

**Acceptance:**
- [ ] Endpoint shape identical across modes; auth middleware + mode decide ownership (prompt-store precedent).
- [ ] Tenant surface never exposes platform-wide or other-tenant holds.

### LH-5: Enforcement hooks in 37-5 pruner + 37-8 erasure

**Scope:** add `ILegalHoldGuard` call sites; emit BLOCKED_BY_HOLD events. Depends on 37-1; integrates
with 37-5/37-8 (NEW call sites — sibling stories).

**Files:**
- Modify: the 37-5 pruner — before each delete batch, `EvaluateAsync` per candidate; skip held rows,
  emit `AUDIT.RETENTION.BLOCKED_BY_HOLD` once per affected hold with skipped count, continue with
  unheld rows.
- Modify: the 37-8 erasure flow — before deleting a subject's records, `EvaluateAsync`; if held, emit
  `AUDIT.ERASURE.BLOCKED_BY_HOLD` and reject the whole request (fail closed, no partial delete).
- Modify (if 37-1 exposes the prune surface here): `IAuditRecordRepository.cs` — hold-aware
  candidate query. **Verify the real 37-1 read-model name first.**

> **Merge-order tolerance:** if 37-5/37-8 land after this story, ship the hooks as guarded
> no-ops against the (absent) delete loop + leave the integration tests `[Trait("pending",...)]`;
> wire them fully when those stories merge. The guard + registry + lifecycle are complete and
> independently testable regardless of order.

**Tests (first):**
- `LegalHoldRetentionTests.cs` — seed past-window audit rows; place hold over a subset; run pruner;
  in-scope survive, out-of-scope deleted, exactly one `AUDIT.RETENTION.BLOCKED_BY_HOLD` per hold;
  release → re-run → now deleted, no BLOCKED event.
- `LegalHoldErasureTests.cs` — place hold over a subject; run erasure; nothing deleted, request
  rejected, one `AUDIT.ERASURE.BLOCKED_BY_HOLD`; release → re-run → erased, no BLOCKED event.

**Acceptance:**
- [ ] An active hold blocks BOTH prune and erasure of in-scope records.
- [ ] Releasing the last covering hold re-enables both; standard 37-5/37-8 success events fire.
- [ ] Erasure of a held subject is all-or-nothing (no partial delete).

---

## Task order & dependencies

LH-1 → LH-2 → LH-3 → LH-4 ; LH-5 depends on LH-2 (guard) + Story 37-1 (read-model) + Story 37-5/37-8
(delete loops). LH-1 is the only hard prerequisite for everything else in this story.

External hard prerequisite: **Story 37-1** (audit read-model + audit event path). LH-5 consumers:
**37-5**, **37-8**.

## Risks

- **Fail-open deletion (highest):** a guard bug or swallowed exception that returns "not held" on
  failure would let a court-ordered record be pruned/erased. Mitigation: fail-closed contract in
  LH-2 (query failure ⇒ held), asserted by a dedicated test; ERROR-log when suppressing deletes so
  the unreachable-registry condition is visible.
- **Retroactivity:** holds must protect records written before the hold was placed. Computing
  coverage from the registry per evaluation (no per-record flag, no backfill) handles this — but it
  means the guard runs on the prune/erasure hot path. Mitigation: partial index on active holds;
  holds are few (litigation-scale), candidates are batched.
- **Scope-match correctness:** an over-broad match freezes too much (retention never reclaims space);
  an under-broad match leaks a record past a hold. Mitigation: the LH-2 scope-matrix tests pin every
  predicate (actor/subject/time-range/case-ref/combined/NULL-wildcard) and the inclusive time bounds.
- **Per-mode ownership leak:** wrong scope derivation exposes a tenant hold on the platform surface
  or vice-versa. Mitigation: derive from `ITammaModeProvider` + route, force `tenant_id` server-side
  on the tenant path (body mismatch 400), and pin the matrix in LH-4 tests.
- **Merge order with 37-1/37-5/37-8:** siblings may not all exist when this lands. Mitigation: the
  registry + guard + lifecycle ship + test independently; LH-5 hooks degrade to guarded no-ops with
  pending integration tests until the delete loops exist. Verify the real 37-1 read-model name before
  wiring `IAuditRecordRepository`.
- **Migration discipline:** additive table, but still run `has-pending-model-changes` after
  `AddLegalHolds`, keep entity config solely in `TammaModelConfiguration.cs` (single source), and
  verify clean apply + rollback.
