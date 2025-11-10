# Task 4: Develop Capability Mapping

## Overview

Implement standardized capability mapping across different provider APIs with validation, normalization, and model filtering functionality.

## Objectives

- Create standardized capability definitions and taxonomy
- Implement provider-specific capability mappers for major providers
- Add capability validation and normalization with conflict resolution
- Create model filtering and search functionality with capability-based queries

## Implementation Steps

### Subtask 4.1: Create Standardized Capability Definitions

**Description**: Define a comprehensive capability taxonomy with standardized names, data types, and validation rules.

**Implementation Details**:

1. **Create Capability Registry**:

```typescript
// packages/providers/src/capabilities/capability-registry.ts
import {
  CapabilityDefinition,
  CapabilityCategory,
  CapabilityDataType,
  CapabilityValidation,
} from '../interfaces/capability-mapper.interface';

export class CapabilityRegistry {
  private capabilities: Map<string, CapabilityDefinition> = new Map();
  private categories: Map<CapabilityCategory, Set<string>> = new Map();
  private aliases: Map<string, string> = new Map();
  private dependencies: Map<string, Set<string>> = new Map();

  constructor() {
    this.initializeStandardCapabilities();
  }

  // Capability registration
  registerCapability(definition: CapabilityDefinition): void {
    this.validateCapabilityDefinition(definition);

    this.capabilities.set(definition.name, definition);

    // Add to category
    if (!this.categories.has(definition.category)) {
      this.categories.set(definition.category, new Set());
    }
    this.categories.get(definition.category)!.add(definition.name);

    // Register aliases
    if (definition.aliases) {
      for (const alias of definition.aliases) {
        this.aliases.set(alias, definition.name);
      }
    }
  }

  unregisterCapability(name: string): void {
    const definition = this.capabilities.get(name);
    if (!definition) {
      return;
    }

    this.capabilities.delete(name);

    // Remove from category
    const categoryCapabilities = this.categories.get(definition.category);
    if (categoryCapabilities) {
      categoryCapabilities.delete(name);
    }

    // Remove aliases
    if (definition.aliases) {
      for (const alias of definition.aliases) {
        this.aliases.delete(alias);
      }
    }

    // Remove dependencies
    this.dependencies.delete(name);
  }

  // Capability retrieval
  getCapability(name: string): CapabilityDefinition | undefined {
    // Try direct name first
    let capability = this.capabilities.get(name);

    // Try aliases
    if (!capability) {
      const canonicalName = this.aliases.get(name);
      if (canonicalName) {
        capability = this.capabilities.get(canonicalName);
      }
    }

    return capability;
  }

  getAllCapabilities(): CapabilityDefinition[] {
    return Array.from(this.capabilities.values());
  }

  getCapabilitiesByCategory(category: CapabilityCategory): CapabilityDefinition[] {
    const capabilityNames = this.categories.get(category);
    if (!capabilityNames) {
      return [];
    }

    return Array.from(capabilityNames)
      .map((name) => this.capabilities.get(name)!)
      .filter(Boolean);
  }

  getCategories(): CapabilityCategory[] {
    return Array.from(this.categories.keys());
  }

  // Capability validation
  validateCapability(name: string, value: any): CapabilityValidationResult {
    const definition = this.getCapability(name);
    if (!definition) {
      return {
        valid: false,
        errors: [`Unknown capability: ${name}`],
        warnings: [],
      };
    }

    return this.validateValue(value, definition);
  }

  validateCapabilities(capabilities: Record<string, any>): CapabilityValidationResult {
    const allErrors: string[] = [];
    const allWarnings: string[] = [];

    for (const [capabilityName, value] of Object.entries(capabilities)) {
      const result = this.validateCapability(capabilityName, value);
      allErrors.push(...result.errors);
      allWarnings.push(...result.warnings);
    }

    return {
      valid: allErrors.length === 0,
      errors: allErrors,
      warnings: allWarnings,
    };
  }

  // Capability relationships
  addDependency(capability: string, dependsOn: string): void {
    if (!this.dependencies.has(capability)) {
      this.dependencies.set(capability, new Set());
    }
    this.dependencies.get(capability)!.add(dependsOn);
  }

  getDependencies(capability: string): string[] {
    const deps = this.dependencies.get(capability);
    return deps ? Array.from(deps) : [];
  }

  getDependents(capability: string): string[] {
    const dependents: string[] = [];

    for (const [cap, deps] of this.dependencies) {
      if (deps.has(capability)) {
        dependents.push(cap);
      }
    }

    return dependents;
  }

  resolveDependencies(capability: string): string[] {
    const resolved: string[] = [];
    const visited = new Set<string>();

    this.resolveDependenciesRecursive(capability, resolved, visited);
    return resolved;
  }

  // Capability normalization
  normalizeCapabilityName(name: string): string {
    // Convert to lowercase and replace special characters
    const normalized = name
      .toLowerCase()
      .replace(/[^a-z0-9_]/g, '_')
      .replace(/_+/g, '_')
      .replace(/^_|_$/g, '');

    // Check if it's an alias
    const canonicalName = this.aliases.get(normalized);
    return canonicalName || normalized;
  }

  // Capability search and filtering
  searchCapabilities(query: CapabilitySearchQuery): CapabilityDefinition[] {
    let capabilities = Array.from(this.capabilities.values());

    // Filter by category
    if (query.categories && query.categories.length > 0) {
      capabilities = capabilities.filter((cap) => query.categories!.includes(cap.category));
    }

    // Filter by data type
    if (query.dataTypes && query.dataTypes.length > 0) {
      capabilities = capabilities.filter((cap) => query.dataTypes!.includes(cap.dataType));
    }

    // Filter by deprecated status
    if (query.includeDeprecated === false) {
      capabilities = capabilities.filter((cap) => !cap.deprecated);
    }

    // Text search
    if (query.searchTerm) {
      const searchTerm = query.searchTerm.toLowerCase();
      capabilities = capabilities.filter(
        (cap) =>
          cap.name.toLowerCase().includes(searchTerm) ||
          cap.description.toLowerCase().includes(searchTerm) ||
          (cap.aliases && cap.aliases.some((alias) => alias.toLowerCase().includes(searchTerm)))
      );
    }

    // Filter by required status
    if (query.requiredOnly) {
      capabilities = capabilities.filter((cap) => cap.required);
    }

    return capabilities;
  }

  // Export and import
  exportCapabilities(): string {
    const exportData = {
      version: '1.0',
      timestamp: new Date().toISOString(),
      capabilities: Array.from(this.capabilities.values()),
      categories: Object.fromEntries(
        Array.from(this.categories.entries()).map(([cat, caps]) => [cat, Array.from(caps)])
      ),
      aliases: Object.fromEntries(this.aliases),
      dependencies: Object.fromEntries(
        Array.from(this.dependencies.entries()).map(([cap, deps]) => [cap, Array.from(deps)])
      ),
    };

    return JSON.stringify(exportData, null, 2);
  }

  importCapabilities(data: string): void {
    try {
      const importData = JSON.parse(data);

      if (importData.capabilities) {
        for (const capability of importData.capabilities) {
          this.registerCapability(capability);
        }
      }

      if (importData.aliases) {
        for (const [alias, canonical] of Object.entries(importData.aliases)) {
          this.aliases.set(alias, canonical);
        }
      }

      if (importData.dependencies) {
        for (const [capability, deps] of Object.entries(importData.dependencies)) {
          for (const dep of deps as string[]) {
            this.addDependency(capability, dep);
          }
        }
      }
    } catch (error) {
      throw new Error(`Failed to import capabilities: ${error.message}`);
    }
  }

  // Private helper methods
  private initializeStandardCapabilities(): void {
    // Core capabilities
    this.registerCapability({
      name: 'text_generation',
      category: CapabilityCategory.CORE,
      description: 'Ability to generate text responses',
      dataType: CapabilityDataType.OBJECT,
      required: true,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          maxTokens: { type: 'number', minimum: 1 },
          streaming: { type: 'boolean' },
          temperature: { type: 'number', minimum: 0, maximum: 2 },
        },
        required: ['supported'],
      },
      examples: [{ supported: true, maxTokens: 4096, streaming: true, temperature: 0.7 }],
    });

    this.registerCapability({
      name: 'function_calling',
      category: CapabilityCategory.CORE,
      description: 'Ability to call external functions/tools',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          maxFunctions: { type: 'number', minimum: 1 },
          parallelCalls: { type: 'boolean' },
          streaming: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    this.registerCapability({
      name: 'vision',
      category: CapabilityCategory.INPUT,
      description: 'Ability to process and understand images',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          maxImageSize: { type: 'number', minimum: 1 },
          supportedFormats: { type: 'array', items: { type: 'string' } },
          maxImages: { type: 'number', minimum: 1 },
        },
        required: ['supported'],
      },
    });

    this.registerCapability({
      name: 'audio_processing',
      category: CapabilityCategory.INPUT,
      description: 'Ability to process and understand audio',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          maxAudioLength: { type: 'number', minimum: 1 },
          supportedFormats: { type: 'array', items: { type: 'string' } },
          transcription: { type: 'boolean' },
          generation: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    this.registerCapability({
      name: 'embedding',
      category: CapabilityCategory.OUTPUT,
      description: 'Ability to generate text embeddings',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          dimensions: { type: 'number', minimum: 1 },
          maxTokens: { type: 'number', minimum: 1 },
          similarity: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    this.registerCapability({
      name: 'code_generation',
      category: CapabilityCategory.SPECIALIZED,
      description: 'Specialized ability to generate and understand code',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          languages: { type: 'array', items: { type: 'string' } },
          maxTokens: { type: 'number', minimum: 1 },
          syntaxHighlighting: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    this.registerCapability({
      name: 'reasoning',
      category: CapabilityCategory.SPECIALIZED,
      description: 'Advanced reasoning and analytical capabilities',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          depth: { type: 'number', minimum: 1, maximum: 10 },
          chainOfThought: { type: 'boolean' },
          multiStep: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    // Performance capabilities
    this.registerCapability({
      name: 'performance',
      category: CapabilityCategory.PERFORMANCE,
      description: 'Performance characteristics and limits',
      dataType: CapabilityDataType.OBJECT,
      required: true,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          maxTokens: { type: 'number', minimum: 1 },
          inputCostPer1K: { type: 'number', minimum: 0 },
          outputCostPer1K: { type: 'number', minimum: 0 },
          requestsPerMinute: { type: 'number', minimum: 1 },
          averageLatency: { type: 'number', minimum: 0 },
        },
        required: ['maxTokens'],
      },
    });

    // Security capabilities
    this.registerCapability({
      name: 'content_filtering',
      category: CapabilityCategory.SECURITY,
      description: 'Content safety and filtering capabilities',
      dataType: CapabilityDataType.OBJECT,
      required: false,
      version: '1.0',
      validation: {
        type: 'object',
        properties: {
          supported: { type: 'boolean' },
          categories: { type: 'array', items: { type: 'string' } },
          customizable: { type: 'boolean' },
          strictMode: { type: 'boolean' },
        },
        required: ['supported'],
      },
    });

    // Add aliases for common variations
    this.aliases.set('text', 'text_generation');
    this.aliases.set('chat', 'text_generation');
    this.aliases.set('completion', 'text_generation');
    this.aliases.set('tools', 'function_calling');
    this.aliases.set('image', 'vision');
    this.aliases.set('multimodal', 'vision');
    this.aliases.set('vectors', 'embedding');

    // Add dependencies
    this.addDependency('function_calling', 'text_generation');
    this.addDependency('code_generation', 'text_generation');
    this.addDependency('reasoning', 'text_generation');
  }

  private validateCapabilityDefinition(definition: CapabilityDefinition): void {
    if (!definition.name || definition.name.trim() === '') {
      throw new Error('Capability name is required');
    }

    if (!definition.description || definition.description.trim() === '') {
      throw new Error('Capability description is required');
    }

    if (!Object.values(CapabilityCategory).includes(definition.category)) {
      throw new Error(`Invalid category: ${definition.category}`);
    }

    if (!Object.values(CapabilityDataType).includes(definition.dataType)) {
      throw new Error(`Invalid data type: ${definition.dataType}`);
    }

    if (this.capabilities.has(definition.name)) {
      throw new Error(`Capability '${definition.name}' is already registered`);
    }
  }

  private validateValue(value: any, definition: CapabilityDefinition): CapabilityValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (definition.validation) {
      const validation = definition.validation;

      // Type validation
      if (validation.type && typeof value !== validation.type) {
        errors.push(`Expected ${validation.type}, got ${typeof value}`);
      }

      // Property validation for objects
      if (validation.type === 'object' && validation.properties && typeof value === 'object') {
        for (const [propName, propValidation] of Object.entries(validation.properties)) {
          if (propValidation.required && !(propName in value)) {
            errors.push(`Required property '${propName}' is missing`);
          }

          if (propName in value) {
            const propValue = value[propName];
            const propErrors = this.validateProperty(propValue, propValidation, propName);
            errors.push(...propErrors);
          }
        }
      }

      // Array validation
      if (validation.type === 'array' && validation.items && Array.isArray(value)) {
        for (let i = 0; i < value.length; i++) {
          const itemErrors = this.validateProperty(value[i], validation.items, `item[${i}]`);
          errors.push(...itemErrors);
        }
      }

      // Enum validation
      if (validation.enum && !validation.enum.includes(value)) {
        errors.push(`Value must be one of: ${validation.enum.join(', ')}`);
      }

      // Range validation
      if (typeof value === 'number') {
        if (validation.minimum !== undefined && value < validation.minimum) {
          errors.push(`Value must be >= ${validation.minimum}`);
        }
        if (validation.maximum !== undefined && value > validation.maximum) {
          errors.push(`Value must be <= ${validation.maximum}`);
        }
      }

      // String validation
      if (typeof value === 'string') {
        if (validation.minLength !== undefined && value.length < validation.minLength) {
          errors.push(`String length must be >= ${validation.minLength}`);
        }
        if (validation.maxLength !== undefined && value.length > validation.maxLength) {
          errors.push(`String length must be <= ${validation.maxLength}`);
        }
        if (validation.pattern && !new RegExp(validation.pattern).test(value)) {
          errors.push(`String does not match required pattern`);
        }
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  private validateProperty(value: any, validation: any, propertyName: string): string[] {
    const errors: string[] = [];

    if (validation.type && typeof value !== validation.type) {
      errors.push(`Property '${propertyName}' expected ${validation.type}, got ${typeof value}`);
    }

    if (typeof value === 'number') {
      if (validation.minimum !== undefined && value < validation.minimum) {
        errors.push(`Property '${propertyName}' must be >= ${validation.minimum}`);
      }
      if (validation.maximum !== undefined && value > validation.maximum) {
        errors.push(`Property '${propertyName}' must be <= ${validation.maximum}`);
      }
    }

    if (typeof value === 'string') {
      if (validation.minLength !== undefined && value.length < validation.minLength) {
        errors.push(`Property '${propertyName}' length must be >= ${validation.minLength}`);
      }
      if (validation.maxLength !== undefined && value.length > validation.maxLength) {
        errors.push(`Property '${propertyName}' length must be <= ${validation.maxLength}`);
      }
    }

    return errors;
  }

  private resolveDependenciesRecursive(
    capability: string,
    resolved: string[],
    visited: Set<string>
  ): void {
    if (visited.has(capability)) {
      return; // Circular dependency
    }

    visited.add(capability);
    const dependencies = this.dependencies.get(capability);

    if (dependencies) {
      for (const dep of dependencies) {
        this.resolveDependenciesRecursive(dep, resolved, visited);
      }
    }

    resolved.push(capability);
  }
}

// Supporting interfaces
export interface CapabilitySearchQuery {
  categories?: CapabilityCategory[];
  dataTypes?: CapabilityDataType[];
  includeDeprecated?: boolean;
  requiredOnly?: boolean;
  searchTerm?: string;
  limit?: number;
  offset?: number;
}

export interface CapabilityValidationResult {
  valid: boolean;
  errors: string[];
  warnings: string[];
}
```

### Subtask 4.2: Implement Provider-Specific Capability Mappers

**Description**: Create capability mappers for major AI providers (OpenAI, Anthropic, Google, etc.) that normalize provider-specific capabilities to standard format.

**Implementation Details**:

1. **Create Base Provider Mapper**:

```typescript
// packages/providers/src/capabilities/base-provider-mapper.ts
import {
  IProviderCapabilityMapper,
  ModelCapabilities,
  ModelCapability,
  ProviderCapability,
} from '../interfaces/capability-mapper.interface';

export abstract class BaseProviderCapabilityMapper implements IProviderCapabilityMapper {
  protected capabilityRegistry: CapabilityRegistry;

  constructor(capabilityRegistry: CapabilityRegistry) {
    this.capabilityRegistry = capabilityRegistry;
  }

  abstract providerType: string;
  abstract version: string;

  abstract mapCapabilities(providerModel: any): ModelCapabilities;
  abstract mapCapability(providerCapability: ProviderCapability): ModelCapability | null;
  abstract getSupportedCapabilities(): string[];

  // Common helper methods
  protected mapTextGeneration(providerModel: any): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('text_generation');
    if (!capabilityDef) return null;

    const supported = this.isTextGenerationSupported(providerModel);
    if (!supported) return null;

    return {
      name: 'text_generation',
      displayName: 'Text Generation',
      description: capabilityDef.description,
      supported: true,
      value: {
        supported: true,
        maxTokens: this.getMaxTokens(providerModel),
        streaming: this.supportsStreaming(providerModel),
        temperature: this.getTemperatureRange(providerModel),
      },
      metadata: {
        providerSpecific: this.getProviderSpecificTextGenData(providerModel),
      },
    };
  }

  protected mapFunctionCalling(providerModel: any): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('function_calling');
    if (!capabilityDef) return null;

    const supported = this.isFunctionCallingSupported(providerModel);
    if (!supported) return null;

    return {
      name: 'function_calling',
      displayName: 'Function Calling',
      description: capabilityDef.description,
      supported: true,
      value: {
        supported: true,
        maxFunctions: this.getMaxFunctions(providerModel),
        parallelCalls: this.supportsParallelFunctionCalls(providerModel),
        streaming: this.supportsFunctionStreaming(providerModel),
      },
      metadata: {
        providerSpecific: this.getProviderSpecificFunctionCallingData(providerModel),
      },
    };
  }

  protected mapVision(providerModel: any): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('vision');
    if (!capabilityDef) return null;

    const supported = this.isVisionSupported(providerModel);
    if (!supported) return null;

    return {
      name: 'vision',
      displayName: 'Vision',
      description: capabilityDef.description,
      supported: true,
      value: {
        supported: true,
        maxImageSize: this.getMaxImageSize(providerModel),
        supportedFormats: this.getSupportedImageFormats(providerModel),
        maxImages: this.getMaxImages(providerModel),
      },
      metadata: {
        providerSpecific: this.getProviderSpecificVisionData(providerModel),
      },
    };
  }

  protected mapPerformance(providerModel: any): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('performance');
    if (!capabilityDef) return null;

    return {
      name: 'performance',
      displayName: 'Performance',
      description: capabilityDef.description,
      supported: true,
      value: {
        maxTokens: this.getMaxTokens(providerModel),
        inputCostPer1K: this.getInputCostPer1K(providerModel),
        outputCostPer1K: this.getOutputCostPer1K(providerModel),
        requestsPerMinute: this.getRequestsPerMinute(providerModel),
        averageLatency: this.getAverageLatency(providerModel),
      },
      metadata: {
        providerSpecific: this.getProviderSpecificPerformanceData(providerModel),
      },
    };
  }

  // Abstract methods for provider-specific implementations
  protected abstract isTextGenerationSupported(providerModel: any): boolean;
  protected abstract isFunctionCallingSupported(providerModel: any): boolean;
  protected abstract isVisionSupported(providerModel: any): boolean;

  protected abstract getMaxTokens(providerModel: any): number;
  protected abstract supportsStreaming(providerModel: any): boolean;
  protected abstract getTemperatureRange(providerModel: any): any;
  protected abstract getMaxFunctions(providerModel: any): number;
  protected abstract supportsParallelFunctionCalls(providerModel: any): boolean;
  protected abstract supportsFunctionStreaming(providerModel: any): boolean;
  protected abstract getMaxImageSize(providerModel: any): number;
  protected abstract getSupportedImageFormats(providerModel: any): string[];
  protected abstract getMaxImages(providerModel: any): number;
  protected abstract getInputCostPer1K(providerModel: any): number;
  protected abstract getOutputCostPer1K(providerModel: any): number;
  protected abstract getRequestsPerMinute(providerModel: any): number;
  protected abstract getAverageLatency(providerModel: any): number;

  protected abstract getProviderSpecificTextGenData(providerModel: any): any;
  protected abstract getProviderSpecificFunctionCallingData(providerModel: any): any;
  protected abstract getProviderSpecificVisionData(providerModel: any): any;
  protected abstract getProviderSpecificPerformanceData(providerModel: any): any;
}
```

2. **Create OpenAI Capability Mapper**:

```typescript
// packages/providers/src/capabilities/openai-capability-mapper.ts
import { BaseProviderCapabilityMapper } from './base-provider-mapper';
import {
  ModelCapabilities,
  ModelCapability,
  ProviderCapability,
} from '../interfaces/capability-mapper.interface';
import { CapabilityRegistry } from './capability-registry';

export class OpenAICapabilityMapper extends BaseProviderCapabilityMapper {
  providerType = 'openai';
  version = '1.0';

  constructor(capabilityRegistry: CapabilityRegistry) {
    super(capabilityRegistry);
  }

  mapCapabilities(providerModel: any): ModelCapabilities {
    const capabilities: ModelCapabilities = {};

    // Map text generation
    const textGen = this.mapTextGeneration(providerModel);
    if (textGen) {
      capabilities.text_generation = textGen.value;
    }

    // Map function calling
    const functionCalling = this.mapFunctionCalling(providerModel);
    if (functionCalling) {
      capabilities.function_calling = functionCalling.value;
    }

    // Map vision
    const vision = this.mapVision(providerModel);
    if (vision) {
      capabilities.vision = vision.value;
    }

    // Map performance
    const performance = this.mapPerformance(providerModel);
    if (performance) {
      capabilities.performance = performance.value;
    }

    // Map other OpenAI-specific capabilities
    capabilities.embedding = this.mapEmbedding(providerModel);
    capabilities.fine_tuning = this.mapFineTuning(providerModel);
    capabilities.batch_processing = this.mapBatchProcessing(providerModel);

    return capabilities;
  }

  mapCapability(providerCapability: ProviderCapability): ModelCapability | null {
    // Map OpenAI-specific capabilities to standard ones
    switch (providerCapability.name) {
      case 'text_completion':
      case 'chat_completion':
        return this.mapTextGeneration({ supports: true, ...providerCapability });

      case 'function_call':
      case 'tools':
        return this.mapFunctionCalling({ supports: true, ...providerCapability });

      case 'vision':
      case 'image_input':
        return this.mapVision({ supports: true, ...providerCapability });

      case 'embedding':
        return this.mapEmbeddingCapability(providerCapability);

      default:
        return null;
    }
  }

  getSupportedCapabilities(): string[] {
    return [
      'text_generation',
      'function_calling',
      'vision',
      'embedding',
      'fine_tuning',
      'batch_processing',
      'performance',
    ];
  }

  // OpenAI-specific implementations
  protected isTextGenerationSupported(providerModel: any): boolean {
    return (
      providerModel.object === 'chat.completion' ||
      providerModel.object === 'text.completion' ||
      providerModel.type?.includes('gpt')
    );
  }

  protected isFunctionCallingSupported(providerModel: any): boolean {
    return (
      providerModel.function_call === true ||
      providerModel.tools === true ||
      (providerModel.type &&
        providerModel.type.includes('gpt') &&
        ['gpt-4', 'gpt-4-turbo', 'gpt-3.5-turbo'].some((model) =>
          providerModel.type.includes(model)
        ))
    );
  }

  protected isVisionSupported(providerModel: any): boolean {
    return (
      providerModel.vision === true ||
      providerModel.type?.includes('vision') ||
      providerModel.type?.includes('gpt-4-vision') ||
      providerModel.type?.includes('gpt-4o')
    );
  }

  protected getMaxTokens(providerModel: any): number {
    // OpenAI model token limits
    const tokenLimits: Record<string, number> = {
      'gpt-3.5-turbo': 4096,
      'gpt-3.5-turbo-16k': 16384,
      'gpt-4': 8192,
      'gpt-4-32k': 32768,
      'gpt-4-turbo': 128000,
      'gpt-4-turbo-preview': 128000,
      'gpt-4o': 128000,
      'gpt-4o-mini': 128000,
    };

    return (
      providerModel.max_tokens ||
      tokenLimits[providerModel.type] ||
      tokenLimits[providerModel.id] ||
      4096
    );
  }

  protected supportsStreaming(providerModel: any): boolean {
    return providerModel.streaming !== false; // Default to true for most models
  }

  protected getTemperatureRange(providerModel: any): any {
    return {
      min: 0,
      max: 2,
      default: 1,
    };
  }

  protected getMaxFunctions(providerModel: any): number {
    return 128; // OpenAI function calling limit
  }

  protected supportsParallelFunctionCalls(providerModel: any): boolean {
    return (
      providerModel.parallel_tool_calls === true ||
      (providerModel.type && providerModel.type.includes('gpt-4'))
    );
  }

  protected supportsFunctionStreaming(providerModel: any): boolean {
    return (
      providerModel.tool_choice === 'required' ||
      (providerModel.type && providerModel.type.includes('gpt-4'))
    );
  }

  protected getMaxImageSize(providerModel: any): number {
    // OpenAI vision image size limits
    return 20 * 1024 * 1024; // 20MB
  }

  protected getSupportedImageFormats(providerModel: any): string[] {
    return ['png', 'jpeg', 'jpg', 'webp', 'gif'];
  }

  protected getMaxImages(providerModel: any): number {
    return providerModel.type?.includes('gpt-4o') ? 1 : 5; // GPT-4o supports 1 image, others support up to 5
  }

  protected getInputCostPer1K(providerModel: any): number {
    // OpenAI pricing (example rates)
    const pricing: Record<string, number> = {
      'gpt-3.5-turbo': 0.0015,
      'gpt-3.5-turbo-16k': 0.003,
      'gpt-4': 0.03,
      'gpt-4-32k': 0.06,
      'gpt-4-turbo': 0.01,
      'gpt-4-turbo-preview': 0.01,
      'gpt-4o': 0.005,
      'gpt-4o-mini': 0.00015,
    };

    return (
      providerModel.input_cost_per_1k ||
      pricing[providerModel.type] ||
      pricing[providerModel.id] ||
      0.01
    );
  }

  protected getOutputCostPer1K(providerModel: any): number {
    // OpenAI pricing (example rates)
    const pricing: Record<string, number> = {
      'gpt-3.5-turbo': 0.002,
      'gpt-3.5-turbo-16k': 0.004,
      'gpt-4': 0.06,
      'gpt-4-32k': 0.12,
      'gpt-4-turbo': 0.03,
      'gpt-4-turbo-preview': 0.03,
      'gpt-4o': 0.015,
      'gpt-4o-mini': 0.0006,
    };

    return (
      providerModel.output_cost_per_1k ||
      pricing[providerModel.type] ||
      pricing[providerModel.id] ||
      0.02
    );
  }

  protected getRequestsPerMinute(providerModel: any): number {
    // OpenAI rate limits (example)
    return 3500; // Default for paid accounts
  }

  protected getAverageLatency(providerModel: any): number {
    // OpenAI typical latencies (example)
    const latencies: Record<string, number> = {
      'gpt-3.5-turbo': 800,
      'gpt-4': 2500,
      'gpt-4-turbo': 1200,
      'gpt-4o': 600,
      'gpt-4o-mini': 400,
    };

    return latencies[providerModel.type] || latencies[providerModel.id] || 1000;
  }

  // OpenAI-specific capability mappers
  private mapEmbedding(providerModel: any): any {
    const supported =
      providerModel.object === 'embedding' ||
      providerModel.type?.includes('text-embedding') ||
      providerModel.type?.includes('text-embedding-');

    if (!supported) return undefined;

    return {
      supported: true,
      dimensions: this.getEmbeddingDimensions(providerModel),
      maxTokens: this.getMaxTokens(providerModel),
      similarity: providerModel.type?.includes('search') || false,
    };
  }

  private mapFineTuning(providerModel: any): any {
    const supported =
      providerModel.fine_tuned === true || providerModel.type?.includes('fine-tuned');

    if (!supported) return undefined;

    return {
      supported: true,
      trainable: providerModel.trainable || false,
      customData: providerModel.training_data ? true : false,
    };
  }

  private mapBatchProcessing(providerModel: any): any {
    return {
      supported: providerModel.batch_processing === true,
      maxBatchSize: providerModel.max_batch_size || 20,
      batchWindow: providerModel.batch_window || '24h',
    };
  }

  private mapEmbeddingCapability(providerCapability: ProviderCapability): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('embedding');
    if (!capabilityDef) return null;

    return {
      name: 'embedding',
      displayName: 'Text Embedding',
      description: capabilityDef.description,
      supported: true,
      value: {
        supported: true,
        dimensions: providerCapability.dimensions || 1536,
        maxTokens: providerCapability.max_tokens || 8192,
        similarity: providerCapability.similarity || false,
      },
      metadata: {
        providerSpecific: providerCapability,
      },
    };
  }

  // Provider-specific data extraction
  protected getProviderSpecificTextGenData(providerModel: any): any {
    return {
      object: providerModel.object,
      type: providerModel.type,
      owned_by: providerModel.owned_by,
      permission: providerModel.permission,
    };
  }

  protected getProviderSpecificFunctionCallingData(providerModel: any): any {
    return {
      function_call: providerModel.function_call,
      tools: providerModel.tools,
      tool_choice: providerModel.tool_choice,
      parallel_tool_calls: providerModel.parallel_tool_calls,
    };
  }

  protected getProviderSpecificVisionData(providerModel: any): any {
    return {
      vision: providerModel.vision,
      image_detail: providerModel.image_detail,
      max_image_pixels: providerModel.max_image_pixels,
    };
  }

  protected getProviderSpecificPerformanceData(providerModel: any): any {
    return {
      pricing: {
        input: providerModel.input_cost_per_1k,
        output: providerModel.output_cost_per_1k,
      },
      rate_limits: {
        requests_per_minute: providerModel.requests_per_minute,
        tokens_per_minute: providerModel.tokens_per_minute,
      },
    };
  }
}
```

3. **Create Anthropic Capability Mapper**:

```typescript
// packages/providers/src/capabilities/anthropic-capability-mapper.ts
import { BaseProviderCapabilityMapper } from './base-provider-mapper';
import {
  ModelCapabilities,
  ModelCapability,
  ProviderCapability,
} from '../interfaces/capability-mapper.interface';
import { CapabilityRegistry } from './capability-registry';

export class AnthropicCapabilityMapper extends BaseProviderCapabilityMapper {
  providerType = 'anthropic';
  version = '1.0';

  constructor(capabilityRegistry: CapabilityRegistry) {
    super(capabilityRegistry);
  }

  mapCapabilities(providerModel: any): ModelCapabilities {
    const capabilities: ModelCapabilities = {};

    // Map text generation
    const textGen = this.mapTextGeneration(providerModel);
    if (textGen) {
      capabilities.text_generation = textGen.value;
    }

    // Map vision
    const vision = this.mapVision(providerModel);
    if (vision) {
      capabilities.vision = vision.value;
    }

    // Map performance
    const performance = this.mapPerformance(providerModel);
    if (performance) {
      capabilities.performance = performance.value;
    }

    // Map Anthropic-specific capabilities
    capabilities.reasoning = this.mapReasoning(providerModel);
    capabilities.long_context = this.mapLongContext(providerModel);
    capabilities.tool_use = this.mapToolUse(providerModel);

    return capabilities;
  }

  mapCapability(providerCapability: ProviderCapability): ModelCapability | null {
    switch (providerCapability.name) {
      case 'message':
      case 'text':
        return this.mapTextGeneration({ supports: true, ...providerCapability });

      case 'vision':
      case 'image':
        return this.mapVision({ supports: true, ...providerCapability });

      case 'tool_use':
      case 'tools':
        return this.mapToolUseCapability(providerCapability);

      default:
        return null;
    }
  }

  getSupportedCapabilities(): string[] {
    return ['text_generation', 'vision', 'reasoning', 'long_context', 'tool_use', 'performance'];
  }

  // Anthropic-specific implementations
  protected isTextGenerationSupported(providerModel: any): boolean {
    return providerModel.type?.includes('claude') || providerModel.model?.includes('claude');
  }

  protected isFunctionCallingSupported(providerModel: any): boolean {
    // Anthropic uses "tool_use" instead of "function_calling"
    return false;
  }

  protected isVisionSupported(providerModel: any): boolean {
    return (
      providerModel.vision === true ||
      providerModel.type?.includes('claude-3-vision') ||
      providerModel.type?.includes('claude-3-5-vision')
    );
  }

  protected getMaxTokens(providerModel: any): number {
    // Anthropic model token limits
    const tokenLimits: Record<string, number> = {
      'claude-3-opus': 200000,
      'claude-3-sonnet': 200000,
      'claude-3-haiku': 200000,
      'claude-3-5-sonnet': 200000,
      'claude-3-5-haiku': 200000,
    };

    return (
      providerModel.max_tokens ||
      tokenLimits[providerModel.type] ||
      tokenLimits[providerModel.model] ||
      200000
    );
  }

  protected supportsStreaming(providerModel: any): boolean {
    return true; // Anthropic models support streaming
  }

  protected getTemperatureRange(providerModel: any): any {
    return {
      min: 0,
      max: 1,
      default: 0.7,
    };
  }

  protected getMaxFunctions(providerModel: any): number {
    return 0; // Anthropic doesn't use traditional function calling
  }

  protected supportsParallelFunctionCalls(providerModel: any): boolean {
    return false;
  }

  protected supportsFunctionStreaming(providerModel: any): boolean {
    return false;
  }

  protected getMaxImageSize(providerModel: any): number {
    return 5 * 1024 * 1024; // 5MB for Anthropic
  }

  protected getSupportedImageFormats(providerModel: any): string[] {
    return ['png', 'jpeg', 'jpg', 'webp', 'gif'];
  }

  protected getMaxImages(providerModel: any): number {
    return providerModel.type?.includes('claude-3-5') ? 1 : 20;
  }

  protected getInputCostPer1K(providerModel: any): number {
    // Anthropic pricing (example rates)
    const pricing: Record<string, number> = {
      'claude-3-opus': 0.015,
      'claude-3-sonnet': 0.003,
      'claude-3-haiku': 0.00025,
      'claude-3-5-sonnet': 0.003,
      'claude-3-5-haiku': 0.0008,
    };

    return (
      providerModel.input_cost_per_1k ||
      pricing[providerModel.type] ||
      pricing[providerModel.model] ||
      0.003
    );
  }

  protected getOutputCostPer1K(providerModel: any): number {
    // Anthropic pricing (example rates)
    const pricing: Record<string, number> = {
      'claude-3-opus': 0.075,
      'claude-3-sonnet': 0.015,
      'claude-3-haiku': 0.00125,
      'claude-3-5-sonnet': 0.015,
      'claude-3-5-haiku': 0.004,
    };

    return (
      providerModel.output_cost_per_1k ||
      pricing[providerModel.type] ||
      pricing[providerModel.model] ||
      0.015
    );
  }

  protected getRequestsPerMinute(providerModel: any): number {
    return 1000; // Anthropic typical rate limit
  }

  protected getAverageLatency(providerModel: any): number {
    const latencies: Record<string, number> = {
      'claude-3-opus': 3000,
      'claude-3-sonnet': 1500,
      'claude-3-haiku': 800,
      'claude-3-5-sonnet': 1200,
      'claude-3-5-haiku': 600,
    };

    return latencies[providerModel.type] || latencies[providerModel.model] || 1000;
  }

  // Anthropic-specific capability mappers
  private mapReasoning(providerModel: any): any {
    const supported =
      providerModel.type?.includes('claude-3') || providerModel.model?.includes('claude-3');

    if (!supported) return undefined;

    return {
      supported: true,
      depth: this.getReasoningDepth(providerModel),
      chainOfThought: true,
      multiStep: true,
    };
  }

  private mapLongContext(providerModel: any): any {
    const supported =
      providerModel.type?.includes('claude-3') || providerModel.model?.includes('claude-3');

    if (!supported) return undefined;

    return {
      supported: true,
      maxContextTokens: this.getMaxTokens(providerModel),
      slidingWindow: providerModel.type?.includes('claude-3-5'),
    };
  }

  private mapToolUse(providerModel: any): any {
    const supported = providerModel.tool_use === true || providerModel.type?.includes('claude-3');

    if (!supported) return undefined;

    return {
      supported: true,
      maxTools: this.getMaxTools(providerModel),
      parallelToolUse: this.supportsParallelToolUse(providerModel),
      streaming: true,
    };
  }

  private mapToolUseCapability(providerCapability: ProviderCapability): ModelCapability | null {
    const capabilityDef = this.capabilityRegistry.getCapability('tool_use');
    if (!capabilityDef) return null;

    return {
      name: 'tool_use',
      displayName: 'Tool Use',
      description: 'Anthropic tool use capability',
      supported: true,
      value: {
        supported: true,
        maxTools: providerCapability.max_tools || 50,
        parallelToolUse: providerCapability.parallel_tool_use || false,
        streaming: providerCapability.streaming || true,
      },
      metadata: {
        providerSpecific: providerCapability,
      },
    };
  }

  private getReasoningDepth(providerModel: any): number {
    const depths: Record<string, number> = {
      'claude-3-opus': 10,
      'claude-3-sonnet': 7,
      'claude-3-haiku': 5,
      'claude-3-5-sonnet': 8,
      'claude-3-5-haiku': 6,
    };

    return depths[providerModel.type] || depths[providerModel.model] || 5;
  }

  private getMaxTools(providerModel: any): number {
    return providerModel.max_tools || 50;
  }

  private supportsParallelToolUse(providerModel: any): boolean {
    return (
      providerModel.parallel_tool_use === true ||
      (providerModel.type && providerModel.type.includes('claude-3-5'))
    );
  }

  // Provider-specific data extraction
  protected getProviderSpecificTextGenData(providerModel: any): any {
    return {
      model: providerModel.model,
      type: providerModel.type,
      thinking: providerModel.thinking,
    };
  }

  protected getProviderSpecificFunctionCallingData(providerModel: any): any {
    return {}; // Anthropic doesn't use traditional function calling
  }

  protected getProviderSpecificVisionData(providerModel: any): any {
    return {
      vision: providerModel.vision,
      image_processing: providerModel.image_processing,
    };
  }

  protected getProviderSpecificPerformanceData(providerModel: any): any {
    return {
      pricing: {
        input: providerModel.input_cost_per_1k,
        output: providerModel.output_cost_per_1k,
      },
      rate_limits: {
        requests_per_minute: providerModel.requests_per_minute,
        tokens_per_minute: providerModel.tokens_per_minute,
      },
    };
  }
}
```

### Subtask 4.3: Add Capability Validation and Normalization

**Description**: Implement comprehensive capability validation, normalization, and conflict resolution with detailed error reporting.

**Implementation Details**:

1. **Create Capability Validator**:

```typescript
// packages/providers/src/capabilities/capability-validator.ts
import {
  IModelCapabilityMapper,
  ModelCapabilities,
  CapabilityValidationResult,
  CapabilityValidationError,
  CapabilityValidationWarning,
} from '../interfaces/capability-mapper.interface';

import {
  CapabilityRegistry,
  CapabilityValidationResult as RegistryValidationResult,
} from './capability-registry';

export interface ValidationRule {
  name: string;
  description: string;
  severity: 'error' | 'warning' | 'info';
  validate: (capabilities: ModelCapabilities, context: ValidationContext) => ValidationResult;
}

export interface ValidationContext {
  providerType: string;
  modelName: string;
  operation: 'discovery' | 'update' | 'comparison';
  strictMode: boolean;
  customRules?: ValidationRule[];
}

export interface ValidationResult {
  valid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
  info: ValidationInfo[];
}

export interface ValidationError {
  code: string;
  message: string;
  capability: string;
  field?: string;
  value?: any;
  suggestion?: string;
}

export interface ValidationWarning {
  code: string;
  message: string;
  capability: string;
  field?: string;
  value?: any;
  recommendation?: string;
}

export interface ValidationInfo {
  code: string;
  message: string;
  capability: string;
  field?: string;
  value?: any;
}

export class CapabilityValidator {
  private registry: CapabilityRegistry;
  private validationRules: Map<string, ValidationRule> = new Map();
  private conflictResolvers: Map<string, ConflictResolver> = new Map();

  constructor(registry: CapabilityRegistry) {
    this.registry = registry;
    this.initializeDefaultRules();
    this.initializeConflictResolvers();
  }

  // Main validation methods
  validateCapabilities(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): CapabilityValidationResult {
    const allErrors: CapabilityValidationError[] = [];
    const allWarnings: CapabilityValidationWarning[] = [];

    // 1. Schema validation using registry
    const schemaResult = this.registry.validateCapabilities(capabilities);
    allErrors.push(...this.convertRegistryErrors(schemaResult.errors));
    allWarnings.push(...this.convertRegistryWarnings(schemaResult.warnings));

    // 2. Business logic validation
    const businessResult = this.validateBusinessLogic(capabilities, context);
    allErrors.push(...businessResult.errors);
    allWarnings.push(...businessResult.warnings);

    // 3. Custom validation rules
    const customResult = this.validateCustomRules(capabilities, context);
    allErrors.push(...customResult.errors);
    allWarnings.push(...customResult.warnings);

    // 4. Conflict detection and resolution
    const conflictResult = this.detectAndResolveConflicts(capabilities, context);
    allErrors.push(...conflictResult.errors);
    allWarnings.push(...conflictResult.warnings);

    // 5. Cross-capability validation
    const crossResult = this.validateCrossCapabilityDependencies(capabilities, context);
    allErrors.push(...crossResult.errors);
    allWarnings.push(...crossResult.warnings);

    // 6. Provider-specific validation
    const providerResult = this.validateProviderSpecificConstraints(capabilities, context);
    allErrors.push(...providerResult.errors);
    allWarnings.push(...providerResult.warnings);

    return {
      valid: allErrors.length === 0,
      errors: allErrors,
      warnings: allWarnings,
    };
  }

  normalizeCapabilities(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ModelCapabilities {
    let normalized = { ...capabilities };

    // Apply normalization rules
    normalized = this.normalizeCapabilityNames(normalized);
    normalized = this.normalizeCapabilityValues(normalized, context);
    normalized = this.normalizeCapabilityStructure(normalized);
    normalized = this.applyProviderSpecificNormalization(normalized, context);

    return normalized;
  }

  // Validation rule management
  addValidationRule(rule: ValidationRule): void {
    this.validationRules.set(rule.name, rule);
  }

  removeValidationRule(ruleName: string): void {
    this.validationRules.delete(ruleName);
  }

  getValidationRules(): ValidationRule[] {
    return Array.from(this.validationRules.values());
  }

  // Conflict resolution
  addConflictResolver(type: string, resolver: ConflictResolver): void {
    this.conflictResolvers.set(type, resolver);
  }

  resolveConflict(conflict: CapabilityConflict, context: ValidationContext): ConflictResolution {
    const resolver = this.conflictResolvers.get(conflict.type);
    if (resolver) {
      return resolver.resolve(conflict, context);
    }

    // Default resolution
    return this.defaultConflictResolver(conflict, context);
  }

  // Private validation methods
  private validateBusinessLogic(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Validate text generation constraints
    if (capabilities.text_generation) {
      const textGen = capabilities.text_generation;

      if (textGen.maxTokens && textGen.maxTokens <= 0) {
        errors.push({
          code: 'INVALID_MAX_TOKENS',
          message: 'Max tokens must be greater than 0',
          capability: 'text_generation',
          field: 'maxTokens',
          value: textGen.maxTokens,
          suggestion: 'Set a positive maxTokens value',
        });
      }

      if (textGen.temperature !== undefined) {
        if (
          typeof textGen.temperature !== 'number' ||
          textGen.temperature < 0 ||
          textGen.temperature > 2
        ) {
          errors.push({
            code: 'INVALID_TEMPERATURE',
            message: 'Temperature must be between 0 and 2',
            capability: 'text_generation',
            field: 'temperature',
            value: textGen.temperature,
          });
        }
      }

      if (textGen.streaming === true && !textGen.maxTokens) {
        warnings.push({
          code: 'STREAMING_WITHOUT_TOKEN_LIMIT',
          message: 'Streaming enabled but no maxTokens specified',
          capability: 'text_generation',
          field: 'streaming',
          recommendation: 'Consider setting maxTokens for better resource management',
        });
      }
    }

    // Validate function calling constraints
    if (capabilities.function_calling) {
      const funcCalling = capabilities.function_calling;

      if (funcCalling.maxFunctions && funcCalling.maxFunctions <= 0) {
        errors.push({
          code: 'INVALID_MAX_FUNCTIONS',
          message: 'Max functions must be greater than 0',
          capability: 'function_calling',
          field: 'maxFunctions',
          value: funcCalling.maxFunctions,
        });
      }

      if (funcCalling.parallelCalls === true && !funcCalling.maxFunctions) {
        warnings.push({
          code: 'PARALLEL_CALLS_WITHOUT_LIMIT',
          message: 'Parallel function calls enabled but no maxFunctions specified',
          capability: 'function_calling',
          recommendation: 'Set maxFunctions to prevent resource exhaustion',
        });
      }
    }

    // Validate vision constraints
    if (capabilities.vision) {
      const vision = capabilities.vision;

      if (vision.maxImageSize && vision.maxImageSize <= 0) {
        errors.push({
          code: 'INVALID_MAX_IMAGE_SIZE',
          message: 'Max image size must be greater than 0',
          capability: 'vision',
          field: 'maxImageSize',
          value: vision.maxImageSize,
        });
      }

      if (vision.maxImages && vision.maxImages <= 0) {
        errors.push({
          code: 'INVALID_MAX_IMAGES',
          message: 'Max images must be greater than 0',
          capability: 'vision',
          field: 'maxImages',
          value: vision.maxImages,
        });
      }

      if (vision.supportedFormats && Array.isArray(vision.supportedFormats)) {
        const validFormats = ['png', 'jpeg', 'jpg', 'webp', 'gif', 'bmp'];
        const invalidFormats = vision.supportedFormats.filter(
          (format) => !validFormats.includes(format.toLowerCase())
        );

        if (invalidFormats.length > 0) {
          warnings.push({
            code: 'UNSUPPORTED_IMAGE_FORMATS',
            message: `Potentially unsupported image formats: ${invalidFormats.join(', ')}`,
            capability: 'vision',
            field: 'supportedFormats',
            value: invalidFormats,
            recommendation: 'Verify these formats are actually supported by the provider',
          });
        }
      }
    }

    // Validate performance constraints
    if (capabilities.performance) {
      const performance = capabilities.performance;

      if (performance.inputCostPer1K && performance.inputCostPer1K < 0) {
        errors.push({
          code: 'INVALID_INPUT_COST',
          message: 'Input cost per 1K cannot be negative',
          capability: 'performance',
          field: 'inputCostPer1K',
          value: performance.inputCostPer1K,
        });
      }

      if (performance.outputCostPer1K && performance.outputCostPer1K < 0) {
        errors.push({
          code: 'INVALID_OUTPUT_COST',
          message: 'Output cost per 1K cannot be negative',
          capability: 'performance',
          field: 'outputCostPer1K',
          value: performance.outputCostPer1K,
        });
      }

      if (performance.requestsPerMinute && performance.requestsPerMinute <= 0) {
        errors.push({
          code: 'INVALID_REQUEST_RATE',
          message: 'Requests per minute must be greater than 0',
          capability: 'performance',
          field: 'requestsPerMinute',
          value: performance.requestsPerMinute,
        });
      }

      // Cost reasonableness check
      if (performance.inputCostPer1K && performance.outputCostPer1K) {
        const totalCost = performance.inputCostPer1K + performance.outputCostPer1K;
        if (totalCost > 100) {
          // $100 per 1K tokens is very expensive
          warnings.push({
            code: 'HIGH_COST_MODEL',
            message: `Very high cost per 1K tokens: $${totalCost}`,
            capability: 'performance',
            recommendation: 'Consider using a more cost-effective model',
          });
        }
      }
    }

    return { valid: errors.length === 0, errors, warnings, info: [] };
  }

  private validateCustomRules(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    if (context.customRules) {
      for (const rule of context.customRules) {
        const result = rule.validate(capabilities, context);
        errors.push(...result.errors);
        warnings.push(...result.warnings);
      }
    }

    return { valid: errors.length === 0, errors, warnings, info: [] };
  }

  private detectAndResolveConflicts(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Detect conflicts
    const conflicts = this.identifyConflicts(capabilities);

    for (const conflict of conflicts) {
      const resolution = this.resolveConflict(conflict, context);

      if (resolution.resolved) {
        // Apply resolution
        this.applyConflictResolution(capabilities, resolution);

        if (resolution.warnings) {
          warnings.push(...resolution.warnings);
        }
      } else {
        // Conflict couldn't be resolved
        errors.push({
          code: 'UNRESOLVABLE_CONFLICT',
          message: `Unresolvable conflict: ${conflict.description}`,
          capability: conflict.capabilities.join(', '),
          suggestion: resolution.suggestion,
        });
      }
    }

    return { valid: errors.length === 0, errors, warnings, info: [] };
  }

  private validateCrossCapabilityDependencies(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Check function calling dependency on text generation
    if (capabilities.function_calling && capabilities.function_calling.supported) {
      if (!capabilities.text_generation || !capabilities.text_generation.supported) {
        errors.push({
          code: 'MISSING_DEPENDENCY',
          message: 'Function calling requires text generation capability',
          capability: 'function_calling',
          suggestion: 'Enable text generation capability',
        });
      }
    }

    // Check vision dependency on text generation for multimodal
    if (capabilities.vision && capabilities.vision.supported) {
      if (!capabilities.text_generation || !capabilities.text_generation.supported) {
        warnings.push({
          code: 'RECOMMENDED_DEPENDENCY',
          message: 'Vision capability typically requires text generation for multimodal processing',
          capability: 'vision',
          recommendation: 'Consider enabling text generation for full multimodal support',
        });
      }
    }

    // Check token limit consistency
    const tokenLimits = [
      capabilities.text_generation?.maxTokens,
      capabilities.vision?.maxTokens,
      capabilities.performance?.maxTokens,
    ].filter((limit) => limit && limit > 0);

    if (tokenLimits.length > 1) {
      const maxToken = Math.max(...tokenLimits);
      const minToken = Math.min(...tokenLimits);

      if (maxToken / minToken > 10) {
        warnings.push({
          code: 'INCONSISTENT_TOKEN_LIMITS',
          message: `Large variance in token limits: ${minToken} to ${maxToken}`,
          capability: 'performance',
          recommendation: 'Verify token limits are correct for all capabilities',
        });
      }
    }

    return { valid: errors.length === 0, errors, warnings, info: [] };
  }

  private validateProviderSpecificConstraints(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Provider-specific validation logic would go here
    switch (context.providerType) {
      case 'openai':
        return this.validateOpenAIConstraints(capabilities, context);
      case 'anthropic':
        return this.validateAnthropicConstraints(capabilities, context);
      case 'google':
        return this.validateGoogleConstraints(capabilities, context);
      default:
        return { valid: true, errors: [], warnings: [], info: [] };
    }
  }

  // Normalization methods
  private normalizeCapabilityNames(capabilities: ModelCapabilities): ModelCapabilities {
    const normalized: ModelCapabilities = {};

    for (const [key, value] of Object.entries(capabilities)) {
      const normalizedName = this.registry.normalizeCapabilityName(key);
      normalized[normalizedName] = value;
    }

    return normalized;
  }

  private normalizeCapabilityValues(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ModelCapabilities {
    const normalized = { ...capabilities };

    for (const [capabilityName, capabilityValue] of Object.entries(normalized)) {
      if (capabilityValue && typeof capabilityValue === 'object') {
        normalized[capabilityName] = this.normalizeCapabilityValuesObject(
          capabilityValue as any,
          capabilityName,
          context
        );
      }
    }

    return normalized;
  }

  private normalizeCapabilityValuesObject(
    value: any,
    capabilityName: string,
    context: ValidationContext
  ): any {
    const normalized = { ...value };

    // Normalize boolean values
    for (const [key, val] of Object.entries(normalized)) {
      if (typeof val === 'string') {
        const lowerVal = val.toLowerCase();
        if (lowerVal === 'true' || lowerVal === 'yes' || lowerVal === '1') {
          normalized[key] = true;
        } else if (lowerVal === 'false' || lowerVal === 'no' || lowerVal === '0') {
          normalized[key] = false;
        }
      }
    }

    // Normalize numeric values
    for (const [key, val] of Object.entries(normalized)) {
      if (typeof val === 'string' && !isNaN(Number(val))) {
        normalized[key] = Number(val);
      }
    }

    return normalized;
  }

  private normalizeCapabilityStructure(capabilities: ModelCapabilities): ModelCapabilities {
    const normalized = { ...capabilities };

    // Ensure required fields are present
    for (const [capabilityName, capabilityValue] of Object.entries(normalized)) {
      if (capabilityValue && typeof capabilityValue === 'object') {
        const capabilityDef = this.registry.getCapability(capabilityName);
        if (capabilityDef && capabilityDef.validation) {
          normalized[capabilityName] = this.ensureRequiredFields(
            capabilityValue as any,
            capabilityDef.validation
          );
        }
      }
    }

    return normalized;
  }

  private applyProviderSpecificNormalization(
    capabilities: ModelCapabilities,
    context: ValidationContext
  ): ModelCapabilities {
    let normalized = { ...capabilities };

    switch (context.providerType) {
      case 'openai':
        normalized = this.applyOpenAINormalization(normalized);
        break;
      case 'anthropic':
        normalized = this.applyAnthropicNormalization(normalized);
        break;
      case 'google':
        normalized = this.applyGoogleNormalization(normalized);
        break;
    }

    return normalized;
  }

  // Helper methods
  private initializeDefaultRules(): void {
    // Add default validation rules
    this.addValidationRule({
      name: 'required_capabilities',
      description: 'Ensure required capabilities are present',
      severity: 'error',
      validate: (capabilities, context) => {
        const errors: ValidationError[] = [];

        if (!capabilities.text_generation || !capabilities.text_generation.supported) {
          errors.push({
            code: 'MISSING_REQUIRED_CAPABILITY',
            message: 'Text generation is required',
            capability: 'text_generation',
          });
        }

        return { valid: errors.length === 0, errors, warnings: [], info: [] };
      },
    });

    this.addValidationRule({
      name: 'cost_reasonableness',
      description: 'Check if model costs are reasonable',
      severity: 'warning',
      validate: (capabilities, context) => {
        const warnings: ValidationWarning[] = [];

        if (capabilities.performance) {
          const { inputCostPer1K, outputCostPer1K } = capabilities.performance;
          const totalCost = (inputCostPer1K || 0) + (outputCostPer1K || 0);

          if (totalCost > 50) {
            warnings.push({
              code: 'HIGH_COST',
              message: `High cost per 1K tokens: $${totalCost}`,
              capability: 'performance',
              recommendation: 'Consider using a more cost-effective model',
            });
          }
        }

        return { valid: true, errors: [], warnings, info: [] };
      },
    });
  }

  private initializeConflictResolvers(): void {
    // Add conflict resolvers
    this.addConflictResolver('token_limit_conflict', new TokenLimitConflictResolver());
    this.addConflictResolver('capability_naming_conflict', new CapabilityNamingConflictResolver());
    this.addConflictResolver('value_type_conflict', new ValueTypeConflictResolver());
  }

  private convertRegistryErrors(errors: string[]): ValidationError[] {
    return errors.map((error) => ({
      code: 'SCHEMA_VALIDATION_ERROR',
      message: error,
      capability: 'unknown',
    }));
  }

  private convertRegistryWarnings(warnings: string[]): ValidationWarning[] {
    return warnings.map((warning) => ({
      code: 'SCHEMA_VALIDATION_WARNING',
      message: warning,
      capability: 'unknown',
    }));
  }

  // Additional helper methods would be implemented here...
}

// Conflict resolver interfaces and implementations
export interface CapabilityConflict {
  type: string;
  capabilities: string[];
  description: string;
  severity: 'error' | 'warning';
  data: any;
}

export interface ConflictResolver {
  resolve(conflict: CapabilityConflict, context: ValidationContext): ConflictResolution;
}

export interface ConflictResolution {
  resolved: boolean;
  action?: string;
  resolvedCapabilities?: any;
  warnings?: ValidationWarning[];
  suggestion?: string;
}

class TokenLimitConflictResolver implements ConflictResolver {
  resolve(conflict: CapabilityConflict, context: ValidationContext): ConflictResolution {
    // Resolve token limit conflicts by using the minimum
    return {
      resolved: true,
      action: 'use_minimum_token_limit',
      suggestion: 'Using minimum token limit to resolve conflict',
    };
  }
}

class CapabilityNamingConflictResolver implements ConflictResolver {
  resolve(conflict: CapabilityConflict, context: ValidationContext): ConflictResolution {
    // Resolve naming conflicts by using standardized names
    return {
      resolved: true,
      action: 'use_standardized_names',
      suggestion: 'Using standardized capability names',
    };
  }
}

class ValueTypeConflictResolver implements ConflictResolver {
  resolve(conflict: CapabilityConflict, context: ValidationContext): ConflictResolution {
    // Resolve type conflicts by type conversion
    return {
      resolved: true,
      action: 'type_conversion',
      suggestion: 'Converting value types to resolve conflict',
    };
  }
}
```

### Subtask 4.4: Create Model Filtering and Search Functionality

**Description**: Implement advanced model filtering and search with capability-based queries, faceted search, and performance optimization.

**Implementation Details**:

1. **Create Model Search Engine**:

```typescript
// packages/providers/src/search/model-search-engine.ts
import { Model, ModelFilter, ModelCapabilities } from '../interfaces/model.types';
import { CapabilityRegistry } from '../capabilities/capability-registry';
import { EventEmitter } from 'events';

export interface SearchQuery {
  text?: string;
  capabilities?: string[];
  provider?: string[];
  tags?: string[];
  priceRange?: PriceRange;
  tokenRange?: TokenRange;
  features?: ModelFeature[];
  sortBy?: SortOption;
  limit?: number;
  offset?: number;
  facets?: FacetConfig[];
}

export interface PriceRange {
  min?: number;
  max?: number;
  perToken?: boolean;
}

export interface TokenRange {
  min?: number;
  max?: number;
}

export interface ModelFeature {
  streaming: boolean;
  functionCalling: boolean;
  vision: boolean;
  embedding: boolean;
  fineTuning: boolean;
  batchProcessing: boolean;
}

export interface SortOption {
  field: SortField;
  direction: 'asc' | 'desc';
}

export enum SortField {
  NAME = 'name',
  PROVIDER = 'provider',
  TOKENS = 'maxTokens',
  COST = 'cost',
  CREATED = 'createdAt',
  UPDATED = 'updatedAt',
}

export interface FacetConfig {
  field: FacetField;
  limit?: number;
  sort?: 'count' | 'name';
}

export enum FacetField {
  PROVIDER = 'provider',
  CAPABILITIES = 'capabilities',
  TAGS = 'tags',
  FEATURES = 'features',
  PRICE_RANGE = 'priceRange',
  TOKEN_RANGE = 'tokenRange',
}

export interface SearchResult {
  models: Model[];
  total: number;
  facets: FacetResults;
  query: SearchQuery;
  took: number;
  suggestions?: string[];
}

export interface FacetResults {
  providers: FacetBucket[];
  capabilities: FacetBucket[];
  tags: FacetBucket[];
  features: FacetBucket[];
  priceRanges: FacetBucket[];
  tokenRanges: FacetBucket[];
}

export interface FacetBucket {
  value: string;
  count: number;
  selected?: boolean;
}

export interface SearchIndex {
  models: Map<string, IndexedModel>;
  textIndex: Map<string, Set<string>>; // word -> model IDs
  capabilityIndex: Map<string, Set<string>>; // capability -> model IDs
  providerIndex: Map<string, Set<string>>; // provider -> model IDs
  tagIndex: Map<string, Set<string>>; // tag -> model IDs
  featureIndex: Map<string, Set<string>>; // feature -> model IDs
}

export interface IndexedModel extends Model {
  searchableText: string;
  capabilitiesList: string[];
  tagsList: string[];
  featuresList: string[];
  costPer1K: number;
  tokenCount: number;
}

export class ModelSearchEngine extends EventEmitter {
  private index: SearchIndex;
  private capabilityRegistry: CapabilityRegistry;

  constructor(capabilityRegistry: CapabilityRegistry) {
    super();
    this.capabilityRegistry = capabilityRegistry;
    this.index = {
      models: new Map(),
      textIndex: new Map(),
      capabilityIndex: new Map(),
      providerIndex: new Map(),
      tagIndex: new Map(),
      featureIndex: new Map(),
    };
  }

  // Index management
  async buildIndex(models: Model[]): Promise<void> {
    const startTime = Date.now();

    this.clearIndex();

    for (const model of models) {
      const indexedModel = this.createIndexedModel(model);
      this.index.models.set(model.id, indexedModel);

      // Update text index
      this.updateTextIndex(indexedModel);

      // Update capability index
      this.updateCapabilityIndex(indexedModel);

      // Update provider index
      this.updateProviderIndex(indexedModel);

      // Update tag index
      this.updateTagIndex(indexedModel);

      // Update feature index
      this.updateFeatureIndex(indexedModel);
    }

    const buildTime = Date.now() - startTime;

    this.emit('indexBuilt', {
      modelCount: models.length,
      buildTime,
      indexSize: this.getIndexSize(),
    });
  }

  async addToIndex(models: Model[]): Promise<void> {
    for (const model of models) {
      const indexedModel = this.createIndexedModel(model);
      this.index.models.set(model.id, indexedModel);

      this.updateTextIndex(indexedModel);
      this.updateCapabilityIndex(indexedModel);
      this.updateProviderIndex(indexedModel);
      this.updateTagIndex(indexedModel);
      this.updateFeatureIndex(indexedModel);
    }

    this.emit('modelsAdded', { modelCount: models.length });
  }

  async removeFromIndex(modelIds: string[]): Promise<void> {
    for (const modelId of modelIds) {
      const indexedModel = this.index.models.get(modelId);
      if (indexedModel) {
        this.removeFromTextIndex(indexedModel);
        this.removeFromCapabilityIndex(indexedModel);
        this.removeFromProviderIndex(indexedModel);
        this.removeFromTagIndex(indexedModel);
        this.removeFromFeatureIndex(indexedModel);

        this.index.models.delete(modelId);
      }
    }

    this.emit('modelsRemoved', { modelCount: modelIds.length });
  }

  clearIndex(): void {
    this.index.models.clear();
    this.index.textIndex.clear();
    this.index.capabilityIndex.clear();
    this.index.providerIndex.clear();
    this.index.tagIndex.clear();
    this.index.featureIndex.clear();
  }

  // Search operations
  async search(query: SearchQuery): Promise<SearchResult> {
    const startTime = Date.now();

    // Start with all models
    let candidateIds = new Set(this.index.models.keys());

    // Apply filters
    candidateIds = this.applyTextFilter(candidateIds, query.text);
    candidateIds = this.applyCapabilityFilter(candidateIds, query.capabilities);
    candidateIds = this.applyProviderFilter(candidateIds, query.provider);
    candidateIds = this.applyTagFilter(candidateIds, query.tags);
    candidateIds = this.applyFeatureFilter(candidateIds, query.features);
    candidateIds = this.applyPriceFilter(candidateIds, query.priceRange);
    candidateIds = this.applyTokenFilter(candidateIds, query.tokenRange);

    // Convert to array and sort
    let models = Array.from(candidateIds).map((id) => this.index.models.get(id)!);
    models = this.applySorting(models, query.sortBy);

    // Apply pagination
    const total = models.length;
    if (query.offset) {
      models = models.slice(query.offset);
    }
    if (query.limit) {
      models = models.slice(0, query.limit);
    }

    // Generate facets
    const facets = await this.generateFacets(candidateIds, query.facets);

    // Generate suggestions
    const suggestions = this.generateSuggestions(query);

    const took = Date.now() - startTime;

    return {
      models,
      total,
      facets,
      query,
      took,
      suggestions,
    };
  }

  async suggest(query: string, limit: number = 5): Promise<string[]> {
    const suggestions: string[] = [];
    const lowerQuery = query.toLowerCase();

    // Simple text-based suggestions
    for (const [word, modelIds] of this.index.textIndex) {
      if (word.includes(lowerQuery) || lowerQuery.includes(word)) {
        for (const modelId of modelIds) {
          const model = this.index.models.get(modelId);
          if (model && !suggestions.includes(model.name)) {
            suggestions.push(model.name);
            if (suggestions.length >= limit) break;
          }
        }
      }
      if (suggestions.length >= limit) break;
    }

    return suggestions.slice(0, limit);
  }

  async getSimilarModels(modelId: string, limit: number = 10): Promise<Model[]> {
    const targetModel = this.index.models.get(modelId);
    if (!targetModel) {
      return [];
    }

    const similarities = new Map<string, number>();

    // Calculate similarity scores
    for (const [id, model] of this.index.models) {
      if (id === modelId) continue;

      const score = this.calculateSimilarity(targetModel, model);
      similarities.set(id, score);
    }

    // Sort by similarity and return top results
    const sortedModels = Array.from(similarities.entries())
      .sort(([, a], [, b]) => b - a)
      .slice(0, limit)
      .map(([id]) => this.index.models.get(id)!);

    return sortedModels;
  }

  // Private helper methods
  private createIndexedModel(model: Model): IndexedModel {
    const capabilitiesList = this.extractCapabilitiesList(model.capabilities);
    const tagsList = model.tags || [];
    const featuresList = this.extractFeaturesList(model);
    const costPer1K = this.calculateCostPer1K(model);
    const tokenCount = model.maxTokens || 0;

    const searchableText = this.createSearchableText(
      model,
      capabilitiesList,
      tagsList,
      featuresList
    );

    return {
      ...model,
      searchableText,
      capabilitiesList,
      tagsList,
      featuresList,
      costPer1K,
      tokenCount,
    };
  }

  private extractCapabilitiesList(capabilities?: ModelCapabilities): string[] {
    if (!capabilities) return [];

    return Object.entries(capabilities)
      .filter(([, value]) => value && typeof value === 'object' && (value as any).supported)
      .map(([name]) => name);
  }

  private extractFeaturesList(model: Model): string[] {
    const features: string[] = [];

    if (model.supportsStreaming) features.push('streaming');
    if (model.supportsFunctionCalling) features.push('functionCalling');
    if (model.supportsVision) features.push('vision');
    if (model.capabilities?.embedding?.supported) features.push('embedding');
    if (model.capabilities?.fine_tuning?.supported) features.push('fineTuning');
    if (model.capabilities?.batch_processing?.supported) features.push('batchProcessing');

    return features;
  }

  private calculateCostPer1K(model: Model): number {
    const inputCost = model.inputCostPer1K || 0;
    const outputCost = model.outputCostPer1K || 0;
    return inputCost + outputCost;
  }

  private createSearchableText(
    model: Model,
    capabilitiesList: string[],
    tagsList: string[],
    featuresList: string[]
  ): string {
    const parts = [
      model.name,
      model.displayName,
      model.description || '',
      model.providerName,
      ...capabilitiesList,
      ...tagsList,
      ...featuresList,
    ];

    return parts.join(' ').toLowerCase();
  }

  private updateTextIndex(model: IndexedModel): void {
    const words = model.searchableText.split(/\s+/).filter((word) => word.length > 2);

    for (const word of words) {
      if (!this.index.textIndex.has(word)) {
        this.index.textIndex.set(word, new Set());
      }
      this.index.textIndex.get(word)!.add(model.id);
    }
  }

  private updateCapabilityIndex(model: IndexedModel): void {
    for (const capability of model.capabilitiesList) {
      if (!this.index.capabilityIndex.has(capability)) {
        this.index.capabilityIndex.set(capability, new Set());
      }
      this.index.capabilityIndex.get(capability)!.add(model.id);
    }
  }

  private updateProviderIndex(model: IndexedModel): void {
    if (!this.index.providerIndex.has(model.providerName)) {
      this.index.providerIndex.set(model.providerName, new Set());
    }
    this.index.providerIndex.get(model.providerName)!.add(model.id);
  }

  private updateTagIndex(model: IndexedModel): void {
    for (const tag of model.tagsList) {
      if (!this.index.tagIndex.has(tag)) {
        this.index.tagIndex.set(tag, new Set());
      }
      this.index.tagIndex.get(tag)!.add(model.id);
    }
  }

  private updateFeatureIndex(model: IndexedModel): void {
    for (const feature of model.featuresList) {
      if (!this.index.featureIndex.has(feature)) {
        this.index.featureIndex.set(feature, new Set());
      }
      this.index.featureIndex.get(feature)!.add(model.id);
    }
  }

  // Filter application methods
  private applyTextFilter(candidateIds: Set<string>, text?: string): Set<string> {
    if (!text) return candidateIds;

    const words = text
      .toLowerCase()
      .split(/\s+/)
      .filter((word) => word.length > 2);
    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        const modelText = model.searchableText;

        // Check if all words are present
        const hasAllWords = words.every((word) => modelText.includes(word));
        if (hasAllWords) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applyCapabilityFilter(candidateIds: Set<string>, capabilities?: string[]): Set<string> {
    if (!capabilities || capabilities.length === 0) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        const hasAllCapabilities = capabilities.every((cap) =>
          model.capabilitiesList.includes(cap)
        );
        if (hasAllCapabilities) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applyProviderFilter(candidateIds: Set<string>, providers?: string[]): Set<string> {
    if (!providers || providers.length === 0) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model && providers.includes(model.providerName)) {
        result.add(modelId);
      }
    }

    return result;
  }

  private applyTagFilter(candidateIds: Set<string>, tags?: string[]): Set<string> {
    if (!tags || tags.length === 0) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        const hasAllTags = tags.every((tag) => model.tagsList.includes(tag));
        if (hasAllTags) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applyFeatureFilter(candidateIds: Set<string>, features?: ModelFeature[]): Set<string> {
    if (!features) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        const hasAllFeatures = this.checkFeatures(model, features);
        if (hasAllFeatures) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applyPriceFilter(candidateIds: Set<string>, priceRange?: PriceRange): Set<string> {
    if (!priceRange) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        let inRange = true;

        if (priceRange.min !== undefined && model.costPer1K < priceRange.min) {
          inRange = false;
        }

        if (priceRange.max !== undefined && model.costPer1K > priceRange.max) {
          inRange = false;
        }

        if (inRange) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applyTokenFilter(candidateIds: Set<string>, tokenRange?: TokenRange): Set<string> {
    if (!tokenRange) return candidateIds;

    const result = new Set<string>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        let inRange = true;

        if (tokenRange.min !== undefined && model.tokenCount < tokenRange.min) {
          inRange = false;
        }

        if (tokenRange.max !== undefined && model.tokenCount > tokenRange.max) {
          inRange = false;
        }

        if (inRange) {
          result.add(modelId);
        }
      }
    }

    return result;
  }

  private applySorting(models: IndexedModel[], sortBy?: SortOption): IndexedModel[] {
    if (!sortBy) {
      return models;
    }

    return models.sort((a, b) => {
      let comparison = 0;

      switch (sortBy.field) {
        case SortField.NAME:
          comparison = a.name.localeCompare(b.name);
          break;
        case SortField.PROVIDER:
          comparison = a.providerName.localeCompare(b.providerName);
          break;
        case SortField.TOKENS:
          comparison = a.tokenCount - b.tokenCount;
          break;
        case SortField.COST:
          comparison = a.costPer1K - b.costPer1K;
          break;
        case SortField.CREATED:
          comparison =
            new Date(a.createdAt || '').getTime() - new Date(b.createdAt || '').getTime();
          break;
        case SortField.UPDATED:
          comparison =
            new Date(a.updatedAt || '').getTime() - new Date(b.updatedAt || '').getTime();
          break;
      }

      return sortBy.direction === 'desc' ? -comparison : comparison;
    });
  }

  private async generateFacets(
    candidateIds: Set<string>,
    facetConfigs?: FacetConfig[]
  ): Promise<FacetResults> {
    const facets: FacetResults = {
      providers: [],
      capabilities: [],
      tags: [],
      features: [],
      priceRanges: [],
      tokenRanges: [],
    };

    if (!facetConfigs || facetConfigs.length === 0) {
      return facets;
    }

    for (const facetConfig of facetConfigs) {
      switch (facetConfig.field) {
        case FacetField.PROVIDER:
          facets.providers = this.generateProviderFacets(candidateIds, facetConfig);
          break;
        case FacetField.CAPABILITIES:
          facets.capabilities = this.generateCapabilityFacets(candidateIds, facetConfig);
          break;
        case FacetField.TAGS:
          facets.tags = this.generateTagFacets(candidateIds, facetConfig);
          break;
        case FacetField.FEATURES:
          facets.features = this.generateFeatureFacets(candidateIds, facetConfig);
          break;
        case FacetField.PRICE_RANGE:
          facets.priceRanges = this.generatePriceRangeFacets(candidateIds, facetConfig);
          break;
        case FacetField.TOKEN_RANGE:
          facets.tokenRanges = this.generateTokenRangeFacets(candidateIds, facetConfig);
          break;
      }
    }

    return facets;
  }

  private generateProviderFacets(candidateIds: Set<string>, config: FacetConfig): FacetBucket[] {
    const providerCounts = new Map<string, number>();

    for (const modelId of candidateIds) {
      const model = this.index.models.get(modelId);
      if (model) {
        const count = providerCounts.get(model.providerName) || 0;
        providerCounts.set(model.providerName, count + 1);
      }
    }

    let buckets = Array.from(providerCounts.entries()).map(([provider, count]) => ({
      value: provider,
      count,
    }));

    // Sort and limit
    if (config.sort === 'count') {
      buckets.sort((a, b) => b.count - a.count);
    } else {
      buckets.sort((a, b) => a.value.localeCompare(b.value));
    }

    if (config.limit) {
      buckets = buckets.slice(0, config.limit);
    }

    return buckets;
  }

  // Additional facet generation methods would be implemented here...

  private generateSuggestions(query: SearchQuery): string[] {
    const suggestions: string[] = [];

    // Generate suggestions based on common typos
    if (query.text) {
      const commonTypos = new Map([
        ['gpt', ['gpt3', 'gpt4']],
        ['claude', ['cloud']],
        ['openai', ['open ai', 'openai']],
        ['anthropic', ['antropik']],
      ]);

      for (const [correct, typos] of commonTypos) {
        if (typos.some((typo) => query.text!.toLowerCase().includes(typo))) {
          suggestions.push(correct);
        }
      }
    }

    return suggestions;
  }

  private calculateSimilarity(model1: IndexedModel, model2: IndexedModel): number {
    // Simple similarity calculation based on multiple factors
    let score = 0;

    // Provider similarity
    if (model1.providerName === model2.providerName) {
      score += 0.3;
    }

    // Capability similarity
    const commonCapabilities = model1.capabilitiesList.filter((cap) =>
      model2.capabilitiesList.includes(cap)
    ).length;
    const totalCapabilities = new Set([...model1.capabilitiesList, ...model2.capabilitiesList])
      .size;
    score += (commonCapabilities / totalCapabilities) * 0.3;

    // Feature similarity
    const commonFeatures = model1.featuresList.filter((feature) =>
      model2.featuresList.includes(feature)
    ).length;
    const totalFeatures = new Set([...model1.featuresList, ...model2.featuresList]).size;
    score += (commonFeatures / totalFeatures) * 0.2;

    // Token count similarity
    const tokenDiff = Math.abs(model1.tokenCount - model2.tokenCount);
    const maxTokens = Math.max(model1.tokenCount, model2.tokenCount);
    score += (1 - tokenDiff / maxTokens) * 0.1;

    // Cost similarity
    const costDiff = Math.abs(model1.costPer1K - model2.costPer1K);
    const maxCost = Math.max(model1.costPer1K, model2.costPer1K);
    score += (1 - costDiff / maxCost) * 0.1;

    return score;
  }

  private checkFeatures(model: IndexedModel, features?: ModelFeature[]): boolean {
    if (!features) return true;

    for (const feature of features) {
      const hasFeature = model.featuresList.includes(this.featureToString(feature));
      if (!hasFeature) {
        return false;
      }
    }

    return true;
  }

  private featureToString(feature: ModelFeature): string {
    switch (feature) {
      case ModelFeature.STREAMING:
        return 'streaming';
      case ModelFeature.FUNCTION_CALLING:
        return 'functionCalling';
      case ModelFeature.VISION:
        return 'vision';
      case ModelFeature.EMBEDDING:
        return 'embedding';
      case ModelFeature.FINE_TUNING:
        return 'fineTuning';
      case ModelFeature.BATCH_PROCESSING:
        return 'batchProcessing';
      default:
        return '';
    }
  }

  private getIndexSize(): number {
    return {
      models: this.index.models.size,
      textIndex: Array.from(this.index.textIndex.values()).reduce((sum, set) => sum + set.size, 0),
      capabilityIndex: Array.from(this.index.capabilityIndex.values()).reduce(
        (sum, set) => sum + set.size,
        0
      ),
      providerIndex: Array.from(this.index.providerIndex.values()).reduce(
        (sum, set) => sum + set.size,
        0
      ),
      tagIndex: Array.from(this.index.tagIndex.values()).reduce((sum, set) => sum + set.size, 0),
      featureIndex: Array.from(this.index.featureIndex.values()).reduce(
        (sum, set) => sum + set.size,
        0
      ),
    };
  }

  // Cleanup methods for index updates
  private removeFromTextIndex(model: IndexedModel): void {
    const words = model.searchableText.split(/\s+/);

    for (const word of words) {
      const modelIds = this.index.textIndex.get(word);
      if (modelIds) {
        modelIds.delete(model.id);
        if (modelIds.size === 0) {
          this.index.textIndex.delete(word);
        }
      }
    }
  }

  // Additional cleanup methods would be implemented here...
}
```

## Files to Create

1. **Capability Registry**:
   - `packages/providers/src/capabilities/capability-registry.ts`
   - `packages/providers/src/capabilities/base-provider-mapper.ts`
   - `packages/providers/src/capabilities/openai-capability-mapper.ts`
   - `packages/providers/src/capabilities/anthropic-capability-mapper.ts`

2. **Validation and Normalization**:
   - `packages/providers/src/capabilities/capability-validator.ts`
   - `packages/providers/src/capabilities/capability-normalizer.ts`

3. **Search and Filtering**:
   - `packages/providers/src/search/model-search-engine.ts`
   - `packages/providers/src/search/search-filters.ts`
   - `packages/providers/src/search/facet-generator.ts`

4. **Updated Files**:
   - `packages/providers/src/index.ts` (export new classes)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/capabilities/capability-registry.test.ts
describe('CapabilityRegistry', () => {
  let registry: CapabilityRegistry;

  beforeEach(() => {
    registry = new CapabilityRegistry();
  });

  describe('capability registration', () => {
    it('should register valid capability', () => {
      const definition = createMockCapabilityDefinition();
      registry.registerCapability(definition);

      const retrieved = registry.getCapability(definition.name);
      expect(retrieved).toEqual(definition);
    });

    it('should reject duplicate capability', () => {
      const definition = createMockCapabilityDefinition();
      registry.registerCapability(definition);

      expect(() => registry.registerCapability(definition)).toThrow();
    });
  });

  describe('capability validation', () => {
    it('should validate capability values', () => {
      const result = registry.validateCapability('text_generation', {
        supported: true,
        maxTokens: 4096,
      });

      expect(result.valid).toBe(true);
    });

    it('should reject invalid values', () => {
      const result = registry.validateCapability('text_generation', {
        supported: 'invalid',
        maxTokens: -1,
      });

      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/capability-mapping-integration.test.ts
describe('Capability Mapping Integration', () => {
  let openaiMapper: OpenAICapabilityMapper;
  let anthropicMapper: AnthropicCapabilityMapper;
  let registry: CapabilityRegistry;

  beforeEach(() => {
    registry = new CapabilityRegistry();
    openaiMapper = new OpenAICapabilityMapper(registry);
    anthropicMapper = new AnthropicCapabilityMapper(registry);
  });

  it('should map OpenAI model capabilities correctly', () => {
    const openaiModel = createMockOpenAIModel();
    const capabilities = openaiMapper.mapCapabilities(openaiModel);

    expect(capabilities.text_generation).toBeDefined();
    expect(capabilities.text_generation.supported).toBe(true);
    expect(capabilities.function_calling).toBeDefined();
    expect(capabilities.vision).toBeDefined();
  });

  it('should map Anthropic model capabilities correctly', () => {
    const anthropicModel = createMockAnthropicModel();
    const capabilities = anthropicMapper.mapCapabilities(anthropicModel);

    expect(capabilities.text_generation).toBeDefined();
    expect(capabilities.text_generation.supported).toBe(true);
    expect(capabilities.vision).toBeDefined();
    expect(capabilities.reasoning).toBeDefined();
  });
});
```

## Security Considerations

1. **Input Validation**:
   - Validate all capability values against schemas
   - Prevent injection attacks in capability names
   - Sanitize text search inputs

2. **Access Control**:
   - Restrict access to certain capabilities based on user permissions
   - Implement capability-based authorization
   - Audit capability access and modifications

3. **Data Privacy**:
   - Sanitize model metadata before indexing
   - Implement data retention policies for search logs
   - Protect sensitive capability information

## Dependencies

### New Dependencies

```json
{
  "fuse.js": "^7.0.0",
  "lunr": "^2.3.9"
}
```

### Dev Dependencies

```json
{
  "@types/fuse.js": "^7.0.1",
  "@types/lunr": "^2.3.7"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Capability mapping success/failure rates
   - Search query performance and result relevance
   - Index build times and sizes
   - Validation error frequencies

2. **Logging**:
   - All capability mapping operations
   - Search queries with performance metrics
   - Validation errors and warnings
   - Index updates and maintenance

3. **Alerts**:
   - High validation error rates
   - Search performance degradation
   - Index corruption or inconsistencies
   - Capability mapping failures

## Acceptance Criteria

1. ✅ **Standardized Definitions**: Complete capability taxonomy with validation
2. ✅ **Provider Mappers**: Mappers for major AI providers
3. ✅ **Validation System**: Comprehensive validation with conflict resolution
4. ✅ **Normalization**: Capability normalization across providers
5. ✅ **Search Functionality**: Advanced search with faceting and filtering
6. ✅ **Performance**: Efficient indexing and search operations
7. ✅ **Extensibility**: Easy addition of new providers and capabilities
8. ✅ **Testing**: Complete test coverage for all components
9. ✅ **Documentation**: Clear API documentation and examples
10. ✅ **Security**: Secure handling of capability data and searches

## Success Metrics

- Capability mapping accuracy > 95%
- Search query response time < 100ms
- Index build time < 5 seconds for 1000 models
- Validation error rate < 1%
- Search relevance score > 85%
- Zero security incidents in capability handling
- Complete audit trail for all capability operations
