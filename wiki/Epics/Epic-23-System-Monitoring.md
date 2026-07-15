# Epic 23: System Monitoring & Observability Dashboard

**Status:** In build-out — 8/12 stories shipped 2026-07-04/05 (23-1..23-6, 23-8, 23-12); 23-7/23-9/23-10 still placeholder pages; 23-11's drafted Fastify plugin superseded by per-story C# endpoints
**Stories:** 12 (23-1 through 23-12)
**Task plans:** 26 detailed implementation breakdowns
**Packages:** `@tamma/dashboard` (`pages/monitoring/*`, `components/monitoring/*`, `hooks/monitoring/*`) + C# `Tamma.Api` endpoints (the drafted `@tamma/api` Fastify `routes/monitoring/*` plugin was not built)

## Overview

Epic 23 is production-grade monitoring for every surface Tamma owns: the HTTP API, every registered engine, every AI provider, every workflow, every infrastructure dependency (PostgreSQL, RabbitMQ, OpenSearch, ChromaDB, Nginx), every MCP server, the event store, the cost tracker, and the user/auth plane. Twelve stories, each with 2–3 task plans, spec the screens and their backing endpoints down to the last metric.

The epic is strictly **additive** — it reads from existing data sources (Pino structured logs, health-check endpoints, the DCB event stream, the `ICostTracker`, OpenSearch, `EngineRegistry`, `HealthService`, `DiagnosticsService`, and the KB analytics routes) and delivers the consolidated operator console at `app.tamma.dev/monitoring/*`. No new external dependencies are introduced.

The frontend is a new "Monitoring" section of the existing React dashboard (`packages/dashboard/src/pages/monitoring/*` with shared primitives in `components/monitoring/*` and hooks in `hooks/monitoring/*`) — this shipped as drafted. The backend did **not** ship as the drafted Fastify plugin: the pages instead compose existing `Tamma.Api` (C#) endpoints plus new per-story ones (`GET /api/v1/runs/summary` for the workflow monitor, deep provider-diagnostics aggregations on `ProviderEndpoints`, `GET /api/admin/monitoring/infrastructure` for infra metrics). Tenant-facing reads are tenant-scoped and fail closed on a null tenant; the infrastructure endpoint is platform-owner-only.

## Architecture

```
                                 app.tamma.dev
                                  (React SPA)
                                       │
                                       │ HTTP + SSE
                                       ▼
                 ┌─────────────────────────────────────────┐
                 │   packages/api/src/routes/monitoring/   │
                 │  overview-routes / metrics-routes / ... │
                 │  (registerMonitoringRoutes plugin)       │
                 └─────────────────────────────────────────┘
                                       │
                ┌──────────────────────┼──────────────────────┐
                │                      │                      │
    ┌───────────▼───────────┐  ┌───────▼───────┐   ┌──────────▼──────────┐
    │ MonitoringAggregator  │  │ MetricsCollector│   │ SseHelpers          │
    │  (5s TTL cache)       │  │ (1h sliding)   │   │  (heartbeat + drain)│
    └───────────┬───────────┘  └───────┬───────┘   └──────────┬──────────┘
                │                      │                      │
    ┌───────────┼──────────────────────┼──────────────────────┼─────────┐
    │           │                      │                      │         │
 ┌──▼──┐ ┌─────▼─────┐ ┌──▼──┐ ┌─────▼──────┐ ┌────▼────┐ ┌─▼────┐ ┌─▼────┐
 │Engine│ │Health     │ │Diag │ │Workflow    │ │Cost     │ │Event │ │OpenSearch│
 │Reg.  │ │Service    │ │Svc  │ │Store       │ │Tracker  │ │Store │ │(logs)   │
 └─────┘  └───────────┘ └─────┘ └────────────┘ └─────────┘ └──────┘ └────────┘
```

**Data-flow rules** (Story 23-11):

- **Real-time** — live engine state, live logs, live health → SSE with 15s heartbeat; `reply.raw.write()` backpressure drops events rather than buffering unboundedly.
- **Aggregate** — historical metrics, time-series charts, percentiles → REST with polling at 5s / 10s / 30s / 60s selectable intervals; `MonitoringAggregator` caches the combined snapshot for 5s to absorb thundering-herd refresh bursts.
- **Bounded memory** — `MetricsCollector` is a circular buffer sized for 1 hour of request metrics (~36k entries at 10 req/s).
- **Permission gate** — every route passes through `requirePermission('settings:view')`; no anonymous or member-tier access.

## Components

### Backend (`packages/api/src/routes/monitoring/` + `services/monitoring/`)

| Component | Responsibility |
|-----------|----------------|
| `registerMonitoringRoutes` plugin | Fastify plugin wiring all sub-routes under `/api/monitoring/*`. |
| `MonitoringAggregator` | Pull combined system snapshot (engines, health, workflows, cost) with 5s TTL cache. |
| `MetricsCollector` | `onResponse` hook gathers per-route count + error count + latency; computes p50/p95/p99 over 1h window. |
| `SseHelpers.createSSEStream()` | Sets SSE headers, returns `send(event, data)` + cleanup; heartbeat every 15s; backpressure aware. |
| `TimeBucketHelper` | Groups `{ts, value}[]` into 1min / 5min / 1h / 1d buckets with count, sum, min, max, avg, p50, p95, p99. |
| `overview-routes` | `GET /api/monitoring/overview` — combined snapshot. |
| `metrics-routes` | `GET /api/monitoring/metrics` + SSE stream at `/metrics/stream`. |
| `health-routes` | `GET /api/monitoring/health` — per-service status, uptime, response time, error count. |
| `agents-routes` | `GET /api/monitoring/agents` — per-role provider chain + cost + status + rate limits. |
| `events-routes` | `GET /api/monitoring/events` — event-store search/filter/export + replay. |
| `workflows-routes` | `GET /api/monitoring/workflows` — active/historical instances + Gantt. |
| `providers-routes` | `GET /api/monitoring/providers` — latency histograms + token analytics + error classification. |
| `logs-routes` | `GET /api/monitoring/logs` — OpenSearch-backed Lucene query + SSE tail. |
| `infrastructure-routes` | `GET /api/monitoring/infrastructure` — PostgreSQL / RabbitMQ / ChromaDB / OpenSearch / Docker metrics. |
| `knowledge-base-routes` | `GET /api/monitoring/knowledge-base` — vector DB health, embedding coverage, RAG freshness, MCP status. |
| `config-routes` | `GET /api/monitoring/config` — sources, diffs vs defaults, change history. |
| `security-routes` | `GET /api/monitoring/security` — logins, sessions, API-key usage, rate-limit violations. |

### Frontend (`packages/dashboard/src/pages/monitoring/` + `components/monitoring/`)

| Component | Responsibility |
|-----------|----------------|
| `MonitoringLayout` | Page shell: title, description, last-updated, auto-refresh toggle (off/5s/10s/30s/60s), time-range selector (1h/6h/24h/7d/30d/custom), SSE connection indicator. |
| `useMonitoringSSE` hook | EventSource with exponential-backoff reconnect (1s → 2s → 4s → max 30s). |
| `useAutoRefresh` hook | Polling loop that pauses via `document.visibilityState`. |
| `useTimeRange` hook | Presets + URL query-param persistence. |
| `StatusBadge`, `MetricCard`, `MetricGrid`, `TimeSeriesChart` (SVG, no chart lib), `DataTable`, `EmptyState`, `ErrorBanner`, `ProgressRing`, `LatencyBar` | Shared primitives used across all 10 screens. |
| `SystemHealthPage` | Service grid, dependency graph, error/request rate charts (23-1). |
| `AgentMonitorPage` | Per-role provider chains, cost, rate limits, API-key validation (23-2). |
| `EventStoreExplorerPage` | Search/filter/visualize/export + replay (23-3). |
| `ConfigurationAuditPage` | Source + diff-vs-defaults + change history (23-4). |
| `WorkflowMonitorPage` | Active/historical workflows, Gantt, queue depth (23-5). |
| `ProviderDiagnosticsPage` | Latency histograms, error classification, token analytics, model availability, cost comparison, call logs (23-6). |
| `LogExplorerPage` | Live tail, Lucene search, saved searches, alerts (23-7). |
| `InfrastructureMonitorPage` | PostgreSQL / RabbitMQ / ChromaDB / OpenSearch / Docker container metrics (23-8). |
| `KnowledgeBaseMonitorPage` | Vector DB, embeddings, index freshness, RAG + MCP (23-9). |
| `SecurityAuditPage` | Logins, sessions, API keys, rate limits, suspicious activity (23-10). |

## Class diagram

```
                   ┌──────────────────────────┐
                   │   MonitoringAggregator   │
                   │ getSystemOverview()      │
                   │ cache: Map<key, TTL>     │
                   └─────────────┬────────────┘
                                 │ depends on
         ┌───────────────────────┼────────────────────────┐
         │                       │                        │
 ┌───────▼───────┐     ┌─────────▼───────┐     ┌──────────▼──────────┐
 │EngineRegistry │     │ HealthService   │     │ DiagnosticsService  │
 │(existing)     │     │ (existing)      │     │ (existing, last 500)│
 └───────────────┘     └─────────────────┘     └─────────────────────┘

         ┌─────────────────────────────┐
         │ MetricsCollector            │
         │  onResponse hook            │
         │  ring buffer (1h, 36k slots)│
         │  getRequestMetrics()        │
         │  getErrorRate()             │
         │  getLatencyPercentiles()    │
         └─────────────────────────────┘

         ┌─────────────────────────────┐         ┌────────────────────┐
         │ SseHelpers                  │         │ TimeBucketHelper   │
         │  createSSEStream(reply, opt)│         │  bucket(points, w) │
         │   → { send(ev, data), close}│         │   → Bucket[]       │
         │  heartbeat(15s)             │         │  p50/p95/p99       │
         │  backpressure drain         │         └────────────────────┘
         └─────────────────────────────┘

 React side
 ┌───────────────┐   ┌────────────────┐   ┌────────────────┐
 │MonitoringLayout│◀─│useTimeRange    │   │useAutoRefresh  │
 │               │   │useMonitoringSSE│   │visibility-gated│
 └───────┬───────┘   └────────────────┘   └────────────────┘
         │ renders Outlet
         ▼
 ┌───────┼──────┬──────┬──────┬──────┬──────┬──────┬──────┬──────┬─────┐
 │SystemHealth  │Agent │Event │Config│Workfl│Provid│Log   │Infra │KB   │Security│
 │   Page       │Page  │Page  │Page  │Page  │Page  │Page  │Page  │Page │Page    │
 └──────────────┴──────┴──────┴──────┴──────┴──────┴──────┴──────┴─────┴────────┘
```

## Sequence diagram — system health overview

```
Admin browser          React Monitoring          Fastify /monitoring   HealthService  EngineRegistry  CostTracker
     │                        │                        │                    │              │              │
     │ GET /monitoring/health │                        │                    │              │              │
     │───────────────────────▶│                        │                    │              │              │
     │                        │ useAutoRefresh (10s)   │                    │              │              │
     │                        │ GET /api/monitoring/overview                │              │              │
     │                        │───────────────────────▶│                    │              │              │
     │                        │                        │ cache miss         │              │              │
     │                        │                        │ getSystemOverview()│              │              │
     │                        │                        │───────────────────▶│              │              │
     │                        │                        │                    │ checkAll()   │              │
     │                        │                        │                    │─────────────▶│              │
     │                        │                        │                    │◀─────────────│              │
     │                        │                        │                    │─────────────────────────────▶
     │                        │                        │                    │◀─────────────────────────────
     │                        │                        │◀───────────────────│  health + engines + costs    │
     │                        │                        │ cache(5s)          │                              │
     │                        │◀───────────────────────│                    │                              │
     │                        │ render grid + badges   │                    │                              │
     │                        │ SSE /monitoring/metrics/stream               │                              │
     │                        │◀══════════════════════════════════════════════════════════════════════════│ heartbeat
     │                        │ rolling latency + errors                                                    │
     │ live update            │                                                                             │
     │◀───────────────────────│                                                                             │
```

## Use cases

1. **On-call SRE opens `/monitoring/health` at 3 AM** and sees one red dot (OpenSearch degraded), clicks through to see the last 5 minutes of OpenSearch error logs via `LogExplorerPage`, confirms a transient connection pool issue, and resolves.
2. **Engineer investigates a failed workflow** by opening `/monitoring/events`, filtering by `issueId`, replaying the DCB event stream to reconstruct the exact 14-step flow, and exporting the bundle to attach to a bug report.
3. **Platform admin watches cost creep** on `/monitoring/agents`, sees that one provider chain spent $18 on retries in the last hour, drills into `/monitoring/providers` for the error classification, confirms a 429 cascade, and temporarily disables a problem model.
4. **Tenant admin audits security** on `/monitoring/security` — sees 12 failed logins on one user, confirms account was rate-limited, and triggers an email alert.
5. **Dev tunes prompt strategy** on `/monitoring/providers` latency histogram + token analytics — discovers that one role's p95 has doubled since the prompt edit landed, rolls back the prompt override (see Epic 27).
6. **Knowledge-base owner** opens `/monitoring/knowledge-base` to check vector DB health, confirms embedding coverage at 98%, spots one stale collection, triggers a reindex via the existing vector-db-routes.
7. **Config drift check** on `/monitoring/config` — diffs the live config vs defaults, sees a committed-but-not-deployed provider and raises a deploy ticket.

## Stories

| # | Story | Task plans | Description | Status |
|---|-------|-----------:|-------------|--------|
| 23-1 | System Health Dashboard (Overview) | 2 | Service grid, dependency graph, error/request/latency rates | Done (2026-07-05) |
| 23-2 | Agent Monitor (Realtime) | 2 | Per-role provider chains, cost, rate limits, key validation | Done (2026-07-05) |
| 23-3 | Event Store Explorer | 2 | Search/filter/timeline/replay/export | Done (2026-07-05) |
| 23-4 | Configuration Audit | 2 | Sources, validation, diffs vs defaults, change history | Done (2026-07-05) |
| 23-5 | Workflow Monitor | 2 | Active/historical instances, Gantt, queue depth | Done (2026-07-05) |
| 23-6 | Provider Diagnostics (Deep) | 2 | Latency histograms, token analytics, error classification, cost compare, call logs | Done (2026-07-05) |
| 23-7 | Log Explorer (OpenSearch) | 2 | Live tail, Lucene search, saved searches, alerts | Placeholder page |
| 23-8 | Infrastructure Monitor | 2 | PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, Docker metrics | Done (2026-07-05) |
| 23-9 | Knowledge Base Monitor | 2 | Vector DB, embeddings, RAG freshness, MCP connections | Placeholder page |
| 23-10 | Security & Access Audit | 2 | Logins, sessions, API keys, rate limits, suspicious activity | Placeholder page |
| 23-11 | Monitoring API Foundation | 3 | Route registration, SSE helpers, aggregators (foundation) | Superseded (per-story C# endpoints instead) |
| 23-12 | Dashboard Navigation & Layout | 3 | Sidebar, shared layout, primitives, hooks | Done (2026-07-04) |

**Total:** 26 task plans across 12 stories.

## Dependency order

```
23-11 (API Foundation)  +  23-12 (Navigation & Layout)
    │
    ├─▶ 23-1 (System Health)   ─▶ 23-8 (Infrastructure)
    ├─▶ 23-2 (Agent Monitor)   ─▶ 23-6 (Provider Diagnostics)
    ├─▶ 23-3 (Event Store Explorer)
    ├─▶ 23-4 (Configuration Audit)
    ├─▶ 23-5 (Workflow Monitor)
    ├─▶ 23-7 (Log Explorer)
    ├─▶ 23-9 (Knowledge Base Monitor)
    └─▶ 23-10 (Security & Access Audit)
```

## Existing infrastructure leveraged

| Component | Location | Provides |
|-----------|----------|----------|
| Admin Health Routes | `packages/api/src/routes/admin/health-routes.ts` | Per-service health (PG, ELSA, OpenSearch, RabbitMQ, ChromaDB) |
| Provider Health Tracker | `packages/providers/src/provider-health.ts` | Circuit breaker state per provider+model |
| Health Service | `packages/api/src/services/settings/HealthService.ts` | Provider health API |
| Diagnostics Service | `packages/api/src/services/settings/DiagnosticsService.ts` | Last 500 tool/provider call events |
| Engine Routes | `packages/api/src/routes/engine/index.ts` | Engine state, stats, event history, SSE |
| Dashboard Routes | `packages/api/src/routes/dashboard/index.ts` | Summary, engine list, workflow definitions |
| Workflow Routes | `packages/api/src/routes/workflows/index.ts` | Definitions, instances, per-instance SSE |
| Cost Tracker | `packages/cost-monitor/src/cost-tracker.ts` | Usage records, aggregates, limits, alerts |
| KB Analytics | `packages/api/src/routes/knowledge-base/analytics-routes.ts` | Usage, quality, cost analytics |
| Vector DB Routes | `packages/api/src/routes/knowledge-base/vector-db-routes.ts` | Collections, stats, storage |
| MCP Routes | `packages/api/src/routes/knowledge-base/mcp-routes.ts` | MCP server status |
| Event Store | `packages/shared/src/types/index.ts` | `IEventStore` with 17 event types |
| Config Service | `packages/api/src/services/settings/ConfigService.ts` | Agents, security, prompts, providers config |
| User Store | `packages/api/src/persistence/user-store.ts` | Users, roles, sessions |
| API Key Store | `packages/api/src/persistence/user-api-key-store.ts` | API keys |

## Performance targets

- Dashboard initial load: < 2s
- Health check round-trip: < 1s
- SSE stream latency: < 500ms
- Event store search (10k events): < 200ms
- Log search via OpenSearch: < 500ms
- Infrastructure metrics refresh: 5s polling interval

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Observability Dashboard Framework | Epic 5 | New pages extend the existing SPA shell |
| Engine Core + Event Store | Epic 10 / Epic 4 | Data sources for event explorer + workflow monitor |
| Log Aggregation | Epic 15 | OpenSearch powers `LogExplorerPage` |
| Security & Secrets | Epic 11 | Security audit reads session + rate-limit events |
| Unified Auth & RBAC | Epic 16 | `settings:view` permission on every route |
| Cost Monitor | Epic 1.5 / Epic 6 | `ICostTracker` feeds agent monitor + provider diagnostics |
| Knowledge Base | Epic 6 | KB analytics / vector-db / MCP routes power KB monitor |
| Provider Diagnostics | Epic 9 | Circuit-breaker + diagnostics queue feed provider deep view |

## Current state

**Landed (2026-07-04/05):**

- **23-12 Navigation & Layout foundation** (2026-07-04) — sidebar "Monitoring" section, `MonitoringLayout` shell, the full shared-primitive set (`StatusBadge`, `MetricCard`, `MetricGrid`, `TimeSeriesChart` — hand-rolled SVG, no chart lib — `DataTable`, `EmptyState`, `ErrorBanner`, `ProgressRing`, `LatencyBar`), the three hooks (`useMonitoringSSE` with exponential-backoff reconnect, `useAutoRefresh` with visibility gating, `useTimeRange` with URL persistence), `monitoring-nav.ts` with all 10 routes, an overview page, and placeholder stubs for every screen.
- **23-1 System Health** — `SystemHealthPage` + `useSystemHealth` composing the *existing* health / diagnostics / event sources into the service grid and rate views (no new backend).
- **23-2 Agent Monitor (realtime)** — `AgentMonitorPage` showing active agents with a live tool-loop tail via `useRunStreamTail` over the existing Story 32-23 run-stream endpoint.
- **23-3 Event Store Explorer** — `EventExplorerPage` + `useEventQuery` + `EventDetailPanel`: search / filter / detail over the DCB store through the Story 4-7 event query API.
- **23-4 Configuration Audit** — `ConfigAuditPage` + `useConfigAudit`: effective configuration plus change history.
- **23-5 Workflow Monitor** — `WorkflowMonitorPage` + `useWorkflowMonitor` over a new backend slice: `GET /api/v1/runs/summary` (`ReposRunsEndpoints`) backed by `WorkflowRepository.WorkflowInstanceSummary` — workflow instances, statuses, durations.
- **23-6 Provider Diagnostics (deep)** — `ProviderDiagnosticsPage` + `useProviderDiagnostics` over a new `DiagnosticsService` / `DiagnosticsRepository` (latency / error / cost aggregations) exposed via `ProviderEndpoints`. A same-day fix made the endpoint **fail closed on a null tenant** (no cross-tenant economics fan-out on the `SettingsView` policy), clamped the max query window, and UTC-normalized date bounds.
- **23-8 Infrastructure Monitor** — `InfrastructureMonitorPage` + `useInfrastructureMonitor` over a new lightweight `GET /api/admin/monitoring/infrastructure` endpoint (`InfrastructureMetricsService`: runtime, disk, dependency health) gated by the `PlatformOwnerAccess` policy.

**Placeholder pages (design only):** 23-7 Log Explorer, 23-9 Knowledge Base Monitor, 23-10 Security & Access Audit — routes and stubs exist via 23-12, no data wiring yet.

**Drift from the draft:** the backend shipped as C# `Tamma.Api` endpoints (existing + the three new slices above) rather than the drafted Fastify `packages/api/src/routes/monitoring/*` plugin — so 23-11's `MonitoringAggregator` / `MetricsCollector` / `SseHelpers` foundation was not built as specced; the architecture diagram above reflects the original draft.

**No external dependencies added**: the epic deliberately reuses PostgreSQL, OpenSearch, RabbitMQ, ChromaDB, and in-memory stores.

## See also

- [Epic 5 — Observability Dashboard & Docs](Epic-5-Observability.md) — the underlying dashboard framework.
- [Epic 4 — Event Sourcing](Epic-4-Event-Sourcing.md) — DCB stream consumed by the event explorer.
- [Epic 15 — Log Aggregation](Epic-15-Log-Aggregation.md) — OpenSearch pipeline for the log explorer.
- [Epic 9 — Agent Management](Epic-9-Agent-Management.md) — provider chain + diagnostics data model.
- [Epic 16 — Unified Auth & RBAC](Epic-16-Auth-Admin.md) — permissions gating monitoring routes.
- [Epic 6 — Context & Knowledge Base](Epic-6-Context-Knowledge.md) — KB and MCP observability surfaces.
- [Roadmap](Roadmap.md) — placement in the overall plan.

## Story files

[Epic 23 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-23)

---

_Last updated: 2026-07-15_
