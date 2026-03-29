# Epic 23: System Monitoring & Observability Dashboard

Production-grade monitoring, diagnostics, and observability for every service, provider, workflow, and infrastructure component in the Tamma platform. Every screen, every metric, every interaction is specified as an implementable story.

## Architecture

**Data Flow**: Services emit metrics via existing Pino structured logging, health check endpoints, the event store, the cost tracker, and OpenSearch. The dashboard consumes these via REST API endpoints and SSE streams. New API endpoints are added to `packages/api/src/routes/monitoring/` and exposed under `/api/monitoring/*`. Dashboard pages live under `packages/dashboard/src/pages/monitoring/`.

**Key Principles**:
- Real-time data via SSE for live views (engine state, logs, health)
- Polling for aggregate/historical data (5s-60s intervals depending on urgency)
- All monitoring endpoints require `settings:view` permission (admin/owner)
- No new external dependencies -- uses existing PostgreSQL, OpenSearch, and in-memory stores
- Every metric ties back to an existing data source or a well-defined new API endpoint

## Stories

| # | Story | Status | Screens |
|---|-------|--------|---------|
| 23-1 | [System Health Dashboard](23-1-system-health-dashboard.md) | planned | Overview, dependency graph, error/request rates |
| 23-2 | [Agent Monitor](23-2-agent-monitor.md) | planned | Agent roles, provider chains, cost, status |
| 23-3 | [Event Store Explorer](23-3-event-store-explorer.md) | planned | Search, filter, timeline, replay, export |
| 23-4 | [Configuration Audit](23-4-configuration-audit.md) | planned | Config sources, validation, diff, history |
| 23-5 | [Workflow Monitor](23-5-workflow-monitor.md) | planned | Active workflows, Gantt timeline, queue depth |
| 23-6 | [Provider Diagnostics Deep](23-6-provider-diagnostics.md) | planned | Latency histograms, token analytics, error classification |
| 23-7 | [Log Explorer](23-7-log-explorer.md) | planned | Live tail, full-text search, saved searches, alerts |
| 23-8 | [Infrastructure Monitor](23-8-infrastructure-monitor.md) | planned | PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, Docker |
| 23-9 | [Knowledge Base Monitor](23-9-knowledge-base-monitor.md) | planned | Vector DB, embeddings, RAG health, MCP connections |
| 23-10 | [Security & Access Audit](23-10-security-access-audit.md) | planned | Login attempts, sessions, permissions, rate limits |
| 23-11 | [Monitoring API Foundation](23-11-monitoring-api-foundation.md) | planned | Route registration, SSE infrastructure, data aggregation |
| 23-12 | [Dashboard Navigation & Layout](23-12-dashboard-navigation.md) | planned | Sidebar updates, monitoring section, responsive layout |

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
| Engine Registry | `packages/api/src/engine-registry.ts` | Multi-engine management |
| Event Store | `packages/shared/src/types/index.ts` | IEventStore with 17 event types |
| Config Service | `packages/api/src/services/settings/ConfigService.ts` | Agents, security, prompts, providers config |
| User Store | `packages/api/src/persistence/user-store.ts` | User management, roles, sessions |
| API Key Store | `packages/api/src/persistence/user-api-key-store.ts` | API key management |

## Performance Targets

- Dashboard initial load: < 2s
- Health check round-trip: < 1s
- SSE stream latency: < 500ms
- Event store search (10k events): < 200ms
- Log search via OpenSearch: < 500ms
- Infrastructure metrics refresh: 5s polling interval
