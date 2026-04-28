import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { OpenRouterProvider } from './openrouter-provider.js';
import type { ProviderConfig, MessageRequest } from './types.js';

// Mock the openai module — factory must be self-contained (hoisted)
const mockCreate = vi.fn();
const mockModelsList = vi.fn();

vi.mock('openai', () => {
  class APIError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
      this.name = 'APIError';
    }
  }

  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  const OpenAIMock: any = vi.fn().mockImplementation(function () {
    return {
      chat: {
        completions: {
          create: mockCreate,
        },
      },
      models: {
        list: mockModelsList,
      },
    };
  });

  // Real OpenAI exposes APIError as static property on the class
  OpenAIMock.APIError = APIError;

  return {
    default: OpenAIMock,
    APIError,
  };
});

const baseConfig: ProviderConfig = {
  apiKey: 'test-openrouter-key',
  metadata: {
    httpReferer: 'https://tamma.dev',
    title: 'Tamma',
  },
};

/** Get the MockAPIError class from the mocked module */
async function getAPIError(): Promise<new (status: number, message: string) => Error & { status: number }> {
  const mod = await import('openai');
  return (mod as any).APIError;
}

function createSyncResponse(content: string, model = 'openai/gpt-4o') {
  return {
    id: 'chatcmpl-test-123',
    model,
    choices: [
      {
        message: { role: 'assistant', content, tool_calls: undefined },
        finish_reason: 'stop',
      },
    ],
    usage: { prompt_tokens: 10, completion_tokens: 20, total_tokens: 30 },
  };
}

function createStreamChunks(content: string, model = 'openai/gpt-4o') {
  const words = content.split(' ');
  return {
    async *[Symbol.asyncIterator]() {
      for (const word of words) {
        yield {
          id: 'chatcmpl-stream-123',
          model,
          choices: [
            {
              delta: { content: word + ' ', role: 'assistant' },
              finish_reason: null,
            },
          ],
        };
      }
      yield {
        id: 'chatcmpl-stream-123',
        model,
        choices: [{ delta: {}, finish_reason: 'stop' }],
        usage: { prompt_tokens: 10, completion_tokens: 20, total_tokens: 30 },
      };
    },
  };
}

describe('OpenRouterProvider', () => {
  let provider: OpenRouterProvider;

  beforeEach(async () => {
    provider = new OpenRouterProvider();
    vi.clearAllMocks();
  });

  afterEach(async () => {
    await provider.dispose();
  });

  describe('initialize', () => {
    it('should create OpenAI client with OpenRouter base URL', async () => {
      const OpenAI = (await import('openai')).default;
      await provider.initialize(baseConfig);

      expect(OpenAI).toHaveBeenCalledWith(
        expect.objectContaining({
          apiKey: 'test-openrouter-key',
          baseURL: 'https://openrouter.ai/api/v1',
          defaultHeaders: {
            'HTTP-Referer': 'https://tamma.dev',
            'X-OpenRouter-Title': 'Tamma',
          },
        }),
      );
    });

    it('should use custom baseUrl when provided', async () => {
      const OpenAI = (await import('openai')).default;
      await provider.initialize({ ...baseConfig, baseUrl: 'https://custom.api/v1' });

      expect(OpenAI).toHaveBeenCalledWith(
        expect.objectContaining({
          baseURL: 'https://custom.api/v1',
        }),
      );
    });
  });

  describe('sendMessageSync', () => {
    const request: MessageRequest = {
      messages: [
        { role: 'user', content: 'Hello, what is 2+2?' },
      ],
    };

    it('should return a complete response', async () => {
      await provider.initialize(baseConfig);
      mockCreate.mockResolvedValue(createSyncResponse('4'));

      const response = await provider.sendMessageSync(request);

      expect(response.content).toBe('4');
      expect(response.model).toBe('openai/gpt-4o');
      expect(response.usage.inputTokens).toBe(10);
      expect(response.usage.outputTokens).toBe(20);
      expect(response.finishReason).toBe('stop');
    });

    it('should pass model, maxTokens, temperature to the API', async () => {
      await provider.initialize(baseConfig);
      mockCreate.mockResolvedValue(createSyncResponse('test'));

      await provider.sendMessageSync({
        ...request,
        model: 'anthropic/claude-3-opus',
        maxTokens: 1000,
        temperature: 0.5,
      });

      expect(mockCreate).toHaveBeenCalledWith(
        expect.objectContaining({
          model: 'anthropic/claude-3-opus',
          max_tokens: 1000,
          temperature: 0.5,
          stream: false,
        }),
      );
    });

    it('should throw on 401 with INVALID_API_KEY code', async () => {
      await provider.initialize({ ...baseConfig, maxRetries: 0 });
      const APIError = await getAPIError();
      mockCreate.mockRejectedValue(new APIError(401, 'Invalid key'));

      await expect(provider.sendMessageSync(request)).rejects.toMatchObject({
        code: 'INVALID_API_KEY',
        retryable: false,
      });
    });

    it('should throw on 429 with RATE_LIMIT_EXCEEDED code', async () => {
      await provider.initialize({ ...baseConfig, maxRetries: 0 });
      const APIError = await getAPIError();
      mockCreate.mockRejectedValue(new APIError(429, 'Rate limited'));

      await expect(provider.sendMessageSync(request)).rejects.toMatchObject({
        code: 'RATE_LIMIT_EXCEEDED',
        retryable: true,
      });
    });

    it('should throw on 402 with QUOTA_EXCEEDED code', async () => {
      await provider.initialize({ ...baseConfig, maxRetries: 0 });
      const APIError = await getAPIError();
      mockCreate.mockRejectedValue(new APIError(402, 'Quota exceeded'));

      await expect(provider.sendMessageSync(request)).rejects.toMatchObject({
        code: 'QUOTA_EXCEEDED',
        retryable: false,
      });
    });

    it('should throw if not initialized', async () => {
      await expect(provider.sendMessageSync(request)).rejects.toThrow(
        'not initialized',
      );
    });

    it('should retry transient errors then succeed', async () => {
      await provider.initialize({ ...baseConfig, maxRetries: 2, retryDelay: 10 });
      const APIError = await getAPIError();

      mockCreate
        .mockRejectedValueOnce(new APIError(503, 'Service unavailable'))
        .mockResolvedValueOnce(createSyncResponse('Recovered'));

      const response = await provider.sendMessageSync(request);
      expect(response.content).toBe('Recovered');
      expect(mockCreate).toHaveBeenCalledTimes(2);
    });

    it('should handle tool calls in response', async () => {
      await provider.initialize(baseConfig);
      mockCreate.mockResolvedValue({
        id: 'chatcmpl-tools',
        model: 'openai/gpt-4o',
        choices: [
          {
            message: {
              role: 'assistant',
              content: null,
              tool_calls: [
                {
                  id: 'call-123',
                  type: 'function',
                  function: { name: 'search', arguments: '{"q":"test"}' },
                },
              ],
            },
            finish_reason: 'tool_calls',
          },
        ],
        usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
      });

      const response = await provider.sendMessageSync({
        ...request,
        tools: [{ name: 'search', description: 'Search', input_schema: {} }],
      });

      expect(response.finishReason).toBe('tool_calls');
      expect(response.tool_calls).toHaveLength(1);
      expect(response.tool_calls![0].function.name).toBe('search');
    });
  });

  describe('sendMessage (streaming)', () => {
    const request: MessageRequest = {
      messages: [{ role: 'user', content: 'Stream test' }],
    };

    it('should yield chunks from streaming response', async () => {
      await provider.initialize(baseConfig);
      mockCreate.mockResolvedValue(createStreamChunks('Hello world'));

      const iterable = await provider.sendMessage(request);
      const chunks: any[] = [];
      for await (const chunk of iterable) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThanOrEqual(2);
      expect(chunks.some((c: any) => c.delta?.includes('Hello'))).toBe(true);
    });

    it('should call onChunk and onComplete callbacks', async () => {
      await provider.initialize(baseConfig);
      mockCreate.mockResolvedValue(createStreamChunks('Test'));

      const onChunk = vi.fn();
      const onComplete = vi.fn();
      const onError = vi.fn();

      const iterable = await provider.sendMessage(request, {
        onChunk,
        onComplete,
        onError,
      });

      for await (const _chunk of iterable) {
        // consume
      }

      expect(onChunk).toHaveBeenCalled();
      expect(onComplete).toHaveBeenCalledTimes(1);
      expect(onError).not.toHaveBeenCalled();
    });
  });

  describe('getModels', () => {
    it('should return models from OpenRouter API', async () => {
      await provider.initialize(baseConfig);

      mockModelsList.mockResolvedValue({
        async *[Symbol.asyncIterator]() {
          yield { id: 'anthropic/claude-3-opus', context_length: 200000 };
          yield { id: 'openai/gpt-4o', context_length: 128000 };
        },
      });

      const models = await provider.getModels();
      expect(models).toHaveLength(2);
      expect(models[0].id).toBe('anthropic/claude-3-opus');
      expect(models[0].maxTokens).toBe(200000);
    });
  });

  describe('getCapabilities', () => {
    it('should report streaming and tools support', () => {
      const caps = provider.getCapabilities();
      expect(caps.supportsStreaming).toBe(true);
      expect(caps.supportsTools).toBe(true);
    });
  });

  describe('dispose', () => {
    it('should clear internal state', async () => {
      await provider.initialize(baseConfig);
      await provider.dispose();

      await expect(
        provider.sendMessageSync({ messages: [{ role: 'user', content: 'test' }] }),
      ).rejects.toThrow('not initialized');
    });
  });
});
