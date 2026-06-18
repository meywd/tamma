# Story 37-3: Audit Query, Search & Filter API — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Replace the thin type-prefix tenant audit read (`OrgEndpoints.ListTenantAudit` over raw
`domain_events`) with a rich, filterable, **keyset-paginated** query API over the curated audit
read-model from Story 37-1 — per-tenant `audit_records` and control-plane `platform_records`.
Filter by `category`, `action`, `actorUserId`, `targetType`/`targetId`, `severity`, `outcome`,
`ipAddress`, and a `[from, to)` time range; search (`q`) over actor/target/payload; paginate by
`source_sequence_number` (stable under concurrent inserts). Enforce per-mode RBAC (SaaS
`tenant_admin+` for own-tenant tenant audit; `PlatformOwnerAccess` for platform audit; single-user
sole user sees everything) with cross-tenant non-leakage as defence-in-depth. Every read emits an
`AUDIT.QUERIED` meta-audit event. Re-targets the stale Epic 23-10 / 23-4 specs onto the C# stack.

**Story:** [37-3-audit-query-search-and-filter-api.md](../../stories/epic-37/story-37-3/37-3-audit-query-search-and-filter-api.md)

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (`Tamma.Api` endpoints + query
service; `Tamma.Data` repositories + indexes + migrations). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."` — the session docker group is stale, see
`reference_dotnet_test_docker`). The build itself needs no `sg` wrapper.

---

## Non-goals (YAGNI guard)

- **NO target in `packages/api`.** It is DELETED. All artifacts land in the C# `apps/tamma-elsa`
  solution. Do not create or cite a TypeScript audit module.
- **NO write/projection changes.** The curated `audit_records` / `platform_records` tables, their
  columns, and the projector that populates `source_sequence_number` etc. are owned by Story 37-1.
  37-3 is read-only over them. If a needed column is missing, surface it as a 37-1 gap — do not
  bolt projection logic onto the query path.
- **NO new RBAC primitives.** Reuse `RequireTenantMembershipFilter`, `TenantRoleHierarchy`,
  `PlatformOwnerAccess`, `ITenantContext`, and the per-tenant global query filter verbatim.
- **NO offset pagination as the new contract.** Keyset on `source_sequence_number` only. `offset`
  is accepted-but-ignored-with-WARN for one release for backward compat with `AuditLogTab`.
- **NO `pg_trgm` GIN index in this story.** B-tree composites satisfy the structured-filter p95;
  trigram payload search is a documented follow-up.
- **NO dashboard work.** The admin/tenant audit dashboard consuming these endpoints is separate
  Epic 37 scope. This story keeps the existing `AuditLogTab` working via the compat shim only.
- **NO meta-audit recursion.** `AUDIT.QUERIED` is a DCB event, not an `audit_records` row.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Site | State today |
|---|---|
| `src/Tamma.Api/Endpoints/OrgEndpoints.cs:527` `ListTenantAudit` | Thin read: `IEventRepository.ListByTenantAsync(tenantId, type, limit, offset)` over `domain_events`. Enforces `RequireTenantMembershipFilter` role item + `TenantRoleHierarchy.IsAtLeast(role, Admin)` (line 539); pins `ITenantContext.SetTenantId` for the global-query-filter wall; offset paging clamped `[1,200]`. Projects `AuditEventResponse`. **This is the handler 37-3 rewrites.** |
| `src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:71` `AuditEventResponse` | `(Id, Type, CreatedAt, Tags, Data)` — Tags/Data as raw JSON strings. The new `AuditRecordResponse` follows this raw-JSON-payload precedent. |
| `src/Tamma.Api/Program.cs:1512` `orgs` group | `MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")`; `1550: orgs.MapGet("/{tenantId:guid}/audit", OrgEndpoints.ListTenantAudit)`. Existing route is reused (richer handler). |
| `src/Tamma.Api/Program.cs:1442` `v1Admin` group | `MapGroup("/api/v1/admin").RequireAuthorization("AdminAccess")`; per-route `PlatformOwnerAccess` (alerts/alert-rules pattern, lines 1443+). **The new platform audit route follows this convention** → `/api/v1/admin/audit` (spec said `/api/admin/audit`; live convention is `/api/v1/admin/*` — story documents the choice). |
| `src/Tamma.Api/Program.cs:986` `PlatformOwnerAccess` policy | `PlatformPermissionRequirement("platform_admin")` — the platform gate. |
| `src/Tamma.Data/Repositories/IEventRepository.cs:46` `ListByTenantAsync` + `PlatformEventRepository.QueryAsync` | The current event-store reads; **NOT** the curated read-model. 37-1 introduces `audit_records`/`platform_records`. |
| `src/Tamma.Data/TenantDbContext.cs`, `TammaModelConfiguration.cs` | Per-tenant global query filter lives here; `audit_records` mapping is owned by 37-1 (37-3 adds query indexes via migration). |
| `packages/dashboard/src/pages/admin/AuditLogTab.tsx` | Existing consumer of `?type=&limit=&offset=` — must not break (compat shim). |

**Verified-absent (all NEW in this work):** `audit_records`, `platform_records`,
`source_sequence_number`/`SourceSequenceNumber` (grep: zero hits), any `AUDIT.*` event type,
`AuditQueryService`, `IAuditRecordRepository`, `/api/v1/admin/audit` route. The first two +
`source_sequence_number` are introduced by **Story 37-1** (its `story-37-1/` dir is currently empty
— 37-1 is an un-authored hard dependency; confirm its entity/column names before coding 37-3).

---

## Architecture

**Parse → query (keyset) → project → meta-audit**, two physically separate scopes:

1. **`AuditQueryFilter`** (`Services/Audit/`) — immutable record + `TryParse(IQueryCollection)`
   that validates enums (`severity`/`outcome`/`category`/`action`), enforces `from < to`, clamps
   `limit` to `[1,200]`, and decodes the opaque base64url `cursor` (a single `long`
   `source_sequence_number`). Parse failure → `400` with a descriptive message (never a silent
   empty list).
2. **`AuditQueryService` / `IAuditQueryService`** — builds the EF `IQueryable`, applies each filter
   conditionally (AND-combined), applies `q` via parameterized `EF.Functions.ILike` over
   `ActorLabel` / `TargetId` / `PayloadText`, applies the keyset predicate
   (`SourceSequenceNumber < cursor`), orders `DESC` (unique monotonic key = its own tiebreak),
   `Take(limit + 1)` to compute `nextCursor`, and returns an estimated `total`.
3. **Repositories** — `AuditRecordRepository` (TenantDbContext-bound, global-query-filter aware)
   and `PlatformAuditRecordRepository` (ControlPlaneDbContext-bound). Created here or extended from
   37-1's seam.
4. **Endpoints** — `OrgEndpoints.ListTenantAudit` rewritten to delegate to the service (keeping the
   exact RBAC gate it already has); new `AdminEndpoints.ListPlatformAudit` gated `PlatformOwnerAccess`.
5. **Meta-audit** — `AUDIT.QUERIED` appended best-effort after a successful read (tenant store for
   tenant scope, CP store for platform scope); tags `tenantId`/`actorUserId`/`scope`/`mode`, data
   = applied filter set + result count (NOT rows). 403s emit nothing.
6. **Indexes + migrations** — composite B-trees on `audit_records` (Tenant migration) and
   `platform_records` (ControlPlane migration) to hit p95 < 300ms over 1M rows.

### Per-mode ownership (the two-scoping-model answer)

| | single-user | SaaS |
|---|---|---|
| Tenant audit read | sole user (owns everything) | `tenant_owner`/`tenant_admin`, **own tenant only**; `member` → 403; cross-tenant → 403 |
| Platform audit read | sole user (operator) | `PlatformOwnerAccess` only; tenants never see it |
| Cross-tenant wall | n/a | `ITenantContext.SetTenantId` + global query filter (2nd wall behind explicit `WHERE tenant_id`) |
| Meta-audit fan-out | user feed (`tenantId` null) | tenant scope → `tenantId` set; platform scope → `tenantId` null |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) | same |

---

## Task breakdown

### T1: `AuditQueryFilter` — parse, validate, cursor codec (pure, no DB)

**Scope:** The typed filter record + `TryParse` + base64url cursor encode/decode. Pure unit-testable.

**Files:**
- New: `src/Tamma.Api/Services/Audit/AuditQueryFilter.cs` (record + `TryParse` returning
  `(AuditQueryFilter?, string? error)`; `ToAuditableShape()` for the meta-audit data; static
  `EncodeCursor(long)` / `TryDecodeCursor(string)`).
- New: `src/Tamma.Api/Services/Audit/AuditQueryEventTypes.cs` (`public const string Queried = "AUDIT.QUERIED";`).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditQueryFilterTests.cs` — valid combos parse;
invalid `severity`/`outcome`/`category`/`action` → error; `from > to` → error; `limit` clamps to
`[1,200]` (0→1, 999→200, non-int→default 50); cursor round-trips (`EncodeCursor` ∘ `TryDecodeCursor`
== identity); garbage cursor → error; whitespace `q` normalized to null.

**Acceptance:**
- [ ] Parsing is total: every bad input yields a descriptive error string, never an exception.
- [ ] Cursor is opaque (base64url) and round-trips a `long` exactly.
- [ ] `ToAuditableShape()` excludes nothing sensitive beyond raw search term handling per logging rules.

### T2: Confirm/define the 37-1 read-model seam + repositories

**Scope:** Pin the `audit_records` / `platform_records` entity + column names from Story 37-1
(assume `SourceSequenceNumber:long`, `ActionCategory`, `ActionCode`, `ActorUserId:Guid`,
`ActorLabel`, `TargetType`, `TargetId`, `Severity`, `Outcome`, `IpAddress`, `OccurredAt:DateTime`,
`PayloadText`/`Payload`, `TenantId` on tenant rows). Create the keyset/filter repositories.

**Files:**
- New or extend: `src/Tamma.Data/Repositories/IAuditRecordRepository.cs` +
  `AuditRecordRepository.cs` (TenantDbContext-bound; exposes
  `Task<(IReadOnlyList<AuditRecord> Rows, long? NextCursor, int Total)> QueryAsync(Guid tenantId, AuditQuerySpec spec, CancellationToken)` —
  spec is the DB-facing shape derived from `AuditQueryFilter`).
- New: `src/Tamma.Data/Repositories/IPlatformAuditRecordRepository.cs` +
  `PlatformAuditRecordRepository.cs` (ControlPlaneDbContext-bound; no `tenant_id` predicate).

> **Blocking check:** if Story 37-1 already ships `IAuditRecordRepository`, extend it rather than
> duplicate. If 37-1 has not landed, this task is gated — either land 37-1 first or stub the entity
> with a CLEARLY-marked `// OWNED BY 37-1` note and coordinate.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditRecordRepositoryTests.cs` (Testcontainers
Postgres) — seed rows, assert `QueryAsync` keyset seek returns correct ordered subset; assert the
TenantDbContext global query filter blocks foreign-tenant rows even with a tampered explicit
predicate.

**Acceptance:**
- [ ] Repos read ONLY their own table (tenant repo never touches `platform_records`, vice-versa).
- [ ] Keyset seek uses `WHERE source_sequence_number < cursor ORDER BY source_sequence_number DESC`.

### T3: Composite indexes + migrations (p95 < 300ms)

**Scope:** Add the supporting indexes via additive EF migrations.

**Files:**
- New Tenant migration `src/Tamma.Data/Migrations/Tenant/*_AuditRecordsQueryIndexes.cs`:
  `(tenant_id, action_category, occurred_at DESC)`, `(tenant_id, actor_user_id, occurred_at DESC)`,
  `(tenant_id, source_sequence_number DESC)`.
- New ControlPlane migration `src/Tamma.Data/Migrations/ControlPlane/*_PlatformRecordsQueryIndexes.cs`:
  `(action_category, occurred_at DESC)`, `(source_sequence_number DESC)`.
- Mirror index config in `TammaModelConfiguration.cs` (single source of truth) if 37-1 maps these
  entities there.

**Tests / verification:** after generating each migration, run `dotnet ef migrations has-pending-model-changes`
→ must report none; apply + roll back cleanly on a Testcontainers DB. The p95 perf assertion lives
in T7.

**Acceptance:**
- [ ] `has-pending-model-changes` reports none after both migrations.
- [ ] Index names match the story; migrations apply and roll back on a clean DB.

### T4: `AuditQueryService` — the query orchestrator

**Scope:** `IAuditQueryService` with `QueryTenantAsync(tenantId, filter, ct)` and
`QueryPlatformAsync(filter, ct)`. Pins `ITenantContext.SetTenantId(tenantId)` for the tenant path
(2nd-wall), delegates to the repo, projects `AuditRecordResponse`, computes `nextCursor`, returns
an estimated `total` (planner estimate or capped exact count), and emits `AUDIT.QUERIED`
best-effort after materialization.

**Files:**
- New: `src/Tamma.Api/Services/Audit/IAuditQueryService.cs`, `AuditQueryService.cs`.
- New: `src/Tamma.Api/Dtos/Audit/AuditRecordResponse.cs` (AC-11 shape; payload as raw JSON string),
  `AuditQueryResponse.cs` (`{ records, nextCursor, total }`).
- DI: register in `Program.cs` (scoped), alongside the repos.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditQueryServiceTests.cs` — each filter dimension
isolates the right subset; AND-combination narrows; `q` matches `ActorLabel`/`TargetId`/`PayloadText`
case-insensitively; injection-shaped `q` returns only legit matches (parameterization);
`nextCursor` null on last page, set otherwise; `total` returned; `AUDIT.QUERIED` appended once on
success with filter set + count in `Data`; append-failure logs WARN and does NOT fail the read;
tenant path pins ambient context.

**Acceptance:**
- [ ] All AC-3 filters + AC-4 search behave per spec; `q` is parameterized.
- [ ] Meta-audit emitted exactly once per successful read; never recurses; never fails the read.
- [ ] `total` is an estimate (documented), not a blocking full COUNT over millions of rows.

### T5: Endpoints — tenant rewrite + platform new

**Scope:** Rewrite `OrgEndpoints.ListTenantAudit` to parse `AuditQueryFilter` and delegate to
`QueryTenantAsync`, **keeping the existing RBAC gate verbatim** (membership role item +
`TenantRoleHierarchy.IsAtLeast(role, Admin)`); keep `type`/`offset` accepted-but-shimmed for compat.
Add `AdminEndpoints.ListPlatformAudit` → `QueryPlatformAsync`, wired on `v1Admin` with
`PlatformOwnerAccess`.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/OrgEndpoints.cs` (`ListTenantAudit`).
- Modify: `src/Tamma.Api/Endpoints/AdminEndpoints.cs` (add `ListPlatformAudit`).
- Modify: `src/Tamma.Api/Program.cs` (`v1Admin.MapGet("/audit", AdminEndpoints.ListPlatformAudit).RequireAuthorization("PlatformOwnerAccess");`).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditEndpointsTests.cs` — invalid filter → 400;
valid filter → 200 with `{records, nextCursor, total}`; legacy `?type=&offset=` still returns rows
(compat shim, WARN on `offset`); platform route present and shaped identically.

**Acceptance:**
- [ ] Tenant route keeps its existing membership + admin gate (byte-for-byte RBAC behaviour where
      a `member` still gets 403).
- [ ] Platform route is `PlatformOwnerAccess`-gated and reads `platform_records` only.
- [ ] Backward-compat: existing `AuditLogTab` request shape still returns rows.

### T6: RBAC matrix + cross-tenant non-leakage (integration)

**Scope:** The full security matrix and the isolation wall, end-to-end through the HTTP pipeline.

**Files:**
- New: `tests/Tamma.Api.Tests/Audit/AuditRbacTests.cs`,
  `tests/Tamma.Api.Tests/Audit/AuditCrossTenantTests.cs`,
  `tests/Tamma.Api.Tests/Audit/AuditMetaAuditTests.cs`.

**Tests:**
- RBAC per mode (single-user, SaaS): member→403, tenant_admin own→200, tenant_admin foreign→403,
  non-platform-owner on `/api/v1/admin/audit`→403, platform owner→200, single-user→200 on both.
- Cross-tenant: tenant-A admin with a predicate tampered to tenant B (ambient pinned to A) → zero
  B rows (global query filter). Tenant query never returns `platform_records` rows; platform query
  never returns tenant rows.
- Meta-audit: success → one `AUDIT.QUERIED` (`scope=tenant`/`platform`, correct `tenantId`); 403 →
  none.

**Acceptance:**
- [ ] Entire AC-7 / AC-8 / AC-10 matrix is green.
- [ ] A deliberately-broken explicit `WHERE tenant_id` predicate still leaks nothing (2nd wall proven).

### T7: Keyset correctness + performance (integration)

**Scope:** Page-boundary correctness, concurrent-insert stability, and the p95 budget.

**Files:**
- New: `tests/Tamma.Api.Tests/Audit/AuditKeysetPaginationTests.cs`,
  `tests/Tamma.Api.Tests/Audit/AuditPerformanceTests.cs` (gated/`[Trait("perf")]` so it's
  opt-in locally but runnable in CI).

**Tests:**
- 250 rows @ `limit=100` → 3 pages, no overlap/gap, `nextCursor` null on page 3.
- Concurrent-insert stability: snapshot page-1 cursor, append 10 newer rows, fetch page 2 → exactly
  the originally-expected rows (boundary unmoved).
- `from > to` → 400; undecodable cursor → 400.
- Perf: ~1M `audit_records` for one tenant → representative filtered + keyset query p95 < 300ms
  with indexes; regression canary: drop `(tenant_id, source_sequence_number DESC)` → query degrades
  sharply (asserts the index is load-bearing).

**Acceptance:**
- [ ] Keyset is provably stable under concurrent inserts.
- [ ] p95 < 300ms with the composite indexes over 1M rows.

### T8: Full-suite green + verification

**Scope:** Run the whole C# suite, confirm no regressions, confirm migrations clean.

**Steps:**
- [ ] `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"` (or the project-scoped equivalent) green.
- [ ] `dotnet ef migrations has-pending-model-changes` → none for both contexts.
- [ ] Verify `AuditLogTab` request shape still returns rows (compat shim) — manual or e2e.

**Acceptance:**
- [ ] Full suite green; no pending model changes; no regression in existing org/admin tests.

---

## Task order & dependencies

T1 (pure) → T2 (repos, **gated on 37-1 seam**) → T3 (indexes) → T4 (service) → T5 (endpoints) →
T6 + T7 (parallel-safe integration suites) → T8 (verify). T1 and the 37-1 seam confirmation can
proceed immediately; T4 needs T1+T2; T5 needs T4; T6/T7 need T5.

## Risks

- **Story 37-1 not landed / column names differ.** Hardest dependency. T2 is gated on confirming
  the exact `audit_records`/`platform_records` schema and the `source_sequence_number` column
  name/type. Mitigation: confirm the seam before T2; if 37-1 lags, do not invent the projection —
  coordinate or stub with `// OWNED BY 37-1` markers.
- **Keyset assumes `source_sequence_number` is unique + monotonic.** If 37-1's sequence is
  per-tenant-reset or non-unique, the single-key keyset breaks (needs a `(occurred_at, id)`
  composite tiebreak). Mitigation: assert uniqueness/monotonicity in T2; fall back to a composite
  keyset only if 37-1's sequence is not globally unique.
- **`total` cost.** A naive `COUNT(*)` over filtered millions blows p95. Mitigation: estimate
  (planner) or cap; T4 must not gate the page on an exact count. Document "~N"/"N+" semantics.
- **`q` payload search at scale.** `ILIKE '%...%'` is a scan; fine within narrowed filters,
  expensive for `q`-only. Mitigation: documented `pg_trgm` follow-up; keep it out of the p95 AC.
- **Cross-tenant regression.** The explicit `WHERE tenant_id` could drift. Mitigation: the ambient
  `ITenantContext` + global query filter is the load-bearing 2nd wall; T6 asserts a tampered
  predicate still leaks nothing — do NOT remove the global filter "because the explicit one exists".
- **Meta-audit recursion / failure coupling.** `AUDIT.QUERIED` must be a DCB event (not an
  `audit_records` row) and append best-effort. Mitigation: T4 asserts append-failure does not fail
  the read and that the event never re-enters this query surface.
- **Route convention drift.** Spec said `/api/admin/audit`; live convention is `/api/v1/admin/*`
  (`v1Admin` group, `PlatformOwnerAccess` per-route, alerts precedent). Story documents the choice;
  add a `/api/admin/audit` alias only if Epic 37 lead requires parity.
- **Migration discipline.** Indexes are additive, but still run `has-pending-model-changes` (none)
  after each migration and mirror config in `TammaModelConfiguration.cs` if 37-1 maps the entities
  there (single source of truth).
