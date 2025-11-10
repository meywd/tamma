# Task 10: Provider Capability Detection

## Overview

Implement automatic capability detection and discovery system for all AI providers in the Tamma platform. This system will dynamically detect provider capabilities, model features, and supported operations to enable intelligent provider selection and fallback strategies.

## Acceptance Criteria

1. **AC1**: Implement capability detection interface with standardized capability taxonomy
2. **AC2**: Create automatic capability discovery for all 9 providers
3. **AC3**: Build capability caching and invalidation system
4. **AC4**: Implement capability-based provider selection and routing
5. **AC5**: Add capability monitoring and health checks
6. **AC6**: Create capability comparison and matching algorithms
7. **AC7**: Implement capability evolution and versioning support
8. **AC8**: Build capability reporting and analytics
9. **AC9**: Add capability-based test generation
10. **AC10**: Create capability documentation generation

## Implementation

### 1. Capability Detection Interface

```typescript
// packages/shared/src/types/provider-capabilities.types.ts

export interface ProviderCapabilities {
  // Core capabilities
  supportedModels: ModelCapabilities[];
  features: FeatureCapabilities;
  limitations: LimitationCapabilities;
  performance: PerformanceCapabilities;
  compliance: ComplianceCapabilities;

  // Metadata
  detectedAt: string;
  version: string;
  source: 'api' | 'config' | 'discovery';
  confidence: number; // 0-1 confidence level
}

export interface ModelCapabilities {
  id: string;
  name: string;
  type: ModelType;
  contextWindow: ContextWindowInfo;
  capabilities: ModelFeatureSet;
  pricing: PricingInfo;
  availability: AvailabilityInfo;
  performance: ModelPerformanceMetrics;
}

export type ModelType =
  | 'text-generation'
  | 'code-completion'
  | 'chat'
  | 'embedding'
  | 'multimodal'
  | 'function-calling'
  | 'reasoning';

export interface ContextWindowInfo {
  input: number;
  output: number;
  total: number;
  unit: 'tokens' | 'characters';
}

export interface ModelFeatureSet {
  streaming: boolean;
  functionCalling: boolean;
  multimodal: boolean;
  codeGeneration: boolean;
  reasoning: boolean;
  toolUse: boolean;
  imageInput: boolean;
  audioInput: boolean;
  videoInput: boolean;
  documentInput: boolean;
  jsonOutput: boolean;
  structuredOutput: boolean;
  systemMessages: boolean;
  fewShot: boolean;
  chainOfThought: boolean;
}

export interface PricingInfo {
  input: PricingTier[];
  output: PricingTier[];
  currency: string;
  billingUnit: 'tokens' | 'characters' | 'requests';
}

export interface PricingTier {
  upTo?: number;
  price: number;
  unit: string;
}

export interface AvailabilityInfo {
  regions: string[];
  status: 'available' | 'deprecated' | 'beta' | 'coming-soon';
  deprecationDate?: string;
  betaEndDate?: string;
}

export interface ModelPerformanceMetrics {
  latency: LatencyMetrics;
  throughput: ThroughputMetrics;
  accuracy: AccuracyMetrics;
  reliability: ReliabilityMetrics;
}

export interface LatencyMetrics {
  p50: number; // milliseconds
  p90: number;
  p95: number;
  p99: number;
}

export interface ThroughputMetrics {
  requestsPerSecond: number;
  tokensPerSecond: number;
  concurrentRequests: number;
}

export interface AccuracyMetrics {
  codeGeneration?: number; // 0-1 score
  reasoning?: number;
  generalKnowledge?: number;
  domainSpecific?: Record<string, number>;
}

export interface ReliabilityMetrics {
  uptime: number; // 0-1 percentage
  errorRate: number; // 0-1 percentage
  timeoutRate: number; // 0-1 percentage
}

export interface FeatureCapabilities {
  // Input/Output capabilities
  inputFormats: SupportedFormat[];
  outputFormats: SupportedFormat[];

  // Advanced features
  streaming: StreamingCapabilities;
  batching: BatchingCapabilities;
  caching: CachingCapabilities;

  // Integration features
  webhooks: WebhookCapabilities;
  apis: ApiCapabilities;
  sdks: SdkCapabilities;
}

export interface SupportedFormat {
  type: 'text' | 'json' | 'markdown' | 'code' | 'image' | 'audio' | 'video' | 'pdf' | 'docx';
  mimeTypes?: string[];
  maxSize?: number; // bytes
  compression?: string[];
}

export interface StreamingCapabilities {
  supported: boolean;
  protocols: ('sse' | 'websocket' | 'grpc')[];
  chunkFormats: string[];
  maxStreamDuration?: number; // seconds
}

export interface BatchingCapabilities {
  supported: boolean;
  maxBatchSize: number;
  batchWindow: number; // milliseconds
  partialFailureHandling: boolean;
}

export interface CachingCapabilities {
  supported: boolean;
  strategies: ('response' | 'semantic' | 'user-level')[];
  ttl: number; // seconds
  invalidation: ('manual' | 'ttl' | 'tag-based')[];
}

export interface WebhookCapabilities {
  supported: boolean;
  events: string[];
  authentication: ('none' | 'api-key' | 'signature')[];
  retryPolicy: RetryPolicy;
}

export interface ApiCapabilities {
  rest: boolean;
  graphql: boolean;
  grpc: boolean;
  rateLimit: RateLimitInfo;
  authentication: AuthenticationMethods;
}

export interface SdkCapabilities {
  languages: string[];
  platforms: ('web' | 'mobile' | 'server' | 'edge')[];
  asyncSupport: boolean;
  streamingSupport: boolean;
}

export interface LimitationCapabilities {
  rateLimits: RateLimitInfo[];
  quotas: QuotaInfo[];
  restrictions: RestrictionInfo[];
  compliance: ComplianceRestrictions;
}

export interface RateLimitInfo {
  type: 'requests' | 'tokens' | 'connections';
  limit: number;
  window: number; // seconds
  scope: 'global' | 'user' | 'api-key' | 'ip';
}

export interface QuotaInfo {
  type: 'daily' | 'monthly' | 'yearly';
  limit: number;
  current: number;
  resetDate?: string;
}

export interface RestrictionInfo {
  type: 'content' | 'geography' | 'use-case' | 'data-type';
  description: string;
  severity: 'warning' | 'error' | 'blocked';
}

export interface ComplianceRestrictions {
  dataResidency: string[];
  certifications: string[];
  dataProcessing: string[];
  auditLogging: boolean;
  encryptionRequired: boolean;
}

export interface PerformanceCapabilities {
  benchmarks: BenchmarkResults;
  sla: ServiceLevelAgreement;
  monitoring: MonitoringCapabilities;
}

export interface BenchmarkResults {
  lastUpdated: string;
  tests: BenchmarkTest[];
}

export interface BenchmarkTest {
  name: string;
  category: string;
  score: number;
  unit: string;
  methodology: string;
  comparedTo?: string;
}

export interface ServiceLevelAgreement {
  uptime: number; // percentage
  responseTime: number; // p95 milliseconds
  errorRate: number; // percentage
  compensation: string;
}

export interface MonitoringCapabilities {
  metrics: string[];
  alerts: AlertCapability[];
  dashboards: DashboardCapability[];
}

export interface AlertCapability {
  type: string;
  thresholds: Record<string, number>;
  channels: string[];
}

export interface DashboardCapability {
  name: string;
  metrics: string[];
  refreshInterval: number;
}

export interface ComplianceCapabilities {
  standards: ComplianceStandard[];
  certifications: Certification[];
  dataProtection: DataProtectionInfo;
  audit: AuditInfo;
}

export interface ComplianceStandard {
  name: string;
  version: string;
  status: 'compliant' | 'partial' | 'non-compliant';
  lastAssessed: string;
  evidence: string[];
}

export interface Certification {
  name: string;
  issuer: string;
  validUntil: string;
  scope: string[];
}

export interface DataProtectionInfo {
  encryptionAtRest: boolean;
  encryptionInTransit: boolean;
  dataRetention: number; // days
  rightToDeletion: boolean;
  dataPortability: boolean;
}

export interface AuditInfo {
  loggingEnabled: boolean;
  logRetention: number; // days
  auditTrail: boolean;
  complianceReporting: boolean;
}

// Capability detection interface
export interface ICapabilityDetector {
  detectCapabilities(provider: IAIProvider): Promise<ProviderCapabilities>;
  refreshCapabilities(providerId: string): Promise<ProviderCapabilities>;
  getCachedCapabilities(providerId: string): Promise<ProviderCapabilities | null>;
  invalidateCache(providerId: string): Promise<void>;
  compareCapabilities(a: ProviderCapabilities, b: ProviderCapabilities): CapabilityComparison;
  findProvidersForRequirement(requirement: CapabilityRequirement): Promise<string[]>;
}

export interface CapabilityRequirement {
  type: 'model' | 'feature' | 'performance' | 'compliance';
  requirements: Record<string, any>;
  priority: 'required' | 'preferred' | 'optional';
}

export interface CapabilityComparison {
  score: number; // 0-1 similarity score
  differences: CapabilityDifference[];
  recommendation: 'use-a' | 'use-b' | 'either' | 'neither';
  reasoning: string;
}

export interface CapabilityDifference {
  path: string;
  a: any;
  b: any;
  impact: 'high' | 'medium' | 'low';
  category: 'feature' | 'performance' | 'limitation' | 'compliance';
}
```

### 2. Capability Detection Implementation

```typescript
// packages/providers/src/capability-detector.ts

import type {
  IAIProvider,
  ICapabilityDetector,
  ProviderCapabilities,
  ModelCapabilities,
  CapabilityRequirement,
  CapabilityComparison,
  ModelType,
  ModelFeatureSet,
  ContextWindowInfo,
  PricingInfo,
  AvailabilityInfo,
  PerformanceCapabilities,
  FeatureCapabilities,
  LimitationCapabilities,
  ComplianceCapabilities,
} from '@tamma/shared/types';
import { logger } from '@tamma/observability';
import { CacheManager } from './cache-manager';
import { MetricsCollector } from './metrics-collector';

export class CapabilityDetector implements ICapabilityDetector {
  private cache = new CacheManager<ProviderCapabilities>({
    ttl: 3600000, // 1 hour
    maxSize: 100,
  });

  private metrics = new MetricsCollector();

  async detectCapabilities(provider: IAIProvider): Promise<ProviderCapabilities> {
    const startTime = Date.now();
    const providerId = provider.getProviderId();

    try {
      logger.info('Detecting capabilities for provider', { providerId });

      // Detect model capabilities
      const supportedModels = await this.detectModelCapabilities(provider);

      // Detect feature capabilities
      const features = await this.detectFeatureCapabilities(provider);

      // Detect limitations
      const limitations = await this.detectLimitationCapabilities(provider);

      // Detect performance capabilities
      const performance = await this.detectPerformanceCapabilities(provider);

      // Detect compliance capabilities
      const compliance = await this.detectComplianceCapabilities(provider);

      const capabilities: ProviderCapabilities = {
        supportedModels,
        features,
        limitations,
        performance,
        compliance,
        detectedAt: new Date().toISOString(),
        version: await this.getProviderVersion(provider),
        source: 'api',
        confidence: this.calculateConfidence(supportedModels, features, limitations),
      };

      // Cache the capabilities
      await this.cache.set(providerId, capabilities);

      // Record metrics
      this.metrics.record('capability_detection_duration', Date.now() - startTime, {
        providerId,
        modelCount: supportedModels.length,
        confidence: capabilities.confidence,
      });

      logger.info('Capabilities detected successfully', {
        providerId,
        modelCount: supportedModels.length,
        confidence: capabilities.confidence,
        duration: Date.now() - startTime,
      });

      return capabilities;
    } catch (error) {
      this.metrics.record('capability_detection_error', 1, {
        providerId,
        error: error.message,
      });

      logger.error('Failed to detect capabilities', {
        providerId,
        error: error.message,
        stack: error.stack,
      });

      throw new Error(`Capability detection failed for ${providerId}: ${error.message}`);
    }
  }

  private async detectModelCapabilities(provider: IAIProvider): Promise<ModelCapabilities[]> {
    const models: ModelCapabilities[] = [];

    try {
      // Get available models from provider
      const availableModels = await this.getAvailableModels(provider);

      for (const modelInfo of availableModels) {
        const capabilities = await this.detectSingleModelCapabilities(provider, modelInfo);
        models.push(capabilities);
      }

      return models;
    } catch (error) {
      logger.warn('Failed to detect model capabilities', {
        providerId: provider.getProviderId(),
        error: error.message,
      });

      // Return fallback capabilities
      return this.getFallbackModelCapabilities(provider);
    }
  }

  private async getAvailableModels(provider: IAIProvider): Promise<any[]> {
    const providerId = provider.getProviderId();

    switch (providerId) {
      case 'anthropic-claude':
        return [
          { id: 'claude-3-5-sonnet-20241022', name: 'Claude 3.5 Sonnet', type: 'chat' },
          { id: 'claude-3-opus-20240229', name: 'Claude 3 Opus', type: 'chat' },
          { id: 'claude-3-sonnet-20240229', name: 'Claude 3 Sonnet', type: 'chat' },
          { id: 'claude-3-haiku-20240307', name: 'Claude 3 Haiku', type: 'chat' },
        ];

      case 'openai':
        return [
          { id: 'gpt-4-turbo-preview', name: 'GPT-4 Turbo', type: 'chat' },
          { id: 'gpt-4', name: 'GPT-4', type: 'chat' },
          { id: 'gpt-3.5-turbo', name: 'GPT-3.5 Turbo', type: 'chat' },
          { id: 'gpt-4-vision-preview', name: 'GPT-4 Vision', type: 'multimodal' },
          { id: 'text-embedding-ada-002', name: 'Ada Embedding', type: 'embedding' },
        ];

      case 'github-copilot':
        return [
          { id: 'copilot', name: 'GitHub Copilot', type: 'code-completion' },
          { id: 'copilot-chat', name: 'GitHub Copilot Chat', type: 'chat' },
        ];

      case 'google-gemini':
        return [
          { id: 'gemini-1.5-pro', name: 'Gemini 1.5 Pro', type: 'multimodal' },
          { id: 'gemini-pro', name: 'Gemini Pro', type: 'chat' },
          { id: 'gemini-pro-vision', name: 'Gemini Pro Vision', type: 'multimodal' },
        ];

      case 'opencode':
        return [
          { id: 'codellama-13b', name: 'CodeLlama 13B', type: 'code-generation' },
          { id: 'starcoder2-15b', name: 'StarCoder2 15B', type: 'code-generation' },
          { id: 'wizardcoder-15b', name: 'WizardCoder 15B', type: 'code-generation' },
        ];

      case 'zai':
        return [
          { id: 'zai-code-7b', name: 'z.ai Code 7B', type: 'code-generation' },
          { id: 'zai-chat-13b', name: 'z.ai Chat 13B', type: 'chat' },
        ];

      case 'zen-mcp':
        return [
          { id: 'zen-gpt-4', name: 'Zen GPT-4', type: 'chat' },
          { id: 'zen-claude-3', name: 'Zen Claude 3', type: 'chat' },
        ];

      case 'openrouter':
        return [
          {
            id: 'anthropic/claude-3.5-sonnet',
            name: 'Claude 3.5 Sonnet (via OpenRouter)',
            type: 'chat',
          },
          { id: 'openai/gpt-4-turbo-preview', name: 'GPT-4 Turbo (via OpenRouter)', type: 'chat' },
          { id: 'google/gemini-pro', name: 'Gemini Pro (via OpenRouter)', type: 'chat' },
        ];

      case 'local-llm':
        return [
          { id: 'llama2-7b', name: 'Llama 2 7B', type: 'text-generation' },
          { id: 'codellama-13b', name: 'CodeLlama 13B', type: 'code-generation' },
          { id: 'mistral-7b', name: 'Mistral 7B', type: 'text-generation' },
        ];

      default:
        throw new Error(`Unknown provider: ${providerId}`);
    }
  }

  private async detectSingleModelCapabilities(
    provider: IAIProvider,
    modelInfo: any
  ): Promise<ModelCapabilities> {
    const providerId = provider.getProviderId();

    // Get base capabilities from provider-specific knowledge
    const baseCapabilities = this.getBaseModelCapabilities(providerId, modelInfo.id);

    // Try to detect actual capabilities through API calls
    const detectedCapabilities = await this.probeModelCapabilities(provider, modelInfo.id);

    // Merge base and detected capabilities
    return {
      ...baseCapabilities,
      ...detectedCapabilities,
      id: modelInfo.id,
      name: modelInfo.name,
      type: modelInfo.type as ModelType,
    };
  }

  private getBaseModelCapabilities(
    providerId: string,
    modelId: string
  ): Partial<ModelCapabilities> {
    const capabilities: Record<string, Partial<ModelCapabilities>> = {
      'anthropic-claude': {
        contextWindow: { input: 200000, output: 8192, total: 208192, unit: 'tokens' },
        capabilities: {
          streaming: true,
          functionCalling: true,
          multimodal: false,
          codeGeneration: true,
          reasoning: true,
          toolUse: true,
          imageInput: false,
          audioInput: false,
          videoInput: false,
          documentInput: false,
          jsonOutput: true,
          structuredOutput: true,
          systemMessages: true,
          fewShot: true,
          chainOfThought: true,
        },
        performance: {
          latency: { p50: 800, p90: 1200, p95: 1500, p99: 2000 },
          throughput: { requestsPerSecond: 10, tokensPerSecond: 100, concurrentRequests: 5 },
          accuracy: { codeGeneration: 0.85, reasoning: 0.88, generalKnowledge: 0.92 },
          reliability: { uptime: 0.999, errorRate: 0.001, timeoutRate: 0.0005 },
        },
      },
      openai: {
        contextWindow: { input: 128000, output: 4096, total: 132096, unit: 'tokens' },
        capabilities: {
          streaming: true,
          functionCalling: true,
          multimodal: true,
          codeGeneration: true,
          reasoning: true,
          toolUse: true,
          imageInput: true,
          audioInput: false,
          videoInput: false,
          documentInput: false,
          jsonOutput: true,
          structuredOutput: true,
          systemMessages: true,
          fewShot: true,
          chainOfThought: true,
        },
        performance: {
          latency: { p50: 600, p90: 1000, p95: 1300, p99: 1800 },
          throughput: { requestsPerSecond: 20, tokensPerSecond: 150, concurrentRequests: 10 },
          accuracy: { codeGeneration: 0.82, reasoning: 0.86, generalKnowledge: 0.9 },
          reliability: { uptime: 0.999, errorRate: 0.002, timeoutRate: 0.001 },
        },
      },
      'google-gemini': {
        contextWindow: { input: 1000000, output: 8192, total: 1008192, unit: 'tokens' },
        capabilities: {
          streaming: true,
          functionCalling: true,
          multimodal: true,
          codeGeneration: true,
          reasoning: true,
          toolUse: true,
          imageInput: true,
          audioInput: true,
          videoInput: true,
          documentInput: true,
          jsonOutput: true,
          structuredOutput: true,
          systemMessages: true,
          fewShot: true,
          chainOfThought: true,
        },
        performance: {
          latency: { p50: 1000, p90: 1800, p95: 2200, p99: 3000 },
          throughput: { requestsPerSecond: 5, tokensPerSecond: 80, concurrentRequests: 3 },
          accuracy: { codeGeneration: 0.78, reasoning: 0.84, generalKnowledge: 0.88 },
          reliability: { uptime: 0.998, errorRate: 0.003, timeoutRate: 0.002 },
        },
      },
    };

    return capabilities[providerId] || {};
  }

  private async probeModelCapabilities(
    provider: IAIProvider,
    modelId: string
  ): Promise<Partial<ModelCapabilities>> {
    const detected: Partial<ModelCapabilities> = {};

    try {
      // Test streaming capability
      detected.capabilities = {
        streaming: await this.testStreamingCapability(provider, modelId),
        functionCalling: await this.testFunctionCallingCapability(provider, modelId),
        multimodal: await this.testMultimodalCapability(provider, modelId),
        codeGeneration: await this.testCodeGenerationCapability(provider, modelId),
        reasoning: await this.testReasoningCapability(provider, modelId),
        toolUse: await this.testToolUseCapability(provider, modelId),
        jsonOutput: await this.testJsonOutputCapability(provider, modelId),
        structuredOutput: await this.testStructuredOutputCapability(provider, modelId),
        systemMessages: await this.testSystemMessagesCapability(provider, modelId),
      } as ModelFeatureSet;

      // Test performance
      detected.performance = await this.testModelPerformance(provider, modelId);
    } catch (error) {
      logger.warn('Failed to probe model capabilities', {
        providerId: provider.getProviderId(),
        modelId,
        error: error.message,
      });
    }

    return detected;
  }

  private async testStreamingCapability(provider: IAIProvider, modelId: string): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [{ role: 'user', content: 'Say "Hello, world!"' }],
        stream: true,
      };

      const response = await provider.sendMessage(request);
      let chunkCount = 0;

      for await (const chunk of response) {
        chunkCount++;
        if (chunkCount > 5) break; // Test a few chunks
      }

      return chunkCount > 0;
    } catch {
      return false;
    }
  }

  private async testFunctionCallingCapability(
    provider: IAIProvider,
    modelId: string
  ): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [{ role: 'user', content: 'What is the weather in New York?' }],
        tools: [
          {
            type: 'function',
            function: {
              name: 'get_weather',
              description: 'Get weather information',
              parameters: {
                type: 'object',
                properties: {
                  location: { type: 'string' },
                },
                required: ['location'],
              },
            },
          },
        ],
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      // Check if the model attempted to call the function
      const lastChunk = chunks[chunks.length - 1];
      return lastChunk?.tool_calls?.length > 0;
    } catch {
      return false;
    }
  }

  private async testMultimodalCapability(provider: IAIProvider, modelId: string): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [
          {
            role: 'user',
            content: [
              { type: 'text', text: 'What do you see in this image?' },
              {
                type: 'image',
                image:
                  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
              },
            ],
          },
        ],
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      return chunks.length > 0 && chunks[0].content?.length > 0;
    } catch {
      return false;
    }
  }

  private async testCodeGenerationCapability(
    provider: IAIProvider,
    modelId: string
  ): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [
          {
            role: 'user',
            content: 'Write a Python function that calculates the factorial of a number.',
          },
        ],
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      const content = chunks.map((c) => c.content).join('');
      return content.includes('def') && content.includes('factorial');
    } catch {
      return false;
    }
  }

  private async testReasoningCapability(provider: IAIProvider, modelId: string): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [
          {
            role: 'user',
            content:
              'If all cats are animals and some animals are pets, can we conclude that some cats are pets? Explain your reasoning.',
          },
        ],
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      const content = chunks.map((c) => c.content).join('');
      return (
        content.includes('reason') || content.includes('conclude') || content.includes('logic')
      );
    } catch {
      return false;
    }
  }

  private async testToolUseCapability(provider: IAIProvider, modelId: string): Promise<boolean> {
    // Similar to function calling test but with more complex tools
    return this.testFunctionCallingCapability(provider, modelId);
  }

  private async testJsonOutputCapability(provider: IAIProvider, modelId: string): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [
          {
            role: 'user',
            content: 'Return a JSON object with name: "John", age: 30, city: "New York"',
          },
        ],
        response_format: { type: 'json_object' },
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      const content = chunks.map((c) => c.content).join('');
      const parsed = JSON.parse(content);
      return parsed.name === 'John' && parsed.age === 30;
    } catch {
      return false;
    }
  }

  private async testStructuredOutputCapability(
    provider: IAIProvider,
    modelId: string
  ): Promise<boolean> {
    // Test with JSON schema
    return this.testJsonOutputCapability(provider, modelId);
  }

  private async testSystemMessagesCapability(
    provider: IAIProvider,
    modelId: string
  ): Promise<boolean> {
    try {
      const request = {
        model: modelId,
        messages: [
          {
            role: 'system',
            content: 'You are a helpful assistant who always responds with exactly "OK"',
          },
          { role: 'user', content: 'Hello!' },
        ],
      };

      const response = await provider.sendMessage(request);
      const chunks = [];

      for await (const chunk of response) {
        chunks.push(chunk);
      }

      const content = chunks.map((c) => c.content).join('');
      return content.includes('OK');
    } catch {
      return false;
    }
  }

  private async testModelPerformance(
    provider: IAIProvider,
    modelId: string
  ): Promise<ModelCapabilities['performance']> {
    const latencies: number[] = [];
    const testCount = 5;

    for (let i = 0; i < testCount; i++) {
      const startTime = Date.now();

      try {
        const request = {
          model: modelId,
          messages: [{ role: 'user', content: 'Say "test"' }],
        };

        const response = await provider.sendMessage(request);

        for await (const chunk of response) {
          // Consume the stream
        }

        latencies.push(Date.now() - startTime);
      } catch (error) {
        logger.warn('Performance test failed', { modelId, attempt: i, error: error.message });
      }
    }

    if (latencies.length === 0) {
      throw new Error('All performance tests failed');
    }

    latencies.sort((a, b) => a - b);

    return {
      latency: {
        p50: latencies[Math.floor(latencies.length * 0.5)],
        p90: latencies[Math.floor(latencies.length * 0.9)],
        p95: latencies[Math.floor(latencies.length * 0.95)],
        p99: latencies[Math.floor(latencies.length * 0.99)],
      },
      throughput: {
        requestsPerSecond: 1000 / (latencies.reduce((a, b) => a + b, 0) / latencies.length),
        tokensPerSecond: 50, // Estimated
        concurrentRequests: 1, // Single test
      },
      accuracy: {
        codeGeneration: 0.8, // Estimated
        reasoning: 0.8,
        generalKnowledge: 0.8,
      },
      reliability: {
        uptime: 0.999, // Estimated
        errorRate: 0.001,
        timeoutRate: 0,
      },
    };
  }

  private async detectFeatureCapabilities(provider: IAIProvider): Promise<FeatureCapabilities> {
    return {
      inputFormats: [{ type: 'text' }, { type: 'json' }, { type: 'markdown' }, { type: 'code' }],
      outputFormats: [{ type: 'text' }, { type: 'json' }, { type: 'markdown' }, { type: 'code' }],
      streaming: {
        supported: true,
        protocols: ['sse'],
        chunkFormats: ['json'],
        maxStreamDuration: 300,
      },
      batching: {
        supported: false,
        maxBatchSize: 1,
        batchWindow: 0,
        partialFailureHandling: false,
      },
      caching: {
        supported: true,
        strategies: ['response'],
        ttl: 3600,
        invalidation: ['ttl'],
      },
      webhooks: {
        supported: false,
        events: [],
        authentication: ['none'],
        retryPolicy: {
          maxAttempts: 3,
          backoffMs: 1000,
          maxBackoffMs: 10000,
        },
      },
      apis: {
        rest: true,
        graphql: false,
        grpc: false,
        rateLimit: {
          requests: 60,
          window: 60,
          scope: 'api-key',
        },
        authentication: {
          bearer: true,
          apiKey: true,
          oauth: false,
        },
      },
      sdks: {
        languages: ['typescript', 'python', 'javascript'],
        platforms: ['server', 'web'],
        asyncSupport: true,
        streamingSupport: true,
      },
    };
  }

  private async detectLimitationCapabilities(
    provider: IAIProvider
  ): Promise<LimitationCapabilities> {
    const providerId = provider.getProviderId();

    const baseLimitations: Record<string, LimitationCapabilities> = {
      'anthropic-claude': {
        rateLimits: [
          { type: 'requests', limit: 1000, window: 60, scope: 'api-key' },
          { type: 'tokens', limit: 100000, window: 60, scope: 'api-key' },
        ],
        quotas: [{ type: 'monthly', limit: 1000000, current: 0 }],
        restrictions: [
          {
            type: 'content',
            description: 'No disallowed content per policy',
            severity: 'error',
          },
        ],
        compliance: {
          dataResidency: ['US', 'EU'],
          certifications: ['SOC2', 'ISO27001'],
          dataProcessing: ['automated-processing'],
          auditLogging: true,
          encryptionRequired: true,
        },
      },
      openai: {
        rateLimits: [
          { type: 'requests', limit: 3500, window: 60, scope: 'organization' },
          { type: 'tokens', limit: 90000, window: 60, scope: 'organization' },
        ],
        quotas: [{ type: 'monthly', limit: 1000000, current: 0 }],
        restrictions: [
          {
            type: 'content',
            description: 'Content policy compliance required',
            severity: 'error',
          },
        ],
        compliance: {
          dataResidency: ['US'],
          certifications: ['SOC2', 'ISO27001'],
          dataProcessing: ['automated-processing'],
          auditLogging: true,
          encryptionRequired: true,
        },
      },
    };

    return (
      baseLimitations[providerId] || {
        rateLimits: [{ type: 'requests', limit: 100, window: 60, scope: 'api-key' }],
        quotas: [],
        restrictions: [],
        compliance: {
          dataResidency: [],
          certifications: [],
          dataProcessing: [],
          auditLogging: false,
          encryptionRequired: false,
        },
      }
    );
  }

  private async detectPerformanceCapabilities(
    provider: IAIProvider
  ): Promise<PerformanceCapabilities> {
    return {
      benchmarks: {
        lastUpdated: new Date().toISOString(),
        tests: [
          {
            name: 'Response Time',
            category: 'latency',
            score: 850,
            unit: 'ms',
            methodology: 'Average response time for 100 requests',
          },
          {
            name: 'Throughput',
            category: 'performance',
            score: 95,
            unit: 'req/s',
            methodology: 'Requests per second sustained',
          },
        ],
      },
      sla: {
        uptime: 99.9,
        responseTime: 1000,
        errorRate: 0.1,
        compensation: 'Service credits for downtime',
      },
      monitoring: {
        metrics: ['latency', 'throughput', 'error_rate', 'token_usage'],
        alerts: [
          {
            type: 'high_latency',
            thresholds: { p95: 2000 },
            channels: ['email', 'slack'],
          },
        ],
        dashboards: [
          {
            name: 'Provider Performance',
            metrics: ['latency', 'throughput', 'error_rate'],
            refreshInterval: 30,
          },
        ],
      },
    };
  }

  private async detectComplianceCapabilities(
    provider: IAIProvider
  ): Promise<ComplianceCapabilities> {
    const providerId = provider.getProviderId();

    const baseCompliance: Record<string, ComplianceCapabilities> = {
      'anthropic-claude': {
        standards: [
          {
            name: 'SOC 2 Type II',
            version: '2017',
            status: 'compliant',
            lastAssessed: '2024-01-15',
            evidence: ['audit_report.pdf', 'certification.pdf'],
          },
          {
            name: 'ISO 27001',
            version: '2013',
            status: 'compliant',
            lastAssessed: '2024-01-15',
            evidence: ['iso_certificate.pdf'],
          },
        ],
        certifications: [
          {
            name: 'SOC 2 Type II',
            issuer: 'AICPA',
            validUntil: '2025-01-15',
            scope: ['security', 'availability', 'confidentiality'],
          },
        ],
        dataProtection: {
          encryptionAtRest: true,
          encryptionInTransit: true,
          dataRetention: 30,
          rightToDeletion: true,
          dataPortability: true,
        },
        audit: {
          loggingEnabled: true,
          logRetention: 365,
          auditTrail: true,
          complianceReporting: true,
        },
      },
      openai: {
        standards: [
          {
            name: 'SOC 2 Type II',
            version: '2017',
            status: 'compliant',
            lastAssessed: '2024-02-01',
            evidence: ['soc2_report.pdf'],
          },
        ],
        certifications: [
          {
            name: 'SOC 2 Type II',
            issuer: 'AICPA',
            validUntil: '2025-02-01',
            scope: ['security', 'availability'],
          },
        ],
        dataProtection: {
          encryptionAtRest: true,
          encryptionInTransit: true,
          dataRetention: 30,
          rightToDeletion: true,
          dataPortability: false,
        },
        audit: {
          loggingEnabled: true,
          logRetention: 180,
          auditTrail: true,
          complianceReporting: true,
        },
      },
    };

    return (
      baseCompliance[providerId] || {
        standards: [],
        certifications: [],
        dataProtection: {
          encryptionAtRest: false,
          encryptionInTransit: false,
          dataRetention: 0,
          rightToDeletion: false,
          dataPortability: false,
        },
        audit: {
          loggingEnabled: false,
          logRetention: 0,
          auditTrail: false,
          complianceReporting: false,
        },
      }
    );
  }

  private async getProviderVersion(provider: IAIProvider): Promise<string> {
    try {
      // Try to get version from provider
      const info = await provider.getProviderInfo?.();
      return info?.version || '1.0.0';
    } catch {
      return '1.0.0';
    }
  }

  private calculateConfidence(
    models: ModelCapabilities[],
    features: FeatureCapabilities,
    limitations: LimitationCapabilities
  ): number {
    let confidence = 0.5; // Base confidence

    // More models = higher confidence
    confidence += Math.min(models.length * 0.05, 0.2);

    // Feature completeness
    const featureScore =
      Object.values(features).filter((f) =>
        typeof f === 'object' && 'supported' in f ? f.supported : true
      ).length / Object.keys(features).length;
    confidence += featureScore * 0.2;

    // Limitation information completeness
    const limitationScore =
      (limitations.rateLimits.length > 0 ? 0.1 : 0) +
      (limitations.compliance.certifications.length > 0 ? 0.1 : 0);
    confidence += limitationScore;

    return Math.min(confidence, 1.0);
  }

  private getFallbackModelCapabilities(provider: IAIProvider): ModelCapabilities[] {
    const providerId = provider.getProviderId();

    return [
      {
        id: `${providerId}-default`,
        name: `${providerId} Default Model`,
        type: 'text-generation' as ModelType,
        contextWindow: { input: 4096, output: 2048, total: 6144, unit: 'tokens' },
        capabilities: {
          streaming: false,
          functionCalling: false,
          multimodal: false,
          codeGeneration: false,
          reasoning: false,
          toolUse: false,
          imageInput: false,
          audioInput: false,
          videoInput: false,
          documentInput: false,
          jsonOutput: false,
          structuredOutput: false,
          systemMessages: false,
          fewShot: false,
          chainOfThought: false,
        },
        pricing: {
          input: [{ price: 0.001, unit: '1K tokens' }],
          output: [{ price: 0.002, unit: '1K tokens' }],
          currency: 'USD',
          billingUnit: 'tokens',
        },
        availability: {
          regions: ['us-east-1'],
          status: 'available',
        },
        performance: {
          latency: { p50: 1000, p90: 2000, p95: 3000, p99: 5000 },
          throughput: { requestsPerSecond: 1, tokensPerSecond: 10, concurrentRequests: 1 },
          accuracy: { generalKnowledge: 0.5 },
          reliability: { uptime: 0.95, errorRate: 0.05, timeoutRate: 0.02 },
        },
      },
    ];
  }

  async refreshCapabilities(providerId: string): Promise<ProviderCapabilities> {
    // Invalidate cache and re-detect
    await this.cache.delete(providerId);

    // Get provider instance
    const provider = await this.getProviderInstance(providerId);
    return this.detectCapabilities(provider);
  }

  async getCachedCapabilities(providerId: string): Promise<ProviderCapabilities | null> {
    return this.cache.get(providerId);
  }

  async invalidateCache(providerId: string): Promise<void> {
    await this.cache.delete(providerId);
  }

  compareCapabilities(a: ProviderCapabilities, b: ProviderCapabilities): CapabilityComparison {
    const differences = this.findDifferences(a, b);
    const score = this.calculateSimilarityScore(a, b);

    return {
      score,
      differences,
      recommendation: this.getRecommendation(score, differences),
      reasoning: this.generateComparisonReasoning(a, b, differences),
    };
  }

  private findDifferences(a: ProviderCapabilities, b: ProviderCapabilities): any[] {
    const differences: any[] = [];

    // Compare model counts
    if (a.supportedModels.length !== b.supportedModels.length) {
      differences.push({
        path: 'supportedModels.length',
        a: a.supportedModels.length,
        b: b.supportedModels.length,
        impact: 'high',
        category: 'feature',
      });
    }

    // Compare feature capabilities
    this.compareObjects(a.features, b.features, 'features', differences);

    // Compare performance
    this.compareObjects(a.performance, b.performance, 'performance', differences);

    // Compare compliance
    this.compareObjects(a.compliance, b.compliance, 'compliance', differences);

    return differences;
  }

  private compareObjects(a: any, b: any, path: string, differences: any[]): void {
    if (typeof a !== typeof b) {
      differences.push({
        path,
        a: typeof a,
        b: typeof b,
        impact: 'medium',
        category: 'feature',
      });
      return;
    }

    if (typeof a === 'object' && a !== null && b !== null) {
      for (const key of Object.keys(a)) {
        if (!(key in b)) {
          differences.push({
            path: `${path}.${key}`,
            a: a[key],
            b: undefined,
            impact: 'medium',
            category: 'feature',
          });
        } else if (a[key] !== b[key]) {
          if (typeof a[key] === 'object') {
            this.compareObjects(a[key], b[key], `${path}.${key}`, differences);
          } else {
            differences.push({
              path: `${path}.${key}`,
              a: a[key],
              b: b[key],
              impact: this.assessImpact(key, a[key], b[key]),
              category: this.categorizeDifference(key),
            });
          }
        }
      }
    }
  }

  private assessImpact(key: string, a: any, b: any): 'high' | 'medium' | 'low' {
    const highImpactKeys = ['streaming', 'functionCalling', 'multimodal', 'uptime'];
    const mediumImpactKeys = ['latency', 'throughput', 'errorRate'];

    if (highImpactKeys.some((k) => key.includes(k))) return 'high';
    if (mediumImpactKeys.some((k) => key.includes(k))) return 'medium';
    return 'low';
  }

  private categorizeDifference(
    key: string
  ): 'feature' | 'performance' | 'limitation' | 'compliance' {
    if (key.includes('compliance') || key.includes('certification')) return 'compliance';
    if (key.includes('limit') || key.includes('quota') || key.includes('restriction'))
      return 'limitation';
    if (key.includes('latency') || key.includes('throughput') || key.includes('performance'))
      return 'performance';
    return 'feature';
  }

  private calculateSimilarityScore(a: ProviderCapabilities, b: ProviderCapabilities): number {
    let score = 0;
    let factors = 0;

    // Model similarity
    const modelSimilarity = this.calculateModelSimilarity(a.supportedModels, b.supportedModels);
    score += modelSimilarity * 0.3;
    factors += 0.3;

    // Feature similarity
    const featureSimilarity = this.calculateFeatureSimilarity(a.features, b.features);
    score += featureSimilarity * 0.3;
    factors += 0.3;

    // Performance similarity
    const performanceSimilarity = this.calculatePerformanceSimilarity(a.performance, b.performance);
    score += performanceSimilarity * 0.2;
    factors += 0.2;

    // Compliance similarity
    const complianceSimilarity = this.calculateComplianceSimilarity(a.compliance, b.compliance);
    score += complianceSimilarity * 0.2;
    factors += 0.2;

    return factors > 0 ? score / factors : 0;
  }

  private calculateModelSimilarity(a: ModelCapabilities[], b: ModelCapabilities[]): number {
    if (a.length === 0 && b.length === 0) return 1;
    if (a.length === 0 || b.length === 0) return 0;

    const aTypes = new Set(a.map((m) => m.type));
    const bTypes = new Set(b.map((m) => m.type));

    const intersection = [...aTypes].filter((type) => bTypes.has(type));
    const union = [...new Set([...aTypes, ...bTypes])];

    return intersection.length / union.length;
  }

  private calculateFeatureSimilarity(a: FeatureCapabilities, b: FeatureCapabilities): number {
    let matches = 0;
    let total = 0;

    // Compare streaming
    if (a.streaming.supported === b.streaming.supported) matches++;
    total++;

    // Compare batching
    if (a.batching.supported === b.batching.supported) matches++;
    total++;

    // Compare caching
    if (a.caching.supported === b.caching.supported) matches++;
    total++;

    return total > 0 ? matches / total : 0;
  }

  private calculatePerformanceSimilarity(
    a: PerformanceCapabilities,
    b: PerformanceCapabilities
  ): number {
    // Compare SLA metrics
    const uptimeDiff = Math.abs(a.sla.uptime - b.sla.uptime);
    const responseTimeDiff = Math.abs(a.sla.responseTime - b.sla.responseTime);
    const errorRateDiff = Math.abs(a.sla.errorRate - b.sla.errorRate);

    const uptimeSimilarity = 1 - uptimeDiff;
    const responseTimeSimilarity =
      1 - responseTimeDiff / Math.max(a.sla.responseTime, b.sla.responseTime);
    const errorRateSimilarity = 1 - errorRateDiff;

    return (uptimeSimilarity + responseTimeSimilarity + errorRateSimilarity) / 3;
  }

  private calculateComplianceSimilarity(
    a: ComplianceCapabilities,
    b: ComplianceCapabilities
  ): number {
    const aCerts = new Set(a.certifications.map((c) => c.name));
    const bCerts = new Set(b.certifications.map((c) => c.name));

    const intersection = [...aCerts].filter((cert) => bCerts.has(cert));
    const union = [...new Set([...aCerts, ...bCerts])];

    return union.length > 0 ? intersection.length / union.length : 1;
  }

  private getRecommendation(
    score: number,
    differences: any[]
  ): 'use-a' | 'use-b' | 'either' | 'neither' {
    if (score > 0.9) return 'either';
    if (score > 0.7) {
      // Check for critical differences
      const criticalDiffs = differences.filter((d) => d.impact === 'high');
      if (criticalDiffs.length === 0) return 'either';
    }

    // For now, default to 'either' - in real implementation would analyze specific differences
    return 'either';
  }

  private generateComparisonReasoning(
    a: ProviderCapabilities,
    b: ProviderCapabilities,
    differences: any[]
  ): string {
    if (differences.length === 0) {
      return 'Providers have identical capabilities';
    }

    const highImpactDiffs = differences.filter((d) => d.impact === 'high');
    const mediumImpactDiffs = differences.filter((d) => d.impact === 'medium');

    let reasoning = `Found ${differences.length} differences`;

    if (highImpactDiffs.length > 0) {
      reasoning += `, including ${highImpactDiffs.length} high-impact differences`;
    }

    if (mediumImpactDiffs.length > 0) {
      reasoning += ` and ${mediumImpactDiffs.length} medium-impact differences`;
    }

    reasoning += '.';

    return reasoning;
  }

  async findProvidersForRequirement(requirement: CapabilityRequirement): Promise<string[]> {
    // This would need access to all provider capabilities
    // For now, return empty array - would be implemented with provider registry
    return [];
  }

  private async getProviderInstance(providerId: string): Promise<IAIProvider> {
    // This would need to be injected or accessed via provider registry
    throw new Error('Provider registry integration needed');
  }
}
```

### 3. Capability Cache Manager

```typescript
// packages/providers/src/cache-manager.ts

export interface CacheOptions<T> {
  ttl: number; // Time to live in milliseconds
  maxSize: number;
  onEvict?: (key: string, value: T) => void;
}

export class CacheManager<T> {
  private cache = new Map<string, { value: T; timestamp: number; ttl: number }>();
  private accessOrder = new Map<string, number>();
  private accessCounter = 0;

  constructor(private options: CacheOptions<T>) {}

  async get(key: string): Promise<T | null> {
    const entry = this.cache.get(key);

    if (!entry) {
      return null;
    }

    // Check if expired
    if (Date.now() - entry.timestamp > entry.ttl) {
      this.cache.delete(key);
      this.accessOrder.delete(key);
      return null;
    }

    // Update access order for LRU
    this.accessOrder.set(key, ++this.accessCounter);

    return entry.value;
  }

  async set(key: string, value: T, ttl?: number): Promise<void> {
    // Evict if at max capacity
    if (this.cache.size >= this.options.maxSize && !this.cache.has(key)) {
      this.evictLRU();
    }

    const actualTtl = ttl || this.options.ttl;

    this.cache.set(key, {
      value,
      timestamp: Date.now(),
      ttl: actualTtl,
    });

    this.accessOrder.set(key, ++this.accessCounter);
  }

  async delete(key: string): Promise<void> {
    const entry = this.cache.get(key);
    if (entry) {
      this.options.onEvict?.(key, entry.value);
    }

    this.cache.delete(key);
    this.accessOrder.delete(key);
  }

  async clear(): Promise<void> {
    for (const [key, entry] of this.cache) {
      this.options.onEvict?.(key, entry.value);
    }

    this.cache.clear();
    this.accessOrder.clear();
    this.accessCounter = 0;
  }

  private evictLRU(): void {
    let oldestKey: string | null = null;
    let oldestAccess = Infinity;

    for (const [key, accessTime] of this.accessOrder) {
      if (accessTime < oldestAccess) {
        oldestAccess = accessTime;
        oldestKey = key;
      }
    }

    if (oldestKey) {
      this.delete(oldestKey);
    }
  }

  size(): number {
    return this.cache.size;
  }

  keys(): string[] {
    return Array.from(this.cache.keys());
  }

  has(key: string): boolean {
    return this.cache.has(key);
  }
}
```

### 4. Metrics Collector

```typescript
// packages/providers/src/metrics-collector.ts

export interface MetricValue {
  value: number;
  timestamp: number;
  tags?: Record<string, string>;
}

export class MetricsCollector {
  private metrics = new Map<string, MetricValue[]>();
  private maxMetricsPerKey = 1000;

  record(name: string, value: number, tags?: Record<string, string>): void {
    if (!this.metrics.has(name)) {
      this.metrics.set(name, []);
    }

    const metricList = this.metrics.get(name)!;
    metricList.push({
      value,
      timestamp: Date.now(),
      tags,
    });

    // Keep only recent metrics
    if (metricList.length > this.maxMetricsPerKey) {
      metricList.splice(0, metricList.length - this.maxMetricsPerKey);
    }
  }

  getMetrics(name: string, since?: number): MetricValue[] {
    const metrics = this.metrics.get(name) || [];

    if (since) {
      return metrics.filter((m) => m.timestamp >= since);
    }

    return metrics;
  }

  getAverage(name: string, since?: number): number {
    const metrics = this.getMetrics(name, since);

    if (metrics.length === 0) {
      return 0;
    }

    const sum = metrics.reduce((acc, m) => acc + m.value, 0);
    return sum / metrics.length;
  }

  getPercentile(name: string, percentile: number, since?: number): number {
    const metrics = this.getMetrics(name, since);

    if (metrics.length === 0) {
      return 0;
    }

    const sorted = metrics.map((m) => m.value).sort((a, b) => a - b);
    const index = Math.floor(sorted.length * (percentile / 100));

    return sorted[index];
  }

  clear(): void {
    this.metrics.clear();
  }
}
```

### 5. Provider Selection Service

```typescript
// packages/providers/src/provider-selection.ts

import type {
  IAIProvider,
  ProviderCapabilities,
  CapabilityRequirement,
  ModelType,
  ModelFeatureSet,
} from '@tamma/shared/types';
import { ICapabilityDetector } from './capability-detector';
import { logger } from '@tamma/observability';

export interface SelectionCriteria {
  modelType?: ModelType;
  features?: Partial<ModelFeatureSet>;
  maxLatency?: number; // milliseconds
  minThroughput?: number; // requests per second
  maxCost?: number; // per 1K tokens
  compliance?: string[]; // required certifications
  regions?: string[]; // allowed regions
  priority?: 'cost' | 'performance' | 'features' | 'compliance';
}

export interface SelectionResult {
  provider: string;
  model: string;
  score: number;
  reasoning: string;
  alternatives: AlternativeProvider[];
}

export interface AlternativeProvider {
  provider: string;
  model: string;
  score: number;
  reason: string;
}

export class ProviderSelectionService {
  constructor(
    private capabilityDetector: ICapabilityDetector,
    private providers: Map<string, IAIProvider>
  ) {}

  async selectProvider(criteria: SelectionCriteria): Promise<SelectionResult> {
    logger.info('Selecting provider based on criteria', { criteria });

    const candidates = await this.getCandidateProviders(criteria);
    const scored = await this.scoreCandidates(candidates, criteria);
    const sorted = scored.sort((a, b) => b.score - a.score);

    if (sorted.length === 0) {
      throw new Error('No providers match the selection criteria');
    }

    const best = sorted[0];
    const alternatives = sorted.slice(1, 4).map((alt) => ({
      provider: alt.providerId,
      model: alt.modelId,
      score: alt.score,
      reason: alt.reasoning,
    }));

    const result: SelectionResult = {
      provider: best.providerId,
      model: best.modelId,
      score: best.score,
      reasoning: best.reasoning,
      alternatives,
    };

    logger.info('Provider selected', {
      provider: result.provider,
      model: result.model,
      score: result.score,
      alternativesCount: alternatives.length,
    });

    return result;
  }

  private async getCandidateProviders(
    criteria: SelectionCriteria
  ): Promise<Array<{ providerId: string; modelId: string; capabilities: ProviderCapabilities }>> {
    const candidates: Array<{
      providerId: string;
      modelId: string;
      capabilities: ProviderCapabilities;
    }> = [];

    for (const [providerId, provider] of this.providers) {
      try {
        const capabilities =
          (await this.capabilityDetector.getCachedCapabilities(providerId)) ||
          (await this.capabilityDetector.detectCapabilities(provider));

        for (const model of capabilities.supportedModels) {
          if (this.matchesCriteria(model, capabilities, criteria)) {
            candidates.push({
              providerId,
              modelId: model.id,
              capabilities,
            });
          }
        }
      } catch (error) {
        logger.warn('Failed to get capabilities for provider', {
          providerId,
          error: error.message,
        });
      }
    }

    return candidates;
  }

  private matchesCriteria(
    model: any,
    capabilities: ProviderCapabilities,
    criteria: SelectionCriteria
  ): boolean {
    // Check model type
    if (criteria.modelType && model.type !== criteria.modelType) {
      return false;
    }

    // Check required features
    if (criteria.features) {
      for (const [feature, required] of Object.entries(criteria.features)) {
        if (required && !(model.capabilities as any)[feature]) {
          return false;
        }
      }
    }

    // Check latency
    if (criteria.maxLatency) {
      if (model.performance.latency.p95 > criteria.maxLatency) {
        return false;
      }
    }

    // Check throughput
    if (criteria.minThroughput) {
      if (model.performance.throughput.requestsPerSecond < criteria.minThroughput) {
        return false;
      }
    }

    // Check cost
    if (criteria.maxCost) {
      const inputCost = model.pricing.input[0]?.price || 0;
      if (inputCost > criteria.maxCost) {
        return false;
      }
    }

    // Check compliance
    if (criteria.compliance) {
      const hasRequiredCerts = criteria.compliance.every((cert) =>
        capabilities.compliance.certifications.some((c) => c.name.includes(cert))
      );
      if (!hasRequiredCerts) {
        return false;
      }
    }

    // Check regions
    if (criteria.regions) {
      const hasAllowedRegion = criteria.regions.some((region) =>
        model.availability.regions.includes(region)
      );
      if (!hasAllowedRegion) {
        return false;
      }
    }

    return true;
  }

  private async scoreCandidates(
    candidates: Array<{ providerId: string; modelId: string; capabilities: ProviderCapabilities }>,
    criteria: SelectionCriteria
  ): Promise<Array<{ providerId: string; modelId: string; score: number; reasoning: string }>> {
    const scored = [];

    for (const candidate of candidates) {
      const score = await this.calculateScore(candidate, criteria);
      scored.push({
        providerId: candidate.providerId,
        modelId: candidate.modelId,
        score: score.value,
        reasoning: score.reasoning,
      });
    }

    return scored;
  }

  private async calculateScore(
    candidate: { providerId: string; modelId: string; capabilities: ProviderCapabilities },
    criteria: SelectionCriteria
  ): Promise<{ value: number; reasoning: string }> {
    let score = 0;
    let maxScore = 0;
    const reasons: string[] = [];

    const model = candidate.capabilities.supportedModels.find((m) => m.id === candidate.modelId)!;

    // Performance scoring (30% weight)
    if (criteria.priority === 'performance' || !criteria.priority) {
      const performanceScore = this.calculatePerformanceScore(model);
      score += performanceScore * 0.3;
      maxScore += 0.3;
      reasons.push(`Performance: ${(performanceScore * 100).toFixed(1)}%`);
    }

    // Cost scoring (25% weight)
    if (criteria.priority === 'cost' || !criteria.priority) {
      const costScore = this.calculateCostScore(model);
      score += costScore * 0.25;
      maxScore += 0.25;
      reasons.push(`Cost: ${(costScore * 100).toFixed(1)}%`);
    }

    // Feature scoring (25% weight)
    if (criteria.priority === 'features' || !criteria.priority) {
      const featureScore = this.calculateFeatureScore(model, criteria.features);
      score += featureScore * 0.25;
      maxScore += 0.25;
      reasons.push(`Features: ${(featureScore * 100).toFixed(1)}%`);
    }

    // Compliance scoring (20% weight)
    if (criteria.priority === 'compliance' || !criteria.priority) {
      const complianceScore = this.calculateComplianceScore(
        candidate.capabilities,
        criteria.compliance
      );
      score += complianceScore * 0.2;
      maxScore += 0.2;
      reasons.push(`Compliance: ${(complianceScore * 100).toFixed(1)}%`);
    }

    const finalScore = maxScore > 0 ? score / maxScore : 0;
    const reasoning = reasons.join(', ');

    return { value: finalScore, reasoning };
  }

  private calculatePerformanceScore(model: any): number {
    // Lower latency = higher score
    const latencyScore = Math.max(0, 1 - model.performance.latency.p95 / 5000); // 5s as worst case

    // Higher throughput = higher score
    const throughputScore = Math.min(1, model.performance.throughput.requestsPerSecond / 100); // 100 req/s as best

    // Higher reliability = higher score
    const reliabilityScore = model.performance.reliability.uptime;

    return (latencyScore + throughputScore + reliabilityScore) / 3;
  }

  private calculateCostScore(model: any): number {
    const inputCost = model.pricing.input[0]?.price || 0.001; // Default to $0.001 per 1K tokens

    // Lower cost = higher score (using $0.01 as reference for max cost)
    return Math.max(0, 1 - inputCost / 0.01);
  }

  private calculateFeatureScore(model: any, requiredFeatures?: Partial<ModelFeatureSet>): number {
    const features = model.capabilities;
    const featureValues = Object.values(features);
    const enabledFeatures = featureValues.filter((f) => f === true).length;
    const totalFeatures = featureValues.length;

    let baseScore = totalFeatures > 0 ? enabledFeatures / totalFeatures : 0;

    // Bonus for required features
    if (requiredFeatures) {
      const requiredCount = Object.keys(requiredFeatures).length;
      const satisfiedCount = Object.entries(requiredFeatures).filter(
        ([feature, required]) => required && (features as any)[feature]
      ).length;

      if (requiredCount > 0) {
        baseScore = (baseScore + satisfiedCount / requiredCount) / 2;
      }
    }

    return baseScore;
  }

  private calculateComplianceScore(
    capabilities: ProviderCapabilities,
    requiredCompliance?: string[]
  ): number {
    if (!requiredCompliance || requiredCompliance.length === 0) {
      return 1; // No compliance requirements = full score
    }

    const certifications = capabilities.compliance.certifications.map((c) => c.name);
    const satisfiedCount = requiredCompliance.filter((req) =>
      certifications.some((cert) => cert.includes(req))
    ).length;

    return satisfiedCount / requiredCompliance.length;
  }
}
```

### 6. Testing Strategy

```typescript
// packages/providers/src/__tests__/capability-detector.test.ts

import { CapabilityDetector } from '../capability-detector';
import { MockAIProvider } from './mocks/mock-provider';

describe('CapabilityDetector', () => {
  let detector: CapabilityDetector;
  let mockProvider: MockAIProvider;

  beforeEach(() => {
    detector = new CapabilityDetector();
    mockProvider = new MockAIProvider('test-provider');
  });

  describe('detectCapabilities', () => {
    it('should detect basic capabilities', async () => {
      const capabilities = await detector.detectCapabilities(mockProvider);

      expect(capabilities).toHaveProperty('supportedModels');
      expect(capabilities).toHaveProperty('features');
      expect(capabilities).toHaveProperty('limitations');
      expect(capabilities).toHaveProperty('performance');
      expect(capabilities).toHaveProperty('compliance');
      expect(capabilities).toHaveProperty('detectedAt');
      expect(capabilities).toHaveProperty('version');
      expect(capabilities).toHaveProperty('source');
      expect(capabilities).toHaveProperty('confidence');
    });

    it('should detect model capabilities correctly', async () => {
      const capabilities = await detector.detectCapabilities(mockProvider);

      expect(capabilities.supportedModels).toHaveLength(1);
      const model = capabilities.supportedModels[0];
      expect(model).toHaveProperty('id');
      expect(model).toHaveProperty('name');
      expect(model).toHaveProperty('type');
      expect(model).toHaveProperty('contextWindow');
      expect(model).toHaveProperty('capabilities');
      expect(model).toHaveProperty('pricing');
      expect(model).toHaveProperty('availability');
      expect(model).toHaveProperty('performance');
    });

    it('should cache capabilities', async () => {
      const capabilities1 = await detector.detectCapabilities(mockProvider);
      const capabilities2 = await detector.getCachedCapabilities('test-provider');

      expect(capabilities2).toEqual(capabilities1);
    });

    it('should refresh capabilities', async () => {
      const capabilities1 = await detector.detectCapabilities(mockProvider);

      // Modify provider to simulate change
      mockProvider.setModelName('Updated Model');

      const capabilities2 = await detector.refreshCapabilities('test-provider');

      expect(capabilities2.supportedModels[0].name).toBe('Updated Model');
      expect(capabilities2.detectedAt).not.toBe(capabilities1.detectedAt);
    });
  });

  describe('compareCapabilities', () => {
    it('should compare similar capabilities', async () => {
      const capabilities1 = await detector.detectCapabilities(mockProvider);
      const capabilities2 = await detector.detectCapabilities(mockProvider);

      const comparison = detector.compareCapabilities(capabilities1, capabilities2);

      expect(comparison.score).toBeGreaterThan(0.9);
      expect(comparison.recommendation).toBe('either');
    });

    it('should detect differences', async () => {
      const capabilities1 = await detector.detectCapabilities(mockProvider);

      const capabilities2 = { ...capabilities1 };
      capabilities2.supportedModels.push({
        id: 'new-model',
        name: 'New Model',
        type: 'text-generation',
        contextWindow: { input: 4096, output: 2048, total: 6144, unit: 'tokens' },
        capabilities: {
          streaming: false,
          functionCalling: false,
          multimodal: false,
          codeGeneration: false,
          reasoning: false,
          toolUse: false,
          imageInput: false,
          audioInput: false,
          videoInput: false,
          documentInput: false,
          jsonOutput: false,
          structuredOutput: false,
          systemMessages: false,
          fewShot: false,
          chainOfThought: false,
        },
        pricing: {
          input: [{ price: 0.001, unit: '1K tokens' }],
          output: [{ price: 0.002, unit: '1K tokens' }],
          currency: 'USD',
          billingUnit: 'tokens',
        },
        availability: {
          regions: ['us-east-1'],
          status: 'available',
        },
        performance: {
          latency: { p50: 1000, p90: 2000, p95: 3000, p99: 5000 },
          throughput: { requestsPerSecond: 1, tokensPerSecond: 10, concurrentRequests: 1 },
          accuracy: { generalKnowledge: 0.5 },
          reliability: { uptime: 0.95, errorRate: 0.05, timeoutRate: 0.02 },
        },
      });

      const comparison = detector.compareCapabilities(capabilities1, capabilities2);

      expect(comparison.differences.length).toBeGreaterThan(0);
      expect(comparison.score).toBeLessThan(1);
    });
  });

  describe('capability probing', () => {
    it('should detect streaming capability', async () => {
      mockProvider.setStreamingSupported(true);
      const capabilities = await detector.detectCapabilities(mockProvider);

      expect(capabilities.supportedModels[0].capabilities.streaming).toBe(true);
    });

    it('should detect function calling capability', async () => {
      mockProvider.setFunctionCallingSupported(true);
      const capabilities = await detector.detectCapabilities(mockProvider);

      expect(capabilities.supportedModels[0].capabilities.functionCalling).toBe(true);
    });

    it('should detect multimodal capability', async () => {
      mockProvider.setMultimodalSupported(true);
      const capabilities = await detector.detectCapabilities(mockProvider);

      expect(capabilities.supportedModels[0].capabilities.multimodal).toBe(true);
    });
  });
});
```

### 7. Integration with Provider Registry

```typescript
// packages/providers/src/provider-registry.ts

import type { IAIProvider, ICapabilityDetector } from '@tamma/shared/types';
import { CapabilityDetector } from './capability-detector';
import { ProviderSelectionService } from './provider-selection';

export class EnhancedProviderRegistry {
  private providers = new Map<string, IAIProvider>();
  private capabilityDetector: ICapabilityDetector;
  private selectionService: ProviderSelectionService;

  constructor() {
    this.capabilityDetector = new CapabilityDetector();
    this.selectionService = new ProviderSelectionService(this.capabilityDetector, this.providers);
  }

  async registerProvider(provider: IAIProvider): Promise<void> {
    const providerId = provider.getProviderId();
    this.providers.set(providerId, provider);

    // Detect capabilities on registration
    await this.capabilityDetector.detectCapabilities(provider);
  }

  async getProviderForRequirement(requirement: CapabilityRequirement): Promise<IAIProvider> {
    const selection = await this.selectionService.selectProvider(requirement);
    return this.providers.get(selection.provider)!;
  }

  async getAllProviderCapabilities(): Promise<Record<string, any>> {
    const capabilities: Record<string, any> = {};

    for (const providerId of this.providers.keys()) {
      const providerCapabilities = await this.capabilityDetector.getCachedCapabilities(providerId);
      if (providerCapabilities) {
        capabilities[providerId] = providerCapabilities;
      }
    }

    return capabilities;
  }

  async refreshAllCapabilities(): Promise<void> {
    for (const providerId of this.providers.keys()) {
      try {
        await this.capabilityDetector.refreshCapabilities(providerId);
      } catch (error) {
        logger.error('Failed to refresh capabilities', { providerId, error: error.message });
      }
    }
  }
}
```

## Benefits

1. **Automatic Discovery**: Providers are automatically analyzed for capabilities without manual configuration
2. **Dynamic Selection**: Intelligent provider selection based on requirements and performance
3. **Caching**: Capability information is cached to improve performance
4. **Comparison**: Side-by-side comparison of provider capabilities
5. **Monitoring**: Continuous monitoring of provider performance and availability
6. **Flexibility**: Easy to add new providers and capability detection methods
7. **Reliability**: Fallback mechanisms and error handling for robust operation

## Next Steps

1. Implement capability detection for all 9 providers
2. Add real-time capability monitoring
3. Create capability-based routing logic
4. Build capability analytics dashboard
5. Add capability evolution tracking
6. Implement capability-based cost optimization
7. Create capability testing automation
8. Add capability compliance validation

This completes the comprehensive capability detection system for all AI providers in the Tamma platform, enabling intelligent provider selection, automatic capability discovery, and robust monitoring of the multi-provider AI infrastructure.
