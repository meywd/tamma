---
title: "Epic 15: Observability & Log Aggregation"
sidebar:
  order: 15
---

**Status:** Done
**Stories:** 1 implemented (15-1), 2 planned (15-2, 15-3)

## Overview

Epic 15 deploys a centralized log aggregation pipeline that collects structured logs from every Tamma service (C# ELSA workflows, .NET REST API, TypeScript Fastify API, TypeScript Engine, Dashboard) into OpenSearch, with pre-built dashboards, retention management, and basic alerting.

## Goals

1. Deploy OpenSearch single-node cluster with dashboards
2. Configure log sinks for all services (Serilog for .NET, Pino for TypeScript)
3. Set up index templates, ISM retention policies (30-day auto-cleanup)
4. Build pre-built dashboards for errors, workflow timelines, LLM call latency
5. Configure alerting on error spikes and workflow failures

## Value Delivered

- Single pane of glass for all platform logs across C# and TypeScript services
- Full-text search and filtering by workflowInstanceId, issueNumber, sessionId, service, level
- Correlation of events across service boundaries
- 30-day automatic retention with ISM policies
- Pre-built dashboards for errors, workflow timelines, LLM latency, tool execution duration
- Zero log data loss during OpenSearch downtime (buffered transports with retry)

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 15-1 | OpenSearch Log Aggregation | P0 (Critical) | Done |
| 15-2 | Structured Logging Gap Remediation | P1 (High) | Planned |
| 15-3 | Advanced Dashboards & Alerting Tuning | P2 (Medium) | Planned |

## Key Technical Details

### Why OpenSearch (Not Elasticsearch)

- **License**: Apache 2.0 -- all features free (security, RBAC, alerting, ISM)
- **Feature parity**: OpenSearch 2.19 matches Elasticsearch 7.x plus extras
- **Sink compatibility**: Serilog Elasticsearch sink and `pino-elasticsearch` work with OpenSearch

### Architecture

```
ELSA Server (.NET)    Tamma API (.NET)    Tamma API (TS)
  Serilog               Serilog             Pino
     |                     |                  |
     | Serilog.Sinks.      | Serilog.Sinks.  | pino-elasticsearch
     | Elasticsearch       | Elasticsearch   | transport
     |                     |                  |
     +---------------------+------------------+
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
                  +-----------------+
```

### Host Constraints

- **VPS**: Hetzner CPX42, 16 GB RAM, 8 vCPU
- **OpenSearch**: Max 4 GB JVM heap
- **OpenSearch Dashboards**: Max 1.5 GB
- **Total after deployment**: ~13.9 GB (fits in 16 GB with ~2 GB headroom)

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Docker Deployment | Epic 1.5 | OpenSearch deployed via Docker Compose |
| Observability | Epic 5 | Extends structured logging with aggregation |

## Story Files

[Story documents on GitHub](/stories/epic-15/)
