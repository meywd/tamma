import type {
  IAIProvider,
  ProviderConfig,
  ProviderCapabilities,
  MessageRequest,
  MessageResponse,
  MessageChunk,
  ModelInfo,
  StreamOptions,
} from './types.js';
import { PROVIDER_ERROR_CODES } from './types.js';

function createProviderError(
  code: string,
  message: string,
  retryable: boolean,
  severity: 'low' | 'medium' | 'high' | 'critical' = 'medium',
): Error & { code: string; retryable: boolean; severity: string } {
  const error = new Error(message) as Error & {
    code: string;
    retryable: boolean;
    severity: string;
  };
  error.code = code;
  error.retryable = retryable;
  error.severity = severity;
  return error;
}

/**
 * Minimal MCP type interfaces to avoid importing SDK types at the module level.
 * The actual SDK is dynamically imported in initialize().
 */
interface MCPClient {
  connect: (transport: unknown) => Promise<void>;
  close: () => Promise<void>;
  callTool: (params: { name: string; arguments?: Record<string, unknown> }) => Promise<{
    content: Array<{ type: string; text?: string }>;
  }>;
  listTools: () => Promise<{ tools: Array<{ name: string; description?: string }> }>;
}

/**
 * Zen MCP provider — wraps MCP tool calls into an LLM-like interface.
 *
 * Spawns a Zen MCP server process, connects as an MCP client, and translates
 * sendMessage/sendMessageSync calls into MCP `chat` tool invocations.
 *
 * MCP is request-response, so streaming wraps the sync response in a
 * single-chunk async generator.
 */
export class ZenMCPProvider implements IAIProvider {
  private mcpClient: MCPClient | null = null;
  private transport: { close?: () => Promise<void> } | null = null;
  private config: ProviderConfig | null = null;
  private availableTools: string[] = [];

  async initialize(config: ProviderConfig): Promise<void> {
    this.config = config;
    const meta = config.metadata ?? {};

    // Dynamic import of MCP SDK
    const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
    const { StdioClientTransport } = await import(
      '@modelcontextprotocol/sdk/client/stdio.js'
    );

    const command = (typeof meta['command'] === 'string' ? meta['command'] : 'npx');
    const args = (Array.isArray(meta['args']) ? meta['args'] : ['zen-mcp-server-199bio']) as string[];

    // Build env vars — pass through API keys from metadata
    const env: Record<string, string> = { ...process.env } as Record<string, string>;
    if (config.apiKey) {
      env['ZEN_MCP_API_KEY'] = config.apiKey;
    }
    // Forward any environment overrides from metadata
    const metaEnv = meta['env'];
    if (metaEnv && typeof metaEnv === 'object') {
      for (const [key, value] of Object.entries(metaEnv as Record<string, string>)) {
        env[key] = value;
      }
    }

    this.transport = new StdioClientTransport({ command, args, env });

    const client = new Client(
      { name: 'tamma-zen-mcp', version: '1.0.0' },
    );

    await client.connect(this.transport as unknown as Parameters<typeof client.connect>[0]);
    this.mcpClient = client as unknown as MCPClient;

    // Discover available tools
    try {
      const toolsResult = await this.mcpClient.listTools();
      this.availableTools = toolsResult.tools.map((t) => t.name);
    } catch {
      this.availableTools = [];
    }
  }

  async sendMessage(
    request: MessageRequest,
    options?: StreamOptions,
  ): Promise<AsyncIterable<MessageChunk>> {
    // MCP is request-response, so we wrap the sync call in a single-chunk generator
    const response = await this.sendMessageSync(request);

    const chunk: MessageChunk = {
      id: response.id,
      content: response.content,
      delta: response.content,
      model: response.model,
      usage: response.usage,
      finishReason: response.finishReason,
    };

    return {
      async *[Symbol.asyncIterator]() {
        if (options?.onChunk) {
          await options.onChunk(chunk);
        }
        yield chunk;
        if (options?.onComplete) {
          await options.onComplete();
        }
      },
    };
  }

  async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
    this.ensureInitialized();

    // Build the prompt from messages
    const prompt = request.messages
      .map((msg) => {
        const content = typeof msg.content === 'string'
          ? msg.content
          : msg.content
              .filter((part) => part.type === 'text' && part.text !== undefined)
              .map((part) => part.text!)
              .join('');
        return `${msg.role}: ${content}`;
      })
      .join('\n');

    // Call the chat tool on the MCP server
    const toolName = this.availableTools.includes('chat') ? 'chat' : 'zen_chat';
    const args: Record<string, unknown> = { prompt };

    if (request.model) {
      args['model'] = request.model;
    } else if (this.config?.model) {
      args['model'] = this.config.model;
    }
    if (request.maxTokens) {
      args['max_tokens'] = request.maxTokens;
    }
    if (request.temperature !== undefined) {
      args['temperature'] = request.temperature;
    }

    try {
      const result = await this.mcpClient!.callTool({
        name: toolName,
        arguments: args,
      });

      const text = result.content
        .filter((c) => c.type === 'text' && c.text !== undefined)
        .map((c) => c.text!)
        .join('');

      const response: MessageResponse = {
        id: `zen-${Date.now()}`,
        content: text,
        model: (request.model ?? this.config?.model ?? 'zen-mcp'),
        usage: {
          inputTokens: 0,
          outputTokens: 0,
          totalTokens: 0,
        },
        finishReason: 'stop',
      };
      if (request.metadata) {
        response.metadata = request.metadata;
      }
      return response;
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      throw createProviderError(
        PROVIDER_ERROR_CODES.PROVIDER_ERROR,
        `Zen MCP tool call failed: ${message}`,
        false,
        'high',
      );
    }
  }

  getCapabilities(): ProviderCapabilities {
    return {
      supportsStreaming: false,
      supportsImages: false,
      supportsTools: false,
      maxInputTokens: 128_000,
      maxOutputTokens: 8_192,
      supportedModels: [],
      features: {},
    };
  }

  async getModels(): Promise<ModelInfo[]> {
    this.ensureInitialized();

    // Try to call a listmodels tool if available
    if (this.availableTools.includes('listmodels')) {
      try {
        const result = await this.mcpClient!.callTool({ name: 'listmodels' });
        const text = result.content
          .filter((c) => c.type === 'text' && c.text !== undefined)
          .map((c) => c.text!)
          .join('');

        const parsed = JSON.parse(text) as Array<{ id: string; name?: string }>;
        return parsed.map((m) => ({
          id: m.id,
          name: m.name ?? m.id,
          maxTokens: 128_000,
          supportsStreaming: false,
          supportsImages: false,
          supportsTools: false,
        }));
      } catch {
        // Fall through to empty list
      }
    }

    return [];
  }

  async dispose(): Promise<void> {
    if (this.mcpClient) {
      try {
        await this.mcpClient.close();
      } catch {
        // Ignore close errors
      }
      this.mcpClient = null;
    }
    if (this.transport && typeof this.transport.close === 'function') {
      try {
        await this.transport.close();
      } catch {
        // Ignore close errors
      }
      this.transport = null;
    }
    this.config = null;
    this.availableTools = [];
  }

  private ensureInitialized(): void {
    if (!this.mcpClient || !this.config) {
      throw createProviderError(
        PROVIDER_ERROR_CODES.PROVIDER_ERROR,
        'Zen MCP provider not initialized. Call initialize() first.',
        false,
        'critical',
      );
    }
  }
}
