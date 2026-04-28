# Epic 5: Observability Dashboard & Documentation

**Status:** Partially Implemented (4/14 done; 3 in-progress; 5 drafted; 2 backlog)
**Stories:** 14 (5-1 through 5-10 with 5-9a..5-9e sub-stories)
**Tech Spec:** [tech-spec-epic-5.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-5/tech-spec-epic-5.md)

## Overview

Epic 5 makes Tamma **legible from the outside**. It takes the rich event stream from Epic 4, the cost signals from Epic 3/6, and the engine state from Epic 2, and surfaces them in three forms: **structured logs** (Pino JSON, one line per event, shipped to stdout and optionally to OpenSearch), **metrics** (Prometheus-format counters / gauges / histograms on a `/metrics` endpoint), and **real-time dashboards** (React SPA at `@tamma/dashboard` with health, velocity, event-trail, and alert views).

The epic's other half is **documentation**: installation guides, usage / configuration reference, API reference, a searchable documentation website, and an optional video walkthrough. Story 5-9 was split into 5-9a..5-9e as scope unfolded. Story 5-10 is the alpha-release preparation bundle that ties the whole epic together — doc site live, metrics endpoint live, alert channels configured, feedback loop wired.

The dashboard is already scaffolded at `@tamma/dashboard` (React 18 + Vite + Tailwind 4 + Zustand + React Router). The existing admin surface covers health, users, audit log, API keys, quick-links, tenants, and prompts. The observability-specific views (system health real-time, dev velocity, event-trail exploration, alerts, feedback capture) are the remaining work.

## Architecture

Logs flow through Pino. `@tamma/observability` wraps `pino.Logger` behind the `ILogger` interface defined in `@tamma/shared`, so every package logs through the same abstraction. A dual transport is configured: stdout for local/container runs, and `pino-elasticsearch` for OpenSearch when `OPENSEARCH_ENABLED=true` (Epic 15 turns this from optional to production default). `ILogger.child({ workflowInstanceId, issueNumber, sessionId })` creates scoped loggers that automatically bind context on every line.

Metrics are exposed on `GET /metrics` in the Fastify API. Counters (`issues_processed_total`, `prs_created_total`, `escalations_total`), gauges (`active_autonomous_loops`, `pending_approvals`, `queue_depth`), and histograms (`issue_completion_duration_seconds`, `ai_request_duration_seconds`) are maintained in-memory and scraped by Prometheus. Alertmanager rules fire on thresholds → notifications to Slack/email/webhook (Story 5-6).

Dashboards hit the event query API from Epic 4 (`GET /api/v1/events?...`), the metrics endpoint for real-time gauges, and the logs (via OpenSearch) for full-text search and timeline views. Dashboard state uses Zustand stores; React Query caches server data with 30s staleTime. Pages are code-split via React Router lazy imports.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `@tamma/observability` logger | `ILogger`-compatible Pino wrapper, OpenSearch transport support | `packages/observability/src/logger.ts` | Done (5-1) |
| Simple logger | Zero-dep logger for CLI paths | `packages/observability/src/simple-logger.ts` | Done |
| `/metrics` endpoint | Prometheus-format scrape target | `apps/tamma-elsa/src/Tamma.Api/Endpoints/Metrics` (planned) | Drafted (5-2) |
| Metrics collectors | Counters / gauges / histograms for loop, AI, gates | planned | Drafted (5-2) |
| `@tamma/dashboard` — base | React 18 + Vite + Tailwind 4 + Zustand + React Router shell | `packages/dashboard/src/index.tsx`, `router.tsx` | Done |
| Admin → Health tab | System health view | `packages/dashboard/src/pages/admin/HealthTab.tsx` | Done (5-3) |
| Admin → Audit Log tab | Event-trail browser | `packages/dashboard/src/pages/admin/AuditLogTab.tsx` | Done (partial 5-5) |
| Admin → API Keys tab | API key management | `packages/dashboard/src/pages/admin/ApiKeysTab.tsx` | Done |
| Admin → Users tab | User management | `packages/dashboard/src/pages/admin/UsersTab.tsx` | Done |
| Admin → Quick Links tab | Operator links | `packages/dashboard/src/pages/admin/QuickLinksTab.tsx` | Done |
| Admin → Tenants | Multi-tenant admin | `packages/dashboard/src/pages/admin/tenants/` | Done |
| Admin → Prompts | Prompt store UI | `packages/dashboard/src/pages/admin/prompts/` | Done |
| Knowledge-base pages | From Epic 6 | `packages/dashboard/src/pages/knowledge-base/` | Done |
| Organization / Account / Onboarding | User-facing pages | `packages/dashboard/src/pages/organization/`, `AccountPage.tsx`, `onboarding/` | Done |
| Dev velocity dashboard | PRs/day, cycle time, lead time, success rate | planned | Drafted (5-4) |
| Event-trail exploration UI | Full event timeline with filter / search / correlation | `AuditLogTab.tsx` (partial) + planned deep-link view | In progress (5-5) |
| Alert system | Threshold-based alerts → Slack/email/webhook | planned | In progress (5-6) |
| Feedback collection | In-app feedback form → events + GitHub issue sync | planned | In progress (5-7) |
| Integration test suite | End-to-end smoke across CLI + server + engine | `packages/cli/src/cli.integration.test.ts` + workflow smoke | Done (5-8) |
| Installation & setup docs | npm / Docker / binary install guides | [Wiki: Deployment](Deployment) | Drafted (5-9a) |
| Usage & config docs | CLI commands, config file, provider/platform setup | `CLAUDE.md`, [Wiki: Architecture](Architecture) | Drafted (5-9b) |
| API reference | REST + SSE + webhook reference | Backlog (5-9c) | Backlog |
| Documentation website | Searchable site | `apps/wiki-site/` — live at wiki.tamma.dev | In progress (5-9d) |
| Video walkthrough | 5-10 min demo | Backlog (5-9e) | Backlog |
| Alpha release prep | Release checklist + bundle | `.github/workflows/release.yml` | Done (5-10) |

## Class diagram

```
     ILogger  <<interface>>                     (packages/shared)
     + debug(msg, ctx?)
     + info(msg, ctx?)
     + warn(msg, ctx?)
     + error(msg, ctx?)
     + child(bindings) : ILogger
           ^
           |
     PinoLogger (packages/observability)
     - pinoInstance : pino.Logger
     - transport : pino-elasticsearch (optional)

     SimpleLogger (packages/observability)
     - minLevel : LogLevel
     - sink : console
     (used by CLI for zero-dep logging)


     Metrics (planned — Prometheus client)
     + counter(name, labels, help) : Counter
     + gauge(name, labels, help)   : Gauge
     + histogram(name, labels, help, buckets) : Histogram


     Dashboard  (packages/dashboard)
     +-- AdminLayout  ->  Health / AuditLog / ApiKeys / Users / Tenants / Prompts
     +-- KnowledgeBase pages (Epic 6 UI)
     +-- Organization / Account / Onboarding
     +-- Services layer   (fetch wrappers to /api/v1/*)
     +-- Stores (Zustand) (auth, tenants, ui)
     +-- Hooks            (data-fetch + real-time subscriptions)


     Data source -> View mapping
     /metrics         -----> Health tab (gauges, uptime, queue depth)
     /api/v1/events    -----> AuditLog tab, Event trail view, Velocity charts
     OpenSearch logs   -----> Log search view (Epic 15 extension)
     /api/v1/alerts    -----> Alert system view (5-6)


     Alert system (planned)
     AlertRule
     - metricName, operator (gt|lt|eq), threshold, windowSec
     - channels : [Slack | Email | Webhook]
     + evaluate(metric) : AlertEvent?
     AlertDispatcher
     + send(alertEvent)  --> Slack webhook / SMTP / custom HTTP
```

## Data flow — "operator sees a failing issue bubble up in the dashboard" sequence

```
TammaActivity       EventRepository    Pino Logger   Prometheus    Dashboard (React)   Operator browser
     |                    |                 |            |                 |                    |
     | activity fails     |                 |            |                 |                    |
     |------ emit END -->                   |            |                 |                    |
     |   { status: failed,                  |            |                 |                    |
     |     type: "CI.FAILED.ATTEMPT_3",     |            |                 |                    |
     |     tags: { issueNumber: 123 } }                  |                 |                    |
     |                    | INSERT row      |            |                 |                    |
     |                    |                                                                     |
     |------ log.error  ----------------- > | JSON line                                         |
     |                                       | to stdout / OpenSearch                           |
     |                                                                                          |
     |------ counter.inc ------------------> | counter escalations_total                        |
     |                                       | +1                                               |
     |                                                                                          |
     |------ emit ESCALATION.TRIGGERED ---> INSERT row                                          |
     |                                                                                          |
                                          |                                                    |
     (operator opens dashboard)                                                                 |
     |<-------------------------------- GET /dashboard/admin/audit-log --- browser  <---------- |
     |                                                                                          |
     | Dashboard loads                                                                          |
     |    React Query: fetch /api/v1/events?issueNumber=123                                     |
     |    -->  EventRepository.QueryAsync                                                       |
     |    <-- list of events chronologically                                                    |
     |    render timeline with status chips                                                     |
     |                                                                                          |
     | Dashboard polls /metrics every 30s                                                       |
     |    -->  GET /metrics                                                                     |
     |    <-- escalations_total 1, active_autonomous_loops 2, pending_approvals 1               |
     |    render gauges + sparkline                                                             |
     |                                                                                          |
     | Alert rule `escalations_total > 0 in 5m` -> AlertDispatcher -> Slack channel #tamma-ops  |
     |                                                                                          |
     | Operator sees alert + dashboard timeline showing 3 CI.FAILED.ATTEMPT events              |
     | clicks into issue 123, reads debug context, unblocks with `unblock` PR comment           |
```

## Use cases

- **On-call engineer** gets paged by **an escalation alert** that fired because `escalations_total > 0 in 5 min`: clicks Slack link → lands on dashboard event-trail pre-filtered to issue → reads the 3 retry attempts + LLM diagnoses → decides whether to fix or defer.
- **PM** wants **weekly velocity numbers**: opens Dev Velocity tab (5-4) → shows PRs merged / week, median cycle time, escalation rate, cost per merged PR. Data sourced from event query API.
- **Support engineer** wants **to triage a customer issue**: pastes correlation ID into event trail search → gets the full sequence of events for that workflow run → exports as HTML for the ticket.
- **Platform engineer** wants **to tune retry thresholds**: metrics dashboard shows `ci_retry_count` histogram P95 = 2 → safe to lower cap from 3 to 2 for certain projects.
- **First-time user** wants **to install Tamma on their laptop**: reads Installation docs (5-9a) → `npm i -g @tamma/cli` → runs `tamma init` → first-run wizard links to dashboard.
- **Compliance lead** wants **proof of audit-log completeness**: feedback form (5-7) surfaces any case where loop ran without events → integration tests (5-8) include "every activity emits start + end event" assertion.

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — `ILogger` in `@tamma/shared`; CLI entry to package + ship.
- [Epic 2](Epic-2-Autonomous-Loop.md) — the loop whose metrics are surfaced.
- [Epic 3](Epic-3-Quality-Gates.md) — gate outcomes + cost signals.
- [Epic 4](Epic-4-Event-Sourcing.md) — event query API is the dashboard's primary data source.

**Downstream:**
- [Epic 15](Epic-15-Log-Aggregation.md) — extends 5-1 logging with OpenSearch as the default.
- [Epic 23](Epic-23-System-Monitoring.md) — system-level monitoring builds on 5-2 + 5-6.
- [Epic 25](Epic-25-Wiki-Site.md) — the `apps/wiki-site/` that satisfies 5-9d.
- Dashboard pages for Epic 16 (auth admin), 17 (tenants), 27 (prompts) reuse the same shell.

## Current state

**Landed:**

- **Structured logging** (5-1) — `@tamma/observability` with `ILogger`, Pino, and optional OpenSearch transport via `pino-elasticsearch`.
- **System health dashboard** (5-3) — `HealthTab` exists with provider / tenant / API key health.
- **Integration testing suite** (5-8) — CLI integration tests + docker smoke workflow + deploy smoke.
- **Alpha release preparation** (5-10) — release workflow, binary installers, NPM publish all green.
- Dashboard shell — React 18 + Vite + Tailwind 4 + Zustand + React Router with admin, knowledge-base, organization, account, onboarding routes.
- Wiki site (5-9d) — `apps/wiki-site/` live at `wiki.tamma.dev`, React Router + react-markdown (but note: does NOT render mermaid natively, so wiki pages use ASCII diagrams — see `apps/wiki-site/src/components/MarkdownPage.tsx`).

**In progress:**

- 5-5 Event Trail Exploration UI — `AuditLogTab` partially implements; deep-link filter + correlation-ID search pending.
- 5-6 Alert System — rule schema + dispatcher designed; not yet wired to channels.
- 5-7 Feedback Collection — story brief exists; no UI yet.

**Drafted:**

- 5-2 Metrics Collection Infrastructure — Prometheus client choice pending (`prom-client` TS vs `prometheus-net` C#).
- 5-4 Dev Velocity Dashboard — depends on 5-2 + 5-5.
- 5-9a Installation docs, 5-9b Usage docs — first-draft content exists in [Deployment](Deployment) and [Architecture](Architecture); not yet consolidated as a single onboarding flow.

**Backlog:**

- 5-9c API reference — OpenAPI spec generation is planned but no endpoint renders schema yet.
- 5-9e Video walkthrough — nice-to-have, not blocking alpha.

**Drift from briefs:**

- The brief calls for a dedicated monitoring stack (Grafana + Prometheus + Alertmanager); current shipping model uses the in-app React dashboard for primary views and defers Grafana to operators who want to run it themselves.
- Wiki site (5-9d) evolved into its own epic (Epic 25) once it grew beyond simple markdown serving.
- Event-trail exploration (5-5) overlaps with Epic 4 Story 4-7 query API — 5-5 is the UI layer, 4-7 is the data layer.
- The dashboard has admin surfaces from Epics 11/16/17/27 already; from a consumer perspective those feel like "Epic 5" but technically belong to their owning epics.

## See also

- **Docs:** [docs/stories/epic-5/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-5) — all 14 story briefs including 5-9a..5-9e split.
- **Tech spec:** [tech-spec-epic-5.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-5/tech-spec-epic-5.md).
- **Related wiki pages:**
  - [Architecture](Architecture) — overall observability architecture.
  - [Epic 4: Event Sourcing](Epic-4-Event-Sourcing.md) — data source for dashboards.
  - [Epic 15: Log Aggregation](Epic-15-Log-Aggregation.md) — OpenSearch stack.
  - [Epic 23: System Monitoring](Epic-23-System-Monitoring.md) — host + container monitoring.
  - [Epic 25: Wiki Site](Epic-25-Wiki-Site.md) — documentation website.
- **Code paths:**
  - `packages/observability/src/logger.ts` — Pino logger.
  - `packages/dashboard/src/pages/` — dashboard React pages.
  - `apps/wiki-site/` — documentation website source.
  - `.github/workflows/release.yml` — release process.
  - `packages/cli/src/cli.integration.test.ts` — integration test suite.
