# Epic Technical Specification: Benchmark Execution Engine

**Date:** 2025-11-07  
**Author:** meywd  
**Epic ID:** 4  
**Status:** Updated  
**Project:** AI Benchmarking Test Platform (AIBaaS)

---

## Overview

Epic 4 implements the core benchmark execution engine that orchestrates AI model evaluation across multiple providers and tasks. This epic delivers the task execution framework, automated scoring system, result storage with time-series management, benchmark orchestration with scheduling capabilities, agent customization benchmarking, cross-platform intelligence, and user dashboard for custom benchmarks. The engine supports parallel execution, resource management, error handling, and comprehensive result collection while maintaining consistency and reliability across different AI providers and benchmark scenarios.

This epic directly addresses the core benchmarking requirements from the PRD: multi-provider benchmark execution (FR-9), automated scoring and evaluation (FR-10), result management and storage (FR-11), benchmark orchestration (FR-12), and extends with agent customization benchmarking, cross-platform intelligence, and user custom benchmark capabilities. By implementing a robust, scalable execution engine with support for concurrent benchmarks, resource optimization, and comprehensive result tracking, Epic 4 provides the technical foundation for reliable, reproducible AI model evaluation at scale.

## Objectives and Scope

**In Scope:**

- Story 4.1: Task Execution Engine - Core execution framework with provider abstraction
- Story 4.2: Automated Scoring System - Multi-dimensional scoring with evaluation criteria
- Story 4.3: Result Storage & Time-Series Management - Scalable result storage with historical tracking
- Story 4.4: Benchmark Orchestration & Scheduling - Workflow management and resource optimization
- Story 4.5: Agent Customization Benchmarking Suite - Performance impact measurement for agent configurations
- Story 4.6: Cross-Platform Intelligence Engine - Collective intelligence and best practice discovery
- Story 4.7: User Benchmarking Dashboard - Interactive dashboard for custom benchmarks and comparative analysis

**Out of Scope:**

- Multi-judge evaluation systems (Epic 5)
- Public leaderboards and organization dashboards (Epic 6)
- Advanced analytics and business intelligence (Epic 5 enhancements)
- Real-time infrastructure and event streaming (Epic 9)
- Comprehensive monitoring and observability (Epic 10)

## System Architecture Alignment

Epic 4 implements the benchmark execution layer that coordinates between test bank management, AI providers, and result storage:

### Execution Engine Architecture

- **Task Executor:** Core execution framework with provider integration
- **Scoring Engine:** Automated evaluation with multiple scoring methods
- **Result Manager:** Time-series storage with historical analysis
- **Orchestrator:** Workflow management with scheduling and resource optimization
- **Agent Benchmarking Suite:** Performance impact measurement for agent customizations
- **Cross-Platform Intelligence Engine:** Collective intelligence and best practice discovery
- **User Dashboard:** Interactive interface for custom benchmarks and comparative analysis

### Provider Integration Layer

- **Provider Abstraction:** Unified interface for all AI providers
- **Execution Context:** Context management for benchmark runs
- **Error Handling:** Comprehensive error recovery and retry logic
- **Performance Monitoring:** Real-time execution metrics and optimization

### Result Management System

- **Time-Series Storage:** Efficient storage for large-scale result data
- **Historical Analysis:** Trend analysis and comparison capabilities
- **Data Aggregation:** Statistical analysis and summary generation
- **Export Capabilities:** Multiple export formats and integrations

## Detailed Design

### Services and Modules

#### 1. Task Execution Engine (Story 4.1)

**Core Execution Framework:**

```typescript
interface TaskExecutor {
  executeTask(request: TaskExecutionRequest): Promise<TaskExecutionResult>;
  executeTaskBatch(requests: TaskExecutionRequest[]): Promise<TaskExecutionResult[]>;
  executeBenchmark(benchmark: BenchmarkDefinition): Promise<BenchmarkResult>;
  cancelExecution(executionId: string): Promise<void>;
  getExecutionStatus(executionId: string): Promise<ExecutionStatus>;
}

interface TaskExecutionRequest {
  id: string;
  taskId: string;
  providerId: string;
  modelId: string;
  config: ExecutionConfig;
  context: ExecutionContext;
  priority: ExecutionPriority;
  timeout?: number;
  retryPolicy?: RetryPolicy;
}

interface ExecutionConfig {
  temperature?: number;
  maxTokens?: number;
  topP?: number;
  frequencyPenalty?: number;
  presencePenalty?: number;
  stop?: string | string[];
  stream?: boolean;
  customParameters?: Record<string, any>;
}

interface ExecutionContext {
  benchmarkId: string;
  organizationId: string;
  userId: string;
  sessionId?: string;
  environment: 'development' | 'staging' | 'production';
  metadata: Record<string, any>;
}

enum ExecutionPriority {
  LOW = 'low',
  NORMAL = 'normal',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface RetryPolicy {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  retryableErrors: string[];
}

interface TaskExecutionResult {
  id: string;
  taskId: string;
  providerId: string;
  modelId: string;
  status: ExecutionStatus;
  startTime: Date;
  endTime?: Date;
  duration?: number;
  response?: ModelResponse;
  error?: ExecutionError;
  metrics: ExecutionMetrics;
  context: ExecutionContext;
  metadata: Record<string, any>;
}

enum ExecutionStatus {
  PENDING = 'pending',
  RUNNING = 'running',
  COMPLETED = 'completed',
  FAILED = 'failed',
  CANCELLED = 'cancelled',
  TIMEOUT = 'timeout',
}

interface ModelResponse {
  content: string;
  finishReason: string;
  tokenUsage: TokenUsage;
  metadata: Record<string, any>;
  rawResponse?: any;
}

interface TokenUsage {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
}

interface ExecutionError {
  code: string;
  message: string;
  details?: any;
  stack?: string;
  retryable: boolean;
  providerError?: any;
}

interface ExecutionMetrics {
  requestLatency: number;
  firstTokenTime?: number;
  tokensPerSecond?: number;
  queueTime?: number;
  providerLatency?: number;
  networkLatency?: number;
  retryCount: number;
}
```

**Task Executor Implementation:**

```typescript
class TaskExecutorService implements TaskExecutor {
  constructor(
    private providerRegistry: ProviderRegistry,
    private taskRepository: TaskRepository,
    private resultStorage: ResultStorage,
    private metricsCollector: MetricsCollector,
    private queueManager: QueueManager,
    private rateLimiter: RateLimiter
  ) {}

  async executeTask(request: TaskExecutionRequest): Promise<TaskExecutionResult> {
    const executionId = request.id;
    const startTime = new Date();

    try {
      // Update status to running
      await this.updateExecutionStatus(executionId, ExecutionStatus.RUNNING);

      // Get task and provider
      const task = await this.taskRepository.getTask(request.taskId);
      if (!task) {
        throw new Error(`Task ${request.taskId} not found`);
      }

      const provider = this.providerRegistry.getProvider(request.providerId);
      if (!provider) {
        throw new Error(`Provider ${request.providerId} not found`);
      }

      // Check rate limits
      await this.rateLimiter.checkLimit(request.providerId, request.modelId);

      // Prepare execution context
      const context = this.prepareExecutionContext(task, request);

      // Execute with retry logic
      const result = await this.executeWithRetry(provider, context, request.retryPolicy);

      // Calculate metrics
      const metrics = await this.calculateMetrics(startTime, result);

      // Store result
      const executionResult: TaskExecutionResult = {
        id: executionId,
        taskId: request.taskId,
        providerId: request.providerId,
        modelId: request.modelId,
        status: ExecutionStatus.COMPLETED,
        startTime,
        endTime: new Date(),
        duration: Date.now() - startTime.getTime(),
        response: result.response,
        metrics,
        context: request.context,
        metadata: request.metadata || {},
      };

      await this.resultStorage.storeExecution(executionResult);
      await this.metricsCollector.recordExecution(executionResult);

      return executionResult;
    } catch (error) {
      const executionError = this.handleExecutionError(error);

      const failedResult: TaskExecutionResult = {
        id: executionId,
        taskId: request.taskId,
        providerId: request.providerId,
        modelId: request.modelId,
        status: this.determineFailureStatus(error),
        startTime,
        endTime: new Date(),
        duration: Date.now() - startTime.getTime(),
        error: executionError,
        metrics: { requestLatency: Date.now() - startTime.getTime(), retryCount: 0 },
        context: request.context,
        metadata: request.metadata || {},
      };

      await this.resultStorage.storeExecution(failedResult);
      await this.metricsCollector.recordExecution(failedResult);

      return failedResult;
    }
  }

  async executeTaskBatch(requests: TaskExecutionRequest[]): Promise<TaskExecutionResult[]> {
    // Group requests by provider for optimal execution
    const groupedRequests = this.groupRequestsByProvider(requests);
    const results: TaskExecutionResult[] = [];

    // Execute groups in parallel with concurrency limits
    const groupPromises = Array.from(groupedRequests.entries()).map(
      ([providerId, providerRequests]) => this.executeProviderGroup(providerId, providerRequests)
    );

    const groupResults = await Promise.allSettled(groupPromises);

    // Flatten results
    for (const groupResult of groupResults) {
      if (groupResult.status === 'fulfilled') {
        results.push(...groupResult.value);
      } else {
        console.error('Provider group execution failed:', groupResult.reason);
      }
    }

    return results;
  }

  async executeBenchmark(benchmark: BenchmarkDefinition): Promise<BenchmarkResult> {
    const benchmarkId = benchmark.id;
    const startTime = new Date();

    try {
      // Create execution requests for all task-model combinations
      const executionRequests = this.createExecutionRequests(benchmark);

      // Execute with optimal scheduling
      const executionResults = await this.executeWithScheduling(executionRequests);

      // Calculate benchmark results
      const benchmarkResult = await this.calculateBenchmarkResults(
        benchmarkId,
        executionResults,
        benchmark
      );

      // Store benchmark result
      await this.resultStorage.storeBenchmark(benchmarkResult);

      return benchmarkResult;
    } catch (error) {
      throw new Error(`Benchmark execution failed: ${error.message}`);
    }
  }

  async cancelExecution(executionId: string): Promise<void> {
    await this.updateExecutionStatus(executionId, ExecutionStatus.CANCELLED);
    await this.queueManager.cancel(executionId);
  }

  async getExecutionStatus(executionId: string): Promise<ExecutionStatus> {
    const execution = await this.resultStorage.getExecution(executionId);
    return execution?.status || ExecutionStatus.PENDING;
  }

  private prepareExecutionContext(task: Task, request: TaskExecutionRequest): any {
    const providerRequest = {
      model: request.modelId,
      messages: this.convertTaskToMessages(task),
      temperature: request.config.temperature,
      maxTokens: request.config.maxTokens,
      topP: request.config.topP,
      frequencyPenalty: request.config.frequencyPenalty,
      presencePenalty: request.config.presencePenalty,
      stop: request.config.stop,
      stream: request.config.stream,
      ...request.config.customParameters,
    };

    return providerRequest;
  }

  private convertTaskToMessages(task: Task): any[] {
    const messages: any[] = [];

    // Add system message if present
    if (task.content.context) {
      messages.push({
        role: 'system',
        content: task.content.context,
      });
    }

    // Add main prompt
    messages.push({
      role: 'user',
      content: task.content.prompt,
    });

    // Add examples if present
    if (task.content.examples) {
      for (const example of task.content.examples) {
        messages.push({
          role: 'user',
          content: example.input,
        });
        messages.push({
          role: 'assistant',
          content: example.expectedOutput,
        });
      }
    }

    return messages;
  }

  private async executeWithRetry(
    provider: IAIProvider,
    context: any,
    retryPolicy?: RetryPolicy
  ): Promise<{ response: ModelResponse }> {
    const policy = retryPolicy || this.getDefaultRetryPolicy();
    let lastError: Error;

    for (let attempt = 1; attempt <= policy.maxAttempts; attempt++) {
      try {
        const startTime = Date.now();
        const response = await provider.createChatCompletion(context);
        const latency = Date.now() - startTime;

        return {
          response: {
            content: response.choices[0].message.content,
            finishReason: response.choices[0].finishReason,
            tokenUsage: {
              promptTokens: response.usage.promptTokens,
              completionTokens: response.usage.completionTokens,
              totalTokens: response.usage.totalTokens,
            },
            metadata: response.metadata,
            rawResponse: response,
          },
        };
      } catch (error) {
        lastError = error;

        if (!this.isRetryableError(error, policy) || attempt === policy.maxAttempts) {
          throw error;
        }

        const delay = Math.min(
          policy.baseDelay * Math.pow(policy.backoffMultiplier, attempt - 1),
          policy.maxDelay
        );

        await this.sleep(delay);
      }
    }

    throw lastError!;
  }

  private async executeProviderGroup(
    providerId: string,
    requests: TaskExecutionRequest[]
  ): Promise<TaskExecutionResult[]> {
    const results: TaskExecutionResult[] = [];
    const concurrencyLimit = this.getConcurrencyLimit(providerId);

    // Execute with concurrency control
    for (let i = 0; i < requests.length; i += concurrencyLimit) {
      const batch = requests.slice(i, i + concurrencyLimit);
      const batchPromises = batch.map((request) => this.executeTask(request));
      const batchResults = await Promise.allSettled(batchPromises);

      for (const result of batchResults) {
        if (result.status === 'fulfilled') {
          results.push(result.value);
        } else {
          console.error('Task execution failed:', result.reason);
        }
      }
    }

    return results;
  }

  private createExecutionRequests(benchmark: BenchmarkDefinition): TaskExecutionRequest[] {
    const requests: TaskExecutionRequest[] = [];

    for (const taskConfig of benchmark.taskConfigs) {
      for (const modelConfig of benchmark.modelConfigs) {
        requests.push({
          id: `${benchmark.id}_${taskConfig.taskId}_${modelConfig.modelId}`,
          taskId: taskConfig.taskId,
          providerId: modelConfig.providerId,
          modelId: modelConfig.modelId,
          config: taskConfig.config || {},
          context: {
            benchmarkId: benchmark.id,
            organizationId: benchmark.organizationId,
            userId: benchmark.userId,
            environment: benchmark.environment || 'production',
            metadata: benchmark.metadata || {},
          },
          priority: benchmark.priority || ExecutionPriority.NORMAL,
          timeout: benchmark.timeout,
          retryPolicy: benchmark.retryPolicy,
        });
      }
    }

    return requests;
  }

  private async executeWithScheduling(
    requests: TaskExecutionRequest[]
  ): Promise<TaskExecutionResult[]> {
    // Sort by priority and resource requirements
    const sortedRequests = this.sortRequestsByPriority(requests);

    // Execute with optimal resource allocation
    return await this.executeTaskBatch(sortedRequests);
  }

  private async calculateMetrics(startTime: Date, result: any): Promise<ExecutionMetrics> {
    const endTime = Date.now();
    const requestLatency = endTime - startTime.getTime();

    return {
      requestLatency,
      firstTokenTime: result.response?.metadata?.firstTokenTime,
      tokensPerSecond: this.calculateTokensPerSecond(result.response),
      queueTime: result.response?.metadata?.queueTime,
      providerLatency: result.response?.metadata?.providerLatency,
      networkLatency: result.response?.metadata?.networkLatency,
      retryCount: 0,
    };
  }

  private calculateTokensPerSecond(response: ModelResponse): number {
    if (!response.tokenUsage || !response.metadata?.duration) {
      return 0;
    }

    return response.tokenUsage.totalTokens / (response.metadata.duration / 1000);
  }

  private isRetryableError(error: any, policy: RetryPolicy): boolean {
    if (!error.code) return false;
    return policy.retryableErrors.includes(error.code);
  }

  private determineFailureStatus(error: any): ExecutionStatus {
    if (error.code === 'TIMEOUT') return ExecutionStatus.TIMEOUT;
    if (error.code === 'CANCELLED') return ExecutionStatus.CANCELLED;
    return ExecutionStatus.FAILED;
  }

  private handleExecutionError(error: any): ExecutionError {
    return {
      code: error.code || 'UNKNOWN_ERROR',
      message: error.message || 'Unknown error occurred',
      details: error.details,
      stack: error.stack,
      retryable: error.retryable || false,
      providerError: error.providerError,
    };
  }

  private getDefaultRetryPolicy(): RetryPolicy {
    return {
      maxAttempts: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      retryableErrors: ['RATE_LIMIT', 'TIMEOUT', 'NETWORK_ERROR'],
    };
  }

  private groupRequestsByProvider(
    requests: TaskExecutionRequest[]
  ): Map<string, TaskExecutionRequest[]> {
    const groups = new Map<string, TaskExecutionRequest[]>();

    for (const request of requests) {
      const providerRequests = groups.get(request.providerId) || [];
      providerRequests.push(request);
      groups.set(request.providerId, providerRequests);
    }

    return groups;
  }

  private sortRequestsByPriority(requests: TaskExecutionRequest[]): TaskExecutionRequest[] {
    const priorityOrder = {
      [ExecutionPriority.CRITICAL]: 0,
      [ExecutionPriority.HIGH]: 1,
      [ExecutionPriority.NORMAL]: 2,
      [ExecutionPriority.LOW]: 3,
    };

    return requests.sort((a, b) => priorityOrder[a.priority] - priorityOrder[b.priority]);
  }

  private getConcurrencyLimit(providerId: string): number {
    // Provider-specific concurrency limits
    const limits: Record<string, number> = {
      'anthropic-claude': 10,
      openai: 20,
      'google-gemini': 15,
    };

    return limits[providerId] || 5;
  }

  private async updateExecutionStatus(executionId: string, status: ExecutionStatus): Promise<void> {
    await this.resultStorage.updateExecutionStatus(executionId, status);
  }

  private async calculateBenchmarkResults(
    benchmarkId: string,
    executionResults: TaskExecutionResult[],
    benchmark: BenchmarkDefinition
  ): Promise<BenchmarkResult> {
    // Group results by task and model
    const resultsByTask = this.groupResultsByTask(executionResults);
    const resultsByModel = this.groupResultsByModel(executionResults);

    // Calculate aggregate metrics
    const aggregateMetrics = this.calculateAggregateMetrics(executionResults);

    return {
      id: benchmarkId,
      benchmarkDefinition: benchmark,
      executionResults,
      resultsByTask,
      resultsByModel,
      aggregateMetrics,
      startTime: new Date(Math.min(...executionResults.map((r) => r.startTime.getTime()))),
      endTime: new Date(Math.max(...executionResults.map((r) => r.endTime?.getTime() || 0))),
      status: this.determineBenchmarkStatus(executionResults),
      metadata: {
        totalExecutions: executionResults.length,
        successfulExecutions: executionResults.filter((r) => r.status === ExecutionStatus.COMPLETED)
          .length,
        failedExecutions: executionResults.filter((r) => r.status === ExecutionStatus.FAILED)
          .length,
        averageDuration: aggregateMetrics.averageDuration,
        totalTokens: aggregateMetrics.totalTokens,
      },
    };
  }

  private groupResultsByTask(
    results: TaskExecutionResult[]
  ): Record<string, TaskExecutionResult[]> {
    const grouped: Record<string, TaskExecutionResult[]> = {};

    for (const result of results) {
      const taskResults = grouped[result.taskId] || [];
      taskResults.push(result);
      grouped[result.taskId] = taskResults;
    }

    return grouped;
  }

  private groupResultsByModel(
    results: TaskExecutionResult[]
  ): Record<string, TaskExecutionResult[]> {
    const grouped: Record<string, TaskExecutionResult[]> = {};

    for (const result of results) {
      const modelKey = `${result.providerId}:${result.modelId}`;
      const modelResults = grouped[modelKey] || [];
      modelResults.push(result);
      grouped[modelKey] = modelResults;
    }

    return grouped;
  }

  private calculateAggregateMetrics(results: TaskExecutionResult[]): any {
    const successfulResults = results.filter((r) => r.status === ExecutionStatus.COMPLETED);

    if (successfulResults.length === 0) {
      return {
        averageDuration: 0,
        totalTokens: 0,
        averageTokensPerRequest: 0,
        successRate: 0,
      };
    }

    const totalDuration = successfulResults.reduce((sum, r) => sum + (r.duration || 0), 0);
    const totalTokens = successfulResults.reduce(
      (sum, r) => sum + (r.response?.tokenUsage?.totalTokens || 0),
      0
    );

    return {
      averageDuration: totalDuration / successfulResults.length,
      totalTokens,
      averageTokensPerRequest: totalTokens / successfulResults.length,
      successRate: successfulResults.length / results.length,
    };
  }

  private determineBenchmarkStatus(results: TaskExecutionResult[]): string {
    const hasFailures = results.some((r) => r.status === ExecutionStatus.FAILED);
    const hasRunning = results.some((r) => r.status === ExecutionStatus.RUNNING);
    const hasPending = results.some((r) => r.status === ExecutionStatus.PENDING);

    if (hasRunning || hasPending) return 'RUNNING';
    if (hasFailures) return 'COMPLETED_WITH_ERRORS';
    return 'COMPLETED';
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

interface BenchmarkDefinition {
  id: string;
  name: string;
  description: string;
  organizationId: string;
  userId: string;
  taskConfigs: TaskConfig[];
  modelConfigs: ModelConfig[];
  priority?: ExecutionPriority;
  timeout?: number;
  retryPolicy?: RetryPolicy;
  environment?: string;
  metadata?: Record<string, any>;
}

interface TaskConfig {
  taskId: string;
  config?: ExecutionConfig;
  weight?: number;
}

interface ModelConfig {
  providerId: string;
  modelId: string;
  config?: ExecutionConfig;
}

interface BenchmarkResult {
  id: string;
  benchmarkDefinition: BenchmarkDefinition;
  executionResults: TaskExecutionResult[];
  resultsByTask: Record<string, TaskExecutionResult[]>;
  resultsByModel: Record<string, TaskExecutionResult[]>;
  aggregateMetrics: any;
  startTime: Date;
  endTime: Date;
  status: string;
  metadata: Record<string, any>;
}
```

#### 2. Automated Scoring System (Story 4.2)

**Scoring Framework:**

```typescript
interface ScoringEngine {
  scoreExecution(execution: TaskExecutionResult, task: Task): Promise<ExecutionScore>;
  scoreBatch(executions: TaskExecutionResult[], tasks: Task[]): Promise<ExecutionScore[]>;
  getScoringCriteria(taskId: string): Promise<ScoringCriteria[]>;
  validateScore(score: ExecutionScore): Promise<ValidationResult>;
}

interface ExecutionScore {
  id: string;
  executionId: string;
  taskId: string;
  overallScore: number;
  criterionScores: CriterionScore[];
  confidence: number;
  feedback: ScoreFeedback;
  metadata: Record<string, any>;
  scoredAt: Date;
  scoredBy: string;
}

interface CriterionScore {
  criterionId: string;
  name: string;
  score: number;
  maxScore: number;
  weight: number;
  feedback: string;
  evidence: ScoreEvidence[];
  confidence: number;
}

interface ScoreEvidence {
  type: EvidenceType;
  content: string;
  relevance: number;
  source: string;
}

enum EvidenceType {
  TEXT_MATCH = 'text_match',
  SEMANTIC_SIMILARITY = 'semantic_similarity',
  STRUCTURAL_MATCH = 'structural_match',
  FORMAT_COMPLIANCE = 'format_compliance',
  CUSTOM_RULE = 'custom_rule',
}

interface ScoreFeedback {
  strengths: string[];
  weaknesses: string[];
  suggestions: string[];
  explanation: string;
}

interface ScoringCriteria {
  id: string;
  taskId: string;
  name: string;
  description: string;
  type: ScoringType;
  weight: number;
  maxScore: number;
  evaluationMethod: EvaluationMethod;
  config: ScoringConfig;
  isActive: boolean;
}

enum ScoringType {
  AUTOMATIC = 'automatic',
  MANUAL = 'manual',
  HYBRID = 'hybrid',
}

enum EvaluationMethod {
  EXACT_MATCH = 'exact_match',
  SEMANTIC_SIMILARITY = 'semantic_similarity',
  REGEX_MATCH = 'regex_match',
  CUSTOM_FUNCTION = 'custom_function',
  LLM_EVALUATION = 'llm_evaluation',
  HUMAN_REVIEW = 'human_review',
}

interface ScoringConfig {
  method: EvaluationMethod;
  parameters: Record<string, any>;
  thresholds?: {
    excellent: number;
    good: number;
    satisfactory: number;
  };
  rules?: ScoringRule[];
}

interface ScoringRule {
  type: string;
  condition: string;
  action: string;
  weight: number;
}
```

**Scoring Engine Implementation:**

```typescript
class AutomatedScoringEngine implements ScoringEngine {
  constructor(
    private taskRepository: TaskRepository,
    private textSimilarity: TextSimilarityService,
    private semanticSearch: SemanticSearchService,
    private llmEvaluator: LLMEvaluator,
    private ruleEngine: RuleEngine
  ) {}

  async scoreExecution(execution: TaskExecutionResult, task: Task): Promise<ExecutionScore> {
    // Get scoring criteria for the task
    const criteria = await this.getScoringCriteria(task.id);

    // Get actual response
    const actualResponse = execution.response?.content || '';
    const expectedOutput = task.expectedOutput;

    const criterionScores: CriterionScore[] = [];

    // Score each criterion
    for (const criterion of criteria) {
      const score = await this.scoreCriterion(criterion, actualResponse, expectedOutput, task);
      criterionScores.push(score);
    }

    // Calculate overall score
    const overallScore = this.calculateOverallScore(criterionScores);

    // Generate feedback
    const feedback = await this.generateFeedback(criterionScores, task);

    // Calculate confidence
    const confidence = this.calculateConfidence(criterionScores);

    return {
      id: `score_${execution.id}`,
      executionId: execution.id,
      taskId: task.id,
      overallScore,
      criterionScores,
      confidence,
      feedback,
      metadata: {
        scoringMethod: 'automated',
        criteriaCount: criteria.length,
        executionDuration: execution.duration,
      },
      scoredAt: new Date(),
      scoredBy: 'automated_scoring_engine',
    };
  }

  async scoreBatch(executions: TaskExecutionResult[], tasks: Task[]): Promise<ExecutionScore[]> {
    const scores: ExecutionScore[] = [];

    // Create task lookup
    const taskMap = new Map(tasks.map((t) => [t.id, t]));

    // Score executions in parallel with batching
    const batchSize = 10;
    for (let i = 0; i < executions.length; i += batchSize) {
      const batch = executions.slice(i, i + batchSize);
      const batchPromises = batch.map((execution) => {
        const task = taskMap.get(execution.taskId);
        return task ? this.scoreExecution(execution, task) : null;
      });

      const batchScores = await Promise.all(batchPromises);
      scores.push(...(batchScores.filter(Boolean) as ExecutionScore[]));
    }

    return scores;
  }

  async getScoringCriteria(taskId: string): Promise<ScoringCriteria[]> {
    const task = await this.taskRepository.getTask(taskId);
    if (!task || !task.content.evaluationCriteria) {
      return this.getDefaultCriteria(task);
    }

    return task.content.evaluationCriteria.map((criterion, index) => ({
      id: `criterion_${taskId}_${index}`,
      taskId,
      name: criterion.name,
      description: criterion.description,
      type: this.determineScoringType(criterion.evaluationMethod),
      weight: criterion.weight,
      maxScore: 100,
      evaluationMethod: criterion.evaluationMethod,
      config: this.buildScoringConfig(criterion),
      isActive: true,
    }));
  }

  async validateScore(score: ExecutionScore): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Validate overall score range
    if (score.overallScore < 0 || score.overallScore > 100) {
      errors.push({
        field: 'overallScore',
        message: 'Overall score must be between 0 and 100',
        code: 'INVALID_SCORE_RANGE',
      });
    }

    // Validate criterion scores
    for (const criterionScore of score.criterionScores) {
      if (criterionScore.score < 0 || criterionScore.score > criterionScore.maxScore) {
        errors.push({
          field: `criterionScores.${criterionScore.criterionId}`,
          message: `Criterion score must be between 0 and ${criterionScore.maxScore}`,
          code: 'INVALID_CRITERION_SCORE',
        });
      }

      if (criterionScore.weight < 0 || criterionScore.weight > 1) {
        warnings.push({
          field: `criterionScores.${criterionScore.criterionId}.weight`,
          message: 'Criterion weight should be between 0 and 1',
          code: 'UNUSUAL_WEIGHT',
        });
      }
    }

    // Validate confidence
    if (score.confidence < 0 || score.confidence > 1) {
      errors.push({
        field: 'confidence',
        message: 'Confidence must be between 0 and 1',
        code: 'INVALID_CONFIDENCE',
      });
    }

    return {
      isValid: errors.length === 0,
      errors,
      warnings,
    };
  }

  private async scoreCriterion(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any,
    task: Task
  ): Promise<CriterionScore> {
    switch (criterion.evaluationMethod) {
      case EvaluationMethod.EXACT_MATCH:
        return await this.scoreExactMatch(criterion, actualResponse, expectedOutput);

      case EvaluationMethod.SEMANTIC_SIMILARITY:
        return await this.scoreSemanticSimilarity(criterion, actualResponse, expectedOutput);

      case EvaluationMethod.REGEX_MATCH:
        return await this.scoreRegexMatch(criterion, actualResponse, expectedOutput);

      case EvaluationMethod.CUSTOM_FUNCTION:
        return await this.scoreCustomFunction(criterion, actualResponse, expectedOutput, task);

      case EvaluationMethod.LLM_EVALUATION:
        return await this.scoreWithLLM(criterion, actualResponse, expectedOutput, task);

      case EvaluationMethod.HUMAN_REVIEW:
        return this.createManualScore(criterion);

      default:
        throw new Error(`Unsupported evaluation method: ${criterion.evaluationMethod}`);
    }
  }

  private async scoreExactMatch(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any
  ): Promise<CriterionScore> {
    const expected =
      typeof expectedOutput?.value === 'string'
        ? expectedOutput.value
        : JSON.stringify(expectedOutput?.value);

    const isExactMatch = actualResponse.trim() === expected.trim();
    const score = isExactMatch ? criterion.maxScore : 0;

    const evidence: ScoreEvidence[] = [
      {
        type: EvidenceType.TEXT_MATCH,
        content: `Expected: "${expected}", Actual: "${actualResponse}"`,
        relevance: 1.0,
        source: 'exact_match_comparison',
      },
    ];

    return {
      criterionId: criterion.id,
      name: criterion.name,
      score,
      maxScore: criterion.maxScore,
      weight: criterion.weight,
      feedback: isExactMatch
        ? 'Perfect match with expected output'
        : 'Response does not match expected output exactly',
      evidence,
      confidence: 1.0,
    };
  }

  private async scoreSemanticSimilarity(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any
  ): Promise<CriterionScore> {
    const expected =
      typeof expectedOutput?.value === 'string'
        ? expectedOutput.value
        : JSON.stringify(expectedOutput?.value);

    const similarity = await this.semanticSearch.calculateSimilarity(actualResponse, expected);
    const score = Math.round(similarity * criterion.maxScore);

    const evidence: ScoreEvidence[] = [
      {
        type: EvidenceType.SEMANTIC_SIMILARITY,
        content: `Semantic similarity: ${similarity.toFixed(3)}`,
        relevance: similarity,
        source: 'semantic_similarity_calculation',
      },
    ];

    let feedback: string;
    if (similarity >= 0.9) {
      feedback = 'Excellent semantic match with expected output';
    } else if (similarity >= 0.7) {
      feedback = 'Good semantic similarity with expected output';
    } else if (similarity >= 0.5) {
      feedback = 'Moderate semantic similarity with expected output';
    } else {
      feedback = 'Low semantic similarity with expected output';
    }

    return {
      criterionId: criterion.id,
      name: criterion.name,
      score,
      maxScore: criterion.maxScore,
      weight: criterion.weight,
      feedback,
      evidence,
      confidence: 0.8,
    };
  }

  private async scoreRegexMatch(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any
  ): Promise<CriterionScore> {
    const patterns = criterion.config.parameters?.patterns || [];
    let totalMatches = 0;
    const evidence: ScoreEvidence[] = [];

    for (const pattern of patterns) {
      const regex = new RegExp(pattern, 'i');
      const matches = actualResponse.match(regex);
      const matchCount = matches ? matches.length : 0;

      totalMatches += matchCount;

      if (matchCount > 0) {
        evidence.push({
          type: EvidenceType.REGEX_MATCH,
          content: `Pattern "${pattern}" matched ${matchCount} times`,
          relevance: Math.min(matchCount / 5, 1.0), // Cap at 5 matches
          source: 'regex_pattern_matching',
        });
      }
    }

    const expectedMatches = patterns.length;
    const score = Math.round((totalMatches / expectedMatches) * criterion.maxScore);

    return {
      criterionId: criterion.id,
      name: criterion.name,
      score: Math.min(score, criterion.maxScore),
      maxScore: criterion.maxScore,
      weight: criterion.weight,
      feedback: `Matched ${totalMatches} out of ${expectedMatches} expected patterns`,
      evidence,
      confidence: 0.9,
    };
  }

  private async scoreCustomFunction(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any,
    task: Task
  ): Promise<CriterionScore> {
    const functionName = criterion.config.parameters?.functionName;

    if (!functionName) {
      throw new Error('Custom function name not specified');
    }

    try {
      // Execute custom scoring function
      const result = await this.ruleEngine.executeFunction(functionName, {
        actualResponse,
        expectedOutput,
        task,
        criterion,
      });

      return {
        criterionId: criterion.id,
        name: criterion.name,
        score: Math.min(Math.round(result.score), criterion.maxScore),
        maxScore: criterion.maxScore,
        weight: criterion.weight,
        feedback: result.feedback || 'Custom function evaluation completed',
        evidence: result.evidence || [],
        confidence: result.confidence || 0.7,
      };
    } catch (error) {
      throw new Error(`Custom function execution failed: ${error.message}`);
    }
  }

  private async scoreWithLLM(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any,
    task: Task
  ): Promise<CriterionScore> {
    const evaluationPrompt = this.buildLLMEvaluationPrompt(
      criterion,
      actualResponse,
      expectedOutput,
      task
    );

    try {
      const evaluation = await this.llmEvaluator.evaluate(evaluationPrompt);

      return {
        criterionId: criterion.id,
        name: criterion.name,
        score: Math.min(Math.round(evaluation.score), criterion.maxScore),
        maxScore: criterion.maxScore,
        weight: criterion.weight,
        feedback: evaluation.reasoning || 'LLM evaluation completed',
        evidence: evaluation.evidence || [],
        confidence: evaluation.confidence || 0.6,
      };
    } catch (error) {
      throw new Error(`LLM evaluation failed: ${error.message}`);
    }
  }

  private createManualScore(criterion: ScoringCriteria): CriterionScore {
    return {
      criterionId: criterion.id,
      name: criterion.name,
      score: 0, // Will be filled by human reviewer
      maxScore: criterion.maxScore,
      weight: criterion.weight,
      feedback: 'Pending manual review',
      evidence: [],
      confidence: 0.0,
    };
  }

  private calculateOverallScore(criterionScores: CriterionScore[]): number {
    let totalWeightedScore = 0;
    let totalWeight = 0;

    for (const criterionScore of criterionScores) {
      totalWeightedScore += criterionScore.score * criterionScore.weight;
      totalWeight += criterionScore.weight;
    }

    return totalWeight > 0 ? Math.round(totalWeightedScore / totalWeight) : 0;
  }

  private async generateFeedback(
    criterionScores: CriterionScore[],
    task: Task
  ): Promise<ScoreFeedback> {
    const strengths: string[] = [];
    const weaknesses: string[] = [];
    const suggestions: string[] = [];

    // Analyze criterion scores
    for (const criterionScore of criterionScores) {
      const percentage = (criterionScore.score / criterionScore.maxScore) * 100;

      if (percentage >= 80) {
        strengths.push(`Excellent performance on ${criterionScore.name}`);
      } else if (percentage >= 60) {
        suggestions.push(`Good performance on ${criterionScore.name}, but room for improvement`);
      } else {
        weaknesses.push(`Needs improvement on ${criterionScore.name}: ${criterionScore.feedback}`);
        suggestions.push(`Focus on ${criterionScore.name} to improve overall score`);
      }
    }

    // Generate overall explanation
    const overallScore = this.calculateOverallScore(criterionScores);
    let explanation: string;

    if (overallScore >= 90) {
      explanation = 'Outstanding performance across all evaluation criteria';
    } else if (overallScore >= 80) {
      explanation = 'Strong performance with minor areas for improvement';
    } else if (overallScore >= 70) {
      explanation = 'Good performance meeting most requirements';
    } else if (overallScore >= 60) {
      explanation = 'Satisfactory performance with several areas needing improvement';
    } else {
      explanation = 'Performance below expectations, significant improvement needed';
    }

    return {
      strengths,
      weaknesses,
      suggestions,
      explanation,
    };
  }

  private calculateConfidence(criterionScores: CriterionScore[]): number {
    if (criterionScores.length === 0) return 0;

    const totalConfidence = criterionScores.reduce((sum, score) => sum + score.confidence, 0);
    return totalConfidence / criterionScores.length;
  }

  private determineScoringType(evaluationMethod: EvaluationMethod): ScoringType {
    switch (evaluationMethod) {
      case EvaluationMethod.EXACT_MATCH:
      case EvaluationMethod.SEMANTIC_SIMILARITY:
      case EvaluationMethod.REGEX_MATCH:
      case EvaluationMethod.CUSTOM_FUNCTION:
      case EvaluationMethod.LLM_EVALUATION:
        return ScoringType.AUTOMATIC;

      case EvaluationMethod.HUMAN_REVIEW:
        return ScoringType.MANUAL;

      default:
        return ScoringType.AUTOMATIC;
    }
  }

  private buildScoringConfig(criterion: any): ScoringConfig {
    return {
      method: criterion.evaluationMethod,
      parameters: criterion.parameters || {},
      thresholds: criterion.thresholds || {
        excellent: 90,
        good: 80,
        satisfactory: 70,
      },
      rules: criterion.rules || [],
    };
  }

  private getDefaultCriteria(task: Task): ScoringCriteria[] {
    // Default criteria based on task type
    switch (task.taskType) {
      case TaskType.CODING:
        return [
          {
            id: `default_correctness_${task.id}`,
            taskId: task.id,
            name: 'Correctness',
            description: 'Code produces correct output',
            type: ScoringType.AUTOMATIC,
            weight: 0.5,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.CUSTOM_FUNCTION,
            config: {
              method: EvaluationMethod.CUSTOM_FUNCTION,
              parameters: {
                functionName: 'validateCodeOutput',
              },
            },
            isActive: true,
          },
          {
            id: `default_code_quality_${task.id}`,
            taskId: task.id,
            name: 'Code Quality',
            description: 'Code follows best practices',
            type: ScoringType.AUTOMATIC,
            weight: 0.3,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.LLM_EVALUATION,
            config: {
              method: EvaluationMethod.LLM_EVALUATION,
              parameters: {
                aspects: ['readability', 'maintainability', 'efficiency'],
              },
            },
            isActive: true,
          },
          {
            id: `default_format_compliance_${task.id}`,
            taskId: task.id,
            name: 'Format Compliance',
            description: 'Code follows required format',
            type: ScoringType.AUTOMATIC,
            weight: 0.2,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.REGEX_MATCH,
            config: {
              method: EvaluationMethod.REGEX_MATCH,
              parameters: {
                patterns: task.expectedOutput?.constraints?.map((c: any) => c.rule) || [],
              },
            },
            isActive: true,
          },
        ];

      case TaskType.REASONING:
        return [
          {
            id: `default_logical_correctness_${task.id}`,
            taskId: task.id,
            name: 'Logical Correctness',
            description: 'Reasoning is logically sound',
            type: ScoringType.AUTOMATIC,
            weight: 0.6,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.LLM_EVALUATION,
            config: {
              method: EvaluationMethod.LLM_EVALUATION,
              parameters: {
                aspects: ['logic', 'reasoning', 'conclusions'],
              },
            },
            isActive: true,
          },
          {
            id: `default_explanation_clarity_${task.id}`,
            taskId: task.id,
            name: 'Explanation Clarity',
            description: 'Explanation is clear and well-structured',
            type: ScoringType.AUTOMATIC,
            weight: 0.4,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.SEMANTIC_SIMILARITY,
            config: {
              method: EvaluationMethod.SEMANTIC_SIMILARITY,
              parameters: {
                referenceText: task.content.prompt,
              },
            },
            isActive: true,
          },
        ];

      default:
        return [
          {
            id: `default_general_${task.id}`,
            taskId: task.id,
            name: 'General Quality',
            description: 'Overall response quality',
            type: ScoringType.AUTOMATIC,
            weight: 1.0,
            maxScore: 100,
            evaluationMethod: EvaluationMethod.SEMANTIC_SIMILARITY,
            config: {
              method: EvaluationMethod.SEMANTIC_SIMILARITY,
              parameters: {},
            },
            isActive: true,
          },
        ];
    }
  }

  private buildLLMEvaluationPrompt(
    criterion: ScoringCriteria,
    actualResponse: string,
    expectedOutput: any,
    task: Task
  ): string {
    return `
Evaluate the following AI response based on the given criteria:

Task: ${task.content.prompt}
Expected Output: ${JSON.stringify(expectedOutput)}
Actual Response: ${actualResponse}

Evaluation Criteria: ${criterion.name}
Description: ${criterion.description}

Please provide:
1. A score from 0 to ${criterion.maxScore}
2. Detailed reasoning for the score
3. Specific feedback for improvement
4. Confidence level in your evaluation (0-1)

Respond in JSON format:
{
  "score": <number>,
  "reasoning": "<string>",
  "feedback": "<string>",
  "confidence": <number>,
  "evidence": [
    {
      "type": "string",
      "content": "string",
      "relevance": <number>
    }
  ]
}
`;
  }
}

interface LLMEvaluator {
  evaluate(prompt: string): Promise<LLMEvaluationResult>;
}

interface LLMEvaluationResult {
  score: number;
  reasoning: string;
  feedback: string;
  confidence: number;
  evidence: ScoreEvidence[];
}

interface RuleEngine {
  executeFunction(functionName: string, context: any): Promise<FunctionResult>;
}

interface FunctionResult {
  score: number;
  feedback: string;
  evidence: ScoreEvidence[];
  confidence: number;
}
```

#### 3. Result Storage & Time-Series Management (Story 4.3)

**Result Storage Architecture:**

```typescript
interface ResultStorage {
  storeExecution(execution: TaskExecutionResult): Promise<void>;
  storeExecutionScore(score: ExecutionScore): Promise<void>;
  storeBenchmark(benchmark: BenchmarkResult): Promise<void>;
  getExecution(executionId: string): Promise<TaskExecutionResult | null>;
  getExecutionScore(executionId: string): Promise<ExecutionScore | null>;
  getBenchmark(benchmarkId: string): Promise<BenchmarkResult | null>;
  queryExecutions(query: ExecutionQuery): Promise<ExecutionQueryResult>;
  queryScores(query: ScoreQuery): Promise<ScoreQueryResult>;
  queryBenchmarks(query: BenchmarkQuery): Promise<BenchmarkQueryResult>;
  getTimeSeriesData(query: TimeSeriesQuery): Promise<TimeSeriesData>;
  aggregateResults(query: AggregationQuery): Promise<AggregationResult>;
}

interface ExecutionQuery {
  executionIds?: string[];
  taskIds?: string[];
  providerIds?: string[];
  modelIds?: string[];
  organizationId?: string;
  userId?: string;
  status?: ExecutionStatus[];
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  limit?: number;
  offset?: number;
  orderBy?: string;
  orderDirection?: 'asc' | 'desc';
}

interface ScoreQuery {
  executionIds?: string[];
  taskIds?: string[];
  criteriaIds?: string[];
  minScore?: number;
  maxScore?: number;
  organizationId?: string;
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  limit?: number;
  offset?: number;
}

interface BenchmarkQuery {
  benchmarkIds?: string[];
  organizationId?: string;
  userId?: string;
  status?: string[];
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  limit?: number;
  offset?: number;
}

interface TimeSeriesQuery {
  metric: TimeSeriesMetric;
  granularity: TimeSeriesGranularity;
  filters: Record<string, any>;
  dateRange: {
    startDate: Date;
    endDate: Date;
  };
  groupBy?: string[];
}

enum TimeSeriesMetric {
  EXECUTION_COUNT = 'execution_count',
  SUCCESS_RATE = 'success_rate',
  AVERAGE_DURATION = 'average_duration',
  AVERAGE_SCORE = 'average_score',
  TOKEN_USAGE = 'token_usage',
  COST = 'cost',
  ERROR_RATE = 'error_rate',
}

enum TimeSeriesGranularity {
  MINUTE = 'minute',
  HOUR = 'hour',
  DAY = 'day',
  WEEK = 'week',
  MONTH = 'month',
}

interface TimeSeriesData {
  metric: TimeSeriesMetric;
  granularity: TimeSeriesGranularity;
  dataPoints: TimeSeriesDataPoint[];
  metadata: Record<string, any>;
}

interface TimeSeriesDataPoint {
  timestamp: Date;
  value: number;
  metadata?: Record<string, any>;
}

interface AggregationQuery {
  metric: AggregationMetric;
  dimensions: string[];
  filters: Record<string, any>;
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
}

enum AggregationMetric {
  TOTAL_EXECUTIONS = 'total_executions',
  AVERAGE_SCORE = 'average_score',
  MEDIAN_SCORE = 'median_score',
  SCORE_DISTRIBUTION = 'score_distribution',
  MODEL_PERFORMANCE = 'model_performance',
  TASK_DIFFICULTY = 'task_difficulty',
  PROVIDER_COMPARISON = 'provider_comparison',
}

interface AggregationResult {
  metric: AggregationMetric;
  dimensions: string[];
  results: AggregationResultItem[];
  metadata: Record<string, any>;
}

interface AggregationResultItem {
  dimensions: Record<string, any>;
  value: number;
  count?: number;
  metadata?: Record<string, any>;
}
```

**Result Storage Implementation:**

```typescript
class ResultStorageService implements ResultStorage {
  constructor(
    private primaryDB: Database, // PostgreSQL for structured data
    private timeSeriesDB: TimeSeriesDatabase, // TimescaleDB or InfluxDB
    private objectStorage: ObjectStorage, // S3 for large objects
    private cache: Cache // Redis for caching
  ) {}

  async storeExecution(execution: TaskExecutionResult): Promise<void> {
    await this.primaryDB.transaction(async (tx) => {
      // Store main execution record
      await tx.query(
        `
        INSERT INTO executions (
          id, task_id, provider_id, model_id, status, 
          start_time, end_time, duration, 
          response_data, error_data, metrics, 
          context, metadata, created_at
        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, NOW())
      `,
        [
          execution.id,
          execution.taskId,
          execution.providerId,
          execution.modelId,
          execution.status,
          execution.startTime,
          execution.endTime,
          execution.duration,
          execution.response ? JSON.stringify(execution.response) : null,
          execution.error ? JSON.stringify(execution.error) : null,
          JSON.stringify(execution.metrics),
          JSON.stringify(execution.context),
          JSON.stringify(execution.metadata),
        ]
      );

      // Store time-series data
      await this.storeExecutionTimeSeries(execution);

      // Update aggregates
      await this.updateExecutionAggregates(execution);
    });

    // Invalidate cache
    await this.invalidateExecutionCache(execution.id);
  }

  async storeExecutionScore(score: ExecutionScore): Promise<void> {
    await this.primaryDB.transaction(async (tx) => {
      // Store main score record
      await tx.query(
        `
        INSERT INTO execution_scores (
          id, execution_id, task_id, overall_score, 
          confidence, feedback, metadata, 
          scored_at, scored_by
        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
      `,
        [
          score.id,
          score.executionId,
          score.taskId,
          score.overallScore,
          score.confidence,
          JSON.stringify(score.feedback),
          JSON.stringify(score.metadata),
          score.scoredAt,
          score.scoredBy,
        ]
      );

      // Store criterion scores
      for (const criterionScore of score.criterionScores) {
        await tx.query(
          `
          INSERT INTO criterion_scores (
            id, score_id, criterion_id, name, score, 
            max_score, weight, feedback, evidence, confidence
          ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        `,
          [
            `criterion_${score.id}_${criterionScore.criterionId}`,
            score.id,
            criterionScore.criterionId,
            criterionScore.name,
            criterionScore.score,
            criterionScore.maxScore,
            criterionScore.weight,
            criterionScore.feedback,
            JSON.stringify(criterionScore.evidence),
            criterionScore.confidence,
          ]
        );
      }

      // Store score time-series data
      await this.storeScoreTimeSeries(score);
    });

    // Invalidate cache
    await this.invalidateScoreCache(score.executionId);
  }

  async storeBenchmark(benchmark: BenchmarkResult): Promise<void> {
    // Store large result data in object storage
    const resultDataKey = `benchmark-results/${benchmark.id}`;
    await this.objectStorage.store(resultDataKey, JSON.stringify(benchmark.executionResults));

    await this.primaryDB.transaction(async (tx) => {
      // Store main benchmark record
      await tx.query(
        `
        INSERT INTO benchmarks (
          id, name, description, organization_id, user_id,
          start_time, end_time, status, result_data_key,
          aggregate_metrics, metadata, created_at
        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, NOW())
      `,
        [
          benchmark.id,
          benchmark.benchmarkDefinition.name,
          benchmark.benchmarkDefinition.description,
          benchmark.benchmarkDefinition.organizationId,
          benchmark.benchmarkDefinition.userId,
          benchmark.startTime,
          benchmark.endTime,
          benchmark.status,
          resultDataKey,
          JSON.stringify(benchmark.aggregateMetrics),
          JSON.stringify(benchmark.metadata),
        ]
      );

      // Store benchmark time-series data
      await this.storeBenchmarkTimeSeries(benchmark);
    });
  }

  async getExecution(executionId: string): Promise<TaskExecutionResult | null> {
    // Try cache first
    const cached = await this.cache.get(`execution:${executionId}`);
    if (cached) {
      return JSON.parse(cached);
    }

    const result = await this.primaryDB.query(
      `
      SELECT * FROM executions WHERE id = $1
    `,
      [executionId]
    );

    if (result.rows.length === 0) {
      return null;
    }

    const row = result.rows[0];
    const execution: TaskExecutionResult = {
      id: row.id,
      taskId: row.task_id,
      providerId: row.provider_id,
      modelId: row.model_id,
      status: row.status,
      startTime: row.start_time,
      endTime: row.end_time,
      duration: row.duration,
      response: row.response_data ? JSON.parse(row.response_data) : undefined,
      error: row.error_data ? JSON.parse(row.error_data) : undefined,
      metrics: JSON.parse(row.metrics),
      context: JSON.parse(row.context),
      metadata: JSON.parse(row.metadata),
    };

    // Cache for 1 hour
    await this.cache.set(`execution:${executionId}`, JSON.stringify(execution), 3600);

    return execution;
  }

  async getExecutionScore(executionId: string): Promise<ExecutionScore | null> {
    // Try cache first
    const cached = await this.cache.get(`score:${executionId}`);
    if (cached) {
      return JSON.parse(cached);
    }

    const result = await this.primaryDB.query(
      `
      SELECT s.*, 
             json_agg(
               json_build_object(
                 'criterionId', cs.criterion_id,
                 'name', cs.name,
                 'score', cs.score,
                 'maxScore', cs.max_score,
                 'weight', cs.weight,
                 'feedback', cs.feedback,
                 'evidence', cs.evidence,
                 'confidence', cs.confidence
               )
             ) as criterion_scores
      FROM execution_scores s
      LEFT JOIN criterion_scores cs ON s.id = cs.score_id
      WHERE s.execution_id = $1
      GROUP BY s.id
    `,
      [executionId]
    );

    if (result.rows.length === 0) {
      return null;
    }

    const row = result.rows[0];
    const score: ExecutionScore = {
      id: row.id,
      executionId: row.execution_id,
      taskId: row.task_id,
      overallScore: row.overall_score,
      criterionScores: row.criterion_scores,
      confidence: row.confidence,
      feedback: JSON.parse(row.feedback),
      metadata: JSON.parse(row.metadata),
      scoredAt: row.scored_at,
      scoredBy: row.scored_by,
    };

    // Cache for 1 hour
    await this.cache.set(`score:${executionId}`, JSON.stringify(score), 3600);

    return score;
  }

  async getBenchmark(benchmarkId: string): Promise<BenchmarkResult | null> {
    const result = await this.primaryDB.query(
      `
      SELECT * FROM benchmarks WHERE id = $1
    `,
      [benchmarkId]
    );

    if (result.rows.length === 0) {
      return null;
    }

    const row = result.rows[0];

    // Retrieve execution results from object storage
    const executionResults = await this.objectStorage.retrieve(row.result_data_key);

    return {
      id: row.id,
      benchmarkDefinition: JSON.parse(row.metadata), // Would be stored separately
      executionResults: JSON.parse(executionResults),
      resultsByTask: {}, // Would be computed
      resultsByModel: {}, // Would be computed
      aggregateMetrics: JSON.parse(row.aggregate_metrics),
      startTime: row.start_time,
      endTime: row.end_time,
      status: row.status,
      metadata: JSON.parse(row.metadata),
    };
  }

  async queryExecutions(query: ExecutionQuery): Promise<ExecutionQueryResult> {
    let sql = `
      SELECT e.*, t.name as task_name, t.category as task_category
      FROM executions e
      LEFT JOIN tasks t ON e.task_id = t.id
      WHERE 1=1
    `;
    const params: any[] = [];
    let paramIndex = 1;

    // Build WHERE clause
    if (query.executionIds?.length) {
      sql += ` AND e.id = ANY($${paramIndex++})`;
      params.push(query.executionIds);
    }

    if (query.taskIds?.length) {
      sql += ` AND e.task_id = ANY($${paramIndex++})`;
      params.push(query.taskIds);
    }

    if (query.providerIds?.length) {
      sql += ` AND e.provider_id = ANY($${paramIndex++})`;
      params.push(query.providerIds);
    }

    if (query.modelIds?.length) {
      sql += ` AND e.model_id = ANY($${paramIndex++})`;
      params.push(query.modelIds);
    }

    if (query.organizationId) {
      sql += ` AND e.context->>'organizationId' = $${paramIndex++}`;
      params.push(query.organizationId);
    }

    if (query.status?.length) {
      sql += ` AND e.status = ANY($${paramIndex++})`;
      params.push(query.status);
    }

    if (query.dateRange) {
      sql += ` AND e.start_time >= $${paramIndex++} AND e.start_time <= $${paramIndex++}`;
      params.push(query.dateRange.startDate, query.dateRange.endDate);
    }

    // Add ORDER BY
    if (query.orderBy) {
      sql += ` ORDER BY e.${query.orderBy} ${query.orderDirection || 'DESC'}`;
    } else {
      sql += ` ORDER BY e.start_time DESC`;
    }

    // Add LIMIT and OFFSET
    if (query.limit) {
      sql += ` LIMIT $${paramIndex++}`;
      params.push(query.limit);
    }

    if (query.offset) {
      sql += ` OFFSET $${paramIndex++}`;
      params.push(query.offset);
    }

    const result = await this.primaryDB.query(sql, params);

    // Get total count
    const countResult = await this.primaryDB.query(
      `
      SELECT COUNT(*) as total
      FROM executions e
      WHERE 1=1
      ${this.buildCountWhereClause(query)}
    `,
      this.buildCountParams(query)
    );

    return {
      executions: result.rows.map(this.mapExecutionRow),
      pagination: {
        total: parseInt(countResult.rows[0].total),
        limit: query.limit || 20,
        offset: query.offset || 0,
      },
    };
  }

  async getTimeSeriesData(query: TimeSeriesQuery): Promise<TimeSeriesData> {
    const timeSeriesQuery = this.buildTimeSeriesQuery(query);
    const result = await this.timeSeriesDB.query(timeSeriesQuery);

    return {
      metric: query.metric,
      granularity: query.granularity,
      dataPoints: result.rows.map((row) => ({
        timestamp: row.timestamp,
        value: parseFloat(row.value),
        metadata: row.metadata ? JSON.parse(row.metadata) : undefined,
      })),
      metadata: {
        query,
        dataPointCount: result.rows.length,
      },
    };
  }

  async aggregateResults(query: AggregationQuery): Promise<AggregationResult> {
    const sql = this.buildAggregationQuery(query);
    const result = await this.primaryDB.query(sql, this.buildAggregationParams(query));

    return {
      metric: query.metric,
      dimensions: query.dimensions,
      results: result.rows.map((row) => ({
        dimensions: this.extractDimensions(row, query.dimensions),
        value: parseFloat(row.value),
        count: row.count ? parseInt(row.count) : undefined,
        metadata: row.metadata ? JSON.parse(row.metadata) : undefined,
      })),
      metadata: {
        query,
        resultCount: result.rows.length,
      },
    };
  }

  private async storeExecutionTimeSeries(execution: TaskExecutionResult): Promise<void> {
    const timestamp = execution.startTime;
    const tags = {
      task_id: execution.taskId,
      provider_id: execution.providerId,
      model_id: execution.modelId,
      organization_id: execution.context.organizationId,
      status: execution.status,
    };

    const metrics = [
      {
        metric: TimeSeriesMetric.EXECUTION_COUNT,
        value: 1,
        timestamp,
        tags,
      },
      {
        metric: TimeSeriesMetric.AVERAGE_DURATION,
        value: execution.duration || 0,
        timestamp,
        tags,
      },
    ];

    if (execution.response?.tokenUsage) {
      metrics.push({
        metric: TimeSeriesMetric.TOKEN_USAGE,
        value: execution.response.tokenUsage.totalTokens,
        timestamp,
        tags,
      });
    }

    if (execution.status === ExecutionStatus.COMPLETED) {
      metrics.push({
        metric: TimeSeriesMetric.SUCCESS_RATE,
        value: 1,
        timestamp,
        tags,
      });
    } else {
      metrics.push({
        metric: TimeSeriesMetric.ERROR_RATE,
        value: 1,
        timestamp,
        tags,
      });
    }

    await this.timeSeriesDB.insertMetrics(metrics);
  }

  private async storeScoreTimeSeries(score: ExecutionScore): Promise<void> {
    const timestamp = score.scoredAt;
    const tags = {
      task_id: score.taskId,
      execution_id: score.executionId,
      organization_id: score.metadata.organizationId,
    };

    const metrics = [
      {
        metric: TimeSeriesMetric.AVERAGE_SCORE,
        value: score.overallScore,
        timestamp,
        tags,
      },
    ];

    await this.timeSeriesDB.insertMetrics(metrics);
  }

  private async storeBenchmarkTimeSeries(benchmark: BenchmarkResult): Promise<void> {
    const timestamp = benchmark.startTime;
    const tags = {
      benchmark_id: benchmark.id,
      organization_id: benchmark.benchmarkDefinition.organizationId,
    };

    const metrics = [
      {
        metric: TimeSeriesMetric.EXECUTION_COUNT,
        value: benchmark.executionResults.length,
        timestamp,
        tags,
      },
    ];

    await this.timeSeriesDB.insertMetrics(metrics);
  }

  private async updateExecutionAggregates(execution: TaskExecutionResult): Promise<void> {
    // Update daily/weekly/monthly aggregates
    const aggregateKey = `${execution.context.organizationId}:${new Date().toISOString().split('T')[0]}`;

    await this.primaryDB.query(
      `
      INSERT INTO daily_execution_aggregates (aggregate_key, date, organization_id, execution_count, success_count, total_duration)
      VALUES ($1, $2, $3, $4, $5, $6)
      ON CONFLICT (aggregate_key) 
      DO UPDATE SET 
        execution_count = daily_execution_aggregates.execution_count + 1,
        success_count = daily_execution_aggregates.success_count + $5,
        total_duration = daily_execution_aggregates.total_duration + $6
    `,
      [
        aggregateKey,
        execution.startTime,
        execution.context.organizationId,
        1,
        execution.status === ExecutionStatus.COMPLETED ? 1 : 0,
        execution.duration || 0,
      ]
    );
  }

  private buildTimeSeriesQuery(query: TimeSeriesQuery): string {
    const { metric, granularity, filters, dateRange, groupBy } = query;

    let sql = `
      SELECT time_bucket($1, timestamp) as timestamp,
             AVG(value) as value
      FROM time_series_metrics
      WHERE metric = $2
    `;

    const params: any[] = [this.getTimeBucketSize(granularity), metric];
    let paramIndex = 3;

    // Add filters
    if (filters) {
      for (const [key, value] of Object.entries(filters)) {
        sql += ` AND tags->>${paramIndex} = $${paramIndex + 1}`;
        params.push(key, value);
        paramIndex += 2;
      }
    }

    // Add date range
    if (dateRange) {
      sql += ` AND timestamp >= $${paramIndex} AND timestamp <= $${paramIndex + 1}`;
      params.push(dateRange.startDate, dateRange.endDate);
      paramIndex += 2;
    }

    sql += ` GROUP BY time_bucket($1, timestamp) ORDER BY timestamp`;

    return sql;
  }

  private buildAggregationQuery(query: AggregationQuery): string {
    // Build aggregation query based on metric and dimensions
    // This is a simplified version - real implementation would be more complex
    let sql = `SELECT `;

    if (query.dimensions.length > 0) {
      sql += query.dimensions.map((dim) => `${dim}`).join(', ');
    } else {
      sql += 'NULL as dimension';
    }

    switch (query.metric) {
      case AggregationMetric.TOTAL_EXECUTIONS:
        sql += ', COUNT(*) as value, COUNT(*) as count';
        break;
      case AggregationMetric.AVERAGE_SCORE:
        sql += ', AVG(s.overall_score) as value, COUNT(*) as count';
        break;
      default:
        sql += ', COUNT(*) as value, COUNT(*) as count';
    }

    sql += ' FROM executions e LEFT JOIN execution_scores s ON e.id = s.execution_id WHERE 1=1';

    // Add filters
    if (query.filters) {
      // Add filter conditions
    }

    if (query.dimensions.length > 0) {
      sql += ` GROUP BY ${query.dimensions.join(', ')}`;
    }

    return sql;
  }

  private getTimeBucketSize(granularity: TimeSeriesGranularity): string {
    switch (granularity) {
      case TimeSeriesGranularity.MINUTE:
        return '1 minute';
      case TimeSeriesGranularity.HOUR:
        return '1 hour';
      case TimeSeriesGranularity.DAY:
        return '1 day';
      case TimeSeriesGranularity.WEEK:
        return '1 week';
      case TimeSeriesGranularity.MONTH:
        return '1 month';
      default:
        return '1 hour';
    }
  }

  private mapExecutionRow(row: any): TaskExecutionResult {
    return {
      id: row.id,
      taskId: row.task_id,
      providerId: row.provider_id,
      modelId: row.model_id,
      status: row.status,
      startTime: row.start_time,
      endTime: row.end_time,
      duration: row.duration,
      response: row.response_data ? JSON.parse(row.response_data) : undefined,
      error: row.error_data ? JSON.parse(row.error_data) : undefined,
      metrics: JSON.parse(row.metrics),
      context: JSON.parse(row.context),
      metadata: JSON.parse(row.metadata),
    };
  }

  private extractDimensions(row: any, dimensions: string[]): Record<string, any> {
    const result: Record<string, any> = {};
    for (const dimension of dimensions) {
      result[dimension] = row[dimension];
    }
    return result;
  }

  private async invalidateExecutionCache(executionId: string): Promise<void> {
    await this.cache.del(`execution:${executionId}`);
  }

  private async invalidateScoreCache(executionId: string): Promise<void> {
    await this.cache.del(`score:${executionId}`);
  }
}

interface ExecutionQueryResult {
  executions: TaskExecutionResult[];
  pagination: {
    total: number;
    limit: number;
    offset: number;
  };
}

interface ScoreQueryResult {
  scores: ExecutionScore[];
  pagination: {
    total: number;
    limit: number;
    offset: number;
  };
}

interface BenchmarkQueryResult {
  benchmarks: BenchmarkResult[];
  pagination: {
    total: number;
    limit: number;
    offset: number;
  };
}

interface TimeSeriesDatabase {
  query(sql: string, params?: any[]): Promise<any>;
  insertMetrics(metrics: any[]): Promise<void>;
}

interface ObjectStorage {
  store(key: string, data: string): Promise<void>;
  retrieve(key: string): Promise<string>;
  delete(key: string): Promise<void>;
}
```

#### 4. Benchmark Orchestration & Scheduling (Story 4.4)

**Orchestration Framework:**

```typescript
interface BenchmarkOrchestrator {
  createBenchmark(request: CreateBenchmarkRequest): Promise<BenchmarkDefinition>;
  scheduleBenchmark(benchmarkId: string, schedule: ScheduleConfig): Promise<void>;
  executeBenchmark(benchmarkId: string): Promise<BenchmarkResult>;
  pauseBenchmark(benchmarkId: string): Promise<void>;
  resumeBenchmark(benchmarkId: string): Promise<void>;
  cancelBenchmark(benchmarkId: string): Promise<void>;
  getBenchmarkStatus(benchmarkId: string): Promise<BenchmarkStatus>;
  listBenchmarks(filter: BenchmarkFilter): Promise<BenchmarkList>;
}

interface CreateBenchmarkRequest {
  name: string;
  description: string;
  organizationId: string;
  userId: string;
  taskConfigs: TaskConfig[];
  modelConfigs: ModelConfig[];
  schedule?: ScheduleConfig;
  resources?: ResourceConfig;
  notifications?: NotificationConfig;
  metadata?: Record<string, any>;
}

interface ScheduleConfig {
  type: ScheduleType;
  startTime?: Date;
  endTime?: Date;
  frequency?: ScheduleFrequency;
  timezone?: string;
  retryPolicy?: RetryPolicy;
}

enum ScheduleType {
  IMMEDIATE = 'immediate',
  SCHEDULED = 'scheduled',
  RECURRING = 'recurring',
  ON_DEMAND = 'on_demand',
}

interface ScheduleFrequency {
  type: 'minutes' | 'hours' | 'days' | 'weeks' | 'months';
  interval: number;
  daysOfWeek?: number[]; // 0-6 (Sunday-Saturday)
  dayOfMonth?: number;
}

interface ResourceConfig {
  maxConcurrentExecutions: number;
  priority: ExecutionPriority;
  timeout: number;
  retryPolicy: RetryPolicy;
  resourceLimits: {
    maxTokens: number;
    maxCost: number;
    maxDuration: number;
  };
}

interface NotificationConfig {
  onSuccess: boolean;
  onFailure: boolean;
  onProgress: boolean;
  channels: NotificationChannel[];
  recipients: string[];
}

interface NotificationChannel {
  type: 'email' | 'webhook' | 'slack' | 'teams';
  config: Record<string, any>;
}

interface BenchmarkStatus {
  benchmarkId: string;
  status: BenchmarkExecutionStatus;
  progress: BenchmarkProgress;
  nextRun?: Date;
  lastRun?: Date;
  metrics: BenchmarkMetrics;
}

enum BenchmarkExecutionStatus {
  PENDING = 'pending',
  SCHEDULED = 'scheduled',
  RUNNING = 'running',
  PAUSED = 'paused',
  COMPLETED = 'completed',
  FAILED = 'failed',
  CANCELLED = 'cancelled',
}

interface BenchmarkProgress {
  totalExecutions: number;
  completedExecutions: number;
  failedExecutions: number;
  runningExecutions: number;
  percentage: number;
  estimatedCompletion?: Date;
  currentPhase?: string;
}

interface BenchmarkMetrics {
  startTime?: Date;
  endTime?: Date;
  duration?: number;
  totalCost?: number;
  totalTokens?: number;
  averageScore?: number;
  successRate?: number;
}

interface BenchmarkFilter {
  organizationId?: string;
  userId?: string;
  status?: BenchmarkExecutionStatus[];
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  limit?: number;
  offset?: number;
}

interface BenchmarkList {
  benchmarks: BenchmarkDefinition[];
  pagination: {
    total: number;
    limit: number;
    offset: number;
  };
}
```

**Orchestrator Implementation:**

```typescript
class BenchmarkOrchestratorService implements BenchmarkOrchestrator {
  constructor(
    private benchmarkRepository: BenchmarkRepository,
    private taskExecutor: TaskExecutor,
    private scheduler: TaskScheduler,
    private resourceManager: ResourceManager,
    private notificationService: NotificationService,
    private metricsCollector: MetricsCollector
  ) {}

  async createBenchmark(request: CreateBenchmarkRequest): Promise<BenchmarkDefinition> {
    // Validate request
    await this.validateBenchmarkRequest(request);

    // Create benchmark definition
    const benchmark: BenchmarkDefinition = {
      id: `benchmark_${Date.now()}`,
      name: request.name,
      description: request.description,
      organizationId: request.organizationId,
      userId: request.userId,
      taskConfigs: request.taskConfigs,
      modelConfigs: request.modelConfigs,
      priority: request.resources?.priority || ExecutionPriority.NORMAL,
      timeout: request.resources?.timeout || 300000, // 5 minutes default
      retryPolicy: request.resources?.retryPolicy || this.getDefaultRetryPolicy(),
      environment: 'production',
      metadata: request.metadata || {},
    };

    // Store benchmark
    await this.benchmarkRepository.create(benchmark);

    // Schedule if requested
    if (request.schedule) {
      await this.scheduleBenchmark(benchmark.id, request.schedule);
    }

    // Send notification
    await this.notificationService.sendNotification({
      type: 'benchmark_created',
      benchmarkId: benchmark.id,
      organizationId: request.organizationId,
      userId: request.userId,
      message: `Benchmark "${request.name}" created successfully`,
    });

    return benchmark;
  }

  async scheduleBenchmark(benchmarkId: string, schedule: ScheduleConfig): Promise<void> {
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (!benchmark) {
      throw new Error(`Benchmark ${benchmarkId} not found`);
    }

    // Create schedule
    const scheduleRecord = {
      benchmarkId,
      config: schedule,
      isActive: true,
      createdAt: new Date(),
    };

    await this.benchmarkRepository.createSchedule(scheduleRecord);

    // Schedule with scheduler
    if (schedule.type === ScheduleType.IMMEDIATE) {
      await this.scheduler.scheduleImmediate(benchmarkId);
    } else if (schedule.type === ScheduleType.SCHEDULED && schedule.startTime) {
      await this.scheduler.scheduleAt(benchmarkId, schedule.startTime);
    } else if (schedule.type === ScheduleType.RECURRING && schedule.frequency) {
      await this.scheduler.scheduleRecurring(benchmarkId, schedule);
    }
  }

  async executeBenchmark(benchmarkId: string): Promise<BenchmarkResult> {
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (!benchmark) {
      throw new Error(`Benchmark ${benchmarkId} not found`);
    }

    // Check resource availability
    await this.resourceManager.checkResources(benchmark);

    // Update status
    await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.RUNNING);

    try {
      // Create execution requests
      const executionRequests = this.createExecutionRequests(benchmark);

      // Execute with resource management
      const result = await this.executeWithResourceManagement(benchmark, executionRequests);

      // Update final status
      await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.COMPLETED);

      // Send completion notification
      await this.notificationService.sendNotification({
        type: 'benchmark_completed',
        benchmarkId,
        organizationId: benchmark.organizationId,
        userId: benchmark.userId,
        message: `Benchmark "${benchmark.name}" completed successfully`,
        data: {
          totalExecutions: executionRequests.length,
          successRate: result.metadata.successfulExecutions / executionRequests.length,
          averageScore: result.aggregateMetrics.averageScore,
        },
      });

      return result;
    } catch (error) {
      await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.FAILED);

      // Send failure notification
      await this.notificationService.sendNotification({
        type: 'benchmark_failed',
        benchmarkId,
        organizationId: benchmark.organizationId,
        userId: benchmark.userId,
        message: `Benchmark "${benchmark.name}" failed: ${error.message}`,
        data: { error: error.message },
      });

      throw error;
    }
  }

  async pauseBenchmark(benchmarkId: string): Promise<void> {
    await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.PAUSED);
    await this.scheduler.pauseBenchmark(benchmarkId);

    // Send notification
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (benchmark) {
      await this.notificationService.sendNotification({
        type: 'benchmark_paused',
        benchmarkId,
        organizationId: benchmark.organizationId,
        userId: benchmark.userId,
        message: `Benchmark "${benchmark.name}" paused`,
      });
    }
  }

  async resumeBenchmark(benchmarkId: string): Promise<void> {
    await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.RUNNING);
    await this.scheduler.resumeBenchmark(benchmarkId);

    // Send notification
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (benchmark) {
      await this.notificationService.sendNotification({
        type: 'benchmark_resumed',
        benchmarkId,
        organizationId: benchmark.organizationId,
        userId: benchmark.userId,
        message: `Benchmark "${benchmark.name}" resumed`,
      });
    }
  }

  async cancelBenchmark(benchmarkId: string): Promise<void> {
    await this.updateBenchmarkStatus(benchmarkId, BenchmarkExecutionStatus.CANCELLED);
    await this.scheduler.cancelBenchmark(benchmarkId);

    // Cancel running executions
    await this.cancelRunningExecutions(benchmarkId);

    // Send notification
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (benchmark) {
      await this.notificationService.sendNotification({
        type: 'benchmark_cancelled',
        benchmarkId,
        organizationId: benchmark.organizationId,
        userId: benchmark.userId,
        message: `Benchmark "${benchmark.name}" cancelled`,
      });
    }
  }

  async getBenchmarkStatus(benchmarkId: string): Promise<BenchmarkStatus> {
    const benchmark = await this.benchmarkRepository.get(benchmarkId);
    if (!benchmark) {
      throw new Error(`Benchmark ${benchmarkId} not found`);
    }

    const schedule = await this.benchmarkRepository.getSchedule(benchmarkId);
    const runningExecutions = await this.getRunningExecutions(benchmarkId);

    // Calculate progress
    const totalExecutions = benchmark.taskConfigs.length * benchmark.modelConfigs.length;
    const completedExecutions = await this.getCompletedExecutionsCount(benchmarkId);
    const failedExecutions = await this.getFailedExecutionsCount(benchmarkId);

    const progress: BenchmarkProgress = {
      totalExecutions,
      completedExecutions,
      failedExecutions,
      runningExecutions: runningExecutions.length,
      percentage: Math.round((completedExecutions / totalExecutions) * 100),
      estimatedCompletion: this.calculateEstimatedCompletion(
        benchmarkId,
        completedExecutions,
        totalExecutions
      ),
    };

    // Get metrics
    const metrics = await this.calculateBenchmarkMetrics(benchmarkId);

    return {
      benchmarkId,
      status: await this.getCurrentBenchmarkStatus(benchmarkId),
      progress,
      nextRun: schedule?.config?.startTime,
      lastRun: await this.getLastRunTime(benchmarkId),
      metrics,
    };
  }

  async listBenchmarks(filter: BenchmarkFilter): Promise<BenchmarkList> {
    return await this.benchmarkRepository.list(filter);
  }

  private async validateBenchmarkRequest(request: CreateBenchmarkRequest): Promise<void> {
    if (!request.name || request.name.trim().length === 0) {
      throw new Error('Benchmark name is required');
    }

    if (!request.taskConfigs || request.taskConfigs.length === 0) {
      throw new Error('At least one task configuration is required');
    }

    if (!request.modelConfigs || request.modelConfigs.length === 0) {
      throw new Error('At least one model configuration is required');
    }

    // Validate task configurations
    for (const taskConfig of request.taskConfigs) {
      if (!taskConfig.taskId) {
        throw new Error('Task ID is required in task configuration');
      }
    }

    // Validate model configurations
    for (const modelConfig of request.modelConfigs) {
      if (!modelConfig.providerId || !modelConfig.modelId) {
        throw new Error('Provider ID and Model ID are required in model configuration');
      }
    }

    // Check resource limits
    if (request.resources) {
      await this.resourceManager.validateResourceConfig(request.resources);
    }
  }

  private createExecutionRequests(benchmark: BenchmarkDefinition): TaskExecutionRequest[] {
    const requests: TaskExecutionRequest[] = [];

    for (const taskConfig of benchmark.taskConfigs) {
      for (const modelConfig of benchmark.modelConfigs) {
        requests.push({
          id: `${benchmark.id}_${taskConfig.taskId}_${modelConfig.modelId}_${Date.now()}`,
          taskId: taskConfig.taskId,
          providerId: modelConfig.providerId,
          modelId: modelConfig.modelId,
          config: { ...taskConfig.config, ...modelConfig.config },
          context: {
            benchmarkId: benchmark.id,
            organizationId: benchmark.organizationId,
            userId: benchmark.userId,
            environment: benchmark.environment || 'production',
            metadata: benchmark.metadata || {},
          },
          priority: benchmark.priority,
          timeout: benchmark.timeout,
          retryPolicy: benchmark.retryPolicy,
        });
      }
    }

    return requests;
  }

  private async executeWithResourceManagement(
    benchmark: BenchmarkDefinition,
    requests: TaskExecutionRequest[]
  ): Promise<BenchmarkResult> {
    // Reserve resources
    const resourceReservation = await this.resourceManager.reserveResources(
      benchmark.organizationId,
      requests.length,
      benchmark.timeout || 300000
    );

    try {
      // Execute in batches based on resource limits
      const maxConcurrent = resourceReservation.maxConcurrentExecutions;
      const results: TaskExecutionResult[] = [];

      for (let i = 0; i < requests.length; i += maxConcurrent) {
        const batch = requests.slice(i, i + maxConcurrent);

        // Update progress
        await this.updateBenchmarkProgress(benchmark.id, {
          totalExecutions: requests.length,
          completedExecutions: results.length,
          failedExecutions: results.filter((r) => r.status === ExecutionStatus.FAILED).length,
          runningExecutions: batch.length,
          percentage: Math.round((results.length / requests.length) * 100),
          currentPhase: `Executing batch ${Math.floor(i / maxConcurrent) + 1}`,
        });

        // Execute batch
        const batchResults = await this.taskExecutor.executeTaskBatch(batch);
        results.push(...batchResults);

        // Send progress notification if configured
        if (i % (maxConcurrent * 2) === 0) {
          // Every 2 batches
          await this.sendProgressNotification(benchmark, results.length, requests.length);
        }
      }

      // Create benchmark result
      return await this.createBenchmarkResult(benchmark, results);
    } finally {
      // Release resources
      await this.resourceManager.releaseResources(resourceReservation.id);
    }
  }

  private async createBenchmarkResult(
    benchmark: BenchmarkDefinition,
    executionResults: TaskExecutionResult[]
  ): Promise<BenchmarkResult> {
    const benchmarkId = benchmark.id;
    const startTime = new Date(Math.min(...executionResults.map((r) => r.startTime.getTime())));
    const endTime = new Date(Math.max(...executionResults.map((r) => r.endTime?.getTime() || 0)));

    // Group results by task and model
    const resultsByTask = this.groupResultsByTask(executionResults);
    const resultsByModel = this.groupResultsByModel(executionResults);

    // Calculate aggregate metrics
    const aggregateMetrics = this.calculateAggregateMetrics(executionResults);

    return {
      id: benchmarkId,
      benchmarkDefinition: benchmark,
      executionResults,
      resultsByTask,
      resultsByModel,
      aggregateMetrics,
      startTime,
      endTime,
      status: this.determineBenchmarkStatus(executionResults),
      metadata: {
        totalExecutions: executionResults.length,
        successfulExecutions: executionResults.filter((r) => r.status === ExecutionStatus.COMPLETED)
          .length,
        failedExecutions: executionResults.filter((r) => r.status === ExecutionStatus.FAILED)
          .length,
        averageDuration: aggregateMetrics.averageDuration,
        totalTokens: aggregateMetrics.totalTokens,
        estimatedCost: aggregateMetrics.estimatedCost,
      },
    };
  }

  private groupResultsByTask(
    results: TaskExecutionResult[]
  ): Record<string, TaskExecutionResult[]> {
    const grouped: Record<string, TaskExecutionResult[]> = {};

    for (const result of results) {
      const taskResults = grouped[result.taskId] || [];
      taskResults.push(result);
      grouped[result.taskId] = taskResults;
    }

    return grouped;
  }

  private groupResultsByModel(
    results: TaskExecutionResult[]
  ): Record<string, TaskExecutionResult[]> {
    const grouped: Record<string, TaskExecutionResult[]> = {};

    for (const result of results) {
      const modelKey = `${result.providerId}:${result.modelId}`;
      const modelResults = grouped[modelKey] || [];
      modelResults.push(result);
      grouped[modelKey] = modelResults;
    }

    return grouped;
  }

  private calculateAggregateMetrics(results: TaskExecutionResult[]): any {
    const successfulResults = results.filter((r) => r.status === ExecutionStatus.COMPLETED);

    if (successfulResults.length === 0) {
      return {
        averageDuration: 0,
        totalTokens: 0,
        averageTokensPerRequest: 0,
        successRate: 0,
        estimatedCost: 0,
      };
    }

    const totalDuration = successfulResults.reduce((sum, r) => sum + (r.duration || 0), 0);
    const totalTokens = successfulResults.reduce(
      (sum, r) => sum + (r.response?.tokenUsage?.totalTokens || 0),
      0
    );

    return {
      averageDuration: totalDuration / successfulResults.length,
      totalTokens,
      averageTokensPerRequest: totalTokens / successfulResults.length,
      successRate: successfulResults.length / results.length,
      estimatedCost: this.calculateEstimatedCost(totalTokens), // Simplified cost calculation
    };
  }

  private determineBenchmarkStatus(results: TaskExecutionResult[]): string {
    const hasFailures = results.some((r) => r.status === ExecutionStatus.FAILED);
    const hasRunning = results.some((r) => r.status === ExecutionStatus.RUNNING);
    const hasPending = results.some((r) => r.status === ExecutionStatus.PENDING);

    if (hasRunning || hasPending) return 'RUNNING';
    if (hasFailures) return 'COMPLETED_WITH_ERRORS';
    return 'COMPLETED';
  }

  private calculateEstimatedCost(tokens: number): number {
    // Simplified cost calculation - would be more sophisticated in reality
    const averageCostPerMillionTokens = 10; // $10 per 1M tokens
    return (tokens / 1000000) * averageCostPerMillionTokens;
  }

  private async updateBenchmarkStatus(
    benchmarkId: string,
    status: BenchmarkExecutionStatus
  ): Promise<void> {
    await this.benchmarkRepository.updateStatus(benchmarkId, status);
  }

  private async updateBenchmarkProgress(
    benchmarkId: string,
    progress: BenchmarkProgress
  ): Promise<void> {
    await this.benchmarkRepository.updateProgress(benchmarkId, progress);
  }

  private async getCurrentBenchmarkStatus(benchmarkId: string): Promise<BenchmarkExecutionStatus> {
    return await this.benchmarkRepository.getStatus(benchmarkId);
  }

  private async getRunningExecutions(benchmarkId: string): Promise<TaskExecutionResult[]> {
    return await this.benchmarkRepository.getRunningExecutions(benchmarkId);
  }

  private async getCompletedExecutionsCount(benchmarkId: string): Promise<number> {
    return await this.benchmarkRepository.getExecutionsCount(
      benchmarkId,
      ExecutionStatus.COMPLETED
    );
  }

  private async getFailedExecutionsCount(benchmarkId: string): Promise<number> {
    return await this.benchmarkRepository.getExecutionsCount(benchmarkId, ExecutionStatus.FAILED);
  }

  private async getLastRunTime(benchmarkId: string): Promise<Date | undefined> {
    return await this.benchmarkRepository.getLastRunTime(benchmarkId);
  }

  private calculateEstimatedCompletion(
    benchmarkId: string,
    completedExecutions: number,
    totalExecutions: number
  ): Date | undefined {
    if (completedExecutions === 0) return undefined;

    const averageExecutionTime = 30000; // 30 seconds average
    const remainingExecutions = totalExecutions - completedExecutions;
    const estimatedRemainingTime = remainingExecutions * averageExecutionTime;

    return new Date(Date.now() + estimatedRemainingTime);
  }

  private async calculateBenchmarkMetrics(benchmarkId: string): Promise<BenchmarkMetrics> {
    const executions = await this.benchmarkRepository.getAllExecutions(benchmarkId);

    if (executions.length === 0) {
      return {};
    }

    const startTime = new Date(Math.min(...executions.map((r) => r.startTime.getTime())));
    const endTime = new Date(Math.max(...executions.map((r) => r.endTime?.getTime() || 0)));
    const duration = endTime.getTime() - startTime.getTime();

    const totalCost = executions.reduce((sum, r) => sum + (r.metadata?.cost || 0), 0);

    const totalTokens = executions.reduce(
      (sum, r) => sum + (r.response?.tokenUsage?.totalTokens || 0),
      0
    );

    const successfulExecutions = executions.filter((r) => r.status === ExecutionStatus.COMPLETED);
    const averageScore =
      successfulExecutions.length > 0
        ? successfulExecutions.reduce((sum, r) => sum + (r.metadata?.score || 0), 0) /
          successfulExecutions.length
        : 0;

    const successRate = successfulExecutions.length / executions.length;

    return {
      startTime,
      endTime,
      duration,
      totalCost,
      totalTokens,
      averageScore,
      successRate,
    };
  }

  private async sendProgressNotification(
    benchmark: BenchmarkDefinition,
    completed: number,
    total: number
  ): Promise<void> {
    const percentage = Math.round((completed / total) * 100);

    await this.notificationService.sendNotification({
      type: 'benchmark_progress',
      benchmarkId: benchmark.id,
      organizationId: benchmark.organizationId,
      userId: benchmark.userId,
      message: `Benchmark "${benchmark.name}" progress: ${completed}/${total} (${percentage}%)`,
      data: { completed, total, percentage },
    });
  }

  private async cancelRunningExecutions(benchmarkId: string): Promise<void> {
    const runningExecutions = await this.getRunningExecutions(benchmarkId);

    for (const execution of runningExecutions) {
      await this.taskExecutor.cancelExecution(execution.id);
    }
  }

  private getDefaultRetryPolicy(): RetryPolicy {
    return {
      maxAttempts: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      retryableErrors: ['RATE_LIMIT', 'TIMEOUT', 'NETWORK_ERROR'],
    };
  }
}

interface BenchmarkRepository {
  create(benchmark: BenchmarkDefinition): Promise<void>;
  get(benchmarkId: string): Promise<BenchmarkDefinition | null>;
  updateStatus(benchmarkId: string, status: BenchmarkExecutionStatus): Promise<void>;
  updateProgress(benchmarkId: string, progress: BenchmarkProgress): Promise<void>;
  createSchedule(schedule: any): Promise<void>;
  getSchedule(benchmarkId: string): Promise<any>;
  list(filter: BenchmarkFilter): Promise<BenchmarkList>;
  getStatus(benchmarkId: string): Promise<BenchmarkExecutionStatus>;
  getRunningExecutions(benchmarkId: string): Promise<TaskExecutionResult[]>;
  getAllExecutions(benchmarkId: string): Promise<TaskExecutionResult[]>;
  getExecutionsCount(benchmarkId: string, status: ExecutionStatus): Promise<number>;
  getLastRunTime(benchmarkId: string): Promise<Date | undefined>;
}

interface TaskScheduler {
  scheduleImmediate(benchmarkId: string): Promise<void>;
  scheduleAt(benchmarkId: string, startTime: Date): Promise<void>;
  scheduleRecurring(benchmarkId: string, schedule: ScheduleConfig): Promise<void>;
  pauseBenchmark(benchmarkId: string): Promise<void>;
  resumeBenchmark(benchmarkId: string): Promise<void>;
  cancelBenchmark(benchmarkId: string): Promise<void>;
}

interface ResourceManager {
  checkResources(benchmark: BenchmarkDefinition): Promise<void>;
  validateResourceConfig(config: ResourceConfig): Promise<void>;
  reserveResources(
    organizationId: string,
    executionCount: number,
    timeout: number
  ): Promise<ResourceReservation>;
  releaseResources(reservationId: string): Promise<void>;
}

interface ResourceReservation {
  id: string;
  maxConcurrentExecutions: number;
}

interface NotificationService {
  sendNotification(notification: NotificationRequest): Promise<void>;
}

interface NotificationRequest {
  type: string;
  benchmarkId: string;
  organizationId: string;
  userId: string;
  message: string;
  data?: Record<string, any>;
}
```

#### 5. Agent Customization Benchmarking Suite (Story 4.5)

**Purpose:** Measure performance impact of agent customizations and provide optimization recommendations for autonomous development tasks.

**Core Components:**

- **Agent Performance Framework:** Baseline vs custom configuration comparison system
- **Cross-Context Testing:** Multi-scenario agent capability evaluation (development, code review, testing)
- **Performance Impact Analysis:** Measurement across speed, quality, cost, and context utilization
- **Optimization Engine:** Automated recommendations based on benchmark results
- **A/B Testing Framework:** Comparative analysis of agent customizations
- **Historical Tracking:** Performance trend analysis over time
- **Privacy-Preserving Sharing:** Anonymized benchmark result sharing

**Technical Implementation:**

```typescript
interface AgentBenchmarkConfig {
  baselineConfig: AgentConfiguration;
  customConfig: AgentConfiguration;
  testScenarios: ('development' | 'code-review' | 'testing')[];
  performanceMetrics: PerformanceMetric[];
}

interface PerformanceImpactResult {
  configId: string;
  scenario: string;
  baselineScore: number;
  customScore: number;
  impactPercentage: number;
  recommendations: OptimizationRecommendation[];
  costImpact: CostAnalysis;
  speedImpact: PerformanceAnalysis;
  qualityImpact: QualityAnalysis;
}
```

**Key Features:**

- Multi-dimensional performance analysis (speed, quality, cost, context utilization)
- Cross-scenario agent capability testing with consistent evaluation criteria
- Automated optimization recommendations based on performance patterns
- Integration with Tamma's agent configuration system for applying optimizations
- Privacy-preserving benchmark result sharing with Test Platform users
- Historical performance tracking with trend analysis and anomaly detection

#### 6. Cross-Platform Intelligence Engine (Story 4.6)

**Purpose:** Aggregate collective intelligence across all AI providers and customizations to provide best practice discovery and informed decision-making.

**Core Components:**

- **Data Aggregation System:** Cross-platform performance data collection with privacy controls
- **Best Practice Discovery:** Pattern recognition algorithms for effective instruction patterns
- **Community Knowledge Base:** Anonymized optimization insights with user consent
- **Provider-Specific Recommendations:** Context-aware provider selection guidance
- **Real-Time Insights:** Continuous updates as new benchmark data becomes available
- **Competitive Intelligence:** Relative provider performance analysis
- **External API:** Interface for external systems to consume intelligence insights

**Technical Implementation:**

```typescript
interface CrossPlatformIntelligence {
  aggregatedData: ProviderPerformanceData[];
  bestPractices: BestPracticePattern[];
  communityInsights: CommunityInsight[];
  providerRecommendations: ProviderRecommendation[];
  competitiveAnalysis: CompetitiveIntelligence;
}

interface BestPracticePattern {
  patternId: string;
  category: 'prompt-engineering' | 'context-management' | 'task-structuring';
  effectiveness: number;
  applicableProviders: string[];
  usageFrequency: number;
  communityValidation: number;
}
```

**Key Features:**

- Privacy-preserving data aggregation with user consent controls
- Best practice discovery using machine learning pattern recognition
- Community knowledge base with anonymized optimization insights
- Provider-specific recommendation engine based on aggregated performance data
- Real-time insight updates as new benchmark results become available
- Competitive intelligence showing relative provider strengths and weaknesses
- API for external systems to consume intelligence insights

#### 7. User Benchmarking Dashboard (Story 4.7)

**Purpose:** Provide comprehensive interactive dashboard for running custom instruction benchmarks and viewing comparative results.

**Core Components:**

- **Interactive Dashboard:** Modern responsive UI for creating and running custom benchmarks
- **Real-Time Execution:** Live benchmark progress tracking with result streaming
- **Comparative Analysis:** Baseline vs custom instruction performance comparison
- **Provider Comparison:** Side-by-side performance metrics and visualization
- **Custom Instruction Editor:** Syntax highlighting, validation, and template library
- **Historical Tracking:** Benchmark history with trend analysis and performance insights
- **Export Capabilities:** Multiple format support for sharing results and insights

**Technical Implementation:**

```typescript
interface UserBenchmarkDashboard {
  benchmarkCreation: BenchmarkBuilder;
  liveExecution: ExecutionMonitor;
  comparativeAnalysis: ResultComparison;
  providerComparison: ProviderComparison;
  instructionEditor: CodeEditor;
  historicalTracking: BenchmarkHistory;
  exportTools: ExportManager;
}

interface CustomBenchmark {
  id: string;
  name: string;
  description: string;
  customInstructions: InstructionTemplate[];
  baselineInstructions?: InstructionTemplate[];
  providers: string[];
  status: 'draft' | 'running' | 'completed' | 'failed';
  results: BenchmarkResult[];
}
```

**Key Features:**

- Interactive dashboard for creating and running custom instruction benchmarks
- Real-time benchmark execution with progress tracking and live results
- Comparative analysis showing baseline vs custom instruction performance
- Provider comparison tools with side-by-side performance metrics
- Custom instruction editor with syntax highlighting and validation
- Historical benchmark tracking with trend analysis and performance insights
- Export capabilities for sharing benchmark results and insights
- Integration with cross-platform intelligence for optimization recommendations

## Technology Stack

### Core Technologies

- **Execution Engine:** Node.js with TypeScript for high-performance async execution
- **Queue System:** Redis Bull Queue for job scheduling and management
- **Database:** PostgreSQL for structured data, TimescaleDB for time-series
- **Object Storage:** S3-compatible storage for large result objects
- **Caching:** Redis for performance optimization

### AI/ML Technologies

- **Text Similarity:** Cosine similarity, Jaccard similarity, Levenshtein distance
- **Semantic Search:** Sentence transformers for embedding-based similarity
- **LLM Evaluation:** GPT-4/Claude for automated scoring
- **Custom Functions:** JavaScript/TypeScript execution sandbox for custom scoring

### Development Tools

- **Language:** TypeScript 5.7+ (strict mode)
- **Testing:** Vitest with comprehensive test coverage
- **Monitoring:** Custom metrics and performance tracking
- **Documentation:** OpenAPI specifications for all endpoints

## Data Models

### Core Entities

```typescript
interface TaskExecutionResult {
  id: string;
  taskId: string;
  providerId: string;
  modelId: string;
  status: ExecutionStatus;
  startTime: Date;
  endTime?: Date;
  duration?: number;
  response?: ModelResponse;
  error?: ExecutionError;
  metrics: ExecutionMetrics;
  context: ExecutionContext;
  metadata: Record<string, any>;
}

interface ExecutionScore {
  id: string;
  executionId: string;
  taskId: string;
  overallScore: number;
  criterionScores: CriterionScore[];
  confidence: number;
  feedback: ScoreFeedback;
  metadata: Record<string, any>;
  scoredAt: Date;
  scoredBy: string;
}

interface BenchmarkResult {
  id: string;
  benchmarkDefinition: BenchmarkDefinition;
  executionResults: TaskExecutionResult[];
  resultsByTask: Record<string, TaskExecutionResult[]>;
  resultsByModel: Record<string, TaskExecutionResult[]>;
  aggregateMetrics: any;
  startTime: Date;
  endTime: Date;
  status: string;
  metadata: Record<string, any>;
}

interface BenchmarkDefinition {
  id: string;
  name: string;
  description: string;
  organizationId: string;
  userId: string;
  taskConfigs: TaskConfig[];
  modelConfigs: ModelConfig[];
  priority?: ExecutionPriority;
  timeout?: number;
  retryPolicy?: RetryPolicy;
  environment?: string;
  metadata?: Record<string, any>;
}
```

## API Specifications

### Execution Endpoints

```yaml
/executions:
  post:
    summary: Execute task
    security:
      - bearerAuth: []
    requestBody:
      required: true
      content:
        application/json:
          schema:
            $ref: '#/components/schemas/TaskExecutionRequest'
    responses:
      202:
        description: Execution accepted
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/TaskExecutionResult'

/executions/{executionId}:
  get:
    summary: Get execution result
    security:
      - bearerAuth: []
    parameters:
      - name: executionId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Execution result retrieved
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/TaskExecutionResult'

  delete:
    summary: Cancel execution
    security:
      - bearerAuth: []
    parameters:
      - name: executionId
        in: path
        required: true
        schema:
          type: string
    responses:
      204:
        description: Execution cancelled

/executions/{executionId}/score:
  post:
    summary: Score execution
    security:
      - bearerAuth: []
    parameters:
      - name: executionId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Execution scored
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/ExecutionScore'
```

### Benchmark Endpoints

```yaml
/benchmarks:
  get:
    summary: List benchmarks
    security:
      - bearerAuth: []
    parameters:
      - name: organizationId
        in: query
        schema:
          type: string
      - name: status
        in: query
        schema:
          type: array
          items:
            type: string
      - name: limit
        in: query
        schema:
          type: integer
          default: 20
    responses:
      200:
        description: Benchmarks retrieved
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/BenchmarkList'

  post:
    summary: Create benchmark
    security:
      - bearerAuth: []
    requestBody:
      required: true
      content:
        application/json:
          schema:
            $ref: '#/components/schemas/CreateBenchmarkRequest'
    responses:
      201:
        description: Benchmark created
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/BenchmarkDefinition'

/benchmarks/{benchmarkId}:
  get:
    summary: Get benchmark details
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Benchmark retrieved
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/BenchmarkDefinition'

/benchmarks/{benchmarkId}/execute:
  post:
    summary: Execute benchmark
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      202:
        description: Benchmark execution started
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/BenchmarkStatus'

/benchmarks/{benchmarkId}/status:
  get:
    summary: Get benchmark status
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Benchmark status retrieved
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/BenchmarkStatus'

/benchmarks/{benchmarkId}/pause:
  post:
    summary: Pause benchmark
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Benchmark paused

/benchmarks/{benchmarkId}/resume:
  post:
    summary: Resume benchmark
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Benchmark resumed

/benchmarks/{benchmarkId}/cancel:
  post:
    summary: Cancel benchmark
    security:
      - bearerAuth: []
    parameters:
      - name: benchmarkId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Benchmark cancelled
```

## Performance Requirements

### Execution Performance

- **Task Execution:** 95th percentile < 30 seconds for standard tasks
- **Batch Execution:** 1000 tasks in < 10 minutes
- **Benchmark Execution:** 100 task-model combinations in < 30 minutes
- **Queue Processing:** 95th percentile < 100ms queue time

### Scoring Performance

- **Automatic Scoring:** 95th percentile < 5 seconds per execution
- **Batch Scoring:** 1000 executions in < 15 minutes
- **LLM Evaluation:** 95th percentile < 10 seconds per evaluation
- **Custom Functions:** 95th percentile < 2 seconds per execution

### Storage Performance

- **Execution Storage:** 95th percentile < 500ms write time
- **Query Performance:** 95th percentile < 1 second for complex queries
- **Time-Series Queries:** 95th percentile < 2 seconds for 30-day ranges
- **Export Operations:** 10,000 results exported in < 2 minutes

## Testing Strategy

### Unit Tests

- **Task Executor Tests:** Execution logic, error handling, retry mechanisms
- **Scoring Engine Tests:** All scoring methods and criteria evaluation
- **Result Storage Tests:** Data storage, retrieval, and query operations
- **Orchestrator Tests:** Benchmark creation, scheduling, and execution

### Integration Tests

- **End-to-End Execution:** Complete task execution workflow
- **Scoring Integration:** Scoring engine with execution results
- **Storage Integration:** Result storage with time-series data
- **Orchestration Integration:** Complete benchmark execution workflow

### Performance Tests

- **Concurrent Execution:** 1000 concurrent task executions
- **Large Benchmarks:** 10,000 task-model combinations
- **Scoring Throughput:** 5000 executions scored per hour
- **Storage Load:** High-volume result storage and querying

### Load Tests

- **System Load:** Simulate peak usage patterns
- **Resource Limits:** Test resource management and limits
- **Error Recovery:** Test behavior under failure conditions
- **Data Consistency:** Ensure data integrity under load

## Security Considerations

### Execution Security

- **Code Execution:** Sandboxed execution for custom scoring functions
- **Input Validation:** Validate all execution inputs and parameters
- **Resource Limits:** Enforce CPU, memory, and time limits
- **Access Control:** Role-based access to execution features

### Data Protection

- **Result Encryption:** Encrypt sensitive execution results
- **Audit Logging:** Complete audit trail for all executions
- **Data Retention:** Configurable retention policies for execution data
- **Privacy Protection:** Sanitize personal data from results

### Infrastructure Security

- **Network Isolation:** Isolate execution environments
- **Secrets Management:** Secure storage of API keys and credentials
- **Monitoring:** Security event logging and alerting
- **Compliance:** Ensure compliance with data protection regulations

## Monitoring and Observability

### Execution Metrics

- **Performance Metrics:** Latency, throughput, error rates
- **Resource Metrics:** CPU, memory, network usage
- **Business Metrics:** Execution counts, success rates, costs
- **Quality Metrics:** Score distributions, scoring accuracy

### System Health

- **Service Health:** Health checks for all components
- **Database Health:** Connection pools, query performance
- **Queue Health:** Queue depth, processing rates
- **External Dependencies:** Provider API health and performance

### Alerting

- **Performance Alerts:** Degraded performance or high latency
- **Error Alerts:** Increased error rates or system failures
- **Resource Alerts:** Resource exhaustion or limits reached
- **Business Alerts:** Unusual usage patterns or cost overruns

---

**Next Steps:**

1. Review and approve this technical specification
2. Set up development environment with required dependencies
3. Begin Story 4.1 implementation (Task Execution Engine)
4. Create comprehensive test suites for all components
5. Establish performance benchmarks and monitoring
