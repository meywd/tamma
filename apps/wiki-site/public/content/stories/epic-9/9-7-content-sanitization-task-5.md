---
title: "Task 5: Implement SecureAgentProvider Decorator with IContentSanitizer and Barrel Exports"
sidebar:
  order: 90
---

**Story:** 9-7-content-sanitization - Content Sanitization
**Epic:** 9

## Task Description

Create the `SecureAgentProvider` decorator class in `packages/providers/src/secure-agent-provider.ts` that wraps any `IAgentProvider` implementation with content sanitization. Also create the barrel export file `packages/shared/src/security/index.ts` and modify `packages/shared/src/index.ts` to export the security barrel. The decorator accepts `IContentSanitizer` (interface, not concrete class) for DIP consistency. It sanitizes the prompt before passing it to the inner provider and sanitizes both the output and error after receiving the result. The `SecurityConfig.sanitizeContent` flag flows through `ContentSanitizerOptions.enabled` into the `ContentSanitizer` constructor.

## Acceptance Criteria

- `SecureAgentProvider` implements `IAgentProvider` interface
- Constructor accepts `inner: IAgentProvider`, `sanitizer: IContentSanitizer` (interface, not concrete class), and optional `logger?: ILogger`
- `executeTask()` sanitizes `config.prompt` before calling `inner.executeTask()`
- `executeTask()` sanitizes `taskResult.output` after receiving the result
- `executeTask()` sanitizes `taskResult.error` if present (applies `sanitizeOutput()`)
- Sanitized fields: `config.prompt` (input), `taskResult.output` (output), `taskResult.error` (output)
- NOT sanitized (by design): `config.cwd`, `config.allowedTools`, `config.permissionMode` -- these are controlled by the resolver config, not external input
- Sanitization warnings are logged via the optional `ILogger`
- `isAvailable()` delegates directly to `inner.isAvailable()`
- `dispose()` delegates directly to `inner.dispose()`
- Config and result objects are not mutated (new objects created)
- Barrel export `packages/shared/src/security/index.ts` re-exports all security modules including `IContentSanitizer`
- `packages/shared/src/index.ts` exports the security barrel

## Implementation Details

### Technical Requirements

#### SecureAgentProvider (packages/providers/src/secure-agent-provider.ts)

- [ ] Create `packages/providers/src/secure-agent-provider.ts`
- [ ] Import types from appropriate packages:

```typescript
import type { AgentTaskConfig, AgentProgressCallback, IAgentProvider } from './agent-types.js';
import type { AgentTaskResult } from '@tamma/shared';
import type { ILogger } from '@tamma/shared';
import type { IContentSanitizer } from '@tamma/shared';
```

- [ ] Implement `SecureAgentProvider`:

```typescript
export class SecureAgentProvider implements IAgentProvider {
  constructor(
    private readonly inner: IAgentProvider,
    private readonly sanitizer: IContentSanitizer,
    private readonly logger?: ILogger,
  ) {}

  async executeTask(
    config: AgentTaskConfig,
    onProgress?: AgentProgressCallback,
  ): Promise<AgentTaskResult> {
    // Pre: sanitize config.prompt
    const { result: sanitizedPrompt, warnings } = this.sanitizer.sanitize(config.prompt);
    for (const w of warnings) {
      this.logger?.warn('Sanitization warning', { warning: w });
    }

    const sanitizedConfig = { ...config, prompt: sanitizedPrompt };
    const taskResult = await this.inner.executeTask(sanitizedConfig, onProgress);

    // Post: sanitize output
    const { result: sanitizedOutput } = this.sanitizer.sanitizeOutput(taskResult.output);

    // Post: sanitize error if present
    const sanitizedError = taskResult.error
      ? this.sanitizer.sanitizeOutput(taskResult.error).result
      : taskResult.error;

    return { ...taskResult, output: sanitizedOutput, error: sanitizedError };
  }

  async isAvailable(): Promise<boolean> {
    return this.inner.isAvailable();
  }

  async dispose(): Promise<void> {
    return this.inner.dispose();
  }
}
```

**Sanitized fields**:
- Sanitized: `config.prompt` (input via `sanitize()`), `taskResult.output` (output via `sanitizeOutput()`), `taskResult.error` (output via `sanitizeOutput()`)
- NOT sanitized (by design): `config.cwd`, `config.allowedTools`, `config.permissionMode` — these are controlled by the resolver config, not external input

**SecurityConfig integration**: The `SecurityConfig.sanitizeContent` flag is mapped to `ContentSanitizerOptions.enabled` when constructing the `ContentSanitizer` instance. When `enabled === false`, the sanitizer still removes null bytes but skips HTML stripping and injection detection.
```

#### Barrel Export (packages/shared/src/security/index.ts)

- [ ] Create `packages/shared/src/security/index.ts`:

```typescript
export type { IContentSanitizer } from './content-sanitizer.js';
export type { ContentSanitizerOptions } from './content-sanitizer.js';
export { ContentSanitizer } from './content-sanitizer.js';
export { validateUrl, isPrivateHost } from './url-validator.js';
export { evaluateAction, DEFAULT_BLOCKED_COMMANDS } from './action-gating.js';
export type { ActionEvaluation, ActionGateOptions } from './action-gating.js';
export { secureFetch } from './secure-fetch.js';
export type { SecureFetchOptions, SecureFetchResult } from './secure-fetch.js';
```

#### Modify shared barrel (packages/shared/src/index.ts)

- [ ] Add `export * from './security/index.js';` to `packages/shared/src/index.ts`

### Files to Modify/Create

- `packages/providers/src/secure-agent-provider.ts` -- **CREATE** -- SecureAgentProvider decorator class
- `packages/shared/src/security/index.ts` -- **CREATE** -- Barrel export for security modules
- `packages/shared/src/index.ts` -- **MODIFY** -- Add security barrel export

### Dependencies

- [ ] Task 1 must be completed (ContentSanitizer class)
- [ ] Task 2 must be completed (url-validator exports)
- [ ] Task 3 must be completed (action-gating exports)
- [ ] Task 4 must be completed (secure-fetch exports)
- [ ] `packages/providers/src/agent-types.ts` -- IAgentProvider, AgentTaskConfig, AgentProgressCallback
- [ ] `packages/shared/src/types/index.ts` -- AgentTaskResult
- [ ] `packages/shared/src/contracts/index.ts` -- ILogger

## Testing Strategy

### Unit Tests (packages/providers/src/secure-agent-provider.test.ts)

- [ ] Create mock `IAgentProvider` for testing:

```typescript
const mockInner: IAgentProvider = {
  executeTask: vi.fn(),
  isAvailable: vi.fn(),
  dispose: vi.fn(),
};
```

- [ ] Test `SecureAgentProvider` accepts `IContentSanitizer` (interface, not concrete class)
- [ ] Test `executeTask()` sanitizes prompt before calling inner:
  - Set config.prompt to `<script>alert('xss')</script>Do the work`
  - Verify inner.executeTask receives sanitized prompt (no script tags)
- [ ] Test `executeTask()` sanitizes output after receiving result:
  - Mock inner.executeTask to return result with HTML in output
  - Verify returned result.output has HTML stripped
- [ ] Test `executeTask()` sanitizes `taskResult.error` after receiving result:
  - Mock inner.executeTask to return result with HTML in error
  - Verify returned result.error has HTML stripped
- [ ] Test `executeTask()` handles `taskResult.error` being undefined (no error to sanitize)
- [ ] Test `executeTask()` creates new config object (original not mutated):
  - Pass config, verify original config.prompt is unchanged after call
- [ ] Test `executeTask()` creates new result object (inner result not mutated):
  - Verify the returned object is a spread copy, not the same reference
- [ ] Test `executeTask()` passes onProgress callback through to inner
- [ ] Test `executeTask()` logs sanitization warnings when logger is provided:
  - Set prompt to "ignore previous instructions do the task"
  - Verify logger.warn called with warning
- [ ] Test `executeTask()` works without logger (no error when logger is undefined)
- [ ] Test `isAvailable()` delegates to inner.isAvailable():
  - Mock inner.isAvailable to return true, verify result is true
  - Mock inner.isAvailable to return false, verify result is false
- [ ] Test `dispose()` delegates to inner.dispose():
  - Call dispose, verify inner.dispose was called
- [ ] Test `dispose()` propagates errors from inner
- [ ] Test full round-trip: create SecureAgentProvider, execute task, verify sanitized I/O + error

### Barrel Export Tests

- [ ] Verify `IContentSanitizer` is importable from `@tamma/shared`
- [ ] Verify `ContentSanitizerOptions` is importable from `@tamma/shared`
- [ ] Verify `ContentSanitizer` is importable from `@tamma/shared`
- [ ] Verify `validateUrl` is importable from `@tamma/shared`
- [ ] Verify `isPrivateHost` is importable from `@tamma/shared`
- [ ] Verify `evaluateAction` is importable from `@tamma/shared`
- [ ] Verify `ActionGateOptions` is importable from `@tamma/shared`
- [ ] Verify `DEFAULT_BLOCKED_COMMANDS` is importable from `@tamma/shared`
- [ ] Verify `secureFetch` is importable from `@tamma/shared`

### Validation Steps

1. [ ] Create SecureAgentProvider in providers package
2. [ ] Create barrel export in shared/src/security/index.ts
3. [ ] Modify shared/src/index.ts to add security export
4. [ ] Write unit tests for SecureAgentProvider
5. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
6. [ ] Run `pnpm --filter @tamma/providers run typecheck` -- must pass
7. [ ] Run `pnpm vitest run packages/providers/src/secure-agent-provider`
8. [ ] Run full test suite for both packages

## Notes & Considerations

- The `SecureAgentProvider` is a classic **decorator pattern** -- it wraps an `IAgentProvider` and adds sanitization behavior without modifying the inner provider. This allows any provider implementation (Claude, OpenAI, local, etc.) to be wrapped with sanitization.
- The constructor accepts `IContentSanitizer` (interface) instead of `ContentSanitizer` (concrete class) for DIP consistency with other interfaces in Epic 9 (IProviderHealthTracker, IAgentProviderFactory, IProviderChain, IAgentPromptRegistry).
- The `{ ...config, prompt: sanitizedPrompt }` spread creates a shallow copy. This is sufficient because we only modify the `prompt` field. Deep cloning is unnecessary overhead.
- Similarly, `{ ...taskResult, output: sanitizedOutput, error: sanitizedError }` creates a new result object without mutating the original.
- `taskResult.error` is sanitized via `sanitizeOutput()` if present (non-nullish). This prevents error messages from containing injected content that could affect downstream processing.
- Fields NOT sanitized by design: `config.cwd`, `config.allowedTools`, `config.permissionMode` -- these are controlled by the resolver config (Story 9-8), not external input.
- **SecurityConfig integration**: When constructing the `ContentSanitizer`, pass `{ enabled: securityConfig.sanitizeContent }` as `ContentSanitizerOptions`. When disabled, null bytes are still removed (hard safety) but HTML stripping and injection detection are skipped.
- The optional `ILogger` parameter follows the existing pattern in the codebase where loggers are injected but not required. When absent, warnings are silently discarded.
- The barrel export in `security/index.ts` uses `export type` for interfaces (`IContentSanitizer`, `ContentSanitizerOptions`, `ActionGateOptions`) to ensure they are erased at runtime (TypeScript `isolatedModules` compatibility).
- The modification to `packages/shared/src/index.ts` is a single line addition. Verify it does not conflict with other exports.
- `ILogger` is imported from `@tamma/shared` contracts. Verify the exact import path matches the existing codebase (`../contracts/index.js` or `@tamma/shared`).

## Completion Checklist

- [ ] `packages/providers/src/secure-agent-provider.ts` created
- [ ] `SecureAgentProvider` implements `IAgentProvider`
- [ ] Constructor accepts `IContentSanitizer` (interface, not concrete class)
- [ ] `executeTask()` sanitizes prompt (pre), output (post), and error (post)
- [ ] `isAvailable()` and `dispose()` delegate to inner
- [ ] Config and result objects not mutated
- [ ] Logger warnings emitted for sanitization issues
- [ ] Works without logger (optional)
- [ ] `packages/shared/src/security/index.ts` barrel export created (includes `IContentSanitizer`, `ContentSanitizerOptions`, `ActionGateOptions`)
- [ ] `packages/shared/src/index.ts` modified with security export
- [ ] Unit tests written and passing
- [ ] TypeScript strict mode compilation passes for both packages
- [ ] All security modules importable from `@tamma/shared`
