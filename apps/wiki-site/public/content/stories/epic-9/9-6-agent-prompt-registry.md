---
title: "Story 6: Agent Prompt Registry"
sidebar:
  order: 90
---

## Goal
Resolve system prompt TEMPLATES with fallback: per-provider-per-role -> per-role -> per-provider-default -> global default -> built-in. Templates are parametric with `{{variable}}` placeholders, not static strings.

**Why "AgentPromptRegistry"**: The name `PromptRegistry` already exists in `packages/mcp-client/src/registry.ts` (MCP prompt management). This class serves a different purpose — managing agent role prompt templates — so it is named `AgentPromptRegistry` to avoid collision.

## Design

**New file: `packages/providers/src/agent-prompt-registry.ts`**

The prompts in `engine.ts` are parametric: they embed `${issue.number}`, `${context}`, `${plan.summary}`, etc. via template literals. The registry cannot return those dynamic strings — it stores only the **role preamble** (the system context portion), and the engine appends dynamic content after it.

Design: the registry stores prompt templates with `{{variable}}` placeholders. It exposes `render()` instead of `resolve()`.

The class implements `IAgentPromptRegistry` interface (matching the pattern from IProviderHealthTracker (9-3), IAgentProviderFactory (9-4), IProviderChain (9-5)). The constructor accepts an options object (`AgentPromptRegistryOptions`) with config, optional logger, and optional immutable roles set. The caller (engine) is responsible for sanitizing variable values before passing to `render()`.

```typescript
import type { AgentType } from '@tamma/shared';
import type { AgentsConfig } from '@tamma/shared/src/types/agent-config.js';
import type { ILogger } from '@tamma/shared/contracts';

/**
 * Built-in role preambles extracted from engine.ts.
 * These are the static system context prefixes; the engine appends
 * dynamic content (issue details, plan data, etc.) after rendering.
 *
 * Frozen to prevent accidental mutation of module-level constant.
 */
const BUILTIN_TEMPLATES: Partial<Record<AgentType, string>> = Object.freeze({
  architect: 'You are analyzing a GitHub issue to create a development plan.\n\n{{context}}',
  implementer: 'You are an autonomous coding agent. Implement the following plan for issue #{{issueNumber}}.',
  reviewer: 'You are a code reviewer. Review the changes for correctness, style, and security.',
  tester: 'You are a testing agent. Write and run tests for the described changes.',
  analyst: 'You are analyzing project context to understand codebase structure and conventions.',
  scrum_master: 'You are a project coordinator. Select the most appropriate issue to work on next.',
  researcher: 'You are a research agent. Investigate and gather information about the topic at hand.',
  planner: 'You are a planning agent. Create structured plans and organize work breakdown.',
  documenter: 'You are a documentation agent. Write clear, comprehensive documentation for the codebase.',
});

const GENERIC_FALLBACK = 'You are an AI assistant working on a software development task.';

/** Maximum rendered template length (1MB). Templates exceeding this are truncated with a warning. */
const MAX_TEMPLATE_LENGTH = 1_000_000;

/** Maximum variable value length (100KB). Variables exceeding this are skipped with a warning. */
const MAX_VAR_VALUE_LENGTH = 100_000;

/** Keys that cannot be used as role names or provider names (prototype pollution guard). */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

export interface AgentPromptRegistryOptions {
  config: AgentsConfig;
  logger?: ILogger;
  immutableRoles?: ReadonlySet<string>;
}

export interface IAgentPromptRegistry {
  render(role: AgentType, providerName: string, vars?: Record<string, string>): string;
  resolveTemplate(role: AgentType, providerName: string): string;
  registerBuiltin(role: string, template: string): void;
}

export class AgentPromptRegistry implements IAgentPromptRegistry {
  private readonly config: AgentsConfig;
  private readonly logger?: ILogger;
  private readonly immutableRoles: ReadonlySet<string>;
  private builtinTemplates: Record<string, string>;

  constructor(options: AgentPromptRegistryOptions) {
    this.config = options.config;
    this.logger = options.logger;
    this.immutableRoles = options.immutableRoles ?? new Set();
    // Use Object.create(null) for prototype-free backing (prototype pollution guard)
    this.builtinTemplates = Object.create(null) as Record<string, string>;
    for (const [key, value] of Object.entries(BUILTIN_TEMPLATES)) {
      this.builtinTemplates[key] = value;
    }
  }

  /**
   * Resolve the template for a role+provider and render it with variables.
   *
   * Resolution chain (first non-undefined wins):
   * 1. roles[role].providerPrompts?.[providerName]
   * 2. roles[role].systemPrompt
   * 3. defaults.providerPrompts?.[providerName]
   * 4. defaults.systemPrompt
   * 5. builtinTemplates[role]
   * 6. GENERIC_FALLBACK
   *
   * Null-safety: every step guards against undefined roles, missing
   * providerPrompts map, and missing keys.
   *
   * Note: The caller (engine) is responsible for sanitizing variable values
   * before passing to render(). Variables are applied iteratively via
   * split+join in Object.entries() order. If variable A's replacement
   * contains {{B}} and B is also provided, B WILL be expanded within A's
   * value. This is by design -- callers should sanitize values if this is
   * undesirable.
   */
  render(
    role: AgentType,
    providerName: string,
    vars: Record<string, string> = {},
  ): string {
    const template = this.resolveTemplate(role, providerName);
    return this.interpolate(template, vars);
  }

  /** Resolve without rendering — returns the raw template string. */
  resolveTemplate(role: AgentType, providerName: string): string {
    // Validate providerName against prototype pollution
    if (FORBIDDEN_KEYS.has(providerName)) {
      throw new Error(`Cannot resolve template with forbidden provider name: ${providerName}`);
    }

    // 1. Per-provider-per-role
    const roleConfig = this.config.roles?.[role];
    if (roleConfig?.providerPrompts !== undefined) {
      if (Object.hasOwn(roleConfig.providerPrompts, providerName)) {
        const prompt = roleConfig.providerPrompts[providerName];
        if (prompt !== undefined) return prompt;
      }
    }

    // 2. Per-role system prompt
    if (roleConfig?.systemPrompt !== undefined) {
      return roleConfig.systemPrompt;
    }

    // 3. Per-provider default
    if (this.config.defaults.providerPrompts !== undefined) {
      if (Object.hasOwn(this.config.defaults.providerPrompts, providerName)) {
        const prompt = this.config.defaults.providerPrompts[providerName];
        if (prompt !== undefined) return prompt;
      }
    }

    // 4. Global default system prompt
    if (this.config.defaults.systemPrompt !== undefined) {
      return this.config.defaults.systemPrompt;
    }

    // 5. Built-in template for role
    const builtin = this.builtinTemplates[role];
    if (builtin !== undefined) return builtin;

    // 6. Generic fallback
    return GENERIC_FALLBACK;
  }

  /**
   * Replace `{{key}}` placeholders with values from vars.
   *
   * Variables are applied iteratively in Object.entries() order using
   * split+join. If variable A's replacement contains {{B}} and B is also
   * provided, B WILL be expanded within A's value. This is by design --
   * callers should sanitize values if this is undesirable.
   *
   * Variables whose values exceed MAX_VAR_VALUE_LENGTH are skipped with
   * a warning log.
   */
  private interpolate(template: string, vars: Record<string, string>): string {
    let result = template;
    for (const [key, value] of Object.entries(vars)) {
      if (value.length > MAX_VAR_VALUE_LENGTH) {
        this.logger?.warn('Skipping variable exceeding MAX_VAR_VALUE_LENGTH', {
          key,
          valueLength: value.length,
          limit: MAX_VAR_VALUE_LENGTH,
        });
        continue;
      }
      // Use split+join instead of regex for safety on untrusted template content
      result = result.split(`{{${key}}}`).join(value);
    }
    // Truncate if rendered template exceeds MAX_TEMPLATE_LENGTH
    if (result.length > MAX_TEMPLATE_LENGTH) {
      this.logger?.warn('Rendered template exceeds MAX_TEMPLATE_LENGTH, truncating', {
        length: result.length,
        limit: MAX_TEMPLATE_LENGTH,
      });
      result = result.slice(0, MAX_TEMPLATE_LENGTH);
    }
    return result;
  }

  /**
   * Register or override a built-in template (for testing or plugins).
   *
   * Rejects forbidden keys (__proto__, constructor, prototype) to prevent
   * prototype pollution. Rejects overriding immutable roles (e.g., reviewer).
   * Logs WARN when overriding an existing template, INFO when adding new.
   */
  registerBuiltin(role: string, template: string): void {
    if (FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Cannot register template with forbidden key: ${role}`);
    }
    if (this.immutableRoles.has(role)) {
      throw new Error(`Cannot override immutable role template: ${role}`);
    }
    if (this.builtinTemplates[role] !== undefined) {
      this.logger?.warn('Overriding existing built-in template', { role });
    } else {
      this.logger?.info('Registering new built-in template', { role });
    }
    this.builtinTemplates[role] = template;
  }
}
```

**Engine usage example (Story 9 will do the full integration)**:
```typescript
// In generatePlan():
const preamble = this.agentResolver.getPrompt('architect', providerName, {
  context: contextString,
});
// The engine then appends the dynamic JSON schema instructions after the preamble
const fullPrompt = `${preamble}\n\nGenerate a structured development plan as JSON...`;
```

## Files
- CREATE `packages/providers/src/agent-prompt-registry.ts`
- CREATE `packages/providers/src/agent-prompt-registry.test.ts`

## Verify
- Test each level of the resolution chain (6 levels)
- Test fallback to built-in when nothing configured
- Test fallback to generic when role has no built-in
- Test `render()` replaces `{{variable}}` placeholders
- Test `render()` with empty vars leaves `{{placeholders}}` unchanged (safe default)
- Test null-safety: `roles` is undefined, `providerPrompts` is undefined, keys are missing
- Test `registerBuiltin()` overrides default templates
- Test `registerBuiltin()` rejects forbidden keys (`__proto__`, `constructor`, `prototype`)
- Test `registerBuiltin()` rejects overriding immutable roles
- Test `registerBuiltin()` logs WARN on override, INFO on new registration
- Test `IAgentPromptRegistry` interface is exported and `AgentPromptRegistry` implements it
- Test `BUILTIN_TEMPLATES` includes entries for all 9 `AgentType` roles (architect, implementer, reviewer, tester, analyst, scrum_master, researcher, planner, documenter)
- Test empty string `''` is treated as a valid template at any resolution level (not skipped)
- Test `resolveTemplate()` rejects forbidden provider names (`__proto__`, `constructor`, `prototype`)
- Test `interpolate()` skips variables whose values exceed `MAX_VAR_VALUE_LENGTH` (100KB)
- Test rendered templates exceeding `MAX_TEMPLATE_LENGTH` (1MB) are truncated with warning
- Test unmatched `{{placeholders}}` are left as-is in rendered output (not removed, not throwing)
- Test with minimal config (`{ defaults: { providerChain: [] } }` with no roles, no systemPrompt, no providerPrompts)
- Test `Object.hasOwn()` guards on `providerPrompts` property access
- Test constructor accepts `AgentPromptRegistryOptions` object (not bare `AgentsConfig`)

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
