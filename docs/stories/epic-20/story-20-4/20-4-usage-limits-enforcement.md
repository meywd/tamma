# Story 20-4: Usage Limits Enforcement

Status: planned

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform operator**,
I want usage limits enforced at the orchestrator level before dispatching workflows,
So that Free-tier tenants cannot exceed their allocation, Pro tenants are charged overages, and the system degrades gracefully when limits are hit.

## Priority

P0 - Required to prevent unbounded resource consumption on Free tier

## Acceptance Criteria

1. A `UsageLimitGuard` service is called before every workflow dispatch in the orchestrator; it returns `{ allowed: boolean, reason?: string, remainingQuota: QuotaSnapshot }` within 50ms (p95)
2. For Free-tier tenants, exceeding any limit (workflow runs, LLM tokens, connected repos) blocks the workflow with a clear error message explaining the limit and how to upgrade
3. For Pro-tier tenants, exceeding base limits is allowed (overages are billed via Stripe metered prices); a warning is included in the workflow response indicating overage charges will apply
4. For Enterprise-tier tenants, all limits return unlimited (`-1`); the guard is effectively a no-op pass-through
5. The guard checks all three metrics: workflow runs remaining in current period, LLM token budget remaining, and connected repos count vs limit
6. Connected repos limit is enforced when adding a new repo to an installation (not at workflow dispatch time); attempting to connect a repo beyond the limit returns a 403 with upgrade instructions
7. When a Free-tier tenant hits their workflow run limit, queued workflows are not discarded -- they are held in a `pending_upgrade` state and can be released if the tenant upgrades within the billing period
8. A `GET /api/v1/billing/quota` endpoint returns the current quota snapshot: `{ plan, limits, usage, remaining, overage_active }` for the authenticated tenant
9. Usage data is cached in-memory with a 30-second TTL to avoid hitting the database on every dispatch; cache is invalidated on plan change webhook events
10. Domain events are emitted: `BILLING.USAGE.LIMIT_REACHED` (when a Free tenant hits a limit), `BILLING.USAGE.OVERAGE_STARTED` (when a Pro tenant exceeds base allocation), `BILLING.USAGE.QUOTA_WARNING` (when usage reaches 80% of a limit)
11. A `UsageLimitMiddleware` Fastify plugin wraps billing-relevant routes to inject quota context into the request, making it available to downstream handlers
12. Unit tests cover: Free limit block, Pro overage allow, Enterprise pass-through, cache hit/miss, cache invalidation on plan change, quota endpoint response shape, all three metric types, edge cases (exactly at limit, one over, zero usage)
13. Performance test: `UsageLimitGuard.check()` executes in < 50ms p95 with 1,000 concurrent calls using cached data

## Technical Design

### Package Structure

```
packages/api/src/services/billing/
  usage-limit-guard.ts          # Core limit enforcement
  quota-cache.ts                # In-memory cache with TTL
  usage-limit-guard.test.ts     # Unit tests
  quota-cache.test.ts           # Unit tests

packages/api/src/routes/billing/
  quota.ts                      # GET /api/v1/billing/quota
  __tests__/quota.test.ts
```

### Usage Limit Guard

```typescript
// packages/api/src/services/billing/usage-limit-guard.ts
export interface QuotaSnapshot {
  plan: PlanName;
  limits: PlanLimits;
  usage: {
    workflow_runs: number;
    llm_tokens: number;
    connected_repos: number;
  };
  remaining: {
    workflow_runs: number;    // -1 = unlimited
    llm_tokens: number;       // -1 = unlimited
    connected_repos: number;  // -1 = unlimited
  };
  overage_active: boolean;    // true if Pro tenant is past base allocation
}

export interface LimitCheckResult {
  allowed: boolean;
  reason?: string;            // human-readable explanation when blocked
  quota: QuotaSnapshot;
}

export class UsageLimitGuard {
  constructor(
    private cache: QuotaCache,
    private usageMetering: UsageMeteringService,
    private pool: pg.Pool,
    private logger: ILogger,
  ) {}

  /**
   * Check whether a workflow dispatch is allowed for the given tenant.
   * Returns within 50ms p95 using cached data.
   */
  async checkWorkflowDispatch(installationId: string): Promise<LimitCheckResult> {
    const quota = await this.getQuota(installationId);

    // Enterprise: always allowed
    if (quota.plan === 'enterprise') {
      return { allowed: true, quota };
    }

    // Check workflow run limit
    if (quota.limits.workflow_runs !== -1) {
      if (quota.usage.workflow_runs >= quota.limits.workflow_runs) {
        if (quota.plan === 'free') {
          this.logger.info('Free tier workflow limit reached', {
            installationId,
            usage: quota.usage.workflow_runs,
            limit: quota.limits.workflow_runs,
          });
          // Emit BILLING.USAGE.LIMIT_REACHED
          return {
            allowed: false,
            reason: `Free plan limit reached: ${quota.usage.workflow_runs}/${quota.limits.workflow_runs} workflow runs used this period. Upgrade to Pro for 2,000 runs/month.`,
            quota,
          };
        }

        if (quota.plan === 'pro' && !quota.overage_active) {
          this.logger.info('Pro tier overage started', {
            installationId,
            usage: quota.usage.workflow_runs,
            limit: quota.limits.workflow_runs,
          });
          // Emit BILLING.USAGE.OVERAGE_STARTED
        }
      }
    }

    // Emit quota warning at 80%
    if (quota.limits.workflow_runs !== -1) {
      const pct = quota.usage.workflow_runs / quota.limits.workflow_runs;
      if (pct >= 0.8 && pct < 1.0) {
        // Emit BILLING.USAGE.QUOTA_WARNING (only once per threshold crossing)
      }
    }

    return { allowed: true, quota };
  }

  /**
   * Check whether a new repo can be connected to the installation.
   * Called when repos are added, not at workflow dispatch time.
   */
  async checkRepoConnection(installationId: string): Promise<LimitCheckResult> {
    const quota = await this.getQuota(installationId);

    if (quota.limits.connected_repos === -1) {
      return { allowed: true, quota };
    }

    if (quota.usage.connected_repos >= quota.limits.connected_repos) {
      return {
        allowed: false,
        reason: `Plan limit reached: ${quota.usage.connected_repos}/${quota.limits.connected_repos} repos connected. Upgrade to add more repositories.`,
        quota,
      };
    }

    return { allowed: true, quota };
  }

  /**
   * Check LLM token budget before an AI provider call.
   * Only blocks Free-tier; Pro-tier gets overage.
   */
  async checkTokenBudget(installationId: string, estimatedTokens: number): Promise<LimitCheckResult> {
    const quota = await this.getQuota(installationId);

    if (quota.limits.llm_tokens === -1) {
      return { allowed: true, quota };
    }

    const projected = quota.usage.llm_tokens + estimatedTokens;
    if (projected > quota.limits.llm_tokens && quota.plan === 'free') {
      return {
        allowed: false,
        reason: `Free plan token limit reached: ${quota.usage.llm_tokens.toLocaleString()}/${quota.limits.llm_tokens.toLocaleString()} tokens used. Upgrade to Pro for 10M tokens/month.`,
        quota,
      };
    }

    return { allowed: true, quota };
  }

  private async getQuota(installationId: string): Promise<QuotaSnapshot> {
    // Check cache first
    const cached = this.cache.get(installationId);
    if (cached) return cached;

    // Fetch from database
    const [planResult, usage] = await Promise.all([
      this.pool.query(
        'SELECT plan, plan_limits FROM installations WHERE id = $1',
        [installationId],
      ),
      this.usageMetering.getCurrentUsage(installationId),
    ]);

    const row = planResult.rows[0];
    if (!row) {
      throw new TammaError('BILLING.INSTALLATION_NOT_FOUND', 'Installation not found');
    }

    const plan = row.plan as PlanName;
    const limits = row.plan_limits as PlanLimits;

    const remaining = {
      workflow_runs: limits.workflow_runs === -1 ? -1 : Math.max(0, limits.workflow_runs - usage.workflow_runs),
      llm_tokens: limits.llm_tokens === -1 ? -1 : Math.max(0, limits.llm_tokens - usage.llm_tokens),
      connected_repos: limits.connected_repos === -1 ? -1 : Math.max(0, limits.connected_repos - usage.connected_repos),
    };

    const overage_active = plan === 'pro' && (
      usage.workflow_runs > limits.workflow_runs ||
      usage.llm_tokens > limits.llm_tokens ||
      usage.connected_repos > limits.connected_repos
    );

    const quota: QuotaSnapshot = {
      plan,
      limits,
      usage: {
        workflow_runs: usage.workflow_runs,
        llm_tokens: usage.llm_tokens,
        connected_repos: usage.connected_repos,
      },
      remaining,
      overage_active,
    };

    this.cache.set(installationId, quota);
    return quota;
  }
}
```

### Quota Cache

```typescript
// packages/api/src/services/billing/quota-cache.ts
export class QuotaCache {
  private store = new Map<string, { quota: QuotaSnapshot; expiresAt: number }>();

  constructor(private ttlMs: number = 30_000) {}

  get(installationId: string): QuotaSnapshot | null {
    const entry = this.store.get(installationId);
    if (!entry) return null;
    if (Date.now() > entry.expiresAt) {
      this.store.delete(installationId);
      return null;
    }
    return entry.quota;
  }

  set(installationId: string, quota: QuotaSnapshot): void {
    this.store.set(installationId, {
      quota,
      expiresAt: Date.now() + this.ttlMs,
    });
  }

  /** Invalidate cache for a specific tenant (called on plan change webhook). */
  invalidate(installationId: string): void {
    this.store.delete(installationId);
  }

  /** Clear all cached entries. */
  clear(): void {
    this.store.clear();
  }
}
```

### Orchestrator Integration

The `UsageLimitGuard` is called at the top of the orchestrator's workflow dispatch function. This is the primary enforcement point.

```typescript
// In orchestrator dispatch (conceptual integration)
async function dispatchWorkflow(context: WorkflowContext): Promise<void> {
  // CHECK LIMITS BEFORE DISPATCH
  const limitCheck = await usageLimitGuard.checkWorkflowDispatch(context.installationId);

  if (!limitCheck.allowed) {
    // For Free tier: hold workflow in pending_upgrade state
    if (limitCheck.quota.plan === 'free') {
      await workflowStore.updateStatus(context.workflowId, 'pending_upgrade');
      throw new TammaError(
        'BILLING.LIMIT_REACHED',
        limitCheck.reason!,
        { quota: limitCheck.quota },
        false, // not retryable
        'medium',
      );
    }
  }

  // Pro overage: allow but include warning in response
  if (limitCheck.quota.overage_active) {
    context.metadata.overageWarning = 'Usage exceeds base plan allocation. Overage charges will apply.';
  }

  // Proceed with workflow dispatch...
}
```

### Repo Connection Enforcement

```typescript
// In GitHub webhook handler when repos are added
async function handleInstallationRepositoriesAdded(event: WebhookEvent): Promise<void> {
  const newRepoCount = event.repositories_added.length;

  for (const repo of event.repositories_added) {
    const check = await usageLimitGuard.checkRepoConnection(installationId);
    if (!check.allowed) {
      logger.warn('Repo connection blocked by billing limit', {
        installationId,
        repo: repo.full_name,
        reason: check.reason,
      });
      // Skip this repo, notify user via GitHub comment or dashboard
      continue;
    }
    await installationStore.addRepo(installationId, repo);
  }
}
```

### Quota Endpoint

```typescript
// GET /api/v1/billing/quota
// Returns: QuotaSnapshot

app.get('/api/v1/billing/quota', async (request, reply) => {
  const user = request.user;
  const quota = await usageLimitGuard.getQuota(user.installationId);
  return reply.send(quota);
});
```

### Webhook Integration (Cache Invalidation)

When a subscription webhook fires (handled by Story 20-2's `WebhookProcessor`), the quota cache for the affected tenant must be invalidated:

```typescript
// In WebhookProcessor.handleSubscriptionUpdated()
await this.quotaCache.invalidate(installationId);
```

This ensures that after a plan change, the next limit check fetches fresh data from the database.

## Dependencies

- **Prerequisite**: Story 20-1 (plan config, plan limits)
- **Prerequisite**: Story 20-3 (usage metering service for current usage data)
- **Prerequisite**: Story 20-2 (webhook processor for cache invalidation on plan change)
- **Blocks**: Story 20-5 (billing dashboard displays quota snapshot)
- **Related**: Epic 2 (orchestrator -- primary integration point for enforcement)

## Testing Strategy

1. **Unit tests (UsageLimitGuard)**:
   - Free tier: usage below limit -> allowed
   - Free tier: usage at limit -> blocked with reason
   - Free tier: usage above limit -> blocked with reason
   - Pro tier: usage below limit -> allowed
   - Pro tier: usage above limit -> allowed with overage_active=true
   - Enterprise tier: any usage -> always allowed
   - Token budget check: projected exceeds limit for free -> blocked
   - Repo connection check: at limit -> blocked
   - Quota warning emitted at 80% threshold
2. **Unit tests (QuotaCache)**:
   - Cache hit within TTL -> returns cached value
   - Cache miss after TTL -> returns null
   - Cache invalidation -> subsequent get returns null
   - Clear empties all entries
3. **Integration test**: Full flow: create installation with Free plan, dispatch 50 workflows (all allowed), attempt 51st (blocked), upgrade to Pro, attempt again (allowed)
4. **Performance test**: 1,000 concurrent `checkWorkflowDispatch` calls with warm cache, measure p95 latency < 50ms

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/api/src/services/billing/usage-limit-guard.ts` | Create |
| `packages/api/src/services/billing/quota-cache.ts` | Create |
| `packages/api/src/services/billing/usage-limit-guard.test.ts` | Create |
| `packages/api/src/services/billing/quota-cache.test.ts` | Create |
| `packages/api/src/routes/billing/quota.ts` | Create |
| `packages/api/src/routes/billing/__tests__/quota.test.ts` | Create |
| `packages/api/src/routes/billing/index.ts` | Modify (register quota route) |
| `packages/api/src/services/billing/webhook-processor.ts` | Modify (add cache invalidation) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed the orchestrator dispatch flow to identify the correct injection point
4. Planned TDD approach (Red-Green-Refactor cycle)

### Performance Considerations

The `UsageLimitGuard.check()` method is on the hot path for every workflow dispatch. Key optimizations:
- In-memory cache with 30s TTL avoids database queries on most calls
- `Promise.all` for parallel plan + usage queries when cache is cold
- No Stripe API calls in the check path -- all data comes from local DB/cache
- Cache invalidation on plan changes ensures correctness without polling

### Pending Upgrade State

When a Free-tier workflow is blocked, it enters `pending_upgrade` state rather than being rejected outright. This is stored in the workflow store. If the tenant upgrades within the billing period, a background process releases all `pending_upgrade` workflows. This prevents data loss and reduces friction for conversion.

Implementation detail: the `WebhookProcessor` for `customer.subscription.created` should check for `pending_upgrade` workflows and re-dispatch them.

### Edge Case: Token Budget Estimation

The `checkTokenBudget()` method receives an `estimatedTokens` parameter. This is a best-effort estimate (based on prompt length and model's typical output ratio). If the actual usage differs, the difference is recorded post-call by the metering service. The guard uses the estimate to prevent obvious overshoot (e.g., a 100K token prompt when only 5K tokens remain).

### Thread Safety

The `QuotaCache` uses a simple `Map` which is safe in Node.js's single-threaded event loop. If this service is ever deployed in a worker thread pool, the cache would need to be replaced with a shared store (Redis or similar).

## Logging Requirements

- **INFO**: Workflow blocked by limit (installation_id, plan, metric, usage, limit), overage started, quota warning threshold crossed
- **DEBUG**: Limit check executed (installation_id, duration_ms, cache_hit), quota snapshot details
- **WARN**: Cache miss rate > 50% in last minute, pending_upgrade queue growing > 100
- **ERROR**: Quota check database query failed, cache corruption detected
- **Structured context**: Include `{ installationId, plan, metric, usage, limit, allowed, cacheHit, duration }` where applicable
- **Credential safety**: No sensitive data in limit enforcement (only plan/usage numbers)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-03-28 | 1.0.0   | Initial story creation | Claude |
