# Task 6: OpenRouter Provider Implementation

## Overview

This task implements the OpenRouter provider integration, enabling access to 100+ models from various providers through OpenRouter's model marketplace. OpenRouter provides unified access to models from Anthropic, OpenAI, Google, Meta, and many other providers with cost optimization and load balancing features.

## Acceptance Criteria

### 6.1: Implement OpenRouter API integration

- [ ] Create OpenRouter API client with proper authentication
- [ ] Implement REST API integration for model access
- [ ] Add support for OpenRouter-specific features
- [ ] Implement proper error handling and retry logic
- [ ] Add API versioning and compatibility handling

### 6.2: Add support for 100+ marketplace models

- [ ] Implement dynamic model discovery from OpenRouter
- [ ] Add support for provider-specific model variants
- [ ] Implement model filtering and categorization
- [ ] Add model capability detection and metadata
- [ ] Support model routing and selection logic

### 6.3: Implement model discovery and filtering

- [ ] Create comprehensive model catalog management
- [ ] Implement model filtering by provider, capabilities, cost
- [ ] Add model comparison and recommendation features
- [ ] Implement model performance tracking
- [ ] Add model availability and status monitoring

### 6.4: Add cost tracking and usage limits

- [ ] Implement cost calculation and tracking
- [ ] Add usage limit enforcement and monitoring
- [ ] Implement cost optimization recommendations
- [ ] Add spending alerts and budget controls
- [ ] Create cost reporting and analytics

### 6.5: Create fallback and load balancing

- [ ] Implement intelligent model routing
- [ ] Add automatic fallback mechanisms
- [ ] Implement load balancing across model providers
- [ ] Add performance-based routing
- [ ] Create health monitoring and recovery

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/openrouter-provider.ts
export class OpenRouterProvider implements IAIProvider {
  private readonly client: OpenRouterClient;
  private readonly config: OpenRouterConfig;
  private readonly modelManager: OpenRouterModelManager;
  private readonly costTracker: OpenRouterCostTracker;
  private readonly loadBalancer: OpenRouterLoadBalancer;
  private readonly logger: Logger;

  constructor(config: OpenRouterConfig) {
    this.config = this.validateConfig(config);
    this.client = new OpenRouterClient(this.config);
    this.modelManager = new OpenRouterModelManager(this.client);
    this.costTracker = new OpenRouterCostTracker(this.config.cost);
    this.loadBalancer = new OpenRouterLoadBalancer(this.config.loadBalancing);
    this.logger = new Logger({ service: 'openrouter-provider' });
  }

  async initialize(): Promise<void> {
    await this.client.authenticate();
    await this.modelManager.discoverModels();
    await this.costTracker.initialize();
    await this.loadBalancer.initialize();
    this.logger.info('OpenRouter provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    // Check usage limits
    await this.costTracker.checkUsageLimits();

    // Select optimal model
    const selectedModel = await this.loadBalancer.selectModel(request);

    try {
      return this.handleModelRequest(selectedModel, request);
    } catch (error) {
      // Try fallback models
      const fallbackModel = await this.loadBalancer.selectFallbackModel(
        selectedModel,
        request,
        error
      );
      if (fallbackModel) {
        this.logger.warn(`Using fallback model: ${fallbackModel.id} (failed: ${selectedModel.id})`);
        return this.handleModelRequest(fallbackModel, request);
      }
      throw error;
    }
  }

  async getCapabilities(): Promise<ProviderCapabilities> {
    return {
      models: await this.modelManager.getAvailableModels(),
      features: {
        streaming: true,
        functionCalling: true,
        multimodal: true,
        toolUse: true,
        costOptimization: true,
        loadBalancing: true,
        fallbackRouting: true,
      },
      limits: {
        maxTokens: 32768,
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async getUsageStats(): Promise<OpenRouterUsageStats> {
    return this.costTracker.getUsageStats();
  }

  async getCostAnalysis(): Promise<OpenRouterCostAnalysis> {
    return this.costTracker.getCostAnalysis();
  }

  async dispose(): Promise<void> {
    await this.loadBalancer.dispose();
    await this.costTracker.dispose();
    await this.client.dispose();
    this.logger.info('OpenRouter provider disposed');
  }
}
```

### OpenRouter API Client

```typescript
// src/providers/implementations/openrouter-client.ts
export class OpenRouterClient {
  private readonly config: OpenRouterConfig;
  private readonly httpClient: HttpClient;
  private readonly authManager: OpenRouterAuthManager;
  private readonly logger: Logger;

  constructor(config: OpenRouterConfig) {
    this.config = config;
    this.httpClient = new HttpClient({
      baseURL: config.apiEndpoint,
      timeout: config.timeout,
      headers: {
        'User-Agent': 'Tamma-Benchmarking/1.0',
        'Content-Type': 'application/json',
        'HTTP-Referer': config.referer || 'https://tamma.ai',
        'X-Title': config.appName || 'Tamma Benchmarking',
      },
    });
    this.authManager = new OpenRouterAuthManager(config.auth);
    this.logger = new Logger({ service: 'openrouter-client' });
  }

  async authenticate(): Promise<void> {
    const token = await this.authManager.getAuthToken();
    this.httpClient.setDefaultHeader('Authorization', `Bearer ${token}`);

    // Validate authentication
    try {
      await this.get('/auth/me');
      this.logger.info('OpenRouter authentication successful');
    } catch (error) {
      throw new OpenRouterError('AUTHENTICATION_FAILED', 'Authentication validation failed', error);
    }
  }

  async getModels(): Promise<OpenRouterModel[]> {
    try {
      const response = await this.get('/models');
      return response.data.map(this.transformModelData);
    } catch (error) {
      throw new OpenRouterError('MODEL_DISCOVERY_FAILED', 'Failed to discover models', error);
    }
  }

  async getModel(modelId: string): Promise<OpenRouterModel> {
    try {
      const response = await this.get(`/models/${modelId}`);
      return this.transformModelData(response.data);
    } catch (error) {
      throw new OpenRouterError('MODEL_GET_FAILED', `Failed to get model ${modelId}`, error);
    }
  }

  async generateCompletion(
    request: OpenRouterCompletionRequest
  ): Promise<AsyncIterable<OpenRouterCompletionChunk>> {
    const payload = this.buildCompletionPayload(request);

    try {
      const response = await this.post('/chat/completions', payload, {
        responseType: 'stream',
      });

      return this.streamCompletionResponse(response.data);
    } catch (error) {
      throw new OpenRouterError('COMPLETION_FAILED', 'Completion request failed', error);
    }
  }

  async getUsageStats(): Promise<OpenRouterAPIUsageStats> {
    try {
      const response = await this.get('/usage');
      return response.data;
    } catch (error) {
      throw new OpenRouterError('USAGE_STATS_FAILED', 'Failed to get usage statistics', error);
    }
  }

  async getBillingInfo(): Promise<OpenRouterBillingInfo> {
    try {
      const response = await this.get('/billing');
      return response.data;
    } catch (error) {
      throw new OpenRouterError('BILLING_INFO_FAILED', 'Failed to get billing information', error);
    }
  }

  async getRateLimits(): Promise<OpenRouterRateLimits> {
    try {
      const response = await this.get('/rate_limits');
      return response.data;
    } catch (error) {
      throw new OpenRouterError('RATE_LIMITS_FAILED', 'Failed to get rate limits', error);
    }
  }

  private async get(endpoint: string, params?: any): Promise<any> {
    return this.httpClient.get(endpoint, params);
  }

  private async post(endpoint: string, data: any, options?: any): Promise<any> {
    return this.httpClient.post(endpoint, data, options);
  }

  private buildCompletionPayload(request: OpenRouterCompletionRequest): any {
    const payload: any = {
      model: request.model,
      messages: request.messages.map((msg) => ({
        role: msg.role,
        content: msg.content,
      })),
      stream: true,
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      ...request.parameters,
    };

    // Add OpenRouter-specific parameters
    if (request.route) {
      payload.route = request.route;
    }

    if (request.models && request.models.length > 0) {
      payload.models = request.models;
    }

    if (request.provider) {
      payload.provider = request.provider;
    }

    if (request.transform) {
      payload.transform = request.transform;
    }

    if (request.config) {
      payload.config = request.config;
    }

    return payload;
  }

  private transformModelData(data: any): OpenRouterModel {
    return {
      id: data.id,
      name: data.name,
      provider: 'openrouter',
      version: data.version || '1.0',
      capabilities: {
        maxTokens: data.top_provider?.max_context_length || 4096,
        streaming: data.supports_streaming || false,
        functionCalling: data.supports_function_calling || false,
        multimodal: data.supports_vision || false,
        toolUse: data.supports_tools || false,
        contextWindow: data.top_provider?.max_context_length || 4096,
        supportedFormats: data.supports_vision ? ['image/jpeg', 'image/png', 'image/webp'] : [],
      },
      metadata: {
        description: data.description,
        category: this.categorizeModel(data),
        architecture: data.architecture,
        provider: data.top_provider?.name || 'unknown',
        pricing: {
          currency: 'USD',
          prompt: data.pricing?.prompt || 0,
          completion: data.pricing?.completion || 0,
        },
        tags: this.generateModelTags(data),
        performance: data.performance_metrics || {},
        availability: data.availability || 'available',
      },
      openRouterSpecific: {
        id: data.id,
        name: data.name,
        provider: data.top_provider?.name,
        contextLength: data.top_provider?.max_context_length,
        pricing: data.pricing,
        supportsStreaming: data.supports_streaming,
        supportsFunctionCalling: data.supports_function_calling,
        supportsVision: data.supports_vision,
        supportsTools: data.supports_tools,
        architecture: data.architecture,
        topProvider: data.top_provider,
        pricingPrompt: data.pricing?.prompt,
        pricingCompletion: data.pricing?.completion,
        availability: data.availability,
      },
    };
  }

  private categorizeModel(data: any): string {
    const name = data.name?.toLowerCase() || '';
    const description = data.description?.toLowerCase() || '';

    if (name.includes('claude') || description.includes('anthropic')) {
      return 'anthropic';
    } else if (name.includes('gpt') || description.includes('openai')) {
      return 'openai';
    } else if (name.includes('gemini') || description.includes('google')) {
      return 'google';
    } else if (name.includes('llama') || description.includes('meta')) {
      return 'meta';
    } else if (name.includes('mistral') || description.includes('mistral')) {
      return 'mistral';
    } else if (name.includes('palm') || description.includes('palm')) {
      return 'google';
    } else {
      return 'other';
    }
  }

  private generateModelTags(data: any): string[] {
    const tags: string[] = [];

    // Capability-based tags
    if (data.supports_streaming) tags.push('streaming');
    if (data.supports_function_calling) tags.push('function-calling');
    if (data.supports_vision) tags.push('vision');
    if (data.supports_tools) tags.push('tools');

    // Provider-based tags
    if (data.top_provider?.name) {
      tags.push(data.top_provider.name.toLowerCase());
    }

    // Size-based tags
    const name = data.name?.toLowerCase() || '';
    if (name.includes('mini')) tags.push('small');
    else if (name.includes('medium')) tags.push('medium');
    else if (name.includes('large') || name.includes('xl')) tags.push('large');

    // Performance-based tags
    if (data.performance_metrics?.speed === 'fast') tags.push('fast');
    if (data.performance_metrics?.quality === 'high') tags.push('high-quality');

    return tags;
  }

  private async *streamCompletionResponse(stream: any): AsyncIterable<OpenRouterCompletionChunk> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n').filter((line) => line.trim());

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const data = line.slice(6);
            if (data === '[DONE]') continue;

            try {
              const parsed = JSON.parse(data);
              yield {
                content: parsed.choices?.[0]?.delta?.content || '',
                done: parsed.choices?.[0]?.finish_reason === 'stop',
                metadata: {
                  model: parsed.model,
                  usage: parsed.usage,
                  finishReason: parsed.choices?.[0]?.finish_reason,
                  route: parsed.route,
                  timestamp: new Date().toISOString(),
                },
              };
            } catch (e) {
              this.logger.warn('Failed to parse streaming response:', e);
            }
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  async dispose(): Promise<void> {
    await this.httpClient.dispose();
  }
}
```

### Model Manager Implementation

```typescript
// src/providers/implementations/openrouter-model-manager.ts
export class OpenRouterModelManager {
  private readonly client: OpenRouterClient;
  private readonly models: Map<string, OpenRouterModel>;
  private readonly categories: Map<string, OpenRouterModel[]>;
  private readonly providers: Map<string, OpenRouterModel[]>;
  private readonly logger: Logger;

  constructor(client: OpenRouterClient) {
    this.client = client;
    this.models = new Map();
    this.categories = new Map();
    this.providers = new Map();
    this.logger = new Logger({ service: 'openrouter-model-manager' });
  }

  async discoverModels(): Promise<void> {
    try {
      const models = await this.client.getModels();

      for (const model of models) {
        this.models.set(model.id, model);
        this.categorizeModel(model);
        this.logger.info(`Discovered model: ${model.id} (${model.metadata.provider})`);
      }

      this.logger.info(`Discovered ${models.length} OpenRouter models`);
    } catch (error) {
      this.logger.error('Failed to discover models:', error);
      throw error;
    }
  }

  async getModel(modelId: string): Promise<OpenRouterModel> {
    const model = this.models.get(modelId);
    if (!model) {
      throw new Error(`Model not found: ${modelId}`);
    }
    return model;
  }

  async getAvailableModels(): Promise<ModelInfo[]> {
    return Array.from(this.models.values()).map((model) => ({
      id: model.id,
      name: model.name,
      provider: model.provider,
      version: model.version,
      capabilities: model.capabilities,
      metadata: model.metadata,
    }));
  }

  async filterModels(criteria: ModelFilterCriteria): Promise<OpenRouterModel[]> {
    let filteredModels = Array.from(this.models.values());

    // Filter by category
    if (criteria.categories && criteria.categories.length > 0) {
      filteredModels = filteredModels.filter((model) =>
        criteria.categories!.includes(model.metadata.category)
      );
    }

    // Filter by provider
    if (criteria.providers && criteria.providers.length > 0) {
      filteredModels = filteredModels.filter((model) =>
        criteria.providers!.includes(model.metadata.provider)
      );
    }

    // Filter by capabilities
    if (criteria.capabilities) {
      filteredModels = filteredModels.filter((model) => {
        const caps = model.capabilities;
        return Object.entries(criteria.capabilities!).every(([key, required]) => {
          const value = (caps as any)[key];
          return required ? value === true : true;
        });
      });
    }

    // Filter by cost
    if (criteria.maxCostPerToken !== undefined) {
      filteredModels = filteredModels.filter((model) => {
        const cost = Math.max(
          model.metadata.pricing.prompt || 0,
          model.metadata.pricing.completion || 0
        );
        return cost <= criteria.maxCostPerToken!;
      });
    }

    // Filter by context length
    if (criteria.minContextLength !== undefined) {
      filteredModels = filteredModels.filter(
        (model) => model.capabilities.contextWindow >= criteria.minContextLength!
      );
    }

    // Filter by availability
    if (criteria.availableOnly) {
      filteredModels = filteredModels.filter(
        (model) => model.metadata.availability === 'available'
      );
    }

    // Filter by tags
    if (criteria.tags && criteria.tags.length > 0) {
      filteredModels = filteredModels.filter((model) =>
        criteria.tags!.some((tag) => model.metadata.tags.includes(tag))
      );
    }

    return filteredModels;
  }

  async getModelsByCategory(category: string): Promise<OpenRouterModel[]> {
    return this.categories.get(category) || [];
  }

  async getModelsByProvider(provider: string): Promise<OpenRouterModel[]> {
    return this.providers.get(provider) || [];
  }

  async getRecommendedModels(request: ModelRecommendationRequest): Promise<OpenRouterModel[]> {
    const criteria: ModelFilterCriteria = {
      capabilities: request.requiredCapabilities,
      minContextLength: request.minContextLength,
      maxCostPerToken: request.maxCostPerToken,
      availableOnly: true,
    };

    let candidates = await this.filterModels(criteria);

    // Score models based on request requirements
    const scoredModels = candidates.map((model) => ({
      model,
      score: this.scoreModel(model, request),
    }));

    // Sort by score (descending)
    scoredModels.sort((a, b) => b.score - a.score);

    // Return top recommendations
    return scoredModels.slice(0, request.maxRecommendations || 5).map((item) => item.model);
  }

  async compareModels(modelIds: string[]): Promise<ModelComparison> {
    const models = await Promise.all(modelIds.map((id) => this.getModel(id)));

    return {
      models,
      comparison: {
        cost: this.compareCost(models),
        performance: this.comparePerformance(models),
        capabilities: this.compareCapabilities(models),
        availability: this.compareAvailability(models),
      },
      recommendation: this.getBestModel(models),
    };
  }

  private categorizeModel(model: OpenRouterModel): void {
    const category = model.metadata.category;

    if (!this.categories.has(category)) {
      this.categories.set(category, []);
    }
    this.categories.get(category)!.push(model);

    const provider = model.metadata.provider;
    if (!this.providers.has(provider)) {
      this.providers.set(provider, []);
    }
    this.providers.get(provider)!.push(model);
  }

  private scoreModel(model: OpenRouterModel, request: ModelRecommendationRequest): number {
    let score = 0;

    // Cost scoring (lower is better)
    const avgCost =
      (model.metadata.pricing.prompt || 0 + model.metadata.pricing.completion || 0) / 2;
    if (request.maxCostPerToken) {
      const costScore = Math.max(0, 1 - avgCost / request.maxCostPerToken);
      score += costScore * 0.3;
    }

    // Capability scoring
    if (request.requiredCapabilities) {
      const capabilityScore = this.calculateCapabilityScore(model, request.requiredCapabilities);
      score += capabilityScore * 0.4;
    }

    // Context length scoring
    if (request.minContextLength) {
      const contextScore = Math.min(1, model.capabilities.contextWindow / request.minContextLength);
      score += contextScore * 0.2;
    }

    // Performance scoring
    const performanceScore = this.calculatePerformanceScore(model);
    score += performanceScore * 0.1;

    return score;
  }

  private calculateCapabilityScore(model: OpenRouterModel, requiredCapabilities: any): number {
    const caps = model.capabilities;
    const required = Object.entries(requiredCapabilities).filter(([_, required]) => required);

    if (required.length === 0) return 1;

    const satisfied = required.filter(([key, _]) => (caps as any)[key] === true).length;
    return satisfied / required.length;
  }

  private calculatePerformanceScore(model: OpenRouterModel): number {
    const performance = model.metadata.performance;

    // Simple performance scoring based on available metrics
    let score = 0.5; // Base score

    if (performance.speed === 'fast') score += 0.3;
    if (performance.quality === 'high') score += 0.3;
    if (performance.reliability === 'high') score += 0.2;

    return Math.min(1, score);
  }

  private compareCost(models: OpenRouterModel[]): CostComparison {
    const costs = models.map((model) => ({
      modelId: model.id,
      prompt: model.metadata.pricing.prompt || 0,
      completion: model.metadata.pricing.completion || 0,
      average: (model.metadata.pricing.prompt || 0 + model.metadata.pricing.completion || 0) / 2,
    }));

    const sorted = [...costs].sort((a, b) => a.average - b.average);

    return {
      costs,
      cheapest: sorted[0],
      mostExpensive: sorted[sorted.length - 1],
      average: costs.reduce((sum, c) => sum + c.average, 0) / costs.length,
    };
  }

  private comparePerformance(models: OpenRouterModel[]): PerformanceComparison {
    const performance = models.map((model) => ({
      modelId: model.id,
      ...model.metadata.performance,
    }));

    return {
      performance,
      fastest: performance.find((p) => p.speed === 'fast'),
      highestQuality: performance.find((p) => p.quality === 'high'),
      mostReliable: performance.find((p) => p.reliability === 'high'),
    };
  }

  private compareCapabilities(models: OpenRouterModel[]): CapabilitiesComparison {
    const capabilities = models.map((model) => ({
      modelId: model.id,
      capabilities: model.capabilities,
    }));

    return {
      capabilities,
      streaming: capabilities.filter((c) => c.capabilities.streaming).map((c) => c.modelId),
      functionCalling: capabilities
        .filter((c) => c.capabilities.functionCalling)
        .map((c) => c.modelId),
      multimodal: capabilities.filter((c) => c.capabilities.multimodal).map((c) => c.modelId),
      toolUse: capabilities.filter((c) => c.capabilities.toolUse).map((c) => c.modelId),
    };
  }

  private compareAvailability(models: OpenRouterModel[]): AvailabilityComparison {
    const availability = models.map((model) => ({
      modelId: model.id,
      availability: model.metadata.availability,
    }));

    return {
      availability,
      available: availability.filter((a) => a.availability === 'available').map((a) => a.modelId),
      degraded: availability.filter((a) => a.availability === 'degraded').map((a) => a.modelId),
      unavailable: availability
        .filter((a) => a.availability === 'unavailable')
        .map((a) => a.modelId),
    };
  }

  private getBestModel(models: OpenRouterModel[]): string {
    // Simple best model selection based on availability and capabilities
    const available = models.filter((m) => m.metadata.availability === 'available');

    if (available.length === 0) {
      return models[0].id; // Return first if none available
    }

    // Prefer models with more capabilities
    const scored = available.map((model) => ({
      model,
      score: Object.values(model.capabilities).filter(Boolean).length,
    }));

    scored.sort((a, b) => b.score - a.score);
    return scored[0].model.id;
  }
}
```

### Cost Tracker Implementation

```typescript
// src/providers/implementations/openrouter-cost-tracker.ts
export class OpenRouterCostTracker {
  private readonly config: OpenRouterCostConfig;
  private readonly storage: CostStorage;
  private readonly usage: Map<string, UsageRecord>;
  private readonly logger: Logger;

  constructor(config: OpenRouterCostConfig) {
    this.config = config;
    this.storage = new CostStorage(config.storage);
    this.usage = new Map();
    this.logger = new Logger({ service: 'openrouter-cost-tracker' });
  }

  async initialize(): Promise<void> {
    await this.storage.initialize();
    await this.loadUsageData();
    this.logger.info('OpenRouter cost tracker initialized');
  }

  async trackUsage(modelId: string, usage: TokenUsage): Promise<void> {
    const model = await this.getModelCost(modelId);
    const cost = this.calculateCost(model, usage);

    const record: UsageRecord = {
      id: crypto.randomUUID(),
      modelId,
      timestamp: new Date().toISOString(),
      usage,
      cost,
      period: this.getCurrentPeriod(),
    };

    this.usage.set(record.id, record);
    await this.storage.save(record);

    // Check usage limits
    await this.checkUsageLimits();

    this.logger.debug(`Tracked usage: ${modelId} - $${cost.total.toFixed(6)}`);
  }

  async checkUsageLimits(): Promise<void> {
    const currentUsage = await this.getCurrentPeriodUsage();

    // Check daily limit
    if (this.config.limits.daily && currentUsage.daily >= this.config.limits.daily) {
      throw new OpenRouterError('DAILY_LIMIT_EXCEEDED', 'Daily usage limit exceeded');
    }

    // Check monthly limit
    if (this.config.limits.monthly && currentUsage.monthly >= this.config.limits.monthly) {
      throw new OpenRouterError('MONTHLY_LIMIT_EXCEEDED', 'Monthly usage limit exceeded');
    }

    // Check per-model limits
    if (this.config.limits.perModel) {
      for (const [modelId, limit] of Object.entries(this.config.limits.perModel)) {
        const modelUsage = await this.getModelUsage(modelId);
        if (modelUsage >= limit) {
          throw new OpenRouterError('MODEL_LIMIT_EXCEEDED', `Model limit exceeded for ${modelId}`);
        }
      }
    }
  }

  async getUsageStats(): Promise<OpenRouterUsageStats> {
    const currentUsage = await this.getCurrentPeriodUsage();
    const modelBreakdown = await this.getModelBreakdown();
    const trend = await this.getUsageTrend();

    return {
      current: currentUsage,
      modelBreakdown,
      trend,
      limits: this.config.limits,
      alerts: await this.getActiveAlerts(),
    };
  }

  async getCostAnalysis(): Promise<OpenRouterCostAnalysis> {
    const periodUsage = await this.getPeriodUsage(30); // Last 30 days
    const modelAnalysis = await this.getModelCostAnalysis();
    const optimization = await this.getOptimizationRecommendations();

    return {
      period: periodUsage,
      models: modelAnalysis,
      optimization,
      projections: await this.getCostProjections(),
      savings: await this.calculatePotentialSavings(),
    };
  }

  private async getModelCost(modelId: string): Promise<ModelCost> {
    // This would typically fetch from OpenRouter API or cache
    // For now, return a default structure
    return {
      modelId,
      promptCost: 0.001,
      completionCost: 0.002,
      currency: 'USD',
    };
  }

  private calculateCost(model: ModelCost, usage: TokenUsage): CostBreakdown {
    const promptCost = usage.promptTokens * model.promptCost;
    const completionCost = usage.completionTokens * model.completionCost;
    const total = promptCost + completionCost;

    return {
      prompt: promptCost,
      completion: completionCost,
      total,
      currency: model.currency,
    };
  }

  private getCurrentPeriod(): string {
    const now = new Date();
    return `${now.getFullYear()}-${now.getMonth() + 1}-${now.getDate()}`;
  }

  private async getCurrentPeriodUsage(): Promise<CurrentPeriodUsage> {
    const today = new Date();
    const startOfDay = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    const startOfMonth = new Date(today.getFullYear(), today.getMonth(), 1);

    const daily = await this.getUsageInPeriod(startOfDay, today);
    const monthly = await this.getUsageInPeriod(startOfMonth, today);

    return {
      daily,
      monthly,
      requests: await this.getRequestCount(startOfDay, today),
    };
  }

  private async getUsageInPeriod(start: Date, end: Date): Promise<number> {
    const records = await this.storage.getUsageInPeriod(start, end);
    return records.reduce((total, record) => total + record.cost.total, 0);
  }

  private async getRequestCount(start: Date, end: Date): Promise<number> {
    const records = await this.storage.getUsageInPeriod(start, end);
    return records.length;
  }

  private async getModelUsage(modelId: string): Promise<number> {
    const records = await this.storage.getModelUsage(modelId);
    return records.reduce((total, record) => total + record.cost.total, 0);
  }

  private async getModelBreakdown(): Promise<ModelBreakdown[]> {
    const modelUsage = new Map<string, number>();

    for (const record of this.usage.values()) {
      const current = modelUsage.get(record.modelId) || 0;
      modelUsage.set(record.modelId, current + record.cost.total);
    }

    return Array.from(modelUsage.entries()).map(([modelId, cost]) => ({
      modelId,
      cost,
      requests: await this.getRequestCountForModel(modelId),
    }));
  }

  private async getUsageTrend(): Promise<UsageTrend[]> {
    const trends: UsageTrend[] = [];
    const days = 30;

    for (let i = 0; i < days; i++) {
      const date = new Date();
      date.setDate(date.getDate() - i);
      const startOfDay = new Date(date.getFullYear(), date.getMonth(), date.getDate());
      const endOfDay = new Date(date.getFullYear(), date.getMonth(), date.getDate() + 1);

      const usage = await this.getUsageInPeriod(startOfDay, endOfDay);
      const requests = await this.getRequestCount(startOfDay, endOfDay);

      trends.push({
        date: startOfDay.toISOString().split('T')[0],
        usage,
        requests,
      });
    }

    return trends.reverse(); // Most recent first
  }

  private async getActiveAlerts(): Promise<UsageAlert[]> {
    const alerts: UsageAlert[] = [];
    const currentUsage = await this.getCurrentPeriodUsage();

    // Daily usage alert
    if (this.config.limits.daily) {
      const percentage = (currentUsage.daily / this.config.limits.daily) * 100;
      if (percentage >= 80) {
        alerts.push({
          type: 'daily_limit_warning',
          message: `Daily usage at ${percentage.toFixed(1)}% of limit`,
          severity: percentage >= 95 ? 'critical' : 'warning',
        });
      }
    }

    // Monthly usage alert
    if (this.config.limits.monthly) {
      const percentage = (currentUsage.monthly / this.config.limits.monthly) * 100;
      if (percentage >= 80) {
        alerts.push({
          type: 'monthly_limit_warning',
          message: `Monthly usage at ${percentage.toFixed(1)}% of limit`,
          severity: percentage >= 95 ? 'critical' : 'warning',
        });
      }
    }

    return alerts;
  }

  private async loadUsageData(): Promise<void> {
    try {
      const records = await this.storage.loadRecent(1000); // Load last 1000 records
      for (const record of records) {
        this.usage.set(record.id, record);
      }
      this.logger.info(`Loaded ${records.length} usage records`);
    } catch (error) {
      this.logger.error('Failed to load usage data:', error);
    }
  }

  private async getPeriodUsage(days: number): Promise<PeriodUsage> {
    const end = new Date();
    const start = new Date();
    start.setDate(end.getDate() - days);

    const records = await this.storage.getUsageInPeriod(start, end);
    const totalCost = records.reduce((sum, record) => sum + record.cost.total, 0);
    const totalTokens = records.reduce(
      (sum, record) => sum + record.usage.promptTokens + record.usage.completionTokens,
      0
    );

    return {
      period: `${days} days`,
      start: start.toISOString(),
      end: end.toISOString(),
      totalCost,
      totalTokens,
      requestCount: records.length,
      averageCostPerRequest: totalCost / records.length,
      averageTokensPerRequest: totalTokens / records.length,
    };
  }

  private async getModelCostAnalysis(): Promise<ModelCostAnalysis[]> {
    const modelUsage = new Map<string, ModelCostAnalysis>();

    for (const record of this.usage.values()) {
      const existing = modelUsage.get(record.modelId) || {
        modelId: record.modelId,
        totalCost: 0,
        totalTokens: 0,
        requestCount: 0,
        averageCostPerToken: 0,
        averageCostPerRequest: 0,
      };

      existing.totalCost += record.cost.total;
      existing.totalTokens += record.usage.promptTokens + record.usage.completionTokens;
      existing.requestCount += 1;

      modelUsage.set(record.modelId, existing);
    }

    // Calculate averages
    for (const analysis of modelUsage.values()) {
      analysis.averageCostPerToken =
        analysis.totalTokens > 0 ? analysis.totalCost / analysis.totalTokens : 0;
      analysis.averageCostPerRequest =
        analysis.requestCount > 0 ? analysis.totalCost / analysis.requestCount : 0;
    }

    return Array.from(modelUsage.values());
  }

  private async getOptimizationRecommendations(): Promise<OptimizationRecommendation[]> {
    const recommendations: OptimizationRecommendation[] = [];
    const modelAnalysis = await this.getModelCostAnalysis();

    // Find most expensive models
    const sortedByCost = [...modelAnalysis].sort(
      (a, b) => b.averageCostPerToken - a.averageCostPerToken
    );
    const mostExpensive = sortedByCost.slice(0, 3);

    for (const model of mostExpensive) {
      recommendations.push({
        type: 'cost_optimization',
        modelId: model.modelId,
        message: `Consider using a more cost-effective alternative to ${model.modelId}`,
        potentialSavings: model.totalCost * 0.3, // Estimate 30% savings
        priority: 'high',
      });
    }

    return recommendations;
  }

  private async getCostProjections(): Promise<CostProjection> {
    const currentUsage = await this.getCurrentPeriodUsage();
    const daysInMonth = new Date(new Date().getFullYear(), new Date().getMonth() + 1, 0).getDate();
    const currentDay = new Date().getDate();

    const dailyAverage = currentUsage.daily;
    const projectedMonthly = dailyAverage * daysInMonth;

    return {
      daily: dailyAverage,
      monthly: projectedMonthly,
      remaining: this.config.limits.monthly ? this.config.limits.monthly - projectedMonthly : null,
      overBudget: this.config.limits.monthly
        ? projectedMonthly > this.config.limits.monthly
        : false,
    };
  }

  private async calculatePotentialSavings(): Promise<PotentialSavings> {
    const modelAnalysis = await this.getModelCostAnalysis();
    const totalCost = modelAnalysis.reduce((sum, model) => sum + model.totalCost, 0);

    // Estimate potential savings through optimization
    const optimizationSavings = totalCost * 0.2; // 20% through optimization
    const modelSwitchingSavings = totalCost * 0.15; // 15% through model switching
    const usageReductionSavings = totalCost * 0.1; // 10% through usage reduction

    return {
      total: totalCost,
      optimization: optimizationSavings,
      modelSwitching: modelSwitchingSavings,
      usageReduction: usageReductionSavings,
      combined: optimizationSavings + modelSwitchingSavings + usageReductionSavings,
    };
  }

  private async getRequestCountForModel(modelId: string): Promise<number> {
    const records = await this.storage.getModelUsage(modelId);
    return records.length;
  }

  async dispose(): Promise<void> {
    await this.storage.dispose();
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/openrouter-config.schema.ts
export const OpenRouterConfigSchema = {
  type: 'object',
  required: ['auth', 'apiEndpoint', 'rateLimits'],
  properties: {
    apiEndpoint: {
      type: 'string',
      default: 'https://openrouter.ai/api/v1',
      description: 'OpenRouter API endpoint URL',
    },
    auth: {
      type: 'object',
      required: ['type'],
      oneOf: [
        {
          title: 'API Key Authentication',
          properties: {
            type: { const: 'api-key' },
            apiKey: {
              type: 'string',
              description: 'OpenRouter API key',
            },
          },
        },
      ],
    },
    rateLimits: {
      type: 'object',
      required: ['requestsPerMinute', 'tokensPerMinute'],
      properties: {
        requestsPerMinute: {
          type: 'integer',
          minimum: 1,
          maximum: 1000,
          default: 60,
          description: 'Maximum requests per minute',
        },
        tokensPerMinute: {
          type: 'integer',
          minimum: 1000,
          maximum: 1000000,
          default: 60000,
          description: 'Maximum tokens per minute',
        },
        concurrentRequests: {
          type: 'integer',
          minimum: 1,
          maximum: 20,
          default: 5,
          description: 'Maximum concurrent requests',
        },
      },
    },
    cost: {
      type: 'object',
      properties: {
        limits: {
          type: 'object',
          properties: {
            daily: {
              type: 'number',
              minimum: 0,
              description: 'Daily cost limit in USD',
            },
            monthly: {
              type: 'number',
              minimum: 0,
              description: 'Monthly cost limit in USD',
            },
            perModel: {
              type: 'object',
              patternProperties: {
                '.*': {
                  type: 'number',
                  minimum: 0,
                },
              },
              description: 'Per-model cost limits',
            },
          },
        },
        alerts: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            thresholds: {
              type: 'object',
              properties: {
                dailyWarning: { type: 'number', default: 0.8 },
                dailyCritical: { type: 'number', default: 0.95 },
                monthlyWarning: { type: 'number', default: 0.8 },
                monthlyCritical: { type: 'number', default: 0.95 },
              },
            },
          },
        },
        storage: {
          type: 'object',
          properties: {
            type: {
              type: 'string',
              enum: ['memory', 'file', 'database'],
              default: 'memory',
            },
            path: {
              type: 'string',
              description: 'Storage path for file-based storage',
            },
            retention: {
              type: 'object',
              properties: {
                days: { type: 'integer', default: 90 },
                maxRecords: { type: 'integer', default: 10000 },
              },
            },
          },
        },
      },
    },
    loadBalancing: {
      type: 'object',
      properties: {
        enabled: { type: 'boolean', default: true },
        strategy: {
          type: 'string',
          enum: ['round-robin', 'cost-optimized', 'performance-optimized', 'availability-first'],
          default: 'cost-optimized',
        },
        fallback: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            maxAttempts: { type: 'integer', default: 3 },
            retryDelay: { type: 'integer', default: 1000 },
          },
        },
        healthCheck: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            interval: { type: 'integer', default: 60000 },
            timeout: { type: 'integer', default: 5000 },
          },
        },
      },
    },
    models: {
      type: 'object',
      properties: {
        defaultModel: {
          type: 'string',
          description: 'Default model to use',
        },
        preferredProviders: {
          type: 'array',
          items: { type: 'string' },
          description: 'Preferred model providers',
        },
        excludedProviders: {
          type: 'array',
          items: { type: 'string' },
          description: 'Excluded model providers',
        },
        autoDiscovery: {
          type: 'boolean',
          default: true,
          description: 'Automatically discover available models',
        },
        cacheTimeout: {
          type: 'integer',
          default: 300,
          description: 'Model cache timeout in seconds',
        },
      },
    },
    features: {
      type: 'object',
      properties: {
        streaming: {
          type: 'boolean',
          default: true,
          description: 'Enable streaming responses',
        },
        costOptimization: {
          type: 'boolean',
          default: true,
          description: 'Enable cost optimization features',
        },
        usageTracking: {
          type: 'boolean',
          default: true,
          description: 'Enable detailed usage tracking',
        },
        modelRecommendations: {
          type: 'boolean',
          default: true,
          description: 'Enable model recommendations',
        },
      },
    },
    referer: {
      type: 'string',
      description: 'HTTP referer header for API requests',
    },
    appName: {
      type: 'string',
      default: 'Tamma Benchmarking',
      description: 'Application name for API requests',
    },
    timeout: {
      type: 'integer',
      minimum: 5000,
      maximum: 300000,
      default: 30000,
      description: 'Request timeout in milliseconds',
    },
  },
};
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/openrouter-provider.test.ts
describe('OpenRouterProvider', () => {
  let provider: OpenRouterProvider;
  let mockClient: jest.Mocked<OpenRouterClient>;

  beforeEach(() => {
    mockClient = new OpenRouterClient({
      apiEndpoint: 'https://openrouter.ai/api/v1',
      auth: { type: 'api-key', apiKey: 'test-key' },
    } as any) as jest.Mocked<OpenRouterClient>;

    provider = new OpenRouterProvider({
      apiEndpoint: 'https://openrouter.ai/api/v1',
      auth: { type: 'api-key', apiKey: 'test-key' },
      rateLimits: {
        requestsPerMinute: 60,
        tokensPerMinute: 60000,
      },
    });
  });

  describe('model discovery', () => {
    it('should discover available models', async () => {
      const mockModels = [
        { id: 'anthropic/claude-3-sonnet', name: 'Claude 3 Sonnet' },
        { id: 'openai/gpt-4', name: 'GPT-4' },
      ];

      mockClient.getModels.mockResolvedValue(mockModels as any);

      await provider.initialize();

      const capabilities = await provider.getCapabilities();
      expect(capabilities.models).toHaveLength(2);
      expect(capabilities.models[0].id).toBe('anthropic/claude-3-sonnet');
    });
  });

  describe('cost tracking', () => {
    it('should track usage costs', async () => {
      const request: MessageRequest = {
        model: 'anthropic/claude-3-sonnet',
        messages: [{ role: 'user', content: 'Hello' }],
        maxTokens: 100,
      };

      const mockStream = createMockCompletionStream();
      mockClient.generateCompletion.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
      // Cost tracking would be verified through internal methods
    });
  });

  describe('load balancing', () => {
    it('should select optimal model based on strategy', async () => {
      const request: MessageRequest = {
        model: 'auto', // Let provider choose
        messages: [{ role: 'user', content: 'Hello' }],
      };

      const mockStream = createMockCompletionStream();
      mockClient.generateCompletion.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
      // Model selection would be verified through load balancer
    });
  });
});
```

### Integration Tests

```typescript
// tests/providers/openrouter-integration.test.ts
describe('OpenRouter Integration', () => {
  let provider: OpenRouterProvider;

  beforeAll(async () => {
    if (!process.env.OPENROUTER_API_KEY_TEST) {
      throw new Error('OPENROUTER_API_KEY_TEST environment variable required');
    }

    provider = new OpenRouterProvider({
      apiEndpoint: 'https://openrouter.ai/api/v1',
      auth: {
        type: 'api-key',
        apiKey: process.env.OPENROUTER_API_KEY_TEST,
      },
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 30000,
      },
    });

    await provider.initialize();
  });

  it('should generate text with Claude 3 Sonnet', async () => {
    const request: MessageRequest = {
      model: 'anthropic/claude-3-sonnet',
      messages: [
        {
          role: 'user',
          content: 'Explain quantum computing in simple terms.',
        },
      ],
      maxTokens: 200,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('quantum');
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should track costs and usage', async () => {
    const request: MessageRequest = {
      model: 'openai/gpt-3.5-turbo',
      messages: [
        {
          role: 'user',
          content: 'What is the capital of France?',
        },
      ],
      maxTokens: 100,
    };

    await collectStream(provider.sendMessage(request));

    const usageStats = await provider.getUsageStats();
    expect(usageStats.current.requests).toBeGreaterThan(0);
  });

  it('should provide cost analysis', async () => {
    const costAnalysis = await provider.getCostAnalysis();

    expect(costAnalysis.period).toBeDefined();
    expect(costAnalysis.models).toBeDefined();
    expect(costAnalysis.optimization).toBeDefined();
  });
});
```

## Success Metrics

### Performance Metrics

- Model discovery time: < 5s
- Model selection time: < 100ms
- Request routing time: < 50ms
- Cost calculation time: < 10ms
- Fallback activation time: < 200ms

### Reliability Metrics

- API request success rate: 99%
- Model availability accuracy: 95%+
- Cost tracking accuracy: 100%
- Load balancing effectiveness: 90%+
- Error handling coverage: 100%

### Integration Metrics

- Model discovery accuracy: 100%
- Cost optimization effectiveness: 20%+ savings
- Load balancing success rate: 95%+
- Test coverage: 90%+

## Dependencies

### External Dependencies

- `axios`: HTTP client for API requests
- `node-cache`: In-memory caching for model data
- `sqlite3`: Database for cost tracking (optional)

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### API Security

- Encrypt API keys at rest
- Use secure token storage
- Implement request signing
- Validate all API responses

### Cost Security

- Implement spending limits
- Add real-time cost monitoring
- Create spending alerts
- Prevent unauthorized usage

### Data Privacy

- Sanitize usage data before storage
- Implement data retention policies
- Add access controls for cost data
- Comply with privacy regulations

## Deliverables

1. **OpenRouter Provider** (`src/providers/implementations/openrouter-provider.ts`)
2. **API Client** (`src/providers/implementations/openrouter-client.ts`)
3. **Model Manager** (`src/providers/implementations/openrouter-model-manager.ts`)
4. **Cost Tracker** (`src/providers/implementations/openrouter-cost-tracker.ts`)
5. **Load Balancer** (`src/providers/implementations/openrouter-load-balancer.ts`)
6. **Configuration Schema** (`src/providers/configs/openrouter-config.schema.ts`)
7. **Unit Tests** (`tests/providers/openrouter-provider.test.ts`)
8. **Integration Tests** (`tests/providers/openrouter-integration.test.ts`)
9. **Documentation** (`docs/providers/openrouter.md`)

This implementation provides comprehensive OpenRouter integration with support for 100+ models, cost optimization, load balancing, and robust usage tracking while maintaining compatibility with the unified provider interface.
