/**
 * Database connection configuration and initialization
 * Integrates with connection pool manager for optimized performance and resilience
 */

import { Knex } from 'knex';
import { getConnectionManager, DatabaseConnectionManager } from './connection-pool';
import { logger } from '../observability/logger';
import knexConfig from '../../knexfile';

// Get the current environment
const environment = process.env.NODE_ENV || 'development';

// Database connection manager instance
let connectionManager: DatabaseConnectionManager | null = null;
let db: Knex | null = null;

/**
 * Initialize the database connection with pool manager
 */
export async function initializeDatabase(): Promise<void> {
  try {
    // Get configuration for current environment
    const config = knexConfig[environment];

    if (!config) {
      const error = new Error(`No database configuration found for environment: ${environment}`);
      logger.error('Database configuration error', { error: error.message, environment });
      throw error;
    }

    logger.info('Initializing database connection', {
      environment,
      host: typeof config.connection === 'object' && 'host' in config.connection ? config.connection.host : 'unknown',
      database: typeof config.connection === 'object' && 'database' in config.connection ? config.connection.database : 'unknown',
    });

    // Get connection manager instance
    connectionManager = getConnectionManager(config, environment);

    // Initialize the connection pool
    await connectionManager.initialize();

    // Get the database connection
    db = await connectionManager.getConnection();

    // Test the connection
    const isConnected = await connectionManager.testConnection();
    if (!isConnected) {
      throw new Error('Database connection test failed');
    }

    logger.info('Database connection established successfully', {
      environment,
      poolStats: await connectionManager.getPoolStats(),
    });
  } catch (error) {
    logger.error('Failed to initialize database', {
      error: error instanceof Error ? error.message : String(error),
      environment,
    });
    throw error;
  }
}

/**
 * Get the database connection (initialize if needed)
 */
export async function getDatabase(): Promise<Knex> {
  if (!db || !connectionManager) {
    await initializeDatabase();
  }

  if (!db) {
    throw new Error('Database connection not available');
  }

  return db;
}

// Connection health check
export async function testConnection(): Promise<boolean> {
  try {
    if (!connectionManager) {
      await initializeDatabase();
    }

    return await connectionManager!.testConnection();
  } catch (error) {
    logger.error('Database connection test failed', {
      error: error instanceof Error ? error.message : String(error),
      environment,
    });
    return false;
  }
}

// Get connection statistics with enhanced pool information
export async function getConnectionStats(): Promise<{
  numUsed: number;
  numFree: number;
  numPendingAcquires: number;
  numPendingCreates: number;
  poolHealth?: string;
  circuitBreakerState?: string;
}> {
  if (!connectionManager) {
    await initializeDatabase();
  }

  const poolStats = await connectionManager!.getPoolStats();
  const circuitBreakerState = connectionManager!.getCircuitBreakerState();

  return {
    numUsed: poolStats.used,
    numFree: poolStats.free,
    numPendingAcquires: poolStats.waiting,
    numPendingCreates: 0, // Not directly available in new implementation
    poolHealth: poolStats.health,
    circuitBreakerState,
  };
}

// Close database connection
export async function closeConnection(): Promise<void> {
  try {
    if (connectionManager) {
      await connectionManager.close();
      connectionManager = null;
      db = null;
      logger.info('Database connection closed successfully');
    }
  } catch (error) {
    logger.error('Error closing database connection', {
      error: error instanceof Error ? error.message : String(error),
    });
    throw error;
  }
}

// Transaction helper with retry logic
export async function withTransaction<T>(
  callback: (trx: Knex.Transaction) => Promise<T>,
  context?: Record<string, any>
): Promise<T> {
  if (!connectionManager) {
    await initializeDatabase();
  }

  return connectionManager!.withTransaction(callback, context);
}

// Execute operation with connection retry logic
export async function withConnection<T>(
  operation: (db: Knex) => Promise<T>,
  context?: Record<string, any>
): Promise<T> {
  if (!connectionManager) {
    await initializeDatabase();
  }

  return connectionManager!.withConnection(operation, context);
}

// Export default database getter for backward compatibility
export default {
  get raw() {
    if (!db) {
      throw new Error('Database not initialized. Call initializeDatabase() first.');
    }
    return db.raw.bind(db);
  },

  get schema() {
    if (!db) {
      throw new Error('Database not initialized. Call initializeDatabase() first.');
    }
    return db.schema;
  },

  get client() {
    if (!db) {
      throw new Error('Database not initialized. Call initializeDatabase() first.');
    }
    return db.client;
  },

  // Proxy other database methods
  async transaction(callback: (trx: Knex.Transaction) => Promise<any>) {
    return withTransaction(callback);
  },

  async destroy() {
    return closeConnection();
  }
};

// Export types for use in other modules
export type Database = Knex;
export type Transaction = Knex.Transaction;
export type QueryBuilder = Knex.QueryBuilder;

// Re-export utilities from connection pool and retry logic
export { DatabaseConnectionManager } from './connection-pool';
export { withDatabaseRetry, withTransactionRetry, RetryError } from './retry-logic';
export type { PoolConfig, PoolStats } from './connection-pool';
export type { RetryOptions } from './retry-logic';