# Story 6-8: Agent Permissions System

## User Story

As a **Tamma administrator**, I need a comprehensive permissions system for agents so that I can control what actions each agent type can perform, with both global defaults and per-project overrides.

## Description

Implement a hierarchical permissions system that defines what tools, operations, and resources each agent type can access. Permissions can be set globally as defaults and overridden at the project level. The system enforces permissions before any agent action.

## Acceptance Criteria

### AC1: Permission Categories
- [ ] Tool permissions (Read, Write, Edit, Bash, Glob, Grep, Git, etc.)
- [ ] File system permissions (paths, patterns, read/write)
- [ ] Command permissions (allowed/blocked shell commands)
- [ ] API permissions (which external APIs can be called)
- [ ] Resource limits (max tokens, max budget, max time)
- [ ] Git permissions (commit, push, branch, merge)

### AC2: Per-Agent-Type Defaults
- [ ] Define default permissions for each agent type
- [ ] Scrum Master: full project access, no code execution
- [ ] Architect: read all, write design docs, no direct code changes
- [ ] Researcher: read-only, web search, no file writes
- [ ] Analyst: read-only, issue/PR access
- [ ] Planner: read-only, can create plans
- [ ] Implementer: full file access within project, command execution
- [ ] Reviewer: read-only, can comment
- [ ] Tester: read/write tests, command execution for tests only
- [ ] Documenter: write docs only, no code changes

### AC3: Global Permissions
- [ ] System-wide defaults for all projects
- [ ] Global blocked patterns (e.g., .env, secrets)
- [ ] Global allowed patterns
- [ ] Global resource limits
- [ ] Require approval for certain actions

### AC4: Project-Level Overrides
- [ ] Override global permissions per project
- [ ] Stricter or more permissive than global
- [ ] Project-specific blocked/allowed paths
- [ ] Project-specific resource limits
- [ ] Inherit from global with modifications

### AC5: Permission Enforcement
- [ ] Check permissions before every agent action
- [ ] Block unauthorized actions immediately
- [ ] Log permission denials
- [ ] Alert on repeated permission violations
- [ ] Graceful error messages to agent

### AC6: Permission Requests
- [ ] Agents can request elevated permissions
- [ ] Request routed to Scrum Master / human
- [ ] Temporary permission grants
- [ ] Audit trail of grants

### AC7: Management UI
- [ ] View/edit global permissions
- [ ] View/edit per-project permissions
- [ ] View permission inheritance
- [ ] View permission violations log
- [ ] Test permission checks

## Technical Design

### Permission Schema

```typescript
interface Permission {
  id: string;
  category: PermissionCategory;
  resource: string;        // Tool name, path pattern, command, etc.
  action: PermissionAction;
  conditions?: PermissionCondition[];
}

type PermissionCategory =
  | 'tool'           // Claude Code tools
  | 'file'           // File system access
  | 'command'        // Shell commands
  | 'api'            // External APIs
  | 'git'            // Git operations
  | 'resource';      // Resource limits

type PermissionAction = 'allow' | 'deny' | 'require_approval';

interface PermissionCondition {
  type: 'pattern' | 'limit' | 'time' | 'context';
  value: unknown;
}

// Full permission set for an agent type
interface AgentPermissionSet {
  agentType: AgentType;
  scope: 'global' | 'project';
  scopeId?: string;  // Project ID if scope is 'project'

  tools: ToolPermissions;
  files: FilePermissions;
  commands: CommandPermissions;
  apis: APIPermissions;
  git: GitPermissions;
  resources: ResourceLimits;
}

interface ToolPermissions {
  allowed: string[];        // ['Read', 'Glob', 'Grep']
  denied: string[];         // ['Bash', 'Write']
  requireApproval: string[]; // ['Edit']
}

interface FilePermissions {
  read: {
    allowed: string[];      // ['**/*']
    denied: string[];       // ['**/.env', '**/secrets/**']
  };
  write: {
    allowed: string[];      // ['src/**/*.ts', 'tests/**/*.ts']
    denied: string[];       // ['package.json', 'tsconfig.json']
  };
}

interface CommandPermissions {
  allowed: string[];        // ['npm test', 'npm run lint']
  denied: string[];         // ['rm -rf', 'sudo *']
  patterns: {
    allow: RegExp[];
    deny: RegExp[];
  };
}

interface GitPermissions {
  canCommit: boolean;
  canPush: boolean;
  canCreateBranch: boolean;
  canMerge: boolean;
  canDeleteBranch: boolean;
  protectedBranches: string[];  // Cannot modify
}

interface ResourceLimits {
  maxTokensPerTask: number;
  maxBudgetPerTask: number;
  maxDurationMinutes: number;
  maxFilesModified: number;
  maxLinesChanged: number;
}
```

### Default Agent Permissions

```typescript
const defaultPermissions: Record<AgentType, Partial<AgentPermissionSet>> = {
  scrum_master: {
    tools: {
      allowed: ['Read', 'Glob', 'Grep', 'WebSearch', 'WebFetch'],
      denied: ['Bash', 'Write', 'Edit'],
      requireApproval: [],
    },
    files: {
      read: { allowed: ['**/*'], denied: [] },
      write: { allowed: [], denied: ['**/*'] },
    },
    git: {
      canCommit: false,
      canPush: false,
      canCreateBranch: false,
      canMerge: false,
      canDeleteBranch: false,
      protectedBranches: ['*'],
    },
    resources: {
      maxTokensPerTask: 50000,
      maxBudgetPerTask: 1.0,
      maxDurationMinutes: 30,
      maxFilesModified: 0,
      maxLinesChanged: 0,
    },
  },

  architect: {
    tools: {
      allowed: ['Read', 'Glob', 'Grep', 'WebSearch', 'WebFetch'],
      denied: ['Bash'],
      requireApproval: ['Write', 'Edit'],
    },
    files: {
      read: { allowed: ['**/*'], denied: [] },
      write: { allowed: ['docs/**/*.md', 'architecture/**/*'], denied: ['src/**/*'] },
    },
    git: {
      canCommit: false,
      canPush: false,
      canCreateBranch: false,
      canMerge: false,
      canDeleteBranch: false,
      protectedBranches: ['main', 'master'],
    },
    resources: {
      maxTokensPerTask: 100000,
      maxBudgetPerTask: 2.0,
      maxDurationMinutes: 60,
      maxFilesModified: 5,
      maxLinesChanged: 500,
    },
  },

  researcher: {
    tools: {
      allowed: ['Read', 'Glob', 'Grep', 'WebSearch', 'WebFetch'],
      denied: ['Write', 'Edit', 'Bash'],
      requireApproval: [],
    },
    files: {
      read: { allowed: ['**/*'], denied: ['**/.env*', '**/secrets/**'] },
      write: { allowed: [], denied: ['**/*'] },
    },
    resources: {
      maxTokensPerTask: 30000,
      maxBudgetPerTask: 0.5,
      maxDurationMinutes: 15,
      maxFilesModified: 0,
      maxLinesChanged: 0,
    },
  },

  implementer: {
    tools: {
      allowed: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
      denied: [],
      requireApproval: [],
    },
    files: {
      read: { allowed: ['**/*'], denied: [] },
      write: {
        allowed: ['src/**/*', 'tests/**/*', 'package.json'],
        denied: ['**/.env*', '**/secrets/**', '.github/**/*'],
      },
    },
    commands: {
      allowed: ['npm *', 'pnpm *', 'yarn *', 'git *', 'tsc *', 'eslint *'],
      denied: ['rm -rf /*', 'sudo *', 'curl * | bash', 'wget * | sh'],
    },
    git: {
      canCommit: true,
      canPush: true,
      canCreateBranch: true,
      canMerge: false,
      canDeleteBranch: false,
      protectedBranches: ['main', 'master'],
    },
    resources: {
      maxTokensPerTask: 200000,
      maxBudgetPerTask: 10.0,
      maxDurationMinutes: 120,
      maxFilesModified: 50,
      maxLinesChanged: 5000,
    },
  },

  // ... similar for other agent types
};
```

### Permission Service

```typescript
interface IPermissionService {
  // Check permissions
  checkToolPermission(agentType: AgentType, projectId: string, tool: string): PermissionResult;
  checkFilePermission(agentType: AgentType, projectId: string, path: string, action: 'read' | 'write'): PermissionResult;
  checkCommandPermission(agentType: AgentType, projectId: string, command: string): PermissionResult;
  checkGitPermission(agentType: AgentType, projectId: string, operation: GitOperation): PermissionResult;
  checkResourceLimit(agentType: AgentType, projectId: string, resource: string, value: number): PermissionResult;

  // Get effective permissions (global + project overrides)
  getEffectivePermissions(agentType: AgentType, projectId: string): AgentPermissionSet;

  // Management
  setGlobalPermissions(agentType: AgentType, permissions: Partial<AgentPermissionSet>): Promise<void>;
  setProjectPermissions(projectId: string, agentType: AgentType, permissions: Partial<AgentPermissionSet>): Promise<void>;

  // Permission requests
  requestPermission(request: PermissionRequest): Promise<PermissionRequestResult>;
  approvePermissionRequest(requestId: string, approver: string): Promise<void>;
  denyPermissionRequest(requestId: string, approver: string, reason: string): Promise<void>;

  // Audit
  getPermissionViolations(filter?: ViolationFilter): Promise<PermissionViolation[]>;
}

interface PermissionResult {
  allowed: boolean;
  reason?: string;
  requiresApproval?: boolean;
  suggestedAlternative?: string;
}

interface PermissionRequest {
  agentType: AgentType;
  projectId: string;
  taskId: string;
  requestedPermission: Permission;
  reason: string;
  duration?: number;  // Temporary grant duration in minutes
}
```

### Permission Enforcement

```typescript
class PermissionEnforcer {
  private permissionService: IPermissionService;
  private logger: ILogger;

  // Called before every agent action
  async enforcePermission(
    agentType: AgentType,
    projectId: string,
    action: AgentAction
  ): Promise<void> {
    let result: PermissionResult;

    switch (action.type) {
      case 'tool':
        result = this.permissionService.checkToolPermission(
          agentType, projectId, action.toolName
        );
        break;

      case 'file_read':
        result = this.permissionService.checkFilePermission(
          agentType, projectId, action.path, 'read'
        );
        break;

      case 'file_write':
        result = this.permissionService.checkFilePermission(
          agentType, projectId, action.path, 'write'
        );
        break;

      case 'command':
        result = this.permissionService.checkCommandPermission(
          agentType, projectId, action.command
        );
        break;

      case 'git':
        result = this.permissionService.checkGitPermission(
          agentType, projectId, action.operation
        );
        break;

      default:
        result = { allowed: true };
    }

    if (!result.allowed) {
      // Log violation
      this.logger.warn('Permission denied', {
        agentType,
        projectId,
        action,
        reason: result.reason,
      });

      // Record violation
      await this.recordViolation(agentType, projectId, action, result);

      // Throw error to stop agent
      throw new PermissionDeniedError(
        `Permission denied: ${result.reason}`,
        result.suggestedAlternative
      );
    }

    if (result.requiresApproval) {
      // Route to approval workflow
      const approved = await this.requestApproval(agentType, projectId, action);
      if (!approved) {
        throw new PermissionDeniedError('Permission request denied');
      }
    }
  }
}
```

## Configuration

```yaml
permissions:
  # Global defaults
  global:
    blocked_patterns:
      - "**/.env*"
      - "**/secrets/**"
      - "**/*.pem"
      - "**/*.key"
      - "**/credentials*"

    blocked_commands:
      - "rm -rf /"
      - "sudo *"
      - "curl * | bash"
      - "wget * | sh"
      - "chmod 777 *"

    require_approval:
      - delete_branch
      - modify_ci_config
      - change_dependencies

  # Per-agent-type overrides (see defaults above)
  agent_types:
    implementer:
      resources:
        maxBudgetPerTask: 15.0  # Override default

  # Per-project overrides
  projects:
    - id: "project-a"
      overrides:
        implementer:
          files:
            write:
              denied:
                - "src/legacy/**/*"  # Additional restriction
        researcher:
          tools:
            allowed:
              - "Bash"  # Allow for this project

  # Approval workflow
  approval:
    default_approvers: ["scrum_master"]
    escalation_timeout_minutes: 30
    auto_deny_after_minutes: 60
```

## Dependencies

- All agent implementations
- Scrum Master (for approvals)
- Alert Manager (for violation alerts)
- Event Store (for audit trail)

## Testing Strategy

### Unit Tests
- Permission checking logic
- Pattern matching (glob)
- Command matching (regex)
- Inheritance resolution

### Integration Tests
- End-to-end permission enforcement
- Approval workflows
- Violation recording

## Success Metrics

- Zero unauthorized actions
- Permission check latency < 10ms
- 100% audit trail coverage
- Approval workflow completion < 5 minutes

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
