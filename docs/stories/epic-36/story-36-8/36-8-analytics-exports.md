# Story 36-8: Analytics Exports (CSV / PDF)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **tenant administrator** (SaaS) **/ self-hosted owner** (single-user),
I want to export any of my analytics views — usage, cost, or agent performance — as a CSV
(raw dimensional rows) or a branded PDF (the same summary + breakdown tables and chart images the
dashboard renders) for a chosen date range and grouping,
so that I can hand a finance team a spreadsheet, attach a performance report to a ticket, or keep
an offline archive — without ever seeing another tenant's data, and without a large range
blocking the request thread.

## Priority

P1 — Exports are the first "take my analytics out of the product" surface. They are gated behind
the Story 36-1 fact tables and the Story 36-3/36-4/36-5 query services; they ship after those
read paths exist but are independent of scheduled reports (Story 36-9) and owner business
analytics.

## Scope

Export pipeline **only**. This story adds, on top of the per-tenant dimensional store
(Stories 36-1/36-2) and the tenant query services (Stories 36-3 usage / 36-4 cost / 36-5 agent
performance):

- a tenant-scoped `POST /api/v1/orgs/{tenantId}/analytics/exports` endpoint that accepts a
  `{ type, format, from, to, groupBy }` spec, validates + bounds it, and either returns the file
  inline (small ranges) or enqueues a background job and returns a job id (large ranges);
- an `AnalyticsExportService` that renders one export by **calling the existing 36-3/36-4/36-5
  query services** (never re-aggregating the raw event stream) into CSV (raw rows) or PDF
  (rendered report);
- a CSV writer with stable, documented column headers matching the query-service DTOs;
- a PDF report renderer (summary + breakdown tables + chart images) branded for Tamma;
- an async path on the existing tenant `QueuedTask` queue + `TaskQueueProcessor` for ranges over
  the streaming threshold, with a signed, expiring download URL on completion;
- a `DATA.EXPORT` DCB audit event (REQUESTED / COMPLETED / FAILED) appended to the tenant's own
  event store, feeding Epic 37 (audit/compliance).

**Out of scope:** the query services themselves (36-3/36-4/36-5 own the aggregation + DTOs — this
story only reads them); scheduled/recurring report delivery (36-9); the platform-owner business
analytics export (a later owner-only story); a long-lived object store / CDN — the signed download
is served from the tenant DB-backed export-artifact row over the app, not an external blob store
(an `IExportArtifactStore` seam keeps a future S3/blob backend a drop-in).

## Acceptance Criteria

1. `POST /api/v1/orgs/{tenantId}/analytics/exports` accepts a JSON body
   `{ type: "usage"|"cost"|"agents", format: "csv"|"pdf", from: ISO8601, to: ISO8601,
   groupBy?: string[] }` and returns **either** the rendered file inline
   (`200`, `Content-Disposition: attachment`, correct content type) when the requested range is at
   or under the streaming threshold, **or** `202 Accepted` with `{ jobId, status: "queued" }` when
   the range exceeds the threshold. `type`/`format` are validated against allow-lists (400 on an
   unknown value); `from`/`to` are required, must parse as UTC, and `from <= to` (400 otherwise);
   `groupBy` entries are validated against the dimension allow-list the target query service
   exposes (`provider`, `agent`, `workflow`, `repo`, `costBasis`) — an unknown dimension is a 400.

2. **CSV export** emits one row per dimension-tuple per time bucket, with **stable, documented
   column headers that match the corresponding query-service DTO** (usage → 36-3 DTO, cost → 36-4,
   agents → 36-5). The dimension columns (`bucket`, `provider`, `agentId`, `workflowDefinitionId`,
   `repoId`, `costBasis` — those the `groupBy` selects) come first, then the measure columns
   (`tokensIn`, `tokensOut`, `costUsd`, `platformBilledUsd`, workflow counts, agent dispatches…).
   Measure numbers are written as **unrounded raw measures** (the same `long`/`decimal(20,4)` the
   fact store holds — no display rounding); the `NULL` dimension bucket (Story 36-2 "unattributed")
   is written as an empty cell, never a `"unknown"` sentinel. The header row + a UTF-8 BOM make the
   file Excel-friendly; the column order is asserted by a golden test so it never silently drifts.

3. **PDF export** renders a branded report containing the **same summary block + per-dimension
   breakdown tables (and chart images) the corresponding dashboard view shows** for the period:
   a header (Tamma brand, tenant display name, report type, range, generated-at), a summary
   section (period totals), one or more breakdown tables (one per `groupBy` dimension), and chart
   image(s) matching the dashboard chart for that view. The PDF is generated server-side from the
   same query-service results the CSV path uses (one source of truth — the two formats can never
   disagree on the numbers for the same spec).

4. **Hard tenant scoping** — identical guard to the Story 36-3 query endpoints:
   `RequireTenantMembershipFilter` proves the caller is a member of `{tenantId}` (401 unauth /
   403 non-member), the export reads **only** that tenant's per-tenant schema via
   `ITenantDbContextFactory.CreateAsync(tenantId, ct)` (the query services it calls are already
   per-tenant), and a download/job lookup for an export belonging to another tenant returns
   **403/404** (never the file). In single-user mode the sole user owns every export; in SaaS mode
   any tenant member may request + download their tenant's exports (read-only is fine — an export
   is a read), mirroring `MemberAccess` on the 36-3 read path. A cross-tenant export attempt 403s.

5. Export generation emits a **`DATA.EXPORT.REQUESTED`** DCB event on accept,
   **`DATA.EXPORT.COMPLETED`** when the file is rendered (inline or async), and
   **`DATA.EXPORT.FAILED`** on render failure — each appended via `IEventRepository.AppendAsync`
   with `TenantId` set so it lands in the **tenant's own** `domain_events` store, tagged
   `tenantId`, `userId`, `type`, `format` (+ `jobId`, `rowCount`, `bytes`, `durationMs` on
   completion). These are the audit trail Epic 37 consumes; emission is best-effort and never
   blocks or fails the export (PromptEvents/AlertEndpoints best-effort precedent).

6. **Range/size are bounded.** The endpoint rejects (400) a range wider than the configured
   maximum window (default 366 days). A configurable **streaming threshold** (default: estimated
   row count > 50,000, or range > 92 days) decides sync-vs-async: at/under the threshold the file
   renders inline on the request; over it, the request enqueues a tenant-scoped
   `QueuedTask` (`Type = "analytics.export"`, `TenantId = {tenantId}`, payload = the validated
   spec + requesting userId) and returns `202 { jobId }`. The `AnalyticsExportTaskHandler`
   (`ITaskHandler`) renders the export on the `TaskQueueProcessor` thread, writes the artifact, and
   emits `DATA.EXPORT.COMPLETED`/`FAILED`; the handler is idempotent on re-delivery (a completed
   job id is a no-op).

7. A completed async export is retrievable via **`GET /api/v1/orgs/{tenantId}/analytics/exports/{jobId}`**
   (status + a **signed, time-bounded download URL** when `status = "completed"`) and
   **`GET /api/v1/orgs/{tenantId}/analytics/exports/{jobId}/download?sig=…`** (streams the artifact
   when the HMAC signature is valid and unexpired). The signature is computed over
   `(tenantId, jobId, expiresAt)` with a server secret (HMAC-SHA256, constant-time compare); an
   expired or tampered signature 403s; the artifact + its signing inputs are tenant-scoped so a
   signed URL minted for tenant A can never resolve tenant B's file. Both routes sit behind
   `RequireTenantMembershipFilter`.

8. The export artifact is persisted to a per-tenant `analytics_exports` row (job state machine
   `queued → running → completed | failed`, plus `type`, `format`, `spec` JSON, `requestedBy`,
   `rowCount`, `byteSize`, `contentBytes`/storage-pointer, `expiresAt`, timestamps) in the
   **tenant schema** (additive Tenant EF migration). Artifact bytes are read/written through an
   `IExportArtifactStore` seam whose default implementation stores the bytes on the row
   (`bytea`); the seam keeps a future external blob/S3 backend a drop-in with no endpoint change.
   A retention sweep (best-effort, mirroring `PurgeStaleAnalyticsActivity`) deletes
   `analytics_exports` rows past `expiresAt` (default 7-day artifact TTL).

9. **Per-mode + per-tenant ownership is explicit.** single-user: the sole user owns every export
   (`requestedBy = userId`, one tenant schema, no RBAC beyond authentication). SaaS: the export
   belongs to the tenant; any member may request + download (an export is a read of data they can
   already see); the artifact, job lookup, and signed URL are all hard-scoped to the path tenant.
   The endpoint shape is identical across modes — the membership filter + per-tenant
   `TenantDbContext` resolve scope, exactly as the prompt-store/36-3 precedent prescribes.

10. **Tests** cover: CSV column correctness (golden header + row ordering + raw-number /
    empty-NULL-cell formatting) for all three `type`s; PDF generation smoke (valid PDF bytes,
    %PDF- magic, contains the summary + a breakdown table); the sync-vs-async threshold decision
    (small → inline 200; large → 202 + jobId); the async job path end-to-end (enqueue → handler
    renders → artifact persisted → status=completed → signed download streams the same bytes);
    tenant isolation (cross-tenant export request 403; cross-tenant job/download lookup 403/404;
    a signed URL for tenant A rejected on tenant B); signed-URL expiry + tamper rejection; and
    `DATA.EXPORT.REQUESTED/COMPLETED/FAILED` audit-event emission (with the documented tags).

11. The export **never re-aggregates the raw event stream** — it calls the Story 36-3/36-4/36-5
    query services (or their shared query-result contracts) for the numbers. A structure/contract
    test asserts the service depends on the query-service abstractions, not on `DomainEvent` /
    `ProviderDiagnostic` directly, so the export and the dashboard are guaranteed to show the same
    figures for the same spec.

12. CSV and PDF rendering tolerate an **empty result** (a range with no facts) — CSV emits the
    header row only; PDF emits a valid report with a "no data for this period" summary — both
    still emit `DATA.EXPORT.COMPLETED` (rowCount 0). Rendering failures (e.g. PDF library throwing)
    flip the job to `failed`, emit `DATA.EXPORT.FAILED` with `errorType`, and (sync path) return a
    `500` with a sanitized message — never a partial/corrupt file.

## Tasks / Subtasks

- [ ] Task 1: `analytics_exports` entity + Tenant EF mapping + migration (AC: 6, 8)
  - [ ] Add `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsExport.cs` (job + artifact row);
        DbSet on `TenantDbContext`; configure in `TammaModelConfiguration.ConfigureTenantEntities`
        (status/type/format CHECK-style guards via length + allow-list constants; index on
        `(Status, ExpiresAt)` for the retention sweep).
  - [ ] Additive Tenant migration `AddAnalyticsExports` (`dotnet ef migrations add … -c
        TenantDbContext`); `has-pending-model-changes -c TenantDbContext` reports none.

- [ ] Task 2: Export spec + DTO/validation + event types (AC: 1, 5, 6)
  - [ ] `AnalyticsExportSpec` record (type/format/from/to/groupBy) + validator (allow-lists,
        UTC parse, `from<=to`, max-window, groupBy-dimension allow-list).
  - [ ] `AnalyticsExportEventTypes` constants (`DATA.EXPORT.REQUESTED|COMPLETED|FAILED`).

- [ ] Task 3: CSV writer (AC: 2, 12)
  - [ ] `AnalyticsCsvWriter` — per-type column order from the query-service DTO; raw measures;
        empty cell for NULL dimension; UTF-8 BOM; header-only on empty. Golden tests first.

- [ ] Task 4: PDF renderer (AC: 3, 12)
  - [ ] `AnalyticsPdfReportRenderer` — branded header, summary, per-dimension breakdown tables,
        chart image(s); "no data" path. Pick a server-side PDF lib (research latest — QuestPDF /
        PuppeteerSharp-from-HTML); document the choice in `.dev/decisions/`.

- [ ] Task 5: `AnalyticsExportService` + `IExportArtifactStore` (AC: 1, 3, 5, 8, 11)
  - [ ] Service calls 36-3/36-4/36-5 query services, dispatches to CSV/PDF, persists the artifact
        via `IExportArtifactStore` (default: bytes on the row), emits audit events.
  - [ ] Threshold estimator (sync vs async) driven by `AnalyticsExportOptions`.

- [ ] Task 6: Async path — `QueuedTask` handler (AC: 6, 12)
  - [ ] `AnalyticsExportTaskHandler : ITaskHandler` (`TypePrefix = "analytics.export"`):
        deserialize spec, render, persist, emit COMPLETED/FAILED, idempotent on re-delivery.
  - [ ] Register the handler in `ITaskHandlerRegistry` wiring.

- [ ] Task 7: Signed download (AC: 7)
  - [ ] `ExportDownloadSigner` (HMAC-SHA256 over `(tenantId, jobId, expiresAt)`, constant-time
        verify); secret from config. Mint on status read; verify on download.

- [ ] Task 8: Endpoints + wiring (AC: 1, 4, 6, 7, 9)
  - [ ] `AnalyticsExportEndpoints` (POST exports, GET exports/{jobId}, GET …/download);
        map under `/api/v1/orgs/{tenantId}/analytics/exports` with
        `RequireTenantMembershipFilter` in `Program.cs`.

- [ ] Task 9: Retention sweep (AC: 8)
  - [ ] Best-effort `analytics_exports` purge past `ExpiresAt` (activity or handler-scheduled),
        mirroring `PurgeStaleAnalyticsActivity` tolerance.

- [ ] Task 10: Tests (AC: 10, 11, 12)
  - [ ] CSV golden, PDF smoke, threshold, async end-to-end, tenant isolation, signed-URL
        expiry/tamper, audit emission, empty-result, query-service-dependency structure test.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/Entities/AnalyticsExport.cs                              # NEW — job + artifact row (tenant schema)
  Tamma.Data/TenantDbContext.cs                                        # MODIFY — + DbSet<AnalyticsExport>
  Tamma.Data/TammaModelConfiguration.cs                               # MODIFY — + ConfigureAnalyticsExports (tenant graph)
  Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsExports.cs            # NEW (generated)

  Tamma.Api/Endpoints/AnalyticsExportEndpoints.cs                     # NEW — POST exports + GET {jobId} + GET download
  Tamma.Api/Services/Analytics/AnalyticsExportService.cs             # NEW — orchestrates render (reads 36-3/4/5 services)
  Tamma.Api/Services/Analytics/IAnalyticsExportService.cs            # NEW
  Tamma.Api/Services/Analytics/AnalyticsExportSpec.cs               # NEW — validated request spec
  Tamma.Api/Services/Analytics/AnalyticsExportOptions.cs           # NEW — thresholds, max window, artifact TTL
  Tamma.Api/Services/Analytics/AnalyticsExportEventTypes.cs        # NEW — DATA.EXPORT.* constants
  Tamma.Api/Services/Analytics/Render/AnalyticsCsvWriter.cs        # NEW — raw-row CSV
  Tamma.Api/Services/Analytics/Render/AnalyticsPdfReportRenderer.cs # NEW — branded PDF
  Tamma.Api/Services/Analytics/IExportArtifactStore.cs            # NEW — bytes seam (default: row bytea)
  Tamma.Api/Services/Analytics/DbExportArtifactStore.cs          # NEW — default impl
  Tamma.Api/Services/Analytics/ExportDownloadSigner.cs          # NEW — HMAC signed URL
  Tamma.Api/Services/TaskQueue/Handlers/AnalyticsExportTaskHandler.cs # NEW — ITaskHandler ("analytics.export")
  Tamma.Api/Extensions/AnalyticsExportServiceCollectionExtensions.cs  # NEW — DI wiring
  Tamma.Api/Program.cs                                                 # MODIFY — map endpoints + register services/handler

apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/
  AnalyticsCsvWriterTests.cs                  # NEW — golden column/row/format (per type)
  AnalyticsPdfReportRendererTests.cs          # NEW — PDF smoke
  AnalyticsExportServiceTests.cs              # NEW — sync render, threshold, audit emission, empty result
  AnalyticsExportTaskHandlerTests.cs          # NEW — async path + idempotency
  AnalyticsExportEndpointsTests.cs            # NEW — RBAC/tenant-isolation, sync 200 vs async 202, signed download
  ExportDownloadSignerTests.cs                # NEW — sign/verify/expiry/tamper
  AnalyticsExportMigrationTests.cs            # NEW — Postgres 17: per-tenant isolation of analytics_exports
```

### Reads the query services — does NOT re-aggregate (AC11)

The service depends on the **Story 36-3 (usage), 36-4 (cost), 36-5 (agent performance) query
service abstractions** — the same ones the tenant dashboard calls. It never touches `DomainEvent`
or `ProviderDiagnostic` directly (that is the projection's job, Story 36-2). One source of truth
means the CSV, the PDF, and the dashboard can never disagree:

```csharp
public sealed class AnalyticsExportService(
    IUsageAnalyticsQueryService usageQuery,        // Story 36-3 (NEW dep)
    ICostAnalyticsQueryService costQuery,          // Story 36-4 (NEW dep)
    IAgentPerformanceQueryService agentQuery,      // Story 36-5 (NEW dep)
    AnalyticsCsvWriter csv,
    AnalyticsPdfReportRenderer pdf,
    IExportArtifactStore artifacts,
    IEventRepository events,
    ITenantContext tenantContext,
    AnalyticsExportOptions options,
    TimeProvider clock,
    ILogger<AnalyticsExportService> logger) : IAnalyticsExportService
{
    public async Task<ExportResult> RenderAsync(
        Guid tenantId, Guid? userId, AnalyticsExportSpec spec, CancellationToken ct)
    {
        // 1. fetch via the right 36-3/4/5 query service for spec.Type
        // 2. render CSV or PDF from that one result
        // 3. persist artifact, emit DATA.EXPORT.COMPLETED
    }
}
```

> The 36-3/36-4/36-5 query services are **forward dependencies** — at authoring time only
> Stories 36-1/36-2 exist. If a query service is not yet merged, this story is blocked on it; the
> abstraction names above are the contract this story consumes (confirm the exact interface name +
> DTO shape against the merged 36-3/4/5 before coding, and update the column-order golden test to
> match the merged DTO).

### Sync vs async threshold (AC1, AC6)

```
estimate rows for (type, from, to, groupBy)         # cheap COUNT/bucket estimate via query service
  ≤ AnalyticsExportOptions.SyncRowThreshold (50_000)
  AND (to - from) ≤ SyncRangeDays (92)
    → render inline, return 200 file
  else
    → enqueue QueuedTask { Type="analytics.export", TenantId, Payload = {spec, userId} }
    → emit DATA.EXPORT.REQUESTED, return 202 { jobId }
```

The async queue is the **tenant-scoped `QueuedTask`** (`TenantId` non-null) processed by the
existing `TaskQueueProcessor` — *not* `PlatformQueuedTask` (which is CP/pre-routing only, for
work with no tenant context yet). The `AnalyticsExportTaskHandler` runs on the processor thread,
renders the same way the sync path does, writes the artifact, and emits COMPLETED/FAILED. On
re-delivery of an already-completed job id the handler is a no-op (idempotent).

### Signed download (AC7)

```csharp
// mint (on GET {jobId} when completed):  expiresAt = now + options.DownloadTtl (default 1h)
sig = Base64Url(HMACSHA256(serverSecret, $"{tenantId:N}:{jobId:N}:{expiresAt:o}"))
url = $"/api/v1/orgs/{tenantId}/analytics/exports/{jobId}/download?expires={expiresAt:o}&sig={sig}"

// verify (on GET …/download):
//   recompute sig, CryptographicOperations.FixedTimeEquals, reject 403 on mismatch
//   reject 403 if expiresAt < now
//   load artifact WHERE Id = jobId  (tenant TenantDbContext already scopes the schema)
//   stream bytes with the artifact's content type + attachment disposition
```

`RequireTenantMembershipFilter` still guards the download route (membership is required even with
a valid signature — the signature is a second factor binding the URL to the job, not a bypass).
Tenant A's signed URL can never resolve tenant B's file: the route tenant must match the caller's
membership AND the artifact lives in that tenant's schema.

### `analytics_exports` entity (tenant schema)

```csharp
public class AnalyticsExport
{
    public Guid Id { get; set; }                 // == jobId
    public string Type { get; set; } = null!;    // usage | cost | agents
    public string Format { get; set; } = null!;  // csv | pdf
    public string Spec { get; set; } = "{}";     // validated AnalyticsExportSpec JSON
    public Guid? RequestedBy { get; set; }        // userId (single-user: sole user; SaaS: member)
    public string Status { get; set; } = "queued"; // queued | running | completed | failed
    public string? Error { get; set; }
    public long RowCount { get; set; }
    public long ByteSize { get; set; }
    public byte[]? ContentBytes { get; set; }     // default artifact store (bytea); null when offloaded
    public string ContentType { get; set; } = "text/csv";
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

No `TenantId` column — tenancy is the per-tenant schema (Doc 01 §1.4 target shape, exactly as
Story 36-1's fact tables; `ApplyTenantFilter` no-op). Index `(Status, ExpiresAt)` serves the
retention sweep.

### DCB audit events (AC5) — feed Epic 37

```
DATA.EXPORT.REQUESTED   on accept (sync render start OR async enqueue)
DATA.EXPORT.COMPLETED   on rendered artifact (inline or async)
DATA.EXPORT.FAILED      on render failure
```

Appended via `IEventRepository.AppendAsync(new DomainEvent { Type=…, TenantId=tenantId, Tags=… })`.
Because `TenantId` is set, `EventRepository` routes the row into the **tenant's own**
`domain_events` table (Story 28-1 PR D) — which is exactly where a tenant's audit trail belongs
and what Epic 37 reads. Tags: `tenantId`, `userId`, `type`, `format`; completion data adds
`jobId`, `rowCount`, `bytes`, `durationMs`. Emission is best-effort (wrapped, never throws to the
caller) per the `PromptEventsService` / `AlertEndpoints.TryEmitAsync` precedent.

### API shape

| Endpoint | Method | Auth | Behaviour |
|---|---|---|---|
| `/api/v1/orgs/{tenantId}/analytics/exports` | POST | `MemberAccess` + membership filter | sync `200` file or async `202 {jobId}` |
| `/api/v1/orgs/{tenantId}/analytics/exports/{jobId}` | GET | `MemberAccess` + membership filter | job status + signed download URL when completed |
| `/api/v1/orgs/{tenantId}/analytics/exports/{jobId}/download` | GET | `MemberAccess` + membership filter + valid `sig` | streams the artifact |

Mounted on the existing `var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")`
group, each route `.AddEndpointFilter<RequireTenantMembershipFilter>()` — the identical wiring the
Story 36-3 query endpoints (and the alerts tenant surface) use.

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns an export? | The sole user — it lives in their (only) tenant schema. | The tenant — its `t_<hex>` schema; `requestedBy` records the member. |
| Who may request/download? | The user (authentication only). | Any tenant member (`MemberAccess` + membership) — an export is a read of data they can already see. |
| Isolation plane | Search-path schema + connection string. | Same — physically separate schema per tenant; artifact + signed URL hard-scoped to the path tenant. |
| Cross-tenant export/download | N/A (one tenant). | 403 on request; 403/404 on cross-tenant job/download lookup; tenant-A signature rejected on tenant-B. |
| Audit event residency | Tenant's own `domain_events` (the user's). | Tenant's own `domain_events`. |

Mode does not change the endpoint shape — the membership filter + per-tenant `TenantDbContext`
resolve scope, per the CLAUDE.md prompt-store / Story 36-3 precedent.

## Dependencies

**Prerequisite (internal):**
- **Story 36-1** — the per-tenant `analytics_usage_*` fact tables the query services read. (Drafted.)
- **Story 36-2** — populates those tables; nothing to export until it runs. (Drafted.)
- **Story 36-3 (usage query service)**, **Story 36-4 (cost query service)**, **Story 36-5 (agent
  performance query service)** — the query abstractions + DTOs this story renders. **The CSV
  column order and the PDF tables are defined by these DTOs.** *(NEW — not yet authored at the
  time of writing; this story consumes their contracts and is blocked until they merge.)*
- **Epic 28** — per-tenant schema, `ITenantDbContextFactory`, `EfTenantDbMigrator` (the
  `analytics_exports` migration rides the Tenant graph); `QueuedTask` + `TaskQueueProcessor` +
  `ITaskHandler` for the async path. (Merged.)
- **Epic 4 (DCB `DomainEvent`)** + `IEventRepository.AppendAsync` (tenant-routed) for the audit
  events. (In place.)

**Blocks / feeds:**
- **Epic 37 (audit & compliance)** — consumes the `DATA.EXPORT.*` events this story emits. *(NEW —
  planned; the event-type contract above is the integration point.)*
- **Story 36-9 (scheduled reports)** — reuses `AnalyticsExportService` + the renderers to produce
  the recurring artifact; this story keeps the render path injectable so 36-9 calls it headless.

**External:**
- A server-side PDF library — **research the current best option before coding** (QuestPDF license
  terms / PuppeteerSharp HTML-to-PDF); record the choice in `.dev/decisions/`. **No PDF or CSV
  library is in the solution today — both are NEW package additions.**
- PostgreSQL 17 (the `analytics_exports` table + per-tenant isolation).
- EF Core 9 / Npgsql; Testcontainers + Docker for the isolation/migration suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — CSV golden (`AnalyticsCsvWriterTests`):** for each `type` (usage/cost/agents) assert
   the exact header row (column names + order match the 36-3/4/5 DTO), one row per dimension-tuple
   per bucket, raw unrounded measures, empty cell for a NULL dimension (never `"unknown"`), UTF-8
   BOM present, header-only output on an empty result.

2. **Unit — PDF smoke (`AnalyticsPdfReportRendererTests`):** rendered bytes start with `%PDF-`,
   are non-trivial in size, and the report contains the summary + at least one breakdown table for
   a populated spec; the empty-result path produces a valid "no data" PDF.

3. **Unit — service (`AnalyticsExportServiceTests`):** calls the correct query service per `type`
   (mocked 36-3/4/5); renders to the requested format; persists an artifact; emits
   `DATA.EXPORT.REQUESTED` + `…COMPLETED` with the documented tags; the threshold estimator routes
   small→inline / large→enqueue; a renderer throw flips the job `failed` + emits `…FAILED`.

4. **Unit — async handler (`AnalyticsExportTaskHandlerTests`):** a `QueuedTask` payload renders +
   persists + emits COMPLETED; a second delivery of a completed job id is a no-op (idempotent); a
   render failure marks `failed` + emits FAILED (the processor's retry budget applies).

5. **Unit — signer (`ExportDownloadSignerTests`):** sign→verify round-trips; a tampered `sig`,
   a tampered `jobId`/`tenantId`, and an expired `expiresAt` each reject; constant-time compare.

6. **Endpoint — RBAC / tenant isolation (`AnalyticsExportEndpointsTests`, WebApplicationFactory):**
   unauthenticated → 401; non-member of `{tenantId}` → 403; member small range → 200 file with
   `Content-Disposition: attachment`; member large range → 202 `{jobId}`; GET `{jobId}` →
   status + signed URL; download with a valid sig → bytes; cross-tenant export request / job
   lookup / download → 403/404; a signed URL minted for tenant A → 403 on tenant B.

7. **Integration — per-tenant isolation (`AnalyticsExportMigrationTests`, Postgres 17
   Testcontainer, per `SchemaPerTenantMigrationTests`):** two tenant schemas each carry
   `analytics_exports`; an export row written to schema A is invisible through schema B's context;
   `has-pending-model-changes -c TenantDbContext` is clean after the migration.

**Mocks:** the 36-3/36-4/36-5 query services are mocked for render/threshold/audit unit tests (no
raw-event aggregation in this story). InMemory provider for service/handler shape; a real
Postgres 17 Testcontainer for the `analytics_exports` isolation + migration (EF InMemory does not
honour search-path isolation — same rationale as `ConventionStoreMigrationTests`). No external
provider / Stripe calls.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsExport.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (configure `AnalyticsExport` in tenant graph) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsExports.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsExports.Designer.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/TenantDbContextModelSnapshot.cs` | Modify (regenerated) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AnalyticsExportEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsExportService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IAnalyticsExportService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsExportSpec.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsExportOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsExportEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/Render/AnalyticsCsvWriter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/Render/AnalyticsPdfReportRenderer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IExportArtifactStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/DbExportArtifactStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/ExportDownloadSigner.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/Handlers/AnalyticsExportTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AnalyticsExportServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoints, register services + handler) |
| `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` | Modify (add CSV/PDF package refs) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsCsvWriterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsPdfReportRendererTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsExportServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsExportTaskHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsExportEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/ExportDownloadSignerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsExportMigrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, exports, tenancy,
   task queue, PDF).
3. Reviewed Story 36-1/36-2 (the fact tables + projection) and the **merged** Story 36-3/36-4/36-5
   query services — **the CSV column order and the PDF tables are defined by their DTOs; pin them
   before writing the golden test.**
4. Reviewed the tenant-scope endpoint precedent (`AlertEndpoints` tenant surface +
   `RequireTenantMembershipFilter`) and the `QueuedTask`/`TaskQueueProcessor`/`ITaskHandler`
   async-job precedent.
5. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
6. Planned the TDD cycle (CSV golden + signer + service tests red first, then the renderers +
   endpoints).

### Key Design Decisions

- **Read the query services, never the raw stream (AC11).** The export must show the same numbers
  the dashboard shows. Re-aggregating `DomainEvent`/`ProviderDiagnostic` here would fork the math
  and silently diverge the CSV from the chart. The single source of truth is the 36-3/4/5 query
  services; a structure test pins that dependency direction.
- **Tenant-scoped `QueuedTask`, not `PlatformQueuedTask`.** The async export belongs to a resolved
  tenant, so it rides the per-tenant `QueuedTask` queue (`TenantId` non-null) the
  `TaskQueueProcessor` already fans out across tenants. `PlatformQueuedTask` is for pre-routing /
  no-tenant-yet work (installation routing) — wrong queue for a tenant export.
- **Whole-bucket render, idempotent job.** The async handler recomputes the entire export from the
  query services each run, so a re-delivery (processor retry / visibility-timeout reap) of a
  completed job is a safe no-op — no partial artifacts, no double-charged work.
- **Signed URL = second factor, not a bypass.** Membership is still required on the download route;
  the HMAC signature binds the URL to `(tenant, job, expiry)` so a leaked link can't be replayed
  past expiry and can't cross tenants. Constant-time compare; secret from config (never logged).
- **Artifact bytes behind a seam.** `IExportArtifactStore` defaults to `bytea` on the tenant row
  (no new infra, no migration anxiety per CLAUDE.md), but the seam lets a future S3/blob backend
  drop in without touching the endpoint or the signer. Keep artifacts small via the size bound +
  the 7-day TTL retention sweep.
- **Audit lands in the tenant's own event store.** Setting `DomainEvent.TenantId` routes the
  `DATA.EXPORT.*` event into the tenant schema's `domain_events` via `EventRepository` — exactly
  where a tenant's audit trail lives and what Epic 37 will read. Best-effort emission never blocks
  the export.
- **PDF library is a real decision.** No PDF lib exists in the solution; research current options
  (QuestPDF Community-license eligibility for this project vs PuppeteerSharp HTML-to-PDF reusing
  dashboard chart markup) and record it in `.dev/decisions/` before adding the package.

### Export boundary

This story creates **no** analytics aggregation logic (it reads 36-3/4/5), **no** schema change to
the Story 36-1 fact tables (only the additive `analytics_exports` table), **no** scheduled/recurring
delivery (36-9), and **no** owner business-analytics export. Any PR that adds aggregation or alters
the fact-table schema under cover of this story is out of scope — keep the diff to the export
service, renderers, artifact store, signer, async handler, endpoints, the `analytics_exports`
entity/migration, and tests.

## Logging Requirements

- **INFO**: export requested (`tenantId`, `userId`, type, format, range, sync|async); export
  completed (`jobId`, rowCount, bytes, durationMs); async job claimed/completed; retention sweep
  completed (rowsDeleted).
- **DEBUG**: threshold estimate (estimatedRows, decision); CSV/PDF render start; signed-URL minted
  (`jobId`, expiresAt — never the secret); query-service call (`type`, range).
- **WARN**: `DATA.EXPORT.*` audit emission failed (best-effort, swallowed); download rejected
  (expired/tampered signature) — `jobId`, reason (never the signature value); large export over the
  async threshold.
- **ERROR**: export render failed (`jobId`, errorType — sanitized message, no raw stack to client);
  artifact persist/read failure; async handler exhausted the retry budget.
- **Structured context**: include `{ tenantId, jobId, type, format, rowCount, bytes, durationMs }`
  where applicable.
- **Credential safety**: NEVER log the download-signing secret, the signature value, tenant
  connection strings, or search-path schema secrets; sanitized render-failure messages only (no
  raw provider/connection detail) in `DATA.EXPORT.FAILED` data.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
