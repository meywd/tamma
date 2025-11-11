# Task 3: Create Standardized Models

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 3 - Standardized request/response models for code generation tasks  
**Status**: Ready for Development

## Overview

This task involves creating comprehensive, standardized models for all AI provider communications. These models will ensure consistency across different provider implementations while supporting the full range of features needed for code generation tasks, including streaming, function calling, and multi-modal content.

## Subtasks

### Subtask 3.1: Define ChatCompletionRequest Interface

**Objective**: Create a standardized request interface that supports all common AI provider features.

**Implementation Details**:

1. **File Location**: `packages/providers/src/models/ChatCompletionRequest.ts`

2. **Core Request Interface**:

   ```typescript
   export interface ChatCompletionRequest {
     // Required Fields
     messages: Message[];

     // Model Selection
     model?: string;

     // Generation Parameters
     maxTokens?: number;
     temperature?: number;
     topP?: number;
     frequencyPenalty?: number;
     presencePenalty?: number;
     stop?: string | string[];

     // Streaming Configuration
     stream?: boolean;
     streamOptions?: StreamOptions;

     // Function/Tool Calling
     tools?: Tool[];
     toolChoice?: ToolChoice;
     parallelToolCalls?: boolean;

     // Response Format
     responseFormat?: ResponseFormat;

     // Metadata and Context
     metadata?: RequestMetadata;

     // Provider-Specific Options
     providerOptions?: Record<string, unknown>;
   }
   ```

3. **Message Interface**:

   ```typescript
   export interface Message {
     role: MessageRole;
     content: MessageContent;
     name?: string;
     toolCalls?: ToolCall[];
     toolCallId?: string;
   }

   export type MessageRole = 'system' | 'user' | 'assistant' | 'tool' | 'function';

   export type MessageContent = string | MessageContentPart[];

   export interface MessageContentPart {
     type: 'text' | 'image_url' | 'image' | 'audio' | 'video' | 'file';

     // Text content
     text?: string;

     // Image content
     image_url?: {
       url: string;
       detail?: 'low' | 'high' | 'auto';
     };
     image?: {
       data: string; // base64
       media_type: string;
     };

     // Audio content
     audio?: {
       data: string; // base64
       format: string;
     };

     // Video content
     video?: {
       data: string; // base64
       format: string;
     };

     // File content
     file?: {
       data: string; // base64
       filename: string;
       media_type: string;
     };
   }
   ```

4. **Tool/Function Calling Support**:

   ```typescript
   export interface Tool {
     type: 'function';
     function: ToolFunction;
   }

   export interface ToolFunction {
     name: string;
     description?: string;
     parameters?: ToolParameters;
     strict?: boolean;
   }

   export interface ToolParameters {
     type: 'object' | 'string' | 'number' | 'boolean' | 'array';
     properties?: Record<string, ToolParameterProperty>;
     required?: string[];
     additionalProperties?: boolean | ToolParameters;
   }

   export interface ToolParameterProperty {
     type: 'object' | 'string' | 'number' | 'boolean' | 'array' | 'null';
     description?: string;
     enum?: unknown[];
     items?: ToolParameters;
     properties?: Record<string, ToolParameterProperty>;
     required?: string[];
     minimum?: number;
     maximum?: number;
     pattern?: string;
     format?: string;
   }

   export type ToolChoice = 'none' | 'auto' | 'required' | ToolChoiceFunction;

   export interface ToolChoiceFunction {
     type: 'function';
     function: {
       name: string;
     };
   }
   ```

5. **Streaming Options**:

   ```typescript
   export interface StreamOptions {
     includeUsage?: boolean;
     includeToolCalls?: boolean;
     includeReasoning?: boolean;
     chunkSize?: number;
     heartbeatInterval?: number;
   }
   ```

6. **Response Format Configuration**:

   ```typescript
   export interface ResponseFormat {
     type: 'text' | 'json_object' | 'json_schema';
     jsonSchema?: JSONSchema;
   }

   export interface JSONSchema {
     name: string;
     description?: string;
     schema: Record<string, unknown>;
     strict?: boolean;
   }
   ```

7. **Request Metadata**:

   ```typescript
   export interface RequestMetadata {
     requestId?: string;
     userId?: string;
     sessionId?: string;
     conversationId?: string;
     timestamp?: string;
     tags?: Record<string, string>;
     priority?: 'low' | 'normal' | 'high' | 'urgent';
     costLimit?: number;
     timeout?: number;
     retryPolicy?: RetryPolicy;
   }

   export interface RetryPolicy {
     maxAttempts: number;
     baseDelay: number;
     maxDelay: number;
     backoffMultiplier: number;
     retryableErrors?: string[];
   }
   ```

### Subtask 3.2: Define ChatCompletionResponse Interface

**Objective**: Create standardized response interfaces for both streaming and non-streaming scenarios.

**Implementation Details**:

1. **File Location**: `packages/providers/src/models/ChatCompletionResponse.ts`

2. **Non-Streaming Response Interface**:

   ```typescript
   export interface ChatCompletionResponse {
     id: string;
     object: 'chat.completion';
     created: number;
     model: string;
     choices: CompletionChoice[];
     usage?: TokenUsage;
     systemFingerprint?: string;
     finishReason?: FinishReason;
     metadata?: ResponseMetadata;
   }

   export interface CompletionChoice {
     index: number;
     message: Message;
     finishReason: FinishReason;
     logprobs?: LogProbs;
   }

   export type FinishReason =
     | 'stop'
     | 'length'
     | 'tool_calls'
     | 'content_filter'
     | 'function_call'
     | 'max_tokens'
     | 'timeout'
     | 'error';
   ```

3. **Streaming Response Interface**:

   ```typescript
   export interface ChatCompletionChunk {
     id: string;
     object: 'chat.completion.chunk';
     created: number;
     model: string;
     choices: ChunkChoice[];
     usage?: TokenUsage;
     systemFingerprint?: string;
     metadata?: ResponseMetadata;
   }

   export interface ChunkChoice {
     index: number;
     delta: ChunkDelta;
     finishReason?: FinishReason;
     logprobs?: LogProbs;
   }

   export interface ChunkDelta {
     role?: MessageRole;
     content?: string;
     toolCalls?: ToolCallDelta[];
     reasoning?: string;
   }

   export interface ToolCallDelta {
     index?: number;
     id?: string;
     type?: 'function';
     function?: {
       name?: string;
       arguments?: string;
     };
   }
   ```

4. **Token Usage Information**:

   ```typescript
   export interface TokenUsage {
     promptTokens: number;
     completionTokens: number;
     totalTokens: number;
     promptTokensDetails?: PromptTokensDetails;
     completionTokensDetails?: CompletionTokensDetails;
   }

   export interface PromptTokensDetails {
     cachedTokens?: number;
     audioTokens?: number;
     imageTokens?: number;
     textTokens?: number;
   }

   export interface CompletionTokensDetails {
     reasoningTokens?: number;
     audioTokens?: number;
     acceptedPredictionTokens?: number;
     rejectedPredictionTokens?: number;
   }
   ```

5. **Log Probabilities**:

   ```typescript
   export interface LogProbs {
     content?: TokenLogProb[];
     refusal?: TokenLogProb[];
   }

   export interface TokenLogProb {
     token: string;
     logprob: number;
     bytes?: number[];
     topLogprobs?: TopLogProb[];
   }

   export interface TopLogProb {
     token: string;
     logprob: number;
     bytes?: number[];
   }
   ```

6. **Response Metadata**:

   ```typescript
   export interface ResponseMetadata {
     requestId?: string;
     processingTime?: number;
     providerId?: string;
     modelId?: string;
     cached?: boolean;
     rateLimit?: RateLimitInfo;
     cost?: CostInfo;
     warnings?: Warning[];
     debug?: DebugInfo;
   }

   export interface RateLimitInfo {
     requestsRemaining?: number;
     tokensRemaining?: number;
     resetTime?: number;
     limitType?: 'requests' | 'tokens';
   }

   export interface CostInfo {
     inputCost?: number;
     outputCost?: number;
     totalCost?: number;
     currency?: string;
     billingUnit?: 'tokens' | 'requests' | 'minutes';
   }

   export interface Warning {
     code: string;
     message: string;
     type: 'info' | 'warning' | 'error';
     category?: 'rate_limit' | 'content_filter' | 'model_limit' | 'deprecated';
   }

   export interface DebugInfo {
     providerResponse?: unknown;
     requestHeaders?: Record<string, string>;
     responseHeaders?: Record<string, string>;
     timing?: TimingInfo;
   }

   export interface TimingInfo {
     queueTime?: number;
     processingTime?: number;
     networkTime?: number;
     totalTime?: number;
   }
   ```

### Subtask 3.3: Create Token Usage Models

**Objective**: Implement comprehensive token usage tracking and cost calculation models.

**Implementation Details**:

1. **File Location**: `packages/providers/src/models/TokenUsage.ts`

2. **Core Token Usage Models**:

   ```typescript
   export interface TokenUsage {
     // Basic token counts
     promptTokens: number;
     completionTokens: number;
     totalTokens: number;

     // Detailed breakdown
     promptTokensDetails?: PromptTokensDetails;
     completionTokensDetails?: CompletionTokensDetails;

     // Cost information
     cost?: TokenCost;

     // Efficiency metrics
     efficiency?: TokenEfficiency;

     // Provider-specific data
     providerData?: Record<string, unknown>;
   }

   export interface PromptTokensDetails {
     cachedTokens?: number;
     audioTokens?: number;
     imageTokens?: number;
     videoTokens?: number;
     fileTokens?: number;
     textTokens?: number;
     toolTokens?: number;
   }

   export interface CompletionTokensDetails {
     reasoningTokens?: number;
     audioTokens?: number;
     imageTokens?: number;
     toolTokens?: number;
     acceptedPredictionTokens?: number;
     rejectedPredictionTokens?: number;
   }
   ```

3. **Cost Calculation Models**:

   ```typescript
   export interface TokenCost {
     inputCost: number;
     outputCost: number;
     totalCost: number;
     currency: string;
     billingUnit: 'tokens' | 'requests' | 'minutes';
     pricingModel: PricingModel;
     discounts?: Discount[];
     taxes?: TaxInfo;
   }

   export interface PricingModel {
     type: 'per_token' | 'per_request' | 'per_minute' | 'tiered';
     inputTokenPrice: number;
     outputTokenPrice: number;
     currency: string;
     tiers?: PricingTier[];
     minimumCharge?: number;
   }

   export interface PricingTier {
     minTokens: number;
     maxTokens?: number;
     inputPrice: number;
     outputPrice: number;
   }

   export interface Discount {
     type: 'percentage' | 'fixed' | 'volume';
     value: number;
     condition?: string;
     validUntil?: string;
   }

   export interface TaxInfo {
     rate: number;
     amount: number;
     jurisdiction: string;
     included: boolean;
   }
   ```

4. **Efficiency Metrics**:

   ```typescript
   export interface TokenEfficiency {
     // Compression metrics
     compressionRatio?: number;
     averageTokenLength?: number;

     // Quality metrics
     coherenceScore?: number;
     relevanceScore?: number;

     // Performance metrics
     tokensPerSecond?: number;
     costPerToken?: number;

     // Utilization metrics
     contextUtilization?: number;
     modelCapacityUtilization?: number;
   }
   ```

5. **Usage Aggregation Models**:

   ```typescript
   export interface AggregatedTokenUsage {
     period: UsagePeriod;
     totalUsage: TokenUsage;
     usageByModel: Record<string, TokenUsage>;
     usageByUser: Record<string, TokenUsage>;
     usageByFeature: Record<string, TokenUsage>;
     trends?: UsageTrend[];
     forecasts?: UsageForecast[];
   }

   export interface UsagePeriod {
     start: string; // ISO 8601
     end: string; // ISO 8601
     granularity: 'minute' | 'hour' | 'day' | 'week' | 'month';
   }

   export interface UsageTrend {
     metric: string;
     direction: 'increasing' | 'decreasing' | 'stable';
     changeRate: number;
     confidence: number;
   }

   export interface UsageForecast {
     metric: string;
     predictedValue: number;
     confidenceInterval: {
       lower: number;
       upper: number;
     };
     timeHorizon: string;
   }
   ```

6. **Token Usage Calculator**:
   ```typescript
   export class TokenUsageCalculator {
     // Cost calculation
     static calculateCost(usage: TokenUsage, pricing: PricingModel): TokenCost;
     static calculateCostWithDiscounts(
       usage: TokenUsage,
       pricing: PricingModel,
       discounts: Discount[]
     ): TokenCost;

     // Token counting
     static countTokens(text: string, model?: string): number;
     static countImageTokens(image: ImageContent, model?: string): number;
     static countAudioTokens(audio: AudioContent, model?: string): number;

     // Efficiency metrics
     static calculateEfficiency(
       usage: TokenUsage,
       response: ChatCompletionResponse
     ): TokenEfficiency;
     static calculateCompressionRatio(original: string, compressed: string): number;

     // Aggregation
     static aggregateUsage(usages: TokenUsage[]): TokenUsage;
     static aggregateUsageByPeriod(usages: TokenUsage[], period: UsagePeriod): AggregatedTokenUsage;
   }
   ```

## Technical Requirements

### Type Safety Requirements

- All models must use TypeScript strict mode
- Union types should be used for extensible enums
- Generic types should be used where appropriate
- All optional fields must be clearly marked

### Serialization Requirements

- All models must be JSON serializable
- Date fields should use ISO 8601 string format
- Binary data should use base64 encoding
- Circular references must be avoided

### Validation Requirements

- All models should have validation schemas
- Required fields must be validated
- Type validation should be performed
- Range validation for numeric fields

### Performance Requirements

- Models should be memory efficient
- Large payloads should support streaming
- Token counting should be optimized
- Cost calculations should be cached

## Testing Strategy

### Unit Tests

```typescript
describe('ChatCompletionRequest', () => {
  describe('validation', () => {
    it('should validate required fields');
    it('should reject invalid message formats');
    it('should validate tool definitions');
    it('should validate parameter schemas');
  });

  describe('serialization', () => {
    it('should serialize to JSON correctly');
    it('should deserialize from JSON correctly');
    it('should handle circular references');
  });
});

describe('TokenUsage', () => {
  describe('calculation', () => {
    it('should calculate total tokens correctly');
    it('should calculate costs accurately');
    it('should apply discounts correctly');
    it('should handle different pricing models');
  });

  describe('aggregation', () => {
    it('should aggregate multiple usage records');
    it('should calculate trends correctly');
    it('should generate forecasts');
  });
});
```

### Integration Tests

- Test models with real provider responses
- Test serialization/deserialization with large payloads
- Test token counting accuracy across different models
- Test cost calculation with various pricing models

### Performance Tests

- Benchmark token counting performance
- Test memory usage with large models
- Measure serialization/deserialization speed
- Test aggregation performance with many records

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared utilities and validation
- Task 1 output - Base interfaces and types

### External Dependencies

- TypeScript 5.7+ - Type safety
- JSON Schema - Validation schemas
- tiktoken - Token counting (for OpenAI models)
- date-fns - Date manipulation

## Deliverables

1. **Request Models**: `packages/providers/src/models/ChatCompletionRequest.ts`
2. **Response Models**: `packages/providers/src/models/ChatCompletionResponse.ts`
3. **Token Usage Models**: `packages/providers/src/models/TokenUsage.ts`
4. **Supporting Types**: `packages/providers/src/models/index.ts`
5. **Validation Schemas**: `packages/providers/src/models/schemas/`
6. **Token Calculator**: `packages/providers/src/models/TokenUsageCalculator.ts`
7. **Unit Tests**: `packages/providers/src/models/__tests__/`
8. **Integration Tests**: `packages/providers/src/models/__integration__/`

## Acceptance Criteria Verification

- [ ] All request models support required AI provider features
- [ ] Response models handle both streaming and non-streaming scenarios
- [ ] Token usage models provide accurate cost calculations
- [ ] All models are JSON serializable and deserializable
- [ ] Validation schemas catch invalid data
- [ ] Performance requirements are met
- [ ] Type safety is maintained throughout
- [ ] Documentation is comprehensive

## Implementation Notes

### Model Versioning

All models should include version information for backward compatibility:

```typescript
interface ModelVersion {
  version: string;
  compatibleVersions: string[];
  deprecatedFields: string[];
  newFields: string[];
}
```

### Extensibility Patterns

Use extensible patterns for future provider features:

```typescript
interface ExtensibleModel {
  // Standard fields
  standardField: string;

  // Extensible provider-specific fields
  [providerName: string]: unknown;
}
```

### Validation Strategy

Implement comprehensive validation using JSON Schema:

```typescript
const requestSchema = {
  type: 'object',
  required: ['messages'],
  properties: {
    messages: {
      type: 'array',
      items: { $ref: '#/definitions/Message' },
    },
    // ... other properties
  },
  definitions: {
    Message: {
      /* message schema */
    },
  },
};
```

### Error Handling

Models should include validation error information:

```typescript
interface ValidationError {
  field: string;
  code: string;
  message: string;
  value: unknown;
  allowedValues?: unknown[];
}
```

## Next Steps

After completing this task:

1. Move to Task 4: Implement Error Handling
2. Use these models in provider implementations
3. Integrate with token usage tracking system

## Risk Mitigation

### Technical Risks

- **Model Bloat**: Keep models focused and avoid unnecessary complexity
- **Performance Impact**: Optimize for memory and CPU efficiency
- **Compatibility Issues**: Ensure models work with all target providers

### Mitigation Strategies

- Regular model reviews and refactoring
- Performance profiling and optimization
- Comprehensive testing with all provider types
- Version compatibility testing
  ```

  ```
