# Story 1.8 Task 1: Define Orchestrator Mode Architecture

## Task Overview

Define comprehensive architecture for orchestrator mode, including responsibilities, service architecture, task queue management, state management, and startup/shutdown sequences. This task establishes the foundation for the autonomous coordination component of Tamma's hybrid architecture.

## Acceptance Criteria

### 1.1 Document Orchestrator Responsibilities and Scope

- [ ] Define core orchestrator responsibilities (issue selection, loop coordination, state management)
- [ ] Document orchestrator scope and boundaries
- [ ] Define orchestrator interaction patterns with external systems
- [ ] Specify orchestrator ownership and data management responsibilities
- [ ] Document orchestrator constraints and limitations

### 1.2 Design Orchestrator Service Architecture

- [ ] Design Fastify server architecture with REST API endpoints
- [ ] Define WebSocket implementation for real-time communication
- [ ] Design service layer architecture with separation of concerns
- [ ] Define middleware stack and request processing pipeline
- [ ] Document service discovery and configuration management

### 1.3 Define Task Queue Management and Worker Pool Coordination

- [ ] Design task queue architecture with priority handling
- [ ] Define worker pool management and load balancing strategies
- [ ] Document task assignment and distribution algorithms
- [ ] Design worker health monitoring and failure handling
- [ ] Define task queue persistence and recovery mechanisms

### 1.4 Design State Management and Persistence Strategy

- [ ] Define orchestrator state model and data structures
- [ ] Design state persistence strategy with PostgreSQL
- [ ] Document state synchronization and consistency guarantees
- [ ] Define state recovery and rollback procedures
- [ ] Design state migration and versioning strategy

### 1.5 Document Orchestrator Startup and Shutdown Sequences

- [ ] Define orchestrator startup sequence with dependency resolution
- [ ] Document initialization order and health checks
- [ ] Design graceful shutdown sequence with in-flight task handling
- [ ] Define shutdown timeout and force termination procedures
- [ ] Document startup/shutdown error handling and recovery

## Implementation Details

### 1.1 Orchestrator Responsibilities and Scope

```markdown
# Orchestrator Mode Responsibilities and Scope

## Core Responsibilities

### 1. Issue Selection and Prioritization

- **Issue Discovery**: Monitor configured Git platforms for new issues and pull requests
- **Eligibility Evaluation**: Apply business rules to determine which issues are suitable for autonomous processing
- **Priority Assignment**: Calculate priority scores based on issue age, labels, assignee availability, and business impact
- **Queue Management**: Maintain prioritized queue of eligible issues for processing

### 2. Autonomous Loop Coordination

- **Workflow Orchestration**: Execute the 14-step autonomous development workflow for each selected issue
- **Step Coordination**: Coordinate between AI providers, Git platforms, and quality gates
- **Progress Tracking**: Monitor workflow execution progress and handle step failures
- **Decision Making**: Make autonomous decisions about continuation, retry, or escalation

### 3. State Management and Persistence

- **Workflow State**: Maintain complete state of all active workflows
- **Context Management**: Preserve context across workflow steps and restarts
- **Audit Trail**: Emit DCB events for all orchestrator actions and decisions
- **Recovery Support**: Enable graceful recovery from orchestrator restarts

### 4. Worker Pool Management

- **Worker Registration**: Manage registration and deregistration of worker nodes
- **Load Balancing**: Distribute tasks across available workers based on capabilities
- **Health Monitoring**: Monitor worker health, performance, and availability
- **Resource Allocation**: Optimize resource utilization across the worker pool

### 5. Quality Gate Coordination

- **Gate Execution**: Coordinate quality gate checks (build, test, security scan)
- **Result Evaluation**: Evaluate quality gate results and make pass/fail decisions
- **Retry Logic**: Implement retry strategies for failed quality gates
- **Escalation**: Escalate persistent failures to human attention

## Scope and Boundaries

### In Scope

- Autonomous issue processing from selection to completion
- Multi-platform Git integration (GitHub, GitLab, Gitea, Forgejo)
- Multi-provider AI integration (Claude, OpenAI, etc.)
- Quality gate orchestration and evaluation
- Worker pool management and coordination
- State persistence and recovery
- Real-time progress monitoring and reporting

### Out of Scope

- Direct code execution (delegated to workers)
- Local filesystem operations (delegated to workers)
- CI/CD pipeline integration (delegated to workers)
- User interface and dashboard (separate concern)
- Authentication and authorization (shared infrastructure)

## Interaction Patterns

### External System Interactions

- **Git Platforms**: Issue discovery, PR creation, status updates
- **AI Providers**: Code generation, analysis, decision support
- **Quality Gates**: Build execution, test running, security scanning
- **Workers**: Task assignment, progress monitoring, result collection
- **Database**: State persistence, audit trail, configuration storage

### Internal Communication Patterns

- **Event-Driven Architecture**: Use DCB events for loose coupling
- **Synchronous Operations**: Direct API calls for immediate responses
- **Asynchronous Processing**: Background tasks for long-running operations
- **Streaming Communication**: WebSocket for real-time progress updates

## Constraints and Limitations

### Performance Constraints

- Maximum concurrent workflows: 10 (configurable)
- Workflow timeout: 2 hours (configurable)
- Worker response timeout: 30 seconds (configurable)
- Database connection pool: 20 connections (configurable)

### Resource Constraints

- Memory usage: 2GB maximum (configurable)
- CPU usage: 80% maximum (configurable)
- Disk usage: 10GB for state and logs (configurable)
- Network bandwidth: 100Mbps (configurable)

### Business Constraints

- Only process issues with valid business justification
- Require human approval for high-risk changes
- Maintain 100% audit trail for compliance
- Support manual override and intervention capabilities
```

### 1.2 Orchestrator Service Architecture

```typescript
// packages/orchestrator/src/types/orchestrator.ts

export interface IOrchestratorConfig {
  server: {
    host: string;
    port: number;
    cors: {
      origin: string[];
      credentials: boolean;
    };
    rateLimit: {
      max: number;
      windowMs: number;
    };
  };
  database: {
    host: string;
    port: number;
    database: string;
    username: string;
    password: string;
    ssl: boolean;
    pool: {
      min: number;
      max: number;
      idleTimeoutMillis: number;
    };
  };
  workflow: {
    maxConcurrent: number;
    timeout: number;
    retryAttempts: number;
    retryDelay: number;
  };
  workers: {
    heartbeatInterval: number;
    healthCheckInterval: number;
    maxIdleTime: number;
  };
  events: {
    batchSize: number;
    flushInterval: number;
    retentionDays: number;
  };
}

export interface IOrchestratorServices {
  httpServer: FastifyInstance;
  webSocketServer: WebSocketServer;
  taskQueue: ITaskQueue;
  workerPool: IWorkerPool;
  workflowEngine: IWorkflowEngine;
  stateManager: IStateManager;
  eventEmitter: IEventEmitter;
  logger: ILogger;
  metrics: IMetrics;
}

export interface IOrchestrator {
  initialize(config: IOrchestratorConfig): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
  getServices(): IOrchestratorServices;
  healthCheck(): Promise<HealthStatus>;
}
```

```typescript
// packages/orchestrator/src/orchestrator.ts

export class Orchestrator implements IOrchestrator {
  private config: IOrchestratorConfig;
  private services: IOrchestratorServices;
  private isRunning: boolean = false;

  constructor(
    private taskQueue: ITaskQueue,
    private workerPool: IWorkerPool,
    private workflowEngine: IWorkflowEngine,
    private stateManager: IStateManager,
    private eventEmitter: IEventEmitter,
    private logger: ILogger,
    private metrics: IMetrics
  ) {}

  async initialize(config: IOrchestratorConfig): Promise<void> {
    this.config = config;

    // Initialize HTTP server
    this.services.httpServer = fastify({
      logger: this.logger,
      trustProxy: true,
    });

    // Setup middleware
    await this.setupMiddleware();

    // Register routes
    await this.registerRoutes();

    // Initialize WebSocket server
    this.services.webSocketServer = new WebSocketServer({
      server: this.services.httpServer.server,
      path: '/ws',
    });

    // Setup WebSocket handlers
    await this.setupWebSocketHandlers();

    // Initialize database connections
    await this.initializeDatabase();

    // Initialize services
    await this.initializeServices();

    this.logger.info('Orchestrator initialized successfully');
  }

  async start(): Promise<void> {
    if (this.isRunning) {
      throw new Error('Orchestrator is already running');
    }

    try {
      // Start HTTP server
      await this.services.httpServer.listen({
        port: this.config.server.port,
        host: this.config.server.host,
      });

      // Start background services
      await this.startBackgroundServices();

      this.isRunning = true;
      this.logger.info(
        `Orchestrator started on ${this.config.server.host}:${this.config.server.port}`
      );

      // Emit startup event
      await this.eventEmitter.emit({
        type: 'ORCHESTRATOR.STARTED.SUCCESS',
        tags: {
          host: this.config.server.host,
          port: this.config.server.port.toString(),
        },
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system',
        },
        data: {
          startTime: new Date().toISOString(),
          config: this.sanitizeConfig(this.config),
        },
      });
    } catch (error) {
      this.logger.error('Failed to start orchestrator', { error });
      await this.eventEmitter.emit({
        type: 'ORCHESTRATOR.STARTED.FAILED',
        tags: {},
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system',
        },
        data: {
          error: error.message,
          startTime: new Date().toISOString(),
        },
      });
      throw error;
    }
  }

  async stop(): Promise<void> {
    if (!this.isRunning) {
      return;
    }

    this.logger.info('Stopping orchestrator...');

    try {
      // Stop accepting new tasks
      await this.taskQueue.pause();

      // Wait for in-flight workflows to complete or timeout
      await this.waitForWorkflowsCompletion();

      // Stop background services
      await this.stopBackgroundServices();

      // Close WebSocket connections
      await this.services.webSocketServer.close();

      // Stop HTTP server
      await this.services.httpServer.close();

      // Close database connections
      await this.closeDatabaseConnections();

      this.isRunning = false;
      this.logger.info('Orchestrator stopped successfully');

      // Emit shutdown event
      await this.eventEmitter.emit({
        type: 'ORCHESTRATOR.STOPPED.SUCCESS',
        tags: {},
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system',
        },
        data: {
          stopTime: new Date().toISOString(),
        },
      });
    } catch (error) {
      this.logger.error('Error during orchestrator shutdown', { error });
      throw error;
    }
  }

  private async setupMiddleware(): Promise<void> {
    const server = this.services.httpServer;

    // CORS middleware
    server.register(cors, {
      origin: this.config.server.cors.origin,
      credentials: this.config.server.cors.credentials,
    });

    // Rate limiting middleware
    server.register(rateLimit, {
      max: this.config.server.rateLimit.max,
      windowMs: this.config.server.rateLimit.windowMs,
    });

    // Request logging middleware
    server.addHook('onRequest', async (request, reply) => {
      this.logger.debug('Incoming request', {
        method: request.method,
        url: request.url,
        userAgent: request.headers['user-agent'],
      });
    });

    // Response logging middleware
    server.addHook('onResponse', async (request, reply) => {
      this.logger.debug('Request completed', {
        method: request.method,
        url: request.url,
        statusCode: reply.statusCode,
        responseTime: reply.getResponseTime(),
      });
    });

    // Error handling middleware
    server.addHook('onError', async (request, reply, error) => {
      this.logger.error('Request error', {
        method: request.method,
        url: request.url,
        error: error.message,
      });
    });
  }

  private async registerRoutes(): Promise<void> {
    const server = this.services.httpServer;

    // Health check endpoint
    server.get('/health', async (request, reply) => {
      const health = await this.healthCheck();
      const statusCode = health.status === 'healthy' ? 200 : 503;
      reply.code(statusCode).send(health);
    });

    // Metrics endpoint
    server.get('/metrics', async (request, reply) => {
      const metrics = await this.metrics.getMetrics();
      reply.type('text/plain').send(metrics);
    });

    // Workflow management endpoints
    server.register(workflowRoutes, { prefix: '/api/v1/workflows' });

    // Worker management endpoints
    server.register(workerRoutes, { prefix: '/api/v1/workers' });

    // Task queue endpoints
    server.register(taskRoutes, { prefix: '/api/v1/tasks' });

    // Event streaming endpoints
    server.register(eventRoutes, { prefix: '/api/v1/events' });
  }

  private async setupWebSocketHandlers(): Promise<void> {
    const ws = this.services.webSocketServer;

    ws.on('connection', (socket, request) => {
      this.logger.debug('WebSocket connection established', {
        remoteAddress: request.socket.remoteAddress,
      });

      // Handle workflow progress subscriptions
      socket.on('message', async (data) => {
        try {
          const message = JSON.parse(data.toString());
          await this.handleWebSocketMessage(socket, message);
        } catch (error) {
          this.logger.error('WebSocket message error', { error });
          socket.send(
            JSON.stringify({
              type: 'error',
              message: 'Invalid message format',
            })
          );
        }
      });

      // Handle connection close
      socket.on('close', () => {
        this.logger.debug('WebSocket connection closed');
      });

      // Send welcome message
      socket.send(
        JSON.stringify({
          type: 'connected',
          timestamp: new Date().toISOString(),
        })
      );
    });
  }

  private async handleWebSocketMessage(socket: WebSocket, message: any): Promise<void> {
    switch (message.type) {
      case 'subscribe':
        await this.handleSubscription(socket, message);
        break;
      case 'unsubscribe':
        await this.handleUnsubscription(socket, message);
        break;
      default:
        socket.send(
          JSON.stringify({
            type: 'error',
            message: `Unknown message type: ${message.type}`,
          })
        );
    }
  }

  private async handleSubscription(socket: WebSocket, message: any): Promise<void> {
    const { workflowId, events } = message;

    // Subscribe to workflow events
    if (workflowId) {
      await this.eventEmitter.subscribe(`workflow.${workflowId}.*`, (event) => {
        socket.send(
          JSON.stringify({
            type: 'workflow_event',
            workflowId,
            event,
          })
        );
      });
    }

    // Subscribe to specific event types
    if (events && Array.isArray(events)) {
      for (const eventType of events) {
        await this.eventEmitter.subscribe(eventType, (event) => {
          socket.send(
            JSON.stringify({
              type: 'event',
              eventType,
              event,
            })
          );
        });
      }
    }

    socket.send(
      JSON.stringify({
        type: 'subscribed',
        workflowId,
        events,
      })
    );
  }

  getServices(): IOrchestratorServices {
    return this.services;
  }

  async healthCheck(): Promise<HealthStatus> {
    const checks = await Promise.allSettled([
      this.taskQueue.healthCheck(),
      this.workerPool.healthCheck(),
      this.workflowEngine.healthCheck(),
      this.stateManager.healthCheck(),
      this.eventEmitter.healthCheck(),
    ]);

    const results = checks.map((check, index) => {
      const name = ['taskQueue', 'workerPool', 'workflowEngine', 'stateManager', 'eventEmitter'][
        index
      ];
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
      checks: results,
    };
  }
}
```

### 1.3 Task Queue Management and Worker Pool Coordination

```typescript
// packages/orchestrator/src/task-queue/types.ts

export interface ITask {
  id: string;
  type: 'workflow' | 'quality_gate' | 'git_operation';
  priority: number;
  data: Record<string, unknown>;
  createdAt: string;
  scheduledAt?: string;
  startedAt?: string;
  completedAt?: string;
  failedAt?: string;
  retryCount: number;
  maxRetries: number;
  status: 'pending' | 'scheduled' | 'running' | 'completed' | 'failed' | 'cancelled';
  assignedWorkerId?: string;
  metadata: {
    workflowId?: string;
    issueId?: string;
    stepNumber?: number;
    [key: string]: unknown;
  };
}

export interface ITaskQueue {
  enqueue(task: Omit<ITask, 'id' | 'createdAt' | 'retryCount'>): Promise<string>;
  dequeue(workerCapabilities: string[]): Promise<ITask | null>;
  complete(taskId: string, result: unknown): Promise<void>;
  fail(taskId: string, error: Error): Promise<void>;
  retry(taskId: string): Promise<void>;
  cancel(taskId: string): Promise<void>;
  getTask(taskId: string): Promise<ITask | null>;
  getTasks(filter?: TaskFilter): Promise<ITask[]>;
  getStats(): Promise<TaskQueueStats>;
  healthCheck(): Promise<HealthCheckResult>;
  pause(): Promise<void>;
  resume(): Promise<void>;
}

export interface IWorkerPool {
  registerWorker(worker: WorkerRegistration): Promise<void>;
  unregisterWorker(workerId: string): Promise<void>;
  updateWorkerHeartbeat(workerId: string): Promise<void>;
  getAvailableWorkers(capabilities?: string[]): Promise<Worker[]>;
  getWorker(workerId: string): Promise<Worker | null>;
  getAllWorkers(): Promise<Worker[]>;
  assignTask(workerId: string, task: ITask): Promise<void>;
  completeTask(workerId: string, taskId: string): Promise<void>;
  failTask(workerId: string, taskId: string, error: Error): Promise<void>;
  healthCheck(): Promise<HealthCheckResult>;
}
```

```typescript
// packages/orchestrator/src/task-queue/task-queue.ts

export class TaskQueue implements ITaskQueue {
  private db: Database;
  private eventEmitter: IEventEmitter;
  private logger: ILogger;
  private isPaused: boolean = false;

  constructor(db: Database, eventEmitter: IEventEmitter, logger: ILogger) {
    this.db = db;
    this.eventEmitter = eventEmitter;
    this.logger = logger;
  }

  async enqueue(task: Omit<ITask, 'id' | 'createdAt' | 'retryCount'>): Promise<string> {
    const taskId = generateUUID();
    const now = new Date().toISOString();

    const fullTask: ITask = {
      ...task,
      id: taskId,
      createdAt: now,
      retryCount: 0,
    };

    await this.db.query(
      `
      INSERT INTO tasks (
        id, type, priority, data, created_at, scheduled_at, 
        started_at, completed_at, failed_at, retry_count, 
        max_retries, status, assigned_worker_id, metadata
      ) VALUES (
        $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14
      )
    `,
      [
        taskId,
        task.type,
        task.priority,
        JSON.stringify(task.data),
        now,
        task.scheduledAt,
        null,
        null,
        null,
        0,
        task.maxRetries,
        task.status,
        task.assignedWorkerId,
        JSON.stringify(task.metadata),
      ]
    );

    this.logger.info('Task enqueued', { taskId, type: task.type, priority: task.priority });

    await this.eventEmitter.emit({
      type: 'TASK.ENQUEUED.SUCCESS',
      tags: {
        taskId,
        taskType: task.type,
        priority: task.priority.toString(),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        taskId,
        taskType: task.type,
        priority: task.priority,
        createdAt: now,
      },
    });

    return taskId;
  }

  async dequeue(workerCapabilities: string[]): Promise<ITask | null> {
    if (this.isPaused) {
      return null;
    }

    const now = new Date().toISOString();

    // Find the highest priority task that matches worker capabilities
    const result = await this.db.query(
      `
      UPDATE tasks 
      SET status = 'running', started_at = $1, assigned_worker_id = $2
      WHERE id = (
        SELECT id FROM tasks 
        WHERE status = 'pending' 
        AND (scheduled_at IS NULL OR scheduled_at <= $1)
        ORDER BY priority DESC, created_at ASC 
        LIMIT 1
      )
      RETURNING *
    `,
      [now, workerCapabilities.join(',')]
    );

    if (result.rows.length === 0) {
      return null;
    }

    const task = this.mapRowToTask(result.rows[0]);

    this.logger.info('Task dequeued', {
      taskId: task.id,
      type: task.type,
      workerCapabilities,
    });

    await this.eventEmitter.emit({
      type: 'TASK.DEQUEUED.SUCCESS',
      tags: {
        taskId,
        taskType: task.type,
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        taskId,
        taskType: task.type,
        startedAt: now,
        workerCapabilities,
      },
    });

    return task;
  }

  async complete(taskId: string, result: unknown): Promise<void> {
    const now = new Date().toISOString();

    await this.db.query(
      `
      UPDATE tasks 
      SET status = 'completed', completed_at = $1, data = $2
      WHERE id = $3
    `,
      [now, JSON.stringify(result), taskId]
    );

    this.logger.info('Task completed', { taskId });

    await this.eventEmitter.emit({
      type: 'TASK.COMPLETED.SUCCESS',
      tags: {
        taskId,
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        taskId,
        completedAt: now,
        result,
      },
    });
  }

  async fail(taskId: string, error: Error): Promise<void> {
    const task = await this.getTask(taskId);
    if (!task) {
      throw new Error(`Task ${taskId} not found`);
    }

    const now = new Date().toISOString();

    if (task.retryCount < task.maxRetries) {
      // Retry the task
      await this.retry(taskId);
    } else {
      // Mark as failed
      await this.db.query(
        `
        UPDATE tasks 
        SET status = 'failed', failed_at = $1
        WHERE id = $2
      `,
        [now, taskId]
      );

      this.logger.error('Task failed permanently', { taskId, error: error.message });

      await this.eventEmitter.emit({
        type: 'TASK.FAILED.PERMANENT',
        tags: {
          taskId,
        },
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system',
        },
        data: {
          taskId,
          failedAt: now,
          error: error.message,
          retryCount: task.retryCount,
        },
      });
    }
  }

  async retry(taskId: string): Promise<void> {
    const task = await this.getTask(taskId);
    if (!task) {
      throw new Error(`Task ${taskId} not found`);
    }

    const retryDelay = Math.min(1000 * Math.pow(2, task.retryCount), 60000); // Max 1 minute
    const scheduledAt = new Date(Date.now() + retryDelay).toISOString();

    await this.db.query(
      `
      UPDATE tasks 
      SET status = 'pending', retry_count = retry_count + 1, 
          scheduled_at = $1, assigned_worker_id = NULL
      WHERE id = $2
    `,
      [scheduledAt, taskId]
    );

    this.logger.info('Task scheduled for retry', {
      taskId,
      retryCount: task.retryCount + 1,
      retryDelay,
    });

    await this.eventEmitter.emit({
      type: 'TASK.RETRY.SCHEDULED',
      tags: {
        taskId,
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        taskId,
        retryCount: task.retryCount + 1,
        scheduledAt,
        retryDelay,
      },
    });
  }

  async getStats(): Promise<TaskQueueStats> {
    const result = await this.db.query(`
      SELECT 
        status,
        COUNT(*) as count,
        AVG(EXTRACT(EPOCH FROM (COALESCE(completed_at, failed_at, started_at) - created_at))) as avg_duration
      FROM tasks 
      WHERE created_at > NOW() - INTERVAL '24 hours'
      GROUP BY status
    `);

    const stats: TaskQueueStats = {
      total: 0,
      pending: 0,
      running: 0,
      completed: 0,
      failed: 0,
      avgDuration: 0,
    };

    for (const row of result.rows) {
      stats[row.status] = parseInt(row.count);
      stats.total += parseInt(row.count);
      if (row.avg_duration) {
        stats.avgDuration = parseFloat(row.avg_duration);
      }
    }

    return stats;
  }

  async pause(): Promise<void> {
    this.isPaused = true;
    this.logger.info('Task queue paused');
  }

  async resume(): Promise<void> {
    this.isPaused = false;
    this.logger.info('Task queue resumed');
  }

  private mapRowToTask(row: any): ITask {
    return {
      id: row.id,
      type: row.type,
      priority: row.priority,
      data: JSON.parse(row.data),
      createdAt: row.created_at,
      scheduledAt: row.scheduled_at,
      startedAt: row.started_at,
      completedAt: row.completed_at,
      failedAt: row.failed_at,
      retryCount: row.retry_count,
      maxRetries: row.max_retries,
      status: row.status,
      assignedWorkerId: row.assigned_worker_id,
      metadata: JSON.parse(row.metadata),
    };
  }
}
```

### 1.4 State Management and Persistence Strategy

```typescript
// packages/orchestrator/src/state-manager/types.ts

export interface IWorkflowState {
  id: string;
  issueId: string;
  platform: string;
  repository: string;
  currentStep: number;
  status: 'pending' | 'running' | 'completed' | 'failed' | 'paused';
  context: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  completedAt?: string;
  failedAt?: string;
  metadata: {
    assignee?: string;
    priority: number;
    labels: string[];
    [key: string]: unknown;
  };
}

export interface IStateManager {
  createWorkflowState(
    state: Omit<IWorkflowState, 'id' | 'createdAt' | 'updatedAt'>
  ): Promise<string>;
  updateWorkflowState(workflowId: string, updates: Partial<IWorkflowState>): Promise<void>;
  getWorkflowState(workflowId: string): Promise<IWorkflowState | null>;
  getWorkflowStates(filter?: WorkflowFilter): Promise<IWorkflowState[]>;
  deleteWorkflowState(workflowId: string): Promise<void>;
  archiveWorkflowState(workflowId: string): Promise<void>;
  getWorkflowHistory(workflowId: string): Promise<WorkflowStateHistory[]>;
  healthCheck(): Promise<HealthCheckResult>;
}
```

```typescript
// packages/orchestrator/src/state-manager/state-manager.ts

export class StateManager implements IStateManager {
  private db: Database;
  private eventEmitter: IEventEmitter;
  private logger: ILogger;

  constructor(db: Database, eventEmitter: IEventEmitter, logger: ILogger) {
    this.db = db;
    this.eventEmitter = eventEmitter;
    this.logger = logger;
  }

  async createWorkflowState(
    state: Omit<IWorkflowState, 'id' | 'createdAt' | 'updatedAt'>
  ): Promise<string> {
    const workflowId = generateUUID();
    const now = new Date().toISOString();

    const fullState: IWorkflowState = {
      ...state,
      id: workflowId,
      createdAt: now,
      updatedAt: now,
    };

    await this.db.query(
      `
      INSERT INTO workflow_states (
        id, issue_id, platform, repository, current_step, status,
        context, created_at, updated_at, started_at, completed_at,
        failed_at, metadata
      ) VALUES (
        $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13
      )
    `,
      [
        workflowId,
        state.issueId,
        state.platform,
        state.repository,
        state.currentStep,
        state.status,
        JSON.stringify(state.context),
        now,
        now,
        state.startedAt,
        state.completedAt,
        state.failedAt,
        JSON.stringify(state.metadata),
      ]
    );

    this.logger.info('Workflow state created', {
      workflowId,
      issueId: state.issueId,
      currentStep: state.currentStep,
    });

    await this.eventEmitter.emit({
      type: 'WORKFLOW.STATE.CREATED',
      tags: {
        workflowId,
        issueId: state.issueId,
        step: state.currentStep.toString(),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        workflowId,
        issueId: state.issueId,
        currentStep: state.currentStep,
        status: state.status,
        createdAt: now,
      },
    });

    return workflowId;
  }

  async updateWorkflowState(workflowId: string, updates: Partial<IWorkflowState>): Promise<void> {
    const currentState = await this.getWorkflowState(workflowId);
    if (!currentState) {
      throw new Error(`Workflow state ${workflowId} not found`);
    }

    const now = new Date().toISOString();
    const updatedState = { ...currentState, ...updates, updatedAt: now };

    // Build dynamic update query
    const updateFields = [];
    const updateValues = [];
    let paramIndex = 1;

    for (const [key, value] of Object.entries(updates)) {
      if (key === 'id' || key === 'createdAt' || key === 'updatedAt') continue;

      updateFields.push(`${this.camelToSnake(key)} = $${paramIndex}`);
      updateValues.push(key === 'context' || key === 'metadata' ? JSON.stringify(value) : value);
      paramIndex++;
    }

    updateFields.push(`updated_at = $${paramIndex}`);
    updateValues.push(now);
    updateValues.push(workflowId);

    await this.db.query(
      `
      UPDATE workflow_states 
      SET ${updateFields.join(', ')}
      WHERE id = $${paramIndex + 1}
    `,
      updateValues
    );

    this.logger.info('Workflow state updated', {
      workflowId,
      updates: Object.keys(updates),
    });

    await this.eventEmitter.emit({
      type: 'WORKFLOW.STATE.UPDATED',
      tags: {
        workflowId,
        step: updatedState.currentStep.toString(),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        workflowId,
        previousState: currentState,
        updates,
        updatedAt: now,
      },
    });
  }

  async getWorkflowState(workflowId: string): Promise<IWorkflowState | null> {
    const result = await this.db.query(
      `
      SELECT * FROM workflow_states WHERE id = $1
    `,
      [workflowId]
    );

    if (result.rows.length === 0) {
      return null;
    }

    return this.mapRowToWorkflowState(result.rows[0]);
  }

  async getWorkflowStates(filter?: WorkflowFilter): Promise<IWorkflowState[]> {
    let query = 'SELECT * FROM workflow_states WHERE 1=1';
    const params = [];
    let paramIndex = 1;

    if (filter) {
      if (filter.status) {
        query += ` AND status = $${paramIndex}`;
        params.push(filter.status);
        paramIndex++;
      }

      if (filter.platform) {
        query += ` AND platform = $${paramIndex}`;
        params.push(filter.platform);
        paramIndex++;
      }

      if (filter.issueId) {
        query += ` AND issue_id = $${paramIndex}`;
        params.push(filter.issueId);
        paramIndex++;
      }

      if (filter.createdAfter) {
        query += ` AND created_at >= $${paramIndex}`;
        params.push(filter.createdAfter);
        paramIndex++;
      }

      if (filter.createdBefore) {
        query += ` AND created_at <= $${paramIndex}`;
        params.push(filter.createdBefore);
        paramIndex++;
      }

      if (filter.limit) {
        query += ` LIMIT $${paramIndex}`;
        params.push(filter.limit);
        paramIndex++;
      }

      if (filter.offset) {
        query += ` OFFSET $${paramIndex}`;
        params.push(filter.offset);
        paramIndex++;
      }
    }

    query += ' ORDER BY created_at DESC';

    const result = await this.db.query(query, params);
    return result.rows.map((row) => this.mapRowToWorkflowState(row));
  }

  async getWorkflowHistory(workflowId: string): Promise<WorkflowStateHistory[]> {
    const result = await this.db.query(
      `
      SELECT * FROM workflow_state_history 
      WHERE workflow_id = $1 
      ORDER BY changed_at ASC
    `,
      [workflowId]
    );

    return result.rows.map((row) => ({
      workflowId: row.workflow_id,
      changedAt: row.changed_at,
      changes: JSON.parse(row.changes),
      changedBy: row.changed_by,
    }));
  }

  private mapRowToWorkflowState(row: any): IWorkflowState {
    return {
      id: row.id,
      issueId: row.issue_id,
      platform: row.platform,
      repository: row.repository,
      currentStep: row.current_step,
      status: row.status,
      context: JSON.parse(row.context),
      createdAt: row.created_at,
      updatedAt: row.updated_at,
      startedAt: row.started_at,
      completedAt: row.completed_at,
      failedAt: row.failed_at,
      metadata: JSON.parse(row.metadata),
    };
  }

  private camelToSnake(str: string): string {
    return str.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`);
  }
}
```

### 1.5 Startup and Shutdown Sequences

```typescript
// packages/orchestrator/src/lifecycle/orchestrator-lifecycle.ts

export class OrchestratorLifecycle {
  private orchestrator: IOrchestrator;
  private logger: ILogger;
  private shutdownTimeout: number = 30000; // 30 seconds

  constructor(orchestrator: IOrchestrator, logger: ILogger) {
    this.orchestrator = orchestrator;
    this.logger = logger;
  }

  async startup(): Promise<void> {
    this.logger.info('Starting orchestrator startup sequence...');

    try {
      // Phase 1: Initialize core services
      await this.initializeCoreServices();

      // Phase 2: Start HTTP server
      await this.startHttpServer();

      // Phase 3: Start background services
      await this.startBackgroundServices();

      // Phase 4: Run health checks
      await this.runStartupHealthChecks();

      // Phase 5: Emit startup complete event
      await this.emitStartupComplete();

      this.logger.info('Orchestrator startup sequence completed successfully');
    } catch (error) {
      this.logger.error('Orchestrator startup failed', { error });
      await this.handleStartupFailure(error);
      throw error;
    }
  }

  async shutdown(): Promise<void> {
    this.logger.info('Starting orchestrator shutdown sequence...');

    try {
      // Phase 1: Stop accepting new requests
      await this.stopAcceptingRequests();

      // Phase 2: Wait for in-flight operations
      await this.waitForInFlightOperations();

      // Phase 3: Stop background services
      await this.stopBackgroundServices();

      // Phase 4: Stop HTTP server
      await this.stopHttpServer();

      // Phase 5: Cleanup resources
      await this.cleanupResources();

      // Phase 6: Emit shutdown complete event
      await this.emitShutdownComplete();

      this.logger.info('Orchestrator shutdown sequence completed successfully');
    } catch (error) {
      this.logger.error('Error during orchestrator shutdown', { error });
      throw error;
    }
  }

  private async initializeCoreServices(): Promise<void> {
    this.logger.info('Phase 1: Initializing core services...');

    const services = this.orchestrator.getServices();

    // Initialize database connections
    await this.initializeDatabase();

    // Initialize event emitter
    await services.eventEmitter.initialize();

    // Initialize task queue
    await services.taskQueue.initialize();

    // Initialize worker pool
    await services.workerPool.initialize();

    // Initialize workflow engine
    await services.workflowEngine.initialize();

    // Initialize state manager
    await services.stateManager.initialize();

    this.logger.info('Core services initialized successfully');
  }

  private async startHttpServer(): Promise<void> {
    this.logger.info('Phase 2: Starting HTTP server...');

    await this.orchestrator.start();

    this.logger.info('HTTP server started successfully');
  }

  private async startBackgroundServices(): Promise<void> {
    this.logger.info('Phase 3: Starting background services...');

    const services = this.orchestrator.getServices();

    // Start workflow engine
    await services.workflowEngine.start();

    // Start worker pool monitoring
    await services.workerPool.startMonitoring();

    // Start task queue processing
    await services.taskQueue.startProcessing();

    // Start metrics collection
    await services.metrics.startCollection();

    this.logger.info('Background services started successfully');
  }

  private async runStartupHealthChecks(): Promise<void> {
    this.logger.info('Phase 4: Running startup health checks...');

    const health = await this.orchestrator.healthCheck();

    if (health.status !== 'healthy') {
      const unhealthyServices = health.checks
        .filter((check) => check.status !== 'healthy')
        .map((check) => check.name);

      throw new Error(`Health check failed for services: ${unhealthyServices.join(', ')}`);
    }

    this.logger.info('All startup health checks passed');
  }

  private async stopAcceptingRequests(): Promise<void> {
    this.logger.info('Phase 1: Stopping acceptance of new requests...');

    const services = this.orchestrator.getServices();

    // Pause task queue
    await services.taskQueue.pause();

    // Set server to draining mode
    await this.setDrainingMode();

    this.logger.info('Stopped accepting new requests');
  }

  private async waitForInFlightOperations(): Promise<void> {
    this.logger.info('Phase 2: Waiting for in-flight operations to complete...');

    const services = this.orchestrator.getServices();

    // Wait for active workflows to complete
    await this.waitForWorkflowsCompletion(services.workflowEngine);

    // Wait for active tasks to complete
    await this.waitForTasksCompletion(services.taskQueue);

    this.logger.info('All in-flight operations completed');
  }

  private async waitForWorkflowsCompletion(workflowEngine: IWorkflowEngine): Promise<void> {
    const startTime = Date.now();
    const maxWaitTime = this.shutdownTimeout;

    while (Date.now() - startTime < maxWaitTime) {
      const activeWorkflows = await workflowEngine.getActiveWorkflows();

      if (activeWorkflows.length === 0) {
        break;
      }

      this.logger.info(`Waiting for ${activeWorkflows.length} active workflows to complete...`);
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    const remainingWorkflows = await workflowEngine.getActiveWorkflows();
    if (remainingWorkflows.length > 0) {
      this.logger.warn(`Forcing shutdown with ${remainingWorkflows.length} workflows still active`);
    }
  }

  private async waitForTasksCompletion(taskQueue: ITaskQueue): Promise<void> {
    const startTime = Date.now();
    const maxWaitTime = this.shutdownTimeout;

    while (Date.now() - startTime < maxWaitTime) {
      const stats = await taskQueue.getStats();

      if (stats.running === 0) {
        break;
      }

      this.logger.info(`Waiting for ${stats.running} running tasks to complete...`);
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    const stats = await taskQueue.getStats();
    if (stats.running > 0) {
      this.logger.warn(`Forcing shutdown with ${stats.running} tasks still running`);
    }
  }

  private async stopBackgroundServices(): Promise<void> {
    this.logger.info('Phase 3: Stopping background services...');

    const services = this.orchestrator.getServices();

    // Stop metrics collection
    await services.metrics.stopCollection();

    // Stop task queue processing
    await services.taskQueue.stopProcessing();

    // Stop worker pool monitoring
    await services.workerPool.stopMonitoring();

    // Stop workflow engine
    await services.workflowEngine.stop();

    this.logger.info('Background services stopped successfully');
  }

  private async stopHttpServer(): Promise<void> {
    this.logger.info('Phase 4: Stopping HTTP server...');

    await this.orchestrator.stop();

    this.logger.info('HTTP server stopped successfully');
  }

  private async cleanupResources(): Promise<void> {
    this.logger.info('Phase 5: Cleaning up resources...');

    const services = this.orchestrator.getServices();

    // Close database connections
    await this.closeDatabaseConnections();

    // Dispose services
    await services.eventEmitter.dispose();
    await services.taskQueue.dispose();
    await services.workerPool.dispose();
    await services.workflowEngine.dispose();
    await services.stateManager.dispose();

    this.logger.info('Resources cleaned up successfully');
  }

  private async setDrainingMode(): Promise<void> {
    // Implementation for setting server to draining mode
    // This would involve setting a flag and having middleware check it
  }

  private async initializeDatabase(): Promise<void> {
    // Implementation for database initialization
  }

  private async closeDatabaseConnections(): Promise<void> {
    // Implementation for closing database connections
  }

  private async emitStartupComplete(): Promise<void> {
    const services = this.orchestrator.getServices();

    await services.eventEmitter.emit({
      type: 'ORCHESTRATOR.STARTUP.COMPLETE',
      tags: {},
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        timestamp: new Date().toISOString(),
        services: Object.keys(services),
      },
    });
  }

  private async emitShutdownComplete(): Promise<void> {
    const services = this.orchestrator.getServices();

    await services.eventEmitter.emit({
      type: 'ORCHESTRATOR.SHUTDOWN.COMPLETE',
      tags: {},
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        timestamp: new Date().toISOString(),
      },
    });
  }

  private async handleStartupFailure(error: Error): Promise<void> {
    const services = this.orchestrator.getServices();

    await services.eventEmitter.emit({
      type: 'ORCHESTRATOR.STARTUP.FAILED',
      tags: {},
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        timestamp: new Date().toISOString(),
        error: error.message,
        stack: error.stack,
      },
    });
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// packages/orchestrator/src/__tests__/orchestrator.test.ts

describe('Orchestrator', () => {
  let orchestrator: Orchestrator;
  let mockServices: MockOrchestratorServices;

  beforeEach(() => {
    mockServices = createMockServices();
    orchestrator = new Orchestrator(
      mockServices.taskQueue,
      mockServices.workerPool,
      mockServices.workflowEngine,
      mockServices.stateManager,
      mockServices.eventEmitter,
      mockServices.logger,
      mockServices.metrics
    );
  });

  describe('initialization', () => {
    it('should initialize with valid configuration', async () => {
      const config = createValidConfig();

      await orchestrator.initialize(config);

      expect(orchestrator.getServices().httpServer).toBeDefined();
      expect(orchestrator.getServices().webSocketServer).toBeDefined();
    });

    it('should throw error with invalid configuration', async () => {
      const config = createInvalidConfig();

      await expect(orchestrator.initialize(config)).rejects.toThrow();
    });
  });

  describe('lifecycle', () => {
    it('should start successfully', async () => {
      const config = createValidConfig();
      await orchestrator.initialize(config);

      await orchestrator.start();

      expect(orchestrator.getServices().httpServer.server.listening).toBe(true);
    });

    it('should stop gracefully', async () => {
      const config = createValidConfig();
      await orchestrator.initialize(config);
      await orchestrator.start();

      await orchestrator.stop();

      expect(orchestrator.getServices().httpServer.server.listening).toBe(false);
    });
  });

  describe('health checks', () => {
    it('should return healthy status when all services are healthy', async () => {
      mockServices.taskQueue.healthCheck.mockResolvedValue({ healthy: true });
      mockServices.workerPool.healthCheck.mockResolvedValue({ healthy: true });
      mockServices.workflowEngine.healthCheck.mockResolvedValue({ healthy: true });
      mockServices.stateManager.healthCheck.mockResolvedValue({ healthy: true });
      mockServices.eventEmitter.healthCheck.mockResolvedValue({ healthy: true });

      const health = await orchestrator.healthCheck();

      expect(health.status).toBe('healthy');
    });

    it('should return unhealthy status when any service is unhealthy', async () => {
      mockServices.taskQueue.healthCheck.mockResolvedValue({ healthy: false });

      const health = await orchestrator.healthCheck();

      expect(health.status).toBe('unhealthy');
    });
  });
});
```

## Completion Checklist

- [ ] Document orchestrator responsibilities and scope
- [ ] Design orchestrator service architecture with Fastify and WebSocket
- [ ] Define task queue management and worker pool coordination
- [ ] Design state management and persistence strategy
- [ ] Document orchestrator startup and shutdown sequences
- [ ] Create comprehensive TypeScript interfaces and types
- [ ] Implement orchestrator class with all required methods
- [ ] Add comprehensive error handling and logging
- [ ] Create unit tests for all orchestrator components
- [ ] Add integration tests for startup/shutdown sequences
- [ ] Document architecture decisions and trade-offs
- [ ] Create sequence diagrams for orchestrator workflows

## Dependencies

- Task 2: Worker Mode Architecture (for coordination patterns)
- Task 3: Shared Components and Interfaces (for common infrastructure)
- Task 4: Sequence Diagrams and Workflows (for visual documentation)
- Task 5: State Persistence and Recovery Strategy (for data management)
- Task 6: Integration Points and APIs (for external interfaces)

## Estimated Time

**Orchestrator Responsibilities**: 2-3 days
**Service Architecture Design**: 3-4 days
**Task Queue and Worker Pool**: 4-5 days
**State Management Design**: 3-4 days
**Startup/Shutdown Sequences**: 2-3 days
**Implementation and Testing**: 4-5 days
**Total**: 18-24 days
