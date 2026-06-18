# Story 36-8 — Analytics Exports (CSV / PDF) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Let a tenant export any analytics view — usage (36-3), cost (36-4), or agent performance
(36-5) — as a raw-row **CSV** or a branded **PDF** report, for a chosen range + grouping. Exports
run server-side off the per-tenant dimensional store, **read the existing query services (never
re-aggregate the raw event stream)**, are hard tenant-scoped + RBAC-gated, emit `DATA.EXPORT.*` DCB
audit events (Epic 37), and switch from inline rendering to a background `QueuedTask` job (with a
signed, expiring download) when the range is large.

**Story file:** `docs/stories/epic-36/story-36-8/36-8-analytics-exports.md` (drafted; 12 ACs).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (`Tamma.Api` endpoints +
services, `Tamma.Data` per-tenant entities). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). PDF/CSV libraries are NEW package additions (decide first).

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Seam | File (verified) | Note |
|---|---|---|
| Per-tenant DB factory | `Tamma.Data/Abstractions/ITenantDbContextFactory.cs` | `CreateAsync(tenantId, ct)` → per-tenant `TenantDbContext` (search-path schema). Analytics read models (36-1/2) live here. |
| Tenant guard | `Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` | 401 unauth / 400 bad `tenantId` / 403 non-member; stashes `Items["TenantRole"]`. The "same guard as 36-3". |
| Tenant-scope endpoint precedent | `Tamma.Api/Endpoints/AlertEndpoints.cs` (tenant section ~L560+) + `Program.cs` orgs group (L1512+) | `MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")` + per-route `.AddEndpointFilter<RequireTenantMembershipFilter>()`; admin+ mutation gate via `TenantRoleHierarchy.IsAtLeast`. |
| Async job queue | `Tamma.Data/Entities/QueuedTask.cs` (tenant-scoped, `TenantId` non-null), `Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs`, `ITaskHandler.cs` | Processor fans out across tenants, `MarkProcessing/Completed/Failed`, retry budget, visibility-timeout reaper. `PlatformQueuedTask` is CP/pre-routing — **do not** use it for tenant exports. |
| DCB append | `Tamma.Data/Repositories/EventRepository.cs` `AppendAsync` | `DomainEvent.TenantId` set → routes the event into the **tenant's own** `domain_events`. Best-effort emit precedent: `PromptEventsService`, `AlertEndpoints.TryEmitAsync`. |
| Analytics endpoint precedent | `Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` (owner-only, CP) | `/api/admin/analytics/*` is owner/CP; tenant exports are a NEW tenant-scope surface. |
| Activities/Analytics dir | `Tamma.Activities/Analytics/` (`AnalyticsRollupEvents`, `PurgeStaleAnalyticsActivity`, …) | Retention-purge tolerance shape to mirror for the artifact sweep. |

**Gaps (NEW — must build):**
- Stories **36-3/36-4/36-5 query services are not yet authored** — they are this story's read
  contract. Confirm their merged interface + DTO names before pinning the CSV golden header.
- **No CSV or PDF library** in the solution — both are NEW package refs (decide PDF lib first).
- **No blob/object store** — artifact bytes go on the tenant row (`bytea`) behind an
  `IExportArtifactStore` seam; signed download is served by the app, not a CDN.
- **Epic 37** (audit/compliance) is planned/NEW — this story's `DATA.EXPORT.*` events are its input.

---

## Non-goals (YAGNI guard)

- NO analytics aggregation here — read 36-3/4/5; re-aggregating `DomainEvent`/`ProviderDiagnostic`
  would fork the numbers from the dashboard. A structure test pins the dependency direction.
- NO schema change to the 36-1 fact tables — only the additive `analytics_exports` table.
- NO scheduled/recurring delivery (Story 36-9) and NO owner business-analytics export — keep the
  render path injectable so 36-9 can reuse it later.
- NO external object store / CDN — `bytea` on the tenant row behind `IExportArtifactStore`; the
  seam makes S3/blob a future drop-in with no endpoint change.
- NO `PlatformQueuedTask` — tenant exports use the tenant-scoped `QueuedTask` queue.
- NO new alert delivery — `DATA.EXPORT.*` are audit events for Epic 37, not alerts.

---

## Architecture

**request → validate/bound → (sync render | enqueue) → render via 36-3/4/5 → persist artifact →
audit event → signed download**, reusing the tenant guard, the task queue, and the DCB store.

```
POST /api/v1/orgs/{tenantId}/analytics/exports   (MemberAccess + RequireTenantMembershipFilter)
  │  validate spec (type/format/from/to/groupBy allow-lists, UTC, from<=to, max-window)
  │  estimate rows  ──► ≤ threshold ─► AnalyticsExportService.RenderAsync (inline) ─► 200 file
  │                  └► > threshold ─► enqueue QueuedTask{analytics.export, TenantId} ─► 202 {jobId}
  │  emit DATA.EXPORT.REQUESTED
  ▼
AnalyticsExportService.RenderAsync(tenantId, userId, spec)
  │  switch spec.Type → IUsage/Cost/AgentPerformance QueryService  (36-3/4/5 — reads, no re-agg)
  │  switch spec.Format → AnalyticsCsvWriter | AnalyticsPdfReportRenderer
  │  IExportArtifactStore.SaveAsync(bytes) → analytics_exports row (tenant schema, bytea default)
  │  emit DATA.EXPORT.COMPLETED  (best-effort)   |   on throw → status=failed + DATA.EXPORT.FAILED
  ▼
AnalyticsExportTaskHandler : ITaskHandler ("analytics.export")  — same RenderAsync, idempotent
  ▼
GET  …/exports/{jobId}            → status + signed URL (ExportDownloadSigner, HMAC, TTL)
GET  …/exports/{jobId}/download   → verify sig (fixed-time, expiry) → stream artifact bytes
```

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who owns an export? | sole user; their one tenant schema. | the tenant; its `t_<hex>` schema (`requestedBy` = member). |
| Who may request/download? | the user (authentication only). | any tenant member (`MemberAccess` + membership) — an export is a read. |
| Cross-tenant | N/A | 403 request; 403/404 job/download lookup; tenant-A signature → 403 on tenant-B. |
| Audit residency | tenant's own `domain_events`. | tenant's own `domain_events`. |
| Mode source | `RequireTenantMembershipFilter` + per-tenant `TenantDbContext`. | same. |

---

## Task breakdown

### T1: `analytics_exports` entity + Tenant mapping + migration (AC 6, 8)

**Files:** new `Tamma.Data/Entities/AnalyticsExport.cs`; DbSet on `Tamma.Data/TenantDbContext.cs`;
`ConfigureAnalyticsExports` in `Tamma.Data/TammaModelConfiguration.cs` (called from
`ConfigureTenantEntities`, no `TenantId` column — `ApplyTenantFilter` no-op per Story 36-1; index
`(Status, ExpiresAt)`); additive migration via
`dotnet ef migrations add AddAnalyticsExports -c TenantDbContext`.

**Tests (first):** `AnalyticsExportMigrationTests` (Postgres 17 Testcontainer) — two schemas each
carry `analytics_exports`; a row in schema A invisible through schema B; `has-pending-model-changes
-c TenantDbContext` clean.

**AC:**
- [ ] Entity + tenant mapping + DbSet; no `TenantId` column (schema isolation).
- [ ] Migration applies/rolls back cleanly; pending-model-changes reports none.
- [ ] Per-tenant isolation proven (schema A row unreachable from schema B).

### T2: spec + validation + event-type constants (AC 1, 5, 6)

**Files:** new `AnalyticsExportSpec.cs` (record + validator), `AnalyticsExportOptions.cs`
(SyncRowThreshold=50_000, SyncRangeDays=92, MaxWindowDays=366, DownloadTtl=1h, ArtifactTtl=7d),
`AnalyticsExportEventTypes.cs` (`DATA.EXPORT.REQUESTED|COMPLETED|FAILED`).

**Tests (first):** spec validator — unknown type/format → invalid; non-UTC / unparseable date →
invalid; `from > to` → invalid; range > MaxWindowDays → invalid; unknown `groupBy` dimension →
invalid; valid spec round-trips.

**AC:**
- [ ] Allow-lists + UTC + `from<=to` + max-window + groupBy-dimension validation.
- [ ] Event-type constants match the `AGGREGATE.ACTION.STATUS` convention.

### T3: CSV writer (AC 2, 12)

**Files:** new `Render/AnalyticsCsvWriter.cs` — per-type column order **from the 36-3/4/5 DTO**;
dimensions first then measures; raw unrounded numbers; empty cell for NULL dimension; UTF-8 BOM;
header-only on empty.

**Tests (first):** `AnalyticsCsvWriterTests` — golden header + row order per type; raw-measure
formatting; empty-NULL cell (never `"unknown"`); BOM; empty-result header-only.

**AC:**
- [ ] One row per dimension-tuple per bucket; documented stable headers matching the DTO.
- [ ] Raw measures; empty NULL cell; BOM; empty-result tolerated.

### T4: PDF report renderer (AC 3, 12)

**Pre-step (research):** WebSearch the current best server-side PDF option (QuestPDF license
eligibility vs PuppeteerSharp HTML-to-PDF reusing dashboard chart markup); record in
`.dev/decisions/`. Add the package to `Tamma.Api.csproj`.

**Files:** new `Render/AnalyticsPdfReportRenderer.cs` — branded header (Tamma brand, tenant name,
type, range, generated-at), summary, per-`groupBy` breakdown tables, chart image(s); "no data"
path.

**Tests (first):** `AnalyticsPdfReportRendererTests` — `%PDF-` magic, non-trivial size, contains
summary + ≥1 breakdown table for populated spec; valid "no data" PDF for empty.

**AC:**
- [ ] Branded report with the same summary + breakdown tables + chart(s) as the dashboard view.
- [ ] Rendered from the same query-service result as the CSV (one source of truth).

### T5: export service + artifact store (AC 1, 3, 5, 8, 11)

**Files:** new `IAnalyticsExportService.cs` + `AnalyticsExportService.cs` (calls 36-3/4/5 query
services by `spec.Type`, dispatches to CSV/PDF, persists via `IExportArtifactStore`, emits audit,
threshold estimator); `IExportArtifactStore.cs` + `DbExportArtifactStore.cs` (bytes on row).

**Tests (first):** `AnalyticsExportServiceTests` — correct query service per type (mocked 36-3/4/5);
renders requested format; persists artifact; emits REQUESTED+COMPLETED with documented tags;
threshold routes small→inline / large→enqueue; renderer throw → failed + FAILED; empty result →
COMPLETED rowCount 0; **structure test: depends on query-service abstractions, NOT `DomainEvent`/
`ProviderDiagnostic`** (AC11).

**AC:**
- [ ] Reads 36-3/4/5; never re-aggregates raw events (structure test pins it).
- [ ] Persists artifact; emits the three audit events with correct tags.
- [ ] Threshold estimator decides sync vs async.

### T6: async path — `QueuedTask` handler (AC 6, 12)

**Files:** new `Services/TaskQueue/Handlers/AnalyticsExportTaskHandler.cs`
(`ITaskHandler`, `TypePrefix = "analytics.export"`): deserialize spec+userId, `RenderAsync`,
persist, emit COMPLETED/FAILED, idempotent (completed job id → no-op); register in the
task-handler registry wiring.

**Tests (first):** `AnalyticsExportTaskHandlerTests` — payload renders+persists+COMPLETED; second
delivery of completed job → no-op; render failure → failed + FAILED (processor retry budget
applies).

**AC:**
- [ ] Renders the same way the sync path does; idempotent on re-delivery.
- [ ] Enqueues a tenant-scoped `QueuedTask` (`TenantId` non-null), not `PlatformQueuedTask`.

### T7: signed download (AC 7)

**Files:** new `ExportDownloadSigner.cs` — HMAC-SHA256 over `(tenantId, jobId, expiresAt)`,
constant-time verify (`CryptographicOperations.FixedTimeEquals`); secret from config.

**Tests (first):** `ExportDownloadSignerTests` — sign→verify round-trip; tampered
sig/jobId/tenantId reject; expired `expiresAt` rejects.

**AC:**
- [ ] Sign on status read; verify on download (403 on tamper/expiry); constant-time compare.
- [ ] Secret never logged.

### T8: endpoints + wiring (AC 1, 4, 6, 7, 9)

**Files:** new `Tamma.Api/Endpoints/AnalyticsExportEndpoints.cs` (POST exports, GET `{jobId}`,
GET `{jobId}/download`); new `Extensions/AnalyticsExportServiceCollectionExtensions.cs`; map under
the orgs group in `Program.cs` each with `.AddEndpointFilter<RequireTenantMembershipFilter>()`;
register services + handler.

**Tests (first):** `AnalyticsExportEndpointsTests` (WebApplicationFactory) — 401 unauth; 403
non-member; small→200 file (`Content-Disposition: attachment`); large→202 `{jobId}`; GET `{jobId}`→
status+signed URL; download valid sig→bytes; cross-tenant request/job/download→403/404; tenant-A
signature→403 on tenant-B.

**AC:**
- [ ] Endpoints behind membership filter; hard tenant scope; cross-tenant 403/404.
- [ ] Endpoint shape identical across modes (filter + per-tenant context resolve scope).

### T9: retention sweep (AC 8)

**Files:** best-effort purge of `analytics_exports` past `ExpiresAt` (mirror
`PurgeStaleAnalyticsActivity` tolerance — never rethrow a transient failure).

**Tests (first):** purge deletes expired rows, leaves live rows, swallows transient failure.

**AC:**
- [ ] Expired artifacts swept per the configured TTL; best-effort.

---

## Task order & dependencies

T1 → T2 → (T3 ∥ T4 ∥ T7) → T5 (needs T2/T3/T4 + 36-3/4/5 query services) → T6 (needs T5) →
T8 (needs T5/T6/T7) → T9. T3/T4/T7 are parallel-safe after T2.

**Hard external prerequisite:** Stories 36-3/36-4/36-5 query services must be merged before T5 —
they define the read contract + the CSV/PDF column shape. T1–T4/T7 (entity, spec, CSV scaffolding,
PDF lib decision, signer) can start before they land, but the DTO-shaped golden tests (T3) and the
service (T5) are blocked on the merged DTOs.

## Risks

- **Query-service contract drift (T3/T5):** the CSV golden header + PDF tables are defined by the
  36-3/4/5 DTOs, which aren't authored yet. Mitigation: pin the DTO names/shape against the merged
  stories before writing the golden test; keep the column map in one place so a DTO change is a
  single edit. Don't invent a DTO — block on the real one.
- **PDF library choice:** no PDF lib exists; QuestPDF's license (Community vs Professional) must be
  confirmed for this project, or use PuppeteerSharp HTML-to-PDF (heavier runtime, but reuses
  dashboard chart markup). Decide + record in `.dev/decisions/` before adding the package
  (CLAUDE.md: research latest before using a new dependency).
- **Large artifact on the row (`bytea`):** a multi-MB export on the tenant row is acceptable for v1
  behind the size bound + 7-day TTL sweep, but the `IExportArtifactStore` seam must stay clean so a
  future S3 backend is a drop-in — don't leak `bytea` assumptions into the endpoint or signer.
- **Audit event must not block the export:** `DATA.EXPORT.*` emission is best-effort (wrapped, never
  throws) per the `PromptEventsService`/`AlertEndpoints.TryEmitAsync` precedent — a broken event
  store must not turn a successful export into a 500.
- **Async idempotency:** the processor can re-deliver a task (visibility-timeout reap / retry); the
  handler must no-op a completed job id and never produce a partial artifact — whole-bucket
  re-render makes this safe.
- **Migration discipline:** `analytics_exports` is additive on the Tenant graph; still verify
  `has-pending-model-changes -c TenantDbContext` reports none and mirror entity config only in
  `TammaModelConfiguration.cs` (single source).
- **Event-store topology (Story 28-1 / Epic 30):** `DATA.EXPORT.*` events carry `TenantId` so they
  already route to the tenant's own `domain_events` — no change needed when per-tenant event
  routing tightens.

## Verification

- `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Analytics"`
  green (CSV golden, PDF smoke, service, handler, endpoints, signer, migration isolation).
- `dotnet ef migrations has-pending-model-changes -c TenantDbContext` → none.
- Full suite stays green; no new lint/build warnings.
- Manual smoke: POST a small usage CSV export → 200 attachment; POST a >92-day range → 202 +
  jobId → GET `{jobId}` → signed URL → download streams identical bytes; cross-tenant download →
  403; check the tenant's `domain_events` has `DATA.EXPORT.REQUESTED/COMPLETED`.
