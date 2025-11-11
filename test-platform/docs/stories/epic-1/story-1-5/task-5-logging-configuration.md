# Task 5: Logging Configuration

## Overview

Implement comprehensive logging configuration using Pino for structured, high-performance logging across all Tamma platform services with proper log levels, formatting, and output destinations.

## Objectives

- Configure Pino logging for all services
- Implement structured JSON logging with correlation IDs
- Set up log rotation and archival
- Configure different log levels per environment
- Implement log aggregation and monitoring
- Add performance metrics and error tracking

## Implementation Steps

### Step 1: Base Logger Configuration

Create centralized logging configuration:

```typescript
// packages/shared/src/logging/logger.ts
import pino, { Logger, LoggerOptions } from 'pino';
import { randomUUID } from 'crypto';

export interface LogContext {
  requestId?: string;
  userId?: string;
  service?: string;
  version?: string;
  environment?: string;
  [key: string]: unknown;
}

export interface LogMetadata {
  timestamp?: string;
  level: string;
  message: string;
  context?: LogContext;
  error?: {
    name: string;
    message: string;
    stack?: string;
    code?: string;
  };
  duration?: number;
  [key: string]: unknown;
}

export class TammaLogger {
  private logger: Logger;
  private context: LogContext;

  constructor(service: string, context: LogContext = {}) {
    this.context = {
      service,
      version: process.env.npm_package_version || '1.0.0',
      environment: process.env.NODE_ENV || 'development',
      ...context,
    };

    const options: LoggerOptions = {
      level: process.env.LOG_LEVEL || 'info',
      formatters: {
        level: (label) => ({ level: label }),
        log: (object) => this.formatLog(object as LogMetadata),
      },
      timestamp: pino.stdTimeFunctions.isoTime,
      base: {
        pid: process.pid,
        hostname: require('os').hostname(),
        service: this.context.service,
      },
    };

    // Add pretty printing in development
    if (process.env.NODE_ENV === 'development') {
      options.transport = {
        target: 'pino-pretty',
        options: {
          colorize: true,
          translateTime: 'HH:MM:ss Z',
          ignore: 'pid,hostname',
          messageFormat: '{service} [{level}] {msg}',
          customPrettifiers: {
            time: (timestamp: string) => timestamp,
          },
        },
      };
    }

    this.logger = pino(options);
  }

  private formatLog(log: LogMetadata): LogMetadata {
    return {
      ...log,
      context: {
        ...this.context,
        ...log.context,
      },
      timestamp: log.timestamp || new Date().toISOString(),
    };
  }

  private createChildLogger(additionalContext: LogContext): Logger {
    const childContext = { ...this.context, ...additionalContext };
    return this.logger.child(childContext);
  }

  debug(message: string, data?: Record<string, unknown>, context?: LogContext): void {
    const logger = context ? this.createChildLogger(context) : this.logger;
    logger.debug({ ...data, context }, message);
  }

  info(message: string, data?: Record<string, unknown>, context?: LogContext): void {
    const logger = context ? this.createChildLogger(context) : this.logger;
    logger.info({ ...data, context }, message);
  }

  warn(message: string, data?: Record<string, unknown>, context?: LogContext): void {
    const logger = context ? this.createChildLogger(context) : this.logger;
    logger.warn({ ...data, context }, message);
  }

  error(message: string, error?: Error | Record<string, unknown>, context?: LogContext): void {
    const errorData =
      error instanceof Error
        ? {
            name: error.name,
            message: error.message,
            stack: error.stack,
            code: (error as any).code,
          }
        : error;

    const logger = context ? this.createChildLogger(context) : this.logger;
    logger.error({ error: errorData, context }, message);
  }

  // Performance logging
  time(label: string, context?: LogContext): () => void {
    const start = Date.now();
    const requestId = context?.requestId || randomUUID();

    this.debug(`Timer started: ${label}`, { label, requestId }, context);

    return () => {
      const duration = Date.now() - start;
      this.info(
        `Timer completed: ${label}`,
        {
          label,
          duration,
          requestId,
          durationMs: duration,
        },
        context
      );
    };
  }

  // Request logging
  logRequest(req: any, res: any, duration: number): void {
    const context: LogContext = {
      requestId: req.id || randomUUID(),
      userId: req.user?.id,
      method: req.method,
      url: req.url,
      statusCode: res.statusCode,
      userAgent: req.headers['user-agent'],
      ip: req.ip || req.connection.remoteAddress,
    };

    const level = res.statusCode >= 400 ? 'warn' : 'info';
    const message = `${req.method} ${req.url} ${res.statusCode} - ${duration}ms`;

    this.logger[level](
      {
        method: req.method,
        url: req.url,
        statusCode: res.statusCode,
        duration,
        userAgent: req.headers['user-agent'],
        ip: req.ip,
        context,
      },
      message
    );
  }

  // Create child logger with additional context
  child(context: LogContext): TammaLogger {
    return new TammaLogger(this.context.service!, { ...this.context, ...context });
  }

  // Get raw pino logger for advanced usage
  getRawLogger(): Logger {
    return this.logger;
  }
}

// Default logger instance
export const logger = new TammaLogger('tamma');

// Service-specific loggers
export const createServiceLogger = (service: string, context?: LogContext): TammaLogger => {
  return new TammaLogger(service, context);
};
```

### Step 2: Logging Middleware

Create HTTP request logging middleware:

```typescript
// packages/shared/src/logging/middleware.ts
import { FastifyRequest, FastifyReply } from 'fastify';
import { TammaLogger } from './logger';

export interface RequestLogContext {
  requestId: string;
  userId?: string;
  organizationId?: string;
  userAgent?: string;
  ip?: string;
  method: string;
  url: string;
  startTime: number;
}

export class LoggingMiddleware {
  constructor(private logger: TammaLogger) {}

  requestLogger() {
    return async (request: FastifyRequest, reply: FastifyReply) => {
      const startTime = Date.now();
      const requestId = request.id || this.generateRequestId();

      // Add request context to logger
      const requestLogger = this.logger.child({
        requestId,
        method: request.method,
        url: request.url,
        userAgent: request.headers['user-agent'],
        ip: request.ip,
        userId: (request as any).user?.id,
        organizationId: (request as any).user?.organizationId,
      });

      // Log request start
      requestLogger.debug('Request started', {
        method: request.method,
        url: request.url,
        headers: this.sanitizeHeaders(request.headers),
      });

      // Store logger in request for use in handlers
      (request as any).log = requestLogger;

      // Log response when finished
      reply.addHook('onSend', async (request, reply, payload) => {
        const duration = Date.now() - startTime;
        const level = reply.statusCode >= 400 ? 'warn' : 'info';

        requestLogger[level]('Request completed', {
          statusCode: reply.statusCode,
          duration,
          contentLength: reply.getHeader('content-length'),
          responseTime: duration,
        });

        return payload;
      });

      // Log errors
      reply.addHook('onError', async (request, reply, error) => {
        const duration = Date.now() - startTime;

        requestLogger.error('Request failed', error, {
          statusCode: reply.statusCode,
          duration,
          errorName: error.name,
          errorMessage: error.message,
        });
      });
    };
  }

  private generateRequestId(): string {
    return Math.random().toString(36).substring(2) + Date.now().toString(36);
  }

  private sanitizeHeaders(headers: Record<string, any>): Record<string, any> {
    const sanitized = { ...headers };

    // Remove sensitive headers
    const sensitiveHeaders = ['authorization', 'cookie', 'x-api-key', 'x-auth-token', 'password'];

    for (const header of sensitiveHeaders) {
      if (sanitized[header]) {
        sanitized[header] = '[REDACTED]';
      }
    }

    return sanitized;
  }
}

// Error logging middleware
export class ErrorLoggingMiddleware {
  constructor(private logger: TammaLogger) {}

  errorHandler() {
    return async (error: Error, request: FastifyRequest, reply: FastifyReply) => {
      const requestLogger = (request as any).log || this.logger;

      requestLogger.error('Unhandled error', error, {
        requestId: request.id,
        method: request.method,
        url: request.url,
        userId: (request as any).user?.id,
        stack: error.stack,
      });

      // Send appropriate error response
      reply.status(500).send({
        error: {
          message: 'Internal server error',
          code: 'INTERNAL_ERROR',
          requestId: request.id,
        },
      });
    };
  }
}
```

### Step 3: Log Rotation and Archival

Configure log rotation with different strategies per environment:

```typescript
// packages/shared/src/logging/rotation.ts
import { createWriteStream, WriteStream } from 'fs';
import { join } from 'path';
import { pipeline } from 'stream/promises';
import { createGzip } from 'zlib';
import { createReadStream, unlinkSync } from 'fs';

export interface LogRotationConfig {
  enabled: boolean;
  directory: string;
  maxSize: string; // e.g., '100MB', '1GB'
  maxFiles: number;
  compress: boolean;
  schedule?: string; // cron expression
}

export class LogRotation {
  private streams: Map<string, WriteStream> = new Map();
  private sizes: Map<string, number> = new Map();
  private config: LogRotationConfig;

  constructor(config: LogRotationConfig) {
    this.config = config;
  }

  createLogStream(filename: string): WriteStream {
    if (!this.config.enabled) {
      return process.stdout;
    }

    const logPath = join(this.config.directory, filename);
    const stream = createWriteStream(logPath, { flags: 'a' });

    this.streams.set(filename, stream);
    this.sizes.set(filename, 0);

    // Monitor file size
    stream.on('write', (chunk) => {
      const currentSize = this.sizes.get(filename) || 0;
      this.sizes.set(filename, currentSize + chunk.length);

      if (this.shouldRotate(filename)) {
        this.rotateFile(filename);
      }
    });

    return stream;
  }

  private shouldRotate(filename: string): boolean {
    const maxSizeBytes = this.parseSize(this.config.maxSize);
    const currentSize = this.sizes.get(filename) || 0;
    return currentSize >= maxSizeBytes;
  }

  private async rotateFile(filename: string): Promise<void> {
    const stream = this.streams.get(filename);
    if (!stream) return;

    // Close current stream
    stream.end();
    this.streams.delete(filename);

    const logPath = join(this.config.directory, filename);
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
    const rotatedPath = join(this.config.directory, `${filename}.${timestamp}`);

    // Move current log to rotated filename
    await this.renameFile(logPath, rotatedPath);

    // Compress if enabled
    if (this.config.compress) {
      await this.compressFile(rotatedPath);
    }

    // Clean up old files
    await this.cleanupOldFiles(filename);

    // Create new stream
    this.createLogStream(filename);
  }

  private async renameFile(oldPath: string, newPath: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const fs = require('fs');
      fs.rename(oldPath, newPath, (err: any) => {
        if (err) reject(err);
        else resolve();
      });
    });
  }

  private async compressFile(filePath: string): Promise<void> {
    const compressedPath = `${filePath}.gz`;

    try {
      await pipeline(createReadStream(filePath), createGzip(), createWriteStream(compressedPath));

      // Remove uncompressed file
      unlinkSync(filePath);
    } catch (error) {
      console.error('Failed to compress log file:', error);
    }
  }

  private async cleanupOldFiles(filename: string): Promise<void> {
    const fs = require('fs').promises;
    const path = require('path');

    try {
      const files = await fs.readdir(this.config.directory);
      const logFiles = files
        .filter((file) => file.startsWith(filename) && file !== filename)
        .map((file) => ({
          name: file,
          path: path.join(this.config.directory, file),
          mtime: fs.stat(path.join(this.config.directory, file)).then((stat: any) => stat.mtime),
        }));

      // Sort by modification time (oldest first)
      const sortedFiles = await Promise.all(
        logFiles.map(async (file) => ({
          ...file,
          mtime: await file.mtime,
        }))
      );
      sortedFiles.sort((a, b) => a.mtime.getTime() - b.mtime.getTime());

      // Remove excess files
      if (sortedFiles.length > this.config.maxFiles) {
        const filesToRemove = sortedFiles.slice(0, sortedFiles.length - this.config.maxFiles);

        for (const file of filesToRemove) {
          try {
            await fs.unlink(file.path);
            console.log(`Removed old log file: ${file.name}`);
          } catch (error) {
            console.error(`Failed to remove log file ${file.name}:`, error);
          }
        }
      }
    } catch (error) {
      console.error('Failed to cleanup old log files:', error);
    }
  }

  private parseSize(sizeStr: string): number {
    const units: Record<string, number> = {
      B: 1,
      KB: 1024,
      MB: 1024 * 1024,
      GB: 1024 * 1024 * 1024,
    };

    const match = sizeStr.match(/^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB)$/i);
    if (!match) {
      throw new Error(`Invalid size format: ${sizeStr}`);
    }

    const value = parseFloat(match[1]);
    const unit = match[2].toUpperCase();
    return Math.floor(value * units[unit]);
  }

  async close(): Promise<void> {
    for (const [filename, stream] of this.streams) {
      stream.end();
    }
    this.streams.clear();
    this.sizes.clear();
  }
}
```

### Step 4: Environment-Specific Configurations

Create logging configurations for different environments:

```typescript
// packages/shared/src/logging/config.ts
import { LogRotationConfig } from './rotation';

export interface LoggingConfig {
  level: string;
  format: 'json' | 'pretty';
  rotation: LogRotationConfig;
  outputs: LogOutput[];
  metrics: {
    enabled: boolean;
    interval: number;
  };
  audit: {
    enabled: boolean;
    events: string[];
  };
}

export interface LogOutput {
  type: 'console' | 'file' | 'http' | 'database';
  config: Record<string, unknown>;
}

export const loggingConfigs: Record<string, LoggingConfig> = {
  development: {
    level: 'debug',
    format: 'pretty',
    rotation: {
      enabled: false,
      directory: './logs',
      maxSize: '100MB',
      maxFiles: 5,
      compress: false,
    },
    outputs: [{ type: 'console', config: {} }],
    metrics: {
      enabled: true,
      interval: 30000, // 30 seconds
    },
    audit: {
      enabled: true,
      events: ['USER_ACTION', 'SECURITY_EVENT', 'ERROR'],
    },
  },

  test: {
    level: 'error',
    format: 'json',
    rotation: {
      enabled: false,
      directory: './test-logs',
      maxSize: '10MB',
      maxFiles: 3,
      compress: false,
    },
    outputs: [{ type: 'console', config: {} }],
    metrics: {
      enabled: false,
      interval: 0,
    },
    audit: {
      enabled: false,
      events: [],
    },
  },

  staging: {
    level: 'info',
    format: 'json',
    rotation: {
      enabled: true,
      directory: './logs',
      maxSize: '500MB',
      maxFiles: 10,
      compress: true,
    },
    outputs: [
      { type: 'file', config: { filename: 'app.log' } },
      { type: 'http', config: { url: process.env.LOG_ENDPOINT } },
    ],
    metrics: {
      enabled: true,
      interval: 60000, // 1 minute
    },
    audit: {
      enabled: true,
      events: ['USER_ACTION', 'SECURITY_EVENT', 'API_CALL', 'ERROR', 'SYSTEM_EVENT'],
    },
  },

  production: {
    level: 'warn',
    format: 'json',
    rotation: {
      enabled: true,
      directory: '/var/log/tamma',
      maxSize: '1GB',
      maxFiles: 30,
      compress: true,
      schedule: '0 0 * * *', // Daily at midnight
    },
    outputs: [
      { type: 'file', config: { filename: 'app.log' } },
      { type: 'http', config: { url: process.env.LOG_ENDPOINT } },
      { type: 'database', config: { table: 'application_logs' } },
    ],
    metrics: {
      enabled: true,
      interval: 300000, // 5 minutes
    },
    audit: {
      enabled: true,
      events: ['USER_ACTION', 'SECURITY_EVENT', 'API_CALL', 'ERROR', 'SYSTEM_EVENT', 'DATA_CHANGE'],
    },
  },
};

export function getLoggingConfig(environment?: string): LoggingConfig {
  const env = environment || process.env.NODE_ENV || 'development';
  return loggingConfigs[env] || loggingConfigs.development;
}
```

### Step 5: Audit Logging

Implement comprehensive audit logging for compliance:

```typescript
// packages/shared/src/logging/audit.ts
import { TammaLogger } from './logger';
import { getLoggingConfig } from './config';

export interface AuditEvent {
  type: string;
  action: string;
  resource: string;
  userId?: string;
  organizationId?: string;
  requestId?: string;
  timestamp: string;
  details: Record<string, unknown>;
  result: 'SUCCESS' | 'FAILURE' | 'PARTIAL';
  ipAddress?: string;
  userAgent?: string;
}

export class AuditLogger {
  private logger: TammaLogger;
  private config = getLoggingConfig();

  constructor(logger: TammaLogger) {
    this.logger = logger.child({ component: 'audit' });
  }

  log(event: AuditEvent): void {
    if (!this.config.audit.enabled) return;

    if (!this.config.audit.events.includes(event.type)) {
      return;
    }

    const auditLog = {
      ...event,
      timestamp: event.timestamp || new Date().toISOString(),
      category: 'AUDIT',
    };

    this.logger.info(`Audit: ${event.type} - ${event.action}`, auditLog);
  }

  // Convenience methods for common audit events
  logUserLogin(userId: string, success: boolean, details?: Record<string, unknown>): void {
    this.log({
      type: 'USER_ACTION',
      action: 'LOGIN',
      resource: 'AUTH',
      userId,
      result: success ? 'SUCCESS' : 'FAILURE',
      details: details || {},
    });
  }

  logUserLogout(userId: string, details?: Record<string, unknown>): void {
    this.log({
      type: 'USER_ACTION',
      action: 'LOGOUT',
      resource: 'AUTH',
      userId,
      result: 'SUCCESS',
      details: details || {},
    });
  }

  logDataAccess(
    userId: string,
    resource: string,
    action: string,
    result: 'SUCCESS' | 'FAILURE' | 'PARTIAL',
    details?: Record<string, unknown>
  ): void {
    this.log({
      type: 'DATA_ACCESS',
      action,
      resource,
      userId,
      result,
      details: details || {},
    });
  }

  logSecurityEvent(event: string, details: Record<string, unknown>, userId?: string): void {
    this.log({
      type: 'SECURITY_EVENT',
      action: event,
      resource: 'SECURITY',
      userId,
      result: 'SUCCESS',
      details,
    });
  }

  logApiCall(
    userId: string,
    method: string,
    endpoint: string,
    statusCode: number,
    duration: number,
    details?: Record<string, unknown>
  ): void {
    this.log({
      type: 'API_CALL',
      action: `${method} ${endpoint}`,
      resource: 'API',
      userId,
      result: statusCode >= 400 ? 'FAILURE' : 'SUCCESS',
      details: {
        statusCode,
        duration,
        ...details,
      },
    });
  }

  logSystemEvent(
    event: string,
    details: Record<string, unknown>,
    result: 'SUCCESS' | 'FAILURE' | 'PARTIAL' = 'SUCCESS'
  ): void {
    this.log({
      type: 'SYSTEM_EVENT',
      action: event,
      resource: 'SYSTEM',
      result,
      details,
    });
  }

  logDataChange(
    userId: string,
    resource: string,
    action: 'CREATE' | 'UPDATE' | 'DELETE',
    resourceId: string,
    changes?: Record<string, unknown>,
    result: 'SUCCESS' | 'FAILURE' | 'PARTIAL' = 'SUCCESS'
  ): void {
    this.log({
      type: 'DATA_CHANGE',
      action,
      resource,
      userId,
      result,
      details: {
        resourceId,
        changes,
      },
    });
  }
}

// Global audit logger instance
export const auditLogger = new AuditLogger(logger);
```

### Step 6: Performance Metrics Logging

Add performance monitoring and metrics:

```typescript
// packages/shared/src/logging/metrics.ts
import { TammaLogger } from './logger';
import { performance } from 'perf_hooks';

export interface PerformanceMetric {
  name: string;
  value: number;
  unit: string;
  timestamp: string;
  tags?: Record<string, string>;
}

export interface RequestMetrics {
  requestId: string;
  method: string;
  url: string;
  statusCode: number;
  duration: number;
  memoryUsage: NodeJS.MemoryUsage;
  cpuUsage: NodeJS.CpuUsage;
}

export class MetricsLogger {
  private logger: TammaLogger;
  private metrics: Map<string, PerformanceMetric[]> = new Map();
  private interval: NodeJS.Timeout | null = null;

  constructor(logger: TammaLogger) {
    this.logger = logger.child({ component: 'metrics' });
  }

  start(intervalMs: number = 60000): void {
    if (this.interval) return;

    this.interval = setInterval(() => {
      this.collectAndLogMetrics();
    }, intervalMs);
  }

  stop(): void {
    if (this.interval) {
      clearInterval(this.interval);
      this.interval = null;
    }
  }

  recordMetric(metric: PerformanceMetric): void {
    const key = metric.name;
    if (!this.metrics.has(key)) {
      this.metrics.set(key, []);
    }

    const metrics = this.metrics.get(key)!;
    metrics.push(metric);

    // Keep only last 1000 metrics per type
    if (metrics.length > 1000) {
      metrics.splice(0, metrics.length - 1000);
    }
  }

  recordRequestMetrics(metrics: RequestMetrics): void {
    this.logger.info('Request metrics', {
      requestId: metrics.requestId,
      method: metrics.method,
      url: metrics.url,
      statusCode: metrics.statusCode,
      duration: metrics.duration,
      memoryUsage: metrics.memoryUsage,
      cpuUsage: metrics.cpuUsage,
      category: 'PERFORMANCE',
    });

    // Record duration as metric
    this.recordMetric({
      name: 'http_request_duration',
      value: metrics.duration,
      unit: 'milliseconds',
      timestamp: new Date().toISOString(),
      tags: {
        method: metrics.method,
        status_code: metrics.statusCode.toString(),
      },
    });
  }

  recordDatabaseQuery(query: string, duration: number, success: boolean): void {
    this.recordMetric({
      name: 'database_query_duration',
      value: duration,
      unit: 'milliseconds',
      timestamp: new Date().toISOString(),
      tags: {
        query_type: this.getQueryType(query),
        success: success.toString(),
      },
    });
  }

  recordCacheOperation(operation: 'hit' | 'miss' | 'set', key: string): void {
    this.recordMetric({
      name: 'cache_operation',
      value: 1,
      unit: 'count',
      timestamp: new Date().toISOString(),
      tags: {
        operation,
        key_prefix: key.split(':')[0] || 'unknown',
      },
    });
  }

  recordError(error: Error, context?: Record<string, unknown>): void {
    this.recordMetric({
      name: 'error_count',
      value: 1,
      unit: 'count',
      timestamp: new Date().toISOString(),
      tags: {
        error_type: error.constructor.name,
        error_name: error.name,
      },
    });

    this.logger.error('Error recorded', error, {
      ...context,
      category: 'ERROR_METRIC',
    });
  }

  private collectAndLogMetrics(): void {
    const memoryUsage = process.memoryUsage();
    const cpuUsage = process.cpuUsage();

    // System metrics
    this.logger.info('System metrics', {
      memory: {
        rss: memoryUsage.rss,
        heapTotal: memoryUsage.heapTotal,
        heapUsed: memoryUsage.heapUsed,
        external: memoryUsage.external,
      },
      cpu: {
        user: cpuUsage.user,
        system: cpuUsage.system,
      },
      uptime: process.uptime(),
      category: 'SYSTEM_METRICS',
    });

    // Aggregate metrics
    for (const [name, metrics] of this.metrics) {
      if (metrics.length === 0) continue;

      const values = metrics.map((m) => m.value);
      const aggregated = {
        name,
        count: values.length,
        min: Math.min(...values),
        max: Math.max(...values),
        avg: values.reduce((sum, val) => sum + val, 0) / values.length,
        p95: this.percentile(values, 0.95),
        p99: this.percentile(values, 0.99),
      };

      this.logger.info('Aggregated metrics', {
        ...aggregated,
        category: 'AGGREGATED_METRICS',
      });
    }

    // Clear old metrics
    this.metrics.clear();
  }

  private getQueryType(query: string): string {
    const trimmed = query.trim().toUpperCase();
    if (trimmed.startsWith('SELECT')) return 'SELECT';
    if (trimmed.startsWith('INSERT')) return 'INSERT';
    if (trimmed.startsWith('UPDATE')) return 'UPDATE';
    if (trimmed.startsWith('DELETE')) return 'DELETE';
    return 'OTHER';
  }

  private percentile(values: number[], p: number): number {
    const sorted = values.slice().sort((a, b) => a - b);
    const index = Math.ceil(sorted.length * p) - 1;
    return sorted[Math.max(0, index)];
  }
}

// Global metrics logger instance
export const metricsLogger = new MetricsLogger(logger);
```

### Step 7: Service Integration

Integrate logging into all services:

```typescript
// packages/api/src/logging.ts
import { createServiceLogger } from '@shared/logging/logger';
import { LoggingMiddleware, ErrorLoggingMiddleware } from '@shared/logging/middleware';
import { AuditLogger } from '@shared/logging/audit';
import { MetricsLogger } from '@shared/logging/metrics';

export const apiLogger = createServiceLogger('api');
export const auditLogger = new AuditLogger(apiLogger);
export const metricsLogger = new MetricsLogger(apiLogger);

export const loggingMiddleware = new LoggingMiddleware(apiLogger);
export const errorLoggingMiddleware = new ErrorLoggingMiddleware(apiLogger);

// Request timing utility
export function withTiming<T>(
  target: any,
  propertyName: string,
  descriptor: TypedPropertyDescriptor<T>
): void {
  const method = descriptor.value!;

  descriptor.value = function (this: any, ...args: any[]) {
    const endTimer = apiLogger.time(`${target.constructor.name}.${propertyName}`);

    try {
      const result = method.apply(this, args);

      if (result && typeof result.then === 'function') {
        return result
          .then((value: any) => {
            endTimer();
            return value;
          })
          .catch((error: any) => {
            apiLogger.error(`Method ${propertyName} failed`, error);
            endTimer();
            throw error;
          });
      }

      endTimer();
      return result;
    } catch (error) {
      apiLogger.error(`Method ${propertyName} failed`, error);
      endTimer();
      throw error;
    }
  } as any;
}
```

## Files to Create

1. **Core Logging**:
   - `packages/shared/src/logging/logger.ts`
   - `packages/shared/src/logging/middleware.ts`
   - `packages/shared/src/logging/rotation.ts`

2. **Configuration**:
   - `packages/shared/src/logging/config.ts`
   - `packages/shared/src/logging/types.ts`

3. **Specialized Logging**:
   - `packages/shared/src/logging/audit.ts`
   - `packages/shared/src/logging/metrics.ts`

4. **Service Integration**:
   - `packages/api/src/logging.ts`
   - `packages/orchestrator/src/logging.ts`
   - `packages/workers/src/logging.ts`

5. **Configuration Files**:
   - `pino.config.js` (Pino configuration)
   - `logrotate.conf` (System log rotation)

## Dependencies

- **Core Logging**:
  - `pino` - Fast JSON logger
  - `pino-pretty` - Pretty printing for development
  - `pino-roll` - File rotation support

- **Additional Tools**:
  - `@types/pino` - TypeScript definitions
  - `fastify` - For middleware integration
  - `cron` - For scheduled log rotation

## Testing Requirements

1. **Unit Tests**:
   - Test logger configuration and formatting
   - Test middleware request/response logging
   - Test audit logging functionality
   - Test metrics collection and aggregation

2. **Integration Tests**:
   - Test log rotation and archival
   - Test performance under high load
   - Test error handling and recovery

3. **Performance Tests**:
   - Measure logging overhead
   - Test memory usage with high log volume
   - Validate log rotation performance

## Security Considerations

1. **Data Sanitization**:
   - Redact sensitive information (passwords, tokens)
   - Sanitize PII in production logs
   - Implement log data retention policies

2. **Access Control**:
   - Secure log file permissions
   - Encrypt logs at rest for sensitive data
   - Implement log tamper detection

3. **Compliance**:
   - Ensure audit trail immutability
   - Meet GDPR data protection requirements
   - Implement proper log retention periods

## Notes

- Logging should have minimal performance impact (<5% overhead)
- All logs should be structured JSON for easy parsing
- Correlation IDs must flow through all service calls
- Audit logs must be immutable and tamper-evident
- Metrics collection should be configurable and toggleable
- Log levels should be environment-specific and dynamically adjustable
