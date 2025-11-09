# Task 4: Token & Cost Management

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: Medium

## Overview

Implement accurate token counting, cost calculation, and usage tracking for the Anthropic Claude provider. This enables precise cost monitoring, budget management, and optimization recommendations for users.

## Detailed Implementation Plan

### Subtask 4.1: Implement Accurate Token Counting

**File**: `src/providers/anthropic/token-management/token-counter.ts`

````typescript
import { logger } from '@tamma/observability';
import type { MessageRequest, MessageChunk } from '@tamma/shared/contracts';
import type { AnthropicMessage, AnthropicTool } from '../types/anthropic-types';

export interface TokenCount {
  input: number;
  output: number;
  total: number;
}

export interface TokenCountOptions {
  model: string;
  includeSystemPrompt: boolean;
  includeTools: boolean;
}

export class TokenCounter {
  private readonly tiktokenInstance: any = null; // Will be lazy-loaded
  private readonly modelTokenizers: Map<string, string> = new Map();

  constructor() {
    // Map Anthropic models to their tokenizer encoding
    this.modelTokenizers.set('claude-3-5-sonnet-20241022', 'cl100k_base');
    this.modelTokenizers.set('claude-3-5-haiku-20241022', 'cl100k_base');
    this.modelTokenizers.set('claude-3-opus-20240229', 'cl100k_base');
  }

  async countInputTokens(request: MessageRequest): Promise<number> {
    const options: TokenCountOptions = {
      model: request.model,
      includeSystemPrompt: true,
      includeTools: true,
    };

    let totalTokens = 0;

    // Count message tokens
    for (const message of request.messages) {
      totalTokens += await this.countMessageTokens(message, options);
    }

    // Count system prompt tokens
    if (options.includeSystemPrompt && request.system) {
      totalTokens += await this.countTextTokens(request.system, options);
    }

    // Count tool definition tokens
    if (options.includeTools && request.tools) {
      totalTokens += await this.countToolTokens(request.tools, options);
    }

    // Add overhead tokens for API formatting
    totalTokens += this.countOverheadTokens(request, options);

    logger.debug('Input tokens counted', {
      model: request.model,
      totalTokens,
      messageCount: request.messages.length,
      hasSystemPrompt: !!request.system,
      toolCount: request.tools?.length || 0,
    });

    return totalTokens;
  }

  async countOutputTokens(chunks: MessageChunk[]): Promise<number> {
    let totalTokens = 0;

    for (const chunk of chunks) {
      // Count content tokens
      if (chunk.content) {
        totalTokens += await this.countTextTokens(chunk.content, {
          model: chunk.metadata?.model || 'claude-3-5-sonnet-20241022',
          includeSystemPrompt: false,
          includeTools: false,
        });
      }

      // Count tool call tokens
      if (chunk.toolCalls) {
        for (const toolCall of chunk.toolCalls) {
          totalTokens += await this.countToolCallTokens(toolCall, {
            model: chunk.metadata?.model || 'claude-3-5-sonnet-20241022',
            includeSystemPrompt: false,
            includeTools: false,
          });
        }
      }
    }

    return totalTokens;
  }

  async countMessageTokens(message: any, options: TokenCountOptions): Promise<number> {
    let tokens = 0;

    // Count role tokens
    tokens += await this.countTextTokens(message.role, options);

    // Count content tokens
    if (typeof message.content === 'string') {
      tokens += await this.countTextTokens(message.content, options);
    } else if (Array.isArray(message.content)) {
      for (const contentBlock of message.content) {
        tokens += await this.countContentBlockTokens(contentBlock, options);
      }
    }

    // Add formatting overhead
    tokens += 3; // Approximately 3 tokens for message formatting

    return tokens;
  }

  async countContentBlockTokens(contentBlock: any, options: TokenCountOptions): Promise<number> {
    let tokens = 0;

    // Count type identifier
    tokens += await this.countTextTokens(contentBlock.type, options);

    switch (contentBlock.type) {
      case 'text':
        if (contentBlock.text) {
          tokens += await this.countTextTokens(contentBlock.text, options);
        }
        break;

      case 'tool_use':
        if (contentBlock.id) {
          tokens += await this.countTextTokens(contentBlock.id, options);
        }
        if (contentBlock.name) {
          tokens += await this.countTextTokens(contentBlock.name, options);
        }
        if (contentBlock.input) {
          tokens += await this.countTextTokens(JSON.stringify(contentBlock.input), options);
        }
        break;

      case 'tool_result':
        if (contentBlock.tool_use_id) {
          tokens += await this.countTextTokens(contentBlock.tool_use_id, options);
        }
        if (contentBlock.content) {
          if (typeof contentBlock.content === 'string') {
            tokens += await this.countTextTokens(contentBlock.content, options);
          } else if (Array.isArray(contentBlock.content)) {
            for (const block of contentBlock.content) {
              tokens += await this.countContentBlockTokens(block, options);
            }
          }
        }
        break;

      default:
        logger.warn('Unknown content block type for token counting', {
          type: contentBlock.type,
        });
    }

    return tokens;
  }

  async countToolTokens(tools: AnthropicTool[], options: TokenCountOptions): Promise<number> {
    let tokens = 0;

    for (const tool of tools) {
      // Count tool name and description
      tokens += await this.countTextTokens(tool.name, options);
      tokens += await this.countTextTokens(tool.description, options);

      // Count tool schema
      if (tool.input_schema) {
        tokens += await this.countTextTokens(JSON.stringify(tool.input_schema), options);
      }

      // Add formatting overhead
      tokens += 5; // Approximately 5 tokens per tool for formatting
    }

    return tokens;
  }

  async countToolCallTokens(toolCall: any, options: TokenCountOptions): Promise<number> {
    let tokens = 0;

    if (toolCall.id) {
      tokens += await this.countTextTokens(toolCall.id, options);
    }
    if (toolCall.name) {
      tokens += await this.countTextTokens(toolCall.name, options);
    }
    if (toolCall.arguments) {
      tokens += await this.countTextTokens(toolCall.arguments, options);
    }

    // Add formatting overhead
    tokens += 3; // Approximately 3 tokens for tool call formatting

    return tokens;
  }

  async countTextTokens(text: string, options: TokenCountOptions): Promise<number> {
    if (!text || text.length === 0) {
      return 0;
    }

    try {
      // Use tiktoken for accurate counting if available
      if (this.tiktokenInstance) {
        const encoding = this.getEncodingForModel(options.model);
        const tokens = encoding.encode(text);
        return tokens.length;
      }
    } catch (error) {
      logger.warn('Failed to use tiktoken, falling back to estimation', {
        error: error.message,
        model: options.model,
      });
    }

    // Fallback to character-based estimation
    return this.estimateTokensFromText(text);
  }

  private countOverheadTokens(request: MessageRequest, options: TokenCountOptions): number {
    // Estimate overhead tokens for API formatting
    let overhead = 10; // Base overhead for request structure

    if (request.system) {
      overhead += 3; // System prompt formatting
    }

    if (request.tools && request.tools.length > 0) {
      overhead += 5; // Tools array formatting
    }

    if (request.temperature !== undefined) {
      overhead += 1;
    }
    if (request.topP !== undefined) {
      overhead += 1;
    }
    if (request.stopSequences && request.stopSequences.length > 0) {
      overhead += 2 + request.stopSequences.length;
    }

    return overhead;
  }

  private getEncodingForModel(model: string): any {
    const encodingName = this.modelTokenizers.get(model) || 'cl100k_base';

    // This would be implemented with actual tiktoken
    // For now, return a mock encoding
    return {
      encode: (text: string) => this.mockEncode(text),
      decode: (tokens: number[]) => this.mockDecode(tokens),
    };
  }

  private mockEncode(text: string): number[] {
    // Mock encoding - in reality, this would use tiktoken
    // Rough approximation: ~4 characters per token
    const tokens = [];
    for (let i = 0; i < text.length; i += 4) {
      tokens.push(Math.floor(Math.random() * 100000));
    }
    return tokens;
  }

  private mockDecode(tokens: number[]): string {
    // Mock decoding - not used in our implementation
    return '';
  }

  private estimateTokensFromText(text: string): number {
    // Character-based estimation
    // Claude uses approximately 4 characters per token on average
    const charCount = text.length;
    const estimatedTokens = Math.ceil(charCount / 4);

    // Adjust for common patterns
    if (text.includes('```')) {
      // Code blocks tend to have more tokens per character
      return Math.ceil(estimatedTokens * 1.2);
    }

    if (text.includes('{') && text.includes('}')) {
      // JSON tends to have more tokens per character
      return Math.ceil(estimatedTokens * 1.3);
    }

    return estimatedTokens;
  }

  // Static utility methods
  static async estimateRequestCost(
    request: MessageRequest,
    pricing: { input: number; output: number }
  ): Promise<{ inputCost: number; estimatedOutputCost: number }> {
    const counter = new TokenCounter();
    const inputTokens = await counter.countInputTokens(request);

    // Estimate output tokens (roughly 25% of input for typical responses)
    const estimatedOutputTokens = Math.ceil(inputTokens * 0.25);

    return {
      inputCost: (inputTokens / 1000000) * pricing.input,
      estimatedOutputCost: (estimatedOutputTokens / 1000000) * pricing.output,
    };
  }
}
````

### Subtask 4.2: Add Cost Calculation

**File**: `src/providers/anthropic/cost-management/cost-calculator.ts`

```typescript
import { logger } from '@tamma/observability';
import type { TokenCount, PricingInfo } from '@tamma/shared/contracts';
import { CLAUDE_MODELS } from '../models/claude-models';

export interface CostBreakdown {
  inputCost: number;
  outputCost: number;
  totalCost: number;
  currency: string;
  pricing: PricingInfo;
}

export interface CostMetrics {
  requestCount: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  averageCostPerRequest: number;
  averageTokensPerRequest: number;
}

export class CostCalculator {
  private readonly modelPricing: Map<string, PricingInfo> = new Map();

  constructor() {
    // Initialize pricing from model definitions
    Object.values(CLAUDE_MODELS).forEach((model) => {
      this.modelPricing.set(model.id, model.pricing);
    });
  }

  calculateCost(model: string, tokenCount: TokenCount, currency: string = 'USD'): CostBreakdown {
    const pricing = this.modelPricing.get(model);
    if (!pricing) {
      throw new Error(`No pricing information available for model: ${model}`);
    }

    const inputCost = (tokenCount.input / 1000000) * pricing.input;
    const outputCost = (tokenCount.output / 1000000) * pricing.output;
    const totalCost = inputCost + outputCost;

    const breakdown: CostBreakdown = {
      inputCost: this.roundCost(inputCost),
      outputCost: this.roundCost(outputCost),
      totalCost: this.roundCost(totalCost),
      currency,
      pricing,
    };

    logger.debug('Cost calculated', {
      model,
      inputTokens: tokenCount.input,
      outputTokens: tokenCount.output,
      inputCost: breakdown.inputCost,
      outputCost: breakdown.outputCost,
      totalCost: breakdown.totalCost,
    });

    return breakdown;
  }

  calculateBatchCosts(
    requests: Array<{
      model: string;
      tokenCount: TokenCount;
    }>,
    currency: string = 'USD'
  ): CostBreakdown {
    let totalInputCost = 0;
    let totalOutputCost = 0;
    let totalInputTokens = 0;
    let totalOutputTokens = 0;

    for (const request of requests) {
      const breakdown = this.calculateCost(request.model, request.tokenCount, currency);
      totalInputCost += breakdown.inputCost;
      totalOutputCost += breakdown.outputCost;
      totalInputTokens += request.tokenCount.input;
      totalOutputTokens += request.tokenCount.output;
    }

    return {
      inputCost: this.roundCost(totalInputCost),
      outputCost: this.roundCost(totalOutputCost),
      totalCost: this.roundCost(totalInputCost + totalOutputCost),
      currency,
      pricing: {
        input: totalInputCost / (totalInputTokens / 1000000),
        output: totalOutputCost / (totalOutputTokens / 1000000),
      },
    };
  }

  generateCostMetrics(
    costHistory: Array<{
      timestamp: number;
      model: string;
      tokenCount: TokenCount;
      cost: CostBreakdown;
    }>
  ): CostMetrics {
    if (costHistory.length === 0) {
      return {
        requestCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCost: 0,
        averageCostPerRequest: 0,
        averageTokensPerRequest: 0,
      };
    }

    const requestCount = costHistory.length;
    const totalInputTokens = costHistory.reduce((sum, entry) => sum + entry.tokenCount.input, 0);
    const totalOutputTokens = costHistory.reduce((sum, entry) => sum + entry.tokenCount.output, 0);
    const totalCost = costHistory.reduce((sum, entry) => sum + entry.cost.totalCost, 0);

    return {
      requestCount,
      totalInputTokens,
      totalOutputTokens,
      totalCost: this.roundCost(totalCost),
      averageCostPerRequest: this.roundCost(totalCost / requestCount),
      averageTokensPerRequest: Math.ceil((totalInputTokens + totalOutputTokens) / requestCount),
    };
  }

  estimateCostForRequest(
    model: string,
    estimatedInputTokens: number,
    estimatedOutputTokens: number,
    currency: string = 'USD'
  ): CostBreakdown {
    const pricing = this.modelPricing.get(model);
    if (!pricing) {
      throw new Error(`No pricing information available for model: ${model}`);
    }

    const inputCost = (estimatedInputTokens / 1000000) * pricing.input;
    const outputCost = (estimatedOutputTokens / 1000000) * pricing.output;

    return {
      inputCost: this.roundCost(inputCost),
      outputCost: this.roundCost(outputCost),
      totalCost: this.roundCost(inputCost + outputCost),
      currency,
      pricing,
    };
  }

  compareModelCosts(
    inputTokens: number,
    outputTokens: number,
    models?: string[]
  ): Array<{
    model: string;
    cost: CostBreakdown;
    efficiency: number; // cost per 1K tokens
  }> {
    const modelsToCompare = models || Array.from(this.modelPricing.keys());

    return modelsToCompare
      .map((model) => {
        const cost = this.estimateCostForRequest(model, inputTokens, outputTokens);
        const totalTokens = inputTokens + outputTokens;
        const efficiency = cost.totalCost / (totalTokens / 1000); // cost per 1K tokens

        return { model, cost, efficiency };
      })
      .sort((a, b) => a.efficiency - b.efficiency); // Sort by efficiency (lowest cost first)
  }

  getOptimalModel(
    inputTokens: number,
    outputTokens: number,
    requirements: {
      maxCost?: number;
      minCapability?: string[];
      excludeModels?: string[];
    } = {}
  ): string | null {
    const comparisons = this.compareModelCosts(inputTokens, outputTokens);

    for (const { model, cost } of comparisons) {
      // Check exclusions
      if (requirements.excludeModels?.includes(model)) {
        continue;
      }

      // Check cost constraint
      if (requirements.maxCost && cost.totalCost > requirements.maxCost) {
        continue;
      }

      // Check capability requirements
      if (requirements.minCapability) {
        const modelConfig = CLAUDE_MODELS[model];
        if (!requirements.minCapability.every((cap) => modelConfig.capabilities.includes(cap))) {
          continue;
        }
      }

      return model;
    }

    return null;
  }

  private roundCost(cost: number): number {
    // Round to 6 decimal places for currency precision
    return Math.round(cost * 1000000) / 1000000;
  }

  // Static utility methods
  static formatCost(cost: number, currency: string = 'USD'): string {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 6,
    }).format(cost);
  }

  static formatTokenCount(tokens: number): string {
    if (tokens >= 1000000) {
      return `${(tokens / 1000000).toFixed(1)}M`;
    } else if (tokens >= 1000) {
      return `${(tokens / 1000).toFixed(1)}K`;
    }
    return tokens.toString();
  }
}
```

### Subtask 4.3: Create Usage Tracking

**File**: `src/providers/anthropic/usage/usage-tracker.ts`

```typescript
import { logger } from '@tamma/observability';
import { EventEmitter } from 'events';
import type { TokenCount, CostBreakdown } from '@tamma/shared/contracts';
import { CostCalculator } from '../cost-management/cost-calculator';

export interface UsageRecord {
  id: string;
  timestamp: number;
  model: string;
  tokenCount: TokenCount;
  cost: CostBreakdown;
  duration: number;
  success: boolean;
  errorType?: string;
  metadata: Record<string, unknown>;
}

export interface UsageSummary {
  period: {
    start: number;
    end: number;
  };
  totalRequests: number;
  successfulRequests: number;
  failedRequests: number;
  totalTokens: number;
  totalCost: number;
  averageCostPerRequest: number;
  averageTokensPerRequest: number;
  modelBreakdown: Record<
    string,
    {
      requests: number;
      tokens: number;
      cost: number;
    }
  >;
  errorBreakdown: Record<string, number>;
}

export interface UsageAlert {
  type: 'budget' | 'rate' | 'anomaly';
  severity: 'low' | 'medium' | 'high' | 'critical';
  message: string;
  metrics: Record<string, number>;
  timestamp: number;
}

export class UsageTracker extends EventEmitter {
  private records: UsageRecord[] = [];
  private readonly maxRecords: number;
  private readonly costCalculator: CostCalculator;
  private alerts: UsageAlert[] = [];

  constructor(options: { maxRecords?: number } = {}) {
    super();
    this.maxRecords = options.maxRecords || 10000;
    this.costCalculator = new CostCalculator();
  }

  recordUsage(usage: Omit<UsageRecord, 'id' | 'timestamp'>): string {
    const id = this.generateId();
    const timestamp = Date.now();

    const record: UsageRecord = {
      id,
      timestamp,
      ...usage,
    };

    this.records.push(record);
    this.trimRecords();

    // Emit events for monitoring
    this.emit('usageRecorded', record);

    // Check for alerts
    this.checkAlerts(record);

    logger.debug('Usage recorded', {
      id,
      model: usage.model,
      tokens: usage.tokenCount.total,
      cost: usage.cost.totalCost,
      success: usage.success,
    });

    return id;
  }

  getUsageSummary(startTime?: number, endTime?: number, model?: string): UsageSummary {
    const now = Date.now();
    const start = startTime || now - 24 * 60 * 60 * 1000; // Default to last 24 hours
    const end = endTime || now;

    const filteredRecords = this.records.filter((record) => {
      if (record.timestamp < start || record.timestamp > end) {
        return false;
      }
      if (model && record.model !== model) {
        return false;
      }
      return true;
    });

    const totalRequests = filteredRecords.length;
    const successfulRequests = filteredRecords.filter((r) => r.success).length;
    const failedRequests = totalRequests - successfulRequests;

    const totalTokens = filteredRecords.reduce((sum, record) => sum + record.tokenCount.total, 0);

    const totalCost = filteredRecords.reduce((sum, record) => sum + record.cost.totalCost, 0);

    // Model breakdown
    const modelBreakdown: Record<string, { requests: number; tokens: number; cost: number }> = {};
    for (const record of filteredRecords) {
      if (!modelBreakdown[record.model]) {
        modelBreakdown[record.model] = { requests: 0, tokens: 0, cost: 0 };
      }
      modelBreakdown[record.model].requests++;
      modelBreakdown[record.model].tokens += record.tokenCount.total;
      modelBreakdown[record.model].cost += record.cost.totalCost;
    }

    // Error breakdown
    const errorBreakdown: Record<string, number> = {};
    for (const record of filteredRecords) {
      if (!record.success && record.errorType) {
        errorBreakdown[record.errorType] = (errorBreakdown[record.errorType] || 0) + 1;
      }
    }

    return {
      period: { start, end },
      totalRequests,
      successfulRequests,
      failedRequests,
      totalTokens,
      totalCost,
      averageCostPerRequest: totalRequests > 0 ? totalCost / totalRequests : 0,
      averageTokensPerRequest: totalRequests > 0 ? totalTokens / totalRequests : 0,
      modelBreakdown,
      errorBreakdown,
    };
  }

  getUsageTrends(
    hours: number = 24,
    intervalMinutes: number = 60
  ): Array<{
    timestamp: number;
    requests: number;
    tokens: number;
    cost: number;
  }> {
    const now = Date.now();
    const startTime = now - hours * 60 * 60 * 1000;
    const intervalMs = intervalMinutes * 60 * 1000;

    const trends: Array<{
      timestamp: number;
      requests: number;
      tokens: number;
      cost: number;
    }> = [];

    for (let time = startTime; time < now; time += intervalMs) {
      const intervalEnd = time + intervalMs;
      const intervalRecords = this.records.filter(
        (record) => record.timestamp >= time && record.timestamp < intervalEnd
      );

      trends.push({
        timestamp: time,
        requests: intervalRecords.length,
        tokens: intervalRecords.reduce((sum, r) => sum + r.tokenCount.total, 0),
        cost: intervalRecords.reduce((sum, r) => sum + r.cost.totalCost, 0),
      });
    }

    return trends;
  }

  checkBudgetAlerts(budgetLimit: number, periodHours: number = 24): UsageAlert | null {
    const summary = this.getUsageSummary(Date.now() - periodHours * 60 * 60 * 1000);

    const usagePercentage = (summary.totalCost / budgetLimit) * 100;

    if (usagePercentage >= 100) {
      return {
        type: 'budget',
        severity: 'critical',
        message: `Budget exceeded: ${CostCalculator.formatCost(summary.totalCost)} of ${CostCalculator.formatCost(budgetLimit)}`,
        metrics: {
          usagePercentage,
          budgetLimit,
          actualUsage: summary.totalCost,
        },
        timestamp: Date.now(),
      };
    } else if (usagePercentage >= 90) {
      return {
        type: 'budget',
        severity: 'high',
        message: `Budget nearly exceeded: ${CostCalculator.formatCost(summary.totalCost)} of ${CostCalculator.formatCost(budgetLimit)}`,
        metrics: {
          usagePercentage,
          budgetLimit,
          actualUsage: summary.totalCost,
        },
        timestamp: Date.now(),
      };
    } else if (usagePercentage >= 75) {
      return {
        type: 'budget',
        severity: 'medium',
        message: `Budget usage warning: ${CostCalculator.formatCost(summary.totalCost)} of ${CostCalculator.formatCost(budgetLimit)}`,
        metrics: {
          usagePercentage,
          budgetLimit,
          actualUsage: summary.totalCost,
        },
        timestamp: Date.now(),
      };
    }

    return null;
  }

  detectAnomalies(lookbackHours: number = 24, thresholdMultiplier: number = 2): UsageAlert[] {
    const trends = this.getUsageTrends(lookbackHours, 60);
    const alerts: UsageAlert[] = [];

    if (trends.length < 2) {
      return alerts;
    }

    // Calculate averages from earlier periods
    const recentTrends = trends.slice(-4); // Last 4 intervals
    const historicalTrends = trends.slice(0, -4);

    if (historicalTrends.length === 0) {
      return alerts;
    }

    const avgRecentCost = recentTrends.reduce((sum, t) => sum + t.cost, 0) / recentTrends.length;
    const avgHistoricalCost =
      historicalTrends.reduce((sum, t) => sum + t.cost, 0) / historicalTrends.length;

    // Detect cost anomalies
    if (avgRecentCost > avgHistoricalCost * thresholdMultiplier) {
      alerts.push({
        type: 'anomaly',
        severity: 'high',
        message: `Cost anomaly detected: Recent cost ${CostCalculator.formatCost(avgRecentCost)} is ${thresholdMultiplier}x historical average`,
        metrics: {
          recentCost: avgRecentCost,
          historicalCost: avgHistoricalCost,
          multiplier: avgRecentCost / avgHistoricalCost,
        },
        timestamp: Date.now(),
      });
    }

    return alerts;
  }

  getAlerts(severity?: 'low' | 'medium' | 'high' | 'critical'): UsageAlert[] {
    if (severity) {
      return this.alerts.filter((alert) => alert.severity === severity);
    }
    return [...this.alerts];
  }

  clearAlerts(): void {
    this.alerts = [];
  }

  exportUsageData(format: 'json' | 'csv' = 'json', startTime?: number, endTime?: number): string {
    const summary = this.getUsageSummary(startTime, endTime);

    if (format === 'csv') {
      return this.exportToCSV(summary);
    }

    return JSON.stringify(summary, null, 2);
  }

  private checkAlerts(record: UsageRecord): void {
    // Check budget alerts (would need budget configuration)
    // This is a placeholder for budget checking logic

    // Detect anomalies
    const anomalies = this.detectAnomalies();
    this.alerts.push(...anomalies);

    // Keep only recent alerts
    const oneWeekAgo = Date.now() - 7 * 24 * 60 * 60 * 1000;
    this.alerts = this.alerts.filter((alert) => alert.timestamp > oneWeekAgo);
  }

  private trimRecords(): void {
    if (this.records.length > this.maxRecords) {
      const excess = this.records.length - this.maxRecords;
      this.records.splice(0, excess);
    }
  }

  private generateId(): string {
    return `usage_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private exportToCSV(summary: UsageSummary): string {
    const headers = [
      'Period Start',
      'Period End',
      'Total Requests',
      'Successful Requests',
      'Failed Requests',
      'Total Tokens',
      'Total Cost',
      'Average Cost per Request',
      'Average Tokens per Request',
    ];

    const row = [
      new Date(summary.period.start).toISOString(),
      new Date(summary.period.end).toISOString(),
      summary.totalRequests.toString(),
      summary.successfulRequests.toString(),
      summary.failedRequests.toString(),
      summary.totalTokens.toString(),
      summary.totalCost.toString(),
      summary.averageCostPerRequest.toString(),
      summary.averageTokensPerRequest.toString(),
    ];

    return [headers.join(','), row.join(',')].join('\n');
  }
}
```

### Subtask 4.4: Add Cost Optimization Recommendations

**File**: `src/providers/anthropic/optimization/cost-optimizer.ts`

```typescript
import { logger } from '@tamma/observability';
import type { MessageRequest, TokenCount } from '@tamma/shared/contracts';
import { TokenCounter } from '../token-management/token-counter';
import { CostCalculator } from '../cost-management/cost-calculator';
import { CLAUDE_MODELS } from '../models/claude-models';

export interface OptimizationRecommendation {
  type: 'model' | 'prompt' | 'parameters' | 'batching';
  priority: 'low' | 'medium' | 'high';
  description: string;
  estimatedSavings: number; // in USD
  implementation: string;
  impact: string;
}

export interface CostAnalysis {
  currentCost: number;
  optimizedCost: number;
  potentialSavings: number;
  savingsPercentage: number;
  recommendations: OptimizationRecommendation[];
}

export interface PromptOptimization {
  originalTokens: number;
  optimizedTokens: number;
  tokenReduction: number;
  reductionPercentage: number;
  optimizations: string[];
}

export class CostOptimizer {
  private readonly tokenCounter: TokenCounter;
  private readonly costCalculator: CostCalculator;

  constructor() {
    this.tokenCounter = new TokenCounter();
    this.costCalculator = new CostCalculator();
  }

  async analyzeRequestCost(
    request: MessageRequest
  ): Promise<CostAnalysis> {
    const currentModel = request.model;
    const inputTokens = await this.tokenCounter.countInputTokens(request);

    // Estimate output tokens (roughly 25% of input for typical responses)
    const estimatedOutputTokens = Math.ceil(inputTokens * 0.25);

    const currentCost = this.costCalculator.estimateCostForRequest(
      currentModel,
      inputTokens,
      estimatedOutputTokens
    );

    const recommendations = await this.generateRecommendations(request, inputTokens);
    const optimizedCost = this.calculateOptimizedCost(request, inputTokens, recommendations);

    const potentialSavings = currentCost.totalCost - optimizedCost.totalCost;
    const savingsPercentage = currentCost.totalCost > 0
      ? (potentialSavings / currentCost.totalCost) * 100
      : 0;

    return {
      currentCost: currentCost.totalCost,
      optimizedCost: optimizedCost.totalCost,
      potentialSavings,
      savingsPercentage,
      recommendations
    };
  }

  async optimizePrompt(
    prompt: string,
    model: string
  ): Promise<PromptOptimization> {
    const originalTokens = await this.tokenCounter.countTextTokens(prompt, {
      model,
      includeSystemPrompt: false,
      includeTools: false
    });

    const optimizations: string[] = [];
    let optimizedPrompt = prompt;

    // Remove redundant whitespace
    if (/\s{2,}/.test(optimizedPrompt)) {
      optimizedPrompt = optimizedPrompt.replace(/\s{2,}/g, ' ');
      optimizations.push('Removed redundant whitespace');
    }

    // Remove unnecessary line breaks
    const lineBreakCount = (optimizedPrompt.match(/\n/g) || []).length;
    if (lineBreakCount > 10) {
      optimizedPrompt = optimizedPrompt.replace(/\n{3,}/g, '\n\n');
      optimizations.push('Consolidated excessive line breaks');
    }

    // Remove redundant phrases
    const redundantPhrases = [
      'Please make sure to',
      'It is important that',
      'I would like you to',
      'Could you please'
    ];

    for (const phrase of redundantPhrases) {
      if (optimizedPrompt.includes(phrase)) {
        optimizedPrompt = optimizedPrompt.replace(new RegExp(phrase, 'g'), '');
        optimizations.push(`Removed redundant phrase: "${phrase}"`);
      }
    }

    // Simplify complex sentences
    const sentences = optimizedPrompt.split('.').filter(s => s.trim());
    const longSentences = sentences.filter(s => s.length > 200);

    if (longSentences.length > 0) {
      optimizations.push(`Found ${longSentences.length} overly long sentences that could be simplified`);
    }

    const optimizedTokens = await this.tokenCounter.countTextTokens(optimizedPrompt, {
      model,
      includeSystemPrompt: false,
      includeTools: false
    });

    const tokenReduction = originalTokens - optimizedTokens;
    const reductionPercentage = originalTokens > 0
      ? (tokenReduction / originalTokens) * 100
      : 0;

    return {
      originalTokens,
      optimizedTokens,
      tokenReduction,
      reductionPercentage,
      optimizations
    };
  }

  suggestOptimalModel(
    request: MessageRequest,
    requirements: {
      maxCost?: number;
      minCapability?: string[];
      priority: 'cost' | 'quality' | 'speed';
    } = { priority: 'cost' }
  ): string | null {
    const inputTokens = this.estimateInputTokens(request);
    const estimatedOutputTokens = Math.ceil(inputTokens * 0.25);

    // Get all available models
    const availableModels = Object.keys(CLAUDE_MODELS);

    // Filter models based on capability requirements
    let candidateModels = availableModels.filter(model => {
      if (!requirements.minCapability) return true;

      const modelConfig = CLAUDE_MODELS[model];
      return requirements.minCapability.every(cap =>
        modelConfig.capabilities.includes(cap)
      );
    });

    // Filter by cost constraint
    if (requirements.maxCost) {
      candidateModels = candidateModels.filter(model => {
        const cost = this.costCalculator.estimateCostForRequest(
          model,
          inputTokens,
          estimatedOutputTokens
        );
        return cost.totalCost <= requirements.maxCost!;
      });
    }

    if (candidateModels.length === 0) {
      return null;
    }

    // Sort based on priority
    candidateModels.sort((a, b) => {
      const costA = this.costCalculator.estimateCostForRequest(a, inputTokens, estimatedOutputTokens);
      const costB = this.costCalculator.estimateCostForRequest(b, inputTokens, estimatedOutputTokens);

      switch (requirements.priority) {
        case 'cost':
          return costA.totalCost - costB.totalCost;

        case 'quality':
          // Prefer more capable models (Opus > Sonnet > Haiku)
          const qualityOrder = ['claude-3-opus-20240229', 'claude-3-5-sonnet-20241022', 'claude-3-5-haiku-20241022'];
          return qualityOrder.indexOf(a) - qualityOrder.indexOf(b);

        case 'speed':
          // Prefer faster models (Haiku > Sonnet > Opus)
          const speedOrder = ['claude-3-5-haiku-20241022', 'claude-3-5-sonnet-20241022', 'claude-3-opus-20240229'];
          return speedOrder.indexOf(a) - speedOrder.indexOf(b);

        default:
          return costA.totalCost - costB.totalCost;
      }
    });

    return candidateModels[0];
  }

  private async generateRecommendations(
    request: MessageRequest,
    inputTokens: number
  ): Promise<OptimizationRecommendation[]> {
    const recommendations: OptimizationRecommendation[] = [];

    // Model optimization
    const modelRecommendation = this.generateModelRecommendation(request, inputTokens);
    if (modelRecommendation) {
      recommendations.push(modelRecommendation);
    }

    // Prompt optimization
    const promptRecommendation = await this.generatePromptRecommendation(request);
    if (promptRecommendation) {
      recommendations.push(promptRecommendation);
    }

    // Parameter optimization
    const parameterRecommendation = this.generateParameterRecommendation(request);
    if (parameterRecommendation) {
      recommendations.push(parameterRecommendation);
    }

    // Batching recommendation
    const batchingRecommendation = this.generateBatchingRecommendation(request);
    if (batchingRecommendation) {
      recommendations.push(batchingRecommendation);
    }

    return recommendations.sort((a, b) => {
      const priorityOrder = { high: 3, medium: 2, low: 1 };
      return priorityOrder[b.priority] - priorityOrder[a.priority];
    });
  }

  private generateModelRecommendation(
    request: MessageRequest,
    inputTokens: number
  ): OptimizationRecommendation | null {
    const currentModel = request.model;
    const currentCost = this.costCalculator.estimateCostForRequest(
      currentModel,
      inputTokens,
      Math.ceil(inputTokens * 0.25)
    );

    // Check if a cheaper model could handle this request
    const cheaperModels = ['claude-3-5-haiku-20241022'];

    for (const model of cheaperModels) {
      if (model === currentModel) continue;

      const cheaperCost = this.costCalculator.estimateCostForRequest(
        model,
        inputTokens,
        Math.ceil(inputTokens * 0.25)
      );

      const savings = currentCost.totalCost - cheaperCost.totalCost;

      if (savings > 0.001) { // Only recommend if savings are significant
        return {
          type: 'model',
          priority: savings > 0.01 ? 'high' : 'medium',
          description: `Switch from ${currentModel} to ${model} for estimated savings of ${CostCalculator.formatCost(savings)}`,
          estimatedSavings: savings,
          implementation: `Change model parameter from "${currentModel}" to "${model}"`,
          impact: `Reduces cost by ${((savings / currentCost.totalCost) * 100).toFixed(1)}%`
        };
      }
    }

    return null;
  }

  private async generatePromptRecommendation(
    request: MessageRequest
  ): Promise<OptimizationRecommendation | null {
    const systemPrompt = request.system || '';
    const messagesText = JSON.stringify(request.messages);
    const fullPrompt = systemPrompt + messagesText;

    const optimization = await this.optimizePrompt(fullPrompt, request.model);

    if (optimization.reductionPercentage > 5) { // Only recommend if reduction is significant
      const estimatedSavings = this.calculateSavingsFromTokenReduction(
        optimization.tokenReduction,
        request.model
      );

      return {
        type: 'prompt',
        priority: optimization.reductionPercentage > 15 ? 'high' : 'medium',
        description: `Optimize prompt to reduce ${optimization.tokenReduction} tokens (${optimization.reductionPercentage.toFixed(1)}% reduction)`,
        estimatedSavings,
        implementation: 'Apply prompt optimization techniques: remove redundancy, simplify language, consolidate whitespace',
        impact: `Reduces input tokens by ${optimization.tokenReduction}, saving ${CostCalculator.formatCost(estimatedSavings)} per request`
      };
    }

    return null;
  }

  private generateParameterRecommendation(
    request: MessageRequest
  ): OptimizationRecommendation | null {
    const recommendations: string[] = [];
    let estimatedSavings = 0;

    // Check maxTokens setting
    if (request.maxTokens && request.maxTokens > 4000) {
      recommendations.push('Reduce maxTokens from current setting to 4000 or less for typical requests');
      // Rough savings calculation
      estimatedSavings += 0.002; // Estimated savings per request
    }

    // Check temperature setting
    if (request.temperature !== undefined && request.temperature > 0.7) {
      recommendations.push('Consider reducing temperature to 0.7 or lower for more focused responses');
    }

    if (recommendations.length > 0) {
      return {
        type: 'parameters',
        priority: 'low',
        description: 'Optimize request parameters for efficiency',
        estimatedSavings,
        implementation: recommendations.join('. '),
        impact: 'Minor cost savings and potentially faster responses'
      };
    }

    return null;
  }

  private generateBatchingRecommendation(
    request: MessageRequest
  ): OptimizationRecommendation | null {
    // This would be relevant if we detect multiple similar requests
    // For now, return null as this is more of a system-level optimization
    return null;
  }

  private calculateOptimizedCost(
    request: MessageRequest,
    inputTokens: number,
    recommendations: OptimizationRecommendation[]
  ): { totalCost: number } {
    let optimizedModel = request.model;
    let optimizedInputTokens = inputTokens;

    // Apply model optimization
    const modelRec = recommendations.find(r => r.type === 'model');
    if (modelRec) {
      // Extract model name from implementation text
      const match = modelRec.implementation.match(/to "([^"]+)"/);
      if (match) {
        optimizedModel = match[1];
      }
    }

    // Apply prompt optimization
    const promptRec = recommendations.find(r => r.type === 'prompt');
    if (promptRec) {
      // Extract token reduction from description
      const match = promptRec.description.match(/reduce (\d+) tokens/);
      if (match) {
        optimizedInputTokens -= parseInt(match[1], 10);
      }
    }

    return this.costCalculator.estimateCostForRequest(
      optimizedModel,
      optimizedInputTokens,
      Math.ceil(optimizedInputTokens * 0.25)
    );
  }

  private estimateInputTokens(request: MessageRequest): number {
    // Rough estimation for quick calculations
    const text = JSON.stringify(request.messages) +
                (request.system || '') +
                (JSON.stringify(request.tools || []));
    return Math.ceil(text.length / 4);
  }

  private calculateSavingsFromTokenReduction(
    tokenReduction: number,
    model: string
  ): number {
    const pricing = CLAUDE_MODELS[model]?.pricing;
    if (!pricing) return 0;

    return (tokenReduction / 1000000) * pricing.input;
  }
}
```

## Integration with Main Provider

**Update to**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
// Add these imports and update the provider
import { TokenCounter } from './token-management/token-counter';
import { CostCalculator } from './cost-management/cost-calculator';
import { UsageTracker } from './usage/usage-tracker';
import { CostOptimizer } from './optimization/cost-optimizer';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly tokenCounter: TokenCounter;
  private readonly costCalculator: CostCalculator;
  private readonly usageTracker: UsageTracker;
  private readonly costOptimizer: CostOptimizer;

  constructor(config: AnthropicProviderConfig) {
    // ... existing constructor code ...

    // Initialize cost management components
    this.tokenCounter = new TokenCounter();
    this.costCalculator = new CostCalculator();
    this.usageTracker = new UsageTracker();
    this.costOptimizer = new CostOptimizer();
  }

  async sendMessage(
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    const startTime = Date.now();
    let inputTokens = 0;
    let outputTokens = 0;
    let success = false;
    let errorType: string | undefined;

    try {
      // Count input tokens
      inputTokens = await this.tokenCounter.countInputTokens(request);

      // Create stream with cost tracking
      const baseStream = await this.messageStream.createStream(this.client, request, options);

      // Wrap stream to track output tokens
      const trackedStream = this.createTrackedStream(baseStream, request);

      // Return the tracked stream
      return trackedStream;
    } catch (error) {
      errorType = error.constructor.name;
      throw error;
    } finally {
      // Record usage (this would be called after stream completion)
      // In a real implementation, you'd track this when the stream completes
      const duration = Date.now() - startTime;

      // This is a simplified example - real implementation would track
      // when the stream actually completes
      this.usageTracker.recordUsage({
        model: request.model,
        tokenCount: { input: inputTokens, output: outputTokens, total: inputTokens + outputTokens },
        cost: this.costCalculator.calculateCost(request.model, {
          input: inputTokens,
          output: outputTokens,
          total: inputTokens + outputTokens,
        }),
        duration,
        success,
        errorType,
        metadata: { requestId: options.requestId },
      });
    }
  }

  private createTrackedStream(
    baseStream: AsyncIterable<MessageChunk>,
    request: MessageRequest
  ): AsyncIterable<MessageChunk> {
    const chunks: MessageChunk[] = [];

    return {
      [Symbol.asyncIterator]() {
        const iterator = baseStream[Symbol.asyncIterator]();

        return {
          async next(): Promise<IteratorResult<MessageChunk>> {
            const result = await iterator.next();

            if (result.value) {
              chunks.push(result.value);
            }

            return result;
          },
        };
      },
    };
  }

  // Public methods for cost management
  async getCostAnalysis(request: MessageRequest): Promise<any> {
    return this.costOptimizer.analyzeRequestCost(request);
  }

  getUsageSummary(startTime?: number, endTime?: number): any {
    return this.usageTracker.getUsageSummary(startTime, endTime);
  }

  getOptimizationRecommendations(request: MessageRequest): Promise<any> {
    return this.costOptimizer.analyzeRequestCost(request);
  }
}
```

## Dependencies

### Internal Dependencies

- `@tamma/shared/contracts` - TokenCount, PricingInfo interfaces
- `@tamma/observability` - Logging utilities
- Story 2.1 cost management interfaces

### External Dependencies

- Optional: `tiktoken` for accurate token counting (if available)

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/token-management/token-counter.test.ts
describe('TokenCounter', () => {
  describe('countInputTokens', () => {
    it('should count tokens for simple messages');
    it('should count tokens for complex messages with tools');
    it('should handle system prompts correctly');
    it('should estimate tokens when tiktoken unavailable');
  });
});

// tests/providers/anthropic/cost-management/cost-calculator.test.ts
describe('CostCalculator', () => {
  describe('calculateCost', () => {
    it('should calculate cost correctly for each model');
    it('should handle batch cost calculations');
    it('should compare model costs accurately');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/cost-management/integration.test.ts
describe('Cost Management Integration', () => {
  it('should track real API usage costs');
  it('should provide accurate cost breakdowns');
  it('should generate optimization recommendations');
});
```

## Risk Mitigation

### Technical Risks

1. **Inaccurate Token Counting**: Token estimation being off
   - Mitigation: Use tiktoken when available, provide fallback estimation
2. **Pricing Changes**: Model pricing changing over time
   - Mitigation: External configuration, regular updates
3. **Cost Overruns**: Unexpected high costs
   - Mitigation: Real-time tracking, budget alerts, usage limits

### Operational Risks

1. **Memory Usage**: Usage tracking consuming too much memory
   - Mitigation: Configurable limits, automatic cleanup
2. **Performance Overhead**: Cost calculation slowing requests
   - Mitigation: Efficient algorithms, caching, async operations
3. **Data Privacy**: Usage data containing sensitive information
   - Mitigation: Data anonymization, secure storage

## Deliverables

1. **Token Counter**: Accurate token counting with fallback
2. **Cost Calculator**: Precise cost calculation and comparison
3. **Usage Tracker**: Comprehensive usage monitoring and alerting
4. **Cost Optimizer**: Intelligent optimization recommendations
5. **Integration**: Updated provider with cost management
6. **Unit Tests**: Full test coverage for all components
7. **Integration Tests**: Real API cost tracking validation
8. **Documentation**: Cost management and optimization guide

## Success Criteria

- [ ] Accurate token counting within 5% margin
- [ ] Precise cost calculation for all models
- [ ] Real-time usage tracking and alerting
- [ ] Actionable optimization recommendations
- [ ] Budget monitoring and enforcement
- [ ] Performance impact under 5% overhead
- [ ] Comprehensive test coverage
- [ ] Export capabilities for usage data

## File Structure

```
src/providers/anthropic/token-management/
├── token-counter.ts           # Token counting logic
└── index.ts                   # Public exports

src/providers/anthropic/cost-management/
├── cost-calculator.ts         # Cost calculation
└── index.ts                   # Public exports

src/providers/anthropic/usage/
├── usage-tracker.ts          # Usage tracking and alerting
└── index.ts                   # Public exports

src/providers/anthropic/optimization/
├── cost-optimizer.ts         # Optimization recommendations
└── index.ts                   # Public exports

tests/providers/anthropic/token-management/
├── token-counter.test.ts
└── integration.test.ts

tests/providers/anthropic/cost-management/
├── cost-calculator.test.ts
└── integration.test.ts
```

This task provides comprehensive token and cost management capabilities that enable users to monitor, control, and optimize their AI usage while maintaining transparency and providing actionable insights for cost efficiency.
