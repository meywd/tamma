---
title: "Task 3: Create ToolInterceptorChain with Pre/Post Interceptors"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Create `packages/mcp-client/src/interceptors.ts` with the `ToolInterceptorChain` class and the `PreInterceptor`/`PostInterceptor` type definitions. The chain provides blocking (awaited) pre and post transformation hooks for MCP tool calls. This is Part B of the story. The interceptor chain has no queue, no processor, and no dependency on `@tamma/shared` telemetry.

## Acceptance Criteria

- `PreInterceptor` type is `(toolName: string, args: Record<string, unknown>) => Promise<{ args: Record<string, unknown>; warnings: string[] }>`
- `PostInterceptor` type is `(toolName: string, result: ToolResult) => Promise<{ result: ToolResult; warnings: string[] }>`
- `ToolInterceptorChain` has `addPreInterceptor()` and `addPostInterceptor()` methods
- `runPre()` awaits each pre-interceptor in registration order, piping args through (output of one is input to next), with try/catch per interceptor (F09)
- `runPost()` awaits each post-interceptor in registration order, piping result through, with try/catch per interceptor (F09)
- On interceptor error: fail-open (continue with unmodified args/result, add warning). Security-critical interceptors should use fail-closed at the application level (F09)
- Interceptor return values are validated for prototype pollution keys (`__proto__`, `constructor`, `prototype`) and those keys are removed with a warning (F16)
- Warnings from all interceptors (and from error isolation/pollution checks) are collected and returned as a flat array
- Empty chain is a no-op passthrough: `runPre()` returns original args with no warnings, `runPost()` returns original result with no warnings

## Implementation Details

### Technical Requirements

- [ ] Create `packages/mcp-client/src/interceptors.ts`
  - [ ] Import `ToolResult` from `./types.js` using `import type`
  - [ ] Define `PreInterceptor` type alias:
    ```typescript
    export type PreInterceptor = (
      toolName: string,
      args: Record<string, unknown>
    ) => Promise<{ args: Record<string, unknown>; warnings: string[] }>;
    ```
  - [ ] Define `PostInterceptor` type alias:
    ```typescript
    export type PostInterceptor = (
      toolName: string,
      result: ToolResult
    ) => Promise<{ result: ToolResult; warnings: string[] }>;
    ```
  - [ ] Implement `ToolInterceptorChain` class:
    - Private fields: `preInterceptors: PreInterceptor[]`, `postInterceptors: PostInterceptor[]`
    - `addPreInterceptor(fn: PreInterceptor): void` -- pushes to array
    - `addPostInterceptor(fn: PostInterceptor): void` -- pushes to array
    - `async runPre(toolName: string, args: Record<string, unknown>): Promise<{ args: Record<string, unknown>; warnings: string[] }>`:
      - Iterates `preInterceptors` in order
      - Each interceptor is wrapped in try/catch (F09):
        - On success: validate returned args for prototype pollution keys (`__proto__`, `constructor`, `prototype`), remove them with a warning (F16); replace current args with interceptor output
        - On error: add warning with error message, continue with unmodified args (fail-open)
      - Collects warnings from all interceptors
      - Returns final args and accumulated warnings
    - `async runPost(toolName: string, result: ToolResult): Promise<{ result: ToolResult; warnings: string[] }>`:
      - Iterates `postInterceptors` in order
      - Each interceptor is wrapped in try/catch (F09):
        - On success: replace current result with interceptor output
        - On error: add warning with error message, continue with unmodified result (fail-open)
      - Collects warnings from all interceptors
      - Returns final result and accumulated warnings

### Files to Modify/Create

- `packages/mcp-client/src/interceptors.ts` -- **CREATE** -- Interceptor types and chain class

### Dependencies

- [ ] `packages/mcp-client/src/types.ts` must export `ToolResult` (already does)
- [ ] No dependency on `@tamma/shared` telemetry or `@tamma/cost-monitor`

## Testing Strategy

### Unit Tests

- [ ] Test `addPreInterceptor()` adds interceptor to chain
- [ ] Test `addPostInterceptor()` adds interceptor to chain
- [ ] Test `runPre()` with empty chain returns original args and empty warnings
- [ ] Test `runPost()` with empty chain returns original result and empty warnings
- [ ] Test single pre-interceptor modifies args and returns warnings
- [ ] Test single post-interceptor modifies result and returns warnings
- [ ] Test multiple pre-interceptors run in registration order, each receiving output of previous
- [ ] Test multiple post-interceptors run in registration order, each receiving output of previous
- [ ] Test warnings from multiple interceptors are accumulated in a flat array
- [ ] Test interceptor receives correct `toolName` parameter
- [ ] Test `runPre()` awaits async interceptors (not fire-and-forget)
- [ ] Test `runPost()` awaits async interceptors (not fire-and-forget)
- [ ] Test `runPre()` catches interceptor error, adds warning, continues with unmodified args (F09)
- [ ] Test `runPost()` catches interceptor error, adds warning, continues with unmodified result (F09)
- [ ] Test `runPre()` strips prototype pollution keys (`__proto__`, `constructor`, `prototype`) from returned args (F16)
- [ ] Test `runPre()` adds warning when prototype pollution key is stripped (F16)

### Validation Steps

1. [ ] Create the interceptors file
2. [ ] Run `pnpm --filter @tamma/mcp-client run typecheck` -- must pass
3. [ ] Write unit tests in `packages/mcp-client/src/interceptors.test.ts` (created in Task 6)
4. [ ] Run `pnpm vitest run packages/mcp-client/src/interceptors`

## Notes & Considerations

- The interceptors are **blocking** (awaited), unlike `DiagnosticsQueue.emit()` which is fire-and-forget. This is intentional because interceptors transform data that the tool execution depends on.
- The chain is a simple sequential pipeline, not a middleware stack. There is no "next()" callback pattern -- each interceptor receives the full args/result and returns a new version.
- Interceptors should not mutate input args or results. They should return new objects. However, this is a convention enforced by documentation, not by the chain itself (deep-cloning would add overhead).
- The `warnings` array allows interceptors to report non-fatal issues (e.g., "URL was rewritten", "content was sanitized") without throwing.
- Error handling in interceptors IS handled per-interceptor with try/catch (F09). Non-security interceptors (diagnostics, URL warnings) use fail-open behavior (continue with unmodified args/result). Security-critical interceptors (sanitization) should use fail-closed behavior at the application level where the tool call is aborted.
- Interceptor return values are validated for prototype pollution keys (`__proto__`, `constructor`, `prototype`). These forbidden keys are removed from returned args objects with a warning (F16).

## Completion Checklist

- [ ] `packages/mcp-client/src/interceptors.ts` created
- [ ] `PreInterceptor` type exported
- [ ] `PostInterceptor` type exported
- [ ] `ToolInterceptorChain` class exported with `addPreInterceptor()`, `addPostInterceptor()`, `runPre()`, `runPost()`
- [ ] `runPre()` pipes args through interceptors in order with per-interceptor try/catch (F09)
- [ ] `runPost()` pipes result through interceptors in order with per-interceptor try/catch (F09)
- [ ] Prototype pollution keys stripped from interceptor return values with warning (F16)
- [ ] Empty chain is a no-op passthrough
- [ ] Warnings accumulated from all interceptors (including error isolation and pollution warnings)
- [ ] TypeScript strict mode compilation passes
