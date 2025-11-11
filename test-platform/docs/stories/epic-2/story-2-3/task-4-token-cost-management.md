# Task 4: Token and Cost Management

## Overview

Implement comprehensive token counting and cost tracking for OpenAI API usage to provide accurate usage metrics and cost control for the Tamma platform.

## Objectives

- Implement accurate token counting for OpenAI models
- Add cost calculation based on current OpenAI pricing
- Provide usage tracking and budget management features
- Support different pricing tiers for various models

## Implementation Steps

### Subtask 4.1: Implement Token Counting Logic

**Description**: Create a robust token counting system that accurately calculates input and output tokens for OpenAI API requests and responses.

**Implementation Details**:

1. **Create Token Counter Interface**:

```typescript
// packages/providers/src/interfaces/token-counter.interface.ts
export interface ITokenCounter {
  countTokens(text: string, model: string): Promise<number>;
  countMessagesTokens(messages: ChatMessage[], model: string): Promise<number>;
  estimateResponseTokens(promptTokens: number, model: string): number;
  getModelContextInfo(model: string): ModelContextInfo;
}

export interface ModelContextInfo {
  maxTokens: number;
  inputCostPer1K: number;
  outputCostPer1K: number;
  supportsVision: boolean;
  supportsFunctions: boolean;
}
```

2. **Implement OpenAI Token Counter**:

```typescript
// packages/providers/src/openai/openai-token-counter.ts
import { ITokenCounter, ModelContextInfo } from '../interfaces/token-counter.interface';
import { ChatMessage } from '@tamma/shared/types';

export class OpenAITokenCounter implements ITokenCounter {
  private readonly MODEL_CONTEXT_INFO: Record<string, ModelContextInfo> = {
    'gpt-4': {
      maxTokens: 8192,
      inputCostPer1K: 0.03,
      outputCostPer1K: 0.06,
      supportsVision: false,
      supportsFunctions: true,
    },
    'gpt-4-32k': {
      maxTokens: 32768,
      inputCostPer1K: 0.06,
      outputCostPer1K: 0.12,
      supportsVision: false,
      supportsFunctions: true,
    },
    'gpt-4-turbo': {
      maxTokens: 128000,
      inputCostPer1K: 0.01,
      outputCostPer1K: 0.03,
      supportsVision: true,
      supportsFunctions: true,
    },
    'gpt-4-turbo-preview': {
      maxTokens: 128000,
      inputCostPer1K: 0.01,
      outputCostPer1K: 0.03,
      supportsVision: false,
      supportsFunctions: true,
    },
    'gpt-3.5-turbo': {
      maxTokens: 4096,
      inputCostPer1K: 0.0015,
      outputCostPer1K: 0.002,
      supportsVision: false,
      supportsFunctions: true,
    },
    'gpt-3.5-turbo-16k': {
      maxTokens: 16384,
      inputCostPer1K: 0.003,
      outputCostPer1K: 0.004,
      supportsVision: false,
      supportsFunctions: true,
    },
  };

  async countTokens(text: string, model: string): Promise<number> {
    // For OpenAI, we can use tiktoken or approximate
    // For now, use a simple approximation (roughly 4 chars per token)
    // In production, integrate tiktoken for accuracy
    return Math.ceil(text.length / 4);
  }

  async countMessagesTokens(messages: ChatMessage[], model: string): Promise<number> {
    let totalTokens = 0;

    for (const message of messages) {
      // Count role tokens
      totalTokens += await this.countTokens(message.role, model);

      // Count content tokens
      if (typeof message.content === 'string') {
        totalTokens += await this.countTokens(message.content, model);
      } else if (Array.isArray(message.content)) {
        for (const content of message.content) {
          if (content.type === 'text') {
            totalTokens += await this.countTokens(content.text, model);
          } else if (content.type === 'image_url') {
            // Image tokens are calculated differently
            totalTokens += this.calculateImageTokens(content.image_url, model);
          }
        }
      }

      // Add formatting tokens (approximately 3 per message)
      totalTokens += 3;
    }

    return totalTokens;
  }

  estimateResponseTokens(promptTokens: number, model: string): number {
    // Rough estimation based on typical response patterns
    const modelInfo = this.getModelContextInfo(model);
    const maxResponseTokens = Math.floor(modelInfo.maxTokens * 0.75); // Leave room for prompt
    return Math.min(maxResponseTokens, Math.max(100, Math.floor(promptTokens * 0.8)));
  }

  getModelContextInfo(model: string): ModelContextInfo {
    // Handle model name variations
    const normalizedModel = this.normalizeModelName(model);
    const info = this.MODEL_CONTEXT_INFO[normalizedModel];

    if (!info) {
      throw new Error(
        `Unknown model: ${model}. Available models: ${Object.keys(this.MODEL_CONTEXT_INFO).join(', ')}`
      );
    }

    return info;
  }

  private normalizeModelName(model: string): string {
    // Map various model names to standard ones
    const modelMappings: Record<string, string> = {
      'gpt-4-0613': 'gpt-4',
      'gpt-4-32k-0613': 'gpt-4-32k',
      'gpt-4-1106-preview': 'gpt-4-turbo-preview',
      'gpt-4-0125-preview': 'gpt-4-turbo-preview',
      'gpt-3.5-turbo-0613': 'gpt-3.5-turbo',
      'gpt-3.5-turbo-16k-0613': 'gpt-3.5-turbo-16k',
    };

    return modelMappings[model] || model;
  }

  private calculateImageTokens(
    imageUrl: { url: string; detail?: 'low' | 'high' },
    model: string
  ): number {
    // OpenAI vision pricing
    const detail = imageUrl.detail || 'auto';

    if (detail === 'low') {
      return 85; // Fixed cost for low detail
    }

    // High detail: 85 tokens base + additional based on image size
    // For estimation, we'll use a typical value
    return 170; // Approximate for high detail images
  }
}
```

3. **Add Tiktoken Integration (Optional but Recommended)**:

```typescript
// packages/providers/src/openai/tiktoken-wrapper.ts
import tiktoken from 'tiktoken';

export class TiktokenWrapper {
  private encoders: Map<string, any> = new Map();

  async getEncoder(model: string): Promise<any> {
    if (this.encoders.has(model)) {
      return this.encoders.get(model);
    }

    let encodingName: string;
    if (model.startsWith('gpt-4')) {
      encodingName = 'cl100k_base';
    } else if (model.startsWith('gpt-3.5-turbo')) {
      encodingName = 'cl100k_base';
    } else {
      encodingName = 'cl100k_base'; // Default for most models
    }

    const encoder = tiktoken.encoding_for_model(encodingName);
    this.encoders.set(model, encoder);
    return encoder;
  }

  async countTokens(text: string, model: string): Promise<number> {
    const encoder = await this.getEncoder(model);
    const tokens = encoder.encode(text);
    return tokens.length;
  }

  cleanup(): void {
    for (const encoder of this.encoders.values()) {
      encoder.free();
    }
    this.encoders.clear();
  }
}
```

### Subtask 4.2: Implement Cost Calculation

**Description**: Create a cost calculation system that tracks API usage costs in real-time and provides detailed cost breakdowns.

**Implementation Details**:

1. **Create Cost Calculator**:

```typescript
// packages/providers/src/interfaces/cost-calculator.interface.ts
export interface ICostCalculator {
  calculateCost(tokens: number, model: string, type: 'input' | 'output'): number;
  calculateRequestCost(inputTokens: number, outputTokens: number, model: string): RequestCost;
  getModelPricing(model: string): ModelPricing;
  updatePricing(model: string, pricing: Partial<ModelPricing>): void;
}

export interface RequestCost {
  inputCost: number;
  outputCost: number;
  totalCost: number;
  inputTokens: number;
  outputTokens: number;
  model: string;
  currency: string;
}

export interface ModelPricing {
  inputCostPer1K: number;
  outputCostPer1K: number;
  currency: string;
  lastUpdated: string;
}
```

2. **Implement OpenAI Cost Calculator**:

```typescript
// packages/providers/src/openai/openai-cost-calculator.ts
import {
  ICostCalculator,
  RequestCost,
  ModelPricing,
} from '../interfaces/cost-calculator.interface';
import { OpenAITokenCounter } from './openai-token-counter';

export class OpenAICostCalculator implements ICostCalculator {
  private readonly pricing: Map<string, ModelPricing> = new Map();
  private readonly tokenCounter: OpenAITokenCounter;

  constructor() {
    this.tokenCounter = new OpenAITokenCounter();
    this.initializePricing();
  }

  calculateCost(tokens: number, model: string, type: 'input' | 'output'): number {
    const pricing = this.getModelPricing(model);
    const costPerToken =
      type === 'input' ? pricing.inputCostPer1K / 1000 : pricing.outputCostPer1K / 1000;

    return tokens * costPerToken;
  }

  calculateRequestCost(inputTokens: number, outputTokens: number, model: string): RequestCost {
    const inputCost = this.calculateCost(inputTokens, model, 'input');
    const outputCost = this.calculateCost(outputTokens, model, 'output');

    return {
      inputCost,
      outputCost,
      totalCost: inputCost + outputCost,
      inputTokens,
      outputTokens,
      model,
      currency: 'USD',
    };
  }

  getModelPricing(model: string): ModelPricing {
    const normalizedModel = this.normalizeModelName(model);
    const pricing = this.pricing.get(normalizedModel);

    if (!pricing) {
      throw new Error(`No pricing information available for model: ${model}`);
    }

    return pricing;
  }

  updatePricing(model: string, pricingUpdate: Partial<ModelPricing>): void {
    const currentPricing = this.getModelPricing(model);
    const updatedPricing: ModelPricing = {
      ...currentPricing,
      ...pricingUpdate,
      lastUpdated: new Date().toISOString(),
    };

    this.pricing.set(this.normalizeModelName(model), updatedPricing);
  }

  private initializePricing(): void {
    // Current OpenAI pricing as of 2024
    const defaultPricing: Record<string, Omit<ModelPricing, 'lastUpdated'>> = {
      'gpt-4': {
        inputCostPer1K: 0.03,
        outputCostPer1K: 0.06,
        currency: 'USD',
      },
      'gpt-4-32k': {
        inputCostPer1K: 0.06,
        outputCostPer1K: 0.12,
        currency: 'USD',
      },
      'gpt-4-turbo': {
        inputCostPer1K: 0.01,
        outputCostPer1K: 0.03,
        currency: 'USD',
      },
      'gpt-4-turbo-preview': {
        inputCostPer1K: 0.01,
        outputCostPer1K: 0.03,
        currency: 'USD',
      },
      'gpt-3.5-turbo': {
        inputCostPer1K: 0.0015,
        outputCostPer1K: 0.002,
        currency: 'USD',
      },
      'gpt-3.5-turbo-16k': {
        inputCostPer1K: 0.003,
        outputCostPer1K: 0.004,
        currency: 'USD',
      },
    };

    for (const [model, pricing] of Object.entries(defaultPricing)) {
      this.pricing.set(model, {
        ...pricing,
        lastUpdated: new Date().toISOString(),
      });
    }
  }

  private normalizeModelName(model: string): string {
    const modelMappings: Record<string, string> = {
      'gpt-4-0613': 'gpt-4',
      'gpt-4-32k-0613': 'gpt-4-32k',
      'gpt-4-1106-preview': 'gpt-4-turbo-preview',
      'gpt-4-0125-preview': 'gpt-4-turbo-preview',
      'gpt-3.5-turbo-0613': 'gpt-3.5-turbo',
      'gpt-3.5-turbo-16k-0613': 'gpt-3.5-turbo-16k',
    };

    return modelMappings[model] || model;
  }
}
```

### Subtask 4.3: Add Usage Tracking and Budget Management

**Description**: Implement usage tracking with budget limits, alerts, and detailed usage analytics for cost control.

**Implementation Details**:

1. **Create Usage Tracker**:

```typescript
// packages/providers/src/interfaces/usage-tracker.interface.ts
export interface IUsageTracker {
  trackRequest(request: UsageRequest): Promise<UsageRecord>;
  getUsage(filter: UsageFilter): Promise<UsageSummary>;
  checkBudgetLimit(userId: string, organizationId?: string): Promise<BudgetStatus>;
  setBudgetLimit(userId: string, limit: BudgetLimit): Promise<void>;
  getUsageHistory(timeRange: TimeRange): Promise<UsageRecord[]>;
}

export interface UsageRequest {
  userId: string;
  organizationId?: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  requestCost: number;
  metadata?: Record<string, any>;
}

export interface UsageRecord {
  id: string;
  userId: string;
  organizationId?: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  requestCost: number;
  timestamp: string;
  metadata?: Record<string, any>;
}

export interface UsageSummary {
  totalRequests: number;
  totalTokens: number;
  totalCost: number;
  costByModel: Record<string, number>;
  tokensByModel: Record<string, number>;
  requestsByModel: Record<string, number>;
  timeRange: TimeRange;
}

export interface BudgetLimit {
  dailyLimit?: number;
  monthlyLimit?: number;
  alertThresholds?: number[]; // Percentages (e.g., [50, 75, 90])
}

export interface BudgetStatus {
  dailyUsed: number;
  dailyLimit?: number;
  monthlyUsed: number;
  monthlyLimit?: number;
  dailyPercentage: number;
  monthlyPercentage: number;
  alerts: BudgetAlert[];
  withinLimit: boolean;
}

export interface BudgetAlert {
  type: 'daily' | 'monthly';
  level: 'warning' | 'critical';
  percentage: number;
  message: string;
}

export interface UsageFilter {
  userId?: string;
  organizationId?: string;
  model?: string;
  timeRange: TimeRange;
}

export interface TimeRange {
  start: string;
  end: string;
}
```

2. **Implement Usage Tracker**:

```typescript
// packages/providers/src/openai/openai-usage-tracker.ts
import {
  IUsageTracker,
  UsageRequest,
  UsageRecord,
  UsageSummary,
  BudgetStatus,
  BudgetLimit,
  UsageFilter,
  TimeRange,
  BudgetAlert,
} from '../interfaces/usage-tracker.interface';
import { v4 as uuidv4 } from 'uuid';
import { EventEmitter } from 'events';

export class OpenAIUsageTracker extends EventEmitter implements IUsageTracker {
  private usageRecords: Map<string, UsageRecord[]> = new Map();
  private budgetLimits: Map<string, BudgetLimit> = new Map();

  async trackRequest(request: UsageRequest): Promise<UsageRecord> {
    const record: UsageRecord = {
      id: uuidv4(),
      userId: request.userId,
      organizationId: request.organizationId,
      model: request.model,
      inputTokens: request.inputTokens,
      outputTokens: request.outputTokens,
      totalTokens: request.inputTokens + request.outputTokens,
      requestCost: request.requestCost,
      timestamp: new Date().toISOString(),
      metadata: request.metadata,
    };

    // Store record
    const userRecords = this.usageRecords.get(request.userId) || [];
    userRecords.push(record);
    this.usageRecords.set(request.userId, userRecords);

    // Check budget limits
    const budgetStatus = await this.checkBudgetLimit(request.userId, request.organizationId);

    // Emit alerts if needed
    for (const alert of budgetStatus.alerts) {
      this.emit('budgetAlert', {
        userId: request.userId,
        organizationId: request.organizationId,
        alert,
      });
    }

    // Emit usage event
    this.emit('usageTracked', record);

    return record;
  }

  async getUsage(filter: UsageFilter): Promise<UsageSummary> {
    const allRecords = await this.getUsageHistory(filter.timeRange);

    let filteredRecords = allRecords;

    if (filter.userId) {
      filteredRecords = filteredRecords.filter((r) => r.userId === filter.userId);
    }

    if (filter.organizationId) {
      filteredRecords = filteredRecords.filter((r) => r.organizationId === filter.organizationId);
    }

    if (filter.model) {
      filteredRecords = filteredRecords.filter((r) => r.model === filter.model);
    }

    const summary: UsageSummary = {
      totalRequests: filteredRecords.length,
      totalTokens: filteredRecords.reduce((sum, r) => sum + r.totalTokens, 0),
      totalCost: filteredRecords.reduce((sum, r) => sum + r.requestCost, 0),
      costByModel: {},
      tokensByModel: {},
      requestsByModel: {},
      timeRange: filter.timeRange,
    };

    // Aggregate by model
    for (const record of filteredRecords) {
      summary.costByModel[record.model] =
        (summary.costByModel[record.model] || 0) + record.requestCost;
      summary.tokensByModel[record.model] =
        (summary.tokensByModel[record.model] || 0) + record.totalTokens;
      summary.requestsByModel[record.model] = (summary.requestsByModel[record.model] || 0) + 1;
    }

    return summary;
  }

  async checkBudgetLimit(userId: string, organizationId?: string): Promise<BudgetStatus> {
    const budgetLimit = this.budgetLimits.get(userId);
    const now = new Date();
    const startOfDay = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const startOfMonth = new Date(now.getFullYear(), now.getMonth(), 1);

    const userRecords = this.usageRecords.get(userId) || [];

    // Calculate daily usage
    const dailyRecords = userRecords.filter((r) => new Date(r.timestamp) >= startOfDay);
    const dailyUsed = dailyRecords.reduce((sum, r) => sum + r.requestCost, 0);

    // Calculate monthly usage
    const monthlyRecords = userRecords.filter((r) => new Date(r.timestamp) >= startOfMonth);
    const monthlyUsed = monthlyRecords.reduce((sum, r) => sum + r.requestCost, 0);

    const alerts: BudgetAlert[] = [];

    // Check daily limits
    let dailyPercentage = 0;
    if (budgetLimit?.dailyLimit) {
      dailyPercentage = (dailyUsed / budgetLimit.dailyLimit) * 100;

      if (budgetLimit.alertThresholds) {
        for (const threshold of budgetLimit.alertThresholds) {
          if (dailyPercentage >= threshold && dailyPercentage < threshold + 5) {
            alerts.push({
              type: 'daily',
              level: threshold >= 90 ? 'critical' : 'warning',
              percentage: dailyPercentage,
              message: `Daily budget usage is ${dailyPercentage.toFixed(1)}% ($${dailyUsed.toFixed(2)} of $${budgetLimit.dailyLimit})`,
            });
          }
        }
      }
    }

    // Check monthly limits
    let monthlyPercentage = 0;
    if (budgetLimit?.monthlyLimit) {
      monthlyPercentage = (monthlyUsed / budgetLimit.monthlyLimit) * 100;

      if (budgetLimit.alertThresholds) {
        for (const threshold of budgetLimit.alertThresholds) {
          if (monthlyPercentage >= threshold && monthlyPercentage < threshold + 5) {
            alerts.push({
              type: 'monthly',
              level: threshold >= 90 ? 'critical' : 'warning',
              percentage: monthlyPercentage,
              message: `Monthly budget usage is ${monthlyPercentage.toFixed(1)}% ($${monthlyUsed.toFixed(2)} of $${budgetLimit.monthlyLimit})`,
            });
          }
        }
      }
    }

    const withinLimit =
      (!budgetLimit?.dailyLimit || dailyUsed <= budgetLimit.dailyLimit) &&
      (!budgetLimit?.monthlyLimit || monthlyUsed <= budgetLimit.monthlyLimit);

    return {
      dailyUsed,
      dailyLimit: budgetLimit?.dailyLimit,
      monthlyUsed,
      monthlyLimit: budgetLimit?.monthlyLimit,
      dailyPercentage,
      monthlyPercentage,
      alerts,
      withinLimit,
    };
  }

  async setBudgetLimit(userId: string, limit: BudgetLimit): Promise<void> {
    this.budgetLimits.set(userId, limit);
    this.emit('budgetLimitUpdated', { userId, limit });
  }

  async getUsageHistory(timeRange: TimeRange): Promise<UsageRecord[]> {
    const allRecords: UsageRecord[] = [];

    for (const records of this.usageRecords.values()) {
      for (const record of records) {
        const recordTime = new Date(record.timestamp);
        const startTime = new Date(timeRange.start);
        const endTime = new Date(timeRange.end);

        if (recordTime >= startTime && recordTime <= endTime) {
          allRecords.push(record);
        }
      }
    }

    return allRecords.sort(
      (a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()
    );
  }
}
```

3. **Integrate with OpenAI Provider**:

```typescript
// packages/providers/src/openai/openai-provider.ts (updated sections)
import { OpenAITokenCounter } from './openai-token-counter';
import { OpenAICostCalculator } from './openai-cost-calculator';
import { OpenAIUsageTracker } from './openai-usage-tracker';

export class OpenAIProvider implements IAIProvider {
  private tokenCounter: OpenAITokenCounter;
  private costCalculator: OpenAICostCalculator;
  private usageTracker: OpenAIUsageTracker;

  constructor(private config: OpenAIProviderConfig) {
    this.tokenCounter = new OpenAITokenCounter();
    this.costCalculator = new OpenAICostCalculator();
    this.usageTracker = new OpenAIUsageTracker();

    // Set up budget alerts
    this.usageTracker.on('budgetAlert', (alert) => {
      logger.warn('Budget alert', { userId: alert.userId, alert: alert.alert });
    });
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    // Count input tokens
    const inputTokens = await this.tokenCounter.countMessagesTokens(
      request.messages,
      request.model
    );

    // Make the API call
    const response = await this.client.chat.completions.create({
      model: request.model,
      messages: request.messages,
      stream: true,
      temperature: request.temperature,
      max_tokens: request.maxTokens,
      ...request.options,
    });

    let outputTokens = 0;
    let responseContent = '';

    return this.processStream(response, async (chunk) => {
      // Track output tokens as they come in
      if (chunk.content) {
        responseContent += chunk.content;
        outputTokens = await this.tokenCounter.countTokens(responseContent, request.model);
      }
    });
  }

  private async processStream(
    stream: any,
    onChunk: (chunk: MessageChunk) => Promise<void>
  ): Promise<AsyncIterable<MessageChunk>> {
    const chunks: MessageChunk[] = [];
    let inputTokens = 0;
    let outputTokens = 0;

    return async function* () {
      try {
        for await (const chunk of stream) {
          const messageChunk = this.parseChunk(chunk);
          chunks.push(messageChunk);
          await onChunk(messageChunk);
          yield messageChunk;
        }
      } finally {
        // Track usage when stream completes
        if (request.userId && chunks.length > 0) {
          const requestCost = this.costCalculator.calculateRequestCost(
            inputTokens,
            outputTokens,
            request.model
          );

          await this.usageTracker.trackRequest({
            userId: request.userId,
            organizationId: request.organizationId,
            model: request.model,
            inputTokens,
            outputTokens,
            requestCost,
            metadata: {
              requestId: request.id,
              streamLength: chunks.length,
            },
          });
        }
      }
    }.bind(this)();
  }
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/token-counter.interface.ts`
   - `packages/providers/src/interfaces/cost-calculator.interface.ts`
   - `packages/providers/src/interfaces/usage-tracker.interface.ts`

2. **OpenAI Implementation**:
   - `packages/providers/src/openai/openai-token-counter.ts`
   - `packages/providers/src/openai/openai-cost-calculator.ts`
   - `packages/providers/src/openai/openai-usage-tracker.ts`
   - `packages/providers/src/openai/tiktoken-wrapper.ts` (optional)

3. **Updated Files**:
   - `packages/providers/src/openai/openai-provider.ts` (integrate token/cost tracking)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/openai-token-counter.test.ts
describe('OpenAITokenCounter', () => {
  let tokenCounter: OpenAITokenCounter;

  beforeEach(() => {
    tokenCounter = new OpenAITokenCounter();
  });

  describe('countTokens', () => {
    it('should count tokens for simple text', async () => {
      const text = 'Hello, world!';
      const tokens = await tokenCounter.countTokens(text, 'gpt-4');
      expect(tokens).toBeGreaterThan(0);
    });

    it('should handle empty text', async () => {
      const tokens = await tokenCounter.countTokens('', 'gpt-4');
      expect(tokens).toBe(0);
    });
  });

  describe('countMessagesTokens', () => {
    it('should count tokens for message array', async () => {
      const messages: ChatMessage[] = [
        { role: 'user', content: 'Hello' },
        { role: 'assistant', content: 'Hi there!' },
      ];
      const tokens = await tokenCounter.countMessagesTokens(messages, 'gpt-4');
      expect(tokens).toBeGreaterThan(0);
    });
  });

  describe('getModelContextInfo', () => {
    it('should return correct model info', () => {
      const info = tokenCounter.getModelContextInfo('gpt-4');
      expect(info.maxTokens).toBe(8192);
      expect(info.inputCostPer1K).toBe(0.03);
    });

    it('should handle model name variations', () => {
      const info = tokenCounter.getModelContextInfo('gpt-4-0613');
      expect(info.maxTokens).toBe(8192);
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/openai-cost-calculator.test.ts
describe('OpenAICostCalculator', () => {
  let costCalculator: OpenAICostCalculator;

  beforeEach(() => {
    costCalculator = new OpenAICostCalculator();
  });

  describe('calculateCost', () => {
    it('should calculate input cost correctly', () => {
      const cost = costCalculator.calculateCost(1000, 'gpt-4', 'input');
      expect(cost).toBe(0.03);
    });

    it('should calculate output cost correctly', () => {
      const cost = costCalculator.calculateCost(1000, 'gpt-4', 'output');
      expect(cost).toBe(0.06);
    });
  });

  describe('calculateRequestCost', () => {
    it('should calculate total request cost', () => {
      const cost = costCalculator.calculateRequestCost(500, 300, 'gpt-4');
      expect(cost.inputCost).toBe(0.015);
      expect(cost.outputCost).toBe(0.018);
      expect(cost.totalCost).toBe(0.033);
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/openai-usage-tracker.test.ts
describe('OpenAIUsageTracker', () => {
  let usageTracker: OpenAIUsageTracker;

  beforeEach(() => {
    usageTracker = new OpenAIUsageTracker();
  });

  describe('trackRequest', () => {
    it('should track usage request', async () => {
      const request: UsageRequest = {
        userId: 'user1',
        model: 'gpt-4',
        inputTokens: 100,
        outputTokens: 50,
        requestCost: 0.05,
      };

      const record = await usageTracker.trackRequest(request);
      expect(record.userId).toBe('user1');
      expect(record.totalTokens).toBe(150);
      expect(record.requestCost).toBe(0.05);
    });
  });

  describe('checkBudgetLimit', () => {
    it('should check budget status', async () => {
      await usageTracker.setBudgetLimit('user1', {
        dailyLimit: 1.0,
        monthlyLimit: 30.0,
        alertThresholds: [50, 75, 90],
      });

      const status = await usageTracker.checkBudgetLimit('user1');
      expect(status.dailyLimit).toBe(1.0);
      expect(status.monthlyLimit).toBe(30.0);
      expect(status.withinLimit).toBe(true);
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/openai-provider-cost-tracking.test.ts
describe('OpenAI Provider Cost Tracking Integration', () => {
  let provider: OpenAIProvider;
  let testConfig: OpenAIProviderConfig;

  beforeAll(() => {
    testConfig = {
      apiKey: process.env.OPENAI_API_KEY || 'test-key',
      organization: process.env.OPENAI_ORGANIZATION,
    };
  });

  beforeEach(() => {
    provider = new OpenAIProvider(testConfig);
  });

  it('should track costs for real API call', async () => {
    const request: MessageRequest = {
      id: 'test-1',
      userId: 'test-user',
      model: 'gpt-3.5-turbo',
      messages: [{ role: 'user', content: 'Say "Hello"' }],
      temperature: 0.7,
    };

    const chunks = [];
    for await (const chunk of provider.sendMessage(request)) {
      chunks.push(chunk);
    }

    expect(chunks.length).toBeGreaterThan(0);

    // Verify usage was tracked (would need to access internal usage tracker)
    // This would require exposing usage tracking methods or using events
  }, 10000);
});
```

## Security Considerations

1. **Cost Protection**:
   - Implement hard limits to prevent runaway costs
   - Require explicit budget approval for high-cost operations
   - Monitor for unusual usage patterns

2. **Data Privacy**:
   - Don't store sensitive content in usage records
   - Anonymize usage data for analytics
   - Implement data retention policies

3. **Access Control**:
   - Restrict budget management to authorized users
   - Implement organization-level budget controls
   - Audit all budget limit changes

## Dependencies

### New Dependencies

```json
{
  "tiktoken": "^1.0.10",
  "uuid": "^9.0.1"
}
```

### Dev Dependencies

```json
{
  "@types/uuid": "^9.0.7"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Token usage per model/user/organization
   - Cost trends and anomalies
   - Budget alert frequency
   - API call success/failure rates

2. **Logging**:
   - Cost calculation details
   - Budget limit changes
   - Usage tracking errors
   - Alert triggers

3. **Alerts**:
   - Budget threshold breaches
   - Unusual cost spikes
   - Token counting errors
   - Pricing update failures

## Acceptance Criteria

1. ✅ **Token Counting**: Accurate token counting for all supported OpenAI models
2. ✅ **Cost Calculation**: Real-time cost calculation with current pricing
3. ✅ **Usage Tracking**: Comprehensive usage tracking with detailed analytics
4. ✅ **Budget Management**: Configurable budget limits with alerts
5. ✅ **Integration**: Seamless integration with OpenAI provider
6. ✅ **Testing**: Complete unit and integration test coverage
7. ✅ **Documentation**: Clear API documentation and usage examples
8. ✅ **Performance**: Minimal overhead on API response times
9. ✅ **Security**: Secure handling of cost and usage data
10. ✅ **Monitoring**: Comprehensive metrics and alerting

## Success Metrics

- Token counting accuracy > 95%
- Cost calculation precision to 4 decimal places
- Usage tracking overhead < 5ms per request
- Budget alert latency < 1 second
- Zero cost calculation errors in production
- Complete audit trail for all usage tracking
