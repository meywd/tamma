# Epic 15: Observability & Log Aggregation

## Overview

**Goal**: Deploy a centralized log aggregation pipeline that collects structured logs from every Tamma service (C# ELSA workflows, .NET REST API, TypeScript Fastify API, TypeScript Engine, Dashboard) into OpenSearch, with pre-built dashboards, retention management, and basic alerting.

**Value Delivered**:
- Single pane of glass for all platform logs across C# and TypeScript services
- Full-text search and filtering by workflowInstanceId, issueNumber, sessionId, service, level
- Correlation of events across service boundaries (C# workflow triggers TS engine action)
- 30-day automatic retention with ISM policies (no manual cleanup)
- Pre-built dashboards for errors, workflow timelines, LLM call latency, tool execution duration
- Alerting on error spikes and workflow failures
- Zero log data loss during OpenSearch downtime (buffered transports with retry)

## Why OpenSearch (Not Elasticsearch)

- **License**: Apache 2.0 — all features free, including security, RBAC, alerting, ISM
- **Feature parity**: OpenSearch 2.19 matches Elasticsearch 7.x feature set plus extras
- **No license risk**: Elasticsearch moved to SSPL/Elastic License 2.0 in 2021
- **Sink compatibility**: Serilog Elasticsearch sink works with OpenSearch (same REST API)
- **Pino transport**: `pino-elasticsearch` works with OpenSearch (same bulk API)

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 15.1 | OpenSearch Log Aggregation | P0 (Critical) | None | Planned |
| 15.2 | Structured Logging Gap Remediation | P1 (High) | Story 15.1 | Planned |
| 15.3 | Advanced Dashboards & Alerting Tuning | P2 (Medium) | Story 15.1 | Planned |

## Dependency Graph

```
Story 15.1 (infrastructure + sinks) --> Story 15.2 (fix logging gaps) --> Story 15.3 (advanced dashboards)
```

## Architecture

```
+---------------------+     +---------------------+     +---------------------+
| ELSA Server (.NET)  |     | Tamma API (.NET)    |     | Tamma API (TS)      |
| Serilog             |     | Serilog             |     | Pino                |
| Console+File+OS     |     | Console+File+OS     |     | stdout+OS           |
+----------+----------+     +----------+----------+     +----------+----------+
           |                           |                           |
           |    Serilog.Sinks.         |   Serilog.Sinks.         |  pino-elasticsearch
           |    Elasticsearch          |   Elasticsearch          |  transport
           |                           |                           |
           +---------------------------+---------------------------+
                                       |
                              +--------v--------+
                              |   OpenSearch     |
                              |   (single-node)  |
                              |   Port 9200      |
                              +--------+--------+
                                       |
                              +--------v--------+
                              | OpenSearch       |
                              | Dashboards       |
                              | Port 5601        |
                              +--------+--------+
                                       |
                              +--------v--------+
                              | nginx-proxy      |
                              | logs.tamma.dev   |
                              +-----------------+
```

## Host Constraints

- **VPS**: Hetzner CPX42, 16 GB RAM, 8 vCPU (AMD EPYC)
- **OpenSearch**: Max 4 GB JVM heap (`-Xms4g -Xmx4g`)
- **OpenSearch Dashboards**: Max 1.5 GB (`--max-old-space-size=1536`)
- **Current memory budget** (from docker-compose.prod.yml):
  - PostgreSQL: 2 GB
  - RabbitMQ: 1 GB
  - ChromaDB: 2 GB
  - ELSA Server: 1 GB
  - Tamma API (.NET): 512 MB
  - Tamma API (TS): 512 MB
  - Tamma Engine: 1 GB
  - Dashboard: 256 MB
  - nginx: 128 MB
  - **Total existing**: ~8.4 GB
  - **OpenSearch + Dashboards**: 4 GB + 1.5 GB = 5.5 GB
  - **Total after**: ~13.9 GB (fits in 16 GB with ~2 GB headroom for OS)

## Source Plan

`docs/stories/epic-15/15-1-opensearch-log-aggregation-impl-plan.md`

---

**Last Updated**: 2026-03-28
**Epic Owner**: Platform Engineering
