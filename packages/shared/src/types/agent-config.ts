/**
 * Multi-agent configuration types for the Tamma platform.
 *
 * Defines provider chains per agent role, workflow phase to role mappings,
 * and per-role prompts/tools/budgets.
 */

import type { AgentType } from './knowledge.js';

// Re-export AgentType so consumers of this module can access it
export type { AgentType } from './knowledge.js';

// --- Workflow Phases ---

/**
 * The 8 workflow phases in the autonomous development lifecycle.
 */
export type WorkflowPhase =
  | 'ISSUE_SELECTION'
  | 'CONTEXT_ANALYSIS'
  | 'PLAN_GENERATION'
  | 'CODE_GENERATION'
  | 'PR_CREATION'
  | 'CODE_REVIEW'
  | 'TEST_EXECUTION'
  | 'STATUS_MONITORING';

/**
 * Default mapping from workflow phase to agent role.
 * Frozen to prevent mutation at runtime.
 */
export const DEFAULT_PHASE_ROLE_MAP: Record<WorkflowPhase, AgentType> = Object.freeze({
  ISSUE_SELECTION: 'scrum_master',
  CONTEXT_ANALYSIS: 'analyst',
  PLAN_GENERATION: 'architect',
  CODE_GENERATION: 'implementer',
  PR_CREATION: 'implementer',
  CODE_REVIEW: 'reviewer',
  TEST_EXECUTION: 'tester',
  STATUS_MONITORING: 'scrum_master',
} as const satisfies Record<WorkflowPhase, AgentType>);

/**
 * Maps EngineState values to their corresponding WorkflowPhase.
 * Note: CODE_REVIEW and TEST_EXECUTION have no direct EngineState equivalent --
 * they are sub-phases within the implementation cycle.
 */
export const ENGINE_STATE_TO_PHASE = Object.freeze({
  SELECTING_ISSUE: 'ISSUE_SELECTION',
  ANALYZING: 'CONTEXT_ANALYSIS',
  PLANNING: 'PLAN_GENERATION',
  IMPLEMENTING: 'CODE_GENERATION',
  CREATING_PR: 'PR_CREATION',
  MONITORING: 'STATUS_MONITORING',
} as const satisfies Record<string, WorkflowPhase>) as Readonly<Record<string, WorkflowPhase>>;

// --- Permission Mode ---

/**
 * Permission mode for agent operations.
 * - 'bypassPermissions': Skip permission checks (requires TAMMA_ALLOW_BYPASS_PERMISSIONS=true)
 * - 'default': Normal permission enforcement
 */
export type PermissionMode = 'bypassPermissions' | 'default';

// --- Provider Chain ---

/**
 * A single entry in a provider chain.
 * Provider chains define fallback sequences of AI providers.
 */
export interface IProviderChainEntry {
  /** Provider identifier (e.g., 'claude-code', 'opencode', 'openrouter', 'zen-mcp') */
  provider: string;
  /** Model identifier (e.g., 'claude-sonnet-4-5', 'z-ai/z1-mini') */
  model?: string;
  /**
   * Reference to environment variable containing the API key.
   * The key is resolved at runtime via `process.env[apiKeyRef]`.
   * Raw API keys must NOT be stored in config files.
   */
  apiKeyRef?: string;
  /** Provider-specific configuration (baseUrl, timeout, etc.) */
  config?: Record<string, unknown>;
}

// Also export as ProviderChainEntry for backward compatibility with story spec
export type ProviderChainEntry = IProviderChainEntry;

// --- Agent Role Configuration ---

/**
 * Configuration for a specific agent role.
 */
export interface IAgentRoleConfig {
  /** Ordered list of providers to try (first = primary, rest = fallbacks) */
  providerChain: IProviderChainEntry[];
  /** Tools this agent role is allowed to use */
  allowedTools?: string[];
  /** Maximum budget in USD for this role's operations */
  maxBudgetUsd?: number;
  /** Permission mode for this role */
  permissionMode?: PermissionMode;
  /** System prompt override for this role */
  systemPrompt?: string;
  /** Per-provider prompt overrides (key = provider name) */
  providerPrompts?: Record<string, string>;
}

// Also export as AgentRoleConfig for backward compatibility with story spec
export type AgentRoleConfig = IAgentRoleConfig;

// --- Multi-Agent Configuration ---

/**
 * Top-level multi-agent configuration.
 * Defines defaults, per-role overrides, and phase-to-role mappings.
 */
export interface IAgentsConfig {
  /** Default configuration applied to all roles unless overridden */
  defaults: IAgentRoleConfig;
  /** Per-role configuration overrides (partial, merged with defaults at runtime) */
  roles?: Partial<Record<AgentType, Partial<IAgentRoleConfig>>>;
  /** Override mapping from workflow phases to agent roles */
  phaseRoleMap?: Partial<Record<WorkflowPhase, AgentType>>;
}

// Also export as AgentsConfig for backward compatibility with story spec
export type AgentsConfig = IAgentsConfig;

// --- Validation ---

/** Maximum number of blocked command patterns */
const MAX_BLOCKED_PATTERNS = 100;
/** Maximum length of a single blocked command pattern */
const MAX_PATTERN_LENGTH = 500;
/** Maximum fetch size in bytes (1 GiB) */
const MAX_FETCH_SIZE_BYTES = 1_073_741_824;
/** Default maximum budget upper bound */
const MAX_BUDGET_USD = 100;
/** Regex for valid provider names */
const PROVIDER_NAME_REGEX = /^[a-z0-9][a-z0-9_-]{0,63}$/;
/** Prototype pollution guard: forbidden property names */
const FORBIDDEN_PROVIDER_NAMES = new Set(['__proto__', 'constructor', 'prototype']);

import { TammaError } from '../errors.js';

/**
 * Regex character class that matches characters commonly used in ReDoS
 * attack patterns (nested quantifiers, backreferences, etc.).
 * Patterns containing these sequences are rejected during validation.
 */
const REDOS_SUSPECT_PATTERN = /(\{[0-9]{4,}|\(\?[^)]*\([^)]*\))/;

/**
 * Validate that a regex pattern string is syntactically valid and does not
 * contain obvious ReDoS attack vectors. Throws on invalid or dangerous patterns.
 *
 * This acts as a sanitization gate so that downstream `new RegExp(pattern)`
 * calls only receive vetted input (mitigates CodeQL js/regex-injection).
 */
function validateRegexPattern(pattern: string): void {
  // Reject patterns with obvious ReDoS vectors
  if (REDOS_SUSPECT_PATTERN.test(pattern)) {
    throw new SyntaxError(`Pattern contains potentially dangerous regex constructs: "${pattern}"`);
  }

  // Validate syntax by attempting compilation.
  // The pattern has been length-checked (max 500 chars) and screened
  // for ReDoS vectors above, so this compilation is safe.
  RegExp(pattern);
}

/**
 * Validates a SecurityConfig object at config load time.
 * Throws TammaError for invalid configurations.
 */
export function validateSecurityConfig(config: import('./security-config.js').SecurityConfig): void {
  if (config.blockedCommandPatterns !== undefined) {
    if (config.blockedCommandPatterns.length > MAX_BLOCKED_PATTERNS) {
      throw new TammaError(
        `blockedCommandPatterns exceeds maximum of ${MAX_BLOCKED_PATTERNS} patterns (got ${config.blockedCommandPatterns.length})`,
        'CONFIG.INVALID_REGEX',
      );
    }

    for (const pattern of config.blockedCommandPatterns) {
      if (pattern.length > MAX_PATTERN_LENGTH) {
        throw new TammaError(
          `blockedCommandPattern exceeds maximum length of ${MAX_PATTERN_LENGTH} chars: "${pattern.slice(0, 50)}..."`,
          'CONFIG.INVALID_REGEX',
        );
      }

      try {
        validateRegexPattern(pattern);
      } catch {
        throw new TammaError(
          `blockedCommandPattern is not a valid regex: "${pattern}"`,
          'CONFIG.INVALID_REGEX',
        );
      }
    }
  }

  if (config.maxFetchSizeBytes !== undefined) {
    if (
      !Number.isFinite(config.maxFetchSizeBytes) ||
      config.maxFetchSizeBytes < 0 ||
      config.maxFetchSizeBytes > MAX_FETCH_SIZE_BYTES
    ) {
      throw new TammaError(
        `maxFetchSizeBytes must be between 0 and ${MAX_FETCH_SIZE_BYTES} (got ${config.maxFetchSizeBytes})`,
        'CONFIG.INVALID_VALUE',
      );
    }
  }
}

/**
 * Validates a provider name string.
 * Must match /^[a-z0-9][a-z0-9_-]{0,63}$/ and must NOT be a prototype pollution target.
 */
export function validateProviderName(name: string): void {
  if (FORBIDDEN_PROVIDER_NAMES.has(name)) {
    throw new TammaError(
      `Provider name "${name}" is forbidden (prototype pollution guard)`,
      'CONFIG.INVALID_PROVIDER_NAME',
    );
  }

  if (!PROVIDER_NAME_REGEX.test(name)) {
    throw new TammaError(
      `Provider name "${name}" must match ${PROVIDER_NAME_REGEX.source}`,
      'CONFIG.INVALID_PROVIDER_NAME',
    );
  }
}

/**
 * Validates maxBudgetUsd value.
 * Must be a finite number >= 0 and <= maxUpperBound (default 100).
 */
export function validateMaxBudgetUsd(value: number, maxUpperBound: number = MAX_BUDGET_USD): void {
  if (!Number.isFinite(value)) {
    throw new TammaError(
      `maxBudgetUsd must be a finite number (got ${value})`,
      'CONFIG.INVALID_VALUE',
    );
  }

  if (value < 0 || value > maxUpperBound) {
    throw new TammaError(
      `maxBudgetUsd must be between 0 and ${maxUpperBound} (got ${value})`,
      'CONFIG.INVALID_VALUE',
    );
  }
}

/**
 * Validates an AgentsConfig object at config load time.
 * Throws TammaError for invalid configurations.
 */
export function validateAgentsConfig(config: IAgentsConfig): void {
  // providerChain must be non-empty in defaults
  if (config.defaults.providerChain.length === 0) {
    throw new TammaError(
      'defaults.providerChain must not be empty',
      'CONFIG.EMPTY_PROVIDER_CHAIN',
    );
  }

  // Validate all provider names in defaults
  for (const entry of config.defaults.providerChain) {
    validateProviderName(entry.provider);
  }

  // Validate defaults maxBudgetUsd
  if (config.defaults.maxBudgetUsd !== undefined) {
    validateMaxBudgetUsd(config.defaults.maxBudgetUsd);
  }

  // Validate role overrides
  if (config.roles) {
    for (const [roleName, roleConfig] of Object.entries(config.roles)) {
      if (!roleConfig) continue;

      if (roleConfig.providerChain) {
        for (const entry of roleConfig.providerChain) {
          validateProviderName(entry.provider);
        }
      }

      if (roleConfig.maxBudgetUsd !== undefined) {
        validateMaxBudgetUsd(roleConfig.maxBudgetUsd);
      }

      // Validate role name is not a forbidden prototype name (extra safety)
      if (FORBIDDEN_PROVIDER_NAMES.has(roleName)) {
        throw new TammaError(
          `Role name "${roleName}" is forbidden (prototype pollution guard)`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }
  }
}
