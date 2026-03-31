---
title: "Story 20-3: Usage Metering"
sidebar:
  order: 200
---

Status: planned

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform operator**,
I want to accurately track workflow runs, LLM tokens consumed, and connected repositories per tenant,
So that usage data drives billing (overage charges on Pro plan) and is available for limit enforcement and the billing dashboard.

## Priority

P0 - Required for usage-based billing and limit enforcement

## Acceptance Criteria

1. Three Stripe Billing Meters are created (via seed script from Story 20-1): `tamma.workflow_runs` (SUM aggregation), `tamma.llm_tokens` (SUM aggregation), `tamma.connected_repos` (LAST aggregation as a gauge)
2. A `UsageMeteringService` in `packages/api/src/services/billing/` captures usage events from the orchestrator and AI provider layers and writes them to a local `usage_records` table
3. Workflow run completion (successful or failed) emits a meter event with `value: 1` to the `tamma.workflow_runs` meter, tagged with `stripe_customer_id`
4. Every LLM API call completion emits a meter event with `value: <total_tokens>` (input + output tokens) to the `tamma.llm_tokens` meter
5. Connected repo count changes (repo added/removed from installation) emit a meter event with `value: <current_count>` to the `tamma.connected_repos` meter
6. Meter events are batched in-memory and flushed to Stripe every 60 seconds (configurable via `BILLING_METER_FLUSH_INTERVAL_MS`) to avoid exceeding the 1,000 events/second rate limit
7. If the Stripe meter event API call fails, events are persisted to the `usage_records` table with `reported_to_stripe = false` and retried on the next flush cycle
8. A `GET /api/v1/billing/usage` endpoint returns current-period usage for the authenticated tenant: `{ workflow_runs: number, llm_tokens: number, connected_repos: number, period_start: string, period_end: string }`
9. Usage aggregation queries the local `usage_records` table for real-time data (not Stripe, which processes events asynchronously)
10. A background reconciliation job (runs hourly) compares local usage totals with Stripe meter summaries and logs discrepancies as WARN-level events
11. Domain events are emitted: `BILLING.USAGE.RECORDED` (per batch flush), `BILLING.USAGE.FLUSH_FAILED`, `BILLING.USAGE.RECONCILIATION_MISMATCH`
12. Unit tests cover: event capture for all three meter types, batching logic, flush success/failure/retry, usage aggregation query, reconciliation comparison
13. Integration test (requires `STRIPE_SECRET_KEY_TEST`): send meter events, retrieve meter event summaries from Stripe, verify values match

## Technical Design

### Package Structure

```
packages/api/src/services/billing/
  usage-metering-service.ts       # Core metering service
  meter-event-buffer.ts           # In-memory batch buffer
  usage-reconciliation.ts         # Hourly reconciliation job
  usage-metering-service.test.ts  # Unit tests
  meter-event-buffer.test.ts      # Unit tests
```

### Stripe Billing Meters Setup

Three meters are created by the `stripe-seed.ts` script from Story 20-1:

```typescript
// Workflow runs meter (SUM: add up all events in billing period)
const workflowRunsMeter = await stripe.billing.meters.create({
  display_name: 'Workflow Runs',
  event_name: 'tamma.workflow_runs',
  default_aggregation: { formula: 'sum' },
  customer_mapping: {
    event_payload_key: 'stripe_customer_id',
    type: 'by_id',
  },
});

// LLM tokens meter (SUM: total tokens across all calls)
const llmTokensMeter = await stripe.billing.meters.create({
  display_name: 'LLM Tokens',
  event_name: 'tamma.llm_tokens',
  default_aggregation: { formula: 'sum' },
  customer_mapping: {
    event_payload_key: 'stripe_customer_id',
    type: 'by_id',
  },
});

// Connected repos meter (LAST: gauge of current count)
const connectedReposMeter = await stripe.billing.meters.create({
  display_name: 'Connected Repos',
  event_name: 'tamma.connected_repos',
  default_aggregation: { formula: 'last' },
  customer_mapping: {
    event_payload_key: 'stripe_customer_id',
    type: 'by_id',
  },
});
```

### Meter Event Buffer

```typescript
// packages/api/src/services/billing/meter-event-buffer.ts
export interface MeterEvent {
  event_name: string;
  payload: {
    stripe_customer_id: string;
    value: string;    // Stripe requires string representation of whole numbers
  };
  timestamp: number;  // Unix seconds
}

export class MeterEventBuffer {
  private buffer: MeterEvent[] = [];
  private flushInterval: NodeJS.Timeout | null = null;

  constructor(
    private stripe: Stripe,
    private pool: pg.Pool,
    private logger: ILogger,
    private flushIntervalMs: number = 60_000,
  ) {}

  /** Add an event to the buffer. */
  enqueue(event: MeterEvent): void {
    this.buffer.push(event);
  }

  /** Start the periodic flush timer. */
  start(): void {
    this.flushInterval = setInterval(() => {
      void this.flush();
    }, this.flushIntervalMs);
  }

  /** Stop the flush timer and do a final flush. */
  async stop(): Promise<void> {
    if (this.flushInterval) {
      clearInterval(this.flushInterval);
      this.flushInterval = null;
    }
    await this.flush();
  }

  /** Flush all buffered events to Stripe. */
  async flush(): Promise<void> {
    if (this.buffer.length === 0) return;

    const batch = this.buffer.splice(0, this.buffer.length);
    const failed: MeterEvent[] = [];

    for (const event of batch) {
      try {
        await this.stripe.billing.meterEvents.create({
          event_name: event.event_name,
          payload: event.payload,
          timestamp: event.timestamp,
        });
      } catch (error) {
        this.logger.warn('Failed to send meter event to Stripe', {
          event_name: event.event_name,
          error,
        });
        failed.push(event);
      }
    }

    // Persist failed events for retry
    if (failed.length > 0) {
      await this.persistFailedEvents(failed);
      // Re-enqueue for next flush
      this.buffer.unshift(...failed);
    }

    this.logger.info('Meter events flushed', {
      total: batch.length,
      succeeded: batch.length - failed.length,
      failed: failed.length,
    });
  }

  private async persistFailedEvents(events: MeterEvent[]): Promise<void> {
    // Store in usage_records with reported_to_stripe = false
    for (const event of events) {
      await this.pool.query(
        `INSERT INTO usage_records
         (installation_id, meter_name, value, period_start, period_end, reported_to_stripe)
         SELECT id, $2, $3, date_trunc('month', NOW()), date_trunc('month', NOW()) + INTERVAL '1 month', false
         FROM installations WHERE stripe_customer_id = $1`,
        [event.payload.stripe_customer_id, event.event_name, parseInt(event.payload.value, 10)],
      );
    }
  }
}
```

### Usage Metering Service

```typescript
// packages/api/src/services/billing/usage-metering-service.ts
export class UsageMeteringService {
  constructor(
    private buffer: MeterEventBuffer,
    private pool: pg.Pool,
    private logger: ILogger,
  ) {}

  /** Record a completed workflow run. */
  async recordWorkflowRun(installationId: string): Promise<void> {
    const customerId = await this.getCustomerId(installationId);
    if (!customerId) return; // billing not configured

    this.buffer.enqueue({
      event_name: 'tamma.workflow_runs',
      payload: { stripe_customer_id: customerId, value: '1' },
      timestamp: Math.floor(Date.now() / 1000),
    });

    // Also record locally for real-time queries
    await this.recordLocally(installationId, 'workflow_runs', 1);
  }

  /** Record LLM tokens consumed by an AI provider call. */
  async recordLlmTokens(installationId: string, totalTokens: number): Promise<void> {
    const customerId = await this.getCustomerId(installationId);
    if (!customerId) return;

    this.buffer.enqueue({
      event_name: 'tamma.llm_tokens',
      payload: { stripe_customer_id: customerId, value: String(totalTokens) },
      timestamp: Math.floor(Date.now() / 1000),
    });

    await this.recordLocally(installationId, 'llm_tokens', totalTokens);
  }

  /** Record the current number of connected repos (gauge). */
  async recordConnectedRepos(installationId: string, repoCount: number): Promise<void> {
    const customerId = await this.getCustomerId(installationId);
    if (!customerId) return;

    this.buffer.enqueue({
      event_name: 'tamma.connected_repos',
      payload: { stripe_customer_id: customerId, value: String(repoCount) },
      timestamp: Math.floor(Date.now() / 1000),
    });

    await this.recordLocally(installationId, 'connected_repos', repoCount);
  }

  /** Get current billing period usage for a tenant. */
  async getCurrentUsage(installationId: string): Promise<UsageSummary> {
    const result = await this.pool.query(
      `SELECT meter_name, SUM(value) as total
       FROM usage_records
       WHERE installation_id = $1
         AND period_start <= NOW()
         AND period_end > NOW()
       GROUP BY meter_name`,
      [installationId],
    );

    const usage: UsageSummary = {
      workflow_runs: 0,
      llm_tokens: 0,
      connected_repos: 0,
      period_start: '',
      period_end: '',
    };

    for (const row of result.rows) {
      if (row.meter_name === 'workflow_runs') usage.workflow_runs = parseInt(row.total, 10);
      if (row.meter_name === 'llm_tokens') usage.llm_tokens = parseInt(row.total, 10);
      if (row.meter_name === 'connected_repos') usage.connected_repos = parseInt(row.total, 10);
    }

    return usage;
  }

  private async recordLocally(installationId: string, meterName: string, value: number): Promise<void> {
    await this.pool.query(
      `INSERT INTO usage_records
       (installation_id, meter_name, value, period_start, period_end, reported_to_stripe)
       VALUES ($1, $2, $3, date_trunc('month', NOW()), date_trunc('month', NOW()) + INTERVAL '1 month', true)`,
      [installationId, meterName, value],
    );
  }

  private async getCustomerId(installationId: string): Promise<string | null> {
    const result = await this.pool.query(
      'SELECT stripe_customer_id FROM installations WHERE id = $1',
      [installationId],
    );
    return result.rows[0]?.stripe_customer_id ?? null;
  }
}
```

### Integration Points

#### Orchestrator -- Workflow Run Completion

The orchestrator's workflow completion handler calls `usageMeteringService.recordWorkflowRun()` after every workflow finishes (success or failure). This hooks into the existing event flow where `WORKFLOW.STEP_COMPLETED` events are emitted.

```typescript
// In orchestrator dispatch loop (packages/orchestrator/src/engine.ts or equivalent)
// After workflow completes:
await usageMeteringService.recordWorkflowRun(context.installationId);
```

#### AI Provider Layer -- Token Tracking

The AI provider abstraction layer (`packages/providers/`) already returns token counts in `MessageResponse`. A post-call hook records tokens:

```typescript
// In provider wrapper or middleware
const response = await provider.sendMessageSync(request);
if (response.usage) {
  const totalTokens = (response.usage.inputTokens ?? 0) + (response.usage.outputTokens ?? 0);
  await usageMeteringService.recordLlmTokens(context.installationId, totalTokens);
}
```

#### GitHub Webhook -- Repo Count Changes

When a GitHub installation event updates the set of connected repositories, the handler calls:

```typescript
// In GitHub webhook handler (packages/api/src/routes/github/github-webhook.ts)
const repoCount = installation.repositories.length;
await usageMeteringService.recordConnectedRepos(installationId, repoCount);
```

### Usage API Endpoint

```typescript
// GET /api/v1/billing/usage
// Returns: UsageSummary

interface UsageSummary {
  workflow_runs: number;
  llm_tokens: number;
  connected_repos: number;
  period_start: string;    // ISO 8601
  period_end: string;      // ISO 8601
}
```

### Reconciliation Job

```typescript
// packages/api/src/services/billing/usage-reconciliation.ts
export class UsageReconciliation {
  /** Run hourly to compare local totals with Stripe meter summaries. */
  async reconcile(): Promise<void> {
    const installations = await this.getActiveInstallations();

    for (const inst of installations) {
      const local = await this.metering.getCurrentUsage(inst.id);

      // Query Stripe meter summaries
      const stripeSummary = await this.stripe.billing.meters.listEventSummaries(
        inst.workflowRunsMeterId,
        {
          customer: inst.stripe_customer_id,
          start_time: Math.floor(new Date(local.period_start).getTime() / 1000),
          end_time: Math.floor(Date.now() / 1000),
        },
      );

      const stripeTotal = stripeSummary.data.reduce(
        (sum, s) => sum + s.aggregated_value, 0,
      );

      if (Math.abs(local.workflow_runs - stripeTotal) > 0) {
        this.logger.warn('Usage reconciliation mismatch', {
          installationId: inst.id,
          meter: 'workflow_runs',
          local: local.workflow_runs,
          stripe: stripeTotal,
        });
        // Emit BILLING.USAGE.RECONCILIATION_MISMATCH
      }
    }
  }
}
```

### Database Migration

```sql
-- migrations/20260328_003_add_usage_records.sql
CREATE TABLE IF NOT EXISTS usage_records (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  installation_id UUID NOT NULL REFERENCES installations(id),
  meter_name TEXT NOT NULL,
  value BIGINT NOT NULL,
  period_start TIMESTAMPTZ NOT NULL,
  period_end TIMESTAMPTZ NOT NULL,
  reported_to_stripe BOOLEAN DEFAULT FALSE,
  stripe_event_id TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_usage_records_lookup
  ON usage_records(installation_id, meter_name, period_start);

CREATE INDEX IF NOT EXISTS idx_usage_records_unreported
  ON usage_records(reported_to_stripe) WHERE reported_to_stripe = FALSE;
```

## Dependencies

- **Prerequisite**: Story 20-1 (Stripe SDK, Billing Meters created via seed, plan config)
- **Prerequisite**: Story 20-2 (subscription exists so overage line items are attached)
- **Blocks**: Story 20-4 (limit enforcement queries usage from this service)
- **Blocks**: Story 20-5 (billing dashboard displays usage from this service)
- **Related**: Epic 2 (orchestrator dispatches workflow runs -- integration point)
- **Related**: Epic 1 (AI provider layer -- token tracking integration point)

## Testing Strategy

1. **Unit tests**: Mock Stripe SDK and pg pool; verify `recordWorkflowRun`, `recordLlmTokens`, `recordConnectedRepos` produce correct meter events; verify buffer batching and flush logic; verify failed event persistence and retry; verify `getCurrentUsage` aggregation query
2. **Buffer tests**: Enqueue 100 events, verify they are flushed in one batch; simulate flush failure, verify events are persisted and re-enqueued; verify `stop()` performs a final flush
3. **Reconciliation tests**: Mock local and Stripe data with matching values (no warn), mismatched values (warn logged)
4. **Integration tests**: (require `STRIPE_SECRET_KEY_TEST`) Send real meter events, query Stripe meter summaries after a delay, verify values
5. **Performance test**: Enqueue 10,000 events, measure flush throughput, ensure total processing < 10 seconds

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/api/src/services/billing/usage-metering-service.ts` | Create |
| `packages/api/src/services/billing/meter-event-buffer.ts` | Create |
| `packages/api/src/services/billing/usage-reconciliation.ts` | Create |
| `packages/api/src/services/billing/usage-metering-service.test.ts` | Create |
| `packages/api/src/services/billing/meter-event-buffer.test.ts` | Create |
| `packages/api/src/routes/billing/usage.ts` | Create |
| `packages/api/src/routes/billing/__tests__/usage.test.ts` | Create |
| `database/migrations/20260328_003_add_usage_records.sql` | Create |
| `scripts/stripe-seed.ts` | Modify (add meter creation) |
| `packages/api/src/routes/billing/index.ts` | Modify (register usage route) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed Stripe Billing Meters documentation and rate limits
4. Verified test Stripe account has Meters enabled (it is on by default)
5. Planned TDD approach (Red-Green-Refactor cycle)

### Stripe Meter Event Rate Limits

- Standard `billing.meterEvents.create`: 1,000 events/second in live mode
- Meter Event Streams (v2): 10,000 events/second in live mode
- Start with standard API; upgrade to streams if throughput demands it
- Pre-aggregate events locally (batch per customer per minute) to reduce API calls

### Meter Event Payload Constraints

- The `value` field in meter event payloads only accepts whole number values as strings
- For token counts, always round to integers (tokens are already integers from providers)
- The `timestamp` field is Unix seconds (not milliseconds)
- Events are processed asynchronously by Stripe -- they may not appear in summaries immediately

### Connected Repos as a Gauge

Unlike workflow runs and tokens (which are counters), connected repos is a gauge. Use `LAST` aggregation so Stripe always bills based on the most recent reported value, not a sum. Report the current count whenever repos are added or removed, and also on a daily schedule as a heartbeat.

### Graceful Degradation

If Stripe is unreachable or billing is unconfigured:
- Usage is still recorded locally in `usage_records`
- The buffer silently queues events without sending
- Orchestrator and AI providers are never blocked by billing failures
- Reconciliation job retries unreported events on the next run

## Logging Requirements

- **INFO**: Meter event batch flushed (count, succeeded, failed), reconciliation completed, usage endpoint queried
- **DEBUG**: Individual meter event enqueued (meter_name, value), flush cycle started, Stripe API response
- **WARN**: Meter event flush failed (event_name, error), reconciliation mismatch (meter, local, stripe), unreported events backlog > 1000
- **ERROR**: Buffer flush completely failed (all events), Stripe meter not found, database write failure
- **Structured context**: Include `{ installationId, meterName, value, batchSize, flushDuration }` where applicable
- **Credential safety**: NEVER log Stripe API keys or customer payment details

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-03-28 | 1.0.0   | Initial story creation | Claude |
