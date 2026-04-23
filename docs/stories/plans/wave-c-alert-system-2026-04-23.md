# Wave C — Alert System Build-Out

**Date**: 2026-04-23
**Branch target**: `feat/wave-a` (continues to roll forward until PR #329 ships)
**Source stories**:
- Story 5.6 — Alert System for Critical Issues (Draft, 1417 lines)
- Story 1.5-37 — Operator notification channels (ready-for-dev, 670 lines)

## Why

Today's pipeline for "something interesting happened":

```
SomethingHappened → _logger.LogWarning("…") → stdout + logs/tamma-api-.log (7-day rotation, no aggregation) → void
```

Gaps:
- No push notifications (no Slack, no PagerDuty, no email)
- No alert rule engine (warn lines fire regardless of operational importance)
- No acknowledgment / resolution workflow
- Tenant has no visibility into alerts about their own workflows
- The DCB event stream has the correlation data, but nothing escalates off it

What we want:

```
DCB event → alert rule engine → alert created → channel fan-out (Slack/PagerDuty/email/webhook)
                                             → ack/resolve workflow via dashboards
```

## Streams (4 agents, sequenced by dependency)

### C.1 — Alert core (foundation; no deps)
**Scope**
- Schema: `alerts`, `alert_channels`, `alert_delivery_attempts` tables on `ControlPlaneDbContext` (platform-plane — rules + channels fan out across tenants)
- `IAlertSink` abstraction: `RaiseAsync(AlertPayload)` — the single write side the rest of the code calls
- `IAlertChannel` seam with 4 implementations:
  - **EmailChannel** — reuses existing `platform_email_outbox` (Story 28-6)
  - **SlackChannel** — webhook POST to `channel.Config["webhookUrl"]`
  - **PagerDutyChannel** — Events v2 API (`routing_key` from channel.Config + severity mapping)
  - **WebhookChannel** — generic POST with HMAC signature header
- `NotificationDispatcher` background service — polls `alerts` + `alert_delivery_attempts`, invokes channels with retry envelope (5 attempts / exponential backoff / 10min total)
- Rate limiter: per-rule token bucket, 5/min default (configurable per rule)
- `IAlertAccessAuditor` — emits `ALERT.*` DCB events for every raise / ack / resolve
- Channel credentials stored via existing `ISecretStore` (Epic 29) — no plaintext in `alert_channels` table

**Deliverables**
- 1 migration, ~8 source files, ~6 test files
- ~30-50 tests (unit for channels, integration for dispatcher + retry)
- EndpointFilter-gated admin endpoints under `/api/v1/admin/alerts/*` (ownerAccess)

### C.2 — Rule engine + built-in rules (depends on C.1)
**Scope**
- `IAlertRule` abstraction with `Evaluate(DomainEvent) → AlertPayload?` contract
- `alert_rules` table — rule DSL stored as JSON (`{ eventType, predicate, severity, throttleSeconds, channels[] }`)
- `AlertRuleEvaluator` background service — subscribes to `domain_events` new-row triggers (via existing EventBus), evaluates enabled rules
- 5 built-in rules:
  - `BUDGET.EXHAUSTED` → warning, email + Slack
  - `AGENT.DISPATCH.FAILED` (3x in 5min window) → warning, Slack + webhook
  - `WORKFLOW.RETRY_EXCEEDED` (>3 retries on single workflow) → critical, PagerDuty + email
  - `PLATFORM.API.UNHEALTHY` (5xx rate >50% over 5min) → critical, PagerDuty + Slack
  - `SECRET.ROTATION.FAILED` (any) → critical, PagerDuty + email (ties into Stories 29-6/7/8)
- Admin rule CRUD endpoints under `/api/v1/admin/alert-rules/*`

**Deliverables**
- 1 migration, ~10 source files, ~8 test files
- Built-in rule seed data (idempotent re-insert on startup)

### C.3 — Admin + tenant UIs (depends on C.1 + C.2)
**Scope**
- Platform admin dashboard (`Tamma.Studio`):
  - Alert rule CRUD page
  - Channel CRUD page (add/remove/disable per-channel)
  - Alert feed (filter by severity / status / time window)
  - Ack/resolve actions
- Tenant-facing dashboard (`packages/dashboard-user`):
  - Alert feed scoped to `tenantId` (only alerts where payload.tenantId matches)
  - Channel config for the tenant (email target, Slack webhook)
  - Ack/resolve within tenant scope
- Both use the existing `SecretRevealModal` pattern for channel credential input

**Deliverables**
- ~6 Blazor components (admin) + ~6 React components (tenant)
- ~20 frontend tests total

### C.4 — Wire existing sites (depends on C.1 + C.2)
**Scope**
- Emit `BUDGET.EXHAUSTED` DCB event in `CheckBudgetActivity` (currently has no event, just the dropped warn log)
- Emit `PLATFORM.API.UNHEALTHY` in `TammaApiClient` when 5xx rate crosses threshold over sliding window (thin wrapper around existing `CircuitBreakerState`)
- Emit `SECRET.ROTATION.FAILED` from `RotateSecretSagaActivity` compensation path (Story 29-6, already merged)
- Emit `AGENT.DISPATCH.FAILED` from `DispatchCycleActivity` on terminal failure
- Emit `WORKFLOW.RETRY_EXCEEDED` from retry-counting logic in Elsa host
- Delete the remaining identifier-less warn logs if the event emission makes them redundant

**Deliverables**
- Small edits across 5-6 activity files
- Event emission tests
- Integration test: raise-to-delivery end-to-end (in-memory channel)

## Dependency graph

```
C.1 ─┬─► C.2 ─┬─► C.3
     │        │
     │        └─► C.4
     │
     (C.2 is independent of C.3 and C.4 after landing)
```

## Dispatch plan

**Phase 1** (parallel, single agent): C.1 alert core — lands schema + seams + 4 channels + dispatcher
**Phase 2** (parallel, 2 agents, after C.1 merges):
- Agent A: C.2 rule engine
- Agent B: _blocked_ — waits for C.2 seam
**Phase 3** (parallel, 2 agents, after C.2 merges):
- Agent C: C.3 UIs (admin + tenant)
- Agent D: C.4 wire existing sites

Sequential because C.2 depends on C.1's schema + abstractions, and C.3/C.4 depend on C.2's rule engine. Parallelising C.1+C.2 would cause merge-conflict churn on the same DI registration + DbContext files.

## Non-goals (explicit)

- **Metric-based alerts** (threshold on gauges) — Story 5.6 §condition has this in the original TS design. DEFER to a post-Wave-C story. Event-based alerts cover every MVP-critical trigger we have today.
- **Alert digest / batching** — one alert = one delivery attempt per channel. Batching is a v2 concern.
- **Alert templates / custom payload shapes** — fixed `AlertPayload` shape (severity, title, description, correlation id, tenantId, timestamp, metadata). Channel-specific rendering is per-channel code; no user-editable templates.
- **CLI channel** — Story 5.6 lists "CLI output if running". We don't have a long-running CLI; skip.

## Success criteria

- `dotnet test` green after every phase merge
- `BUDGET.EXHAUSTED` event flows: activity → DB event → rule evaluator → email outbox row → SMTP send (tested via test-SMTP fixture)
- Slack/PagerDuty/webhook channels tested via WireMock fixture
- Admin dashboard shows a live alert feed
- Tenant dashboard shows only their own tenant's alerts
- Rate limiter verifiable via test (6 alerts in 1 minute → 5 delivered, 1 dropped with `DROPPED_RATE_LIMIT` delivery-attempt row)
