/**
 * Resolves a TammaConfig from separate provider and repo configs.
 *
 * This is the core merge function for the layered configuration system.
 * Repo config references providers by name; this function wires them together.
 */

import type { TammaConfig, EngineConfig, AgentConfig } from '../types/index.js';
import type { IAgentsConfig, IProviderChainEntry, IAgentRoleConfig, AgentType } from '../types/agent-config.js';
import type { SecurityConfig } from '../types/security-config.js';
import type { IProvidersConfig } from '../types/providers-config.js';
import type { IRepoConfig, IRepoRoleConfig } from '../types/repo-config.js';

/** Default engine configuration. */
const DEFAULT_ENGINE: EngineConfig = {
  pollIntervalMs: 300_000,
  workingDirectory: '.',
  approvalMode: 'cli',
  ciPollIntervalMs: 30_000,
  ciMonitorTimeoutMs: 3_600_000,
};

/** Default legacy agent configuration. */
const DEFAULT_AGENT: AgentConfig = {
  model: 'claude-sonnet-4-5',
  maxBudgetUsd: 1.0,
  allowedTools: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
  permissionMode: 'default',
};

/**
 * Build an IProviderChainEntry from a provider name and its definition.
 */
function buildChainEntry(
  providerName: string,
  providers: IProvidersConfig,
  modelOverride?: string,
): IProviderChainEntry {
  const def = providers.providers[providerName];
  const entry: IProviderChainEntry = { provider: providerName };

  if (modelOverride !== undefined) {
    entry.model = modelOverride;
  } else if (def?.defaultModel !== undefined) {
    entry.model = def.defaultModel;
  }

  // Wire up apiKey as env ref pattern (the chain entry uses apiKeyRef,
  // but for directly-resolved configs we store config overrides)
  if (def) {
    const config: Record<string, unknown> = {};
    if (def.baseUrl !== undefined) {
      config['baseUrl'] = def.baseUrl;
    }
    if (def.timeoutSeconds !== undefined) {
      config['timeoutSeconds'] = def.timeoutSeconds;
    }
    if (def.apiKey !== undefined) {
      config['apiKey'] = def.apiKey;
    }
    if (Object.keys(config).length > 0) {
      entry.config = config;
    }
  }

  return entry;
}

/**
 * Convert a repo role config into an IAgentRoleConfig by resolving the
 * provider reference against the providers config.
 */
function resolveRoleConfig(
  roleConfig: IRepoRoleConfig,
  providers: IProvidersConfig,
  warnings: string[],
): Partial<IAgentRoleConfig> {
  const providerName = roleConfig.provider;
  const providerNames = Object.keys(providers.providers);

  // Check if the referenced provider exists
  if (!providers.providers[providerName]) {
    const fallback = providerNames[0];
    if (fallback !== undefined) {
      warnings.push(
        `Role references provider "${providerName}" which is not defined in providers config; falling back to "${fallback}"`,
      );
      const result: Partial<IAgentRoleConfig> = {
        providerChain: [buildChainEntry(fallback, providers, roleConfig.model)],
      };
      if (roleConfig.allowedTools !== undefined) result.allowedTools = roleConfig.allowedTools;
      if (roleConfig.maxBudgetUsd !== undefined) result.maxBudgetUsd = roleConfig.maxBudgetUsd;
      if (roleConfig.systemPrompt !== undefined) result.systemPrompt = roleConfig.systemPrompt;
      if (roleConfig.providerPrompts !== undefined) result.providerPrompts = roleConfig.providerPrompts;
      return result;
    }
  }

  const result: Partial<IAgentRoleConfig> = {
    providerChain: [buildChainEntry(providerName, providers, roleConfig.model)],
  };
  if (roleConfig.allowedTools !== undefined) result.allowedTools = roleConfig.allowedTools;
  if (roleConfig.maxBudgetUsd !== undefined) result.maxBudgetUsd = roleConfig.maxBudgetUsd;
  if (roleConfig.systemPrompt !== undefined) result.systemPrompt = roleConfig.systemPrompt;
  if (roleConfig.providerPrompts !== undefined) result.providerPrompts = roleConfig.providerPrompts;
  return result;
}

/**
 * Resolve a TammaConfig from separate provider and repo configs.
 *
 * Resolution order:
 * 1. Start with defaults
 * 2. Build agents config from repo roles + provider credentials
 * 3. Apply repo engine, security, github settings
 * 4. Apply env overrides last
 *
 * Returns the resolved config and any warning messages.
 */
export function resolveConfig(
  providers: IProvidersConfig,
  repoConfig: IRepoConfig,
  envOverrides?: Partial<TammaConfig>,
): { config: TammaConfig; warnings: string[] } {
  const warnings: string[] = [];

  // Build the default provider chain from the first provider in providers.json
  const providerNames = Object.keys(providers.providers);
  const primaryProvider = providerNames[0] ?? 'claude-code';

  const defaultChainEntry = providerNames.length > 0
    ? buildChainEntry(primaryProvider, providers)
    : { provider: 'claude-code' } satisfies IProviderChainEntry;

  // Build agents config
  const agentsConfig: IAgentsConfig = {
    defaults: {
      providerChain: [defaultChainEntry],
    },
  };

  // Apply global budget/permission from providers config
  if (providers.maxBudgetUsd !== undefined) {
    agentsConfig.defaults.maxBudgetUsd = providers.maxBudgetUsd;
  }
  if (providers.permissionMode !== undefined) {
    agentsConfig.defaults.permissionMode = providers.permissionMode;
  }

  // Resolve per-role configs from repo config
  if (repoConfig.roles) {
    const roles: Partial<Record<AgentType, Partial<IAgentRoleConfig>>> = {};
    for (const [roleName, roleConfig] of Object.entries(repoConfig.roles)) {
      if (!roleConfig) continue;
      roles[roleName as AgentType] = resolveRoleConfig(roleConfig, providers, warnings);
    }
    if (Object.keys(roles).length > 0) {
      agentsConfig.roles = roles;
    }
  }

  // Resolve phase role map from repo config
  if (repoConfig.phaseRoleMap !== undefined) {
    const phaseMap = repoConfig.phaseRoleMap as NonNullable<IAgentsConfig['phaseRoleMap']>;
    agentsConfig.phaseRoleMap = phaseMap;
  }

  // Build engine config
  const engine: EngineConfig = { ...DEFAULT_ENGINE };
  if (repoConfig.engine) {
    if (repoConfig.engine.approvalMode !== undefined) engine.approvalMode = repoConfig.engine.approvalMode;
    if (repoConfig.engine.pollIntervalMs !== undefined) engine.pollIntervalMs = repoConfig.engine.pollIntervalMs;
    if (repoConfig.engine.ciPollIntervalMs !== undefined) engine.ciPollIntervalMs = repoConfig.engine.ciPollIntervalMs;
    if (repoConfig.engine.ciMonitorTimeoutMs !== undefined) engine.ciMonitorTimeoutMs = repoConfig.engine.ciMonitorTimeoutMs;
  }

  // Build security config
  let security: SecurityConfig | undefined;
  if (repoConfig.security) {
    security = {};
    if (repoConfig.security.sanitizeContent !== undefined) security.sanitizeContent = repoConfig.security.sanitizeContent;
    if (repoConfig.security.validateUrls !== undefined) security.validateUrls = repoConfig.security.validateUrls;
    if (repoConfig.security.gateActions !== undefined) security.gateActions = repoConfig.security.gateActions;
    if (repoConfig.security.maxFetchSizeBytes !== undefined) security.maxFetchSizeBytes = repoConfig.security.maxFetchSizeBytes;
    if (repoConfig.security.blockedCommandPatterns !== undefined) security.blockedCommandPatterns = repoConfig.security.blockedCommandPatterns;
  }

  // Build GitHub config overrides from repo config
  const githubBase = {
    authMode: 'pat' as const,
    token: '',
    owner: '',
    repo: '',
    issueLabels: ['tamma'] as string[],
    excludeLabels: ['wontfix'] as string[],
    botUsername: 'tamma-bot',
  };
  if (repoConfig.github) {
    if (repoConfig.github.issueLabels !== undefined) githubBase.issueLabels = repoConfig.github.issueLabels;
    if (repoConfig.github.excludeLabels !== undefined) githubBase.excludeLabels = repoConfig.github.excludeLabels;
    if (repoConfig.github.botUsername !== undefined) githubBase.botUsername = repoConfig.github.botUsername;
  }

  // Assemble the base config
  const config: TammaConfig = {
    mode: 'standalone',
    logLevel: 'info',
    github: githubBase,
    agent: { ...DEFAULT_AGENT },
    engine,
    agents: agentsConfig,
  };

  if (security !== undefined) {
    config.security = security;
  }

  // Apply env overrides last
  if (envOverrides) {
    if (envOverrides.mode !== undefined) config.mode = envOverrides.mode;
    if (envOverrides.logLevel !== undefined) config.logLevel = envOverrides.logLevel;
    if (envOverrides.github !== undefined) {
      config.github = { ...config.github, ...envOverrides.github };
    }
    if (envOverrides.agent !== undefined) {
      config.agent = { ...config.agent, ...envOverrides.agent };
    }
    if (envOverrides.engine !== undefined) {
      config.engine = { ...config.engine, ...envOverrides.engine };
    }
    if (envOverrides.agents !== undefined) {
      config.agents = { ...config.agents, ...envOverrides.agents };
    }
    if (envOverrides.security !== undefined) {
      config.security = { ...config.security, ...envOverrides.security };
    }
    if (envOverrides.elsa !== undefined) {
      config.elsa = envOverrides.elsa;
    }
    if (envOverrides.server !== undefined) {
      config.server = envOverrides.server;
    }
    if (envOverrides.aiProviders !== undefined) {
      config.aiProviders = envOverrides.aiProviders;
    }
    if (envOverrides.defaultProvider !== undefined) {
      config.defaultProvider = envOverrides.defaultProvider;
    }
  }

  return { config, warnings };
}
