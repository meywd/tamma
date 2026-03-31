/**
 * Repo-level configuration types for <repo>/.tamma/config.json.
 *
 * This is project configuration — roles, engine, security, prompts.
 * Committed to the repository. Must NEVER contain secrets.
 */

/** Repo-level role configuration — references providers by name, no secrets. */
export interface IRepoRoleConfig {
  /** Key into providers.json (e.g. "anthropic", "openrouter"). */
  provider: string;
  /** Override the provider's defaultModel for this role. */
  model?: string;
  /** Tools this role is allowed to use. */
  allowedTools?: string[];
  /** Per-role budget cap in USD for this repo. */
  maxBudgetUsd?: number;
  /** System prompt override for this role. */
  systemPrompt?: string;
  /** Per-provider prompt overrides (key = provider name). */
  providerPrompts?: Record<string, string>;
}

/** Repo-level configuration — committed to git. */
export interface IRepoConfig {
  /** Engine behavior settings. */
  engine?: Partial<{
    approvalMode: 'cli' | 'auto';
    pollIntervalMs: number;
    ciPollIntervalMs: number;
    ciMonitorTimeoutMs: number;
  }>;
  /** Per-role configuration (key = agent role name). */
  roles?: Partial<Record<string, IRepoRoleConfig>>;
  /** Maps workflow phases to role names. */
  phaseRoleMap?: Partial<Record<string, string>>;
  /** Security policy for this repo. */
  security?: {
    sanitizeContent?: boolean;
    validateUrls?: boolean;
    gateActions?: boolean;
    maxFetchSizeBytes?: number;
    blockedCommandPatterns?: string[];
  };
  /** GitHub-specific settings. */
  github?: Partial<{
    issueLabels: string[];
    excludeLabels: string[];
    botUsername: string;
  }>;
}
