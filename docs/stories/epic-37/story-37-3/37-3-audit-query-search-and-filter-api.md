# Story 37-3: Audit Query, Search & Filter API

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant administrator** (and, separately, a **platform owner**),
I want to query, search, and filter the curated audit trail by actor, action, target, severity, outcome, IP, and time range with stable pagination,
So that I can investigate "who did what, to what, when, and from where" for compliance (SOC2 / GDPR), incident response, and customer support — without ever seeing another tenant's records.

## Priority

P0 — Audit query/search is the primary consumer-facing surface of the Epic 37 audit product; the curated read-model from Story 37-1 is useless without a queryable API on top of it.

## Context & Scope

Story 37-1 introduces a **curated audit read-model**: a per-tenant `audit_records` table (in each tenant's own database via `TenantDbContext`) and a control-plane `platform_records` table (in `ControlPlaneDbContext`) that capture *sensitive* actions (RBAC changes, BYOK/secret access, tenant lifecycle, impersonation, prompt/provider config edits) projected from the Epic 4 DCB event substrate. Each curated row carries normalized columns — `actor_user_id`, `action_category`, `action_code`, `target_type`, `target_id`, `severity`, `outcome`, `ip_address`, `occurred_at`, a JSONB `payload`, and a monotonic `source_sequence_number` (the DCB global sequence the row was projected from) for stable ordering.

This story (37-3) **replaces the thin type-prefix tenant audit read** that exists today —
`OrgEndpoints.ListTenantAudit` at `src/Tamma.Api/Endpoints/OrgEndpoints.cs:527`, which calls `IEventRepository.ListByTenantAsync(tenantId, type, limit, offset)` over the raw `domain_events` table and supports only an exact `type` prefix + offset paging — with a **rich, filterable, keyset-paginated query API** over the curated 37-1 tables. It adds a parallel **platform-scoped** endpoint for the control-plane `platform_records`.

> **Re-targeting note.** This story re-targets the stale Epic 23-10 / 23-4 audit-query specs (which assumed the deleted TypeScript `packages/api`) onto the live C# stack in `apps/tamma-elsa`. The TypeScript `packages/api` is DELETED — it is **not** a target of this story under any circumstance. All work lands in `Tamma.Api` (endpoints, DTOs, query service) and `Tamma.Data` (repositories, indexes).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who can read **tenant** audit (`audit_records`)? | The sole user — it is their instance; the user owns and reads everything. | `tenant_owner` / `tenant_admin` of **that tenant only**. `member` → 403. Cross-tenant → rejected (membership filter + global query filter, defence-in-depth). |
| Who can read **platform** audit (`platform_records`)? | The sole user (they are the operator). | Platform owner ONLY (`PlatformOwnerAccess`). Never exposed to tenants — it would leak cross-tenant + platform internals (impersonation, tenant lifecycle, platform RBAC, platform-level BYOK). |
| Mode source | `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`) — process-stable. | same |

The endpoint shape is **identical between modes**; the auth filter (`RequireTenantMembershipFilter` + `TenantRoleHierarchy.IsAtLeast`) decides what the caller may read, exactly as the prompt-store and current `ListTenantAudit` precedents do.

## Acceptance Criteria

1. **Tenant audit query endpoint.** `GET /api/v1/orgs/{tenantId}/audit` (extending `OrgEndpoints.ListTenantAudit`, now reading the curated `audit_records` table instead of raw `domain_events`) accepts the query parameters `category`, `action`, `actorUserId`, `targetType`, `targetId`, `severity`, `outcome`, `ipAddress`, `from`, `to`, `q` (search), `limit`, and `cursor`. It returns `{ records: AuditRecordResponse[], nextCursor: string | null, total: number }` where `total` is an estimate (see AC 9). Unknown/invalid filter values yield `400` with a descriptive error (not a silent empty list).

2. **Platform audit query endpoint.** `GET /api/v1/admin/audit` (gated by `PlatformOwnerAccess`, matching the established platform-audit route convention used by `/api/v1/admin/alerts`) exposes the **same filter surface** over the control-plane `platform_records` table (impersonation, tenant lifecycle, platform RBAC, platform-level BYOK). Tenant audit and platform audit are physically separate tables — a platform query NEVER reads tenant `audit_records` and vice-versa.

3. **Filter semantics — each dimension independently applied (AND-combined).**
   - `category` / `action` — exact match on `action_category` / `action_code` (enumerated; invalid value → 400).
   - `actorUserId` — exact `Guid` match on `actor_user_id`.
   - `targetType` / `targetId` — exact match on `target_type` / `target_id`.
   - `severity` — exact match on the enumerated severity (`info` / `warning` / `critical`).
   - `outcome` — exact match (`success` / `failure` / `denied`).
   - `ipAddress` — exact match on `ip_address`.
   - `from` / `to` — half-open `occurred_at` range `[from, to)`, ISO-8601 UTC; `from > to` → 400.
   Absent filters impose no constraint; all supplied filters AND-combine.

4. **Search (`q`).** A non-empty `q` performs a case-insensitive contains match across `actor` (display/email captured in the curated row), `target_id`, and the JSONB `payload` rendered as text, AND-combined with the structured filters. `q` is parameterized — no string interpolation into SQL; injection attempts are inert. Empty/whitespace `q` is ignored.

5. **Keyset (cursor) pagination.** Pagination is **keyset by `source_sequence_number`**, NOT offset. Ordering is most-recent-first: `ORDER BY source_sequence_number DESC` (`source_sequence_number` is unique and monotonic, so it is its own deterministic tiebreak — no secondary sort key needed). The opaque `cursor` encodes the last-seen `source_sequence_number`; the next page is `WHERE source_sequence_number < {cursor} ...`. Results are stable under concurrent inserts (a new row appended mid-pagination never shifts, duplicates, or skips a page boundary). `nextCursor` is `null` on the last page.

6. **`limit` clamping.** `limit` defaults to 50 and is clamped to `[1, 200]` (matching the existing `ListTenantAudit` clamp). An out-of-range or non-integer `limit` is clamped, not rejected.

7. **RBAC enforced per mode (the matrix).**
   - SaaS `member` → `403` on `GET /api/v1/orgs/{tenantId}/audit`.
   - SaaS `tenant_admin` / `tenant_owner` → allowed for **their own tenant only**.
   - SaaS non-platform-owner → `403` on `GET /api/v1/admin/audit`.
   - single-user sole user → allowed on both endpoints.
   - Caller requesting a tenant they are not a member of → `403` (via `RequireTenantMembershipFilter`), never `200`.

8. **No cross-tenant leakage (defence-in-depth).** Even with RBAC satisfied, the tenant query reads through `TenantDbContext` with the ambient `ITenantContext` ID set to the path `tenantId`, so the global query filter on `audit_records` is the second wall: a regression in the explicit `WHERE tenant_id = ...` predicate still cannot return another tenant's rows. An integration test asserts that injecting a foreign `tenantId` into the filter (with the ambient context pinned to the caller's tenant) returns zero foreign rows.

9. **Query performance & supporting indexes.** A filtered query over a table seeded with ~1M `audit_records` for one tenant returns **p95 < 300ms**. Supporting composite indexes exist on `audit_records`: `(tenant_id, action_category, occurred_at DESC)`, `(tenant_id, actor_user_id, occurred_at DESC)`, and the pagination index `(tenant_id, source_sequence_number DESC)`; the same pattern (minus `tenant_id` where rows are control-plane-wide) applies to `platform_records`. `total` is an **estimated** count (the bounded planner estimate or a capped exact count) so paging is not gated on a full `COUNT(*)` over millions of rows.

10. **Meta-audit on every read.** Every successful audit read (tenant or platform) itself emits an `AUDIT.QUERIED` DCB event — so *access to the audit log is itself auditable*. Tags: `tenantId` (null for platform reads), `actorUserId`, `scope` (`tenant` | `platform`), `mode`. Data captures the applied filter set (NOT the result rows) and the result count. A read that 403s does NOT emit `AUDIT.QUERIED` (no successful access occurred); RBAC denials are covered by existing auth-failure logging.

11. **Response DTO.** `AuditRecordResponse` projects the curated row: `id`, `actionCategory`, `actionCode`, `actorUserId`, `actorLabel`, `targetType`, `targetId`, `severity`, `outcome`, `ipAddress`, `occurredAt`, `payload` (raw JSON string, as the existing `AuditEventResponse` does for `Tags`/`Data`), and `sourceSequenceNumber`. Platform-internal columns (raw DCB metadata) are NOT exposed.

12. **Backward-compatible deprecation of the thin read.** The legacy `type` + `offset` query parameters on `GET /api/v1/orgs/{tenantId}/audit` continue to function for one release (mapped onto `action` and ignored-with-warning for `offset`) so the existing dashboard `AuditLogTab` does not break; new clients use `cursor`. The deprecation is documented in the Change Log and Dev Notes.

13. **Tests.** Unit + integration tests cover: each filter dimension in isolation and combined; `q` search across all three target fields; keyset pagination correctness across page boundaries (including a concurrent-insert stability test); the full RBAC matrix per mode (member 403, admin own-tenant, cross-tenant 403, platform-owner-only platform endpoint, single-user allowed); cross-tenant non-leakage via the global query filter; `AUDIT.QUERIED` emission on success and non-emission on 403; `400` on invalid filter values and `from > to`; `limit` clamping.

## Technical Design

### Component layout

```
apps/tamma-elsa/src/Tamma.Api/
  Endpoints/
    OrgEndpoints.cs                     # MODIFY: ListTenantAudit → rich query over audit_records
    AdminEndpoints.cs                   # MODIFY: add ListPlatformAudit handler (PlatformOwnerAccess)
  Services/Audit/
    AuditQueryService.cs                # NEW: builds the IQueryable, applies filters/search/keyset
    IAuditQueryService.cs              # NEW
    AuditQueryFilter.cs                # NEW: parsed/validated filter record + cursor codec
    AuditQueryEventTypes.cs           # NEW: "AUDIT.QUERIED" (meta-audit)
  Dtos/Audit/
    AuditRecordResponse.cs             # NEW: curated-row projection (AC 11)
    AuditQueryResponse.cs              # NEW: { records, nextCursor, total }

apps/tamma-elsa/src/Tamma.Data/
  Repositories/
    IAuditRecordRepository.cs          # NEW (or extend 37-1's): keyset query over audit_records
    AuditRecordRepository.cs           # NEW: TenantDbContext-bound, global-query-filter aware
    IPlatformAuditRecordRepository.cs  # NEW: keyset query over platform_records (CP store)
    PlatformAuditRecordRepository.cs   # NEW: ControlPlaneDbContext-bound
  Migrations/Tenant/                    # NEW: composite indexes on audit_records (AC 9)
  Migrations/ControlPlane/             # NEW: composite indexes on platform_records (AC 9)
```

> `audit_records` / `platform_records` entities + DbSets are owned by **Story 37-1**. If 37-1 has not yet defined `IAuditRecordRepository`, this story creates it; if it has a basic read, this story extends it with the keyset/filter query. Confirm the seam at implementation time.

### Filter parsing & validation (`AuditQueryFilter`)

A single immutable record parses raw query strings into typed, validated values up front so handlers stay thin:

```csharp
public sealed record AuditQueryFilter(
    string? Category,
    string? Action,
    Guid? ActorUserId,
    string? TargetType,
    string? TargetId,
    string? Severity,       // validated against {info, warning, critical}
    string? Outcome,        // validated against {success, failure, denied}
    string? IpAddress,
    DateTime? From,         // UTC
    DateTime? To,           // UTC; From < To enforced
    string? Search,         // q
    int Limit,              // clamped [1,200]
    long? Cursor)           // last-seen source_sequence_number
{
    // TryParse(...) returns (filter, error?) — error → 400 in the handler.
}
```

`Severity`/`Outcome`/`Category`/`Action` validate against the enums the 37-1 projector writes; an unknown value returns a `400` error string (AC 3) rather than silently matching nothing.

### Keyset query (the load-bearing part)

```csharp
// AuditQueryService — tenant scope (same shape for platform scope)
public async Task<AuditQueryResult> QueryTenantAsync(
    Guid tenantId, AuditQueryFilter f, CancellationToken ct)
{
    // Ambient context pinned so the global query filter is the 2nd wall (AC 8).
    _tenantContext.SetTenantId(tenantId);

    var q = _db.AuditRecords.AsNoTracking()
        .Where(r => r.TenantId == tenantId);                 // explicit 1st wall

    if (f.Category is not null)   q = q.Where(r => r.ActionCategory == f.Category);
    if (f.Action is not null)     q = q.Where(r => r.ActionCode == f.Action);
    if (f.ActorUserId is not null)q = q.Where(r => r.ActorUserId == f.ActorUserId);
    if (f.TargetType is not null) q = q.Where(r => r.TargetType == f.TargetType);
    if (f.TargetId is not null)   q = q.Where(r => r.TargetId == f.TargetId);
    if (f.Severity is not null)   q = q.Where(r => r.Severity == f.Severity);
    if (f.Outcome is not null)    q = q.Where(r => r.Outcome == f.Outcome);
    if (f.IpAddress is not null)  q = q.Where(r => r.IpAddress == f.IpAddress);
    if (f.From is not null)       q = q.Where(r => r.OccurredAt >= f.From);
    if (f.To is not null)         q = q.Where(r => r.OccurredAt <  f.To);

    if (!string.IsNullOrWhiteSpace(f.Search))
    {
        var term = $"%{f.Search.Trim()}%";
        q = q.Where(r =>
            EF.Functions.ILike(r.ActorLabel, term) ||
            EF.Functions.ILike(r.TargetId,  term) ||
            EF.Functions.ILike(r.PayloadText, term));        // payload::text column or computed
    }

    // Keyset (AC 5): cursor < last-seen sequence, most-recent first.
    if (f.Cursor is not null)
        q = q.Where(r => r.SourceSequenceNumber < f.Cursor);

    var page = await q
        .OrderByDescending(r => r.SourceSequenceNumber)
        .Take(f.Limit + 1)                                    // +1 to compute nextCursor
        .ToListAsync(ct);

    var hasMore = page.Count > f.Limit;
    var rows = hasMore ? page.Take(f.Limit).ToList() : page;
    long? nextCursor = hasMore ? rows[^1].SourceSequenceNumber : null;

    var total = await EstimateCountAsync(q, ct);              // planner estimate / capped (AC 9)
    return new AuditQueryResult(rows, nextCursor, total);
}
```

**Why keyset over offset:** offset pagination over a high-write audit table shifts rows when a new event is appended between page loads (the classic "row appears twice / row skipped" bug). `source_sequence_number` is monotonic and unique, so `WHERE source_sequence_number < cursor ORDER BY ... DESC` is a stable, index-friendly seek that the `(tenant_id, source_sequence_number DESC)` index serves with no sort.

**Cursor encoding:** the opaque `cursor` is the base64url of the `source_sequence_number` (a single `long`). Opaque so clients don't build their own; trivially decodable server-side. An undecodable cursor → 400.

### Indexes (AC 9)

```sql
-- audit_records (per-tenant DB; created via Tenant migration)
CREATE INDEX IF NOT EXISTS ix_audit_records_tenant_category_occurred
  ON audit_records (tenant_id, action_category, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_records_tenant_actor_occurred
  ON audit_records (tenant_id, actor_user_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_records_tenant_seq
  ON audit_records (tenant_id, source_sequence_number DESC);   -- pagination seek

-- platform_records (control-plane DB; created via ControlPlane migration)
CREATE INDEX IF NOT EXISTS ix_platform_records_category_occurred
  ON platform_records (action_category, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_platform_records_seq
  ON platform_records (source_sequence_number DESC);
```

For `q` payload search at scale, a `pg_trgm` GIN index on `payload_text` / `actor_label` is a documented follow-up (Dev Notes) — out of scope for the p95-on-structured-filters AC, which the composite B-tree indexes satisfy.

### Meta-audit emission (AC 10)

After a successful query, emit one `AUDIT.QUERIED` event via `IEventRepository.AppendAsync` (tenant scope; routed to the tenant store) / the platform event repository (platform scope, control-plane store):

```csharp
await _events.AppendAsync(new DomainEvent
{
    Type = AuditQueryEventTypes.Queried,            // "AUDIT.QUERIED"
    Tags = Json(new {
        tenantId,                                    // null for platform reads
        actorUserId,
        scope = "tenant",                            // or "platform"
        mode = _mode.Mode.ToString(),
    }),
    Data = Json(new {
        filters = f.ToAuditableShape(),              // applied filter set, NOT result rows
        resultCount = rows.Count,
    }),
}, ct);
```

Emission is best-effort relative to the response (the read already succeeded); a failure to append `AUDIT.QUERIED` is logged at WARN and does not fail the request. The filter set is captured for forensic value; result rows are NOT duplicated into the meta-audit event (avoids unbounded event payloads and PII fan-out).

### Endpoint wiring

```csharp
// Program.cs — tenant audit stays on the existing orgs group (MemberAccess base;
// handler enforces admin+ and membership), now backed by AuditQueryService.
orgs.MapGet("/{tenantId:guid}/audit", OrgEndpoints.ListTenantAudit);   // existing route, richer handler

// Platform audit — v1Admin group (AdminAccess base), per-route PlatformOwnerAccess,
// matching the alerts/alert-rules platform-admin convention.
v1Admin.MapGet("/audit", AdminEndpoints.ListPlatformAudit)
    .RequireAuthorization("PlatformOwnerAccess");
```

> The spec lists `GET /api/admin/audit`; the live platform-admin convention in `Program.cs` is the `/api/v1/admin/*` group (`v1Admin`) with a `PlatformOwnerAccess` per-route gate (see `/api/v1/admin/alerts`). This story follows the live convention (`/api/v1/admin/audit`); if a `/api/admin/audit` alias is required for parity it is a one-line additional `MapGet`. Confirm with the Epic 37 lead at implementation time.

## Dependencies

- **Hard prerequisite — Story 37-1** (Curated audit read-model): defines the `audit_records` / `platform_records` entities, the projector that populates them (including `source_sequence_number`, `action_category`, `action_code`, `actor_label`, `outcome`, `ip_address`), and possibly a base `IAuditRecordRepository`. 37-3 cannot land without these tables.
- **Related — Story 37-2** (whatever 37-2 contributes to the curated trail, e.g. tamper-evidence / signing or export prerequisites): consumed read-only here; this story does not modify the write/projection path.
- **Epic 28** (RBAC + isolation): `RequireTenantMembershipFilter`, `TenantRoleHierarchy`, the `PlatformOwnerAccess` policy, `ITenantContext`, and the per-tenant global query filter on `TenantDbContext` are all reused as-is for the RBAC matrix (AC 7) and cross-tenant defence-in-depth (AC 8).
- **Blocks** the Epic 37 audit dashboard work (admin + tenant audit dashboards) — they consume these endpoints.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound integration suites run via `sg docker -c "dotnet test ..."`).

1. **Filter-dimension unit tests** (`Audit/AuditQueryServiceTests.cs`): seed a fixed set of `audit_records`; assert each filter (`category`, `action`, `actorUserId`, `targetType`, `targetId`, `severity`, `outcome`, `ipAddress`, `from`/`to`) returns exactly the expected subset; assert AND-combination of two+ filters narrows correctly; assert invalid `severity`/`outcome`/`category` → parse error (→ 400).
2. **Search tests:** `q` matches on `actorLabel`, on `targetId`, and on `payload` text; case-insensitive; a SQL-injection-shaped `q` (`' OR 1=1 --`) returns only legitimately-matching rows (proves parameterization).
3. **Keyset-pagination correctness** (`Audit/AuditKeysetPaginationTests.cs`): page through a 250-row set at `limit=100`, assert 3 pages with no overlaps/gaps and `nextCursor` null on the last page; **concurrent-insert stability** — capture page 1's cursor, append 10 newer rows, fetch page 2, assert page 2 contains exactly the originally-expected rows (newer rows do not shift the boundary); undecodable cursor → 400; `from > to` → 400.
4. **RBAC matrix** (integration, `Audit/AuditRbacTests.cs`): per mode (single-user, SaaS) assert — member → 403, tenant_admin own-tenant → 200, tenant_admin foreign-tenant → 403, non-platform-owner on `/api/v1/admin/audit` → 403, platform owner → 200, single-user → 200 on both.
5. **Cross-tenant non-leakage** (integration): seed tenant A and tenant B `audit_records`; as a tenant-A admin, attempt a query whose explicit predicate is tampered to tenant B (ambient context pinned to A) — assert zero tenant-B rows (global query filter wall). Assert tenant query never reads `platform_records` and vice-versa.
6. **Meta-audit** (integration): a successful tenant read appends exactly one `AUDIT.QUERIED` event tagged `scope=tenant` with the filter set and result count in `Data`; a successful platform read appends `scope=platform` with `tenantId=null`; a 403 read appends NO `AUDIT.QUERIED`.
7. **Performance** (integration, gated/optional locally): seed ~1M `audit_records` for one tenant; assert a representative filtered + keyset query is **p95 < 300ms** with the composite indexes present; assert the same query degrades sharply (regression canary) if the `(tenant_id, source_sequence_number DESC)` index is dropped.
8. **Backward-compat** (integration): legacy `?type=X&offset=Y` against the tenant route still returns rows (mapped to `action`; `offset` ignored-with-warning), proving the existing `AuditLogTab` is not broken.

## Estimated Effort

4-5 days (1 day query service + filter/cursor codec; 1 day repositories + indexes + migrations; 1 day endpoints + DTOs + meta-audit; 1.5-2 days the full test matrix including the cross-tenant/concurrent-insert/perf cases).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (`ListTenantAudit` → rich query over `audit_records`; keep legacy params) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (add `ListPlatformAudit` handler) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/IAuditQueryService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditQueryService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditQueryFilter.cs` | Create (parse/validate + cursor codec) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditQueryEventTypes.cs` | Create (`AUDIT.QUERIED`) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Audit/AuditRecordResponse.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Audit/AuditQueryResponse.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAuditRecordRepository.cs` | Create or extend (37-1 seam) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/AuditRecordRepository.cs` | Create or extend |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IPlatformAuditRecordRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformAuditRecordRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*_AuditRecordsQueryIndexes.cs` | Create (composite indexes) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_PlatformRecordsQueryIndexes.cs` | Create (composite indexes) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register `/api/v1/admin/audit`; DI for `AuditQueryService` + repos) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditQueryServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditKeysetPaginationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditRbacTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditCrossTenantTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditMetaAuditTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (audit, keyset pagination, tenant isolation).
3. Confirmed the Story 37-1 seam: the exact entity names/columns of `audit_records` / `platform_records`, whether `IAuditRecordRepository` already exists, and the precise name + type of the sequence column (this story assumes `SourceSequenceNumber` / `source_sequence_number`, a monotonic `long` from the DCB global sequence).
4. Planned TDD (Red-Green-Refactor) — tests first, especially the keyset and cross-tenant cases.

### `packages/api` is deleted — do NOT target it

The TypeScript `packages/api` referenced by the stale Epic 23 specs no longer exists. Every artifact in this story lands in the C# `apps/tamma-elsa` solution (`Tamma.Api`, `Tamma.Data`, `Tamma.Api.Tests`). Do not create or cite `packages/api`.

### Reuse the existing isolation seams verbatim

- RBAC: `RequireTenantMembershipFilter` (sets `httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`) + `TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin)` — the exact pattern already in `ListTenantAudit` (`OrgEndpoints.cs:539`). Copy it; do not invent a new gate.
- Cross-tenant wall: pin `ITenantContext.SetTenantId(tenantId)` before the query so the per-tenant global query filter on `TenantDbContext` engages (defence-in-depth), exactly as the current handler does.
- Platform gate: `PlatformOwnerAccess` policy (defined in `Program.cs:986`), used per-route on the `v1Admin` group as the alerts endpoints do.

### Keyset, not offset

The current handler uses `offset` — fine for a small dashboard, wrong for a 1M-row compliance trail. The new contract is `cursor` (keyset on `source_sequence_number`). Keep `offset` accepted-but-ignored-with-WARN for one release for the existing `AuditLogTab` (`packages/dashboard/src/pages/admin/AuditLogTab.tsx`); the dashboard migration to `cursor` is a separate follow-up.

### `total` is an estimate by design

A full `COUNT(*)` over filtered millions of rows would blow the p95 budget. Return the Postgres planner's row estimate (`EXPLAIN`-style) or a capped exact count (e.g. exact up to 10k, then "10000+"). The dashboard shows "~N" / "N+" — document the semantics in the response field comment.

### `q` search scaling

The B-tree composite indexes serve the structured-filter p95 (AC 9). `ILIKE '%term%'` over `payload_text`/`actor_label` is a sequential scan within the already-narrowed result set; for large unfiltered `q`-only queries, add a `pg_trgm` GIN index as a documented follow-up — out of scope here.

### Meta-audit must not recurse or fail the read

`AUDIT.QUERIED` is a DCB event, NOT an `audit_records` row, so it does not feed back into this query surface (no recursion). Append it best-effort after the response data is materialized; a WARN-logged append failure must not fail the user's read.

## Logging Requirements

- **INFO**: audit query served (scope, tenantId, actorUserId, applied-filter keys, resultCount, page durationMs); `AUDIT.QUERIED` emitted.
- **DEBUG**: parsed filter set, decoded cursor, generated keyset predicate (no PII — filter *keys*, not full payload search terms at INFO).
- **WARN**: `AUDIT.QUERIED` append failed (read still served); legacy `offset` param used (deprecation); `total` fell back to estimate after count cap; undecodable cursor (paired with the 400).
- **ERROR**: query execution failure (DB/timeout) — surfaced as 500 with a correlation id, never a partial result.
- **Structured context**: `{ scope, tenantId, actorUserId, filterKeys, resultCount, durationMs, mode }`.
- **Credential / PII safety**: NEVER log full `q` search terms at INFO+ (may contain emails/IDs); NEVER log `ip_address` of audited rows in application logs; NEVER log row payloads. Redact per the existing logging conventions.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
