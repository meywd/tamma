import OpenAI from 'openai';
import type {
  IAIProvider,
  ProviderConfig,
  ProviderCapabilities,
  MessageRequest,
  MessageResponse,
  MessageChunk,
  ModelInfo,
  StreamOptions,
  ProviderError,
  TokenUsage,
} from './types.js';
import { PROVIDER_ERROR_CODES } from './types.js';

const DEFAULT_BASE_URL = 'https://openrouter.ai/api/v1';
const DEFAULT_MAX_RETRIES = 3;
const DEFAULT_RETRY_DELAY = 1000;
const DEFAULT_TIMEOUT = 60_000;

function createProviderError(
  code: string,
  message: string,
  retryable: boolean,
  severity: 'low' | 'medium' | 'high' | 'critical' = 'medium',
): ProviderError {
  const error = new Error(message) as ProviderError;
  error.code = code;
  error.retryable = retryable;
  error.severity = severity;
  return error;
}

function mapHttpError(status: number, message: string): ProviderError {
  switch (status) {
    case 401:
      return createProviderError(
        PROVIDER_ERROR_CODES.INVALID_API_KEY,
        `Authentication failed: ${message}`,
        false,
        'critical',
      );
    case 402:
      return createProviderError(
        PROVIDER_ERROR_CODES.QUOTA_EXCEEDED,
        `Quota exceeded: ${message}`,
        false,
        'high',
      );
    case 429:
      return createProviderError(
        PROVIDER_ERROR_CODES.RATE_LIMIT_EXCEEDED,
        `Rate limit exceeded: ${message}`,
        true,
        'medium',
      );
    case 503:
    case 502:
    case 500:
      return createProviderError(
        PROVIDER_ERROR_CODES.SERVICE_UNAVAILABLE,
        `Service unavailable: ${message}`,
        true,
        'high',
      );
    default:
      return createProviderError(
        PROVIDER_ERROR_CODES.PROVIDER_ERROR,
        message,
        status >= 500,
        'medium',
      );
  }
}

function convertMessages(
  messages: MessageRequest['messages'],
): OpenAI.Chat.Completions.ChatCompletionMessageParam[] {
  return messages.map((msg) => {
    const content = typeof msg.content === 'string'
      ? msg.content
      : msg.content
          .filter((part) => part.type === 'text' && part.text !== undefined)
          .map((part) => part.text!)
          .join('');

    if (msg.role === 'system') {
      return { role: 'system' as const, content };
    }
    if (msg.role === 'assistant') {
      return { role: 'assistant' as const, content };
    }
    if (msg.role === 'tool' && msg.tool_call_id) {
      return { role: 'tool' as const, content, tool_call_id: msg.tool_call_id };
    }
    return { role: 'user' as const, content };
  });
}

function convertTools(
  tools: MessageRequest['tools'] | undefined,
): OpenAI.Chat.Completions.ChatCompletionTool[] | undefined {
  if (!tools || tools.length === 0) return undefined;
  return tools.map((tool) => ({
    type: 'function' as const,
    function: {
      name: tool.name,
      description: tool.description,
      parameters: tool.input_schema,
    },
  }));
}

function extractUsage(usage: OpenAI.CompletionUsage | null | undefined): TokenUsage {
  return {
    inputTokens: usage?.prompt_tokens ?? 0,
    outputTokens: usage?.completion_tokens ?? 0,
    totalTokens: usage?.total_tokens ?? 0,
  };
}

function mapFinishReason(
  reason: string | null | undefined,
): MessageResponse['finishReason'] {
  switch (reason) {
    case 'stop': return 'stop';
    case 'length': return 'length';
    case 'tool_calls': return 'tool_calls';
    case 'content_filter': return 'content_filter';
    default: return 'stop';
  }
}

/**
 * OpenRouter provider — OpenAI-compatible LLM API gateway.
 *
 * Uses the `openai` package with `baseURL: 'https://openrouter.ai/api/v1'`.
 * Supports streaming, tool use, and model listing via the OpenRouter API.
 */
export class OpenRouterProvider implements IAIProvider {
  private client: OpenAI | null = null;
  private config: ProviderConfig | null = null;
  private cachedModels: ModelInfo[] | null = null;

  async initialize(config: ProviderConfig): Promise<void> {
    this.config = config;
    const defaultHeaders: Record<string, string> = {};

    const meta = config.metadata ?? {};
    if (typeof meta['httpReferer'] === 'string') {
      defaultHeaders['HTTP-Referer'] = meta['httpReferer'];
    }
    if (typeof meta['title'] === 'string') {
      defaultHeaders['X-OpenRouter-Title'] = meta['title'];
    }

    this.client = new OpenAI({
      apiKey: config.apiKey,
      baseURL: config.baseUrl ?? DEFAULT_BASE_URL,
      timeout: config.timeout ?? DEFAULT_TIMEOUT,
      maxRetries: 0, // we handle retries ourselves
      defaultHeaders,
    });
  }

  async sendMessage(
    request: MessageRequest,
    options?: StreamOptions,
  ): Promise<AsyncIterable<MessageChunk>> {
    this.ensureInitialized();
    const model = request.model ?? this.config!.model ?? 'openai/gpt-4o';

    const makeStream = async (): Promise<AsyncIterable<MessageChunk>> => {
      const params: OpenAI.Chat.Completions.ChatCompletionCreateParamsStreaming = {
        model,
        messages: convertMessages(request.messages),
        stream: true,
      };
      if (request.maxTokens !== undefined) params.max_tokens = request.maxTokens;
      if (request.temperature !== undefined) params.temperature = request.temperature;
      if (request.topP !== undefined) params.top_p = request.topP;
      const toolsConverted = convertTools(request.tools);
      if (toolsConverted !== undefined) params.tools = toolsConverted;

      const stream = await this.client!.chat.completions.create(params);

      const self = this;
      return {
        async *[Symbol.asyncIterator]() {
          try {
            for await (const chunk of stream) {
              const choice = chunk.choices?.[0];
              const deltaContent = choice?.delta?.content;
              const chunkUsage = chunk.usage;
              const toolCalls = choice?.delta?.tool_calls;

              const mapped: MessageChunk = {
                id: chunk.id,
                model: chunk.model,
                finishReason: mapFinishReason(choice?.finish_reason),
              };
              if (deltaContent != null) mapped.delta = deltaContent;
              if (chunkUsage) mapped.usage = extractUsage(chunkUsage);
              if (toolCalls) {
                mapped.tool_calls = toolCalls.map((tc) => ({
                  id: tc.id ?? '',
                  type: tc.type ?? 'function',
                  function: {
                    name: tc.function?.name ?? '',
                    arguments: tc.function?.arguments ?? '',
                  },
                }));
              }

              if (options?.onChunk) {
                await options.onChunk(mapped);
              }

              yield mapped;
            }

            if (options?.onComplete) {
              await options.onComplete();
            }
          } catch (err) {
            const mapped = self.mapError(err);
            if (options?.onError) {
              await options.onError(mapped);
            }
            throw mapped;
          }
        },
      };
    };

    return this.withRetry(makeStream);
  }

  async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
    this.ensureInitialized();
    const model = request.model ?? this.config!.model ?? 'openai/gpt-4o';

    return this.withRetry(async () => {
      const params: OpenAI.Chat.Completions.ChatCompletionCreateParamsNonStreaming = {
        model,
        messages: convertMessages(request.messages),
        stream: false,
      };
      if (request.maxTokens !== undefined) params.max_tokens = request.maxTokens;
      if (request.temperature !== undefined) params.temperature = request.temperature;
      if (request.topP !== undefined) params.top_p = request.topP;
      const toolsConverted = convertTools(request.tools);
      if (toolsConverted !== undefined) params.tools = toolsConverted;

      const completion = await this.client!.chat.completions.create(params);

      const choice = completion.choices[0];
      const response: MessageResponse = {
        id: completion.id,
        content: choice?.message?.content ?? '',
        model: completion.model,
        usage: extractUsage(completion.usage),
        finishReason: mapFinishReason(choice?.finish_reason),
      };

      const toolCalls = choice?.message?.tool_calls;
      if (toolCalls) {
        // openai v6: tool_calls is a discriminated union of function and custom tool calls.
        // We only support function tool calls — filter and narrow.
        response.tool_calls = toolCalls
          .filter((tc): tc is Extract<typeof tc, { type: 'function' }> => tc.type === 'function')
          .map((tc) => ({
            id: tc.id,
            type: tc.type,
            function: {
              name: tc.function.name,
              arguments: tc.function.arguments,
            },
          }));
      }

      if (request.metadata) {
        response.metadata = request.metadata;
      }

      return response;
    });
  }

  getCapabilities(): ProviderCapabilities {
    return {
      supportsStreaming: true,
      supportsImages: true,
      supportsTools: true,
      maxInputTokens: 200_000,
      maxOutputTokens: 16_384,
      supportedModels: this.cachedModels ?? [],
      features: {
        parallelToolUse: true,
      },
    };
  }

  async getModels(): Promise<ModelInfo[]> {
    this.ensureInitialized();

    const response = await this.client!.models.list();
    const models: ModelInfo[] = [];

    for await (const m of response) {
      const contextLength = (m as unknown as Record<string, unknown>)['context_length'];
      models.push({
        id: m.id,
        name: m.id,
        maxTokens: typeof contextLength === 'number' ? contextLength : 4096,
        supportsStreaming: true,
        supportsImages: false,
        supportsTools: true,
      });
    }

    this.cachedModels = models;
    return models;
  }

  async dispose(): Promise<void> {
    this.client = null;
    this.config = null;
    this.cachedModels = null;
  }

  private ensureInitialized(): void {
    if (!this.client || !this.config) {
      throw createProviderError(
        PROVIDER_ERROR_CODES.PROVIDER_ERROR,
        'OpenRouter provider not initialized. Call initialize() first.',
        false,
        'critical',
      );
    }
  }

  private mapError(err: unknown): ProviderError {
    if (err instanceof OpenAI.APIError) {
      return mapHttpError(err.status ?? 500, err.message);
    }
    if (err instanceof Error && err.message.includes('timeout')) {
      return createProviderError(
        PROVIDER_ERROR_CODES.TIMEOUT,
        err.message,
        true,
        'medium',
      );
    }
    const message = err instanceof Error ? err.message : String(err);
    return createProviderError(
      PROVIDER_ERROR_CODES.PROVIDER_ERROR,
      message,
      false,
      'medium',
    );
  }

  private async withRetry<T>(fn: () => Promise<T>): Promise<T> {
    const maxRetries = this.config?.maxRetries ?? DEFAULT_MAX_RETRIES;
    const baseDelay = this.config?.retryDelay ?? DEFAULT_RETRY_DELAY;

    for (let attempt = 0; attempt <= maxRetries; attempt++) {
      try {
        return await fn();
      } catch (err) {
        const mapped = this.mapError(err);
        if (!mapped.retryable || attempt >= maxRetries) {
          throw mapped;
        }
        const delay = Math.min(baseDelay * Math.pow(2, attempt), 30_000);
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }

    // Unreachable, but satisfies TypeScript
    throw createProviderError(
      PROVIDER_ERROR_CODES.PROVIDER_ERROR,
      'Retry logic exhausted',
      false,
    );
  }
}
