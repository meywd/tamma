---
title: "Task 2: Implement getAgentForRole with Chain Creation and Security Wrapping"
sidebar:
  order: 90
---

**Story:** 9-8-role-based-agent-resolver - Role-Based Agent Resolver
**Epic:** 9

## Task Description

Implement the `getAgentForRole()` method which is the core resolution path. It creates (or retrieves from cache) a `ProviderChain` for the given role, obtains an `IAgentProvider` from the chain, and optionally wraps it with `SecureAgentProvider` if a content sanitizer is configured.

## Acceptance Criteria

- `getAgentForRole(role, context)` calls `getOrCreateChain(role)` to get/create a ProviderChain
- `chain.getProvider()` is called with `{ agentType: role, projectId, engineId }`
- Returned provider is wrapped with `SecureAgentProvider` when `this.sanitizer` is provided
- Returned provider is NOT wrapped when `this.sanitizer` is absent; a WARN log is emitted
- `SecureAgentProvider` constructor receives `(provider, sanitizer, logger)`
- `recordSuccess()` is NOT called here -- it is handled inside InstrumentedAgentProvider
- Debug logging emitted at entry with role and context

## Implementation Details

### Technical Requirements

- [ ] Implement `getAgentForRole(role: AgentType, context: { projectId: string; engineId: string }): Promise<IAgentProvider>`
- [ ] Call `this.getOrCreateChain(role)` to get the ProviderChain for the role
- [ ] Call `chain.getProvider({ agentType: role, ...context })` to get the InstrumentedAgentProvider
- [ ] Check `if (this.sanitizer)` -- if truthy, wrap with `new SecureAgentProvider(provider, this.sanitizer, this.logger)`
- [ ] Return the (optionally wrapped) provider
- [ ] Also implement the private `getOrCreateChain(role)` method (shared with Task 4, but needed here for getAgentForRole to work)

### Key Code

```typescript
async getAgentForRole(
  role: AgentType,
  context: { projectId: string; engineId: string },
): Promise<IAgentProvider> {
  this.logger?.debug('Resolving agent for role', { role, ...context });

  const chain = this.getOrCreateChain(role);

  // chain.getProvider() returns an InstrumentedAgentProvider.
  // recordSuccess() is handled inside InstrumentedAgentProvider on
  // task completion -- NOT here.
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
```

### Files to Modify

- `packages/providers/src/role-based-agent-resolver.ts` -- ADD: getAgentForRole() method and getOrCreateChain() private method
- `packages/providers/src/role-based-agent-resolver.test.ts` -- ADD: tests for getAgentForRole

### Dependencies

- [ ] Task 1: Class skeleton and constructor
- [ ] Story 9-5: ProviderChain.getProvider() returns InstrumentedAgentProvider
- [ ] Story 9-7: SecureAgentProvider decorator

## Testing Strategy

### Unit Tests

- [ ] Test: `getAgentForRole('implementer', ctx)` calls `getOrCreateChain('implementer')`
- [ ] Test: `chain.getProvider()` is called with `{ agentType: 'implementer', projectId: 'owner/repo', engineId: 'engine-1' }`
- [ ] Test: when sanitizer is provided, returned provider is an instance of SecureAgentProvider
- [ ] Test: when sanitizer is NOT provided, returned provider is the raw provider from chain.getProvider()
- [ ] Test: SecureAgentProvider receives the correct logger from options
- [ ] Test: fallback works when first provider fails (ProviderChain handles internally, but verify the error propagates if all fail)
- [ ] Test: `getAgentForPhase` -> `getAgentForRole` end-to-end flow works
- [ ] Test: `getAgentForRole` logs debug message with role and context when logger is provided
- [ ] Test: `getAgentForRole` logs warn when sanitizer is NOT provided and logger is available
- [ ] Test: `getAgentForRole` does not throw when logger is undefined (no sanitizer path)

### Mock Setup

```typescript
// Mock ProviderChain
const mockProvider = {
  executeTask: vi.fn(),
  isAvailable: vi.fn().mockResolvedValue(true),
  dispose: vi.fn(),
} as unknown as IAgentProvider;

const mockChain = {
  getProvider: vi.fn().mockResolvedValue(mockProvider),
} as unknown as IProviderChain;

// Mock factory to verify chain creation
const mockFactory = {
  create: vi.fn(),
} as unknown as IAgentProviderFactory;

// Mock sanitizer
const mockSanitizer = {
  sanitize: vi.fn(),
  sanitizeOutput: vi.fn(),
} as unknown as IContentSanitizer;
```

### Validation Steps

1. [ ] Implement getAgentForRole with chain resolution
2. [ ] Implement getOrCreateChain with ProviderChain creation
3. [ ] Add security wrapping conditional
4. [ ] Write tests for with-sanitizer path
5. [ ] Write tests for without-sanitizer path
6. [ ] Write tests for chain.getProvider context passing
7. [ ] Verify no recordSuccess() calls in resolver

## Notes & Considerations

- **No pooling**: Each `getAgentForRole()` may create a fresh provider via the chain. Subprocess-based providers (claude-code, opencode) are stateful and should not be shared across phases.
- **recordSuccess() responsibility**: Success recording happens inside `InstrumentedAgentProvider` when a task completes, NOT in the resolver. The resolver only obtains and optionally wraps the provider.
- **getOrCreateChain()**: This private method is also tested in Task 4 for the empty-array fallback edge case. In this task, focus on the basic creation and caching behavior.
- **SecureAgentProvider wrapping order**: The chain returns an `InstrumentedAgentProvider`, then `SecureAgentProvider` wraps that. So the call stack is: SecureAgentProvider -> InstrumentedAgentProvider -> actual provider.

## Completion Checklist

- [ ] `getAgentForRole()` implemented
- [ ] `getOrCreateChain()` implemented (basic version)
- [ ] Security wrapping conditional works
- [ ] Context passed correctly to chain.getProvider()
- [ ] No recordSuccess() calls in resolver
- [ ] Debug logging at entry of getAgentForRole
- [ ] Warn logging when sanitizer is absent
- [ ] All security wrapping tests passing
- [ ] All provider resolution tests passing
- [ ] TypeScript strict mode compilation verified
