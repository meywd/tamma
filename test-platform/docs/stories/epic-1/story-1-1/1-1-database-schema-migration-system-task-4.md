# Implementation Plan: Task 4 - Database Optimization

**Story**: 1.1 Database Schema Migration System  
**Task**: 4 - Database Optimization  
**Acceptance Criteria**: #5, #6 - Database indexes optimized for query performance; Connection pooling and retry logic implemented

## Overview

Optimize database performance through strategic indexing, connection pooling configuration, and robust retry logic with exponential backoff.

## Implementation Steps

### Subtask 4.1: Create performance indexes for all query patterns

**Objective**: Add comprehensive indexes for optimal query performance

**Migration File**: `database/migrations/20240101000006_create_performance_indexes.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  // Organizations table indexes
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_organizations_name_trgm 
    ON organizations USING GIN (name gin_trgm_ops);
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_organizations_domain 
    ON organizations (domain) WHERE domain IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_organizations_status_created 
    ON organizations (status, created_at DESC);
  `);

  // Users table indexes
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_users_email_lower 
    ON users (LOWER(email));
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_users_name_search 
    ON users (LOWER(first_name), LOWER(last_name)) 
    WHERE first_name IS NOT NULL AND last_name IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_users_status_role 
    ON users (status, role);
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_users_login_activity 
    ON users (last_login_at DESC, current_login_at DESC) 
    WHERE status = 'active';
  `);

  // User Organizations join table indexes
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_user_orgs_user_status 
    ON user_organizations (user_id, status) 
    WHERE status IN ('active', 'pending');
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_user_orgs_org_role 
    ON user_organizations (organization_id, role) 
    WHERE status = 'active';
  `);

  // Events table - DCB pattern specific indexes
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_aggregate_timeline 
    ON events (aggregate_type, aggregate_id, aggregate_version, event_timestamp DESC);
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_type_timeline 
    ON events (event_type, event_timestamp DESC);
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_org_timeline 
    ON events (organization_id, event_timestamp DESC) 
    WHERE organization_id IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_user_timeline 
    ON events (user_id, event_timestamp DESC) 
    WHERE user_id IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_correlation 
    ON events (correlation_id, event_timestamp) 
    WHERE correlation_id IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_unprocessed 
    ON events (created_at) 
    WHERE processed = false AND retry_count < 5;
  `);

  // API Keys table indexes
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_keys_user_status 
    ON api_keys (user_id, status) 
    WHERE status IN ('active', 'inactive');
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_keys_org_status 
    ON api_keys (organization_id, status) 
    WHERE organization_id IS NOT NULL AND status IN ('active', 'inactive');
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_keys_expires 
    ON api_keys (expires_at) 
    WHERE expires_at IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_keys_usage 
    ON api_keys (last_used_at DESC) 
    WHERE status = 'active';
  `);

  // Composite indexes for common query patterns
  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_org_type_timeline 
    ON events (organization_id, event_type, event_timestamp DESC) 
    WHERE organization_id IS NOT NULL;
  `);

  await knex.raw(`
    CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_events_user_type_timeline 
    ON events (user_id, event_type, event_timestamp DESC) 
    WHERE user_id IS NOT NULL;
  `);
}

export async function down(knex: Knex): Promise<void> {
  // Drop all performance indexes
  const indexes = [
    'idx_organizations_name_trgm',
    'idx_organizations_domain',
    'idx_organizations_status_created',
    'idx_users_email_lower',
    'idx_users_name_search',
    'idx_users_status_role',
    'idx_users_login_activity',
    'idx_user_orgs_user_status',
    'idx_user_orgs_org_role',
    'idx_events_aggregate_timeline',
    'idx_events_type_timeline',
    'idx_events_org_timeline',
    'idx_events_user_timeline',
    'idx_events_correlation',
    'idx_events_unprocessed',
    'idx_api_keys_user_status',
    'idx_api_keys_org_status',
    'idx_api_keys_expires',
    'idx_api_keys_usage',
    'idx_events_org_type_timeline',
    'idx_events_user_type_timeline',
  ];

  for (const indexName of indexes) {
    await knex.raw(`DROP INDEX CONCURRENTLY IF EXISTS ${indexName}`);
  }
}
```

### Subtask 4.2: Implement connection retry logic with exponential backoff

**Objective**: Create robust connection retry mechanism for database operations

**File**: `src/database/retry-logic.ts`

```typescript
import { logger } from '../observability/logger';

export interface RetryOptions {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
}

export const DEFAULT_RETRY_OPTIONS: RetryOptions = {
  maxAttempts: 5,
  baseDelay: 1000, // 1 second
  maxDelay: 30000, // 30 seconds
  backoffMultiplier: 2,
  jitter: true,
  retryableErrors: [
    'connection timeout',
    'connection refused',
    'connection lost',
    'connection reset',
    'timeout expired',
    'database is locked',
    'too many connections',
    'connection pool exhausted',
    'ECONNRESET',
    'ECONNREFUSED',
    'ETIMEDOUT',
    'ENOTFOUND',
  ],
};

export class RetryError extends Error {
  constructor(
    message: string,
    public attempts: number,
    public totalDelay: number,
    public lastError: Error
  ) {
    super(message);
    this.name = 'RetryError';
  }
}

export async function retryWithBackoff<T>(
  operation: () => Promise<T>,
  options: Partial<RetryOptions> = {},
  context: Record<string, any> = {}
): Promise<T> {
  const opts = { ...DEFAULT_RETRY_OPTIONS, ...options };
  let lastError: Error;
  let totalDelay = 0;

  for (let attempt = 1; attempt <= opts.maxAttempts; attempt++) {
    try {
      const result = await operation();

      if (attempt > 1) {
        logger.info('Operation succeeded after retry', {
          ...context,
          attempt,
          totalDelay,
        });
      }

      return result;
    } catch (error) {
      lastError = error as Error;

      // Check if error is retryable
      const isRetryable =
        opts.retryableErrors.some((pattern) =>
          lastError.message.toLowerCase().includes(pattern.toLowerCase())
        ) ||
        (lastError as any).code === 'ECONNRESET' ||
        (lastError as any).code === 'ECONNREFUSED' ||
        (lastError as any).code === 'ETIMEDOUT';

      if (!isRetryable || attempt === opts.maxAttempts) {
        logger.error('Operation failed, not retryable or max attempts reached', {
          ...context,
          attempt,
          maxAttempts: opts.maxAttempts,
          isRetryable,
          error: lastError.message,
        });
        throw lastError;
      }

      // Calculate delay with exponential backoff
      let delay = Math.min(
        opts.baseDelay * Math.pow(opts.backoffMultiplier, attempt - 1),
        opts.maxDelay
      );

      // Add jitter to prevent thundering herd
      if (opts.jitter) {
        delay = delay * (0.5 + Math.random() * 0.5);
      }

      totalDelay += delay;

      logger.warn('Operation failed, retrying', {
        ...context,
        attempt,
        maxAttempts: opts.maxAttempts,
        delay: Math.round(delay),
        totalDelay: Math.round(totalDelay),
        error: lastError.message,
      });

      await sleep(delay);
    }
  }

  throw new RetryError(
    `Operation failed after ${opts.maxAttempts} attempts`,
    opts.maxAttempts,
    totalDelay,
    lastError!
  );
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Database-specific retry wrapper
export function withDatabaseRetry<T>(
  operation: () => Promise<T>,
  context: Record<string, any> = {}
): Promise<T> {
  return retryWithBackoff(
    operation,
    {
      maxAttempts: 3,
      baseDelay: 500,
      maxDelay: 5000,
      backoffMultiplier: 2,
    },
    { ...context, operation: 'database' }
  );
}

// Transaction retry wrapper (for serialization failures)
export function withTransactionRetry<T>(
  operation: () => Promise<T>,
  context: Record<string, any> = {}
): Promise<T> {
  return retryWithBackoff(
    operation,
    {
      maxAttempts: 5,
      baseDelay: 100,
      maxDelay: 2000,
      backoffMultiplier: 2,
      retryableErrors: [
        ...DEFAULT_RETRY_OPTIONS.retryableErrors,
        'could not serialize access',
        'deadlock detected',
        'tuple concurrently updated',
      ],
    },
    { ...context, operation: 'transaction' }
  );
}
```

### Subtask 4.3: Configure connection pool settings

**Objective**: Optimize connection pool configuration for high concurrency

**File**: `src/database/connection-pool.ts`

```typescript
import knex, { Knex } from 'knex';
import { logger } from '../observability/logger';
import { withDatabaseRetry } from './retry-logic';

export interface PoolConfig {
  min: number;
  max: number;
  acquireTimeoutMillis: number;
  createTimeoutMillis: number;
  destroyTimeoutMillis: number;
  idleTimeoutMillis: number;
  reapIntervalMillis: number;
  createRetryIntervalMillis: number;
  propagateCreateError: boolean;
}

export const DEFAULT_POOL_CONFIG: PoolConfig = {
  min: 2,
  max: 10,
  acquireTimeoutMillis: 60000,
  createTimeoutMillis: 30000,
  destroyTimeoutMillis: 5000,
  idleTimeoutMillis: 30000,
  reapIntervalMillis: 1000,
  createRetryIntervalMillis: 200,
  propagateCreateError: false,
};

export class DatabaseConnectionManager {
  private db: Knex;
  private poolConfig: PoolConfig;
  private environment: string;

  constructor(config: Knex.Config, environment: string) {
    this.environment = environment;
    this.poolConfig = this.getPoolConfigForEnvironment(environment);

    this.db = knex({
      ...config,
      pool: {
        ...config.pool,
        ...this.poolConfig,
      },
      // Enable connection validation
      afterCreate: (conn: any, done: any) => {
        conn.query('SELECT 1', (err: any) => {
          if (err) {
            logger.error('Database connection validation failed', { error: err });
          }
          done(err, conn);
        });
      },
    });

    this.setupPoolMonitoring();
  }

  private getPoolConfigForEnvironment(environment: string): PoolConfig {
    switch (environment) {
      case 'development':
        return {
          ...DEFAULT_POOL_CONFIG,
          min: 1,
          max: 5,
          idleTimeoutMillis: 10000,
        };

      case 'test':
        return {
          ...DEFAULT_POOL_CONFIG,
          min: 1,
          max: 3,
          idleTimeoutMillis: 5000,
          acquireTimeoutMillis: 30000,
        };

      case 'staging':
        return {
          ...DEFAULT_POOL_CONFIG,
          min: 5,
          max: 20,
          idleTimeoutMillis: 60000,
          acquireTimeoutMillis: 45000,
        };

      case 'production':
        return {
          ...DEFAULT_POOL_CONFIG,
          min: 10,
          max: 30,
          idleTimeoutMillis: 300000, // 5 minutes
          acquireTimeoutMillis: 60000,
          createTimeoutMillis: 60000,
        };

      default:
        return DEFAULT_POOL_CONFIG;
    }
  }

  private setupPoolMonitoring(): void {
    // Monitor pool events
    this.db.client.pool.on('acquire', (resourceId: string) => {
      logger.debug('Database connection acquired', { resourceId });
    });

    this.db.client.pool.on('release', (resourceId: string) => {
      logger.debug('Database connection released', { resourceId });
    });

    this.db.client.pool.on('destroy', (resourceId: string) => {
      logger.debug('Database connection destroyed', { resourceId });
    });

    this.db.client.pool.on('error', (err: Error) => {
      logger.error('Database pool error', { error: err.message });
    });

    // Periodic pool health check
    setInterval(() => {
      this.checkPoolHealth();
    }, 30000); // Every 30 seconds
  }

  private async checkPoolHealth(): Promise<void> {
    try {
      const pool = this.db.client.pool as any;
      const used = pool.numUsed();
      const free = pool.numFree();
      const total = pool.numUsed() + pool.numFree();
      const waiting = pool.numPendingAcquires();

      logger.debug('Database pool status', {
        environment: this.environment,
        used,
        free,
        total,
        waiting,
        min: this.poolConfig.min,
        max: this.poolConfig.max,
      });

      // Alert if pool is under pressure
      if (waiting > 0) {
        logger.warn('Database pool under pressure', {
          environment: this.environment,
          waiting,
          used,
          free,
          total,
        });
      }

      // Alert if pool is at capacity
      if (used >= this.poolConfig.max * 0.9) {
        logger.warn('Database pool near capacity', {
          environment: this.environment,
          used,
          max: this.poolConfig.max,
          utilization: ((used / this.poolConfig.max) * 100).toFixed(1) + '%',
        });
      }
    } catch (error) {
      logger.error('Failed to check pool health', { error });
    }
  }

  async getConnection(): Promise<Knex> {
    return this.db;
  }

  async withConnection<T>(
    operation: (db: Knex) => Promise<T>,
    context: Record<string, any> = {}
  ): Promise<T> {
    return withDatabaseRetry(
      async () => {
        const db = await this.getConnection();
        return await operation(db);
      },
      { ...context, environment: this.environment }
    );
  }

  async withTransaction<T>(
    operation: (trx: Knex.Transaction) => Promise<T>,
    context: Record<string, any> = {}
  ): Promise<T> {
    return withDatabaseRetry(
      async () => {
        return await this.db.transaction(async (trx) => {
          return await operation(trx);
        });
      },
      { ...context, environment: this.environment, operation: 'transaction' }
    );
  }

  async testConnection(): Promise<boolean> {
    try {
      await this.db.raw('SELECT 1');
      logger.info('Database connection test successful', {
        environment: this.environment,
      });
      return true;
    } catch (error) {
      logger.error('Database connection test failed', {
        environment: this.environment,
        error,
      });
      return false;
    }
  }

  async getPoolStats(): Promise<Record<string, any>> {
    const pool = this.db.client.pool as any;
    return {
      environment: this.environment,
      used: pool.numUsed(),
      free: pool.numFree(),
      total: pool.numUsed() + pool.numFree(),
      waiting: pool.numPendingAcquires(),
      min: this.poolConfig.min,
      max: this.poolConfig.max,
      utilization: ((pool.numUsed() / this.poolConfig.max) * 100).toFixed(1) + '%',
    };
  }

  async close(): Promise<void> {
    await this.db.destroy();
    logger.info('Database connection pool closed', {
      environment: this.environment,
    });
  }
}

// Singleton instance
let connectionManager: DatabaseConnectionManager | null = null;

export function getConnectionManager(
  config: Knex.Config,
  environment: string
): DatabaseConnectionManager {
  if (!connectionManager) {
    connectionManager = new DatabaseConnectionManager(config, environment);
  }
  return connectionManager;
}

export function getDatabase(): Knex {
  if (!connectionManager) {
    throw new Error('Database connection manager not initialized');
  }
  return connectionManager.getConnection();
}
```

## Files to Create

1. `database/migrations/20240101000006_create_performance_indexes.ts`
2. `src/database/retry-logic.ts`
3. `src/database/connection-pool.ts`
4. Update `src/database/connection.ts` to use new connection manager

## Dependencies

- PostgreSQL 17
- Knex.js
- pg-pool (built into pg)
- Logger from observability package

## Testing

1. Performance benchmarking with and without indexes
2. Connection pool stress testing
3. Retry logic validation with simulated failures
4. Pool monitoring and alerting tests
5. Load testing with concurrent connections

## Notes

- Use CONCURRENTLY for index creation in production
- Monitor pool metrics and set up alerts
- Test retry logic with various failure scenarios
- Consider connection pool sizing based on application load
- Implement circuit breaker pattern for extreme failure scenarios
