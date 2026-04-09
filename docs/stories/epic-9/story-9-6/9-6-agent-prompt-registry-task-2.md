# SUPERSEDED by Epic 27

> **This task file is superseded by Epic 27 (Prompt Store -- Multi-Tenant Prompt Management).**
> The prompt registry functionality defined here has been absorbed into the Postgres-backed prompt store.
> See `/home/meywd/tamma/docs/stories/epic-27/README.md` for the replacement.
> This file is retained for historical reference only. Do not implement.

---

# Task 2: Implement Template Resolution Chain (6 Levels) with Security Guards

**Story:** 9-6-agent-prompt-registry - Agent Prompt Registry
**Epic:** 9

## Task Description

Implement the `resolveTemplate()` method on `AgentPromptRegistry` with a 6-level resolution chain and prototype pollution guards, and the `registerBuiltin()` method with forbidden key rejection, immutable role protection, and audit trail logging. The resolution chain allows prompt templates to be overridden at increasingly specific levels, from generic fallback up to per-provider-per-role customization.

## Acceptance Criteria

- `resolveTemplate(role, providerName)` returns the first non-undefined template from the 6-level chain
- Level 1: `config.roles[role].providerPrompts[providerName]` (guarded with `Object.hasOwn()`)
- Level 2: `config.roles[role].systemPrompt`
- Level 3: `config.defaults.providerPrompts[providerName]` (guarded with `Object.hasOwn()`)
- Level 4: `config.defaults.systemPrompt`
- Level 5: `builtinTemplates[role]`
- Level 6: `GENERIC_FALLBACK`
- `resolveTemplate()` validates `providerName` against `FORBIDDEN_KEYS` and throws for forbidden values
- `Object.hasOwn()` is used for `providerPrompts` property access to guard against prototype pollution
- Every step guards against undefined `roles`, missing `providerPrompts` map, and missing keys
- Empty string `''` is treated as a valid template at any resolution level (not skipped by `!== undefined` checks)
- `registerBuiltin()` allows overriding or adding built-in templates
- `registerBuiltin()` rejects forbidden keys (`__proto__`, `constructor`, `prototype`) with thrown error
- `registerBuiltin()` rejects overriding immutable roles (configured via `immutableRoles` in options) with thrown error
- `registerBuiltin()` logs WARN when overriding an existing template, INFO when adding a new one
- No exceptions thrown for any combination of undefined/missing config fields (only for security violations)
- Works correctly with minimal config (`{ defaults: { providerChain: [] } }` with no roles, no systemPrompt, no providerPrompts)

## Implementation Details

### Technical Requirements

- [ ] Implement `resolveTemplate(role: AgentType, providerName: string): string` on the `AgentPromptRegistry` class with `Object.hasOwn()` guards and forbidden key validation:
  ```typescript
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
  ```
- [ ] Implement `registerBuiltin(role: string, template: string): void` with security guards:
  ```typescript
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
  ```
- [ ] Use explicit `!== undefined` checks (not truthiness) to allow empty-string prompts as valid overrides

### Files to Modify/Create

- `packages/providers/src/agent-prompt-registry.ts` -- **MODIFY** -- Add `resolveTemplate()` and `registerBuiltin()` methods to the class stub from Task 1
- `packages/providers/src/agent-prompt-registry.test.ts` -- **MODIFY** -- Add resolution chain tests

### Dependencies

- [ ] Task 1: AgentPromptRegistry class stub with BUILTIN_TEMPLATES (frozen, 9 roles), GENERIC_FALLBACK, MAX_TEMPLATE_LENGTH, MAX_VAR_VALUE_LENGTH, FORBIDDEN_KEYS, IAgentPromptRegistry, AgentPromptRegistryOptions
- [ ] `AgentsConfig` type from `packages/shared/src/types/agent-config.ts` (Story 9-1)
- [ ] `ILogger` type from `@tamma/shared/contracts`

## Testing Strategy

### Unit Tests

**Level 1 -- per-provider-per-role:**
- [ ] When `roles[role].providerPrompts[providerName]` is set, returns that prompt
- [ ] When `roles[role].providerPrompts` exists but has no entry for the provider, falls through to level 2

**Level 2 -- per-role systemPrompt:**
- [ ] When `roles[role].systemPrompt` is set (and no provider-specific match), returns that prompt
- [ ] When `roles[role]` exists but has no `systemPrompt`, falls through to level 3

**Level 3 -- per-provider default:**
- [ ] When `defaults.providerPrompts[providerName]` is set, returns that prompt
- [ ] When `defaults.providerPrompts` exists but has no entry for the provider, falls through to level 4

**Level 4 -- global default systemPrompt:**
- [ ] When `defaults.systemPrompt` is set (and no provider-specific match), returns that prompt
- [ ] When `defaults` has no `systemPrompt`, falls through to level 5

**Level 5 -- built-in template:**
- [ ] For roles with built-in templates (architect, implementer, etc.), returns the built-in
- [ ] For roles without built-in templates, falls through to level 6

**Level 6 -- generic fallback:**
- [ ] Returns `GENERIC_FALLBACK` when no other level matches
- [ ] Test with an unknown role that has no built-in template and no config

**Empty-string semantics:**
- [ ] Empty string `''` at level 1 (providerPrompts) is returned as valid (not skipped)
- [ ] Empty string `''` at level 2 (systemPrompt) is returned as valid (not skipped)
- [ ] Empty string `''` at level 4 (defaults.systemPrompt) is returned as valid (not skipped)

**Prototype pollution and security guards:**
- [ ] `resolveTemplate()` throws for `providerName = '__proto__'`
- [ ] `resolveTemplate()` throws for `providerName = 'constructor'`
- [ ] `resolveTemplate()` throws for `providerName = 'prototype'`
- [ ] `Object.hasOwn()` guards on `providerPrompts` property access prevent prototype chain lookups

**Minimal config:**
- [ ] Config `{ defaults: { providerChain: [] } }` with no roles, no systemPrompt, no providerPrompts -- falls through to built-in or generic

**Null-safety edge cases:**
- [ ] `config.roles` is undefined -- resolution skips levels 1 and 2
- [ ] `config.roles[role]` is undefined -- resolution skips levels 1 and 2
- [ ] `config.roles[role]` exists but `providerPrompts` is undefined -- resolution skips level 1
- [ ] `config.defaults.providerPrompts` is undefined -- resolution skips level 3
- [ ] `config.defaults.systemPrompt` is undefined -- resolution skips level 4

**registerBuiltin():**
- [ ] Overrides an existing built-in template (e.g., replace `architect` template) -- logs WARN
- [ ] Adds a template for a role not in the original `BUILTIN_TEMPLATES` (e.g., a custom role) -- logs INFO
- [ ] After `registerBuiltin()`, `resolveTemplate()` returns the new template at level 5
- [ ] Does not affect the module-level `BUILTIN_TEMPLATES` constant (test with a second instance)
- [ ] Rejects `__proto__` key with thrown error
- [ ] Rejects `constructor` key with thrown error
- [ ] Rejects `prototype` key with thrown error
- [ ] Rejects overriding a role in `immutableRoles` set with thrown error
- [ ] Logs WARN when overriding an existing template
- [ ] Logs INFO when adding a new template

### Validation Steps

1. [ ] Implement `resolveTemplate()` method
2. [ ] Implement `registerBuiltin()` method
3. [ ] Run `pnpm --filter @tamma/providers run typecheck` -- must pass
4. [ ] Write all resolution chain tests
5. [ ] Run `pnpm vitest run packages/providers/src/agent-prompt-registry`

## Notes & Considerations

- Use `!== undefined` checks, not truthiness checks, so that an empty string `""` is treated as a valid prompt override (truthy check would skip it). Empty string `''` is a valid template at any resolution level.
- The `roleConfig` variable is extracted once and reused for both level 1 and level 2 checks to avoid redundant optional chaining
- `registerBuiltin()` accepts `string` (not `AgentType`) to allow plugins to register custom role templates without modifying the AgentType union
- The `builtinTemplates` instance field uses `Record<string, string>` backed by `Object.create(null)` to accommodate both `AgentType` keys from `BUILTIN_TEMPLATES` and arbitrary string keys from `registerBuiltin()`, while preventing prototype pollution
- `GENERIC_FALLBACK` is a module-level constant, not configurable -- it is the absolute last resort
- `Object.hasOwn()` is used instead of `in` operator or direct bracket access for `providerPrompts` property lookup to guard against prototype chain traversal
- `FORBIDDEN_KEYS` rejection in `registerBuiltin()` and `resolveTemplate()` prevents prototype pollution attacks
- `immutableRoles` protection prevents overriding security-critical role templates (e.g., `reviewer` which controls code review behavior)
- The constructor accepts `AgentPromptRegistryOptions` (not bare `AgentsConfig`), which includes optional `logger` for audit trail and optional `immutableRoles` for protection

## Completion Checklist

- [ ] `resolveTemplate()` method implemented with 6-level chain
- [ ] `resolveTemplate()` validates `providerName` against `FORBIDDEN_KEYS`
- [ ] `resolveTemplate()` uses `Object.hasOwn()` for `providerPrompts` property access
- [ ] `registerBuiltin()` method implemented with forbidden key rejection, immutable role protection, and audit logging
- [ ] All null-safety guards in place (undefined roles, missing maps, missing keys)
- [ ] Unit tests for all 6 levels passing
- [ ] Unit tests for empty-string semantics passing
- [ ] Unit tests for null-safety edge cases passing
- [ ] Unit tests for `registerBuiltin()` passing (including security guard tests)
- [ ] Unit tests for prototype pollution guards passing
- [ ] Unit tests for immutable roles passing
- [ ] Unit tests for minimal config passing
- [ ] TypeScript strict mode compilation passes
