/**
 * Agent Resolver Service
 *
 * Story 9-8: Unified Agent Resolver API
 *
 * Composes AgentConfigStore + HealthStore + PromptStore + SanitizationStore
 * to provide a single resolution path for both the TS engine (in-process)
 * and Elsa workflows (via REST API).
 *
 * Resolution flow:
 *   1. Phase -> role mapping (phaseRoleMap or DEFAULT_PHASE_ROLE_MAP)
 *   2. Role -> provider chain (account config or default)
 *   3. Provider chain -> first healthy, within-budget provider
 *   4. Role -> task config merge (defaults < role < task overrides with clamping)
 *   5. Role + provider -> prompt resolution (via PromptStore)
 *   6. Provider -> sanitization rules
 */

import type {
  AgentType,
  WorkflowPhase,
  IAgentsConfig,
  ProviderChainEntry,
} from '@tamma/shared';
import { DEFAULT_PHASE_ROLE_MAP } from '@tamma/shared';

import type { IAgentConfigStore } from '../persistence/agent-config-store.js';
import type { IHealthStore } from './health-store.js';
import type { IPromptStore } from './prompt-store.js';
import type { ISanitizationStore } from './sanitization-store.js';

/** Subset of AgentTaskConfig fields relevant to the resolver. */
interface TaskConfigOverrides {
  maxBudgetUsd?: number;
  allowedTools?: string[];
  permissionMode?: 'default' | 'bypassPermissions';
  prompt?: string;
  cwd?: string;
  model?: string;
  sessionId?: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Keys that must be rejected to prevent prototype pollution attacks. */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

// ---------------------------------------------------------------------------
// Response types
// ---------------------------------------------------------------------------

/** Provider info in the resolved response. */
export interface ResolvedProviderInfo {
  name: string;
  model: string;
}

/** Resolved task configuration (clamped). */
export interface ResolvedTaskConfig {
  allowedTools: string[];
  maxBudgetUsd: number | null;
  permissionMode: 'default' | 'bypassPermissions';
}

/** A chain entry with its health status. */
export interface ResolvedChainEntry {
  provider: string;
  model: string;
  healthy: boolean;
  circuitOpen: boolean;
}

/** Full resolved agent configuration returned by the API. */
export interface ResolvedAgentResult {
  role: AgentType;
  provider: ResolvedProviderInfo;
  taskConfig: ResolvedTaskConfig;
  systemPrompt: string;
  sanitizationEnabled: boolean;
  chainEntries: ResolvedChainEntry[];
}

/** Extended result that includes the phase for resolve-for-phase. */
export interface ResolvedAgentForPhaseResult extends ResolvedAgentResult {
  phase: WorkflowPhase;
}

// ---------------------------------------------------------------------------
// IAgentResolverService Interface
// ---------------------------------------------------------------------------

export interface IAgentResolverService {
  /**
   * Resolve full agent configuration for a given role.
   */
  resolveForRole(
    accountId: string,
    role: AgentType,
    options?: { projectId?: string; engineId?: string },
  ): Promise<ResolvedAgentResult>;

  /**
   * Resolve agent configuration for a workflow phase.
   * Maps phase to role first, then resolves.
   */
  resolveForPhase(
    accountId: string,
    phase: WorkflowPhase,
    options?: ResolveForPhaseOptions,
  ): Promise<ResolvedAgentForPhaseResult>;
}

/** Options for resolveForPhase — allows undefined on all props for exactOptionalPropertyTypes. */
export interface ResolveForPhaseOptions {
  projectId?: string | undefined;
  engineId?: string | undefined;
  taskOverrides?: Partial<TaskConfigOverrides> | undefined;
}

// ---------------------------------------------------------------------------
// AgentResolverService
// ---------------------------------------------------------------------------

export interface AgentResolverServiceDeps {
  configStore: IAgentConfigStore;
  healthStore: IHealthStore;
  promptStore: IPromptStore;
  sanitizationStore: ISanitizationStore;
}

export class AgentResolverService implements IAgentResolverService {
  private readonly configStore: IAgentConfigStore;
  private readonly healthStore: IHealthStore;
  private readonly promptStore: IPromptStore;
  private readonly sanitizationStore: ISanitizationStore;

  constructor(deps: AgentResolverServiceDeps) {
    this.configStore = deps.configStore;
    this.healthStore = deps.healthStore;
    this.promptStore = deps.promptStore;
    this.sanitizationStore = deps.sanitizationStore;
  }

  async resolveForRole(
    accountId: string,
    role: AgentType,
    _options?: { projectId?: string; engineId?: string },
  ): Promise<ResolvedAgentResult> {
    _validateRole(role);

    // 1. Load agent config for account
    const resolved = await this.configStore.resolve(accountId);
    const agentsConfig = resolved.config.agents;

    // 2. Get provider chain for role
    const chainEntries = _getProviderChain(agentsConfig, role);

    // 3. Get health status for each chain entry and find first healthy provider
    const resolvedChain = await this._resolveChainHealth(chainEntries);
    const firstHealthy = resolvedChain.find((e) => e.healthy);

    const provider: ResolvedProviderInfo = firstHealthy
      ? { name: firstHealthy.provider, model: firstHealthy.model }
      : { name: chainEntries[0]?.provider ?? 'none', model: chainEntries[0]?.model ?? 'default' };

    // 4. Get task config with clamping
    const taskConfig = _mergeTaskConfig(agentsConfig, role);

    // 5. Get prompt from PromptStore
    const systemPrompt = await this._resolvePrompt(accountId, role, provider.name);

    // 6. Get sanitization rules
    const sanitizationRules = await this.sanitizationStore.getRules(accountId);

    return {
      role,
      provider,
      taskConfig,
      systemPrompt,
      sanitizationEnabled: sanitizationRules.enabled,
      chainEntries: resolvedChain,
    };
  }

  async resolveForPhase(
    accountId: string,
    phase: WorkflowPhase,
    options?: ResolveForPhaseOptions,
  ): Promise<ResolvedAgentForPhaseResult> {
    _validatePhase(phase);

    // 1. Load agent config for account
    const resolved = await this.configStore.resolve(accountId);
    const agentsConfig = resolved.config.agents;

    // 2. Phase -> role mapping
    const role = _getRoleForPhase(agentsConfig, phase);

    // 3. Get provider chain for role
    const chainEntries = _getProviderChain(agentsConfig, role);

    // 4. Get health status for each chain entry
    const resolvedChain = await this._resolveChainHealth(chainEntries);
    const firstHealthy = resolvedChain.find((e) => e.healthy);

    const provider: ResolvedProviderInfo = firstHealthy
      ? { name: firstHealthy.provider, model: firstHealthy.model }
      : { name: chainEntries[0]?.provider ?? 'none', model: chainEntries[0]?.model ?? 'default' };

    // 5. Get task config with clamping (including task overrides)
    const taskConfig = _mergeTaskConfig(agentsConfig, role, options?.taskOverrides);

    // 6. Get prompt from PromptStore
    const systemPrompt = await this._resolvePrompt(accountId, role, provider.name);

    // 7. Get sanitization rules
    const sanitizationRules = await this.sanitizationStore.getRules(accountId);

    return {
      phase,
      role,
      provider,
      taskConfig,
      systemPrompt,
      sanitizationEnabled: sanitizationRules.enabled,
      chainEntries: resolvedChain,
    };
  }

  // -----------------------------------------------------------------------
  // Private helpers
  // -----------------------------------------------------------------------

  /**
   * Resolve health status for each chain entry.
   * Uses ProviderHealthTracker key format: "provider:model".
   */
  private async _resolveChainHealth(
    entries: readonly ProviderChainEntry[],
  ): Promise<ResolvedChainEntry[]> {
    const result: ResolvedChainEntry[] = [];

    for (const entry of entries) {
      const model = entry.model ?? 'default';
      const key = `${entry.provider}:${model}`;

      const healthStatus = await this.healthStore.get(key);

      result.push({
        provider: entry.provider,
        model,
        healthy: healthStatus === null || healthStatus.healthy,
        circuitOpen: healthStatus?.circuitOpen ?? false,
      });
    }

    return result;
  }

  /**
   * Resolve system prompt. Tries PromptStore first, falls back to
   * AgentsConfig systemPrompt, then to a generic default.
   */
  private async _resolvePrompt(
    accountId: string,
    role: AgentType,
    providerName: string,
  ): Promise<string> {
    // Try PromptStore system prompt first
    const storePrompt = await this.promptStore.getSystemPrompt(accountId, role);
    if (storePrompt !== undefined) {
      return storePrompt;
    }

    // Try to render a role-specific prompt from the template store
    const rendered = await this.promptStore.render(accountId, role, 'default', {
      variables: { provider: providerName },
    });
    if (rendered !== undefined) {
      return rendered.renderedSystemPrompt || rendered.renderedTemplate;
    }

    return `You are an AI assistant working as a ${role} on a software development task.`;
  }
}

// ---------------------------------------------------------------------------
// Pure utility functions (module-private)
// ---------------------------------------------------------------------------

/**
 * Validate a role name against prototype pollution.
 */
function _validateRole(role: string): void {
  if (FORBIDDEN_KEYS.has(role)) {
    throw new Error(`Forbidden role name: "${role}"`);
  }
  if (role.length === 0 || role.length > 64) {
    throw new Error(`Role name must be 1-64 characters (got ${role.length})`);
  }
}

/**
 * Validate a workflow phase value.
 */
function _validatePhase(phase: string): void {
  const validPhases: readonly string[] = [
    'ISSUE_SELECTION',
    'CONTEXT_ANALYSIS',
    'PLAN_GENERATION',
    'CODE_GENERATION',
    'PR_CREATION',
    'CODE_REVIEW',
    'TEST_EXECUTION',
    'STATUS_MONITORING',
  ];
  if (!validPhases.includes(phase)) {
    throw new Error(`Invalid workflow phase: "${phase}"`);
  }
}

/**
 * Get the agent role for a workflow phase.
 * Resolution: config.phaseRoleMap (account override) -> DEFAULT_PHASE_ROLE_MAP.
 */
function _getRoleForPhase(config: IAgentsConfig, phase: WorkflowPhase): AgentType {
  const customRole = config.phaseRoleMap?.[phase];
  const role = customRole ?? DEFAULT_PHASE_ROLE_MAP[phase];

  if (FORBIDDEN_KEYS.has(role)) {
    throw new Error(`Forbidden role name resolved for phase "${phase}": "${role}"`);
  }

  return role;
}

/**
 * Get the provider chain entries for a role.
 * Uses role-specific chain if available and non-empty, else defaults.
 */
function _getProviderChain(config: IAgentsConfig, role: AgentType): readonly ProviderChainEntry[] {
  const roleConfig = config.roles?.[role];
  const roleChain = roleConfig?.providerChain;
  if (roleChain !== undefined && roleChain.length > 0) {
    return roleChain;
  }
  return config.defaults.providerChain;
}

/**
 * 3-level task config merge with clamping.
 *
 * Merge order: defaults < role < taskOverrides
 * Clamping rules:
 *   - maxBudgetUsd: cannot exceed ceiling from defaults/role
 *   - bypassPermissions: requires TAMMA_ALLOW_BYPASS_PERMISSIONS=true
 *   - allowedTools: intersection only (restrict, never expand)
 */
function _mergeTaskConfig(
  config: IAgentsConfig,
  role: AgentType,
  taskOverrides?: Partial<TaskConfigOverrides>,
): ResolvedTaskConfig {
  // Level 1: Defaults
  let allowedTools: string[] = [];
  let maxBudgetUsd: number | null = null;
  let permissionMode: 'default' | 'bypassPermissions' = 'default';

  if (config.defaults.allowedTools !== undefined) {
    allowedTools = [...config.defaults.allowedTools];
  }
  if (config.defaults.maxBudgetUsd !== undefined) {
    maxBudgetUsd = config.defaults.maxBudgetUsd;
  }
  if (config.defaults.permissionMode !== undefined) {
    permissionMode = config.defaults.permissionMode;
  }

  // Level 2: Role-specific overrides
  const roleConfig = config.roles?.[role];
  if (roleConfig !== undefined) {
    if (roleConfig.allowedTools !== undefined) {
      allowedTools = [...roleConfig.allowedTools];
    }
    if (roleConfig.maxBudgetUsd !== undefined) {
      maxBudgetUsd = roleConfig.maxBudgetUsd;
    }
    if (roleConfig.permissionMode !== undefined) {
      permissionMode = roleConfig.permissionMode;
    }
  }

  // Level 3: Task overrides with clamping
  if (taskOverrides !== undefined) {
    // Budget clamping
    if (taskOverrides.maxBudgetUsd !== undefined) {
      if (maxBudgetUsd !== null) {
        maxBudgetUsd = Math.min(taskOverrides.maxBudgetUsd, maxBudgetUsd);
      } else {
        maxBudgetUsd = taskOverrides.maxBudgetUsd;
      }
    }

    // Permission clamping
    if (taskOverrides.permissionMode !== undefined) {
      if (taskOverrides.permissionMode === 'bypassPermissions') {
        const envAllow = process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'];
        if (envAllow === 'true') {
          permissionMode = 'bypassPermissions';
        }
        // Otherwise keep current permissionMode
      } else {
        permissionMode = taskOverrides.permissionMode;
      }
    }

    // Tool clamping: intersection only
    if (taskOverrides.allowedTools !== undefined) {
      if (allowedTools.length > 0) {
        const currentSet = new Set(allowedTools);
        allowedTools = taskOverrides.allowedTools.filter((t: string) => currentSet.has(t));
      } else {
        allowedTools = [...taskOverrides.allowedTools];
      }
    }
  }

  return {
    allowedTools,
    maxBudgetUsd,
    permissionMode,
  };
}
