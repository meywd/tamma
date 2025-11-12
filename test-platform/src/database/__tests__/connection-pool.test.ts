/**
 * Tests for database connection pool management
 */

import { Knex } from 'knex';
import {
  DatabaseConnectionManager,
  DEFAULT_POOL_CONFIG,
  PoolConfig,
  PoolStats,
} from '../connection-pool';

// Mock knex module
jest.mock('knex');

// Mock logger
jest.mock('../../observability/logger', () => ({
  logger: {
    info: jest.fn(),
    warn: jest.fn(),
    error: jest.fn(),
    debug: jest.fn(),
    trace: jest.fn(),
  },
  metrics: {
    incrementCounter: jest.fn(),
    recordHistogram: jest.fn(),
    recordGauge: jest.fn(),
  },
  createChildLogger: jest.fn(() => ({
    info: jest.fn(),
    warn: jest.fn(),
    error: jest.fn(),
    debug: jest.fn(),
    trace: jest.fn(),
  })),
}));

// Mock retry logic
jest.mock('../retry-logic', () => ({
  withDatabaseRetry: jest.fn((fn) => fn()),
  withTransactionRetry: jest.fn((fn) => fn()),
  CircuitBreaker: jest.fn().mockImplementation(() => ({
    execute: jest.fn((fn) => fn()),
    getState: jest.fn(() => 'CLOSED'),
    reset: jest.fn(),
  })),
}));

describe('DatabaseConnectionManager', () => {
  let mockKnex: jest.Mocked<Knex>;
  let mockPool: any;

  beforeEach(() => {
    // Mock pool object
    mockPool = {
      numUsed: jest.fn(() => 5),
      numFree: jest.fn(() => 15),
      numPendingAcquires: jest.fn(() => 0),
      numPendingCreates: jest.fn(() => 0),
      on: jest.fn(),
    };

    // Mock knex instance
    mockKnex = {
      raw: jest.fn().mockResolvedValue({ rows: [{ '?column?': 1 }] }),
      destroy: jest.fn().mockResolvedValue(undefined),
      transaction: jest.fn((cb) => {
        const mockTrx = {} as Knex.Transaction;
        return cb(mockTrx);
      }),
      client: {
        pool: mockPool,
      },
    } as any;

    // Mock knex constructor
    const knex = require('knex');
    knex.default = jest.fn(() => mockKnex);
  });

  afterEach(() => {
    jest.clearAllMocks();
  });

  describe('Constructor and Initialization', () => {
    it('should create manager with default configuration', () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {
          host: 'localhost',
          port: 5432,
          database: 'test',
          user: 'test',
          password: 'test',
        },
      };

      const manager = new DatabaseConnectionManager(config, 'development');
      expect(manager).toBeDefined();
    });

    it('should use environment-specific pool configuration', () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const developmentManager = new DatabaseConnectionManager(config, 'development');
      const productionManager = new DatabaseConnectionManager(config, 'production');

      expect(developmentManager['poolConfig'].max).toBe(5);
      expect(productionManager['poolConfig'].max).toBe(30);
    });

    it('should initialize database connection successfully', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      expect(mockKnex.raw).toHaveBeenCalledWith('SELECT 1');
    });

    it('should handle initialization failure', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      mockKnex.raw.mockRejectedValueOnce(new Error('Connection failed'));

      const manager = new DatabaseConnectionManager(config, 'test');
      await expect(manager.initialize()).rejects.toThrow('Connection failed');
    });

    it('should not reinitialize if already initialized', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const knex = require('knex');
      const callCount = knex.default.mock.calls.length;

      await manager.initialize();
      expect(knex.default.mock.calls.length).toBe(callCount);
    });
  });

  describe('Connection Management', () => {
    it('should get connection after initialization', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const connection = await manager.getConnection();
      expect(connection).toBe(mockKnex);
    });

    it('should throw error when getting connection before initialization', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await expect(manager.getConnection()).rejects.toThrow(
        'Database connection not initialized'
      );
    });

    it('should test connection successfully', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const result = await manager.testConnection();
      expect(result).toBe(true);
      expect(mockKnex.raw).toHaveBeenCalledWith('SELECT 1');
    });

    it('should handle connection test failure', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      mockKnex.raw.mockRejectedValueOnce(new Error('Connection lost'));

      const result = await manager.testConnection();
      expect(result).toBe(false);
    });
  });

  describe('Pool Monitoring', () => {
    it('should get pool statistics', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const stats = await manager.getPoolStats();

      expect(stats).toEqual({
        environment: 'test',
        used: 5,
        free: 15,
        total: 20,
        waiting: 0,
        min: 1,
        max: 3,
        utilization: '166.7',
        health: 'critical',
      });
    });

    it('should determine health status correctly', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      // Test healthy status
      mockPool.numUsed.mockReturnValue(1);
      mockPool.numFree.mockReturnValue(2);
      mockPool.numPendingAcquires.mockReturnValue(0);

      let stats = await manager.getPoolStats();
      expect(stats.health).toBe('healthy');

      // Test degraded status (75% utilization)
      mockPool.numUsed.mockReturnValue(2);
      mockPool.numFree.mockReturnValue(1);

      stats = await manager.getPoolStats();
      expect(stats.health).toBe('degraded');

      // Test critical status (90% utilization)
      mockPool.numUsed.mockReturnValue(3);
      mockPool.numFree.mockReturnValue(0);

      stats = await manager.getPoolStats();
      expect(stats.health).toBe('critical');

      // Test critical status (waiting connections)
      mockPool.numUsed.mockReturnValue(2);
      mockPool.numFree.mockReturnValue(1);
      mockPool.numPendingAcquires.mockReturnValue(6);

      stats = await manager.getPoolStats();
      expect(stats.health).toBe('critical');
    });

    it('should setup pool event monitoring', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      expect(mockPool.on).toHaveBeenCalledWith('acquireRequest', expect.any(Function));
      expect(mockPool.on).toHaveBeenCalledWith('acquireSuccess', expect.any(Function));
      expect(mockPool.on).toHaveBeenCalledWith('acquireFail', expect.any(Function));
      expect(mockPool.on).toHaveBeenCalledWith('release', expect.any(Function));
      expect(mockPool.on).toHaveBeenCalledWith('error', expect.any(Function));
    });
  });

  describe('Transaction Management', () => {
    it('should execute operation with connection', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const operation = jest.fn().mockResolvedValue('result');
      const result = await manager.withConnection(operation);

      expect(result).toBe('result');
      expect(operation).toHaveBeenCalledWith(mockKnex);
    });

    it('should execute transaction operation', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const operation = jest.fn().mockResolvedValue('tx-result');
      const result = await manager.withTransaction(operation);

      expect(result).toBe('tx-result');
      expect(mockKnex.transaction).toHaveBeenCalled();
    });
  });

  describe('Circuit Breaker Integration', () => {
    it('should get circuit breaker state', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      const state = manager.getCircuitBreakerState();
      expect(state).toBe('CLOSED');
    });

    it('should reset circuit breaker', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      manager.resetCircuitBreaker();
      expect(manager['circuitBreaker'].reset).toHaveBeenCalled();
    });
  });

  describe('Cleanup', () => {
    it('should close connections properly', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      await manager.close();

      expect(mockKnex.destroy).toHaveBeenCalled();
    });

    it('should handle close errors gracefully', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      mockKnex.destroy.mockRejectedValueOnce(new Error('Close failed'));

      // Should not throw
      await manager.close();
    });

    it('should clean up intervals on close', async () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      jest.useFakeTimers();
      const clearIntervalSpy = jest.spyOn(global, 'clearInterval');

      const manager = new DatabaseConnectionManager(config, 'test');
      await manager.initialize();

      // Advance timers to create intervals
      jest.advanceTimersByTime(1000);

      await manager.close();

      expect(clearIntervalSpy).toHaveBeenCalled();

      jest.useRealTimers();
    });
  });

  describe('Environment Configurations', () => {
    const testEnvironments = [
      { env: 'development', expectedMin: 1, expectedMax: 5 },
      { env: 'test', expectedMin: 1, expectedMax: 3 },
      { env: 'staging', expectedMin: 5, expectedMax: 20 },
      { env: 'production', expectedMin: 10, expectedMax: 30 },
      { env: 'high-load', expectedMin: 20, expectedMax: 50 },
    ];

    testEnvironments.forEach(({ env, expectedMin, expectedMax }) => {
      it(`should use correct pool config for ${env} environment`, () => {
        const config: Knex.Config = {
          client: 'postgresql',
          connection: {},
        };

        const manager = new DatabaseConnectionManager(config, env);
        const poolConfig = manager['poolConfig'];

        expect(poolConfig.min).toBe(expectedMin);
        expect(poolConfig.max).toBe(expectedMax);
      });
    });

    it('should use default config for unknown environment', () => {
      const config: Knex.Config = {
        client: 'postgresql',
        connection: {},
      };

      const manager = new DatabaseConnectionManager(config, 'unknown-env');
      const poolConfig = manager['poolConfig'];

      expect(poolConfig.min).toBe(DEFAULT_POOL_CONFIG.min);
      expect(poolConfig.max).toBe(DEFAULT_POOL_CONFIG.max);
    });
  });
});