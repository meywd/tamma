/**
 * Provider configuration types for ~/.tamma/providers.json (CLI)
 * and user.settings JSONB column (SaaS).
 *
 * This is personal configuration — credentials, models, budgets.
 * Never committed to a repository.
 */

import type { PermissionMode } from './agent-config.js';

/** A single provider definition with credentials and defaults. */
export interface IProviderDefinition {
  /** API key for the provider (resolved at runtime from env or file). */
  apiKey?: string;
  /** Default model to use with this provider. */
  defaultModel?: string;
  /** Base URL for the provider API (useful for proxies, local providers). */
  baseUrl?: string;
  /** Request timeout in seconds. */
  timeoutSeconds?: number;
}

/** User-level provider settings — personal, never committed. */
export interface IProvidersConfig {
  /** Map of provider name → provider definition with credentials. */
  providers: Record<string, IProviderDefinition>;
  /** Global maximum budget in USD across all providers. */
  maxBudgetUsd?: number;
  /** Default permission mode for agent operations. */
  permissionMode?: PermissionMode;
}
