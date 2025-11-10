# Story 1.8 Task 2: Define Worker Mode Architecture

## Task Overview

Define comprehensive architecture for worker mode, including responsibilities, execution engine, orchestrator communication protocols, local resource management, and startup/shutdown sequences. This task establishes the foundation for the stateless execution component of Tamma's hybrid architecture.

## Acceptance Criteria

### 2.1 Document Worker Responsibilities and Scope

- [ ] Define core worker responsibilities (CI/CD integration, single-task execution, exit codes)
- [ ] Document worker scope and boundaries
- [ ] Define worker interaction patterns with orchestrator
- [ ] Specify worker ownership and resource management responsibilities
- [ ] Document worker constraints and limitations

### 2.2 Design Worker Execution Engine and Task Processing

- [ ] Design task execution engine with pluggable task handlers
- [ ] Define task processing pipeline and workflow
- [ ] Design resource isolation and sandboxing mechanisms
- [ ] Define error handling and recovery procedures
- [ ] Document task timeout and cancellation mechanisms

### 2.3 Define Orchestrator Communication Protocols

- [ ] Design worker registration and discovery protocol
- [ ] Define heartbeat and health check communication
- [ ] Design task polling and assignment protocol
- [ ] Define progress reporting and result submission
- [ ] Document error reporting and failure notification

### 2.4 Design Local Resource Management and Isolation

- [ ] Define local filesystem access and sandboxing
- [ ] Design resource usage monitoring and limits
- [ ] Define temporary resource cleanup procedures
- [ ] Design security isolation and privilege separation
- [ ] Document resource allocation and deallocation

### 2.5 Document Worker Startup and Shutdown Sequences

- [ ] Define worker startup sequence with orchestrator registration
- [ ] Document initialization order and capability detection
- [ ] Design graceful shutdown sequence with task completion
- [ ] Define shutdown timeout and force termination procedures
- [ ] Document startup/shutdown error handling and recovery

## Implementation Details

### 2.1 Worker Responsibilities and Scope

```markdown
# Worker Mode Responsibilities and Scope

## Core Responsibilities

### 1. CI/CD Integration

- **Pipeline Integration**: Integrate with CI/CD pipelines (GitHub Actions, GitLab CI, Jenkins)
- **Build Execution**: Execute build commands and compilation processes
- **Test Running**: Run unit tests, integration tests, and end-to-end tests
- **Security Scanning**: Perform security scans and vulnerability assessments
- **Artifact Management**: Handle build artifacts and test results

### 2. Single-Task Execution

- **Task Processing**: Execute individual tasks assigned by orchestrator
- **Resource Isolation**: Execute tasks in isolated environments
- **Progress Reporting**: Report real-time progress to orchestrator
- **Result Collection**: Collect and format task results
- **Exit Code Management**: Return appropriate exit codes for task outcomes

### 3. Local Development Support

- **Local Execution**: Support local development and testing workflows
- **Standalone Mode**: Operate without orchestrator for local tasks
- **Configuration Management**: Handle local configuration and settings
- **Debug Support**: Provide debugging and troubleshooting capabilities
- **Logging Integration**: Integrate with local logging infrastructure

### 4. Resource Management

- **Resource Allocation**: Manage CPU, memory, and disk resources
- **Process Isolation**: Isolate task execution processes
- **Cleanup Operations**: Clean up temporary files and resources
- **Monitoring**: Monitor resource usage and performance
- **Optimization**: Optimize resource utilization for task execution

## Scope and Boundaries

### In Scope

- Task execution in isolated environments
- CI/CD pipeline integration
- Local filesystem operations
- Build and test execution
- Security scanning and vulnerability assessment
- Resource management and monitoring
- Progress reporting and result collection
- Error handling and recovery

### Out of Scope

- Task scheduling and prioritization (orchestrator responsibility)
- Workflow coordination and state management (orchestrator responsibility)
- Multi-worker coordination (orchestrator responsibility)
- User interface and dashboard (separate concern)
- Authentication and authorization (shared infrastructure)
- Persistent data storage (orchestrator responsibility)

## Interaction Patterns

### Orchestrator Communication

- **Registration**: Register with orchestrator on startup
- **Heartbeat**: Send periodic health status updates
- **Task Polling**: Request tasks from orchestrator
- **Progress Reporting**: Report task execution progress
- **Result Submission**: Submit task completion results
- **Error Reporting**: Report errors and failures

### External System Interactions

- **Git Platforms**: Clone repositories, create commits, push changes
- **Build Tools**: Execute build commands and compilation
- **Test Frameworks**: Run tests and collect results
- **Security Tools**: Execute security scans and assessments
- **Artifact Storage**: Store and retrieve build artifacts

### Local Operations

- **Filesystem**: Read/write files within task workspace
- **Process Management**: Start, monitor, and terminate processes
- **Network**: Make HTTP requests and API calls
- **Environment**: Manage environment variables and configuration
- **Logging**: Write structured logs for debugging and monitoring

## Constraints and Limitations

### Performance Constraints

- Task timeout: 30 minutes (configurable)
- Memory usage: 4GB maximum (configurable)
- CPU usage: 2 cores maximum (configurable)
- Disk usage: 10GB temporary space (configurable)
- Network bandwidth: 1Gbps (configurable)

### Security Constraints

- No privileged operations without explicit authorization
- Filesystem access limited to task workspace
- Network access restricted to approved endpoints
- Process isolation mandatory for all tasks
- Audit logging mandatory for all operations

### Resource Constraints

- Single task execution at a time
- No persistent local storage
- Limited local caching capabilities
- Resource cleanup mandatory after each task
- No background services or daemons
```

### 2.2 Worker Execution Engine and Task Processing

```typescript
// packages/worker/src/types/worker.ts

export interface IWorkerConfig {
  orchestrator: {
    url: string;
    registrationEndpoint: string;
    taskPollingEndpoint: string;
    heartbeatEndpoint: string;
    resultSubmissionEndpoint: string;
  };
  execution: {
    workspaceRoot: string;
    tempDir: string;
    maxExecutionTime: number;
    maxMemoryUsage: number;
    maxCpuUsage: number;
    enableSandbox: boolean;
  };
  capabilities: {
    supportedTaskTypes: string[];
    maxConcurrentTasks: number;
    supportedPlatforms: string[];
    supportedProviders: string[];
  };
  logging: {
    level: string;
    format: string;
    output: string[];
  };
  monitoring: {
    metricsEnabled: boolean;
    healthCheckInterval: number;
    resourceMonitoringInterval: number;
  };
}

export interface IWorkerServices {
  taskExecutor: ITaskExecutor;
  resourceManager: IResourceManager;
  orchestratorClient: IOrchestratorClient;
  logger: ILogger;
  metrics: IMetrics;
  healthChecker: IHealthChecker;
}

export interface IWorker {
  initialize(config: IWorkerConfig): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
  getCapabilities(): WorkerCapabilities;
  healthCheck(): Promise<HealthStatus>;
}

export interface ITaskExecutor {
  executeTask(task: Task): Promise<TaskResult>;
  cancelTask(taskId: string): Promise<void>;
  getTaskStatus(taskId: string): Promise<TaskStatus>;
  getSupportedTaskTypes(): string[];
}

export interface IResourceManager {
  allocateResources(task: Task): Promise<ResourceAllocation>;
  releaseResources(allocation: ResourceAllocation): Promise<void>;
  monitorUsage(allocation: ResourceAllocation): Promise<ResourceUsage>;
  enforceLimits(allocation: ResourceAllocation): Promise<void>;
  cleanupWorkspace(taskId: string): Promise<void>;
}
```

```typescript
// packages/worker/src/worker.ts

export class Worker implements IWorker {
  private config: IWorkerConfig;
  private services: IWorkerServices;
  private isRunning: boolean = false;
  private currentTask: Task | null = null;
  private workerId: string;

  constructor(
    private taskExecutor: ITaskExecutor,
    private resourceManager: IResourceManager,
    private orchestratorClient: IOrchestratorClient,
    private logger: ILogger,
    private metrics: IMetrics,
    private healthChecker: IHealthChecker
  ) {
    this.workerId = generateUUID();
  }

  async initialize(config: IWorkerConfig): Promise<void> {
    this.config = config;

    // Initialize services
    await this.taskExecutor.initialize(config.execution);
    await this.resourceManager.initialize(config.execution);
    await this.orchestratorClient.initialize(config.orchestrator);
    await this.logger.initialize(config.logging);
    await this.metrics.initialize(config.monitoring);
    await this.healthChecker.initialize(config.monitoring);

    // Setup workspace directories
    await this.setupWorkspaceDirectories();

    this.logger.info('Worker initialized', { workerId: this.workerId });
  }

  async start(): Promise<void> {
    if (this.isRunning) {
      throw new Error('Worker is already running');
    }

    try {
      // Register with orchestrator
      await this.registerWithOrchestrator();

      // Start background services
      await this.startBackgroundServices();

      this.isRunning = true;
      this.logger.info('Worker started successfully', { workerId: this.workerId });

      // Start main task processing loop
      await this.startTaskProcessingLoop();
    } catch (error) {
      this.logger.error('Failed to start worker', { error, workerId: this.workerId });
      throw error;
    }
  }

  async stop(): Promise<void> {
    if (!this.isRunning) {
      return;
    }

    this.logger.info('Stopping worker...', { workerId: this.workerId });

    try {
      // Stop accepting new tasks
      await this.stopTaskProcessing();

      // Wait for current task to complete or timeout
      await this.waitForCurrentTaskCompletion();

      // Stop background services
      await this.stopBackgroundServices();

      // Unregister from orchestrator
      await this.unregisterFromOrchestrator();

      // Cleanup resources
      await this.cleanupResources();

      this.isRunning = false;
      this.logger.info('Worker stopped successfully', { workerId: this.workerId });
    } catch (error) {
      this.logger.error('Error during worker shutdown', { error, workerId: this.workerId });
      throw error;
    }
  }

  getCapabilities(): WorkerCapabilities {
    return {
      workerId: this.workerId,
      supportedTaskTypes: this.config.capabilities.supportedTaskTypes,
      maxConcurrentTasks: this.config.capabilities.maxConcurrentTasks,
      supportedPlatforms: this.config.capabilities.supportedPlatforms,
      supportedProviders: this.config.capabilities.supportedProviders,
      resources: {
        maxMemory: this.config.execution.maxMemoryUsage,
        maxCpu: this.config.execution.maxCpuUsage,
        maxDiskSpace: 10 * 1024 * 1024 * 1024, // 10GB
        supportedArchitectures: ['x64', 'arm64'],
      },
      version: '1.0.0',
      startTime: new Date().toISOString(),
    };
  }

  async healthCheck(): Promise<HealthStatus> {
    const checks = await Promise.allSettled([
      this.taskExecutor.healthCheck(),
      this.resourceManager.healthCheck(),
      this.orchestratorClient.healthCheck(),
      this.healthChecker.checkSystemHealth(),
    ]);

    const results = checks.map((check, index) => {
      const name = ['taskExecutor', 'resourceManager', 'orchestratorClient', 'system'][index];
      return {
        name,
        status: check.status === 'fulfilled' && check.value.healthy ? 'healthy' : 'unhealthy',
        details: check.status === 'fulfilled' ? check.value : { error: check.reason },
      };
    });

    const overallStatus = results.every((r) => r.status === 'healthy') ? 'healthy' : 'unhealthy';

    return {
      status: overallStatus,
      timestamp: new Date().toISOString(),
      uptime: process.uptime(),
      version: '1.0.0',
      workerId: this.workerId,
      currentTask: this.currentTask?.id || null,
      checks: results,
    };
  }

  private async registerWithOrchestrator(): Promise<void> {
    const capabilities = this.getCapabilities();

    await this.orchestratorClient.registerWorker(capabilities);

    this.logger.info('Registered with orchestrator', {
      workerId: this.workerId,
      capabilities,
    });
  }

  private async startTaskProcessingLoop(): Promise<void> {
    while (this.isRunning) {
      try {
        // Poll for tasks
        const task = await this.orchestratorClient.pollForTask(this.workerId);

        if (task) {
          await this.executeTask(task);
        } else {
          // No task available, wait before next poll
          await new Promise((resolve) => setTimeout(resolve, 5000));
        }
      } catch (error) {
        this.logger.error('Error in task processing loop', { error, workerId: this.workerId });
        await new Promise((resolve) => setTimeout(resolve, 10000)); // Wait longer on error
      }
    }
  }

  private async executeTask(task: Task): Promise<void> {
    this.currentTask = task;

    this.logger.info('Executing task', {
      taskId: task.id,
      type: task.type,
      workerId: this.workerId,
    });

    try {
      // Allocate resources for task
      const allocation = await this.resourceManager.allocateResources(task);

      // Report task started
      await this.orchestratorClient.reportTaskStarted(task.id, this.workerId);

      // Execute task
      const result = await this.taskExecutor.executeTask(task);

      // Report task completed
      await this.orchestratorClient.reportTaskCompleted(task.id, result);

      this.logger.info('Task completed successfully', {
        taskId: task.id,
        duration: result.duration,
        workerId: this.workerId,
      });
    } catch (error) {
      this.logger.error('Task execution failed', {
        taskId: task.id,
        error: error.message,
        workerId: this.workerId,
      });

      // Report task failed
      await this.orchestratorClient.reportTaskFailed(task.id, error);
    } finally {
      // Cleanup resources
      await this.resourceManager.cleanupWorkspace(task.id);
      this.currentTask = null;
    }
  }

  private async setupWorkspaceDirectories(): Promise<void> {
    const fs = require('fs').promises;

    // Create workspace root
    await fs.mkdir(this.config.execution.workspaceRoot, { recursive: true });

    // Create temp directory
    await fs.mkdir(this.config.execution.tempDir, { recursive: true });

    this.logger.info('Workspace directories created', {
      workspaceRoot: this.config.execution.workspaceRoot,
      tempDir: this.config.execution.tempDir,
    });
  }

  private async startBackgroundServices(): Promise<void> {
    // Start heartbeat service
    this.startHeartbeatService();

    // Start metrics collection
    await this.metrics.startCollection();

    // Start health monitoring
    await this.healthChecker.startMonitoring();
  }

  private startHeartbeatService(): void {
    setInterval(async () => {
      if (this.isRunning) {
        try {
          const health = await this.healthCheck();
          await this.orchestratorClient.sendHeartbeat(this.workerId, health);
        } catch (error) {
          this.logger.error('Failed to send heartbeat', { error, workerId: this.workerId });
        }
      }
    }, 30000); // Every 30 seconds
  }

  private async waitForCurrentTaskCompletion(): Promise<void> {
    if (!this.currentTask) {
      return;
    }

    this.logger.info('Waiting for current task to complete...', {
      taskId: this.currentTask.id,
    });

    const startTime = Date.now();
    const maxWaitTime = this.config.execution.maxExecutionTime;

    while (this.currentTask && Date.now() - startTime < maxWaitTime) {
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    if (this.currentTask) {
      this.logger.warn('Force terminating current task', {
        taskId: this.currentTask.id,
      });

      await this.taskExecutor.cancelTask(this.currentTask.id);
    }
  }

  private async stopBackgroundServices(): Promise<void> {
    await this.metrics.stopCollection();
    await this.healthChecker.stopMonitoring();
  }

  private async cleanupResources(): Promise<void> {
    await this.resourceManager.cleanupAll();
  }

  private async unregisterFromOrchestrator(): Promise<void> {
    try {
      await this.orchestratorClient.unregisterWorker(this.workerId);
      this.logger.info('Unregistered from orchestrator', { workerId: this.workerId });
    } catch (error) {
      this.logger.error('Failed to unregister from orchestrator', {
        error,
        workerId: this.workerId,
      });
    }
  }
}
```

### 2.3 Orchestrator Communication Protocols

```typescript
// packages/worker/src/orchestrator-client/types.ts

export interface IOrchestratorClient {
  initialize(config: OrchestratorConfig): Promise<void>;
  registerWorker(capabilities: WorkerCapabilities): Promise<void>;
  unregisterWorker(workerId: string): Promise<void>;
  pollForTask(workerId: string): Promise<Task | null>;
  reportTaskStarted(taskId: string, workerId: string): Promise<void>;
  reportTaskProgress(taskId: string, progress: TaskProgress): Promise<void>;
  reportTaskCompleted(taskId: string, result: TaskResult): Promise<void>;
  reportTaskFailed(taskId: string, error: Error): Promise<void>;
  sendHeartbeat(workerId: string, health: HealthStatus): Promise<void>;
  healthCheck(): Promise<HealthCheckResult>;
}

export interface OrchestratorConfig {
  url: string;
  registrationEndpoint: string;
  taskPollingEndpoint: string;
  heartbeatEndpoint: string;
  resultSubmissionEndpoint: string;
  timeout: number;
  retryAttempts: number;
  retryDelay: number;
}

export interface WorkerRegistration {
  workerId: string;
  capabilities: WorkerCapabilities;
  registeredAt: string;
  lastHeartbeat: string;
}

export interface TaskAssignment {
  taskId: string;
  workerId: string;
  assignedAt: string;
  startedAt?: string;
  completedAt?: string;
}
```

```typescript
// packages/worker/src/orchestrator-client/orchestrator-client.ts

export class OrchestratorClient implements IOrchestratorClient {
  private config: OrchestratorConfig;
  private httpClient: HttpClient;
  private logger: ILogger;
  private workerId: string;

  constructor(httpClient: HttpClient, logger: ILogger) {
    this.httpClient = httpClient;
    this.logger = logger;
  }

  async initialize(config: OrchestratorConfig): Promise<void> {
    this.config = config;

    // Configure HTTP client
    this.httpClient.setBaseUrl(config.url);
    this.httpClient.setTimeout(config.timeout);
    this.httpClient.setRetryConfig({
      attempts: config.retryAttempts,
      delay: config.retryDelay,
    });

    this.logger.info('Orchestrator client initialized', { url: config.url });
  }

  async registerWorker(capabilities: WorkerCapabilities): Promise<void> {
    const url = `${this.config.url}${this.config.registrationEndpoint}`;

    try {
      const response = await this.httpClient.post(url, {
        workerId: capabilities.workerId,
        capabilities,
        registeredAt: new Date().toISOString(),
      });

      if (response.status !== 200) {
        throw new Error(`Registration failed with status ${response.status}`);
      }

      this.workerId = capabilities.workerId;
      this.logger.info('Worker registered successfully', {
        workerId: this.workerId,
        capabilities,
      });
    } catch (error) {
      this.logger.error('Worker registration failed', { error, capabilities });
      throw error;
    }
  }

  async unregisterWorker(workerId: string): Promise<void> {
    const url = `${this.config.url}${this.config.registrationEndpoint}/${workerId}`;

    try {
      await this.httpClient.delete(url);

      this.logger.info('Worker unregistered successfully', { workerId });
    } catch (error) {
      this.logger.error('Worker unregistration failed', { error, workerId });
      throw error;
    }
  }

  async pollForTask(workerId: string): Promise<Task | null> {
    const url = `${this.config.url}${this.config.taskPollingEndpoint}`;

    try {
      const response = await this.httpClient.get(url, {
        params: { workerId },
      });

      if (response.status === 204) {
        return null; // No tasks available
      }

      if (response.status !== 200) {
        throw new Error(`Task polling failed with status ${response.status}`);
      }

      const task = response.data as Task;

      this.logger.debug('Task received from orchestrator', {
        taskId: task.id,
        type: task.type,
        workerId,
      });

      return task;
    } catch (error) {
      this.logger.error('Task polling failed', { error, workerId });
      throw error;
    }
  }

  async reportTaskStarted(taskId: string, workerId: string): Promise<void> {
    const url = `${this.config.url}${this.config.resultSubmissionEndpoint}/started`;

    try {
      await this.httpClient.post(url, {
        taskId,
        workerId,
        startedAt: new Date().toISOString(),
      });

      this.logger.debug('Task started reported', { taskId, workerId });
    } catch (error) {
      this.logger.error('Failed to report task started', { error, taskId, workerId });
      throw error;
    }
  }

  async reportTaskProgress(taskId: string, progress: TaskProgress): Promise<void> {
    const url = `${this.config.url}${this.config.resultSubmissionEndpoint}/progress`;

    try {
      await this.httpClient.post(url, {
        taskId,
        progress,
        timestamp: new Date().toISOString(),
      });

      this.logger.debug('Task progress reported', {
        taskId,
        progress: progress.percentage,
        workerId: this.workerId,
      });
    } catch (error) {
      this.logger.error('Failed to report task progress', { error, taskId, progress });
      throw error;
    }
  }

  async reportTaskCompleted(taskId: string, result: TaskResult): Promise<void> {
    const url = `${this.config.url}${this.config.resultSubmissionEndpoint}/completed`;

    try {
      await this.httpClient.post(url, {
        taskId,
        result,
        workerId: this.workerId,
        completedAt: new Date().toISOString(),
      });

      this.logger.info('Task completion reported', {
        taskId,
        duration: result.duration,
        workerId: this.workerId,
      });
    } catch (error) {
      this.logger.error('Failed to report task completion', { error, taskId, result });
      throw error;
    }
  }

  async reportTaskFailed(taskId: string, error: Error): Promise<void> {
    const url = `${this.config.url}${this.config.resultSubmissionEndpoint}/failed`;

    try {
      await this.httpClient.post(url, {
        taskId,
        error: {
          message: error.message,
          stack: error.stack,
          name: error.name,
        },
        workerId: this.workerId,
        failedAt: new Date().toISOString(),
      });

      this.logger.error('Task failure reported', {
        taskId,
        errorMessage: error.message,
        workerId: this.workerId,
      });
    } catch (error) {
      this.logger.error('Failed to report task failure', {
        error,
        taskId,
        originalError: error.message,
      });
      throw error;
    }
  }

  async sendHeartbeat(workerId: string, health: HealthStatus): Promise<void> {
    const url = `${this.config.url}${this.config.heartbeatEndpoint}`;

    try {
      await this.httpClient.post(url, {
        workerId,
        health,
        timestamp: new Date().toISOString(),
      });

      this.logger.debug('Heartbeat sent', { workerId, status: health.status });
    } catch (error) {
      this.logger.error('Failed to send heartbeat', { error, workerId });
      throw error;
    }
  }

  async healthCheck(): Promise<HealthCheckResult> {
    const url = `${this.config.url}/health`;

    try {
      const response = await this.httpClient.get(url);

      return {
        healthy: response.status === 200,
        details: response.data,
      };
    } catch (error) {
      return {
        healthy: false,
        details: { error: error.message },
      };
    }
  }
}
```

### 2.4 Local Resource Management and Isolation

```typescript
// packages/worker/src/resource-manager/types.ts

export interface IResourceManager {
  initialize(config: ExecutionConfig): Promise<void>;
  allocateResources(task: Task): Promise<ResourceAllocation>;
  releaseResources(allocation: ResourceAllocation): Promise<void>;
  monitorUsage(allocation: ResourceAllocation): Promise<ResourceUsage>;
  enforceLimits(allocation: ResourceAllocation): Promise<void>;
  cleanupWorkspace(taskId: string): Promise<void>;
  cleanupAll(): Promise<void>;
  healthCheck(): Promise<HealthCheckResult>;
}

export interface ResourceAllocation {
  taskId: string;
  workspace: string;
  tempDir: string;
  memoryLimit: number;
  cpuLimit: number;
  diskLimit: number;
  networkAllowed: boolean;
  sandboxEnabled: boolean;
  createdAt: string;
}

export interface ResourceUsage {
  taskId: string;
  memoryUsage: number;
  cpuUsage: number;
  diskUsage: number;
  networkUsage: number;
  processCount: number;
  timestamp: string;
}

export interface ExecutionConfig {
  workspaceRoot: string;
  tempDir: string;
  maxExecutionTime: number;
  maxMemoryUsage: number;
  maxCpuUsage: number;
  enableSandbox: boolean;
}
```

```typescript
// packages/worker/src/resource-manager/resource-manager.ts

export class ResourceManager implements IResourceManager {
  private config: ExecutionConfig;
  private logger: ILogger;
  private activeAllocations: Map<string, ResourceAllocation> = new Map();
  private usageMonitors: Map<string, NodeJS.Timeout> = new Map();

  constructor(logger: ILogger) {
    this.logger = logger;
  }

  async initialize(config: ExecutionConfig): Promise<void> {
    this.config = config;

    // Create base directories
    await this.ensureDirectoryExists(config.workspaceRoot);
    await this.ensureDirectoryExists(config.tempDir);

    this.logger.info('Resource manager initialized', {
      workspaceRoot: config.workspaceRoot,
      tempDir: config.tempDir,
      enableSandbox: config.enableSandbox,
    });
  }

  async allocateResources(task: Task): Promise<ResourceAllocation> {
    const taskId = task.id;
    const workspace = path.join(this.config.workspaceRoot, taskId);
    const tempDir = path.join(this.config.tempDir, taskId);

    // Create task-specific directories
    await this.ensureDirectoryExists(workspace);
    await this.ensureDirectoryExists(tempDir);

    const allocation: ResourceAllocation = {
      taskId,
      workspace,
      tempDir,
      memoryLimit: this.config.maxMemoryUsage,
      cpuLimit: this.config.maxCpuUsage,
      diskLimit: 10 * 1024 * 1024 * 1024, // 10GB
      networkAllowed: task.type !== 'security_scan', // Restrict network for security scans
      sandboxEnabled: this.config.enableSandbox,
      createdAt: new Date().toISOString(),
    };

    this.activeAllocations.set(taskId, allocation);

    // Start usage monitoring
    this.startUsageMonitoring(allocation);

    this.logger.info('Resources allocated for task', {
      taskId,
      workspace,
      memoryLimit: allocation.memoryLimit,
      cpuLimit: allocation.cpuLimit,
    });

    return allocation;
  }

  async releaseResources(allocation: ResourceAllocation): Promise<void> {
    const taskId = allocation.taskId;

    // Stop usage monitoring
    this.stopUsageMonitoring(taskId);

    // Clean up workspace
    await this.cleanupDirectory(allocation.workspace);
    await this.cleanupDirectory(allocation.tempDir);

    // Remove from active allocations
    this.activeAllocations.delete(taskId);

    this.logger.info('Resources released for task', { taskId });
  }

  async monitorUsage(allocation: ResourceAllocation): Promise<ResourceUsage> {
    const usage = await this.collectResourceUsage(allocation);

    // Check if limits are exceeded
    await this.enforceLimits(allocation, usage);

    return usage;
  }

  async enforceLimits(allocation: ResourceAllocation, usage?: ResourceUsage): Promise<void> {
    if (!usage) {
      usage = await this.collectResourceUsage(allocation);
    }

    // Check memory limit
    if (usage.memoryUsage > allocation.memoryLimit) {
      this.logger.warn('Memory limit exceeded', {
        taskId: allocation.taskId,
        usage: usage.memoryUsage,
        limit: allocation.memoryLimit,
      });

      // Kill processes exceeding memory limit
      await this.killTaskProcesses(allocation.taskId);
      throw new Error(`Memory limit exceeded: ${usage.memoryUsage} > ${allocation.memoryLimit}`);
    }

    // Check CPU limit
    if (usage.cpuUsage > allocation.cpuLimit) {
      this.logger.warn('CPU limit exceeded', {
        taskId: allocation.taskId,
        usage: usage.cpuUsage,
        limit: allocation.cpuLimit,
      });

      // Throttle processes exceeding CPU limit
      await this.throttleTaskProcesses(allocation.taskId);
    }

    // Check disk limit
    if (usage.diskUsage > allocation.diskLimit) {
      this.logger.warn('Disk limit exceeded', {
        taskId: allocation.taskId,
        usage: usage.diskUsage,
        limit: allocation.diskLimit,
      });

      throw new Error(`Disk limit exceeded: ${usage.diskUsage} > ${allocation.diskLimit}`);
    }
  }

  async cleanupWorkspace(taskId: string): Promise<void> {
    const allocation = this.activeAllocations.get(taskId);
    if (allocation) {
      await this.releaseResources(allocation);
    }
  }

  async cleanupAll(): Promise<void> {
    this.logger.info('Cleaning up all resources...');

    // Stop all usage monitors
    for (const taskId of this.usageMonitors.keys()) {
      this.stopUsageMonitoring(taskId);
    }

    // Clean up all active allocations
    for (const allocation of this.activeAllocations.values()) {
      await this.releaseResources(allocation);
    }

    // Clean up base directories
    await this.cleanupDirectory(this.config.workspaceRoot);
    await this.cleanupDirectory(this.config.tempDir);

    this.logger.info('All resources cleaned up');
  }

  async healthCheck(): Promise<HealthCheckResult> {
    try {
      // Check if directories exist and are writable
      await this.checkDirectoryAccess(this.config.workspaceRoot);
      await this.checkDirectoryAccess(this.config.tempDir);

      // Check system resources
      const systemUsage = await this.getSystemResourceUsage();

      return {
        healthy: true,
        details: {
          activeAllocations: this.activeAllocations.size,
          systemUsage,
        },
      };
    } catch (error) {
      return {
        healthy: false,
        details: { error: error.message },
      };
    }
  }

  private async collectResourceUsage(allocation: ResourceAllocation): Promise<ResourceUsage> {
    const usage = await this.getTaskResourceUsage(allocation.taskId);

    return {
      taskId: allocation.taskId,
      memoryUsage: usage.memory,
      cpuUsage: usage.cpu,
      diskUsage: await this.getDirectorySize(allocation.workspace),
      networkUsage: usage.network,
      processCount: usage.processCount,
      timestamp: new Date().toISOString(),
    };
  }

  private startUsageMonitoring(allocation: ResourceAllocation): Promise<void> {
    const monitor = setInterval(async () => {
      try {
        const usage = await this.monitorUsage(allocation);
        this.logger.debug('Resource usage', {
          taskId: allocation.taskId,
          memory: usage.memoryUsage,
          cpu: usage.cpuUsage,
          disk: usage.diskUsage,
        });
      } catch (error) {
        this.logger.error('Error monitoring resource usage', {
          taskId: allocation.taskId,
          error: error.message,
        });
      }
    }, 5000); // Monitor every 5 seconds

    this.usageMonitors.set(allocation.taskId, monitor);
  }

  private stopUsageMonitoring(taskId: string): void {
    const monitor = this.usageMonitors.get(taskId);
    if (monitor) {
      clearInterval(monitor);
      this.usageMonitors.delete(taskId);
    }
  }

  private async ensureDirectoryExists(dirPath: string): Promise<void> {
    const fs = require('fs').promises;
    await fs.mkdir(dirPath, { recursive: true });
  }

  private async cleanupDirectory(dirPath: string): Promise<void> {
    const fs = require('fs').promises;
    try {
      await fs.rm(dirPath, { recursive: true, force: true });
    } catch (error) {
      this.logger.warn('Failed to cleanup directory', { dirPath, error: error.message });
    }
  }

  private async getDirectorySize(dirPath: string): Promise<number> {
    // Implementation to calculate directory size
    // This would recursively sum file sizes
    return 0; // Placeholder
  }

  private async getTaskResourceUsage(taskId: string): Promise<any> {
    // Implementation to get resource usage for specific task
    // This would use system calls to get process information
    return {
      memory: 0,
      cpu: 0,
      network: 0,
      processCount: 0,
    }; // Placeholder
  }

  private async getSystemResourceUsage(): Promise<any> {
    // Implementation to get system-wide resource usage
    return {
      totalMemory: 0,
      usedMemory: 0,
      totalCpu: 0,
      usedCpu: 0,
    }; // Placeholder
  }

  private async checkDirectoryAccess(dirPath: string): Promise<void> {
    const fs = require('fs').promises;
    try {
      await fs.access(dirPath, fs.constants.W_OK);
    } catch (error) {
      throw new Error(`Directory ${dirPath} is not writable`);
    }
  }

  private async killTaskProcesses(taskId: string): Promise<void> {
    // Implementation to kill processes associated with a task
    this.logger.warn('Killing processes for task', { taskId });
  }

  private async throttleTaskProcesses(taskId: string): Promise<void> {
    // Implementation to throttle processes associated with a task
    this.logger.warn('Throttling processes for task', { taskId });
  }
}
```

### 2.5 Startup and Shutdown Sequences

```typescript
// packages/worker/src/lifecycle/worker-lifecycle.ts

export class WorkerLifecycle {
  private worker: IWorker;
  private logger: ILogger;
  private shutdownTimeout: number = 60000; // 60 seconds

  constructor(worker: IWorker, logger: ILogger) {
    this.worker = worker;
    this.logger = logger;
  }

  async startup(): Promise<void> {
    this.logger.info('Starting worker startup sequence...');

    try {
      // Phase 1: Initialize worker
      await this.initializeWorker();

      // Phase 2: Detect capabilities
      await this.detectCapabilities();

      // Phase 3: Register with orchestrator
      await this.registerWithOrchestrator();

      // Phase 4: Start task processing
      await this.startTaskProcessing();

      // Phase 5: Run health checks
      await this.runStartupHealthChecks();

      // Phase 6: Emit startup complete event
      await this.emitStartupComplete();

      this.logger.info('Worker startup sequence completed successfully');
    } catch (error) {
      this.logger.error('Worker startup failed', { error });
      await this.handleStartupFailure(error);
      throw error;
    }
  }

  async shutdown(): Promise<void> {
    this.logger.info('Starting worker shutdown sequence...');

    try {
      // Phase 1: Stop accepting new tasks
      await this.stopAcceptingTasks();

      // Phase 2: Wait for current task to complete
      await this.waitForCurrentTaskCompletion();

      // Phase 3: Unregister from orchestrator
      await this.unregisterFromOrchestrator();

      // Phase 4: Cleanup resources
      await this.cleanupResources();

      // Phase 5: Emit shutdown complete event
      await this.emitShutdownComplete();

      this.logger.info('Worker shutdown sequence completed successfully');
    } catch (error) {
      this.logger.error('Error during worker shutdown', { error });
      throw error;
    }
  }

  private async initializeWorker(): Promise<void> {
    this.logger.info('Phase 1: Initializing worker...');

    // Load configuration
    const config = await this.loadConfiguration();

    // Initialize worker with configuration
    await this.worker.initialize(config);

    this.logger.info('Worker initialized successfully');
  }

  private async detectCapabilities(): Promise<void> {
    this.logger.info('Phase 2: Detecting capabilities...');

    const capabilities = this.worker.getCapabilities();

    this.logger.info('Capabilities detected', {
      supportedTaskTypes: capabilities.supportedTaskTypes,
      supportedPlatforms: capabilities.supportedPlatforms,
      maxConcurrentTasks: capabilities.maxConcurrentTasks,
    });
  }

  private async registerWithOrchestrator(): Promise<void> {
    this.logger.info('Phase 3: Registering with orchestrator...');

    const capabilities = this.worker.getCapabilities();

    // Registration is handled by worker.start()
    // This is just for logging and validation
    this.logger.info('Worker capabilities ready for registration', {
      workerId: capabilities.workerId,
      supportedTaskTypes: capabilities.supportedTaskTypes.length,
    });
  }

  private async startTaskProcessing(): Promise<void> {
    this.logger.info('Phase 4: Starting task processing...');

    await this.worker.start();

    this.logger.info('Task processing started');
  }

  private async runStartupHealthChecks(): Promise<void> {
    this.logger.info('Phase 5: Running startup health checks...');

    const health = await this.worker.healthCheck();

    if (health.status !== 'healthy') {
      const unhealthyServices = health.checks
        .filter((check) => check.status !== 'healthy')
        .map((check) => check.name);

      throw new Error(`Health check failed for services: ${unhealthyServices.join(', ')}`);
    }

    this.logger.info('All startup health checks passed');
  }

  private async stopAcceptingTasks(): Promise<void> {
    this.logger.info('Phase 1: Stopping acceptance of new tasks...');

    // This is handled by worker.stop()
    // This is just for logging
    this.logger.info('Stopped accepting new tasks');
  }

  private async waitForCurrentTaskCompletion(): Promise<void> {
    this.logger.info('Phase 2: Waiting for current task to complete...');

    const startTime = Date.now();
    const maxWaitTime = this.shutdownTimeout;

    while (Date.now() - startTime < maxWaitTime) {
      const health = await this.worker.healthCheck();

      if (!health.currentTask) {
        break;
      }

      this.logger.info(`Waiting for current task to complete... (${health.currentTask})`);
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    const finalHealth = await this.worker.healthCheck();
    if (finalHealth.currentTask) {
      this.logger.warn(`Force shutdown with task still active: ${finalHealth.currentTask}`);
    }
  }

  private async unregisterFromOrchestrator(): Promise<void> {
    this.logger.info('Phase 3: Unregistering from orchestrator...');

    // This is handled by worker.stop()
    // This is just for logging
    this.logger.info('Unregistered from orchestrator');
  }

  private async cleanupResources(): Promise<void> {
    this.logger.info('Phase 4: Cleaning up resources...');

    await this.worker.stop();

    this.logger.info('Resources cleaned up');
  }

  private async loadConfiguration(): Promise<IWorkerConfig> {
    // Implementation to load worker configuration
    // This would read from config files, environment variables, etc.
    return {
      orchestrator: {
        url: process.env.ORCHESTRATOR_URL || 'http://localhost:3000',
        registrationEndpoint: '/api/v1/workers/register',
        taskPollingEndpoint: '/api/v1/tasks/poll',
        heartbeatEndpoint: '/api/v1/workers/heartbeat',
        resultSubmissionEndpoint: '/api/v1/tasks/result',
      },
      execution: {
        workspaceRoot: process.env.WORKSPACE_ROOT || '/tmp/tamma-workspace',
        tempDir: process.env.TEMP_DIR || '/tmp/tamma-temp',
        maxExecutionTime: parseInt(process.env.MAX_EXECUTION_TIME || '1800000'), // 30 minutes
        maxMemoryUsage: parseInt(process.env.MAX_MEMORY_USAGE || '4194304000'), // 4GB
        maxCpuUsage: parseInt(process.env.MAX_CPU_USAGE || '200'), // 200%
        enableSandbox: process.env.ENABLE_SANDBOX === 'true',
      },
      capabilities: {
        supportedTaskTypes: (process.env.SUPPORTED_TASK_TYPES || 'build,test,security_scan').split(
          ','
        ),
        maxConcurrentTasks: parseInt(process.env.MAX_CONCURRENT_TASKS || '1'),
        supportedPlatforms: (process.env.SUPPORTED_PLATFORMS || 'github,gitlab').split(','),
        supportedProviders: (process.env.SUPPORTED_PROVIDERS || 'anthropic,openai').split(','),
      },
      logging: {
        level: process.env.LOG_LEVEL || 'info',
        format: process.env.LOG_FORMAT || 'json',
        output: (process.env.LOG_OUTPUT || 'stdout').split(','),
      },
      monitoring: {
        metricsEnabled: process.env.METRICS_ENABLED === 'true',
        healthCheckInterval: parseInt(process.env.HEALTH_CHECK_INTERVAL || '30000'),
        resourceMonitoringInterval: parseInt(process.env.RESOURCE_MONITORING_INTERVAL || '5000'),
      },
    };
  }

  private async emitStartupComplete(): Promise<void> {
    this.logger.info('Worker startup complete', {
      timestamp: new Date().toISOString(),
      workerId: this.worker.getCapabilities().workerId,
    });
  }

  private async emitShutdownComplete(): Promise<void> {
    this.logger.info('Worker shutdown complete', {
      timestamp: new Date().toISOString(),
      workerId: this.worker.getCapabilities().workerId,
    });
  }

  private async handleStartupFailure(error: Error): Promise<void> {
    this.logger.error('Worker startup failed', {
      error: error.message,
      stack: error.stack,
      timestamp: new Date().toISOString(),
    });
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// packages/worker/src/__tests__/worker.test.ts

describe('Worker', () => {
  let worker: Worker;
  let mockServices: MockWorkerServices;

  beforeEach(() => {
    mockServices = createMockServices();
    worker = new Worker(
      mockServices.taskExecutor,
      mockServices.resourceManager,
      mockServices.orchestratorClient,
      mockServices.logger,
      mockServices.metrics,
      mockServices.healthChecker
    );
  });

  describe('initialization', () => {
    it('should initialize with valid configuration', async () => {
      const config = createValidWorkerConfig();

      await worker.initialize(config);

      expect(mockServices.taskExecutor.initialize).toHaveBeenCalledWith(config.execution);
      expect(mockServices.resourceManager.initialize).toHaveBeenCalledWith(config.execution);
    });

    it('should throw error with invalid configuration', async () => {
      const config = createInvalidWorkerConfig();

      await expect(worker.initialize(config)).rejects.toThrow();
    });
  });

  describe('task execution', () => {
    it('should execute task successfully', async () => {
      const config = createValidWorkerConfig();
      const task = createMockTask();

      await worker.initialize(config);
      await worker.start();

      // Mock orchestrator to return task
      mockServices.orchestratorClient.pollForTask.mockResolvedValue(task);
      mockServices.taskExecutor.executeTask.mockResolvedValue(createMockTaskResult());

      // Wait for task execution
      await new Promise((resolve) => setTimeout(resolve, 100));

      expect(mockServices.taskExecutor.executeTask).toHaveBeenCalledWith(task);
      expect(mockServices.orchestratorClient.reportTaskCompleted).toHaveBeenCalled();
    });
  });

  describe('resource management', () => {
    it('should allocate and release resources for task', async () => {
      const config = createValidWorkerConfig();
      const task = createMockTask();
      const allocation = createMockResourceAllocation();

      await worker.initialize(config);

      mockServices.resourceManager.allocateResources.mockResolvedValue(allocation);

      // Execute task
      await worker.executeTask(task);

      expect(mockServices.resourceManager.allocateResources).toHaveBeenCalledWith(task);
      expect(mockServices.resourceManager.cleanupWorkspace).toHaveBeenCalledWith(task.id);
    });
  });
});
```

## Completion Checklist

- [ ] Document worker responsibilities and scope
- [ ] Design worker execution engine and task processing
- [ ] Define orchestrator communication protocols
- [ ] Design local resource management and isolation
- [ ] Document worker startup and shutdown sequences
- [ ] Create comprehensive TypeScript interfaces and types
- [ ] Implement worker class with all required methods
- [ ] Add comprehensive error handling and logging
- [ ] Create unit tests for all worker components
- [ ] Add integration tests for communication protocols
- [ ] Document architecture decisions and trade-offs
- [ ] Create sequence diagrams for worker workflows

## Dependencies

- Task 1: Orchestrator Mode Architecture (for coordination patterns)
- Task 3: Shared Components and Interfaces (for common infrastructure)
- Task 4: Sequence Diagrams and Workflows (for visual documentation)
- Task 5: State Persistence and Recovery Strategy (for data management)
- Task 6: Integration Points and APIs (for external interfaces)

## Estimated Time

**Worker Responsibilities**: 2-3 days
**Execution Engine Design**: 3-4 days
**Communication Protocols**: 3-4 days
**Resource Management Design**: 4-5 days
**Startup/Shutdown Sequences**: 2-3 days
**Implementation and Testing**: 4-5 days
**Total**: 18-24 days
