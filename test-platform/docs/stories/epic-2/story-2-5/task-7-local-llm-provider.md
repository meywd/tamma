# Task 7: Local LLM Provider Support

## Overview

This task implements local LLM provider support through Ollama integration, enabling the Tamma platform to run and benchmark models locally. This provides privacy, cost control, and offline capabilities while supporting a wide range of open-source models.

## Acceptance Criteria

### 7.1: Implement Ollama API integration

- [ ] Create Ollama API client with proper connection handling
- [ ] Implement REST API integration for model management
- [ ] Add support for Ollama streaming API
- [ ] Implement proper error handling and connection recovery
- [ ] Add Ollama server detection and validation

### 7.2: Add support for local model management

- [ ] Implement model download and installation
- [ ] Add model listing and information retrieval
- [ ] Implement model deletion and cleanup
- [ ] Add model version management
- [ ] Support model customization and configuration

### 7.3: Implement model download and updates

- [ ] Create download progress tracking
- [ ] Implement resume/pause functionality
- [ ] Add download verification and integrity checks
- [ ] Implement automatic update detection
- [ ] Add bandwidth management and throttling

### 7.4: Add resource monitoring and limits

- [ ] Implement CPU and memory usage monitoring
- [ ] Add GPU utilization tracking
- [ ] Implement resource limit enforcement
- [ ] Add performance profiling and optimization
- [ ] Create resource allocation strategies

### 7.5: Create local configuration management

- [ ] Implement Ollama server configuration
- [ ] Add model-specific configuration management
- [ ] Implement performance tuning parameters
- [ ] Add security and access controls
- [ ] Create configuration validation and defaults

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/local-llm-provider.ts
export class LocalLLMProvider implements IAIProvider {
  private readonly client: OllamaClient;
  private readonly config: LocalLLMConfig;
  private readonly modelManager: LocalModelManager;
  private readonly resourceMonitor: ResourceMonitor;
  private readonly downloadManager: DownloadManager;
  private readonly logger: Logger;

  constructor(config: LocalLLMConfig) {
    this.config = this.validateConfig(config);
    this.client = new OllamaClient(this.config.ollama);
    this.modelManager = new LocalModelManager(this.client, this.config.models);
    this.resourceMonitor = new ResourceMonitor(this.config.resources);
    this.downloadManager = new DownloadManager(this.config.downloads);
    this.logger = new Logger({ service: 'local-llm-provider' });
  }

  async initialize(): Promise<void> {
    // Check Ollama server availability
    await this.client.checkServer();

    // Initialize components
    await this.modelManager.initialize();
    await this.resourceMonitor.initialize();
    await this.downloadManager.initialize();

    this.logger.info('Local LLM provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    // Check resource availability
    await this.resourceMonitor.checkResources();

    // Ensure model is available
    const model = await this.modelManager.ensureModel(request.model);

    try {
      return this.handleLocalGeneration(model, request);
    } catch (error) {
      this.handleProviderError(error);
      throw error;
    }
  }

  async getCapabilities(): Promise<ProviderCapabilities> {
    return {
      models: await this.modelManager.getAvailableModels(),
      features: {
        streaming: true,
        functionCalling: false,
        multimodal: false,
        toolUse: false,
        localExecution: true,
        resourceMonitoring: true,
        offlineMode: true,
        privacyGuaranteed: true,
      },
      limits: {
        maxTokens: 4096, // Varies by model
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async downloadModel(
    modelId: string,
    options?: DownloadOptions
  ): Promise<AsyncIterable<DownloadProgress>> {
    return this.downloadManager.downloadModel(modelId, options);
  }

  async deleteModel(modelId: string): Promise<void> {
    return this.modelManager.deleteModel(modelId);
  }

  async getResourceUsage(): Promise<ResourceUsage> {
    return this.resourceMonitor.getCurrentUsage();
  }

  async getModelInfo(modelId: string): Promise<LocalModelInfo> {
    return this.modelManager.getModelInfo(modelId);
  }

  async dispose(): Promise<void> {
    await this.resourceMonitor.dispose();
    await this.downloadManager.dispose();
    await this.client.dispose();
    this.logger.info('Local LLM provider disposed');
  }
}
```

### Ollama Client Implementation

```typescript
// src/providers/implementations/ollama-client.ts
export class OllamaClient {
  private readonly config: OllamaConfig;
  private readonly httpClient: HttpClient;
  private readonly logger: Logger;
  private serverInfo: OllamaServerInfo | null = null;

  constructor(config: OllamaConfig) {
    this.config = config;
    this.httpClient = new HttpClient({
      baseURL: config.endpoint,
      timeout: config.timeout,
      headers: {
        'User-Agent': 'Tamma-Benchmarking/1.0',
        'Content-Type': 'application/json',
      },
    });
    this.logger = new Logger({ service: 'ollama-client' });
  }

  async checkServer(): Promise<OllamaServerInfo> {
    try {
      const response = await this.get('/api/version');
      this.serverInfo = {
        version: response.version,
        build: response.build,
        status: 'online',
        endpoint: this.config.endpoint,
      };

      this.logger.info(`Ollama server online: ${response.version}`);
      return this.serverInfo;
    } catch (error) {
      this.serverInfo = {
        version: 'unknown',
        build: 'unknown',
        status: 'offline',
        endpoint: this.config.endpoint,
        error: error.message,
      };

      throw new OllamaError('SERVER_OFFLINE', 'Ollama server is not accessible', error);
    }
  }

  async listModels(): Promise<OllamaModel[]> {
    this.ensureServerOnline();

    try {
      const response = await this.get('/api/tags');
      return response.models.map(this.transformModelData);
    } catch (error) {
      throw new OllamaError('MODEL_LIST_FAILED', 'Failed to list models', error);
    }
  }

  async showModel(modelName: string): Promise<OllamaModelInfo> {
    this.ensureServerOnline();

    try {
      const response = await this.post('/api/show', { name: modelName });
      return this.transformModelInfo(response);
    } catch (error) {
      throw new OllamaError(
        'MODEL_INFO_FAILED',
        `Failed to get model info for ${modelName}`,
        error
      );
    }
  }

  async pullModel(modelName: string, options?: PullOptions): Promise<AsyncIterable<PullProgress>> {
    this.ensureServerOnline();

    const payload: any = { name: modelName };
    if (options?.insecure) {
      payload.insecure = true;
    }

    try {
      const response = await this.post('/api/pull', payload, {
        responseType: 'stream',
      });

      return this.streamPullProgress(response.data);
    } catch (error) {
      throw new OllamaError('MODEL_PULL_FAILED', `Failed to pull model ${modelName}`, error);
    }
  }

  async deleteModel(modelName: string): Promise<void> {
    this.ensureServerOnline();

    try {
      await this.delete('/api/delete', { name: modelName });
      this.logger.info(`Deleted model: ${modelName}`);
    } catch (error) {
      throw new OllamaError('MODEL_DELETE_FAILED', `Failed to delete model ${modelName}`, error);
    }
  }

  async generateCompletion(
    request: OllamaGenerationRequest
  ): Promise<AsyncIterable<OllamaGenerationChunk>> {
    this.ensureServerOnline();

    const payload = {
      model: request.model,
      prompt: request.prompt,
      system: request.system,
      template: request.template,
      context: request.context,
      options: this.buildGenerationOptions(request),
      stream: true,
      format: request.format,
      images: request.images,
    };

    try {
      const response = await this.post('/api/generate', payload, {
        responseType: 'stream',
      });

      return this.streamGenerationResponse(response.data);
    } catch (error) {
      throw new OllamaError('GENERATION_FAILED', 'Generation request failed', error);
    }
  }

  async generateChatCompletion(
    request: OllamaChatRequest
  ): Promise<AsyncIterable<OllamaChatChunk>> {
    this.ensureServerOnline();

    const payload = {
      model: request.model,
      messages: request.messages,
      stream: true,
      options: this.buildGenerationOptions(request),
      format: request.format,
      template: request.template,
      keep_alive: request.keepAlive,
    };

    try {
      const response = await this.post('/api/chat', payload, {
        responseType: 'stream',
      });

      return this.streamChatResponse(response.data);
    } catch (error) {
      throw new OllamaError('CHAT_GENERATION_FAILED', 'Chat generation failed', error);
    }
  }

  async getServerInfo(): Promise<OllamaServerInfo> {
    if (!this.serverInfo) {
      return this.checkServer();
    }
    return this.serverInfo;
  }

  async getRunningModels(): Promise<OllamaRunningModel[]> {
    this.ensureServerOnline();

    try {
      const response = await this.get('/api/ps');
      return response.models || [];
    } catch (error) {
      throw new OllamaError('RUNNING_MODELS_FAILED', 'Failed to get running models', error);
    }
  }

  private async get(endpoint: string, params?: any): Promise<any> {
    return this.httpClient.get(endpoint, params);
  }

  private async post(endpoint: string, data: any, options?: any): Promise<any> {
    return this.httpClient.post(endpoint, data, options);
  }

  private async delete(endpoint: string, data?: any): Promise<any> {
    return this.httpClient.delete(endpoint, data);
  }

  private ensureServerOnline(): void {
    if (!this.serverInfo || this.serverInfo.status !== 'online') {
      throw new OllamaError('SERVER_OFFLINE', 'Ollama server is not online');
    }
  }

  private buildGenerationOptions(request: any): any {
    const options: any = {};

    if (request.temperature !== undefined) options.temperature = request.temperature;
    if (request.topP !== undefined) options.top_p = request.topP;
    if (request.topK !== undefined) options.top_k = request.topK;
    if (request.numPredict !== undefined) options.num_predict = request.numPredict;
    if (request.numCtx !== undefined) options.num_ctx = request.numCtx;
    if (request.repeatPenalty !== undefined) options.repeat_penalty = request.repeatPenalty;
    if (request.stop !== undefined) options.stop = request.stop;
    if (request.seed !== undefined) options.seed = request.seed;
    if (request.mirostat !== undefined) options.mirostat = request.mirostat;
    if (request.mirostatEta !== undefined) options.mirostat_eta = request.mirostat_eta;
    if (request.mirostatTau !== undefined) options.mirostat_tau = request.mirostat_tau;

    return options;
  }

  private transformModelData(data: any): OllamaModel {
    return {
      name: data.name,
      size: data.size,
      digest: data.digest,
      modified: data.modified_at,
      details: data.details || {},
      modelInfo: data.modelinfo || {},
      expiresAt: data.expires_at,
    };
  }

  private transformModelInfo(data: any): OllamaModelInfo {
    return {
      name: data.model,
      modified: data.modified_at,
      size: data.size,
      digest: data.digest,
      details: data.details || {},
      modelInfo: data.modelinfo || {},
      parameters: data.parameters || {},
      template: data.template,
      license: data.license,
      modelfile: data.modelfile,
    };
  }

  private async *streamPullProgress(stream: any): AsyncIterable<PullProgress> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n').filter((line) => line.trim());

        for (const line of lines) {
          try {
            const data = JSON.parse(line);

            if (data.status === 'success') {
              yield {
                status: 'completed',
                digest: data.digest,
                total: data.total,
                completed: data.total,
                percentage: 100,
                speed: 0,
                timestamp: new Date().toISOString(),
              };
            } else if (data.status === 'pulling manifest') {
              yield {
                status: 'downloading',
                digest: data.digest,
                total: data.total,
                completed: data.completed,
                percentage: data.total ? (data.completed / data.total) * 100 : 0,
                speed: 0,
                timestamp: new Date().toISOString(),
              };
            }
          } catch (e) {
            this.logger.warn('Failed to parse pull progress:', e);
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private async *streamGenerationResponse(stream: any): AsyncIterable<OllamaGenerationChunk> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n').filter((line) => line.trim());

        for (const line of lines) {
          try {
            const data = JSON.parse(line);

            yield {
              response: data.response || '',
              done: data.done || false,
              context: data.context,
              created_at: data.created_at,
              model: data.model,
              prompt_eval_count: data.prompt_eval_count,
              prompt_eval_duration: data.prompt_eval_duration,
              eval_count: data.eval_count,
              eval_duration: data.eval_duration,
              load_duration: data.load_duration,
              total_duration: data.total_duration,
            };
          } catch (e) {
            this.logger.warn('Failed to parse generation response:', e);
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private async *streamChatResponse(stream: any): AsyncIterable<OllamaChatChunk> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n').filter((line) => line.trim());

        for (const line of lines) {
          try {
            const data = JSON.parse(line);

            yield {
              message: data.message || { role: 'assistant', content: '' },
              done: data.done || false,
              created_at: data.created_at,
              model: data.model,
              prompt_eval_count: data.prompt_eval_count,
              prompt_eval_duration: data.prompt_eval_duration,
              eval_count: data.eval_count,
              eval_duration: data.eval_duration,
              load_duration: data.load_duration,
              total_duration: data.total_duration,
            };
          } catch (e) {
            this.logger.warn('Failed to parse chat response:', e);
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

### Local Model Manager Implementation

```typescript
// src/providers/implementations/local-model-manager.ts
export class LocalModelManager {
  private readonly client: OllamaClient;
  private readonly config: LocalModelConfig;
  private readonly models: Map<string, LocalModel>;
  private readonly logger: Logger;

  constructor(client: OllamaClient, config: LocalModelConfig) {
    this.client = client;
    this.config = config;
    this.models = new Map();
    this.logger = new Logger({ service: 'local-model-manager' });
  }

  async initialize(): Promise<void> {
    await this.refreshModels();
    this.logger.info('Local model manager initialized');
  }

  async refreshModels(): Promise<void> {
    try {
      const ollamaModels = await this.client.listModels();

      this.models.clear();

      for (const ollamaModel of ollamaModels) {
        const localModel = await this.transformToLocalModel(ollamaModel);
        this.models.set(localModel.id, localModel);
      }

      this.logger.info(`Refreshed ${this.models.size} local models`);
    } catch (error) {
      this.logger.error('Failed to refresh models:', error);
      throw error;
    }
  }

  async ensureModel(modelId: string): Promise<LocalModel> {
    let model = this.models.get(modelId);

    if (!model) {
      // Model not found locally, try to download
      this.logger.info(`Model ${modelId} not found locally, attempting to download`);
      await this.downloadModel(modelId);
      await this.refreshModels();
      model = this.models.get(modelId);
    }

    if (!model) {
      throw new Error(`Failed to ensure model availability: ${modelId}`);
    }

    return model;
  }

  async downloadModel(modelId: string, options?: DownloadOptions): Promise<void> {
    try {
      this.logger.info(`Starting download of model: ${modelId}`);

      for await (const progress of this.client.pullModel(modelId, options)) {
        this.logger.debug(`Download progress: ${progress.percentage.toFixed(1)}%`);

        if (progress.status === 'completed') {
          this.logger.info(`Model download completed: ${modelId}`);
          break;
        }
      }

      // Refresh model list
      await this.refreshModels();
    } catch (error) {
      throw new LocalModelError(
        'MODEL_DOWNLOAD_FAILED',
        `Failed to download model ${modelId}`,
        error
      );
    }
  }

  async deleteModel(modelId: string): Promise<void> {
    try {
      await this.client.deleteModel(modelId);
      this.models.delete(modelId);
      this.logger.info(`Deleted model: ${modelId}`);
    } catch (error) {
      throw new LocalModelError('MODEL_DELETE_FAILED', `Failed to delete model ${modelId}`, error);
    }
  }

  async getModelInfo(modelId: string): Promise<LocalModelInfo> {
    try {
      const ollamaInfo = await this.client.showModel(modelId);
      const model = this.models.get(modelId);

      return {
        ...model,
        details: ollamaInfo.details,
        parameters: ollamaInfo.parameters,
        template: ollamaInfo.template,
        license: ollamaInfo.license,
        modelfile: ollamaInfo.modelfile,
      };
    } catch (error) {
      throw new LocalModelError(
        'MODEL_INFO_FAILED',
        `Failed to get model info for ${modelId}`,
        error
      );
    }
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

  async getRecommendedModels(criteria: ModelRecommendationCriteria): Promise<LocalModel[]> {
    const available = Array.from(this.models.values());

    return available
      .filter((model) => {
        // Filter by size
        if (criteria.maxSizeGB && model.metadata.sizeGB > criteria.maxSizeGB) {
          return false;
        }

        // Filter by capabilities
        if (criteria.requireStreaming && !model.capabilities.streaming) {
          return false;
        }

        // Filter by architecture
        if (
          criteria.architectures &&
          !criteria.architectures.includes(model.metadata.architecture)
        ) {
          return false;
        }

        return true;
      })
      .sort((a, b) => {
        // Sort by relevance (could be enhanced with more sophisticated scoring)
        let scoreA = 0;
        let scoreB = 0;

        // Prefer smaller models for resource efficiency
        scoreA += (100 - (a.metadata.sizeGB || 0)) * 0.3;
        scoreB += (100 - (b.metadata.sizeGB || 0)) * 0.3;

        // Prefer models with better capabilities
        scoreA += Object.values(a.capabilities).filter(Boolean).length * 10;
        scoreB += Object.values(b.capabilities).filter(Boolean).length * 10;

        return scoreB - scoreA;
      });
  }

  async getModelUsage(modelId: string): Promise<ModelUsage> {
    const model = this.models.get(modelId);
    if (!model) {
      throw new Error(`Model not found: ${modelId}`);
    }

    // This would typically track usage over time
    return {
      modelId,
      totalRequests: 0,
      totalTokens: 0,
      averageLatency: 0,
      lastUsed: model.metadata.lastUsed,
      resourceUsage: {
        memoryUsage: 0,
        cpuUsage: 0,
        gpuUsage: 0,
      },
    };
  }

  async updateModelUsage(modelId: string, usage: Partial<ModelUsage>): Promise<void> {
    const model = this.models.get(modelId);
    if (model) {
      model.metadata.lastUsed = new Date().toISOString();
      // Update other usage metrics as needed
    }
  }

  private async transformToLocalModel(ollamaModel: OllamaModel): Promise<LocalModel> {
    const name = ollamaModel.name;
    const [model, tag] = name.split(':');
    const sizeGB = ollamaModel.size ? ollamaModel.size / (1024 * 1024 * 1024) : 0;

    return {
      id: name,
      name: model,
      provider: 'local-llm',
      version: tag || 'latest',
      capabilities: this.inferCapabilities(name, ollamaModel),
      metadata: {
        description: `Local model: ${name}`,
        category: this.categorizeModel(name),
        architecture: this.inferArchitecture(name),
        sizeGB,
        parameters: this.estimateParameters(name, sizeGB),
        modified: ollamaModel.modified,
        digest: ollamaModel.digest,
        localPath: this.getLocalModelPath(name),
        lastUsed: null,
        tags: this.generateModelTags(name),
      },
    };
  }

  private inferCapabilities(name: string, ollamaModel: OllamaModel): ModelCapabilities {
    const capabilities: ModelCapabilities = {
      maxTokens: 4096, // Default, could be inferred from model info
      streaming: true,
      functionCalling: false,
      multimodal: false,
      toolUse: false,
      localExecution: true,
      offlineMode: true,
      privacyGuaranteed: true,
    };

    // Infer capabilities from model name
    const lowerName = name.toLowerCase();

    if (lowerName.includes('vision') || lowerName.includes('llava')) {
      capabilities.multimodal = true;
    }

    if (lowerName.includes('tool') || lowerName.includes('function')) {
      capabilities.toolUse = true;
      capabilities.functionCalling = true;
    }

    // Adjust context window based on model size
    if (lowerName.includes('70b') || lowerName.includes('8x7b')) {
      capabilities.maxTokens = 4096;
    } else if (lowerName.includes('34b') || lowerName.includes('13b')) {
      capabilities.maxTokens = 8192;
    } else if (lowerName.includes('7b') || lowerName.includes('8b')) {
      capabilities.maxTokens = 4096;
    }

    return capabilities;
  }

  private categorizeModel(name: string): string {
    const lowerName = name.toLowerCase();

    if (lowerName.includes('llama')) return 'llama';
    if (lowerName.includes('mistral')) return 'mistral';
    if (lowerName.includes('codellama')) return 'code';
    if (lowerName.includes('vicuna')) return 'chat';
    if (lowerName.includes('wizard')) return 'chat';
    if (lowerName.includes('yi')) return 'general';
    if (lowerName.includes('qwen')) return 'general';
    if (lowerName.includes('phi')) return 'general';

    return 'other';
  }

  private inferArchitecture(name: string): string {
    const lowerName = name.toLowerCase();

    if (lowerName.includes('llama')) return 'llama';
    if (lowerName.includes('mistral')) return 'mistral';
    if (lowerName.includes('mixtral')) return 'mixtral';
    if (lowerName.includes('gemma')) return 'gemma';
    if (lowerName.includes('phi')) return 'phi';
    if (lowerName.includes('qwen')) return 'qwen';

    return 'unknown';
  }

  private estimateParameters(name: string, sizeGB: number): string {
    const lowerName = name.toLowerCase();

    // Extract parameter count from name if available
    const paramMatch = lowerName.match(/(\d+)b/);
    if (paramMatch) {
      return `${paramMatch[1]}B`;
    }

    // Estimate based on size
    if (sizeGB > 20) return '70B+';
    if (sizeGB > 10) return '34B';
    if (sizeGB > 5) return '13B';
    if (sizeGB > 3) return '7B';

    return 'Unknown';
  }

  private generateModelTags(name: string): string[] {
    const tags: string[] = [];
    const lowerName = name.toLowerCase();

    // Size tags
    if (lowerName.includes('70b')) tags.push('large');
    else if (lowerName.includes('34b')) tags.push('medium-large');
    else if (lowerName.includes('13b')) tags.push('medium');
    else if (lowerName.includes('7b') || lowerName.includes('8b')) tags.push('small');

    // Capability tags
    if (lowerName.includes('code')) tags.push('coding');
    if (lowerName.includes('chat')) tags.push('chat');
    if (lowerName.includes('instruct')) tags.push('instruction');
    if (lowerName.includes('vision')) tags.push('multimodal');

    // Architecture tags
    if (lowerName.includes('llama')) tags.push('llama');
    if (lowerName.includes('mistral')) tags.push('mistral');
    if (lowerName.includes('mixtral')) tags.push('moe');

    return tags;
  }

  private getLocalModelPath(name: string): string {
    // This would typically be configurable
    return `/usr/local/share/ollama/models/${name}`;
  }
}
```

### Resource Monitor Implementation

```typescript
// src/providers/implementations/resource-monitor.ts
export class ResourceMonitor {
  private readonly config: ResourceMonitorConfig;
  private readonly systemMonitor: SystemMonitor;
  private readonly logger: Logger;
  private monitoringInterval: NodeJS.Timeout | null = null;
  private currentUsage: ResourceUsage;

  constructor(config: ResourceMonitorConfig) {
    this.config = config;
    this.systemMonitor = new SystemMonitor();
    this.logger = new Logger({ service: 'resource-monitor' });
    this.currentUsage = {
      cpu: { usage: 0, cores: 0 },
      memory: { used: 0, total: 0, percentage: 0 },
      gpu: { usage: 0, memoryUsed: 0, memoryTotal: 0, temperature: 0 },
      disk: { used: 0, total: 0, percentage: 0 },
      network: { bytesIn: 0, bytesOut: 0 },
    };
  }

  async initialize(): Promise<void> {
    await this.systemMonitor.initialize();
    this.startMonitoring();
    this.logger.info('Resource monitor initialized');
  }

  async checkResources(): Promise<void> {
    const usage = await this.getCurrentUsage();

    // Check CPU usage
    if (usage.cpu.percentage > this.config.limits.maxCpuUsage) {
      throw new ResourceError(
        'CPU_LIMIT_EXCEEDED',
        `CPU usage ${usage.cpu.percentage}% exceeds limit ${this.config.limits.maxCpuUsage}%`
      );
    }

    // Check memory usage
    if (usage.memory.percentage > this.config.limits.maxMemoryUsage) {
      throw new ResourceError(
        'MEMORY_LIMIT_EXCEEDED',
        `Memory usage ${usage.memory.percentage}% exceeds limit ${this.config.limits.maxMemoryUsage}%`
      );
    }

    // Check GPU usage if available
    if (usage.gpu.usage > this.config.limits.maxGpuUsage) {
      throw new ResourceError(
        'GPU_LIMIT_EXCEEDED',
        `GPU usage ${usage.gpu.usage}% exceeds limit ${this.config.limits.maxGpuUsage}%`
      );
    }

    // Check disk space
    if (usage.disk.percentage > this.config.limits.maxDiskUsage) {
      throw new ResourceError(
        'DISK_LIMIT_EXCEEDED',
        `Disk usage ${usage.disk.percentage}% exceeds limit ${this.config.limits.maxDiskUsage}%`
      );
    }
  }

  async getCurrentUsage(): Promise<ResourceUsage> {
    return { ...this.currentUsage };
  }

  async getResourceHistory(duration: number): Promise<ResourceUsage[]> {
    // This would typically retrieve from a time-series database
    return [this.currentUsage]; // Simplified
  }

  async getSystemInfo(): Promise<SystemInfo> {
    return this.systemMonitor.getSystemInfo();
  }

  private startMonitoring(): void {
    if (this.config.monitoring.enabled) {
      this.monitoringInterval = setInterval(
        () => this.updateUsage(),
        this.config.monitoring.interval
      );
    }
  }

  private async updateUsage(): Promise<void> {
    try {
      const cpuUsage = await this.systemMonitor.getCpuUsage();
      const memoryUsage = await this.systemMonitor.getMemoryUsage();
      const gpuUsage = await this.systemMonitor.getGpuUsage();
      const diskUsage = await this.systemMonitor.getDiskUsage();
      const networkUsage = await this.systemMonitor.getNetworkUsage();

      this.currentUsage = {
        cpu: cpuUsage,
        memory: memoryUsage,
        gpu: gpuUsage,
        disk: diskUsage,
        network: networkUsage,
        timestamp: new Date().toISOString(),
      };

      // Check for resource alerts
      await this.checkResourceAlerts();
    } catch (error) {
      this.logger.error('Failed to update resource usage:', error);
    }
  }

  private async checkResourceAlerts(): Promise<void> {
    const usage = this.currentUsage;

    // CPU alerts
    if (usage.cpu.percentage > this.config.alerts.cpuWarning) {
      this.logger.warn(`High CPU usage: ${usage.cpu.percentage}%`);
    }

    // Memory alerts
    if (usage.memory.percentage > this.config.alerts.memoryWarning) {
      this.logger.warn(`High memory usage: ${usage.memory.percentage}%`);
    }

    // GPU alerts
    if (usage.gpu.usage > this.config.alerts.gpuWarning) {
      this.logger.warn(`High GPU usage: ${usage.gpu.usage}%`);
    }

    // GPU temperature alerts
    if (usage.gpu.temperature > this.config.alerts.gpuTemperatureWarning) {
      this.logger.warn(`High GPU temperature: ${usage.gpu.temperature}°C`);
    }
  }

  async dispose(): Promise<void> {
    if (this.monitoringInterval) {
      clearInterval(this.monitoringInterval);
      this.monitoringInterval = null;
    }

    await this.systemMonitor.dispose();
    this.logger.info('Resource monitor disposed');
  }
}
```

### Download Manager Implementation

```typescript
// src/providers/implementations/download-manager.ts
export class DownloadManager {
  private readonly config: DownloadConfig;
  private readonly activeDownloads: Map<string, Download>;
  private readonly logger: Logger;

  constructor(config: DownloadConfig) {
    this.config = config;
    this.activeDownloads = new Map();
    this.logger = new Logger({ service: 'download-manager' });
  }

  async initialize(): Promise<void> {
    // Clean up any interrupted downloads
    await this.cleanupInterruptedDownloads();
    this.logger.info('Download manager initialized');
  }

  async downloadModel(
    modelId: string,
    options?: DownloadOptions
  ): Promise<AsyncIterable<DownloadProgress>> {
    // Check if already downloading
    if (this.activeDownloads.has(modelId)) {
      throw new DownloadError(
        'ALREADY_DOWNLOADING',
        `Model ${modelId} is already being downloaded`
      );
    }

    // Check disk space
    await this.checkDiskSpace(modelId);

    const download = new Download(modelId, this.config, options);
    this.activeDownloads.set(modelId, download);

    try {
      return download.start();
    } catch (error) {
      this.activeDownloads.delete(modelId);
      throw error;
    }
  }

  async pauseDownload(modelId: string): Promise<void> {
    const download = this.activeDownloads.get(modelId);
    if (download) {
      await download.pause();
    }
  }

  async resumeDownload(modelId: string): Promise<void> {
    const download = this.activeDownloads.get(modelId);
    if (download) {
      await download.resume();
    }
  }

  async cancelDownload(modelId: string): Promise<void> {
    const download = this.activeDownloads.get(modelId);
    if (download) {
      await download.cancel();
      this.activeDownloads.delete(modelId);
    }
  }

  async getDownloadStatus(modelId: string): Promise<DownloadStatus | null> {
    const download = this.activeDownloads.get(modelId);
    return download ? download.getStatus() : null;
  }

  async getActiveDownloads(): Promise<DownloadStatus[]> {
    const statuses: DownloadStatus[] = [];

    for (const download of this.activeDownloads.values()) {
      statuses.push(download.getStatus());
    }

    return statuses;
  }

  private async checkDiskSpace(modelId: string): Promise<void> {
    const requiredSpace = await this.estimateModelSize(modelId);
    const availableSpace = await this.getAvailableDiskSpace();

    if (availableSpace < requiredSpace * 1.2) {
      // 20% buffer
      throw new DownloadError(
        'INSUFFICIENT_SPACE',
        `Insufficient disk space. Required: ${requiredSpace}GB, Available: ${availableSpace}GB`
      );
    }
  }

  private async estimateModelSize(modelId: string): Promise<number> {
    // This would typically fetch model metadata or use a lookup table
    const sizeMap: Record<string, number> = {
      'llama2:7b': 3.8,
      'llama2:13b': 7.4,
      'llama2:70b': 38.9,
      'codellama:7b': 3.8,
      'codellama:13b': 7.4,
      'codellama:34b': 19.3,
      'mistral:7b': 4.1,
      'mixtral:8x7b': 26.4,
    };

    return sizeMap[modelId] || 10; // Default 10GB
  }

  private async getAvailableDiskSpace(): Promise<number> {
    // This would typically check actual disk space
    return 100; // 100GB available
  }

  private async cleanupInterruptedDownloads(): Promise<void> {
    // Clean up any partial downloads from previous sessions
    this.logger.info('Cleaning up interrupted downloads');
  }

  async dispose(): Promise<void> {
    // Cancel all active downloads
    for (const [modelId, download] of this.activeDownloads) {
      await download.cancel();
    }

    this.activeDownloads.clear();
    this.logger.info('Download manager disposed');
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/local-llm-config.schema.ts
export const LocalLLMConfigSchema = {
  type: 'object',
  required: ['ollama', 'rateLimits'],
  properties: {
    ollama: {
      type: 'object',
      required: ['endpoint'],
      properties: {
        endpoint: {
          type: 'string',
          default: 'http://localhost:11434',
          description: 'Ollama server endpoint',
        },
        timeout: {
          type: 'integer',
          minimum: 5000,
          maximum: 300000,
          default: 30000,
          description: 'Request timeout in milliseconds',
        },
        retries: {
          type: 'integer',
          minimum: 0,
          maximum: 10,
          default: 3,
          description: 'Number of retry attempts',
        },
        retryDelay: {
          type: 'integer',
          minimum: 100,
          maximum: 10000,
          default: 1000,
          description: 'Retry delay in milliseconds',
        },
      },
    },
    models: {
      type: 'object',
      properties: {
        autoDownload: {
          type: 'boolean',
          default: false,
          description: 'Automatically download missing models',
        },
        defaultModel: {
          type: 'string',
          description: 'Default model to use',
        },
        preferredModels: {
          type: 'array',
          items: { type: 'string' },
          description: 'Preferred models for selection',
        },
        excludedModels: {
          type: 'array',
          items: { type: 'string' },
          description: 'Models to exclude from selection',
        },
        maxSizeGB: {
          type: 'number',
          minimum: 1,
          maximum: 1000,
          description: 'Maximum model size in GB',
        },
        cacheTimeout: {
          type: 'integer',
          default: 300,
          description: 'Model cache timeout in seconds',
        },
      },
    },
    resources: {
      type: 'object',
      properties: {
        limits: {
          type: 'object',
          properties: {
            maxCpuUsage: {
              type: 'number',
              minimum: 0,
              maximum: 100,
              default: 80,
              description: 'Maximum CPU usage percentage',
            },
            maxMemoryUsage: {
              type: 'number',
              minimum: 0,
              maximum: 100,
              default: 80,
              description: 'Maximum memory usage percentage',
            },
            maxGpuUsage: {
              type: 'number',
              minimum: 0,
              maximum: 100,
              default: 90,
              description: 'Maximum GPU usage percentage',
            },
            maxDiskUsage: {
              type: 'number',
              minimum: 0,
              maximum: 100,
              default: 90,
              description: 'Maximum disk usage percentage',
            },
          },
        },
        monitoring: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            interval: {
              type: 'integer',
              minimum: 1000,
              maximum: 60000,
              default: 5000,
              description: 'Monitoring interval in milliseconds',
            },
          },
        },
        alerts: {
          type: 'object',
          properties: {
            cpuWarning: { type: 'number', default: 70 },
            memoryWarning: { type: 'number', default: 70 },
            gpuWarning: { type: 'number', default: 80 },
            gpuTemperatureWarning: { type: 'number', default: 85 },
          },
        },
      },
    },
    downloads: {
      type: 'object',
      properties: {
        concurrentDownloads: {
          type: 'integer',
          minimum: 1,
          maximum: 5,
          default: 2,
          description: 'Maximum concurrent downloads',
        },
        bandwidthLimit: {
          type: 'integer',
          minimum: 1024,
          description: 'Bandwidth limit in bytes per second',
        },
        resumeSupport: {
          type: 'boolean',
          default: true,
          description: 'Enable download resume support',
        },
        verification: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            checksumVerification: { type: 'boolean', default: true },
          },
        },
        storage: {
          type: 'object',
          properties: {
            downloadPath: {
              type: 'string',
              description: 'Custom download path for models',
            },
            tempPath: {
              type: 'string',
              description: 'Temporary path for downloads',
            },
          },
        },
      },
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
          maximum: 10,
          default: 2,
          description: 'Maximum concurrent requests',
        },
      },
    },
    security: {
      type: 'object',
      properties: {
        sandboxMode: {
          type: 'boolean',
          default: false,
          description: 'Run models in sandbox mode',
        },
        networkIsolation: {
          type: 'boolean',
          default: true,
          description: 'Isolate models from network access',
        },
        accessControl: {
          type: 'boolean',
          default: false,
          description: 'Enable access control for model operations',
        },
      },
    },
    performance: {
      type: 'object',
      properties: {
        optimization: {
          type: 'object',
          properties: {
            enableQuantization: { type: 'boolean', default: false },
            enableCaching: { type: 'boolean', default: true },
            enableBatching: { type: 'boolean', default: false },
          },
        },
        tuning: {
          type: 'object',
          properties: {
            defaultTemperature: { type: 'number', default: 0.7 },
            defaultTopP: { type: 'number', default: 0.9 },
            defaultTopK: { type: 'integer', default: 40 },
            defaultRepeatPenalty: { type: 'number', default: 1.1 },
          },
        },
      },
    },
  },
};
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/local-llm-provider.test.ts
describe('LocalLLMProvider', () => {
  let provider: LocalLLMProvider;
  let mockClient: jest.Mocked<OllamaClient>;

  beforeEach(() => {
    mockClient = new OllamaClient({
      endpoint: 'http://localhost:11434',
    } as any) as jest.Mocked<OllamaClient>;

    provider = new LocalLLMProvider({
      ollama: { endpoint: 'http://localhost:11434' },
      rateLimits: {
        requestsPerMinute: 60,
        tokensPerMinute: 60000,
      },
    });
  });

  describe('model management', () => {
    it('should list available models', async () => {
      const mockModels = [
        { name: 'llama2:7b', size: 3892680960 },
        { name: 'codellama:13b', size: 7381975040 },
      ];

      mockClient.listModels.mockResolvedValue(mockModels as any);

      await provider.initialize();

      const capabilities = await provider.getCapabilities();
      expect(capabilities.models).toHaveLength(2);
      expect(capabilities.models[0].id).toBe('llama2:7b');
    });
  });

  describe('text generation', () => {
    it('should handle local model generation', async () => {
      const request: MessageRequest = {
        model: 'llama2:7b',
        messages: [{ role: 'user', content: 'Hello' }],
        maxTokens: 100,
      };

      const mockStream = createMockGenerationStream();
      mockClient.generateChatCompletion.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
    });
  });

  describe('resource monitoring', () => {
    it('should check resource limits', async () => {
      const request: MessageRequest = {
        model: 'llama2:7b',
        messages: [{ role: 'user', content: 'Hello' }],
      };

      // Mock resource monitor to throw limit exceeded
      const resourceMonitor = provider['resourceMonitor'];
      jest
        .spyOn(resourceMonitor, 'checkResources')
        .mockRejectedValue(new Error('CPU limit exceeded'));

      await expect(provider.sendMessage(request)).rejects.toThrow('CPU limit exceeded');
    });
  });
});
```

### Integration Tests

```typescript
// tests/providers/local-llm-integration.test.ts
describe('Local LLM Integration', () => {
  let provider: LocalLLMProvider;

  beforeAll(async () => {
    // Skip tests if Ollama is not available
    try {
      const response = await fetch('http://localhost:11434/api/version');
      if (!response.ok) {
        throw new Error('Ollama not available');
      }
    } catch (error) {
      console.warn('Skipping Local LLM integration tests - Ollama not available');
      return;
    }

    provider = new LocalLLMProvider({
      ollama: { endpoint: 'http://localhost:11434' },
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 30000,
      },
    });

    await provider.initialize();
  });

  it('should generate text with local model', async () => {
    if (!provider) return;

    // Ensure we have a model to test with
    try {
      await provider.downloadModel('llama2:7b');
    } catch (error) {
      // Model might already exist
    }

    const request: MessageRequest = {
      model: 'llama2:7b',
      messages: [
        {
          role: 'user',
          content: 'What is the capital of France?',
        },
      ],
      maxTokens: 100,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('Paris');
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should track resource usage', async () => {
    if (!provider) return;

    const usage = await provider.getResourceUsage();

    expect(usage.cpu).toBeDefined();
    expect(usage.memory).toBeDefined();
    expect(usage.timestamp).toBeDefined();
  });
});
```

## Success Metrics

### Performance Metrics

- Model download speed: > 10MB/s
- Generation latency: < 5s for 100 tokens
- Resource monitoring overhead: < 1% CPU
- Model loading time: < 30s
- Memory usage efficiency: > 80%

### Reliability Metrics

- Model download success rate: 95%+
- Generation success rate: 99%+
- Resource monitoring accuracy: 95%+
- Error handling coverage: 100%
- Recovery from failures: 90%+

### Integration Metrics

- Model discovery accuracy: 100%
- Resource limit enforcement: 100%
- Local execution privacy: 100%
- Test coverage: 90%+

## Dependencies

### External Dependencies

- `node-fetch`: HTTP client for Ollama API
- `systeminformation`: System resource monitoring
- `tar`: Model archive extraction
- `crypto`: Checksum verification

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### Model Security

- Verify model checksums
- Scan models for malware
- Implement model sandboxing
- Control model access permissions

### Resource Security

- Enforce resource limits
- Monitor for resource abuse
- Implement access controls
- Audit resource usage

### Data Privacy

- Ensure local data processing
- Prevent data exfiltration
- Implement secure storage
- Comply with privacy regulations

## Deliverables

1. **Local LLM Provider** (`src/providers/implementations/local-llm-provider.ts`)
2. **Ollama Client** (`src/providers/implementations/ollama-client.ts`)
3. **Model Manager** (`src/providers/implementations/local-model-manager.ts`)
4. **Resource Monitor** (`src/providers/implementations/resource-monitor.ts`)
5. **Download Manager** (`src/providers/implementations/download-manager.ts`)
6. **Configuration Schema** (`src/providers/configs/local-llm-config.schema.ts`)
7. **Unit Tests** (`tests/providers/local-llm-provider.test.ts`)
8. **Integration Tests** (`tests/providers/local-llm-integration.test.ts`)
9. **Documentation** (`docs/providers/local-llm.md`)

This implementation provides comprehensive local LLM support with Ollama integration, resource monitoring, model management, and robust download capabilities while maintaining privacy and cost control.
