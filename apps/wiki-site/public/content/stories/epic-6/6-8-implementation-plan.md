---
title: "Story 6-8: Agent Permissions System - Implementation Plan"
sidebar:
  order: 60
---

## Overview

This document outlines the detailed implementation plan for the Agent Permissions System, a hierarchical permission framework that controls what tools, operations, and resources each agent type can access. The system supports global defaults with per-project overrides and enforces permissions before every agent action.

## Package Location

The Agent Permissions System will be implemented in **`@tamma/gates`** package, which is already set up for quality gates and security-related functionality.

**Rationale:**
- The `@tamma/gates` package is designed for "quality gates (build, test, security)"
- Permissions are a natural security/gate mechanism
- It already has dependencies on `@tamma/shared`, `@tamma/events`, and `@tamma/observability`
- Keeps permission logic centralized and reusable across the platform

```
packages/gates/
├── src/
│   ├── index.ts                           # Public exports
│   ├── permissions/
│   │   ├── index.ts                       # Permissions module exports
│   │   ├── types.ts                       # Permission interfaces and types
│   │   ├── defaults.ts                    # Default permissions per agent type
│   │   ├── permission-service.ts          # Core permission service
│   │   ├── permission-enforcer.ts         # Enforcement middleware
│   │   ├── permission-resolver.ts         # Global + project override resolution
│   │   ├── permission-request.ts          # Elevated permission request handling
│   │   ├── matchers/
│   │   │   ├── index.ts
│   │   │   ├── glob-matcher.ts            # File path pattern matching
│   │   │   ├── command-matcher.ts         # Shell command matching
│   │   │   └── tool-matcher.ts            # Tool permission matching
│   │   └── validators/
│   │       ├── index.ts
│   │       ├── tool-validator.ts
│   │       ├── file-validator.ts
│   │       ├── command-validator.ts
│   │       ├── git-validator.ts
│   │       └── resource-validator.ts
│   └── violations/
│       ├── index.ts
│       ├── violation-recorder.ts          # Record permission violations
│       └── violation-alerter.ts           # Alert on repeated violations
├── __tests__/
│   ├── permissions/
│   │   ├── permission-service.test.ts
│   │   ├── permission-enforcer.test.ts
│   │   ├── permission-resolver.test.ts
│   │   ├── matchers/
│   │   │   ├── glob-matcher.test.ts
│   │   │   └── command-matcher.test.ts
│   │   └── validators/
│   │       └── *.test.ts
│   └── violations/
│       └── violation-recorder.test.ts
└── package.json
```

## Files to Create/Modify

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/gates/src/permissions/types.ts` | All permission-related TypeScript interfaces and types |
| `packages/gates/src/permissions/defaults.ts` | Default permission sets for each agent type |
| `packages/gates/src/permissions/permission-service.ts` | Core IPermissionService implementation |
| `packages/gates/src/permissions/permission-enforcer.ts` | Middleware for enforcing permissions |
| `packages/gates/src/permissions/permission-resolver.ts` | Resolves effective permissions (global + project) |
| `packages/gates/src/permissions/permission-request.ts` | Handles elevated permission requests |
| `packages/gates/src/permissions/matchers/glob-matcher.ts` | Glob pattern matching for file paths |
| `packages/gates/src/permissions/matchers/command-matcher.ts` | Command pattern/regex matching |
| `packages/gates/src/permissions/matchers/tool-matcher.ts` | Tool name matching |
| `packages/gates/src/permissions/validators/*.ts` | Specialized validators for each category |
| `packages/gates/src/violations/violation-recorder.ts` | Records violations to event store |
| `packages/gates/src/violations/violation-alerter.ts` | Alerts on repeated violations |

### Files to Modify

| File | Changes |
|------|---------|
| `packages/gates/src/index.ts` | Export all permission-related modules |
| `packages/gates/package.json` | Add micromatch dependency for glob matching |
| `packages/shared/src/types/index.ts` | Add AgentType enum if not present |
| `packages/orchestrator/src/engine.ts` | Integrate permission enforcement before agent actions |
| `packages/providers/src/agent-types.ts` | Reference shared permission types |

## Interfaces and Types

### Core Permission Types

```typescript
// packages/gates/src/permissions/types.ts

// ============================================
// Permission Categories & Actions
// ============================================

export type PermissionCategory =
  | 'tool'           // Claude Code tools (Read, Write, Edit, Bash, etc.)
  | 'file'           // File system access
  | 'command'        // Shell commands
  | 'api'            // External APIs
  | 'git'            // Git operations
  | 'resource';      // Resource limits

export type PermissionAction = 'allow' | 'deny' | 'require_approval';

export type PermissionScope = 'global' | 'project';

// ============================================
// Agent Types
// ============================================

export type AgentType =
  | 'scrum_master'
  | 'architect'
  | 'researcher'
  | 'analyst'
  | 'planner'
  | 'implementer'
  | 'reviewer'
  | 'tester'
  | 'documenter';

// ============================================
// Core Permission Interface
// ============================================

export interface Permission {
  id: string;
  category: PermissionCategory;
  resource: string;        // Tool name, path pattern, command, etc.
  action: PermissionAction;
  conditions?: PermissionCondition[];
}

export interface PermissionCondition {
  type: 'pattern' | 'limit' | 'time' | 'context';
  value: unknown;
}

// ============================================
// Tool Permissions
// ============================================

export interface ToolPermissions {
  allowed: string[];        // ['Read', 'Glob', 'Grep']
  denied: string[];         // ['Bash', 'Write']
  requireApproval: string[]; // ['Edit']
}

// ============================================
// File Permissions
// ============================================

export interface FilePermissions {
  read: {
    allowed: string[];      // Glob patterns: ['**/*']
    denied: string[];       // ['**/.env', '**/secrets/**']
  };
  write: {
    allowed: string[];      // ['src/**/*.ts', 'tests/**/*.ts']
    denied: string[];       // ['package.json', 'tsconfig.json']
  };
}

// ============================================
// Command Permissions
// ============================================

export interface CommandPermissions {
  allowed: string[];        // ['npm test', 'npm run lint']
  denied: string[];         // ['rm -rf', 'sudo *']
  patterns: {
    allow: string[];        // Regex patterns as strings
    deny: string[];
  };
}

// ============================================
// API Permissions
// ============================================

export interface APIPermissions {
  allowed: string[];        // ['https://api.github.com/*']
  denied: string[];         // ['*://internal.company.com/*']
  requireApproval: string[];
}

// ============================================
// Git Permissions
// ============================================

export type GitOperation =
  | 'commit'
  | 'push'
  | 'create_branch'
  | 'delete_branch'
  | 'merge'
  | 'rebase'
  | 'force_push';

export interface GitPermissions {
  canCommit: boolean;
  canPush: boolean;
  canCreateBranch: boolean;
  canMerge: boolean;
  canDeleteBranch: boolean;
  canRebase: boolean;
  canForcePush: boolean;
  protectedBranches: string[];  // Cannot modify: ['main', 'master', 'release/*']
}

// ============================================
// Resource Limits
// ============================================

export interface ResourceLimits {
  maxTokensPerTask: number;
  maxBudgetPerTask: number;      // USD
  maxDurationMinutes: number;
  maxFilesModified: number;
  maxLinesChanged: number;
  maxConcurrentTasks: number;
}

// ============================================
// Full Permission Set
// ============================================

export interface AgentPermissionSet {
  agentType: AgentType;
  scope: PermissionScope;
  scopeId?: string;  // Project ID if scope is 'project'

  tools: ToolPermissions;
  files: FilePermissions;
  commands: CommandPermissions;
  apis: APIPermissions;
  git: GitPermissions;
  resources: ResourceLimits;

  // Metadata
  createdAt?: Date;
  updatedAt?: Date;
  createdBy?: string;
}

// ============================================
// Permission Check Result
// ============================================

export interface PermissionResult {
  allowed: boolean;
  reason?: string;
  requiresApproval?: boolean;
  suggestedAlternative?: string;
  matchedRule?: Permission;
}

// ============================================
// Agent Action Types
// ============================================

export type AgentActionType =
  | 'tool'
  | 'file_read'
  | 'file_write'
  | 'command'
  | 'git'
  | 'api';

export interface AgentAction {
  type: AgentActionType;
  toolName?: string;
  path?: string;
  command?: string;
  operation?: GitOperation;
  apiUrl?: string;
  metadata?: Record<string, unknown>;
}

// ============================================
// Permission Request (for elevated perms)
// ============================================

export interface PermissionRequest {
  id: string;
  agentType: AgentType;
  projectId: string;
  taskId: string;
  requestedPermission: Permission;
  reason: string;
  duration?: number;  // Temporary grant duration in minutes
  status: 'pending' | 'approved' | 'denied' | 'expired';
  requestedAt: Date;
  resolvedAt?: Date;
  resolvedBy?: string;
  resolutionReason?: string;
}

export interface PermissionRequestResult {
  approved: boolean;
  requestId: string;
  expiresAt?: Date;
  reason?: string;
}

// ============================================
// Permission Violation
// ============================================

export interface PermissionViolation {
  id: string;
  agentType: AgentType;
  projectId: string;
  taskId?: string;
  action: AgentAction;
  deniedPermission: Permission;
  reason: string;
  timestamp: Date;
  severity: 'low' | 'medium' | 'high' | 'critical';
  repeated: boolean;
  repeatCount?: number;
}

export interface ViolationFilter {
  agentType?: AgentType;
  projectId?: string;
  fromDate?: Date;
  toDate?: Date;
  severity?: string[];
  limit?: number;
}
```

### Service Interfaces

```typescript
// packages/gates/src/permissions/types.ts (continued)

// ============================================
// Permission Service Interface
// ============================================

export interface IPermissionService {
  // Check permissions
  checkToolPermission(
    agentType: AgentType,
    projectId: string,
    tool: string
  ): PermissionResult;

  checkFilePermission(
    agentType: AgentType,
    projectId: string,
    path: string,
    action: 'read' | 'write'
  ): PermissionResult;

  checkCommandPermission(
    agentType: AgentType,
    projectId: string,
    command: string
  ): PermissionResult;

  checkGitPermission(
    agentType: AgentType,
    projectId: string,
    operation: GitOperation,
    branch?: string
  ): PermissionResult;

  checkAPIPermission(
    agentType: AgentType,
    projectId: string,
    url: string
  ): PermissionResult;

  checkResourceLimit(
    agentType: AgentType,
    projectId: string,
    resource: keyof ResourceLimits,
    value: number
  ): PermissionResult;

  // Get effective permissions (global + project overrides)
  getEffectivePermissions(
    agentType: AgentType,
    projectId: string
  ): AgentPermissionSet;

  // Management
  setGlobalPermissions(
    agentType: AgentType,
    permissions: Partial<AgentPermissionSet>
  ): Promise<void>;

  setProjectPermissions(
    projectId: string,
    agentType: AgentType,
    permissions: Partial<AgentPermissionSet>
  ): Promise<void>;

  getGlobalPermissions(agentType: AgentType): AgentPermissionSet | undefined;

  getProjectPermissions(
    projectId: string,
    agentType: AgentType
  ): Partial<AgentPermissionSet> | undefined;

  // Permission requests
  requestPermission(request: Omit<PermissionRequest, 'id' | 'status' | 'requestedAt'>): Promise<PermissionRequestResult>;
  approvePermissionRequest(requestId: string, approver: string): Promise<void>;
  denyPermissionRequest(requestId: string, approver: string, reason: string): Promise<void>;
  getPendingRequests(projectId?: string): Promise<PermissionRequest[]>;

  // Audit
  getPermissionViolations(filter?: ViolationFilter): Promise<PermissionViolation[]>;
  recordViolation(violation: Omit<PermissionViolation, 'id' | 'timestamp'>): Promise<void>;
}

// ============================================
// Permission Enforcer Interface
// ============================================

export interface IPermissionEnforcer {
  enforcePermission(
    agentType: AgentType,
    projectId: string,
    action: AgentAction
  ): Promise<void>;

  enforceResourceLimits(
    agentType: AgentType,
    projectId: string,
    currentUsage: Partial<ResourceLimits>
  ): void;
}

// ============================================
// Permission Resolver Interface
// ============================================

export interface IPermissionResolver {
  resolve(
    agentType: AgentType,
    projectId: string
  ): AgentPermissionSet;

  mergePermissions(
    global: AgentPermissionSet,
    projectOverrides: Partial<AgentPermissionSet>
  ): AgentPermissionSet;
}

// ============================================
// Violation Recorder Interface
// ============================================

export interface IViolationRecorder {
  record(violation: Omit<PermissionViolation, 'id' | 'timestamp' | 'repeated' | 'repeatCount'>): Promise<PermissionViolation>;
  getViolations(filter?: ViolationFilter): Promise<PermissionViolation[]>;
  getViolationCount(agentType: AgentType, projectId: string, hours?: number): Promise<number>;
}

// ============================================
// Violation Alerter Interface
// ============================================

export interface IViolationAlerter {
  checkAndAlert(violation: PermissionViolation): Promise<void>;
  setThreshold(agentType: AgentType, threshold: number, windowHours: number): void;
}
```

### Custom Error Types

```typescript
// packages/gates/src/permissions/errors.ts

export class PermissionDeniedError extends Error {
  constructor(
    message: string,
    public readonly suggestedAlternative?: string,
    public readonly violation?: PermissionViolation
  ) {
    super(message);
    this.name = 'PermissionDeniedError';
  }
}

export class PermissionApprovalRequiredError extends Error {
  constructor(
    message: string,
    public readonly requestId: string
  ) {
    super(message);
    this.name = 'PermissionApprovalRequiredError';
  }
}

export class ResourceLimitExceededError extends Error {
  constructor(
    message: string,
    public readonly resource: keyof ResourceLimits,
    public readonly limit: number,
    public readonly current: number
  ) {
    super(message);
    this.name = 'ResourceLimitExceededError';
  }
}
```

## Implementation Phases

### Phase 1: Core Types and Defaults (Week 1)

**Goal:** Establish the type system and default permission configurations.

**Tasks:**
1. Create `packages/gates/src/permissions/types.ts` with all interfaces
2. Create `packages/gates/src/permissions/errors.ts` with custom errors
3. Create `packages/gates/src/permissions/defaults.ts` with default permissions for all 9 agent types
4. Add AgentType enum to `@tamma/shared` if not present
5. Update `packages/gates/package.json` with dependencies

**Deliverables:**
- Complete type definitions
- Default permission sets for: scrum_master, architect, researcher, analyst, planner, implementer, reviewer, tester, documenter
- Unit tests for type validation

**Default Permissions Summary:**

| Agent Type | Tools | Files | Commands | Git |
|------------|-------|-------|----------|-----|
| scrum_master | Read-only tools | Read all | None | None |
| architect | Read + design docs write | Read all, write docs | None | None |
| researcher | Read-only | Read (no secrets) | None | None |
| analyst | Read-only | Read all | None | None |
| planner | Read-only | Read all | None | None |
| implementer | All tools | Read/write src, tests | npm, pnpm, yarn, git, tsc | commit, push, create_branch |
| reviewer | Read-only | Read all | None | None |
| tester | Read + test tools | Read all, write tests | Test commands only | None |
| documenter | Read + write docs | Read all, write docs | None | None |

### Phase 2: Pattern Matchers (Week 1-2)

**Goal:** Implement pattern matching utilities.

**Tasks:**
1. Create `glob-matcher.ts` using micromatch for file path matching
2. Create `command-matcher.ts` for shell command pattern matching
3. Create `tool-matcher.ts` for tool name matching
4. Add comprehensive unit tests for edge cases

**Deliverables:**
- Glob pattern matching with negation support
- Command pattern matching with wildcard and regex support
- Tool name matching (exact and pattern-based)
- 90%+ test coverage for matchers

### Phase 3: Permission Resolution (Week 2)

**Goal:** Implement permission inheritance and override resolution.

**Tasks:**
1. Create `permission-resolver.ts` for merging global + project permissions
2. Implement deep merge logic for permission sets
3. Handle "more restrictive wins" vs "override" semantics
4. Create unit tests for resolution scenarios

**Resolution Rules:**
- Global permissions serve as defaults
- Project permissions can override (more permissive or restrictive)
- Denied patterns always take precedence over allowed
- Resource limits: project can only be more restrictive, not more permissive

### Phase 4: Permission Service (Week 2-3)

**Goal:** Implement the core permission checking service.

**Tasks:**
1. Create `permission-service.ts` implementing `IPermissionService`
2. Implement all permission check methods
3. Integrate with permission resolver
4. Add caching for effective permissions
5. Implement permission request handling
6. Create comprehensive integration tests

**Caching Strategy:**
- Cache effective permissions per (agentType, projectId) pair
- Invalidate on permission updates
- TTL-based expiration (configurable, default 5 minutes)

### Phase 5: Permission Enforcement (Week 3)

**Goal:** Create the enforcement middleware.

**Tasks:**
1. Create `permission-enforcer.ts` implementing `IPermissionEnforcer`
2. Integrate with the engine's action execution flow
3. Implement approval workflow for `require_approval` actions
4. Add resource limit tracking and enforcement
5. Create integration tests with mock engine

**Enforcement Flow:**
```
Agent Action Request
       │
       ▼
┌─────────────────┐
│ Check Permission │
└────────┬────────┘
         │
    ┌────┴────┐
    │         │
    ▼         ▼
 Allowed   Denied
    │         │
    │    ┌────┴────┐
    │    │         │
    │    ▼         ▼
    │  Block   Requires
    │            Approval
    │              │
    ▼              ▼
 Execute     Request
            Approval
```

### Phase 6: Violation Recording and Alerting (Week 3-4)

**Goal:** Implement audit trail and alerting for violations.

**Tasks:**
1. Create `violation-recorder.ts` for recording violations to event store
2. Create `violation-alerter.ts` for threshold-based alerting
3. Integrate with `@tamma/events` for persistence
4. Integrate with `@tamma/observability` for metrics
5. Add alert channels (Scrum Master, Alert Manager)

**Alert Thresholds (configurable):**
- 3 violations in 1 hour: Warning
- 5 violations in 1 hour: Alert to Scrum Master
- 10 violations in 1 hour: Critical alert, possible agent suspension

### Phase 7: Engine Integration (Week 4)

**Goal:** Integrate permissions into the existing engine flow.

**Tasks:**
1. Modify `TammaEngine` to inject permission enforcer
2. Add permission checks before agent task execution
3. Add permission checks in agent provider implementations
4. Handle permission request workflows
5. Create E2E tests for permission enforcement

**Integration Points:**
- Before `executeTask` in agent providers
- Before file operations
- Before command execution
- Before git operations
- In the Scrum Master task loop (Story 6-10)

### Phase 8: Configuration and Management API (Week 4-5)

**Goal:** Add configuration loading and management endpoints.

**Tasks:**
1. Add YAML configuration parsing for permissions
2. Create management API endpoints (for future dashboard)
3. Implement permission import/export
4. Add validation for permission configurations

## Dependencies

### Internal Dependencies

| Package | Usage |
|---------|-------|
| `@tamma/shared` | Common types, utilities, AgentType enum |
| `@tamma/events` | Event store for violation recording |
| `@tamma/observability` | Metrics and logging |
| `@tamma/providers` | Agent provider interfaces |
| `@tamma/orchestrator` | Engine integration |

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `micromatch` | ^4.0.5 | Glob pattern matching |
| `minimatch` | ^9.0.0 | Additional glob support (already in use) |
| `nanoid` | ^5.0.0 | ID generation (already in workspace) |

### Related Stories

| Story | Relationship |
|-------|-------------|
| Story 6-7 (Cost Monitoring) | Resource limits enforcement |
| Story 6-9 (Knowledge Base) | Prohibited actions use similar patterns |
| Story 6-10 (Scrum Master Loop) | Approval workflow integration |

## Testing Strategy

### Unit Tests

```typescript
// Example test structure
describe('PermissionService', () => {
  describe('checkToolPermission', () => {
    it('should allow tool when in allowed list', () => {});
    it('should deny tool when in denied list', () => {});
    it('should require approval when in requireApproval list', () => {});
    it('should deny by default when tool not in any list', () => {});
  });

  describe('checkFilePermission', () => {
    it('should allow read when path matches allowed pattern', () => {});
    it('should deny read when path matches denied pattern', () => {});
    it('should prioritize deny over allow patterns', () => {});
    it('should handle nested glob patterns', () => {});
  });

  describe('checkCommandPermission', () => {
    it('should match exact commands', () => {});
    it('should match wildcard patterns', () => {});
    it('should block dangerous commands by default', () => {});
  });
});

describe('PermissionResolver', () => {
  describe('resolve', () => {
    it('should return global defaults when no project overrides', () => {});
    it('should merge project overrides with global', () => {});
    it('should handle resource limit overrides (more restrictive only)', () => {});
  });
});

describe('GlobMatcher', () => {
  it('should match simple patterns', () => {});
  it('should handle negation', () => {});
  it('should handle dotfiles', () => {});
  it('should handle brace expansion', () => {});
});
```

### Integration Tests

```typescript
describe('Permission Enforcement Integration', () => {
  describe('Engine + Enforcer', () => {
    it('should block agent when permission denied', async () => {});
    it('should allow agent when permission granted', async () => {});
    it('should record violation when action blocked', async () => {});
    it('should trigger alert on repeated violations', async () => {});
  });

  describe('Approval Workflow', () => {
    it('should pause execution pending approval', async () => {});
    it('should continue after approval', async () => {});
    it('should abort after denial', async () => {});
    it('should expire after timeout', async () => {});
  });
});
```

### E2E Tests

```typescript
describe('Permission System E2E', () => {
  it('should enforce implementer cannot write to .env files', async () => {});
  it('should enforce researcher cannot execute bash commands', async () => {});
  it('should allow implementer to commit to feature branch', async () => {});
  it('should block implementer from pushing to main', async () => {});
  it('should enforce resource limits', async () => {});
});
```

### Test Coverage Targets

| Component | Target Coverage |
|-----------|-----------------|
| Matchers | 95% |
| Validators | 90% |
| Permission Service | 90% |
| Permission Resolver | 90% |
| Violation Recorder | 85% |
| Integration | 80% |

## Configuration

### YAML Configuration Schema

```yaml
# config/permissions.yaml

permissions:
  # Global defaults (apply to all projects)
  global:
    # Always blocked patterns (security)
    blocked_patterns:
      - "**/.env*"
      - "**/secrets/**"
      - "**/*.pem"
      - "**/*.key"
      - "**/credentials*"
      - "**/.ssh/**"
      - "**/node_modules/**"

    # Always blocked commands (security)
    blocked_commands:
      - "rm -rf /"
      - "rm -rf /*"
      - "sudo *"
      - "curl * | bash"
      - "wget * | sh"
      - "chmod 777 *"
      - ":(){ :|:& };:"  # Fork bomb
      - "mkfs*"
      - "dd if=*"

    # Actions requiring human approval
    require_approval:
      - delete_branch
      - modify_ci_config
      - change_dependencies
      - force_push
      - merge_to_main

    # Global resource defaults
    resource_limits:
      max_tokens_per_task: 100000
      max_budget_per_task_usd: 5.0
      max_duration_minutes: 60
      max_files_modified: 20
      max_lines_changed: 2000

  # Per-agent-type overrides (from defaults)
  agent_types:
    implementer:
      resources:
        max_budget_per_task_usd: 15.0
        max_files_modified: 50
        max_lines_changed: 5000
      commands:
        allowed:
          - "npm *"
          - "pnpm *"
          - "yarn *"
          - "git *"
          - "tsc *"
          - "eslint *"
          - "prettier *"
          - "vitest *"
          - "jest *"

    tester:
      commands:
        allowed:
          - "npm test*"
          - "npm run test*"
          - "pnpm test*"
          - "vitest *"
          - "jest *"
          - "playwright *"
        denied:
          - "npm install*"
          - "npm uninstall*"

  # Per-project overrides
  projects:
    - id: "project-legacy"
      overrides:
        implementer:
          files:
            write:
              denied:
                - "src/legacy/**/*"  # Don't touch legacy code
        global:
          blocked_patterns:
            - "src/legacy/**/*"  # Extra protection for legacy

    - id: "project-sensitive"
      overrides:
        global:
          require_approval:
            - any_file_write  # Extra cautious
        implementer:
          resources:
            max_budget_per_task_usd: 2.0  # Lower budget

  # Approval workflow settings
  approval:
    default_approvers: ["scrum_master"]
    fallback_approvers: ["human"]
    escalation_timeout_minutes: 30
    auto_deny_after_minutes: 60
    notification_channels: ["slack", "email"]

  # Violation alerting
  violations:
    warning_threshold: 3
    warning_window_hours: 1
    alert_threshold: 5
    alert_window_hours: 1
    critical_threshold: 10
    critical_window_hours: 1
    alert_channels: ["scrum_master", "alert_manager"]

  # Performance settings
  cache:
    enabled: true
    ttl_seconds: 300  # 5 minutes
    max_entries: 1000
```

### Environment Variables

```bash
# Permission system settings
TAMMA_PERMISSIONS_CONFIG_PATH=./config/permissions.yaml
TAMMA_PERMISSIONS_CACHE_ENABLED=true
TAMMA_PERMISSIONS_CACHE_TTL=300

# Default resource limits (can be overridden in config)
TAMMA_DEFAULT_MAX_BUDGET_USD=5.0
TAMMA_DEFAULT_MAX_TOKENS=100000
TAMMA_DEFAULT_MAX_DURATION_MINUTES=60

# Violation alerting
TAMMA_VIOLATION_ALERT_ENABLED=true
TAMMA_VIOLATION_ALERT_THRESHOLD=5
```

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Permission check latency | < 10ms (p95) | Observability metrics |
| Zero unauthorized actions | 100% | Violation audit trail |
| Audit trail coverage | 100% | Event store records |
| Approval workflow completion | < 5 minutes | Workflow duration tracking |
| Cache hit rate | > 80% | Cache metrics |
| Test coverage | > 85% | Jest coverage report |

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Performance impact from permission checks | Caching, efficient pattern matching |
| False positives blocking legitimate actions | Careful pattern design, easy override mechanism |
| Approval workflow delays | Timeout-based auto-denial, clear escalation |
| Configuration complexity | Good defaults, validation, documentation |
| Breaking existing workflows | Gradual rollout, feature flags |

## Future Enhancements (Out of Scope)

- UI for permission management (Story 6-6 expansion)
- Dynamic permission learning from agent behavior
- Role-based permission inheritance
- Permission templates
- Audit log export and compliance reporting
- Multi-tenant permission isolation
