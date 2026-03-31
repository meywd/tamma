---
title: "Task 5: Export All New Modules from Providers Index"
sidebar:
  order: 90
---

**Story:** 9-8-role-based-agent-resolver - Role-Based Agent Resolver
**Epic:** 9

## Task Description

Update the `packages/providers/src/index.ts` barrel export to include the `RoleBasedAgentResolver` class and its options type, along with all other Epic 9 modules that should be part of the public API. Verify that all exports compile correctly under strict TypeScript.

## Acceptance Criteria

- `RoleBasedAgentResolver` class exported from `packages/providers/src/index.ts`
- `IRoleBasedAgentResolver` interface type exported from `packages/providers/src/index.ts`
- `RoleBasedAgentResolverOptions` type exported from `packages/providers/src/index.ts`
- All Epic 9 Story 3-8 provider modules are exported (classes and interfaces)
- Interface re-exports: `IProviderChain`, `IAgentProviderFactory`, `IProviderHealthTracker`, `IAgentPromptRegistry`, `IContentSanitizer`
- `pnpm --filter @tamma/providers run typecheck` passes
- No circular dependency issues from new exports

## Implementation Details

### Technical Requirements

- [ ] Add export for `RoleBasedAgentResolver` class, `IRoleBasedAgentResolver` interface, and `RoleBasedAgentResolverOptions` type
- [ ] Add interface re-exports: `IProviderChain`, `IAgentProviderFactory`, `IProviderHealthTracker`, `IAgentPromptRegistry`, `IContentSanitizer`
- [ ] Verify and add exports for all Epic 9 modules created in Stories 3-8:
  - `errors.ts` -- `createProviderError`, `isProviderError` (Story 3)
  - `provider-health.ts` -- `ProviderHealthTracker` (Story 3)
  - `agent-provider-factory.ts` -- `AgentProviderFactory`, `wrapAsAgent` (Story 4)
  - `provider-chain.ts` -- `ProviderChain` (Story 5)
  - `agent-prompt-registry.ts` -- `AgentPromptRegistry` (Story 6)
  - `secure-agent-provider.ts` -- `SecureAgentProvider` (Story 7)
  - `instrumented-agent-provider.ts` -- `InstrumentedAgentProvider` (Story 2)
  - `role-based-agent-resolver.ts` -- `RoleBasedAgentResolver`, `RoleBasedAgentResolverOptions` (Story 8)

### Expected index.ts additions

```typescript
// Epic 9 exports (Stories 2-8) — classes
export { createProviderError, isProviderError } from './errors.js';
export { ProviderHealthTracker } from './provider-health.js';
export { AgentProviderFactory, wrapAsAgent } from './agent-provider-factory.js';
export { ProviderChain } from './provider-chain.js';
export { AgentPromptRegistry } from './agent-prompt-registry.js';
export { SecureAgentProvider } from './secure-agent-provider.js';
export { InstrumentedAgentProvider } from './instrumented-agent-provider.js';
export { RoleBasedAgentResolver } from './role-based-agent-resolver.js';

// Epic 9 exports — interfaces and types
export type { IRoleBasedAgentResolver, RoleBasedAgentResolverOptions } from './role-based-agent-resolver.js';
export type { IProviderChain } from './provider-chain.js';
export type { IAgentProviderFactory } from './agent-provider-factory.js';
export type { IProviderHealthTracker } from './types.js';
export type { IAgentPromptRegistry } from './agent-prompt-registry.js';
export type { IContentSanitizer } from '@tamma/shared/security';
```

### Files to Modify

- `packages/providers/src/index.ts` -- MODIFY: Add exports for all Epic 9 modules

### Dependencies

- [ ] Task 1-4: RoleBasedAgentResolver fully implemented
- [ ] Stories 2-7: All prerequisite modules implemented

## Testing Strategy

### Verification Tests

- [ ] Verify import: `import { RoleBasedAgentResolver } from '@tamma/providers'` resolves
- [ ] Verify import: `import type { IRoleBasedAgentResolver, RoleBasedAgentResolverOptions } from '@tamma/providers'` resolves
- [ ] Verify import: `import type { IProviderChain, IAgentProviderFactory, IProviderHealthTracker, IAgentPromptRegistry } from '@tamma/providers'` resolves
- [ ] Verify import: `import type { IContentSanitizer } from '@tamma/providers'` resolves
- [ ] Verify import: `import { ProviderChain, ProviderHealthTracker, AgentProviderFactory } from '@tamma/providers'` resolves
- [ ] Verify import: `import { AgentPromptRegistry, SecureAgentProvider } from '@tamma/providers'` resolves
- [ ] Verify import: `import { InstrumentedAgentProvider } from '@tamma/providers'` resolves
- [ ] Verify import: `import { createProviderError, isProviderError } from '@tamma/providers'` resolves

### Compilation Verification

- [ ] Run `pnpm --filter @tamma/providers run typecheck`
- [ ] Verify no circular dependency warnings
- [ ] Verify no duplicate identifier errors

### Validation Steps

1. [ ] Review current index.ts exports
2. [ ] Add all missing Epic 9 module exports
3. [ ] Use `export type` for type-only exports (RoleBasedAgentResolverOptions)
4. [ ] Run typecheck
5. [ ] Verify no circular dependencies

## Notes & Considerations

- **Export type vs export**: Use `export type` for interfaces and type aliases that have no runtime value. Use `export` for classes and functions.
- **Import order in index.ts**: Follow the existing pattern in the file. Group Epic 9 exports together with a comment.
- **Conditional exports**: Some modules may not exist yet if their prerequisite stories have not been implemented. Only add exports for modules that actually exist in the codebase at the time of implementation. The full list above is the target state.
- **Barrel export convention**: The `index.ts` uses `export { X } from './file.js'` pattern with `.js` extensions for ESM.
- **No re-export of internal types**: Types from `@tamma/shared` (like `AgentsConfig`, `WorkflowPhase`) are not re-exported from providers -- consumers import them directly from shared. Exception: `IContentSanitizer` is re-exported from `@tamma/shared/security` as a convenience since it is used as a constructor option.
- **Story 9-9 dependency**: The engine integration (Story 9-9) will import `RoleBasedAgentResolver` and `RoleBasedAgentResolverOptions` from `@tamma/providers`. This export must be in place before Story 9-9 can proceed.

## Completion Checklist

- [ ] `RoleBasedAgentResolver` exported
- [ ] `IRoleBasedAgentResolver` interface type exported
- [ ] `RoleBasedAgentResolverOptions` type exported
- [ ] Interface re-exports: `IProviderChain`, `IAgentProviderFactory`, `IProviderHealthTracker`, `IAgentPromptRegistry`, `IContentSanitizer`
- [ ] All Epic 9 Story 2-8 modules exported
- [ ] `.js` extensions used in all export paths
- [ ] `export type` used for type-only exports (interfaces and type aliases)
- [ ] TypeScript compilation passes
- [ ] No circular dependencies
- [ ] Exports organized with clear comments (classes separate from interfaces)
