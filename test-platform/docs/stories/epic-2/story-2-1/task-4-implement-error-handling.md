# Task 4: Implement Error Handling

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 4 - Error handling and retry logic at provider level  
**Status**: Ready for Development

## Overview

This task involves implementing comprehensive error handling and retry logic for AI provider operations. The system must handle various types of errors gracefully, provide meaningful error information, and implement intelligent retry mechanisms with exponential backoff to ensure reliability.

## Subtasks

### Subtask 4.1: Create Provider-Specific Error Classes

**Objective**: Define a hierarchy of error classes that can represent different types of provider failures.

**Implementation Details**:

1. **File Location**: `packages/providers/src/errors/ProviderError.ts`

2. **Base Provider Error Class**:

   ```typescript
   export class ProviderError extends Error {
     public readonly code: string;
     public readonly type: ProviderErrorType;
     public readonly severity: ErrorSeverity;
     public readonly retryable: boolean;
     public readonly providerName?: string;
     public readonly requestId?: string;
     public readonly timestamp: string;
     public readonly context: Record<string, unknown>;
     public readonly cause?: Error;
     public readonly retryInfo?: RetryInfo;

     constructor(code: string, message: string, options: ProviderErrorOptions = {}) {
       super(message);
       this.name = 'ProviderError';
       this.code = code;
       this.type = options.type || ProviderErrorType.UNKNOWN;
       this.severity = options.severity || ErrorSeverity.MEDIUM;
       this.retryable = options.retryable ?? false;
       this.providerName = options.providerName;
       this.requestId = options.requestId;
       this.timestamp = new Date().toISOString();
       this.context = options.context || {};
       this.cause = options.cause;
       this.retryInfo = options.retryInfo;

       // Maintains proper stack trace for where our error was thrown
       if (Error.captureStackTrace) {
         Error.captureStackTrace(this, ProviderError);
       }
     }

     // Serialization methods
     toJSON(): ProviderErrorJSON {
       return {
         name: this.name,
         code: this.code,
         message: this.message,
         type: this.type,
         severity: this.severity,
         retryable: this.retryable,
         providerName: this.providerName,
         requestId: this.requestId,
         timestamp: this.timestamp,
         context: this.context,
         cause: this.cause?.message,
         retryInfo: this.retryInfo,
         stack: this.stack,
       };
     }

     // Static factory methods
     static fromError(error: unknown, context?: Record<string, unknown>): ProviderError {
       if (error instanceof ProviderError) {
         return error;
       }

       if (error instanceof Error) {
         return new ProviderError('UNKNOWN_ERROR', error.message, {
           cause: error,
           context,
         });
       }

       return new ProviderError('UNKNOWN_ERROR', String(error), { context });
     }

     // Check if error matches specific criteria
     matchesCode(code: string): boolean;
     matchesType(type: ProviderErrorType): boolean;
     isRetryable(): boolean;
     shouldEscalate(): boolean;
   }

   export interface ProviderErrorOptions {
     type?: ProviderErrorType;
     severity?: ErrorSeverity;
     retryable?: boolean;
     providerName?: string;
     requestId?: string;
     context?: Record<string, unknown>;
     cause?: Error;
     retryInfo?: RetryInfo;
   }

   export interface ProviderErrorJSON {
     name: string;
     code: string;
     message: string;
     type: ProviderErrorType;
     severity: ErrorSeverity;
     retryable: boolean;
     providerName?: string;
     requestId?: string;
     timestamp: string;
     context: Record<string, unknown>;
     cause?: string;
     retryInfo?: RetryInfo;
     stack?: string;
   }
   ```

3. **Error Type Enumeration**:

   ```typescript
   export enum ProviderErrorType {
     // Network and connectivity errors
     NETWORK_ERROR = 'network_error',
     TIMEOUT_ERROR = 'timeout_error',
     CONNECTION_REFUSED = 'connection_refused',
     DNS_RESOLUTION_FAILED = 'dns_resolution_failed',

     // Authentication and authorization errors
     AUTHENTICATION_FAILED = 'authentication_failed',
     AUTHORIZATION_FAILED = 'authorization_failed',
     INVALID_API_KEY = 'invalid_api_key',
     EXPIRED_API_KEY = 'expired_api_key',
     INSUFFICIENT_PERMISSIONS = 'insufficient_permissions',

     // Rate limiting errors
     RATE_LIMITED = 'rate_limited',
     QUOTA_EXCEEDED = 'quota_exceeded',
     CONCURRENT_LIMIT_EXCEEDED = 'concurrent_limit_exceeded',

     // Request and response errors
     INVALID_REQUEST = 'invalid_request',
     MALFORMED_RESPONSE = 'malformed_response',
     RESPONSE_TOO_LARGE = 'response_too_large',
     INVALID_MODEL = 'invalid_model',

     // Content and moderation errors
     CONTENT_FILTERED = 'content_filtered',
     SAFETY_VIOLATION = 'safety_violation',
     COPYRIGHT_VIOLATION = 'copyright_violation',

     // Provider-specific errors
     PROVIDER_UNAVAILABLE = 'provider_unavailable',
     PROVIDER_MAINTENANCE = 'provider_maintenance',
     MODEL_UNAVAILABLE = 'model_unavailable',
     FEATURE_NOT_SUPPORTED = 'feature_not_supported',

     // System and internal errors
     INTERNAL_ERROR = 'internal_error',
     CONFIGURATION_ERROR = 'configuration_error',
     VALIDATION_ERROR = 'validation_error',

     // Unknown errors
     UNKNOWN = 'unknown',
   }
   ```

4. **Error Severity Levels**:

   ```typescript
   export enum ErrorSeverity {
     LOW = 'low', // Non-critical, can be ignored
     MEDIUM = 'medium', // Important but not blocking
     HIGH = 'high', // Serious, may impact functionality
     CRITICAL = 'critical', // System-breaking, requires immediate attention
   }
   ```

5. **Specialized Error Classes**:

   ```typescript
   // Network-related errors
   export class NetworkError extends ProviderError {
     constructor(message: string, options: ProviderErrorOptions = {}) {
       super('NETWORK_ERROR', message, {
         type: ProviderErrorType.NETWORK_ERROR,
         severity: ErrorSeverity.MEDIUM,
         retryable: true,
         ...options,
       });
       this.name = 'NetworkError';
     }
   }

   export class TimeoutError extends ProviderError {
     constructor(message: string, timeout: number, options: ProviderErrorOptions = {}) {
       super('TIMEOUT_ERROR', message, {
         type: ProviderErrorType.TIMEOUT_ERROR,
         severity: ErrorSeverity.MEDIUM,
         retryable: true,
         context: { timeout, ...options.context },
         ...options,
       });
       this.name = 'TimeoutError';
     }
   }

   // Authentication errors
   export class AuthenticationError extends ProviderError {
     constructor(message: string, options: ProviderErrorOptions = {}) {
       super('AUTHENTICATION_FAILED', message, {
         type: ProviderErrorType.AUTHENTICATION_FAILED,
         severity: ErrorSeverity.HIGH,
         retryable: false,
         ...options,
       });
       this.name = 'AuthenticationError';
     }
   }

   // Rate limiting errors
   export class RateLimitError extends ProviderError {
     constructor(message: string, retryAfter?: number, options: ProviderErrorOptions = {}) {
       super('RATE_LIMITED', message, {
         type: ProviderErrorType.RATE_LIMITED,
         severity: ErrorSeverity.MEDIUM,
         retryable: true,
         context: { retryAfter, ...options.context },
         retryInfo: { suggestedDelay: retryAfter ? retryAfter * 1000 : undefined },
         ...options,
       });
       this.name = 'RateLimitError';
     }
   }

   // Validation errors
   export class ValidationError extends ProviderError {
     constructor(
       message: string,
       field?: string,
       value?: unknown,
       options: ProviderErrorOptions = {}
     ) {
       super('VALIDATION_ERROR', message, {
         type: ProviderErrorType.VALIDATION_ERROR,
         severity: ErrorSeverity.MEDIUM,
         retryable: false,
         context: { field, value, ...options.context },
         ...options,
       });
       this.name = 'ValidationError';
     }
   }

   // Provider unavailable errors
   export class ProviderUnavailableError extends ProviderError {
     constructor(message: string, options: ProviderErrorOptions = {}) {
       super('PROVIDER_UNAVAILABLE', message, {
         type: ProviderErrorType.PROVIDER_UNAVAILABLE,
         severity: ErrorSeverity.HIGH,
         retryable: true,
         ...options,
       });
       this.name = 'ProviderUnavailableError';
     }
   }
   ```

### Subtask 4.2: Implement Retry Logic with Exponential Backoff

**Objective**: Create intelligent retry mechanisms that handle transient failures appropriately.

**Implementation Details**:

1. **File Location**: `packages/providers/src/retry/RetryManager.ts`

2. **Retry Configuration Interface**:

   ```typescript
   export interface RetryConfig {
     maxAttempts: number;
     baseDelay: number;
     maxDelay: number;
     backoffMultiplier: number;
     jitterEnabled: boolean;
     jitterFactor: number;
     retryableErrors: string[];
     nonRetryableErrors: string[];
     retryCondition?: (error: ProviderError, attempt: number) => boolean;
     onRetry?: (error: ProviderError, attempt: number, delay: number) => void;
     onFailed?: (lastError: ProviderError, totalAttempts: number) => void;
   }

   export interface RetryInfo {
     attempt: number;
     maxAttempts: number;
     totalDelay: number;
     nextDelay: number;
     errors: ProviderError[];
     startTime: number;
     endTime?: number;
   }
   ```

3. **Retry Manager Implementation**:

   ```typescript
   export class RetryManager {
     private config: RetryConfig;
     private logger: Logger;

     constructor(config: RetryConfig, logger: Logger) {
       this.config = this.validateConfig(config);
       this.logger = logger;
     }

     async executeWithRetry<T>(
       operation: () => Promise<T>,
       context?: Record<string, unknown>
     ): Promise<T> {
       const startTime = Date.now();
       const errors: ProviderError[] = [];

       for (let attempt = 1; attempt <= this.config.maxAttempts; attempt++) {
         try {
           const result = await operation();

           if (attempt > 1) {
             this.logger.info('Operation succeeded after retry', {
               attempt,
               totalAttempts: this.config.maxAttempts,
               totalTime: Date.now() - startTime,
               context,
             });
           }

           return result;
         } catch (error) {
           const providerError = ProviderError.fromError(error, context);
           errors.push(providerError);

           // Check if we should retry
           if (!this.shouldRetry(providerError, attempt)) {
             this.logger.error('Operation failed, not retryable', {
               error: providerError.toJSON(),
               attempt,
               context,
             });

             this.config.onFailed?.(providerError, attempt);
             throw providerError;
           }

           // Calculate delay for next attempt
           const delay = this.calculateDelay(attempt);

           this.logger.warn('Operation failed, retrying', {
             error: providerError.toJSON(),
             attempt,
             nextAttempt: attempt + 1,
             delay,
             context,
           });

           this.config.onRetry?.(providerError, attempt, delay);

           // Wait before retrying
           if (attempt < this.config.maxAttempts) {
             await this.sleep(delay);
           }
         }
       }

       // All attempts failed
       const finalError = errors[errors.length - 1];
       const retryInfo: RetryInfo = {
         attempt: this.config.maxAttempts,
         maxAttempts: this.config.maxAttempts,
         totalDelay: errors.reduce((sum, error, index) => {
           return sum + (index < errors.length - 1 ? this.calculateDelay(index + 1) : 0);
         }, 0),
         nextDelay: 0,
         errors,
         startTime,
         endTime: Date.now(),
       };

       finalError.retryInfo = retryInfo;
       this.config.onFailed?.(finalError, this.config.maxAttempts);

       throw finalError;
     }

     private shouldRetry(error: ProviderError, attempt: number): boolean {
       // Check if we've exceeded max attempts
       if (attempt >= this.config.maxAttempts) {
         return false;
       }

       // Check if error is explicitly non-retryable
       if (this.config.nonRetryableErrors.includes(error.code)) {
         return false;
       }

       // Check if error is explicitly retryable
       if (this.config.retryableErrors.includes(error.code)) {
         return true;
       }

       // Use error's retryable flag
       if (error.retryable !== undefined) {
         return error.retryable;
       }

       // Use custom retry condition
       if (this.config.retryCondition) {
         return this.config.retryCondition(error, attempt);
       }

       // Default behavior based on error type
       return this.isRetryableByType(error.type);
     }

     private calculateDelay(attempt: number): number {
       // Exponential backoff: delay = baseDelay * (backoffMultiplier ^ (attempt - 1))
       let delay = this.config.baseDelay * Math.pow(this.config.backoffMultiplier, attempt - 1);

       // Apply maximum delay limit
       delay = Math.min(delay, this.config.maxDelay);

       // Add jitter if enabled
       if (this.config.jitterEnabled) {
         const jitter = delay * this.config.jitterFactor * (Math.random() - 0.5);
         delay = Math.max(0, delay + jitter);
       }

       return Math.floor(delay);
     }

     private isRetryableByType(errorType: ProviderErrorType): boolean {
       const retryableTypes = new Set([
         ProviderErrorType.NETWORK_ERROR,
         ProviderErrorType.TIMEOUT_ERROR,
         ProviderErrorType.CONNECTION_REFUSED,
         ProviderErrorType.RATE_LIMITED,
         ProviderErrorType.PROVIDER_UNAVAILABLE,
         ProviderErrorType.PROVIDER_MAINTENANCE,
         ProviderErrorType.MODEL_UNAVAILABLE,
       ]);

       return retryableTypes.has(errorType);
     }

     private sleep(ms: number): Promise<void> {
       return new Promise((resolve) => setTimeout(resolve, ms));
     }

     private validateConfig(config: RetryConfig): RetryConfig {
       return {
         maxAttempts: Math.max(1, config.maxAttempts || 3),
         baseDelay: Math.max(0, config.baseDelay || 1000),
         maxDelay: Math.max(config.baseDelay || 1000, config.maxDelay || 30000),
         backoffMultiplier: Math.max(1, config.backoffMultiplier || 2),
         jitterEnabled: config.jitterEnabled !== false,
         jitterFactor: Math.max(0, Math.min(1, config.jitterFactor || 0.1)),
         retryableErrors: config.retryableErrors || [],
         nonRetryableErrors: config.nonRetryableErrors || [],
         retryCondition: config.retryCondition,
         onRetry: config.onRetry,
         onFailed: config.onFailed,
       };
     }
   }
   ```

4. **Predefined Retry Configurations**:

   ```typescript
   export const RetryConfigs = {
     // Conservative config for critical operations
     conservative: {
       maxAttempts: 3,
       baseDelay: 1000,
       maxDelay: 10000,
       backoffMultiplier: 2,
       jitterEnabled: true,
       jitterFactor: 0.1,
       retryableErrors: ['RATE_LIMITED', 'NETWORK_ERROR', 'TIMEOUT_ERROR'],
       nonRetryableErrors: ['AUTHENTICATION_FAILED', 'VALIDATION_ERROR'],
     } as RetryConfig,

     // Aggressive config for non-critical operations
     aggressive: {
       maxAttempts: 5,
       baseDelay: 500,
       maxDelay: 30000,
       backoffMultiplier: 1.5,
       jitterEnabled: true,
       jitterFactor: 0.2,
       retryableErrors: ['RATE_LIMITED', 'NETWORK_ERROR', 'TIMEOUT_ERROR', 'PROVIDER_UNAVAILABLE'],
       nonRetryableErrors: ['AUTHENTICATION_FAILED', 'VALIDATION_ERROR', 'CONTENT_FILTERED'],
     } as RetryConfig,

     // Fast config for real-time operations
     fast: {
       maxAttempts: 2,
       baseDelay: 100,
       maxDelay: 1000,
       backoffMultiplier: 2,
       jitterEnabled: true,
       jitterFactor: 0.1,
       retryableErrors: ['NETWORK_ERROR', 'TIMEOUT_ERROR'],
       nonRetryableErrors: ['AUTHENTICATION_FAILED', 'VALIDATION_ERROR', 'RATE_LIMITED'],
     } as RetryConfig,
   };
   ```

### Subtask 4.3: Add Error Mapping Between Providers

**Objective**: Create a mapping system that translates provider-specific errors to standardized provider errors.

**Implementation Details**:

1. **File Location**: `packages/providers/src/errors/ErrorMapper.ts`

2. **Error Mapping Interface**:

   ```typescript
   export interface ErrorMapping {
     providerType: string;
     mappings: ProviderErrorMapping[];
     defaultMapping?: DefaultErrorMapping;
   }

   export interface ProviderErrorMapping {
     // Match criteria
     match: ErrorMatchCriteria;

     // Error to create
     error: ErrorCreationSpec;

     // Additional context
     context?: Record<string, unknown>;
   }

   export interface ErrorMatchCriteria {
     // HTTP status code
     statusCode?: number | number[];

     // Error code from provider
     errorCode?: string | string[];

     // Error message pattern
     messagePattern?: string | RegExp;

     // Error type
     errorType?: string;

     // Custom matcher function
     customMatcher?: (error: any) => boolean;
   }

   export interface ErrorCreationSpec {
     code: string;
     type: ProviderErrorType;
     severity: ErrorSeverity;
     retryable: boolean;
     messageTemplate?: string;
     contextTemplate?: Record<string, (value: any) => any>;
   }

   export interface DefaultErrorMapping {
     code: string;
     type: ProviderErrorType;
     severity: ErrorSeverity;
     retryable: boolean;
   }
   ```

3. **Error Mapper Implementation**:

   ```typescript
   export class ErrorMapper {
     private mappings: Map<string, ErrorMapping> = new Map();
     private logger: Logger;

     constructor(logger: Logger) {
       this.logger = logger;
       this.initializeDefaultMappings();
     }

     registerMapping(mapping: ErrorMapping): void {
       this.mappings.set(mapping.providerType, mapping);
       this.logger.debug('Registered error mapping', { providerType: mapping.providerType });
     }

     mapError(providerType: string, originalError: any): ProviderError {
       const mapping = this.mappings.get(providerType);

       if (!mapping) {
         this.logger.warn('No error mapping found for provider', { providerType });
         return ProviderError.fromError(originalError, { providerType });
       }

       // Try to find a matching mapping
       for (const errorMapping of mapping.mappings) {
         if (this.matchesCriteria(errorMapping.match, originalError)) {
           return this.createProviderError(errorMapping, originalError, providerType);
         }
       }

       // Use default mapping if available
       if (mapping.defaultMapping) {
         return this.createDefaultProviderError(
           mapping.defaultMapping,
           originalError,
           providerType
         );
       }

       // Fallback to generic error
       this.logger.warn('No specific error mapping found, using generic', {
         providerType,
         originalError: this.sanitizeError(originalError),
       });

       return ProviderError.fromError(originalError, { providerType });
     }

     private matchesCriteria(criteria: ErrorMatchCriteria, error: any): boolean {
       // Check status code
       if (criteria.statusCode !== undefined) {
         const statusCodes = Array.isArray(criteria.statusCode)
           ? criteria.statusCode
           : [criteria.statusCode];
         if (!statusCodes.includes(error.status || error.statusCode)) {
           return false;
         }
       }

       // Check error code
       if (criteria.errorCode !== undefined) {
         const errorCodes = Array.isArray(criteria.errorCode)
           ? criteria.errorCode
           : [criteria.errorCode];
         if (!errorCodes.includes(error.code || error.error_code)) {
           return false;
         }
       }

       // Check message pattern
       if (criteria.messagePattern !== undefined) {
         const message = error.message || error.error || '';
         const pattern =
           criteria.messagePattern instanceof RegExp
             ? criteria.messagePattern
             : new RegExp(criteria.messagePattern);

         if (!pattern.test(message)) {
           return false;
         }
       }

       // Check error type
       if (criteria.errorType !== undefined && error.type !== criteria.errorType) {
         return false;
       }

       // Check custom matcher
       if (criteria.customMatcher && !criteria.customMatcher(error)) {
         return false;
       }

       return true;
     }

     private createProviderError(
       mapping: ProviderErrorMapping,
       originalError: any,
       providerType: string
     ): ProviderError {
       const message = this.interpolateMessage(mapping.error.messageTemplate, originalError);
       const context = this.interpolateContext(mapping.contextTemplate, originalError);

       const error = new ProviderError(mapping.error.code, message, {
         type: mapping.error.type,
         severity: mapping.error.severity,
         retryable: mapping.error.retryable,
         providerName: providerType,
         context: {
           originalError: this.sanitizeError(originalError),
           ...context,
           ...mapping.context,
         },
         cause: originalError instanceof Error ? originalError : undefined,
       });

       return error;
     }

     private createDefaultProviderError(
       defaultMapping: DefaultErrorMapping,
       originalError: any,
       providerType: string
     ): ProviderError {
       return new ProviderError(
         defaultMapping.code,
         originalError.message || 'Unknown provider error',
         {
           type: defaultMapping.type,
           severity: defaultMapping.severity,
           retryable: defaultMapping.retryable,
           providerName: providerType,
           context: { originalError: this.sanitizeError(originalError) },
           cause: originalError instanceof Error ? originalError : undefined,
         }
       );
     }

     private interpolateMessage(template: string | undefined, error: any): string {
       if (!template) {
         return error.message || 'Unknown error';
       }

       return template.replace(/\{\{(\w+)\}\}/g, (match, key) => {
         return error[key] || match;
       });
     }

     private interpolateContext(
       template: Record<string, (value: any) => any> | undefined,
       error: any
     ): Record<string, any> {
       if (!template) {
         return {};
       }

       const result: Record<string, any> = {};
       for (const [key, transformer] of Object.entries(template)) {
         result[key] = transformer(error[key]);
       }
       return result;
     }

     private sanitizeError(error: any): any {
       // Remove sensitive information from error objects
       const sanitized = { ...error };

       // Remove common sensitive fields
       const sensitiveFields = ['apiKey', 'api_key', 'token', 'password', 'secret'];
       for (const field of sensitiveFields) {
         delete sanitized[field];
       }

       return sanitized;
     }

     private initializeDefaultMappings(): void {
       // Anthropic Claude mappings
       this.registerMapping({
         providerType: 'anthropic',
         mappings: [
           {
             match: { statusCode: 401 },
             error: {
               code: 'AUTHENTICATION_FAILED',
               type: ProviderErrorType.AUTHENTICATION_FAILED,
               severity: ErrorSeverity.HIGH,
               retryable: false,
               messageTemplate: 'Anthropic API authentication failed: {{message}}',
             },
           },
           {
             match: { statusCode: 429 },
             error: {
               code: 'RATE_LIMITED',
               type: ProviderErrorType.RATE_LIMITED,
               severity: ErrorSeverity.MEDIUM,
               retryable: true,
               messageTemplate: 'Anthropic API rate limit exceeded',
             },
           },
           {
             match: { errorCode: 'invalid_request_error' },
             error: {
               code: 'INVALID_REQUEST',
               type: ProviderErrorType.INVALID_REQUEST,
               severity: ErrorSeverity.MEDIUM,
               retryable: false,
               messageTemplate: 'Invalid request to Anthropic API: {{message}}',
             },
           },
         ],
         defaultMapping: {
           code: 'ANTHROPIC_ERROR',
           type: ProviderErrorType.UNKNOWN,
           severity: ErrorSeverity.MEDIUM,
           retryable: false,
         },
       });

       // OpenAI mappings
       this.registerMapping({
         providerType: 'openai',
         mappings: [
           {
             match: { statusCode: 401 },
             error: {
               code: 'AUTHENTICATION_FAILED',
               type: ProviderErrorType.AUTHENTICATION_FAILED,
               severity: ErrorSeverity.HIGH,
               retryable: false,
               messageTemplate: 'OpenAI API authentication failed: {{message}}',
             },
           },
           {
             match: { statusCode: 429 },
             error: {
               code: 'RATE_LIMITED',
               type: ProviderErrorType.RATE_LIMITED,
               severity: ErrorSeverity.MEDIUM,
               retryable: true,
               messageTemplate: 'OpenAI API rate limit exceeded',
             },
           },
           {
             match: { errorCode: 'content_filter' },
             error: {
               code: 'CONTENT_FILTERED',
               type: ProviderErrorType.CONTENT_FILTERED,
               severity: ErrorSeverity.MEDIUM,
               retryable: false,
               messageTemplate: 'Content filtered by OpenAI safety filters',
             },
           },
         ],
         defaultMapping: {
           code: 'OPENAI_ERROR',
           type: ProviderErrorType.UNKNOWN,
           severity: ErrorSeverity.MEDIUM,
           retryable: false,
         },
       });
     }
   }
   ```

## Technical Requirements

### Error Handling Requirements

- All errors must be properly typed and categorized
- Error messages must be informative but not expose sensitive data
- Stack traces must be preserved for debugging
- Errors must be serializable for logging and monitoring

### Retry Logic Requirements

- Exponential backoff with jitter to prevent thundering herd
- Configurable retry policies per operation type
- Circuit breaker integration to prevent cascade failures
- Comprehensive retry metrics and monitoring

### Error Mapping Requirements

- Provider-specific error codes must be mapped to standard types
- Context preservation for debugging and monitoring
- Automatic detection of retryable vs non-retryable errors
- Extensible mapping system for new providers

## Testing Strategy

### Unit Tests

```typescript
describe('ProviderError', () => {
  describe('creation', () => {
    it('should create error with all properties');
    it('should serialize to JSON correctly');
    it('should handle nested errors');
    it('should sanitize sensitive information');
  });

  describe('factory methods', () => {
    it('should create from Error objects');
    it('should create from unknown types');
    it('should preserve existing ProviderError instances');
  });
});

describe('RetryManager', () => {
  describe('retry logic', () => {
    it('should retry retryable errors');
    it('should not retry non-retryable errors');
    it('should respect max attempts');
    it('should calculate delays correctly');
    it('should apply jitter');
  });

  describe('exponential backoff', () => {
    it('should increase delay exponentially');
    it('should respect max delay');
    it('should handle jitter correctly');
  });
});

describe('ErrorMapper', () => {
  describe('mapping', () => {
    it('should map known errors correctly');
    it('should use default mapping for unknown errors');
    it('should handle complex matching criteria');
    it('should interpolate message templates');
  });
});
```

### Integration Tests

- Test error handling with real provider APIs
- Test retry logic under various failure scenarios
- Test error mapping with actual provider error responses
- Test circuit breaker integration

### Performance Tests

- Measure error handling overhead
- Test retry logic performance under high load
- Benchmark error mapping performance
- Test memory usage with many error instances

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared utilities and logging
- `@tamma/observability` - Error monitoring and metrics

### External Dependencies

- TypeScript 5.7+ - Type safety
- Node.js built-ins - Error handling primitives

## Deliverables

1. **Error Classes**: `packages/providers/src/errors/ProviderError.ts`
2. **Retry Manager**: `packages/providers/src/retry/RetryManager.ts`
3. **Error Mapper**: `packages/providers/src/errors/ErrorMapper.ts`
4. **Error Types**: `packages/providers/src/errors/types.ts`
5. **Retry Configs**: `packages/providers/src/retry/configs.ts`
6. **Unit Tests**: `packages/providers/src/errors/__tests__/`
7. **Integration Tests**: `packages/providers/src/errors/__integration__/`

## Acceptance Criteria Verification

- [ ] Provider-specific error classes cover all error types
- [ ] Retry logic implements exponential backoff with jitter
- [ ] Error mapping translates provider errors correctly
- [ ] All errors are properly categorized and typed
- [ ] Sensitive information is not exposed in errors
- [ ] Retry policies are configurable and effective
- [ ] Error handling does not impact performance significantly
- [ ] Comprehensive test coverage is achieved

## Implementation Notes

### Error Context Enrichment

Errors should be enriched with contextual information:

```typescript
interface ErrorContext {
  operation: string;
  providerName: string;
  requestId: string;
  userId?: string;
  timestamp: string;
  duration?: number;
  retryCount?: number;
  metadata?: Record<string, unknown>;
}
```

### Monitoring Integration

Errors should integrate with monitoring systems:

```typescript
interface ErrorMetrics {
  errorCount: number;
  errorRate: number;
  retryCount: number;
  successRate: number;
  averageRetries: number;
  errorByType: Record<string, number>;
  errorByProvider: Record<string, number>;
}
```

### Circuit Breaker Integration

Retry logic should integrate with circuit breakers:

```typescript
interface CircuitBreakerState {
  state: 'closed' | 'open' | 'half-open';
  failureCount: number;
  lastFailureTime: number;
  nextAttemptTime: number;
}
```

## Next Steps

After completing this task:

1. Move to Task 5: Add Capability Detection
2. Integrate error handling with provider implementations
3. Add monitoring and alerting for errors

## Risk Mitigation

### Technical Risks

- **Error Swallowing**: Ensure errors are properly propagated
- **Retry Storms**: Use jitter and circuit breakers to prevent
- **Memory Leaks**: Proper cleanup of error objects and retry state

### Mitigation Strategies

- Comprehensive error logging and monitoring
- Circuit breaker patterns for fault tolerance
- Memory profiling and leak detection
- Rate limiting for retry attempts
