# Task 1: Define Model Discovery Interfaces

## Overview

Create comprehensive interfaces for the dynamic model discovery service, establishing contracts for model discovery, caching, capability mapping, and update subscriptions.

## Objectives

- Define core interfaces for model discovery service
- Create model cache interface with TTL support
- Specify capability mapping interface for standardization
- Create update subscription interface for real-time notifications

## Implementation Steps

### Subtask 1.1: Create IModelDiscoveryService Interface

**Description**: Define the main service interface that orchestrates model discovery across all providers.

**Implementation Details**:

1. **Create Core Model Discovery Interface**:

```typescript
// packages/providers/src/interfaces/model-discovery.interface.ts
import { Model, ModelCapabilities, ModelFilter, ModelBenchmark } from './model.types';

export interface IModelDiscoveryService {
  // Core discovery operations
  discoverModels(): Promise<Model[]>;
  refreshProvider(providerName: string): Promise<Model[]>;
  refreshAllProviders(): Promise<Model[]>;

  // Model retrieval operations
  getModel(modelId: string): Promise<Model | undefined>;
  getModelsByProvider(providerName: string): Promise<Model[]>;
  getModelsByCapability(capability: string): Promise<Model[]>;
  searchModels(filter: ModelFilter): Promise<Model[]>;

  // Capability operations
  getModelCapabilities(modelId: string): Promise<ModelCapabilities | undefined>;
  validateModelCapabilities(modelId: string, requiredCapabilities: string[]): Promise<boolean>;

  // Benchmarking operations
  benchmarkModel(modelId: string, benchmarkType?: string): Promise<ModelBenchmark>;
  getModelBenchmarks(modelId: string): Promise<ModelBenchmark[]>;

  // Subscription operations
  subscribeToModelUpdates(callback: ModelUpdateCallback): UnsubscribeFunction;
  subscribeToProviderUpdates(
    providerName: string,
    callback: ProviderUpdateCallback
  ): UnsubscribeFunction;
  subscribeToCapabilityUpdates(
    capability: string,
    callback: CapabilityUpdateCallback
  ): UnsubscribeFunction;

  // Service lifecycle
  start(): Promise<void>;
  stop(): Promise<void>;
  getStatus(): DiscoveryServiceStatus;
  getConfiguration(): DiscoveryServiceConfiguration;
}

export interface ModelUpdateCallback {
  (event: ModelUpdateEvent): void | Promise<void>;
}

export interface ProviderUpdateCallback {
  (event: ProviderUpdateEvent): void | Promise<void>;
}

export interface CapabilityUpdateCallback {
  (event: CapabilityUpdateEvent): void | Promise<void>;
}

export interface UnsubscribeFunction {
  (): void | Promise<void>;
}

export interface ModelUpdateEvent {
  type: 'added' | 'updated' | 'removed' | 'capabilities_changed';
  modelId: string;
  providerName: string;
  model?: Model;
  previousModel?: Model;
  timestamp: string;
  changes?: ModelChange[];
}

export interface ProviderUpdateEvent {
  type: 'provider_added' | 'provider_removed' | 'provider_status_changed' | 'models_refreshed';
  providerName: string;
  status: ProviderStatus;
  modelCount?: number;
  timestamp: string;
  error?: string;
}

export interface CapabilityUpdateEvent {
  type: 'capability_added' | 'capability_removed' | 'capability_updated';
  capability: string;
  affectedModels: string[];
  timestamp: string;
}

export interface ModelChange {
  field: string;
  oldValue: any;
  newValue: any;
  type: 'property_change' | 'capability_change' | 'metadata_change';
}

export enum ProviderStatus {
  ACTIVE = 'active',
  INACTIVE = 'inactive',
  ERROR = 'error',
  MAINTENANCE = 'maintenance',
  RATE_LIMITED = 'rate_limited',
}

export interface DiscoveryServiceStatus {
  status: 'running' | 'stopped' | 'starting' | 'stopping' | 'error';
  startTime?: string;
  lastRefresh?: string;
  totalModels: number;
  totalProviders: number;
  activeProviders: number;
  errorProviders: string[];
  cacheStatus: CacheStatus;
  subscriptionCount: number;
  uptime?: number;
}

export interface CacheStatus {
  status: 'healthy' | 'degraded' | 'error';
  totalEntries: number;
  hitRate: number;
  lastCleanup?: string;
  memoryUsage?: number;
}

export interface DiscoveryServiceConfiguration {
  refreshInterval: number;
  providerTimeout: number;
  cacheEnabled: boolean;
  cacheTTL: number;
  benchmarkingEnabled: boolean;
  notificationsEnabled: boolean;
  maxConcurrentProviders: number;
  retryAttempts: number;
  retryDelay: number;
  healthCheckInterval: number;
}
```

2. **Create Provider Discovery Interface**:

```typescript
// packages/providers/src/interfaces/provider-discovery.interface.ts
import { Model } from './model.types';

export interface IProviderDiscovery {
  // Provider registration
  registerProvider(provider: IModelProvider): void;
  unregisterProvider(providerName: string): void;
  getRegisteredProviders(): IModelProvider[];
  getProvider(providerName: string): IModelProvider | undefined;

  // Provider operations
  discoverProviderModels(providerName: string): Promise<Model[]>;
  checkProviderHealth(providerName: string): Promise<ProviderHealth>;
  getProviderCapabilities(providerName: string): Promise<string[]>;

  // Provider events
  onProviderRegistered(callback: ProviderEventCallback): UnsubscribeFunction;
  onProviderUnregistered(callback: ProviderEventCallback): UnsubscribeFunction;
  onProviderStatusChanged(callback: ProviderStatusCallback): UnsubscribeFunction;
}

export interface IModelProvider {
  name: string;
  type: string;
  version: string;
  status: ProviderStatus;

  // Model discovery
  getModels(): Promise<Model[]>;
  getModel(modelId: string): Promise<Model | undefined>;

  // Health checking
  healthCheck(): Promise<ProviderHealth>;

  // Capabilities
  getSupportedCapabilities(): Promise<string[]>;

  // Configuration
  getConfiguration(): ProviderConfiguration;
  updateConfiguration(config: Partial<ProviderConfiguration>): Promise<void>;

  // Lifecycle
  initialize(): Promise<void>;
  dispose(): Promise<void>;
}

export interface ProviderHealth {
  status: 'healthy' | 'unhealthy' | 'degraded';
  responseTime: number;
  lastCheck: string;
  errorRate: number;
  uptime: number;
  details?: Record<string, any>;
}

export interface ProviderConfiguration {
  enabled: boolean;
  timeout: number;
  retryAttempts: number;
  rateLimit: {
    requestsPerSecond: number;
    burstLimit: number;
  };
  authentication: {
    type: 'api_key' | 'oauth' | 'certificate';
    credentials?: Record<string, string>;
  };
  endpoints: {
    models: string;
    health: string;
    capabilities?: string;
  };
  customSettings?: Record<string, any>;
}

export type ProviderEventCallback = (provider: IModelProvider) => void | Promise<void>;
export type ProviderStatusCallback = (
  providerName: string,
  oldStatus: ProviderStatus,
  newStatus: ProviderStatus
) => void | Promise<void>;
```

### Subtask 1.2: Define ModelCache Interface with TTL Support

**Description**: Create a comprehensive caching interface for model metadata with TTL support, invalidation strategies, and performance optimization.

**Implementation Details**:

1. **Create Model Cache Interface**:

```typescript
// packages/providers/src/interfaces/model-cache.interface.ts
import { Model, ModelFilter } from './model.types';

export interface IModelCache {
  // Basic cache operations
  get(modelId: string): Promise<Model | undefined>;
  set(modelId: string, model: Model, options?: CacheSetOptions): Promise<void>;
  delete(modelId: string): Promise<boolean>;
  has(modelId: string): Promise<boolean>;

  // Batch operations
  getMultiple(modelIds: string[]): Promise<Map<string, Model | undefined>>;
  setMultiple(models: Map<string, Model>, options?: CacheSetOptions): Promise<void>;
  deleteMultiple(modelIds: string[]): Promise<number>;

  // Provider operations
  getProviderModels(providerName: string): Promise<Model[]>;
  setProviderModels(
    providerName: string,
    models: Model[],
    options?: CacheSetOptions
  ): Promise<void>;
  invalidateProvider(providerName: string): Promise<void>;

  // Search and filtering
  getAll(): Promise<Model[]>;
  search(filter: ModelFilter): Promise<Model[]>;
  getByCapability(capability: string): Promise<Model[]>;
  count(filter?: ModelFilter): Promise<number>;

  // TTL and expiration
  getTTL(modelId: string): Promise<number | undefined>;
  setTTL(modelId: string, ttl: number): Promise<void>;
  refresh(modelId: string): Promise<boolean>;
  refreshProvider(providerName: string): Promise<number>;

  // Cache management
  clear(): Promise<void>;
  cleanup(): Promise<number>;
  getStats(): Promise<CacheStats>;

  // Events
  onCacheHit(callback: CacheEventCallback): UnsubscribeFunction;
  onCacheMiss(callback: CacheEventCallback): UnsubscribeFunction;
  onCacheEviction(callback: CacheEvictionCallback): UnsubscribeFunction;
}

export interface CacheSetOptions {
  ttl?: number; // Time to live in milliseconds
  tags?: string[]; // Cache tags for invalidation
  priority?: number; // Cache priority (higher = less likely to evict)
  metadata?: Record<string, any>; // Additional metadata
}

export interface CacheStats {
  totalEntries: number;
  memoryUsage: number;
  hitRate: number;
  missRate: number;
  evictionCount: number;
  lastCleanup: string;
  oldestEntry?: string;
  newestEntry?: string;
  entriesByProvider: Record<string, number>;
  entriesByTag: Record<string, number>;
}

export interface CacheEntry<T> {
  key: string;
  value: T;
  createdAt: string;
  lastAccessed: string;
  ttl?: number;
  expiresAt?: string;
  accessCount: number;
  tags: string[];
  priority: number;
  metadata: Record<string, any>;
}

export interface CacheEvictionEvent {
  key: string;
  reason: 'expired' | 'lru' | 'memory' | 'manual' | 'tag_invalidation';
  entry: CacheEntry<any>;
  timestamp: string;
}

export type CacheEventCallback = (key: string, entry: CacheEntry<any>) => void | Promise<void>;
export type CacheEvictionCallback = (event: CacheEvictionEvent) => void | Promise<void>;

// Cache strategy interfaces
export interface ICacheStrategy {
  shouldEvict(entry: CacheEntry<any>, cache: IModelCache): boolean;
  selectEvictionCandidate(entries: CacheEntry<any>[]): CacheEntry<any> | null;
}

export interface ITTLStrategy {
  calculateTTL(model: Model, context: TTLContext): number;
  shouldRefresh(entry: CacheEntry<Model>): boolean;
}

export interface TTLContext {
  providerName: string;
  operation: 'discovery' | 'refresh' | 'manual';
  cacheSize: number;
  memoryPressure: number;
}

// Built-in cache strategies
export class LRUCacheStrategy implements ICacheStrategy {
  shouldEvict(entry: CacheEntry<any>, cache: IModelCache): boolean {
    // LRU logic - evict least recently used when memory pressure is high
    return false; // Implementation would check memory usage
  }

  selectEvictionCandidate(entries: CacheEntry<any>[]): CacheEntry<any> | null {
    return (
      entries.sort(
        (a, b) => new Date(a.lastAccessed).getTime() - new Date(b.lastAccessed).getTime()
      )[0] || null
    );
  }
}

export class TTLCacheStrategy implements ICacheStrategy {
  shouldEvict(entry: CacheEntry<any>, cache: IModelCache): boolean {
    if (!entry.expiresAt) return false;
    return new Date() >= new Date(entry.expiresAt);
  }

  selectEvictionCandidate(entries: CacheEntry<any>[]): CacheEntry<any> | null {
    return entries.find((entry) => this.shouldEvict(entry, null as any)) || null;
  }
}
```

### Subtask 1.3: Specify ModelCapabilityMapper Interface

**Description**: Define interfaces for mapping and normalizing capabilities across different provider APIs to ensure consistent capability representation.

**Implementation Details**:

1. **Create Capability Mapper Interface**:

```typescript
// packages/providers/src/interfaces/capability-mapper.interface.ts
import { ModelCapabilities, ModelCapability, ProviderCapability } from './model.types';

export interface IModelCapabilityMapper {
  // Core mapping operations
  mapCapabilities(providerModel: any, providerType: string): ModelCapabilities;
  normalizeCapability(capability: string): string;
  denormalizeCapability(normalizedCapability: string, providerType: string): string;

  // Validation operations
  validateCapabilities(capabilities: ModelCapabilities): CapabilityValidationResult;
  validateCapability(capability: ModelCapability): boolean;

  // Provider-specific operations
  getProviderCapabilities(providerType: string): ProviderCapabilityDefinition[];
  mapProviderCapability(
    providerCapability: ProviderCapability,
    providerType: string
  ): ModelCapability | null;

  // Capability registry
  registerCapabilityDefinition(definition: CapabilityDefinition): void;
  getCapabilityDefinition(capability: string): CapabilityDefinition | undefined;
  getAllCapabilityDefinitions(): CapabilityDefinition[];

  // Compatibility operations
  checkCapabilityCompatibility(required: string[], available: string[]): CompatibilityResult;
  findAlternativeCapabilities(required: string[], available: string[]): AlternativeCapability[];

  // Transformation operations
  transformCapabilities(capabilities: ModelCapabilities, targetProvider: string): ModelCapabilities;
  mergeCapabilities(
    capabilities1: ModelCapabilities,
    capabilities2: ModelCapabilities
  ): ModelCapabilities;

  // Analytics and insights
  getCapabilityUsage(): Promise<CapabilityUsageStats>;
  getCapabilityMappingStats(): Promise<MappingStats>;
}

export interface CapabilityDefinition {
  name: string;
  category: CapabilityCategory;
  description: string;
  dataType: CapabilityDataType;
  required: boolean;
  version: string;
  deprecated?: boolean;
  deprecationMessage?: string;
  alternatives?: string[];
  validation?: CapabilityValidation;
  examples?: any[];
}

export enum CapabilityCategory {
  CORE = 'core', // Basic model capabilities
  INPUT = 'input', // Input processing capabilities
  OUTPUT = 'output', // Output generation capabilities
  PERFORMANCE = 'performance', // Performance characteristics
  SECURITY = 'security', // Security and safety features
  INTEGRATION = 'integration', // Integration capabilities
  SPECIALIZED = 'specialized', // Specialized capabilities
}

export enum CapabilityDataType {
  BOOLEAN = 'boolean',
  STRING = 'string',
  NUMBER = 'number',
  ARRAY = 'array',
  OBJECT = 'object',
  ENUM = 'enum',
}

export interface CapabilityValidation {
  pattern?: string; // Regex pattern for string values
  min?: number; // Minimum value for numbers
  max?: number; // Maximum value for numbers
  enum?: string[]; // Allowed values for enums
  required?: string[]; // Required sub-properties for objects
}

export interface ProviderCapabilityDefinition {
  providerType: string;
  providerCapabilityName: string;
  normalizedCapabilityName: string;
  mappingFunction: string; // Function name or reference
  transformationRules?: TransformationRule[];
  version: string;
}

export interface TransformationRule {
  type: 'rename' | 'type_cast' | 'value_map' | 'conditional' | 'custom';
  source: string;
  target: string;
  mapping?: Record<string, any>;
  condition?: string;
}

export interface CapabilityValidationResult {
  valid: boolean;
  errors: CapabilityValidationError[];
  warnings: CapabilityValidationWarning[];
}

export interface CapabilityValidationError {
  capability: string;
  field: string;
  message: string;
  code: string;
  severity: 'error';
}

export interface CapabilityValidationWarning {
  capability: string;
  field: string;
  message: string;
  code: string;
  severity: 'warning';
}

export interface CompatibilityResult {
  compatible: boolean;
  missingCapabilities: string[];
  alternativeCapabilities: AlternativeCapability[];
  compatibilityScore: number; // 0-100
  recommendations: string[];
}

export interface AlternativeCapability {
  required: string;
  alternatives: string[];
  compatibilityScore: number;
  description: string;
}

export interface CapabilityUsageStats {
  totalModels: number;
  capabilityDistribution: Record<string, number>;
  providerDistribution: Record<string, Record<string, number>>;
  usageTrends: CapabilityUsageTrend[];
  mostCommonCapabilities: string[];
  leastCommonCapabilities: string[];
}

export interface CapabilityUsageTrend {
  capability: string;
  period: string;
  count: number;
  changePercentage: number;
}

export interface MappingStats {
  totalMappings: number;
  providerMappings: Record<string, number>;
  mappingAccuracy: Record<string, number>;
  failedMappings: number;
  commonMappingErrors: Record<string, number>;
}

// Built-in capability mappers
export interface IProviderCapabilityMapper {
  providerType: string;
  version: string;
  mapCapabilities(providerModel: any): ModelCapabilities;
  mapCapability(providerCapability: any): ModelCapability | null;
  getSupportedCapabilities(): string[];
}

export class OpenAICapabilityMapper implements IProviderCapabilityMapper {
  providerType = 'openai';
  version = '1.0';

  mapCapabilities(providerModel: any): ModelCapabilities {
    // Implementation for OpenAI capability mapping
    return {
      textGeneration: {
        supported: true,
        maxTokens: providerModel.max_tokens,
        streaming: providerModel.supports_streaming || false,
      },
      functionCalling: providerModel.supports_function_calling || false,
      vision: providerModel.supports_vision || false,
      // ... other capabilities
    };
  }

  mapCapability(providerCapability: any): ModelCapability | null {
    // Implementation for individual capability mapping
    return null;
  }

  getSupportedCapabilities(): string[] {
    return ['text_generation', 'function_calling', 'vision', 'embedding'];
  }
}

export class AnthropicCapabilityMapper implements IProviderCapabilityMapper {
  providerType = 'anthropic';
  version = '1.0';

  mapCapabilities(providerModel: any): ModelCapabilities {
    // Implementation for Anthropic capability mapping
    return {
      textGeneration: {
        supported: true,
        maxTokens: providerModel.max_tokens,
        streaming: true,
      },
      vision: providerModel.supports_vision || false,
      // ... other capabilities
    };
  }

  mapCapability(providerCapability: any): ModelCapability | null {
    return null;
  }

  getSupportedCapabilities(): string[] {
    return ['text_generation', 'vision'];
  }
}
```

### Subtask 1.4: Create ModelUpdateSubscriber Interface

**Description**: Define interfaces for subscribing to and managing model update notifications with filtering, batching, and error handling.

**Implementation Details**:

1. **Create Update Subscriber Interface**:

```typescript
// packages/providers/src/interfaces/update-subscriber.interface.ts
import {
  ModelUpdateEvent,
  ProviderUpdateEvent,
  CapabilityUpdateEvent,
} from './model-discovery.interface';

export interface IModelUpdateSubscriber {
  // Subscription management
  subscribe(subscription: UpdateSubscription): Promise<SubscriptionHandle>;
  unsubscribe(subscriptionId: string): Promise<boolean>;
  unsubscribeAll(clientId?: string): Promise<number>;

  // Subscription query
  getSubscription(subscriptionId: string): Promise<UpdateSubscription | undefined>;
  getSubscriptions(clientId?: string): Promise<UpdateSubscription[]>;
  getSubscriptionsByFilter(filter: SubscriptionFilter): Promise<UpdateSubscription[]>;

  // Event publishing
  publishModelUpdate(event: ModelUpdateEvent): Promise<PublishResult>;
  publishProviderUpdate(event: ProviderUpdateEvent): Promise<PublishResult>;
  publishCapabilityUpdate(event: CapabilityUpdateEvent): Promise<PublishResult>;

  // Batch operations
  publishBatch(events: UpdateEvent[]): Promise<BatchPublishResult>;

  // Subscription management
  pauseSubscription(subscriptionId: string): Promise<boolean>;
  resumeSubscription(subscriptionId: string): Promise<boolean>;
  updateSubscription(
    subscriptionId: string,
    updates: Partial<UpdateSubscription>
  ): Promise<boolean>;

  // Filtering and routing
  addFilter(filter: EventFilter): Promise<string>;
  removeFilter(filterId: string): Promise<boolean>;
  updateFilter(filterId: string, filter: Partial<EventFilter>): Promise<boolean>;

  // Statistics and monitoring
  getSubscriptionStats(): Promise<SubscriptionStats>;
  getPublishStats(): Promise<PublishStats>;
  getEventStats(timeRange?: TimeRange): Promise<EventStats>;

  // Health and maintenance
  healthCheck(): Promise<SubscriberHealth>;
  cleanup(): Promise<CleanupResult>;
}

export interface UpdateSubscription {
  id?: string;
  clientId: string;
  type: SubscriptionType;
  filters: EventFilter[];
  callback: UpdateCallback;
  options: SubscriptionOptions;
  status: SubscriptionStatus;
  createdAt: string;
  lastActivity?: string;
  eventCount?: number;
  errorCount?: number;
}

export enum SubscriptionType {
  MODEL_UPDATES = 'model_updates',
  PROVIDER_UPDATES = 'provider_updates',
  CAPABILITY_UPDATES = 'capability_updates',
  ALL_UPDATES = 'all_updates',
}

export enum SubscriptionStatus {
  ACTIVE = 'active',
  PAUSED = 'paused',
  ERROR = 'error',
  EXPIRED = 'expired',
}

export interface EventFilter {
  id?: string;
  type: FilterType;
  conditions: FilterCondition[];
  operator: 'AND' | 'OR';
  enabled: boolean;
}

export enum FilterType {
  MODEL_FILTER = 'model_filter',
  PROVIDER_FILTER = 'provider_filter',
  CAPABILITY_FILTER = 'capability_filter',
  CUSTOM_FILTER = 'custom_filter',
}

export interface FilterCondition {
  field: string;
  operator: FilterOperator;
  value: any;
  caseSensitive?: boolean;
}

export enum FilterOperator {
  EQUALS = 'equals',
  NOT_EQUALS = 'not_equals',
  CONTAINS = 'contains',
  NOT_CONTAINS = 'not_contains',
  STARTS_WITH = 'starts_with',
  ENDS_WITH = 'ends_with',
  GREATER_THAN = 'greater_than',
  LESS_THAN = 'less_than',
  IN = 'in',
  NOT_IN = 'not_in',
  REGEX = 'regex',
}

export interface SubscriptionOptions {
  batchSize?: number;
  batchTimeout?: number;
  maxRetries?: number;
  retryDelay?: number;
  timeout?: number;
  priority?: number;
  metadata?: Record<string, any>;
}

export interface SubscriptionHandle {
  subscriptionId: string;
  unsubscribe: () => Promise<void>;
  pause: () => Promise<void>;
  resume: () => Promise<void>;
  update: (updates: Partial<UpdateSubscription>) => Promise<void>;
  getStatus: () => Promise<SubscriptionStatus>;
}

export type UpdateCallback = (event: UpdateEvent) => void | Promise<void>;

export type UpdateEvent = ModelUpdateEvent | ProviderUpdateEvent | CapabilityUpdateEvent;

export interface SubscriptionFilter {
  clientId?: string;
  type?: SubscriptionType;
  status?: SubscriptionStatus;
  filters?: EventFilter[];
  createdAfter?: string;
  createdBefore?: string;
  lastActivityAfter?: string;
}

export interface PublishResult {
  success: boolean;
  deliveredCount: number;
  failedCount: number;
  errors: PublishError[];
  duration: number;
}

export interface BatchPublishResult {
  totalEvents: number;
  successCount: number;
  failedCount: number;
  results: PublishResult[];
  totalDuration: number;
}

export interface PublishError {
  subscriptionId: string;
  error: string;
  retryable: boolean;
  eventId?: string;
}

export interface SubscriptionStats {
  totalSubscriptions: number;
  activeSubscriptions: number;
  pausedSubscriptions: number;
  errorSubscriptions: number;
  subscriptionsByType: Record<SubscriptionType, number>;
  subscriptionsByClient: Record<string, number>;
  averageEventsPerSecond: number;
  peakEventsPerSecond: number;
}

export interface PublishStats {
  totalEventsPublished: number;
  successfulDeliveries: number;
  failedDeliveries: number;
  averageDeliveryTime: number;
  peakDeliveryTime: number;
  eventsByType: Record<string, number>;
  errorsByType: Record<string, number>;
}

export interface EventStats {
  totalEvents: number;
  eventsByType: Record<string, number>;
  eventsByProvider: Record<string, number>;
  eventsByModel: Record<string, number>;
  averageProcessingTime: number;
  errorRate: number;
  timeSeriesData: EventTimeSeriesPoint[];
}

export interface EventTimeSeriesPoint {
  timestamp: string;
  eventCount: number;
  errorCount: number;
  averageProcessingTime: number;
}

export interface SubscriberHealth {
  status: 'healthy' | 'degraded' | 'unhealthy';
  uptime: number;
  memoryUsage: number;
  activeConnections: number;
  queueSize: number;
  errorRate: number;
  lastCheck: string;
  issues: HealthIssue[];
}

export interface HealthIssue {
  type: 'memory' | 'performance' | 'connectivity' | 'error_rate';
  severity: 'low' | 'medium' | 'high' | 'critical';
  message: string;
  timestamp: string;
  resolved?: boolean;
}

export interface CleanupResult {
  expiredSubscriptions: number;
  orphanedFilters: number;
  clearedEvents: number;
  freedMemory: number;
  duration: number;
}

export interface TimeRange {
  start: string;
  end: string;
}

// Event batching and delivery
export interface IEventBatcher {
  addEvent(event: UpdateEvent): Promise<void>;
  flush(): Promise<void>;
  setBatchSize(size: number): void;
  setBatchTimeout(timeout: number): void;
  getBatchStats(): Promise<BatchStats>;
}

export interface BatchStats {
  currentBatchSize: number;
  averageBatchSize: number;
  totalBatchesProcessed: number;
  averageProcessingTime: number;
  lastBatchProcessed?: string;
}

// Event delivery strategies
export interface IDeliveryStrategy {
  deliver(subscriptions: UpdateSubscription[], event: UpdateEvent): Promise<DeliveryResult>;
  setConcurrencyLimit(limit: number): void;
  setTimeout(timeout: number): void;
  setRetryPolicy(policy: RetryPolicy): void;
}

export interface DeliveryResult {
  successfulDeliveries: number;
  failedDeliveries: number;
  totalDuration: number;
  errors: DeliveryError[];
}

export interface DeliveryError {
  subscriptionId: string;
  error: string;
  retryable: boolean;
  duration: number;
}

export interface RetryPolicy {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  retryableErrors: string[];
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/model-discovery.interface.ts`
   - `packages/providers/src/interfaces/provider-discovery.interface.ts`
   - `packages/providers/src/interfaces/model-cache.interface.ts`
   - `packages/providers/src/interfaces/capability-mapper.interface.ts`
   - `packages/providers/src/interfaces/update-subscriber.interface.ts`

2. **Supporting Types**:
   - `packages/providers/src/interfaces/model.types.ts` (shared types for all interfaces)

3. **Implementation Examples**:
   - `packages/providers/src/interfaces/examples/cache-strategies.ts`
   - `packages/providers/src/interfaces/examples/capability-mappers.ts`

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/interfaces/model-discovery.interface.test.ts
describe('Model Discovery Interfaces', () => {
  describe('IModelDiscoveryService', () => {
    it('should define all required methods', () => {
      const service: IModelDiscoveryService = {} as any;

      expect(typeof service.discoverModels).toBe('function');
      expect(typeof service.refreshProvider).toBe('function');
      expect(typeof service.getModel).toBe('function');
      expect(typeof service.subscribeToModelUpdates).toBe('function');
      expect(typeof service.start).toBe('function');
      expect(typeof service.stop).toBe('function');
    });
  });

  describe('ModelUpdateEvent', () => {
    it('should create valid model update event', () => {
      const event: ModelUpdateEvent = {
        type: 'added',
        modelId: 'test-model',
        providerName: 'test-provider',
        timestamp: new Date().toISOString(),
      };

      expect(event.type).toBe('added');
      expect(event.modelId).toBe('test-model');
      expect(event.providerName).toBe('test-provider');
      expect(event.timestamp).toBeDefined();
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/interfaces/model-cache.interface.test.ts
describe('Model Cache Interfaces', () => {
  describe('IModelCache', () => {
    it('should define all required cache operations', () => {
      const cache: IModelCache = {} as any;

      expect(typeof cache.get).toBe('function');
      expect(typeof cache.set).toBe('function');
      expect(typeof cache.search).toBe('function');
      expect(typeof cache.getStats).toBe('function');
    });
  });

  describe('CacheSetOptions', () => {
    it('should accept TTL and other options', () => {
      const options: CacheSetOptions = {
        ttl: 300000,
        tags: ['test', 'provider'],
        priority: 1,
        metadata: { source: 'discovery' },
      };

      expect(options.ttl).toBe(300000);
      expect(options.tags).toEqual(['test', 'provider']);
      expect(options.priority).toBe(1);
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/interface-compatibility.test.ts
describe('Interface Compatibility', () => {
  it('should ensure all interfaces are compatible', () => {
    // Test that implementations can satisfy all interfaces
    const mockDiscoveryService = new MockModelDiscoveryService();
    const mockCache = new MockModelCache();
    const mockMapper = new MockCapabilityMapper();
    const mockSubscriber = new MockUpdateSubscriber();

    expect(mockDiscoveryService).toBeDefined();
    expect(mockCache).toBeDefined();
    expect(mockMapper).toBeDefined();
    expect(mockSubscriber).toBeDefined();
  });
});
```

## Security Considerations

1. **Interface Security**:
   - Validate all input parameters in interface implementations
   - Sanitize callback functions to prevent injection attacks
   - Implement proper error handling to prevent information leakage

2. **Subscription Security**:
   - Authenticate subscription requests
   - Authorize access to specific model/provider updates
   - Rate limit subscription operations

3. **Cache Security**:
   - Encrypt sensitive model metadata in cache
   - Implement access controls for cache operations
   - Validate cache keys and values

## Dependencies

### New Dependencies

```json
{
  "uuid": "^9.0.1",
  "eventemitter3": "^5.0.1"
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

1. **Interface Metrics**:
   - Method call frequencies and durations
   - Error rates by interface method
   - Subscription and cache statistics

2. **Logging**:
   - Interface method calls with parameters
   - Cache operations and performance
   - Subscription events and deliveries

3. **Alerts**:
   - High error rates for interface methods
   - Cache performance degradation
   - Subscription delivery failures

## Acceptance Criteria

1. ✅ **Complete Interface Definitions**: All interfaces fully defined with TypeScript types
2. ✅ **Documentation**: Comprehensive JSDoc documentation for all interfaces
3. ✅ **Type Safety**: Strict TypeScript configuration with no any types
4. ✅ **Extensibility**: Interfaces designed for future extensions
5. ✅ **Compatibility**: Interfaces compatible with existing provider patterns
6. ✅ **Testing**: Unit tests for all interface definitions
7. ✅ **Examples**: Implementation examples for major interfaces
8. ✅ **Validation**: Input validation specifications for all methods
9. ✅ **Error Handling**: Proper error type definitions
10. ✅ **Performance**: Performance considerations in interface design

## Success Metrics

- Interface compilation without TypeScript errors
- 100% type coverage for all defined interfaces
- Successful implementation of all interfaces in tests
- Clear documentation with examples for all methods
- Zero security vulnerabilities in interface design
- Performance benchmarks meeting specified requirements
