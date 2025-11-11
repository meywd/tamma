# Task 6: Health Monitoring

## Overview

Implement comprehensive health monitoring and alerting system for the Tamma platform with service health checks, metrics collection, performance monitoring, and automated alerting capabilities.

## Objectives

- Create health check endpoints for all services
- Implement system metrics collection and monitoring
- Set up automated alerting for critical issues
- Create dashboard for system health visualization
- Implement distributed tracing for request tracking
- Set up uptime monitoring and SLA tracking

## Implementation Steps

### Step 1: Health Check Framework

Create comprehensive health check system:

```typescript
// packages/shared/src/health/health-checker.ts
import { EventEmitter } from 'events';

export interface HealthCheckResult {
  status: 'healthy' | 'unhealthy' | 'degraded';
  timestamp: string;
  duration: number;
  message?: string;
  details: Record<string, unknown>;
  error?: string;
}

export interface HealthCheck {
  name: string;
  timeout: number;
  interval: number;
  critical: boolean;
  check: () => Promise<HealthCheckResult>;
}

export interface ServiceHealth {
  service: string;
  status: 'healthy' | 'unhealthy' | 'degraded';
  checks: Record<string, HealthCheckResult>;
  lastUpdated: string;
  uptime: number;
  version: string;
}

export class HealthChecker extends EventEmitter {
  private checks: Map<string, HealthCheck> = new Map();
  private results: Map<string, HealthCheckResult> = new Map();
  private intervals: Map<string, NodeJS.Timeout> = new Map();
  private startTime: number = Date.now();

  constructor(
    private serviceName: string,
    private version: string
  ) {
    super();
  }

  registerCheck(check: HealthCheck): void {
    this.checks.set(check.name, check);
    this.startCheck(check);
  }

  private startCheck(check: HealthCheck): void {
    // Run immediately
    this.runCheck(check);

    // Schedule recurring checks
    const interval = setInterval(() => {
      this.runCheck(check);
    }, check.interval);

    this.intervals.set(check.name, interval);
  }

  private async runCheck(check: HealthCheck): Promise<void> {
    const startTime = Date.now();

    try {
      const result = await Promise.race([
        check.check(),
        new Promise<HealthCheckResult>((_, reject) =>
          setTimeout(() => reject(new Error('Health check timeout')), check.timeout)
        ),
      ]);

      const healthResult: HealthCheckResult = {
        ...result,
        duration: Date.now() - startTime,
        timestamp: new Date().toISOString(),
      };

      this.results.set(check.name, healthResult);
      this.emit('checkCompleted', check.name, healthResult);

      // Emit alerts for critical failures
      if (check.critical && healthResult.status === 'unhealthy') {
        this.emit('criticalFailure', check.name, healthResult);
      }
    } catch (error) {
      const healthResult: HealthCheckResult = {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'Health check failed',
        details: {},
        error: error instanceof Error ? error.message : 'Unknown error',
      };

      this.results.set(check.name, healthResult);
      this.emit('checkCompleted', check.name, healthResult);

      if (check.critical) {
        this.emit('criticalFailure', check.name, healthResult);
      }
    }
  }

  async runCheckOnce(name: string): Promise<HealthCheckResult> {
    const check = this.checks.get(name);
    if (!check) {
      throw new Error(`Health check '${name}' not found`);
    }

    const startTime = Date.now();

    try {
      const result = await Promise.race([
        check.check(),
        new Promise<HealthCheckResult>((_, reject) =>
          setTimeout(() => reject(new Error('Health check timeout')), check.timeout)
        ),
      ]);

      return {
        ...result,
        duration: Date.now() - startTime,
        timestamp: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'Health check failed',
        details: {},
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  getHealth(): ServiceHealth {
    const checks: Record<string, HealthCheckResult> = {};
    let hasUnhealthy = false;
    let hasDegraded = false;

    for (const [name, result] of this.results) {
      checks[name] = result;
      if (result.status === 'unhealthy') hasUnhealthy = true;
      if (result.status === 'degraded') hasDegraded = true;
    }

    const status = hasUnhealthy ? 'unhealthy' : hasDegraded ? 'degraded' : 'healthy';

    return {
      service: this.serviceName,
      status,
      checks,
      lastUpdated: new Date().toISOString(),
      uptime: Date.now() - this.startTime,
      version: this.version,
    };
  }

  async runAllChecks(): Promise<Record<string, HealthCheckResult>> {
    const results: Record<string, HealthCheckResult> = {};

    const promises = Array.from(this.checks.entries()).map(async ([name]) => {
      results[name] = await this.runCheckOnce(name);
    });

    await Promise.allSettled(promises);
    return results;
  }

  stop(): void {
    for (const interval of this.intervals.values()) {
      clearInterval(interval);
    }
    this.intervals.clear();
  }
}
```

### Step 2: Built-in Health Checks

Implement common health checks:

```typescript
// packages/shared/src/health/checks.ts
import { HealthCheckResult } from './health-checker';
import { Pool } from 'pg';
import Redis from 'ioredis';

// Database health check
export function createDatabaseHealthCheck(pool: Pool): () => Promise<HealthCheckResult> {
  return async (): Promise<HealthCheckResult> => {
    const startTime = Date.now();

    try {
      const result = await pool.query('SELECT 1 as health_check');
      const duration = Date.now() - startTime;

      return {
        status: 'healthy',
        timestamp: new Date().toISOString(),
        duration,
        message: 'Database connection successful',
        details: {
          rowCount: result.rowCount,
          connectionPool: {
            totalCount: pool.totalCount,
            idleCount: pool.idleCount,
            waitingCount: pool.waitingCount,
          },
        },
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'Database connection failed',
        details: {},
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  };
}

// Redis health check
export function createRedisHealthCheck(redis: Redis): () => Promise<HealthCheckResult> {
  return async (): Promise<HealthCheckResult> => {
    const startTime = Date.now();

    try {
      const result = await redis.ping();
      const duration = Date.now() - startTime;

      if (result === 'PONG') {
        const info = await redis.info('memory');
        const memoryMatch = info.match(/used_memory:(\d+)/);
        const usedMemory = memoryMatch ? parseInt(memoryMatch[1]) : 0;

        return {
          status: 'healthy',
          timestamp: new Date().toISOString(),
          duration,
          message: 'Redis connection successful',
          details: {
            response: result,
            usedMemory,
            connectedClients: redis.status === 'ready' ? 1 : 0,
          },
        };
      } else {
        return {
          status: 'unhealthy',
          timestamp: new Date().toISOString(),
          duration,
          message: 'Unexpected Redis response',
          details: { response: result },
        };
      }
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'Redis connection failed',
        details: {},
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  };
}

// HTTP endpoint health check
export function createHttpHealthCheck(
  url: string,
  expectedStatus: number = 200,
  timeout: number = 5000
): () => Promise<HealthCheckResult> {
  return async (): Promise<HealthCheckResult> => {
    const startTime = Date.now();

    try {
      const response = await fetch(url, {
        method: 'GET',
        signal: AbortSignal.timeout(timeout),
      });

      const duration = Date.now() - startTime;

      if (response.status === expectedStatus) {
        return {
          status: 'healthy',
          timestamp: new Date().toISOString(),
          duration,
          message: `HTTP endpoint healthy (${response.status})`,
          details: {
            url,
            status: response.status,
            statusText: response.statusText,
            responseTime: duration,
          },
        };
      } else {
        return {
          status: 'degraded',
          timestamp: new Date().toISOString(),
          duration,
          message: `HTTP endpoint returned unexpected status (${response.status})`,
          details: {
            url,
            status: response.status,
            statusText: response.statusText,
            expectedStatus,
          },
        };
      }
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'HTTP endpoint check failed',
        details: { url },
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  };
}

// Disk space health check
export function createDiskSpaceHealthCheck(
  path: string,
  threshold: number = 0.9
): () => Promise<HealthCheckResult> {
  return async (): Promise<HealthCheckResult> => {
    const startTime = Date.now();

    try {
      const fs = require('fs').promises;
      const stats = await fs.statfs(path);

      const total = stats.blocks * stats.bsize;
      const free = stats.bavail * stats.bsize;
      const used = total - free;
      const usageRatio = used / total;

      const duration = Date.now() - startTime;
      const status =
        usageRatio > threshold
          ? 'unhealthy'
          : usageRatio > threshold * 0.8
            ? 'degraded'
            : 'healthy';

      return {
        status,
        timestamp: new Date().toISOString(),
        duration,
        message: `Disk usage: ${(usageRatio * 100).toFixed(2)}%`,
        details: {
          path,
          total,
          used,
          free,
          usageRatio,
          threshold,
        },
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        timestamp: new Date().toISOString(),
        duration: Date.now() - startTime,
        message: 'Failed to check disk space',
        details: { path },
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  };
}

// Memory usage health check
export function createMemoryHealthCheck(threshold: number = 0.9): () => Promise<HealthCheckResult> {
  return (): HealthCheckResult => {
    const memUsage = process.memoryUsage();
    const totalMem = require('os').totalmem();
    const freeMem = require('os').freemem();
    const usedMem = totalMem - freeMem;
    const usageRatio = usedMem / totalMem;

    const status =
      usageRatio > threshold ? 'unhealthy' : usageRatio > threshold * 0.8 ? 'degraded' : 'healthy';

    return {
      status,
      timestamp: new Date().toISOString(),
      duration: 0,
      message: `Memory usage: ${(usageRatio * 100).toFixed(2)}%`,
      details: {
        process: memUsage,
        system: {
          total: totalMem,
          used: usedMem,
          free: freeMem,
          usageRatio,
        },
        threshold,
      },
    };
  };
}

// CPU usage health check
export function createCpuHealthCheck(threshold: number = 0.8): () => Promise<HealthCheckResult> {
  let lastCpuUsage = process.cpuUsage();

  return (): HealthCheckResult => {
    const currentCpuUsage = process.cpuUsage(lastCpuUsage);
    const totalUsage = currentCpuUsage.user + currentCpuUsage.system;
    const uptime = process.uptime();
    const usagePercent = (totalUsage / (uptime * 1000000)) * 100;

    lastCpuUsage = process.cpuUsage();

    const status =
      usagePercent > threshold * 100
        ? 'unhealthy'
        : usagePercent > threshold * 80
          ? 'degraded'
          : 'healthy';

    return {
      status,
      timestamp: new Date().toISOString(),
      duration: 0,
      message: `CPU usage: ${usagePercent.toFixed(2)}%`,
      details: {
        usagePercent,
        user: currentCpuUsage.user,
        system: currentCpuUsage.system,
        uptime,
        threshold,
      },
    };
  };
}
```

### Step 3: Health Check API Endpoints

Create HTTP endpoints for health monitoring:

```typescript
// packages/shared/src/health/routes.ts
import { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import { HealthChecker } from './health-checker';

export class HealthRoutes {
  constructor(private healthChecker: HealthChecker) {}

  async registerRoutes(fastify: FastifyInstance): Promise<void> {
    // Basic health check
    fastify.get(
      '/health',
      {
        schema: {
          description: 'Basic health check',
          tags: ['health'],
          response: {
            200: {
              type: 'object',
              properties: {
                status: { type: 'string', enum: ['healthy', 'unhealthy', 'degraded'] },
                timestamp: { type: 'string' },
                uptime: { type: 'number' },
                version: { type: 'string' },
              },
            },
            503: {
              type: 'object',
              properties: {
                status: { type: 'string' },
                timestamp: { type: 'string' },
                error: { type: 'string' },
              },
            },
          },
        },
      },
      async (request, reply) => {
        const health = this.healthChecker.getHealth();

        const statusCode =
          health.status === 'healthy' ? 200 : health.status === 'degraded' ? 200 : 503;

        reply.code(statusCode).send({
          status: health.status,
          timestamp: health.lastUpdated,
          uptime: health.uptime,
          version: health.version,
        });
      }
    );

    // Detailed health check
    fastify.get(
      '/health/detailed',
      {
        schema: {
          description: 'Detailed health check with all components',
          tags: ['health'],
          response: {
            200: {
              type: 'object',
              properties: {
                service: { type: 'string' },
                status: { type: 'string' },
                lastUpdated: { type: 'string' },
                uptime: { type: 'number' },
                version: { type: 'string' },
                checks: {
                  type: 'object',
                  additionalProperties: {
                    type: 'object',
                    properties: {
                      status: { type: 'string' },
                      timestamp: { type: 'string' },
                      duration: { type: 'number' },
                      message: { type: 'string' },
                      details: { type: 'object' },
                      error: { type: 'string' },
                    },
                  },
                },
              },
            },
          },
        },
      },
      async (request, reply) => {
        const health = this.healthChecker.getHealth();

        const statusCode =
          health.status === 'healthy' ? 200 : health.status === 'degraded' ? 200 : 503;

        reply.code(statusCode).send(health);
      }
    );

    // Run specific health check
    fastify.get(
      '/health/check/:name',
      {
        schema: {
          description: 'Run a specific health check',
          tags: ['health'],
          params: {
            type: 'object',
            properties: {
              name: { type: 'string' },
            },
          },
          response: {
            200: {
              type: 'object',
              properties: {
                status: { type: 'string' },
                timestamp: { type: 'string' },
                duration: { type: 'number' },
                message: { type: 'string' },
                details: { type: 'object' },
                error: { type: 'string' },
              },
            },
            404: {
              type: 'object',
              properties: {
                error: { type: 'string' },
              },
            },
          },
        },
      },
      async (request: FastifyRequest<{ Params: { name: string } }>, reply) => {
        try {
          const result = await this.healthChecker.runCheckOnce(request.params.name);
          const statusCode = result.status === 'healthy' ? 200 : 503;

          reply.code(statusCode).send(result);
        } catch (error) {
          reply.code(404).send({
            error: error instanceof Error ? error.message : 'Health check not found',
          });
        }
      }
    );

    // Health check metrics
    fastify.get(
      '/health/metrics',
      {
        schema: {
          description: 'Health check metrics and statistics',
          tags: ['health'],
        },
      },
      async (request, reply) => {
        const health = this.healthChecker.getHealth();
        const metrics = this.calculateMetrics(health);

        reply.send(metrics);
      }
    );

    // Readiness probe (Kubernetes)
    fastify.get(
      '/ready',
      {
        schema: {
          description: 'Readiness probe for Kubernetes',
          tags: ['health'],
        },
      },
      async (request, reply) => {
        const health = this.healthChecker.getHealth();
        const isReady = health.status !== 'unhealthy';

        reply.code(isReady ? 200 : 503).send({
          ready: isReady,
          status: health.status,
          timestamp: health.lastUpdated,
        });
      }
    );

    // Liveness probe (Kubernetes)
    fastify.get(
      '/live',
      {
        schema: {
          description: 'Liveness probe for Kubernetes',
          tags: ['health'],
        },
      },
      async (request, reply) => {
        // Basic liveness check - service is running
        reply.code(200).send({
          alive: true,
          timestamp: new Date().toISOString(),
        });
      }
    );
  }

  private calculateMetrics(health: any): Record<string, any> {
    const checks = Object.values(health.checks) as any[];
    const totalChecks = checks.length;
    const healthyChecks = checks.filter((c) => c.status === 'healthy').length;
    const unhealthyChecks = checks.filter((c) => c.status === 'unhealthy').length;
    const degradedChecks = checks.filter((c) => c.status === 'degraded').length;

    const durations = checks.map((c) => c.duration).filter((d) => d > 0);
    const avgDuration =
      durations.length > 0 ? durations.reduce((sum, d) => sum + d, 0) / durations.length : 0;

    return {
      summary: {
        totalChecks,
        healthyChecks,
        unhealthyChecks,
        degradedChecks,
        healthPercentage: totalChecks > 0 ? (healthyChecks / totalChecks) * 100 : 0,
      },
      performance: {
        averageCheckDuration: avgDuration,
        slowestCheck: Math.max(...durations, 0),
        fastestCheck: durations.length > 0 ? Math.min(...durations) : 0,
      },
      uptime: health.uptime,
      lastUpdated: health.lastUpdated,
    };
  }
}
```

### Step 4: Metrics Collection System

Implement comprehensive metrics collection:

```typescript
// packages/shared/src/monitoring/metrics-collector.ts
import { EventEmitter } from 'events';
import { performance } from 'perf_hooks';

export interface Metric {
  name: string;
  value: number;
  timestamp: string;
  tags?: Record<string, string>;
  type: 'counter' | 'gauge' | 'histogram' | 'timer';
  unit?: string;
}

export interface MetricAggregation {
  name: string;
  type: string;
  count: number;
  sum: number;
  min: number;
  max: number;
  avg: number;
  p50: number;
  p95: number;
  p99: number;
  tags?: Record<string, string>;
}

export class MetricsCollector extends EventEmitter {
  private metrics: Map<string, Metric[]> = new Map();
  private aggregations: Map<string, MetricAggregation> = new Map();
  private intervals: Map<string, NodeJS.Timeout> = new Map();

  constructor(private aggregationInterval: number = 60000) {
    super();
  }

  recordCounter(name: string, value: number = 1, tags?: Record<string, string>): void {
    this.recordMetric({
      name,
      value,
      timestamp: new Date().toISOString(),
      tags,
      type: 'counter',
      unit: 'count',
    });
  }

  recordGauge(name: string, value: number, tags?: Record<string, string>): void {
    this.recordMetric({
      name,
      value,
      timestamp: new Date().toISOString(),
      tags,
      type: 'gauge',
    });
  }

  recordHistogram(name: string, value: number, tags?: Record<string, string>): void {
    this.recordMetric({
      name,
      value,
      timestamp: new Date().toISOString(),
      tags,
      type: 'histogram',
    });
  }

  recordTimer(name: string, duration: number, tags?: Record<string, string>): void {
    this.recordMetric({
      name,
      value: duration,
      timestamp: new Date().toISOString(),
      tags,
      type: 'timer',
      unit: 'milliseconds',
    });
  }

  private recordMetric(metric: Metric): void {
    const key = this.getMetricKey(metric.name, metric.tags);

    if (!this.metrics.has(key)) {
      this.metrics.set(key, []);
      this.startAggregation(key);
    }

    const metrics = this.metrics.get(key)!;
    metrics.push(metric);

    // Keep only last 10000 metrics per type
    if (metrics.length > 10000) {
      metrics.splice(0, metrics.length - 10000);
    }

    this.emit('metric', metric);
  }

  private startAggregation(key: string): void {
    const interval = setInterval(() => {
      this.aggregateMetrics(key);
    }, this.aggregationInterval);

    this.intervals.set(key, interval);
  }

  private aggregateMetrics(key: string): void {
    const metrics = this.metrics.get(key);
    if (!metrics || metrics.length === 0) return;

    const values = metrics.map((m) => m.value);
    const latestMetric = metrics[metrics.length - 1];

    const aggregation: MetricAggregation = {
      name: latestMetric.name,
      type: latestMetric.type,
      count: values.length,
      sum: values.reduce((sum, val) => sum + val, 0),
      min: Math.min(...values),
      max: Math.max(...values),
      avg: values.reduce((sum, val) => sum + val, 0) / values.length,
      p50: this.percentile(values, 0.5),
      p95: this.percentile(values, 0.95),
      p99: this.percentile(values, 0.99),
      tags: latestMetric.tags,
    };

    this.aggregations.set(key, aggregation);
    this.emit('aggregation', aggregation);

    // Clear old metrics for this key
    this.metrics.set(key, []);
  }

  private getMetricKey(name: string, tags?: Record<string, string>): string {
    if (!tags) return name;

    const tagPairs = Object.entries(tags)
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([k, v]) => `${k}=${v}`);

    return `${name}{${tagPairs.join(',')}}`;
  }

  private percentile(values: number[], p: number): number {
    const sorted = values.slice().sort((a, b) => a - b);
    const index = Math.ceil(sorted.length * p) - 1;
    return sorted[Math.max(0, index)];
  }

  getMetrics(name?: string): Metric[] {
    if (name) {
      const results: Metric[] = [];
      for (const [key, metrics] of this.metrics) {
        if (key.startsWith(name)) {
          results.push(...metrics);
        }
      }
      return results;
    }

    const allMetrics: Metric[] = [];
    for (const metrics of this.metrics.values()) {
      allMetrics.push(...metrics);
    }
    return allMetrics;
  }

  getAggregations(name?: string): MetricAggregation[] {
    const aggregations = Array.from(this.aggregations.values());

    if (name) {
      return aggregations.filter((a) => a.name === name);
    }

    return aggregations;
  }

  stop(): void {
    for (const interval of this.intervals.values()) {
      clearInterval(interval);
    }
    this.intervals.clear();
  }
}

// Performance monitoring decorator
export function monitorPerformance(
  target: any,
  propertyName: string,
  descriptor: PropertyDescriptor
): void {
  const originalMethod = descriptor.value;
  const metricsCollector = new MetricsCollector();

  descriptor.value = async function (...args: any[]) {
    const startTime = performance.now();
    const methodName = `${target.constructor.name}.${propertyName}`;

    try {
      const result = await originalMethod.apply(this, args);
      const duration = performance.now() - startTime;

      metricsCollector.recordTimer(`${methodName}.duration`, duration);
      metricsCollector.recordCounter(`${methodName}.success`);

      return result;
    } catch (error) {
      const duration = performance.now() - startTime;

      metricsCollector.recordTimer(`${methodName}.duration`, duration);
      metricsCollector.recordCounter(`${methodName}.error`);

      throw error;
    }
  };
}
```

### Step 5: Alerting System

Create automated alerting for critical issues:

```typescript
// packages/shared/src/monitoring/alerting.ts
import { EventEmitter } from 'events';
import { HealthCheckResult } from '../health/health-checker';
import { MetricAggregation } from './metrics-collector';

export interface Alert {
  id: string;
  severity: 'low' | 'medium' | 'high' | 'critical';
  type: 'health' | 'metric' | 'custom';
  title: string;
  message: string;
  timestamp: string;
  source: string;
  details: Record<string, unknown>;
  resolved: boolean;
  resolvedAt?: string;
}

export interface AlertRule {
  id: string;
  name: string;
  enabled: boolean;
  severity: 'low' | 'medium' | 'high' | 'critical';
  condition: AlertCondition;
  cooldown: number; // milliseconds
  notifications: NotificationChannel[];
}

export interface AlertCondition {
  type: 'health_check' | 'metric_threshold' | 'metric_rate' | 'custom';
  metric?: string;
  operator: '>' | '<' | '>=' | '<=' | '==' | '!=';
  threshold: number;
  duration?: number; // milliseconds
}

export interface NotificationChannel {
  type: 'email' | 'slack' | 'webhook' | 'pagerduty';
  config: Record<string, unknown>;
  enabled: boolean;
}

export class AlertManager extends EventEmitter {
  private rules: Map<string, AlertRule> = new Map();
  private alerts: Map<string, Alert> = new Map();
  private lastAlerts: Map<string, number> = new Map();
  private cooldowns: Map<string, NodeJS.Timeout> = new Map();

  constructor() {
    super();
  }

  addRule(rule: AlertRule): void {
    this.rules.set(rule.id, rule);
  }

  removeRule(ruleId: string): void {
    this.rules.delete(ruleId);
  }

  async evaluateHealthCheck(checkName: string, result: HealthCheckResult): Promise<void> {
    for (const rule of this.rules.values()) {
      if (!rule.enabled || rule.condition.type !== 'health_check') continue;

      if (this.shouldTriggerHealthAlert(rule, checkName, result)) {
        await this.triggerAlert(rule, {
          type: 'health',
          severity: rule.severity,
          title: `Health Check Alert: ${checkName}`,
          message: `Health check '${checkName}' is ${result.status}`,
          source: 'health-checker',
          details: {
            checkName,
            result,
            rule: rule.name,
          },
        });
      }
    }
  }

  async evaluateMetric(aggregation: MetricAggregation): Promise<void> {
    for (const rule of this.rules.values()) {
      if (!rule.enabled || rule.condition.type !== 'metric_threshold') continue;
      if (rule.condition.metric !== aggregation.name) continue;

      if (this.shouldTriggerMetricAlert(rule, aggregation)) {
        await this.triggerAlert(rule, {
          type: 'metric',
          severity: rule.severity,
          title: `Metric Alert: ${aggregation.name}`,
          message: `Metric '${aggregation.name}' ${rule.condition.operator} ${rule.condition.threshold}`,
          source: 'metrics-collector',
          details: {
            aggregation,
            rule: rule.name,
            threshold: rule.condition.threshold,
          },
        });
      }
    }
  }

  private shouldTriggerHealthAlert(
    rule: AlertRule,
    checkName: string,
    result: HealthCheckResult
  ): boolean {
    // Simple health check condition - trigger if unhealthy
    return result.status === 'unhealthy';
  }

  private shouldTriggerMetricAlert(rule: AlertRule, aggregation: MetricAggregation): boolean {
    const { operator, threshold } = rule.condition;
    const value = aggregation.avg; // Use average for threshold comparison

    switch (operator) {
      case '>':
        return value > threshold;
      case '<':
        return value < threshold;
      case '>=':
        return value >= threshold;
      case '<=':
        return value <= threshold;
      case '==':
        return value === threshold;
      case '!=':
        return value !== threshold;
      default:
        return false;
    }
  }

  private async triggerAlert(
    rule: AlertRule,
    alertData: Omit<Alert, 'id' | 'timestamp' | 'resolved'>
  ): Promise<void> {
    const now = new Date().toISOString();
    const alertId = `${rule.id}-${Date.now()}`;

    // Check cooldown
    const lastAlertTime = this.lastAlerts.get(rule.id) || 0;
    if (now.getTime() - lastAlertTime < rule.cooldown) {
      return;
    }

    const alert: Alert = {
      id: alertId,
      ...alertData,
      timestamp: now,
      resolved: false,
    };

    this.alerts.set(alertId, alert);
    this.lastAlerts.set(rule.id, Date.now());

    // Send notifications
    await this.sendNotifications(alert, rule.notifications);

    // Emit alert event
    this.emit('alert', alert);

    // Set up auto-resolution if configured
    if (rule.cooldown > 0) {
      const cooldown = setTimeout(() => {
        this.resolveAlert(alertId);
      }, rule.cooldown);

      this.cooldowns.set(alertId, cooldown);
    }
  }

  resolveAlert(alertId: string): void {
    const alert = this.alerts.get(alertId);
    if (!alert || alert.resolved) return;

    alert.resolved = true;
    alert.resolvedAt = new Date().toISOString();

    // Clear cooldown timer
    const cooldown = this.cooldowns.get(alertId);
    if (cooldown) {
      clearTimeout(cooldown);
      this.cooldowns.delete(alertId);
    }

    this.emit('alertResolved', alert);
  }

  private async sendNotifications(alert: Alert, channels: NotificationChannel[]): Promise<void> {
    for (const channel of channels) {
      if (!channel.enabled) continue;

      try {
        switch (channel.type) {
          case 'email':
            await this.sendEmailNotification(alert, channel.config);
            break;
          case 'slack':
            await this.sendSlackNotification(alert, channel.config);
            break;
          case 'webhook':
            await this.sendWebhookNotification(alert, channel.config);
            break;
          case 'pagerduty':
            await this.sendPagerDutyNotification(alert, channel.config);
            break;
        }
      } catch (error) {
        console.error(`Failed to send ${channel.type} notification:`, error);
      }
    }
  }

  private async sendEmailNotification(
    alert: Alert,
    config: Record<string, unknown>
  ): Promise<void> {
    // Implementation would use email service (SendGrid, AWS SES, etc.)
    console.log('Email notification:', { alert, config });
  }

  private async sendSlackNotification(
    alert: Alert,
    config: Record<string, unknown>
  ): Promise<void> {
    const webhookUrl = config.webhookUrl as string;
    const payload = {
      text: `🚨 ${alert.title}`,
      attachments: [
        {
          color: this.getSeverityColor(alert.severity),
          fields: [
            { title: 'Severity', value: alert.severity.toUpperCase(), short: true },
            { title: 'Source', value: alert.source, short: true },
            { title: 'Message', value: alert.message, short: false },
            { title: 'Time', value: alert.timestamp, short: true },
          ],
        },
      ],
    };

    await fetch(webhookUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
  }

  private async sendWebhookNotification(
    alert: Alert,
    config: Record<string, unknown>
  ): Promise<void> {
    const url = config.url as string;

    await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(alert),
    });
  }

  private async sendPagerDutyNotification(
    alert: Alert,
    config: Record<string, unknown>
  ): Promise<void> {
    // Implementation would use PagerDuty API
    console.log('PagerDuty notification:', { alert, config });
  }

  private getSeverityColor(severity: string): string {
    switch (severity) {
      case 'critical':
        return 'danger';
      case 'high':
        return 'warning';
      case 'medium':
        return 'good';
      case 'low':
        return '#36a64f';
      default:
        return 'good';
    }
  }

  getActiveAlerts(): Alert[] {
    return Array.from(this.alerts.values()).filter((alert) => !alert.resolved);
  }

  getAlertHistory(limit: number = 100): Alert[] {
    return Array.from(this.alerts.values())
      .sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())
      .slice(0, limit);
  }
}
```

### Step 6: Service Integration

Integrate health monitoring into all services:

```typescript
// packages/api/src/monitoring.ts
import { HealthChecker } from '@shared/health/health-checker';
import { HealthRoutes } from '@shared/health/routes';
import { MetricsCollector } from '@shared/monitoring/metrics-collector';
import { AlertManager } from '@shared/monitoring/alerting';
import {
  createDatabaseHealthCheck,
  createRedisHealthCheck,
  createMemoryHealthCheck,
} from '@shared/health/checks';

export class MonitoringService {
  private healthChecker: HealthChecker;
  private metricsCollector: MetricsCollector;
  private alertManager: AlertManager;
  private healthRoutes: HealthRoutes;

  constructor(
    private dbPool: any,
    private redisClient: any,
    private version: string
  ) {
    this.healthChecker = new HealthChecker('api', version);
    this.metricsCollector = new MetricsCollector();
    this.alertManager = new AlertManager();
    this.healthRoutes = new HealthRoutes(this.healthChecker);

    this.setupHealthChecks();
    this.setupAlerting();
    this.setupMetricsCollection();
  }

  private setupHealthChecks(): void {
    // Database health check
    this.healthChecker.registerCheck({
      name: 'database',
      timeout: 5000,
      interval: 30000,
      critical: true,
      check: createDatabaseHealthCheck(this.dbPool),
    });

    // Redis health check
    this.healthChecker.registerCheck({
      name: 'redis',
      timeout: 3000,
      interval: 30000,
      critical: true,
      check: createRedisHealthCheck(this.redisClient),
    });

    // Memory health check
    this.healthChecker.registerCheck({
      name: 'memory',
      timeout: 1000,
      interval: 60000,
      critical: false,
      check: createMemoryHealthCheck(0.9),
    });
  }

  private setupAlerting(): void {
    // Database alert rule
    this.alertManager.addRule({
      id: 'database-down',
      name: 'Database Unavailable',
      enabled: true,
      severity: 'critical',
      condition: {
        type: 'health_check',
        operator: '==',
        threshold: 0,
      },
      cooldown: 300000, // 5 minutes
      notifications: [
        {
          type: 'slack',
          enabled: true,
          config: {
            webhookUrl: process.env.SLACK_WEBHOOK_URL,
          },
        },
      ],
    });

    // Memory alert rule
    this.alertManager.addRule({
      id: 'high-memory',
      name: 'High Memory Usage',
      enabled: true,
      severity: 'high',
      condition: {
        type: 'metric_threshold',
        metric: 'memory.usage_ratio',
        operator: '>',
        threshold: 0.9,
      },
      cooldown: 600000, // 10 minutes
      notifications: [
        {
          type: 'email',
          enabled: true,
          config: {
            to: process.env.ADMIN_EMAIL,
          },
        },
      ],
    });

    // Listen for health check failures
    this.healthChecker.on('criticalFailure', async (checkName, result) => {
      await this.alertManager.evaluateHealthCheck(checkName, result);
    });
  }

  private setupMetricsCollection(): void {
    // Collect system metrics every 30 seconds
    setInterval(() => {
      this.collectSystemMetrics();
    }, 30000);

    // Listen for metric aggregations
    this.metricsCollector.on('aggregation', async (aggregation) => {
      await this.alertManager.evaluateMetric(aggregation);
    });
  }

  private collectSystemMetrics(): void {
    const memUsage = process.memoryUsage();
    const cpuUsage = process.cpuUsage();

    // Memory metrics
    this.metricsCollector.recordGauge('memory.rss', memUsage.rss);
    this.metricsCollector.recordGauge('memory.heap_used', memUsage.heapUsed);
    this.metricsCollector.recordGauge('memory.heap_total', memUsage.heapTotal);
    this.metricsCollector.recordGauge('memory.external', memUsage.external);

    // CPU metrics
    this.metricsCollector.recordGauge('cpu.user', cpuUsage.user);
    this.metricsCollector.recordGauge('cpu.system', cpuUsage.system);

    // Process metrics
    this.metricsCollector.recordGauge('process.uptime', process.uptime());
    this.metricsCollector.recordCounter('process.requests_handled');
  }

  async registerRoutes(fastify: any): Promise<void> {
    await this.healthRoutes.registerRoutes(fastify);

    // Metrics endpoint
    fastify.get(
      '/metrics',
      {
        schema: {
          description: 'Application metrics',
          tags: ['monitoring'],
        },
      },
      async (request: any, reply: any) => {
        const metrics = this.metricsCollector.getAggregations();
        reply.send(metrics);
      }
    );

    // Alerts endpoint
    fastify.get(
      '/alerts',
      {
        schema: {
          description: 'Active alerts',
          tags: ['monitoring'],
        },
      },
      async (request: any, reply: any) => {
        const alerts = this.alertManager.getActiveAlerts();
        reply.send(alerts);
      }
    );
  }

  getHealthChecker(): HealthChecker {
    return this.healthChecker;
  }

  getMetricsCollector(): MetricsCollector {
    return this.metricsCollector;
  }

  getAlertManager(): AlertManager {
    return this.alertManager;
  }

  async shutdown(): Promise<void> {
    this.healthChecker.stop();
    this.metricsCollector.stop();
  }
}
```

## Files to Create

1. **Health Check Framework**:
   - `packages/shared/src/health/health-checker.ts`
   - `packages/shared/src/health/checks.ts`
   - `packages/shared/src/health/routes.ts`

2. **Monitoring System**:
   - `packages/shared/src/monitoring/metrics-collector.ts`
   - `packages/shared/src/monitoring/alerting.ts`
   - `packages/shared/src/monitoring/types.ts`

3. **Service Integration**:
   - `packages/api/src/monitoring.ts`
   - `packages/orchestrator/src/monitoring.ts`
   - `packages/workers/src/monitoring.ts`

4. **Configuration**:
   - `monitoring/prometheus.yml` (Prometheus configuration)
   - `monitoring/grafana/dashboards/` (Grafana dashboards)
   - `monitoring/alertmanager.yml` (AlertManager configuration)

## Dependencies

- **Health Checks**:
  - No additional dependencies needed

- **Metrics Collection**:
  - `prom-client` - Prometheus metrics
  - `node-cron` - Scheduled tasks

- **Alerting**:
  - `nodemailer` - Email notifications
  - `@slack/web-api` - Slack integration

- **Monitoring Tools**:
  - Prometheus server
  - Grafana dashboard
  - AlertManager

## Testing Requirements

1. **Unit Tests**:
   - Test health check framework
   - Test metrics collection and aggregation
   - Test alerting rules and notifications

2. **Integration Tests**:
   - Test health check endpoints
   - Test alert triggering and resolution
   - Test metrics API endpoints

3. **Load Testing**:
   - Test monitoring system under high load
   - Validate minimal performance impact
   - Test alert delivery reliability

## Security Considerations

1. **Access Control**:
   - Secure health check endpoints
   - Implement authentication for monitoring APIs
   - Rate limit monitoring endpoints

2. **Data Protection**:
   - Sanitize sensitive data in logs
   - Encrypt alert notifications
   - Secure monitoring credentials

3. **Network Security**:
   - Use TLS for monitoring communications
   - Implement firewall rules for monitoring ports
   - Secure webhook endpoints

## Notes

- Health checks should be lightweight and fast
- Metrics collection should have minimal performance impact
- Alerting should include proper cooldown periods to prevent spam
- All monitoring data should be retained according to compliance requirements
- Monitoring system should be highly available and redundant
- Consider implementing distributed tracing for complex request flows
