---
title: "Task 4: Implement getPrompt Delegation and Provider Chain Management"
sidebar:
  order: 90
---

**Story:** 9-8-role-based-agent-resolver - Role-Based Agent Resolver
**Epic:** 9

## Task Description

Implement the `getPrompt()` method that delegates to `AgentPromptRegistry.render()`, and finalize the private `getOrCreateChain()` method with its critical empty-array fallback logic. The chain management includes lazy creation, Map-based caching, and correct handling of the `providerChain: []` edge case.

## Acceptance Criteria

- `getPrompt(role, providerName, vars)` delegates to `this.promptRegistry.render(role, providerName, vars)` and returns the result
- `getPrompt()` passes default empty object for vars when not provided
- `getOrCreateChain(role)` creates a new ProviderChain on first call for a role
- `getOrCreateChain(role)` returns cached ProviderChain on subsequent calls for same role
- Different roles get different ProviderChain instances
- `providerChain: []` (empty array) in role config falls back to `defaults.providerChain`
- `providerChain: undefined` or `null` in role config falls back to `defaults.providerChain`
- Non-empty `providerChain` in role config is used directly
- ProviderChain constructor receives a `ProviderChainOptions` object (not positional args)

## Implementation Details

### Technical Requirements

#### getPrompt()

- [ ] Implement `getPrompt(role: AgentType, providerName: string, vars: Record<string, string> = {}): string`
- [ ] Sanitize variable values: strip `{{` and `}}` from all values to prevent template injection
- [ ] Delegate to `this.promptRegistry.render(role, providerName, sanitizedVars)`
- [ ] Return the rendered string directly

#### getOrCreateChain()

- [ ] Implement `private getOrCreateChain(role: AgentType): ProviderChain`
- [ ] Check `this.chains.has(role)` -- return cached chain if exists
- [ ] Look up `this.config.roles?.[role]?.providerChain` for role-specific entries
- [ ] **Critical**: Check `.length` explicitly for empty array fallback:
  ```typescript
  const candidateEntries = roleConfig?.providerChain;
  const entries: ProviderChainEntry[] =
    (candidateEntries !== undefined && candidateEntries !== null && candidateEntries.length > 0)
      ? candidateEntries
      : this.config.defaults.providerChain;
  ```
- [ ] Create new ProviderChain with `ProviderChainOptions` object: `{ entries, factory, health, diagnostics, costTracker, logger }`
- [ ] Add FORBIDDEN_KEYS guard at start of `getOrCreateChain()`
- [ ] Store in `this.chains` Map and return

### Key Code

```typescript
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
```

### Files to Modify

- `packages/providers/src/role-based-agent-resolver.ts` -- ADD: getPrompt() and finalize getOrCreateChain()
- `packages/providers/src/role-based-agent-resolver.test.ts` -- ADD: tests for getPrompt and chain management

### Dependencies

- [ ] Task 1: Class skeleton and constructor
- [ ] Task 2: getOrCreateChain basic implementation (refine in this task)
- [ ] Story 9-6: AgentPromptRegistry.render()
- [ ] Story 9-5: ProviderChain constructor signature

## Testing Strategy

### Unit Tests -- getPrompt()

- [ ] Test: `getPrompt('architect', 'claude-code', { context: 'project info' })` delegates to `promptRegistry.render('architect', 'claude-code', { context: 'project info' })`
- [ ] Test: `getPrompt('implementer', 'openrouter')` with no vars passes empty object `{}`
- [ ] Test: return value matches what `promptRegistry.render()` returns
- [ ] Test: vars object values are sanitized — `{{` and `}}` stripped before passing to render
- [ ] Test: vars with `{{ malicious }}` content are cleaned to ` malicious ` before render
- [ ] Test: vars without template syntax are passed through unchanged

### Unit Tests -- getOrCreateChain()

- [ ] Test: first call for a role creates a new ProviderChain
- [ ] Test: second call for same role returns the cached ProviderChain (same instance)
- [ ] Test: calls for different roles create different ProviderChain instances
- [ ] Test: role with `providerChain: [{ provider: 'openrouter', model: 'z-ai/z1-mini' }]` uses role entries
- [ ] Test: role with `providerChain: []` (empty array) falls back to `defaults.providerChain`
- [ ] Test: role with no `providerChain` field (undefined) falls back to `defaults.providerChain`
- [ ] Test: role with no entry in `roles` map falls back to `defaults.providerChain`
- [ ] Test: `getOrCreateChain('__proto__')` throws forbidden role key error
- [ ] Test: `getOrCreateChain('constructor')` throws forbidden role key error
- [ ] Test: ProviderChain constructor called with `ProviderChainOptions` object `{ entries, factory, health, diagnostics, costTracker, logger }`

### Mock Setup

```typescript
const mockPromptRegistry = {
  render: vi.fn().mockReturnValue('You are an architect. Analyze this issue.'),
} as unknown as IAgentPromptRegistry;

// Track ProviderChain construction
vi.mock('./provider-chain.js', () => ({
  ProviderChain: vi.fn().mockImplementation(() => ({
    getProvider: vi.fn().mockResolvedValue(mockProvider),
  })),
}));
```

### Validation Steps

1. [ ] Implement getPrompt delegation
2. [ ] Finalize getOrCreateChain with empty-array guard
3. [ ] Write getPrompt delegation tests
4. [ ] Write chain caching tests
5. [ ] Write empty-array fallback test (critical edge case)
6. [ ] Write undefined/null providerChain fallback tests
7. [ ] Verify ProviderChain constructor arguments
8. [ ] Verify TypeScript compilation

## Notes & Considerations

- **Empty array pitfall**: `[] ?? defaultValue` evaluates to `[]` because `??` only triggers for `null`/`undefined`, NOT for falsy values like empty arrays. This is explicitly called out in the story spec and must be handled with a `.length` check.
- **Chain caching**: The `Map<AgentType, IProviderChain>` cache is keyed by role. This means if the resolver is asked for the same role twice, it reuses the same chain. This is correct because the chain configuration does not change at runtime.
- **ProviderChain constructor**: The constructor takes a `ProviderChainOptions` object `{ entries, factory, health, diagnostics, costTracker, logger }` — NOT positional parameters. See Story 9-5 for the `ProviderChainOptions` type definition.
- **getPrompt is synchronous**: Unlike getAgentForRole, getPrompt does not do any async work. It sanitizes variable values (strips `{{` and `}}`) then delegates to the registry's render method.
- **Template injection prevention**: Variable values containing `{{` or `}}` are stripped before rendering to prevent recursive template expansion attacks.
- **Prototype pollution prevention**: `getOrCreateChain()` rejects forbidden role keys (`__proto__`, `constructor`, `prototype`) before accessing role-keyed config.
- **Non-null assertion**: `this.chains.get(role)!` is safe because we just set it in the `if` block above.

## Completion Checklist

- [ ] `getPrompt()` sanitizes variable values (strips `{{` and `}}`) then delegates to `promptRegistry.render()`
- [ ] `getOrCreateChain()` rejects forbidden keys before creating/caching ProviderChain per role
- [ ] Empty array `providerChain: []` falls back to defaults
- [ ] Undefined/null providerChain falls back to defaults
- [ ] Non-empty providerChain used directly
- [ ] ProviderChain constructor called with `ProviderChainOptions` object (not positional params)
- [ ] Chain caching verified (same instance returned)
- [ ] All getPrompt tests passing (including sanitization)
- [ ] All chain management tests passing
- [ ] Forbidden key rejection tests passing
- [ ] TypeScript strict mode compilation verified
