# Story 37-9: Consent & Data-Processing Logging — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Story:** [docs/stories/epic-37/story-37-9/37-9-consent-and-data-processing-logging.md](../../stories/epic-37/story-37-9/37-9-consent-and-data-processing-logging.md)

**Goal:** Record and serve a tamper-evident, append-only log of consent and data-processing
decisions (TOS/DPA acceptance, BYOK vs platform data handling, telemetry opt-in/out, AI-training-
data usage) plus a Records-of-Processing-Activities (ROPA) registry. Each consent change captures
who/what/when/version, emits a sensitive `CONSENT.*` audit event (hash-chained via 37-2, ingested
by 37-1), and is queryable as current effective state + immutable history. Per-mode owned; RBAC.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/` (docker-bound suites run via
`sg docker -c "dotnet test ..."`; build needs no wrapper).

---

## Non-goals (YAGNI guard)

- NO per-user override layer in SaaS. Consent is tenant-owned (tenant_owner/tenant_admin), mirroring
  the prompt-store decision in CLAUDE.md. Members read; they do not write.
- NO in-place mutation of consent rows. Append-only is the invariant — a withdrawal is a NEW row.
  No update/delete method exists on the service or repository.
- NO DB-level immutability triggers. The service/repository API surface is the enforcement boundary
  (same as the DCB event store, which is append-only by API, not by trigger).
- NO consent-collection UI. This story is the API + storage + audit substrate. Dashboard surfacing
  is a separate follow-up (out of scope, like the alert-pipeline dashboard split).
- NO change to resolution semantics elsewhere. `IConsentGate` is a read-only hard-error gate; it
  never auto-grants and never silently degrades (`feedback_resolution_no_empty_fallback`).
- NO new audit substrate. `CONSENT.*` events flow through Story 37-1's `IAuditTrail` (or, until that
  merges, `IEventRepository.AppendAsync` tagged `sensitive`). This story does not build its own.
- NO TS-side code. `packages/api` is DELETED. All targets are the C# `apps/tamma-elsa` solution.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists (reuse it)

| Concern | Verified anchor |
|---|---|
| Subject identity | `src/Tamma.Data/Entities/User.cs` — `Id`, `TenantId`, per-tenant `Role`, `PlatformRole` |
| Audit event row | `src/Tamma.Data/Entities/DomainEvent.cs` — `Type`, `TenantId`, `Tags`, `Data`, `SequenceNumber` (BIGSERIAL total-order tiebreak) |
| Audit write seam | `src/Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync`, `ListByTenantAsync`, `QueryWithPaginationAsync` |
| Dual-scoping precedent | `src/Tamma.Data/Entities/PromptOverride.cs` + `TammaModelConfiguration.cs` ~714: `ck_prompt_overrides_principal_xor`, `.AreNullsDistinct(false)` unique index, `gen_random_uuid()` default |
| Mode | `src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser \| SaaS), process-stable |
| Tenant RBAC | `src/Tamma.Api/Authorization/TenantRoleHierarchy.cs` (`Owner`/`Admin`/`Member`, `IsAtLeast`) + `RequireTenantMembershipFilter` (path-tenant gate sets `HttpContext.Items[TenantRoleItemKey]`) |
| Org endpoints (target) | `src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `ListTenantAudit` (line ~527) is the read precedent; role check via `RoleAtLeast`/`TenantRoleHierarchy.IsAtLeast(..., Admin)` |
| Org route mapping | `src/Tamma.Api/Program.cs` ~1550: `orgs.MapGet("/{tenantId:guid}/audit", OrgEndpoints.ListTenantAudit).AddEndpointFilter<RequireTenantMembershipFilter>()` |
| Platform-owner gate | `OwnerAccess` policy (used by `/api/v1/admin/*` endpoints) |
| Seed-defaults precedent | `src/Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs` + `ConventionStoreSeeder` (insert-missing-only, never reverts edits) |
| Migrations | `src/Tamma.Data/Migrations/ControlPlane/` (current baseline `20260609205701_InitialControlPlane`) |
| Entity-config single source | `src/Tamma.Data/TammaModelConfiguration.cs` (1302 lines; all `HasCheckConstraint` live here) |
| Test layout | `tests/Tamma.Api.Tests/` (per-area folders; `Orgs/TenantAuditEndpointTests.cs` is the endpoint-test precedent — direct-handler invocation + `ApiTestFixture.ResetDatabaseAsync`) |

### What does NOT exist yet (NEW)

- **No consent or ROPA implementation anywhere** (grep for `consent`/`processing.activit`/`ropa`
  returns only unrelated substring hits, e.g. "operator consent via env" in `AgentResolverService`).
- **No `audit_records` table, no hash-chain, no `IAuditTrail`** — those are delivered by Stories
  37-1 and 37-2, which are NOT yet written (their `docs/stories/epic-37/story-37-1|2/` folders are
  empty). **Decision:** depend on 37-1's `IAuditTrail` if merged; otherwise fall back to
  `IEventRepository.AppendAsync` with a `sensitive: "true"` tag so 37-1 can backfill `audit_records`
  from the DCB stream. Confirm 37-1/37-2 state as task 0.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Owns a consent record | sole user (`user_id`, `tenant_id` NULL) | tenant (`tenant_id`, `user_id` NULL) |
| May grant/withdraw | the user (no RBAC) | `tenant_owner`/`tenant_admin`; `member` → 403 |
| May read effective + history | the user | any member of own tenant; cross-tenant rejected |
| Owns ROPA registry | system defaults + user's view | system defaults (platform owner CRUD); members read-only |
| Mode source | `ITammaModeProvider` | same |

---

## Architecture

**record → emit audit event → store append-only → resolve effective → gate**, reusing the audit and
RBAC substrate end-to-end:

1. **`ConsentRecord` + `ProcessingActivity` entities** (CP DB). Consent is append-only, XOR-scoped
   (`user_id`/`tenant_id`), with a `BIGSERIAL SequenceNumber` total-order tiebreak mirroring
   `DomainEvent`. ROPA is system-scoped, seeded insert-missing-only, unique on `ActivityKey`.
2. **`ConsentTypeCatalog`** (code) — canonical types + active versions. A version bump forces
   re-consent via read-time staleness, never a delete.
3. **`ConsentService`** — `RecordAsync` (append + emit `CONSENT.GRANTED`/`CONSENT.WITHDRAWN`
   atomically), `GetEffectiveAsync` (latest per `(scope, type)` + `granted`/`stale` flags),
   `GetHistoryAsync` (immutable timeline). Scope derived from `ITammaModeProvider` + caller.
4. **`CONSENT.*` audit events** (AGGREGATE.ACTION.STATUS) through 37-1's `IAuditTrail` (fallback
   `IEventRepository`), flagged sensitive, hash-chained by 37-2. Row id stored on
   `consent_records.source_event_id`.
5. **`IConsentGate.RequireAsync`** — read-only hard-error gate for consent-dependent paths; wired
   at one demonstrative call site.
6. **Endpoints** — tenant-scoped `POST/GET /api/v1/orgs/{tenantId}/consent` + ROPA read on
   `OrgEndpoints` (the spec target); single-user self-service `POST/GET /api/v1/consent`;
   platform-owner ROPA CRUD under `/api/v1/admin/processing-activities` (`OwnerAccess`).

---

## Task breakdown

### T0: Confirm audit-substrate state (deps 37-1 / 37-2)

**Scope:** Determine whether `audit_records` + `IAuditTrail` (37-1) and the hash-chain (37-2) are
merged. This decides the `ConsentService` emit path.

- [ ] `grep -rl "IAuditTrail\|audit_records\|AuditRecord" apps/tamma-elsa/src` — if present, target
      `IAuditTrail`; if absent, target `IEventRepository.AppendAsync` + `sensitive:"true"` tag.
- [ ] Note the chosen seam in the `ConsentService` doc-comment so a later 37-1 merge is a one-line
      swap.

**Acceptance:** the emit path is decided and documented; no guessing at a non-existent API.

### T1: `ConsentRecord` + `ProcessingActivity` entities, model config, migration (core)

**Scope:** New entities, DbSets, `TammaModelConfiguration` blocks, additive EF migration. No service
wiring yet.

**Files:**
- New: `src/Tamma.Data/Entities/ConsentRecord.cs`, `src/Tamma.Data/Entities/ProcessingActivity.cs`.
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` (add `DbSet<ConsentRecord>`,
  `DbSet<ProcessingActivity>`); `src/Tamma.Data/TenantDbContext.cs` (ignore/scope per the
  established dual-context pattern — follow how `PromptOverride` is treated in each context).
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` — mirror the PromptOverride block:
  - ConsentRecord: `Id` default `gen_random_uuid()`; `ck_consent_records_principal_xor`
    (exactly-one of `UserId`/`TenantId`); index `(UserId, TenantId, ConsentType, SequenceNumber DESC)`
    (NON-unique — history); `SequenceNumber` as `BIGSERIAL`/identity (mirror `DomainEvent`); apply
    `ApplyTenantFilter` like PromptOverride for defence-in-depth.
  - ProcessingActivity: `Id` default `gen_random_uuid()`; unique on `ActivityKey`;
    `DataCategories`/`Recipients` as `text[]`; `CreatedAt`/`UpdatedAt` default `now()`.
- New: `src/Tamma.Data/Migrations/ControlPlane/*_AddConsentAndProcessingActivities.cs` via
  `dotnet ef migrations add AddConsentAndProcessingActivities` (run from the Data project).

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ConsentEntityTests.cs` — XOR CHECK rejects
both-null and both-set; a `(scope, type)` accepts multiple rows (no unique violation); `BIGSERIAL`
assigns strictly increasing `SequenceNumber`; ROPA `ActivityKey` uniqueness enforced.

**Acceptance criteria:**
- [ ] Migration applies + rolls back cleanly; `dotnet ef migrations has-pending-model-changes`
      reports none afterwards.
- [ ] Entity config lives ONLY in `TammaModelConfiguration.cs`.
- [ ] Full suite stays green.

### T2: `ConsentType` catalog + `ConsentEventTypes` + repository (append + query only)

**Scope:** Code-shipped consent-type catalog with active versions; event-type constants; the
append-only repository surface.

**Files:**
- New: `src/Tamma.Api/Services/Compliance/ConsentType.cs` (constants) + `ConsentTypeCatalog.cs`
  (active versions: `terms_of_service`, `data_processing_agreement`, `byok_data_handling`,
  `telemetry_opt_in`, `ai_training_data_usage`).
- New: `src/Tamma.Api/Services/Compliance/ConsentEventTypes.cs` (`CONSENT.GRANTED`,
  `CONSENT.WITHDRAWN`).
- New: `src/Tamma.Data/Repositories/IConsentRepository.cs` + `ConsentRepository.cs` — methods:
  `AppendAsync(ConsentRecord)`, `GetLatestPerTypeAsync(scope)`, `GetHistoryAsync(scope, type?)`.
  **No update/delete method** — append + query only.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ConsentAppendOnlyTests.cs` — assert the
`IConsentRepository` surface exposes only append + query (no Update/Delete/Remove member);
catalog returns the expected active version per type; missing type lookup is handled.

**Acceptance criteria:**
- [ ] `IConsentRepository` has no mutation-other-than-append method (reflection assertion).
- [ ] Catalog is the single source of active versions.

### T3: `ConsentService` — record (atomic emit) + effective + history

**Scope:** The write/read service. `RecordAsync` emits the `CONSENT.*` audit event and inserts the
row atomically (a failed emit aborts the insert); `GetEffectiveAsync` computes `granted`/`stale`;
`GetHistoryAsync` returns the immutable timeline. Scope from `ITammaModeProvider` + caller.

**Files:**
- New: `src/Tamma.Api/Services/Compliance/ConsentService.cs`, `ConsentScope.cs` (record:
  `UserId?`/`TenantId?` + factory from mode+caller).

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ConsentServiceTests.cs`:
- grant → one row + `CONSENT.GRANTED` (tags `consentType`/`version`/`scope`/`tenantId`/`actorUserId`/
  `mode`) + `source_event_id` round-trips.
- withdraw → second row (`granted=false`) + `CONSENT.WITHDRAWN`; grant row untouched.
- grant→withdraw→re-grant → 3 ordered rows; effective = latest.
- staleness: bump `ConsentTypeCatalog` active version above consented → effective `stale=true`.
- type with no record → `granted=false, stale=false, consentedVersion=null`.
- emit failure → record NOT inserted (atomicity) + ERROR logged.

**Acceptance criteria:**
- [ ] Effective resolution orders by `(OccurredAt, SequenceNumber)` and returns one entry per known
      type.
- [ ] Withdrawal never edits the prior grant row (append-only).
- [ ] `source_event_id` references the emitted audit/DCB row.

### T4: `IConsentGate` + one demonstrative call site

**Scope:** Read-only hard-error gate; wire at one consent-dependent site (telemetry emit OR
AI-training-data path).

**Files:**
- New: `src/Tamma.Api/Services/Compliance/IConsentGate.cs` + `ConsentGate.cs`.
- Modify: one demonstrative call site (choose telemetry emit or AI-training-data usage) to call
  `RequireAsync` before the gated action.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ConsentGateTests.cs` — throws
`CONSENT.REQUIRED.MISSING` (severity High) on absent/withdrawn/stale; passes on a fresh active-
version grant; never grants; never silently passes.

**Acceptance criteria:**
- [ ] Gate is read-only and hard-errors (no empty/plain fallback).
- [ ] The demonstrative site is gated and tested.

### T5: ROPA seed + admin CRUD + tenant read

**Scope:** System-default processing activities seeded insert-missing-only; platform-owner CRUD;
tenant read-only surface.

**Files:**
- New: `src/Tamma.Api/Services/Compliance/ProcessingActivitySeedSpecs.cs` + seeder (mirror
  `ConventionSeedSpecs`/`ConventionStoreSeeder`: insert-missing-only, never reverts edits).
- New: `src/Tamma.Api/Endpoints/Admin/AdminProcessingActivityEndpoints.cs`
  (`GET/POST/PATCH /api/v1/admin/processing-activities`, `OwnerAccess`).

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ProcessingActivityTests.cs` — seed adds
defaults once (re-run adds nothing, never reverts an edited row); admin CRUD gated by `OwnerAccess`
(non-owner 403); tenant read returns active activities.

**Acceptance criteria:**
- [ ] Seeder is insert-missing-only.
- [ ] Admin CRUD is platform-owner-only; tenant read is members-only.

### T6: Endpoints + route mapping + DI wiring

**Scope:** Consent + ROPA-read endpoints on `OrgEndpoints`; single-user self-service variant;
route mapping + DI.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `RecordConsent` (admin+ via
  `TenantRoleHierarchy.IsAtLeast(..., Admin)`; member → 403, mirroring `ListTenantAudit`'s role
  gate), `GetConsent` (any member; `?history=true`), `ListProcessingActivities` (any member).
- New: single-user `POST/GET /api/v1/consent` handlers (no path-tenant; principal = the user).
- New: `src/Tamma.Api/Dtos/Orgs/ConsentDtos.cs` (request/response shapes).
- New: `src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs` (DI: repository,
  service, gate, seeder — mirror `AlertServiceCollectionExtensions`).
- Modify: `src/Tamma.Api/Program.cs` — map org consent routes alongside the `/{tenantId:guid}/audit`
  block (~1550), each attaching `RequireTenantMembershipFilter`; map single-user + admin routes;
  call the compliance extension.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ConsentEndpointsTests.cs` (direct-handler,
`ApiTestFixture.ResetDatabaseAsync` precedent from `TenantAuditEndpointTests`):
- single-user: user read+write.
- SaaS: `tenant_owner`/`tenant_admin` write; `member` → 403 on POST; member read OK.
- cross-tenant path rejected by the membership filter.
- effective vs `?history=true` shapes; ROPA read.

**Acceptance criteria:**
- [ ] Endpoint shape identical between modes; auth middleware decides the principal key
      (prompt-store API precedent).
- [ ] RBAC matrix green incl. SaaS member 403 + cross-tenant rejection.

### T7 (verification): full suite + migration round-trip

- [ ] `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"` (or the Compliance filter for the inner
      loop) green.
- [ ] Migration apply + rollback clean; `has-pending-model-changes` → none.
- [ ] No new analyzer/lint warnings introduced.

---

## Task order & dependencies

T0 → T1 → T2 → T3 → (T4 ∥ T5) → T6 → T7.
T1 is the hard prerequisite for everything. T4 (gate) and T5 (ROPA) are parallel-safe after T3/T1.
T6 needs T3+T5.

## Risks

- **Audit-substrate timing (T0):** 37-1/37-2 may not be merged. Mitigation: the
  `IEventRepository.AppendAsync` + `sensitive:"true"` fallback keeps this story shippable; a later
  37-1 merge is a one-line emit-seam swap. Pin the chosen seam in a doc-comment.
- **Atomicity of emit + insert (T3):** the audit event and the consent row must both persist or
  neither. Use a single DB transaction (or emit-then-insert with the insert as the commit point and
  the event id captured first). A consent row without its audit event breaks the evidentiary trail.
- **Append-only erosion:** a future "edit consent" convenience method would silently destroy the
  compliance invariant. The repository-surface assertion test (T2) is the guardrail — keep it.
- **Scope-derivation correctness:** writing a `user_id`-scoped row in SaaS (or vice versa) corrupts
  ownership. Derive scope ONLY from `ITammaModeProvider` + caller, never from request body; the XOR
  CHECK is the backstop and the RBAC tests pin the matrix.
- **Migration discipline:** `consent_records` / `processing_activities` are additive (no baseline
  CHECK edit), so a normal `dotnet ef migrations add` applies — but still verify
  `has-pending-model-changes` and keep all entity config in `TammaModelConfiguration.cs`.
- **Event-store topology shift (Story 28-1 / Epic 30):** `CONSENT.*` events append to the CP store
  the evaluator polls today. System/tenant routing must follow whatever 37-1 does; keep the emit
  going through the shared seam so a per-tenant fan-out migration touches one place.
