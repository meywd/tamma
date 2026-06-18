# Story 36-9 — Scheduled Reports & Delivery (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. **Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.**

**Goal:** Let tenant owners/admins (and, in single-user mode, the sole user) configure recurring
analytics reports (daily/weekly/monthly) that Tamma generates server-side via the 36-8 export
pipeline and emails to chosen recipients through the existing outbox — plus an optional
platform-owner-only business digest (MRR/churn from 36-10). Schedules are stored per-mode
(`user_id` single-user / `tenant_id` SaaS), RBAC-gated for edit (owner/admin), executed by a
recurring Elsa scheduler, idempotent per period, and tenant-isolated.

**Story:** [`docs/stories/epic-36/story-36-9/36-9-scheduled-reports-and-delivery.md`](../../stories/epic-36/story-36-9/36-9-scheduled-reports-and-delivery.md)

**Tech stack:** .NET 9 / EF Core 9 / Npgsql + Elsa workflows in `apps/tamma-elsa`. Tests in
`apps/tamma-elsa/tests/` (xUnit; docker-bound suites run `sg docker -c "dotnet test ..."`).
**`packages/api` is DELETED — never add to it.**

---

## Non-goals (YAGNI guard)

- **NO new email transport.** `OutboxSmtpSender` already drains the per-tenant `email_outbox` AND the
  CP `platform_email_outbox` with retry/backoff + `EMAIL.SENT.SUCCESS`. This story only ENQUEUES.
- **NO new export engine.** Report bodies come from `AnalyticsExportService` (Story 36-8). If 36-8
  has not landed, this story is blocked — do not stub a parallel exporter.
- **NO Elsa cron-trigger package.** Follow Story 28-10's `BackgroundService` + advisory-lock pattern.
- **NO per-user override layer in SaaS** (per CLAUDE.md prompt-store precedent) — tenant schedules are
  owned by tenant_owner/tenant_admin; members read only.
- **NO synchronous on-demand "run now" endpoint in v1.** The scheduler + ledger cover recurring runs;
  a manual re-run can be a cheap follow-up via the `TaskQueueProcessor` escape hatch.
- **NO change to analytics query semantics or the 36-3 tenant-scope guard** — reuse them.
- **NO per-tenant detail in the platform digest** — aggregates only; SaaS-only; `OwnerAccess`.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Capability | Verified location | Use |
|---|---|---|
| Recurring scheduler | `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — `BackgroundService`, poll + cron offset, `pg_try_advisory_lock` leader lock (`IRollupSchedulerLeaderLock` / `PostgresAdvisoryLeaderLock`), `Enabled` gate, `InvokeTickForTestsAsync`. | Template for `ScheduledReportsScheduler`. |
| Recurring workflow | `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` — `WorkflowBase`, fan-out, per-item failure isolation, terminal `HOUR_COMPLETED` event. | Template for `ScheduledReportsWorkflow`. |
| Fan-out isolation | `src/Tamma.Activities/Analytics/FanOutTenantRollupsActivity.cs` (per-tenant try/catch, continue on failure). | Pattern for per-report isolation (AC9). |
| Event helper | `src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` (`AGGREGATE.ACTION.STATUS`, tag dict, `BuildEvent`). | Pattern for `ScheduledReportEventTypes` + DCB tags. |
| DCB append | `src/Tamma.Data/Repositories/IEventRepository.cs` (`AppendAsync`). | Emit `ANALYTICS.REPORT.*`. |
| Per-tenant outbox | `src/Tamma.Data/Entities/EmailOutboxMessage.cs` + `IEmailOutboxRepository.EnqueueAsync` (`src/Tamma.Data/Repositories/IEmailOutboxRepository.cs`). | Enqueue tenant report deliveries. |
| Platform outbox | `src/Tamma.Data/Entities/PlatformEmailOutboxMessage.cs` + `IPlatformEmailOutboxRepository`. | Enqueue platform digest. |
| Outbox sender (transport) | `src/Tamma.Api/Services/Email/OutboxSmtpSender.cs` — drains BOTH tables, retry/backoff, `EMAIL.SENT.SUCCESS`. | Reuse unchanged — delivery is free. |
| Email templates | `src/Tamma.Api/Services/Email/EmailTemplates.cs`. | Add `scheduled-report`. |
| Per-mode XOR / dedup | `prompt_overrides` in `src/Tamma.Data/TammaModelConfiguration.cs` ~714: `ck_prompt_overrides_principal_xor` + `.AreNullsDistinct(false)` unique. | Mirror for `scheduled_reports` + `scheduled_report_runs`. |
| Tenant RBAC (member→403) | `src/Tamma.Api/Endpoints/ConventionStoreEndpoints.cs` ~248 (`ConventionManage` policy, `TenantScope` via `ITenantContext` + `ITammaModeProvider`). | Mirror for report-CRUD mutating routes. |
| Export service (36-8) | `src/Tamma.Api/Services/Analytics/AnalyticsExportService.cs` — **NEW in 36-8**. | Hard dependency — call to render artifacts. |
| Migration baseline | `src/Tamma.Data/Migrations/ControlPlane/` collapsed to `InitialControlPlane`. | Additive `dotnet ef migrations add`; verify `has-pending-model-changes` = none. |

**Key gap closed for free:** because the outbox sender already drains both tables, the only delivery
work is enqueue. The only genuinely new logic is (a) due-selection/period math and (b) per-period
idempotency — both are pure and testable.

### Per-mode ownership (two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Schedule key | `user_id` set, `tenant_id` NULL | `tenant_id` set, `user_id` NULL |
| Create/edit/delete | sole user (no role check) | `tenant_owner`/`tenant_admin`; `member` → 403 |
| Read | sole user | any tenant member |
| Report delivery | per-tenant `email_outbox` | per-tenant `email_outbox` (`TenantId` set) |
| Platform digest | N/A (skip) | platform owner only, platform outbox, aggregates only |
| Mode source | `ITammaModeProvider` | same |

---

## Architecture

**Configure → schedule → due-select → generate (36-8) → enqueue (existing outbox) → audit.**

```
ScheduledReportEndpoints  ── CRUD (per-mode RBAC) ──▶  scheduled_reports (user_id XOR tenant_id)
                                                              │
ScheduledReportsScheduler (BackgroundService, advisory lock) ─┤ dispatches every PollInterval
                                                              ▼
ScheduledReportsWorkflow (Elsa)
   ├─ SelectDueReports        (ScheduledReportCadence.IsDue → periodKey)
   ├─ fan-out per report ▶ GenerateScheduledReportActivity
   │     ├─ INSERT scheduled_report_runs (report_id, period_key)  ── unique → idempotent gate
   │     ├─ AnalyticsExportService.Generate(...)        (36-8)
   │     ├─ emit ANALYTICS.REPORT.GENERATED
   │     ├─ IEmailOutboxRepository.EnqueueAsync(scheduled-report)  ── store outbox_msg_id
   │     └─ emit ANALYTICS.REPORT.SENT   (try/catch → ANALYTICS.REPORT.FAILED, continue)
   ├─ [SaaS + 36-10] GeneratePlatformDigest ▶ IPlatformEmailOutboxRepository (aggregates only)
   └─ EmitReportsTickCompleted (counts)

OutboxSmtpSender (UNCHANGED) drains email_outbox + platform_email_outbox → SMTP → EMAIL.SENT.SUCCESS
```

**Idempotency contract (AC7):** `(report_id, period_key)` unique on `scheduled_report_runs`.
Insert-if-absent owns the period; a unique violation = already handled = skip. Replay / restart /
multi-pod all collapse to one send.

---

## Task breakdown

### T1 — Entities + EF config + migration (foundation)

**Scope:** `ScheduledReport` + `ScheduledReportRun` entities; DbSets; `TammaModelConfiguration`
entity config (XOR CHECK, enum CHECKs, `NULLS NOT DISTINCT` uniques); additive migration. No
behaviour yet.

**Files:** new `src/Tamma.Data/Entities/ScheduledReport.cs`, `ScheduledReportRun.cs`; modify
`ControlPlaneDbContext.cs` (DbSets), `TammaModelConfiguration.cs` (config blocks mirroring
`prompt_overrides`); new migration under `Migrations/ControlPlane/`.

**Tests first:** `tests/Tamma.Api.Tests/Analytics/ScheduledReportRepositoryTests.cs` (with T2 repo)
asserts XOR rejects both-null/both-set, enum CHECKs reject bad values, unique blocks duplicate
schedule + duplicate `(report_id, period_key)`.

**Acceptance:**
- [ ] `ck_scheduled_reports_principal_xor` enforces exactly-one principal key.
- [ ] `cadence`/`report_type`/`format` CHECKs pin closed enums.
- [ ] `NULLS NOT DISTINCT` uniques on both tables behave (schedule de-dup + run de-dup).
- [ ] `dotnet ef migrations has-pending-model-changes` reports none; migration applies + rolls back.

### T2 — Repository (CRUD + due-select + run-ledger upsert)

**Scope:** `IScheduledReportRepository` / `ScheduledReportRepository` — create/get/list/update/delete
(scoped by principal), `ListEnabledDueAsync`, and `TryClaimRunAsync(reportId, periodKey)` returning
true only on a fresh insert (catch 23505 → false).

**Files:** new `src/Tamma.Data/Repositories/IScheduledReportRepository.cs`, `ScheduledReportRepository.cs`;
register in `Program.cs`.

**Tests first:** extend `ScheduledReportRepositoryTests` — CRUD round-trip; cross-principal isolation
(single-user row invisible to a tenant query and vice-versa); `TryClaimRunAsync` returns true once,
false on the second concurrent call.

**Acceptance:**
- [ ] `TryClaimRunAsync` is the single idempotency primitive (fresh insert = own period).
- [ ] List/get/update/delete are principal-scoped; no cross-tenant leakage.

### T3 — Cadence + period-key math (pure, the risky logic)

**Scope:** `ScheduledReportCadence` static helper: `(isDue, periodKey) Evaluate(cadence, timeZone,
lastRunAt, now)`. Daily/weekly(ISO,Mon)/monthly; period_key formats `yyyy-MM-dd` / `yyyy-Www` /
`yyyy-MM`; computes the export period range for 36-8.

**Files:** new `src/Tamma.Api/Services/Analytics/ScheduledReportCadence.cs`.

**Tests first:** `tests/Tamma.Api.Tests/Analytics/ScheduledReportCadenceTests.cs` — DST boundary,
month rollover, ISO-week start, never-run schedule, already-run-this-period (not due), time-zone
offsets. No DB.

**Acceptance:**
- [ ] Each cadence is due exactly once per period; `periodKey` is stable and matches the export range.
- [ ] Pure (no DB / no clock dependency beyond injected `now`).

### T4 — Event types + email template

**Scope:** `ScheduledReportEventTypes` (`ANALYTICS.REPORT.GENERATED|SENT|FAILED|TICK_COMPLETED`) +
tag-dict builder (mirror `AnalyticsRollupEvents.BuildEvent`); add `scheduled-report` template to
`EmailTemplates.cs` (subject + html/text wrapping the export; no recipient/body leakage).

**Files:** new `src/Tamma.Api/Services/Analytics/ScheduledReportEventTypes.cs`; modify
`src/Tamma.Api/Services/Email/EmailTemplates.cs`.

**Tests first:** `EmailTemplatesTests` extension — `scheduled-report` renders subject + both bodies;
event-type constants are stable.

**Acceptance:**
- [ ] Event names follow `AGGREGATE.ACTION.STATUS`; tags carry reportId/type/format/periodKey/tenantId.
- [ ] Template renders; no PII in event `data`.

### T5 — Generate activity (render via 36-8 + enqueue outbox + emit events)

**Scope:** `GenerateScheduledReportActivity` (Elsa `CodeActivity`): claim run (T2) → on fresh claim,
call `AnalyticsExportService.Generate` (36-8) → emit GENERATED → build `EmailOutboxMessage` (template
`scheduled-report`) → `EnqueueAsync` → store `outbox_msg_id`, flip run `sent`, emit SENT. Wrapped in
try/catch → FAILED + mark run failed + continue (AC9).

**Files:** new `src/Tamma.Activities/Analytics/GenerateScheduledReportActivity.cs`.

**Tests first:** `tests/Tamma.Activities.Tests/Analytics/GenerateScheduledReportActivityTests.cs` —
mocked export service: enqueues exactly one outbox row, emits GENERATED+SENT; already-claimed period
→ no-op (idempotent); export throws → FAILED emitted, run marked failed, no enqueue; recipients never
appear in event data.

**Acceptance:**
- [ ] One fresh period → one generate, one enqueue, GENERATED+SENT pair.
- [ ] Failure path emits FAILED and never throws out of the per-report loop.
- [ ] `ANALYTICS.REPORT.SENT` means "enqueued," not "SMTP delivered" (documented).

### T6 — Workflow (select due → fan out → optional digest → terminal event)

**Scope:** `ScheduledReportsWorkflow` (`WorkflowBase`, model on `HourlyAnalyticsRollupWorkflow`):
`SelectDueReports` → fan out `GenerateScheduledReportActivity` per due report (isolated) →
optional `GeneratePlatformDigest` (SaaS + 36-10) → `EmitReportsTickCompleted`.

**Files:** new `src/Tamma.ElsaServer/Workflows/ScheduledReportsWorkflow.cs`; register in
`src/Tamma.ElsaServer/Program.cs`.

**Tests first:** `tests/Tamma.Activities.Tests/Analytics/ScheduledReportsWorkflowTests.cs` — two due
reports, one throws → the other still delivered + TICK_COMPLETED counts (1 sent, 1 failed); replay
the instance → no duplicate sends (AC7); platform digest uses platform outbox + aggregates only;
digest skipped when single-user or 36-10 absent.

**Acceptance:**
- [ ] Per-report failure isolation holds (sibling delivered).
- [ ] Replay is a no-op (idempotent end-to-end).
- [ ] Platform digest gated SaaS + OwnerAccess + 36-10-available; aggregates only.

### T7 — Scheduler (BackgroundService + advisory leader lock)

**Scope:** `ScheduledReportsScheduler` modelled on `HourlyAnalyticsRollupScheduler`:
`ScheduledReportsSchedulerOptions { Enabled=true, PollInterval=5m }`, advisory leader lock per tick
(reuse `IRollupSchedulerLeaderLock` / `PostgresAdvisoryLeaderLock` — extract to shared helper if
cheap, else mirror), dispatch failure → WARN + continue, `InvokeTickForTestsAsync`.

**Files:** new `src/Tamma.ElsaServer/Workflows/ScheduledReportsScheduler.cs`; register in
`src/Tamma.ElsaServer/Program.cs`.

**Tests first:** scheduler test — `InvokeTickForTestsAsync` dispatches once; `Enabled=false` → no
dispatch; second pod (lock held) → no double dispatch.

**Acceptance:**
- [ ] Multi-pod safe (one dispatch per tick).
- [ ] `Enabled=false` gates the loop for unrelated tests.
- [ ] Dispatch failure does not kill the loop.

### T8 — Endpoints (CRUD, per-mode RBAC, tenant-scoped)

**Scope:** `ScheduledReportEndpoints` — `GET/GET{id}/POST/PATCH/DELETE
/api/v1/orgs/{tenantId}/analytics/reports`. Mutating routes gated by the tenant_owner/tenant_admin
policy (mirror `ConventionStoreEndpoints`); GET allowed for any member / sole user; hard tenant-scope
guard (36-3); recipient validation (RFC-5322, non-empty).

**Files:** new `src/Tamma.Api/Endpoints/ScheduledReportEndpoints.cs`; map in `src/Tamma.Api/Program.cs`.

**Tests first:** `tests/Tamma.Api.Tests/Analytics/ScheduledReportEndpointsTests.cs` — RBAC matrix
(owner/admin CUD; member 403 on CUD, 200 GET; cross-tenant 403/404; single-user full access);
recipient validation; identical route shape both modes.

**Acceptance:**
- [ ] Endpoint shape identical between modes; auth middleware picks principal key.
- [ ] Member → 403 on mutate, 200 on read; cross-tenant blocked.
- [ ] Empty/malformed recipients → 400.

---

## Task order & dependencies

T1 → T2 → T3 (parallel-safe with T4) → T4 → T5 → T6 → T7 → T8.
T1/T2/T3 are the foundation; T5 depends on **Story 36-8 having landed** (`AnalyticsExportService`).
T8 can be built any time after T2 but its tests need T1.

## Risks

- **36-8 not landed:** hard block — T5 calls `AnalyticsExportService`. Confirm 36-8 first; do not
  stub a parallel exporter (non-goal).
- **Idempotency is the spec's hard AC (AC7):** the `(report_id, period_key)` unique + insert-if-absent
  is load-bearing. A bug here double-sends emails to customers. The replay test (T6) is mandatory.
- **Email PII / CodeQL taint:** recipient/subject/body must never reach logs or DCB event `data` —
  reference `outbox_msg_id` only. The outbox entities are already CodeQL-tainted; preserve that.
- **Multi-pod double-dispatch:** mitigated by the advisory leader lock (T7) AND the per-period
  idempotency (T6) as belt-and-suspenders — keep both.
- **Time-zone / DST edge cases:** the cadence math (T3) is the subtle part; pin DST + month-rollover +
  ISO-week tests before wiring it into the workflow.
- **Platform digest leakage:** the digest must be aggregates-only and SaaS+OwnerAccess-gated;
  feature-flag it off when 36-10 is absent so 36-9 ships independently.
- **Migration discipline:** additive tables → normal `migrations add`, but verify
  `has-pending-model-changes` = none and keep config in `TammaModelConfiguration.cs` only.
- **Delivery semantics confusion:** `ANALYTICS.REPORT.SENT` = enqueued, not SMTP-accepted. Final
  delivery status lives in `EMAIL.SENT.SUCCESS` / the outbox `failed` row keyed by `outbox_msg_id`.
  Document this so ops don't misread the event.
