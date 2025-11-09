# Task 6: Monitoring & Logging

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: Medium

## Overview

Implement comprehensive monitoring and logging for the Anthropic Claude provider, including structured logging, metrics collection, and performance monitoring. This enables observability, debugging, and optimization of the provider's operation.

## Detailed Implementation Plan

### Subtask 6.1: Add Structured Logging for All API Calls

**File**: `src/providers/anthropic/logging/provider-logger.ts`

```typescript
import { logger } from '@tamma/observability';
import type { MessageRequest, MessageChunk } from '@tamma/shared/contracts';

export interface LogContext {
  requestId?: string;
  userId?: string;
  sessionId?: string;
  model?: string;
  provider?: string;
  operation?: string;
  [key: string]: unknown;
}

export interface LogEntry {
  level: 'debug' | 'info' | 'warn' | 'error';
  message: string;
  timestamp: string;
  context: LogContext;
  duration?: number;
  error?: {
    name: string;
    message: string;
    stack?: string;
  };
  metrics?: Record<string, number>;
}

export class ProviderLogger {
  private readonly serviceName: string;
  private readonly logLevel: string;
  private readonly enableMetrics: boolean;

  constructor(
    options: {
      serviceName?: string;
      logLevel?: string;
      enableMetrics?: boolean;
    } = {}
  ) {
    this.serviceName = options.serviceName || 'anthropic-provider';
    this.logLevel = options.logLevel || 'info';
    this.enableMetrics = options.enableMetrics ?? true;
  }

  logApiCallStart(operation: string, request: MessageRequest, context: LogContext = {}): void {
    const logContext: LogContext = {
      ...context,
      operation,
      model: request.model,
      provider: 'anthropic',
      messageCount: request.messages.length,
      hasSystemPrompt: !!request.system,
      toolCount: request.tools?.length || 0,
      maxTokens: request.maxTokens,
      temperature: request.temperature,
    };

    // Sanitize request for logging (remove sensitive data)
    const sanitizedRequest = this.sanitizeRequest(request);

    logger.info('API call started', {
      ...logContext,
      request: sanitizedRequest,
    });
  }

  logApiCallSuccess(
    operation: string,
    request: MessageRequest,
    response: any,
    duration: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation,
      model: request.model,
      provider: 'anthropic',
      duration,
      success: true,
    };

    // Extract response metrics
    const responseMetrics = this.extractResponseMetrics(response);

    logger.info('API call completed successfully', {
      ...logContext,
      response: {
        id: response.id,
        model: response.model,
        stopReason: response.stop_reason,
      },
      metrics: responseMetrics,
    });
  }

  logApiCallError(
    operation: string,
    request: MessageRequest,
    error: Error,
    duration: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation,
      model: request.model,
      provider: 'anthropic',
      duration,
      success: false,
      errorCode: this.getErrorCode(error),
      errorType: error.constructor.name,
    };

    logger.error('API call failed', {
      ...logContext,
      error: {
        name: error.name,
        message: this.sanitizeErrorMessage(error.message),
        stack: process.env.NODE_ENV === 'development' ? error.stack : undefined,
      },
    });
  }

  logStreamStart(request: MessageRequest, context: LogContext = {}): void {
    const logContext: LogContext = {
      ...context,
      operation: 'stream',
      model: request.model,
      provider: 'anthropic',
      streamType: 'message',
    };

    logger.info('Stream started', logContext);
  }

  logStreamChunk(chunk: MessageChunk, chunkIndex: number, context: LogContext = {}): void {
    if (this.logLevel === 'debug') {
      const logContext: LogContext = {
        ...context,
        operation: 'stream_chunk',
        chunkIndex,
        contentLength: chunk.content?.length || 0,
        toolCallCount: chunk.toolCalls?.length || 0,
        hasFinishReason: !!chunk.finishReason,
      };

      logger.debug('Stream chunk received', logContext);
    }
  }

  logStreamEnd(
    request: MessageRequest,
    totalChunks: number,
    duration: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'stream_end',
      model: request.model,
      provider: 'anthropic',
      totalChunks,
      duration,
      averageChunkTime: duration / totalChunks,
    };

    logger.info('Stream completed', logContext);
  }

  logStreamError(
    request: MessageRequest,
    error: Error,
    chunksProcessed: number,
    duration: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'stream_error',
      model: request.model,
      provider: 'anthropic',
      chunksProcessed,
      duration,
      errorCode: this.getErrorCode(error),
      errorType: error.constructor.name,
    };

    logger.error('Stream failed', {
      ...logContext,
      error: {
        name: error.name,
        message: this.sanitizeErrorMessage(error.message),
      },
    });
  }

  logTokenUsage(
    model: string,
    inputTokens: number,
    outputTokens: number,
    cost: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'token_usage',
      model,
      provider: 'anthropic',
      inputTokens,
      outputTokens,
      totalTokens: inputTokens + outputTokens,
      cost,
    };

    logger.info('Token usage recorded', logContext);
  }

  logToolExecution(
    toolName: string,
    toolCallId: string,
    success: boolean,
    executionTime: number,
    error?: string,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'tool_execution',
      toolName,
      toolCallId,
      provider: 'anthropic',
      success,
      executionTime,
    };

    if (success) {
      logger.info('Tool executed successfully', logContext);
    } else {
      logger.warn('Tool execution failed', {
        ...logContext,
        error: this.sanitizeErrorMessage(error || 'Unknown error'),
      });
    }
  }

  logRetryAttempt(
    operation: string,
    attempt: number,
    maxAttempts: number,
    error: Error,
    delay: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'retry',
      retryOperation: operation,
      attempt,
      maxAttempts,
      delay,
      errorCode: this.getErrorCode(error),
    };

    logger.warn('Retry attempt', {
      ...logContext,
      error: {
        name: error.name,
        message: this.sanitizeErrorMessage(error.message),
      },
    });
  }

  logCircuitBreakerStateChange(
    state: string,
    reason: string,
    metrics: Record<string, number>,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'circuit_breaker',
      state,
      reason,
      provider: 'anthropic',
      ...metrics,
    };

    logger.warn('Circuit breaker state changed', logContext);
  }

  logRateLimitHit(
    limitType: 'requests' | 'tokens',
    limit: number,
    current: number,
    retryAfter?: number,
    context: LogContext = {}
  ): void {
    const logContext: LogContext = {
      ...context,
      operation: 'rate_limit',
      limitType,
      limit,
      current,
      utilization: (current / limit) * 100,
      retryAfter,
      provider: 'anthropic',
    };

    logger.warn('Rate limit hit', logContext);
  }

  logPerformanceMetrics(
    operation: string,
    metrics: Record<string, number>,
    context: LogContext = {}
  ): void {
    if (!this.enableMetrics) {
      return;
    }

    const logContext: LogContext = {
      ...context,
      operation: 'performance_metrics',
      provider: 'anthropic',
      ...metrics,
    };

    logger.info('Performance metrics', logContext);
  }

  private sanitizeRequest(request: MessageRequest): any {
    const sanitized = {
      model: request.model,
      messages: request.messages.map((msg) => ({
        role: msg.role,
        contentLength:
          typeof msg.content === 'string' ? msg.content.length : JSON.stringify(msg.content).length,
      })),
      maxTokens: request.maxTokens,
      temperature: request.temperature,
      topP: request.topP,
      hasSystemPrompt: !!request.system,
      toolCount: request.tools?.length || 0,
    };

    return sanitized;
  }

  private sanitizeErrorMessage(message: string): string {
    // Remove potential sensitive information
    return message
      .replace(/api[_-]?key["\s]*[:=]["\s]*[^"\\s}]+/gi, 'api_key: [REDACTED]')
      .replace(/token["\s]*[:=]["\s]*[^"\\s}]+/gi, 'token: [REDACTED]')
      .replace(/password["\s]*[:=]["\s]*[^"\\s}]+/gi, 'password: [REDACTED]');
  }

  private getErrorCode(error: Error): string {
    if ('code' in error && typeof error.code === 'string') {
      return error.code;
    }

    if ('status' in error && typeof error.status === 'number') {
      return `HTTP_${error.status}`;
    }

    return 'UNKNOWN_ERROR';
  }

  private extractResponseMetrics(response: any): Record<string, number> {
    const metrics: Record<string, number> = {};

    if (response.usage) {
      metrics.inputTokens = response.usage.input_tokens;
      metrics.outputTokens = response.usage.output_tokens;
      metrics.totalTokens = response.usage.input_tokens + response.usage.output_tokens;
    }

    if (response.content) {
      metrics.contentBlocks = Array.isArray(response.content) ? response.content.length : 1;
    }

    return metrics;
  }

  // Static factory methods
  static createDefault(): ProviderLogger {
    return new ProviderLogger();
  }

  static createWithLevel(level: string): ProviderLogger {
    return new ProviderLogger({ logLevel: level });
  }

  static createForTesting(): ProviderLogger {
    return new ProviderLogger({
      logLevel: 'debug',
      enableMetrics: true,
    });
  }
}
```

### Subtask 6.2: Implement Metrics Collection

**File**: `src/providers/anthropic/monitoring/metrics-collector.ts`

```typescript
import { EventEmitter } from 'events';
import type { MessageRequest, MessageChunk } from '@tamma/shared/contracts';

export interface MetricValue {
  value: number;
  timestamp: number;
  labels?: Record<string, string>;
}

export interface MetricSummary {
  count: number;
  sum: number;
  min: number;
  max: number;
  average: number;
  p50: number;
  p95: number;
  p99: number;
}

export interface MetricsSnapshot {
  timestamp: number;
  requests: {
    total: number;
    successful: number;
    failed: number;
    averageDuration: number;
  };
  tokens: {
    totalInput: number;
    totalOutput: number;
    totalCost: number;
  };
  models: Record<
    string,
    {
      requests: number;
      tokens: number;
      cost: number;
      averageDuration: number;
    }
  >;
  errors: Record<string, number>;
  tools: Record<
    string,
    {
      executions: number;
      successes: number;
      failures: number;
      averageDuration: number;
    }
  >;
}

export class MetricsCollector extends EventEmitter {
  private readonly metrics: Map<string, MetricValue[]> = new Map();
  private readonly maxDataPoints: number;
  private readonly retentionPeriod: number;

  constructor(
    options: {
      maxDataPoints?: number;
      retentionPeriod?: number; // in milliseconds
    } = {}
  ) {
    super();
    this.maxDataPoints = options.maxDataPoints || 10000;
    this.retentionPeriod = options.retentionPeriod || 24 * 60 * 60 * 1000; // 24 hours
  }

  recordRequest(
    operation: string,
    model: string,
    success: boolean,
    duration: number,
    inputTokens?: number,
    outputTokens?: number,
    cost?: number
  ): void {
    const timestamp = Date.now();
    const labels = { operation, model, success: success.toString() };

    // Record duration
    this.recordMetric('request_duration', duration, timestamp, labels);

    // Record request count
    this.recordMetric('request_count', 1, timestamp, labels);

    // Record token usage if provided
    if (inputTokens !== undefined) {
      this.recordMetric('input_tokens', inputTokens, timestamp, { model });
    }
    if (outputTokens !== undefined) {
      this.recordMetric('output_tokens', outputTokens, timestamp, { model });
    }
    if (cost !== undefined) {
      this.recordMetric('cost', cost, timestamp, { model });
    }

    // Emit event for real-time monitoring
    this.emit('request', {
      operation,
      model,
      success,
      duration,
      inputTokens,
      outputTokens,
      cost,
      timestamp,
    });
  }

  recordStreamMetrics(
    model: string,
    totalChunks: number,
    duration: number,
    success: boolean
  ): void {
    const timestamp = Date.now();
    const labels = { model, success: success.toString() };

    this.recordMetric('stream_duration', duration, timestamp, labels);
    this.recordMetric('stream_chunks', totalChunks, timestamp, labels);
    this.recordMetric('stream_chunk_rate', totalChunks / (duration / 1000), timestamp, labels);

    this.emit('stream', {
      model,
      totalChunks,
      duration,
      success,
      timestamp,
    });
  }

  recordToolExecution(toolName: string, success: boolean, duration: number, error?: string): void {
    const timestamp = Date.now();
    const labels = { toolName, success: success.toString() };

    this.recordMetric('tool_duration', duration, timestamp, labels);
    this.recordMetric('tool_count', 1, timestamp, labels);

    if (error) {
      this.recordMetric('tool_errors', 1, timestamp, {
        toolName,
        errorType: this.getErrorType(error),
      });
    }

    this.emit('tool', {
      toolName,
      success,
      duration,
      error,
      timestamp,
    });
  }

  recordError(operation: string, errorType: string, model?: string, duration?: number): void {
    const timestamp = Date.now();
    const labels = { operation, errorType, model: model || 'unknown' };

    this.recordMetric('error_count', 1, timestamp, labels);

    if (duration !== undefined) {
      this.recordMetric('error_duration', duration, timestamp, labels);
    }

    this.emit('error', {
      operation,
      errorType,
      model,
      duration,
      timestamp,
    });
  }

  recordRetry(operation: string, attempt: number, delay: number, success: boolean): void {
    const timestamp = Date.now();
    const labels = { operation, success: success.toString() };

    this.recordMetric('retry_count', 1, timestamp, labels);
    this.recordMetric('retry_attempt', attempt, timestamp, labels);
    this.recordMetric('retry_delay', delay, timestamp, labels);

    this.emit('retry', {
      operation,
      attempt,
      delay,
      success,
      timestamp,
    });
  }

  recordRateLimit(
    limitType: 'requests' | 'tokens',
    limit: number,
    current: number,
    retryAfter?: number
  ): void {
    const timestamp = Date.now();
    const labels = { limitType };

    this.recordMetric('rate_limit_hit', 1, timestamp, labels);
    this.recordMetric('rate_limit_utilization', (current / limit) * 100, timestamp, labels);

    if (retryAfter !== undefined) {
      this.recordMetric('rate_limit_retry_after', retryAfter, timestamp, labels);
    }

    this.emit('rateLimit', {
      limitType,
      limit,
      current,
      retryAfter,
      timestamp,
    });
  }

  recordCircuitBreakerStateChange(state: string, failures: number, requests: number): void {
    const timestamp = Date.now();
    const labels = { state };

    this.recordMetric('circuit_breaker_state_change', 1, timestamp, labels);
    this.recordMetric('circuit_breaker_failures', failures, timestamp);
    this.recordMetric('circuit_breaker_requests', requests, timestamp);

    this.emit('circuitBreaker', {
      state,
      failures,
      requests,
      timestamp,
    });
  }

  getMetric(
    name: string,
    labels?: Record<string, string>,
    startTime?: number,
    endTime?: number
  ): MetricValue[] {
    const values = this.metrics.get(name) || [];
    const now = Date.now();
    const start = startTime || now - this.retentionPeriod;
    const end = endTime || now;

    return values.filter((value) => {
      if (value.timestamp < start || value.timestamp > end) {
        return false;
      }

      if (labels) {
        for (const [key, expectedValue] of Object.entries(labels)) {
          if (value.labels?.[key] !== expectedValue) {
            return false;
          }
        }
      }

      return true;
    });
  }

  getMetricSummary(
    name: string,
    labels?: Record<string, string>,
    startTime?: number,
    endTime?: number
  ): MetricSummary {
    const values = this.getMetric(name, labels, startTime, endTime);

    if (values.length === 0) {
      return {
        count: 0,
        sum: 0,
        min: 0,
        max: 0,
        average: 0,
        p50: 0,
        p95: 0,
        p99: 0,
      };
    }

    const sortedValues = values.map((v) => v.value).sort((a, b) => a - b);
    const count = sortedValues.length;
    const sum = sortedValues.reduce((a, b) => a + b, 0);
    const min = sortedValues[0];
    const max = sortedValues[count - 1];
    const average = sum / count;

    return {
      count,
      sum,
      min,
      max,
      average,
      p50: this.getPercentile(sortedValues, 50),
      p95: this.getPercentile(sortedValues, 95),
      p99: this.getPercentile(sortedValues, 99),
    };
  }

  getSnapshot(): MetricsSnapshot {
    const now = Date.now();
    const start = now - this.retentionPeriod;

    // Request metrics
    const totalRequests = this.getMetricSummary('request_count').count;
    const successfulRequests = this.getMetricSummary('request_count', { success: 'true' }).count;
    const failedRequests = totalRequests - successfulRequests;
    const averageDuration = this.getMetricSummary('request_duration').average;

    // Token metrics
    const totalInputTokens = this.getMetricSummary('input_tokens').sum;
    const totalOutputTokens = this.getMetricSummary('output_tokens').sum;
    const totalCost = this.getMetricSummary('cost').sum;

    // Model breakdown
    const models: Record<string, any> = {};
    const modelNames = this.extractUniqueLabelValues('request_duration', 'model');

    for (const model of modelNames) {
      const modelRequests = this.getMetricSummary('request_count', { model }).count;
      const modelTokens =
        this.getMetricSummary('input_tokens', { model }).sum +
        this.getMetricSummary('output_tokens', { model }).sum;
      const modelCost = this.getMetricSummary('cost', { model }).sum;
      const modelDuration = this.getMetricSummary('request_duration', { model }).average;

      models[model] = {
        requests: modelRequests,
        tokens: modelTokens,
        cost: modelCost,
        averageDuration: modelDuration,
      };
    }

    // Error breakdown
    const errors: Record<string, number> = {};
    const errorTypes = this.extractUniqueLabelValues('error_count', 'errorType');

    for (const errorType of errorTypes) {
      errors[errorType] = this.getMetricSummary('error_count', { errorType }).count;
    }

    // Tool breakdown
    const tools: Record<string, any> = {};
    const toolNames = this.extractUniqueLabelValues('tool_count', 'toolName');

    for (const toolName of toolNames) {
      const toolExecutions = this.getMetricSummary('tool_count', { toolName }).count;
      const toolSuccesses = this.getMetricSummary('tool_count', {
        toolName,
        success: 'true',
      }).count;
      const toolFailures = toolExecutions - toolSuccesses;
      const toolDuration = this.getMetricSummary('tool_duration', { toolName }).average;

      tools[toolName] = {
        executions: toolExecutions,
        successes: toolSuccesses,
        failures: toolFailures,
        averageDuration: toolDuration,
      };
    }

    return {
      timestamp: now,
      requests: {
        total: totalRequests,
        successful: successfulRequests,
        failed: failedRequests,
        averageDuration,
      },
      tokens: {
        totalInput: totalInputTokens,
        totalOutput: totalOutputTokens,
        totalCost,
      },
      models,
      errors,
      tools,
    };
  }

  exportMetrics(format: 'json' | 'prometheus' = 'json'): string {
    const snapshot = this.getSnapshot();

    if (format === 'prometheus') {
      return this.exportAsPrometheus(snapshot);
    }

    return JSON.stringify(snapshot, null, 2);
  }

  reset(): void {
    this.metrics.clear();
    this.emit('reset');
  }

  private recordMetric(
    name: string,
    value: number,
    timestamp: number,
    labels?: Record<string, string>
  ): void {
    if (!this.metrics.has(name)) {
      this.metrics.set(name, []);
    }

    const values = this.metrics.get(name)!;
    values.push({ value, timestamp, labels });

    // Trim old data points
    this.trimMetricData(name);
  }

  private trimMetricData(name: string): void {
    const values = this.metrics.get(name);
    if (!values) return;

    const now = Date.now();
    const cutoffTime = now - this.retentionPeriod;

    // Remove old data points
    for (let i = values.length - 1; i >= 0; i--) {
      if (values[i].timestamp < cutoffTime) {
        values.splice(0, i + 1);
        break;
      }
    }

    // Limit by count
    if (values.length > this.maxDataPoints) {
      const excess = values.length - this.maxDataPoints;
      values.splice(0, excess);
    }
  }

  private extractUniqueLabelValues(metricName: string, labelName: string): string[] {
    const values = this.metrics.get(metricName) || [];
    const uniqueValues = new Set<string>();

    for (const value of values) {
      if (value.labels?.[labelName]) {
        uniqueValues.add(value.labels[labelName]);
      }
    }

    return Array.from(uniqueValues);
  }

  private getPercentile(sortedValues: number[], percentile: number): number {
    if (sortedValues.length === 0) return 0;

    const index = Math.ceil((percentile / 100) * sortedValues.length) - 1;
    return sortedValues[Math.max(0, index)];
  }

  private getErrorType(error: string): string {
    if (error.includes('timeout')) return 'timeout';
    if (error.includes('rate limit')) return 'rate_limit';
    if (error.includes('authentication')) return 'authentication';
    if (error.includes('network')) return 'network';
    if (error.includes('validation')) return 'validation';
    return 'unknown';
  }

  private exportAsPrometheus(snapshot: MetricsSnapshot): string {
    const lines: string[] = [];
    const timestamp = Math.floor(snapshot.timestamp / 1000);

    // Request metrics
    lines.push(`# HELP anthropic_requests_total Total number of requests`);
    lines.push(`# TYPE anthropic_requests_total counter`);
    lines.push(`anthropic_requests_total ${snapshot.requests.total} ${timestamp}`);
    lines.push(`anthropic_requests_successful_total ${snapshot.requests.successful} ${timestamp}`);
    lines.push(`anthropic_requests_failed_total ${snapshot.requests.failed} ${timestamp}`);

    lines.push(`# HELP anthropic_request_duration_seconds Request duration in seconds`);
    lines.push(`# TYPE anthropic_request_duration_seconds histogram`);
    lines.push(
      `anthropic_request_duration_seconds_avg ${snapshot.requests.averageDuration / 1000} ${timestamp}`
    );

    // Token metrics
    lines.push(`# HELP anthropic_tokens_total Total tokens processed`);
    lines.push(`# TYPE anthropic_tokens_total counter`);
    lines.push(`anthropic_input_tokens_total ${snapshot.tokens.totalInput} ${timestamp}`);
    lines.push(`anthropic_output_tokens_total ${snapshot.tokens.totalOutput} ${timestamp}`);

    lines.push(`# HELP anthropic_cost_total Total cost in USD`);
    lines.push(`# TYPE anthropic_cost_total counter`);
    lines.push(`anthropic_cost_total ${snapshot.tokens.totalCost} ${timestamp}`);

    // Model-specific metrics
    for (const [model, stats] of Object.entries(snapshot.models)) {
      const safeModelName = model.replace(/[^a-zA-Z0-9_]/g, '_');
      lines.push(`anthropic_model_requests_total{model="${model}"} ${stats.requests} ${timestamp}`);
      lines.push(`anthropic_model_tokens_total{model="${model}"} ${stats.tokens} ${timestamp}`);
      lines.push(`anthropic_model_cost_total{model="${model}"} ${stats.cost} ${timestamp}`);
    }

    return lines.join('\n') + '\n';
  }
}
```

### Subtask 6.3: Create Performance Monitoring Dashboards

**File**: `src/providers/anthropic/monitoring/performance-monitor.ts`

```typescript
import { EventEmitter } from 'events';
import type { MetricsSnapshot } from './metrics-collector';

export interface PerformanceAlert {
  id: string;
  type: 'latency' | 'error_rate' | 'cost' | 'throughput';
  severity: 'low' | 'medium' | 'high' | 'critical';
  message: string;
  currentValue: number;
  threshold: number;
  timestamp: number;
  resolved: boolean;
  resolvedAt?: number;
}

export interface PerformanceThreshold {
  metric: string;
  operator: '>' | '<' | '=' | '>=' | '<=';
  threshold: number;
  duration: number; // in milliseconds
  severity: 'low' | 'medium' | 'high' | 'critical';
}

export interface PerformanceReport {
  period: {
    start: number;
    end: number;
  };
  summary: {
    totalRequests: number;
    successRate: number;
    averageLatency: number;
    totalCost: number;
    errorRate: number;
  };
  trends: {
    latency: 'improving' | 'degrading' | 'stable';
    errorRate: 'improving' | 'degrading' | 'stable';
    cost: 'increasing' | 'decreasing' | 'stable';
  };
  alerts: PerformanceAlert[];
  recommendations: string[];
}

export class PerformanceMonitor extends EventEmitter {
  private readonly thresholds: Map<string, PerformanceThreshold> = new Map();
  private readonly alerts: Map<string, PerformanceAlert> = new Map();
  private readonly historicalData: MetricsSnapshot[] = [];
  private readonly maxHistorySize: number = 100; // Keep last 100 snapshots

  constructor() {
    super();
    this.setupDefaultThresholds();
  }

  addThreshold(threshold: PerformanceThreshold): void {
    this.thresholds.set(threshold.metric, threshold);

    this.emit('thresholdAdded', threshold);
  }

  removeThreshold(metric: string): void {
    this.thresholds.delete(metric);
    this.emit('thresholdRemoved', metric);
  }

  checkPerformance(snapshot: MetricsSnapshot): PerformanceAlert[] {
    const newAlerts: PerformanceAlert[] = [];
    const now = Date.now();

    // Store historical data
    this.addHistoricalData(snapshot);

    // Check each threshold
    for (const [metric, threshold] of this.thresholds) {
      const currentValue = this.extractMetricValue(snapshot, metric);

      if (currentValue === null) continue;

      const isViolated = this.checkThreshold(currentValue, threshold);
      const existingAlert = this.findActiveAlert(metric);

      if (isViolated && !existingAlert) {
        // New alert
        const alert: PerformanceAlert = {
          id: this.generateAlertId(),
          type: this.getAlertType(metric),
          severity: threshold.severity,
          message: this.generateAlertMessage(metric, currentValue, threshold),
          currentValue,
          threshold: threshold.threshold,
          timestamp: now,
          resolved: false,
        };

        this.alerts.set(alert.id, alert);
        newAlerts.push(alert);

        this.emit('alert', alert);
      } else if (!isViolated && existingAlert) {
        // Resolve alert
        existingAlert.resolved = true;
        existingAlert.resolvedAt = now;

        this.emit('alertResolved', existingAlert);
      }
    }

    return newAlerts;
  }

  generateReport(hours: number = 24): PerformanceReport {
    const now = Date.now();
    const start = now - hours * 60 * 60 * 1000;

    // Get relevant historical data
    const relevantData = this.historicalData.filter((snapshot) => snapshot.timestamp >= start);

    if (relevantData.length === 0) {
      return this.createEmptyReport(start, now);
    }

    const latest = relevantData[relevantData.length - 1];
    const previous = relevantData.length > 1 ? relevantData[relevantData.length - 2] : latest;

    const summary = {
      totalRequests: latest.requests.total,
      successRate:
        latest.requests.total > 0 ? (latest.requests.successful / latest.requests.total) * 100 : 0,
      averageLatency: latest.requests.averageDuration,
      totalCost: latest.tokens.totalCost,
      errorRate:
        latest.requests.total > 0 ? (latest.requests.failed / latest.requests.total) * 100 : 0,
    };

    const trends = {
      latency: this.calculateTrend(
        previous.requests.averageDuration,
        latest.requests.averageDuration
      ),
      errorRate: this.calculateTrend(
        previous.requests.total > 0
          ? (previous.requests.failed / previous.requests.total) * 100
          : 0,
        latest.requests.total > 0 ? (latest.requests.failed / latest.requests.total) * 100 : 0
      ),
      cost: this.calculateTrend(previous.tokens.totalCost, latest.tokens.totalCost),
    };

    const activeAlerts = Array.from(this.alerts.values()).filter((alert) => !alert.resolved);
    const recommendations = this.generateRecommendations(summary, trends, activeAlerts);

    return {
      period: { start, end: now },
      summary,
      trends,
      alerts: activeAlerts,
      recommendations,
    };
  }

  getActiveAlerts(): PerformanceAlert[] {
    return Array.from(this.alerts.values()).filter((alert) => !alert.resolved);
  }

  getAlertHistory(limit?: number): PerformanceAlert[] {
    const allAlerts = Array.from(this.alerts.values()).sort((a, b) => b.timestamp - a.timestamp);

    return limit ? allAlerts.slice(0, limit) : allAlerts;
  }

  resolveAlert(alertId: string): void {
    const alert = this.alerts.get(alertId);
    if (alert && !alert.resolved) {
      alert.resolved = true;
      alert.resolvedAt = Date.now();
      this.emit('alertResolved', alert);
    }
  }

  getPerformanceInsights(): {
    strengths: string[];
    weaknesses: string[];
    opportunities: string[];
  } {
    if (this.historicalData.length < 2) {
      return {
        strengths: ['Insufficient data for analysis'],
        weaknesses: ['Need more historical data'],
        opportunities: ['Continue monitoring to gather insights'],
      };
    }

    const latest = this.historicalData[this.historicalData.length - 1];
    const insights = {
      strengths: [] as string[],
      weaknesses: [] as string[],
      opportunities: [] as string[],
    };

    // Analyze performance
    const successRate =
      latest.requests.total > 0 ? (latest.requests.successful / latest.requests.total) * 100 : 0;

    if (successRate >= 99) {
      insights.strengths.push('Excellent success rate (>99%)');
    } else if (successRate >= 95) {
      insights.strengths.push('Good success rate (>95%)');
    } else if (successRate < 90) {
      insights.weaknesses.push('Low success rate (<90%)');
    }

    const avgLatency = latest.requests.averageDuration;
    if (avgLatency < 1000) {
      insights.strengths.push('Fast response times (<1s average)');
    } else if (avgLatency > 5000) {
      insights.weaknesses.push('Slow response times (>5s average)');
    }

    // Analyze cost efficiency
    const costPerRequest =
      latest.requests.total > 0 ? latest.tokens.totalCost / latest.requests.total : 0;

    if (costPerRequest < 0.01) {
      insights.strengths.push('Cost-effective operations (<$0.01 per request)');
    } else if (costPerRequest > 0.1) {
      insights.weaknesses.push('High cost per request (>$0.10)');
      insights.opportunities.push('Consider optimizing prompts or switching models');
    }

    // Analyze model usage
    const modelCount = Object.keys(latest.models).length;
    if (modelCount === 1) {
      insights.opportunities.push('Consider using multiple models for cost optimization');
    }

    return insights;
  }

  private setupDefaultThresholds(): void {
    // Latency thresholds
    this.addThreshold({
      metric: 'averageLatency',
      operator: '>',
      threshold: 5000, // 5 seconds
      duration: 300000, // 5 minutes
      severity: 'high',
    });

    this.addThreshold({
      metric: 'averageLatency',
      operator: '>',
      threshold: 10000, // 10 seconds
      duration: 60000, // 1 minute
      severity: 'critical',
    });

    // Error rate thresholds
    this.addThreshold({
      metric: 'errorRate',
      operator: '>',
      threshold: 5, // 5%
      duration: 300000, // 5 minutes
      severity: 'medium',
    });

    this.addThreshold({
      metric: 'errorRate',
      operator: '>',
      threshold: 10, // 10%
      duration: 60000, // 1 minute
      severity: 'high',
    });

    // Cost thresholds
    this.addThreshold({
      metric: 'totalCost',
      operator: '>',
      threshold: 100, // $100
      duration: 3600000, // 1 hour
      severity: 'medium',
    });
  }

  private extractMetricValue(snapshot: MetricsSnapshot, metric: string): number | null {
    switch (metric) {
      case 'averageLatency':
        return snapshot.requests.averageDuration;
      case 'errorRate':
        return snapshot.requests.total > 0
          ? (snapshot.requests.failed / snapshot.requests.total) * 100
          : 0;
      case 'totalCost':
        return snapshot.tokens.totalCost;
      case 'successRate':
        return snapshot.requests.total > 0
          ? (snapshot.requests.successful / snapshot.requests.total) * 100
          : 0;
      default:
        return null;
    }
  }

  private checkThreshold(value: number, threshold: PerformanceThreshold): boolean {
    switch (threshold.operator) {
      case '>':
        return value > threshold.threshold;
      case '<':
        return value < threshold.threshold;
      case '>=':
        return value >= threshold.threshold;
      case '<=':
        return value <= threshold.threshold;
      case '=':
        return value === threshold.threshold;
      default:
        return false;
    }
  }

  private findActiveAlert(metric: string): PerformanceAlert | undefined {
    return Array.from(this.alerts.values()).find(
      (alert) => !alert.resolved && alert.type === this.getAlertType(metric)
    );
  }

  private getAlertType(metric: string): PerformanceAlert['type'] {
    if (metric.includes('latency') || metric.includes('duration')) return 'latency';
    if (metric.includes('error')) return 'error_rate';
    if (metric.includes('cost')) return 'cost';
    if (metric.includes('request') || metric.includes('throughput')) return 'throughput';
    return 'latency'; // default
  }

  private generateAlertMessage(
    metric: string,
    currentValue: number,
    threshold: PerformanceThreshold
  ): string {
    const unit = this.getMetricUnit(metric);
    return `${metric} is ${currentValue}${unit}, which exceeds threshold of ${threshold.threshold}${unit}`;
  }

  private getMetricUnit(metric: string): string {
    if (metric.includes('latency') || metric.includes('duration')) return 'ms';
    if (metric.includes('rate')) return '%';
    if (metric.includes('cost')) return ' USD';
    return '';
  }

  private generateAlertId(): string {
    return `alert_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private addHistoricalData(snapshot: MetricsSnapshot): void {
    this.historicalData.push(snapshot);

    // Trim history
    if (this.historicalData.length > this.maxHistorySize) {
      const excess = this.historicalData.length - this.maxHistorySize;
      this.historicalData.splice(0, excess);
    }
  }

  private calculateTrend(previous: number, current: number): 'improving' | 'degrading' | 'stable' {
    const change = ((current - previous) / previous) * 100;

    if (Math.abs(change) < 5) return 'stable';

    // For latency and error rate, lower is better
    if (previous > 0) {
      return change < 0 ? 'improving' : 'degrading';
    }

    return 'stable';
  }

  private generateRecommendations(summary: any, trends: any, alerts: PerformanceAlert[]): string[] {
    const recommendations: string[] = [];

    // Latency recommendations
    if (summary.averageLatency > 3000) {
      recommendations.push('Consider optimizing prompts or using faster models to reduce latency');
    }

    // Error rate recommendations
    if (summary.errorRate > 5) {
      recommendations.push(
        'Investigate and address the root cause of errors to improve reliability'
      );
    }

    // Cost recommendations
    if (summary.totalCost > 50) {
      recommendations.push('Monitor usage closely and consider cost optimization strategies');
    }

    // Trend-based recommendations
    if (trends.latency === 'degrading') {
      recommendations.push('Latency is trending upward - investigate performance bottlenecks');
    }

    if (trends.errorRate === 'degrading') {
      recommendations.push('Error rate is increasing - review recent changes and system health');
    }

    // Alert-based recommendations
    const criticalAlerts = alerts.filter((alert) => alert.severity === 'critical');
    if (criticalAlerts.length > 0) {
      recommendations.push('Address critical alerts immediately to prevent service impact');
    }

    return recommendations;
  }

  private createEmptyReport(start: number, end: number): PerformanceReport {
    return {
      period: { start, end },
      summary: {
        totalRequests: 0,
        successRate: 0,
        averageLatency: 0,
        totalCost: 0,
        errorRate: 0,
      },
      trends: {
        latency: 'stable',
        errorRate: 'stable',
        cost: 'stable',
      },
      alerts: [],
      recommendations: ['Insufficient data for analysis - continue monitoring'],
    };
  }
}
```

### Subtask 6.4: Add Alerting for Performance Degradation

**File**: `src/providers/anthropic/monitoring/alerting-system.ts`

```typescript
import { EventEmitter } from 'events';
import type { PerformanceAlert } from './performance-monitor';

export interface AlertChannel {
  id: string;
  name: string;
  type: 'email' | 'webhook' | 'slack' | 'pagerduty';
  config: Record<string, unknown>;
  enabled: boolean;
  rateLimit?: {
    maxAlerts: number;
    windowMs: number;
  };
}

export interface AlertRule {
  id: string;
  name: string;
  description: string;
  condition: {
    metric: string;
    operator: '>' | '<' | '=' | '>=' | '<=';
    threshold: number;
    duration: number;
  };
  channels: string[];
  enabled: boolean;
  severity: 'low' | 'medium' | 'high' | 'critical';
  cooldown: number; // Cooldown period in milliseconds
}

export interface AlertNotification {
  id: string;
  ruleId: string;
  alert: PerformanceAlert;
  channel: AlertChannel;
  sentAt: number;
  status: 'pending' | 'sent' | 'failed';
  error?: string;
}

export class AlertingSystem extends EventEmitter {
  private readonly channels: Map<string, AlertChannel> = new Map();
  private readonly rules: Map<string, AlertRule> = new Map();
  private readonly notifications: AlertNotification[] = [];
  private readonly rateLimitTracker: Map<string, number[]> = new Map();
  private readonly maxNotifications: number = 1000;

  constructor() {
    super();
    this.setupDefaultChannels();
  }

  addChannel(channel: AlertChannel): void {
    this.validateChannel(channel);
    this.channels.set(channel.id, channel);
    this.emit('channelAdded', channel);
  }

  updateChannel(channelId: string, updates: Partial<AlertChannel>): void {
    const existing = this.channels.get(channelId);
    if (!existing) {
      throw new Error(`Channel ${channelId} not found`);
    }

    const updated = { ...existing, ...updates };
    this.validateChannel(updated);
    this.channels.set(channelId, updated);
    this.emit('channelUpdated', updated);
  }

  removeChannel(channelId: string): void {
    this.channels.delete(channelId);
    this.emit('channelRemoved', channelId);
  }

  addRule(rule: AlertRule): void {
    this.validateRule(rule);
    this.rules.set(rule.id, rule);
    this.emit('ruleAdded', rule);
  }

  updateRule(ruleId: string, updates: Partial<AlertRule>): void {
    const existing = this.rules.get(ruleId);
    if (!existing) {
      throw new Error(`Rule ${ruleId} not found`);
    }

    const updated = { ...existing, ...updates };
    this.validateRule(updated);
    this.rules.set(ruleId, updated);
    this.emit('ruleUpdated', updated);
  }

  removeRule(ruleId: string): void {
    this.rules.delete(ruleId);
    this.emit('ruleRemoved', ruleId);
  }

  async processAlert(alert: PerformanceAlert): Promise<AlertNotification[]> {
    const matchingRules = this.findMatchingRules(alert);
    const notifications: AlertNotification[] = [];

    for (const rule of matchingRules) {
      if (!this.shouldSendNotification(rule, alert)) {
        continue;
      }

      for (const channelId of rule.channels) {
        const channel = this.channels.get(channelId);
        if (!channel || !channel.enabled) {
          continue;
        }

        if (this.isRateLimited(channel)) {
          continue;
        }

        const notification: AlertNotification = {
          id: this.generateNotificationId(),
          ruleId: rule.id,
          alert,
          channel,
          sentAt: Date.now(),
          status: 'pending',
        };

        try {
          await this.sendNotification(notification);
          notification.status = 'sent';
          this.trackRateLimit(channel);
        } catch (error) {
          notification.status = 'failed';
          notification.error = error.message;
        }

        this.notifications.push(notification);
        notifications.push(notification);
        this.emit('notificationSent', notification);
      }
    }

    this.trimNotifications();
    return notifications;
  }

  getChannels(): AlertChannel[] {
    return Array.from(this.channels.values());
  }

  getRules(): AlertRule[] {
    return Array.from(this.rules.values());
  }

  getNotifications(limit?: number, status?: 'pending' | 'sent' | 'failed'): AlertNotification[] {
    let notifications = [...this.notifications].sort((a, b) => b.sentAt - a.sentAt);

    if (status) {
      notifications = notifications.filter((n) => n.status === status);
    }

    return limit ? notifications.slice(0, limit) : notifications;
  }

  testChannel(channelId: string): Promise<boolean> {
    const channel = this.channels.get(channelId);
    if (!channel) {
      throw new Error(`Channel ${channelId} not found`);
    }

    return this.sendTestNotification(channel);
  }

  private setupDefaultChannels(): void {
    // Default console channel for development
    this.addChannel({
      id: 'console',
      name: 'Console Output',
      type: 'webhook',
      config: {
        url: 'console://output',
      },
      enabled: true,
    });
  }

  private validateChannel(channel: AlertChannel): void {
    if (!channel.id || !channel.name || !channel.type) {
      throw new Error('Channel must have id, name, and type');
    }

    const validTypes = ['email', 'webhook', 'slack', 'pagerduty'];
    if (!validTypes.includes(channel.type)) {
      throw new Error(`Invalid channel type: ${channel.type}`);
    }

    // Type-specific validation
    switch (channel.type) {
      case 'email':
        if (!channel.config.to || !Array.isArray(channel.config.to)) {
          throw new Error('Email channel must have "to" array');
        }
        break;
      case 'webhook':
        if (!channel.config.url) {
          throw new Error('Webhook channel must have "url"');
        }
        break;
      case 'slack':
        if (!channel.config.webhookUrl) {
          throw new Error('Slack channel must have "webhookUrl"');
        }
        break;
      case 'pagerduty':
        if (!channel.config.integrationKey) {
          throw new Error('PagerDuty channel must have "integrationKey"');
        }
        break;
    }
  }

  private validateRule(rule: AlertRule): void {
    if (!rule.id || !rule.name || !rule.condition) {
      throw new Error('Rule must have id, name, and condition');
    }

    if (!rule.channels || !Array.isArray(rule.channels)) {
      throw new Error('Rule must have channels array');
    }

    // Validate that all channels exist
    for (const channelId of rule.channels) {
      if (!this.channels.has(channelId)) {
        throw new Error(`Channel ${channelId} not found`);
      }
    }
  }

  private findMatchingRules(alert: PerformanceAlert): AlertRule[] {
    return Array.from(this.rules.values()).filter((rule) => {
      if (!rule.enabled) return false;

      // Check if rule matches alert type and severity
      const conditionMatches = this.matchesCondition(alert, rule.condition);
      const severityMatches = this.matchesSeverity(alert.severity, rule.severity);

      return conditionMatches && severityMatches;
    });
  }

  private matchesCondition(alert: PerformanceAlert, condition: any): boolean {
    // This is a simplified matching - in practice, you'd have more sophisticated
    // condition evaluation based on the alert type and metrics
    return alert.type === condition.metric;
  }

  private matchesSeverity(
    alertSeverity: PerformanceAlert['severity'],
    ruleSeverity: AlertRule['severity']
  ): boolean {
    const severityLevels = { low: 1, medium: 2, high: 3, critical: 4 };
    return severityLevels[alertSeverity] >= severityLevels[ruleSeverity];
  }

  private shouldSendNotification(rule: AlertRule, alert: PerformanceAlert): boolean {
    // Check cooldown period
    const recentNotifications = this.notifications.filter(
      (n) =>
        n.ruleId === rule.id && n.alert.type === alert.type && Date.now() - n.sentAt < rule.cooldown
    );

    return recentNotifications.length === 0;
  }

  private isRateLimited(channel: AlertChannel): boolean {
    if (!channel.rateLimit) return false;

    const now = Date.now();
    const windowStart = now - channel.rateLimit.windowMs;

    if (!this.rateLimitTracker.has(channel.id)) {
      this.rateLimitTracker.set(channel.id, []);
    }

    const timestamps = this.rateLimitTracker.get(channel.id)!;

    // Remove old timestamps
    for (let i = timestamps.length - 1; i >= 0; i--) {
      if (timestamps[i] < windowStart) {
        timestamps.splice(0, i + 1);
        break;
      }
    }

    return timestamps.length >= channel.rateLimit.maxAlerts;
  }

  private trackRateLimit(channel: AlertChannel): void {
    if (!channel.rateLimit) return;

    if (!this.rateLimitTracker.has(channel.id)) {
      this.rateLimitTracker.set(channel.id, []);
    }

    this.rateLimitTracker.get(channel.id)!.push(Date.now());
  }

  private async sendNotification(notification: AlertNotification): Promise<void> {
    const { channel, alert } = notification;

    switch (channel.type) {
      case 'console':
        this.sendConsoleNotification(alert);
        break;
      case 'email':
        await this.sendEmailNotification(channel, alert);
        break;
      case 'webhook':
        await this.sendWebhookNotification(channel, alert);
        break;
      case 'slack':
        await this.sendSlackNotification(channel, alert);
        break;
      case 'pagerduty':
        await this.sendPagerDutyNotification(channel, alert);
        break;
      default:
        throw new Error(`Unknown channel type: ${channel.type}`);
    }
  }

  private sendConsoleNotification(alert: PerformanceAlert): void {
    console.log(`🚨 ALERT [${alert.severity.toUpperCase()}] ${alert.message}`);
    console.log(`   Current: ${alert.currentValue}, Threshold: ${alert.threshold}`);
    console.log(`   Time: ${new Date(alert.timestamp).toISOString()}`);
  }

  private async sendEmailNotification(
    channel: AlertChannel,
    alert: PerformanceAlert
  ): Promise<void> {
    // In a real implementation, you'd use an email service
    console.log(`Email notification sent to ${channel.config.to} for alert: ${alert.message}`);
  }

  private async sendWebhookNotification(
    channel: AlertChannel,
    alert: PerformanceAlert
  ): Promise<void> {
    const payload = {
      alert,
      timestamp: new Date().toISOString(),
    };

    // In a real implementation, you'd make an HTTP request
    console.log(`Webhook notification sent to ${channel.config.url} with payload:`, payload);
  }

  private async sendSlackNotification(
    channel: AlertChannel,
    alert: PerformanceAlert
  ): Promise<void> {
    const payload = {
      text: `🚨 ${alert.severity.toUpperCase()} Alert`,
      attachments: [
        {
          color: this.getSeverityColor(alert.severity),
          fields: [
            { title: 'Message', value: alert.message, short: false },
            { title: 'Current Value', value: alert.currentValue.toString(), short: true },
            { title: 'Threshold', value: alert.threshold.toString(), short: true },
            { title: 'Time', value: new Date(alert.timestamp).toISOString(), short: true },
          ],
        },
      ],
    };

    // In a real implementation, you'd send to Slack webhook
    console.log(`Slack notification sent to ${channel.config.webhookUrl} with payload:`, payload);
  }

  private async sendPagerDutyNotification(
    channel: AlertChannel,
    alert: PerformanceAlert
  ): Promise<void> {
    const payload = {
      routing_key: channel.config.integrationKey,
      event_action: 'trigger',
      payload: {
        summary: alert.message,
        source: 'anthropic-provider',
        severity: alert.severity,
        timestamp: alert.timestamp / 1000,
        custom_details: {
          currentValue: alert.currentValue,
          threshold: alert.threshold,
          alertType: alert.type,
        },
      },
    };

    // In a real implementation, you'd send to PagerDuty API
    console.log(`PagerDuty notification sent with payload:`, payload);
  }

  private async sendTestNotification(channel: AlertChannel): Promise<boolean> {
    try {
      const testAlert: PerformanceAlert = {
        id: 'test-alert',
        type: 'latency',
        severity: 'low',
        message: 'This is a test notification',
        currentValue: 100,
        threshold: 50,
        timestamp: Date.now(),
        resolved: false,
      };

      const notification: AlertNotification = {
        id: this.generateNotificationId(),
        ruleId: 'test-rule',
        alert: testAlert,
        channel,
        sentAt: Date.now(),
        status: 'pending',
      };

      await this.sendNotification(notification);
      return true;
    } catch (error) {
      console.error(`Test notification failed for channel ${channel.id}:`, error);
      return false;
    }
  }

  private getSeverityColor(severity: PerformanceAlert['severity']): string {
    const colors = {
      low: 'good',
      medium: 'warning',
      high: 'danger',
      critical: '#ff0000',
    };
    return colors[severity] || 'good';
  }

  private generateNotificationId(): string {
    return `notif_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private trimNotifications(): void {
    if (this.notifications.length > this.maxNotifications) {
      const excess = this.notifications.length - this.maxNotifications;
      this.notifications.splice(0, excess);
    }
  }
}
```

## Integration with Main Provider

**Update to**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
// Add these imports and update the provider
import { ProviderLogger } from './logging/provider-logger';
import { MetricsCollector } from './monitoring/metrics-collector';
import { PerformanceMonitor } from './monitoring/performance-monitor';
import { AlertingSystem } from './monitoring/alerting-system';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly logger: ProviderLogger;
  private readonly metrics: MetricsCollector;
  private readonly performanceMonitor: PerformanceMonitor;
  private readonly alertingSystem: AlertingSystem;

  constructor(config: AnthropicProviderConfig) {
    // ... existing constructor code ...

    // Initialize monitoring components
    this.logger = ProviderLogger.createDefault();
    this.metrics = new MetricsCollector();
    this.performanceMonitor = new PerformanceMonitor();
    this.alertingSystem = new AlertingSystem();

    // Setup monitoring integration
    this.setupMonitoringIntegration();
  }

  async sendMessage(
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    const startTime = Date.now();
    let success = false;
    let inputTokens = 0;
    let outputTokens = 0;
    let cost = 0;

    try {
      this.logger.logApiCallStart('sendMessage', request, {
        requestId: options.requestId,
        userId: options.userId,
      });

      // Count input tokens
      inputTokens = await this.tokenCounter.countInputTokens(request);

      // Create stream with monitoring
      const baseStream = await this.messageStream.createStream(this.client, request, options);

      // Wrap stream for monitoring
      return this.createMonitoredStream(baseStream, request, startTime, inputTokens, options);
    } catch (error) {
      const duration = Date.now() - startTime;

      this.logger.logApiCallError('sendMessage', request, error as Error, duration, {
        requestId: options.requestId,
        userId: options.userId,
      });

      this.metrics.recordError('sendMessage', this.getErrorType(error), request.model, duration);

      throw error;
    }
  }

  private createMonitoredStream(
    baseStream: AsyncIterable<MessageChunk>,
    request: MessageRequest,
    startTime: number,
    inputTokens: number,
    options: StreamOptions
  ): AsyncIterable<MessageChunk> {
    let chunkCount = 0;
    let outputTokens = 0;

    this.logger.logStreamStart(request, {
      requestId: options.requestId,
      userId: options.userId,
    });

    return {
      [Symbol.asyncIterator]() {
        const iterator = baseStream[Symbol.asyncIterator]();

        return {
          async next(): Promise<IteratorResult<MessageChunk>> {
            try {
              const result = await iterator.next();

              if (result.value) {
                chunkCount++;

                // Count output tokens
                if (result.value.content) {
                  outputTokens += await this.tokenCounter.countTextTokens(result.value.content, {
                    model: request.model,
                    includeSystemPrompt: false,
                    includeTools: false,
                  });
                }

                this.logger.logStreamChunk(result.value, chunkCount, {
                  requestId: options.requestId,
                  userId: options.userId,
                });
              }

              if (result.done) {
                const duration = Date.now() - startTime;
                const cost = this.costCalculator.calculateCost(request.model, {
                  input: inputTokens,
                  output: outputTokens,
                  total: inputTokens + outputTokens,
                });

                // Record metrics
                this.metrics.recordRequest(
                  'sendMessage',
                  request.model,
                  true,
                  duration,
                  inputTokens,
                  outputTokens,
                  cost.totalCost
                );

                this.metrics.recordStreamMetrics(request.model, chunkCount, duration, true);

                // Log completion
                this.logger.logStreamEnd(request, chunkCount, duration, {
                  requestId: options.requestId,
                  userId: options.userId,
                });

                this.logger.logTokenUsage(
                  request.model,
                  inputTokens,
                  outputTokens,
                  cost.totalCost,
                  {
                    requestId: options.requestId,
                    userId: options.userId,
                  }
                );

                // Check performance and generate alerts
                const snapshot = this.metrics.getSnapshot();
                const alerts = this.performanceMonitor.checkPerformance(snapshot);

                if (alerts.length > 0) {
                  await this.alertingSystem.processAlert(alerts[0]);
                }
              }

              return result;
            } catch (error) {
              const duration = Date.now() - startTime;

              this.logger.logStreamError(request, error as Error, chunkCount, duration, {
                requestId: options.requestId,
                userId: options.userId,
              });

              this.metrics.recordError('stream', this.getErrorType(error), request.model, duration);
              this.metrics.recordStreamMetrics(request.model, chunkCount, duration, false);

              throw error;
            }
          },
        };
      },
    };
  }

  private setupMonitoringIntegration(): void {
    // Forward metrics events to performance monitor
    this.metrics.on('request', (data) => {
      // Performance monitoring will be updated periodically
    });

    this.metrics.on('error', (data) => {
      this.logger.logApiCallError(
        data.operation,
        {} as any,
        new Error(data.errorType),
        data.duration || 0
      );
    });

    // Forward alert events to logger
    this.alertingSystem.on('notificationSent', (notification) => {
      this.logger.logPerformanceMetrics('alert_sent', {
        alertId: notification.alert.id,
        severity: notification.alert.severity,
        channel: notification.channel.type,
      });
    });
  }

  // Public methods for monitoring
  getMetrics(): any {
    return this.metrics.getSnapshot();
  }

  getPerformanceReport(hours?: number): any {
    return this.performanceMonitor.generateReport(hours);
  }

  getActiveAlerts(): any[] {
    return this.performanceMonitor.getActiveAlerts();
  }

  exportMetrics(format?: 'json' | 'prometheus'): string {
    return this.metrics.exportMetrics(format);
  }

  private getErrorType(error: any): string {
    if (error.constructor) {
      return error.constructor.name;
    }
    return 'UnknownError';
  }
}
```

## Dependencies

### Internal Dependencies

- `@tamma/observability` - Base logging utilities
- Previous tasks components for integration

### External Dependencies

- Node.js EventEmitter for event handling

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/logging/provider-logger.test.ts
describe('ProviderLogger', () => {
  describe('API call logging', () => {
    it('should log API call start correctly');
    it('should log API call success with metrics');
    it('should log API call errors with context');
  });
});

// tests/providers/anthropic/monitoring/metrics-collector.test.ts
describe('MetricsCollector', () => {
  describe('metric recording', () => {
    it('should record request metrics');
    it('should calculate summaries correctly');
    it('should export metrics in different formats');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/monitoring/integration.test.ts
describe('Monitoring Integration', () => {
  it('should collect metrics during real API calls');
  it('should generate performance alerts');
  it('should send notifications for critical issues');
});
```

## Risk Mitigation

### Performance Risks

1. **Monitoring Overhead**: Monitoring impacting request performance
   - Mitigation: Async operations, sampling, efficient data structures
2. **Memory Usage**: Metrics consuming excessive memory
   - Mitigation: Data retention limits, efficient storage
3. **Log Volume**: Excessive logging affecting performance
   - Mitigation: Configurable log levels, structured logging

### Operational Risks

1. **Alert Fatigue**: Too many alerts causing ignore
   - Mitigation: Smart thresholds, cooldown periods, severity levels
2. **False Positives**: Incorrect alerts triggering unnecessarily
   - Mitigation: Proper threshold tuning, confirmation periods
3. **Notification Failures**: Alert delivery failures
   - Mitigation: Multiple channels, retry logic, fallback mechanisms

## Deliverables

1. **Provider Logger**: Structured logging for all operations
2. **Metrics Collector**: Comprehensive metrics collection and aggregation
3. **Performance Monitor**: Performance analysis and alerting
4. **Alerting System**: Multi-channel alert notifications
5. **Integration**: Full monitoring integration with provider
6. **Unit Tests**: Comprehensive test coverage
7. **Integration Tests**: End-to-end monitoring validation
8. **Documentation**: Monitoring setup and configuration guide

## Success Criteria

- [ ] Structured logging for all API operations
- [ ] Comprehensive metrics collection with retention
- [ ] Real-time performance monitoring and alerting
- [ ] Multi-channel alert notifications
- [ ] Performance reports with insights and recommendations
- [ ] Export capabilities for external monitoring systems
- [ ] Minimal performance overhead (<5% impact)
- [ ] Comprehensive test coverage

## File Structure

```
src/providers/anthropic/logging/
├── provider-logger.ts         # Structured logging implementation
└── index.ts                   # Public exports

src/providers/anthropic/monitoring/
├── metrics-collector.ts       # Metrics collection and aggregation
├── performance-monitor.ts     # Performance analysis and alerting
├── alerting-system.ts         # Multi-channel notifications
└── index.ts                   # Public exports

tests/providers/anthropic/logging/
├── provider-logger.test.ts
└── integration.test.ts

tests/providers/anthropic/monitoring/
├── metrics-collector.test.ts
├── performance-monitor.test.ts
├── alerting-system.test.ts
└── integration.test.ts
```

This task provides comprehensive monitoring and logging capabilities that enable full observability of the Anthropic provider, with real-time alerting, performance analysis, and actionable insights for optimization and reliability.
