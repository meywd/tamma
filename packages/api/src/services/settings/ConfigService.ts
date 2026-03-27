/**
 * Config Service
 *
 * Reads and writes TammaConfig for agent and security settings.
 * In-memory store with validation via shared validateAgentsConfig/validateSecurityConfig.
 *
 * On prompt template updates, syncs to the ELSA Agents store if an ElsaAgentsClient
 * is configured. The ELSA Agents DB is the single source of truth for the llm-call
 * workflow — edits via ELSA Studio take effect immediately without API sync.
 */

import type { IAgentsConfig, SecurityConfig, AgentType, IProvidersConfig, IRepoConfig, TammaConfig } from '@tamma/shared';
import { validateAgentsConfig, validateSecurityConfig, validateProvidersConfig, resolveConfig } from '@tamma/shared';
import type { ElsaAgentsClient } from './ElsaAgentsClient.js';
import type { IUserStore } from '../../persistence/user-store.js';
import type { RepoConfigReader } from './repo-config-reader.js';

const DEFAULT_CONFIG: IAgentsConfig = {
  defaults: {
    providerChain: [{ provider: 'claude-code' }],
  },
};

const DEFAULT_SECURITY: SecurityConfig = {
  sanitizeContent: true,
  validateUrls: true,
  gateActions: false,
  maxFetchSizeBytes: 10_485_760,
  blockedCommandPatterns: ['rm\\s+-rf\\s+/', 'DROP\\s+TABLE', 'DELETE\\s+FROM'],
};

export class ConfigService {
  private agentsConfig: IAgentsConfig;
  private securityConfig: SecurityConfig;
  private elsaClient: ElsaAgentsClient | null;
  private userStore: IUserStore | null;
  private repoConfigReader: RepoConfigReader | null;

  constructor(
    initialAgents?: IAgentsConfig,
    initialSecurity?: SecurityConfig,
    elsaAgentsClient?: ElsaAgentsClient | null,
    userStore?: IUserStore | null,
    repoConfigReader?: RepoConfigReader | null,
  ) {
    this.agentsConfig = initialAgents
      ? structuredClone(initialAgents)
      : structuredClone(DEFAULT_CONFIG);
    this.securityConfig = initialSecurity
      ? structuredClone(initialSecurity)
      : structuredClone(DEFAULT_SECURITY);
    this.elsaClient = elsaAgentsClient ?? null;
    this.userStore = userStore ?? null;
    this.repoConfigReader = repoConfigReader ?? null;
  }

  async getAgentsConfig(): Promise<IAgentsConfig> {
    return structuredClone(this.agentsConfig);
  }

  async updateAgentsConfig(config: IAgentsConfig): Promise<IAgentsConfig> {
    validateAgentsConfig(config);
    this.agentsConfig = structuredClone(config);
    return structuredClone(this.agentsConfig);
  }

  async getSecurityConfig(): Promise<SecurityConfig> {
    return structuredClone(this.securityConfig);
  }

  async updateSecurityConfig(config: SecurityConfig): Promise<SecurityConfig> {
    validateSecurityConfig(config);
    this.securityConfig = structuredClone(config);
    return structuredClone(this.securityConfig);
  }

  /**
   * Get prompt templates for all roles.
   * Returns a record of role -> { systemPrompt, providerPrompts }.
   */
  async getPromptTemplates(): Promise<Record<string, { systemPrompt?: string; providerPrompts?: Record<string, string> }>> {
    const result: Record<string, { systemPrompt?: string; providerPrompts?: Record<string, string> }> = {};

    // Include defaults
    const defaultsEntry: { systemPrompt?: string; providerPrompts?: Record<string, string> } = {};
    if (this.agentsConfig.defaults.systemPrompt !== undefined) {
      defaultsEntry.systemPrompt = this.agentsConfig.defaults.systemPrompt;
    }
    if (this.agentsConfig.defaults.providerPrompts !== undefined) {
      defaultsEntry.providerPrompts = { ...this.agentsConfig.defaults.providerPrompts };
    }
    result['defaults'] = defaultsEntry;

    // Include per-role overrides
    if (this.agentsConfig.roles) {
      for (const [role, roleConfig] of Object.entries(this.agentsConfig.roles)) {
        if (!roleConfig) continue;
        const entry: { systemPrompt?: string; providerPrompts?: Record<string, string> } = {};
        if (roleConfig.systemPrompt !== undefined) {
          entry.systemPrompt = roleConfig.systemPrompt;
        }
        if (roleConfig.providerPrompts !== undefined) {
          entry.providerPrompts = { ...roleConfig.providerPrompts };
        }
        result[role] = entry;
      }
    }

    return result;
  }

  private static readonly FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

  /**
   * Update prompt templates for a specific role.
   * Creates new config object to avoid direct mutation.
   */
  async updatePromptTemplate(
    role: string,
    template: { systemPrompt?: string; providerPrompts?: Record<string, string> },
  ): Promise<void> {
    if (ConfigService.FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role name: ${role}`);
    }

    const updated = structuredClone(this.agentsConfig);

    // Empty string means "clear the value"
    const normalizedPrompt = template.systemPrompt === '' ? undefined : template.systemPrompt;

    if (role === 'defaults') {
      if (template.systemPrompt !== undefined) {
        if (normalizedPrompt !== undefined) {
          updated.defaults.systemPrompt = normalizedPrompt;
        } else {
          delete updated.defaults.systemPrompt;
        }
      }
      if (template.providerPrompts !== undefined) {
        updated.defaults.providerPrompts = template.providerPrompts;
      }
      this.agentsConfig = updated;
      return;
    }

    if (!updated.roles) {
      updated.roles = {};
    }

    const existing = { ...(updated.roles[role as AgentType] ?? {}) };
    if (template.systemPrompt !== undefined) {
      if (normalizedPrompt !== undefined) {
        existing.systemPrompt = normalizedPrompt;
      } else {
        delete existing.systemPrompt;
      }
    }
    if (template.providerPrompts !== undefined) {
      existing.providerPrompts = template.providerPrompts;
    }
    updated.roles[role as AgentType] = existing;
    this.agentsConfig = updated;

    // Sync to ELSA Agents store (best-effort — failure is logged, not thrown)
    if (normalizedPrompt !== undefined) {
      await this.syncPromptToElsa(role, normalizedPrompt);
    }
  }

  // --- User-scoped provider settings (SaaS mode) ---

  /**
   * Get a user's provider settings.
   * Returns empty config if user store is not configured or user not found.
   */
  async getUserProviders(userId: string): Promise<IProvidersConfig> {
    if (!this.userStore) {
      return { providers: {} };
    }
    return this.userStore.getUserSettings(userId);
  }

  /**
   * Update a user's provider settings.
   * Validates before persisting.
   */
  async updateUserProviders(userId: string, config: IProvidersConfig): Promise<IProvidersConfig> {
    if (!this.userStore) {
      throw new Error('User store not configured — cannot update user providers in this mode');
    }
    validateProvidersConfig(config);
    return this.userStore.updateUserSettings(userId, config);
  }

  /**
   * Resolve a full TammaConfig for a specific repo in SaaS mode.
   * Merges: user providers → repo config (from git) → resolved TammaConfig.
   */
  async resolveForRepo(
    userId: string,
    owner: string,
    repo: string,
    branch: string,
  ): Promise<{ config: TammaConfig; warnings: string[] }> {
    // Get user provider settings
    const providers = await this.getUserProviders(userId);

    // Get repo config from git
    let repoConfig: IRepoConfig = {};
    if (this.repoConfigReader) {
      repoConfig = await this.repoConfigReader.readRepoConfig(owner, repo, branch);
    }

    return resolveConfig(providers, repoConfig);
  }

  /**
   * Best-effort sync of a prompt template to the ELSA Agents store.
   * The llm-call workflow reads prompts from the ELSA Agents DB directly,
   * so this ensures edits via the Tamma Dashboard are reflected immediately.
   * Failures are swallowed — the in-memory update still succeeds.
   */
  private async syncPromptToElsa(role: string, promptTemplate: string): Promise<void> {
    if (!this.elsaClient) return;

    try {
      const agentName = `tamma-${role}`;
      const agent = await this.elsaClient.findAgentByName(agentName);
      if (!agent) return;

      await this.elsaClient.updateAgent(agent.id, {
        name: agent.name,
        description: agent.description,
        agentConfig: {
          ...agent.agentConfig,
          promptTemplate,
        },
      });
    } catch {
      // ELSA sync failure is non-fatal — the llm-call workflow will pick up
      // the change on next startup via AgentSeeder, or via direct ELSA Studio edit.
    }
  }
}
