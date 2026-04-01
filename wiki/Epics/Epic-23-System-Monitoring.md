# Epic 23: System Monitoring & Observability Dashboard

**Status:** Drafted (implementation-ready, 26 task plans; some stories partially implemented -- 23-1, 23-6, 23-9, 23-11, 23-12 in progress)
**Stories:** 12 (23-1 through 23-12)
**Task Plans:** 26 detailed implementation breakdowns
**Packages:** `@tamma/api`, `@tamma/dashboard`

## Overview

Production-grade monitoring, diagnostics, and observability for every service, provider, workflow, and infrastructure component in the Tamma platform. Every screen, every metric, every interaction is specified as an implementable story.

## Architecture

**Data Flow:** Services emit metrics via existing Pino structured logging, health check endpoints, the event store, the cost tracker, and OpenSearch. The dashboard consumes these via REST API endpoints and SSE streams. New API endpoints are added to `packages/api/src/routes/monitoring/` and exposed under `/api/monitoring/*`. Dashboard pages live under `packages/dashboard/src/pages/monitoring/`.

**Key Principles:**
- Real-time data via SSE for live views (engine state, logs, health)
- Polling for aggregate/historical data (5s-60s intervals depending on urgency)
- All monitoring endpoints require `settings:view` permission (admin/owner)
- No new external dependencies -- uses existing PostgreSQL, OpenSearch, and in-memory stores
- Every metric ties back to an existing data source or a well-defined new API endpoint

## Stories

All 12 stories now have detailed task plan breakdowns with implementation-ready specifications.

| # | Story | Task Plans | Status | Description |
|---|-------|------------|--------|-------------|
| 23-1 | System Health Dashboard (Overview) | 2 | Planned | At-a-glance view of every service: health status, uptime, resource usage, response times, error rates, request rates, service dependency graph |
| 23-2 | Agent Monitor (Realtime) | 2 | Planned | Real-time monitoring of agent roles, provider chains, operational status, cost tracking, rate limits, API key validation |
| 23-3 | Event Store Explorer | 2 | Planned | Search, filter, visualize, and export all DCB engine events; primary debugging tool for workflow execution |
| 23-4 | Configuration Audit | 2 | Planned | Config sources, setting validation, missing value highlights, diffs against defaults, change history |
| 23-5 | Workflow Monitor | 2 | Planned | Active/historical workflow instances, current phase, duration, cost, Gantt timeline, queue depth |
| 23-6 | Provider Diagnostics (Deep) | 2 | Planned | Per-provider latency histograms, error classification, token usage analytics, model availability, cost comparison, API call logs |
| 23-7 | Log Explorer (OpenSearch) | 2 | Planned | Live log tailing, full-text search (Lucene syntax), service/level filtering, error drill-down, saved searches, alert rules |
| 23-8 | Infrastructure Monitor | 2 | Planned | PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, Docker container metrics |
| 23-9 | Knowledge Base Monitor | 2 | Planned | Vector DB health, embedding coverage, index freshness, RAG pipeline health, MCP connections |
| 23-10 | Security & Access Audit | 2 | Planned | Login attempts, active sessions, API key usage, role distribution, rate limit violations, suspicious activity detection |
| 23-11 | Monitoring API Foundation | 3 | Planned | Route registration, SSE helpers, data aggregation services, monitoring middleware (foundation for all screens) |
| 23-12 | Dashboard Navigation & Layout | 3 | Planned | Sidebar "Monitoring" section, shared layout components, tab navigation, time range selector, auto-refresh toggle |

**Total: 26 task plans across 12 stories**

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-23)

## Dependency Order

```
23-11 (API Foundation) + 23-12 (Navigation)
    |
    +---> 23-1 (System Health) ---> 23-8 (Infrastructure)
    +---> 23-2 (Agent Monitor) ---> 23-6 (Provider Diagnostics)
    +---> 23-3 (Event Store Explorer)
    +---> 23-4 (Configuration Audit)
    +---> 23-5 (Workflow Monitor)
    +---> 23-7 (Log Explorer)
    +---> 23-9 (Knowledge Base Monitor)
    +---> 23-10 (Security Audit)
```

## Existing Infrastructure Leveraged

| Component | Location | What It Provides |
|-----------|----------|-----------------|
| Admin Health Routes | `packages/api/src/routes/admin/health-routes.ts` | Service health checks (PostgreSQL, ELSA, OpenSearch, RabbitMQ, ChromaDB) |
| Provider Health Tracker | `packages/providers/src/provider-health.ts` | Circuit breaker state per provider+model |
| Health Service | `packages/api/src/services/settings/HealthService.ts` | Provider health status API |
| Diagnostics Service | `packages/api/src/services/settings/DiagnosticsService.ts` | Tool/provider call events (last 500) |
| Engine Routes | `packages/api/src/routes/engine/index.ts` | Engine state, stats, event history, SSE streams |
| Dashboard Routes | `packages/api/src/routes/dashboard/index.ts` | Summary, engine list, workflow definitions |
| Workflow Routes | `packages/api/src/routes/workflows/index.ts` | Definitions, instances, SSE per instance |
| Cost Tracker | `packages/cost-monitor/src/cost-tracker.ts` | Usage records, aggregates, limits, alerts, reports |
| KB Analytics | `packages/api/src/routes/knowledge-base/analytics-routes.ts` | Usage, quality, cost analytics |
| Vector DB Routes | `packages/api/src/routes/knowledge-base/vector-db-routes.ts` | Collections, stats, storage |
| MCP Routes | `packages/api/src/routes/knowledge-base/mcp-routes.ts` | MCP server status, tool invocation |
| Event Store | `packages/shared/src/types/index.ts` | IEventStore with 17 event types |
| Config Service | `packages/api/src/services/settings/ConfigService.ts` | Agents, security, prompts, providers config |
| User Store | `packages/api/src/persistence/user-store.ts` | User management, roles, sessions |

## Performance Targets

- Dashboard initial load: < 2s
- Health check round-trip: < 1s
- SSE stream latency: < 500ms
- Event store search (10k events): < 200ms
- Log search via OpenSearch: < 500ms
- Infrastructure metrics refresh: 5s polling interval

---

_See the [Roadmap](Roadmap) for how this epic fits into the overall plan._
