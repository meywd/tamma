# Story 2.3: OpenAI Provider Implementation - Task Implementation Plan

## Overview

This document provides detailed implementation plans for each task in Story 2.3: OpenAI Provider Implementation. Each task includes comprehensive technical specifications, code examples, and step-by-step implementation guidance.

---

## Task 1: OpenAI SDK Integration (AC: 1)

### Subtask 1.1: Install and configure OpenAI SDK

**Objective**: Set up the OpenAI SDK as a dependency and configure basic integration.

**Implementation Steps**:

1. **Install Dependencies**:

```bash
pnpm add openai
pnpm add -D @types/openai
```

2. **Create Provider Configuration Schema**:

```typescript
// packages/providers/src/configs/openai-config.schema.ts
import { z } from 'zod';

export const OpenAIConfigSchema = z.object({
  apiKey: z.string().min(1, 'OpenAI API key is required'),
  organization: z.string().optional(),
  baseURL: z.string().url().optional(),
  maxRetries: z.number().min(0).max(10).default(3),
  timeout: z.number().min(1000).max(300000).default(60000),
  defaultModel: z.string().default('gpt-4'),
  models: z.array(z.string()).default(['gpt-4', 'gpt-4-turbo-preview', 'gpt-3.5-turbo']),
});

export type OpenAIConfig = z.infer<typeof OpenAIConfigSchema>;
```

3. **Create Base Provider Structure**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
import { OpenAI } from 'openai';
import { OpenAIConfig } from '../configs/openai-config.schema';

export class OpenAIProvider {
  private client: OpenAI;
  private config: OpenAIConfig;

  constructor(config: OpenAIConfig) {
    this.config = config;
    this.client = new OpenAI({
      apiKey: config.apiKey,
      organization: config.organization,
      baseURL: config.baseURL,
      maxRetries: config.maxRetries,
      timeout: config.timeout,
    });
  }

  getClient(): OpenAI {
    return this.client;
  }

  getConfig(): OpenAIConfig {
    return { ...this.config };
  }
}
```

**Files to Create**:

- `packages/providers/src/configs/openai-config.schema.ts`
- `packages/providers/src/implementations/openai-provider.ts`

**Dependencies**:

- `openai` package
- `zod` for schema validation

---

### Subtask 1.2: Implement authentication with API key

**Objective**: Implement secure API key authentication and validation.

**Implementation Steps**:

1. **Add Authentication Validation**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
import { TammaError } from '@shared/errors';

export class OpenAIProvider {
  // ... previous code ...

  private async validateAuthentication(): Promise<void> {
    try {
      // Test authentication with a minimal API call
      await this.client.models.list();
    } catch (error) {
      if (error instanceof Error) {
        throw new TammaError(
          'OPENAI_AUTH_FAILED',
          `OpenAI authentication failed: ${error.message}`,
          { originalError: error },
          false,
          'critical'
        );
      }
      throw error;
    }
  }

  async initialize(): Promise<void> {
    await this.validateAuthentication();
  }
}
```

2. **Add API Key Security**:

```typescript
// packages/providers/src/utils/security.ts
import { createHash } from 'crypto';

export class SecurityUtils {
  static hashApiKey(apiKey: string): string {
    return createHash('sha256').update(apiKey).digest('hex').substring(0, 8);
  }

  static validateApiKeyFormat(apiKey: string): boolean {
    // OpenAI API keys start with 'sk-' and are typically 51 characters
    return /^sk-[A-Za-z0-9]{48}$/.test(apiKey);
  }

  static redactApiKey(apiKey: string): string {
    if (!apiKey || apiKey.length < 10) return '[INVALID]';
    return `${apiKey.substring(0, 7)}...${apiKey.substring(apiKey.length - 4)}`;
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/security.ts`

---

### Subtask 1.3: Add organization and base URL support

**Objective**: Support OpenAI organization and custom base URL configuration.

**Implementation Steps**:

1. **Enhance Configuration Handling**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  private client: OpenAI;
  private config: OpenAIConfig;

  constructor(config: OpenAIConfig) {
    this.validateConfig(config);
    this.config = config;

    const clientConfig: any = {
      apiKey: config.apiKey,
      maxRetries: config.maxRetries,
      timeout: config.timeout,
    };

    // Add organization if provided
    if (config.organization) {
      clientConfig.organization = config.organization;
    }

    // Add custom base URL if provided
    if (config.baseURL) {
      clientConfig.baseURL = config.baseURL;
    }

    this.client = new OpenAI(clientConfig);
  }

  private validateConfig(config: OpenAIConfig): void {
    if (!SecurityUtils.validateApiKeyFormat(config.apiKey)) {
      throw new TammaError(
        'INVALID_API_KEY',
        'Invalid OpenAI API key format',
        { apiKey: SecurityUtils.redactApiKey(config.apiKey) },
        false,
        'high'
      );
    }

    if (config.baseURL && !this.isValidUrl(config.baseURL)) {
      throw new TammaError(
        'INVALID_BASE_URL',
        'Invalid base URL provided',
        { baseURL: config.baseURL },
        false,
        'high'
      );
    }
  }

  private isValidUrl(url: string): boolean {
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  }
}
```

**Testing Requirements**:

- Test API key validation
- Test organization configuration
- Test custom base URL configuration
- Test authentication failure scenarios

---

## Task 2: Model Support Implementation (AC: 2)

### Subtask 2.1: Implement GPT-4 model support

**Objective**: Add support for GPT-4 model with proper parameter handling.

**Implementation Steps**:

1. **Define Model Configuration**:

```typescript
// packages/providers/src/types/openai-types.ts
export interface OpenAIModelConfig {
  id: string;
  name: string;
  maxTokens: number;
  contextWindow: number;
  inputCostPer1K: number;
  outputCostPer1K: number;
  supportsStreaming: boolean;
  supportsFunctions: boolean;
}

export const OPENAI_MODELS: Record<string, OpenAIModelConfig> = {
  'gpt-4': {
    id: 'gpt-4',
    name: 'GPT-4',
    maxTokens: 8192,
    contextWindow: 8192,
    inputCostPer1K: 0.03,
    outputCostPer1K: 0.06,
    supportsStreaming: true,
    supportsFunctions: true,
  },
  'gpt-4-turbo-preview': {
    id: 'gpt-4-turbo-preview',
    name: 'GPT-4 Turbo',
    maxTokens: 4096,
    contextWindow: 128000,
    inputCostPer1K: 0.01,
    outputCostPer1K: 0.03,
    supportsStreaming: true,
    supportsFunctions: true,
  },
  'gpt-3.5-turbo': {
    id: 'gpt-3.5-turbo',
    name: 'GPT-3.5 Turbo',
    maxTokens: 4096,
    contextWindow: 16385,
    inputCostPer1K: 0.0015,
    outputCostPer1K: 0.002,
    supportsStreaming: true,
    supportsFunctions: true,
  },
};
```

2. **Add Model Support Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  isModelSupported(modelId: string): boolean {
    return modelId in OPENAI_MODELS;
  }

  getModelConfig(modelId: string): OpenAIModelConfig | null {
    return OPENAI_MODELS[modelId] || null;
  }

  getSupportedModels(): OpenAIModelConfig[] {
    return Object.values(OPENAI_MODELS);
  }

  async validateModel(modelId: string): Promise<void> {
    if (!this.isModelSupported(modelId)) {
      throw new TammaError(
        'UNSUPPORTED_MODEL',
        `Model '${modelId}' is not supported by OpenAI provider`,
        { supportedModels: Object.keys(OPENAI_MODELS) },
        false,
        'medium'
      );
    }

    try {
      // Verify model availability by attempting to retrieve model info
      await this.client.models.retrieve(modelId);
    } catch (error) {
      throw new TammaError(
        'MODEL_UNAVAILABLE',
        `Model '${modelId}' is currently unavailable`,
        { modelId, error: error instanceof Error ? error.message : 'Unknown error' },
        true,
        'high'
      );
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/types/openai-types.ts`

---

### Subtask 2.2: Implement GPT-4 Turbo model support

**Objective**: Add specific support for GPT-4 Turbo with its unique capabilities.

**Implementation Steps**:

1. **Add Turbo-Specific Configuration**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  private getTurboConfig(): Partial<OpenAI.Chat.ChatCompletionCreateParams> {
    return {
      model: 'gpt-4-turbo-preview',
      response_format: { type: 'text' }, // Can be extended for JSON mode
      temperature: 0.7,
      max_tokens: 4096,
      top_p: 1,
      frequency_penalty: 0,
      presence_penalty: 0,
    };
  }

  async createTurboCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    const config = { ...this.getTurboConfig(), ...options };

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }
}
```

---

### Subtask 2.3: Implement GPT-3.5 Turbo model support

**Objective**: Add support for GPT-3.5 Turbo with cost-effective configuration.

**Implementation Steps**:

1. **Add GPT-3.5 Specific Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  private getGPT35TurboConfig(): Partial<OpenAI.Chat.ChatCompletionCreateParams> {
    return {
      model: 'gpt-3.5-turbo',
      temperature: 0.7,
      max_tokens: 4096,
      top_p: 1,
      frequency_penalty: 0,
      presence_penalty: 0,
    };
  }

  async createGPT35TurboCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    const config = { ...this.getGPT35TurboConfig(), ...options };

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }
}
```

**Testing Requirements**:

- Test all model configurations
- Validate model availability checks
- Test model-specific parameter handling
- Verify cost calculations per model

---

## Task 3: Streaming Response Handling (AC: 3)

### Subtask 3.1: Implement streaming chat completion

**Objective**: Add streaming support for real-time response generation.

**Implementation Steps**:

1. **Create Streaming Interface**:

```typescript
// packages/providers/src/types/streaming.ts
export interface StreamingChunk {
  id: string;
  content: string;
  finishReason?: string;
  usage?: {
    promptTokens: number;
    completionTokens: number;
    totalTokens: number;
  };
  timestamp: string;
}

export interface StreamingOptions {
  onChunk?: (chunk: StreamingChunk) => void;
  onError?: (error: Error) => void;
  onComplete?: (usage: any) => void;
}
```

2. **Implement Streaming Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  async *createStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): AsyncIterable<StreamingChunk> {
    await this.validateModel(model);

    const stream = await this.client.chat.completions.create({
      model,
      messages,
      stream: true,
      ...options,
    });

    try {
      for await (const chunk of stream) {
        const delta = chunk.choices[0]?.delta;

        if (delta?.content) {
          yield {
            id: chunk.id,
            content: delta.content,
            finishReason: chunk.choices[0]?.finish_reason,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          };
        }

        if (chunk.choices[0]?.finish_reason) {
          yield {
            id: chunk.id,
            content: '',
            finishReason: chunk.choices[0].finish_reason,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          };
          break;
        }
      }
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createStreamingCompletionWithCallback(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: StreamingOptions & Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<void> {
    const { onChunk, onError, onComplete, ...completionOptions } = options;

    try {
      for await (const chunk of this.createStreamingCompletion(
        messages,
        model,
        completionOptions
      )) {
        onChunk?.(chunk);

        if (chunk.finishReason) {
          onComplete?.(chunk.usage);
          break;
        }
      }
    } catch (error) {
      onError?.(error instanceof Error ? error : new Error('Streaming failed'));
      throw error;
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/types/streaming.ts`

---

### Subtask 3.2: Add chunked processing for real-time updates

**Objective**: Implement efficient chunked processing with buffering and aggregation.

**Implementation Steps**:

1. **Create Chunk Processor**:

```typescript
// packages/providers/src/utils/chunk-processor.ts
import { StreamingChunk } from '../types/streaming';

export class ChunkProcessor {
  private buffer: string = '';
  private lastChunkTime: number = 0;
  private readonly flushInterval: number;

  constructor(flushInterval: number = 100) {
    this.flushInterval = flushInterval;
  }

  processChunk(chunk: StreamingChunk): StreamingChunk | null {
    this.buffer += chunk.content;
    this.lastChunkTime = Date.now();

    // Return null if we should buffer more content
    if (!this.shouldFlush(chunk)) {
      return null;
    }

    const processedChunk: StreamingChunk = {
      ...chunk,
      content: this.buffer,
    };

    this.buffer = '';
    return processedChunk;
  }

  private shouldFlush(chunk: StreamingChunk): boolean {
    // Flush on finish reason
    if (chunk.finishReason) {
      return true;
    }

    // Flush on sentence boundaries
    if (/[.!?]\s*$/.test(this.buffer)) {
      return true;
    }

    // Flush on time interval
    return Date.now() - this.lastChunkTime > this.flushInterval;
  }

  flush(): StreamingChunk | null {
    if (this.buffer.length === 0) {
      return null;
    }

    const chunk: StreamingChunk = {
      id: `flush-${Date.now()}`,
      content: this.buffer,
      timestamp: new Date().toISOString(),
    };

    this.buffer = '';
    return chunk;
  }
}
```

---

### Subtask 3.3: Handle streaming errors and reconnection

**Objective**: Implement robust error handling and reconnection logic for streaming.

**Implementation Steps**:

1. **Add Streaming Error Handling**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  async createResilientStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & {
      maxRetries?: number;
      retryDelay?: number;
    } = {}
  ): AsyncIterable<StreamingChunk> {
    const { maxRetries = 3, retryDelay = 1000, ...completionOptions } = options;
    let lastError: Error | null = null;

    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        for await (const chunk of this.createStreamingCompletion(
          messages,
          model,
          completionOptions
        )) {
          yield chunk;
        }
        return; // Success, exit retry loop
      } catch (error) {
        lastError = error instanceof Error ? error : new Error('Unknown streaming error');

        if (attempt === maxRetries || !this.isRetryableError(lastError)) {
          throw lastError;
        }

        // Exponential backoff with jitter
        const delay = retryDelay * Math.pow(2, attempt - 1) + Math.random() * 1000;
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }

    throw lastError!;
  }

  private isRetryableError(error: Error): boolean {
    const message = error.message.toLowerCase();

    // Retry on network errors and rate limits
    return (
      message.includes('timeout') ||
      message.includes('connection') ||
      message.includes('rate limit') ||
      message.includes('temporary')
    );
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/chunk-processor.ts`

**Testing Requirements**:

- Test streaming functionality end-to-end
- Test chunk processing and buffering
- Test error handling and reconnection
- Test performance under network issues

---

## Task 4: Token and Cost Management (AC: 4)

### Subtask 4.1: Implement token counting for requests/responses

**Objective**: Accurately count tokens for OpenAI models using tiktoken.

**Implementation Steps**:

1. **Install Token Counting Library**:

```bash
pnpm add tiktoken
pnpm add -D @types/tiktoken
```

2. **Create Token Counter**:

```typescript
// packages/providers/src/utils/token-counter.ts
import { Tiktoken, get_encoding } from 'tiktoken';
import { OPENAI_MODELS } from '../types/openai-types';

export class TokenCounter {
  private encoders: Map<string, Tiktoken> = new Map();

  private getEncoder(model: string): Tiktoken {
    if (this.encoders.has(model)) {
      return this.encoders.get(model)!;
    }

    let encoding: Tiktoken;

    // Select appropriate encoding based on model
    if (model.startsWith('gpt-4')) {
      encoding = get_encoding('cl100k_base'); // GPT-4 uses cl100k_base
    } else if (model.startsWith('gpt-3.5-turbo')) {
      encoding = get_encoding('cl100k_base'); // GPT-3.5 Turbo uses cl100k_base
    } else {
      encoding = get_encoding('p50k_base'); // Fallback encoding
    }

    this.encoders.set(model, encoding);
    return encoding;
  }

  countTokens(text: string, model: string): number {
    const encoder = this.getEncoder(model);
    const tokens = encoder.encode(text);
    return tokens.length;
  }

  countMessageTokens(messages: OpenAI.Chat.ChatCompletionMessageParam[], model: string): number {
    let tokenCount = 0;
    const encoder = this.getEncoder(model);

    for (const message of messages) {
      // Add tokens for message format
      tokenCount += 4; // Every message follows <|start|>{role/name}\n{content}<|end|>\n

      if (message.role === 'name') {
        tokenCount += this.countTokens(message.name || '', model);
      }

      if (typeof message.content === 'string') {
        tokenCount += this.countTokens(message.content, model);
      } else if (Array.isArray(message.content)) {
        for (const content of message.content) {
          if (content.type === 'text') {
            tokenCount += this.countTokens(content.text, model);
          }
        }
      }
    }

    // Add tokens for final assistant message prefix
    tokenCount += 3; // Every reply is primed with <|start|>assistant<|message|>

    return tokenCount;
  }

  countFunctionTokens(
    functions: OpenAI.Chat.ChatCompletionCreateParams.Function[],
    model: string
  ): number {
    let tokenCount = 0;
    const encoder = this.getEncoder(model);

    for (const func of functions) {
      // Add tokens for function definition
      tokenCount += this.countTokens(func.name || '', model);
      tokenCount += this.countTokens(func.description || '', model);

      if (func.parameters) {
        // Rough estimation for JSON schema
        const paramStr = JSON.stringify(func.parameters);
        tokenCount += this.countTokens(paramStr, model);
      }
    }

    return tokenCount;
  }

  cleanup(): void {
    for (const encoder of this.encoders.values()) {
      encoder.free();
    }
    this.encoders.clear();
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/token-counter.ts`

---

### Subtask 4.2: Add cost calculation based on OpenAI pricing

**Objective**: Calculate costs for API usage based on current OpenAI pricing.

**Implementation Steps**:

1. **Create Cost Calculator**:

```typescript
// packages/providers/src/utils/cost-calculator.ts
import { OPENAI_MODELS, OpenAIModelConfig } from '../types/openai-types';
import { TokenCounter } from './token-counter';

export interface UsageInfo {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  cost: number;
  model: string;
}

export class CostCalculator {
  private tokenCounter: TokenCounter;

  constructor() {
    this.tokenCounter = new TokenCounter();
  }

  calculateCost(promptTokens: number, completionTokens: number, model: string): number {
    const modelConfig = OPENAI_MODELS[model];
    if (!modelConfig) {
      throw new Error(`Unknown model: ${model}`);
    }

    const inputCost = (promptTokens / 1000) * modelConfig.inputCostPer1K;
    const outputCost = (completionTokens / 1000) * modelConfig.outputCostPer1K;

    return inputCost + outputCost;
  }

  calculateUsageCost(usage: OpenAI.Chat.CompletionUsage, model: string): UsageInfo {
    const cost = this.calculateCost(usage.prompt_tokens, usage.completion_tokens, model);

    return {
      promptTokens: usage.prompt_tokens,
      completionTokens: usage.completion_tokens,
      totalTokens: usage.total_tokens,
      cost,
      model,
    };
  }

  estimateRequestCost(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string,
    maxTokens?: number
  ): { estimatedCost: number; estimatedTokens: number } {
    const promptTokens = this.tokenCounter.countMessageTokens(messages, model);
    const estimatedCompletionTokens = maxTokens || this.estimateCompletionTokens(messages, model);

    const estimatedCost = this.calculateCost(promptTokens, estimatedCompletionTokens, model);

    return {
      estimatedCost,
      estimatedTokens: promptTokens + estimatedCompletionTokens,
    };
  }

  private estimateCompletionTokens(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string
  ): number {
    // Simple heuristic: estimate completion tokens based on input
    const inputTokens = this.tokenCounter.countMessageTokens(messages, model);

    // Rough estimation: completion is typically 25-50% of input
    return Math.floor(inputTokens * 0.4);
  }

  getModelPricing(): Record<string, { input: number; output: number }> {
    const pricing: Record<string, { input: number; output: number }> = {};

    for (const [modelId, config] of Object.entries(OPENAI_MODELS)) {
      pricing[modelId] = {
        input: config.inputCostPer1K,
        output: config.outputCostPer1K,
      };
    }

    return pricing;
  }

  cleanup(): void {
    this.tokenCounter.cleanup();
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/cost-calculator.ts`

---

### Subtask 4.3: Track usage metrics for billing

**Objective**: Implement usage tracking and metrics collection for billing purposes.

**Implementation Steps**:

1. **Create Usage Tracker**:

```typescript
// packages/providers/src/utils/usage-tracker.ts
import { UsageInfo } from './cost-calculator';

export interface UsageRecord extends UsageInfo {
  timestamp: string;
  requestId: string;
  userId?: string;
  organizationId?: string;
  endpoint: string;
}

export interface UsageSummary {
  totalRequests: number;
  totalTokens: number;
  totalCost: number;
  modelBreakdown: Record<
    string,
    {
      requests: number;
      tokens: number;
      cost: number;
    }
  >;
  timeRange: {
    start: string;
    end: string;
  };
}

export class UsageTracker {
  private records: UsageRecord[] = [];
  private maxRecords: number = 10000;

  recordUsage(
    usage: UsageInfo,
    metadata: {
      requestId: string;
      endpoint: string;
      userId?: string;
      organizationId?: string;
    }
  ): void {
    const record: UsageRecord = {
      ...usage,
      timestamp: new Date().toISOString(),
      ...metadata,
    };

    this.records.push(record);

    // Maintain memory limits
    if (this.records.length > this.maxRecords) {
      this.records.splice(0, this.records.length - this.maxRecords);
    }
  }

  getUsageSummary(
    startDate?: Date,
    endDate?: Date,
    userId?: string,
    organizationId?: string
  ): UsageSummary {
    let filteredRecords = this.records;

    // Apply filters
    if (startDate) {
      filteredRecords = filteredRecords.filter((record) => new Date(record.timestamp) >= startDate);
    }

    if (endDate) {
      filteredRecords = filteredRecords.filter((record) => new Date(record.timestamp) <= endDate);
    }

    if (userId) {
      filteredRecords = filteredRecords.filter((record) => record.userId === userId);
    }

    if (organizationId) {
      filteredRecords = filteredRecords.filter(
        (record) => record.organizationId === organizationId
      );
    }

    // Calculate summary
    const summary: UsageSummary = {
      totalRequests: filteredRecords.length,
      totalTokens: filteredRecords.reduce((sum, record) => sum + record.totalTokens, 0),
      totalCost: filteredRecords.reduce((sum, record) => sum + record.cost, 0),
      modelBreakdown: {},
      timeRange: {
        start: startDate?.toISOString() || '',
        end: endDate?.toISOString() || '',
      },
    };

    // Model breakdown
    for (const record of filteredRecords) {
      if (!summary.modelBreakdown[record.model]) {
        summary.modelBreakdown[record.model] = {
          requests: 0,
          tokens: 0,
          cost: 0,
        };
      }

      summary.modelBreakdown[record.model].requests++;
      summary.modelBreakdown[record.model].tokens += record.totalTokens;
      summary.modelBreakdown[record.model].cost += record.cost;
    }

    return summary;
  }

  exportUsageData(
    format: 'json' | 'csv' = 'json',
    filters?: {
      startDate?: Date;
      endDate?: Date;
      userId?: string;
      organizationId?: string;
    }
  ): string {
    const summary = this.getUsageSummary(
      filters?.startDate,
      filters?.endDate,
      filters?.userId,
      filters?.organizationId
    );

    if (format === 'json') {
      return JSON.stringify(summary, null, 2);
    }

    if (format === 'csv') {
      const headers = ['Timestamp', 'Model', 'Request ID', 'User ID', 'Tokens', 'Cost'];
      const rows = this.records
        .filter((record) => {
          if (filters?.startDate && new Date(record.timestamp) < filters.startDate) return false;
          if (filters?.endDate && new Date(record.timestamp) > filters.endDate) return false;
          if (filters?.userId && record.userId !== filters.userId) return false;
          if (filters?.organizationId && record.organizationId !== filters.organizationId)
            return false;
          return true;
        })
        .map((record) => [
          record.timestamp,
          record.model,
          record.requestId,
          record.userId || '',
          record.totalTokens.toString(),
          record.cost.toFixed(6),
        ]);

      return [headers, ...rows].map((row) => row.join(',')).join('\n');
    }

    throw new Error(`Unsupported export format: ${format}`);
  }

  clearRecords(): void {
    this.records = [];
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/usage-tracker.ts`

**Testing Requirements**:

- Test token counting accuracy
- Validate cost calculations
- Test usage tracking and reporting
- Verify export functionality

---

## Task 5: Rate Limiting and Retry Logic (AC: 5)

### Subtask 5.1: Implement rate limit detection

**Objective**: Detect and handle OpenAI rate limits effectively.

**Implementation Steps**:

1. **Create Rate Limit Detector**:

```typescript
// packages/providers/src/utils/rate-limiter.ts
import { TammaError } from '@shared/errors';

export interface RateLimitInfo {
  limit: number;
  remaining: number;
  resetTime: Date;
  retryAfter: number;
}

export class RateLimiter {
  private lastRequestTime: number = 0;
  private requestCount: number = 0;
  private rateLimitWindow: number = 60000; // 1 minute
  private maxRequestsPerWindow: number = 3000; // OpenAI's typical limit

  detectRateLimit(error: any): RateLimitInfo | null {
    // Check for OpenAI rate limit error
    if (error?.error?.type === 'rate_limit_exceeded') {
      return {
        limit: error.error.limit || this.maxRequestsPerWindow,
        remaining: error.error.remaining || 0,
        resetTime: new Date(error.error.reset_time || Date.now() + 60000),
        retryAfter: error.error.retry_after || 60,
      };
    }

    // Check HTTP headers for rate limit info
    if (error?.headers) {
      const limit = parseInt(error.headers['x-ratelimit-limit-requests'] || '0');
      const remaining = parseInt(error.headers['x-ratelimit-remaining-requests'] || '0');
      const resetTime = error.headers['x-ratelimit-reset-requests'];

      if (limit > 0 && remaining === 0) {
        return {
          limit,
          remaining,
          resetTime: new Date(resetTime || Date.now() + 60000),
          retryAfter: 60,
        };
      }
    }

    return null;
  }

  async waitForRateLimit(rateLimitInfo: RateLimitInfo): Promise<void> {
    const now = Date.now();
    const waitTime = Math.max(0, rateLimitInfo.resetTime.getTime() - now);

    if (waitTime > 0) {
      console.warn(`Rate limit reached. Waiting ${waitTime}ms until reset`);
      await new Promise((resolve) => setTimeout(resolve, waitTime));
    }
  }

  shouldThrottle(): boolean {
    const now = Date.now();

    // Reset counter if window has passed
    if (now - this.lastRequestTime > this.rateLimitWindow) {
      this.requestCount = 0;
      this.lastRequestTime = now;
    }

    // Check if we're approaching the limit
    return this.requestCount >= this.maxRequestsPerWindow * 0.8; // 80% threshold
  }

  recordRequest(): void {
    this.requestCount++;
  }

  getRateLimitStatus(): {
    requestsInWindow: number;
    maxRequestsPerWindow: number;
    windowResetTime: Date;
  } {
    return {
      requestsInWindow: this.requestCount,
      maxRequestsPerWindow: this.maxRequestsPerWindow,
      windowResetTime: new Date(this.lastRequestTime + this.rateLimitWindow),
    };
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/rate-limiter.ts`

---

### Subtask 5.2: Add exponential backoff retry mechanism

**Objective**: Implement intelligent retry logic with exponential backoff and jitter.

**Implementation Steps**:

1. **Create Retry Manager**:

```typescript
// packages/providers/src/utils/retry-manager.ts
import { TammaError } from '@shared/errors';

export interface RetryOptions {
  maxRetries: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
}

export class RetryManager {
  private defaultOptions: RetryOptions = {
    maxRetries: 3,
    baseDelay: 1000,
    maxDelay: 30000,
    backoffMultiplier: 2,
    jitter: true,
    retryableErrors: [
      'rate_limit_exceeded',
      'insufficient_quota',
      'model_overloaded',
      'timeout',
      'connection_error',
    ],
  };

  async executeWithRetry<T>(
    operation: () => Promise<T>,
    options: Partial<RetryOptions> = {}
  ): Promise<T> {
    const config = { ...this.defaultOptions, ...options };
    let lastError: Error;

    for (let attempt = 1; attempt <= config.maxRetries + 1; attempt++) {
      try {
        return await operation();
      } catch (error) {
        lastError = error instanceof Error ? error : new Error('Unknown error');

        // Don't retry on the last attempt
        if (attempt > config.maxRetries) {
          break;
        }

        // Check if error is retryable
        if (!this.isRetryableError(lastError, config.retryableErrors)) {
          break;
        }

        // Calculate delay for next attempt
        const delay = this.calculateDelay(attempt, config);

        console.warn(`Attempt ${attempt} failed. Retrying in ${delay}ms:`, lastError.message);
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }

    throw lastError!;
  }

  private isRetryableError(error: Error, retryableErrors: string[]): boolean {
    const errorMessage = error.message.toLowerCase();

    // Check for specific error types
    for (const retryableError of retryableErrors) {
      if (errorMessage.includes(retryableError.toLowerCase())) {
        return true;
      }
    }

    // Check for HTTP status codes that should be retried
    if (
      errorMessage.includes('429') || // Too Many Requests
      errorMessage.includes('502') || // Bad Gateway
      errorMessage.includes('503') || // Service Unavailable
      errorMessage.includes('504')
    ) {
      // Gateway Timeout
      return true;
    }

    return false;
  }

  private calculateDelay(attempt: number, config: RetryOptions): number {
    // Exponential backoff
    let delay = config.baseDelay * Math.pow(config.backoffMultiplier, attempt - 1);

    // Apply maximum delay limit
    delay = Math.min(delay, config.maxDelay);

    // Add jitter to prevent thundering herd
    if (config.jitter) {
      const jitterRange = delay * 0.1; // 10% jitter
      delay += Math.random() * jitterRange - jitterRange / 2;
    }

    return Math.max(0, Math.floor(delay));
  }

  async executeWithCircuitBreaker<T>(
    operation: () => Promise<T>,
    options: {
      failureThreshold?: number;
      recoveryTimeout?: number;
      monitoringPeriod?: number;
    } = {}
  ): Promise<T> {
    const { failureThreshold = 5, recoveryTimeout = 60000, monitoringPeriod = 10000 } = options;

    // Simple circuit breaker implementation
    // In production, consider using a dedicated circuit breaker library

    return this.executeWithRetry(operation, {
      maxRetries: 3,
      baseDelay: 1000,
      maxDelay: 10000,
    });
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/retry-manager.ts`

---

### Subtask 5.3: Handle quota exceeded scenarios

**Objective**: Gracefully handle quota exceeded errors and provide clear feedback.

**Implementation Steps**:

1. **Add Quota Management**:

```typescript
// packages/providers/src/utils/quota-manager.ts
import { TammaError } from '@shared/errors';

export interface QuotaInfo {
  currentUsage: number;
  limit: number;
  remaining: number;
  resetDate: Date;
  gracePeriodUsed: boolean;
}

export class QuotaManager {
  private quotaCache: Map<string, QuotaInfo> = new Map();
  private cacheTimeout: number = 300000; // 5 minutes

  detectQuotaExceeded(error: any): QuotaInfo | null {
    // Check for OpenAI quota exceeded error
    if (error?.error?.type === 'insufficient_quota') {
      const quotaInfo: QuotaInfo = {
        currentUsage: error.error.usage || 0,
        limit: error.error.limit || 0,
        remaining: 0,
        resetDate: new Date(error.error.reset_date || this.getNextMonthStart()),
        gracePeriodUsed: error.error.grace_period_used || false,
      };

      this.cacheQuotaInfo('default', quotaInfo);
      return quotaInfo;
    }

    return null;
  }

  private getNextMonthStart(): Date {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth() + 1, 1);
  }

  private cacheQuotaInfo(key: string, quotaInfo: QuotaInfo): void {
    this.quotaCache.set(key, quotaInfo);

    // Clear cache after timeout
    setTimeout(() => {
      this.quotaCache.delete(key);
    }, this.cacheTimeout);
  }

  getCachedQuotaInfo(key: string = 'default'): QuotaInfo | null {
    return this.quotaCache.get(key) || null;
  }

  isQuotaExceeded(quotaInfo: QuotaInfo): boolean {
    return quotaInfo.remaining <= 0 && new Date() >= quotaInfo.resetDate;
  }

  getTimeUntilQuotaReset(quotaInfo: QuotaInfo): number {
    const now = new Date();
    return Math.max(0, quotaInfo.resetDate.getTime() - now.getTime());
  }

  createQuotaExceededError(quotaInfo: QuotaInfo): TammaError {
    const timeUntilReset = this.getTimeUntilQuotaReset(quotaInfo);
    const hoursUntilReset = Math.ceil(timeUntilReset / (1000 * 60 * 60));

    return new TammaError(
      'QUOTA_EXCEEDED',
      `OpenAI API quota exceeded. Quota resets in ${hoursUntilReset} hours.`,
      {
        currentUsage: quotaInfo.currentUsage,
        limit: quotaInfo.limit,
        resetDate: quotaInfo.resetDate,
        gracePeriodUsed: quotaInfo.gracePeriodUsed,
      },
      false,
      'high'
    );
  }

  async waitForQuotaReset(quotaInfo: QuotaInfo): Promise<void> {
    const timeUntilReset = this.getTimeUntilQuotaReset(quotaInfo);

    if (timeUntilReset > 0) {
      console.warn(`Quota exceeded. Waiting ${timeUntilReset}ms for quota reset`);
      await new Promise((resolve) => setTimeout(resolve, timeUntilReset));
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/quota-manager.ts`

**Testing Requirements**:

- Test rate limit detection and handling
- Verify retry logic with exponential backoff
- Test quota exceeded scenarios
- Validate circuit breaker functionality

---

## Task 6: Function Calling Support (AC: 6)

### Subtask 6.1: Implement function calling interface

**Objective**: Add support for OpenAI function calling capabilities.

**Implementation Steps**:

1. **Create Function Calling Types**:

```typescript
// packages/providers/src/types/function-calling.ts
export interface FunctionDefinition {
  name: string;
  description: string;
  parameters: {
    type: 'object';
    properties: Record<string, any>;
    required?: string[];
  };
}

export interface FunctionCall {
  name: string;
  arguments: string;
}

export interface FunctionResult {
  name: string;
  result: any;
  error?: string;
}

export interface FunctionCallingOptions {
  functions?: FunctionDefinition[];
  functionCall?: 'auto' | 'none' | { name: string };
  maxFunctionCalls?: number;
}
```

2. **Add Function Calling Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  async createCompletionWithFunctions(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: FunctionCallingOptions & Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    const { functions, functionCall, maxFunctionCalls = 5, ...completionOptions } = options;

    const request: OpenAI.Chat.ChatCompletionCreateParams = {
      ...completionOptions,
      messages,
      functions: functions?.map((func) => this.convertFunctionDefinition(func)),
      function_call: functionCall,
    };

    try {
      return await this.client.chat.completions.create(request);
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async executeFunctionCall(
    completion: OpenAI.Chat.ChatCompletion,
    functionHandlers: Record<string, (args: any) => Promise<any>>
  ): Promise<FunctionResult[]> {
    const results: FunctionResult[] = [];
    const message = completion.choices[0]?.message;

    if (!message?.function_call) {
      return results;
    }

    const functionCall = message.function_call;
    const handler = functionHandlers[functionCall.name];

    if (!handler) {
      results.push({
        name: functionCall.name,
        result: null,
        error: `No handler found for function: ${functionCall.name}`,
      });
      return results;
    }

    try {
      const args = JSON.parse(functionCall.arguments || '{}');
      const result = await handler(args);

      results.push({
        name: functionCall.name,
        result,
      });
    } catch (error) {
      results.push({
        name: functionCall.name,
        result: null,
        error: error instanceof Error ? error.message : 'Unknown error',
      });
    }

    return results;
  }

  async createConversationWithFunctions(
    initialMessages: OpenAI.Chat.ChatCompletionMessageParam[],
    functionHandlers: Record<string, (args: any) => Promise<any>>,
    options: FunctionCallingOptions & Partial<OpenAI.Chat.ChatCompletionCreateParams> = {},
    maxIterations: number = 10
  ): Promise<OpenAI.Chat.ChatCompletion> {
    let messages = [...initialMessages];
    let iteration = 0;

    while (iteration < maxIterations) {
      const completion = await this.createCompletionWithFunctions(messages, options);
      const message = completion.choices[0]?.message;

      if (!message) {
        break;
      }

      messages.push(message);

      // Check if function call was made
      if (message.function_call) {
        const results = await this.executeFunctionCall(completion, functionHandlers);

        // Add function results to conversation
        for (const result of results) {
          messages.push({
            role: 'function',
            name: result.name,
            content: result.error
              ? JSON.stringify({ error: result.error })
              : JSON.stringify(result.result),
          });
        }

        iteration++;
        continue;
      }

      // No function call, conversation complete
      break;
    }

    // Return final completion
    return await this.client.chat.completions.create({
      ...options,
      messages,
    });
  }

  private convertFunctionDefinition(
    func: FunctionDefinition
  ): OpenAI.Chat.ChatCompletionCreateParams.Function {
    return {
      name: func.name,
      description: func.description,
      parameters: func.parameters,
    };
  }
}
```

**Files to Create**:

- `packages/providers/src/types/function-calling.ts`

---

### Subtask 6.2: Add function definition validation

**Objective**: Validate function definitions before sending to OpenAI.

**Implementation Steps**:

1. **Create Function Validator**:

```typescript
// packages/providers/src/utils/function-validator.ts
import { FunctionDefinition } from '../types/function-calling';
import { TammaError } from '@shared/errors';

export class FunctionValidator {
  validateFunctionDefinition(func: FunctionDefinition): void {
    // Validate name
    if (!func.name || typeof func.name !== 'string') {
      throw new TammaError(
        'INVALID_FUNCTION_NAME',
        'Function name is required and must be a string',
        { function: func.name },
        false,
        'medium'
      );
    }

    if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(func.name)) {
      throw new TammaError(
        'INVALID_FUNCTION_NAME',
        'Function name must be a valid identifier',
        { function: func.name },
        false,
        'medium'
      );
    }

    // Validate description
    if (!func.description || typeof func.description !== 'string') {
      throw new TammaError(
        'INVALID_FUNCTION_DESCRIPTION',
        'Function description is required and must be a string',
        { function: func.name },
        false,
        'medium'
      );
    }

    // Validate parameters
    if (!func.parameters || typeof func.parameters !== 'object') {
      throw new TammaError(
        'INVALID_FUNCTION_PARAMETERS',
        'Function parameters are required and must be an object',
        { function: func.name },
        false,
        'medium'
      );
    }

    if (func.parameters.type !== 'object') {
      throw new TammaError(
        'INVALID_FUNCTION_PARAMETERS',
        'Function parameters must have type "object"',
        { function: func.name },
        false,
        'medium'
      );
    }

    if (!func.parameters.properties || typeof func.parameters.properties !== 'object') {
      throw new TammaError(
        'INVALID_FUNCTION_PARAMETERS',
        'Function parameters must have properties object',
        { function: func.name },
        false,
        'medium'
      );
    }

    // Validate each property
    for (const [propName, propSchema] of Object.entries(func.parameters.properties)) {
      this.validatePropertySchema(propName, propSchema);
    }

    // Validate required properties
    if (func.parameters.required) {
      if (!Array.isArray(func.parameters.required)) {
        throw new TammaError(
          'INVALID_FUNCTION_PARAMETERS',
          'Function required properties must be an array',
          { function: func.name },
          false,
          'medium'
        );
      }

      for (const requiredProp of func.parameters.required) {
        if (!func.parameters.properties[requiredProp]) {
          throw new TammaError(
            'INVALID_FUNCTION_PARAMETERS',
            `Required property '${requiredProp}' not found in function parameters`,
            { function: func.name, requiredProp },
            false,
            'medium'
          );
        }
      }
    }
  }

  private validatePropertySchema(propName: string, schema: any): void {
    if (!schema || typeof schema !== 'object') {
      throw new TammaError(
        'INVALID_PROPERTY_SCHEMA',
        `Property '${propName}' must have a valid schema object`,
        { propertyName: propName },
        false,
        'medium'
      );
    }

    if (!schema.type) {
      throw new TammaError(
        'INVALID_PROPERTY_SCHEMA',
        `Property '${propName}' must have a type`,
        { propertyName: propName },
        false,
        'medium'
      );
    }

    const validTypes = ['string', 'number', 'boolean', 'object', 'array', 'null'];
    if (!validTypes.includes(schema.type)) {
      throw new TammaError(
        'INVALID_PROPERTY_SCHEMA',
        `Property '${propName}' has invalid type '${schema.type}'`,
        { propertyName: propName, type: schema.type },
        false,
        'medium'
      );
    }
  }

  validateFunctionArguments(funcName: string, args: any, funcDef: FunctionDefinition): void {
    if (typeof args !== 'object' || args === null) {
      throw new TammaError(
        'INVALID_FUNCTION_ARGUMENTS',
        `Function arguments must be an object`,
        { function: funcName, arguments: args },
        false,
        'medium'
      );
    }

    // Check required properties
    if (funcDef.parameters.required) {
      for (const requiredProp of funcDef.parameters.required) {
        if (!(requiredProp in args)) {
          throw new TammaError(
            'MISSING_REQUIRED_ARGUMENT',
            `Missing required argument '${requiredProp}' for function '${funcName}'`,
            { function: funcName, missingArgument: requiredProp },
            false,
            'medium'
          );
        }
      }
    }

    // Validate argument types
    for (const [propName, propValue] of Object.entries(args)) {
      const propSchema = funcDef.parameters.properties[propName];
      if (!propSchema) {
        throw new TammaError(
          'UNKNOWN_ARGUMENT',
          `Unknown argument '${propName}' for function '${funcName}'`,
          { function: funcName, unknownArgument: propName },
          false,
          'medium'
        );
      }

      this.validateArgumentType(propName, propValue, propSchema);
    }
  }

  private validateArgumentType(propName: string, value: any, schema: any): void {
    const expectedType = schema.type;
    let actualType = typeof value;

    // Special handling for arrays
    if (expectedType === 'array' && Array.isArray(value)) {
      actualType = 'array';
    }

    // Special handling for null
    if (value === null) {
      actualType = 'null';
    }

    if (actualType !== expectedType) {
      throw new TammaError(
        'INVALID_ARGUMENT_TYPE',
        `Argument '${propName}' has invalid type. Expected ${expectedType}, got ${actualType}`,
        { propertyName: propName, expectedType, actualType },
        false,
        'medium'
      );
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/function-validator.ts`

---

### Subtask 6.3: Handle function call responses

**Objective**: Process and handle function call responses appropriately.

**Implementation Steps**:

1. **Create Function Response Handler**:

```typescript
// packages/providers/src/utils/function-handler.ts
import { FunctionResult, FunctionCall } from '../types/function-calling';
import { FunctionValidator } from './function-validator';

export class FunctionHandler {
  private validator: FunctionValidator;
  private handlers: Map<string, (args: any) => Promise<any>> = new Map();

  constructor() {
    this.validator = new FunctionValidator();
  }

  registerHandler(name: string, handler: (args: any) => Promise<any>): void {
    this.handlers.set(name, handler);
  }

  unregisterHandler(name: string): void {
    this.handlers.delete(name);
  }

  async handleFunctionCall(functionCall: FunctionCall): Promise<FunctionResult> {
    const handler = this.handlers.get(functionCall.name);

    if (!handler) {
      return {
        name: functionCall.name,
        result: null,
        error: `No handler registered for function: ${functionCall.name}`,
      };
    }

    try {
      const args = JSON.parse(functionCall.arguments || '{}');
      const result = await handler(args);

      return {
        name: functionCall.name,
        result,
      };
    } catch (error) {
      return {
        name: functionCall.name,
        result: null,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  async handleMultipleFunctionCalls(functionCalls: FunctionCall[]): Promise<FunctionResult[]> {
    const results: FunctionResult[] = [];

    for (const functionCall of functionCalls) {
      const result = await this.handleFunctionCall(functionCall);
      results.push(result);
    }

    return results;
  }

  createFunctionResponseMessage(results: FunctionResult[]): OpenAI.Chat.ChatCompletionMessageParam {
    const content = results
      .map((result) => {
        if (result.error) {
          return `Error in ${result.name}: ${result.error}`;
        } else {
          return `${result.name}: ${JSON.stringify(result.result)}`;
        }
      })
      .join('\n\n');

    return {
      role: 'function',
      content,
    };
  }

  getRegisteredHandlers(): string[] {
    return Array.from(this.handlers.keys());
  }

  hasHandler(name: string): boolean {
    return this.handlers.has(name);
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/function-handler.ts`

**Testing Requirements**:

- Test function calling interface
- Validate function definition validation
- Test function execution and error handling
- Verify multi-function conversations

---

## Task 7: Configuration Management (AC: 7)

### Subtask 7.1: Add model parameter configuration

**Objective**: Provide flexible configuration for model parameters.

**Implementation Steps**:

1. **Create Parameter Configuration**:

```typescript
// packages/providers/src/configs/parameter-config.ts
export interface ModelParameters {
  temperature?: number;
  topP?: number;
  maxTokens?: number;
  frequencyPenalty?: number;
  presencePenalty?: number;
  stop?: string | string[];
  logitBias?: Record<string, number>;
  user?: string;
}

export interface ModelParameterConfig {
  model: string;
  defaultParameters: ModelParameters;
  parameterConstraints: {
    temperature: { min: number; max: number; default: number };
    topP: { min: number; max: number; default: number };
    maxTokens: { min: number; max: number; default: number };
    frequencyPenalty: { min: number; max: number; default: number };
    presencePenalty: { min: number; max: number; default: number };
  };
}

export const MODEL_PARAMETER_CONFIGS: Record<string, ModelParameterConfig> = {
  'gpt-4': {
    model: 'gpt-4',
    defaultParameters: {
      temperature: 0.7,
      topP: 1,
      maxTokens: 4096,
      frequencyPenalty: 0,
      presencePenalty: 0,
    },
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 8192, default: 4096 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
  },
  'gpt-4-turbo-preview': {
    model: 'gpt-4-turbo-preview',
    defaultParameters: {
      temperature: 0.7,
      topP: 1,
      maxTokens: 4096,
      frequencyPenalty: 0,
      presencePenalty: 0,
    },
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 4096, default: 4096 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
  },
  'gpt-3.5-turbo': {
    model: 'gpt-3.5-turbo',
    defaultParameters: {
      temperature: 0.7,
      topP: 1,
      maxTokens: 4096,
      frequencyPenalty: 0,
      presencePenalty: 0,
    },
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 4096, default: 4096 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
  },
};
```

2. **Add Parameter Validation**:

```typescript
// packages/providers/src/utils/parameter-validator.ts
import {
  ModelParameters,
  ModelParameterConfig,
  MODEL_PARAMETER_CONFIGS,
} from '../configs/parameter-config';
import { TammaError } from '@shared/errors';

export class ParameterValidator {
  validateAndNormalizeParameters(model: string, parameters: ModelParameters): ModelParameters {
    const config = MODEL_PARAMETER_CONFIGS[model];
    if (!config) {
      throw new TammaError(
        'UNKNOWN_MODEL',
        `No parameter configuration found for model: ${model}`,
        { model },
        false,
        'medium'
      );
    }

    const validatedParams: ModelParameters = {};
    const constraints = config.parameterConstraints;

    // Validate and normalize each parameter
    if (parameters.temperature !== undefined) {
      validatedParams.temperature = this.validateNumberParameter(
        'temperature',
        parameters.temperature,
        constraints.temperature
      );
    }

    if (parameters.topP !== undefined) {
      validatedParams.topP = this.validateNumberParameter(
        'topP',
        parameters.topP,
        constraints.topP
      );
    }

    if (parameters.maxTokens !== undefined) {
      validatedParams.maxTokens = this.validateNumberParameter(
        'maxTokens',
        parameters.maxTokens,
        constraints.maxTokens
      );
    }

    if (parameters.frequencyPenalty !== undefined) {
      validatedParams.frequencyPenalty = this.validateNumberParameter(
        'frequencyPenalty',
        parameters.frequencyPenalty,
        constraints.frequencyPenalty
      );
    }

    if (parameters.presencePenalty !== undefined) {
      validatedParams.presencePenalty = this.validateNumberParameter(
        'presencePenalty',
        parameters.presencePenalty,
        constraints.presencePenalty
      );
    }

    // Copy non-numeric parameters as-is
    if (parameters.stop !== undefined) {
      validatedParams.stop = parameters.stop;
    }

    if (parameters.logitBias !== undefined) {
      validatedParams.logitBias = parameters.logitBias;
    }

    if (parameters.user !== undefined) {
      validatedParams.user = parameters.user;
    }

    return validatedParams;
  }

  private validateNumberParameter(
    name: string,
    value: number,
    constraint: { min: number; max: number; default: number }
  ): number {
    if (typeof value !== 'number' || isNaN(value)) {
      throw new TammaError(
        'INVALID_PARAMETER',
        `Parameter '${name}' must be a valid number`,
        { parameter: name, value },
        false,
        'medium'
      );
    }

    if (value < constraint.min || value > constraint.max) {
      throw new TammaError(
        'PARAMETER_OUT_OF_RANGE',
        `Parameter '${name}' must be between ${constraint.min} and ${constraint.max}`,
        { parameter: name, value, min: constraint.min, max: constraint.max },
        false,
        'medium'
      );
    }

    return value;
  }

  getDefaultParameters(model: string): ModelParameters {
    const config = MODEL_PARAMETER_CONFIGS[model];
    if (!config) {
      throw new TammaError(
        'UNKNOWN_MODEL',
        `No parameter configuration found for model: ${model}`,
        { model },
        false,
        'medium'
      );
    }

    return { ...config.defaultParameters };
  }

  getParameterConstraints(model: string) {
    const config = MODEL_PARAMETER_CONFIGS[model];
    if (!config) {
      throw new TammaError(
        'UNKNOWN_MODEL',
        `No parameter configuration found for model: ${model}`,
        { model },
        false,
        'medium'
      );
    }

    return config.parameterConstraints;
  }
}
```

**Files to Create**:

- `packages/providers/src/configs/parameter-config.ts`
- `packages/providers/src/utils/parameter-validator.ts`

---

### Subtask 7.2: Implement system prompt handling

**Objective**: Add system prompt configuration and management.

**Implementation Steps**:

1. **Create System Prompt Manager**:

```typescript
// packages/providers/src/utils/system-prompt-manager.ts
export interface SystemPrompt {
  id: string;
  name: string;
  content: string;
  description?: string;
  category?: string;
  variables?: string[];
}

export interface SystemPromptTemplate {
  id: string;
  name: string;
  template: string;
  description?: string;
  category?: string;
  variables: string[];
}

export class SystemPromptManager {
  private prompts: Map<string, SystemPrompt> = new Map();
  private templates: Map<string, SystemPromptTemplate> = new Map();

  constructor() {
    this.initializeDefaultPrompts();
  }

  private initializeDefaultPrompts(): void {
    // Default system prompts for different use cases
    const defaultPrompts: SystemPrompt[] = [
      {
        id: 'default',
        name: 'Default Assistant',
        content:
          'You are a helpful AI assistant. Provide clear, accurate, and thoughtful responses.',
        category: 'general',
      },
      {
        id: 'code-assistant',
        name: 'Code Assistant',
        content:
          'You are an expert software developer. Provide clean, well-commented code following best practices. Explain your reasoning and consider edge cases.',
        category: 'coding',
      },
      {
        id: 'data-analyst',
        name: 'Data Analyst',
        content:
          'You are a data analyst. Provide insights based on data, suggest appropriate visualizations, and explain statistical concepts clearly.',
        category: 'analysis',
      },
    ];

    for (const prompt of defaultPrompts) {
      this.prompts.set(prompt.id, prompt);
    }
  }

  addPrompt(prompt: SystemPrompt): void {
    this.validatePrompt(prompt);
    this.prompts.set(prompt.id, prompt);
  }

  addTemplate(template: SystemPromptTemplate): void {
    this.validateTemplate(template);
    this.templates.set(template.id, template);
  }

  getPrompt(id: string): SystemPrompt | null {
    return this.prompts.get(id) || null;
  }

  getTemplate(id: string): SystemPromptTemplate | null {
    return this.templates.get(id) || null;
  }

  renderTemplate(templateId: string, variables: Record<string, string>): string {
    const template = this.templates.get(templateId);
    if (!template) {
      throw new Error(`Template not found: ${templateId}`);
    }

    let rendered = template.template;

    // Replace variables in template
    for (const [key, value] of Object.entries(variables)) {
      const placeholder = `{{${key}}}`;
      if (!template.variables.includes(key)) {
        throw new Error(`Variable '${key}' not found in template '${templateId}'`);
      }
      rendered = rendered.replace(new RegExp(placeholder, 'g'), value);
    }

    // Check for unreplaced variables
    const unreplacedVariables = template.variables.filter(
      (variable) => !Object.keys(variables).includes(variable)
    );

    if (unreplacedVariables.length > 0) {
      throw new Error(
        `Missing variables for template '${templateId}': ${unreplacedVariables.join(', ')}`
      );
    }

    return rendered;
  }

  listPrompts(category?: string): SystemPrompt[] {
    const prompts = Array.from(this.prompts.values());

    if (category) {
      return prompts.filter((prompt) => prompt.category === category);
    }

    return prompts;
  }

  listTemplates(category?: string): SystemPromptTemplate[] {
    const templates = Array.from(this.templates.values());

    if (category) {
      return templates.filter((template) => template.category === category);
    }

    return templates;
  }

  deletePrompt(id: string): boolean {
    return this.prompts.delete(id);
  }

  deleteTemplate(id: string): boolean {
    return this.templates.delete(id);
  }

  private validatePrompt(prompt: SystemPrompt): void {
    if (!prompt.id || typeof prompt.id !== 'string') {
      throw new Error('Prompt ID is required and must be a string');
    }

    if (!prompt.name || typeof prompt.name !== 'string') {
      throw new Error('Prompt name is required and must be a string');
    }

    if (!prompt.content || typeof prompt.content !== 'string') {
      throw new Error('Prompt content is required and must be a string');
    }
  }

  private validateTemplate(template: SystemPromptTemplate): void {
    if (!template.id || typeof template.id !== 'string') {
      throw new Error('Template ID is required and must be a string');
    }

    if (!template.name || typeof template.name !== 'string') {
      throw new Error('Template name is required and must be a string');
    }

    if (!template.template || typeof template.template !== 'string') {
      throw new Error('Template content is required and must be a string');
    }

    if (!Array.isArray(template.variables)) {
      throw new Error('Template variables must be an array');
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/system-prompt-manager.ts`

---

### Subtask 7.3: Add temperature and other parameter controls

**Objective**: Implement fine-grained control over model parameters.

**Implementation Steps**:

1. **Add Parameter Control Methods**:

````typescript
// packages/providers/src/implementations/openai-provider.ts
export class OpenAIProvider {
  // ... previous code ...

  async createCompletionWithParameterControl(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    parameterOverrides: ModelParameters = {},
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    // Get default parameters for model
    const defaultParams = this.parameterValidator.getDefaultParameters(model);

    // Validate and merge parameters
    const validatedParams = this.parameterValidator.validateAndNormalizeParameters(model, {
      ...defaultParams,
      ...parameterOverrides,
    });

    const request: OpenAI.Chat.ChatCompletionCreateParams = {
      model,
      messages,
      temperature: validatedParams.temperature,
      top_p: validatedParams.topP,
      max_tokens: validatedParams.maxTokens,
      frequency_penalty: validatedParams.frequencyPenalty,
      presence_penalty: validatedParams.presencePenalty,
      stop: validatedParams.stop,
      logit_bias: validatedParams.logitBias,
      user: validatedParams.user,
      ...options,
    };

    try {
      return await this.client.chat.completions.create(request);
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createAdaptiveCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    adaptationRules: {
      highComplexity?: ModelParameters;
      lowComplexity?: ModelParameters;
      codeGeneration?: ModelParameters;
      creativeWriting?: ModelParameters;
    } = {},
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    // Analyze message complexity and select appropriate parameters
    const complexity = this.analyzeMessageComplexity(messages);
    const contentType = this.detectContentType(messages);

    let selectedParams: ModelParameters = {};

    switch (contentType) {
      case 'code':
        selectedParams = adaptationRules.codeGeneration || {
          temperature: 0.1,
          topP: 0.95,
          maxTokens: 4096,
          frequencyPenalty: 0,
          presencePenalty: 0,
        };
        break;
      case 'creative':
        selectedParams = adaptationRules.creativeWriting || {
          temperature: 0.9,
          topP: 1,
          maxTokens: 2048,
          frequencyPenalty: 0.5,
          presencePenalty: 0.5,
        };
        break;
      case 'analytical':
        selectedParams = adaptationRules.highComplexity || {
          temperature: 0.3,
          topP: 0.8,
          maxTokens: 4096,
          frequencyPenalty: 0,
          presencePenalty: 0,
        };
        break;
      default:
        selectedParams = adaptationRules.lowComplexity || {
          temperature: 0.7,
          topP: 1,
          maxTokens: 2048,
          frequencyPenalty: 0,
          presencePenalty: 0,
        };
    }

    return this.createCompletionWithParameterControl(messages, model, selectedParams, options);
  }

  private analyzeMessageComplexity(
    messages: OpenAI.Chat.ChatCompletionMessageParam[]
  ): 'low' | 'medium' | 'high' {
    const lastMessage = messages[messages.length - 1];
    if (!lastMessage || typeof lastMessage.content !== 'string') {
      return 'low';
    }

    const content = lastMessage.content;
    const wordCount = content.split(/\s+/).length;
    const sentenceCount = content.split(/[.!?]+/).length;
    const avgWordsPerSentence = wordCount / Math.max(sentenceCount, 1);

    // Simple heuristics for complexity
    if (wordCount > 500 || avgWordsPerSentence > 20) {
      return 'high';
    } else if (wordCount > 100 || avgWordsPerSentence > 15) {
      return 'medium';
    } else {
      return 'low';
    }
  }

  private detectContentType(
    messages: OpenAI.Chat.ChatCompletionMessageParam[]
  ): 'code' | 'creative' | 'analytical' | 'general' {
    const lastMessage = messages[messages.length - 1];
    if (!lastMessage || typeof lastMessage.content !== 'string') {
      return 'general';
    }

    const content = lastMessage.content.toLowerCase();

    // Check for code-related keywords
    if (
      content.includes('code') ||
      content.includes('function') ||
      content.includes('algorithm') ||
      content.includes('programming') ||
      content.includes('debug') ||
      /```/.test(content)
    ) {
      return 'code';
    }

    // Check for creative writing keywords
    if (
      content.includes('story') ||
      content.includes('poem') ||
      content.includes('creative') ||
      content.includes('imagine') ||
      content.includes('write') ||
      content.includes('narrative')
    ) {
      return 'creative';
    }

    // Check for analytical keywords
    if (
      content.includes('analyze') ||
      content.includes('compare') ||
      content.includes('evaluate') ||
      content.includes('research') ||
      content.includes('data') ||
      content.includes('statistics')
    ) {
      return 'analytical';
    }

    return 'general';
  }
}
````

**Testing Requirements**:

- Test parameter validation and normalization
- Verify system prompt management
- Test adaptive parameter selection
- Validate parameter constraints

---

## Task 8: Error Handling (AC: 8)

### Subtask 8.1: Implement API error classification

**Objective**: Classify and handle different types of OpenAI API errors.

**Implementation Steps**:

1. **Create Error Classifier**:

```typescript
// packages/providers/src/utils/error-classifier.ts
import { TammaError } from '@shared/errors';

export enum OpenAIErrorType {
  AUTHENTICATION_ERROR = 'authentication_error',
  PERMISSION_ERROR = 'permission_error',
  NOT_FOUND_ERROR = 'not_found_error',
  RATE_LIMIT_ERROR = 'rate_limit_error',
  API_ERROR = 'api_error',
  INVALID_REQUEST_ERROR = 'invalid_request_error',
  INSUFFICIENT_QUOTA = 'insufficient_quota',
  MODEL_OVERLOADED = 'model_overloaded',
  TIMEOUT_ERROR = 'timeout_error',
  CONNECTION_ERROR = 'connection_error',
  UNKNOWN_ERROR = 'unknown_error',
}

export interface ClassifiedError {
  type: OpenAIErrorType;
  severity: 'low' | 'medium' | 'high' | 'critical';
  retryable: boolean;
  userMessage: string;
  technicalMessage: string;
  originalError: any;
}

export class ErrorClassifier {
  classifyError(error: any): ClassifiedError {
    // Handle OpenAI API errors
    if (error?.error) {
      return this.classifyOpenAIError(error);
    }

    // Handle HTTP errors
    if (error?.status || error?.statusCode) {
      return this.classifyHTTPError(error);
    }

    // Handle network errors
    if (
      error?.code === 'ECONNRESET' ||
      error?.code === 'ENOTFOUND' ||
      error?.code === 'ETIMEDOUT'
    ) {
      return {
        type: OpenAIErrorType.CONNECTION_ERROR,
        severity: 'high',
        retryable: true,
        userMessage: 'Connection to OpenAI failed. Please check your internet connection.',
        technicalMessage: `Network error: ${error.code}`,
        originalError: error,
      };
    }

    // Handle timeout errors
    if (error?.name === 'AbortError' || error?.message?.includes('timeout')) {
      return {
        type: OpenAIErrorType.TIMEOUT_ERROR,
        severity: 'medium',
        retryable: true,
        userMessage: 'Request timed out. Please try again.',
        technicalMessage: `Request timeout: ${error.message}`,
        originalError: error,
      };
    }

    // Default classification
    return {
      type: OpenAIErrorType.UNKNOWN_ERROR,
      severity: 'medium',
      retryable: false,
      userMessage: 'An unexpected error occurred. Please try again.',
      technicalMessage: `Unknown error: ${error?.message || 'No message available'}`,
      originalError: error,
    };
  }

  private classifyOpenAIError(error: any): ClassifiedError {
    const openAIError = error.error;
    const errorType = openAIError.type;
    const code = openAIError.code;

    switch (errorType) {
      case 'invalid_request_error':
        return {
          type: OpenAIErrorType.INVALID_REQUEST_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'Invalid request. Please check your input and try again.',
          technicalMessage: `Invalid request: ${openAIError.message}`,
          originalError: error,
        };

      case 'authentication_error':
        return {
          type: OpenAIErrorType.AUTHENTICATION_ERROR,
          severity: 'critical',
          retryable: false,
          userMessage: 'Authentication failed. Please check your API key.',
          technicalMessage: `Authentication error: ${openAIError.message}`,
          originalError: error,
        };

      case 'permission_error':
        return {
          type: OpenAIErrorType.PERMISSION_ERROR,
          severity: 'high',
          retryable: false,
          userMessage: "Permission denied. You don't have access to this resource.",
          technicalMessage: `Permission error: ${openAIError.message}`,
          originalError: error,
        };

      case 'not_found_error':
        return {
          type: OpenAIErrorType.NOT_FOUND_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'Requested resource not found.',
          technicalMessage: `Not found error: ${openAIError.message}`,
          originalError: error,
        };

      case 'rate_limit_error':
        return {
          type: OpenAIErrorType.RATE_LIMIT_ERROR,
          severity: 'high',
          retryable: true,
          userMessage: 'Rate limit exceeded. Please wait and try again.',
          technicalMessage: `Rate limit error: ${openAIError.message}`,
          originalError: error,
        };

      case 'insufficient_quota':
        return {
          type: OpenAIErrorType.INSUFFICIENT_QUOTA,
          severity: 'critical',
          retryable: false,
          userMessage: 'API quota exceeded. Please check your billing settings.',
          technicalMessage: `Insufficient quota: ${openAIError.message}`,
          originalError: error,
        };

      case 'api_error':
        if (code === 'model_overloaded') {
          return {
            type: OpenAIErrorType.MODEL_OVERLOADED,
            severity: 'high',
            retryable: true,
            userMessage: 'The model is currently overloaded. Please try again later.',
            technicalMessage: `Model overloaded: ${openAIError.message}`,
            originalError: error,
          };
        } else {
          return {
            type: OpenAIErrorType.API_ERROR,
            severity: 'high',
            retryable: true,
            userMessage: 'OpenAI API error. Please try again.',
            technicalMessage: `API error: ${openAIError.message}`,
            originalError: error,
          };
        }

      default:
        return {
          type: OpenAIErrorType.UNKNOWN_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'An unexpected error occurred. Please try again.',
          technicalMessage: `Unknown OpenAI error: ${openAIError.message}`,
          originalError: error,
        };
    }
  }

  private classifyHTTPError(error: any): ClassifiedError {
    const status = error.status || error.statusCode;

    switch (status) {
      case 400:
        return {
          type: OpenAIErrorType.INVALID_REQUEST_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'Bad request. Please check your input.',
          technicalMessage: `HTTP 400: ${error.message}`,
          originalError: error,
        };

      case 401:
        return {
          type: OpenAIErrorType.AUTHENTICATION_ERROR,
          severity: 'critical',
          retryable: false,
          userMessage: 'Authentication failed. Please check your API key.',
          technicalMessage: `HTTP 401: ${error.message}`,
          originalError: error,
        };

      case 403:
        return {
          type: OpenAIErrorType.PERMISSION_ERROR,
          severity: 'high',
          retryable: false,
          userMessage: "Access forbidden. You don't have permission to access this resource.",
          technicalMessage: `HTTP 403: ${error.message}`,
          originalError: error,
        };

      case 404:
        return {
          type: OpenAIErrorType.NOT_FOUND_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'Requested resource not found.',
          technicalMessage: `HTTP 404: ${error.message}`,
          originalError: error,
        };

      case 429:
        return {
          type: OpenAIErrorType.RATE_LIMIT_ERROR,
          severity: 'high',
          retryable: true,
          userMessage: 'Too many requests. Please wait and try again.',
          technicalMessage: `HTTP 429: ${error.message}`,
          originalError: error,
        };

      case 500:
      case 502:
      case 503:
      case 504:
        return {
          type: OpenAIErrorType.API_ERROR,
          severity: 'high',
          retryable: true,
          userMessage: 'Server error. Please try again later.',
          technicalMessage: `HTTP ${status}: ${error.message}`,
          originalError: error,
        };

      default:
        return {
          type: OpenAIErrorType.UNKNOWN_ERROR,
          severity: 'medium',
          retryable: false,
          userMessage: 'An unexpected error occurred. Please try again.',
          technicalMessage: `HTTP ${status}: ${error.message}`,
          originalError: error,
        };
    }
  }

  createTammaError(classifiedError: ClassifiedError): TammaError {
    return new TammaError(
      classifiedError.type.toUpperCase(),
      classifiedError.userMessage,
      {
        technicalMessage: classifiedError.technicalMessage,
        originalError: classifiedError.originalError,
        retryable: classifiedError.retryable,
      },
      classifiedError.retryable,
      classifiedError.severity
    );
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/error-classifier.ts`

---

### Subtask 8.2: Add timeout and connection error handling

**Objective**: Implement robust handling of timeout and connection errors.

**Implementation Steps**:

1. **Add Connection Error Handler**:

```typescript
// packages/providers/src/utils/connection-handler.ts
import { TammaError } from '@shared/errors';

export interface ConnectionOptions {
  timeout: number;
  maxRetries: number;
  retryDelay: number;
  connectionTimeout: number;
  keepAlive: boolean;
}

export class ConnectionHandler {
  private defaultOptions: ConnectionOptions = {
    timeout: 60000,
    maxRetries: 3,
    retryDelay: 1000,
    connectionTimeout: 10000,
    keepAlive: true,
  };

  async executeWithConnectionHandling<T>(
    operation: () => Promise<T>,
    options: Partial<ConnectionOptions> = {}
  ): Promise<T> {
    const config = { ...this.defaultOptions, ...options };
    let lastError: Error;

    for (let attempt = 1; attempt <= config.maxRetries + 1; attempt++) {
      try {
        // Create abort controller for timeout
        const controller = new AbortController();
        const timeoutId = setTimeout(() => {
          controller.abort();
        }, config.timeout);

        try {
          const result = await Promise.race([
            operation(),
            new Promise<never>((_, reject) => {
              controller.signal.addEventListener('abort', () => {
                reject(
                  new TammaError(
                    'TIMEOUT',
                    `Operation timed out after ${config.timeout}ms`,
                    { timeout: config.timeout, attempt },
                    true,
                    'medium'
                  )
                );
              });
            }),
          ]);

          clearTimeout(timeoutId);
          return result;
        } finally {
          clearTimeout(timeoutId);
        }
      } catch (error) {
        lastError = error instanceof Error ? error : new Error('Unknown error');

        // Don't retry on the last attempt
        if (attempt > config.maxRetries) {
          break;
        }

        // Check if error is retryable
        if (!this.isRetryableConnectionError(lastError)) {
          break;
        }

        // Calculate delay for next attempt
        const delay = this.calculateRetryDelay(attempt, config);

        console.warn(
          `Connection attempt ${attempt} failed. Retrying in ${delay}ms:`,
          lastError.message
        );
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }

    throw lastError!;
  }

  private isRetryableConnectionError(error: Error): boolean {
    const message = error.message.toLowerCase();

    // Retry on network-related errors
    return (
      message.includes('timeout') ||
      message.includes('connection') ||
      message.includes('network') ||
      message.includes('econnreset') ||
      message.includes('enotfound') ||
      message.includes('etimedout') ||
      message.includes('econnrefused')
    );
  }

  private calculateRetryDelay(attempt: number, config: ConnectionOptions): number {
    // Exponential backoff with jitter
    const baseDelay = config.retryDelay * Math.pow(2, attempt - 1);
    const jitter = Math.random() * 0.1 * baseDelay; // 10% jitter
    return Math.min(baseDelay + jitter, 30000); // Max 30 seconds
  }

  createEnhancedFetch(baseURL: string, defaultHeaders: Record<string, string> = {}) {
    return async (url: string, options: RequestInit = {}): Promise<Response> => {
      const fullUrl = url.startsWith('http') ? url : `${baseURL}${url}`;

      const enhancedOptions: RequestInit = {
        ...options,
        headers: {
          ...defaultHeaders,
          ...options.headers,
          Connection: 'keep-alive',
          'Keep-Alive': 'timeout=5, max=1000',
        },
        signal: AbortSignal.timeout(30000), // Default 30 second timeout
      };

      try {
        const response = await fetch(fullUrl, enhancedOptions);

        // Check for HTTP errors
        if (!response.ok) {
          const errorText = await response.text();
          throw new TammaError(
            'HTTP_ERROR',
            `HTTP ${response.status}: ${response.statusText}`,
            {
              status: response.status,
              statusText: response.statusText,
              url: fullUrl,
              responseText: errorText,
            },
            response.status >= 500, // Retry on server errors
            this.getSeverityFromStatus(response.status)
          );
        }

        return response;
      } catch (error) {
        if (error instanceof TammaError) {
          throw error;
        }

        // Convert fetch errors to TammaError
        throw new TammaError(
          'CONNECTION_ERROR',
          `Connection failed: ${error instanceof Error ? error.message : 'Unknown error'}`,
          {
            url: fullUrl,
            originalError: error,
          },
          true,
          'high'
        );
      }
    };
  }

  private getSeverityFromStatus(status: number): 'low' | 'medium' | 'high' | 'critical' {
    if (status >= 500) return 'high';
    if (status >= 400) return 'medium';
    if (status >= 300) return 'low';
    return 'low';
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/connection-handler.ts`

---

### Subtask 8.3: Handle rate limit and quota errors gracefully

**Objective**: Provide graceful handling and user feedback for rate limit and quota errors.

**Implementation Steps**:

1. **Add Graceful Error Handler**:

```typescript
// packages/providers/src/utils/graceful-error-handler.ts
import { TammaError } from '@shared/errors';
import { ErrorClassifier, ClassifiedError } from './error-classifier';
import { RateLimiter } from './rate-limiter';
import { QuotaManager } from './quota-manager';

export interface ErrorHandlingOptions {
  enableRetry: boolean;
  maxRetries: number;
  retryDelay: number;
  enableGracefulDegradation: boolean;
  fallbackModel?: string;
  notifyUser: boolean;
}

export class GracefulErrorHandler {
  private errorClassifier: ErrorClassifier;
  private rateLimiter: RateLimiter;
  private quotaManager: QuotaManager;

  constructor() {
    this.errorClassifier = new ErrorClassifier();
    this.rateLimiter = new RateLimiter();
    this.quotaManager = new QuotaManager();
  }

  async handleError(
    error: any,
    context: {
      operation: string;
      model: string;
      requestId: string;
      userId?: string;
    },
    options: ErrorHandlingOptions = {
      enableRetry: true,
      maxRetries: 3,
      retryDelay: 1000,
      enableGracefulDegradation: true,
      notifyUser: true,
    }
  ): Promise<{
    shouldRetry: boolean;
    retryDelay?: number;
    fallbackAction?: string;
    userMessage: string;
    technicalDetails: string;
  }> {
    const classifiedError = this.errorClassifier.classifyError(error);

    // Handle specific error types
    switch (classifiedError.type) {
      case OpenAIErrorType.RATE_LIMIT_ERROR:
        return this.handleRateLimitError(classifiedError, context, options);

      case OpenAIErrorType.INSUFFICIENT_QUOTA:
        return this.handleQuotaError(classifiedError, context, options);

      case OpenAIErrorType.MODEL_OVERLOADED:
        return this.handleModelOverloadedError(classifiedError, context, options);

      case OpenAIErrorType.AUTHENTICATION_ERROR:
        return this.handleAuthenticationError(classifiedError, context, options);

      case OpenAIErrorType.INVALID_REQUEST_ERROR:
        return this.handleInvalidRequestError(classifiedError, context, options);

      case OpenAIErrorType.TIMEOUT_ERROR:
        return this.handleTimeoutError(classifiedError, context, options);

      case OpenAIErrorType.CONNECTION_ERROR:
        return this.handleConnectionError(classifiedError, context, options);

      default:
        return this.handleGenericError(classifiedError, context, options);
    }
  }

  private async handleRateLimitError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    const rateLimitInfo = this.rateLimiter.detectRateLimit(error.originalError);

    if (rateLimitInfo) {
      await this.rateLimiter.waitForRateLimit(rateLimitInfo);

      return {
        shouldRetry: options.enableRetry,
        retryDelay: rateLimitInfo.retryAfter * 1000,
        userMessage: `Rate limit reached. Waiting ${rateLimitInfo.retryAfter} seconds before retrying...`,
        technicalDetails: `Rate limit: ${rateLimitInfo.remaining}/${rateLimitInfo.limit}, Reset: ${rateLimitInfo.resetTime.toISOString()}`,
      };
    }

    return {
      shouldRetry: options.enableRetry,
      retryDelay: options.retryDelay * 2, // Exponential backoff
      userMessage: 'Rate limit exceeded. Please wait a moment before trying again.',
      technicalDetails: error.technicalMessage,
    };
  }

  private async handleQuotaError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    const quotaInfo = this.quotaManager.detectQuotaExceeded(error.originalError);

    if (quotaInfo) {
      const timeUntilReset = this.quotaManager.getTimeUntilQuotaReset(quotaInfo);
      const hoursUntilReset = Math.ceil(timeUntilReset / (1000 * 60 * 60));

      return {
        shouldRetry: false,
        userMessage: `API quota exceeded. Quota will reset in approximately ${hoursUntilReset} hours.`,
        technicalDetails: `Quota used: ${quotaInfo.currentUsage}/${quotaInfo.limit}, Reset: ${quotaInfo.resetDate.toISOString()}`,
        fallbackAction: options.enableGracefulDegradation
          ? 'suggest_alternative_provider'
          : undefined,
      };
    }

    return {
      shouldRetry: false,
      userMessage: 'API quota exceeded. Please check your billing settings.',
      technicalDetails: error.technicalMessage,
    };
  }

  private async handleModelOverloadedError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    const retryDelay = Math.min(options.retryDelay * 3, 30000); // Max 30 seconds

    return {
      shouldRetry: options.enableRetry,
      retryDelay,
      fallbackAction:
        options.enableGracefulDegradation && options.fallbackModel
          ? 'use_fallback_model'
          : undefined,
      userMessage:
        'The requested model is currently overloaded. Trying again or using alternative model...',
      technicalDetails: error.technicalMessage,
    };
  }

  private async handleAuthenticationError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    return {
      shouldRetry: false,
      userMessage: 'Authentication failed. Please check your API key configuration.',
      technicalDetails: error.technicalMessage,
      fallbackAction: 'check_configuration',
    };
  }

  private async handleInvalidRequestError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    return {
      shouldRetry: false,
      userMessage: 'Invalid request. Please check your input parameters.',
      technicalDetails: error.technicalMessage,
      fallbackAction: 'validate_input',
    };
  }

  private async handleTimeoutError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    return {
      shouldRetry: options.enableRetry,
      retryDelay: options.retryDelay * 2,
      userMessage: 'Request timed out. Retrying with adjusted parameters...',
      technicalDetails: error.technicalMessage,
    };
  }

  private async handleConnectionError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    return {
      shouldRetry: options.enableRetry,
      retryDelay: options.retryDelay * 2,
      userMessage: 'Connection error. Checking network connectivity and retrying...',
      technicalDetails: error.technicalMessage,
    };
  }

  private async handleGenericError(
    error: ClassifiedError,
    context: any,
    options: ErrorHandlingOptions
  ): Promise<any> {
    return {
      shouldRetry: error.retryable && options.enableRetry,
      retryDelay: options.retryDelay,
      userMessage: error.userMessage,
      technicalDetails: error.technicalMessage,
    };
  }

  createUserFriendlyMessage(error: ClassifiedError, context: any): string {
    const { operation, model } = context;

    switch (error.type) {
      case OpenAIErrorType.RATE_LIMIT_ERROR:
        return `You've reached the rate limit for ${model}. Please wait a moment before continuing.`;

      case OpenAIErrorType.INSUFFICIENT_QUOTA:
        return `You've reached your API usage limit. Please check your billing settings or upgrade your plan.`;

      case OpenAIErrorType.MODEL_OVERLOADED:
        return `The ${model} model is currently experiencing high demand. Please try again in a moment.`;

      case OpenAIErrorType.AUTHENTICATION_ERROR:
        return `There's an issue with your API key. Please check your configuration.`;

      case OpenAIErrorType.INVALID_REQUEST_ERROR:
        return `The request was invalid. Please check your input and try again.`;

      case OpenAIErrorType.TIMEOUT_ERROR:
        return `The request took too long to complete. Please try again.`;

      case OpenAIErrorType.CONNECTION_ERROR:
        return `Unable to connect to the service. Please check your internet connection.`;

      default:
        return `An error occurred while ${operation}. Please try again or contact support if the problem persists.`;
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/graceful-error-handler.ts`

**Testing Requirements**:

- Test error classification for all error types
- Verify timeout and connection error handling
- Test graceful degradation scenarios
- Validate user-friendly error messages

---

## Complete Implementation Summary

### Files to Create (Total):

1. **Core Provider**:
   - `packages/providers/src/implementations/openai-provider.ts`

2. **Configuration**:
   - `packages/providers/src/configs/openai-config.schema.ts`
   - `packages/providers/src/configs/parameter-config.ts`

3. **Types**:
   - `packages/providers/src/types/openai-types.ts`
   - `packages/providers/src/types/streaming.ts`
   - `packages/providers/src/types/function-calling.ts`

4. **Utilities**:
   - `packages/providers/src/utils/security.ts`
   - `packages/providers/src/utils/token-counter.ts`
   - `packages/providers/src/utils/cost-calculator.ts`
   - `packages/providers/src/utils/usage-tracker.ts`
   - `packages/providers/src/utils/rate-limiter.ts`
   - `packages/providers/src/utils/retry-manager.ts`
   - `packages/providers/src/utils/quota-manager.ts`
   - `packages/providers/src/utils/chunk-processor.ts`
   - `packages/providers/src/utils/function-validator.ts`
   - `packages/providers/src/utils/function-handler.ts`
   - `packages/providers/src/utils/parameter-validator.ts`
   - `packages/providers/src/utils/system-prompt-manager.ts`
   - `packages/providers/src/utils/error-classifier.ts`
   - `packages/providers/src/utils/connection-handler.ts`
   - `packages/providers/src/utils/graceful-error-handler.ts`

5. **Tests**:
   - `tests/providers/openai-provider.test.ts`
   - `tests/providers/utils/` (individual utility tests)

### Dependencies Required:

```json
{
  "dependencies": {
    "openai": "^4.0.0",
    "tiktoken": "^1.0.0",
    "zod": "^3.0.0"
  },
  "devDependencies": {
    "@types/openai": "^4.0.0",
    "@types/tiktoken": "^1.0.0"
  }
}
```

### Integration Points:

1. **IAIProvider Interface**: Implement the interface defined in Story 2.1
2. **ProviderRegistry**: Register the OpenAI provider with the registry
3. **Error Handling**: Use standard error types from the shared error system
4. **Logging**: Integrate with the logging system from Story 1.5
5. **Metrics**: Report usage and performance metrics to the monitoring system

### Testing Strategy:

1. **Unit Tests**: Test all utility functions and individual components
2. **Integration Tests**: Test the complete provider implementation
3. **Mock Tests**: Use OpenAI SDK mocking for reliable testing
4. **Performance Tests**: Validate streaming and rate limiting performance
5. **Error Scenario Tests**: Test all error handling paths

This comprehensive implementation plan provides detailed guidance for implementing the OpenAI provider with all required features, robust error handling, and production-ready capabilities.
