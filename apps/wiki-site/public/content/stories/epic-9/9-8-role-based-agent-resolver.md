---
title: "Story 8: Role-Based Agent Resolver"
sidebar:
  order: 90
---

## Goal
Tie everything together: phase -> role -> provider chain -> instrumented + secure provider. Uses options-object constructor, `IAgentPromptRegistry`, and `DiagnosticsQueue`.

## Design

**New file: `packages/providers/src/role-based-agent-resolver.ts`**

Replaces the 8-parameter constructor with a single options object for clarity and extensibility. Uses `IProviderChain`, `IAgentProviderFactory`, `IProviderHealthTracker`, and `IAgentPromptRegistry` interfaces (not concrete classes) for dependency inversion.

```typescript
import type { AgentType } from '@tamma/shared';
import type { ILogger } from '@tamma/shared/contracts';
import type { AgentsConfig, WorkflowPhase, ProviderChainEntry } from '@tamma/shared/src/types/agent-config.js';
import type { ICostTracker } from '@tamma/cost-monitor';
import type { DiagnosticsQueue } from '@tamma/shared/src/telemetry/index.js';
import type { AgentTaskConfig } from './agent-types.js';
import type { IAgentProvider } from './agent-types.js';
import type { IAgentProviderFactory } from './agent-provider-factory.js';
import type { IProviderHealthTracker } from './types.js';
import type { IProviderChain } from './provider-chain.js';
import { DEFAULT_PHASE_ROLE_MAP } from '@tamma/shared/src/types/agent-config.js';
import type { IAgentPromptRegistry } from './agent-prompt-registry.js';
import { ProviderChain } from './provider-chain.js';
import { SecureAgentProvider } from './secure-agent-provider.js';
import type { IContentSanitizer } from '@tamma/shared/security';

export interface IRoleBasedAgentResolver {
  getAgentForPhase(phase: WorkflowPhase, context: { projectId: string; engineId: string }): Promise<IAgentProvider>;
  getAgentForRole(role: AgentType, context: { projectId: string; engineId: string }): Promise<IAgentProvider>;
  getTaskConfig(role: AgentType, taskOverrides?: Partial<AgentTaskConfig>): Partial<AgentTaskConfig>;
  getPrompt(role: AgentType, providerName: string, vars?: Record<string, string>): string;
  getRoleForPhase(phase: WorkflowPhase): AgentType;
  dispose(): Promise<void>;
}

export interface RoleBasedAgentResolverOptions {
  config: AgentsConfig;
  factory: IAgentProviderFactory;
  health: IProviderHealthTracker;
  promptRegistry: IAgentPromptRegistry;
  diagnostics: DiagnosticsQueue;
  costTracker?: ICostTracker;
  sanitizer?: IContentSanitizer;
  logger?: ILogger;
}

export class RoleBasedAgentResolver implements IRoleBasedAgentResolver {
  private static readonly FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

  /**
   * Fields that participate in the config merge cascade.
   * Other AgentTaskConfig fields (prompt, cwd, model, sessionId) are set
   * by the engine at call time and do not participate in the resolver merge.
   */
  private static readonly MERGEABLE_FIELDS = ['allowedTools', 'maxBudgetUsd', 'permissionMode'] as const;

  private chains = new Map<AgentType, IProviderChain>();
  private readonly config: AgentsConfig;
  private readonly factory: IAgentProviderFactory;
  private readonly health: IProviderHealthTracker;
  private readonly promptRegistry: IAgentPromptRegistry;
  private readonly diagnostics: DiagnosticsQueue;
  private readonly costTracker?: ICostTracker;
  private readonly sanitizer?: IContentSanitizer;
  private readonly logger?: ILogger;

  constructor(options: RoleBasedAgentResolverOptions) {
    this.config = options.config;
    this.factory = options.factory;
    this.health = options.health;
    this.promptRegistry = options.promptRegistry;
    this.diagnostics = options.diagnostics;
    this.costTracker = options.costTracker;
    this.sanitizer = options.sanitizer;
    this.logger = options.logger;

    // Validate defaults.providerChain is non-empty
    if (!this.config.defaults.providerChain || this.config.defaults.providerChain.length === 0) {
      throw new Error('config.defaults.providerChain must be non-empty');
    }
  }

  async getAgentForPhase(
    phase: WorkflowPhase,
    context: { projectId: string; engineId: string },
  ): Promise<IAgentProvider> {
    const role = this.config.phaseRoleMap?.[phase]
      ?? DEFAULT_PHASE_ROLE_MAP[phase];

    // Runtime validation: reject forbidden keys and unknown roles
    if (RoleBasedAgentResolver.FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role key in phaseRoleMap: ${role}`);
    }

    return this.getAgentForRole(role, context);
  }

  getRoleForPhase(phase: WorkflowPhase): AgentType {
    return this.config.phaseRoleMap?.[phase] ?? DEFAULT_PHASE_ROLE_MAP[phase];
  }

  async getAgentForRole(
    role: AgentType,
    context: { projectId: string; engineId: string },
  ): Promise<IAgentProvider> {
    this.logger?.debug('Resolving agent for role', { role, ...context });

    const chain = this.getOrCreateChain(role);

    // chain.getProvider() returns an InstrumentedAgentProvider.
    // recordSuccess() is handled inside InstrumentedAgentProvider on
    // task completion — NOT here.
    const provider = await chain.getProvider({
      agentType: role,
      ...context,
    });

    // Wrap with security if sanitizer provided
    if (this.sanitizer) {
      return new SecureAgentProvider(provider, this.sanitizer, this.logger);
    }
    this.logger?.warn('No content sanitizer configured — provider returned without security wrapping', { role });
    return provider;
  }

  /**
   * Get merged task config for a role.
   *
   * Merge precedence (last wins, with clamping to prevent escalation):
   *   1. Engine defaults (this.config.defaults)
   *   2. Role-specific config (this.config.roles[role])
   *   3. Task-level overrides (caller-provided taskOverrides) — clamped
   *
   * Clamping rules for taskOverrides:
   *   - maxBudgetUsd: cannot exceed the resolved config ceiling (min of role and defaults)
   *   - permissionMode: 'bypassPermissions' requires TAMMA_ALLOW_BYPASS_PERMISSIONS=true env var
   *   - allowedTools: overrides can only restrict (intersection), not expand
   */
  getTaskConfig(
    role: AgentType,
    taskOverrides?: Partial<AgentTaskConfig>,
  ): Partial<AgentTaskConfig> {
    if (RoleBasedAgentResolver.FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role key: ${role}`);
    }

    const defaults = this.config.defaults;
    const roleConfig = this.config.roles?.[role];

    const base: Partial<AgentTaskConfig> = {
      allowedTools: defaults.allowedTools,
      maxBudgetUsd: defaults.maxBudgetUsd,
      permissionMode: defaults.permissionMode,
    };

    const roleLevel: Partial<AgentTaskConfig> = {
      ...(roleConfig?.allowedTools !== undefined && { allowedTools: roleConfig.allowedTools }),
      ...(roleConfig?.maxBudgetUsd !== undefined && { maxBudgetUsd: roleConfig.maxBudgetUsd }),
      ...(roleConfig?.permissionMode !== undefined && { permissionMode: roleConfig.permissionMode }),
    };

    const merged = { ...base, ...roleLevel };

    // Apply task overrides with clamping to prevent escalation
    if (taskOverrides) {
      // Budget: task override cannot exceed the resolved config ceiling
      if (taskOverrides.maxBudgetUsd !== undefined && merged.maxBudgetUsd !== undefined) {
        merged.maxBudgetUsd = Math.min(taskOverrides.maxBudgetUsd, merged.maxBudgetUsd);
      }
      // Permission mode: bypassPermissions in overrides requires env var guard
      if (taskOverrides.permissionMode === 'bypassPermissions') {
        if (process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'] !== 'true') {
          this.logger?.warn('taskOverrides requested bypassPermissions but TAMMA_ALLOW_BYPASS_PERMISSIONS is not set');
        } else {
          merged.permissionMode = taskOverrides.permissionMode;
        }
      } else if (taskOverrides.permissionMode !== undefined) {
        merged.permissionMode = taskOverrides.permissionMode;
      }
      // Allowed tools: overrides can only restrict, not expand
      if (taskOverrides.allowedTools !== undefined && merged.allowedTools !== undefined) {
        merged.allowedTools = taskOverrides.allowedTools.filter(t => merged.allowedTools!.includes(t));
      }
    }

    return merged;
  }

  /**
   * Render the prompt template for a role+provider with variables.
   * Delegates to IAgentPromptRegistry.render().
   *
   * Variable values are sanitized: {{ and }} are stripped to prevent
   * recursive template expansion (template injection).
   */
  getPrompt(
    role: AgentType,
    providerName: string,
    vars: Record<string, string> = {},
  ): string {
    // Sanitize variable values: strip {{ and }} to prevent recursive template expansion
    const sanitizedVars: Record<string, string> = {};
    for (const [key, value] of Object.entries(vars)) {
      sanitizedVars[key] = value.replaceAll('{{', '').replaceAll('}}', '');
    }
    return this.promptRegistry.render(role, providerName, sanitizedVars);
  }

  private getOrCreateChain(role: AgentType): IProviderChain {
    if (RoleBasedAgentResolver.FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role key: ${role}`);
    }

    if (!this.chains.has(role)) {
      const roleConfig = this.config.roles?.[role];

      // IMPORTANT: roleConfig?.providerChain may be an empty array [].
      // The ?? fallback does NOT trigger for [] (only for null/undefined).
      // We must check .length explicitly.
      const candidateEntries = roleConfig?.providerChain;
      const entries: ProviderChainEntry[] =
        (candidateEntries !== undefined && candidateEntries !== null && candidateEntries.length > 0)
          ? candidateEntries
          : this.config.defaults.providerChain;

      this.chains.set(role, new ProviderChain({
        entries,
        factory: this.factory,
        health: this.health,
        diagnostics: this.diagnostics,
        costTracker: this.costTracker,
        logger: this.logger,
      }));
    }
    return this.chains.get(role)!;
  }

  async dispose(): Promise<void> {
    this.chains.clear();
    this.logger?.debug('RoleBasedAgentResolver disposed', { cachedChains: 0 });
  }
}
```

## Design Notes

> **providerName for `getPrompt()`**: The caller should use the first entry's provider name from the role's provider chain config. The `InstrumentedAgentProvider` should expose a `providerName` readonly property (already receives it in constructor context). Alternatively, callers can use `resolver.getRoleForPhase()` + check `config.roles[role].providerChain[0].provider` or `config.defaults.providerChain[0].provider`.

## Files
- CREATE `packages/providers/src/role-based-agent-resolver.ts`
- CREATE `packages/providers/src/role-based-agent-resolver.test.ts`
- MODIFY `packages/providers/src/index.ts` — export all new modules

## Verify
- Test: phase maps to correct role
- Test: role uses correct provider chain
- Test: fallback works when first provider fails
- Test: returned provider is instrumented + secure
- Test: custom phaseRoleMap overrides defaults
- Test: `getTaskConfig()` merge precedence — task overrides > role config > defaults
- Test: `getTaskConfig()` with undefined roleConfig falls back to defaults only
- Test: empty `providerChain: []` in role config falls back to defaults (not treated as configured)
- Test: `getPrompt()` delegates to `IAgentPromptRegistry.render()` with vars
- Test: `getPrompt()` sanitizes `{{` and `}}` from variable values to prevent template injection
- Test: constructor accepts `RoleBasedAgentResolverOptions` object (no positional params)
- Test: constructor throws when `defaults.providerChain` is empty or missing
- Test: uses `DiagnosticsQueue`, not `ToolHookRegistry`
- Test: `RoleBasedAgentResolverOptions` uses `IAgentProviderFactory` (interface, not concrete `AgentProviderFactory`)
- Test: `RoleBasedAgentResolverOptions` uses `IProviderHealthTracker` (interface, not concrete `ProviderHealthTracker`)
- Test: `getOrCreateChain()` returns `IProviderChain` (interface, not concrete `ProviderChain`)
- Test: `ProviderChain` constructed with `ProviderChainOptions` object (not positional params)
- Test: `RoleBasedAgentResolverOptions` uses `IAgentPromptRegistry` (interface, not concrete `AgentPromptRegistry`)
- Test: `getOrCreateChain()` rejects forbidden keys (`__proto__`, `constructor`, `prototype`)
- Test: `getTaskConfig()` rejects forbidden keys (`__proto__`, `constructor`, `prototype`)
- Test: `getAgentForPhase()` rejects forbidden role keys from phaseRoleMap
- Test: `getTaskConfig()` clamping — `maxBudgetUsd` override cannot exceed resolved ceiling
- Test: `getTaskConfig()` clamping — `bypassPermissions` requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`
- Test: `getTaskConfig()` clamping — `allowedTools` override is intersected (not replaced)
- Test: `getAgentForRole()` logs debug message with role and context
- Test: `getAgentForRole()` logs warn when sanitizer is not configured
- Test: `dispose()` clears the chains Map
- Test: `getRoleForPhase()` returns role synchronously without creating a provider
- Test: class implements `IRoleBasedAgentResolver` interface

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
