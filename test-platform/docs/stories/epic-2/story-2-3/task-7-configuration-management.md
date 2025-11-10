# Task 7: Configuration Management

## Overview

Implement comprehensive configuration management for the OpenAI provider, supporting multiple configuration sources, environment-specific settings, validation, and hot-reloading capabilities.

## Objectives

- Implement flexible configuration system with multiple sources
- Add environment-specific configuration management
- Provide configuration validation and schema enforcement
- Support hot-reloading and configuration change notifications

## Implementation Steps

### Subtask 7.1: Implement Configuration Schema and Validation

**Description**: Create a robust configuration schema with validation, type checking, and default values for all OpenAI provider settings.

**Implementation Details**:

1. **Create Configuration Schema**:

```typescript
// packages/providers/src/interfaces/config-schema.interface.ts
export interface OpenAIProviderConfig {
  // Core API configuration
  apiKey: string;
  organization?: string;
  baseURL?: string;
  timeout?: number;
  maxRetries?: number;

  // Model configuration
  defaultModel?: string;
  supportedModels: ModelConfig[];
  modelDefaults: Record<string, ModelDefaults>;

  // Rate limiting configuration
  rateLimit: RateLimitConfig;

  // Retry configuration
  retry: RetryConfig;

  // Circuit breaker configuration
  circuitBreaker: CircuitBreakerConfig;

  // Function calling configuration
  functionCalling: FunctionCallingConfig;

  // Token and cost management
  costManagement: CostManagementConfig;

  // Security configuration
  security: SecurityConfig;

  // Logging and monitoring
  logging: LoggingConfig;

  // Environment-specific overrides
  environments?: Record<string, Partial<OpenAIProviderConfig>>;

  // Feature flags
  features: FeatureFlags;
}

export interface ModelConfig {
  name: string;
  displayName: string;
  maxTokens: number;
  inputCostPer1K: number;
  outputCostPer1K: number;
  supportsStreaming: boolean;
  supportsFunctionCalling: boolean;
  supportsVision: boolean;
  deprecated?: boolean;
  deprecationDate?: string;
  alternatives?: string[];
}

export interface ModelDefaults {
  temperature?: number;
  maxTokens?: number;
  topP?: number;
  frequencyPenalty?: number;
  presencePenalty?: number;
  stop?: string[];
}

export interface RateLimitConfig {
  requestsPerMinute: number;
  tokensPerMinute: number;
  requestsPerDay: number;
  burstAllowance: number;
  organizationLimits?: Record<string, Partial<RateLimitConfig>>;
  modelLimits?: Record<string, Partial<RateLimitConfig>>;
}

export interface RetryConfig {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
  retryableStatusCodes: number[];
  customRetryStrategies?: Record<string, string>;
}

export interface CircuitBreakerConfig {
  failureThreshold: number;
  recoveryTimeout: number;
  monitoringPeriod: number;
  expectedRecoveryTime: number;
  halfOpenMaxCalls: number;
}

export interface FunctionCallingConfig {
  enabled: boolean;
  autoExecute: boolean;
  maxConcurrentCalls: number;
  defaultTimeout: number;
  allowedTools?: string[];
  blockedTools?: string[];
  sandbox: SandboxConfig;
}

export interface SandboxConfig {
  maxExecutionTime: number;
  maxMemoryUsage: number;
  maxFileSize: number;
  allowedDomains: string[];
  blockedDomains: string[];
  allowedPaths: string[];
  blockedPaths: string[];
  enableNetworkAccess: boolean;
  enableFileSystemAccess: boolean;
  enableSystemCommands: boolean;
}

export interface CostManagementConfig {
  enabled: boolean;
  budgetLimits: BudgetLimits;
  alerts: AlertConfig;
  tracking: TrackingConfig;
}

export interface BudgetLimits {
  dailyLimit?: number;
  monthlyLimit?: number;
  perUserLimits?: Record<string, UserBudgetLimit>;
  perOrganizationLimits?: Record<string, UserBudgetLimit>;
}

export interface UserBudgetLimit {
  dailyLimit?: number;
  monthlyLimit?: number;
  alertThresholds?: number[];
}

export interface AlertConfig {
  enabled: boolean;
  thresholds: number[];
  channels: AlertChannel[];
  cooldownPeriod: number;
}

export interface AlertChannel {
  type: 'email' | 'webhook' | 'slack' | 'teams';
  config: Record<string, any>;
  enabled: boolean;
}

export interface TrackingConfig {
  enabled: boolean;
  granularity: 'request' | 'user' | 'organization';
  retentionPeriod: number;
  anonymizeData: boolean;
}

export interface SecurityConfig {
  encryptApiKeys: boolean;
  keyRotationInterval: number;
  auditLogging: boolean;
  ipWhitelist?: string[];
  allowedOrigins?: string[];
  requestSigning: boolean;
  rateLimitByUser: boolean;
}

export interface LoggingConfig {
  level: 'debug' | 'info' | 'warn' | 'error';
  format: 'json' | 'text';
  includeRequestBody: boolean;
  includeResponseBody: boolean;
  sanitizeSensitiveData: boolean;
  outputs: LogOutput[];
}

export interface LogOutput {
  type: 'console' | 'file' | 'http' | 'database';
  config: Record<string, any>;
  enabled: boolean;
}

export interface FeatureFlags {
  streamingEnabled: boolean;
  functionCallingEnabled: boolean;
  costTrackingEnabled: boolean;
  advancedRetryEnabled: boolean;
  circuitBreakerEnabled: boolean;
  betaFeatures: boolean;
  debugMode: boolean;
}

export interface ConfigValidationResult {
  valid: boolean;
  errors: ConfigValidationError[];
  warnings: ConfigValidationWarning[];
}

export interface ConfigValidationError {
  path: string;
  message: string;
  code: string;
  severity: 'error';
}

export interface ConfigValidationWarning {
  path: string;
  message: string;
  code: string;
  severity: 'warning';
}
```

2. **Implement Configuration Validator**:

```typescript
// packages/providers/src/openai/config-validator.ts
import {
  OpenAIProviderConfig,
  ConfigValidationResult,
  ConfigValidationError,
  ConfigValidationWarning,
} from '../interfaces/config-schema.interface';

export class ConfigValidator {
  private readonly REQUIRED_FIELDS = ['apiKey'];
  private readonly VALID_LOG_LEVELS = ['debug', 'info', 'warn', 'error'];
  private readonly VALID_LOG_FORMATS = ['json', 'text'];

  validate(config: Partial<OpenAIProviderConfig>): ConfigValidationResult {
    const errors: ConfigValidationError[] = [];
    const warnings: ConfigValidationWarning[] = [];

    // Validate required fields
    this.validateRequiredFields(config, errors);

    // Validate API configuration
    this.validateApiConfig(config, errors, warnings);

    // Validate model configuration
    this.validateModelConfig(config, errors, warnings);

    // Validate rate limiting
    this.validateRateLimitConfig(config, errors, warnings);

    // Validate retry configuration
    this.validateRetryConfig(config, errors, warnings);

    // Validate circuit breaker
    this.validateCircuitBreakerConfig(config, errors, warnings);

    // Validate function calling
    this.validateFunctionCallingConfig(config, errors, warnings);

    // Validate cost management
    this.validateCostManagementConfig(config, errors, warnings);

    // Validate security
    this.validateSecurityConfig(config, errors, warnings);

    // Validate logging
    this.validateLoggingConfig(config, errors, warnings);

    // Validate feature flags
    this.validateFeatureFlags(config, errors, warnings);

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  private validateRequiredFields(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[]
  ): void {
    for (const field of this.REQUIRED_FIELDS) {
      if (!(field in config) || config[field as keyof OpenAIProviderConfig] === undefined) {
        errors.push({
          path: field,
          message: `Required field '${field}' is missing`,
          code: 'REQUIRED_FIELD_MISSING',
          severity: 'error',
        });
      }
    }
  }

  private validateApiConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    // Validate API key format
    if (config.apiKey && typeof config.apiKey !== 'string') {
      errors.push({
        path: 'apiKey',
        message: 'API key must be a string',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (config.apiKey && !config.apiKey.startsWith('sk-')) {
      warnings.push({
        path: 'apiKey',
        message: 'API key should start with "sk-"',
        code: 'INVALID_FORMAT',
        severity: 'warning',
      });
    }

    // Validate timeout
    if (config.timeout !== undefined) {
      if (typeof config.timeout !== 'number' || config.timeout <= 0) {
        errors.push({
          path: 'timeout',
          message: 'Timeout must be a positive number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      } else if (config.timeout > 300000) {
        // 5 minutes
        warnings.push({
          path: 'timeout',
          message: 'Timeout is very high (>5 minutes), may cause issues',
          code: 'HIGH_VALUE',
          severity: 'warning',
        });
      }
    }

    // Validate max retries
    if (config.maxRetries !== undefined) {
      if (typeof config.maxRetries !== 'number' || config.maxRetries < 0) {
        errors.push({
          path: 'maxRetries',
          message: 'Max retries must be a non-negative number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      } else if (config.maxRetries > 10) {
        warnings.push({
          path: 'maxRetries',
          message: 'High retry count may cause excessive delays',
          code: 'HIGH_VALUE',
          severity: 'warning',
        });
      }
    }

    // Validate base URL
    if (config.baseURL) {
      try {
        new URL(config.baseURL);
      } catch {
        errors.push({
          path: 'baseURL',
          message: 'Base URL must be a valid URL',
          code: 'INVALID_URL',
          severity: 'error',
        });
      }
    }
  }

  private validateModelConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.supportedModels) return;

    if (!Array.isArray(config.supportedModels)) {
      errors.push({
        path: 'supportedModels',
        message: 'Supported models must be an array',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
      return;
    }

    const modelNames = new Set<string>();

    for (let i = 0; i < config.supportedModels.length; i++) {
      const model = config.supportedModels[i];
      const path = `supportedModels[${i}]`;

      // Check for duplicates
      if (modelNames.has(model.name)) {
        errors.push({
          path: `${path}.name`,
          message: `Duplicate model name: ${model.name}`,
          code: 'DUPLICATE_VALUE',
          severity: 'error',
        });
      }
      modelNames.add(model.name);

      // Validate model structure
      if (!model.name || typeof model.name !== 'string') {
        errors.push({
          path: `${path}.name`,
          message: 'Model name is required and must be a string',
          code: 'REQUIRED_FIELD_MISSING',
          severity: 'error',
        });
      }

      if (!model.displayName || typeof model.displayName !== 'string') {
        errors.push({
          path: `${path}.displayName`,
          message: 'Model display name is required and must be a string',
          code: 'REQUIRED_FIELD_MISSING',
          severity: 'error',
        });
      }

      if (typeof model.maxTokens !== 'number' || model.maxTokens <= 0) {
        errors.push({
          path: `${path}.maxTokens`,
          message: 'Max tokens must be a positive number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      }

      if (typeof model.inputCostPer1K !== 'number' || model.inputCostPer1K < 0) {
        errors.push({
          path: `${path}.inputCostPer1K`,
          message: 'Input cost per 1K must be a non-negative number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      }

      if (typeof model.outputCostPer1K !== 'number' || model.outputCostPer1K < 0) {
        errors.push({
          path: `${path}.outputCostPer1K`,
          message: 'Output cost per 1K must be a non-negative number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      }

      // Check for deprecated models
      if (model.deprecated && !model.alternatives?.length) {
        warnings.push({
          path: `${path}.deprecated`,
          message: 'Deprecated model should have alternatives specified',
          code: 'MISSING_ALTERNATIVES',
          severity: 'warning',
        });
      }
    }
  }

  private validateRateLimitConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.rateLimit) return;

    const { rateLimit } = config;

    if (typeof rateLimit.requestsPerMinute !== 'number' || rateLimit.requestsPerMinute <= 0) {
      errors.push({
        path: 'rateLimit.requestsPerMinute',
        message: 'Requests per minute must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof rateLimit.tokensPerMinute !== 'number' || rateLimit.tokensPerMinute <= 0) {
      errors.push({
        path: 'rateLimit.tokensPerMinute',
        message: 'Tokens per minute must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof rateLimit.requestsPerDay !== 'number' || rateLimit.requestsPerDay <= 0) {
      errors.push({
        path: 'rateLimit.requestsPerDay',
        message: 'Requests per day must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof rateLimit.burstAllowance !== 'number' || rateLimit.burstAllowance < 0) {
      errors.push({
        path: 'rateLimit.burstAllowance',
        message: 'Burst allowance must be a non-negative number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    } else if (rateLimit.burstAllowance > rateLimit.requestsPerMinute) {
      warnings.push({
        path: 'rateLimit.burstAllowance',
        message: 'Burst allowance should not exceed requests per minute',
        code: 'LOGIC_WARNING',
        severity: 'warning',
      });
    }
  }

  private validateRetryConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.retry) return;

    const { retry } = config;

    if (typeof retry.maxAttempts !== 'number' || retry.maxAttempts < 0) {
      errors.push({
        path: 'retry.maxAttempts',
        message: 'Max attempts must be a non-negative number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof retry.baseDelay !== 'number' || retry.baseDelay <= 0) {
      errors.push({
        path: 'retry.baseDelay',
        message: 'Base delay must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof retry.maxDelay !== 'number' || retry.maxDelay <= 0) {
      errors.push({
        path: 'retry.maxDelay',
        message: 'Max delay must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    } else if (retry.maxDelay <= retry.baseDelay) {
      warnings.push({
        path: 'retry.maxDelay',
        message: 'Max delay should be greater than base delay',
        code: 'LOGIC_WARNING',
        severity: 'warning',
      });
    }

    if (typeof retry.backoffMultiplier !== 'number' || retry.backoffMultiplier <= 1) {
      errors.push({
        path: 'retry.backoffMultiplier',
        message: 'Backoff multiplier must be greater than 1',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof retry.jitter !== 'boolean') {
      errors.push({
        path: 'retry.jitter',
        message: 'Jitter must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }
  }

  private validateCircuitBreakerConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.circuitBreaker) return;

    const { circuitBreaker } = config;

    if (
      typeof circuitBreaker.failureThreshold !== 'number' ||
      circuitBreaker.failureThreshold <= 0
    ) {
      errors.push({
        path: 'circuitBreaker.failureThreshold',
        message: 'Failure threshold must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof circuitBreaker.recoveryTimeout !== 'number' || circuitBreaker.recoveryTimeout <= 0) {
      errors.push({
        path: 'circuitBreaker.recoveryTimeout',
        message: 'Recovery timeout must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (
      typeof circuitBreaker.monitoringPeriod !== 'number' ||
      circuitBreaker.monitoringPeriod <= 0
    ) {
      errors.push({
        path: 'circuitBreaker.monitoringPeriod',
        message: 'Monitoring period must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (
      typeof circuitBreaker.halfOpenMaxCalls !== 'number' ||
      circuitBreaker.halfOpenMaxCalls <= 0
    ) {
      errors.push({
        path: 'circuitBreaker.halfOpenMaxCalls',
        message: 'Half-open max calls must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }
  }

  private validateFunctionCallingConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.functionCalling) return;

    const { functionCalling } = config;

    if (typeof functionCalling.enabled !== 'boolean') {
      errors.push({
        path: 'functionCalling.enabled',
        message: 'Function calling enabled must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof functionCalling.autoExecute !== 'boolean') {
      errors.push({
        path: 'functionCalling.autoExecute',
        message: 'Auto execute must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (
      typeof functionCalling.maxConcurrentCalls !== 'number' ||
      functionCalling.maxConcurrentCalls <= 0
    ) {
      errors.push({
        path: 'functionCalling.maxConcurrentCalls',
        message: 'Max concurrent calls must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof functionCalling.defaultTimeout !== 'number' || functionCalling.defaultTimeout <= 0) {
      errors.push({
        path: 'functionCalling.defaultTimeout',
        message: 'Default timeout must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    // Validate sandbox config
    this.validateSandboxConfig(
      functionCalling.sandbox,
      errors,
      warnings,
      'functionCalling.sandbox'
    );
  }

  private validateSandboxConfig(
    sandbox: any,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[],
    pathPrefix: string
  ): void {
    if (!sandbox) return;

    if (typeof sandbox.maxExecutionTime !== 'number' || sandbox.maxExecutionTime <= 0) {
      errors.push({
        path: `${pathPrefix}.maxExecutionTime`,
        message: 'Max execution time must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof sandbox.maxMemoryUsage !== 'number' || sandbox.maxMemoryUsage <= 0) {
      errors.push({
        path: `${pathPrefix}.maxMemoryUsage`,
        message: 'Max memory usage must be a positive number',
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof sandbox.enableNetworkAccess !== 'boolean') {
      errors.push({
        path: `${pathPrefix}.enableNetworkAccess`,
        message: 'Enable network access must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof sandbox.enableFileSystemAccess !== 'boolean') {
      errors.push({
        path: `${pathPrefix}.enableFileSystemAccess`,
        message: 'Enable file system access must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }
  }

  private validateCostManagementConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.costManagement) return;

    const { costManagement } = config;

    if (typeof costManagement.enabled !== 'boolean') {
      errors.push({
        path: 'costManagement.enabled',
        message: 'Cost management enabled must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    // Validate budget limits
    if (costManagement.budgetLimits) {
      if (
        costManagement.budgetLimits.dailyLimit !== undefined &&
        (typeof costManagement.budgetLimits.dailyLimit !== 'number' ||
          costManagement.budgetLimits.dailyLimit < 0)
      ) {
        errors.push({
          path: 'costManagement.budgetLimits.dailyLimit',
          message: 'Daily limit must be a non-negative number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      }

      if (
        costManagement.budgetLimits.monthlyLimit !== undefined &&
        (typeof costManagement.budgetLimits.monthlyLimit !== 'number' ||
          costManagement.budgetLimits.monthlyLimit < 0)
      ) {
        errors.push({
          path: 'costManagement.budgetLimits.monthlyLimit',
          message: 'Monthly limit must be a non-negative number',
          code: 'INVALID_VALUE',
          severity: 'error',
        });
      }
    }
  }

  private validateSecurityConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.security) return;

    const { security } = config;

    if (typeof security.encryptApiKeys !== 'boolean') {
      errors.push({
        path: 'security.encryptApiKeys',
        message: 'Encrypt API keys must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof security.auditLogging !== 'boolean') {
      errors.push({
        path: 'security.auditLogging',
        message: 'Audit logging must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof security.requestSigning !== 'boolean') {
      errors.push({
        path: 'security.requestSigning',
        message: 'Request signing must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof security.rateLimitByUser !== 'boolean') {
      errors.push({
        path: 'security.rateLimitByUser',
        message: 'Rate limit by user must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }
  }

  private validateLoggingConfig(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.logging) return;

    const { logging } = config;

    if (!this.VALID_LOG_LEVELS.includes(logging.level)) {
      errors.push({
        path: 'logging.level',
        message: `Log level must be one of: ${this.VALID_LOG_LEVELS.join(', ')}`,
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (!this.VALID_LOG_FORMATS.includes(logging.format)) {
      errors.push({
        path: 'logging.format',
        message: `Log format must be one of: ${this.VALID_LOG_FORMATS.join(', ')}`,
        code: 'INVALID_VALUE',
        severity: 'error',
      });
    }

    if (typeof logging.includeRequestBody !== 'boolean') {
      errors.push({
        path: 'logging.includeRequestBody',
        message: 'Include request body must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof logging.includeResponseBody !== 'boolean') {
      errors.push({
        path: 'logging.includeResponseBody',
        message: 'Include response body must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }

    if (typeof logging.sanitizeSensitiveData !== 'boolean') {
      errors.push({
        path: 'logging.sanitizeSensitiveData',
        message: 'Sanitize sensitive data must be a boolean',
        code: 'INVALID_TYPE',
        severity: 'error',
      });
    }
  }

  private validateFeatureFlags(
    config: Partial<OpenAIProviderConfig>,
    errors: ConfigValidationError[],
    warnings: ConfigValidationWarning[]
  ): void {
    if (!config.features) return;

    const { features } = config;

    const booleanFlags = [
      'streamingEnabled',
      'functionCallingEnabled',
      'costTrackingEnabled',
      'advancedRetryEnabled',
      'circuitBreakerEnabled',
      'betaFeatures',
      'debugMode',
    ];

    for (const flag of booleanFlags) {
      if (typeof features[flag as keyof typeof features] !== 'boolean') {
        errors.push({
          path: `features.${flag}`,
          message: `Feature flag ${flag} must be a boolean`,
          code: 'INVALID_TYPE',
          severity: 'error',
        });
      }
    }
  }
}
```

### Subtask 7.2: Add Multi-Source Configuration Loading

**Description**: Implement configuration loading from multiple sources including files, environment variables, and remote configuration services with proper precedence handling.

**Implementation Details**:

1. **Create Configuration Loader**:

```typescript
// packages/providers/src/openai/config-loader.ts
import { OpenAIProviderConfig } from '../interfaces/config-schema.interface';
import { ConfigValidator } from './config-validator';
import { EventEmitter } from 'events';
import { readFile } from 'fs/promises';
import { resolve } from 'path';

export interface ConfigSource {
  name: string;
  priority: number;
  load(): Promise<Partial<OpenAIProviderConfig>>;
  watch?(callback: (config: Partial<OpenAIProviderConfig>) => void): void;
}

export interface ConfigLoaderOptions {
  environment?: string;
  configFile?: string;
  enableHotReload?: boolean;
  remoteConfigUrl?: string;
  remoteConfigHeaders?: Record<string, string>;
}

export class ConfigLoader extends EventEmitter {
  private sources: ConfigSource[] = [];
  private validator: ConfigValidator;
  private currentConfig: OpenAIProviderConfig;
  private watchers: Map<string, any> = new Map();

  constructor(private options: ConfigLoaderOptions = {}) {
    super();
    this.validator = new ConfigValidator();
    this.currentConfig = {} as OpenAIProviderConfig;
  }

  async load(): Promise<OpenAIProviderConfig> {
    // Initialize default sources
    this.initializeSources();

    // Load configurations from all sources
    const configs: Partial<OpenAIProviderConfig>[] = [];

    for (const source of this.sources.sort((a, b) => a.priority - b.priority)) {
      try {
        const config = await source.load();
        configs.push(config);
        this.emit('sourceLoaded', { source: source.name, config });
      } catch (error) {
        this.emit('sourceError', { source: source.name, error });
        if (source.priority <= 10) {
          // Critical sources
          throw new Error(
            `Failed to load critical configuration source '${source.name}': ${error.message}`
          );
        }
      }
    }

    // Merge configurations (higher priority overrides lower)
    const mergedConfig = this.mergeConfigs(configs);

    // Apply environment-specific overrides
    const finalConfig = this.applyEnvironmentOverrides(mergedConfig);

    // Validate final configuration
    const validation = this.validator.validate(finalConfig);
    if (!validation.valid) {
      throw new Error(
        `Configuration validation failed: ${validation.errors.map((e) => e.message).join(', ')}`
      );
    }

    // Apply defaults
    this.currentConfig = this.applyDefaults(finalConfig);

    // Set up hot reload if enabled
    if (this.options.enableHotReload) {
      this.setupHotReload();
    }

    this.emit('configLoaded', this.currentConfig);
    return this.currentConfig;
  }

  getCurrentConfig(): OpenAIProviderConfig {
    return { ...this.currentConfig };
  }

  addSource(source: ConfigSource): void {
    this.sources.push(source);
    this.emit('sourceAdded', source);
  }

  removeSource(sourceName: string): void {
    this.sources = this.sources.filter((s) => s.name !== sourceName);
    this.emit('sourceRemoved', sourceName);
  }

  reload(): Promise<OpenAIProviderConfig> {
    return this.load();
  }

  private initializeSources(): void {
    this.sources = [];

    // 1. Default configuration (lowest priority)
    this.addSource({
      name: 'defaults',
      priority: 1,
      load: async () => this.getDefaultConfig(),
    });

    // 2. Configuration file
    if (this.options.configFile) {
      this.addSource({
        name: 'file',
        priority: 5,
        load: async () => this.loadFromFile(this.options.configFile!),
        watch: (callback) => this.watchFile(this.options.configFile!, callback),
      });
    }

    // 3. Environment variables
    this.addSource({
      name: 'environment',
      priority: 10,
      load: async () => this.loadFromEnvironment(),
    });

    // 4. Remote configuration
    if (this.options.remoteConfigUrl) {
      this.addSource({
        name: 'remote',
        priority: 15,
        load: async () => this.loadFromRemote(),
        watch: (callback) => this.watchRemote(callback),
      });
    }

    // 5. Runtime overrides (highest priority)
    this.addSource({
      name: 'runtime',
      priority: 20,
      load: async () => ({}),
    });
  }

  private async getDefaultConfig(): Promise<Partial<OpenAIProviderConfig>> {
    return {
      timeout: 30000,
      maxRetries: 3,
      defaultModel: 'gpt-3.5-turbo',
      supportedModels: [
        {
          name: 'gpt-3.5-turbo',
          displayName: 'GPT-3.5 Turbo',
          maxTokens: 4096,
          inputCostPer1K: 0.0015,
          outputCostPer1K: 0.002,
          supportsStreaming: true,
          supportsFunctionCalling: true,
          supportsVision: false,
        },
        {
          name: 'gpt-4',
          displayName: 'GPT-4',
          maxTokens: 8192,
          inputCostPer1K: 0.03,
          outputCostPer1K: 0.06,
          supportsStreaming: true,
          supportsFunctionCalling: true,
          supportsVision: false,
        },
        {
          name: 'gpt-4-turbo',
          displayName: 'GPT-4 Turbo',
          maxTokens: 128000,
          inputCostPer1K: 0.01,
          outputCostPer1K: 0.03,
          supportsStreaming: true,
          supportsFunctionCalling: true,
          supportsVision: true,
        },
      ],
      modelDefaults: {
        'gpt-3.5-turbo': {
          temperature: 0.7,
          maxTokens: 1000,
          topP: 1,
          frequencyPenalty: 0,
          presencePenalty: 0,
        },
        'gpt-4': {
          temperature: 0.7,
          maxTokens: 2000,
          topP: 1,
          frequencyPenalty: 0,
          presencePenalty: 0,
        },
        'gpt-4-turbo': {
          temperature: 0.7,
          maxTokens: 4000,
          topP: 1,
          frequencyPenalty: 0,
          presencePenalty: 0,
        },
      },
      rateLimit: {
        requestsPerMinute: 3500,
        tokensPerMinute: 90000,
        requestsPerDay: 10000,
        burstAllowance: 10,
      },
      retry: {
        maxAttempts: 3,
        baseDelay: 1000,
        maxDelay: 30000,
        backoffMultiplier: 2,
        jitter: true,
        retryableErrors: [
          'rate_limit_exceeded',
          'insufficient_quota',
          'model_overloaded',
          'timeout',
          'connection_error',
          'temporary_error',
        ],
        retryableStatusCodes: [429, 500, 502, 503, 504],
      },
      circuitBreaker: {
        failureThreshold: 5,
        recoveryTimeout: 60000,
        monitoringPeriod: 300000,
        expectedRecoveryTime: 120000,
        halfOpenMaxCalls: 3,
      },
      functionCalling: {
        enabled: true,
        autoExecute: false,
        maxConcurrentCalls: 5,
        defaultTimeout: 30000,
        sandbox: {
          maxExecutionTime: 30000,
          maxMemoryUsage: 100 * 1024 * 1024,
          maxFileSize: 10 * 1024 * 1024,
          allowedDomains: [],
          blockedDomains: [],
          allowedPaths: ['/tmp'],
          blockedPaths: ['/etc', '/usr/bin', '/bin'],
          enableNetworkAccess: false,
          enableFileSystemAccess: true,
          enableSystemCommands: false,
        },
      },
      costManagement: {
        enabled: true,
        budgetLimits: {
          dailyLimit: 100,
          monthlyLimit: 3000,
        },
        alerts: {
          enabled: true,
          thresholds: [50, 75, 90],
          channels: [],
          cooldownPeriod: 300000,
        },
        tracking: {
          enabled: true,
          granularity: 'request',
          retentionPeriod: 90,
          anonymizeData: false,
        },
      },
      security: {
        encryptApiKeys: true,
        keyRotationInterval: 86400000,
        auditLogging: true,
        rateLimitByUser: true,
        requestSigning: false,
      },
      logging: {
        level: 'info',
        format: 'json',
        includeRequestBody: false,
        includeResponseBody: false,
        sanitizeSensitiveData: true,
        outputs: [
          {
            type: 'console',
            config: {},
            enabled: true,
          },
        ],
      },
      features: {
        streamingEnabled: true,
        functionCallingEnabled: true,
        costTrackingEnabled: true,
        advancedRetryEnabled: true,
        circuitBreakerEnabled: true,
        betaFeatures: false,
        debugMode: false,
      },
    };
  }

  private async loadFromFile(configFile: string): Promise<Partial<OpenAIProviderConfig>> {
    try {
      const filePath = resolve(configFile);
      const content = await readFile(filePath, 'utf-8');

      if (configFile.endsWith('.json')) {
        return JSON.parse(content);
      } else if (configFile.endsWith('.yaml') || configFile.endsWith('.yml')) {
        const yaml = await import('js-yaml');
        return yaml.load(content) as Partial<OpenAIProviderConfig>;
      } else {
        throw new Error(`Unsupported config file format: ${configFile}`);
      }
    } catch (error) {
      throw new Error(`Failed to load config from file ${configFile}: ${error.message}`);
    }
  }

  private loadFromEnvironment(): Partial<OpenAIProviderConfig> {
    const config: Partial<OpenAIProviderConfig> = {};

    // API configuration
    if (process.env.OPENAI_API_KEY) {
      config.apiKey = process.env.OPENAI_API_KEY;
    }
    if (process.env.OPENAI_ORGANIZATION) {
      config.organization = process.env.OPENAI_ORGANIZATION;
    }
    if (process.env.OPENAI_BASE_URL) {
      config.baseURL = process.env.OPENAI_BASE_URL;
    }
    if (process.env.OPENAI_TIMEOUT) {
      config.timeout = parseInt(process.env.OPENAI_TIMEOUT, 10);
    }
    if (process.env.OPENAI_MAX_RETRIES) {
      config.maxRetries = parseInt(process.env.OPENAI_MAX_RETRIES, 10);
    }

    // Feature flags
    if (process.env.OPENAI_STREAMING_ENABLED) {
      config.features = {
        ...config.features,
        streamingEnabled: process.env.OPENAI_STREAMING_ENABLED === 'true',
      };
    }
    if (process.env.OPENAI_FUNCTION_CALLING_ENABLED) {
      config.features = {
        ...config.features,
        functionCallingEnabled: process.env.OPENAI_FUNCTION_CALLING_ENABLED === 'true',
      };
    }
    if (process.env.OPENAI_COST_TRACKING_ENABLED) {
      config.features = {
        ...config.features,
        costTrackingEnabled: process.env.OPENAI_COST_TRACKING_ENABLED === 'true',
      };
    }
    if (process.env.OPENAI_DEBUG_MODE) {
      config.features = {
        ...config.features,
        debugMode: process.env.OPENAI_DEBUG_MODE === 'true',
      };
    }

    // Logging
    if (process.env.OPENAI_LOG_LEVEL) {
      config.logging = {
        ...config.logging,
        level: process.env.OPENAI_LOG_LEVEL as any,
      };
    }

    return config;
  }

  private async loadFromRemote(): Promise<Partial<OpenAIProviderConfig>> {
    if (!this.options.remoteConfigUrl) {
      return {};
    }

    try {
      const response = await fetch(this.options.remoteConfigUrl, {
        headers: this.options.remoteConfigHeaders || {},
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      return await response.json();
    } catch (error) {
      throw new Error(`Failed to load remote config: ${error.message}`);
    }
  }

  private mergeConfigs(configs: Partial<OpenAIProviderConfig>[]): Partial<OpenAIProviderConfig> {
    return configs.reduce((merged, config) => {
      return this.deepMerge(merged, config);
    }, {});
  }

  private deepMerge(target: any, source: any): any {
    const result = { ...target };

    for (const key in source) {
      if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key])) {
        result[key] = this.deepMerge(result[key] || {}, source[key]);
      } else {
        result[key] = source[key];
      }
    }

    return result;
  }

  private applyEnvironmentOverrides(
    config: Partial<OpenAIProviderConfig>
  ): Partial<OpenAIProviderConfig> {
    const environment = this.options.environment || process.env.NODE_ENV || 'development';

    if (config.environments && config.environments[environment]) {
      return this.deepMerge(config, config.environments[environment]);
    }

    return config;
  }

  private applyDefaults(config: Partial<OpenAIProviderConfig>): OpenAIProviderConfig {
    const defaults = this.getDefaultConfig();
    return this.deepMerge(defaults, config) as OpenAIProviderConfig;
  }

  private setupHotReload(): void {
    for (const source of this.sources) {
      if (source.watch) {
        const watcher = source.watch(async (newConfig) => {
          try {
            await this.reload();
            this.emit('hotReload', { source: source.name, config: this.currentConfig });
          } catch (error) {
            this.emit('hotReloadError', { source: source.name, error });
          }
        });

        this.watchers.set(source.name, watcher);
      }
    }
  }

  private watchFile(
    configFile: string,
    callback: (config: Partial<OpenAIProviderConfig>) => void
  ): void {
    const fs = require('fs');
    const filePath = resolve(configFile);

    fs.watchFile(filePath, async () => {
      try {
        const config = await this.loadFromFile(configFile);
        callback(config);
      } catch (error) {
        this.emit('watchError', { source: 'file', error });
      }
    });
  }

  private watchRemote(callback: (config: Partial<OpenAIProviderConfig>) => void): void {
    // Poll remote configuration every 5 minutes
    const interval = setInterval(
      async () => {
        try {
          const config = await this.loadFromRemote();
          callback(config);
        } catch (error) {
          this.emit('watchError', { source: 'remote', error });
        }
      },
      5 * 60 * 1000
    );

    this.watchers.set('remote', interval);
  }

  destroy(): void {
    // Clean up watchers
    for (const [name, watcher] of this.watchers) {
      if (name === 'file') {
        const fs = require('fs');
        fs.unwatchFile(this.options.configFile);
      } else if (name === 'remote') {
        clearInterval(watcher);
      }
    }
    this.watchers.clear();

    this.removeAllListeners();
  }
}
```

### Subtask 7.3: Add Hot-Reload and Change Notifications

**Description**: Implement hot-reload capabilities with change notifications, configuration versioning, and rollback functionality.

**Implementation Details**:

1. **Create Configuration Manager**:

```typescript
// packages/providers/src/openai/config-manager.ts
import { OpenAIProviderConfig } from '../interfaces/config-schema.interface';
import { ConfigLoader, ConfigLoaderOptions } from './config-loader';
import { EventEmitter } from 'events';

export interface ConfigManagerOptions extends ConfigLoaderOptions {
  enableHistory?: boolean;
  maxHistorySize?: number;
  enableValidation?: boolean;
  enableRollback?: boolean;
}

export interface ConfigChange {
  version: number;
  timestamp: string;
  source: string;
  changes: ConfigChangeDetail[];
  config: OpenAIProviderConfig;
  previousConfig?: OpenAIProviderConfig;
}

export interface ConfigChangeDetail {
  path: string;
  oldValue: any;
  newValue: any;
  type: 'added' | 'removed' | 'modified';
}

export interface ConfigHistory {
  changes: ConfigChange[];
  currentVersion: number;
}

export class ConfigManager extends EventEmitter {
  private loader: ConfigLoader;
  private currentConfig: OpenAIProviderConfig;
  private configHistory: ConfigChange[] = [];
  private currentVersion: number = 0;
  private isReloading: boolean = false;

  constructor(private options: ConfigManagerOptions = {}) {
    super();
    this.loader = new ConfigLoader(options);
    this.currentConfig = {} as OpenAIProviderConfig;
  }

  async initialize(): Promise<OpenAIProviderConfig> {
    this.currentConfig = await this.loader.load();
    this.currentVersion = 1;

    // Set up event listeners
    this.setupEventListeners();

    // Record initial configuration
    if (this.options.enableHistory) {
      this.recordConfigChange('initial', this.currentConfig, undefined);
    }

    this.emit('initialized', this.currentConfig);
    return this.currentConfig;
  }

  getCurrentConfig(): OpenAIProviderConfig {
    return { ...this.currentConfig };
  }

  async reload(): Promise<OpenAIProviderConfig> {
    if (this.isReloading) {
      throw new Error('Configuration reload already in progress');
    }

    this.isReloading = true;

    try {
      const previousConfig = { ...this.currentConfig };
      const newConfig = await this.loader.reload();

      const changes = this.detectChanges(previousConfig, newConfig);

      if (changes.length > 0) {
        this.currentConfig = newConfig;
        this.currentVersion++;

        if (this.options.enableHistory) {
          this.recordConfigChange('reload', newConfig, previousConfig, changes);
        }

        this.emit('configChanged', {
          version: this.currentVersion,
          changes,
          config: newConfig,
          previousConfig,
        });
      }

      return newConfig;
    } finally {
      this.isReloading = false;
    }
  }

  async updateConfig(
    updates: Partial<OpenAIProviderConfig>,
    source: string = 'manual'
  ): Promise<OpenAIProviderConfig> {
    const previousConfig = { ...this.currentConfig };
    const newConfig = this.deepMerge(previousConfig, updates);

    const changes = this.detectChanges(previousConfig, newConfig);

    if (changes.length === 0) {
      return this.currentConfig;
    }

    // Validate new configuration
    if (this.options.enableValidation) {
      const { ConfigValidator } = await import('./config-validator');
      const validator = new ConfigValidator();
      const validation = validator.validate(newConfig);

      if (!validation.valid) {
        throw new Error(
          `Configuration validation failed: ${validation.errors.map((e) => e.message).join(', ')}`
        );
      }
    }

    this.currentConfig = newConfig;
    this.currentVersion++;

    if (this.options.enableHistory) {
      this.recordConfigChange(source, newConfig, previousConfig, changes);
    }

    this.emit('configChanged', {
      version: this.currentVersion,
      changes,
      config: newConfig,
      previousConfig,
    });

    return newConfig;
  }

  async rollback(version: number): Promise<OpenAIProviderConfig> {
    if (!this.options.enableRollback) {
      throw new Error('Rollback is disabled');
    }

    const targetChange = this.configHistory.find((change) => change.version === version);
    if (!targetChange) {
      throw new Error(`Configuration version ${version} not found`);
    }

    const previousConfig = { ...this.currentConfig };
    const changes = this.detectChanges(previousConfig, targetChange.config);

    this.currentConfig = targetChange.config;
    this.currentVersion++;

    if (this.options.enableHistory) {
      this.recordConfigChange(
        `rollback-to-${version}`,
        this.currentConfig,
        previousConfig,
        changes
      );
    }

    this.emit('configRolledBack', {
      fromVersion: this.currentVersion - 1,
      toVersion: version,
      config: this.currentConfig,
      previousConfig,
    });

    return this.currentConfig;
  }

  getHistory(): ConfigHistory {
    return {
      changes: [...this.configHistory],
      currentVersion: this.currentVersion,
    };
  }

  getConfigVersion(version: number): OpenAIProviderConfig | undefined {
    const change = this.configHistory.find((c) => c.version === version);
    return change?.config;
  }

  async exportConfig(format: 'json' | 'yaml' = 'json'): Promise<string> {
    const exportData = {
      version: this.currentVersion,
      timestamp: new Date().toISOString(),
      config: this.currentConfig,
      history: this.options.enableHistory ? this.configHistory : undefined,
    };

    if (format === 'yaml') {
      const yaml = await import('js-yaml');
      return yaml.dump(exportData);
    } else {
      return JSON.stringify(exportData, null, 2);
    }
  }

  async importConfig(
    configData: string,
    format: 'json' | 'yaml' = 'json'
  ): Promise<OpenAIProviderConfig> {
    let importData: any;

    try {
      if (format === 'yaml') {
        const yaml = await import('js-yaml');
        importData = yaml.load(configData);
      } else {
        importData = JSON.parse(configData);
      }
    } catch (error) {
      throw new Error(`Failed to parse configuration: ${error.message}`);
    }

    if (!importData.config) {
      throw new Error('Invalid configuration format: missing config object');
    }

    return this.updateConfig(importData.config, 'import');
  }

  private setupEventListeners(): void {
    this.loader.on('configLoaded', (config) => {
      this.emit('loaderConfigLoaded', config);
    });

    this.loader.on('hotReload', ({ source, config }) => {
      this.emit('loaderHotReload', { source, config });
    });

    this.loader.on('sourceError', ({ source, error }) => {
      this.emit('sourceError', { source, error });
    });

    this.loader.on('hotReloadError', ({ source, error }) => {
      this.emit('hotReloadError', { source, error });
    });
  }

  private detectChanges(oldConfig: any, newConfig: any): ConfigChangeDetail[] {
    const changes: ConfigChangeDetail[] = [];

    this.compareObjects(oldConfig, newConfig, '', changes);

    return changes;
  }

  private compareObjects(
    oldObj: any,
    newObj: any,
    path: string,
    changes: ConfigChangeDetail[]
  ): void {
    const keys = new Set([...Object.keys(oldObj || {}), ...Object.keys(newObj || {})]);

    for (const key of keys) {
      const currentPath = path ? `${path}.${key}` : key;
      const oldValue = oldObj?.[key];
      const newValue = newObj?.[key];

      if (oldValue === undefined && newValue !== undefined) {
        changes.push({
          path: currentPath,
          oldValue,
          newValue,
          type: 'added',
        });
      } else if (oldValue !== undefined && newValue === undefined) {
        changes.push({
          path: currentPath,
          oldValue,
          newValue,
          type: 'removed',
        });
      } else if (oldValue !== newValue) {
        if (
          typeof oldValue === 'object' &&
          typeof newValue === 'object' &&
          oldValue !== null &&
          newValue !== null &&
          !Array.isArray(oldValue) &&
          !Array.isArray(newValue)
        ) {
          this.compareObjects(oldValue, newValue, currentPath, changes);
        } else {
          changes.push({
            path: currentPath,
            oldValue,
            newValue,
            type: 'modified',
          });
        }
      }
    }
  }

  private recordConfigChange(
    source: string,
    config: OpenAIProviderConfig,
    previousConfig?: OpenAIProviderConfig,
    changes?: ConfigChangeDetail[]
  ): void {
    const change: ConfigChange = {
      version: this.currentVersion,
      timestamp: new Date().toISOString(),
      source,
      changes: changes || this.detectChanges(previousConfig || {}, config),
      config: { ...config },
      previousConfig: previousConfig ? { ...previousConfig } : undefined,
    };

    this.configHistory.push(change);

    // Limit history size
    if (this.options.maxHistorySize && this.configHistory.length > this.options.maxHistorySize) {
      this.configHistory = this.configHistory.slice(-this.options.maxHistorySize);
    }
  }

  private deepMerge(target: any, source: any): any {
    const result = { ...target };

    for (const key in source) {
      if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key])) {
        result[key] = this.deepMerge(result[key] || {}, source[key]);
      } else {
        result[key] = source[key];
      }
    }

    return result;
  }

  destroy(): void {
    this.loader.destroy();
    this.removeAllListeners();
  }
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/config-schema.interface.ts`

2. **Configuration Implementation**:
   - `packages/providers/src/openai/config-validator.ts`
   - `packages/providers/src/openai/config-loader.ts`
   - `packages/providers/src/openai/config-manager.ts`

3. **Configuration Files**:
   - `packages/providers/src/config/default.json`
   - `packages/providers/src/config/development.json`
   - `packages/providers/src/config/production.json`

4. **Updated Files**:
   - `packages/providers/src/openai/openai-provider.ts` (integrate configuration management)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/config-validator.test.ts
describe('ConfigValidator', () => {
  let validator: ConfigValidator;

  beforeEach(() => {
    validator = new ConfigValidator();
  });

  describe('validate', () => {
    it('should validate valid configuration', () => {
      const config = {
        apiKey: 'sk-test-key',
        supportedModels: [
          {
            name: 'gpt-3.5-turbo',
            displayName: 'GPT-3.5 Turbo',
            maxTokens: 4096,
            inputCostPer1K: 0.0015,
            outputCostPer1K: 0.002,
            supportsStreaming: true,
            supportsFunctionCalling: true,
            supportsVision: false,
          },
        ],
        rateLimit: {
          requestsPerMinute: 3500,
          tokensPerMinute: 90000,
          requestsPerDay: 10000,
          burstAllowance: 10,
        },
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should reject configuration with missing required fields', () => {
      const config = {
        // Missing apiKey
        supportedModels: [],
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors).toContainEqual(
        expect.objectContaining({
          path: 'apiKey',
          code: 'REQUIRED_FIELD_MISSING',
        })
      );
    });

    it('should provide warnings for potentially problematic values', () => {
      const config = {
        apiKey: 'test-key', // Doesn't start with sk-
        timeout: 400000, // Very high timeout
      };

      const result = validator.validate(config);
      expect(result.warnings.length).toBeGreaterThan(0);
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/config-loader.test.ts
describe('ConfigLoader', () => {
  let loader: ConfigLoader;
  let tempConfigFile: string;

  beforeEach(async () => {
    const { writeFileSync, mkdtempSync } = require('fs');
    const { join } = require('path');
    const { tmpdir } = require('os');

    const tempDir = mkdtempSync(join(tmpdir(), 'config-test-'));
    tempConfigFile = join(tempDir, 'config.json');

    writeFileSync(
      tempConfigFile,
      JSON.stringify({
        apiKey: 'sk-test-key',
        timeout: 60000,
      })
    );

    loader = new ConfigLoader({
      configFile: tempConfigFile,
      enableHotReload: false,
    });
  });

  afterEach(() => {
    loader.destroy();
  });

  describe('load', () => {
    it('should load configuration from file', async () => {
      const config = await loader.load();
      expect(config.apiKey).toBe('sk-test-key');
      expect(config.timeout).toBe(60000);
    });

    it('should merge multiple sources', async () => {
      process.env.OPENAI_TIMEOUT = '30000';

      const config = await loader.load();
      expect(config.apiKey).toBe('sk-test-key'); // From file
      expect(config.timeout).toBe(30000); // From environment

      delete process.env.OPENAI_TIMEOUT;
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/config-manager.test.ts
describe('ConfigManager', () => {
  let manager: ConfigManager;
  let tempConfigFile: string;

  beforeEach(async () => {
    const { writeFileSync, mkdtempSync } = require('fs');
    const { join } = require('path');
    const { tmpdir } = require('os');

    const tempDir = mkdtempSync(join(tmpdir(), 'config-test-'));
    tempConfigFile = join(tempDir, 'config.json');

    writeFileSync(
      tempConfigFile,
      JSON.stringify({
        apiKey: 'sk-test-key',
        timeout: 60000,
      })
    );

    manager = new ConfigManager({
      configFile: tempConfigFile,
      enableHistory: true,
      enableValidation: true,
      enableRollback: true,
    });
  });

  afterEach(() => {
    manager.destroy();
  });

  describe('initialize', () => {
    it('should initialize with configuration', async () => {
      const config = await manager.initialize();
      expect(config.apiKey).toBe('sk-test-key');
      expect(manager.getCurrentConfig()).toEqual(config);
    });
  });

  describe('updateConfig', () => {
    it('should update configuration and track changes', async () => {
      await manager.initialize();

      const newConfig = await manager.updateConfig({
        timeout: 30000,
        maxRetries: 5,
      });

      expect(newConfig.timeout).toBe(30000);
      expect(newConfig.maxRetries).toBe(5);
      expect(newConfig.apiKey).toBe('sk-test-key'); // Unchanged

      const history = manager.getHistory();
      expect(history.changes).toHaveLength(2); // Initial + update
    });
  });

  describe('rollback', () => {
    it('should rollback to previous configuration version', async () => {
      await manager.initialize();

      // Make a change
      await manager.updateConfig({ timeout: 30000 });
      const version1 = manager.getHistory().currentVersion;

      // Make another change
      await manager.updateConfig({ maxRetries: 5 });
      const version2 = manager.getHistory().currentVersion;

      // Rollback to version 1
      const rolledBackConfig = await manager.rollback(version1);

      expect(rolledBackConfig.timeout).toBe(30000);
      expect(rolledBackConfig.maxRetries).toBeUndefined(); // Should be back to default
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/openai-config-integration.test.ts
describe('OpenAI Provider Configuration Integration', () => {
  let provider: OpenAIProvider;
  let tempConfigFile: string;

  beforeEach(async () => {
    const { writeFileSync, mkdtempSync } = require('fs');
    const { join } = require('path');
    const { tmpdir } = require('os');

    const tempDir = mkdtempSync(join(tmpdir(), 'config-integration-test-'));
    tempConfigFile = join(tempDir, 'config.json');

    writeFileSync(
      tempConfigFile,
      JSON.stringify({
        apiKey: process.env.OPENAI_API_KEY || 'sk-test-key',
        timeout: 30000,
        features: {
          streamingEnabled: true,
          functionCallingEnabled: true,
        },
      })
    );

    provider = new OpenAIProvider({
      configFile: tempConfigFile,
      enableHotReload: false,
    });
  });

  afterEach(() => {
    provider.destroy();
  });

  it('should initialize provider with configuration from file', async () => {
    await provider.initialize();

    const config = provider.getConfig();
    expect(config.timeout).toBe(30000);
    expect(config.features.streamingEnabled).toBe(true);
  });

  it('should handle configuration changes during runtime', async () => {
    await provider.initialize();

    // Update configuration
    await provider.updateConfig({
      timeout: 60000,
      features: {
        streamingEnabled: false,
      },
    });

    const config = provider.getConfig();
    expect(config.timeout).toBe(60000);
    expect(config.features.streamingEnabled).toBe(false);
  });
});
```

## Security Considerations

1. **Configuration Security**:
   - Encrypt sensitive configuration values
   - Secure API key handling and rotation
   - Access control for configuration changes
   - Audit logging for all configuration modifications

2. **Input Validation**:
   - Comprehensive validation of all configuration values
   - Type checking and range validation
   - Prevention of configuration injection attacks
   - Sanitization of file paths and URLs

3. **Environment Isolation**:
   - Environment-specific configuration isolation
   - Prevent configuration leakage between environments
   - Secure handling of environment variables
   - Validation of configuration sources

## Dependencies

### New Dependencies

```json
{
  "js-yaml": "^4.1.0",
  "chokidar": "^3.5.3"
}
```

### Dev Dependencies

```json
{
  "@types/js-yaml": "^4.0.8",
  "@types/chokidar": "^2.1.3"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Configuration load times and success rates
   - Hot-reload frequency and success rates
   - Configuration validation failures
   - Rollback operations and reasons

2. **Logging**:
   - All configuration changes with source and timestamp
   - Validation errors and warnings
   - Hot-reload events and failures
   - Rollback operations

3. **Alerts**:
   - Configuration validation failures
   - Hot-reload failures
   - Security-related configuration changes
   - Configuration source unavailability

## Acceptance Criteria

1. ✅ **Schema Validation**: Complete configuration schema with validation
2. ✅ **Multi-Source Loading**: Support for files, environment, and remote config
3. ✅ **Hot-Reload**: Real-time configuration updates without restart
4. ✅ **Change Tracking**: Complete audit trail of configuration changes
5. ✅ **Rollback**: Ability to rollback to previous configuration versions
6. ✅ **Environment Support**: Environment-specific configuration management
7. ✅ **Security**: Secure handling of sensitive configuration data
8. ✅ **Testing**: Comprehensive unit and integration test coverage
9. ✅ **Documentation**: Clear configuration documentation and examples
10. ✅ **Performance**: Minimal overhead on configuration operations

## Success Metrics

- Configuration load time < 100ms
- Hot-reload latency < 500ms
- Configuration validation accuracy > 99%
- Zero configuration-related security incidents
- Complete audit trail for all changes
- 100% test coverage for configuration logic
