---
title: "Story 6-10: Scrum Master Task Loop - Implementation Plan"
sidebar:
  order: 60
---

**Epic**: Epic 6 - Context & Knowledge Management
**Status**: Ready for Development
**Priority**: P1
**Estimated Effort**: 3-4 weeks

## Overview

This implementation plan details the structured approach to building the Scrum Master Task Loop - a mini state machine that governs task assignment, planning, approval, implementation, review, and learning capture within the Tamma platform.

## Package Location

The Scrum Master Task Loop will be implemented in a new package within the monorepo:

```
packages/
  scrum-master/           # New package
    src/
      index.ts            # Public exports
      task-loop.ts        # Core state machine
      states/             # State handlers
      services/           # Supporting services
      types.ts            # TypeScript interfaces
      config.ts           # Configuration
    tests/
      unit/
      integration/
    package.json
```

**Package Name**: `@tamma/scrum-master`

**Dependencies on Existing Packages**:
- `@tamma/shared` - Common types, errors, event store
- `@tamma/providers` - IAgentProvider, ILLMProvider interfaces
- `@tamma/platforms` - IGitPlatform for repository operations
- `@tamma/orchestrator` - TammaEngine integration

---

## Files to Create/Modify

### New Files

| File Path | Purpose |
|-----------|---------|
| `packages/scrum-master/package.json` | Package configuration |
| `packages/scrum-master/tsconfig.json` | TypeScript configuration |
| `packages/scrum-master/src/index.ts` | Public exports |
| `packages/scrum-master/src/types.ts` | Type definitions |
| `packages/scrum-master/src/config.ts` | Configuration schema |
| `packages/scrum-master/src/task-loop.ts` | Core state machine |
| `packages/scrum-master/src/states/index.ts` | State handlers barrel |
| `packages/scrum-master/src/states/receive-task.ts` | RECEIVE_TASK state |
| `packages/scrum-master/src/states/plan.ts` | PLAN state |
| `packages/scrum-master/src/states/approve.ts` | APPROVE state |
| `packages/scrum-master/src/states/implement.ts` | IMPLEMENT state |
| `packages/scrum-master/src/states/review.ts` | REVIEW state |
| `packages/scrum-master/src/states/learn.ts` | LEARN state |
| `packages/scrum-master/src/states/alert.ts` | ALERT state |
| `packages/scrum-master/src/states/adjust-plan.ts` | ADJUST_PLAN state |
| `packages/scrum-master/src/services/knowledge-checker.ts` | Pre-task knowledge validation |
| `packages/scrum-master/src/services/risk-assessor.ts` | Risk assessment logic |
| `packages/scrum-master/src/services/learning-capture.ts` | Learning extraction |
| `packages/scrum-master/src/services/user-interaction.ts` | User interaction handler |
| `packages/scrum-master/src/services/engine-pool.ts` | Engine pool management |
| `packages/scrum-master/src/services/alert-manager.ts` | Alert handling |
| `packages/scrum-master/src/errors.ts` | Custom error types |

### Files to Modify

| File Path | Changes |
|-----------|---------|
| `packages/shared/src/types/index.ts` | Add ScrumMasterState enum, Task types |
| `packages/shared/src/event-store.ts` | Add Scrum Master event types |
| `packages/orchestrator/src/engine.ts` | Integration hooks for Scrum Master |
| `pnpm-workspace.yaml` | Register new package (if not auto-discovered) |

---

## Interfaces and Types

### Core Types (`packages/scrum-master/src/types.ts`)

```typescript
// State machine states
export enum ScrumMasterState {
  RECEIVE_TASK = 'RECEIVE_TASK',
  PLAN = 'PLAN',
  APPROVE = 'APPROVE',
  IMPLEMENT = 'IMPLEMENT',
  REVIEW = 'REVIEW',
  LEARN = 'LEARN',
  ALERT = 'ALERT',
  ADJUST_PLAN = 'ADJUST_PLAN',
  COMPLETE = 'COMPLETE',
  CANCELLED = 'CANCELLED',
}

// Task definition
export interface Task {
  id: string;
  projectId: string;
  title: string;
  description: string;
  type: TaskType;
  priority: TaskPriority;
  issueNumber?: number;
  issueUrl?: string;
  labels: string[];
  createdAt: Date;
  metadata?: Record<string, unknown>;
}

export type TaskType =
  | 'feature'
  | 'bugfix'
  | 'refactor'
  | 'test'
  | 'docs'
  | 'chore';

export type TaskPriority = 'low' | 'medium' | 'high' | 'critical';

// Development plan
export interface ScrumMasterPlan {
  taskId: string;
  summary: string;
  approach: string;
  fileChanges: PlannedFileChange[];
  testingStrategy: string;
  complexity: 'low' | 'medium' | 'high';
  estimatedTokens: number;
  estimatedCostUsd: number;
  risks: string[];
  dependencies: string[];
  generatedAt: Date;
  version: number;
}

export interface PlannedFileChange {
  path: string;
  action: 'create' | 'modify' | 'delete';
  description: string;
  estimatedLines: number;
}

// Task loop context
export interface TaskLoopContext {
  task: Task;
  plan?: ScrumMasterPlan;
  knowledgeCheck?: KnowledgeCheckResult;
  approvalStatus?: ApprovalStatus;
  implementation?: ImplementationResult;
  review?: ReviewResult;
  learnings?: LearningCapture[];
  retryCount: number;
  maxRetries: number;
  startTime: Date;
  errors: TaskError[];
}

// Knowledge check result
export interface KnowledgeCheckResult {
  canProceed: boolean;
  recommendations: KnowledgeMatch[];
  warnings: KnowledgeMatch[];
  blockers: KnowledgeMatch[];
  learnings: KnowledgeMatch[];
  summary: string;
}

export interface KnowledgeMatch {
  knowledge: KnowledgeEntry;
  matchReason: string;
  relevanceScore: number;
}

// Approval status
export interface ApprovalStatus {
  status: 'pending' | 'approved' | 'rejected' | 'auto_approved';
  approvedBy?: string;
  approvedAt?: Date;
  reason?: string;
  adjustments?: PlanAdjustment[];
}

export interface PlanAdjustment {
  field: string;
  originalValue: unknown;
  newValue: unknown;
  reason: string;
}

// Implementation result
export interface ImplementationResult {
  success: boolean;
  output: string;
  costUsd: number;
  durationMs: number;
  filesModified: string[];
  testsRun: number;
  testsPassed: number;
  error?: string;
  sessionId?: string;
}

// Review result
export interface ReviewResult {
  passed: boolean;
  score: number; // 0-100
  issues: ReviewIssue[];
  suggestions: string[];
  qualityChecks: QualityCheck[];
}

export interface ReviewIssue {
  severity: 'info' | 'warning' | 'error';
  category: 'code' | 'test' | 'style' | 'security' | 'performance';
  message: string;
  file?: string;
  line?: number;
}

export interface QualityCheck {
  name: string;
  passed: boolean;
  message: string;
}

// Learning capture
export interface LearningCapture {
  taskId: string;
  type: 'success' | 'failure' | 'improvement';
  title: string;
  description: string;
  keywords: string[];
  priority: 'low' | 'medium' | 'high';
  suggestedKnowledgeType: 'recommendation' | 'prohibition' | 'learning';
  status: 'pending' | 'approved' | 'rejected';
}

// Task result
export interface TaskResult {
  taskId: string;
  success: boolean;
  state: ScrumMasterState;
  plan?: ScrumMasterPlan;
  implementation?: ImplementationResult;
  review?: ReviewResult;
  learnings: LearningCapture[];
  totalCostUsd: number;
  totalDurationMs: number;
  retryCount: number;
  error?: string;
  completedAt: Date;
}

// Task error
export interface TaskError {
  state: ScrumMasterState;
  message: string;
  timestamp: Date;
  recoverable: boolean;
  context?: Record<string, unknown>;
}
```

### Service Interfaces

```typescript
// Knowledge service interface (from Story 6-9)
export interface IKnowledgeService {
  checkBeforeTask(task: Task, plan: ScrumMasterPlan): Promise<KnowledgeCheckResult>;
  captureLearning(learning: LearningCapture): Promise<void>;
  getRelevantKnowledge(query: KnowledgeQuery): Promise<KnowledgeResult>;
}

// Cost monitor interface (from Story 6-7)
export interface ICostMonitor {
  estimateCost(request: CostEstimateRequest): Promise<CostEstimate>;
  recordUsage(usage: UsageRecord): Promise<void>;
  checkLimit(context: LimitContext): Promise<LimitCheckResult>;
}

// Permission service interface (from Story 6-8)
export interface IPermissionService {
  checkToolPermission(agentType: string, projectId: string, tool: string): PermissionResult;
  checkFilePermission(agentType: string, projectId: string, path: string, action: 'read' | 'write'): PermissionResult;
  getEffectivePermissions(agentType: string, projectId: string): AgentPermissionSet;
}

// Engine pool interface
export interface IEnginePool {
  acquire(projectId: string): Promise<IEngine>;
  release(engine: IEngine): Promise<void>;
  getAvailableCount(): number;
  getStatus(): EnginePoolStatus;
}

export interface IEngine {
  id: string;
  status: 'idle' | 'busy' | 'error';
  execute(
    prompt: string,
    config: EngineExecutionConfig,
    onProgress?: (event: EngineProgressEvent) => void
  ): Promise<ImplementationResult>;
}

// Alert manager interface
export interface IAlertManager {
  send(alert: Alert): Promise<void>;
  getActiveAlerts(): Promise<Alert[]>;
  acknowledge(alertId: string): Promise<void>;
}

export interface Alert {
  id: string;
  type: AlertType;
  severity: 'info' | 'warning' | 'critical';
  title: string;
  details: string;
  taskId?: string;
  actions: string[];
  createdAt: Date;
}

export type AlertType =
  | 'approval_needed'
  | 'task_blocked'
  | 'max_retries_exceeded'
  | 'review_failed'
  | 'cost_limit_warning'
  | 'error';

// User interaction interface
export interface IUserInterface {
  notifyUser(message: string, data?: Record<string, unknown>): void;
  waitForResponse(options: WaitOptions): Promise<UserResponse>;
  requestApproval(plan: ScrumMasterPlan, riskLevel: RiskLevel): Promise<ApprovalResponse>;
  promptForAction(prompt: string, actions: string[]): Promise<string>;
}

export interface UserResponse {
  action: string;
  message?: string;
  data?: Record<string, unknown>;
}

export interface ApprovalResponse {
  approved: boolean;
  adjustments?: PlanAdjustment[];
  reason?: string;
}

export type RiskLevel = 'low' | 'medium' | 'high';
```

### Configuration Types (`packages/scrum-master/src/config.ts`)

```typescript
export interface ScrumMasterConfig {
  taskLoop: TaskLoopConfig;
  riskThresholds: RiskThresholdConfig;
  learningCapture: LearningCaptureConfig;
  alerts: AlertConfig;
  userInteraction: UserInteractionConfig;
}

export interface TaskLoopConfig {
  maxRetries: number;
  autoApproveLowRisk: boolean;
  requireApprovalHighRisk: boolean;
  timeoutMs: number;
  progressUpdateIntervalMs: number;
}

export interface RiskThresholdConfig {
  low: {
    maxFiles: number;
    maxComplexity: 'low';
    maxEstimatedCostUsd: number;
  };
  medium: {
    maxFiles: number;
    maxComplexity: 'medium';
    maxEstimatedCostUsd: number;
  };
}

export interface LearningCaptureConfig {
  captureSuccess: boolean;
  captureFailure: boolean;
  requireApproval: boolean;
  minRelevanceScore: number;
}

export interface AlertConfig {
  onBlock: boolean;
  onMaxRetries: boolean;
  onApprovalNeeded: boolean;
  onReviewFailed: boolean;
  channels: AlertChannel[];
}

export interface AlertChannel {
  type: 'cli' | 'webhook' | 'slack' | 'email';
  enabled: boolean;
  config?: Record<string, unknown>;
}

export interface UserInteractionConfig {
  proactiveUpdates: boolean;
  updateIntervalSeconds: number;
  autoTimeoutMinutes: number;
}

// Default configuration
export const DEFAULT_SCRUM_MASTER_CONFIG: ScrumMasterConfig = {
  taskLoop: {
    maxRetries: 3,
    autoApproveLowRisk: true,
    requireApprovalHighRisk: true,
    timeoutMs: 3600000, // 1 hour
    progressUpdateIntervalMs: 30000, // 30 seconds
  },
  riskThresholds: {
    low: {
      maxFiles: 5,
      maxComplexity: 'low',
      maxEstimatedCostUsd: 1.0,
    },
    medium: {
      maxFiles: 10,
      maxComplexity: 'medium',
      maxEstimatedCostUsd: 5.0,
    },
  },
  learningCapture: {
    captureSuccess: true,
    captureFailure: true,
    requireApproval: true,
    minRelevanceScore: 0.7,
  },
  alerts: {
    onBlock: true,
    onMaxRetries: true,
    onApprovalNeeded: true,
    onReviewFailed: true,
    channels: [
      { type: 'cli', enabled: true },
    ],
  },
  userInteraction: {
    proactiveUpdates: true,
    updateIntervalSeconds: 30,
    autoTimeoutMinutes: 60,
  },
};
```

---

## Implementation Phases

### Phase 1: Core State Machine (Week 1)

**Goal**: Implement the basic state machine with state transitions.

#### Tasks

1. **Package Setup**
   - Create `packages/scrum-master` directory structure
   - Configure `package.json` with dependencies
   - Set up TypeScript configuration
   - Add to workspace

2. **Type Definitions**
   - Implement all types in `types.ts`
   - Add ScrumMasterState enum to `@tamma/shared`
   - Add Scrum Master event types to event store

3. **Core Task Loop**
   - Implement `ScrumMasterTaskLoop` class
   - Implement state transition logic
   - Add context management
   - Implement basic error handling

4. **State Handlers (Stubs)**
   - Create stub implementations for all states
   - Define handler interfaces
   - Implement state factory pattern

#### Deliverables
- Working state machine that can transition through all states
- Unit tests for state transitions
- Basic context management

### Phase 2: Planning & Approval (Week 2)

**Goal**: Implement PLAN and APPROVE states with knowledge base integration.

#### Tasks

1. **PLAN State Handler**
   - Implement context gathering
   - Implement plan generation using LLM
   - Add plan validation logic
   - Integrate with cost estimation

2. **APPROVE State Handler**
   - Implement knowledge base checking (interface only, full impl in Story 6-9)
   - Implement risk assessment logic
   - Add approval workflow (auto/manual)
   - Handle plan adjustments

3. **Knowledge Checker Service**
   - Create `IKnowledgeService` interface adapter
   - Implement mock service for testing
   - Add blocker/warning/recommendation handling

4. **Risk Assessor Service**
   - Implement risk scoring algorithm
   - Add complexity assessment
   - Implement critical file detection

#### Deliverables
- Working plan generation
- Risk-based approval workflow
- Knowledge check integration (mock)
- Integration tests for PLAN/APPROVE flow

### Phase 3: Implementation & Review (Week 2-3)

**Goal**: Implement IMPLEMENT and REVIEW states with engine pool integration.

#### Tasks

1. **Engine Pool Service**
   - Implement engine pool interface
   - Add engine acquisition/release logic
   - Implement resource tracking
   - Add timeout handling

2. **IMPLEMENT State Handler**
   - Integrate with engine pool
   - Build implementation prompt from plan
   - Monitor implementation progress
   - Track costs and duration
   - Handle failures

3. **REVIEW State Handler**
   - Implement quality checks (tests, lint, type check)
   - Check for prohibited patterns
   - Generate review score
   - Determine pass/fail

4. **ADJUST_PLAN State Handler**
   - Analyze failure reasons
   - Generate adjusted plan
   - Track retry count
   - Handle max retries

#### Deliverables
- Engine pool management
- Full implementation flow
- Review and quality checks
- Retry mechanism

### Phase 4: Learning & Alerts (Week 3)

**Goal**: Implement LEARN and ALERT states with user interaction.

#### Tasks

1. **LEARN State Handler**
   - Implement learning extraction
   - Capture success patterns
   - Capture failure patterns
   - Submit learnings for approval

2. **Learning Capture Service**
   - Extract learnings using LLM
   - Deduplicate similar learnings
   - Generate keywords
   - Determine priority

3. **ALERT State Handler**
   - Implement alert routing
   - Add multi-channel support (CLI, webhook)
   - Handle user responses
   - Track alert status

4. **User Interaction Handler**
   - Implement CLI interaction
   - Add status query handling
   - Implement task commands (pause, cancel)
   - Add conversation context

#### Deliverables
- Learning capture system
- Alert management
- User interaction handling
- Full task lifecycle

### Phase 5: Integration & Testing (Week 4)

**Goal**: Full integration testing and edge case handling.

#### Tasks

1. **Integration with TammaEngine**
   - Add hooks in orchestrator
   - Implement Scrum Master as coordinator
   - Add per-project Scrum Master instances

2. **End-to-End Testing**
   - Test complete task lifecycle
   - Test failure scenarios
   - Test approval workflows
   - Test user interactions

3. **Performance Testing**
   - Measure state transition latency
   - Test concurrent task handling
   - Validate resource usage

4. **Documentation**
   - API documentation
   - Integration guide
   - Configuration reference

#### Deliverables
- Full integration with orchestrator
- Comprehensive test suite
- Performance benchmarks
- Documentation

---

## Dependencies

### Internal Dependencies

| Dependency | Required For | Status |
|------------|--------------|--------|
| Story 6-7: Cost Monitoring | Cost estimation, usage tracking | Planned |
| Story 6-8: Agent Permissions | Permission enforcement | Planned |
| Story 6-9: Agent Knowledge Base | Pre-task checking, learning capture | Planned |
| `@tamma/shared` | Types, errors, event store | Available |
| `@tamma/providers` | IAgentProvider, ILLMProvider | Available |
| `@tamma/orchestrator` | TammaEngine integration | Available |
| `@tamma/platforms` | IGitPlatform | Available |

### External Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| TypeScript | ^5.0.0 | Type safety |
| Vitest | ^1.0.0 | Testing |
| Zod | ^3.22.0 | Runtime validation |

### Dependency Resolution Strategy

Since Stories 6-7, 6-8, and 6-9 are planned but not yet implemented, we will:

1. **Define interfaces first** - Create the interfaces these stories will implement
2. **Use mock implementations** - Build mock services for testing
3. **Design for loose coupling** - Use dependency injection to swap implementations
4. **Implement adapters later** - Connect to real implementations when available

---

## Testing Strategy

### Unit Tests

**Location**: `packages/scrum-master/tests/unit/`

| Component | Test Coverage |
|-----------|---------------|
| State machine | State transitions, context updates |
| State handlers | Each state handler in isolation |
| Risk assessor | Risk scoring, thresholds |
| Learning capture | Extraction logic, deduplication |
| Configuration | Validation, defaults |

**Mocking Strategy**:
- Mock `IKnowledgeService` for knowledge checks
- Mock `IEnginePool` for implementation
- Mock `IAlertManager` for alerts
- Mock `IUserInterface` for user interaction

### Integration Tests

**Location**: `packages/scrum-master/tests/integration/`

| Scenario | Coverage |
|----------|----------|
| Full task lifecycle | Plan -> Approve -> Implement -> Review -> Learn |
| Approval workflow | Auto-approve, manual approve, rejection |
| Retry mechanism | Failure -> Adjust -> Retry |
| Alert handling | Block alerts, approval requests |
| User commands | Pause, cancel, status queries |

### End-to-End Tests

**Location**: `packages/scrum-master/tests/e2e/`

| Scenario | Coverage |
|----------|----------|
| Complete task resolution | Issue -> PR -> Merge |
| Multi-engine coordination | Concurrent tasks |
| Error recovery | Network failures, timeouts |

### Test Commands

```bash
# Run all tests
pnpm --filter @tamma/scrum-master test

# Run unit tests only
pnpm --filter @tamma/scrum-master test:unit

# Run integration tests
pnpm --filter @tamma/scrum-master test:integration

# Run with coverage
pnpm --filter @tamma/scrum-master test:coverage
```

---

## Configuration

### Environment Variables

```bash
# Scrum Master configuration
TAMMA_SCRUM_MASTER_MAX_RETRIES=3
TAMMA_SCRUM_MASTER_AUTO_APPROVE_LOW_RISK=true
TAMMA_SCRUM_MASTER_TIMEOUT_MS=3600000

# Alert channels
TAMMA_ALERT_WEBHOOK_URL=https://hooks.example.com/tamma
TAMMA_ALERT_SLACK_CHANNEL=#tamma-alerts
```

### Configuration File (YAML)

```yaml
# config/scrum-master.yaml
scrum_master:
  task_loop:
    max_retries: 3
    auto_approve_low_risk: true
    require_approval_high_risk: true
    timeout_ms: 3600000
    progress_update_interval_ms: 30000

  risk_thresholds:
    low:
      max_files: 5
      max_complexity: low
      max_estimated_cost_usd: 1.0
    medium:
      max_files: 10
      max_complexity: medium
      max_estimated_cost_usd: 5.0

  learning_capture:
    capture_success: true
    capture_failure: true
    require_approval: true
    min_relevance_score: 0.7

  alerts:
    on_block: true
    on_max_retries: true
    on_approval_needed: true
    on_review_failed: true
    channels:
      - type: cli
        enabled: true
      - type: webhook
        enabled: true
        config:
          url: ${TAMMA_ALERT_WEBHOOK_URL}
      - type: slack
        enabled: false
        config:
          channel: ${TAMMA_ALERT_SLACK_CHANNEL}

  user_interaction:
    proactive_updates: true
    update_interval_seconds: 30
    auto_timeout_minutes: 60
```

### Programmatic Configuration

```typescript
import { ScrumMasterTaskLoop, DEFAULT_SCRUM_MASTER_CONFIG } from '@tamma/scrum-master';

const scrumMaster = new ScrumMasterTaskLoop({
  config: {
    ...DEFAULT_SCRUM_MASTER_CONFIG,
    taskLoop: {
      ...DEFAULT_SCRUM_MASTER_CONFIG.taskLoop,
      maxRetries: 5,
    },
  },
  knowledgeService: myKnowledgeService,
  enginePool: myEnginePool,
  alertManager: myAlertManager,
  userInterface: myUserInterface,
  logger: myLogger,
});
```

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Task completion rate | > 85% | Tasks reaching COMPLETE state |
| Average retries | < 1.5 | Mean retries per completed task |
| Learning capture rate | > 90% | Tasks producing learnings |
| Approval turnaround | < 5 min | Time from APPROVE to decision |
| State transition latency | < 100ms | Time between states |
| User satisfaction | > 4/5 | Survey on status updates |

---

## Risk Assessment

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| Knowledge service not ready | High | Medium | Use mock service, design interface carefully |
| Engine pool complexity | Medium | Medium | Start with single engine, scale later |
| State machine edge cases | High | Low | Comprehensive testing, clear state diagrams |
| User interaction blocking | Medium | Medium | Add timeouts, async operations |
| Learning quality | Low | Medium | Manual review process, refinement over time |

---

## Implementation Tasks Breakdown

### Core Tasks (MVP)

1. **Task 1: Package Setup and Configuration**
   - Create package structure
   - Define types and interfaces
   - Set up testing infrastructure

2. **Task 2: State Machine Core**
   - Implement state enum and transitions
   - Build context management
   - Add event recording

3. **Task 3: RECEIVE_TASK and PLAN States**
   - Implement task reception
   - Build plan generation with LLM

4. **Task 4: APPROVE State and Risk Assessment**
   - Implement approval workflow
   - Build risk scoring

5. **Task 5: IMPLEMENT State and Engine Pool**
   - Implement engine pool
   - Build implementation monitoring

6. **Task 6: REVIEW State and Quality Checks**
   - Implement quality validation
   - Build review scoring

7. **Task 7: LEARN State and Learning Capture**
   - Implement learning extraction
   - Build deduplication

8. **Task 8: ALERT State and User Interaction**
   - Implement alert routing
   - Build user command handling

### Integration Tasks

9. **Task 9: TammaEngine Integration**
   - Connect to orchestrator
   - Add Scrum Master coordination

10. **Task 10: End-to-End Testing**
    - Full lifecycle tests
    - Error scenario tests

---

## Appendix

### State Machine Diagram

```
                           ┌──────────────┐
              ┌────────────│   RECEIVE    │◄────────────┐
              │            │    TASK      │             │
              │            └──────┬───────┘             │
              │                   │                     │
              │                   ▼                     │
              │            ┌──────────────┐             │
              │            │     PLAN     │             │
              │            └──────┬───────┘             │
              │                   │                     │
              │                   ▼                     │
              │            ┌──────────────┐             │
  user        │            │   APPROVE    │─────────────┤ blocked
  cancel      │            └──────┬───────┘             │
              │                   │ approved            │
              │                   ▼                     │
              │            ┌──────────────┐             │
              │            │  IMPLEMENT   │             │
              │            └──────┬───────┘             │
              │                   │                     │
              │          ┌────────┴────────┐            │
              │          │                 │            │
              │          ▼                 ▼            │
              │   ┌──────────┐      ┌──────────┐       │
              │   │ SUCCESS  │      │  FAILED  │       │
              │   └────┬─────┘      └────┬─────┘       │
              │        │                 │             │
              │        ▼                 ▼             │
              │   ┌──────────────────────────────┐     │
              │   │          REVIEW              │     │
              │   └──────────────┬───────────────┘     │
              │                  │                     │
              │         ┌────────┴────────┐            │
              │         │                 │            │
              │         ▼                 ▼            │
              │  ┌───────────┐     ┌───────────┐      │
              │  │  PASSED   │     │  FAILED   │──────┼──► retry?
              │  └─────┬─────┘     └───────────┘      │     │
              │        │                              │     │
              │        ▼                              │     │  yes
              │  ┌──────────────┐                     │     │  (max 3)
              │  │    LEARN     │                     │     │
              │  └──────┬───────┘                     │     ▼
              │         │                             │  ┌───────────┐
              │         ▼                             │  │  ADJUST   │
              │  ┌──────────────┐                     │  │   PLAN    │────────┘
              │  │   COMPLETE   │                     │  └───────────┘
              │  └──────────────┘                     │
              │                                       │  no (max retries)
              └──────────────────────────────────────►│     │
                                                      │     ▼
                                                      │  ┌───────────┐
                                                      └──│   ALERT   │
                                                         └───────────┘
```

### Event Types (to add to `@tamma/shared`)

```typescript
export enum ScrumMasterEventType {
  TASK_RECEIVED = 'SM_TASK_RECEIVED',
  PLAN_STARTED = 'SM_PLAN_STARTED',
  PLAN_GENERATED = 'SM_PLAN_GENERATED',
  KNOWLEDGE_CHECKED = 'SM_KNOWLEDGE_CHECKED',
  APPROVAL_REQUESTED = 'SM_APPROVAL_REQUESTED',
  APPROVAL_AUTO = 'SM_APPROVAL_AUTO',
  APPROVAL_GRANTED = 'SM_APPROVAL_GRANTED',
  APPROVAL_DENIED = 'SM_APPROVAL_DENIED',
  IMPLEMENTATION_STARTED = 'SM_IMPLEMENTATION_STARTED',
  IMPLEMENTATION_PROGRESS = 'SM_IMPLEMENTATION_PROGRESS',
  IMPLEMENTATION_COMPLETED = 'SM_IMPLEMENTATION_COMPLETED',
  IMPLEMENTATION_FAILED = 'SM_IMPLEMENTATION_FAILED',
  REVIEW_STARTED = 'SM_REVIEW_STARTED',
  REVIEW_PASSED = 'SM_REVIEW_PASSED',
  REVIEW_FAILED = 'SM_REVIEW_FAILED',
  PLAN_ADJUSTED = 'SM_PLAN_ADJUSTED',
  LEARNING_CAPTURED = 'SM_LEARNING_CAPTURED',
  ALERT_SENT = 'SM_ALERT_SENT',
  ALERT_ACKNOWLEDGED = 'SM_ALERT_ACKNOWLEDGED',
  TASK_COMPLETED = 'SM_TASK_COMPLETED',
  TASK_CANCELLED = 'SM_TASK_CANCELLED',
  STATE_TRANSITION = 'SM_STATE_TRANSITION',
  ERROR_OCCURRED = 'SM_ERROR_OCCURRED',
}
```

---

**Last Updated**: 2026-02-05
**Author**: Tamma Development Team
**Implementation Start**: TBD
**Target Completion**: 4 weeks from start
