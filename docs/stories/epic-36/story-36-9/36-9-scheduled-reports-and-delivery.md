# Story 36-9: Scheduled Reports & Delivery

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin** (or, in single-user mode, the sole user),
I want to schedule recurring analytics reports (daily/weekly/monthly) that Tamma generates
server-side and emails to the recipients I choose,
So that my team receives usage / cost / agent-performance summaries automatically without anyone
having to open the dashboard and run an export by hand — plus an optional platform-owner business
digest (MRR/churn) delivered to operators only.

## Priority

P2 — automation convenience layered on the analytics product (Epic 36). Depends on the export
pipeline (36-8) and the email/notification infrastructure shipped with Story 5.6 / Epic 18.

## Architecture Context

> **Target stack:** C# / .NET 9 in `apps/tamma-elsa`. **The TypeScript `packages/api` tree is
> DELETED — do NOT add anything there.** All endpoints, services, entities, Elsa workflows, and
> tests for this story live under `apps/tamma-elsa/`.

This story is a thin orchestration layer over machinery that already exists:

| Capability | Where it already lives (verified 2026-06-17) | This story's use |
|---|---|---|
| **Recurring scheduler precedent** | `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — `BackgroundService` that wakes on a poll interval, checks a cron offset, takes a Postgres `pg_try_advisory_lock` leader lock, and dispatches an Elsa workflow. | Copy the shape for `ScheduledReportsScheduler` (multi-pod safe, failure-isolated, `Enabled` gate for tests). |
| **Recurring Elsa workflow precedent** | `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` (`WorkflowBase`, fan-out per tenant, per-item failure isolation). | Model `ScheduledReportsWorkflow` on it: select due reports, generate each, enqueue mail, emit terminal event. |
| **Export generation (36-8)** | `Tamma.Api/Services/Analytics/AnalyticsExportService.cs` — **NEW in Story 36-8** (CSV/PDF off the dimensional store). | The workflow asks this service to render each due report's artifact. **Hard dependency on 36-8.** |
| **Per-tenant email outbox** | `Tamma.Data/Entities/EmailOutboxMessage.cs` + `IEmailOutboxRepository.EnqueueAsync` (`Tamma.Data/Repositories/IEmailOutboxRepository.cs`). | Enqueue tenant report deliveries here. |
| **Platform email outbox** | `Tamma.Data/Entities/PlatformEmailOutboxMessage.cs` + `IPlatformEmailOutboxRepository`. | Enqueue the optional platform-owner business digest here (recipients live outside any tenant). |
| **Outbox delivery (the actual transport)** | `Tamma.Api/Services/Email/OutboxSmtpSender.cs` — a hosted service that **already drains BOTH the per-tenant `email_outbox` AND the CP `platform_email_outbox`** with retry/backoff and emits `EMAIL.SENT.SUCCESS`. | **Reuse as-is.** Once a row is enqueued, delivery (including retry) is free — this story writes NO new transport. |
| **Email templates** | `Tamma.Api/Services/Email/EmailTemplates.cs`. | Add a `scheduled-report` template (subject + html/text body wrapping the export). |
| **DCB events** | `IEventRepository.AppendAsync(DomainEvent)` (`Tamma.Data/Repositories/IEventRepository.cs`). | Emit `ANALYTICS.REPORT.GENERATED/SENT/FAILED`. |
| **Per-mode XOR / dedup precedent** | `prompt_overrides` — `ck_prompt_overrides_principal_xor` CHECK + `NULLS NOT DISTINCT` unique (`Tamma.Data/TammaModelConfiguration.cs` ~714). | Mirror exactly for `scheduled_reports` (user_id XOR tenant_id) and the run-idempotency unique. |
| **Tenant RBAC (owner/admin gate; member → 403)** | `ConventionManage` policy on `ConventionStoreEndpoints.cs` (~248); tenant-scoped route shape `/api/conventions`. | Reuse the same policy / scope-resolution for the report-CRUD endpoints. |
| **Async/queued task fallback** | `Tamma.Data/Entities/QueuedTask.cs` + `Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs` (Epic 28). | NOT required for v1 — the scheduler runs generation inline on its own background thread (cadence is hourly-coarse). The queue is the escape hatch if a single report ever needs to be re-run on demand (see Dev Notes). |

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a **report schedule**? | The sole user — `user_id`-keyed, `tenant_id` NULL (same XOR as `prompt_overrides`). | The tenant — `tenant_id`-keyed, `user_id` NULL. Owned by `tenant_owner`/`tenant_admin`; `member` gets read-only. |
| Who can create/edit/delete? | The user (no role check). | `tenant_owner`/`tenant_admin` only — `member` → **403** (mirrors prompt-store PUT/DELETE RBAC). |
| Where do generated reports deliver? | Per-tenant `email_outbox` keyed to the sole user's tenant context. | Per-tenant `email_outbox` (`TenantId` set) — drained per-tenant by `OutboxSmtpSender`. |
| Who owns the **platform business digest**? | N/A — single-user has no platform-owner separation; the digest is a SaaS/operator feature. Skip in single-user mode. | Platform owner ONLY (`OwnerAccess`). Delivered via the **platform** outbox; aggregates only — **never** per-tenant detail. |
| Mode source | `ITammaModeProvider` (process-stable). | same |

## Acceptance Criteria

1. A `ScheduledReport` entity persists, per mode (`user_id` XOR `tenant_id`, enforced by a
   `ck_scheduled_reports_principal_xor` CHECK mirroring `prompt_overrides`): `cadence`
   (`daily|weekly|monthly`), `report_type` (`usage|cost|agents`), `format` (`csv|pdf`), `recipients`
   (text[] of RFC-5322 addresses), `dimensions` / `group_by`, `enabled`, `time_zone`,
   `last_run_at`, plus standard timestamps. CHECK constraints pin `cadence`/`report_type`/`format`
   to closed enums.
2. CRUD is exposed at `POST/GET/PATCH/DELETE /api/v1/orgs/{tenantId}/analytics/reports`
   (+ `GET .../reports/{id}`); the route shape is identical between modes and the auth middleware
   decides which principal key (`user_id` / `tenant_id`) the row carries.
3. **RBAC**: create / update / delete are restricted to `tenant_owner` / `tenant_admin` in SaaS
   mode (a `member` caller gets **403** before the handler body runs, using the same policy gate as
   `ConventionStoreEndpoints` upsert/delete); in single-user mode there is no role check (sole user
   owns everything). `GET` is allowed for any tenant member / the sole user.
4. All report endpoints are **hard tenant-scoped** (same guard as 36-3): a caller may only read or
   mutate schedules for a tenant they belong to; a cross-tenant access attempt returns **403/404**
   (never the other tenant's rows).
5. A `ScheduledReportsScheduler` (`BackgroundService`, modelled on `HourlyAnalyticsRollupScheduler`)
   wakes on a configurable poll interval, is multi-pod safe via a Postgres advisory leader lock, has
   an `Enabled` gate (default true; tests/non-Elsa hosts set false), and dispatches the
   `ScheduledReportsWorkflow` once per scheduling tick.
6. A `ScheduledReportsWorkflow` (Elsa `WorkflowBase`) selects all reports **due** for the current
   tick (cadence + `time_zone` + `last_run_at` determine due-ness), generates each due report's
   artifact via `AnalyticsExportService` (36-8), and enqueues delivery through the per-tenant
   `IEmailOutboxRepository` (`EmailOutboxMessage` with the report artifact as attachment/body).
7. Report runs are **idempotent per `(report_id, period_key)`**: a scheduler restart, duplicate
   dispatch, or workflow replay must NOT generate or send a report twice for the same period. A
   `scheduled_report_runs` ledger row with a `NULLS NOT DISTINCT` unique on `(report_id, period_key)`
   is the dedup gate; a replay test covers this.
8. Each run emits DCB events `ANALYTICS.REPORT.GENERATED`, `ANALYTICS.REPORT.SENT`, and
   `ANALYTICS.REPORT.FAILED`, tagged with `tenantId` (or the single-user principal), `reportId`,
   `reportType`, `format`, and `periodKey`, via `IEventRepository.AppendAsync` for audit.
9. Failure isolation: a single report's generation/enqueue failure emits `ANALYTICS.REPORT.FAILED`,
   marks that run failed, and **does not block** other tenants' / other reports' runs in the same
   tick (per-item try/catch, mirroring `FanOutTenantRollupsActivity`). The run is retried on the
   next due tick (no tight retry loop).
10. **Email delivery is reused, not rebuilt**: the workflow only enqueues rows on the existing
    outbox; `OutboxSmtpSender` performs the actual send, retry/backoff, and `EMAIL.SENT.SUCCESS`
    emission. A new `scheduled-report` email template renders subject + body wrapping the generated
    export.
11. An **optional platform-owner business digest** (MRR / churn from Story 36-10) is delivered to
    platform-owner recipients via the **platform** outbox (`IPlatformEmailOutboxRepository`),
    contains **only aggregates** (never per-tenant detail), and is gated to platform owner
    (`OwnerAccess`). It is skipped entirely in single-user mode and when 36-10 is unavailable.
12. Recipient addresses are validated (RFC-5322) on write; an empty recipient list is rejected; the
    recipient/subject/body of a queued report email are **never** written to logs or DCB event data
    (the outbox already enforces this — preserve it).
13. Unit + integration tests cover: schedule CRUD RBAC per mode (owner/admin/member/cross-tenant),
    cadence due-selection (daily/weekly/monthly with `time_zone`), idempotent generation
    (replay/restart → one run), email-outbox enqueue (correct table per scope), failure isolation
    (one report failing leaves others delivered), and audit-event emission.

## Technical Design

### Data model

```
ScheduledReport (per-mode key: user_id XOR tenant_id, mirroring prompt_overrides)
  id            uuid pk
  user_id       uuid   -- set in single-user mode; NULL in SaaS
  tenant_id     uuid   -- set in SaaS mode;        NULL in single-user
  cadence       text   -- 'daily' | 'weekly' | 'monthly'
  report_type   text   -- 'usage' | 'cost' | 'agents'
  format        text   -- 'csv' | 'pdf'
  recipients    text[] -- RFC-5322 addresses (>= 1)
  group_by      text   -- dimension key passed to AnalyticsExportService
  time_zone     text   -- IANA tz; default 'UTC' (drives "midnight of the day/week/month")
  enabled       boolean default true
  last_run_at   timestamptz null
  created_at    timestamptz default now()
  updated_at    timestamptz default now()
  -- CK_scheduled_reports_principal_xor : exactly one of user_id / tenant_id
  -- CK_scheduled_reports_cadence / _report_type / _format : closed enums
  -- UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, report_type, cadence, format, group_by)

ScheduledReportRun (idempotency ledger + audit)
  id            uuid pk
  report_id     uuid fk -> scheduled_reports
  period_key    text     -- canonical bucket id, e.g. '2026-06-16' (daily),
                         --   '2026-W24' (weekly), '2026-06' (monthly)
  status        text     -- 'generated' | 'sent' | 'failed'
  outbox_msg_id uuid null -- the EmailOutboxMessage / PlatformEmailOutboxMessage id
  error         text null
  created_at    timestamptz default now()
  -- UNIQUE NULLS NOT DISTINCT (report_id, period_key)  <- the dedup gate (AC7)
```

Both DbSets + EF model config go in `ControlPlaneDbContext` / `TammaModelConfiguration.cs` only
(the single source of truth). Additive EF migration under
`src/Tamma.Data/Migrations/ControlPlane/` (normal `dotnet ef migrations add` — new tables, not a
baseline CHECK edit). Verify `has-pending-model-changes` reports none after generating it.

### Components

```
src/Tamma.Data/Entities/ScheduledReport.cs                              (new)
src/Tamma.Data/Entities/ScheduledReportRun.cs                           (new)
src/Tamma.Data/Repositories/IScheduledReportRepository.cs              (new)
src/Tamma.Data/Repositories/ScheduledReportRepository.cs              (new — CRUD + due-select + run-ledger upsert)
src/Tamma.Api/Endpoints/ScheduledReportEndpoints.cs                    (new — CRUD, per-mode RBAC)
src/Tamma.Api/Services/Analytics/ScheduledReportEventTypes.cs         (new — ANALYTICS.REPORT.*)
src/Tamma.Api/Services/Analytics/ScheduledReportCadence.cs            (new — due-selection + period_key math, PURE/testable)
src/Tamma.Activities/Analytics/GenerateScheduledReportActivity.cs     (new — render via AnalyticsExportService + enqueue outbox)
src/Tamma.ElsaServer/Workflows/ScheduledReportsWorkflow.cs            (new — select due, fan out, terminal event)
src/Tamma.ElsaServer/Workflows/ScheduledReportsScheduler.cs           (new — BackgroundService, advisory leader lock)
```

### Due-selection + idempotency (the load-bearing logic)

`ScheduledReportCadence` is a pure static helper (no DB, fully unit-testable — `ConventionSeedSpecs`
style): given `(cadence, time_zone, last_run_at, now)` it returns `(isDue, periodKey)`.

- **daily**: due when `now` (in `time_zone`) has crossed a new calendar day vs `last_run_at`;
  `periodKey = "yyyy-MM-dd"` of the **completed** day.
- **weekly**: due on the configured week boundary (ISO week, Monday start); `periodKey = "yyyy-Www"`.
- **monthly**: due on the 1st; `periodKey = "yyyy-MM"`.

The `(report_id, period_key)` unique on `scheduled_report_runs` is the hard gate. The workflow does
**insert-if-absent**: a successful insert means "I own this period → generate + enqueue"; a unique
violation (concurrent pod / replay) means "already handled → skip silently." This makes a scheduler
restart or Elsa replay a no-op, satisfying AC7 — exactly the dedup posture
`HourlyAnalyticsRollupWorkflow` relies on via its UPSERT.

### Workflow shape (mirrors `HourlyAnalyticsRollupWorkflow`)

1. `SelectDueReports` — query all `enabled` schedules across the appropriate scope; filter via
   `ScheduledReportCadence.IsDue`.
2. Fan out → per report: `GenerateScheduledReportActivity`
   - `INSERT scheduled_report_runs (report_id, period_key, 'generated')` — on unique violation,
     skip (idempotent).
   - call `AnalyticsExportService.Generate(type, format, period-range, group_by, tenantId)` (36-8).
   - emit `ANALYTICS.REPORT.GENERATED`.
   - build an `EmailOutboxMessage` (template `scheduled-report`, the export as body/attachment) and
     `IEmailOutboxRepository.EnqueueAsync` → store `outbox_msg_id`, flip run → `sent`, emit
     `ANALYTICS.REPORT.SENT`.
   - **per-report try/catch**: on failure emit `ANALYTICS.REPORT.FAILED`, mark run `failed`,
     continue the loop (AC9).
3. Optional `GeneratePlatformDigest` step (SaaS only, 36-10 available) — same shape but uses
   `IPlatformAnalyticsService` (36-10) + `IPlatformEmailOutboxRepository`; aggregates only.
4. `EmitReportsTickCompleted` — terminal `ANALYTICS.REPORT.TICK_COMPLETED` with success/failure
   counts for the ops view.

### Scheduler (mirrors `HourlyAnalyticsRollupScheduler`)

- `BackgroundService` with `ScheduledReportsSchedulerOptions { Enabled (default true), PollInterval
  (default 5 min) }`.
- Postgres advisory leader lock keyed on the current scheduling tick so an N-pod deploy dispatches
  once (reuse the `IRollupSchedulerLeaderLock` / `PostgresAdvisoryLeaderLock` pattern — extract it
  to a shared helper if cheap, otherwise mirror it).
- Dispatch failure → WARN + continue (next tick is the recovery path).
- An `internal InvokeTickForTestsAsync` entry point so unit tests drive one tick without the loop.

### Endpoints (per-mode RBAC, mirrors `ConventionStoreEndpoints`)

```
GET    /api/v1/orgs/{tenantId}/analytics/reports          (member / sole user — list)
GET    /api/v1/orgs/{tenantId}/analytics/reports/{id}     (member / sole user)
POST   /api/v1/orgs/{tenantId}/analytics/reports          (tenant_owner|tenant_admin / sole user)
PATCH  /api/v1/orgs/{tenantId}/analytics/reports/{id}     (tenant_owner|tenant_admin / sole user)
DELETE /api/v1/orgs/{tenantId}/analytics/reports/{id}     (tenant_owner|tenant_admin / sole user)
```

Mutating routes carry the same authorization policy used by `ConventionStoreEndpoints` upsert/delete
(tenant_owner/tenant_admin; member → 403). Scope is resolved from `ITenantContext` + `ITammaModeProvider`
exactly as the prompt/convention stores do.

## Dependencies

- **Prerequisite (hard)**: **Story 36-8** (`AnalyticsExportService` + `AnalyticsExportEndpoints`) —
  this story's workflow calls 36-8's export service to render each report. Without 36-8 there is
  nothing to schedule.
- **Prerequisite**: **Story 5.6 notification infrastructure** + **Epic 18** (email outbox +
  `OutboxSmtpSender` + RBAC roles `tenant_owner`/`tenant_admin`/`member`). The outbox entities,
  repositories, the sender hosted service, and the role gates are all reused unchanged.
- **Prerequisite (analytics scope guard)**: **Story 36-3/36-4/36-5** (the per-tenant analytics query
  services + the hard tenant-scope guard reused by the report endpoints).
- **Optional**: **Story 36-10** (platform business analytics — MRR/churn) for the platform-owner
  digest (AC11). The digest step is feature-flagged off when 36-10 is absent.
- **Related**: Epic 27 (the per-mode override ownership + XOR CHECK + `NULLS NOT DISTINCT` pattern
  this story copies); Story 28-10 (`HourlyAnalyticsRollup*` — the recurring scheduler/workflow
  precedent).

## Testing Strategy

C# tests live under `apps/tamma-elsa/tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."`.

1. **Cadence unit tests** (`Tamma.Api.Tests/Analytics/ScheduledReportCadenceTests.cs`) — pure
   due-selection + period_key for daily/weekly/monthly across `time_zone` boundaries (DST edge,
   month rollover, ISO-week start); no DB.
2. **Repository tests** — CRUD; XOR CHECK rejects both-null/both-set; `NULLS NOT DISTINCT` unique
   blocks a duplicate schedule and a duplicate `(report_id, period_key)` run; due-select returns
   only enabled + due rows.
3. **Endpoint RBAC tests** (`ScheduledReportEndpointsTests.cs`) — matrix: owner/admin can CUD,
   member → 403 on CUD but 200 on GET, cross-tenant → 403/404, single-user (sole user) full access,
   recipient validation (empty list → 400, malformed address → 400).
4. **Workflow / activity tests** (`Tamma.Activities.Tests/Analytics/`) — a due report generates via
   a mocked `AnalyticsExportService`, enqueues exactly one `EmailOutboxMessage`, emits
   GENERATED+SENT; a generation failure emits FAILED + leaves a sibling report delivered (isolation);
   platform digest uses the platform outbox and contains aggregates only.
5. **Idempotency / replay test** (AC7) — dispatch the same tick twice (and re-run a workflow
   instance): exactly one run row, one enqueue, one GENERATED+SENT pair per `(report_id, period_key)`.
6. **Scheduler test** — `InvokeTickForTestsAsync` dispatches once; `Enabled=false` skips; leader-lock
   contention (second pod) does not double-dispatch.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/ScheduledReport.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ScheduledReportRun.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IScheduledReportRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/ScheduledReportRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config: XOR + enum CHECKs + unique) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_ScheduledReports.cs` | Create (additive migration) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ScheduledReportEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/ScheduledReportCadence.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/ScheduledReportEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Email/EmailTemplates.cs` | Modify (add `scheduled-report` template) |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/GenerateScheduledReportActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ScheduledReportsWorkflow.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ScheduledReportsScheduler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoints; register repo) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (register workflow + scheduler) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/ScheduledReportCadenceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/ScheduledReportRepositoryTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/ScheduledReportEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/GenerateScheduledReportActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/ScheduledReportsWorkflowTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (esp. Story 28-10 scheduler and
   Epic 27 per-mode ownership notes)
3. Read the verified precedents: `HourlyAnalyticsRollupScheduler.cs`, `HourlyAnalyticsRollupWorkflow.cs`,
   `OutboxSmtpSender.cs`, `ConventionStoreEndpoints.cs`, and the `prompt_overrides` block in
   `TammaModelConfiguration.cs`
4. Confirmed Story **36-8** has landed (`AnalyticsExportService` exists) — it is the hard prerequisite
5. Planned TDD (Red-Green-Refactor); write the cadence + idempotency tests first — they are the risky
   logic

### Delivery is reused — do NOT write a transport

The single most important design constraint: this story **enqueues** outbox rows and stops. The
existing `OutboxSmtpSender` hosted service already drains both the per-tenant `email_outbox` and the
CP `platform_email_outbox`, with retry/backoff and `EMAIL.SENT.SUCCESS` emission. AC10/AC11 forbid a
new sender. The `ANALYTICS.REPORT.SENT` event means "handed to the outbox," not "SMTP accepted";
final transport status lives in the `EMAIL.SENT.SUCCESS` / outbox `failed` row keyed by the
`outbox_msg_id` we store on the run ledger.

### Why a `BackgroundService` scheduler, not Elsa cron

Same reasoning as Story 28-10: a lightweight `BackgroundService` poll + advisory-lock leader election
is the established pattern in this repo and avoids pulling extra Elsa scheduling packages. Match
`HourlyAnalyticsRollupScheduler` so multi-pod safety and the `Enabled` test gate come for free.

### Idempotency is the spec's hard requirement (AC7)

Treat `(report_id, period_key)` as the dedup key end-to-end. Insert-if-absent on
`scheduled_report_runs` is the gate; everything downstream (generate, enqueue) only happens on a
fresh insert. A replay, a duplicate dispatch from two pods, or a process restart mid-tick must all
collapse to a single send. The replay test is non-negotiable.

### Platform digest is optional and aggregates-only (AC11)

The platform-owner business digest is SaaS-only, gated to `OwnerAccess`, sourced from 36-10's
`IPlatformAnalyticsService`, delivered via the **platform** outbox, and must never leak per-tenant
rows — only platform aggregates (MRR/churn totals). Feature-flag it off when 36-10 is absent so this
story can ship independently of 36-10.

### Migration discipline

The CP migration chain is collapsed to a single `InitialControlPlane` baseline (Phase 0). These are
**additive** tables, so a normal `dotnet ef migrations add` is correct — but still run
`dotnet ef migrations has-pending-model-changes` and confirm it reports none, and put all entity
config in `TammaModelConfiguration.cs` (the single source), not inline in the DbContext.

## Logging Requirements

- **INFO**: scheduler tick dispatched (tick id, leader=true), report generated (reportId, type,
  format, periodKey), report enqueued (reportId, periodKey, outboxMsgId — **id only**), tick
  completed (due count, sent, failed), platform digest enqueued.
- **DEBUG**: due-selection result per schedule (reportId, cadence, isDue, periodKey), workflow step
  entered.
- **WARN**: report run failed (reportId, periodKey, error class), scheduler dispatch failed (next
  tick is recovery), not-leader skip (another pod owns this tick), 36-10 unavailable → digest
  skipped.
- **ERROR**: repository write failure on the run ledger, export-service unrecoverable error.
- **Structured context**: include `{ tenantId, reportId, reportType, format, periodKey, outboxMsgId,
  dueCount }` where applicable.
- **Credential / PII safety**: NEVER log recipient addresses, email subjects, or report bodies — the
  outbox entities are CodeQL-tainted; reference only the `outbox_msg_id` (txn id). DCB event `data`
  carries metadata (reportId/type/format/periodKey/counts) only, never recipients or body.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
