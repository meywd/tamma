/**
 * Core permission service implementation
 * @module @tamma/gates/permissions/permission-service
 */

import { nanoid } from 'nanoid';
import type {
  AgentType,
  AgentPermissionSet,
  IPermissionService,
  PermissionResult,
  GitOperation,
  ResourceLimits,
  PermissionRequest,
  PermissionRequestResult,
  PermissionViolation,
  ViolationFilter,
  Permission,
} from './types.js';
import { PermissionResolver } from './permission-resolver.js';
import { GlobMatcher, createFileGlobMatcher } from './matchers/glob-matcher.js';
import { CommandMatcher, createCommandMatcher } from './matchers/command-matcher.js';
import { ToolMatcher, createToolMatcher } from './matchers/tool-matcher.js';
import type { ILogger } from '@tamma/shared';

/**
 * Core permission service for checking and managing agent permissions
 */
export class PermissionService implements IPermissionService {
  private readonly resolver: PermissionResolver;
  private readonly permissionRequests: Map<string, PermissionRequest>;
  private readonly violations: PermissionViolation[];
  private readonly logger?: ILogger;

  constructor(options?: { cacheTtlMs?: number; logger?: ILogger }) {
    this.resolver = new PermissionResolver(options?.cacheTtlMs);
    this.permissionRequests = new Map();
    this.violations = [];
    this.logger = options?.logger;
  }

  // ============================================
  // Permission Check Methods
  // ============================================

  /**
   * Check if an agent is allowed to use a specific tool
   */
  checkToolPermission(
    agentType: AgentType,
    projectId: string,
    tool: string,
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);
    const matcher = createToolMatcher(
      permissions.tools.allowed,
      permissions.tools.denied,
      permissions.tools.requireApproval,
    );

    const result = matcher.check(tool);

    if (result.matchedIn === 'denied') {
      this.logger?.debug(`Tool ${tool} denied for ${agentType} in project ${projectId}`);
      return {
        allowed: false,
        reason: `Tool '${tool}' is not allowed for agent type '${agentType}'`,
        matchedRule: this.createMatchedRule('tool', tool, 'deny'),
      };
    }

    if (result.matchedIn === 'requireApproval') {
      this.logger?.debug(`Tool ${tool} requires approval for ${agentType} in project ${projectId}`);
      return {
        allowed: false,
        requiresApproval: true,
        reason: `Tool '${tool}' requires approval for agent type '${agentType}'`,
        matchedRule: this.createMatchedRule('tool', tool, 'require_approval'),
      };
    }

    if (result.matchedIn === 'allowed') {
      return { allowed: true };
    }

    // Not in any list - deny by default
    return {
      allowed: false,
      reason: `Tool '${tool}' is not in the allowed list for agent type '${agentType}'`,
    };
  }

  /**
   * Check if an agent is allowed to access a file
   */
  checkFilePermission(
    agentType: AgentType,
    projectId: string,
    path: string,
    action: 'read' | 'write',
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);
    const filePerms = action === 'read' ? permissions.files.read : permissions.files.write;

    const matcher = createFileGlobMatcher(filePerms.allowed, filePerms.denied);
    const result = matcher.isAllowed(path);

    if (!result.matches) {
      const isDenied = matcher.isDenied(path);
      const reason = isDenied.matches
        ? `File '${path}' matches denied pattern '${isDenied.matchedPattern}'`
        : `File '${path}' is not in the allowed patterns for ${action}`;

      this.logger?.debug(
        `File ${action} denied for ${path}: ${reason} (agent: ${agentType}, project: ${projectId})`,
      );

      return {
        allowed: false,
        reason,
        matchedRule: isDenied.matches
          ? this.createMatchedRule('file', isDenied.matchedPattern ?? path, 'deny')
          : undefined,
      };
    }

    return {
      allowed: true,
      matchedRule: this.createMatchedRule('file', result.matchedPattern ?? path, 'allow'),
    };
  }

  /**
   * Check if an agent is allowed to execute a command
   */
  checkCommandPermission(
    agentType: AgentType,
    projectId: string,
    command: string,
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);

    // First check for dangerous patterns regardless of permissions
    const dangerCheck = CommandMatcher.containsDangerousPatterns(command);
    if (dangerCheck.dangerous) {
      this.logger?.warn(
        `Dangerous command blocked: ${command} (patterns: ${dangerCheck.patterns.join(', ')})`,
      );
      return {
        allowed: false,
        reason: `Command contains dangerous patterns: ${dangerCheck.patterns.join(', ')}`,
        matchedRule: this.createMatchedRule('command', dangerCheck.patterns[0] ?? command, 'deny'),
      };
    }

    const matcher = createCommandMatcher(
      permissions.commands.allowed,
      permissions.commands.denied,
      permissions.commands.patterns,
    );

    const result = matcher.isAllowed(command);

    if (!result.matches) {
      const isDenied = matcher.isDenied(command);
      const reason = isDenied.matches
        ? `Command matches denied pattern '${isDenied.matchedPattern}'`
        : `Command '${command}' is not in the allowed list for agent type '${agentType}'`;

      this.logger?.debug(`Command denied: ${command} - ${reason}`);

      return {
        allowed: false,
        reason,
        matchedRule: isDenied.matches
          ? this.createMatchedRule('command', isDenied.matchedPattern ?? command, 'deny')
          : undefined,
      };
    }

    return {
      allowed: true,
      matchedRule: this.createMatchedRule('command', result.matchedPattern ?? command, 'allow'),
    };
  }

  /**
   * Check if an agent is allowed to perform a git operation
   */
  checkGitPermission(
    agentType: AgentType,
    projectId: string,
    operation: GitOperation,
    branch?: string,
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);
    const gitPerms = permissions.git;

    // Map operation to permission flag
    const operationMap: Record<GitOperation, keyof typeof gitPerms> = {
      commit: 'canCommit',
      push: 'canPush',
      create_branch: 'canCreateBranch',
      delete_branch: 'canDeleteBranch',
      merge: 'canMerge',
      rebase: 'canRebase',
      force_push: 'canForcePush',
    };

    const permFlag = operationMap[operation];
    if (permFlag === undefined) {
      return {
        allowed: false,
        reason: `Unknown git operation: ${operation}`,
      };
    }

    // Check if operation is allowed
    if (!gitPerms[permFlag]) {
      this.logger?.debug(`Git operation ${operation} denied for ${agentType} in project ${projectId}`);
      return {
        allowed: false,
        reason: `Git operation '${operation}' is not allowed for agent type '${agentType}'`,
        matchedRule: this.createMatchedRule('git', operation, 'deny'),
      };
    }

    // Check protected branches if branch is specified
    if (branch) {
      const branchMatcher = new GlobMatcher([], gitPerms.protectedBranches);
      const isProtected = branchMatcher.isDenied(branch);

      if (isProtected.matches) {
        this.logger?.debug(`Branch ${branch} is protected, operation ${operation} denied`);
        return {
          allowed: false,
          reason: `Branch '${branch}' is protected (matches '${isProtected.matchedPattern}')`,
          matchedRule: this.createMatchedRule('git', isProtected.matchedPattern ?? branch, 'deny'),
        };
      }
    }

    return { allowed: true };
  }

  /**
   * Check if an agent is allowed to access an API
   */
  checkAPIPermission(
    agentType: AgentType,
    projectId: string,
    url: string,
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);
    const apiPerms = permissions.apis;

    // Create a matcher for URL patterns
    const matcher = new GlobMatcher(apiPerms.allowed, apiPerms.denied);
    const result = matcher.isAllowed(url);

    if (!result.matches) {
      const isDenied = matcher.isDenied(url);
      const reason = isDenied.matches
        ? `URL matches denied pattern '${isDenied.matchedPattern}'`
        : `URL '${url}' is not in the allowed patterns`;

      return {
        allowed: false,
        reason,
        matchedRule: isDenied.matches
          ? this.createMatchedRule('api', isDenied.matchedPattern ?? url, 'deny')
          : undefined,
      };
    }

    // Check if URL requires approval
    const approvalMatcher = new GlobMatcher(apiPerms.requireApproval, []);
    const requiresApproval = approvalMatcher.matchAny(url, apiPerms.requireApproval);

    if (requiresApproval.matches) {
      return {
        allowed: false,
        requiresApproval: true,
        reason: `URL '${url}' requires approval`,
        matchedRule: this.createMatchedRule('api', requiresApproval.matchedPattern ?? url, 'require_approval'),
      };
    }

    return { allowed: true };
  }

  /**
   * Check if a resource usage is within limits
   */
  checkResourceLimit(
    agentType: AgentType,
    projectId: string,
    resource: keyof ResourceLimits,
    value: number,
  ): PermissionResult {
    const permissions = this.getEffectivePermissions(agentType, projectId);
    const limit = permissions.resources[resource];

    if (value > limit) {
      return {
        allowed: false,
        reason: `Resource limit exceeded: ${resource} = ${value} (limit: ${limit})`,
        matchedRule: this.createMatchedRule('resource', resource, 'deny'),
      };
    }

    return { allowed: true };
  }

  // ============================================
  // Permission Management Methods
  // ============================================

  /**
   * Get effective permissions for an agent (global + project overrides)
   */
  getEffectivePermissions(agentType: AgentType, projectId: string): AgentPermissionSet {
    return this.resolver.resolve(agentType, projectId);
  }

  /**
   * Set global permissions for an agent type
   */
  async setGlobalPermissions(
    agentType: AgentType,
    permissions: Partial<AgentPermissionSet>,
  ): Promise<void> {
    const current = this.resolver.getGlobalPermissions(agentType);
    if (current) {
      const merged = this.resolver.mergePermissions(current, permissions);
      this.resolver.setGlobalPermissions(agentType, merged);
    } else {
      // Use defaults as base if no current global permissions
      const defaults = this.getEffectivePermissions(agentType, '');
      const merged = this.resolver.mergePermissions(defaults, permissions);
      this.resolver.setGlobalPermissions(agentType, merged);
    }
  }

  /**
   * Set project-level permission overrides
   */
  async setProjectPermissions(
    projectId: string,
    agentType: AgentType,
    permissions: Partial<AgentPermissionSet>,
  ): Promise<void> {
    this.resolver.setProjectPermissions(projectId, agentType, permissions);
  }

  /**
   * Get global permissions for an agent type
   */
  getGlobalPermissions(agentType: AgentType): AgentPermissionSet | undefined {
    return this.resolver.getGlobalPermissions(agentType);
  }

  /**
   * Get project-level permission overrides
   */
  getProjectPermissions(
    projectId: string,
    agentType: AgentType,
  ): Partial<AgentPermissionSet> | undefined {
    return this.resolver.getProjectPermissions(projectId, agentType);
  }

  // ============================================
  // Permission Request Methods
  // ============================================

  /**
   * Request elevated permissions
   */
  async requestPermission(
    request: Omit<PermissionRequest, 'id' | 'status' | 'requestedAt'>,
  ): Promise<PermissionRequestResult> {
    const id = nanoid();
    const fullRequest: PermissionRequest = {
      ...request,
      id,
      status: 'pending',
      requestedAt: new Date(),
    };

    this.permissionRequests.set(id, fullRequest);

    this.logger?.info(`Permission request created: ${id}`, {
      agentType: request.agentType,
      projectId: request.projectId,
      permission: request.requestedPermission,
    });

    return {
      approved: false,
      requestId: id,
      reason: 'Permission request pending approval',
    };
  }

  /**
   * Approve a permission request
   */
  async approvePermissionRequest(requestId: string, approver: string): Promise<void> {
    const request = this.permissionRequests.get(requestId);
    if (!request) {
      throw new Error(`Permission request not found: ${requestId}`);
    }

    request.status = 'approved';
    request.resolvedAt = new Date();
    request.resolvedBy = approver;

    this.logger?.info(`Permission request approved: ${requestId} by ${approver}`);
  }

  /**
   * Deny a permission request
   */
  async denyPermissionRequest(
    requestId: string,
    approver: string,
    reason: string,
  ): Promise<void> {
    const request = this.permissionRequests.get(requestId);
    if (!request) {
      throw new Error(`Permission request not found: ${requestId}`);
    }

    request.status = 'denied';
    request.resolvedAt = new Date();
    request.resolvedBy = approver;
    request.resolutionReason = reason;

    this.logger?.info(`Permission request denied: ${requestId} by ${approver} - ${reason}`);
  }

  /**
   * Get pending permission requests
   */
  async getPendingRequests(projectId?: string): Promise<PermissionRequest[]> {
    const requests = [...this.permissionRequests.values()].filter(
      (r) => r.status === 'pending',
    );

    if (projectId) {
      return requests.filter((r) => r.projectId === projectId);
    }

    return requests;
  }

  /**
   * Get a permission request by ID
   */
  getPermissionRequest(requestId: string): PermissionRequest | undefined {
    return this.permissionRequests.get(requestId);
  }

  // ============================================
  // Violation Tracking Methods
  // ============================================

  /**
   * Record a permission violation
   */
  async recordViolation(
    violation: Omit<PermissionViolation, 'id' | 'timestamp'>,
  ): Promise<void> {
    const id = nanoid();
    const fullViolation: PermissionViolation = {
      ...violation,
      id,
      timestamp: new Date(),
    };

    this.violations.push(fullViolation);

    // Trim the violations array when it exceeds the maximum size to prevent unbounded growth.
    const MAX_VIOLATIONS = 10000;
    if (this.violations.length > MAX_VIOLATIONS) {
      this.violations.splice(0, this.violations.length - MAX_VIOLATIONS);
    }

    this.logger?.warn(`Permission violation recorded: ${id}`, {
      agentType: violation.agentType,
      projectId: violation.projectId,
      action: violation.action,
      severity: violation.severity,
    });
  }

  /**
   * Get permission violations with optional filtering
   */
  async getPermissionViolations(filter?: ViolationFilter): Promise<PermissionViolation[]> {
    let results = [...this.violations];

    if (filter?.agentType) {
      results = results.filter((v) => v.agentType === filter.agentType);
    }

    if (filter?.projectId) {
      results = results.filter((v) => v.projectId === filter.projectId);
    }

    if (filter?.fromDate) {
      results = results.filter((v) => v.timestamp >= filter.fromDate!);
    }

    if (filter?.toDate) {
      results = results.filter((v) => v.timestamp <= filter.toDate!);
    }

    if (filter?.severity && filter.severity.length > 0) {
      results = results.filter((v) => filter.severity?.includes(v.severity));
    }

    if (filter?.limit) {
      results = results.slice(0, filter.limit);
    }

    return results;
  }

  // ============================================
  // Helper Methods
  // ============================================

  /**
   * Create a Permission object for a matched rule
   */
  private createMatchedRule(
    category: Permission['category'],
    resource: string,
    action: Permission['action'],
  ): Permission {
    return {
      id: nanoid(),
      category,
      resource,
      action,
    };
  }

  /**
   * Clear the resolver cache
   */
  clearCache(): void {
    this.resolver.clearCache();
  }

  /**
   * Get cache statistics
   */
  getCacheStats(): { size: number; entries: string[] } {
    return this.resolver.getCacheStats();
  }
}

/**
 * Create a permission service with default configuration
 */
export function createPermissionService(options?: {
  cacheTtlMs?: number;
  logger?: ILogger;
}): PermissionService {
  return new PermissionService(options);
}
