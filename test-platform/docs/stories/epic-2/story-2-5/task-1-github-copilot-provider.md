# Task 1: GitHub Copilot Provider Implementation

## Overview

This task implements the GitHub Copilot provider integration, enabling access to GitHub's AI-powered code completion and chat models through the unified provider interface.

## Acceptance Criteria

### 1.1: Implement GitHub authentication and API integration

- [ ] Integrate with GitHub OAuth and Personal Access Token authentication
- [ ] Implement GitHub Copilot API client using official GitHub SDK
- [ ] Add support for GitHub Enterprise Server instances
- [ ] Implement token refresh and validation logic
- [ ] Add GitHub API rate limit handling

### 1.2: Add Copilot model support (Copilot, Copilot Chat)

- [ ] Implement support for GitHub Copilot code completion model
- [ ] Add support for GitHub Copilot Chat conversational model
- [ ] Implement model-specific parameter handling
- [ ] Add model capability detection and metadata
- [ ] Support model switching and selection

### 1.3: Implement streaming response handling

- [ ] Implement real-time streaming for code completion
- [ ] Add support for chat message streaming
- [ ] Implement backpressure handling for large responses
- [ ] Add streaming error handling and recovery
- [ ] Support streaming cancellation and timeout

### 1.4: Add rate limiting and quota management

- [ ] Implement GitHub API rate limit detection
- [ ] Add automatic rate limit handling with backoff
- [ ] Implement quota tracking and usage monitoring
- [ ] Add rate limit warning and alerting
- [ ] Support priority queue for critical requests

### 1.5: Create configuration schema and validation

- [ ] Define GitHub Copilot configuration schema
- [ ] Implement configuration validation with detailed error messages
- [ ] Add support for multiple GitHub instances/organizations
- [ ] Implement environment-specific configuration
- [ ] Add configuration hot-reload support

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/github-copilot-provider.ts
export class GitHubCopilotProvider implements IAIProvider {
  private readonly client: Octokit;
  private readonly config: GitHubCopilotConfig;
  private readonly rateLimiter: RateLimiter;
  private readonly tokenManager: TokenManager;
  private readonly logger: Logger;

  constructor(config: GitHubCopilotConfig) {
    this.config = this.validateConfig(config);
    this.client = this.createGitHubClient();
    this.rateLimiter = new RateLimiter(this.config.rateLimits);
    this.tokenManager = new TokenManager(this.config.auth);
    this.logger = new Logger({ service: 'github-copilot-provider' });
  }

  async initialize(): Promise<void> {
    await this.tokenManager.validateToken();
    await this.detectCapabilities();
    this.logger.info('GitHub Copilot provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    await this.rateLimiter.acquire();

    try {
      const model = await this.getModel(request.model);

      switch (model.type) {
        case 'copilot':
          return this.handleCodeCompletion(request);
        case 'copilot-chat':
          return this.handleChatCompletion(request);
        default:
          throw new Error(`Unsupported model type: ${model.type}`);
      }
    } catch (error) {
      this.handleProviderError(error);
      throw error;
    }
  }

  async getCapabilities(): Promise<ProviderCapabilities> {
    return {
      models: await this.getAvailableModels(),
      features: {
        streaming: true,
        functionCalling: false,
        multimodal: false,
        toolUse: false,
      },
      limits: {
        maxTokens: 8192,
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async dispose(): Promise<void> {
    await this.rateLimiter.dispose();
    this.logger.info('GitHub Copilot provider disposed');
  }
}
```

### Authentication Implementation

```typescript
// src/providers/implementations/github-auth.ts
export class GitHubAuthManager {
  private readonly config: GitHubAuthConfig;
  private tokenCache: Map<string, TokenInfo>;

  constructor(config: GitHubAuthConfig) {
    this.config = config;
    this.tokenCache = new Map();
  }

  async authenticate(): Promise<string> {
    switch (this.config.type) {
      case 'pat':
        return this.authenticateWithPAT();
      case 'oauth':
        return this.authenticateWithOAuth();
      case 'github-app':
        return this.authenticateWithGitHubApp();
      default:
        throw new Error(`Unsupported auth type: ${this.config.type}`);
    }
  }

  private async authenticateWithPAT(): Promise<string> {
    const token = this.config.personalAccessToken;

    // Validate token
    const octokit = new Octokit({ auth: token });
    const { data } = await octokit.rest.users.getAuthenticated();

    this.cacheToken(token, {
      user: data.login,
      scopes: data.scopes || [],
      expiresAt: null,
    });

    return token;
  }

  private async authenticateWithOAuth(): Promise<string> {
    const { accessToken, refreshToken } = await this.exchangeCodeForToken();

    // Store tokens securely
    await this.secureStore.store('github_oauth_token', accessToken);
    if (refreshToken) {
      await this.secureStore.store('github_refresh_token', refreshToken);
    }

    return accessToken;
  }

  private async authenticateWithGitHubApp(): Promise<string> {
    const jwt = this.generateGitHubAppJWT();
    const installationToken = await this.getInstallationToken(jwt);

    return installationToken;
  }

  async refreshTokenIfNeeded(): Promise<string> {
    const token = await this.secureStore.get('github_oauth_token');
    if (!token) {
      throw new Error('No token found');
    }

    if (await this.isTokenExpired(token)) {
      return this.refreshToken();
    }

    return token;
  }

  private async refreshToken(): Promise<string> {
    const refreshToken = await this.secureStore.get('github_refresh_token');
    if (!refreshToken) {
      throw new Error('No refresh token found');
    }

    const response = await fetch('https://github.com/login/oauth/access_token', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
      },
      body: JSON.stringify({
        client_id: this.config.oauth.clientId,
        client_secret: this.config.oauth.clientSecret,
        grant_type: 'refresh_token',
        refresh_token: refreshToken,
      }),
    });

    const data = await response.json();

    if (data.error) {
      throw new Error(`Token refresh failed: ${data.error_description}`);
    }

    await this.secureStore.store('github_oauth_token', data.access_token);
    if (data.refresh_token) {
      await this.secureStore.store('github_refresh_token', data.refresh_token);
    }

    return data.access_token;
  }
}
```

### Model Implementation

````typescript
// src/providers/implementations/github-models.ts
export class GitHubModelManager {
  private readonly client: Octokit;
  private readonly logger: Logger;

  constructor(client: Octokit) {
    this.client = client;
    this.logger = new Logger({ service: 'github-model-manager' });
  }

  async getAvailableModels(): Promise<ModelInfo[]> {
    return [
      {
        id: 'copilot',
        name: 'GitHub Copilot',
        provider: 'github-copilot',
        version: '1.0',
        capabilities: {
          maxTokens: 8192,
          streaming: true,
          functionCalling: false,
          multimodal: false,
          languages: ['javascript', 'python', 'typescript', 'java', 'c++', 'go', 'rust'],
          contextWindow: 4000,
        },
        metadata: {
          description: 'GitHub Copilot code completion model',
          category: 'code-completion',
          pricing: { currency: 'USD', perToken: 0.0001 },
          tags: ['code', 'completion', 'productivity'],
        },
      },
      {
        id: 'copilot-chat',
        name: 'GitHub Copilot Chat',
        provider: 'github-copilot',
        version: '1.0',
        capabilities: {
          maxTokens: 8192,
          streaming: true,
          functionCalling: false,
          multimodal: false,
          languages: ['javascript', 'python', 'typescript', 'java', 'c++', 'go', 'rust'],
          contextWindow: 8000,
        },
        metadata: {
          description: 'GitHub Copilot conversational AI model',
          category: 'chat',
          pricing: { currency: 'USD', perToken: 0.0002 },
          tags: ['chat', 'conversation', 'coding'],
        },
      },
    ];
  }

  async handleCodeCompletion(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    const prompt = this.extractCodeContext(request);

    const completion = await this.client.request('POST /copilot/completions', {
      prompt,
      max_tokens: request.maxTokens || 1000,
      temperature: request.temperature || 0.1,
      stop: request.stopSequences,
      stream: true,
    });

    return this.streamCompletion(completion);
  }

  async handleChatCompletion(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    const messages = this.formatChatMessages(request.messages);

    const chat = await this.client.request('POST /copilot/chat/completions', {
      messages,
      max_tokens: request.maxTokens || 2000,
      temperature: request.temperature || 0.7,
      stream: true,
    });

    return this.streamChatCompletion(chat);
  }

  private extractCodeContext(request: MessageRequest): string {
    const message = request.messages[request.messages.length - 1];

    // Extract code context from the message
    const codeMatch = message.content.match(/```(\w+)?\n([\s\S]*?)\n```/);
    if (codeMatch) {
      return codeMatch[2];
    }

    // If no code block, use the entire content
    return message.content;
  }

  private formatChatMessages(messages: Message[]): any[] {
    return messages.map((msg) => ({
      role: msg.role,
      content: msg.content,
    }));
  }

  private async *streamCompletion(completion: any): AsyncIterable<MessageChunk> {
    for await (const chunk of completion) {
      if (chunk.choices && chunk.choices[0]) {
        const delta = chunk.choices[0].text || '';

        yield {
          content: delta,
          done: chunk.choices[0].finish_reason === 'stop',
          metadata: {
            model: 'copilot',
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          },
        };
      }
    }
  }

  private async *streamChatCompletion(chat: any): AsyncIterable<MessageChunk> {
    for await (const chunk of chat) {
      if (chunk.choices && chunk.choices[0]) {
        const delta = chunk.choices[0].delta;

        yield {
          content: delta.content || '',
          done: chunk.choices[0].finish_reason === 'stop',
          metadata: {
            model: 'copilot-chat',
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          },
        };
      }
    }
  }
}
````

### Rate Limiting Implementation

```typescript
// src/providers/implementations/github-rate-limiter.ts
export class GitHubRateLimiter {
  private readonly config: RateLimitConfig;
  private readonly requestQueue: PriorityQueue<Request>;
  private readonly usageTracker: UsageTracker;
  private readonly logger: Logger;

  constructor(config: RateLimitConfig) {
    this.config = config;
    this.requestQueue = new PriorityQueue();
    this.usageTracker = new UsageTracker();
    this.logger = new Logger({ service: 'github-rate-limiter' });
  }

  async acquire(): Promise<void> {
    // Check current rate limit status
    const rateLimitStatus = await this.checkRateLimitStatus();

    if (rateLimitStatus.remaining === 0) {
      const waitTime = this.calculateWaitTime(rateLimitStatus.resetTime);
      this.logger.warn(`Rate limit exceeded, waiting ${waitTime}ms`);
      await this.sleep(waitTime);
    }

    // Add to queue and wait for turn
    await this.requestQueue.enqueue({
      priority: this.calculatePriority(),
      timestamp: Date.now(),
    });

    await this.waitForTurn();
  }

  private async checkRateLimitStatus(): Promise<RateLimitStatus> {
    try {
      const response = await fetch('https://api.github.com/rate_limit', {
        headers: {
          Authorization: `token ${this.config.token}`,
          'User-Agent': 'Tamma-Benchmarking',
        },
      });

      const data = await response.json();

      return {
        limit: data.resources.core.limit,
        remaining: data.resources.core.remaining,
        resetTime: data.resources.core.reset * 1000, // Convert to milliseconds
        used: data.resources.core.used,
      };
    } catch (error) {
      this.logger.error('Failed to check rate limit status', { error });
      // Return conservative defaults
      return {
        limit: 5000,
        remaining: 100,
        resetTime: Date.now() + 60000,
        used: 4900,
      };
    }
  }

  private calculateWaitTime(resetTime: number): number {
    const now = Date.now();
    const waitTime = resetTime - now;

    // Add buffer time to ensure reset
    return Math.max(waitTime + 1000, 0);
  }

  private calculatePriority(): number {
    // Priority based on request type and user tier
    if (this.config.tier === 'enterprise') {
      return 100;
    } else if (this.config.tier === 'pro') {
      return 50;
    } else {
      return 10;
    }
  }

  private async waitForTurn(): Promise<void> {
    // Implement priority queue waiting logic
    while (this.requestQueue.peek()?.priority !== this.calculatePriority()) {
      await this.sleep(100);
    }
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/github-copilot-config.schema.ts
export const GitHubCopilotConfigSchema = {
  type: 'object',
  required: ['auth', 'rateLimits'],
  properties: {
    auth: {
      type: 'object',
      required: ['type'],
      oneOf: [
        {
          title: 'Personal Access Token',
          properties: {
            type: { const: 'pat' },
            personalAccessToken: {
              type: 'string',
              minLength: 40,
              description: 'GitHub Personal Access Token with copilot scope',
            },
          },
        },
        {
          title: 'OAuth App',
          properties: {
            type: { const: 'oauth' },
            clientId: {
              type: 'string',
              description: 'GitHub OAuth App client ID',
            },
            clientSecret: {
              type: 'string',
              description: 'GitHub OAuth App client secret',
            },
            redirectUri: {
              type: 'string',
              format: 'uri',
              description: 'OAuth redirect URI',
            },
          },
        },
        {
          title: 'GitHub App',
          properties: {
            type: { const: 'github-app' },
            appId: {
              type: 'integer',
              description: 'GitHub App ID',
            },
            privateKey: {
              type: 'string',
              description: 'GitHub App private key (PEM format)',
            },
            installationId: {
              type: 'integer',
              description: 'GitHub App installation ID',
            },
          },
        },
      ],
    },
    rateLimits: {
      type: 'object',
      required: ['requestsPerMinute'],
      properties: {
        requestsPerMinute: {
          type: 'integer',
          minimum: 1,
          maximum: 5000,
          default: 60,
          description: 'Maximum requests per minute',
        },
        tokensPerMinute: {
          type: 'integer',
          minimum: 1000,
          maximum: 1000000,
          default: 100000,
          description: 'Maximum tokens per minute',
        },
        tier: {
          type: 'string',
          enum: ['free', 'pro', 'enterprise'],
          default: 'free',
          description: 'GitHub Copilot subscription tier',
        },
      },
    },
    enterprise: {
      type: 'object',
      properties: {
        enabled: {
          type: 'boolean',
          default: false,
        },
        baseUrl: {
          type: 'string',
          format: 'uri',
          description: 'GitHub Enterprise Server base URL',
        },
        apiVersion: {
          type: 'string',
          default: 'api/v3',
          description: 'GitHub Enterprise API version',
        },
      },
    },
    features: {
      type: 'object',
      properties: {
        streaming: {
          type: 'boolean',
          default: true,
        },
        codeContext: {
          type: 'boolean',
          default: true,
          description: 'Include code context in completions',
        },
        telemetry: {
          type: 'boolean',
          default: false,
          description: 'Enable usage telemetry',
        },
      },
    },
  },
};
```

### Error Handling

```typescript
// src/providers/implementations/github-error-handler.ts
export class GitHubErrorHandler {
  private readonly logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger;
  }

  handleError(error: any): TammaError {
    if (error.status === 401) {
      return new TammaError(
        'GITHUB_AUTHENTICATION_FAILED',
        'GitHub authentication failed. Please check your credentials.',
        { originalError: error.message },
        true,
        'high'
      );
    }

    if (error.status === 403) {
      if (error.message?.includes('rate limit')) {
        return new TammaError(
          'GITHUB_RATE_LIMIT_EXCEEDED',
          'GitHub API rate limit exceeded. Please try again later.',
          {
            resetTime: error.headers?.['x-ratelimit-reset'],
            limit: error.headers?.['x-ratelimit-limit'],
          },
          true,
          'medium'
        );
      }

      return new TammaError(
        'GITHUB_FORBIDDEN',
        'Access forbidden. Check your permissions.',
        { originalError: error.message },
        false,
        'high'
      );
    }

    if (error.status === 404) {
      return new TammaError(
        'GITHUB_NOT_FOUND',
        'GitHub resource not found.',
        { resource: error.request?.url },
        false,
        'medium'
      );
    }

    if (error.status === 422) {
      return new TammaError(
        'GITHUB_VALIDATION_FAILED',
        'Request validation failed.',
        { errors: error.response?.data?.errors },
        false,
        'medium'
      );
    }

    if (error.status >= 500) {
      return new TammaError(
        'GITHUB_SERVER_ERROR',
        'GitHub server error. Please try again later.',
        { status: error.status, message: error.message },
        true,
        'high'
      );
    }

    return new TammaError(
      'GITHUB_UNKNOWN_ERROR',
      `Unknown GitHub error: ${error.message}`,
      { originalError: error },
      false,
      'medium'
    );
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/github-copilot-provider.test.ts
describe('GitHubCopilotProvider', () => {
  let provider: GitHubCopilotProvider;
  let mockOctokit: jest.Mocked<Octokit>;

  beforeEach(() => {
    mockOctokit = new Octokit() as jest.Mocked<Octokit>;
    provider = new GitHubCopilotProvider({
      auth: {
        type: 'pat',
        personalAccessToken: 'test-token',
      },
      rateLimits: {
        requestsPerMinute: 60,
      },
    });
  });

  describe('authentication', () => {
    it('should authenticate with personal access token', async () => {
      mockOctokit.rest.users.getAuthenticated.mockResolvedValue({
        data: { login: 'testuser', scopes: ['copilot'] },
      } as any);

      await provider.initialize();

      expect(mockOctokit.rest.users.getAuthenticated).toHaveBeenCalled();
    });

    it('should handle authentication failure', async () => {
      mockOctokit.rest.users.getAuthenticated.mockRejectedValue({
        status: 401,
        message: 'Bad credentials',
      });

      await expect(provider.initialize()).rejects.toThrow('GITHUB_AUTHENTICATION_FAILED');
    });
  });

  describe('code completion', () => {
    it('should handle code completion requests', async () => {
      const request: MessageRequest = {
        model: 'copilot',
        messages: [{ role: 'user', content: 'function hello() {' }],
        maxTokens: 100,
      };

      const mockStream = createMockCompletionStream();
      mockOctokit.request.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks).toHaveLength(3);
      expect(chunks[0].content).toBe(' console.log("Hello World");');
    });
  });

  describe('rate limiting', () => {
    it('should respect rate limits', async () => {
      const rateLimiter = new GitHubRateLimiter({
        requestsPerMinute: 1,
      });

      const startTime = Date.now();

      await rateLimiter.acquire();
      await rateLimiter.acquire();

      const elapsed = Date.now() - startTime;
      expect(elapsed).toBeGreaterThan(1000); // At least 1 second between requests
    });
  });
});
```

### Integration Tests

````typescript
// tests/providers/github-copilot-integration.test.ts
describe('GitHub Copilot Integration', () => {
  let provider: GitHubCopilotProvider;

  beforeAll(async () => {
    if (!process.env.GITHUB_TOKEN_TEST) {
      throw new Error('GITHUB_TOKEN_TEST environment variable required');
    }

    provider = new GitHubCopilotProvider({
      auth: {
        type: 'pat',
        personalAccessToken: process.env.GITHUB_TOKEN_TEST,
      },
      rateLimits: {
        requestsPerMinute: 30,
      },
    });

    await provider.initialize();
  });

  it('should complete code successfully', async () => {
    const request: MessageRequest = {
      model: 'copilot',
      messages: [
        {
          role: 'user',
          content: '```javascript\nfunction fibonacci(n) {\n',
        },
      ],
      maxTokens: 200,
    };

    const chunks = [];
    for await (const chunk of provider.sendMessage(request)) {
      chunks.push(chunk);
    }

    expect(chunks.length).toBeGreaterThan(0);
    expect(chunks[chunks.length - 1].done).toBe(true);
  });

  it('should handle chat conversations', async () => {
    const request: MessageRequest = {
      model: 'copilot-chat',
      messages: [
        { role: 'user', content: 'What is the difference between let and const in JavaScript?' },
      ],
      maxTokens: 300,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('let');
    expect(response.content).toContain('const');
  });
});
````

## Success Metrics

### Performance Metrics

- Authentication time: < 500ms
- Code completion latency: < 2s
- Chat response latency: < 3s
- Streaming latency: < 100ms per chunk
- Rate limit accuracy: 99%+

### Reliability Metrics

- Authentication success rate: 99.9%
- API request success rate: 99.5%
- Error handling coverage: 100%
- Retry success rate: 95%

### Integration Metrics

- Model discovery accuracy: 100%
- Capability detection accuracy: 100%
- Configuration validation: 100%
- Test coverage: 95%+

## Dependencies

### External Dependencies

- `@octokit/core`: GitHub API client
- `@octokit/auth-oauth-app`: OAuth authentication
- `@octokit/auth-app`: GitHub App authentication
- `jsonwebtoken`: JWT generation for GitHub Apps

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### Credential Management

- Encrypt stored tokens at rest
- Use secure token storage (OS keychain)
- Implement token rotation
- Never log tokens or credentials

### API Security

- Validate all API responses
- Implement request signing
- Use HTTPS for all communications
- Implement request timeout limits

### Rate Limiting

- Respect GitHub rate limits
- Implement exponential backoff
- Monitor usage patterns
- Alert on unusual activity

## Deliverables

1. **GitHub Copilot Provider** (`src/providers/implementations/github-copilot-provider.ts`)
2. **Authentication Manager** (`src/providers/implementations/github-auth.ts`)
3. **Model Manager** (`src/providers/implementations/github-models.ts`)
4. **Rate Limiter** (`src/providers/implementations/github-rate-limiter.ts`)
5. **Error Handler** (`src/providers/implementations/github-error-handler.ts`)
6. **Configuration Schema** (`src/providers/configs/github-copilot-config.schema.ts`)
7. **Unit Tests** (`tests/providers/github-copilot-provider.test.ts`)
8. **Integration Tests** (`tests/providers/github-copilot-integration.test.ts`)
9. **Documentation** (`docs/providers/github-copilot.md`)

This implementation provides comprehensive GitHub Copilot integration with robust authentication, rate limiting, and error handling while maintaining compatibility with the unified provider interface.
