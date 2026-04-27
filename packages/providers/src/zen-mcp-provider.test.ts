import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { ZenMCPProvider } from './zen-mcp-provider.js';
import type { MessageRequest } from './types.js';

// Mock the MCP SDK
const mockConnect = vi.fn();
const mockClose = vi.fn();
const mockCallTool = vi.fn();
const mockListTools = vi.fn();

vi.mock('@modelcontextprotocol/sdk/client/index.js', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  Client: vi.fn().mockImplementation(function () {
    return {
      connect: mockConnect,
      close: mockClose,
      callTool: mockCallTool,
      listTools: mockListTools,
    };
  }),
}));

vi.mock('@modelcontextprotocol/sdk/client/stdio.js', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  StdioClientTransport: vi.fn().mockImplementation(function () {
    return {
      close: vi.fn().mockResolvedValue(undefined),
    };
  }),
}));

describe('ZenMCPProvider', () => {
  let provider: ZenMCPProvider;

  beforeEach(() => {
    provider = new ZenMCPProvider();
    vi.clearAllMocks();

    // Default mock implementations
    mockConnect.mockResolvedValue(undefined);
    mockClose.mockResolvedValue(undefined);
    mockListTools.mockResolvedValue({
      tools: [
        { name: 'chat', description: 'Chat with LLM' },
        { name: 'listmodels', description: 'List models' },
      ],
    });
    mockCallTool.mockResolvedValue({
      content: [{ type: 'text', text: 'Hello from Zen MCP!' }],
    });
  });

  afterEach(async () => {
    await provider.dispose();
  });

  describe('initialize', () => {
    it('should create MCP client and connect', async () => {
      const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
      const { StdioClientTransport } = await import(
        '@modelcontextprotocol/sdk/client/stdio.js'
      );

      await provider.initialize({
        apiKey: 'test-key',
        metadata: { command: 'node', args: ['server.js'] },
      });

      expect(StdioClientTransport).toHaveBeenCalledWith(
        expect.objectContaining({
          command: 'node',
          args: ['server.js'],
        }),
      );
      expect(Client).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'tamma-zen-mcp' }),
      );
      expect(mockConnect).toHaveBeenCalledTimes(1);
      expect(mockListTools).toHaveBeenCalledTimes(1);
    });

    it('should use default command and args when not specified', async () => {
      const { StdioClientTransport } = await import(
        '@modelcontextprotocol/sdk/client/stdio.js'
      );

      await provider.initialize({ apiKey: 'test-key' });

      expect(StdioClientTransport).toHaveBeenCalledWith(
        expect.objectContaining({
          command: 'npx',
          args: ['zen-mcp-server-199bio'],
        }),
      );
    });

    it('should pass env vars including API key', async () => {
      const { StdioClientTransport } = await import(
        '@modelcontextprotocol/sdk/client/stdio.js'
      );

      await provider.initialize({
        apiKey: 'my-key',
        metadata: { env: { OPENAI_API_KEY: 'sk-test' } },
      });

      expect(StdioClientTransport).toHaveBeenCalledWith(
        expect.objectContaining({
          env: expect.objectContaining({
            ZEN_MCP_API_KEY: 'my-key',
            OPENAI_API_KEY: 'sk-test',
          }),
        }),
      );
    });
  });

  describe('sendMessageSync', () => {
    const request: MessageRequest = {
      messages: [
        { role: 'user', content: 'What is 2+2?' },
      ],
    };

    it('should call MCP chat tool and return response', async () => {
      await provider.initialize({ apiKey: 'test-key' });

      const response = await provider.sendMessageSync(request);

      expect(response.content).toBe('Hello from Zen MCP!');
      expect(response.finishReason).toBe('stop');
      expect(mockCallTool).toHaveBeenCalledWith({
        name: 'chat',
        arguments: expect.objectContaining({
          prompt: expect.stringContaining('What is 2+2?'),
        }),
      });
    });

    it('should pass model and temperature to MCP tool', async () => {
      await provider.initialize({ apiKey: 'test-key' });

      await provider.sendMessageSync({
        ...request,
        model: 'gpt-4o',
        maxTokens: 1000,
        temperature: 0.7,
      });

      expect(mockCallTool).toHaveBeenCalledWith({
        name: 'chat',
        arguments: expect.objectContaining({
          model: 'gpt-4o',
          max_tokens: 1000,
          temperature: 0.7,
        }),
      });
    });

    it('should format multi-message conversation as prompt', async () => {
      await provider.initialize({ apiKey: 'test-key' });

      await provider.sendMessageSync({
        messages: [
          { role: 'system', content: 'You are helpful.' },
          { role: 'user', content: 'Hello' },
          { role: 'assistant', content: 'Hi there!' },
          { role: 'user', content: 'How are you?' },
        ],
      });

      const call = mockCallTool.mock.calls[0][0];
      expect(call.arguments.prompt).toContain('system: You are helpful.');
      expect(call.arguments.prompt).toContain('user: Hello');
      expect(call.arguments.prompt).toContain('assistant: Hi there!');
      expect(call.arguments.prompt).toContain('user: How are you?');
    });

    it('should throw if not initialized', async () => {
      await expect(provider.sendMessageSync(request)).rejects.toThrow(
        'not initialized',
      );
    });

    it('should throw on MCP tool call failure', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      mockCallTool.mockRejectedValue(new Error('Tool not found'));

      await expect(provider.sendMessageSync(request)).rejects.toThrow(
        'Zen MCP tool call failed',
      );
    });

    it('should use zen_chat tool when chat is not available', async () => {
      mockListTools.mockResolvedValue({
        tools: [{ name: 'zen_chat', description: 'Chat' }],
      });
      await provider.initialize({ apiKey: 'test-key' });

      await provider.sendMessageSync(request);

      expect(mockCallTool).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'zen_chat' }),
      );
    });
  });

  describe('sendMessage (streaming wrapper)', () => {
    it('should yield single chunk wrapping sync response', async () => {
      await provider.initialize({ apiKey: 'test-key' });

      const request: MessageRequest = {
        messages: [{ role: 'user', content: 'Test' }],
      };

      const iterable = await provider.sendMessage(request);
      const chunks: any[] = [];
      for await (const chunk of iterable) {
        chunks.push(chunk);
      }

      expect(chunks).toHaveLength(1);
      expect(chunks[0].content).toBe('Hello from Zen MCP!');
      expect(chunks[0].delta).toBe('Hello from Zen MCP!');
    });

    it('should call onChunk and onComplete callbacks', async () => {
      await provider.initialize({ apiKey: 'test-key' });

      const onChunk = vi.fn();
      const onComplete = vi.fn();
      const onError = vi.fn();

      const iterable = await provider.sendMessage(
        { messages: [{ role: 'user', content: 'Test' }] },
        { onChunk, onComplete, onError },
      );

      for await (const _chunk of iterable) {
        // consume
      }

      expect(onChunk).toHaveBeenCalledTimes(1);
      expect(onComplete).toHaveBeenCalledTimes(1);
      expect(onError).not.toHaveBeenCalled();
    });
  });

  describe('getCapabilities', () => {
    it('should report no streaming support', () => {
      const caps = provider.getCapabilities();
      expect(caps.supportsStreaming).toBe(false);
      expect(caps.supportsImages).toBe(false);
      expect(caps.supportsTools).toBe(false);
    });
  });

  describe('getModels', () => {
    it('should call listmodels MCP tool when available', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      mockCallTool.mockResolvedValue({
        content: [
          {
            type: 'text',
            text: JSON.stringify([
              { id: 'gpt-4o', name: 'GPT-4o' },
              { id: 'claude-3-opus', name: 'Claude 3 Opus' },
            ]),
          },
        ],
      });

      const models = await provider.getModels();
      expect(models).toHaveLength(2);
      expect(models[0].id).toBe('gpt-4o');
      expect(models[1].name).toBe('Claude 3 Opus');
    });

    it('should return empty array when listmodels tool is not available', async () => {
      mockListTools.mockResolvedValue({ tools: [{ name: 'chat' }] });
      await provider.initialize({ apiKey: 'test-key' });

      const models = await provider.getModels();
      expect(models).toHaveLength(0);
    });

    it('should return empty array when listmodels fails', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      mockCallTool.mockRejectedValue(new Error('Unexpected error'));

      const models = await provider.getModels();
      expect(models).toHaveLength(0);
    });
  });

  describe('dispose', () => {
    it('should close MCP client and transport', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      await provider.dispose();

      expect(mockClose).toHaveBeenCalledTimes(1);
    });

    it('should handle close errors gracefully', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      mockClose.mockRejectedValue(new Error('Already closed'));

      await expect(provider.dispose()).resolves.toBeUndefined();
    });

    it('should make provider unusable after dispose', async () => {
      await provider.initialize({ apiKey: 'test-key' });
      await provider.dispose();

      await expect(
        provider.sendMessageSync({ messages: [{ role: 'user', content: 'test' }] }),
      ).rejects.toThrow('not initialized');
    });
  });
});
