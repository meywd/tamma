# Task 8: Error Handling

## Overview

Implement comprehensive error handling for the OpenAI provider, including error classification, recovery strategies, user-friendly error messages, and detailed error reporting.

## Objectives

- Implement structured error classification and handling
- Add automatic error recovery and fallback strategies
- Provide user-friendly error messages with actionable guidance
- Create comprehensive error reporting and analytics

## Implementation Steps

### Subtask 8.1: Implement Error Classification and Handling

**Description**: Create a sophisticated error classification system that categorizes errors by type, severity, and recoverability, with appropriate handling strategies.

**Implementation Details**:

1. **Create Error Classification System**:

```typescript
// packages/providers/src/interfaces/error-handling.interface.ts
export interface IErrorHandler {
  handleError(error: any, context: ErrorContext): Promise<ErrorHandlingResult>;
  classifyError(error: any): ErrorClassification;
  isRetryable(error: ErrorClassification): boolean;
  getRecoveryStrategy(error: ErrorClassification): RecoveryStrategy;
  formatErrorMessage(error: ErrorClassification, context: ErrorContext): string;
}

export interface ErrorContext {
  operation: string;
  operationId: string;
  userId?: string;
  organizationId?: string;
  model?: string;
  requestDetails?: Record<string, any>;
  timestamp: string;
  attempt?: number;
}

export interface ErrorHandlingResult {
  handled: boolean;
  shouldRetry: boolean;
  retryDelay?: number;
  fallbackUsed?: boolean;
  fallbackResult?: any;
  userMessage: string;
  technicalMessage: string;
  errorId: string;
  severity: ErrorSeverity;
  classification: ErrorClassification;
}

export interface ErrorClassification {
  type: ErrorType;
  category: ErrorCategory;
  severity: ErrorSeverity;
  recoverable: boolean;
  userFriendly: boolean;
  code: string;
  message: string;
  details?: Record<string, any>;
  suggestions?: string[];
}

export enum ErrorType {
  // API Errors
  API_ERROR = 'api_error',
  AUTHENTICATION_ERROR = 'authentication_error',
  PERMISSION_ERROR = 'permission_error',
  RATE_LIMIT_ERROR = 'rate_limit_error',
  QUOTA_EXCEEDED = 'quota_exceeded',
  MODEL_NOT_FOUND = 'model_not_found',
  INVALID_REQUEST = 'invalid_request',

  // Network Errors
  NETWORK_ERROR = 'network_error',
  TIMEOUT_ERROR = 'timeout_error',
  CONNECTION_ERROR = 'connection_error',
  DNS_ERROR = 'dns_error',

  // Configuration Errors
  CONFIGURATION_ERROR = 'configuration_error',
  VALIDATION_ERROR = 'validation_error',

  // Runtime Errors
  RUNTIME_ERROR = 'runtime_error',
  MEMORY_ERROR = 'memory_error',
  PROCESSING_ERROR = 'processing_error',

  // Business Logic Errors
  CONTENT_POLICY_ERROR = 'content_policy_error',
  FUNCTION_EXECUTION_ERROR = 'function_execution_error',
  SANDBOX_ERROR = 'sandbox_error',

  // System Errors
  SYSTEM_ERROR = 'system_error',
  INTERNAL_ERROR = 'internal_error',
  UNKNOWN_ERROR = 'unknown_error',
}

export enum ErrorCategory {
  TRANSIENT = 'transient', // Temporary errors that may resolve
  PERMANENT = 'permanent', // Permanent errors that won't resolve
  USER_ERROR = 'user_error', // Errors caused by user input
  SYSTEM_ERROR = 'system_error', // System-level errors
  BUSINESS_ERROR = 'business_error', // Business logic errors
}

export enum ErrorSeverity {
  LOW = 'low', // Minor issues, no impact on functionality
  MEDIUM = 'medium', // Issues that affect some functionality
  HIGH = 'high', // Serious issues affecting core functionality
  CRITICAL = 'critical', // System-wide failures
}

export interface RecoveryStrategy {
  type: RecoveryType;
  action: RecoveryAction;
  maxAttempts?: number;
  delay?: number;
  fallback?: FallbackOption;
}

export enum RecoveryType {
  RETRY = 'retry',
  FALLBACK = 'fallback',
  ESCALATE = 'escalate',
  IGNORE = 'ignore',
  ALTERNATIVE = 'alternative',
}

export enum RecoveryAction {
  EXPONENTIAL_BACKOFF = 'exponential_backoff',
  LINEAR_BACKOFF = 'linear_backoff',
  FIXED_DELAY = 'fixed_delay',
  IMMEDIATE = 'immediate',
  USE_ALTERNATIVE_MODEL = 'use_alternative_model',
  USE_CACHED_RESPONSE = 'use_cached_response',
  REDUCE_COMPLEXITY = 'reduce_complexity',
  CONTACT_SUPPORT = 'contact_support',
}

export interface FallbackOption {
  type: 'alternative_model' | 'cached_response' | 'simplified_request' | 'manual_intervention';
  config: Record<string, any>;
  description: string;
}

export interface ErrorReport {
  errorId: string;
  classification: ErrorClassification;
  context: ErrorContext;
  handlingResult: ErrorHandlingResult;
  timestamp: string;
  resolved: boolean;
  resolutionTime?: number;
  resolutionMethod?: string;
}
```

2. **Implement Error Handler**:

```typescript
// packages/providers/src/openai/error-handler.ts
import {
  IErrorHandler,
  ErrorContext,
  ErrorHandlingResult,
  ErrorClassification,
  ErrorType,
  ErrorCategory,
  ErrorSeverity,
  RecoveryStrategy,
  RecoveryType,
  RecoveryAction,
  FallbackOption,
} from '../interfaces/error-handling.interface';
import { v4 as uuidv4 } from 'uuid';
import { EventEmitter } from 'events';

export class OpenAIErrorHandler extends EventEmitter implements IErrorHandler {
  private errorPatterns: Map<ErrorType, ErrorPattern> = new Map();
  private recoveryStrategies: Map<ErrorType, RecoveryStrategy> = new Map();
  private errorReports: Map<string, ErrorReport> = new Map();

  constructor() {
    super();
    this.initializeErrorPatterns();
    this.initializeRecoveryStrategies();
  }

  async handleError(error: any, context: ErrorContext): Promise<ErrorHandlingResult> {
    const errorId = uuidv4();
    const classification = this.classifyError(error);
    const recoveryStrategy = this.getRecoveryStrategy(classification);

    const handlingResult: ErrorHandlingResult = {
      handled: true,
      shouldRetry: classification.recoverable && recoveryStrategy.type === RecoveryType.RETRY,
      retryDelay: recoveryStrategy.delay,
      fallbackUsed: false,
      userMessage: this.formatErrorMessage(classification, context),
      technicalMessage: error.message || 'Unknown error',
      errorId,
      severity: classification.severity,
      classification,
    };

    // Apply recovery strategy
    if (classification.recoverable) {
      await this.applyRecoveryStrategy(recoveryStrategy, error, context, handlingResult);
    }

    // Create error report
    const errorReport: ErrorReport = {
      errorId,
      classification,
      context,
      handlingResult,
      timestamp: new Date().toISOString(),
      resolved: handlingResult.handled,
    };

    this.errorReports.set(errorId, errorReport);

    // Emit events
    this.emit('errorHandled', errorReport);

    if (
      classification.severity === ErrorSeverity.HIGH ||
      classification.severity === ErrorSeverity.CRITICAL
    ) {
      this.emit('criticalError', errorReport);
    }

    return handlingResult;
  }

  classifyError(error: any): ErrorClassification {
    // Handle OpenAI API errors
    if (error?.error) {
      return this.classifyOpenAIError(error);
    }

    // Handle HTTP errors
    if (error?.status || error?.statusCode) {
      return this.classifyHTTPError(error);
    }

    // Handle network errors
    if (error?.code) {
      return this.classifyNetworkError(error);
    }

    // Handle validation errors
    if (error?.name === 'ValidationError') {
      return this.classifyValidationError(error);
    }

    // Handle runtime errors
    if (error instanceof Error) {
      return this.classifyRuntimeError(error);
    }

    // Unknown error
    return this.createDefaultClassification(error);
  }

  isRetryable(error: ErrorClassification): boolean {
    return error.recoverable;
  }

  getRecoveryStrategy(error: ErrorClassification): RecoveryStrategy {
    return this.recoveryStrategies.get(error.type) || this.getDefaultRecoveryStrategy();
  }

  formatErrorMessage(error: ErrorClassification, context: ErrorContext): string {
    if (!error.userFriendly) {
      return 'An unexpected error occurred. Please try again or contact support if the problem persists.';
    }

    let message = error.message;

    // Add context-specific information
    if (context.operation) {
      message = `Error during ${context.operation}: ${message}`;
    }

    // Add suggestions if available
    if (error.suggestions && error.suggestions.length > 0) {
      message += '\n\nSuggestions:\n' + error.suggestions.map((s) => `• ${s}`).join('\n');
    }

    // Add error ID for support
    message += `\n\nError ID: ${this.generateErrorId()}`;

    return message;
  }

  getErrorReport(errorId: string): ErrorReport | undefined {
    return this.errorReports.get(errorId);
  }

  getErrorReports(filter?: ErrorReportFilter): ErrorReport[] {
    let reports = Array.from(this.errorReports.values());

    if (filter) {
      if (filter.type) {
        reports = reports.filter((r) => r.classification.type === filter.type);
      }
      if (filter.severity) {
        reports = reports.filter((r) => r.classification.severity === filter.severity);
      }
      if (filter.userId) {
        reports = reports.filter((r) => r.context.userId === filter.userId);
      }
      if (filter.startTime) {
        reports = reports.filter((r) => new Date(r.timestamp) >= filter.startTime!);
      }
      if (filter.endTime) {
        reports = reports.filter((r) => new Date(r.timestamp) <= filter.endTime!);
      }
    }

    return reports.sort(
      (a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()
    );
  }

  private initializeErrorPatterns(): void {
    // OpenAI API errors
    this.errorPatterns.set(ErrorType.AUTHENTICATION_ERROR, {
      patterns: ['invalid_api_key', 'invalid_request', 'authentication'],
      category: ErrorCategory.USER_ERROR,
      severity: ErrorSeverity.HIGH,
      recoverable: false,
      userFriendly: true,
      suggestions: ['Check your API key', 'Verify your OpenAI account is active'],
    });

    this.errorPatterns.set(ErrorType.RATE_LIMIT_ERROR, {
      patterns: ['rate_limit_exceeded', 'too_many_requests'],
      category: ErrorCategory.TRANSIENT,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: ['Wait a moment and try again', 'Reduce request frequency'],
    });

    this.errorPatterns.set(ErrorType.QUOTA_EXCEEDED, {
      patterns: ['insufficient_quota', 'quota_exceeded'],
      category: ErrorCategory.USER_ERROR,
      severity: ErrorSeverity.HIGH,
      recoverable: false,
      userFriendly: true,
      suggestions: ['Check your OpenAI billing', 'Upgrade your plan', 'Add payment method'],
    });

    this.errorPatterns.set(ErrorType.MODEL_NOT_FOUND, {
      patterns: ['model_not_found', 'invalid_model'],
      category: ErrorCategory.USER_ERROR,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: [
        'Check model name spelling',
        'Verify model is available',
        'Use alternative model',
      ],
    });

    this.errorPatterns.set(ErrorType.CONTENT_POLICY_ERROR, {
      patterns: ['content_policy', 'content_filter', 'safety'],
      category: ErrorCategory.USER_ERROR,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: [
        'Modify your content',
        'Remove sensitive information',
        'Use alternative phrasing',
      ],
    });

    // Network errors
    this.errorPatterns.set(ErrorType.NETWORK_ERROR, {
      patterns: ['network', 'connection', 'ECONNRESET', 'ECONNREFUSED'],
      category: ErrorCategory.TRANSIENT,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: [
        'Check your internet connection',
        'Try again in a moment',
        'Contact support if persistent',
      ],
    });

    this.errorPatterns.set(ErrorType.TIMEOUT_ERROR, {
      patterns: ['timeout', 'ETIMEDOUT'],
      category: ErrorCategory.TRANSIENT,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: [
        'Try again with a shorter request',
        'Check network stability',
        'Increase timeout settings',
      ],
    });

    // Configuration errors
    this.errorPatterns.set(ErrorType.CONFIGURATION_ERROR, {
      patterns: ['configuration', 'config', 'invalid_config'],
      category: ErrorCategory.SYSTEM_ERROR,
      severity: ErrorSeverity.HIGH,
      recoverable: false,
      userFriendly: true,
      suggestions: [
        'Check configuration files',
        'Verify environment variables',
        'Contact administrator',
      ],
    });
  }

  private initializeRecoveryStrategies(): void {
    // Rate limiting - exponential backoff
    this.recoveryStrategies.set(ErrorType.RATE_LIMIT_ERROR, {
      type: RecoveryType.RETRY,
      action: RecoveryAction.EXPONENTIAL_BACKOFF,
      maxAttempts: 3,
      delay: 1000,
    });

    // Network errors - exponential backoff with fallback
    this.recoveryStrategies.set(ErrorType.NETWORK_ERROR, {
      type: RecoveryType.RETRY,
      action: RecoveryAction.EXPONENTIAL_BACKOFF,
      maxAttempts: 5,
      delay: 2000,
      fallback: {
        type: 'cached_response',
        config: { maxAge: 300000 }, // 5 minutes
        description: 'Use cached response if available',
      },
    });

    // Model not found - try alternative model
    this.recoveryStrategies.set(ErrorType.MODEL_NOT_FOUND, {
      type: RecoveryType.ALTERNATIVE,
      action: RecoveryAction.USE_ALTERNATIVE_MODEL,
      fallback: {
        type: 'alternative_model',
        config: { alternatives: ['gpt-3.5-turbo', 'gpt-4'] },
        description: 'Use alternative model',
      },
    });

    // Timeout - reduce complexity
    this.recoveryStrategies.set(ErrorType.TIMEOUT_ERROR, {
      type: RecoveryType.FALLBACK,
      action: RecoveryAction.REDUCE_COMPLEXITY,
      fallback: {
        type: 'simplified_request',
        config: { maxTokensReduction: 0.5 },
        description: 'Reduce request complexity',
      },
    });

    // Content policy - escalate to user
    this.recoveryStrategies.set(ErrorType.CONTENT_POLICY_ERROR, {
      type: RecoveryType.ESCALATE,
      action: RecoveryAction.CONTACT_SUPPORT,
      fallback: {
        type: 'manual_intervention',
        config: { requireUserAction: true },
        description: 'Requires user intervention',
      },
    });
  }

  private classifyOpenAIError(error: any): ErrorClassification {
    const errorType = error.error?.type;
    const errorCode = error.error?.code;
    const message = error.error?.message || error.message;

    // Check specific error patterns
    for (const [type, pattern] of this.errorPatterns) {
      if (
        this.matchesPattern(errorType, pattern.patterns) ||
        this.matchesPattern(errorCode, pattern.patterns) ||
        this.matchesPattern(message, pattern.patterns)
      ) {
        return this.createClassification(type, message, pattern);
      }
    }

    // Default OpenAI error classification
    return this.createClassification(ErrorType.API_ERROR, message, {
      category: ErrorCategory.SYSTEM_ERROR,
      severity: ErrorSeverity.MEDIUM,
      recoverable: false,
      userFriendly: false,
      suggestions: ['Try again', 'Contact support if problem persists'],
    });
  }

  private classifyHTTPError(error: any): ErrorClassification {
    const status = error.status || error.statusCode;
    const message = error.message;

    switch (status) {
      case 401:
        return this.createClassification(
          ErrorType.AUTHENTICATION_ERROR,
          message,
          this.errorPatterns.get(ErrorType.AUTHENTICATION_ERROR)!
        );

      case 403:
        return this.createClassification(ErrorType.PERMISSION_ERROR, message, {
          category: ErrorCategory.USER_ERROR,
          severity: ErrorSeverity.HIGH,
          recoverable: false,
          userFriendly: true,
          suggestions: ['Check your permissions', 'Contact administrator'],
        });

      case 429:
        return this.createClassification(
          ErrorType.RATE_LIMIT_ERROR,
          message,
          this.errorPatterns.get(ErrorType.RATE_LIMIT_ERROR)!
        );

      case 500:
      case 502:
      case 503:
      case 504:
        return this.createClassification(ErrorType.SYSTEM_ERROR, message, {
          category: ErrorCategory.TRANSIENT,
          severity: ErrorSeverity.HIGH,
          recoverable: true,
          userFriendly: true,
          suggestions: ['Try again in a moment', 'Contact support if problem persists'],
        });

      default:
        return this.createClassification(ErrorType.API_ERROR, message, {
          category: ErrorCategory.SYSTEM_ERROR,
          severity: ErrorSeverity.MEDIUM,
          recoverable: false,
          userFriendly: false,
          suggestions: ['Check request format', 'Contact support'],
        });
    }
  }

  private classifyNetworkError(error: any): ErrorClassification {
    const code = error.code;
    const message = error.message;

    // Check specific network error patterns
    for (const [type, pattern] of this.errorPatterns) {
      if (
        this.matchesPattern(code, pattern.patterns) ||
        this.matchesPattern(message, pattern.patterns)
      ) {
        return this.createClassification(type, message, pattern);
      }
    }

    return this.createClassification(ErrorType.NETWORK_ERROR, message, {
      category: ErrorCategory.TRANSIENT,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: ['Check network connection', 'Try again'],
    });
  }

  private classifyValidationError(error: any): ErrorClassification {
    return this.createClassification(ErrorType.VALIDATION_ERROR, error.message, {
      category: ErrorCategory.USER_ERROR,
      severity: ErrorSeverity.MEDIUM,
      recoverable: true,
      userFriendly: true,
      suggestions: ['Check input format', 'Validate required fields', 'Review documentation'],
    });
  }

  private classifyRuntimeError(error: Error): ErrorClassification {
    if (error.name === 'MemoryError' || error.message.includes('memory')) {
      return this.createClassification(ErrorType.MEMORY_ERROR, error.message, {
        category: ErrorCategory.SYSTEM_ERROR,
        severity: ErrorSeverity.HIGH,
        recoverable: true,
        userFriendly: true,
        suggestions: ['Reduce request size', 'Try with simpler input', 'Contact support'],
      });
    }

    return this.createClassification(ErrorType.RUNTIME_ERROR, error.message, {
      category: ErrorCategory.SYSTEM_ERROR,
      severity: ErrorSeverity.HIGH,
      recoverable: false,
      userFriendly: false,
      suggestions: ['Contact support', 'Try again later'],
    });
  }

  private createDefaultClassification(error: any): ErrorClassification {
    return this.createClassification(ErrorType.UNKNOWN_ERROR, error?.message || 'Unknown error', {
      category: ErrorCategory.SYSTEM_ERROR,
      severity: ErrorSeverity.MEDIUM,
      recoverable: false,
      userFriendly: false,
      suggestions: ['Try again', 'Contact support if problem persists'],
    });
  }

  private createClassification(
    type: ErrorType,
    message: string,
    pattern: ErrorPattern
  ): ErrorClassification {
    return {
      type,
      category: pattern.category,
      severity: pattern.severity,
      recoverable: pattern.recoverable,
      userFriendly: pattern.userFriendly,
      code: type,
      message,
      suggestions: pattern.suggestions,
    };
  }

  private matchesPattern(text: string, patterns: string[]): boolean {
    if (!text) return false;
    const lowerText = text.toLowerCase();
    return patterns.some((pattern) => lowerText.includes(pattern.toLowerCase()));
  }

  private async applyRecoveryStrategy(
    strategy: RecoveryStrategy,
    error: any,
    context: ErrorContext,
    handlingResult: ErrorHandlingResult
  ): Promise<void> {
    switch (strategy.type) {
      case RecoveryType.RETRY:
        // Retry logic is handled by the retry manager
        handlingResult.retryDelay = strategy.delay;
        break;

      case RecoveryType.FALLBACK:
        if (strategy.fallback) {
          handlingResult.fallbackUsed = true;
          this.emit('fallbackTriggered', { strategy, error, context });
        }
        break;

      case RecoveryType.ALTERNATIVE:
        if (strategy.fallback) {
          handlingResult.fallbackUsed = true;
          this.emit('alternativeTriggered', { strategy, error, context });
        }
        break;

      case RecoveryType.ESCALATE:
        this.emit('errorEscalated', { strategy, error, context });
        break;
    }
  }

  private getDefaultRecoveryStrategy(): RecoveryStrategy {
    return {
      type: RecoveryType.RETRY,
      action: RecoveryAction.EXPONENTIAL_BACKOFF,
      maxAttempts: 3,
      delay: 1000,
    };
  }

  private generateErrorId(): string {
    return `ERR_${Date.now()}_${Math.random().toString(36).substr(2, 9).toUpperCase()}`;
  }
}

interface ErrorPattern {
  patterns: string[];
  category: ErrorCategory;
  severity: ErrorSeverity;
  recoverable: boolean;
  userFriendly: boolean;
  suggestions: string[];
}

interface ErrorReportFilter {
  type?: ErrorType;
  severity?: ErrorSeverity;
  userId?: string;
  startTime?: Date;
  endTime?: Date;
}
```

### Subtask 8.2: Add Automatic Error Recovery

**Description**: Implement automatic error recovery mechanisms including retries, fallbacks, alternative models, and cached responses.

**Implementation Details**:

1. **Create Error Recovery Manager**:

```typescript
// packages/providers/src/openai/error-recovery-manager.ts
import {
  ErrorClassification,
  RecoveryStrategy,
  FallbackOption,
} from '../interfaces/error-handling.interface';
import { MessageRequest, MessageChunk } from '@tamma/shared/types';
import { EventEmitter } from 'events';

export interface RecoveryContext {
  originalRequest: MessageRequest;
  error: any;
  classification: ErrorClassification;
  strategy: RecoveryStrategy;
  attempt: number;
  maxAttempts: number;
}

export interface RecoveryResult {
  success: boolean;
  result?: AsyncIterable<MessageChunk>;
  fallbackUsed?: boolean;
  fallbackType?: string;
  attemptCount: number;
  recoveryTime: number;
  message: string;
}

export class ErrorRecoveryManager extends EventEmitter {
  private fallbackHandlers: Map<string, FallbackHandler> = new Map();
  private responseCache: Map<string, CachedResponse> = new Map();

  constructor() {
    super();
    this.initializeFallbackHandlers();
  }

  async attemptRecovery(context: RecoveryContext): Promise<RecoveryResult> {
    const startTime = Date.now();

    try {
      // Check if we should attempt recovery
      if (!context.classification.recoverable || context.attempt >= context.maxAttempts) {
        return {
          success: false,
          attemptCount: context.attempt,
          recoveryTime: Date.now() - startTime,
          message: 'Recovery not possible or max attempts exceeded',
        };
      }

      // Apply recovery strategy
      const result = await this.applyRecoveryStrategy(context);

      this.emit('recoveryAttempted', { context, result });

      return {
        ...result,
        attemptCount: context.attempt,
        recoveryTime: Date.now() - startTime,
      };
    } catch (error) {
      this.emit('recoveryFailed', { context, error });

      return {
        success: false,
        attemptCount: context.attempt,
        recoveryTime: Date.now() - startTime,
        message: `Recovery failed: ${error.message}`,
      };
    }
  }

  private async applyRecoveryStrategy(context: RecoveryContext): Promise<RecoveryResult> {
    const { strategy, originalRequest, error } = context;

    switch (strategy.action) {
      case 'use_alternative_model':
        return this.useAlternativeModel(originalRequest, strategy.fallback);

      case 'use_cached_response':
        return this.useCachedResponse(originalRequest, strategy.fallback);

      case 'reduce_complexity':
        return this.reduceComplexity(originalRequest, strategy.fallback);

      case 'contact_support':
        return this.escalateToSupport(originalRequest, error);

      default:
        return {
          success: false,
          message: `Unknown recovery action: ${strategy.action}`,
        };
    }
  }

  private async useAlternativeModel(
    request: MessageRequest,
    fallback?: FallbackOption
  ): Promise<RecoveryResult> {
    if (!fallback || fallback.type !== 'alternative_model') {
      return {
        success: false,
        message: 'Alternative model fallback not configured',
      };
    }

    const alternatives = fallback.config.alternatives as string[];
    const currentModel = request.model;

    // Find next available alternative
    const alternativeModel = alternatives.find((model) => model !== currentModel);

    if (!alternativeModel) {
      return {
        success: false,
        message: 'No alternative models available',
      };
    }

    try {
      // Create modified request with alternative model
      const modifiedRequest = {
        ...request,
        model: alternativeModel,
        id: `${request.id}_fallback_${alternativeModel}`,
      };

      // This would be handled by the provider's retry mechanism
      this.emit('alternativeModelSelected', {
        originalModel: currentModel,
        alternativeModel,
        request: modifiedRequest,
      });

      return {
        success: true,
        fallbackUsed: true,
        fallbackType: 'alternative_model',
        message: `Using alternative model: ${alternativeModel}`,
      };
    } catch (error) {
      return {
        success: false,
        message: `Alternative model failed: ${error.message}`,
      };
    }
  }

  private async useCachedResponse(
    request: MessageRequest,
    fallback?: FallbackOption
  ): Promise<RecoveryResult> {
    if (!fallback || fallback.type !== 'cached_response') {
      return {
        success: false,
        message: 'Cached response fallback not configured',
      };
    }

    const cacheKey = this.generateCacheKey(request);
    const cached = this.responseCache.get(cacheKey);

    if (!cached) {
      return {
        success: false,
        message: 'No cached response available',
      };
    }

    const maxAge = fallback.config.maxAge || 300000; // 5 minutes default
    const age = Date.now() - cached.timestamp;

    if (age > maxAge) {
      this.responseCache.delete(cacheKey);
      return {
        success: false,
        message: 'Cached response expired',
      };
    }

    this.emit('cachedResponseUsed', { request, cached, age });

    return {
      success: true,
      fallbackUsed: true,
      fallbackType: 'cached_response',
      message: `Using cached response (${Math.round(age / 1000)}s old)`,
    };
  }

  private async reduceComplexity(
    request: MessageRequest,
    fallback?: FallbackOption
  ): Promise<RecoveryResult> {
    if (!fallback || fallback.type !== 'simplified_request') {
      return {
        success: false,
        message: 'Complexity reduction fallback not configured',
      };
    }

    try {
      const reductionFactor = fallback.config.maxTokensReduction || 0.5;
      const modifiedRequest = this.simplifyRequest(request, reductionFactor);

      this.emit('complexityReduced', {
        originalRequest: request,
        simplifiedRequest: modifiedRequest,
        reductionFactor,
      });

      return {
        success: true,
        fallbackUsed: true,
        fallbackType: 'simplified_request',
        message: `Request complexity reduced by ${Math.round(reductionFactor * 100)}%`,
      };
    } catch (error) {
      return {
        success: false,
        message: `Complexity reduction failed: ${error.message}`,
      };
    }
  }

  private async escalateToSupport(request: MessageRequest, error: any): Promise<RecoveryResult> {
    this.emit('supportEscalation', { request, error });

    return {
      success: false,
      fallbackUsed: true,
      fallbackType: 'manual_intervention',
      message: 'Issue escalated to support team',
    };
  }

  private simplifyRequest(request: MessageRequest, reductionFactor: number): MessageRequest {
    const simplified = { ...request };

    // Reduce max tokens if specified
    if (request.maxTokens) {
      simplified.maxTokens = Math.floor(request.maxTokens * reductionFactor);
    }

    // Truncate message content if too long
    if (request.messages) {
      simplified.messages = request.messages.map((message) => {
        if (typeof message.content === 'string') {
          const targetLength = Math.floor(message.content.length * reductionFactor);
          return {
            ...message,
            content: message.content.substring(0, targetLength),
          };
        }
        return message;
      });
    }

    // Reduce temperature for more predictable responses
    if (request.temperature && request.temperature > 0.5) {
      simplified.temperature = 0.5;
    }

    simplified.id = `${request.id}_simplified`;

    return simplified;
  }

  private generateCacheKey(request: MessageRequest): string {
    const keyData = {
      model: request.model,
      messages: request.messages,
      temperature: request.temperature,
      maxTokens: request.maxTokens,
    };

    return Buffer.from(JSON.stringify(keyData)).toString('base64');
  }

  private initializeFallbackHandlers(): void {
    // Alternative model handler
    this.fallbackHandlers.set('alternative_model', {
      canHandle: (fallback) => fallback.type === 'alternative_model',
      handle: this.useAlternativeModel.bind(this),
    });

    // Cached response handler
    this.fallbackHandlers.set('cached_response', {
      canHandle: (fallback) => fallback.type === 'cached_response',
      handle: this.useCachedResponse.bind(this),
    });

    // Simplified request handler
    this.fallbackHandlers.set('simplified_request', {
      canHandle: (fallback) => fallback.type === 'simplified_request',
      handle: this.reduceComplexity.bind(this),
    });
  }

  // Cache management methods
  cacheResponse(request: MessageRequest, response: AsyncIterable<MessageChunk>): void {
    const cacheKey = this.generateCacheKey(request);
    const chunks: MessageChunk[] = [];

    // Collect chunks from the stream
    (async () => {
      for await (const chunk of response) {
        chunks.push(chunk);
      }

      this.responseCache.set(cacheKey, {
        request,
        chunks,
        timestamp: Date.now(),
      });
    })();
  }

  clearCache(): void {
    this.responseCache.clear();
    this.emit('cacheCleared');
  }

  getCacheStats(): CacheStats {
    const now = Date.now();
    let totalSize = 0;
    let expiredCount = 0;

    for (const [key, cached] of this.responseCache) {
      totalSize += JSON.stringify(cached).length;
      if (now - cached.timestamp > 300000) {
        // 5 minutes
        expiredCount++;
      }
    }

    return {
      totalEntries: this.responseCache.size,
      totalSize,
      expiredCount,
      hitRate: this.calculateHitRate(),
    };
  }

  private calculateHitRate(): number {
    // This would be tracked during operation
    return 0; // Placeholder
  }
}

interface FallbackHandler {
  canHandle: (fallback: FallbackOption) => boolean;
  handle: (request: MessageRequest, fallback: FallbackOption) => Promise<RecoveryResult>;
}

interface CachedResponse {
  request: MessageRequest;
  chunks: MessageChunk[];
  timestamp: number;
}

interface CacheStats {
  totalEntries: number;
  totalSize: number;
  expiredCount: number;
  hitRate: number;
}
```

### Subtask 8.3: Add User-Friendly Error Messages

**Description**: Create a system for generating user-friendly error messages with actionable guidance, context-aware messaging, and multi-language support.

**Implementation Details**:

1. **Create Error Message Formatter**:

```typescript
// packages/providers/src/openai/error-message-formatter.ts
import { ErrorClassification, ErrorContext } from '../interfaces/error-handling.interface';
import { EventEmitter } from 'events';

export interface ErrorMessageTemplate {
  type: string;
  templates: MessageTemplate[];
  variables?: Record<string, string>;
  actions?: ActionTemplate[];
}

export interface MessageTemplate {
  language: string;
  severity: string;
  message: string;
  technicalDetails?: string;
}

export interface ActionTemplate {
  type: 'retry' | 'contact_support' | 'check_config' | 'upgrade_plan';
  label: string;
  description: string;
  url?: string;
  automatic?: boolean;
}

export interface FormattingOptions {
  language: string;
  includeTechnicalDetails: boolean;
  includeActions: boolean;
  severity: ErrorSeverity;
  context?: Record<string, any>;
}

export class ErrorMessageFormatter extends EventEmitter {
  private templates: Map<string, ErrorMessageTemplate> = new Map();
  private fallbackLanguage = 'en';

  constructor() {
    super();
    this.initializeTemplates();
  }

  formatMessage(
    classification: ErrorClassification,
    context: ErrorContext,
    options: Partial<FormattingOptions> = {}
  ): FormattedMessage {
    const opts: FormattingOptions = {
      language: 'en',
      includeTechnicalDetails: false,
      includeActions: true,
      severity: classification.severity,
      ...options,
    };

    const template = this.getTemplate(classification.type, opts.language);
    const variables = this.buildVariables(classification, context, opts);

    const userMessage = this.processTemplate(template.message, variables);
    const technicalMessage =
      opts.includeTechnicalDetails && template.technicalDetails
        ? this.processTemplate(template.technicalDetails, variables)
        : classification.message;

    const actions =
      opts.includeActions && template.actions
        ? template.actions.map((action) => this.formatAction(action, variables))
        : [];

    const formatted: FormattedMessage = {
      userMessage,
      technicalMessage,
      actions,
      severity: classification.severity,
      errorId: this.generateErrorId(),
      timestamp: new Date().toISOString(),
      context: {
        operation: context.operation,
        model: context.model,
        userId: context.userId,
      },
    };

    this.emit('messageFormatted', { classification, context, formatted });

    return formatted;
  }

  formatBatch(
    errors: Array<{ classification: ErrorClassification; context: ErrorContext }>,
    options: Partial<FormattingOptions> = {}
  ): BatchFormattedMessage {
    const formatted = errors.map((error) =>
      this.formatMessage(error.classification, error.context, options)
    );

    const summary = this.createSummary(formatted);

    return {
      errors: formatted,
      summary,
      totalCount: errors.length,
      severity: this.getHighestSeverity(formatted),
      timestamp: new Date().toISOString(),
    };
  }

  private initializeTemplates(): void {
    // Authentication errors
    this.addTemplate({
      type: 'authentication_error',
      templates: [
        {
          language: 'en',
          severity: 'high',
          message: 'Your OpenAI API key is invalid or missing.',
          technicalDetails: 'API authentication failed with error: {{error}}',
        },
        {
          language: 'es',
          severity: 'high',
          message: 'Tu clave de API de OpenAI es inválida o no está configurada.',
          technicalDetails: 'Error de autenticación de API: {{error}}',
        },
      ],
      actions: [
        {
          type: 'check_config',
          label: 'Check API Key',
          description: 'Verify your OpenAI API key is correctly configured',
          automatic: false,
        },
        {
          type: 'contact_support',
          label: 'Get Help',
          description: 'Contact support for assistance with API key issues',
          automatic: false,
        },
      ],
    });

    // Rate limit errors
    this.addTemplate({
      type: 'rate_limit_error',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message: "You've exceeded the rate limit. Please wait {{waitTime}} and try again.",
          technicalDetails: 'Rate limit exceeded: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message: 'Has excedido el límite de velocidad. Espera {{waitTime}} e inténtalo de nuevo.',
          technicalDetails: 'Límite de velocidad excedido: {{error}}',
        },
      ],
      actions: [
        {
          type: 'retry',
          label: 'Retry Automatically',
          description: 'Automatically retry after the rate limit resets',
          automatic: true,
        },
      ],
    });

    // Quota exceeded errors
    this.addTemplate({
      type: 'quota_exceeded',
      templates: [
        {
          language: 'en',
          severity: 'high',
          message:
            "You've exceeded your OpenAI API quota. Please check your billing or upgrade your plan.",
          technicalDetails: 'Quota exceeded: {{error}}',
        },
        {
          language: 'es',
          severity: 'high',
          message:
            'Has excedido tu cuota de API de OpenAI. Revisa tu facturación o actualiza tu plan.',
          technicalDetails: 'Cuota excedida: {{error}}',
        },
      ],
      actions: [
        {
          type: 'upgrade_plan',
          label: 'Upgrade Plan',
          description: 'Upgrade your OpenAI plan to increase quota',
          url: 'https://platform.openai.com/account/billing',
          automatic: false,
        },
        {
          type: 'check_config',
          label: 'Check Billing',
          description: 'Review your OpenAI billing settings',
          url: 'https://platform.openai.com/account/billing',
          automatic: false,
        },
      ],
    });

    // Model not found errors
    this.addTemplate({
      type: 'model_not_found',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message: 'The model "{{model}}" is not available. Using "{{alternativeModel}}" instead.',
          technicalDetails: 'Model not found: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message:
            'El modelo "{{model}}" no está disponible. Usando "{{alternativeModel}}" en su lugar.',
          technicalDetails: 'Modelo no encontrado: {{error}}',
        },
      ],
      actions: [
        {
          type: 'retry',
          label: 'Use Alternative Model',
          description: 'Automatically use an available alternative model',
          automatic: true,
        },
      ],
    });

    // Network errors
    this.addTemplate({
      type: 'network_error',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message:
            'Network connection failed. Please check your internet connection and try again.',
          technicalDetails: 'Network error: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message:
            'Error de conexión de red. Verifica tu conexión a internet e inténtalo de nuevo.',
          technicalDetails: 'Error de red: {{error}}',
        },
      ],
      actions: [
        {
          type: 'retry',
          label: 'Retry',
          description: 'Retry the request after checking network connection',
          automatic: true,
        },
      ],
    });

    // Timeout errors
    this.addTemplate({
      type: 'timeout_error',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message: 'The request timed out after {{timeout}}ms. Trying with a simpler request.',
          technicalDetails: 'Request timeout: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message:
            'La solicitud agotó el tiempo después de {{timeout}}ms. Intentando con una solicitud más simple.',
          technicalDetails: 'Tiempo de espera agotado: {{error}}',
        },
      ],
      actions: [
        {
          type: 'retry',
          label: 'Simplify Request',
          description: 'Automatically retry with reduced complexity',
          automatic: true,
        },
      ],
    });

    // Content policy errors
    this.addTemplate({
      type: 'content_policy_error',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message:
            'Your content was flagged by the safety filter. Please modify your content and try again.',
          technicalDetails: 'Content policy violation: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message:
            'Tu contenido fue marcado por el filtro de seguridad. Modifica tu contenido e inténtalo de nuevo.',
          technicalDetails: 'Violación de política de contenido: {{error}}',
        },
      ],
      actions: [
        {
          type: 'contact_support',
          label: 'Review Guidelines',
          description: 'Review content policy guidelines',
          url: 'https://platform.openai.com/policies',
          automatic: false,
        },
      ],
    });

    // Default error
    this.addTemplate({
      type: 'default',
      templates: [
        {
          language: 'en',
          severity: 'medium',
          message:
            'An unexpected error occurred. Please try again or contact support if the problem persists.',
          technicalDetails: 'Unexpected error: {{error}}',
        },
        {
          language: 'es',
          severity: 'medium',
          message:
            'Ocurrió un error inesperado. Inténtalo de nuevo o contacta soporte si el problema persiste.',
          technicalDetails: 'Error inesperado: {{error}}',
        },
      ],
      actions: [
        {
          type: 'retry',
          label: 'Try Again',
          description: 'Retry the request',
          automatic: true,
        },
        {
          type: 'contact_support',
          label: 'Contact Support',
          description: 'Get help from our support team',
          automatic: false,
        },
      ],
    });
  }

  private addTemplate(template: ErrorMessageTemplate): void {
    this.templates.set(template.type, template);
  }

  private getTemplate(errorType: string, language: string): MessageTemplate {
    const errorTemplate = this.templates.get(errorType);

    if (!errorTemplate) {
      // Fallback to default template
      const defaultTemplate = this.templates.get('default');
      if (!defaultTemplate) {
        throw new Error('Default error template not found');
      }
      return this.findLanguageTemplate(defaultTemplate.templates, language);
    }

    return this.findLanguageTemplate(errorTemplate.templates, language);
  }

  private findLanguageTemplate(templates: MessageTemplate[], language: string): MessageTemplate {
    // Try exact match first
    let template = templates.find((t) => t.language === language);

    // Try fallback language
    if (!template && language !== this.fallbackLanguage) {
      template = templates.find((t) => t.language === this.fallbackLanguage);
    }

    // Try English as ultimate fallback
    if (!template) {
      template = templates.find((t) => t.language === 'en');
    }

    // Return first template as last resort
    if (!template) {
      template = templates[0];
    }

    return template!;
  }

  private buildVariables(
    classification: ErrorClassification,
    context: ErrorContext,
    options: FormattingOptions
  ): Record<string, string> {
    return {
      error: classification.message,
      errorType: classification.type,
      severity: classification.severity,
      operation: context.operation,
      model: context.model || 'unknown',
      userId: context.userId || 'anonymous',
      timestamp: context.timestamp,
      attempt: context.attempt?.toString() || '1',
      waitTime: this.estimateWaitTime(classification),
      timeout: context.requestDetails?.timeout?.toString() || '30000',
      alternativeModel: this.getAlternativeModel(classification, context),
      ...options.context,
    };
  }

  private processTemplate(template: string, variables: Record<string, string>): string {
    return template.replace(/\{\{(\w+)\}\}/g, (match, key) => {
      return variables[key] || match;
    });
  }

  private formatAction(action: ActionTemplate, variables: Record<string, string>): FormattedAction {
    return {
      type: action.type,
      label: this.processTemplate(action.label, variables),
      description: this.processTemplate(action.description, variables),
      url: action.url,
      automatic: action.automatic || false,
    };
  }

  private estimateWaitTime(classification: ErrorClassification): string {
    switch (classification.type) {
      case 'rate_limit_error':
        return 'a few seconds';
      case 'timeout_error':
        return 'a moment';
      case 'network_error':
        return '30 seconds';
      default:
        return 'a moment';
    }
  }

  private getAlternativeModel(classification: ErrorClassification, context: ErrorContext): string {
    if (classification.type === 'model_not_found' && context.model) {
      const alternatives = {
        'gpt-4': 'gpt-3.5-turbo',
        'gpt-4-turbo': 'gpt-4',
        'gpt-3.5-turbo': 'gpt-3.5-turbo-16k',
      };
      return alternatives[context.model as keyof typeof alternatives] || 'gpt-3.5-turbo';
    }
    return 'gpt-3.5-turbo';
  }

  private createSummary(messages: FormattedMessage[]): string {
    const severityCounts = messages.reduce(
      (counts, msg) => {
        counts[msg.severity] = (counts[msg.severity] || 0) + 1;
        return counts;
      },
      {} as Record<string, number>
    );

    const parts: string[] = [];

    if (severityCounts.critical > 0) {
      parts.push(`${severityCounts.critical} critical`);
    }
    if (severityCounts.high > 0) {
      parts.push(`${severityCounts.high} high`);
    }
    if (severityCounts.medium > 0) {
      parts.push(`${severityCounts.medium} medium`);
    }
    if (severityCounts.low > 0) {
      parts.push(`${severityCounts.low} low`);
    }

    return parts.length > 0 ? `${parts.join(', ')} errors` : 'No errors';
  }

  private getHighestSeverity(messages: FormattedMessage[]): ErrorSeverity {
    const severityOrder = [
      ErrorSeverity.CRITICAL,
      ErrorSeverity.HIGH,
      ErrorSeverity.MEDIUM,
      ErrorSeverity.LOW,
    ];

    for (const severity of severityOrder) {
      if (messages.some((msg) => msg.severity === severity)) {
        return severity;
      }
    }

    return ErrorSeverity.LOW;
  }

  private generateErrorId(): string {
    return `ERR_${Date.now()}_${Math.random().toString(36).substr(2, 9).toUpperCase()}`;
  }
}

interface FormattedMessage {
  userMessage: string;
  technicalMessage: string;
  actions: FormattedAction[];
  severity: ErrorSeverity;
  errorId: string;
  timestamp: string;
  context: {
    operation?: string;
    model?: string;
    userId?: string;
  };
}

interface FormattedAction {
  type: string;
  label: string;
  description: string;
  url?: string;
  automatic: boolean;
}

interface BatchFormattedMessage {
  errors: FormattedMessage[];
  summary: string;
  totalCount: number;
  severity: ErrorSeverity;
  timestamp: string;
}

// Import ErrorSeverity enum
enum ErrorSeverity {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/error-handling.interface.ts`

2. **Error Handling Implementation**:
   - `packages/providers/src/openai/error-handler.ts`
   - `packages/providers/src/openai/error-recovery-manager.ts`
   - `packages/providers/src/openai/error-message-formatter.ts`

3. **Error Templates**:
   - `packages/providers/src/openai/error-templates/en.json`
   - `packages/providers/src/openai/error-templates/es.json`
   - `packages/providers/src/openai/error-templates/fr.json`

4. **Updated Files**:
   - `packages/providers/src/openai/openai-provider.ts` (integrate error handling)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/error-handler.test.ts
describe('OpenAIErrorHandler', () => {
  let errorHandler: OpenAIErrorHandler;

  beforeEach(() => {
    errorHandler = new OpenAIErrorHandler();
  });

  describe('classifyError', () => {
    it('should classify OpenAI API errors correctly', () => {
      const openaiError = {
        error: {
          type: 'invalid_api_key',
          message: 'Invalid API key',
        },
      };

      const classification = errorHandler.classifyError(openaiError);

      expect(classification.type).toBe(ErrorType.AUTHENTICATION_ERROR);
      expect(classification.category).toBe(ErrorCategory.USER_ERROR);
      expect(classification.severity).toBe(ErrorSeverity.HIGH);
      expect(classification.recoverable).toBe(false);
    });

    it('should classify HTTP errors correctly', () => {
      const httpError = {
        status: 429,
        message: 'Too many requests',
      };

      const classification = errorHandler.classifyError(httpError);

      expect(classification.type).toBe(ErrorType.RATE_LIMIT_ERROR);
      expect(classification.category).toBe(ErrorCategory.TRANSIENT);
      expect(classification.recoverable).toBe(true);
    });

    it('should classify network errors correctly', () => {
      const networkError = {
        code: 'ECONNRESET',
        message: 'Connection reset',
      };

      const classification = errorHandler.classifyError(networkError);

      expect(classification.type).toBe(ErrorType.NETWORK_ERROR);
      expect(classification.category).toBe(ErrorCategory.TRANSIENT);
      expect(classification.recoverable).toBe(true);
    });
  });

  describe('handleError', () => {
    it('should handle recoverable errors with retry strategy', async () => {
      const error = {
        status: 429,
        message: 'Rate limit exceeded',
      };

      const context: ErrorContext = {
        operation: 'chat_completion',
        operationId: 'test-123',
        timestamp: new Date().toISOString(),
      };

      const result = await errorHandler.handleError(error, context);

      expect(result.handled).toBe(true);
      expect(result.shouldRetry).toBe(true);
      expect(result.retryDelay).toBeGreaterThan(0);
      expect(result.userMessage).toContain('rate limit');
    });

    it('should handle non-recoverable errors appropriately', async () => {
      const error = {
        error: {
          type: 'invalid_api_key',
          message: 'Invalid API key',
        },
      };

      const context: ErrorContext = {
        operation: 'chat_completion',
        operationId: 'test-123',
        timestamp: new Date().toISOString(),
      };

      const result = await errorHandler.handleError(error, context);

      expect(result.handled).toBe(true);
      expect(result.shouldRetry).toBe(false);
      expect(result.userMessage).toContain('API key');
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/error-recovery-manager.test.ts
describe('ErrorRecoveryManager', () => {
  let recoveryManager: ErrorRecoveryManager;

  beforeEach(() => {
    recoveryManager = new ErrorRecoveryManager();
  });

  describe('attemptRecovery', () => {
    it('should recover using alternative model', async () => {
      const context: RecoveryContext = {
        originalRequest: {
          id: 'test-123',
          model: 'gpt-4',
          messages: [{ role: 'user', content: 'Hello' }],
        } as MessageRequest,
        error: new Error('Model not found'),
        classification: {
          type: ErrorType.MODEL_NOT_FOUND,
          category: ErrorCategory.USER_ERROR,
          severity: ErrorSeverity.MEDIUM,
          recoverable: true,
          userFriendly: true,
          code: 'model_not_found',
          message: 'Model not found',
        },
        strategy: {
          type: RecoveryType.ALTERNATIVE,
          action: RecoveryAction.USE_ALTERNATIVE_MODEL,
          fallback: {
            type: 'alternative_model',
            config: { alternatives: ['gpt-3.5-turbo', 'gpt-4'] },
            description: 'Use alternative model',
          },
        },
        attempt: 1,
        maxAttempts: 3,
      };

      const result = await recoveryManager.attemptRecovery(context);

      expect(result.success).toBe(true);
      expect(result.fallbackUsed).toBe(true);
      expect(result.fallbackType).toBe('alternative_model');
    });

    it('should use cached response when available', async () => {
      const request: MessageRequest = {
        id: 'test-123',
        model: 'gpt-3.5-turbo',
        messages: [{ role: 'user', content: 'Hello' }],
      } as MessageRequest;

      // Pre-cache a response
      const mockResponse = [
        /* mock chunks */
      ] as MessageChunk[];
      recoveryManager.cacheResponse(request, mockResponse[Symbol.asyncIterator]());

      const context: RecoveryContext = {
        originalRequest: request,
        error: new Error('Network error'),
        classification: {
          type: ErrorType.NETWORK_ERROR,
          category: ErrorCategory.TRANSIENT,
          severity: ErrorSeverity.MEDIUM,
          recoverable: true,
          userFriendly: true,
          code: 'network_error',
          message: 'Network error',
        },
        strategy: {
          type: RecoveryType.FALLBACK,
          action: RecoveryAction.USE_CACHED_RESPONSE,
          fallback: {
            type: 'cached_response',
            config: { maxAge: 300000 },
            description: 'Use cached response',
          },
        },
        attempt: 1,
        maxAttempts: 3,
      };

      const result = await recoveryManager.attemptRecovery(context);

      expect(result.success).toBe(true);
      expect(result.fallbackUsed).toBe(true);
      expect(result.fallbackType).toBe('cached_response');
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/error-message-formatter.test.ts
describe('ErrorMessageFormatter', () => {
  let formatter: ErrorMessageFormatter;

  beforeEach(() => {
    formatter = new ErrorMessageFormatter();
  });

  describe('formatMessage', () => {
    it('should format authentication error message', () => {
      const classification: ErrorClassification = {
        type: ErrorType.AUTHENTICATION_ERROR,
        category: ErrorCategory.USER_ERROR,
        severity: ErrorSeverity.HIGH,
        recoverable: false,
        userFriendly: true,
        code: 'authentication_error',
        message: 'Invalid API key',
      };

      const context: ErrorContext = {
        operation: 'chat_completion',
        operationId: 'test-123',
        timestamp: new Date().toISOString(),
      };

      const formatted = formatter.formatMessage(classification, context);

      expect(formatted.userMessage).toContain('API key');
      expect(formatted.actions).toHaveLength(2);
      expect(formatted.severity).toBe(ErrorSeverity.HIGH);
      expect(formatted.errorId).toMatch(/^ERR_\d+_[A-Z0-9]+$/);
    });

    it('should format rate limit error with wait time', () => {
      const classification: ErrorClassification = {
        type: ErrorType.RATE_LIMIT_ERROR,
        category: ErrorCategory.TRANSIENT,
        severity: ErrorSeverity.MEDIUM,
        recoverable: true,
        userFriendly: true,
        code: 'rate_limit_error',
        message: 'Rate limit exceeded',
      };

      const context: ErrorContext = {
        operation: 'chat_completion',
        operationId: 'test-123',
        timestamp: new Date().toISOString(),
      };

      const formatted = formatter.formatMessage(classification, context);

      expect(formatted.userMessage).toContain('wait');
      expect(formatted.actions.some((a) => a.type === 'retry')).toBe(true);
    });

    it('should handle multiple languages', () => {
      const classification: ErrorClassification = {
        type: ErrorType.AUTHENTICATION_ERROR,
        category: ErrorCategory.USER_ERROR,
        severity: ErrorSeverity.HIGH,
        recoverable: false,
        userFriendly: true,
        code: 'authentication_error',
        message: 'Invalid API key',
      };

      const context: ErrorContext = {
        operation: 'chat_completion',
        operationId: 'test-123',
        timestamp: new Date().toISOString(),
      };

      const formattedEn = formatter.formatMessage(classification, context, { language: 'en' });
      const formattedEs = formatter.formatMessage(classification, context, { language: 'es' });

      expect(formattedEn.userMessage).toContain('API key');
      expect(formattedEs.userMessage).toContain('clave de API');
      expect(formattedEn.userMessage).not.toBe(formattedEs.userMessage);
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/openai-error-handling.test.ts
describe('OpenAI Provider Error Handling Integration', () => {
  let provider: OpenAIProvider;

  beforeEach(() => {
    provider = new OpenAIProvider({
      apiKey: 'invalid-key',
      errorHandling: {
        enabled: true,
        enableRecovery: true,
        enableUserFriendlyMessages: true,
      },
    });
  });

  describe('API Error Handling', () => {
    it('should handle authentication errors gracefully', async () => {
      const request: MessageRequest = {
        id: 'auth-test',
        userId: 'test-user',
        model: 'gpt-3.5-turbo',
        messages: [{ role: 'user', content: 'Hello' }],
      };

      const chunks = [];
      let error: any;

      try {
        for await (const chunk of provider.sendMessage(request)) {
          chunks.push(chunk);
        }
      } catch (e) {
        error = e;
      }

      expect(error).toBeDefined();
      expect(error.userMessage).toContain('API key');
      expect(error.errorId).toBeDefined();
      expect(error.actions).toBeDefined();
    });

    it('should handle rate limiting with automatic retry', async () => {
      // Mock rate limit response
      const request: MessageRequest = {
        id: 'rate-limit-test',
        userId: 'test-user',
        model: 'gpt-3.5-turbo',
        messages: [{ role: 'user', content: 'Hello' }],
      };

      // This would require mocking the OpenAI client to return rate limit errors
      // and verify that retry logic is triggered
    }, 15000);
  });
});
```

## Security Considerations

1. **Error Information Disclosure**:
   - Sanitize error messages to prevent information leakage
   - Avoid exposing sensitive API keys or internal system details
   - Use generic error messages for security-sensitive errors

2. **Error Injection Prevention**:
   - Validate all error message templates
   - Escape user-provided content in error messages
   - Prevent error message injection attacks

3. **Audit Logging**:
   - Log all errors with appropriate context
   - Include error classification and recovery actions
   - Monitor for error patterns that might indicate attacks

## Dependencies

### New Dependencies

```json
{
  "uuid": "^9.0.1"
}
```

### Dev Dependencies

```json
{
  "@types/uuid": "^9.0.7"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Error rates by type and severity
   - Recovery success rates
   - Fallback usage patterns
   - User satisfaction with error messages

2. **Logging**:
   - All error classifications and handling results
   - Recovery attempts and outcomes
   - User interactions with error messages
   - Performance impact of error handling

3. **Alerts**:
   - High error rates for critical operations
   - Recovery failures
   - Security-related errors
   - User-reported issues

## Acceptance Criteria

1. ✅ **Error Classification**: Comprehensive error classification system
2. ✅ **Recovery Strategies**: Automatic error recovery with multiple strategies
3. ✅ **User Messages**: User-friendly error messages with actionable guidance
4. ✅ **Multi-language**: Support for multiple languages in error messages
5. ✅ **Fallback Options**: Multiple fallback options (alternative models, cache, etc.)
6. ✅ **Integration**: Seamless integration with OpenAI provider
7. ✅ **Testing**: Comprehensive unit and integration test coverage
8. ✅ **Documentation**: Clear error handling documentation
9. ✅ **Performance**: Minimal overhead on normal operations
10. ✅ **Security**: Secure error handling without information leakage

## Success Metrics

- Error classification accuracy > 95%
- Recovery success rate > 80% for recoverable errors
- User satisfaction with error messages > 85%
- Zero security incidents from error handling
- Complete audit trail for all errors
- Performance overhead < 5% on normal operations
