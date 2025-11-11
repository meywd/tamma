# Task 2: Implement Core Discovery Service

## Overview

Implement the core DynamicModelDiscovery class that orchestrates model discovery across all registered providers with health checking and periodic refresh capabilities.

## Objectives

- Create DynamicModelDiscovery class with full provider orchestration
- Implement provider enumeration and model listing with error handling
- Add periodic refresh with configurable intervals and retry logic
- Integrate provider health checking with automatic status updates

## Implementation Steps

### Subtask 2.1: Create DynamicModelDiscovery Class

**Description**: Implement the main discovery service class that coordinates all model discovery operations.

**Implementation Details**:

1. **Create Core Discovery Service**:

```typescript
// packages/providers/src/services/dynamic-model-discovery.ts
import {
  IModelDiscoveryService,
  IProviderDiscovery,
  IModelCache,
  IModelCapabilityMapper,
  IModelUpdateSubscriber,
  DiscoveryServiceStatus,
  DiscoveryServiceConfiguration,
  ModelUpdateEvent,
  ProviderUpdateEvent,
  CapabilityUpdateEvent,
  ModelUpdateCallback,
  ProviderUpdateCallback,
  CapabilityUpdateCallback,
  UnsubscribeFunction,
  ProviderStatus,
} from '../interfaces/model-discovery.interface';

import {
  IModelProvider,
  ProviderHealth,
  ProviderConfiguration,
} from '../interfaces/provider-discovery.interface';

import { Model, ModelFilter, ModelCapabilities } from '../interfaces/model.types';
import { EventEmitter } from 'events';

export class DynamicModelDiscovery extends EventEmitter implements IModelDiscoveryService {
  private providerDiscovery: IProviderDiscovery;
  private modelCache: IModelCache;
  private capabilityMapper: IModelCapabilityMapper;
  private updateSubscriber: IModelUpdateSubscriber;

  private configuration: DiscoveryServiceConfiguration;
  private status: DiscoveryServiceStatus;
  private refreshTimer?: NodeJS.Timeout;
  private healthCheckTimer?: NodeJS.Timeout;
  private isRunning: boolean = false;
  private discoveryPromises: Map<string, Promise<Model[]>> = new Map();

  constructor(
    providerDiscovery: IProviderDiscovery,
    modelCache: IModelCache,
    capabilityMapper: IModelCapabilityMapper,
    updateSubscriber: IModelUpdateSubscriber,
    configuration: Partial<DiscoveryServiceConfiguration> = {}
  ) {
    super();

    this.providerDiscovery = providerDiscovery;
    this.modelCache = modelCache;
    this.capabilityMapper = capabilityMapper;
    this.updateSubscriber = updateSubscriber;

    this.configuration = this.mergeWithDefaults(configuration);
    this.status = this.initializeStatus();

    this.setupProviderEventHandlers();
    this.setupCacheEventHandlers();
  }

  // Core discovery operations
  async discoverModels(): Promise<Model[]> {
    this.ensureRunning();

    const startTime = Date.now();
    const providers = this.providerDiscovery.getRegisteredProviders();
    const allModels: Model[] = [];
    const errors: Array<{ provider: string; error: Error }> = [];

    this.emit('discoveryStarted', { providerCount: providers.length });

    // Discover models from all providers concurrently
    const discoveryPromises = providers.map(async (provider) => {
      try {
        const models = await this.discoverProviderModels(provider.name);
        return { provider: provider.name, models, error: null };
      } catch (error) {
        errors.push({ provider: provider.name, error: error as Error });
        return { provider: provider.name, models: [], error };
      }
    });

    const results = await Promise.allSettled(discoveryPromises);

    for (const result of results) {
      if (result.status === 'fulfilled') {
        allModels.push(...result.value.models);
      }
    }

    // Update cache with discovered models
    await this.updateCache(allModels);

    // Update status
    this.status.lastRefresh = new Date().toISOString();
    this.status.totalModels = allModels.length;
    this.status.totalProviders = providers.length;
    this.status.activeProviders = providers.filter(
      (p) => p.status === ProviderStatus.ACTIVE
    ).length;

    const duration = Date.now() - startTime;

    this.emit('discoveryCompleted', {
      modelCount: allModels.length,
      providerCount: providers.length,
      errorCount: errors.length,
      duration,
    });

    if (errors.length > 0) {
      this.emit('discoveryErrors', errors);
    }

    return allModels;
  }

  async refreshProvider(providerName: string): Promise<Model[]> {
    this.ensureRunning();

    const provider = this.providerDiscovery.getProvider(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    // Check if refresh is already in progress
    if (this.discoveryPromises.has(providerName)) {
      return this.discoveryPromises.get(providerName)!;
    }

    const discoveryPromise = this.performProviderRefresh(provider);
    this.discoveryPromises.set(providerName, discoveryPromise);

    try {
      const models = await discoveryPromise;
      return models;
    } finally {
      this.discoveryPromises.delete(providerName);
    }
  }

  async refreshAllProviders(): Promise<Model[]> {
    this.ensureRunning();

    const providers = this.providerDiscovery.getRegisteredProviders();
    const refreshPromises = providers.map((provider) =>
      this.refreshProvider(provider.name).catch((error) => {
        this.emit('providerRefreshError', { provider: provider.name, error });
        return [] as Model[];
      })
    );

    const results = await Promise.allSettled(refreshPromises);
    const allModels: Model[] = [];

    for (const result of results) {
      if (result.status === 'fulfilled') {
        allModels.push(...result.value);
      }
    }

    return allModels;
  }

  // Model retrieval operations
  async getModel(modelId: string): Promise<Model | undefined> {
    return this.modelCache.get(modelId);
  }

  async getModelsByProvider(providerName: string): Promise<Model[]> {
    return this.modelCache.getProviderModels(providerName);
  }

  async getModelsByCapability(capability: string): Promise<Model[]> {
    return this.modelCache.getByCapability(capability);
  }

  async searchModels(filter: ModelFilter): Promise<Model[]> {
    return this.modelCache.search(filter);
  }

  // Capability operations
  async getModelCapabilities(modelId: string): Promise<ModelCapabilities | undefined> {
    const model = await this.getModel(modelId);
    return model?.capabilities;
  }

  async validateModelCapabilities(
    modelId: string,
    requiredCapabilities: string[]
  ): Promise<boolean> {
    const capabilities = await this.getModelCapabilities(modelId);
    if (!capabilities) return false;

    return (
      this.capabilityMapper.validateCapabilities(capabilities).valid &&
      requiredCapabilities.every((cap) => this.hasCapability(capabilities, cap))
    );
  }

  // Benchmarking operations (placeholder - will be implemented in Task 6)
  async benchmarkModel(modelId: string, benchmarkType?: string): Promise<any> {
    throw new Error('Benchmarking not yet implemented');
  }

  async getModelBenchmarks(modelId: string): Promise<any[]> {
    throw new Error('Benchmarking not yet implemented');
  }

  // Subscription operations
  subscribeToModelUpdates(callback: ModelUpdateCallback): UnsubscribeFunction {
    return this.updateSubscriber.subscribe({
      id: this.generateSubscriptionId(),
      clientId: 'discovery-service',
      type: 'model_updates',
      filters: [],
      callback,
      options: {},
      status: 'active' as any,
      createdAt: new Date().toISOString(),
    });
  }

  subscribeToProviderUpdates(
    providerName: string,
    callback: ProviderUpdateCallback
  ): UnsubscribeFunction {
    return this.updateSubscriber.subscribe({
      id: this.generateSubscriptionId(),
      clientId: 'discovery-service',
      type: 'provider_updates',
      filters: [
        {
          id: this.generateFilterId(),
          type: 'provider_filter' as any,
          conditions: [
            {
              field: 'providerName',
              operator: 'equals' as any,
              value: providerName,
            },
          ],
          operator: 'AND',
          enabled: true,
        },
      ],
      callback: callback as any,
      options: {},
      status: 'active' as any,
      createdAt: new Date().toISOString(),
    });
  }

  subscribeToCapabilityUpdates(
    capability: string,
    callback: CapabilityUpdateCallback
  ): UnsubscribeFunction {
    return this.updateSubscriber.subscribe({
      id: this.generateSubscriptionId(),
      clientId: 'discovery-service',
      type: 'capability_updates',
      filters: [
        {
          id: this.generateFilterId(),
          type: 'capability_filter' as any,
          conditions: [
            {
              field: 'capability',
              operator: 'equals' as any,
              value: capability,
            },
          ],
          operator: 'AND',
          enabled: true,
        },
      ],
      callback: callback as any,
      options: {},
      status: 'active' as any,
      createdAt: new Date().toISOString(),
    });
  }

  // Service lifecycle
  async start(): Promise<void> {
    if (this.isRunning) {
      return;
    }

    this.isRunning = true;
    this.status.status = 'starting';
    this.status.startTime = new Date().toISOString();

    try {
      // Start provider discovery
      await this.initializeProviders();

      // Start periodic refresh
      this.startPeriodicRefresh();

      // Start health checking
      this.startHealthChecking();

      // Perform initial discovery
      await this.discoverModels();

      this.status.status = 'running';
      this.emit('serviceStarted', { configuration: this.configuration });
    } catch (error) {
      this.status.status = 'error';
      this.emit('serviceError', { error, phase: 'startup' });
      throw error;
    }
  }

  async stop(): Promise<void> {
    if (!this.isRunning) {
      return;
    }

    this.isRunning = false;
    this.status.status = 'stopping';

    try {
      // Stop timers
      if (this.refreshTimer) {
        clearInterval(this.refreshTimer);
        this.refreshTimer = undefined;
      }

      if (this.healthCheckTimer) {
        clearInterval(this.healthCheckTimer);
        this.healthCheckTimer = undefined;
      }

      // Cancel ongoing discoveries
      for (const [providerName, promise] of this.discoveryPromises) {
        // Note: In a real implementation, you'd need cancellation tokens
        this.discoveryPromises.delete(providerName);
      }

      this.status.status = 'stopped';
      this.emit('serviceStopped', { uptime: this.getUptime() });
    } catch (error) {
      this.status.status = 'error';
      this.emit('serviceError', { error, phase: 'shutdown' });
      throw error;
    }
  }

  getStatus(): DiscoveryServiceStatus {
    this.updateStatus();
    return { ...this.status };
  }

  getConfiguration(): DiscoveryServiceConfiguration {
    return { ...this.configuration };
  }

  // Private helper methods
  private async discoverProviderModels(providerName: string): Promise<Model[]> {
    const provider = this.providerDiscovery.getProvider(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    this.emit('providerDiscoveryStarted', { providerName });

    try {
      const providerModels = await this.providerDiscovery.discoverProviderModels(providerName);

      // Map capabilities to standard format
      const models = await Promise.all(
        providerModels.map(async (model) => {
          const capabilities = this.capabilityMapper.mapCapabilities(model, provider.type);
          return {
            ...model,
            capabilities,
            providerName,
            discoveredAt: new Date().toISOString(),
          };
        })
      );

      this.emit('providerDiscoveryCompleted', {
        providerName,
        modelCount: models.length,
      });

      return models;
    } catch (error) {
      this.emit('providerDiscoveryError', { providerName, error });
      throw error;
    }
  }

  private async performProviderRefresh(provider: IModelProvider): Promise<Model[]> {
    const startTime = Date.now();

    try {
      // Check provider health first
      const health = await provider.healthCheck();
      if (health.status !== 'healthy') {
        throw new Error(`Provider health check failed: ${health.status}`);
      }

      // Get fresh models from provider
      const models = await this.discoverProviderModels(provider.name);

      // Update cache
      await this.modelCache.setProviderModels(provider.name, models, {
        ttl: this.configuration.cacheTTL,
      });

      // Publish update events
      await this.publishModelUpdates(provider.name, models);

      const duration = Date.now() - startTime;

      this.emit('providerRefreshed', {
        providerName: provider.name,
        modelCount: models.length,
        duration,
      });

      return models;
    } catch (error) {
      this.emit('providerRefreshError', {
        providerName: provider.name,
        error,
      });
      throw error;
    }
  }

  private async updateCache(models: Model[]): Promise<void> {
    const modelMap = new Map<string, Model>();

    for (const model of models) {
      modelMap.set(model.id, model);
    }

    await this.modelCache.setMultiple(modelMap, {
      ttl: this.configuration.cacheTTL,
    });
  }

  private async publishModelUpdates(providerName: string, models: Model[]): Promise<void> {
    // Get previous models for comparison
    const previousModels = await this.modelCache.getProviderModels(providerName);
    const previousModelMap = new Map(previousModels.map((m) => [m.id, m]));
    const currentModelMap = new Map(models.map((m) => [m.id, m]));

    // Detect added models
    for (const [id, model] of currentModelMap) {
      if (!previousModelMap.has(id)) {
        const event: ModelUpdateEvent = {
          type: 'added',
          modelId: id,
          providerName,
          model,
          timestamp: new Date().toISOString(),
        };

        await this.updateSubscriber.publishModelUpdate(event);
      }
    }

    // Detect removed models
    for (const [id, model] of previousModelMap) {
      if (!currentModelMap.has(id)) {
        const event: ModelUpdateEvent = {
          type: 'removed',
          modelId: id,
          providerName,
          previousModel: model,
          timestamp: new Date().toISOString(),
        };

        await this.updateSubscriber.publishModelUpdate(event);
      }
    }

    // Detect updated models
    for (const [id, currentModel] of currentModelMap) {
      const previousModel = previousModelMap.get(id);
      if (previousModel && !this.modelsEqual(previousModel, currentModel)) {
        const event: ModelUpdateEvent = {
          type: 'updated',
          modelId: id,
          providerName,
          model: currentModel,
          previousModel,
          timestamp: new Date().toISOString(),
        };

        await this.updateSubscriber.publishModelUpdate(event);
      }
    }
  }

  private async initializeProviders(): Promise<void> {
    const providers = this.providerDiscovery.getRegisteredProviders();

    for (const provider of providers) {
      try {
        await provider.initialize();
        this.emit('providerInitialized', { providerName: provider.name });
      } catch (error) {
        this.emit('providerInitializationError', {
          providerName: provider.name,
          error,
        });
      }
    }
  }

  private startPeriodicRefresh(): void {
    if (this.configuration.refreshInterval > 0) {
      this.refreshTimer = setInterval(async () => {
        try {
          await this.refreshAllProviders();
        } catch (error) {
          this.emit('periodicRefreshError', { error });
        }
      }, this.configuration.refreshInterval);
    }
  }

  private startHealthChecking(): void {
    if (this.configuration.healthCheckInterval > 0) {
      this.healthCheckTimer = setInterval(async () => {
        await this.performHealthChecks();
      }, this.configuration.healthCheckInterval);
    }
  }

  private async performHealthChecks(): Promise<void> {
    const providers = this.providerDiscovery.getRegisteredProviders();

    for (const provider of providers) {
      try {
        const health = await provider.healthCheck();
        const previousStatus = provider.status;

        // Update provider status if needed
        if (health.status === 'healthy' && provider.status !== ProviderStatus.ACTIVE) {
          provider.status = ProviderStatus.ACTIVE;
        } else if (health.status !== 'healthy' && provider.status === ProviderStatus.ACTIVE) {
          provider.status = ProviderStatus.ERROR;
        }

        // Publish status change event
        if (previousStatus !== provider.status) {
          const event: ProviderUpdateEvent = {
            type: 'provider_status_changed',
            providerName: provider.name,
            status: provider.status,
            timestamp: new Date().toISOString(),
          };

          await this.updateSubscriber.publishProviderUpdate(event);
        }
      } catch (error) {
        this.emit('providerHealthCheckError', {
          providerName: provider.name,
          error,
        });
      }
    }
  }

  private setupProviderEventHandlers(): void {
    this.providerDiscovery.onProviderRegistered((provider) => {
      this.emit('providerRegistered', { providerName: provider.name });
    });

    this.providerDiscovery.onProviderUnregistered((provider) => {
      this.emit('providerUnregistered', { providerName: provider.name });
    });

    this.providerDiscovery.onProviderStatusChanged((providerName, oldStatus, newStatus) => {
      this.emit('providerStatusChanged', { providerName, oldStatus, newStatus });
    });
  }

  private setupCacheEventHandlers(): void {
    this.modelCache.onCacheHit((key, entry) => {
      this.emit('cacheHit', { key, model: entry.value });
    });

    this.modelCache.onCacheMiss((key) => {
      this.emit('cacheMiss', { key });
    });

    this.modelCache.onCacheEviction((event) => {
      this.emit('cacheEviction', event);
    });
  }

  private ensureRunning(): void {
    if (!this.isRunning) {
      throw new Error('Discovery service is not running. Call start() first.');
    }
  }

  private mergeWithDefaults(
    config: Partial<DiscoveryServiceConfiguration>
  ): DiscoveryServiceConfiguration {
    return {
      refreshInterval: 300000, // 5 minutes
      providerTimeout: 30000, // 30 seconds
      cacheEnabled: true,
      cacheTTL: 600000, // 10 minutes
      benchmarkingEnabled: false,
      notificationsEnabled: true,
      maxConcurrentProviders: 5,
      retryAttempts: 3,
      retryDelay: 1000,
      healthCheckInterval: 60000, // 1 minute
      ...config,
    };
  }

  private initializeStatus(): DiscoveryServiceStatus {
    return {
      status: 'stopped',
      totalModels: 0,
      totalProviders: 0,
      activeProviders: 0,
      errorProviders: [],
      cacheStatus: {
        status: 'healthy',
        totalEntries: 0,
        hitRate: 0,
      },
      subscriptionCount: 0,
    };
  }

  private updateStatus(): void {
    const providers = this.providerDiscovery.getRegisteredProviders();
    const cacheStats = this.modelCache.getStats();

    this.status.totalProviders = providers.length;
    this.status.activeProviders = providers.filter(
      (p) => p.status === ProviderStatus.ACTIVE
    ).length;
    this.status.errorProviders = providers
      .filter((p) => p.status === ProviderStatus.ERROR)
      .map((p) => p.name);

    this.status.cacheStatus = {
      status: cacheStats.hitRate > 0.8 ? 'healthy' : 'degraded',
      totalEntries: cacheStats.totalEntries,
      hitRate: cacheStats.hitRate,
    };
  }

  private getUptime(): number {
    if (!this.status.startTime) return 0;
    return Date.now() - new Date(this.status.startTime).getTime();
  }

  private hasCapability(capabilities: ModelCapabilities, capability: string): boolean {
    // Implementation depends on capability structure
    return capability in capabilities;
  }

  private modelsEqual(model1: Model, model2: Model): boolean {
    return JSON.stringify(model1) === JSON.stringify(model2);
  }

  private generateSubscriptionId(): string {
    return `sub_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateFilterId(): string {
    return `filter_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }
}
```

### Subtask 2.2: Implement Provider Enumeration and Model Listing

**Description**: Add methods for enumerating registered providers and listing their models with proper error handling and concurrency control.

**Implementation Details**:

1. **Create Provider Discovery Implementation**:

```typescript
// packages/providers/src/services/provider-discovery.ts
import {
  IProviderDiscovery,
  IModelProvider,
  ProviderHealth,
  ProviderConfiguration,
  ProviderEventCallback,
  ProviderStatusCallback,
  UnsubscribeFunction,
} from '../interfaces/provider-discovery.interface';

import { Model } from '../interfaces/model.types';
import { EventEmitter } from 'events';

export class ProviderDiscovery extends EventEmitter implements IProviderDiscovery {
  private providers: Map<string, IModelProvider> = new Map();
  private healthStatus: Map<string, ProviderHealth> = new Map();
  private healthCheckIntervals: Map<string, NodeJS.Timeout> = new Map();

  // Provider registration
  registerProvider(provider: IModelProvider): void {
    if (this.providers.has(provider.name)) {
      throw new Error(`Provider '${provider.name}' is already registered`);
    }

    this.providers.set(provider.name, provider);
    this.healthStatus.set(provider.name, {
      status: 'unknown',
      responseTime: 0,
      lastCheck: new Date().toISOString(),
      errorRate: 0,
      uptime: 0,
    });

    this.emit('providerRegistered', provider);
  }

  unregisterProvider(providerName: string): void {
    const provider = this.providers.get(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' is not registered`);
    }

    // Stop health checking
    this.stopHealthChecking(providerName);

    // Dispose provider
    await provider.dispose();

    this.providers.delete(providerName);
    this.healthStatus.delete(providerName);

    this.emit('providerUnregistered', provider);
  }

  getRegisteredProviders(): IModelProvider[] {
    return Array.from(this.providers.values());
  }

  getProvider(providerName: string): IModelProvider | undefined {
    return this.providers.get(providerName);
  }

  // Provider operations
  async discoverProviderModels(providerName: string): Promise<Model[]> {
    const provider = this.providers.get(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    try {
      this.emit('providerDiscoveryStarted', { providerName });

      const models = await provider.getModels();

      this.emit('providerDiscoveryCompleted', {
        providerName,
        modelCount: models.length,
      });

      return models;
    } catch (error) {
      this.emit('providerDiscoveryError', { providerName, error });
      throw error;
    }
  }

  async checkProviderHealth(providerName: string): Promise<ProviderHealth> {
    const provider = this.providers.get(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    const startTime = Date.now();

    try {
      const health = await provider.healthCheck();
      const responseTime = Date.now() - startTime;

      const updatedHealth: ProviderHealth = {
        ...health,
        responseTime,
        lastCheck: new Date().toISOString(),
      };

      this.healthStatus.set(providerName, updatedHealth);

      // Check for status changes
      const previousHealth = this.healthStatus.get(providerName);
      if (previousHealth && previousHealth.status !== health.status) {
        this.emit('providerStatusChanged', providerName, previousHealth.status, health.status);
      }

      return updatedHealth;
    } catch (error) {
      const errorHealth: ProviderHealth = {
        status: 'unhealthy',
        responseTime: Date.now() - startTime,
        lastCheck: new Date().toISOString(),
        errorRate: 1,
        uptime: 0,
        details: { error: error.message },
      };

      this.healthStatus.set(providerName, errorHealth);
      throw error;
    }
  }

  async getProviderCapabilities(providerName: string): Promise<string[]> {
    const provider = this.providers.get(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    return provider.getSupportedCapabilities();
  }

  // Provider events
  onProviderRegistered(callback: ProviderEventCallback): UnsubscribeFunction {
    const handler = (provider: IModelProvider) => callback(provider);
    this.on('providerRegistered', handler);

    return () => this.off('providerRegistered', handler);
  }

  onProviderUnregistered(callback: ProviderEventCallback): UnsubscribeFunction {
    const handler = (provider: IModelProvider) => callback(provider);
    this.on('providerUnregistered', handler);

    return () => this.off('providerUnregistered', handler);
  }

  onProviderStatusChanged(callback: ProviderStatusCallback): UnsubscribeFunction {
    const handler = (providerName: string, oldStatus: ProviderStatus, newStatus: ProviderStatus) =>
      callback(providerName, oldStatus, newStatus);
    this.on('providerStatusChanged', handler);

    return () => this.off('providerStatusChanged', handler);
  }

  // Health management
  startHealthChecking(providerName: string, interval: number = 60000): void {
    this.stopHealthChecking(providerName);

    const intervalId = setInterval(async () => {
      try {
        await this.checkProviderHealth(providerName);
      } catch (error) {
        this.emit('healthCheckError', { providerName, error });
      }
    }, interval);

    this.healthCheckIntervals.set(providerName, intervalId);
  }

  stopHealthChecking(providerName: string): void {
    const intervalId = this.healthCheckIntervals.get(providerName);
    if (intervalId) {
      clearInterval(intervalId);
      this.healthCheckIntervals.delete(providerName);
    }
  }

  startAllHealthChecking(interval: number = 60000): void {
    for (const providerName of this.providers.keys()) {
      this.startHealthChecking(providerName, interval);
    }
  }

  stopAllHealthChecking(): void {
    for (const providerName of this.healthCheckIntervals.keys()) {
      this.stopHealthChecking(providerName);
    }
  }

  // Utility methods
  getProviderHealth(providerName: string): ProviderHealth | undefined {
    return this.healthStatus.get(providerName);
  }

  getAllProviderHealth(): Map<string, ProviderHealth> {
    return new Map(this.healthStatus);
  }

  getHealthyProviders(): IModelProvider[] {
    return Array.from(this.providers.values()).filter((provider) => {
      const health = this.healthStatus.get(provider.name);
      return health && health.status === 'healthy';
    });
  }

  async checkAllProvidersHealth(): Promise<Map<string, ProviderHealth>> {
    const healthPromises = Array.from(this.providers.keys()).map(async (providerName) => {
      try {
        const health = await this.checkProviderHealth(providerName);
        return [providerName, health] as [string, ProviderHealth];
      } catch (error) {
        const errorHealth: ProviderHealth = {
          status: 'unhealthy',
          responseTime: 0,
          lastCheck: new Date().toISOString(),
          errorRate: 1,
          uptime: 0,
          details: { error: error.message },
        };
        return [providerName, errorHealth] as [string, ProviderHealth];
      }
    });

    const results = await Promise.allSettled(healthPromises);
    const healthMap = new Map<string, ProviderHealth>();

    for (const result of results) {
      if (result.status === 'fulfilled') {
        healthMap.set(result.value[0], result.value[1]);
      }
    }

    return healthMap;
  }

  // Cleanup
  async dispose(): Promise<void> {
    this.stopAllHealthChecking();

    const disposePromises = Array.from(this.providers.values()).map((provider) =>
      provider
        .dispose()
        .catch((error) => this.emit('providerDisposeError', { providerName: provider.name, error }))
    );

    await Promise.allSettled(disposePromises);

    this.providers.clear();
    this.healthStatus.clear();
    this.removeAllListeners();
  }
}
```

### Subtask 2.3: Add Periodic Refresh with Configurable Intervals

**Description**: Implement configurable periodic refresh mechanism with smart scheduling and conflict resolution.

**Implementation Details**:

1. **Create Refresh Scheduler**:

```typescript
// packages/providers/src/services/refresh-scheduler.ts
import { IModelProvider } from '../interfaces/provider-discovery.interface';
import { Model } from '../interfaces/model.types';
import { EventEmitter } from 'events';

export interface RefreshSchedule {
  providerName: string;
  interval: number;
  enabled: boolean;
  lastRefresh?: string;
  nextRefresh?: string;
  priority: number;
  retryPolicy: RetryPolicy;
}

export interface RetryPolicy {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  retryableErrors: string[];
}

export interface RefreshResult {
  providerName: string;
  success: boolean;
  modelCount: number;
  duration: number;
  error?: string;
  attempt: number;
}

export class RefreshScheduler extends EventEmitter {
  private schedules: Map<string, RefreshSchedule> = new Map();
  private timers: Map<string, NodeJS.Timeout> = new Map();
  private isRunning: boolean = false;
  private refreshPromises: Map<string, Promise<RefreshResult>> = new Map();

  constructor(private providers: Map<string, IModelProvider>) {
    super();
  }

  // Schedule management
  addSchedule(schedule: RefreshSchedule): void {
    this.schedules.set(schedule.providerName, schedule);

    if (this.isRunning && schedule.enabled) {
      this.scheduleNextRefresh(schedule.providerName);
    }

    this.emit('scheduleAdded', schedule);
  }

  removeSchedule(providerName: string): void {
    this.cancelRefresh(providerName);
    this.schedules.delete(providerName);
    this.emit('scheduleRemoved', { providerName });
  }

  updateSchedule(providerName: string, updates: Partial<RefreshSchedule>): void {
    const schedule = this.schedules.get(providerName);
    if (!schedule) {
      throw new Error(`No schedule found for provider '${providerName}'`);
    }

    const updatedSchedule = { ...schedule, ...updates };
    this.schedules.set(providerName, updatedSchedule);

    if (this.isRunning) {
      this.cancelRefresh(providerName);
      if (updatedSchedule.enabled) {
        this.scheduleNextRefresh(providerName);
      }
    }

    this.emit('scheduleUpdated', updatedSchedule);
  }

  getSchedule(providerName: string): RefreshSchedule | undefined {
    return this.schedules.get(providerName);
  }

  getAllSchedules(): RefreshSchedule[] {
    return Array.from(this.schedules.values());
  }

  // Refresh operations
  async refreshProvider(providerName: string, force: boolean = false): Promise<RefreshResult> {
    const schedule = this.schedules.get(providerName);
    const provider = this.providers.get(providerName);

    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    // Check if refresh is already in progress
    if (!force && this.refreshPromises.has(providerName)) {
      return this.refreshPromises.get(providerName)!;
    }

    const refreshPromise = this.performRefresh(
      provider,
      schedule || ({ providerName, retryPolicy: this.getDefaultRetryPolicy() } as RefreshSchedule)
    );
    this.refreshPromises.set(providerName, refreshPromise);

    try {
      const result = await refreshPromise;

      // Update schedule
      if (schedule) {
        schedule.lastRefresh = new Date().toISOString();
        schedule.nextRefresh = this.calculateNextRefresh(schedule);
      }

      this.emit('providerRefreshed', result);
      return result;
    } finally {
      this.refreshPromises.delete(providerName);
    }
  }

  async refreshAllProviders(force: boolean = false): Promise<RefreshResult[]> {
    const providerNames = Array.from(this.providers.keys());
    const refreshPromises = providerNames.map((name) =>
      this.refreshProvider(name, force).catch((error) => ({
        providerName: name,
        success: false,
        modelCount: 0,
        duration: 0,
        error: error.message,
        attempt: 1,
      }))
    );

    const results = await Promise.allSettled(refreshPromises);
    const validResults: RefreshResult[] = [];

    for (const result of results) {
      if (result.status === 'fulfilled') {
        validResults.push(result.value);
      }
    }

    this.emit('allProvidersRefreshed', {
      totalProviders: providerNames.length,
      successCount: validResults.filter((r) => r.success).length,
      failureCount: validResults.filter((r) => !r.success).length,
      results: validResults,
    });

    return validResults;
  }

  // Scheduler lifecycle
  start(): void {
    if (this.isRunning) {
      return;
    }

    this.isRunning = true;

    // Schedule all enabled providers
    for (const schedule of this.schedules.values()) {
      if (schedule.enabled) {
        this.scheduleNextRefresh(schedule.providerName);
      }
    }

    this.emit('schedulerStarted');
  }

  stop(): void {
    if (!this.isRunning) {
      return;
    }

    this.isRunning = false;

    // Cancel all timers
    for (const providerName of this.timers.keys()) {
      this.cancelRefresh(providerName);
    }

    this.emit('schedulerStopped');
  }

  // Private methods
  private async performRefresh(
    provider: IModelProvider,
    schedule: RefreshSchedule
  ): Promise<RefreshResult> {
    const startTime = Date.now();
    let attempt = 0;
    let lastError: Error | undefined;

    while (attempt < schedule.retryPolicy.maxAttempts) {
      attempt++;

      try {
        this.emit('refreshStarted', {
          providerName: provider.name,
          attempt,
        });

        // Check provider health first
        const health = await provider.healthCheck();
        if (health.status !== 'healthy') {
          throw new Error(`Provider health check failed: ${health.status}`);
        }

        // Get models
        const models = await provider.getModels();
        const duration = Date.now() - startTime;

        const result: RefreshResult = {
          providerName: provider.name,
          success: true,
          modelCount: models.length,
          duration,
          attempt,
        };

        this.emit('refreshCompleted', result);
        return result;
      } catch (error) {
        lastError = error as Error;

        this.emit('refreshError', {
          providerName: provider.name,
          error: lastError,
          attempt,
        });

        // Check if error is retryable
        if (!this.isRetryableError(lastError, schedule.retryPolicy)) {
          break;
        }

        // Wait before retry
        if (attempt < schedule.retryPolicy.maxAttempts) {
          const delay = this.calculateRetryDelay(attempt, schedule.retryPolicy);
          await this.sleep(delay);
        }
      }
    }

    // All attempts failed
    const result: RefreshResult = {
      providerName: provider.name,
      success: false,
      modelCount: 0,
      duration: Date.now() - startTime,
      error: lastError?.message || 'Unknown error',
      attempt,
    };

    this.emit('refreshFailed', result);
    return result;
  }

  private scheduleNextRefresh(providerName: string): void {
    const schedule = this.schedules.get(providerName);
    if (!schedule || !schedule.enabled) {
      return;
    }

    this.cancelRefresh(providerName);

    const nextRefreshTime = this.calculateNextRefresh(schedule);
    const delay = new Date(nextRefreshTime).getTime() - Date.now();

    if (delay > 0) {
      const timer = setTimeout(async () => {
        try {
          await this.refreshProvider(providerName);
        } catch (error) {
          this.emit('scheduledRefreshError', { providerName, error });
        } finally {
          // Schedule next refresh
          this.scheduleNextRefresh(providerName);
        }
      }, delay);

      this.timers.set(providerName, timer);

      this.emit('refreshScheduled', {
        providerName,
        nextRefresh: nextRefreshTime,
      });
    }
  }

  private cancelRefresh(providerName: string): void {
    const timer = this.timers.get(providerName);
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(providerName);
      this.emit('refreshCancelled', { providerName });
    }
  }

  private calculateNextRefresh(schedule: RefreshSchedule): string {
    const now = new Date();
    const nextRefresh = new Date(now.getTime() + schedule.interval);

    // Add some jitter to prevent thundering herd
    const jitter = Math.random() * 0.1 * schedule.interval; // 10% jitter
    nextRefresh.setTime(nextRefresh.getTime() + jitter);

    return nextRefresh.toISOString();
  }

  private calculateRetryDelay(attempt: number, retryPolicy: RetryPolicy): number {
    const delay = retryPolicy.baseDelay * Math.pow(retryPolicy.backoffMultiplier, attempt - 1);
    return Math.min(delay, retryPolicy.maxDelay);
  }

  private isRetryableError(error: Error, retryPolicy: RetryPolicy): boolean {
    return retryPolicy.retryableErrors.some(
      (retryableError) =>
        error.message.includes(retryableError) || error.name.includes(retryableError)
    );
  }

  private getDefaultRetryPolicy(): RetryPolicy {
    return {
      maxAttempts: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      retryableErrors: ['timeout', 'connection', 'rate_limit', 'network', 'temporary'],
    };
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // Statistics and monitoring
  getRefreshStats(): RefreshStats {
    const schedules = Array.from(this.schedules.values());
    const now = new Date();

    return {
      totalSchedules: schedules.length,
      enabledSchedules: schedules.filter((s) => s.enabled).length,
      disabledSchedules: schedules.filter((s) => !s.enabled).length,
      providersCurrentlyRefreshing: this.refreshPromises.size,
      nextScheduledRefresh: schedules
        .filter((s) => s.enabled && s.nextRefresh)
        .map((s) => new Date(s.nextRefresh!))
        .sort((a, b) => a.getTime() - b.getTime())[0]
        ?.toISOString(),
      averageRefreshInterval:
        schedules.reduce((sum, s) => sum + s.interval, 0) / schedules.length || 0,
      lastRefreshTimes: schedules
        .filter((s) => s.lastRefresh)
        .map((s) => ({ providerName: s.providerName, lastRefresh: s.lastRefresh! })),
    };
  }

  // Cleanup
  dispose(): void {
    this.stop();
    this.schedules.clear();
    this.removeAllListeners();
  }
}

export interface RefreshStats {
  totalSchedules: number;
  enabledSchedules: number;
  disabledSchedules: number;
  providersCurrentlyRefreshing: number;
  nextScheduledRefresh?: string;
  averageRefreshInterval: number;
  lastRefreshTimes: Array<{ providerName: string; lastRefresh: string }>;
}
```

### Subtask 2.4: Implement Provider Health Checking Integration

**Description**: Integrate comprehensive health checking with automatic recovery and status management.

**Implementation Details**:

1. **Create Health Monitor**:

```typescript
// packages/providers/src/services/health-monitor.ts
import {
  IModelProvider,
  ProviderHealth,
  ProviderStatus,
} from '../interfaces/provider-discovery.interface';
import { EventEmitter } from 'events';

export interface HealthCheckConfig {
  interval: number;
  timeout: number;
  failureThreshold: number;
  recoveryThreshold: number;
  unhealthyThreshold: number;
  checkEndpoints: string[];
  customChecks?: CustomHealthCheck[];
}

export interface CustomHealthCheck {
  name: string;
  check: (provider: IModelProvider) => Promise<HealthCheckResult>;
  weight: number;
  critical: boolean;
}

export interface HealthCheckResult {
  status: 'pass' | 'fail' | 'warn';
  message: string;
  duration: number;
  timestamp: string;
  details?: Record<string, any>;
}

export interface ProviderHealthStatus {
  providerName: string;
  status: ProviderStatus;
  health: ProviderHealth;
  consecutiveFailures: number;
  consecutiveSuccesses: number;
  lastStatusChange: string;
  statusHistory: HealthStatusEntry[];
  alerts: HealthAlert[];
}

export interface HealthStatusEntry {
  status: ProviderStatus;
  timestamp: string;
  reason?: string;
  duration?: number;
}

export interface HealthAlert {
  type: 'status_change' | 'degradation' | 'recovery' | 'critical_failure';
  severity: 'low' | 'medium' | 'high' | 'critical';
  message: string;
  timestamp: string;
  resolved?: boolean;
}

export class HealthMonitor extends EventEmitter {
  private providers: Map<string, IModelProvider> = new Map();
  private healthStatus: Map<string, ProviderHealthStatus> = new Map();
  private checkIntervals: Map<string, NodeJS.Timeout> = new Map();
  private config: HealthCheckConfig;

  constructor(config: Partial<HealthCheckConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);
  }

  // Provider management
  addProvider(provider: IModelProvider): void {
    this.providers.set(provider.name, provider);

    const healthStatus: ProviderHealthStatus = {
      providerName: provider.name,
      status: ProviderStatus.INACTIVE,
      health: {
        status: 'unknown',
        responseTime: 0,
        lastCheck: new Date().toISOString(),
        errorRate: 0,
        uptime: 0,
      },
      consecutiveFailures: 0,
      consecutiveSuccesses: 0,
      lastStatusChange: new Date().toISOString(),
      statusHistory: [],
      alerts: [],
    };

    this.healthStatus.set(provider.name, healthStatus);

    if (this.isRunning()) {
      this.startHealthChecking(provider.name);
    }

    this.emit('providerAdded', { providerName: provider.name });
  }

  removeProvider(providerName: string): void {
    this.stopHealthChecking(providerName);
    this.providers.delete(providerName);
    this.healthStatus.delete(providerName);

    this.emit('providerRemoved', { providerName });
  }

  // Health checking operations
  async checkProviderHealth(providerName: string): Promise<ProviderHealth> {
    const provider = this.providers.get(providerName);
    if (!provider) {
      throw new Error(`Provider '${providerName}' not found`);
    }

    const startTime = Date.now();
    const results: HealthCheckResult[] = [];

    try {
      // Basic health check
      const basicHealth = await provider.healthCheck();
      const basicResult: HealthCheckResult = {
        status: basicHealth.status === 'healthy' ? 'pass' : 'fail',
        message: `Provider status: ${basicHealth.status}`,
        duration: Date.now() - startTime,
        timestamp: new Date().toISOString(),
        details: basicHealth.details,
      };
      results.push(basicResult);

      // Custom health checks
      if (this.config.customChecks) {
        for (const customCheck of this.config.customChecks) {
          try {
            const customResult = await customCheck.check(provider);
            results.push(customResult);
          } catch (error) {
            results.push({
              status: 'fail',
              message: `Custom check '${customCheck.name}' failed: ${error.message}`,
              duration: 0,
              timestamp: new Date().toISOString(),
            });
          }
        }
      }

      // Aggregate results
      const aggregatedHealth = this.aggregateHealthResults(providerName, results);

      // Update health status
      this.updateHealthStatus(providerName, aggregatedHealth, results);

      return aggregatedHealth;
    } catch (error) {
      const errorResult: HealthCheckResult = {
        status: 'fail',
        message: `Health check failed: ${error.message}`,
        duration: Date.now() - startTime,
        timestamp: new Date().toISOString(),
      };

      const errorHealth: ProviderHealth = {
        status: 'unhealthy',
        responseTime: Date.now() - startTime,
        lastCheck: new Date().toISOString(),
        errorRate: 1,
        uptime: 0,
        details: { error: error.message },
      };

      this.updateHealthStatus(providerName, errorHealth, [errorResult]);
      throw error;
    }
  }

  async checkAllProvidersHealth(): Promise<Map<string, ProviderHealth>> {
    const healthPromises = Array.from(this.providers.keys()).map(async (providerName) => {
      try {
        const health = await this.checkProviderHealth(providerName);
        return [providerName, health] as [string, ProviderHealth];
      } catch (error) {
        const errorHealth: ProviderHealth = {
          status: 'unhealthy',
          responseTime: 0,
          lastCheck: new Date().toISOString(),
          errorRate: 1,
          uptime: 0,
          details: { error: error.message },
        };
        return [providerName, errorHealth] as [string, ProviderHealth];
      }
    });

    const results = await Promise.allSettled(healthPromises);
    const healthMap = new Map<string, ProviderHealth>();

    for (const result of results) {
      if (result.status === 'fulfilled') {
        healthMap.set(result.value[0], result.value[1]);
      }
    }

    return healthMap;
  }

  // Monitoring lifecycle
  start(): void {
    if (this.isRunning()) {
      return;
    }

    for (const providerName of this.providers.keys()) {
      this.startHealthChecking(providerName);
    }

    this.emit('monitorStarted');
  }

  stop(): void {
    for (const providerName of this.checkIntervals.keys()) {
      this.stopHealthChecking(providerName);
    }

    this.emit('monitorStopped');
  }

  // Status and statistics
  getProviderHealthStatus(providerName: string): ProviderHealthStatus | undefined {
    return this.healthStatus.get(providerName);
  }

  getAllProviderHealthStatus(): Map<string, ProviderHealthStatus> {
    return new Map(this.healthStatus);
  }

  getHealthyProviders(): string[] {
    const healthyProviders: string[] = [];

    for (const [providerName, status] of this.healthStatus) {
      if (status.status === ProviderStatus.ACTIVE) {
        healthyProviders.push(providerName);
      }
    }

    return healthyProviders;
  }

  getUnhealthyProviders(): string[] {
    const unhealthyProviders: string[] = [];

    for (const [providerName, status] of this.healthStatus) {
      if (status.status === ProviderStatus.ERROR || status.status === ProviderStatus.INACTIVE) {
        unhealthyProviders.push(providerName);
      }
    }

    return unhealthyProviders;
  }

  getHealthSummary(): HealthSummary {
    const statuses = Array.from(this.healthStatus.values());

    return {
      totalProviders: statuses.length,
      healthyProviders: statuses.filter((s) => s.status === ProviderStatus.ACTIVE).length,
      unhealthyProviders: statuses.filter((s) => s.status === ProviderStatus.ERROR).length,
      inactiveProviders: statuses.filter((s) => s.status === ProviderStatus.INACTIVE).length,
      providersWithWarnings: statuses.filter((s) =>
        s.alerts.some((a) => !a.resolved && a.severity !== 'low')
      ).length,
      averageResponseTime:
        statuses.reduce((sum, s) => sum + s.health.responseTime, 0) / statuses.length || 0,
      averageUptime: statuses.reduce((sum, s) => sum + s.health.uptime, 0) / statuses.length || 0,
      lastCheck: statuses.reduce((latest, s) => {
        const checkTime = new Date(s.health.lastCheck).getTime();
        const latestTime = new Date(latest).getTime();
        return checkTime > latestTime ? s.health.lastCheck : latest;
      }, new Date(0).toISOString()),
      activeAlerts: statuses.reduce(
        (count, s) => count + s.alerts.filter((a) => !a.resolved).length,
        0
      ),
    };
  }

  // Private methods
  private startHealthChecking(providerName: string): void {
    this.stopHealthChecking(providerName);

    const interval = setInterval(async () => {
      try {
        await this.checkProviderHealth(providerName);
      } catch (error) {
        this.emit('healthCheckError', { providerName, error });
      }
    }, this.config.interval);

    this.checkIntervals.set(providerName, interval);
  }

  private stopHealthChecking(providerName: string): void {
    const interval = this.checkIntervals.get(providerName);
    if (interval) {
      clearInterval(interval);
      this.checkIntervals.delete(providerName);
    }
  }

  private aggregateHealthResults(
    providerName: string,
    results: HealthCheckResult[]
  ): ProviderHealth {
    const totalDuration = results.reduce((sum, r) => sum + r.duration, 0);
    const averageDuration = totalDuration / results.length;

    const failedChecks = results.filter((r) => r.status === 'fail');
    const errorRate = failedChecks.length / results.length;

    let overallStatus: 'healthy' | 'unhealthy' | 'degraded';
    if (errorRate === 0) {
      overallStatus = 'healthy';
    } else if (errorRate >= 0.5) {
      overallStatus = 'unhealthy';
    } else {
      overallStatus = 'degraded';
    }

    // Check for critical failures
    const criticalFailures = results.filter(
      (r) =>
        r.status === 'fail' &&
        this.config.customChecks?.some((check) => check.critical && r.message.includes(check.name))
    );

    if (criticalFailures.length > 0) {
      overallStatus = 'unhealthy';
    }

    return {
      status: overallStatus,
      responseTime: averageDuration,
      lastCheck: new Date().toISOString(),
      errorRate,
      uptime: this.calculateUptime(providerName),
      details: {
        checkResults: results,
        failureCount: failedChecks.length,
        criticalFailures: criticalFailures.length,
      },
    };
  }

  private updateHealthStatus(
    providerName: string,
    health: ProviderHealth,
    results: HealthCheckResult[]
  ): void {
    const currentStatus = this.healthStatus.get(providerName);
    if (!currentStatus) {
      return;
    }

    const previousStatus = currentStatus.status;
    const newStatus = this.determineProviderStatus(health, currentStatus);

    // Update counters
    if (health.status === 'healthy') {
      currentStatus.consecutiveSuccesses++;
      currentStatus.consecutiveFailures = 0;
    } else {
      currentStatus.consecutiveFailures++;
      currentStatus.consecutiveSuccesses = 0;
    }

    // Update health and status
    currentStatus.health = health;
    currentStatus.status = newStatus;

    // Add to history
    currentStatus.statusHistory.push({
      status: newStatus,
      timestamp: new Date().toISOString(),
      reason: health.status,
      duration: health.responseTime,
    });

    // Trim history (keep last 100 entries)
    if (currentStatus.statusHistory.length > 100) {
      currentStatus.statusHistory = currentStatus.statusHistory.slice(-100);
    }

    // Check for status changes and create alerts
    if (previousStatus !== newStatus) {
      currentStatus.lastStatusChange = new Date().toISOString();

      const alert: HealthAlert = {
        type: newStatus === ProviderStatus.ACTIVE ? 'recovery' : 'status_change',
        severity: this.getAlertSeverity(newStatus, health),
        message: `Provider ${providerName} status changed from ${previousStatus} to ${newStatus}`,
        timestamp: new Date().toISOString(),
      };

      currentStatus.alerts.push(alert);

      this.emit('providerStatusChanged', {
        providerName,
        previousStatus,
        newStatus,
        health,
        alert,
      });
    }

    // Check for degradation
    if (health.status === 'degraded' && previousStatus === ProviderStatus.ACTIVE) {
      const alert: HealthAlert = {
        type: 'degradation',
        severity: 'medium',
        message: `Provider ${providerName} performance degraded`,
        timestamp: new Date().toISOString(),
      };

      currentStatus.alerts.push(alert);
      this.emit('providerDegraded', { providerName, health, alert });
    }

    // Trim alerts (keep last 50)
    if (currentStatus.alerts.length > 50) {
      currentStatus.alerts = currentStatus.alerts.slice(-50);
    }
  }

  private determineProviderStatus(
    health: ProviderHealth,
    currentStatus: ProviderHealthStatus
  ): ProviderStatus {
    const { failureThreshold, recoveryThreshold, unhealthyThreshold } = this.config;

    if (health.status === 'healthy') {
      if (currentStatus.consecutiveFailures >= failureThreshold) {
        return ProviderStatus.ERROR;
      } else if (currentStatus.consecutiveSuccesses >= recoveryThreshold) {
        return ProviderStatus.ACTIVE;
      } else {
        return currentStatus.status; // Maintain current status during recovery
      }
    } else if (health.status === 'degraded') {
      return ProviderStatus.ERROR;
    } else {
      return ProviderStatus.ERROR;
    }
  }

  private getAlertSeverity(
    status: ProviderStatus,
    health: ProviderHealth
  ): 'low' | 'medium' | 'high' | 'critical' {
    if (status === ProviderStatus.ACTIVE) {
      return 'low';
    } else if (health.status === 'degraded') {
      return 'medium';
    } else if (health.errorRate >= 0.8) {
      return 'critical';
    } else {
      return 'high';
    }
  }

  private calculateUptime(providerName: string): number {
    const status = this.healthStatus.get(providerName);
    if (!status) {
      return 0;
    }

    // Simple uptime calculation based on recent history
    const recentHistory = status.statusHistory.slice(-10); // Last 10 checks
    if (recentHistory.length === 0) {
      return 0;
    }

    const healthyChecks = recentHistory.filter(
      (entry) => entry.status === ProviderStatus.ACTIVE
    ).length;

    return (healthyChecks / recentHistory.length) * 100;
  }

  private isRunning(): boolean {
    return this.checkIntervals.size > 0;
  }

  private mergeWithDefaults(config: Partial<HealthCheckConfig>): HealthCheckConfig {
    return {
      interval: 60000, // 1 minute
      timeout: 10000, // 10 seconds
      failureThreshold: 3, // 3 consecutive failures
      recoveryThreshold: 2, // 2 consecutive successes
      unhealthyThreshold: 0.5, // 50% error rate
      checkEndpoints: ['/health', '/status'],
      customChecks: [],
      ...config,
    };
  }

  // Cleanup
  dispose(): void {
    this.stop();
    this.providers.clear();
    this.healthStatus.clear();
    this.removeAllListeners();
  }
}

export interface HealthSummary {
  totalProviders: number;
  healthyProviders: number;
  unhealthyProviders: number;
  inactiveProviders: number;
  providersWithWarnings: number;
  averageResponseTime: number;
  averageUptime: number;
  lastCheck: string;
  activeAlerts: number;
}
```

## Files to Create

1. **Core Service Implementation**:
   - `packages/providers/src/services/dynamic-model-discovery.ts`
   - `packages/providers/src/services/provider-discovery.ts`
   - `packages/providers/src/services/refresh-scheduler.ts`
   - `packages/providers/src/services/health-monitor.ts`

2. **Supporting Types**:
   - `packages/providers/src/interfaces/model.types.ts` (if not already created)

3. **Updated Files**:
   - `packages/providers/src/index.ts` (export new services)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/services/dynamic-model-discovery.test.ts
describe('DynamicModelDiscovery', () => {
  let discovery: DynamicModelDiscovery;
  let mockProviderDiscovery: jest.Mocked<IProviderDiscovery>;
  let mockModelCache: jest.Mocked<IModelCache>;
  let mockCapabilityMapper: jest.Mocked<IModelCapabilityMapper>;
  let mockUpdateSubscriber: jest.Mocked<IModelUpdateSubscriber>;

  beforeEach(() => {
    mockProviderDiscovery = createMockProviderDiscovery();
    mockModelCache = createMockModelCache();
    mockCapabilityMapper = createMockCapabilityMapper();
    mockUpdateSubscriber = createMockUpdateSubscriber();

    discovery = new DynamicModelDiscovery(
      mockProviderDiscovery,
      mockModelCache,
      mockCapabilityMapper,
      mockUpdateSubscriber
    );
  });

  describe('discoverModels', () => {
    it('should discover models from all providers', async () => {
      const mockProviders = [createMockProvider('openai'), createMockProvider('anthropic')];
      mockProviderDiscovery.getRegisteredProviders.mockReturnValue(mockProviders);
      mockProviderDiscovery.discoverProviderModels.mockResolvedValue([
        createMockModel('gpt-4'),
        createMockModel('claude-3'),
      ]);

      const models = await discovery.discoverModels();

      expect(models).toHaveLength(2);
      expect(mockProviderDiscovery.discoverProviderModels).toHaveBeenCalledTimes(2);
    });
  });

  describe('refreshProvider', () => {
    it('should refresh specific provider models', async () => {
      const mockProvider = createMockProvider('openai');
      mockProviderDiscovery.getProvider.mockReturnValue(mockProvider);
      mockProviderDiscovery.discoverProviderModels.mockResolvedValue([createMockModel('gpt-4')]);

      const models = await discovery.refreshProvider('openai');

      expect(models).toHaveLength(1);
      expect(mockProviderDiscovery.discoverProviderModels).toHaveBeenCalledWith('openai');
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/discovery-service-integration.test.ts
describe('Discovery Service Integration', () => {
  let discovery: DynamicModelDiscovery;
  let realProviders: IModelProvider[];

  beforeEach(async () => {
    realProviders = [
      new OpenAIProvider({ apiKey: process.env.OPENAI_API_KEY! }),
      new AnthropicProvider({ apiKey: process.env.ANTHROPIC_API_KEY! }),
    ];

    const providerDiscovery = new ProviderDiscovery();
    const modelCache = new ModelCache();
    const capabilityMapper = new ModelCapabilityMapper();
    const updateSubscriber = new ModelUpdateSubscriber();

    for (const provider of realProviders) {
      providerDiscovery.registerProvider(provider);
    }

    discovery = new DynamicModelDiscovery(
      providerDiscovery,
      modelCache,
      capabilityMapper,
      updateSubscriber
    );

    await discovery.start();
  });

  afterEach(async () => {
    await discovery.stop();
  });

  it('should discover real models from providers', async () => {
    const models = await discovery.discoverModels();

    expect(models.length).toBeGreaterThan(0);
    expect(models.some((m) => m.providerName === 'openai')).toBe(true);
    expect(models.some((m) => m.providerName === 'anthropic')).toBe(true);
  }, 30000);
});
```

## Security Considerations

1. **Provider Authentication**:
   - Secure storage of provider credentials
   - Credential rotation and validation
   - Access control for provider operations

2. **Health Check Security**:
   - Rate limiting health check requests
   - Validation of health check responses
   - Protection against health check manipulation

3. **Data Privacy**:
   - Sanitization of model metadata
   - Secure handling of provider responses
   - Audit logging for all discovery operations

## Dependencies

### New Dependencies

```json
{
  "eventemitter3": "^5.0.1",
  "uuid": "^9.0.1"
}
```

### Dev Dependencies

```json
{
  "@types/uuid": "^9.0.7",
  "@types/eventemitter3": "^2.0.2"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Discovery operation frequencies and durations
   - Provider health status changes
   - Cache hit/miss rates
   - Model update frequencies

2. **Logging**:
   - All discovery operations with timing
   - Provider health check results
   - Cache operations and performance
   - Error details and recovery actions

3. **Alerts**:
   - Provider health status changes
   - Discovery operation failures
   - Cache performance degradation
   - High error rates

## Acceptance Criteria

1. ✅ **Provider Enumeration**: Complete provider registration and enumeration
2. ✅ **Model Discovery**: Automatic model discovery from all providers
3. ✅ **Periodic Refresh**: Configurable periodic refresh with smart scheduling
4. ✅ **Health Checking**: Comprehensive health checking with status management
5. ✅ **Error Handling**: Robust error handling and recovery mechanisms
6. ✅ **Event System**: Complete event system for status changes
7. ✅ **Performance**: Efficient concurrent operations
8. ✅ **Configuration**: Flexible configuration management
9. ✅ **Testing**: Comprehensive unit and integration test coverage
10. ✅ **Monitoring**: Complete observability and alerting

## Success Metrics

- Discovery operation success rate > 95%
- Provider health check accuracy > 99%
- Average discovery time < 30 seconds
- Cache hit rate > 80%
- Zero data loss during refresh operations
- Complete audit trail for all operations
