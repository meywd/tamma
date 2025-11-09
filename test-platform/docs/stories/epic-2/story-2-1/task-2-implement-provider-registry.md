# Task 2: Implement Provider Registry

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 2 - Provider registry system for dynamic provider registration  
**Status**: Ready for Development

## Overview

This task involves implementing a centralized provider registry that manages the lifecycle, registration, and discovery of AI providers. The registry will serve as the single source of truth for all available providers and enable dynamic provider management without requiring application restarts.

## Subtasks

### Subtask 2.1: Create Registry Class with Registration Methods

**Objective**: Implement the core registry functionality for managing provider instances.

**Implementation Details**:

1. **File Location**: `packages/providers/src/registry/ProviderRegistry.ts`

2. **Core Registry Class**:

   ```typescript
   export class ProviderRegistry implements IProviderRegistry {
     private providers: Map<string, RegisteredProvider> = new Map();
     private factory: IProviderFactory;
     private logger: Logger;
     private eventEmitter: EventEmitter;

     constructor(factory: IProviderFactory, logger: Logger) {
       this.factory = factory;
       this.logger = logger;
       this.eventEmitter = new EventEmitter();
     }

     // Registration Methods
     async register(name: string, config: ProviderConfig): Promise<void>;
     async registerWithInstance(name: string, provider: IAIProvider): Promise<void>;
     async unregister(name: string): Promise<void>;

     // Retrieval Methods
     getProvider(name: string): IAIProvider | undefined;
     getProviders(): Map<string, IAIProvider>;
     getProviderNames(): string[];
     hasProvider(name: string): boolean;

     // Lifecycle Management
     async initializeProvider(name: string): Promise<void>;
     async disposeProvider(name: string): Promise<void>;
     async disposeAll(): Promise<void>;

     // Health and Status
     async getProviderHealth(name: string): Promise<ProviderHealthStatus>;
     async getAllProviderHealth(): Promise<Map<string, ProviderHealthStatus>>;

     // Event Handling
     on(
       event: 'provider-registered' | 'provider-unregistered' | 'provider-error',
       listener: Function
     ): void;
     off(event: string, listener: Function): void;
   }
   ```

3. **Registered Provider Structure**:

   ```typescript
   interface RegisteredProvider {
     name: string;
     provider: IAIProvider;
     config: ProviderConfig;
     status: 'initializing' | 'ready' | 'error' | 'disposed';
     registeredAt: Date;
     lastHealthCheck?: Date;
     capabilities?: ProviderCapabilities;
     metadata: {
       source: 'config' | 'plugin' | 'api';
       version?: string;
       [key: string]: unknown;
     };
   }
   ```

4. **Registration Logic**:

   ```typescript
   async register(name: string, config: ProviderConfig): Promise<void> {
     // Validate inputs
     if (!name || typeof name !== 'string') {
       throw new ProviderError('INVALID_NAME', 'Provider name must be a non-empty string');
     }

     if (this.providers.has(name)) {
       throw new ProviderError('ALREADY_EXISTS', `Provider '${name}' is already registered`);
     }

     try {
       // Create provider instance
       const provider = await this.factory.createProvider(config.type, config);

       // Initialize provider
       await provider.initialize(config);

       // Get capabilities
       const capabilities = provider.getCapabilities();

       // Register provider
       const registeredProvider: RegisteredProvider = {
         name,
         provider,
         config,
         status: 'ready',
         registeredAt: new Date(),
         capabilities,
         metadata: { source: 'config' }
       };

       this.providers.set(name, registeredProvider);

       this.logger.info('Provider registered successfully', { name, type: config.type });
       this.eventEmitter.emit('provider-registered', { name, provider: registeredProvider });

     } catch (error) {
       this.logger.error('Failed to register provider', { name, error });
       throw new ProviderError('REGISTRATION_FAILED', `Failed to register provider '${name}': ${error.message}`);
     }
   }
   ```

### Subtask 2.2: Implement Provider Discovery Functionality

**Objective**: Add automatic discovery and registration of providers from various sources.

**Implementation Details**:

1. **File Location**: `packages/providers/src/registry/ProviderDiscovery.ts`

2. **Discovery Service Class**:

   ```typescript
   export class ProviderDiscovery {
     private registry: IProviderRegistry;
     private configLoader: ConfigLoader;
     private pluginLoader: PluginLoader;
     private logger: Logger;

     constructor(
       registry: IProviderRegistry,
       configLoader: ConfigLoader,
       pluginLoader: PluginLoader,
       logger: Logger
     ) {
       this.registry = registry;
       this.configLoader = configLoader;
       this.pluginLoader = pluginLoader;
       this.logger = logger;
     }

     // Discovery Methods
     async discoverFromConfig(configPath?: string): Promise<void>;
     async discoverFromEnvironment(): Promise<void>;
     async discoverFromPlugins(pluginPath?: string): Promise<void>;
     async discoverAll(): Promise<void>;

     // Validation
     async validateDiscoveredProviders(): Promise<ValidationResult[]>;

     // Auto-refresh
     async startAutoRefresh(intervalMs: number): Promise<void>;
     async stopAutoRefresh(): Promise<void>;
   }
   ```

3. **Configuration-Based Discovery**:

   ```typescript
   async discoverFromConfig(configPath?: string): Promise<void> {
     const config = await this.configLoader.loadProviderConfig(configPath);

     for (const [name, providerConfig] of Object.entries(config.providers)) {
       try {
         if (providerConfig.enabled !== false) {
           await this.registry.register(name, providerConfig);
           this.logger.info('Discovered provider from config', { name });
         }
       } catch (error) {
         this.logger.warn('Failed to register discovered provider', { name, error });
       }
     }
   }
   ```

4. **Environment-Based Discovery**:

   ```typescript
   async discoverFromEnvironment(): Promise<void> {
     const envProviders = this.scanEnvironmentForProviders();

     for (const envProvider of envProviders) {
       try {
         const config = this.buildConfigFromEnvironment(envProvider);
         await this.registry.register(envProvider.name, config);
         this.logger.info('Discovered provider from environment', { name: envProvider.name });
       } catch (error) {
         this.logger.warn('Failed to register environment provider', { name: envProvider.name, error });
       }
     }
   }

   private scanEnvironmentForProviders(): EnvProvider[] {
     const providers: EnvProvider[] = [];

     // Scan for common environment variable patterns
     if (process.env.ANTHROPIC_API_KEY) {
       providers.push({
         name: 'anthropic',
         type: 'anthropic',
         envVars: ['ANTHROPIC_API_KEY']
       });
     }

     if (process.env.OPENAI_API_KEY) {
       providers.push({
         name: 'openai',
         type: 'openai',
         envVars: ['OPENAI_API_KEY']
       });
     }

     // Add more providers as needed

     return providers;
   }
   ```

5. **Plugin-Based Discovery**:
   ```typescript
   async discoverFromPlugins(pluginPath?: string): Promise<void> {
     const plugins = await this.pluginLoader.discoverPlugins(pluginPath);

     for (const plugin of plugins) {
       try {
         await this.pluginLoader.loadPlugin(plugin);
         const provider = plugin.createProvider();
         await this.registry.registerWithInstance(plugin.name, provider);
         this.logger.info('Discovered provider from plugin', { name: plugin.name });
       } catch (error) {
         this.logger.warn('Failed to load plugin provider', { name: plugin.name, error });
       }
     }
   }
   ```

### Subtask 2.3: Add Provider Lifecycle Management

**Objective**: Implement comprehensive lifecycle management for provider instances.

**Implementation Details**:

1. **File Location**: `packages/providers/src/registry/ProviderLifecycleManager.ts`

2. **Lifecycle Manager Class**:

   ```typescript
   export class ProviderLifecycleManager {
     private registry: IProviderRegistry;
     private healthChecker: HealthChecker;
     private circuitBreaker: CircuitBreaker;
     private logger: Logger;
     private lifecycleConfig: LifecycleConfig;

     constructor(registry: IProviderRegistry, healthChecker: HealthChecker, logger: Logger) {
       this.registry = registry;
       this.healthChecker = healthChecker;
       this.logger = logger;
       this.circuitBreaker = new CircuitBreaker();
     }

     // Lifecycle Operations
     async initializeProvider(name: string): Promise<void>;
     async startProvider(name: string): Promise<void>;
     async stopProvider(name: string): Promise<void>;
     async restartProvider(name: string): Promise<void>;
     async disposeProvider(name: string): Promise<void>;

     // Bulk Operations
     async initializeAll(): Promise<void>;
     async startAll(): Promise<void>;
     async stopAll(): Promise<void>;
     async disposeAll(): Promise<void>;

     // Health Monitoring
     async startHealthMonitoring(name: string, intervalMs?: number): Promise<void>;
     async stopHealthMonitoring(name: string): Promise<void>;
     async startHealthMonitoringAll(intervalMs?: number): Promise<void>;
     async stopHealthMonitoringAll(): Promise<void>;

     // Graceful Shutdown
     async gracefulShutdown(): Promise<void>;
   }
   ```

3. **Health Monitoring Implementation**:

   ```typescript
   async startHealthMonitoring(name: string, intervalMs: number = 30000): Promise<void> {
     const provider = this.registry.getProvider(name);
     if (!provider) {
       throw new ProviderError('NOT_FOUND', `Provider '${name}' not found`);
     }

     const healthCheckInterval = setInterval(async () => {
       try {
         const health = await this.healthChecker.checkProvider(provider);

         if (health.status === 'healthy') {
           this.circuitBreaker.recordSuccess(name);
         } else {
           this.circuitBreaker.recordFailure(name);
           this.logger.warn('Provider health check failed', { name, health });
         }

         // Update provider status in registry
         await this.updateProviderHealthStatus(name, health);

       } catch (error) {
         this.circuitBreaker.recordFailure(name);
         this.logger.error('Health check error', { name, error });
       }
     }, intervalMs);

     // Store interval reference for cleanup
     this.healthIntervals.set(name, healthCheckInterval);
   }
   ```

4. **Circuit Breaker Integration**:

   ```typescript
   class CircuitBreaker {
     private failureCounts: Map<string, number> = new Map();
     private lastFailureTime: Map<string, number> = new Map();
     private circuitStates: Map<string, 'closed' | 'open' | 'half-open'> = new Map();

     recordSuccess(providerName: string): void {
       this.failureCounts.set(providerName, 0);
       this.circuitStates.set(providerName, 'closed');
     }

     recordFailure(providerName: string): void {
       const currentFailures = this.failureCounts.get(providerName) || 0;
       const newFailures = currentFailures + 1;

       this.failureCounts.set(providerName, newFailures);
       this.lastFailureTime.set(providerName, Date.now());

       // Open circuit after 5 failures within 60 seconds
       if (newFailures >= 5) {
         this.circuitStates.set(providerName, 'open');
         this.logger.warn('Circuit breaker opened for provider', {
           providerName,
           failures: newFailures,
         });
       }
     }

     canExecute(providerName: string): boolean {
       const state = this.circuitStates.get(providerName) || 'closed';

       if (state === 'closed') {
         return true;
       }

       if (state === 'open') {
         const lastFailure = this.lastFailureTime.get(providerName) || 0;
         // Try half-open after 5 minutes
         if (Date.now() - lastFailure > 300000) {
           this.circuitStates.set(providerName, 'half-open');
           return true;
         }
         return false;
       }

       return true; // half-open
     }
   }
   ```

5. **Graceful Shutdown Implementation**:
   ```typescript
   async gracefulShutdown(): Promise<void> {
     this.logger.info('Starting graceful shutdown of all providers');

     try {
       // Stop health monitoring
       await this.stopHealthMonitoringAll();

       // Get all registered providers
       const providers = this.registry.getProviders();
       const shutdownPromises: Promise<void>[] = [];

       // Initiate parallel shutdown with timeout
       for (const [name, provider] of providers) {
         const shutdownPromise = Promise.race([
           provider.dispose(),
           new Promise((_, reject) =>
             setTimeout(() => reject(new Error('Shutdown timeout')), 30000)
           )
         ]).catch(error => {
           this.logger.error('Provider shutdown failed', { name, error });
         });

         shutdownPromises.push(shutdownPromise);
       }

       // Wait for all providers to shutdown (with timeout)
       await Promise.allSettled(shutdownPromises);

       this.logger.info('Graceful shutdown completed');

     } catch (error) {
       this.logger.error('Error during graceful shutdown', { error });
       throw error;
     }
   }
   ```

## Technical Requirements

### Performance Requirements

- Provider registration should complete within 100ms
- Health checks should not impact provider performance
- Registry operations should be thread-safe
- Memory usage should scale linearly with provider count

### Reliability Requirements

- Registry must maintain consistency during concurrent operations
- Health monitoring should be resilient to temporary failures
- Circuit breaker should prevent cascade failures
- Graceful shutdown must complete within 30 seconds

### Security Requirements

- Provider configurations must be validated before registration
- Sensitive data should not be logged or exposed
- Plugin loading must be sandboxed and validated
- Access to registry methods should be controlled

## Testing Strategy

### Unit Tests

```typescript
describe('ProviderRegistry', () => {
  describe('registration', () => {
    it('should register a provider successfully');
    it('should reject duplicate provider names');
    it('should handle registration failures gracefully');
    it('should validate provider configuration');
  });

  describe('retrieval', () => {
    it('should retrieve registered providers');
    it('should return undefined for non-existent providers');
    it('should list all provider names');
  });

  describe('lifecycle', () => {
    it('should initialize providers correctly');
    it('should dispose providers cleanly');
    it('should handle provider failures');
  });
});
```

### Integration Tests

- Test registry with real provider implementations
- Test discovery mechanisms with actual config files
- Test health monitoring with provider failures
- Test graceful shutdown scenarios

### Performance Tests

- Measure registration time with multiple providers
- Test concurrent registration operations
- Benchmark health check overhead
- Test memory usage with many providers

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared types and utilities
- `@tamma/observability` - Logging and monitoring
- Task 1 output - IAIProvider interface and types

### External Dependencies

- TypeScript 5.7+ - Type safety
- Node.js EventEmitter - Event handling
- JSON Schema validation - Configuration validation

## Deliverables

1. **Registry Implementation**: `packages/providers/src/registry/ProviderRegistry.ts`
2. **Discovery Service**: `packages/providers/src/registry/ProviderDiscovery.ts`
3. **Lifecycle Manager**: `packages/providers/src/registry/ProviderLifecycleManager.ts`
4. **Circuit Breaker**: `packages/providers/src/registry/CircuitBreaker.ts`
5. **Health Checker**: `packages/providers/src/registry/HealthChecker.ts`
6. **Registry Interface**: `packages/providers/src/interfaces/IProviderRegistry.ts`
7. **Unit Tests**: `packages/providers/src/registry/__tests__/`
8. **Integration Tests**: `packages/providers/src/registry/__integration__/`

## Acceptance Criteria Verification

- [ ] Registry can register and retrieve providers dynamically
- [ ] Discovery mechanisms work for config, environment, and plugins
- [ ] Lifecycle management handles initialization and disposal
- [ ] Health monitoring detects and reports provider issues
- [ ] Circuit breaker prevents cascade failures
- [ ] Graceful shutdown completes within timeout
- [ ] All operations are thread-safe and consistent
- [ ] Error handling is comprehensive and non-disruptive

## Implementation Notes

### Configuration Schema

```typescript
interface RegistryConfig {
  discovery: {
    configPath?: string;
    pluginPath?: string;
    autoRefreshInterval?: number;
    enableEnvironmentDiscovery?: boolean;
  };
  health: {
    checkInterval: number;
    timeout: number;
    retries: number;
  };
  circuitBreaker: {
    failureThreshold: number;
    recoveryTimeout: number;
    monitoringPeriod: number;
  };
  lifecycle: {
    initTimeout: number;
    disposeTimeout: number;
    gracefulShutdownTimeout: number;
  };
}
```

### Event System

The registry should emit events for:

- `provider-registered`: When a new provider is registered
- `provider-unregistered`: When a provider is removed
- `provider-error`: When a provider encounters an error
- `health-status-changed`: When provider health status changes
- `circuit-breaker-opened`: When circuit breaker opens
- `circuit-breaker-closed`: When circuit breaker closes

### Error Handling

All registry operations should use structured error handling:

```typescript
class RegistryError extends Error {
  constructor(
    public code: string,
    message: string,
    public providerName?: string,
    public context?: Record<string, unknown>
  ) {
    super(message);
    this.name = 'RegistryError';
  }
}
```

## Next Steps

After completing this task:

1. Move to Task 3: Create Standardized Models
2. Use the registry in provider factory implementation
3. Integrate with configuration management system

## Risk Mitigation

### Technical Risks

- **Memory Leaks**: Ensure proper cleanup of providers and intervals
- **Race Conditions**: Use proper synchronization for concurrent operations
- **Performance Impact**: Minimize overhead of health monitoring

### Mitigation Strategies

- Comprehensive testing with concurrent operations
- Memory profiling and leak detection
- Performance benchmarking and optimization
- Circuit breaker patterns to prevent failures
