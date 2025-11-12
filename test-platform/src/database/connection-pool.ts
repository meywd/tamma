/**
 * Database connection pool management with environment-specific configurations
 * Provides optimized pooling, monitoring, and health checks
 */

import knex, { Knex } from 'knex';
import { metrics, createChildLogger } from '../observability/logger';
import { withDatabaseRetry, withTransactionRetry, CircuitBreaker } from './retry-logic';

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

export interface PoolStats {
  environment: string;
  used: number;
  free: number;
  total: number;
  waiting: number;
  min: number;
  max: number;
  utilization: string;
  health: 'healthy' | 'degraded' | 'critical';
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

// Environment-specific pool configurations
const POOL_CONFIGS: Record<string, Partial<PoolConfig>> = {
  development: {
    min: 1,
    max: 5,
    idleTimeoutMillis: 10000,
    acquireTimeoutMillis: 30000,
  },
  test: {
    min: 1,
    max: 3,
    idleTimeoutMillis: 5000,
    acquireTimeoutMillis: 30000,
  },
  staging: {
    min: 5,
    max: 20,
    idleTimeoutMillis: 60000,
    acquireTimeoutMillis: 45000,
  },
  production: {
    min: 10,
    max: 30,
    idleTimeoutMillis: 300000, // 5 minutes
    acquireTimeoutMillis: 60000,
    createTimeoutMillis: 60000,
  },
  'high-load': {
    min: 20,
    max: 50,
    idleTimeoutMillis: 600000, // 10 minutes
    acquireTimeoutMillis: 90000,
    createTimeoutMillis: 60000,
  },
};

export class DatabaseConnectionManager {
  private db: Knex | null = null;
  private poolConfig: PoolConfig;
  private environment: string;
  private healthCheckInterval: NodeJS.Timeout | null = null;
  private metricsInterval: NodeJS.Timeout | null = null;
  private isShuttingDown = false;
  private circuitBreaker: CircuitBreaker;
  private connectionLogger: ReturnType<typeof createChildLogger>;

  constructor(
    private config: Knex.Config,
    environment: string = process.env.NODE_ENV || 'development'
  ) {
    this.environment = environment;
    this.poolConfig = this.getPoolConfigForEnvironment(environment);
    this.circuitBreaker = new CircuitBreaker(5, 60000, 30000);
    this.connectionLogger = createChildLogger({
      component: 'connection-pool',
      environment: this.environment,
    });
  }

  /**
   * Get environment-specific pool configuration
   */
  private getPoolConfigForEnvironment(environment: string): PoolConfig {
    const envConfig = POOL_CONFIGS[environment] || {};
    return { ...DEFAULT_POOL_CONFIG, ...envConfig };
  }

  /**
   * Initialize the database connection with retry logic
   */
  async initialize(): Promise<void> {
    if (this.db) {
      this.connectionLogger.warn('Database connection already initialized');
      return;
    }

    this.connectionLogger.info('Initializing database connection pool', {
      poolConfig: this.poolConfig,
    });

    try {
      await withDatabaseRetry(async () => {
        this.db = knex({
          ...this.config,
          pool: {
            ...this.config.pool,
            ...this.poolConfig,
            // Custom pool event handlers
            afterCreate: async (conn: any, done: (err: any, conn: any) => void) => {
              // Validate connection
              try {
                await conn.query('SELECT 1');
                this.connectionLogger.debug('Database connection validated successfully');
                metrics.incrementCounter('database.connection.created');
                done(null, conn);
              } catch (err) {
                this.connectionLogger.error('Database connection validation failed', {
                  error: (err as Error).message,
                });
                metrics.incrementCounter('database.connection.validation_failed');
                done(err, conn);
              }
            },
          },
          // Connection lifecycle hooks
          acquireConnectionTimeout: this.poolConfig.acquireTimeoutMillis,
          asyncStackTraces: this.environment !== 'production',
          debug: this.environment === 'development',
        });

        // Test the connection
        await this.db.raw('SELECT 1');

        this.connectionLogger.info('Database connection pool initialized successfully');
        metrics.incrementCounter('database.pool.initialized');
      }, { operation: 'initialize' });

      // Setup monitoring
      this.setupPoolMonitoring();
      this.startHealthChecks();
      this.startMetricsCollection();

      // Setup graceful shutdown
      this.setupGracefulShutdown();
    } catch (error) {
      this.connectionLogger.error('Failed to initialize database connection', {
        error: (error as Error).message,
      });
      throw error;
    }
  }

  /**
   * Setup pool event monitoring
   */
  private setupPoolMonitoring(): void {
    if (!this.db) return;

    const pool = this.db.client.pool;

    // Monitor pool events
    pool.on('acquireRequest', (eventId: number) => {
      this.connectionLogger.trace('Pool acquire request', { eventId });
      metrics.incrementCounter('database.pool.acquire_request');
    });

    pool.on('acquireSuccess', (eventId: number, _resource: any) => {
      this.connectionLogger.trace('Pool acquire success', { eventId });
      metrics.incrementCounter('database.pool.acquire_success');
    });

    pool.on('acquireFail', (eventId: number, err: Error) => {
      this.connectionLogger.error('Pool acquire failed', {
        eventId,
        error: err.message,
      });
      metrics.incrementCounter('database.pool.acquire_fail');
    });

    pool.on('release', (_resource: any) => {
      this.connectionLogger.trace('Pool connection released');
      metrics.incrementCounter('database.pool.release');
    });

    pool.on('createRequest', (eventId: number) => {
      this.connectionLogger.debug('Pool create connection request', { eventId });
      metrics.incrementCounter('database.pool.create_request');
    });

    pool.on('createSuccess', (eventId: number, _resource: any) => {
      this.connectionLogger.debug('Pool connection created successfully', { eventId });
      metrics.incrementCounter('database.pool.create_success');
    });

    pool.on('createFail', (eventId: number, err: Error) => {
      this.connectionLogger.error('Pool connection creation failed', {
        eventId,
        error: err.message,
      });
      metrics.incrementCounter('database.pool.create_fail');
    });

    pool.on('destroyRequest', (eventId: number, _resource: any) => {
      this.connectionLogger.debug('Pool destroy connection request', { eventId });
      metrics.incrementCounter('database.pool.destroy_request');
    });

    pool.on('destroySuccess', (eventId: number, _resource: any) => {
      this.connectionLogger.debug('Pool connection destroyed successfully', { eventId });
      metrics.incrementCounter('database.pool.destroy_success');
    });

    pool.on('destroyFail', (eventId: number, _resource: any, err: Error) => {
      this.connectionLogger.error('Pool connection destroy failed', {
        eventId,
        error: err.message,
      });
      metrics.incrementCounter('database.pool.destroy_fail');
    });

    pool.on('error', (err: Error) => {
      this.connectionLogger.error('Database pool error', {
        error: err.message,
        stack: err.stack,
      });
      metrics.incrementCounter('database.pool.error');
    });
  }

  /**
   * Start periodic health checks
   */
  private startHealthChecks(): void {
    const intervalMs = this.environment === 'production' ? 30000 : 60000;

    this.healthCheckInterval = setInterval(async () => {
      if (this.isShuttingDown) return;

      try {
        await this.checkPoolHealth();
      } catch (error) {
        this.connectionLogger.error('Health check failed', {
          error: (error as Error).message,
        });
      }
    }, intervalMs);
  }

  /**
   * Start metrics collection
   */
  private startMetricsCollection(): void {
    const intervalMs = 15000; // Collect metrics every 15 seconds

    this.metricsInterval = setInterval(async () => {
      if (this.isShuttingDown) return;

      try {
        const stats = await this.getPoolStats();

        metrics.recordGauge('database.pool.connections.used', stats.used, {
          environment: this.environment,
        });
        metrics.recordGauge('database.pool.connections.free', stats.free, {
          environment: this.environment,
        });
        metrics.recordGauge('database.pool.connections.waiting', stats.waiting, {
          environment: this.environment,
        });
        metrics.recordGauge(
          'database.pool.utilization',
          parseFloat(stats.utilization),
          { environment: this.environment }
        );
      } catch (error) {
        this.connectionLogger.error('Metrics collection failed', {
          error: (error as Error).message,
        });
      }
    }, intervalMs);
  }

  /**
   * Check pool health and alert on issues
   */
  private async checkPoolHealth(): Promise<void> {
    if (!this.db) {
      this.connectionLogger.error('Database connection not initialized');
      return;
    }

    const stats = await this.getPoolStats();

    this.connectionLogger.debug('Database pool health check', stats);

    // Determine health status
    let health: 'healthy' | 'degraded' | 'critical' = 'healthy';
    const utilizationPercent = parseFloat(stats.utilization);

    if (stats.waiting > 0) {
      this.connectionLogger.warn('Database pool has waiting connections', {
        waiting: stats.waiting,
        used: stats.used,
        free: stats.free,
      });
      health = 'degraded';
    }

    if (utilizationPercent >= 90) {
      this.connectionLogger.error('Database pool near capacity', {
        utilization: stats.utilization,
        used: stats.used,
        max: stats.max,
      });
      health = 'critical';

      // Alert or trigger auto-scaling
      metrics.incrementCounter('database.pool.critical', {
        environment: this.environment,
      });
    } else if (utilizationPercent >= 75) {
      this.connectionLogger.warn('Database pool utilization high', {
        utilization: stats.utilization,
        used: stats.used,
        max: stats.max,
      });
      health = 'degraded';
    }

    // Test actual connectivity
    try {
      await this.testConnection();
    } catch (error) {
      health = 'critical';
      this.connectionLogger.error('Database connectivity test failed', {
        error: (error as Error).message,
      });
    }

    // Record health metric
    metrics.recordGauge('database.pool.health', health === 'healthy' ? 1 : health === 'degraded' ? 0.5 : 0, {
      environment: this.environment,
      status: health,
    });
  }

  /**
   * Get current connection from pool
   */
  async getConnection(): Promise<Knex> {
    if (!this.db) {
      throw new Error('Database connection not initialized. Call initialize() first.');
    }
    return this.db;
  }

  /**
   * Execute operation with connection and retry logic
   */
  async withConnection<T>(
    operation: (db: Knex) => Promise<T>,
    context: Record<string, any> = {}
  ): Promise<T> {
    return this.circuitBreaker.execute(
      () => withDatabaseRetry(
        async () => {
          const db = await this.getConnection();
          return await operation(db);
        },
        { ...context, environment: this.environment }
      ),
      context
    );
  }

  /**
   * Execute operation within a transaction with retry logic
   */
  async withTransaction<T>(
    operation: (trx: Knex.Transaction) => Promise<T>,
    context: Record<string, any> = {}
  ): Promise<T> {
    return this.circuitBreaker.execute(
      () => withTransactionRetry(
        async () => {
          const db = await this.getConnection();
          return await db.transaction(async (trx) => {
            return await operation(trx);
          });
        },
        { ...context, environment: this.environment }
      ),
      context
    );
  }

  /**
   * Test database connectivity
   */
  async testConnection(): Promise<boolean> {
    if (!this.db) {
      return false;
    }

    try {
      const startTime = Date.now();
      await this.db.raw('SELECT 1');
      const duration = Date.now() - startTime;

      this.connectionLogger.info('Database connection test successful', {
        duration,
        environment: this.environment,
      });

      metrics.recordHistogram('database.connection.test.duration', duration, {
        environment: this.environment,
        status: 'success',
      });

      return true;
    } catch (error) {
      this.connectionLogger.error('Database connection test failed', {
        environment: this.environment,
        error: (error as Error).message,
      });

      metrics.incrementCounter('database.connection.test.failure', {
        environment: this.environment,
      });

      return false;
    }
  }

  /**
   * Get pool statistics
   */
  async getPoolStats(): Promise<PoolStats> {
    if (!this.db) {
      throw new Error('Database connection not initialized');
    }

    const pool = this.db.client.pool as any;
    const used = pool.numUsed();
    const free = pool.numFree();
    const total = used + free;
    const waiting = pool.numPendingAcquires();
    const utilizationPercent = total > 0 ? (used / this.poolConfig.max) * 100 : 0;

    // Determine health based on utilization
    let health: 'healthy' | 'degraded' | 'critical' = 'healthy';
    if (utilizationPercent >= 90 || waiting > 5) {
      health = 'critical';
    } else if (utilizationPercent >= 75 || waiting > 0) {
      health = 'degraded';
    }

    return {
      environment: this.environment,
      used,
      free,
      total,
      waiting,
      min: this.poolConfig.min,
      max: this.poolConfig.max,
      utilization: utilizationPercent.toFixed(1),
      health,
    };
  }

  /**
   * Setup graceful shutdown
   */
  private setupGracefulShutdown(): void {
    const shutdownHandler = async (signal: string) => {
      this.connectionLogger.info(`Received ${signal}, starting graceful shutdown`);
      this.isShuttingDown = true;
      await this.close();
      process.exit(0);
    };

    process.on('SIGTERM', () => shutdownHandler('SIGTERM'));
    process.on('SIGINT', () => shutdownHandler('SIGINT'));
  }

  /**
   * Close all connections and cleanup
   */
  async close(): Promise<void> {
    this.isShuttingDown = true;

    // Clear intervals
    if (this.healthCheckInterval) {
      clearInterval(this.healthCheckInterval);
      this.healthCheckInterval = null;
    }

    if (this.metricsInterval) {
      clearInterval(this.metricsInterval);
      this.metricsInterval = null;
    }

    // Close database connections
    if (this.db) {
      try {
        await this.db.destroy();
        this.connectionLogger.info('Database connection pool closed', {
          environment: this.environment,
        });
        metrics.incrementCounter('database.pool.closed');
      } catch (error) {
        this.connectionLogger.error('Error closing database connections', {
          error: (error as Error).message,
        });
      }
      this.db = null;
    }
  }

  /**
   * Get circuit breaker state
   */
  getCircuitBreakerState(): string {
    return this.circuitBreaker.getState();
  }

  /**
   * Reset circuit breaker
   */
  resetCircuitBreaker(): void {
    this.circuitBreaker.reset();
    this.connectionLogger.info('Circuit breaker reset');
  }
}

// Singleton instance management
let connectionManager: DatabaseConnectionManager | null = null;

/**
 * Get or create connection manager instance
 */
export function getConnectionManager(
  config?: Knex.Config,
  environment?: string
): DatabaseConnectionManager {
  if (!connectionManager) {
    if (!config) {
      throw new Error('Database configuration required for first initialization');
    }
    connectionManager = new DatabaseConnectionManager(config, environment);
  }
  return connectionManager;
}

/**
 * Get database connection directly
 */
export async function getDatabase(): Promise<Knex> {
  if (!connectionManager) {
    throw new Error('Database connection manager not initialized');
  }
  return connectionManager.getConnection();
}

/**
 * Reset connection manager (mainly for testing)
 */
export async function resetConnectionManager(): Promise<void> {
  if (connectionManager) {
    await connectionManager.close();
    connectionManager = null;
  }
}