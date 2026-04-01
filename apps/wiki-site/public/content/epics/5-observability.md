---
title: "Epic 5: Observability Dashboard & Documentation"
sidebar:
  order: 5
---

**Status:** Partially Implemented
**Stories:** 14 (5-1 through 5-10, with 5-9a through 5-9e)
**Task Plans:** 0
**Tech Spec:** [tech-spec-epic-5.md](/stories/epic-5//tech-spec-epic-5.md)

## Overview

Epic 5 provides real-time observability, monitoring capabilities, and comprehensive documentation for the Tamma platform. The dashboard scaffolding exists at `@tamma/dashboard` with admin, settings, and knowledge base pages, but most monitoring and documentation stories remain to be implemented.

## Goals

1. Implement structured logging with Pino (JSON format, sensitive data redaction)
2. Collect and expose system metrics (Prometheus format)
3. Build real-time dashboards for system health and development velocity
4. Create event trail exploration UI
5. Implement alert system for critical issues
6. Build feedback collection system
7. Create comprehensive integration testing suite
8. Write installation, usage, API, and documentation website
9. Prepare alpha release

## Stories

### MVP Critical

| Story | Title | Status |
|-------|-------|--------|
| 5-1 | Structured Logging Implementation | Ready for Dev |
| 5-2 | Metrics Collection Infrastructure | Ready for Dev |
| 5-3 | Real-Time Dashboard -- System Health | Ready for Dev |
| 5-4 | Real-Time Dashboard -- Development Velocity | Ready for Dev |
| 5-5 | Event Trail Exploration UI | Ready for Dev |
| 5-6 | Alert System for Critical Issues | Ready for Dev |
| 5-7 | Feedback Collection System | Ready for Dev |
| 5-8 | Integration Testing Suite | Drafted |
| 5-9a | Installation & Setup Documentation | Drafted |
| 5-9b | Usage & Configuration Documentation | Drafted |
| 5-9c | API Reference Documentation | Backlog |
| 5-9d | Full Documentation Website | Backlog |
| 5-9e | Video Walkthrough | Backlog |
| 5-10 | Alpha Release Preparation | Drafted |

## Key Technical Details

### Existing Dashboard

The `@tamma/dashboard` package exists as a React SPA (React 18 + Vite + Tailwind 4) with:
- Admin pages (knowledge base, agents, security, budget, prompts, provider health)
- React Router DOM routing
- Zustand state management
- Tailwind CSS 4 styling

### Structured Logging

- **Library**: Pino (5x faster than Winston)
- **Format**: Structured JSON with `timestamp`, `level`, `message`, `context`
- **Context**: Correlation ID, issue number, PR number, actor ID
- **Redaction**: API keys, tokens, and passwords redacted from all logs
- **Outputs**: stdout (containers), file (local dev), aggregation service (optional)

### Metrics

- Counters: `issues_processed_total`, `prs_created_total`, `escalations_total`
- Gauges: `active_autonomous_loops`, `pending_approvals`, `queue_depth`
- Histograms: `issue_completion_duration_seconds`, `ai_request_duration_seconds`
- Endpoint: `GET /metrics` (Prometheus format)

### Documentation Structure

| Doc | Coverage | Story |
|-----|----------|-------|
| Installation & Setup | npm, Docker, binary installation | 5-9a |
| Usage & Configuration | CLI commands, config options, provider/platform setup | 5-9b |
| API Reference | REST endpoints, webhooks, events, auth | 5-9c |
| Documentation Website | Searchable site on GitHub/Cloudflare Pages | 5-9d |
| Video Walkthrough | 5-10 min demo (optional) | 5-9e |

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Event Sourcing | Epic 4 | Event query API provides data for dashboards |
| Autonomous Loop | Epic 2 | Metrics track loop performance |
| Quality Gates | Epic 3 | Gate results feed alerts |
| CLI & Deployment | Epic 1.5 | Documentation covers installation methods |
| Log Aggregation | Epic 15 | OpenSearch extends structured logging |

## Story Files

[Story documents on GitHub](/stories/epic-5/)
