# Task 6: Retry Logic with Exponential Backoff

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 2-3 days

## 🎯 Objective

Implement retry mechanism with exponential backoff (2s → 4s → 8s) for build failures, with intelligent retry decisions and escalation after 3 failed attempts.

## ✅ Acceptance Criteria

- [ ] System implements maximum 3 retry attempts for build failures
- [ ] Exponential backoff: 2s → 4s → 8s delays between retries
- [ ] Intelligent retry decisions based on failure type
- [ ] Skip retries for non-retryable errors (missing config, invalid credentials)
- [ ] Track retry count and attempt history
- [ ] Escalate to human after max retries exhausted
- [ ] Log all retry attempts with detailed context
- [ ] Support for custom retry strategies per project

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IRetryManager {
  shouldRetry(failure: BuildFailure, attemptCount: number): Promise<RetryDecision>;
  calculateDelay(attemptCount: number, strategy: RetryStrategy): number;
  executeWithRetry<T>(operation: () => Promise<T>, context: RetryContext): Promise<RetryResult<T>>;
  getRetryHistory(buildId: string): Promise<RetryAttempt[]>;
}

interface RetryDecision {
  shouldRetry: boolean;
  reason: string;
  delay: number;
  maxAttempts: number;
  strategy: RetryStrategy;
  nextAttemptTime: Date;
}

interface RetryContext {
  buildId: string;
  operation: string;
  maxAttempts: number;
  strategy: RetryStrategy;
  baseDelay: number;
  maxDelay: number;
  retryableErrors: string[];
  nonRetryableErrors: string[];
  onRetry?: (attempt: number, error: Error) => void;
  onFailure?: (finalError: Error) => void;
}

interface RetryResult<T> {
  success: boolean;
  result?: T;
  finalError?: Error;
  attempts: number;
  totalDuration: number;
  retryHistory: RetryAttempt[];
}

interface RetryAttempt {
  attemptNumber: number;
  timestamp: Date;
  error: Error;
  delay: number;
  duration: number;
  context: any;
}

enum RetryStrategy {
  EXPONENTIAL_BACKOFF = 'exponential_backoff',
  LINEAR_BACKOFF = 'linear_backoff',
  FIXED_DELAY = 'fixed_delay',
  IMMEDIATE = 'immediate',
}

enum RetryableErrorType {
  NETWORK_TIMEOUT = 'network_timeout',
  TEMPORARY_FAILURE = 'temporary_failure',
  RATE_LIMITED = 'rate_limited',
  RESOURCE_EXHAUSTED = 'resource_exhausted',
  DEPENDENCY_UNAVAILABLE = 'dependency_unavailable',
  BUILD_TIMEOUT = 'build_timeout',
  COMPILATION_ERROR = 'compilation_error',
  TEST_FAILURE = 'test_failure',
}

enum NonRetryableErrorType {
  AUTHENTICATION_FAILED = 'authentication_failed',
  CONFIGURATION_ERROR = 'configuration_error',
  MISSING_DEPENDENCY = 'missing_dependency',
  INVALID_CREDENTIALS = 'invalid_credentials',
  PERMISSION_DENIED = 'permission_denied',
  BUILD_NOT_FOUND = 'build_not_found',
  SYNTAX_ERROR = 'syntax_error',
}
```

### Retry Manager Implementation

```typescript
class RetryManager implements IRetryManager {
  private retryHistory: Map<string, RetryAttempt[]> = new Map();
  private retryStrategies: Map<RetryStrategy, IRetryStrategy> = new Map();

  constructor(
    private eventStore: EventStore,
    private logger: Logger,
    private config: RetryConfig
  ) {
    this.initializeStrategies();
  }

  async shouldRetry(failure: BuildFailure, attemptCount: number): Promise<RetryDecision> {
    // Check if we've exceeded max attempts
    if (attemptCount >= this.config.maxAttempts) {
      return {
        shouldRetry: false,
        reason: `Maximum retry attempts (${this.config.maxAttempts}) exceeded`,
        delay: 0,
        maxAttempts: this.config.maxAttempts,
        strategy: this.config.defaultStrategy,
        nextAttemptTime: new Date(),
      };
    }

    // Analyze error type
    const errorAnalysis = await this.analyzeError(failure.error);

    // Check if error is retryable
    if (!errorAnalysis.isRetryable) {
      return {
        shouldRetry: false,
        reason: `Non-retryable error: ${errorAnalysis.type}`,
        delay: 0,
        maxAttempts: this.config.maxAttempts,
        strategy: this.config.defaultStrategy,
        nextAttemptTime: new Date(),
      };
    }

    // Determine retry strategy based on error type
    const strategy = this.selectRetryStrategy(errorAnalysis.type);

    // Calculate delay
    const delay = this.calculateDelay(attemptCount, strategy);

    return {
      shouldRetry: true,
      reason: `Retryable error: ${errorAnalysis.type}`,
      delay,
      maxAttempts: this.config.maxAttempts,
      strategy,
      nextAttemptTime: new Date(Date.now() + delay),
    };
  }

  calculateDelay(attemptCount: number, strategy: RetryStrategy): number {
    const retryStrategy = this.retryStrategies.get(strategy);
    if (!retryStrategy) {
      throw new Error(`Unknown retry strategy: ${strategy}`);
    }

    return retryStrategy.calculateDelay(attemptCount, this.config);
  }

  async executeWithRetry<T>(
    operation: () => Promise<T>,
    context: RetryContext
  ): Promise<RetryResult<T>> {
    const startTime = Date.now();
    const attempts: RetryAttempt[] = [];
    let lastError: Error;

    for (let attempt = 1; attempt <= context.maxAttempts; attempt++) {
      const attemptStartTime = Date.now();

      try {
        // Calculate delay for this attempt (except first attempt)
        if (attempt > 1) {
          const delay = this.calculateDelay(attempt - 1, context.strategy);
          await this.delay(delay);
        }

        // Execute the operation
        const result = await operation();

        // Success - emit success event
        await this.eventStore.append({
          type: 'RETRY.OPERATION_SUCCEEDED',
          tags: {
            buildId: context.buildId,
            operation: context.operation,
            attemptCount: attempt.toString(),
            totalDuration: (Date.now() - startTime).toString(),
          },
          data: {
            attempt,
            totalAttempts: attempt,
            duration: Date.now() - attemptStartTime,
            success: true,
          },
        });

        return {
          success: true,
          result,
          attempts: attempt,
          totalDuration: Date.now() - startTime,
          retryHistory: attempts,
        };
      } catch (error) {
        lastError = error as Error;
        const attemptDuration = Date.now() - attemptStartTime;

        // Record attempt
        const retryAttempt: RetryAttempt = {
          attemptNumber: attempt,
          timestamp: new Date(),
          error: lastError,
          delay: attempt > 1 ? this.calculateDelay(attempt - 1, context.strategy) : 0,
          duration: attemptDuration,
          context: { operation: context.operation },
        };
        attempts.push(retryAttempt);

        // Emit retry attempt event
        await this.eventStore.append({
          type: 'RETRY.ATTEMPT_FAILED',
          tags: {
            buildId: context.buildId,
            operation: context.operation,
            attemptCount: attempt.toString(),
            errorType: this.categorizeError(lastError),
          },
          data: {
            attempt,
            error: lastError.message,
            duration: attemptDuration,
            willRetry: attempt < context.maxAttempts,
          },
        });

        // Call retry callback if provided
        if (context.onRetry) {
          context.onRetry(attempt, lastError);
        }

        // Check if we should retry
        const retryDecision = await this.shouldRetry(
          { error: lastError, buildId: context.buildId },
          attempt
        );

        if (!retryDecision.shouldRetry) {
          break;
        }
      }
    }

    // All attempts failed
    await this.eventStore.append({
      type: 'RETRY.ALL_ATTEMPTS_FAILED',
      tags: {
        buildId: context.buildId,
        operation: context.operation,
        totalAttempts: context.maxAttempts.toString(),
        finalErrorType: this.categorizeError(lastError),
      },
      data: {
        totalAttempts: attempts.length,
        totalDuration: Date.now() - startTime,
        finalError: lastError.message,
      },
    });

    // Call failure callback if provided
    if (context.onFailure) {
      context.onFailure(lastError);
    }

    return {
      success: false,
      finalError: lastError,
      attempts: attempts.length,
      totalDuration: Date.now() - startTime,
      retryHistory: attempts,
    };
  }

  private async analyzeError(error: Error): Promise<ErrorAnalysis> {
    const errorMessage = error.message.toLowerCase();
    const errorStack = error.stack?.toLowerCase() || '';

    // Check for non-retryable errors first
    const nonRetryablePatterns = [
      {
        type: NonRetryableErrorType.AUTHENTICATION_FAILED,
        patterns: [/unauthorized/i, /authentication failed/i, /401/i],
      },
      {
        type: NonRetryableErrorType.CONFIGURATION_ERROR,
        patterns: [/config.*error/i, /invalid configuration/i],
      },
      {
        type: NonRetryableErrorType.MISSING_DEPENDENCY,
        patterns: [/module not found/i, /cannot find module/i],
      },
      {
        type: NonRetryableErrorType.INVALID_CREDENTIALS,
        patterns: [/invalid.*token/i, /credentials.*invalid/i],
      },
      {
        type: NonRetryableErrorType.PERMISSION_DENIED,
        patterns: [/permission denied/i, /access denied/i, /403/i],
      },
      { type: NonRetryableErrorType.SYNTAX_ERROR, patterns: [/syntax error/i, /parse error/i] },
    ];

    for (const { type, patterns } of nonRetryablePatterns) {
      if (this.matchesAnyPattern(errorMessage + ' ' + errorStack, patterns)) {
        return {
          isRetryable: false,
          type,
          category: 'non-retryable',
        };
      }
    }

    // Check for retryable errors
    const retryablePatterns = [
      {
        type: RetryableErrorType.NETWORK_TIMEOUT,
        patterns: [/timeout/i, /etimedout/i, /connection.*timeout/i],
      },
      {
        type: RetryableErrorType.TEMPORARY_FAILURE,
        patterns: [/temporary.*failure/i, /transient.*error/i],
      },
      {
        type: RetryableErrorType.RATE_LIMITED,
        patterns: [/rate.*limit/i, /too.*many.*requests/i, /429/i],
      },
      {
        type: RetryableErrorType.RESOURCE_EXHAUSTED,
        patterns: [/out of memory/i, /disk.*full/i, /resource.*exhausted/i],
      },
      {
        type: RetryableErrorType.DEPENDENCY_UNAVAILABLE,
        patterns: [/service.*unavailable/i, /connection.*refused/i],
      },
      {
        type: RetryableErrorType.BUILD_TIMEOUT,
        patterns: [/build.*timeout/i, /build.*time.*out/i],
      },
      {
        type: RetryableErrorType.COMPILATION_ERROR,
        patterns: [/compilation.*failed/i, /build.*failed/i],
      },
      { type: RetryableErrorType.TEST_FAILURE, patterns: [/test.*failed/i, /assertion.*failed/i] },
    ];

    for (const { type, patterns } of retryablePatterns) {
      if (this.matchesAnyPattern(errorMessage + ' ' + errorStack, patterns)) {
        return {
          isRetryable: true,
          type,
          category: 'retryable',
        };
      }
    }

    // Unknown error - assume retryable with caution
    return {
      isRetryable: true,
      type: RetryableErrorType.TEMPORARY_FAILURE,
      category: 'unknown',
    };
  }

  private selectRetryStrategy(errorType: string): RetryStrategy {
    const strategyMap: Record<string, RetryStrategy> = {
      [RetryableErrorType.NETWORK_TIMEOUT]: RetryStrategy.EXPONENTIAL_BACKOFF,
      [RetryableErrorType.TEMPORARY_FAILURE]: RetryStrategy.EXPONENTIAL_BACKOFF,
      [RetryableErrorType.RATE_LIMITED]: RetryStrategy.EXPONENTIAL_BACKOFF,
      [RetryableErrorType.RESOURCE_EXHAUSTED]: RetryStrategy.LINEAR_BACKOFF,
      [RetryableErrorType.DEPENDENCY_UNAVAILABLE]: RetryStrategy.EXPONENTIAL_BACKOFF,
      [RetryableErrorType.BUILD_TIMEOUT]: RetryStrategy.LINEAR_BACKOFF,
      [RetryableErrorType.COMPILATION_ERROR]: RetryStrategy.FIXED_DELAY,
      [RetryableErrorType.TEST_FAILURE]: RetryStrategy.FIXED_DELAY,
    };

    return strategyMap[errorType] || this.config.defaultStrategy;
  }

  private initializeStrategies(): void {
    this.retryStrategies.set(RetryStrategy.EXPONENTIAL_BACKOFF, new ExponentialBackoffStrategy());
    this.retryStrategies.set(RetryStrategy.LINEAR_BACKOFF, new LinearBackoffStrategy());
    this.retryStrategies.set(RetryStrategy.FIXED_DELAY, new FixedDelayStrategy());
    this.retryStrategies.set(RetryStrategy.IMMEDIATE, new ImmediateStrategy());
  }

  private matchesAnyPattern(text: string, patterns: RegExp[]): boolean {
    return patterns.some((pattern) => pattern.test(text));
  }

  private categorizeError(error: Error): string {
    // Simple categorization for logging
    const message = error.message.toLowerCase();

    if (message.includes('timeout')) return 'timeout';
    if (message.includes('network') || message.includes('connection')) return 'network';
    if (message.includes('permission') || message.includes('access')) return 'permission';
    if (message.includes('auth')) return 'authentication';
    if (message.includes('config')) return 'configuration';
    if (message.includes('build')) return 'build';
    if (message.includes('test')) return 'test';

    return 'unknown';
  }

  private delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
```

### Retry Strategy Implementations

```typescript
interface IRetryStrategy {
  calculateDelay(attemptCount: number, config: RetryConfig): number;
}

class ExponentialBackoffStrategy implements IRetryStrategy {
  calculateDelay(attemptCount: number, config: RetryConfig): number {
    const delay = config.baseDelay * Math.pow(2, attemptCount - 1);
    return Math.min(delay, config.maxDelay);
  }
}

class LinearBackoffStrategy implements IRetryStrategy {
  calculateDelay(attemptCount: number, config: RetryConfig): number {
    const delay = config.baseDelay * attemptCount;
    return Math.min(delay, config.maxDelay);
  }
}

class FixedDelayStrategy implements IRetryStrategy {
  calculateDelay(attemptCount: number, config: RetryConfig): number {
    return config.baseDelay;
  }
}

class ImmediateStrategy implements IRetryStrategy {
  calculateDelay(attemptCount: number, config: RetryConfig): number {
    return 0;
  }
}
```

### Build Retry Orchestrator

```typescript
class BuildRetryOrchestrator {
  constructor(
    private retryManager: RetryManager,
    private fixApplicator: IFixApplicator,
    private buildTrigger: IBuildTrigger,
    private eventStore: EventStore,
    private logger: Logger
  ) {}

  async executeBuildWithRetry(
    buildRequest: BuildRequest,
    failureAnalysis?: BuildFailureAnalysis
  ): Promise<BuildRetryResult> {
    let retryCount = 0;
    let lastBuildResult: BuildResult;
    let appliedFixes: FixSuggestion[] = [];

    const retryContext: RetryContext = {
      buildId: buildRequest.buildId,
      operation: 'build_execution',
      maxAttempts: 3,
      strategy: RetryStrategy.EXPONENTIAL_BACKOFF,
      baseDelay: 2000, // 2 seconds
      maxDelay: 8000, // 8 seconds
      retryableErrors: ['BUILD_TIMEOUT', 'NETWORK_ERROR', 'TEMPORARY_FAILURE'],
      nonRetryableErrors: ['AUTHENTICATION_FAILED', 'CONFIGURATION_ERROR', 'INVALID_CREDENTIALS'],
      onRetry: async (attempt, error) => {
        await this.handleRetryAttempt(attempt, error, buildRequest, failureAnalysis);
      },
      onFailure: async (finalError) => {
        await this.handleFinalFailure(finalError, buildRequest, appliedFixes);
      },
    };

    const retryResult = await this.retryManager.executeWithRetry(async () => {
      retryCount++;

      // Trigger build
      const triggerResult = await this.buildTrigger.triggerBuild(buildRequest);
      if (!triggerResult.success) {
        throw new Error(`Build trigger failed: ${triggerResult.error}`);
      }

      // Wait for build completion
      const buildResult = await this.waitForBuildCompletion(triggerResult.buildId);
      lastBuildResult = buildResult;

      if (buildResult.status !== BuildStatus.SUCCESS) {
        throw new Error(`Build failed: ${buildResult.failures[0]?.message || 'Unknown error'}`);
      }

      return buildResult;
    }, retryContext);

    return {
      success: retryResult.success,
      buildResult: retryResult.success ? retryResult.result : lastBuildResult,
      retryCount: retryResult.attempts,
      totalDuration: retryResult.totalDuration,
      appliedFixes,
      retryHistory: retryResult.retryHistory,
      escalated: !retryResult.success,
    };
  }

  private async handleRetryAttempt(
    attempt: number,
    error: Error,
    buildRequest: BuildRequest,
    failureAnalysis?: BuildFailureAnalysis
  ): Promise<void> {
    this.logger.info('Build retry attempt', {
      buildId: buildRequest.buildId,
      attempt,
      error: error.message,
    });

    // Generate fix suggestion if we have failure analysis
    if (failureAnalysis && attempt <= 2) {
      // Only try fixes for first 2 attempts
      try {
        const fixSuggestion = await this.generateFixSuggestion(failureAnalysis);

        // Apply the fix
        const fixResult = await this.fixApplicator.applyFix(fixSuggestion);

        if (fixResult.success) {
          this.logger.info('Fix applied successfully', {
            buildId: buildRequest.buildId,
            fixId: fixSuggestion.id,
            changesCount: fixResult.appliedChanges.length,
          });
        } else {
          this.logger.warn('Fix application failed', {
            buildId: buildRequest.buildId,
            fixId: fixSuggestion.id,
            errors: fixResult.errors,
          });
        }
      } catch (fixError) {
        this.logger.error('Failed to generate or apply fix', {
          buildId: buildRequest.buildId,
          error: fixError.message,
        });
      }
    }

    // Emit retry event
    await this.eventStore.append({
      type: 'BUILD.RETRY_ATTEMPTED',
      tags: {
        buildId: buildRequest.buildId,
        attemptCount: attempt.toString(),
        errorType: this.categorizeError(error),
      },
      data: {
        attempt,
        error: error.message,
        timestamp: new Date().toISOString(),
      },
    });
  }

  private async handleFinalFailure(
    finalError: Error,
    buildRequest: BuildRequest,
    appliedFixes: FixSuggestion[]
  ): Promise<void> {
    this.logger.error('All build retry attempts failed', {
      buildId: buildRequest.buildId,
      finalError: finalError.message,
      appliedFixesCount: appliedFixes.length,
    });

    // Escalate to human
    await this.escalateToHuman(buildRequest, finalError, appliedFixes);

    // Emit escalation event
    await this.eventStore.append({
      type: 'BUILD.ESCALATED',
      tags: {
        buildId: buildRequest.buildId,
        escalationReason: 'max_retries_exceeded',
        totalRetries: '3',
      },
      data: {
        finalError: finalError.message,
        appliedFixes: appliedFixes.map((f) => ({
          id: f.id,
          explanation: f.explanation,
          confidence: f.confidence,
        })),
        escalatedAt: new Date().toISOString(),
      },
    });
  }

  private async escalateToHuman(
    buildRequest: BuildRequest,
    finalError: Error,
    appliedFixes: FixSuggestion[]
  ): Promise<void> {
    const escalationContext = {
      type: 'build_failure',
      buildId: buildRequest.buildId,
      repositoryUrl: buildRequest.repositoryUrl,
      branch: buildRequest.branch,
      commitHash: buildRequest.commitHash,
      finalError: finalError.message,
      retryCount: 3,
      appliedFixes: appliedFixes.map((f) => ({
        id: f.id,
        explanation: f.explanation,
        confidence: f.confidence,
        changesCount: f.changes.length,
        aiProvider: f.aiProvider,
      })),
      timestamp: new Date().toISOString(),
    };

    // Create escalation notification
    await this.createEscalationNotification(escalationContext);
  }

  private async waitForBuildCompletion(buildId: string): Promise<BuildResult> {
    // This would integrate with the build status polling system
    // For now, return a mock result
    return {
      buildId,
      status: BuildStatus.FAILED,
      startTime: new Date(),
      endTime: new Date(),
      duration: 30000,
      failures: [
        {
          type: 'compilation_error',
          message: 'Mock build failure',
          severity: 'high',
        },
      ],
      logs: [],
      metrics: {},
      retryCount: 0,
      escalated: false,
    };
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('RetryManager', () => {
  let retryManager: RetryManager;
  let mockEventStore: jest.Mocked<EventStore>;

  beforeEach(() => {
    mockEventStore = createMockEventStore();
    retryManager = new RetryManager(mockEventStore, mockLogger, {
      maxAttempts: 3,
      baseDelay: 2000,
      maxDelay: 8000,
      defaultStrategy: RetryStrategy.EXPONENTIAL_BACKOFF,
    });
  });

  it('should calculate exponential backoff delays correctly', () => {
    const delays = [
      retryManager.calculateDelay(1, RetryStrategy.EXPONENTIAL_BACKOFF),
      retryManager.calculateDelay(2, RetryStrategy.EXPONENTIAL_BACKOFF),
      retryManager.calculateDelay(3, RetryStrategy.EXPONENTIAL_BACKOFF),
    ];

    expect(delays).toEqual([2000, 4000, 8000]); // 2s, 4s, 8s
  });

  it('should not retry non-retryable errors', async () => {
    const failure = {
      error: new Error('Authentication failed'),
      buildId: 'test-build',
    };

    const decision = await retryManager.shouldRetry(failure, 1);

    expect(decision.shouldRetry).toBe(false);
    expect(decision.reason).toContain('Non-retryable error');
  });

  it('should retry retryable errors with exponential backoff', async () => {
    const failure = {
      error: new Error('Network timeout'),
      buildId: 'test-build',
    };

    const decision = await retryManager.shouldRetry(failure, 1);

    expect(decision.shouldRetry).toBe(true);
    expect(decision.strategy).toBe(RetryStrategy.EXPONENTIAL_BACKOFF);
    expect(decision.delay).toBe(2000);
  });

  it('should execute operation with retry logic', async () => {
    let attemptCount = 0;
    const operation = jest.fn().mockImplementation(() => {
      attemptCount++;
      if (attemptCount < 3) {
        throw new Error('Temporary failure');
      }
      return 'success';
    });

    const context: RetryContext = {
      buildId: 'test-build',
      operation: 'test-operation',
      maxAttempts: 3,
      strategy: RetryStrategy.EXPONENTIAL_BACKOFF,
      baseDelay: 100, // Short delay for testing
      maxDelay: 1000,
    };

    const result = await retryManager.executeWithRetry(operation, context);

    expect(result.success).toBe(true);
    expect(result.result).toBe('success');
    expect(result.attempts).toBe(3);
    expect(operation).toHaveBeenCalledTimes(3);
  });
});
```

### Integration Tests

```typescript
describe('BuildRetryOrchestrator Integration', () => {
  it('should execute build with retry and fix application', async () => {
    const orchestrator = new BuildRetryOrchestrator(
      new RetryManager(mockEventStore, mockLogger, retryConfig),
      mockFixApplicator,
      mockBuildTrigger,
      mockEventStore,
      mockLogger
    );

    const buildRequest = createMockBuildRequest();
    const failureAnalysis = createMockFailureAnalysis();

    mockBuildTrigger.triggerBuild
      .mockResolvedValueOnce({ success: false, error: 'Build failed' })
      .mockResolvedValueOnce({ success: false, error: 'Build failed' })
      .mockResolvedValueOnce({ success: true, buildId: 'success-build' });

    mockFixApplicator.applyFix.mockResolvedValue({
      success: true,
      appliedChanges: [],
      failedChanges: [],
      rollbackPoint: null,
      commitHash: 'fix-commit',
    });

    const result = await orchestrator.executeBuildWithRetry(buildRequest, failureAnalysis);

    expect(result.success).toBe(true);
    expect(result.retryCount).toBe(3);
    expect(mockFixApplicator.applyFix).toHaveBeenCalledTimes(2); // Fixes for first 2 attempts
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- Retry success rate
- Average retry attempts per build
- Retry strategy effectiveness
- Time spent in retries
- Escalation rate

### Events to Emit

```typescript
// Retry lifecycle events
RETRY.ATTEMPT_STARTED;
RETRY.ATTEMPT_FAILED;
RETRY.OPERATION_SUCCEEDED;
RETRY.ALL_ATTEMPTS_FAILED;
RETRY.DELAY_CALCULATED;

// Build-specific retry events
BUILD.RETRY_ATTEMPTED;
BUILD.FIX_APPLIED_DURING_RETRY;
BUILD.ESCALATED_AFTER_RETRIES;
```

## 🔧 Configuration

### Environment Variables

```bash
# Retry configuration
MAX_RETRY_ATTEMPTS=3
BASE_RETRY_DELAY=2000
MAX_RETRY_DELAY=8000
DEFAULT_RETRY_STRATEGY=exponential_backoff

# Error classification
ENABLE_SMART_RETRY_CLASSIFICATION=true
CUSTOM_RETRY_PATTERNS_FILE=./config/retry_patterns.json
```

### Configuration File

```yaml
retry:
  max_attempts: 3
  base_delay: 2000 # 2 seconds
  max_delay: 8000 # 8 seconds
  default_strategy: exponential_backoff

strategies:
  exponential_backoff:
    multiplier: 2
    jitter: true

  linear_backoff:
    increment: 2000

  fixed_delay:
    delay: 5000

error_classification:
  retryable_errors:
    - pattern: 'timeout'
      strategy: exponential_backoff
    - pattern: 'network'
      strategy: exponential_backoff
    - pattern: 'rate limit'
      strategy: exponential_backoff

  non_retryable_errors:
    - pattern: 'authentication'
    - pattern: 'permission denied'
    - pattern: 'invalid credentials'
    - pattern: 'syntax error'
```

## 🚨 Error Handling

### Common Error Scenarios

1. **Retry configuration errors**
   - Validate configuration on startup
   - Use sensible defaults for invalid values

2. **Infinite retry loops**
   - Enforce maximum attempt limits
   - Monitor for stuck retry cycles

3. **Resource exhaustion during retries**
   - Implement circuit breakers
   - Monitor system resources

### Recovery Strategies

- Automatic fallback to safer retry strategies
- Circuit breaker pattern for repeated failures
- Resource monitoring and adaptive delays
- Manual override capabilities

## 📝 Implementation Checklist

- [ ] Define core interfaces and enums
- [ ] Implement RetryManager class
- [ ] Create retry strategy implementations
- [ ] Implement error classification logic
- [ ] Create BuildRetryOrchestrator
- [ ] Add comprehensive event logging
- [ ] Write unit tests for all components
- [ ] Write integration tests with real scenarios
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document retry strategies and patterns
- [ ] Add circuit breaker protection

---

**Dependencies**: Task 5 (Fix Application and Commit)  
**Blocked By**: None  
**Blocks**: Task 7 (Escalation Workflow)
