# Task 2: Model Support Implementation (AC: 2)

## Overview

Implement support for GPT-4, GPT-4 Turbo, and GPT-3.5 Turbo models with proper parameter handling, capabilities detection, and model-specific configurations.

## Objectives

- Add support for GPT-4 model with proper configuration
- Implement GPT-4 Turbo model support with extended context
- Add GPT-3.5 Turbo model support for cost-effective usage
- Create model capability detection and validation
- Implement model-specific parameter constraints

## Implementation Steps

### Subtask 2.1: Implement GPT-4 model support

**Objective**: Add comprehensive support for GPT-4 model with proper parameter handling and capabilities.

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
  supportsVision: boolean;
  supportsJsonMode: boolean;
  trainingDataCutoff: string;
  description: string;
  capabilities: string[];
  parameterConstraints: {
    temperature: { min: number; max: number; default: number };
    topP: { min: number; max: number; default: number };
    maxTokens: { min: number; max: number; default: number };
    frequencyPenalty: { min: number; max: number; default: number };
    presencePenalty: { min: number; max: number; default: number };
  };
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
    supportsVision: false,
    supportsJsonMode: true,
    trainingDataCutoff: '2021-09',
    description:
      'Most capable GPT-4 model, great for complex tasks that require advanced reasoning',
    capabilities: ['text-generation', 'function-calling', 'json-mode', 'code-generation'],
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 8192, default: 4096 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
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
    supportsVision: false,
    supportsJsonMode: true,
    trainingDataCutoff: '2023-12',
    description: 'Latest GPT-4 model with improved instruction following and larger context window',
    capabilities: [
      'text-generation',
      'function-calling',
      'json-mode',
      'code-generation',
      'large-context',
    ],
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 4096, default: 4096 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
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
    supportsVision: false,
    supportsJsonMode: true,
    trainingDataCutoff: '2021-09',
    description: 'Fast and efficient model for most text generation tasks',
    capabilities: ['text-generation', 'function-calling', 'json-mode', 'code-generation'],
    parameterConstraints: {
      temperature: { min: 0, max: 2, default: 0.7 },
      topP: { min: 0, max: 1, default: 1 },
      maxTokens: { min: 1, max: 4096, default: 2048 },
      frequencyPenalty: { min: -2, max: 2, default: 0 },
      presencePenalty: { min: -2, max: 2, default: 0 },
    },
  },
};
```

2. **Add Model Support Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
import { OPENAI_MODELS, OpenAIModelConfig } from '../types/openai-types';

export class OpenAIProvider implements IAIProvider {
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

  getModelsByCapability(capability: string): OpenAIModelConfig[] {
    return Object.values(OPENAI_MODELS).filter((model) => model.capabilities.includes(capability));
  }

  async validateModel(modelId: string): Promise<void> {
    if (!this.isModelSupported(modelId)) {
      throw new TammaError(
        'UNSUPPORTED_MODEL',
        `Model '${modelId}' is not supported by OpenAI provider`,
        {
          supportedModels: Object.keys(OPENAI_MODELS),
          requestedModel: modelId,
        },
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
        {
          modelId,
          error: error instanceof Error ? error.message : 'Unknown error',
          suggestion: 'Please check your API access and billing settings',
        },
        true,
        'high'
      );
    }
  }

  async getAvailableModels(): Promise<string[]> {
    try {
      const response = await this.client.models.list();
      const availableModelIds = response.data.map((model) => model.id);

      // Filter to only supported models
      return Object.keys(OPENAI_MODELS).filter((modelId) => availableModelIds.includes(modelId));
    } catch (error) {
      throw new TammaError(
        'MODEL_LIST_FAILED',
        'Failed to retrieve available models from OpenAI',
        {
          error: error instanceof Error ? error.message : 'Unknown error',
        },
        true,
        'high'
      );
    }
  }

  getDefaultModel(): string {
    return this.config.defaultModel || 'gpt-4';
  }

  setDefaultModel(modelId: string): void {
    if (!this.isModelSupported(modelId)) {
      throw new TammaError(
        'INVALID_DEFAULT_MODEL',
        `Cannot set unsupported model '${modelId}' as default`,
        {
          modelId,
          supportedModels: Object.keys(OPENAI_MODELS),
        },
        false,
        'medium'
      );
    }
    this.config.defaultModel = modelId;
  }
}
```

3. **Create GPT-4 Specific Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  private getGPT4Config(): Partial<OpenAI.Chat.ChatCompletionCreateParams> {
    const modelConfig = OPENAI_MODELS['gpt-4'];

    return {
      model: 'gpt-4',
      temperature: modelConfig.parameterConstraints.temperature.default,
      max_tokens: modelConfig.parameterConstraints.maxTokens.default,
      top_p: modelConfig.parameterConstraints.topP.default,
      frequency_penalty: modelConfig.parameterConstraints.frequencyPenalty.default,
      presence_penalty: modelConfig.parameterConstraints.presencePenalty.default,
      response_format: { type: 'text' },
    };
  }

  async createGPT4Completion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-4');

    const config = { ...this.getGPT4Config(), ...options };

    // Validate parameters against model constraints
    this.validateParameters('gpt-4', config);

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createGPT4StreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<AsyncIterable<OpenAI.Chat.Completions.ChatCompletionChunk>> {
    await this.validateModel('gpt-4');

    const config = { ...this.getGPT4Config(), ...options, stream: true };

    // Validate parameters against model constraints
    this.validateParameters('gpt-4', config);

    try {
      const stream = await this.client.chat.completions.create({
        ...config,
        messages,
      });

      return stream;
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  private validateParameters(
    modelId: string,
    params: Partial<OpenAI.Chat.ChatCompletionCreateParams>
  ): void {
    const modelConfig = OPENAI_MODELS[modelId];
    if (!modelConfig) {
      return;
    }

    const constraints = modelConfig.parameterConstraints;

    // Validate temperature
    if (params.temperature !== undefined) {
      if (
        params.temperature < constraints.temperature.min ||
        params.temperature > constraints.temperature.max
      ) {
        throw new TammaError(
          'INVALID_TEMPERATURE',
          `Temperature must be between ${constraints.temperature.min} and ${constraints.temperature.max}`,
          {
            temperature: params.temperature,
            min: constraints.temperature.min,
            max: constraints.temperature.max,
            model: modelId,
          },
          false,
          'medium'
        );
      }
    }

    // Validate top_p
    if (params.top_p !== undefined) {
      if (params.top_p < constraints.topP.min || params.top_p > constraints.topP.max) {
        throw new TammaError(
          'INVALID_TOP_P',
          `Top P must be between ${constraints.topP.min} and ${constraints.topP.max}`,
          {
            topP: params.top_p,
            min: constraints.topP.min,
            max: constraints.topP.max,
            model: modelId,
          },
          false,
          'medium'
        );
      }
    }

    // Validate max_tokens
    if (params.max_tokens !== undefined) {
      if (
        params.max_tokens < constraints.maxTokens.min ||
        params.max_tokens > constraints.maxTokens.max
      ) {
        throw new TammaError(
          'INVALID_MAX_TOKENS',
          `Max tokens must be between ${constraints.maxTokens.min} and ${constraints.maxTokens.max}`,
          {
            maxTokens: params.max_tokens,
            min: constraints.maxTokens.min,
            max: constraints.maxTokens.max,
            model: modelId,
          },
          false,
          'medium'
        );
      }
    }

    // Validate frequency_penalty
    if (params.frequency_penalty !== undefined) {
      if (
        params.frequency_penalty < constraints.frequencyPenalty.min ||
        params.frequency_penalty > constraints.frequencyPenalty.max
      ) {
        throw new TammaError(
          'INVALID_FREQUENCY_PENALTY',
          `Frequency penalty must be between ${constraints.frequencyPenalty.min} and ${constraints.frequencyPenalty.max}`,
          {
            frequencyPenalty: params.frequency_penalty,
            min: constraints.frequencyPenalty.min,
            max: constraints.frequencyPenalty.max,
            model: modelId,
          },
          false,
          'medium'
        );
      }
    }

    // Validate presence_penalty
    if (params.presence_penalty !== undefined) {
      if (
        params.presence_penalty < constraints.presencePenalty.min ||
        params.presence_penalty > constraints.presencePenalty.max
      ) {
        throw new TammaError(
          'INVALID_PRESENCE_PENALTY',
          `Presence penalty must be between ${constraints.presencePenalty.min} and ${constraints.presencePenalty.max}`,
          {
            presencePenalty: params.presence_penalty,
            min: constraints.presencePenalty.min,
            max: constraints.presencePenalty.max,
            model: modelId,
          },
          false,
          'medium'
        );
      }
    }
  }
}
```

---

### Subtask 2.2: Implement GPT-4 Turbo model support

**Objective**: Add specific support for GPT-4 Turbo with its extended context window and improved capabilities.

**Implementation Steps**:

1. **Add Turbo-Specific Configuration**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  private getGPT4TurboConfig(): Partial<OpenAI.Chat.ChatCompletionCreateParams> {
    const modelConfig = OPENAI_MODELS['gpt-4-turbo-preview'];

    return {
      model: 'gpt-4-turbo-preview',
      temperature: modelConfig.parameterConstraints.temperature.default,
      max_tokens: modelConfig.parameterConstraints.maxTokens.default,
      top_p: modelConfig.parameterConstraints.topP.default,
      frequency_penalty: modelConfig.parameterConstraints.frequencyPenalty.default,
      presence_penalty: modelConfig.parameterConstraints.presencePenalty.default,
      response_format: { type: 'text' },
    };
  }

  async createGPT4TurboCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-4-turbo-preview');

    const config = { ...this.getGPT4TurboConfig(), ...options };

    // Validate parameters against model constraints
    this.validateParameters('gpt-4-turbo-preview', config);

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createGPT4TurboStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<AsyncIterable<OpenAI.Chat.Completions.ChatCompletionChunk>> {
    await this.validateModel('gpt-4-turbo-preview');

    const config = { ...this.getGPT4TurboConfig(), ...options, stream: true };

    // Validate parameters against model constraints
    this.validateParameters('gpt-4-turbo-preview', config);

    try {
      const stream = await this.client.chat.completions.create({
        ...config,
        messages,
      });

      return stream;
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createGPT4TurboJsonCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-4-turbo-preview');

    const config = {
      ...this.getGPT4TurboConfig(),
      ...options,
      response_format: { type: 'json_object' },
    };

    // Validate parameters against model constraints
    this.validateParameters('gpt-4-turbo-preview', config);

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  // Large context handling for GPT-4 Turbo
  async createGPT4TurboLargeContextCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-4-turbo-preview');

    // Calculate total tokens in messages
    const estimatedTokens = this.estimateMessageTokens(messages);

    if (estimatedTokens > 100000) {
      // Leave buffer for response
      throw new TammaError(
        'CONTEXT_TOO_LARGE',
        'Message context is too large for GPT-4 Turbo. Maximum context is 128K tokens.',
        {
          estimatedTokens,
          maxTokens: 128000,
          model: 'gpt-4-turbo-preview',
        },
        false,
        'medium'
      );
    }

    const config = {
      ...this.getGPT4TurboConfig(),
      ...options,
      max_tokens: Math.min(options.max_tokens || 4096, 128000 - estimatedTokens),
    };

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  private estimateMessageTokens(messages: OpenAI.Chat.ChatCompletionMessageParam[]): number {
    // Simple estimation: ~4 characters per token
    const totalChars = messages.reduce((sum, msg) => {
      const content =
        typeof msg.content === 'string'
          ? msg.content
          : Array.isArray(msg.content)
            ? msg.content.map((c) => (c.type === 'text' ? c.text : '')).join('')
            : '';
      return sum + content.length;
    }, 0);

    return Math.ceil(totalChars / 4);
  }
}
```

---

### Subtask 2.3: Implement GPT-3.5 Turbo model support

**Objective**: Add support for GPT-3.5 Turbo model with cost-effective configuration and optimized parameters.

**Implementation Steps**:

1. **Add GPT-3.5 Specific Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  private getGPT35TurboConfig(): Partial<OpenAI.Chat.ChatCompletionCreateParams> {
    const modelConfig = OPENAI_MODELS['gpt-3.5-turbo'];

    return {
      model: 'gpt-3.5-turbo',
      temperature: modelConfig.parameterConstraints.temperature.default,
      max_tokens: modelConfig.parameterConstraints.maxTokens.default,
      top_p: modelConfig.parameterConstraints.topP.default,
      frequency_penalty: modelConfig.parameterConstraints.frequencyPenalty.default,
      presence_penalty: modelConfig.parameterConstraints.presencePenalty.default,
      response_format: { type: 'text' },
    };
  }

  async createGPT35TurboCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-3.5-turbo');

    const config = { ...this.getGPT35TurboConfig(), ...options };

    // Validate parameters against model constraints
    this.validateParameters('gpt-3.5-turbo', config);

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  async createGPT35TurboStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<AsyncIterable<OpenAI.Chat.Completions.ChatCompletionChunk>> {
    await this.validateModel('gpt-3.5-turbo');

    const config = { ...this.getGPT35TurboConfig(), ...options, stream: true };

    // Validate parameters against model constraints
    this.validateParameters('gpt-3.5-turbo', config);

    try {
      const stream = await this.client.chat.completions.create({
        ...config,
        messages,
      });

      return stream;
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  // Cost-optimized completion for GPT-3.5 Turbo
  async createGPT35TurboOptimizedCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    options: {
      maxCost?: number; // Maximum cost in USD
      prioritizeSpeed?: boolean;
    } & Partial<OpenAI.Chat.ChatCompletionCreateParams> = {}
  ): Promise<OpenAI.Chat.ChatCompletion> {
    await this.validateModel('gpt-3.5-turbo');

    const modelConfig = OPENAI_MODELS['gpt-3.5-turbo'];
    const { maxCost, prioritizeSpeed = false, ...completionOptions } = options;

    let config = { ...this.getGPT35TurboConfig(), ...completionOptions };

    // Apply cost optimization
    if (maxCost) {
      const estimatedInputTokens = this.estimateMessageTokens(messages);
      const maxOutputTokens = Math.floor(
        (maxCost - (estimatedInputTokens / 1000) * modelConfig.inputCostPer1K) /
          (modelConfig.outputCostPer1K / 1000)
      );

      config.max_tokens = Math.min(
        config.max_tokens || modelConfig.parameterConstraints.maxTokens.default,
        Math.max(1, maxOutputTokens)
      );
    }

    // Apply speed optimization
    if (prioritizeSpeed) {
      config.temperature = 0.1; // Lower temperature for faster, more deterministic responses
      config.top_p = 0.8;
      config.max_tokens = Math.min(
        config.max_tokens || 2048,
        1024 // Smaller max tokens for faster responses
      );
    }

    // Validate parameters
    this.validateParameters('gpt-3.5-turbo', config);

    try {
      return await this.client.chat.completions.create({
        ...config,
        messages,
      });
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  // Batch completion for multiple requests
  async createGPT35TurboBatchCompletion(
    requests: Array<{
      messages: OpenAI.Chat.ChatCompletionMessageParam[];
      options?: Partial<OpenAI.Chat.ChatCompletionCreateParams>;
    }>,
    options: {
      maxConcurrency?: number;
      timeout?: number;
    } = {}
  ): Promise<OpenAI.Chat.ChatCompletion[]> {
    await this.validateModel('gpt-3.5-turbo');

    const { maxConcurrency = 5, timeout = 30000 } = options;

    // Process requests in batches
    const results: OpenAI.Chat.ChatCompletion[] = [];

    for (let i = 0; i < requests.length; i += maxConcurrency) {
      const batch = requests.slice(i, i + maxConcurrency);

      const batchPromises = batch.map(async (request) => {
        const config = { ...this.getGPT35TurboConfig(), ...request.options };
        this.validateParameters('gpt-3.5-turbo', config);

        return Promise.race([
          this.client.chat.completions.create({
            ...config,
            messages: request.messages,
          }),
          new Promise<never>((_, reject) =>
            setTimeout(() => reject(new Error('Request timeout')), timeout)
          ),
        ]);
      });

      try {
        const batchResults = await Promise.allSettled(batchPromises);

        for (const result of batchResults) {
          if (result.status === 'fulfilled') {
            results.push(result.value);
          } else {
            throw this.handleOpenAIError(result.reason);
          }
        }
      } catch (error) {
        throw new TammaError(
          'BATCH_COMPLETION_FAILED',
          'Batch completion failed',
          {
            batchSize: batch.length,
            error: error instanceof Error ? error.message : 'Unknown error',
          },
          true,
          'high'
        );
      }
    }

    return results;
  }
}
```

2. **Add Model Comparison Utilities**:

```typescript
// packages/providers/src/utils/model-comparison.ts
import { OpenAIModelConfig, OPENAI_MODELS } from '../types/openai-types';

export interface ModelComparison {
  model: string;
  capabilities: string[];
  costPer1KTokens: number;
  maxTokens: number;
  contextWindow: number;
  speed: 'fast' | 'medium' | 'slow';
  quality: 'basic' | 'standard' | 'premium';
  recommendedFor: string[];
}

export class ModelComparison {
  static compareModels(modelIds: string[]): ModelComparison[] {
    return modelIds.map((modelId) => {
      const config = OPENAI_MODELS[modelId];
      if (!config) {
        throw new Error(`Unknown model: ${modelId}`);
      }

      return {
        model: modelId,
        capabilities: config.capabilities,
        costPer1KTokens: config.inputCostPer1K + config.outputCostPer1K,
        maxTokens: config.maxTokens,
        contextWindow: config.contextWindow,
        speed: this.getModelSpeed(modelId),
        quality: this.getModelQuality(modelId),
        recommendedFor: this.getRecommendedUseCases(modelId),
      };
    });
  }

  static getBestModelForTask(
    task: 'code-generation' | 'text-generation' | 'function-calling' | 'json-mode',
    constraints: {
      maxCost?: number;
      minQuality?: 'basic' | 'standard' | 'premium';
      maxLatency?: number;
      requiresLargeContext?: boolean;
    } = {}
  ): string {
    const availableModels = Object.keys(OPENAI_MODELS).filter((modelId) => {
      const config = OPENAI_MODELS[modelId];

      // Filter by task requirements
      switch (task) {
        case 'function-calling':
          return config.supportsFunctions;
        case 'json-mode':
          return config.supportsJsonMode;
        default:
          return true;
      }
    });

    // Score models based on constraints
    const scoredModels = availableModels.map((modelId) => {
      const config = OPENAI_MODELS[modelId];
      let score = 0;

      // Cost scoring (lower is better)
      if (constraints.maxCost) {
        const avgCost = (config.inputCostPer1K + config.outputCostPer1K) / 2;
        if (avgCost <= constraints.maxCost) {
          score += 10;
        } else {
          score -= 5;
        }
      }

      // Quality scoring
      if (constraints.minQuality) {
        const quality = this.getModelQuality(modelId);
        const qualityScores = { basic: 1, standard: 2, premium: 3 };
        if (qualityScores[quality] >= qualityScores[constraints.minQuality]) {
          score += 5;
        }
      }

      // Context window scoring
      if (constraints.requiresLargeContext && config.contextWindow >= 100000) {
        score += 10;
      }

      return { modelId, score };
    });

    // Return model with highest score
    scoredModels.sort((a, b) => b.score - a.score);
    return scoredModels[0]?.modelId || 'gpt-3.5-turbo';
  }

  private static getModelSpeed(modelId: string): 'fast' | 'medium' | 'slow' {
    const speedMap: Record<string, 'fast' | 'medium' | 'slow'> = {
      'gpt-3.5-turbo': 'fast',
      'gpt-4-turbo-preview': 'medium',
      'gpt-4': 'slow',
    };
    return speedMap[modelId] || 'medium';
  }

  private static getModelQuality(modelId: string): 'basic' | 'standard' | 'premium' {
    const qualityMap: Record<string, 'basic' | 'standard' | 'premium'> = {
      'gpt-3.5-turbo': 'basic',
      'gpt-4-turbo-preview': 'standard',
      'gpt-4': 'premium',
    };
    return qualityMap[modelId] || 'standard';
  }

  private static getRecommendedUseCases(modelId: string): string[] {
    const useCases: Record<string, string[]> = {
      'gpt-3.5-turbo': [
        'Simple text generation',
        'Basic code completion',
        'Chat applications',
        'Content drafting',
        'Cost-sensitive applications',
      ],
      'gpt-4-turbo-preview': [
        'Complex reasoning',
        'Code generation',
        'Data analysis',
        'Function calling',
        'Large context tasks',
      ],
      'gpt-4': [
        'Advanced reasoning',
        'Complex problem solving',
        'Research assistance',
        'High-stakes applications',
        'Premium quality requirements',
      ],
    };
    return useCases[modelId] || [];
  }
}
```

**Files to Create**:

- `packages/providers/src/types/openai-types.ts`
- `packages/providers/src/utils/model-comparison.ts`

## Testing Requirements

### Unit Tests

1. **Model Configuration Tests**:

```typescript
// tests/providers/openai-models.test.ts
describe('OpenAI Model Configuration', () => {
  test('should have correct GPT-4 configuration', () => {
    const config = OPENAI_MODELS['gpt-4'];
    expect(config.id).toBe('gpt-4');
    expect(config.maxTokens).toBe(8192);
    expect(config.contextWindow).toBe(8192);
    expect(config.supportsStreaming).toBe(true);
    expect(config.supportsFunctions).toBe(true);
  });

  test('should have correct GPT-4 Turbo configuration', () => {
    const config = OPENAI_MODELS['gpt-4-turbo-preview'];
    expect(config.contextWindow).toBe(128000);
    expect(config.inputCostPer1K).toBe(0.01);
    expect(config.capabilities).toContain('large-context');
  });

  test('should have correct GPT-3.5 Turbo configuration', () => {
    const config = OPENAI_MODELS['gpt-3.5-turbo'];
    expect(config.inputCostPer1K).toBe(0.0015);
    expect(config.outputCostPer1K).toBe(0.002);
    expect(config.supportsStreaming).toBe(true);
  });
});
```

2. **Model Support Tests**:

```typescript
// tests/providers/openai-model-support.test.ts
describe('OpenAI Model Support', () => {
  let provider: OpenAIProvider;

  beforeAll(() => {
    provider = new OpenAIProvider({
      apiKey: 'sk-test-key',
    });
  });

  test('should correctly identify supported models', () => {
    expect(provider.isModelSupported('gpt-4')).toBe(true);
    expect(provider.isModelSupported('gpt-4-turbo-preview')).toBe(true);
    expect(provider.isModelSupported('gpt-3.5-turbo')).toBe(true);
    expect(provider.isModelSupported('invalid-model')).toBe(false);
  });

  test('should get model configuration', () => {
    const config = provider.getModelConfig('gpt-4');
    expect(config).toBeDefined();
    expect(config?.id).toBe('gpt-4');
    expect(config?.maxTokens).toBe(8192);
  });

  test('should filter models by capability', () => {
    const codeModels = provider.getModelsByCapability('code-generation');
    expect(codeModels).toHaveLength(3);
    expect(codeModels.every((model) => model.capabilities.includes('code-generation'))).toBe(true);
  });
});
```

3. **Parameter Validation Tests**:

```typescript
// tests/providers/openai-parameter-validation.test.ts
describe('OpenAI Parameter Validation', () => {
  let provider: OpenAIProvider;

  beforeAll(() => {
    provider = new OpenAIProvider({
      apiKey: 'sk-test-key',
    });
  });

  test('should validate temperature parameters', () => {
    expect(() => provider.validateParameters('gpt-4', { temperature: -1 })).toThrow();
    expect(() => provider.validateParameters('gpt-4', { temperature: 3 })).toThrow();
    expect(() => provider.validateParameters('gpt-4', { temperature: 1.5 })).not.toThrow();
  });

  test('should validate max_tokens parameters', () => {
    expect(() => provider.validateParameters('gpt-4', { max_tokens: 0 })).toThrow();
    expect(() => provider.validateParameters('gpt-4', { max_tokens: 10000 })).toThrow();
    expect(() => provider.validateParameters('gpt-4', { max_tokens: 4096 })).not.toThrow();
  });
});
```

### Integration Tests

1. **Model Completion Tests**:

```typescript
// tests/providers/openai-completion.test.ts
describe('OpenAI Model Completions', () => {
  let provider: OpenAIProvider;

  beforeAll(async () => {
    provider = new OpenAIProvider({
      apiKey: process.env.OPENAI_TEST_API_KEY || '',
      timeout: 30000,
    });
    await provider.initialize();
  });

  test('should create GPT-4 completion', async () => {
    const messages = [{ role: 'user', content: 'Hello, world!' }];
    const completion = await provider.createGPT4Completion(messages);

    expect(completion.id).toBeDefined();
    expect(completion.choices).toHaveLength(1);
    expect(completion.choices[0].message?.content).toBeDefined();
    expect(completion.model).toBe('gpt-4');
  });

  test('should create GPT-4 Turbo completion', async () => {
    const messages = [{ role: 'user', content: 'Explain quantum computing' }];
    const completion = await provider.createGPT4TurboCompletion(messages);

    expect(completion.id).toBeDefined();
    expect(completion.choices[0].message?.content).toBeDefined();
    expect(completion.model).toBe('gpt-4-turbo-preview');
  });

  test('should create GPT-3.5 Turbo completion', async () => {
    const messages = [{ role: 'user', content: 'Write a simple function' }];
    const completion = await provider.createGPT35TurboCompletion(messages);

    expect(completion.id).toBeDefined();
    expect(completion.choices[0].message?.content).toBeDefined();
    expect(completion.model).toBe('gpt-3.5-turbo');
  });
});
```

2. **Streaming Tests**:

```typescript
// tests/providers/openai-streaming.test.ts
describe('OpenAI Streaming Completions', () => {
  let provider: OpenAIProvider;

  beforeAll(async () => {
    provider = new OpenAIProvider({
      apiKey: process.env.OPENAI_TEST_API_KEY || '',
      timeout: 30000,
    });
    await provider.initialize();
  });

  test('should stream GPT-4 Turbo completion', async () => {
    const messages = [{ role: 'user', content: 'Count to 10' }];
    const stream = await provider.createGPT4TurboStreamingCompletion(messages);

    const chunks: string[] = [];
    for await (const chunk of stream) {
      const content = chunk.choices[0]?.delta?.content;
      if (content) {
        chunks.push(content);
      }
    }

    expect(chunks.length).toBeGreaterThan(0);
    const fullContent = chunks.join('');
    expect(fullContent).toContain('10');
  });
});
```

## Security Considerations

1. **Model Access Control**:
   - Validate model IDs against supported models
   - Check API access for specific models
   - Implement model-specific rate limiting

2. **Parameter Validation**:
   - Validate all parameters against model constraints
   - Sanitize user inputs to prevent injection
   - Implement reasonable defaults for all parameters

3. **Cost Management**:
   - Provide cost estimation before requests
   - Implement spending limits and alerts
   - Track usage per model and user

## Dependencies

- **Runtime Dependencies**:
  - `openai`: Official OpenAI SDK
  - `@shared/errors`: Error handling utilities

- **Development Dependencies**:
  - `vitest`: Testing framework
  - `@types/openai`: TypeScript definitions

## Notes

- GPT-4 Turbo provides the best balance of cost and capability for most use cases
- GPT-3.5 Turbo is ideal for cost-sensitive applications
- GPT-4 is recommended for complex reasoning tasks
- All models support streaming and function calling
- Parameter validation should happen before every API call
- Consider implementing model selection based on task requirements and cost constraints
