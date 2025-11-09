# Task 2: Streaming Implementation

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: High

## Overview

Implement streaming response handling for the Anthropic Claude provider using the AsyncIterable<MessageChunk> pattern. This enables real-time response streaming for better user experience and efficient token usage tracking.

## Detailed Implementation Plan

### Subtask 2.1: Implement Streaming Response Handling

**File**: `src/providers/anthropic/streaming/stream-handler.ts`

```typescript
import { AsyncIterable, AsyncGenerator } from '@anthropic-ai/sdk/streaming';
import type { MessageRequest, MessageChunk, StreamEvent } from '@tamma/shared/contracts';
import type {
  AnthropicMessageRequest,
  AnthropicResponse,
  AnthropicContentBlock,
  AnthropicUsage,
} from '../types/anthropic-types';
import { logger } from '@tamma/observability';
import { AnthropicError } from '../errors/anthropic-error';
import { TokenCounter } from '../utils/token-counter';

export class StreamHandler {
  private readonly tokenCounter: TokenCounter;
  private startTime: number = 0;
  private inputTokens: number = 0;
  private outputTokens: number = 0;

  constructor(tokenCounter: TokenCounter) {
    this.tokenCounter = tokenCounter;
  }

  async *handleStream(
    anthropicStream: AsyncIterable<AnthropicResponse>,
    request: MessageRequest
  ): AsyncGenerator<MessageChunk> {
    this.startTime = Date.now();
    this.inputTokens = await this.tokenCounter.countInputTokens(request);

    logger.debug('Starting stream processing', {
      model: request.model,
      inputTokens: this.inputTokens,
    });

    try {
      let accumulatedContent = '';
      let toolCalls: any[] = [];
      let finishReason: string | null = null;
      let usage: AnthropicUsage | null = null;

      for await (const chunk of anthropicStream) {
        const processedChunk = this.processChunk(chunk);

        if (processedChunk.content) {
          accumulatedContent += processedChunk.content;
        }

        if (processedChunk.toolCalls) {
          toolCalls.push(...processedChunk.toolCalls);
        }

        if (chunk.usage) {
          usage = chunk.usage;
          this.outputTokens = chunk.usage.output_tokens;
        }

        if (chunk.stop_reason) {
          finishReason = this.mapStopReason(chunk.stop_reason);
        }

        // Emit chunk to consumer
        yield {
          id: chunk.id,
          content: processedChunk.content || '',
          toolCalls: processedChunk.toolCalls || [],
          finishReason: finishReason || undefined,
          usage: usage
            ? {
                inputTokens: usage.input_tokens,
                outputTokens: usage.output_tokens,
                totalTokens: usage.input_tokens + usage.output_tokens,
              }
            : undefined,
          metadata: {
            model: request.model,
            timestamp: new Date().toISOString(),
            latency: Date.now() - this.startTime,
          },
        };
      }

      // Log completion metrics
      logger.info('Stream completed', {
        model: request.model,
        inputTokens: this.inputTokens,
        outputTokens: this.outputTokens,
        totalTokens: this.inputTokens + this.outputTokens,
        duration: Date.now() - this.startTime,
        finishReason,
      });
    } catch (error) {
      logger.error('Stream processing failed', {
        error: error.message,
        model: request.model,
        duration: Date.now() - this.startTime,
      });

      throw new AnthropicError('STREAM_PROCESSING_FAILED', 'Failed to process Anthropic stream', {
        cause: error,
        model: request.model,
      });
    }
  }

  private processChunk(chunk: AnthropicResponse): {
    content?: string;
    toolCalls?: any[];
  } {
    const result: { content?: string; toolCalls?: any[] } = {};

    for (const contentBlock of chunk.content) {
      switch (contentBlock.type) {
        case 'text':
          result.content = contentBlock.text || '';
          break;

        case 'tool_use':
          if (!result.toolCalls) {
            result.toolCalls = [];
          }
          result.toolCalls.push({
            id: contentBlock.id!,
            name: contentBlock.name!,
            arguments: JSON.stringify(contentBlock.input || {}),
          });
          break;

        case 'tool_result':
          // Tool results are handled in response processing
          break;

        default:
          logger.warn('Unknown content block type', {
            type: contentBlock.type,
            chunkId: chunk.id,
          });
      }
    }

    return result;
  }

  private mapStopReason(reason: string): string {
    const reasonMap: Record<string, string> = {
      end_turn: 'stop',
      max_tokens: 'length',
      stop_sequence: 'stop',
      tool_use: 'tool_calls',
    };

    return reasonMap[reason] || 'unknown';
  }

  getMetrics(): {
    inputTokens: number;
    outputTokens: number;
    totalTokens: number;
    duration: number;
  } {
    return {
      inputTokens: this.inputTokens,
      outputTokens: this.outputTokens,
      totalTokens: this.inputTokens + this.outputTokens,
      duration: Date.now() - this.startTime,
    };
  }
}
```

### Subtask 2.2: Create AsyncIterable<MessageChunk> Pattern

**File**: `src/providers/anthropic/streaming/message-stream.ts`

```typescript
import type { AsyncIterable, AsyncGenerator } from '@anthropic-ai/sdk';
import type { MessageRequest, MessageChunk, StreamOptions } from '@tamma/shared/contracts';
import type { Anthropic } from '@anthropic-ai/sdk';
import { StreamHandler } from './stream-handler';
import { TokenCounter } from '../utils/token-counter';
import { logger } from '@tamma/observability';
import { AnthropicError } from '../errors/anthropic-error';

export class MessageStream {
  private readonly streamHandler: StreamHandler;
  private readonly tokenCounter: TokenCounter;

  constructor() {
    this.tokenCounter = new TokenCounter();
    this.streamHandler = new StreamHandler(this.tokenCounter);
  }

  async createStream(
    client: Anthropic,
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    return this.createMessageStream(client, request, options);
  }

  private async createMessageStream(
    client: Anthropic,
    request: MessageRequest,
    options: StreamOptions
  ): Promise<AsyncIterable<MessageChunk>> {
    const anthropicRequest = this.convertToAnthropicRequest(request, options);

    logger.debug('Creating Anthropic message stream', {
      model: request.model,
      stream: true,
      maxTokens: anthropicRequest.max_tokens,
    });

    try {
      const stream = await client.messages.create({
        ...anthropicRequest,
        stream: true,
      });

      return this.streamHandler.handleStream(stream, request);
    } catch (error) {
      logger.error('Failed to create message stream', {
        error: error.message,
        model: request.model,
        request: anthropicRequest,
      });

      throw new AnthropicError(
        'STREAM_CREATION_FAILED',
        'Failed to create Anthropic message stream',
        { cause: error, model: request.model }
      );
    }
  }

  private convertToAnthropicRequest(request: MessageRequest, options: StreamOptions): any {
    const modelConfig = this.getModelConfig(request.model);

    return {
      model: request.model,
      messages: this.convertMessages(request.messages),
      max_tokens: request.maxTokens || modelConfig.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      stop_sequences: request.stopSequences,
      system: request.system,
      tools: request.tools ? this.convertTools(request.tools) : undefined,
      stream: true,
    };
  }

  private convertMessages(messages: any[]): any[] {
    return messages.map((msg) => ({
      role: msg.role,
      content: msg.content,
    }));
  }

  private convertTools(tools: any[]): any[] {
    return tools.map((tool) => ({
      name: tool.name,
      description: tool.description,
      input_schema: tool.parameters,
    }));
  }

  private getModelConfig(model: string): any {
    // Import model config to get default max tokens
    const { CLAUDE_MODELS } = require('../models/claude-models');
    return CLAUDE_MODELS[model] || CLAUDE_MODELS['claude-3-5-sonnet-20241022'];
  }
}
```

### Subtask 2.3: Handle Partial Responses and Chunk Aggregation

**File**: `src/providers/anthropic/streaming/chunk-aggregator.ts`

```typescript
import type { MessageChunk } from '@tamma/shared/contracts';
import { logger } from '@tamma/observability';

export interface AggregatedResponse {
  id: string;
  content: string;
  toolCalls: any[];
  finishReason?: string;
  usage: {
    inputTokens: number;
    outputTokens: number;
    totalTokens: number;
  };
  metadata: {
    model: string;
    timestamp: string;
    latency: number;
    chunkCount: number;
  };
}

export class ChunkAggregator {
  private chunks: MessageChunk[] = [];
  private startTime: number = 0;

  start(): void {
    this.chunks = [];
    this.startTime = Date.now();
  }

  addChunk(chunk: MessageChunk): void {
    this.chunks.push(chunk);

    logger.debug('Chunk added', {
      chunkId: chunk.id,
      contentLength: chunk.content?.length || 0,
      toolCallsCount: chunk.toolCalls?.length || 0,
      finishReason: chunk.finishReason,
    });
  }

  getAggregatedResponse(): AggregatedResponse | null {
    if (this.chunks.length === 0) {
      return null;
    }

    const firstChunk = this.chunks[0];
    const lastChunk = this.chunks[this.chunks.length - 1];

    // Aggregate content
    const content = this.chunks.map((chunk) => chunk.content || '').join('');

    // Aggregate tool calls
    const toolCalls = this.chunks
      .flatMap((chunk) => chunk.toolCalls || [])
      .filter((call, index, arr) => arr.findIndex((c) => c.id === call.id) === index); // Remove duplicates by ID

    // Get final usage from last chunk that has it
    const usageChunk = this.chunks.reverse().find((chunk) => chunk.usage);

    const usage = usageChunk?.usage || {
      inputTokens: 0,
      outputTokens: 0,
      totalTokens: 0,
    };

    return {
      id: firstChunk.id,
      content,
      toolCalls,
      finishReason: lastChunk.finishReason,
      usage,
      metadata: {
        model: firstChunk.metadata?.model || 'unknown',
        timestamp: firstChunk.metadata?.timestamp || new Date().toISOString(),
        latency: Date.now() - this.startTime,
        chunkCount: this.chunks.length,
      },
    };
  }

  getPartialResponse(): AggregatedResponse | null {
    if (this.chunks.length === 0) {
      return null;
    }

    const firstChunk = this.chunks[0];

    // Aggregate content so far
    const content = this.chunks.map((chunk) => chunk.content || '').join('');

    // Aggregate tool calls so far
    const toolCalls = this.chunks
      .flatMap((chunk) => chunk.toolCalls || [])
      .filter((call, index, arr) => arr.findIndex((c) => c.id === call.id) === index);

    // Get current usage if available
    const usageChunk = this.chunks.reverse().find((chunk) => chunk.usage);

    const usage = usageChunk?.usage || {
      inputTokens: 0,
      outputTokens: 0,
      totalTokens: 0,
    };

    return {
      id: firstChunk.id,
      content,
      toolCalls,
      usage,
      metadata: {
        model: firstChunk.metadata?.model || 'unknown',
        timestamp: firstChunk.metadata?.timestamp || new Date().toISOString(),
        latency: Date.now() - this.startTime,
        chunkCount: this.chunks.length,
      },
    };
  }

  getMetrics(): {
    chunkCount: number;
    totalContentLength: number;
    totalToolCalls: number;
    duration: number;
  } {
    const totalContentLength = this.chunks.reduce(
      (sum, chunk) => sum + (chunk.content?.length || 0),
      0
    );

    const totalToolCalls = this.chunks.reduce(
      (sum, chunk) => sum + (chunk.toolCalls?.length || 0),
      0
    );

    return {
      chunkCount: this.chunks.length,
      totalContentLength,
      totalToolCalls,
      duration: Date.now() - this.startTime,
    };
  }
}
```

### Subtask 2.4: Add Stream Cancellation and Timeout Handling

**File**: `src/providers/anthropic/streaming/stream-controller.ts`

```typescript
import type { AsyncIterable, AsyncGenerator } from '@anthropic-ai/sdk';
import type { MessageChunk, StreamOptions } from '@tamma/shared/contracts';
import { logger } from '@tamma/observability';
import { AnthropicError } from '../errors/anthropic-error';

export interface StreamControllerOptions {
  timeout?: number;
  maxChunks?: number;
  onCancel?: () => void;
}

export class StreamController {
  private abortController: AbortController | null = null;
  private timeoutId: NodeJS.Timeout | null = null;
  private chunkCount: number = 0;
  private isCancelled: boolean = false;

  constructor(private options: StreamControllerOptions = {}) {}

  async *createControlledStream(
    baseStream: AsyncIterable<MessageChunk>,
    options: StreamOptions = {}
  ): AsyncGenerator<MessageChunk> {
    this.abortController = new AbortController();
    this.chunkCount = 0;
    this.isCancelled = false;

    // Set up timeout if specified
    if (this.options.timeout) {
      this.timeoutId = setTimeout(() => {
        this.cancel('timeout');
      }, this.options.timeout);
    }

    try {
      for await (const chunk of baseStream) {
        // Check if stream is cancelled
        if (this.isCancelled) {
          logger.debug('Stream iteration cancelled', {
            reason: 'manual_cancellation',
            chunksProcessed: this.chunkCount,
          });
          break;
        }

        // Check chunk limit
        if (this.options.maxChunks && this.chunkCount >= this.options.maxChunks) {
          logger.debug('Stream chunk limit reached', {
            maxChunks: this.options.maxChunks,
            chunksProcessed: this.chunkCount,
          });
          break;
        }

        this.chunkCount++;
        yield chunk;

        // Check for natural completion
        if (chunk.finishReason) {
          logger.debug('Stream completed naturally', {
            finishReason: chunk.finishReason,
            totalChunks: this.chunkCount,
          });
          break;
        }
      }
    } catch (error) {
      if (this.isCancelled) {
        logger.debug('Stream cancelled by controller', {
          chunksProcessed: this.chunkCount,
          error: error.message,
        });
        return; // Don't throw on cancellation
      }

      logger.error('Stream iteration failed', {
        error: error.message,
        chunksProcessed: this.chunkCount,
      });

      throw new AnthropicError('STREAM_ITERATION_FAILED', 'Failed to iterate through stream', {
        cause: error,
        chunksProcessed: this.chunkCount,
      });
    } finally {
      this.cleanup();
    }
  }

  cancel(reason: string = 'manual'): void {
    if (this.isCancelled) {
      return; // Already cancelled
    }

    this.isCancelled = true;

    logger.info('Cancelling stream', {
      reason,
      chunksProcessed: this.chunkCount,
    });

    // Abort the underlying request
    if (this.abortController) {
      this.abortController.abort();
    }

    // Clear timeout
    if (this.timeoutId) {
      clearTimeout(this.timeoutId);
      this.timeoutId = null;
    }

    // Call custom cancel handler
    if (this.options.onCancel) {
      try {
        this.options.onCancel();
      } catch (error) {
        logger.error('Cancel handler failed', {
          error: error.message,
        });
      }
    }
  }

  isStreamCancelled(): boolean {
    return this.isCancelled;
  }

  getChunkCount(): number {
    return this.chunkCount;
  }

  private cleanup(): void {
    if (this.timeoutId) {
      clearTimeout(this.timeoutId);
      this.timeoutId = null;
    }

    this.abortController = null;
  }

  // Static factory method for common configurations
  static withTimeout(timeoutMs: number): StreamController {
    return new StreamController({
      timeout: timeoutMs,
    });
  }

  static withChunkLimit(maxChunks: number): StreamController {
    return new StreamController({
      maxChunks,
    });
  }

  static withCancellation(onCancel: () => void): StreamController {
    return new StreamController({
      onCancel,
    });
  }
}
```

## Integration with Main Provider

**Update to**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
// Add these imports and update the sendMessage method
import { MessageStream } from './streaming/message-stream';
import { StreamController } from './streaming/stream-controller';
import type { StreamOptions } from '@tamma/shared/contracts';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly messageStream: MessageStream;

  constructor(config: AnthropicProviderConfig) {
    // ... existing constructor code ...
    this.messageStream = new MessageStream();
  }

  async sendMessage(
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    if (!this.isInitialized) {
      throw new AnthropicError(
        'PROVIDER_NOT_INITIALIZED',
        'Provider must be initialized before sending messages'
      );
    }

    const modelConfig = this.models.get(request.model);
    if (!modelConfig) {
      throw new AnthropicError('MODEL_NOT_SUPPORTED', `Model ${request.model} is not supported`, {
        model: request.model,
        availableModels: Array.from(this.models.keys()),
      });
    }

    try {
      // Create base stream
      const baseStream = await this.messageStream.createStream(this.client, request, options);

      // Apply stream control if needed
      if (options.timeout || options.maxChunks || options.onCancel) {
        const controller = new StreamController({
          timeout: options.timeout,
          maxChunks: options.maxChunks,
          onCancel: options.onCancel,
        });

        return controller.createControlledStream(baseStream, options);
      }

      return baseStream;
    } catch (error) {
      logger.error('Failed to send message', {
        error: error.message,
        model: request.model,
        request,
      });

      throw new AnthropicError('MESSAGE_SEND_FAILED', 'Failed to send message to Anthropic', {
        cause: error,
        model: request.model,
      });
    }
  }
}
```

## Dependencies

### Internal Dependencies

- `@tamma/shared/contracts` - MessageChunk, StreamOptions interfaces
- `@tamma/observability` - Logging utilities
- Story 2.1 streaming interfaces
- Task 2.1 provider foundation

### External Dependencies

- `@anthropic-ai/sdk` - Anthropic streaming support

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/streaming/stream-handler.test.ts
describe('StreamHandler', () => {
  describe('handleStream', () => {
    it('should process text chunks correctly');
    it('should handle tool use chunks');
    it('should aggregate usage information');
    it('should map stop reasons correctly');
    it('should handle stream errors gracefully');
  });

  describe('processChunk', () => {
    it('should extract text content');
    it('should extract tool calls');
    it('should handle unknown content types');
  });
});

// tests/providers/anthropic/streaming/stream-controller.test.ts
describe('StreamController', () => {
  describe('timeout handling', () => {
    it('should cancel stream after timeout');
    it('should cleanup timeout resources');
  });

  describe('chunk limit', () => {
    it('should stop after max chunks');
    it('should yield all chunks up to limit');
  });

  describe('manual cancellation', () => {
    it('should cancel on manual request');
    it('should call cancel handler');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/streaming/integration.test.ts
describe('Streaming Integration', () => {
  it('should stream real Claude responses');
  it('should handle real-time token counting');
  it('should process tool calls in streaming mode');
  it('should handle network interruptions');
  it('should respect rate limits during streaming');
});
```

## Performance Considerations

### Memory Management

- Stream chunks are processed one at a time to minimize memory usage
- Chunk aggregation only stores necessary data
- Automatic cleanup of resources on completion/cancellation

### Latency Optimization

- Minimal processing overhead per chunk
- Parallel token counting where possible
- Efficient string concatenation for content aggregation

### Error Recovery

- Graceful handling of partial stream failures
- Automatic retry for transient errors
- Circuit breaker integration for repeated failures

## Risk Mitigation

### Technical Risks

1. **Memory Leaks**: Uncleaned stream resources
   - Mitigation: Automatic cleanup, proper resource management
2. **Infinite Streams**: Streams that never complete
   - Mitigation: Timeout handling, chunk limits, cancellation support
3. **Chunk Loss**: Missing chunks in the stream
   - Mitigation: Chunk counting, validation, error detection

### Operational Risks

1. **Network Interruption**: Connection drops during streaming
   - Mitigation: Retry logic, graceful degradation, error reporting
2. **Rate Limiting**: Exceeding limits during long streams
   - Mitigation: Rate limiting, backpressure handling
3. **Cost Overrun**: Unexpected token usage
   - Mitigation: Real-time token counting, usage alerts, limits

## Deliverables

1. **Stream Handler**: Core stream processing logic
2. **Message Stream**: AsyncIterable implementation
3. **Chunk Aggregator**: Response aggregation utilities
4. **Stream Controller**: Cancellation and timeout handling
5. **Integration**: Updated provider with streaming support
6. **Unit Tests**: Comprehensive test coverage
7. **Integration Tests**: Real API streaming validation
8. **Documentation**: Streaming usage examples

## Success Criteria

- [ ] Implements AsyncIterable<MessageChunk> pattern
- [ ] Handles real-time streaming from Anthropic API
- [ ] Supports stream cancellation and timeouts
- [ ] Provides accurate token counting during streaming
- [ ] Handles tool calls in streaming mode
- [ ] Graceful error handling and recovery
- [ ] Memory-efficient processing
- [ ] Comprehensive test coverage
- [ ] Performance benchmarks meet requirements

## File Structure

```
src/providers/anthropic/streaming/
├── stream-handler.ts           # Core stream processing
├── message-stream.ts          # AsyncIterable implementation
├── chunk-aggregator.ts        # Response aggregation
├── stream-controller.ts       # Cancellation & control
└── index.ts                   # Public exports

tests/providers/anthropic/streaming/
├── stream-handler.test.ts
├── message-stream.test.ts
├── chunk-aggregator.test.ts
├── stream-controller.test.ts
└── integration.test.ts
```

This task provides a robust streaming implementation that enables real-time AI responses while maintaining proper resource management, error handling, and performance optimization.
