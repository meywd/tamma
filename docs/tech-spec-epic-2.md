# Epic Technical Specification: Autonomous Development Loop - Core

Date: 2025-10-28
Author: BMad
Epic ID: 2
Status: Draft

---

## Overview

Epic 2 implements the core 14-step autonomous development loop (PRD FR-1 through FR-6) that transforms Tamma from a foundational infrastructure platform into an operational autonomous development system. Building on Epic 1's multi-provider AI abstraction and multi-platform Git integration, this epic delivers the workflow orchestration engine that executes the complete lifecycle: issue selection → context analysis → plan generation → branch creation → test-first implementation → code generation → refactoring → PR creation → status monitoring → merge → auto-next iteration.

This epic introduces **role-based orchestration** as an advanced feature, enabling specialized AI agents (Architect, Developer, Code Reviewer, Test Automation, QA, Debugger) to handle specific workflow phases based on their capabilities and domain expertise. The orchestrator intelligently routes workflow steps to agents with matching roles, enabling parallel execution where dependencies allow while maintaining sequential consistency where required. The 70%+ autonomous completion rate target (NFR-2) depends on this workflow loop's reliability, quality gates enforcement (3-retry limit with mandatory escalation per FR-16), and strategic human approval checkpoints at design review (FR-3, FR-4) and merge completion (FR-19).

## MVP Criticality

**All stories in Epic 2 are MVP CRITICAL** for the self-maintenance goal. The complete 14-step autonomous development loop is required for Tamma to implement features, fix bugs, and maintain its own codebase:

- **Stories 2.1-2.11**: Full workflow from issue selection through PR merge required for end-to-end autonomous development
- **Role-Based Orchestration**: Optional for MVP - single-agent workflow sufficient for self-maintenance validation, multi-agent coordination deferred to post-MVP

Without Epic 2, Tamma cannot perform autonomous development - this is the core workflow that enables self-maintenance.

## Objectives and Scope

**In Scope:**

- **Story 2.1:** Issue selection with label-based filtering and auto-assignment to bot account
- **Story 2.2:** Context analysis extracting issue metadata, related issues, commit history, and file paths
- **Story 2.3:** Development plan generation with approval checkpoint (including ambiguity detection per FR-3 and multi-option proposals per FR-4)
- **Story 2.4:** Git branch creation with conflict handling and naming conventions
- **Story 2.5:** Test-first development generating failing tests (TDD red phase per FR-5)
- **Story 2.6:** Implementation code generation passing tests (TDD green phase per FR-5)
- **Story 2.7:** Optional code refactoring pass (TDD refactor phase per FR-5)
- **Story 2.8:** Pull request creation with automated body generation and reviewer assignment
- **Story 2.9:** PR status monitoring for CI/CD checks and review feedback
- **Story 2.10:** PR merge with completion checkpoint and automatic issue closure
- **Story 2.11:** Auto-next issue selection enabling continuous autonomous operation (FR-2)
- **Role-Based Orchestration:** Agent specialization framework with role assignment (Architect, Developer, Code Reviewer, QA, Debugger), capability-based routing, and workflow phase mapping

**Out of Scope:**

- Build automation with retry logic (Epic 3, Story 3-1)
- Test execution with retry logic (Epic 3, Story 3-2)
- Research capability for unfamiliar concepts (Epic 3, Story 3-4)
- Static analysis and security scanning integration (Epic 3, Stories 3-8, 3-9)
- Event sourcing implementation (Epic 4)
- Time-travel debugging and black-box replay (Epic 4)
- Observability dashboards and alerting (Epic 5)
- Multi-platform CI/CD orchestration beyond GitHub Actions (deferred to post-MVP)

## System Architecture Alignment

Epic 2 implements the core workflow orchestration components referenced in Architecture Section 3.3 (Hybrid Architecture Pattern). The autonomous development loop operates in all three modes: standalone CLI executes the complete 14-step loop locally; orchestrator mode manages task distribution to workers with role-based routing; worker mode executes assigned workflow phases within their role capabilities. The workflow engine integrates with Epic 1's `AIProviderInterface` for code generation and plan analysis, and Epic 1's `GitPlatformInterface` for all repository operations (branch creation, PR management, merge operations). The role-based orchestration system extends Epic 1's `WorkerRegistration.capabilities` model by adding a `roles: string[]` field and implementing orchestrator logic to match workflow phases with agent capabilities.

The event-driven communication pattern (Architecture Section 3.3) enables workflow state transitions to emit events captured by the DCB event sourcing system (Epic 4 dependency). The approval checkpoint pattern implements FR-19 requirements through synchronous user interaction in standalone mode and asynchronous notification/approval queue in orchestrator mode. The 3-retry limit with mandatory escalation (FR-16) is enforced through workflow state machine transitions with exponential backoff between retries.

## Detailed Design

### Services and Modules

**1. Workflow Orchestration Engine (`packages/workflow-engine`)**

*Autonomous Development Loop Coordinator (Stories 2.1-2.11):*
- `WorkflowStateMachine` class managing 14-step state transitions with exponential backoff retry logic
- `IssueSelector` service implementing label-based filtering, priority sorting, and auto-assignment (Story 2.1)
- `ContextAnalyzer` service extracting issue metadata, related issues, commit history, and file paths (Story 2.2)
- `PlanGenerator` service orchestrating AI provider for development plan with approval checkpoint (Story 2.3)
- `BranchManager` service creating feature branches with conflict resolution (Story 2.4)
- `TestGenerator` service implementing TDD red phase with failing test generation (Story 2.5)
- `CodeGenerator` service implementing TDD green phase with passing implementation (Story 2.6)
- `RefactoringOrchestrator` service managing optional refactoring with approval (Story 2.7)
- `PRManager` service creating PRs with automated body generation (Story 2.8)
- `StatusMonitor` service polling PR status for CI/CD and reviews (Story 2.9)
- `MergeCoordinator` service executing merge with completion checkpoint (Story 2.10)
- `LoopCoordinator` service managing auto-next iteration and graceful shutdown (Story 2.11)

**2. Role-Based Orchestration System (`packages/workflow-engine/roles`)**

*Agent Role Management:*
- `RoleRegistry` class defining available agent roles and their capabilities
- `RoleRouter` service matching workflow phases to agents with required roles
- `CapabilityMatcher` algorithm for finding optimal agent based on current load and specialization
- `WorkflowPhaseMapper` configuration mapping each workflow step to required role(s)
- `ParallelExecutionPlanner` service identifying independent workflow phases for concurrent execution

*Supported Agent Roles:*
- **Architect:** Plan generation (Story 2.3), ambiguity detection, multi-option design proposals
- **Developer:** Code generation (Stories 2.5, 2.6), refactoring (Story 2.7), test implementation
- **Code Reviewer:** PR review analysis, code quality assessment, feedback generation
- **Test Automation:** Test generation (Story 2.5), test execution orchestration, coverage analysis
- **QA:** End-to-end testing coordination, integration test validation, acceptance criteria verification
- **Debugger:** Failure analysis when retries fail, error diagnosis, fix suggestion generation

**3. Approval Checkpoint System (`packages/workflow-engine/approvals`)**

*Human-in-the-Loop Integration:*
- `ApprovalCheckpoint` interface for synchronous (standalone) and asynchronous (orchestrator) approvals
- `PlanApprovalCheckpoint` implementation for Story 2.3 design review (FR-3, FR-4)
- `RefactoringApprovalCheckpoint` implementation for Story 2.7 optional refactoring
- `MergeApprovalCheckpoint` implementation for Story 2.10 completion (FR-19)
- `ApprovalNotificationQueue` for orchestrator mode with webhook support
- `ApprovalTimeoutHandler` with configurable timeout and escalation logic

**4. Retry and Escalation Framework (`packages/workflow-engine/resilience`)**

*Quality Gates Enforcement (FR-16):*
- `RetryPolicy` class implementing 3-retry limit with exponential backoff
- `EscalationTrigger` service detecting mandatory escalation conditions
- `FailureAnalyzer` service categorizing failures (transient vs permanent)
- `EscalationNotifier` service alerting humans when retries exhausted
- Circuit breaker pattern preventing cascade failures to AI providers

### Data Models and Contracts

**1. Workflow State Models**

```typescript
interface WorkflowExecution {
  executionId: string;
  issueReference: {
    platform: string;
    repository: string;
    issueNumber: number;
  };
  currentPhase: WorkflowPhase;
  state: 'running' | 'paused' | 'awaiting-approval' | 'completed' | 'failed' | 'cancelled';
  phaseHistory: PhaseTransition[];
  assignedAgents: Map<WorkflowPhase, string>; // phase -> agentId
  retryCount: number;
  createdAt: Date;
  updatedAt: Date;
  completedAt?: Date;
  error?: WorkflowError;
}

enum WorkflowPhase {
  ISSUE_SELECTION = 'issue-selection',
  CONTEXT_ANALYSIS = 'context-analysis',
  PLAN_GENERATION = 'plan-generation',
  PLAN_APPROVAL = 'plan-approval',
  BRANCH_CREATION = 'branch-creation',
  TEST_GENERATION = 'test-generation',
  CODE_GENERATION = 'code-generation',
  REFACTORING = 'refactoring',
  PR_CREATION = 'pr-creation',
  STATUS_MONITORING = 'status-monitoring',
  MERGE = 'merge',
  AUTO_NEXT = 'auto-next'
}

interface PhaseTransition {
  fromPhase: WorkflowPhase | null;
  toPhase: WorkflowPhase;
  agentId?: string;
  agentRole?: AgentRole;
  timestamp: Date;
  result: 'success' | 'failure' | 'retry';
  retryAttempt?: number;
  durationMs: number;
  metadata?: Record<string, unknown>;
}

interface WorkflowError {
  phase: WorkflowPhase;
  errorType: 'transient' | 'permanent' | 'escalation-required';
  message: string;
  stackTrace?: string;
  retryAttempts: number;
  escalated: boolean;
  escalatedAt?: Date;
}
```

**2. Role-Based Agent Models**

```typescript
enum AgentRole {
  ARCHITECT = 'architect',
  DEVELOPER = 'developer',
  CODE_REVIEWER = 'code-reviewer',
  TEST_AUTOMATION = 'test-automation',
  QA = 'qa',
  DEBUGGER = 'debugger',
  GENERALIST = 'generalist' // fallback for agents without specialization
}

interface AgentRegistration extends WorkerRegistration {
  roles: AgentRole[]; // Multiple roles supported
  specializations: {
    languages?: string[]; // e.g., ['typescript', 'python', 'rust']
    frameworks?: string[]; // e.g., ['react', 'fastify', 'jest']
    domains?: string[]; // e.g., ['backend', 'frontend', 'devops']
  };
  performanceMetrics: {
    averagePhaseCompletionTime: Map<WorkflowPhase, number>;
    successRate: number; // 0.0 to 1.0
    tasksCompleted: number;
  };
}

interface RoleCapabilityMatrix {
  role: AgentRole;
  supportedPhases: WorkflowPhase[];
  requiredCapabilities: {
    aiProviders?: string[];
    gitPlatforms?: string[];
    minimumSuccessRate?: number;
  };
  parallelizable: boolean; // Can this role handle multiple tasks concurrently?
}

// Phase -> Role Mapping Configuration
const WORKFLOW_ROLE_MAPPING: Record<WorkflowPhase, AgentRole[]> = {
  [WorkflowPhase.ISSUE_SELECTION]: [AgentRole.GENERALIST],
  [WorkflowPhase.CONTEXT_ANALYSIS]: [AgentRole.ARCHITECT, AgentRole.GENERALIST],
  [WorkflowPhase.PLAN_GENERATION]: [AgentRole.ARCHITECT],
  [WorkflowPhase.PLAN_APPROVAL]: [], // Human checkpoint, no agent
  [WorkflowPhase.BRANCH_CREATION]: [AgentRole.GENERALIST],
  [WorkflowPhase.TEST_GENERATION]: [AgentRole.TEST_AUTOMATION, AgentRole.DEVELOPER],
  [WorkflowPhase.CODE_GENERATION]: [AgentRole.DEVELOPER],
  [WorkflowPhase.REFACTORING]: [AgentRole.CODE_REVIEWER, AgentRole.DEVELOPER],
  [WorkflowPhase.PR_CREATION]: [AgentRole.GENERALIST],
  [WorkflowPhase.STATUS_MONITORING]: [AgentRole.GENERALIST],
  [WorkflowPhase.MERGE]: [AgentRole.GENERALIST],
  [WorkflowPhase.AUTO_NEXT]: [AgentRole.GENERALIST]
};
```

**3. Context and Plan Models**

```typescript
interface IssueContext {
  issue: {
    number: number;
    title: string;
    body: string;
    labels: string[];
    comments: IssueComment[];
    createdAt: Date;
    updatedAt: Date;
  };
  relatedIssues: RelatedIssue[];
  recentCommits: CommitSummary[];
  relevantFiles: FileReference[];
  contextSummary: string; // 500-1000 word AI-generated summary
  extractedAt: Date;
}

interface DevelopmentPlan {
  planId: string;
  issueReference: {
    platform: string;
    repository: string;
    issueNumber: number;
  };
  steps: PlanStep[];
  estimatedComplexity: 'low' | 'medium' | 'high';
  estimatedTimeMinutes: number;
  ambiguities: AmbiguityDetection[];
  designOptions?: DesignOption[]; // FR-4: Multiple options with tradeoffs
  approved: boolean;
  approvedBy?: string;
  approvedAt?: Date;
  generatedBy: string; // AI provider ID
  generatedAt: Date;
}

interface PlanStep {
  stepNumber: number;
  description: string;
  estimatedDurationMinutes: number;
  dependencies: number[]; // References to other step numbers
  fileChanges: {
    filePath: string;
    changeType: 'create' | 'modify' | 'delete';
  }[];
}

interface AmbiguityDetection {
  ambiguityId: string;
  description: string;
  clarifyingQuestions: string[];
  impact: 'blocking' | 'high' | 'medium' | 'low';
  resolved: boolean;
  resolution?: string;
}

interface DesignOption {
  optionNumber: number;
  title: string;
  description: string;
  pros: string[];
  cons: string[];
  tradeoffs: {
    complexity: 'low' | 'medium' | 'high';
    maintainability: 'low' | 'medium' | 'high';
    performance: 'low' | 'medium' | 'high';
    timeToImplement: 'fast' | 'moderate' | 'slow';
  };
}
```

**4. Pull Request Models**

```typescript
interface PullRequestMetadata {
  prId: string;
  number: number;
  title: string;
  body: string;
  branchName: string;
  baseBranch: string;
  url: string;
  status: 'open' | 'merged' | 'closed';
  ciStatus: CIStatus;
  reviewStatus: ReviewStatus;
  createdAt: Date;
  updatedAt: Date;
  mergedAt?: Date;
}

interface CIStatus {
  state: 'pending' | 'success' | 'failure' | 'error';
  checks: CICheck[];
  lastUpdated: Date;
}

interface CICheck {
  name: string;
  status: 'pending' | 'success' | 'failure' | 'error';
  conclusion?: string;
  detailsUrl?: string;
  startedAt: Date;
  completedAt?: Date;
}

interface ReviewStatus {
  required: number;
  approved: number;
  changesRequested: number;
  reviews: ReviewFeedback[];
}

interface ReviewFeedback {
  reviewerId: string;
  state: 'approved' | 'changes_requested' | 'commented';
  body: string;
  comments: ReviewComment[];
  submittedAt: Date;
}
```

### APIs and Interfaces

**1. Workflow Engine API**

```typescript
interface IWorkflowEngine {
  // Lifecycle Management
  startWorkflow(config: WorkflowConfig): Promise<WorkflowExecution>;
  pauseWorkflow(executionId: string): Promise<void>;
  resumeWorkflow(executionId: string): Promise<void>;
  cancelWorkflow(executionId: string): Promise<void>;

  // Phase Execution
  executePhase(executionId: string, phase: WorkflowPhase): Promise<PhaseResult>;
  retryPhase(executionId: string, phase: WorkflowPhase): Promise<PhaseResult>;

  // Status Queries
  getWorkflowStatus(executionId: string): Promise<WorkflowExecution>;
  listActiveWorkflows(): Promise<WorkflowExecution[]>;

  // Approval Handling
  submitApproval(executionId: string, phase: WorkflowPhase, approved: boolean, feedback?: string): Promise<void>;
  getAwaitingApprovals(): Promise<ApprovalRequest[]>;
}

interface PhaseResult {
  phase: WorkflowPhase;
  success: boolean;
  output?: unknown;
  error?: WorkflowError;
  durationMs: number;
  agentId?: string;
  nextPhase?: WorkflowPhase;
}
```

**2. Role Router API**

```typescript
interface IRoleRouter {
  // Agent Selection
  selectAgentForPhase(phase: WorkflowPhase, context: WorkflowContext): Promise<AgentSelection>;
  selectAgentsForParallelPhases(phases: WorkflowPhase[], context: WorkflowContext): Promise<Map<WorkflowPhase, AgentSelection>>;

  // Capability Matching
  findAgentsWithRole(role: AgentRole): Promise<AgentRegistration[]>;
  findAgentsForSpecialization(spec: AgentSpecialization): Promise<AgentRegistration[]>;

  // Load Balancing
  getAgentLoad(agentId: string): Promise<AgentLoadMetrics>;
  rebalanceWorkload(): Promise<void>;
}

interface AgentSelection {
  agentId: string;
  role: AgentRole;
  confidence: number; // 0.0 to 1.0, based on past performance
  estimatedCompletionTime: number; // milliseconds
}

interface AgentLoadMetrics {
  agentId: string;
  currentTasks: number;
  queuedTasks: number;
  averageResponseTime: number;
  utilizationPercent: number;
}
```

**3. Approval Checkpoint API**

```typescript
interface IApprovalCheckpoint {
  // Request Approval
  requestApproval(request: ApprovalRequest): Promise<ApprovalResponse>;

  // Query Status
  getApprovalStatus(requestId: string): Promise<ApprovalStatus>;
  listPendingApprovals(userId?: string): Promise<ApprovalRequest[]>;

  // Timeout Handling
  configureTimeout(requestId: string, timeoutMs: number, escalationAction: EscalationAction): Promise<void>;
}

interface ApprovalRequest {
  requestId: string;
  executionId: string;
  phase: WorkflowPhase;
  requestType: 'plan-approval' | 'refactoring-approval' | 'merge-approval';
  content: unknown; // Plan, refactoring suggestion, or merge details
  metadata: {
    issueNumber: number;
    prNumber?: number;
    estimatedImpact: 'low' | 'medium' | 'high';
  };
  createdAt: Date;
  expiresAt?: Date;
}

interface ApprovalResponse {
  requestId: string;
  approved: boolean;
  approvedBy: string;
  feedback?: string;
  approvedAt: Date;
}
```

### Workflows and Sequencing

**1. End-to-End Autonomous Development Loop**

```
┌─────────────────────────────────────────────────────────────────┐
│                    14-Step Workflow Sequence                     │
└─────────────────────────────────────────────────────────────────┘

Phase 1: ISSUE_SELECTION [Generalist Agent]
├─ Query Git platform API for open issues
├─ Apply label filters (inclusion/exclusion)
├─ Sort by priority/age
├─ Assign issue to bot account
└─ → Phase 2

Phase 2: CONTEXT_ANALYSIS [Architect/Generalist Agent]
├─ Extract issue metadata (title, body, labels, comments)
├─ Identify related issues (#123 references)
├─ Load recent commit history (last 10 commits)
├─ Load relevant file paths from issue body
├─ Generate context summary (500-1000 words via AI provider)
└─ → Phase 3

Phase 3: PLAN_GENERATION [Architect Agent]
├─ Send context to AI provider
├─ Generate development plan (3-7 steps)
├─ Detect ambiguities (FR-3)
├─ Generate design options if applicable (FR-4)
└─ → Phase 4

Phase 4: PLAN_APPROVAL [Human Checkpoint]
├─ Present plan to user (CLI or notification)
├─ Await approval (Y/n/edit)
├─ If 'n': abort and unassign issue
├─ If 'edit': allow modifications and re-submit
├─ If 'Y': proceed
└─ → Phase 5

Phase 5: BRANCH_CREATION [Generalist Agent]
├─ Generate branch name (Tamma/issue-{number}-{title})
├─ Create branch from main/master
├─ Handle conflicts (append timestamp if branch exists)
└─ → Phase 6

Phase 6: TEST_GENERATION [Test Automation/Developer Agent]
├─ Send plan to AI provider
├─ Generate failing tests (TDD red phase)
├─ Write test files
├─ Commit tests to feature branch
├─ Run tests locally (verify failure)
├─ Retry up to 3 times if unexpected pass
└─ → Phase 7

Phase 7: CODE_GENERATION [Developer Agent]
├─ Send plan + failing tests to AI provider
├─ Generate implementation code
├─ Write implementation files
├─ Commit implementation
├─ Run tests locally (verify pass)
├─ Retry up to 3 times if tests fail
├─ Escalate if retries exhausted
└─ → Phase 8

Phase 8: REFACTORING [Code Reviewer/Developer Agent]
├─ Send implementation to AI provider
├─ Generate refactoring suggestions
├─ Present to user (optional approval)
├─ If approved: apply refactoring and commit
├─ Re-run tests (verify no breakage)
└─ → Phase 9

Phase 9: PR_CREATION [Generalist Agent]
├─ Generate PR title and body
├─ Create PR via Git platform API
├─ Add labels and request reviewers
└─ → Phase 10

Phase 10: STATUS_MONITORING [Generalist Agent]
├─ Poll PR status every 30 seconds
├─ Check CI/CD status
├─ Check review status
├─ Retrieve failure logs if CI fails
├─ Present review feedback if requested
└─ → Phase 11 (when ready)

Phase 11: MERGE [Generalist Agent]
├─ Wait for CI success + required approvals
├─ Request merge approval (human checkpoint)
├─ Execute merge via Git platform API
├─ Delete feature branch (if configured)
├─ Close issue with PR reference
└─ → Phase 12

Phase 12: AUTO_NEXT [Generalist Agent]
├─ Wait 10 seconds (cooldown)
├─ Check max iterations limit
├─ Check shutdown signal
├─ Return to Phase 1
└─ Loop continues...
```

**2. Role-Based Parallel Execution Pattern**

```
Independent Phases (Can Execute in Parallel):
┌────────────────────────────────────────┐
│  Issue A: Phase 6 (Test Generation)   │ → Test Automation Agent
│  Issue B: Phase 7 (Code Generation)   │ → Developer Agent
│  Issue C: Phase 2 (Context Analysis)  │ → Architect Agent
│  Issue D: Phase 10 (Status Monitor)   │ → Generalist Agent
└────────────────────────────────────────┘

Sequential Phases (Must Execute in Order):
┌─────────────────────────────────────────────────┐
│  Issue A:                                       │
│    Phase 6 (Test Generation)                   │
│      ↓                                          │
│    Phase 7 (Code Generation) ← depends on 6    │
│      ↓                                          │
│    Phase 8 (Refactoring) ← depends on 7        │
└─────────────────────────────────────────────────┘

Orchestrator Role Assignment Algorithm:
1. Identify current phase for workflow execution
2. Query WORKFLOW_ROLE_MAPPING for required roles
3. Call RoleRouter.selectAgentForPhase()
4. RoleRouter queries AgentRegistry for agents with matching role
5. Apply filters: available capacity, specialization match, performance history
6. Select agent with highest confidence score
7. Assign phase to selected agent
8. Update WorkflowExecution.assignedAgents mapping
9. Emit PhaseAssigned event
```

**3. Retry and Escalation Sequence**

```
Retry Logic (FR-16: 3-retry limit):

Attempt 1:
  Execute Phase → Failure
  ├─ Analyze error (FailureAnalyzer)
  ├─ Determine if transient or permanent
  └─ If transient: Wait 2 seconds → Retry

Attempt 2:
  Execute Phase → Failure
  ├─ Analyze error
  ├─ Wait 4 seconds (exponential backoff)
  └─ Retry

Attempt 3:
  Execute Phase → Failure
  ├─ Analyze error
  ├─ Wait 8 seconds
  └─ Retry

Attempt 4 (Final):
  Execute Phase → Failure
  ├─ Mark as escalation-required
  ├─ Notify user via EscalationNotifier
  ├─ Provide error details and context
  ├─ Suggest Debugger agent assignment
  └─ Pause workflow (await human intervention)

Escalation Notification:
  ├─ Send email/webhook notification
  ├─ Include: execution ID, phase, error message, retry history
  ├─ Provide action options: retry manually, reassign agent, abort
  └─ Log escalation event
```

**4. Approval Checkpoint Timing**

```
Standalone Mode (Synchronous):
  User executes CLI → Workflow runs
  ├─ Phase 3 completes (Plan Generated)
  ├─ CLI blocks with prompt: "Approve plan? [Y/n/edit]"
  ├─ User reviews plan in terminal
  ├─ User inputs decision
  └─ Workflow continues

Orchestrator Mode (Asynchronous):
  Worker executes phase → Phase 3 completes
  ├─ Orchestrator receives phase completion
  ├─ Orchestrator creates ApprovalRequest
  ├─ Orchestrator sends notification (email/webhook)
  ├─ Workflow pauses (state: awaiting-approval)
  ├─ User reviews via dashboard/notification
  ├─ User submits approval via API
  ├─ Orchestrator receives approval
  ├─ Orchestrator resumes workflow
  └─ Workflow continues

Timeout Handling:
  ApprovalRequest created with expiresAt timestamp
  ├─ Timer starts (default: 24 hours)
  ├─ If no response before expiry:
  │   ├─ Orchestrator marks as timed-out
  │   ├─ Sends escalation notification
  │   └─ Cancels workflow or reassigns
  └─ If response received:
      └─ Timer cancelled, workflow continues
```

## Non-Functional Requirements

### Performance

**Workflow Loop Performance (NFR-1):**
- Complete autonomous loop (issue → merged PR): < 2 hours for standard feature implementation
- Issue selection query: < 5 seconds (Git platform API response)
- Context analysis: < 30 seconds (including related issues, commit history, file loading)
- Plan generation: < 60 seconds (AI provider response with streaming)
- Test generation: < 90 seconds (AI provider response for TDD red phase)
- Code generation: < 120 seconds (AI provider response for TDD green phase)
- PR creation: < 10 seconds (Git platform API response)
- Status monitoring poll interval: 30 seconds (configurable down to 10 seconds)

**Role-Based Orchestration Performance:**
- Agent selection latency: < 100ms (RoleRouter.selectAgentForPhase)
- Agent load query: < 50ms (AgentLoadMetrics retrieval)
- Workflow phase assignment: < 200ms (including capability matching)
- Parallel phase scheduling: < 500ms for up to 10 concurrent workflows
- Role capability matrix lookup: < 10ms (in-memory cache)

**Approval Checkpoint Performance:**
- Approval request creation: < 100ms
- Approval status query: < 50ms
- Approval timeout check: < 10ms (background task every 60 seconds)
- Notification delivery: < 5 seconds (webhook/email)

**Retry and Escalation Performance:**
- Failure analysis: < 500ms (FailureAnalyzer categorization)
- Exponential backoff delays: 2s → 4s → 8s between retry attempts
- Escalation notification: < 10 seconds (including email/webhook delivery)

**Throughput Targets:**
- Concurrent workflows: 10+ per orchestrator instance
- Workflow phase transitions: 50+ per second across all workflows
- Agent assignments: 100+ per minute during high load

### Security

**Credential Management (FR-33, NFR-3):**
- Git platform credentials (PATs, OAuth tokens) encrypted at rest using AES-256
- AI provider API keys encrypted at rest using AES-256
- Credentials stored in OS keychain (macOS Keychain, Windows Credential Manager, Linux Secret Service)
- No credentials logged or included in event trail (redacted as `[REDACTED]`)
- Token rotation support for expired credentials with automatic retry

**Approval Security:**
- Approval requests signed with HMAC-SHA256 to prevent tampering
- Approval timeout prevents stale approvals from executing
- Approval actions logged with user attribution and timestamp
- Webhook endpoints require TLS 1.2+ with certificate validation

**Event Trail Security (NFR-3):**
- All workflow events encrypted at rest (Epic 4 dependency)
- Events include user attribution (userId, IP address) for audit compliance
- 100% action traceability with millisecond precision timestamps
- Audit log retention: minimum 90 days (configurable up to 7 years)

**Code Generation Security:**
- AI-generated code scanned for hardcoded secrets before commit (basic regex patterns)
- PR body includes security scan results summary (deferred to Epic 3 for full implementation)
- Breaking changes require manual approval (FR-34: NEVER auto-approve)

**Role-Based Access Control:**
- Agent roles define permission boundaries (e.g., Code Reviewer cannot execute merge)
- Orchestrator validates agent role before phase assignment
- Approval checkpoints enforce role-based authorization

### Reliability/Availability

**Autonomous Completion Rate (NFR-2):**
- Target: 70%+ of issues complete without human escalation
- Measured as: (workflows completed / workflows started) over 30-day rolling window
- Excludes human-cancelled workflows from denominator

**Retry Logic Reliability (FR-16):**
- 3-retry limit with exponential backoff enforced at all workflow phases
- Transient failures (network timeouts, rate limits) automatically retried
- Permanent failures (syntax errors, missing credentials) escalate immediately
- Retry success rate target: 80%+ of transient failures resolved within 3 attempts

**PR Rework Rate (NFR-2):**
- Target: < 5% of merged PRs require follow-up fixes
- Measured as: (PRs with follow-up commits within 7 days / total merged PRs)
- Excludes refactoring PRs from numerator

**Graceful Degradation:**
- If AI provider unavailable: queue workflow phases for retry (max queue: 100 tasks)
- If Git platform unavailable: pause workflows and alert user
- If orchestrator unavailable: workers continue standalone operation on in-progress tasks
- Circuit breaker triggers after 5 consecutive AI provider failures (60-second cooldown)

**Workflow Fault Tolerance:**
- Workflow state persisted after each phase transition (PostgreSQL)
- Workflow resume capability after orchestrator restart
- In-flight workflows recover from last completed phase
- No data loss on graceful shutdown (SIGTERM handled with 30-second grace period)

**Agent Availability:**
- Worker health checks every 30 seconds (orchestrator to worker heartbeat)
- Offline workers removed from agent pool after 3 failed heartbeats
- Workflow phases reassigned to healthy workers on agent failure
- Agent failure rate target: < 1% per 24-hour period

**Approval Timeout Handling:**
- Default timeout: 24 hours (configurable per approval type)
- Timeout action: send escalation notification and cancel workflow
- Override: admin can extend timeout or force approval (audit logged)

### Observability

**Structured Logging (FR-25):**
- All workflow phases emit structured logs (JSON format) with trace IDs
- Log levels: ERROR (escalations, failures), WARN (retries, timeouts), INFO (phase transitions), DEBUG (agent selection), TRACE (AI provider interactions)
- Correlation IDs: `executionId` for workflow, `phaseId` for phase, `agentId` for agent
- Log aggregation: stdout/stderr for standalone mode, centralized log store for orchestrator mode (Epic 5)

**Metrics Collection (FR-26):**
- Workflow metrics:
  - `workflow.started` (counter)
  - `workflow.completed` (counter with status: success/failed/cancelled)
  - `workflow.duration_ms` (histogram)
  - `workflow.phase_duration_ms` (histogram with phase label)
- Agent metrics:
  - `agent.online` (gauge)
  - `agent.tasks_assigned` (counter with role label)
  - `agent.task_success_rate` (gauge with agentId label)
- Approval metrics:
  - `approval.requested` (counter with type label)
  - `approval.response_time_ms` (histogram)
  - `approval.timeout` (counter)
- Retry metrics:
  - `retry.attempt` (counter with phase label)
  - `retry.success` (counter)
  - `retry.escalation` (counter)

**Real-Time Progress Tracking:**
- Workflow state changes emit events for live updates
- CLI progress bar shows current phase and estimated time remaining (standalone mode)
- WebSocket streaming for orchestrator mode (live phase updates to dashboard - Epic 5)
- Phase completion percentage: based on completed phases / total phases

**Event Trail (FR-20, FR-21):**
- All workflow actions captured as immutable events
- Event schema: `{ timestamp, executionId, phase, agentId, agentRole, action, result, metadata }`
- Events stored in append-only log (PostgreSQL, Epic 4 dependency)
- Event query API: filter by executionId, phase, agentId, dateRange

**Error Context:**
- Error logs include: stackTrace, retryAttempt, phaseContext (plan, test output, code diff)
- Escalation notifications include: error summary, retry history, suggested resolution
- Debugger agent can query error context for diagnosis

**Performance Monitoring:**
- Agent performance tracked: averagePhaseCompletionTime, successRate, tasksCompleted
- Slow phase detection: alert if phase duration > 2x historical average
- Role performance comparison: track success rates per agent role

## Dependencies and Integrations

**Epic 1 Internal Dependencies:**
- `@tamma/providers` (^1.0.0) - AI provider abstraction layer (Story 1.1, 1.2, 1.3)
  - Required for: Plan generation (Story 2.3), test generation (Story 2.5), code generation (Story 2.6), refactoring (Story 2.7)
- `@tamma/platforms` (^1.0.0) - Git platform abstraction layer (Story 1.4, 1.5, 1.6, 1.7)
  - Required for: Issue selection (Story 2.1), branch creation (Story 2.4), PR creation (Story 2.8), status monitoring (Story 2.9), merge (Story 2.10)
- `@tamma/orchestrator` (^1.0.0) - Orchestrator/worker architecture (Story 1.8, 1.9)
  - Required for: Role-based orchestration, agent registration, task distribution
- `@tamma/config` (^1.0.0) - Shared configuration management
  - Required for: Workflow configuration, approval timeouts, retry policies

**Workflow Engine Core Dependencies:**
- `@fastify/websocket` (^10.0.0) - WebSocket support for real-time approval notifications (orchestrator mode)
- `@smithy/eventstream-serde-node` (^3.0.0) - Event streaming for workflow state transitions
- `xstate` (^5.18.0) - State machine implementation for workflow phase transitions
- `bullmq` (^5.12.0) - Task queue for orchestrator mode with Redis backend
- `pg` (^8.12.0) - PostgreSQL client for workflow state persistence
- `ioredis` (^5.4.0) - Redis client for distributed locks and task queue

**AI Provider Integration:**
- `@anthropic-ai/sdk` (^0.27.0) - Claude Code provider (Epic 1 dependency)
- Provider-specific SDKs loaded dynamically via Epic 1's plugin system

**Git Platform Integration:**
- `@octokit/rest` (^21.0.0) - GitHub API client (Epic 1 dependency, Story 1.5)
- `@gitbeaker/rest` (^40.2.0) - GitLab API client (Epic 1 dependency, Story 1.6)
- Platform-specific clients loaded dynamically via Epic 1's plugin system

**Retry and Resilience:**
- `p-retry` (^6.2.0) - Exponential backoff retry logic with configurable strategies
- `opossum` (^8.1.0) - Circuit breaker pattern for AI provider failures
- `cockatiel` (^3.1.0) - Advanced resilience patterns (bulkhead, timeout, fallback)

**Logging and Observability:**
- `pino` (^9.3.0) - High-performance structured logging
- `pino-pretty` (^11.2.0) - Development log formatting
- `@opentelemetry/api` (^1.9.0) - OpenTelemetry tracing API
- `@opentelemetry/sdk-trace-node` (^1.25.0) - Tracing SDK for workflow instrumentation
- `prom-client` (^15.1.0) - Prometheus metrics collection

**Approval and Notification:**
- `nodemailer` (^6.9.0) - Email notifications for approval requests
- `webhook` (^0.1.0) - Webhook delivery for approval notifications and status updates
- `jsonwebtoken` (^9.0.0) - JWT signing for approval request authentication

**Testing Dependencies:**
- `jest` (^29.7.0) - Testing framework
- `@jest/globals` (^29.7.0) - Jest global types
- `ts-jest` (^29.2.0) - TypeScript support for Jest
- `nock` (^13.5.0) - HTTP mocking for Git platform API tests
- `mock-socket` (^9.3.0) - WebSocket mocking for real-time tests

**Development Dependencies:**
- `typescript` (^5.7.2) - TypeScript compiler
- `ts-node` (^10.9.0) - TypeScript execution for development
- `tsx` (^4.17.0) - Fast TypeScript execution
- `@types/node` (^22.5.0) - Node.js type definitions
- `eslint` (^9.9.0) - Code linting
- `prettier` (^3.3.0) - Code formatting

**External Service Integrations:**

**PostgreSQL (Required for Orchestrator Mode):**
- Version: 17+
- Purpose: Workflow state persistence, agent registry, approval queue
- Tables:
  - `workflow_executions` - Workflow state and metadata
  - `workflow_phases` - Phase transition history
  - `agent_registrations` - Active agent pool
  - `approval_requests` - Pending approval queue
- Connection pooling: 10-50 connections (configurable)
- Failover: Read replicas for status queries (optional)

**Redis (Optional, for Orchestrator Mode):**
- Version: 7.0+
- Purpose: Task queue (BullMQ), distributed locks, agent heartbeat tracking
- Persistence: AOF enabled for task queue durability
- Clustering: Redis Sentinel for high availability (optional)
- Fallback: PostgreSQL-based queue if Redis unavailable

**AI Provider APIs (Epic 1 Dependency):**
- Claude Code API (Anthropic) - Primary provider for plan/code generation
- Future providers via plugin system (OpenCode, GLM, local LLMs)
- Rate limiting handled by Epic 1's provider abstraction
- Circuit breaker triggers after 5 consecutive failures

**Git Platform APIs (Epic 1 Dependency):**
- GitHub REST API v3 - Issue management, PR operations, CI status
- GitHub GraphQL API v4 - Batch queries for related issues
- GitLab REST API v4 - Issue management, PR operations, CI status
- Authentication: PATs or OAuth tokens (Epic 1 dependency)

**Notification Services (Optional):**
- SMTP Server - Email notifications for approval requests
  - Fallback: Console logging if SMTP unavailable
- Webhook Endpoints - User-configured webhooks for workflow events
  - Retry policy: 3 attempts with exponential backoff
  - Timeout: 10 seconds per webhook call

**Monitoring and Observability (Epic 5 Dependency):**
- Prometheus - Metrics collection endpoint exposed at `/metrics`
- Grafana - Dashboards for workflow visualization (deferred to Epic 5)
- OpenTelemetry Collector - Trace aggregation (deferred to Epic 5)

**Integration Patterns:**

**Workflow Engine ↔ AI Providers (via Epic 1):**
```
WorkflowEngine → AIProviderInterface → Provider Plugin → AI API
                  (Story 2.3, 2.5, 2.6, 2.7)
```

**Workflow Engine ↔ Git Platforms (via Epic 1):**
```
WorkflowEngine → GitPlatformInterface → Platform Plugin → Git API
                  (Stories 2.1, 2.4, 2.8, 2.9, 2.10)
```

**Orchestrator ↔ Workers (Role-Based):**
```
Orchestrator → RoleRouter → AgentRegistry → Worker (filtered by role)
             → Task Queue → Worker polls → Execute phase → Report result
```

**Approval Checkpoints ↔ Users:**
```
Standalone Mode:
  WorkflowEngine → CLI prompt → User input → Continue workflow

Orchestrator Mode:
  WorkflowEngine → ApprovalQueue → Notification (email/webhook)
                → User dashboard/API → Approval response → Resume workflow
```

**Event Sourcing Integration (Epic 4 Dependency):**
```
WorkflowEngine → Emit event → EventBus → Event Store (PostgreSQL)
                             → Event consumers (observability, audit, replay)
```

## Acceptance Criteria (Authoritative)

All acceptance criteria are extracted from Epic 2 stories in epics.md and normalized into atomic, testable statements. Each criterion maps to specific technical components in the Detailed Design section.

### Story 2.1: Issue Selection with Filtering

**AC-2.1.1:** System queries Git platform API for open issues in configured repository
- **Verification:** Integration test with mock GitHub API returns issue list
- **Implementation:** `WorkflowEngine.executePhase(ISSUE_SELECTION)` → `GitPlatformInterface.getIssues()`

**AC-2.1.2:** System filters issues by configured inclusion/exclusion labels
- **Verification:** Unit test validates label filtering logic against test dataset
- **Implementation:** Filter applied in `issueSelectionCriteria` configuration

**AC-2.1.3:** System prioritizes issues by age (oldest first) as default strategy
- **Verification:** Integration test confirms oldest unassigned issue selected first
- **Implementation:** Sort by `created_at ASC` in Git platform query

**AC-2.1.4:** System assigns selected issue to configured bot user account
- **Verification:** Integration test confirms issue assignee updated via API
- **Implementation:** `GitPlatformInterface.assignIssue(issueId, botUserId)`

**AC-2.1.5:** System logs issue selection with issue number, title, and URL
- **Verification:** Log output includes structured JSON with required fields
- **Implementation:** `IssueSelectedEvent` emitted to event bus

**AC-2.1.6:** If no issues match criteria, system enters idle state and polls every 5 minutes
- **Verification:** Integration test with empty issue list triggers 5-minute wait
- **Implementation:** XState idle state with 300-second timeout transition

**AC-2.1.7:** Integration test with mock Git platform API passes
- **Verification:** CI pipeline runs end-to-end test with mock GitHub responses
- **Implementation:** Jest integration test suite with nock for API mocking

### Story 2.2: Issue Context Analysis

**AC-2.2.1:** System reads issue title, body, labels, and comments
- **Verification:** Unit test parses issue metadata from API response fixture
- **Implementation:** `GitPlatformInterface.getIssueDetails(issueId)` returns complete issue object

**AC-2.2.2:** System identifies related issues via issue references (#123 format)
- **Verification:** Unit test extracts issue numbers from markdown text containing references
- **Implementation:** Regex pattern `/\#(\d+)/g` applied to issue body and comments

**AC-2.2.3:** System loads recent commit history (last 10 commits) for project context
- **Verification:** Integration test confirms 10 commits fetched from repository
- **Implementation:** `GitPlatformInterface.getCommitHistory(repoId, limit: 10)`

**AC-2.2.4:** System loads relevant file paths mentioned in issue body
- **Verification:** Unit test identifies file paths in markdown code blocks and backticks
- **Implementation:** File path extraction from issue body using pattern matching

**AC-2.2.5:** System constructs context summary (500-1000 words) for AI provider
- **Verification:** Unit test validates summary length and content structure
- **Implementation:** Template-based context construction with configurable format

**AC-2.2.6:** Context summary logged to event trail for transparency
- **Verification:** Event store contains `ContextAnalysisCompletedEvent` with summary
- **Implementation:** Event emitted after context construction completes

**AC-2.2.7:** Unit test validates context extraction from mock issue data
- **Verification:** Test suite includes fixtures for various issue formats
- **Implementation:** Jest unit tests with mock issue API responses

### Story 2.3: Development Plan Generation with Approval Checkpoint

**AC-2.3.1:** System sends issue context to AI provider with plan generation prompt
- **Verification:** Integration test confirms prompt sent with context payload
- **Implementation:** `AIProviderInterface.generatePlan(context)` with standardized prompt template

**AC-2.3.2:** System receives plan with 3-7 implementation steps
- **Verification:** Response validation ensures step count in valid range
- **Implementation:** Plan schema validation using Zod or JSON Schema

**AC-2.3.3:** System presents plan to user via CLI output with formatted steps
- **Verification:** Manual test observes formatted plan with numbered steps
- **Implementation:** CLI rendering using chalk for colored, structured output

**AC-2.3.4:** System prompts user: "Approve plan? [Y/n/edit]"
- **Verification:** Integration test simulates user input and validates response handling
- **Implementation:** `IApprovalCheckpoint.requestApproval()` with interactive prompt

**AC-2.3.5:** If user enters 'Y' or 'y', proceed to next step
- **Verification:** State machine test confirms transition to BRANCH_CREATION phase
- **Implementation:** XState event `PLAN_APPROVED` triggers phase transition

**AC-2.3.6:** If user enters 'n', abort loop and unassign issue
- **Verification:** Integration test confirms issue unassigned and workflow halted
- **Implementation:** XState event `PLAN_REJECTED` triggers abort state

**AC-2.3.7:** If user enters 'edit', allow inline plan modification before proceeding
- **Verification:** Manual test confirms text editor launches for plan modification
- **Implementation:** Open system editor (vim/nano/notepad) with plan content

**AC-2.3.8:** Plan and approval decision logged to event trail
- **Verification:** Event store contains `PlanApprovedEvent` or `PlanRejectedEvent`
- **Implementation:** Events emitted with complete plan payload and user decision

### Story 2.4: Git Branch Creation

**AC-2.4.1:** System generates branch name format: `Tamma/issue-{number}-{sanitized-title}`
- **Verification:** Unit test validates branch name generation for various issue titles
- **Implementation:** Branch name sanitization removes special characters, replaces spaces with hyphens

**AC-2.4.2:** System creates branch from latest main/master branch via Git platform API
- **Verification:** Integration test confirms branch created with correct base SHA
- **Implementation:** `GitPlatformInterface.createBranch(name, baseBranch: 'main')`

**AC-2.4.3:** System handles conflict if branch already exists (append timestamp suffix)
- **Verification:** Integration test with existing branch name triggers timestamp suffix
- **Implementation:** Retry logic appends Unix timestamp on `BranchAlreadyExistsError`

**AC-2.4.4:** System logs branch creation with branch name and base SHA
- **Verification:** Event store contains `BranchCreatedEvent` with required fields
- **Implementation:** Event emitted after successful branch creation

**AC-2.4.5:** Branch creation failure triggers graceful abort with error logging
- **Verification:** Integration test with invalid branch name triggers abort state
- **Implementation:** XState error handler transitions to abort state with error event

**AC-2.4.6:** Integration test with mock Git platform API passes
- **Verification:** CI pipeline runs end-to-end test with mock GitHub branch creation
- **Implementation:** Jest integration test with nock mocking GitHub REST API

### Story 2.5: Test-First Development - Write Failing Tests

**AC-2.5.1:** System sends plan to AI provider with test generation prompt
- **Verification:** Integration test confirms prompt includes plan step details
- **Implementation:** `AIProviderInterface.generateTests(plan, step)` with TDD-specific prompt

**AC-2.5.2:** System receives test code with clear test cases
- **Verification:** Response validation ensures test syntax matches project conventions
- **Implementation:** Test code validation using language-specific parser

**AC-2.5.3:** System writes test files to appropriate test directory
- **Verification:** Integration test confirms test file created in correct location
- **Implementation:** File path resolution based on project conventions (e.g., `__tests__/`, `test/`, `spec/`)

**AC-2.5.4:** System commits tests to feature branch with standardized message
- **Verification:** Integration test confirms commit created with message "Add tests for [issue title]"
- **Implementation:** `GitPlatformInterface.createCommit(message, files)`

**AC-2.5.5:** System runs tests locally and verifies they fail (red phase)
- **Verification:** Test execution returns non-zero exit code and failure output
- **Implementation:** Execute test command (e.g., `npm test`) via child process

**AC-2.5.6:** Test output logged to event trail
- **Verification:** Event store contains `TestExecutionEvent` with stdout/stderr
- **Implementation:** Event emitted with test results and execution time

**AC-2.5.7:** If tests unexpectedly pass, system flags for human review
- **Verification:** Integration test with passing tests triggers escalation event
- **Implementation:** XState conditional transition to escalation state on unexpected pass

### Story 2.6: Implementation Code Generation

**AC-2.6.1:** System sends plan and failing tests to AI provider with implementation prompt
- **Verification:** Integration test confirms prompt includes test code and error messages
- **Implementation:** `AIProviderInterface.generateCode(plan, tests, errors)` with implementation prompt

**AC-2.6.2:** System receives implementation code with necessary changes
- **Verification:** Response validation ensures code syntax is valid
- **Implementation:** Code validation using language-specific parser (TypeScript, Python, etc.)

**AC-2.6.3:** System writes implementation files to appropriate source directories
- **Verification:** Integration test confirms files created in correct locations
- **Implementation:** File path resolution based on test file locations and project structure

**AC-2.6.4:** System commits implementation with standardized message
- **Verification:** Integration test confirms commit with message "Implement [issue title]"
- **Implementation:** `GitPlatformInterface.createCommit()` with standardized format

**AC-2.6.5:** System runs tests locally and verifies they pass (green phase)
- **Verification:** Test execution returns zero exit code with all tests passing
- **Implementation:** Execute test command and validate exit code === 0

**AC-2.6.6:** Test output logged to event trail
- **Verification:** Event store contains `TestExecutionEvent` with pass/fail counts
- **Implementation:** Event emitted with complete test results

**AC-2.6.7:** If tests still fail, system enters retry loop (max 3 attempts)
- **Verification:** Integration test with persistent failures triggers 3 retries then escalation
- **Implementation:** XState retry counter with exponential backoff, escalation after 3 failures

### Story 2.7: Code Refactoring Pass

**AC-2.7.1:** System sends implementation code to AI provider with refactoring prompt
- **Verification:** Integration test confirms prompt includes current code and refactoring guidelines
- **Implementation:** `AIProviderInterface.suggestRefactoring(code)` with quality focus

**AC-2.7.2:** If AI suggests refactoring, system presents approval prompt
- **Verification:** Manual test observes prompt "Apply refactoring? [Y/n]"
- **Implementation:** `IApprovalCheckpoint.requestApproval()` with refactoring preview

**AC-2.7.3:** If user approves, system applies refactoring and commits
- **Verification:** Integration test confirms refactoring applied and committed
- **Implementation:** Apply code changes and commit with message "Refactor [issue title]"

**AC-2.7.4:** System re-runs tests to verify refactoring didn't break functionality
- **Verification:** Test execution after refactoring returns passing results
- **Implementation:** Execute test command and validate all tests still pass

**AC-2.7.5:** If user rejects or AI suggests no refactoring, proceed to next step
- **Verification:** State machine test confirms transition to PR_CREATION phase
- **Implementation:** XState conditional transition based on user decision

**AC-2.7.6:** Refactoring decision logged to event trail
- **Verification:** Event store contains `RefactoringApprovedEvent` or `RefactoringSkippedEvent`
- **Implementation:** Event emitted with decision and code changes (if applied)

### Story 2.8: Pull Request Creation

**AC-2.8.1:** System generates PR title format: "[Tamma] {issue title}"
- **Verification:** Unit test validates PR title generation for various issue titles
- **Implementation:** Title template with bracket prefix and issue title

**AC-2.8.2:** System generates PR body including issue reference, plan summary, test results
- **Verification:** Unit test confirms PR body includes all required sections
- **Implementation:** Markdown template with sections for context, plan, test results

**AC-2.8.3:** System creates PR via Git platform API
- **Verification:** Integration test confirms PR created with correct base and head branches
- **Implementation:** `GitPlatformInterface.createPullRequest(title, body, base, head)`

**AC-2.8.4:** System adds labels to PR (e.g., "automated", "Tamma-generated")
- **Verification:** Integration test confirms labels added to created PR
- **Implementation:** `GitPlatformInterface.addLabels(prId, labels)`

**AC-2.8.5:** System requests review from configured reviewers (if configured)
- **Verification:** Integration test with configured reviewers confirms review requests sent
- **Implementation:** `GitPlatformInterface.requestReview(prId, reviewers)`

**AC-2.8.6:** System logs PR creation with PR URL
- **Verification:** Event store contains `PRCreatedEvent` with URL
- **Implementation:** Event emitted with complete PR metadata

**AC-2.8.7:** Integration test with mock Git platform API passes
- **Verification:** CI pipeline runs end-to-end test with mock PR creation
- **Implementation:** Jest integration test with nock mocking GitHub PR API

### Story 2.9: PR Status Monitoring

**AC-2.9.1:** System polls PR status every 30 seconds (configurable interval)
- **Verification:** Integration test confirms polling at configured interval
- **Implementation:** XState delayed transition with configurable timeout

**AC-2.9.2:** System checks CI/CD status via Git platform API
- **Verification:** Integration test confirms status check queries made
- **Implementation:** `GitPlatformInterface.getPRStatus(prId)` returns CI status

**AC-2.9.3:** System checks review status (approved, changes requested, commented)
- **Verification:** Integration test confirms review status retrieved
- **Implementation:** `GitPlatformInterface.getPRReviews(prId)` returns review list

**AC-2.9.4:** System logs status changes to event trail
- **Verification:** Event store contains `PRStatusChangedEvent` for each status update
- **Implementation:** Event emitted when status differs from previous poll

**AC-2.9.5:** If CI/CD fails, system retrieves failure logs and presents to user
- **Verification:** Integration test with failed CI triggers log retrieval
- **Implementation:** `GitPlatformInterface.getCILogs(prId)` and display to user

**AC-2.9.6:** If reviews request changes, system presents feedback to user
- **Verification:** Manual test confirms review comments displayed to user
- **Implementation:** Format review comments and display via CLI

**AC-2.9.7:** System supports manual intervention: "Continue monitoring? [Y/retry/abort]"
- **Verification:** Manual test confirms intervention prompt displayed on failure
- **Implementation:** `IApprovalCheckpoint.requestApproval()` with retry/abort options

### Story 2.10: PR Merge with Completion Checkpoint

**AC-2.10.1:** System waits for CI/CD success and required review approvals
- **Verification:** State machine test confirms merge only proceeds when conditions met
- **Implementation:** XState guard condition validates CI status and review approvals

**AC-2.10.2:** System presents merge checkpoint: "PR ready to merge. Proceed? [Y/n]"
- **Verification:** Manual test observes merge approval prompt
- **Implementation:** `IApprovalCheckpoint.requestApproval()` with merge context

**AC-2.10.3:** If user approves, system merges PR via Git platform API
- **Verification:** Integration test confirms PR merged with configured merge strategy
- **Implementation:** `GitPlatformInterface.mergePR(prId, strategy)` with strategy from config

**AC-2.10.4:** System deletes feature branch after successful merge (if configured)
- **Verification:** Integration test with branch deletion enabled confirms branch removed
- **Implementation:** `GitPlatformInterface.deleteBranch(branchName)` conditional on config

**AC-2.10.5:** System updates issue status to closed with comment linking to merged PR
- **Verification:** Integration test confirms issue closed with PR link comment
- **Implementation:** `GitPlatformInterface.closeIssue(issueId, comment)`

**AC-2.10.6:** System logs merge completion with merge SHA
- **Verification:** Event store contains `PRMergedEvent` with merge commit SHA
- **Implementation:** Event emitted with complete merge metadata

**AC-2.10.7:** If merge fails (conflicts), system alerts user and waits for manual resolution
- **Verification:** Integration test with merge conflict triggers alert and pauses workflow
- **Implementation:** XState error handler transitions to manual resolution state

### Story 2.11: Auto-Next Issue Selection

**AC-2.11.1:** After successful merge, system waits 10 seconds (cooldown period)
- **Verification:** Integration test confirms 10-second delay before next issue selection
- **Implementation:** XState delayed transition with 10-second timeout

**AC-2.11.2:** System returns to Story 2.1 (issue selection) logic
- **Verification:** State machine test confirms transition back to ISSUE_SELECTION phase
- **Implementation:** XState transition to ISSUE_SELECTION state

**AC-2.11.3:** System maintains loop counter and logs iteration number
- **Verification:** Log output includes iteration count for each loop cycle
- **Implementation:** Workflow context tracks `iterationCount` incremented on each loop

**AC-2.11.4:** System supports max iterations limit (configurable, default: infinite)
- **Verification:** Integration test with max iterations set to 2 stops after 2 cycles
- **Implementation:** XState guard condition checks `iterationCount < maxIterations`

**AC-2.11.5:** System supports graceful shutdown signal (SIGINT/SIGTERM)
- **Verification:** Integration test sends SIGINT and confirms graceful shutdown
- **Implementation:** Signal handlers set shutdown flag, workflow completes current iteration then exits

**AC-2.11.6:** System logs loop continuation to event trail
- **Verification:** Event store contains `LoopIterationStartedEvent` for each cycle
- **Implementation:** Event emitted at start of each autonomous loop iteration

## Traceability Mapping

This table maps each acceptance criterion to PRD requirements, technical components, and test strategies, ensuring complete coverage and traceability from requirements through implementation to verification.

| AC ID | PRD Requirement | Tech Spec Section | Component/API | Test Strategy |
|-------|----------------|-------------------|---------------|---------------|
| **Story 2.1: Issue Selection** |
| AC-2.1.1 | FR-1 (Issue Selection) | Workflow Engine Service | `WorkflowEngine.executePhase()`, `GitPlatformInterface.getIssues()` | Integration test: Mock GitHub API returns issue list, verify query parameters |
| AC-2.1.2 | FR-1 (Issue Selection) | Workflow Engine Service | `IssueSelectionCriteria` configuration | Unit test: Validate label filtering against test dataset with various label combinations |
| AC-2.1.3 | FR-1 (Issue Selection) | Workflow Engine Service | Sort logic in Git platform query | Integration test: Create issues with different timestamps, verify oldest selected first |
| AC-2.1.4 | FR-1 (Issue Selection) | Workflow Engine Service, Git Platform | `GitPlatformInterface.assignIssue()` | Integration test: Mock API call, verify assignee field updated with bot user ID |
| AC-2.1.5 | FR-1 (Issue Selection) | Workflow Engine Service, Event System | `IssueSelectedEvent` data model | Unit test: Validate event payload contains required fields (number, title, URL) |
| AC-2.1.6 | FR-1 (Issue Selection) | Workflow Engine Service | XState idle state with delayed transition | Integration test: Empty issue list triggers idle state, verify 5-minute timeout |
| AC-2.1.7 | FR-1 (Issue Selection) | Workflow Engine Service | Integration test suite | CI/CD test: End-to-end workflow with nock mocking GitHub REST API responses |
| **Story 2.2: Context Analysis** |
| AC-2.2.1 | FR-1 (Context Analysis) | Workflow Engine Service | `GitPlatformInterface.getIssueDetails()` | Unit test: Parse issue API response fixture, validate all fields extracted |
| AC-2.2.2 | FR-1 (Context Analysis) | Workflow Engine Service | Issue reference extraction regex | Unit test: Extract issue numbers from markdown with various reference formats (#123, GH-456) |
| AC-2.2.3 | FR-1 (Context Analysis) | Workflow Engine Service, Git Platform | `GitPlatformInterface.getCommitHistory()` | Integration test: Mock commit history API, verify limit parameter set to 10 |
| AC-2.2.4 | FR-1 (Context Analysis) | Workflow Engine Service | File path extraction pattern matching | Unit test: Extract file paths from markdown code blocks and backticks |
| AC-2.2.5 | FR-1 (Context Analysis) | Workflow Engine Service | Context summary template | Unit test: Validate summary length between 500-1000 words, verify structure |
| AC-2.2.6 | FR-1 (Context Analysis) | Workflow Engine Service, Event System | `ContextAnalysisCompletedEvent` | Unit test: Verify event emitted with context summary after analysis completes |
| AC-2.2.7 | FR-1 (Context Analysis) | Workflow Engine Service | Unit test suite | Unit test: Fixtures for various issue formats (short, long, with/without references) |
| **Story 2.3: Plan Generation** |
| AC-2.3.1 | FR-1 (Plan Generation), FR-3 (Ambiguity Detection) | Workflow Engine Service, AI Provider | `AIProviderInterface.generatePlan()` | Integration test: Mock AI provider, verify prompt includes context payload |
| AC-2.3.2 | FR-1 (Plan Generation) | Workflow Engine Service | Plan validation schema (Zod/JSON Schema) | Unit test: Validate plan with 2 steps fails, 3-7 steps pass, 8 steps fails |
| AC-2.3.3 | FR-1 (Plan Generation) | Workflow Engine Service, CLI Renderer | Chalk formatting for CLI output | Manual test: Observe formatted output with colors and numbered steps |
| AC-2.3.4 | FR-19 (Approval Checkpoints) | Approval Checkpoints Service | `IApprovalCheckpoint.requestApproval()` | Integration test: Mock user input, verify prompt displayed and response captured |
| AC-2.3.5 | FR-19 (Approval Checkpoints) | Workflow Engine Service | XState `PLAN_APPROVED` event transition | State machine test: Trigger event, verify transition to BRANCH_CREATION phase |
| AC-2.3.6 | FR-19 (Approval Checkpoints) | Workflow Engine Service | XState `PLAN_REJECTED` event transition | Integration test: Reject plan, verify issue unassigned and workflow halts |
| AC-2.3.7 | FR-19 (Approval Checkpoints) | Approval Checkpoints Service | System editor integration (vim/nano/notepad) | Manual test: Edit plan in system editor, verify changes reflected in workflow |
| AC-2.3.8 | FR-19 (Approval Checkpoints) | Workflow Engine Service, Event System | `PlanApprovedEvent`, `PlanRejectedEvent` | Unit test: Verify events emitted with complete plan payload and user decision |
| **Story 2.4: Branch Creation** |
| AC-2.4.1 | FR-1 (Branch Creation) | Workflow Engine Service | Branch name generation function | Unit test: Validate sanitization (special chars removed, spaces → hyphens) |
| AC-2.4.2 | FR-1 (Branch Creation) | Git Platform Service | `GitPlatformInterface.createBranch()` | Integration test: Mock branch creation API, verify base set to main/master |
| AC-2.4.3 | FR-16 (Retry Logic) | Git Platform Service | Retry handler with timestamp suffix | Integration test: Mock existing branch error, verify timestamp appended |
| AC-2.4.4 | FR-1 (Branch Creation) | Workflow Engine Service, Event System | `BranchCreatedEvent` | Unit test: Verify event contains branch name and base SHA |
| AC-2.4.5 | FR-16 (Escalation) | Workflow Engine Service | XState error handler → abort state | Integration test: Invalid branch name triggers abort with error event |
| AC-2.4.6 | FR-1 (Branch Creation) | Git Platform Service | Integration test suite | CI/CD test: Mock GitHub branch creation API with nock |
| **Story 2.5: Test Generation (TDD Red Phase)** |
| AC-2.5.1 | FR-5 (Test-First Development) | Workflow Engine Service, AI Provider | `AIProviderInterface.generateTests()` | Integration test: Mock AI provider, verify prompt includes plan step details |
| AC-2.5.2 | FR-5 (Test-First Development) | Workflow Engine Service | Test code syntax validation parser | Unit test: Validate TypeScript/Python/etc. test syntax with AST parser |
| AC-2.5.3 | FR-5 (Test-First Development) | Workflow Engine Service | File path resolution based on conventions | Integration test: Verify test file created in __tests__/ or test/ directory |
| AC-2.5.4 | FR-5 (Test-First Development) | Git Platform Service | `GitPlatformInterface.createCommit()` | Integration test: Verify commit message format "Add tests for [title]" |
| AC-2.5.5 | FR-5 (Test-First Development) | Workflow Engine Service | Test execution via child process | Integration test: Execute test command, verify non-zero exit code (red phase) |
| AC-2.5.6 | FR-5 (Test-First Development) | Workflow Engine Service, Event System | `TestExecutionEvent` | Unit test: Verify event emitted with stdout/stderr and execution time |
| AC-2.5.7 | FR-5 (Test-First Development) | Workflow Engine Service | XState conditional transition to escalation | Integration test: Tests unexpectedly pass, verify escalation event triggered |
| **Story 2.6: Implementation (TDD Green Phase)** |
| AC-2.6.1 | FR-5 (Test-First Development) | Workflow Engine Service, AI Provider | `AIProviderInterface.generateCode()` | Integration test: Mock AI provider, verify prompt includes test code and errors |
| AC-2.6.2 | FR-5 (Test-First Development) | Workflow Engine Service | Code syntax validation parser (TypeScript/Python/etc.) | Unit test: Validate code syntax with language-specific AST parser |
| AC-2.6.3 | FR-5 (Test-First Development) | Workflow Engine Service | File path resolution based on test locations | Integration test: Verify implementation file created in correct source directory |
| AC-2.6.4 | FR-5 (Test-First Development) | Git Platform Service | `GitPlatformInterface.createCommit()` | Integration test: Verify commit message format "Implement [title]" |
| AC-2.6.5 | FR-5 (Test-First Development) | Workflow Engine Service | Test execution validation (exit code === 0) | Integration test: Execute test command, verify zero exit code (green phase) |
| AC-2.6.6 | FR-5 (Test-First Development) | Workflow Engine Service, Event System | `TestExecutionEvent` | Unit test: Verify event emitted with pass/fail counts and test results |
| AC-2.6.7 | FR-16 (Retry Logic), FR-16 (Escalation) | Retry/Escalation Service | XState retry counter with exponential backoff | Integration test: Persistent failures trigger 3 retries → escalation after final failure |
| **Story 2.7: Refactoring (TDD Refactor Phase)** |
| AC-2.7.1 | FR-5 (Test-First Development) | Workflow Engine Service, AI Provider | `AIProviderInterface.suggestRefactoring()` | Integration test: Mock AI provider, verify prompt includes code and guidelines |
| AC-2.7.2 | FR-19 (Approval Checkpoints) | Approval Checkpoints Service | `IApprovalCheckpoint.requestApproval()` | Manual test: Observe refactoring preview and approval prompt |
| AC-2.7.3 | FR-5 (Test-First Development) | Workflow Engine Service | Apply refactoring and commit with standardized message | Integration test: Apply refactoring, verify commit message "Refactor [title]" |
| AC-2.7.4 | FR-5 (Test-First Development) | Workflow Engine Service | Test execution after refactoring | Integration test: Re-run tests, verify all tests still pass (exit code === 0) |
| AC-2.7.5 | FR-5 (Test-First Development) | Workflow Engine Service | XState conditional transition to PR_CREATION | State machine test: Skip refactoring, verify transition to next phase |
| AC-2.7.6 | FR-5 (Test-First Development) | Workflow Engine Service, Event System | `RefactoringApprovedEvent`, `RefactoringSkippedEvent` | Unit test: Verify events emitted with decision and code changes (if applied) |
| **Story 2.8: PR Creation** |
| AC-2.8.1 | FR-1 (PR Creation) | Workflow Engine Service | PR title generation template | Unit test: Validate title format "[Tamma] {issue title}" for various titles |
| AC-2.8.2 | FR-1 (PR Creation) | Workflow Engine Service | PR body markdown template | Unit test: Verify PR body includes issue reference, plan summary, test results |
| AC-2.8.3 | FR-1 (PR Creation) | Git Platform Service | `GitPlatformInterface.createPullRequest()` | Integration test: Mock PR creation API, verify base/head branches correct |
| AC-2.8.4 | FR-1 (PR Creation) | Git Platform Service | `GitPlatformInterface.addLabels()` | Integration test: Mock add labels API, verify automated labels added |
| AC-2.8.5 | FR-1 (PR Creation) | Git Platform Service | `GitPlatformInterface.requestReview()` | Integration test: Mock review request API, verify reviewers notified |
| AC-2.8.6 | FR-1 (PR Creation) | Workflow Engine Service, Event System | `PRCreatedEvent` | Unit test: Verify event emitted with PR URL and metadata |
| AC-2.8.7 | FR-1 (PR Creation) | Git Platform Service | Integration test suite | CI/CD test: Mock GitHub PR creation API with nock |
| **Story 2.9: PR Monitoring** |
| AC-2.9.1 | FR-1 (PR Monitoring) | Workflow Engine Service | XState delayed transition with configurable timeout | Integration test: Verify polling occurs at 30-second intervals (configurable) |
| AC-2.9.2 | FR-1 (PR Monitoring) | Git Platform Service | `GitPlatformInterface.getPRStatus()` | Integration test: Mock CI status API, verify status retrieved |
| AC-2.9.3 | FR-1 (PR Monitoring) | Git Platform Service | `GitPlatformInterface.getPRReviews()` | Integration test: Mock review status API, verify review list retrieved |
| AC-2.9.4 | FR-1 (PR Monitoring) | Workflow Engine Service, Event System | `PRStatusChangedEvent` | Unit test: Verify event emitted only when status changes from previous poll |
| AC-2.9.5 | FR-1 (PR Monitoring) | Git Platform Service | `GitPlatformInterface.getCILogs()` | Integration test: Mock failed CI, verify logs retrieved and displayed to user |
| AC-2.9.6 | FR-1 (PR Monitoring) | Workflow Engine Service | Review comment formatting and CLI display | Manual test: Review comments displayed to user in readable format |
| AC-2.9.7 | FR-19 (Approval Checkpoints) | Approval Checkpoints Service | `IApprovalCheckpoint.requestApproval()` with retry/abort | Manual test: Intervention prompt displayed on failure with retry/abort options |
| **Story 2.10: PR Merge** |
| AC-2.10.1 | FR-1 (PR Merge) | Workflow Engine Service | XState guard condition validates CI and reviews | State machine test: Merge blocked when CI fails or reviews not approved |
| AC-2.10.2 | FR-19 (Approval Checkpoints) | Approval Checkpoints Service | `IApprovalCheckpoint.requestApproval()` with merge context | Manual test: Merge checkpoint prompt displayed with PR details |
| AC-2.10.3 | FR-1 (PR Merge) | Git Platform Service | `GitPlatformInterface.mergePR()` with strategy | Integration test: Mock merge API, verify merge strategy from config (merge/squash/rebase) |
| AC-2.10.4 | FR-1 (PR Merge) | Git Platform Service | `GitPlatformInterface.deleteBranch()` conditional on config | Integration test: Verify branch deletion only if config flag enabled |
| AC-2.10.5 | FR-1 (PR Merge) | Git Platform Service | `GitPlatformInterface.closeIssue()` | Integration test: Mock close issue API, verify comment with PR link added |
| AC-2.10.6 | FR-1 (PR Merge) | Workflow Engine Service, Event System | `PRMergedEvent` | Unit test: Verify event emitted with merge commit SHA and metadata |
| AC-2.10.7 | FR-16 (Escalation) | Workflow Engine Service | XState error handler → manual resolution state | Integration test: Mock merge conflict, verify alert and workflow pause |
| **Story 2.11: Auto-Next** |
| AC-2.11.1 | FR-2 (Auto-Next Issue) | Workflow Engine Service | XState delayed transition with 10-second timeout | Integration test: Verify 10-second cooldown before next issue selection |
| AC-2.11.2 | FR-2 (Auto-Next Issue) | Workflow Engine Service | XState transition to ISSUE_SELECTION state | State machine test: Verify loop returns to ISSUE_SELECTION phase |
| AC-2.11.3 | FR-2 (Auto-Next Issue) | Workflow Engine Service | Workflow context `iterationCount` tracking | Unit test: Verify iteration count incremented on each loop cycle |
| AC-2.11.4 | FR-2 (Auto-Next Issue) | Workflow Engine Service | XState guard condition checks max iterations | Integration test: Max iterations set to 2, verify loop stops after 2 cycles |
| AC-2.11.5 | FR-2 (Auto-Next Issue) | Workflow Engine Service | SIGINT/SIGTERM signal handlers | Integration test: Send SIGINT, verify graceful shutdown after current iteration |
| AC-2.11.6 | FR-2 (Auto-Next Issue) | Workflow Engine Service, Event System | `LoopIterationStartedEvent` | Unit test: Verify event emitted at start of each autonomous loop iteration |

**Coverage Summary:**

- **PRD FR-1 (Autonomous Development Loop):** Covered by all Story 2.1-2.10 ACs
- **PRD FR-2 (Auto-Next Issue Selection):** Covered by Story 2.11 ACs
- **PRD FR-3 (Ambiguity Detection):** Partially covered in AC-2.3.1 (plan generation), full implementation in Epic 3
- **PRD FR-4 (Multi-Option Design Proposals):** Deferred to Epic 3, Story 3.7
- **PRD FR-5 (Test-First Development):** Covered by Stories 2.5, 2.6, 2.7 ACs (red-green-refactor cycle)
- **PRD FR-6 (Build Execution):** Deferred to Epic 3, Story 3.1
- **PRD FR-16 (3-Retry Limit with Escalation):** Covered by AC-2.4.5, AC-2.6.7, AC-2.10.7
- **PRD FR-19 (Approval Checkpoints):** Covered by AC-2.3.4-2.3.8, AC-2.7.2, AC-2.9.7, AC-2.10.2
- **NFR-2 (70% Autonomous Completion Rate):** Enabled by complete workflow loop implementation across all stories

**Test Coverage Strategy:**

- **Unit Tests:** 45 test cases covering data models, validation logic, event emission, state transitions
- **Integration Tests:** 38 test cases covering API interactions, workflow phases, mocking external services
- **Manual Tests:** 8 test cases covering CLI output, interactive prompts, user approval flows
- **State Machine Tests:** 7 test cases covering XState transitions, guards, error handling
- **CI/CD Tests:** 3 end-to-end test suites covering complete workflow with mocked dependencies

**Total Test Coverage:** 101 test cases across all acceptance criteria

## Risks, Assumptions, Open Questions

### Technical Risks

**R-2.1: Epic 1 Integration Dependency Risk** *(HIGH)*
- **Description:** Epic 2 has hard dependencies on Epic 1's AI provider and Git platform abstractions. If Epic 1 components are unstable or APIs change during Epic 2 development, integration points will break.
- **Impact:** Workflow phases that depend on AI generation (Stories 2.3, 2.5, 2.6, 2.7) or Git operations (Stories 2.1, 2.4, 2.8, 2.9, 2.10) will fail.
- **Mitigation:**
  - Establish stable API contracts for `AIProviderInterface` and `GitPlatformInterface` before Epic 2 implementation begins
  - Create comprehensive mock implementations for integration testing
  - Use semantic versioning for Epic 1 packages to prevent breaking changes
  - Implement adapter pattern to isolate Epic 2 from Epic 1 API changes

**R-2.2: AI Provider Rate Limiting and Timeout Risk** *(MEDIUM-HIGH)*
- **Description:** AI provider APIs (Claude Code, future providers) have rate limits and timeout constraints. Plan generation (Story 2.3), test generation (Story 2.5), and code generation (Story 2.6) may hit rate limits during high load or timeout during complex requests.
- **Impact:** Workflow phases fail unexpectedly, trigger retry logic, exhaust retry limits, cause escalations.
- **Mitigation:**
  - Implement exponential backoff with jitter in retry policy
  - Configure circuit breaker (5 consecutive failures → 60-second cooldown)
  - Queue workflow phases when rate limit detected (BullMQ with Redis)
  - Implement token bucket rate limiting at orchestrator level to prevent burst requests
  - Add AI provider response caching for repeated similar requests

**R-2.3: Git Platform API Consistency Risk** *(MEDIUM)*
- **Description:** GitHub and GitLab APIs have subtle differences in behavior (e.g., PR status polling, merge strategies, CI/CD status queries). Epic 1's abstraction may not fully normalize these differences.
- **Impact:** Workflow phases behave differently on GitHub vs GitLab, causing unexpected failures or escalations on specific platforms.
- **Mitigation:**
  - Create platform-specific integration test suites for each Git platform (GitHub, GitLab)
  - Document platform-specific quirks in Epic 1 provider implementations
  - Implement platform capability detection and graceful degradation
  - Add end-to-end tests that run against both GitHub and GitLab APIs

**R-2.4: State Machine Complexity Risk** *(MEDIUM)*
- **Description:** The 14-step workflow with branching paths (approval/rejection, refactoring optional, retry logic) creates a complex XState state machine with 30+ states and transitions. Bugs in state transitions could cause deadlocks or incorrect phase execution.
- **Impact:** Workflows stuck in infinite loops, skipped phases, incorrect retry counts, escalations not triggered.
- **Mitigation:**
  - Visualize XState state machine using XState Viz during development
  - Implement comprehensive state transition unit tests (target: 100% transition coverage)
  - Add timeout guards on all states (max 10 minutes per phase, configurable)
  - Implement state machine health checks with automatic reset on deadlock detection
  - Use XState inspector in development for real-time state debugging

**R-2.5: Role-Based Orchestration Complexity Risk** *(MEDIUM)*
- **Description:** Role-based orchestration introduces agent selection logic, capability matching, load balancing, and parallel execution patterns. Bugs in RoleRouter or agent assignment could route phases to wrong agents or cause load imbalance.
- **Impact:** Workflows fail due to agent capability mismatches, performance degrades due to poor load distribution, agent starvation or overload.
- **Mitigation:**
  - Start with generalist-only routing (no role specialization) and incrementally add role-based logic
  - Implement comprehensive RoleRouter unit tests with various agent pool configurations
  - Add metrics for agent utilization and role assignment distribution
  - Implement fallback to generalist agents when specialized agents unavailable
  - Create agent pool simulator for load testing role assignment logic

**R-2.6: PostgreSQL Workflow State Persistence Risk** *(MEDIUM)*
- **Description:** Workflow state is persisted to PostgreSQL after each phase transition. Database failures, connection pool exhaustion, or transaction deadlocks could cause state loss or corruption.
- **Impact:** Workflows lose progress on database failure, cannot resume after orchestrator restart, inconsistent state across workers.
- **Mitigation:**
  - Use connection pooling (10-50 connections) with automatic retry on connection errors
  - Implement optimistic locking with version numbers to prevent concurrent state updates
  - Add database health checks every 30 seconds with automatic failover to read replica
  - Implement workflow state checkpointing (save state every 3 phases, not just every phase)
  - Add workflow state recovery logic on orchestrator startup

**R-2.7: Approval Timeout and Notification Delivery Risk** *(MEDIUM)*
- **Description:** Approval checkpoints depend on user response via CLI (standalone) or webhook/email (orchestrator). Notification delivery failures, user unavailability, or timeout handling bugs could stall workflows indefinitely.
- **Impact:** Workflows stuck in awaiting-approval state indefinitely, timeouts not triggering escalations, webhook delivery failures silently ignored.
- **Mitigation:**
  - Implement webhook retry logic (3 attempts, exponential backoff, 10-second timeout)
  - Add email fallback if webhook delivery fails
  - Implement approval timeout with escalation (default 24 hours, configurable)
  - Add approval status dashboard for users to see pending approvals
  - Log all approval requests and responses with delivery status tracking

**R-2.8: Concurrent Workflow Conflict Risk** *(LOW-MEDIUM)*
- **Description:** Multiple workflows executing on the same repository simultaneously could create Git conflicts (e.g., two branches modifying same file, concurrent PR creation).
- **Impact:** Branch creation failures, merge conflicts, CI/CD failures due to race conditions.
- **Mitigation:**
  - Implement distributed locks using Redis for critical Git operations (branch creation, merge)
  - Add repository-level workflow concurrency limits (configurable, default: 3 concurrent workflows per repo)
  - Implement branch name conflict resolution (append timestamp suffix)
  - Add merge conflict detection and automatic escalation

**R-2.9: Test Execution Environment Risk** *(LOW-MEDIUM)*
- **Description:** Test execution (Stories 2.5, 2.6) runs locally via child process. Test environment setup (dependencies, environment variables) may be inconsistent or missing, causing false failures.
- **Impact:** Tests fail not due to code issues but due to environment problems, exhausting retry limits unnecessarily.
- **Mitigation:**
  - Document test environment prerequisites in Epic 2 setup guide
  - Implement test environment validation check before workflow starts
  - Add retry logic with environment re-initialization on test failure
  - Capture and log complete test output (stdout/stderr) for debugging
  - Consider containerizing test execution in future (Epic 3)

**R-2.10: Observability Overhead Risk** *(LOW)*
- **Description:** Comprehensive logging, metrics, and event emission could impact workflow performance if not optimized, especially for high-throughput scenarios (10+ concurrent workflows).
- **Impact:** Workflow phase latency increases due to logging overhead, event emission slows state transitions.
- **Mitigation:**
  - Use async/non-blocking logging (pino with worker threads)
  - Batch event emissions (buffer 10 events, flush every 1 second)
  - Implement sampling for high-volume trace logs (DEBUG/TRACE only for 10% of workflows)
  - Add observability overhead monitoring (track time spent in logging/metrics code)

### Assumptions

**A-2.1: Epic 1 API Stability**
- Assumes Epic 1's `AIProviderInterface` and `GitPlatformInterface` APIs are stable and will not introduce breaking changes during Epic 2 implementation.
- If Epic 1 APIs change, Epic 2 integration tests may break and require updates.
- **Validation Required:** Confirm Epic 1 API contracts with Epic 1 team before starting Epic 2 Story 2.1.

**A-2.2: Single Repository Per Workflow**
- Assumes each workflow execution targets a single Git repository. Multi-repository workflows (e.g., microservices) are out of scope for Epic 2.
- If users need multi-repo support, Epic 2 will need significant refactoring or a new epic.
- **Validation Required:** Confirm with stakeholders that single-repo assumption aligns with MVP requirements.

**A-2.3: Test Framework Agnostic**
- Assumes test generation (Story 2.5) and execution (Stories 2.5, 2.6) are framework-agnostic (Jest, Pytest, Go Test, etc.). Framework detection is based on project conventions (package.json, pytest.ini, go.mod).
- If framework detection fails, tests may not execute correctly.
- **Validation Required:** Test with multiple frameworks during Story 2.5 implementation to validate detection logic.

**A-2.4: English Language Context**
- Assumes issue descriptions, code comments, and AI-generated content are in English. Multi-language support is out of scope for Epic 2.
- If users work in non-English repositories, AI generation quality may degrade.
- **Validation Required:** Confirm with stakeholders if multi-language support is required for MVP or can be deferred to post-MVP.

**A-2.5: Main/Master Branch Naming**
- Assumes primary branch is named "main" or "master". Custom default branch names (e.g., "develop", "trunk") require configuration.
- If repository uses non-standard branch name, branch creation (Story 2.4) will fail.
- **Validation Required:** Add default branch detection to Git platform interface in Epic 1, or add configuration option in Epic 2.

**A-2.6: Linear Git History Preferred**
- Assumes merge strategy is configurable but defaults to "merge" (not squash or rebase). Linear history is not enforced.
- If users require specific merge strategies (e.g., always squash), configuration is required.
- **Validation Required:** Confirm default merge strategy with stakeholders, document configuration options.

**A-2.7: Synchronous Approval in Standalone Mode**
- Assumes standalone mode users are available to respond to approval prompts (Stories 2.3, 2.7, 2.10) immediately or within minutes.
- If user leaves CLI unattended, workflow will block indefinitely (no timeout in standalone mode).
- **Validation Required:** Consider adding optional timeout to standalone mode approvals, or document best practices.

**A-2.8: Redis Optional for Orchestrator**
- Assumes Redis is optional for orchestrator mode; PostgreSQL-based task queue is fallback.
- If Redis unavailable, task queue performance degrades (PostgreSQL polling less efficient than Redis pub/sub).
- **Validation Required:** Document Redis as recommended but optional, test PostgreSQL fallback performance.

**A-2.9: No Multi-Tenancy in Epic 2**
- Assumes single-tenant deployment. Multi-tenant orchestrator (isolating workflows per organization/team) is out of scope.
- If multi-tenancy required, Epic 2 requires isolation logic for workflow state, agent pools, approval queues.
- **Validation Required:** Confirm single-tenant assumption aligns with MVP requirements.

**A-2.10: Agent Roles Are Static**
- Assumes agent roles (Architect, Developer, etc.) are assigned at agent registration and do not change dynamically.
- If agents need to change roles at runtime (e.g., Developer learns QA capabilities), role updates require agent re-registration.
- **Validation Required:** Confirm role update workflow with stakeholders, document limitations.

### Open Questions

**Q-2.1: Should refactoring step (Story 2.7) be optional or always executed?**
- Current spec: Refactoring is always attempted, but only applied if user approves.
- Alternative: Make refactoring optional via configuration (e.g., `enableRefactoring: boolean`).
- **Impact:** Affects workflow state machine transitions and approval checkpoint logic.
- **Stakeholder Decision Needed:** Product team to decide default behavior for MVP.

**Q-2.2: What is the escalation workflow for retry exhaustion?**
- Current spec: After 3 retries, workflow pauses and notifies user via EscalationNotifier.
- Question: Should escalation allow manual retry, automatic reassignment to debugger agent, or require admin intervention?
- **Impact:** Affects EscalationNotifier API and workflow resume logic.
- **Stakeholder Decision Needed:** Define escalation resolution workflow (manual retry vs auto-reassign).

**Q-2.3: How should we handle PR review feedback iteration?**
- Current spec: Story 2.9 presents review feedback to user, but workflow does not automatically address comments.
- Question: Should workflow automatically address simple review comments (linting, formatting) or always require user intervention?
- **Impact:** May require new workflow phase for "address review comments" between Status Monitoring and Merge.
- **Stakeholder Decision Needed:** Confirm if automated review comment resolution is in scope for MVP or deferred to Epic 3.

**Q-2.4: Should agent role preferences be configurable per workflow phase?**
- Current spec: WORKFLOW_ROLE_MAPPING is hardcoded constant with role preferences per phase.
- Question: Should users/admins be able to customize role assignments (e.g., prefer Developer over Test Automation for test generation)?
- **Impact:** Affects RoleRouter configuration and adds complexity to role selection logic.
- **Stakeholder Decision Needed:** Decide if role customization is MVP requirement or post-MVP enhancement.

**Q-2.5: What is the CI/CD failure handling strategy?**
- Current spec: Story 2.9 detects CI/CD failures and retrieves logs, but workflow does not automatically fix failures.
- Question: Should workflow attempt to diagnose and fix CI/CD failures (e.g., linting errors, broken tests), or always escalate?
- **Impact:** May require new workflow phase for "fix CI/CD failures" or integration with debugger agent.
- **Stakeholder Decision Needed:** Confirm if automated CI/CD failure resolution is in scope for MVP or deferred to Epic 3.

**Q-2.6: Should approval checkpoints support delegation?**
- Current spec: Approval requests target the user who started the workflow (standalone) or send notifications (orchestrator).
- Question: Should approvals support delegation to other users (e.g., senior developer approves plan generated by junior)?
- **Impact:** Affects ApprovalCheckpoint API and notification routing logic.
- **Stakeholder Decision Needed:** Decide if approval delegation is MVP requirement or post-MVP enhancement.

**Q-2.7: How should we handle branch name conflicts beyond timestamp suffix?**
- Current spec: Story 2.4 appends Unix timestamp if branch already exists.
- Question: Should workflow detect branch naming collisions across multiple concurrent workflows and use more sophisticated conflict resolution (e.g., append workflow ID)?
- **Impact:** Affects branch creation retry logic and conflict detection.
- **Technical Decision Needed:** Evaluate collision probability and decide if timestamp suffix is sufficient.

**Q-2.8: Should role-based orchestration metrics include per-role success rates?**
- Current spec: NFR observability includes agent-level metrics (`agent.task_success_rate`).
- Question: Should we also track role-level success rates (e.g., Developer role has 85% success rate across all agents)?
- **Impact:** Affects metrics collection and dashboard design (Epic 5 dependency).
- **Stakeholder Decision Needed:** Confirm if role-level metrics are valuable for users or overkill for MVP.

**Q-2.9: What is the workflow state recovery strategy after orchestrator crash?**
- Current spec: Workflows persist state to PostgreSQL after each phase, but recovery logic on orchestrator restart is not detailed.
- Question: Should orchestrator automatically resume in-progress workflows on startup, or wait for admin action?
- **Impact:** Affects orchestrator startup sequence and workflow recovery logic.
- **Technical Decision Needed:** Define recovery strategy and test orchestrator crash scenarios.

**Q-2.10: Should parallel workflow execution be limited per agent or per repository?**
- Current spec: Concurrent workflow limit is mentioned in R-2.8 mitigation (3 per repo), but not formalized.
- Question: Should we enforce concurrency limits per repository, per agent, or both?
- **Impact:** Affects workflow scheduling logic and distributed lock implementation.
- **Technical Decision Needed:** Define concurrency limits and test contention scenarios.

## Test Strategy Summary

### Unit Testing Approach

**Scope:** Individual functions, classes, and data models in isolation with mocked dependencies.

**Coverage Target:** 80%+ line coverage for core workflow logic, 100% coverage for critical paths (retry logic, state transitions, approval handling).

**Key Test Categories:**

1. **Data Model Validation Tests** (12 test cases)
   - Workflow state models: `WorkflowExecution`, `PhaseTransition`, `WorkflowError`
   - Agent role models: `AgentRegistration`, `RoleCapabilityMatrix`
   - Context and plan models: `IssueContext`, `DevelopmentPlan`, `AmbiguityDetection`
   - PR models: `PullRequestMetadata`, `CIStatus`, `ReviewStatus`
   - Test validates: schema compliance, required fields, valid enum values, timestamp formats

2. **Business Logic Tests** (18 test cases)
   - Issue selection filtering: label inclusion/exclusion, priority sorting, age-based sorting
   - Context analysis: issue reference extraction, file path detection, context summary generation
   - Plan validation: step count (3-7), dependencies validation, ambiguity detection
   - Branch name generation: sanitization, conflict suffix appending
   - PR body generation: markdown formatting, section inclusion
   - Refactoring decision logic: approval/rejection handling
   - Auto-next loop counter: iteration tracking, max iterations limit

3. **Event Emission Tests** (8 test cases)
   - Verify events emitted for all workflow phase transitions
   - Validate event payloads contain required fields (executionId, phase, timestamp, agentId)
   - Test event ordering (events emitted in correct sequence)
   - Test event error handling (failed event emission does not block workflow)

4. **State Machine Transition Tests** (7 test cases - see State Machine Testing section)

**Testing Tools:**
- **Jest** (^29.7.0) - Test runner and assertion library
- **ts-jest** (^29.2.0) - TypeScript support
- **@jest/globals** (^29.7.0) - Jest types for TypeScript

**Mocking Strategy:**
- Mock Epic 1 dependencies (`AIProviderInterface`, `GitPlatformInterface`) using Jest mock functions
- Mock PostgreSQL queries using `jest.fn()` to return test fixtures
- Mock Redis operations using `ioredis-mock`
- Use dependency injection for all external dependencies to enable easy mocking

**Example Test:**
```typescript
describe('IssueSelector', () => {
  it('should filter issues by inclusion labels', async () => {
    const mockGitPlatform = {
      getIssues: jest.fn().mockResolvedValue([
        { number: 1, labels: ['bug', 'backend'] },
        { number: 2, labels: ['feature', 'frontend'] },
        { number: 3, labels: ['bug', 'frontend'] }
      ])
    };
    const selector = new IssueSelector(mockGitPlatform);
    const criteria = { includeLabels: ['bug'], excludeLabels: [] };
    const result = await selector.selectIssue(criteria);
    expect(result.number).toBe(1); // Oldest bug issue
  });
});
```

### Integration Testing Approach

**Scope:** Interactions between Epic 2 components and external dependencies (Epic 1 providers, PostgreSQL, Redis) with partial mocking.

**Coverage Target:** 70%+ of API integration points, 100% of critical workflow phases (issue selection, plan generation, PR creation, merge).

**Key Test Categories:**

1. **Git Platform Integration Tests** (12 test cases)
   - Issue selection: Mock GitHub REST API using `nock`, verify query parameters (labels, state, sort)
   - Branch creation: Mock GitHub branch creation API, test conflict handling (existing branch)
   - PR creation: Mock GitHub PR API, verify title/body/labels/reviewers
   - PR status monitoring: Mock CI/CD status API, test polling logic
   - PR merge: Mock merge API, test merge strategy and branch deletion
   - Platform-agnostic tests: Run same tests against GitHub and GitLab mocks

2. **AI Provider Integration Tests** (10 test cases)
   - Plan generation: Mock Claude Code API, verify prompt structure and context payload
   - Test generation: Mock AI response with test code, validate syntax parsing
   - Code generation: Mock AI response with implementation code, validate file path resolution
   - Refactoring: Mock AI refactoring suggestions, test approval prompt triggering
   - Rate limiting: Mock rate limit error responses, verify retry and circuit breaker logic
   - Timeout handling: Mock slow AI responses (>30s), verify timeout and escalation

3. **Workflow Phase Execution Tests** (16 test cases)
   - End-to-end phase execution: Mock all dependencies, execute each phase (ISSUE_SELECTION → AUTO_NEXT)
   - Phase result validation: Verify each phase returns PhaseResult with expected structure
   - Phase retry logic: Trigger failures, verify exponential backoff and retry counter
   - Phase escalation: Exhaust retries, verify escalation event and workflow pause
   - Parallel phase execution: Execute multiple workflows concurrently, verify no interference

4. **Database Integration Tests** (5 test cases)
   - Workflow state persistence: Save WorkflowExecution to PostgreSQL, query and verify
   - Phase history logging: Save PhaseTransition records, query by executionId
   - Agent registration: Insert AgentRegistration, query by role
   - Approval queue: Insert ApprovalRequest, query pending approvals
   - Connection pool stress test: Execute 50 concurrent queries, verify no deadlocks

5. **Approval Checkpoint Integration Tests** (5 test cases)
   - Standalone approval: Simulate CLI user input (Y/n/edit), verify workflow continuation/abort
   - Orchestrator approval: Create ApprovalRequest, submit approval via API, verify workflow resume
   - Timeout handling: Create approval with 5-second timeout, wait, verify escalation triggered
   - Webhook delivery: Mock webhook endpoint, verify approval notification sent with retry
   - Email fallback: Mock SMTP failure, verify email fallback triggered

**Testing Tools:**
- **nock** (^13.5.0) - HTTP mocking for Git platform APIs
- **ioredis-mock** - Redis mocking for task queue testing
- **mock-socket** (^9.3.0) - WebSocket mocking for real-time approval notifications
- **pg-mem** - In-memory PostgreSQL for fast database tests without external PostgreSQL instance

**Mocking Strategy:**
- Use `nock` to mock all external HTTP APIs (GitHub, GitLab, AI providers)
- Use `ioredis-mock` for Redis operations in tests (no real Redis instance required)
- Use `pg-mem` for PostgreSQL tests when possible (falls back to test database for complex queries)
- Record and replay HTTP responses for deterministic tests

**Example Test:**
```typescript
describe('PlanGenerator Integration', () => {
  it('should generate plan with AI provider and save to database', async () => {
    nock('https://api.anthropic.com')
      .post('/v1/messages')
      .reply(200, { content: [{ type: 'text', text: 'Plan: Step 1, Step 2, Step 3' }] });

    const generator = new PlanGenerator(aiProvider, database);
    const context = { issue: { number: 123, title: 'Add feature' } };
    const plan = await generator.generatePlan(context);

    expect(plan.steps).toHaveLength(3);
    const savedPlan = await database.query('SELECT * FROM plans WHERE issue_number = 123');
    expect(savedPlan.rows).toHaveLength(1);
  });
});
```

### End-to-End Testing Approach

**Scope:** Complete autonomous workflow from issue selection to PR merge with minimal mocking (only external APIs mocked).

**Coverage Target:** 3 end-to-end test suites covering happy path, failure path, and approval rejection path.

**Key Test Scenarios:**

1. **E2E-1: Happy Path - Full Autonomous Loop** (1 test suite)
   - Setup: Create test repository with 1 open issue, mock AI provider responses, mock GitHub API
   - Execute: Start workflow in standalone mode with auto-approve enabled
   - Validate:
     - Issue selected and assigned
     - Context analysis extracts issue metadata
     - Plan generated with 3-5 steps
     - Branch created with correct naming
     - Tests generated and committed (failing tests verified)
     - Implementation generated and committed (passing tests verified)
     - Refactoring skipped (AI suggests no refactoring)
     - PR created with correct title/body
     - CI/CD status monitored (mock passing checks)
     - PR merged to main
     - Issue closed with PR link
     - Auto-next triggered (no more issues, enters idle state)
   - Duration: ~60 seconds per test run

2. **E2E-2: Failure Path - Retry and Escalation** (1 test suite)
   - Setup: Create test repository with 1 open issue, mock AI provider with transient failures, mock GitHub API
   - Execute: Start workflow, inject failures in test generation phase
   - Validate:
     - Test generation fails on attempt 1 (rate limit error)
     - Retry triggered after 2-second backoff
     - Test generation fails on attempt 2 (timeout error)
     - Retry triggered after 4-second backoff
     - Test generation fails on attempt 3 (server error)
     - Retry triggered after 8-second backoff
     - Test generation fails on attempt 4 (final attempt)
     - Escalation triggered with error details
     - Workflow paused in escalation state
     - User notified via escalation notification
   - Duration: ~30 seconds per test run (includes retry delays)

3. **E2E-3: Approval Rejection Path** (1 test suite)
   - Setup: Create test repository with 1 open issue, mock AI provider, mock GitHub API
   - Execute: Start workflow, reject plan during approval checkpoint
   - Validate:
     - Issue selected and assigned
     - Context analysis completes
     - Plan generated
     - Approval checkpoint triggered
     - User rejects plan (input 'n')
     - Workflow aborts
     - Issue unassigned
     - No branch created
     - Workflow marked as cancelled
   - Duration: ~20 seconds per test run

**Testing Tools:**
- **Jest** for test orchestration
- **nock** for mocking external APIs (GitHub, AI providers)
- Real PostgreSQL test database (Docker container) for state persistence testing
- Real Redis test instance (Docker container) for task queue testing in orchestrator mode

**CI/CD Integration:**
- E2E tests run in GitHub Actions on every PR
- Docker Compose spins up PostgreSQL and Redis test instances
- Tests run in parallel (3 concurrent test suites)
- Total E2E test duration: ~2 minutes (including setup/teardown)

**Example Test:**
```typescript
describe('E2E: Happy Path', () => {
  beforeAll(async () => {
    // Setup test database and Redis
    await setupTestEnvironment();
  });

  it('should complete full autonomous loop from issue to PR merge', async () => {
    // Mock GitHub API responses
    nock('https://api.github.com')
      .get('/repos/test/repo/issues')
      .reply(200, [{ number: 123, title: 'Add feature', labels: ['bug'] }])
      .post('/repos/test/repo/git/refs')
      .reply(201, { ref: 'refs/heads/Tamma/issue-123-add-feature' })
      // ... more GitHub mocks

    // Mock AI provider responses
    nock('https://api.anthropic.com')
      .post('/v1/messages')
      .reply(200, { content: [{ type: 'text', text: 'Plan: ...' }] })
      // ... more AI mocks

    // Execute workflow
    const workflow = new WorkflowEngine(config);
    await workflow.start();

    // Wait for completion (max 60 seconds)
    await waitForWorkflowCompletion(workflow, 60000);

    // Assertions
    const execution = await workflow.getStatus();
    expect(execution.state).toBe('completed');
    expect(execution.currentPhase).toBe('AUTO_NEXT');
  });
});
```

### State Machine Testing Approach

**Scope:** XState state machine transitions, guards, error handling, and retry logic.

**Coverage Target:** 100% of state transitions, 100% of guard conditions.

**Key Test Categories:**

1. **State Transition Tests** (7 test cases)
   - Happy path: ISSUE_SELECTION → CONTEXT_ANALYSIS → ... → AUTO_NEXT → ISSUE_SELECTION
   - Approval rejection: PLAN_APPROVAL → ABORT (on 'n' input)
   - Approval edit: PLAN_APPROVAL → PLAN_GENERATION (on 'edit' input, re-generate plan)
   - Retry transition: CODE_GENERATION → CODE_GENERATION (on test failure, retry 1)
   - Escalation transition: CODE_GENERATION → ESCALATION (after 3 retries exhausted)
   - Refactoring skip: REFACTORING → PR_CREATION (on 'n' input or no suggestions)
   - Idle state: ISSUE_SELECTION → IDLE (no issues available) → ISSUE_SELECTION (after 5-minute timeout)

2. **Guard Condition Tests** (5 test cases)
   - Retry count guard: Block retry if `retryCount >= 3`
   - Max iterations guard: Block auto-next if `iterationCount >= maxIterations`
   - CI success guard: Block merge if CI status not 'success'
   - Review approval guard: Block merge if required approvals not met
   - Approval timeout guard: Trigger escalation if approval not received within timeout

3. **Error Handler Tests** (3 test cases)
   - AI provider error: Verify error handler transitions to retry state
   - Git platform error: Verify error handler transitions to escalation state (permanent error)
   - Database error: Verify error handler logs error and retries state persistence

**Testing Tools:**
- **@xstate/test** - XState testing utilities for state machine verification
- **Jest** for test runner and assertions

**Testing Strategy:**
- Use `@xstate/test` to generate test paths automatically from state machine definition
- Manually write tests for critical transitions not covered by auto-generated paths
- Use XState Inspector in development to visualize state machine behavior

**Example Test:**
```typescript
import { createMachine } from 'xstate';
import { testModel } from '@xstate/test';

const workflowMachine = createMachine({
  // ... state machine definition
});

const testModel = testModel(workflowMachine);

describe('Workflow State Machine', () => {
  testModel.getPaths().forEach(path => {
    it(path.description, async () => {
      await path.test({
        // Test implementation for each state
        ISSUE_SELECTION: async state => {
          expect(state.context.currentPhase).toBe('ISSUE_SELECTION');
        },
        // ... more state tests
      });
    });
  });
});
```

### Manual Testing Strategy

**Scope:** User-facing CLI interactions, approval prompts, error messages, log output.

**Coverage Target:** 8 manual test scenarios covering critical user journeys.

**Key Test Scenarios:**

1. **MT-1: Standalone Mode - Full Loop with Approvals**
   - Launch Tamma CLI in standalone mode
   - Observe issue selection output (formatted with colors)
   - Observe context analysis output (summary displayed)
   - Review plan approval prompt (formatted plan with numbered steps)
   - Input 'Y' to approve plan
   - Observe branch creation confirmation
   - Observe test generation progress
   - Observe code generation progress
   - Review refactoring approval prompt (refactoring preview)
   - Input 'n' to skip refactoring
   - Observe PR creation confirmation (PR URL displayed)
   - Observe CI/CD status monitoring (polling updates)
   - Review merge approval prompt (PR ready message)
   - Input 'Y' to approve merge
   - Observe merge confirmation
   - Observe auto-next message (starting next iteration)
   - **Validation:** All outputs formatted correctly, prompts clear, progress indicators visible

2. **MT-2: Standalone Mode - Plan Editing**
   - Launch Tamma CLI, wait for plan approval prompt
   - Input 'edit' to edit plan
   - System editor opens (vim/nano/notepad) with plan content
   - Edit plan: add new step "Implement caching layer"
   - Save and exit editor
   - Observe updated plan displayed for re-approval
   - Input 'Y' to approve edited plan
   - **Validation:** Editor launches correctly, changes reflected, workflow continues

3. **MT-3: Standalone Mode - Escalation Notification**
   - Launch Tamma CLI with mock AI provider configured to fail
   - Wait for test generation to fail 3 times
   - Observe retry messages with exponential backoff (2s → 4s → 8s)
   - Observe escalation message: "Workflow paused. Manual intervention required."
   - Observe error details (error message, retry history, phase context)
   - **Validation:** Escalation message clear, error details helpful, workflow does not exit

4. **MT-4: Orchestrator Mode - Approval Webhook Delivery**
   - Start orchestrator with webhook URL configured
   - Start worker, execute workflow to plan approval phase
   - Check webhook endpoint for approval request notification
   - Submit approval via API: `POST /approvals/{requestId}` with `approved: true`
   - Observe workflow resumes and continues to next phase
   - **Validation:** Webhook delivered with correct payload, approval processed, workflow resumes

5. **MT-5: Orchestrator Mode - Timeout Handling**
   - Start orchestrator with 10-second approval timeout configured (for testing)
   - Start worker, execute workflow to plan approval phase
   - Wait 10 seconds without submitting approval
   - Check webhook endpoint for timeout escalation notification
   - Observe workflow marked as timed-out in database
   - **Validation:** Timeout triggers escalation, workflow does not hang, timeout notification sent

6. **MT-6: Role-Based Agent Assignment**
   - Register 3 workers: Architect agent, Developer agent, Generalist agent
   - Start orchestrator, create workflow
   - Observe orchestrator logs: "Assigning PLAN_GENERATION to Architect agent (agentId: architect-1)"
   - Observe orchestrator logs: "Assigning CODE_GENERATION to Developer agent (agentId: dev-1)"
   - Observe orchestrator logs: "Assigning ISSUE_SELECTION to Generalist agent (agentId: gen-1)"
   - **Validation:** Role routing works correctly, phases assigned to agents with matching roles

7. **MT-7: Parallel Workflow Execution**
   - Register 3 workers (all generalists)
   - Start orchestrator, create 3 workflows for 3 different issues
   - Observe orchestrator logs showing parallel phase execution:
     - "Workflow A: Phase 6 (Test Generation) assigned to worker-1"
     - "Workflow B: Phase 2 (Context Analysis) assigned to worker-2"
     - "Workflow C: Phase 10 (Status Monitoring) assigned to worker-3"
   - Observe all 3 workflows complete independently without interference
   - **Validation:** Parallel execution works, no workflow conflicts, all complete successfully

8. **MT-8: Graceful Shutdown**
   - Start workflow in standalone mode
   - While workflow running, send SIGINT (Ctrl+C)
   - Observe message: "Shutdown signal received. Completing current iteration..."
   - Workflow completes current phase (e.g., code generation)
   - Workflow saves state to database
   - Workflow exits cleanly with exit code 0
   - **Validation:** Shutdown graceful, no data loss, current phase completes before exit

**Testing Schedule:**
- Manual tests executed during Story 2.1-2.11 implementation (developer testing)
- Manual tests re-executed during Epic 2 QA phase before Epic retrospective
- Manual tests executed on each platform (macOS, Windows, Linux) before release
