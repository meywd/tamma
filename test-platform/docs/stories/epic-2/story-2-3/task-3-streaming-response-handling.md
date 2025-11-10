# Task 3: Streaming Response Handling (AC: 3)

## Overview

Implement streaming response handling with chunked processing for real-time updates, error handling, and reconnection logic for OpenAI API responses.

## Objectives

- Implement streaming chat completion for real-time response generation
- Add chunked processing with buffering and aggregation
- Handle streaming errors and implement reconnection logic
- Provide efficient backpressure handling and memory management

## Implementation Steps

### Subtask 3.1: Implement streaming chat completion

**Objective**: Add streaming support for real-time response generation with proper chunk processing.

**Implementation Steps**:

1. **Create Streaming Interface**:

```typescript
// packages/providers/src/types/streaming.ts
export interface StreamingChunk {
  id: string;
  content: string;
  finishReason?: 'stop' | 'length' | 'content_filter' | 'function_call' | 'tool_calls';
  usage?: {
    promptTokens: number;
    completionTokens: number;
    totalTokens: number;
  };
  timestamp: string;
  model: string;
  index: number;
}

export interface StreamingOptions {
  onChunk?: (chunk: StreamingChunk) => void;
  onError?: (error: Error) => void;
  onComplete?: (usage: any) => void;
  onStart?: (id: string) => void;
  bufferSize?: number;
  flushInterval?: number;
}

export interface StreamingResponse {
  id: string;
  model: string;
  created: number;
  status: 'pending' | 'streaming' | 'completed' | 'error';
  chunks: StreamingChunk[];
  fullContent: string;
  usage?: any;
  error?: Error;
}

export class StreamingManager {
  private responses: Map<string, StreamingResponse> = new Map();
  private options: StreamingOptions;

  constructor(options: StreamingOptions = {}) {
    this.options = {
      bufferSize: 100,
      flushInterval: 50,
      ...options,
    };
  }

  createResponse(id: string, model: string): StreamingResponse {
    const response: StreamingResponse = {
      id,
      model,
      created: Date.now(),
      status: 'pending',
      chunks: [],
      fullContent: '',
    };

    this.responses.set(id, response);
    this.options.onStart?.(id);

    return response;
  }

  addChunk(responseId: string, chunk: StreamingChunk): void {
    const response = this.responses.get(responseId);
    if (!response) {
      throw new Error(`Response not found: ${responseId}`);
    }

    response.chunks.push(chunk);
    response.fullContent += chunk.content;
    response.status = 'streaming';

    this.options.onChunk?.(chunk);

    // Check if streaming is complete
    if (chunk.finishReason) {
      response.status = 'completed';
      response.usage = chunk.usage;
      this.options.onComplete?.(chunk.usage);
    }
  }

  getResponse(responseId: string): StreamingResponse | undefined {
    return this.responses.get(responseId);
  }

  completeResponse(responseId: string, usage?: any): void {
    const response = this.responses.get(responseId);
    if (!response) {
      return;
    }

    response.status = 'completed';
    response.usage = usage;
    this.options.onComplete?.(usage);
  }

  errorResponse(responseId: string, error: Error): void {
    const response = this.responses.get(responseId);
    if (!response) {
      return;
    }

    response.status = 'error';
    response.error = error;
    this.options.onError?.(error);
  }

  cleanup(responseId: string): void {
    this.responses.delete(responseId);
  }

  getAllResponses(): StreamingResponse[] {
    return Array.from(this.responses.values());
  }
}
```

2. **Implement Streaming Methods**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
import { StreamingChunk, StreamingOptions, StreamingManager } from '../types/streaming';

export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  async *createStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & StreamingOptions = {}
  ): AsyncIterable<StreamingChunk> {
    await this.validateModel(model);

    const { onChunk, onError, onComplete, onStart, ...completionOptions } = options;

    // Validate parameters
    this.validateParameters(model, completionOptions);

    const streamingManager = new StreamingManager({
      onChunk,
      onError,
      onComplete,
      onStart,
    });

    try {
      const stream = await this.client.chat.completions.create({
        model,
        messages,
        stream: true,
        ...completionOptions,
      });

      const responseId = this.generateResponseId();
      const response = streamingManager.createResponse(responseId, model);

      for await (const chunk of stream) {
        const delta = chunk.choices[0]?.delta;

        if (delta?.content) {
          const streamingChunk: StreamingChunk = {
            id: chunk.id,
            content: delta.content,
            finishReason: chunk.choices[0]?.finish_reason,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
            model: chunk.model || model,
            index: chunk.choices[0]?.index || 0,
          };

          streamingManager.addChunk(responseId, streamingChunk);
          yield streamingChunk;
        }

        // Handle finish reason
        if (chunk.choices[0]?.finish_reason) {
          const finalChunk: StreamingChunk = {
            id: chunk.id,
            content: '',
            finishReason: chunk.choices[0].finish_reason,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
            model: chunk.model || model,
            index: chunk.choices[0]?.index || 0,
          };

          streamingManager.addChunk(responseId, finalChunk);
          yield finalChunk;
          break;
        }
      }

      streamingManager.cleanup(responseId);
    } catch (error) {
      const streamingError = this.handleOpenAIError(error);
      onError?.(streamingError);
      throw streamingError;
    }
  }

  async createStreamingCompletionWithCallback(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & StreamingOptions = {}
  ): Promise<{
    id: string;
    content: string;
    usage?: any;
    chunks: StreamingChunk[];
  }> {
    const chunks: StreamingChunk[] = [];
    let fullContent = '';
    let responseId: string;
    let finalUsage: any;

    const { onChunk, onError, onComplete, onStart, ...completionOptions } = options;

    try {
      for await (const chunk of this.createStreamingCompletion(messages, model, {
        ...completionOptions,
        onChunk: (chunk) => {
          chunks.push(chunk);
          fullContent += chunk.content;
          onChunk?.(chunk);
        },
        onStart: (id) => {
          responseId = id;
          onStart?.(id);
        },
        onComplete: (usage) => {
          finalUsage = usage;
          onComplete?.(usage);
        },
        onError: onError,
      })) {
        // Chunks are processed in the callback above
      }

      return {
        id: responseId!,
        content: fullContent,
        usage: finalUsage,
        chunks,
      };
    } catch (error) {
      throw this.handleOpenAIError(error);
    }
  }

  private generateResponseId(): string {
    return `openai_${Date.now()}_${Math.random().toString(36).substring(2, 9)}`;
  }
}
```

---

### Subtask 3.2: Add chunked processing for real-time updates

**Objective**: Implement efficient chunked processing with buffering, aggregation, and backpressure handling.

**Implementation Steps**:

1. **Create Chunk Processor**:

```typescript
// packages/providers/src/utils/chunk-processor.ts
import { StreamingChunk } from '../types/streaming';

export interface ChunkProcessorOptions {
  bufferSize?: number;
  flushInterval?: number;
  maxChunkSize?: number;
  enableAggregation?: boolean;
  aggregationStrategy?: 'word' | 'sentence' | 'paragraph';
}

export class ChunkProcessor {
  private buffer: string = '';
  private chunks: StreamingChunk[] = [];
  private lastFlushTime: number = 0;
  private options: Required<ChunkProcessorOptions>;
  private flushTimer: NodeJS.Timeout | null = null;

  constructor(options: ChunkProcessorOptions = {}) {
    this.options = {
      bufferSize: 100,
      flushInterval: 50,
      maxChunkSize: 1000,
      enableAggregation: true,
      aggregationStrategy: 'word',
      ...options,
    };
  }

  processChunk(chunk: StreamingChunk): StreamingChunk | StreamingChunk[] | null {
    this.buffer += chunk.content;
    this.lastFlushTime = Date.now();

    // Store original chunk
    this.chunks.push(chunk);

    // Determine if we should flush
    if (this.shouldFlush(chunk)) {
      return this.flush();
    }

    return null;
  }

  private shouldFlush(chunk: StreamingChunk): boolean {
    // Always flush on finish reason
    if (chunk.finishReason) {
      return true;
    }

    // Flush if buffer is too large
    if (this.buffer.length >= this.options.maxChunkSize) {
      return true;
    }

    // Flush based on aggregation strategy
    if (this.options.enableAggregation) {
      return this.shouldFlushByStrategy();
    }

    // Flush based on time interval
    return Date.now() - this.lastFlushTime >= this.options.flushInterval;
  }

  private shouldFlushByStrategy(): boolean {
    switch (this.options.aggregationStrategy) {
      case 'word':
        return this.buffer.includes(' ') || this.buffer.includes('\n');

      case 'sentence':
        return /[.!?]\s*$/.test(this.buffer);

      case 'paragraph':
        return /\n\s*\n/.test(this.buffer) || this.buffer.length >= this.options.maxChunkSize;

      default:
        return false;
    }
  }

  flush(): StreamingChunk | StreamingChunk[] {
    if (this.buffer.length === 0) {
      return null;
    }

    const lastChunk = this.chunks[this.chunks.length - 1];
    const processedChunk: StreamingChunk = {
      ...lastChunk,
      content: this.buffer,
      timestamp: new Date().toISOString(),
    };

    // Reset buffer
    this.buffer = '';
    this.lastFlushTime = Date.now();

    return processedChunk;
  }

  forceFlush(): StreamingChunk | null {
    return this.flush();
  }

  getBufferedContent(): string {
    return this.buffer;
  }

  getBufferLength(): number {
    return this.buffer.length;
  }

  reset(): void {
    this.buffer = '';
    this.chunks = [];
    this.lastFlushTime = Date.now();

    if (this.flushTimer) {
      clearTimeout(this.flushTimer);
      this.flushTimer = null;
    }
  }

  // Auto-flush with timer
  startAutoFlush(): void {
    if (this.flushTimer) {
      return;
    }

    this.flushTimer = setInterval(() => {
      if (this.buffer.length > 0) {
        this.flush();
      }
    }, this.options.flushInterval);
  }

  stopAutoFlush(): void {
    if (this.flushTimer) {
      clearInterval(this.flushTimer);
      this.flushTimer = null;
    }
  }
}
```

2. **Create Backpressure Handler**:

```typescript
// packages/providers/src/utils/backpressure-handler.ts
import { StreamingChunk } from '../types/streaming';

export interface BackpressureOptions {
  maxBufferSize?: number;
  highWaterMark?: number;
  lowWaterMark?: number;
  pauseThreshold?: number;
  resumeThreshold?: number;
}

export class BackpressureHandler {
  private buffer: StreamingChunk[] = [];
  private isPaused: boolean = false;
  private options: Required<BackpressureOptions>;
  private onPaused?: () => void;
  private onResumed?: () => void;

  constructor(options: BackpressureOptions = {}) {
    this.options = {
      maxBufferSize: 1000,
      highWaterMark: 800,
      lowWaterMark: 200,
      pauseThreshold: 900,
      resumeThreshold: 100,
      ...options,
    };
  }

  setEventHandlers(handlers: { onPaused?: () => void; onResumed?: () => void }): void {
    this.onPaused = handlers.onPaused;
    this.onResumed = handlers.onResumed;
  }

  async processChunk(chunk: StreamingChunk): Promise<boolean> {
    // Check if we should pause
    if (this.shouldPause()) {
      this.pause();

      // Wait until buffer is processed
      await this.waitForResume();
    }

    // Add chunk to buffer
    this.buffer.push(chunk);

    // Check if we exceeded max buffer size
    if (this.buffer.length > this.options.maxBufferSize) {
      // Drop oldest chunks
      const dropped = this.buffer.splice(0, this.buffer.length - this.options.maxBufferSize);
      console.warn(`Dropped ${dropped.length} chunks due to buffer overflow`);
    }

    return true;
  }

  getNextChunk(): StreamingChunk | undefined {
    const chunk = this.buffer.shift();

    // Check if we should resume
    if (this.isPaused && this.shouldResume()) {
      this.resume();
    }

    return chunk;
  }

  private shouldPause(): boolean {
    return !this.isPaused && this.buffer.length >= this.options.pauseThreshold;
  }

  private shouldResume(): boolean {
    return this.isPaused && this.buffer.length <= this.options.resumeThreshold;
  }

  private pause(): void {
    if (!this.isPaused) {
      this.isPaused = true;
      this.onPaused?.();
      console.log('Backpressure: Paused streaming due to high buffer');
    }
  }

  private resume(): void {
    if (this.isPaused) {
      this.isPaused = false;
      this.onResumed?.();
      console.log('Backpressure: Resumed streaming');
    }
  }

  private async waitForResume(): Promise<void> {
    return new Promise<void>((resolve) => {
      const checkResume = () => {
        if (!this.isPaused) {
          resolve();
        } else {
          setTimeout(checkResume, 10);
        }
      };
      checkResume();
    });
  }

  getBufferSize(): number {
    return this.buffer.length;
  }

  isPausedStream(): boolean {
    return this.isPaused;
  }

  getStats(): {
    bufferSize: number;
    isPaused: boolean;
    utilization: number;
  } {
    return {
      bufferSize: this.buffer.length,
      isPaused: this.isPaused,
      utilization: this.buffer.length / this.options.maxBufferSize,
    };
  }

  clear(): void {
    this.buffer = [];
    this.isPaused = false;
  }
}
```

3. **Enhanced Streaming with Processing**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
import { ChunkProcessor } from '../utils/chunk-processor';
import { BackpressureHandler } from '../utils/backpressure-handler';

export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  async *createProcessedStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & {
      chunkProcessor?: ChunkProcessorOptions;
      backpressure?: BackpressureOptions;
    } = {}
  ): AsyncIterable<StreamingChunk> {
    await this.validateModel(model);

    const {
      chunkProcessor: processorOptions,
      backpressure: backpressureOptions,
      ...completionOptions
    } = options;

    // Initialize processors
    const chunkProcessor = new ChunkProcessor(processorOptions);
    const backpressureHandler = new BackpressureHandler(backpressureOptions);

    // Start auto-flush
    chunkProcessor.startAutoFlush();

    try {
      for await (const rawChunk of this.createStreamingCompletion(
        messages,
        model,
        completionOptions
      )) {
        // Process chunk through backpressure handler
        const shouldContinue = await backpressureHandler.processChunk(rawChunk);
        if (!shouldContinue) {
          break;
        }

        // Process chunk through chunk processor
        const processedChunk = chunkProcessor.processChunk(rawChunk);

        if (processedChunk) {
          if (Array.isArray(processedChunk)) {
            for (const chunk of processedChunk) {
              yield chunk;
            }
          } else if (processedChunk) {
            yield processedChunk;
          }
        }

        // Get next chunk from backpressure handler
        const nextChunk = backpressureHandler.getNextChunk();
        if (nextChunk) {
          yield nextChunk;
        }
      }

      // Flush any remaining content
      const finalChunk = chunkProcessor.forceFlush();
      if (finalChunk) {
        yield finalChunk;
      }
    } finally {
      // Cleanup
      chunkProcessor.stopAutoFlush();
      chunkProcessor.reset();
      backpressureHandler.clear();
    }
  }
}
```

---

### Subtask 3.3: Handle streaming errors and reconnection

**Objective**: Implement robust error handling and reconnection logic for streaming connections.

**Implementation Steps**:

1. **Create Streaming Error Handler**:

```typescript
// packages/providers/src/utils/streaming-error-handler.ts
import { StreamingChunk } from '../types/streaming';

export interface StreamingError {
  type: 'network' | 'api' | 'timeout' | 'rate_limit' | 'authentication' | 'unknown';
  message: string;
  originalError: any;
  retryable: boolean;
  retryAfter?: number;
  chunkId?: string;
}

export interface ReconnectionOptions {
  maxRetries?: number;
  baseDelay?: number;
  maxDelay?: number;
  backoffMultiplier?: number;
  jitter?: boolean;
  retryCondition?: (error: StreamingError) => boolean;
}

export class StreamingErrorHandler {
  private options: Required<ReconnectionOptions>;
  private retryCount: number = 0;
  private lastErrorTime: number = 0;

  constructor(options: ReconnectionOptions = {}) {
    this.options = {
      maxRetries: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      jitter: true,
      retryCondition: this.defaultRetryCondition,
      ...options,
    };
  }

  classifyError(error: any): StreamingError {
    const message = error?.message || 'Unknown error';
    const statusCode = error?.status || error?.statusCode;

    // Network errors
    if (
      error?.code === 'ECONNRESET' ||
      error?.code === 'ENOTFOUND' ||
      error?.code === 'ETIMEDOUT' ||
      error?.name === 'NetworkError'
    ) {
      return {
        type: 'network',
        message: `Network error: ${message}`,
        originalError: error,
        retryable: true,
      };
    }

    // Timeout errors
    if (error?.name === 'AbortError' || message.includes('timeout') || statusCode === 408) {
      return {
        type: 'timeout',
        message: `Request timeout: ${message}`,
        originalError: error,
        retryable: true,
      };
    }

    // Rate limit errors
    if (
      statusCode === 429 ||
      message.includes('rate limit') ||
      error?.error?.type === 'rate_limit_exceeded'
    ) {
      const retryAfter = error?.headers?.['retry-after'] || error?.error?.retry_after || 60;

      return {
        type: 'rate_limit',
        message: `Rate limit exceeded: ${message}`,
        originalError: error,
        retryable: true,
        retryAfter,
      };
    }

    // Authentication errors
    if (
      statusCode === 401 ||
      message.includes('unauthorized') ||
      error?.error?.type === 'authentication_error'
    ) {
      return {
        type: 'authentication',
        message: `Authentication failed: ${message}`,
        originalError: error,
        retryable: false,
      };
    }

    // API errors
    if (statusCode >= 400 && statusCode < 500) {
      return {
        type: 'api',
        message: `API error: ${message}`,
        originalError: error,
        retryable: statusCode >= 500, // Retry on server errors
      };
    }

    // Unknown errors
    return {
      type: 'unknown',
      message: `Unknown error: ${message}`,
      originalError: error,
      retryable: true, // Be optimistic with unknown errors
    };
  }

  async handleStreamingError<T>(error: any, retryOperation: () => Promise<T>): Promise<T> {
    const streamingError = this.classifyError(error);

    // Check if we should retry
    if (!this.shouldRetry(streamingError)) {
      throw streamingError;
    }

    // Calculate retry delay
    const delay = this.calculateRetryDelay(streamingError);

    console.warn(
      `Streaming error (attempt ${this.retryCount + 1}): ${streamingError.message}. Retrying in ${delay}ms`
    );

    // Wait before retry
    await this.sleep(delay);

    // Increment retry count
    this.retryCount++;
    this.lastErrorTime = Date.now();

    try {
      const result = await retryOperation();

      // Reset retry count on success
      this.retryCount = 0;

      return result;
    } catch (retryError) {
      // Recursive retry
      return this.handleStreamingError(retryError, retryOperation);
    }
  }

  private shouldRetry(error: StreamingError): boolean {
    // Check custom retry condition
    if (!this.options.retryCondition(error)) {
      return false;
    }

    // Check if error is retryable
    if (!error.retryable) {
      return false;
    }

    // Check retry count
    if (this.retryCount >= this.options.maxRetries) {
      return false;
    }

    // Check rate limit retry after
    if (error.type === 'rate_limit' && error.retryAfter) {
      const timeSinceLastError = Date.now() - this.lastErrorTime;
      return timeSinceLastError >= error.retryAfter * 1000;
    }

    return true;
  }

  private calculateRetryDelay(error: StreamingError): number {
    let delay = this.options.baseDelay * Math.pow(this.options.backoffMultiplier, this.retryCount);

    // Apply rate limit retry after
    if (error.type === 'rate_limit' && error.retryAfter) {
      delay = Math.max(delay, error.retryAfter * 1000);
    }

    // Apply maximum delay limit
    delay = Math.min(delay, this.options.maxDelay);

    // Add jitter
    if (this.options.jitter) {
      const jitterRange = delay * 0.1; // 10% jitter
      delay += Math.random() * jitterRange - jitterRange / 2;
    }

    return Math.max(0, Math.floor(delay));
  }

  private defaultRetryCondition(error: StreamingError): boolean {
    // Don't retry authentication errors
    if (error.type === 'authentication') {
      return false;
    }

    // Don't retry certain API errors
    if (error.type === 'api' && error.originalError?.error?.code === 'invalid_prompt') {
      return false;
    }

    return true;
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  getRetryCount(): number {
    return this.retryCount;
  }

  reset(): void {
    this.retryCount = 0;
    this.lastErrorTime = 0;
  }

  getStats(): {
    retryCount: number;
    lastErrorTime: number;
    maxRetries: number;
  } {
    return {
      retryCount: this.retryCount,
      lastErrorTime: this.lastErrorTime,
      maxRetries: this.options.maxRetries,
    };
  }
}
```

2. **Create Reconnection Manager**:

```typescript
// packages/providers/src/utils/reconnection-manager.ts
import { StreamingErrorHandler, StreamingError } from './streaming-error-handler';

export interface ReconnectionManager {
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  isConnected(): boolean;
  onReconnected?: () => void;
  onDisconnected?: (error?: Error) => void;
}

export class StreamingReconnectionManager {
  private errorHandler: StreamingErrorHandler;
  private connectionAttempts: number = 0;
  private lastConnectionTime: number = 0;
  private isConnectedState: boolean = false;
  private reconnectTimer: NodeJS.Timeout | null = null;

  constructor(
    private connectionManager: ReconnectionManager,
    options: {
      maxReconnectAttempts?: number;
      reconnectDelay?: number;
      maxReconnectDelay?: number;
      heartbeatInterval?: number;
    } = {}
  ) {
    this.errorHandler = new StreamingErrorHandler({
      maxRetries: options.maxReconnectAttempts || 5,
      baseDelay: options.reconnectDelay || 1000,
      maxDelay: options.maxReconnectDelay || 30000,
    });
  }

  async startConnection(): Promise<void> {
    try {
      await this.connectionManager.connect();
      this.isConnectedState = true;
      this.connectionAttempts = 0;
      this.lastConnectionTime = Date.now();

      console.log('Streaming connection established');
      this.connectionManager.onReconnected?.();

      // Start heartbeat
      this.startHeartbeat();
    } catch (error) {
      this.isConnectedState = false;
      this.connectionAttempts++;

      console.error(`Connection attempt ${this.connectionAttempts} failed:`, error);
      this.connectionManager.onDisconnected?.(
        error instanceof Error ? error : new Error('Connection failed')
      );

      // Schedule reconnection
      this.scheduleReconnection(error);
    }
  }

  async stopConnection(): Promise<void> {
    this.isConnectedState = false;

    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    try {
      await this.connectionManager.disconnect();
      console.log('Streaming connection closed');
    } catch (error) {
      console.error('Error closing connection:', error);
    }
  }

  private scheduleReconnection(error: any): void {
    if (this.connectionAttempts >= this.errorHandler.getStats().maxRetries) {
      console.error('Max reconnection attempts reached. Giving up.');
      return;
    }

    const streamingError = this.errorHandler.classifyError(error);
    const delay = this.calculateReconnectionDelay(streamingError);

    console.log(`Scheduling reconnection in ${delay}ms (attempt ${this.connectionAttempts + 1})`);

    this.reconnectTimer = setTimeout(async () => {
      this.reconnectTimer = null;
      await this.startConnection();
    }, delay);
  }

  private calculateReconnectionDelay(error: StreamingError): number {
    // Exponential backoff with jitter
    const baseDelay = 1000;
    const maxDelay = 30000;
    const backoffMultiplier = 2;

    let delay = baseDelay * Math.pow(backoffMultiplier, this.connectionAttempts);
    delay = Math.min(delay, maxDelay);

    // Add jitter
    const jitter = delay * 0.1;
    delay += Math.random() * jitter - jitter / 2;

    // Apply rate limit delay
    if (error.type === 'rate_limit' && error.retryAfter) {
      delay = Math.max(delay, error.retryAfter * 1000);
    }

    return Math.max(0, Math.floor(delay));
  }

  private startHeartbeat(): void {
    // Implementation would depend on the specific connection type
    // This is a placeholder for heartbeat logic
    setInterval(async () => {
      if (this.isConnectedState) {
        try {
          // Send heartbeat or ping
          await this.sendHeartbeat();
        } catch (error) {
          console.warn('Heartbeat failed:', error);
          this.handleConnectionLoss(error);
        }
      }
    }, 30000); // 30 second heartbeat
  }

  private async sendHeartbeat(): Promise<void> {
    // Implementation depends on connection type
    // For HTTP streaming, this might be a no-op
    // For WebSocket, this would send a ping frame
  }

  private handleConnectionLoss(error: any): void {
    if (this.isConnectedState) {
      this.isConnectedState = false;
      this.connectionManager.onDisconnected?.(
        error instanceof Error ? error : new Error('Connection lost')
      );
      this.scheduleReconnection(error);
    }
  }

  isConnected(): boolean {
    return this.isConnectedState;
  }

  getConnectionStats(): {
    isConnected: boolean;
    connectionAttempts: number;
    lastConnectionTime: number;
    timeSinceLastConnection: number;
  } {
    return {
      isConnected: this.isConnectedState,
      connectionAttempts: this.connectionAttempts,
      lastConnectionTime: this.lastConnectionTime,
      timeSinceLastConnection: Date.now() - this.lastConnectionTime,
    };
  }
}
```

3. **Enhanced Streaming with Error Handling**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
import { StreamingErrorHandler } from '../utils/streaming-error-handler';
import { StreamingReconnectionManager } from '../utils/reconnection-manager';

export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  async *createResilientStreamingCompletion(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & {
      reconnection?: {
        maxRetries?: number;
        retryDelay?: number;
      };
    } = {}
  ): AsyncIterable<StreamingChunk> {
    await this.validateModel(model);

    const { reconnection, ...completionOptions } = options;

    const errorHandler = new StreamingErrorHandler(reconnection);

    const createStream = async () => {
      const stream = await this.client.chat.completions.create({
        model,
        messages,
        stream: true,
        ...completionOptions,
      });

      return stream;
    };

    try {
      const stream = await errorHandler.handleStreamingError(null, createStream);

      for await (const chunk of stream) {
        const delta = chunk.choices[0]?.delta;

        if (delta?.content) {
          const streamingChunk: StreamingChunk = {
            id: chunk.id,
            content: delta.content,
            finishReason: chunk.choices[0]?.finish_reason,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
            model: chunk.model || model,
            index: chunk.choices[0]?.index || 0,
          };

          yield streamingChunk;
        }

        if (chunk.choices[0]?.finish_reason) {
          break;
        }
      }
    } catch (error) {
      const streamingError = errorHandler.classifyError(error);
      throw new TammaError(
        'STREAMING_ERROR',
        `Streaming failed: ${streamingError.message}`,
        {
          errorType: streamingError.type,
          retryable: streamingError.retryable,
          originalError: streamingError.originalError,
          retryCount: errorHandler.getRetryCount(),
        },
        streamingError.retryable,
        'high'
      );
    }
  }

  // Streaming with automatic recovery
  async createStreamingCompletionWithRecovery(
    messages: OpenAI.Chat.ChatCompletionMessageParam[],
    model: string = this.config.defaultModel,
    options: Partial<OpenAI.Chat.ChatCompletionCreateParams> & {
      onReconnect?: (attempt: number) => void;
      onChunk?: (chunk: StreamingChunk) => void;
      onError?: (error: StreamingError) => void;
      onComplete?: (usage: any) => void;
    } = {}
  ): Promise<{
    success: boolean;
    content: string;
    chunks: StreamingChunk[];
    usage?: any;
    error?: StreamingError;
  }> {
    const { onReconnect, onChunk, onError, onComplete, ...completionOptions } = options;

    const chunks: StreamingChunk[] = [];
    let fullContent = '';
    let finalUsage: any;

    try {
      for await (const chunk of this.createResilientStreamingCompletion(messages, model, {
        ...completionOptions,
        reconnection: {
          maxRetries: 5,
          retryDelay: 2000,
        },
      })) {
        chunks.push(chunk);
        fullContent += chunk.content;
        onChunk?.(chunk);

        if (chunk.usage) {
          finalUsage = chunk.usage;
          onComplete?.(chunk.usage);
        }
      }

      return {
        success: true,
        content: fullContent,
        chunks,
        usage: finalUsage,
      };
    } catch (error) {
      const streamingError =
        error instanceof TammaError
          ? this.classifyError(error)
          : this.classifyError(new Error(error?.message || 'Unknown error'));

      onError?.(streamingError);

      return {
        success: false,
        content: fullContent,
        chunks,
        error: streamingError,
      };
    }
  }

  private classifyError(error: TammaError): StreamingError {
    return {
      type: 'api',
      message: error.message,
      originalError: error.context?.originalError,
      retryable: error.retryable,
    };
  }
}
```

**Files to Create**:

- `packages/providers/src/types/streaming.ts`
- `packages/providers/src/utils/chunk-processor.ts`
- `packages/providers/src/utils/backpressure-handler.ts`
- `packages/providers/src/utils/streaming-error-handler.ts`
- `packages/providers/src/utils/reconnection-manager.ts`

## Testing Requirements

### Unit Tests

1. **Streaming Interface Tests**:

```typescript
// tests/providers/streaming.test.ts
describe('Streaming Interface', () => {
  test('should create streaming manager', () => {
    const manager = new StreamingManager({
      onChunk: jest.fn(),
      onError: jest.fn(),
      onComplete: jest.fn(),
    });

    const response = manager.createResponse('test-id', 'gpt-4');
    expect(response.id).toBe('test-id');
    expect(response.model).toBe('gpt-4');
    expect(response.status).toBe('pending');
  });

  test('should process chunks correctly', () => {
    const onChunk = jest.fn();
    const manager = new StreamingManager({ onChunk });

    const response = manager.createResponse('test-id', 'gpt-4');
    const chunk: StreamingChunk = {
      id: 'chunk-1',
      content: 'Hello',
      timestamp: new Date().toISOString(),
      model: 'gpt-4',
      index: 0,
    };

    manager.addChunk('test-id', chunk);

    expect(response.chunks).toHaveLength(1);
    expect(response.fullContent).toBe('Hello');
    expect(onChunk).toHaveBeenCalledWith(chunk);
  });
});
```

2. **Chunk Processor Tests**:

```typescript
// tests/providers/chunk-processor.test.ts
describe('Chunk Processor', () => {
  test('should process chunks with word strategy', () => {
    const processor = new ChunkProcessor({
      aggregationStrategy: 'word',
      flushInterval: 50,
    });

    const chunk1: StreamingChunk = {
      id: '1',
      content: 'Hello',
      timestamp: new Date().toISOString(),
      model: 'gpt-4',
      index: 0,
    };

    const chunk2: StreamingChunk = {
      id: '2',
      content: ' world',
      timestamp: new Date().toISOString(),
      model: 'gpt-4',
      index: 1,
    };

    const result1 = processor.processChunk(chunk1);
    expect(result1).toBeNull(); // Should not flush yet

    const result2 = processor.processChunk(chunk2);
    expect(result2).toBeTruthy(); // Should flush on space
    expect(result2!.content).toBe('Hello world');
  });
});
```

3. **Error Handling Tests**:

```typescript
// tests/providers/streaming-error-handler.test.ts
describe('Streaming Error Handler', () => {
  test('should classify network errors', () => {
    const handler = new StreamingErrorHandler();
    const error = { code: 'ECONNRESET', message: 'Connection reset' };

    const streamingError = handler.classifyError(error);

    expect(streamingError.type).toBe('network');
    expect(streamingError.retryable).toBe(true);
  });

  test('should classify rate limit errors', () => {
    const handler = new StreamingErrorHandler();
    const error = { status: 429, message: 'Too many requests' };

    const streamingError = handler.classifyError(error);

    expect(streamingError.type).toBe('rate_limit');
    expect(streamingError.retryable).toBe(true);
  });
});
```

### Integration Tests

1. **End-to-End Streaming Tests**:

```typescript
// tests/providers/openai-streaming-integration.test.ts
describe('OpenAI Streaming Integration', () => {
  let provider: OpenAIProvider;

  beforeAll(async () => {
    provider = new OpenAIProvider({
      apiKey: process.env.OPENAI_TEST_API_KEY || '',
      timeout: 30000,
    });
    await provider.initialize();
  });

  test('should stream completion successfully', async () => {
    const messages = [{ role: 'user', content: 'Count to 5' }];
    const chunks: StreamingChunk[] = [];

    for await (const chunk of provider.createStreamingCompletion(messages)) {
      chunks.push(chunk);
    }

    expect(chunks.length).toBeGreaterThan(0);
    const fullContent = chunks.map((c) => c.content).join('');
    expect(fullContent).toContain('5');
  });

  test('should handle streaming with backpressure', async () => {
    const messages = [{ role: 'user', content: 'Write a long story' }];
    const chunks: StreamingChunk[] = [];

    for await (const chunk of provider.createProcessedStreamingCompletion(
      messages,
      'gpt-3.5-turbo',
      {
        backpressure: {
          maxBufferSize: 10,
          pauseThreshold: 8,
        },
      }
    )) {
      chunks.push(chunk);
    }

    expect(chunks.length).toBeGreaterThan(0);
  });
});
```

## Security Considerations

1. **Stream Security**:
   - Validate all streaming data before processing
   - Implement proper cleanup on stream termination
   - Handle malicious content in streaming responses

2. **Error Handling Security**:
   - Don't expose sensitive information in error messages
   - Implement rate limiting for reconnection attempts
   - Validate reconnection URLs and endpoints

3. **Memory Management**:
   - Implement proper buffer size limits
   - Clean up resources on stream completion
   - Monitor memory usage during long streams

## Dependencies

- **Runtime Dependencies**:
  - `openai`: Official OpenAI SDK
  - `@shared/errors`: Error handling utilities

- **Development Dependencies**:
  - `vitest`: Testing framework

## Notes

- Streaming should be resilient to network interruptions
- Implement proper backpressure handling to prevent memory issues
- Use exponential backoff for reconnection attempts
- Provide clear error messages for different failure types
- Consider implementing streaming metrics and monitoring
- Ensure proper cleanup of resources on stream completion
- Test streaming behavior under various network conditions
