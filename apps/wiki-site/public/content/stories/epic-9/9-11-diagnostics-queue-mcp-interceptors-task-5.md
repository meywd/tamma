---
title: "Task 5: Modify MCPClient.invokeTool() for Interceptor Chain Support"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Modify `packages/mcp-client/src/client.ts` to support the `ToolInterceptorChain`. Add a private `interceptorChain` field, a public `setInterceptorChain()` method, and integrate `runPre()`/`runPost()` calls into the existing `invokeTool()` method. The constructor signature must not change -- the chain is set via a setter method for backward compatibility.

## Acceptance Criteria

- `MCPClient` has a private `interceptorChain?: ToolInterceptorChain` field
- Preferred: accept the interceptor chain in `MCPClientOptions` at construction time to avoid mutability concerns (F04). Alternatively, `MCPClient.setInterceptorChain(chain: ToolInterceptorChain): void` sets the chain.
- `invokeTool()` calls `interceptorChain.runPre()` after argument validation and before the `tool:invoked` event emit, using the intercepted args for execution
- `invokeTool()` logs pre-interceptor warnings via `this.logger?.warn('Pre-interceptor warnings', { toolName, warnings })` (F14)
- `invokeTool()` calls `interceptorChain.runPost()` after result construction and before the `tool:completed` event emit, using the intercepted result for the return value
- `invokeTool()` logs post-interceptor warnings via `this.logger?.warn('Post-interceptor warnings', { toolName, warnings })` (F14)
- When no interceptor chain is set, `invokeTool()` behaves exactly as before (backward compatible)
- Either add `setInterceptorChain()` to `IMCPClient` interface or accept chain in `MCPClientOptions` -- the preferred approach is `MCPClientOptions` (F04)

## Implementation Details

### Technical Requirements

- [ ] Add import for `ToolInterceptorChain` from `./interceptors.js` to `packages/mcp-client/src/client.ts`
- [ ] Add private field to `MCPClient` class:
  ```typescript
  private interceptorChain?: ToolInterceptorChain;
  ```
- [ ] Preferred: add `interceptorChain?: ToolInterceptorChain` to `MCPClientOptions` (F04). Alternatively add public setter:
  ```typescript
  setInterceptorChain(chain: ToolInterceptorChain): void {
    this.interceptorChain = chain;
  }
  ```
- [ ] Modify `invokeTool()` method -- integrate pre-interceptors after argument validation:
  ```typescript
  // After: validateToolArguments(argsWithDefaults, tool.inputSchema, toolName);
  // Before: this.eventEmitter.emit('tool:invoked', ...);

  let finalArgs = argsWithDefaults;
  if (this.interceptorChain) {
    const { args: intercepted, warnings: preWarnings } = await this.interceptorChain.runPre(toolName, argsWithDefaults);
    finalArgs = intercepted;
    if (preWarnings.length > 0) {
      this.logger?.warn('Pre-interceptor warnings', { toolName, warnings: preWarnings });
    }
  }

  this.eventEmitter.emit('tool:invoked', { serverName, toolName, args: finalArgs });
  ```
- [ ] Modify `invokeTool()` method -- use `finalArgs` in the retry execution:
  ```typescript
  // Change: connection.invokeTool(toolName, argsWithDefaults, options?.timeout)
  // To:     connection.invokeTool(toolName, finalArgs, options?.timeout)
  ```
- [ ] Modify `invokeTool()` method -- integrate post-interceptors after result construction:
  ```typescript
  // After: const toolResult: ToolResult = { ... };
  // Before: this.eventEmitter.emit('tool:completed', ...);

  let finalResult = toolResult;
  if (this.interceptorChain) {
    const { result: intercepted, warnings: postWarnings } = await this.interceptorChain.runPost(toolName, toolResult);
    finalResult = intercepted;
    if (postWarnings.length > 0) {
      this.logger?.warn('Post-interceptor warnings', { toolName, warnings: postWarnings });
    }
  }

  this.eventEmitter.emit('tool:completed', {
    serverName,
    toolName,
    success: finalResult.success,
    latencyMs,
  });

  // ... audit logging ...

  return finalResult;
  ```

### Files to Modify/Create

- `packages/mcp-client/src/client.ts` -- **MODIFY** -- Add interceptor chain field, setter, and invokeTool integration

### Code Location Reference

The `invokeTool()` method is at line 282 of `packages/mcp-client/src/client.ts`. Key insertion points:

- **Pre-interceptor insertion**: After line 307 (`validateToolArguments(...)`) and before line 315 (`this.eventEmitter.emit('tool:invoked', ...)`)
- **Post-interceptor insertion**: After line 352 (construction of `toolResult` object) and before line 354 (`this.eventEmitter.emit('tool:completed', ...)`)
- **finalArgs usage**: Change line 324 (`connection.invokeTool(toolName, argsWithDefaults, ...)`) to use `finalArgs`

### Dependencies

- [ ] Task 3 must be completed first (interceptors.ts with ToolInterceptorChain must exist)

## Testing Strategy

### Unit Tests

- [ ] Test `setInterceptorChain()` sets the chain on the client
- [ ] Test `invokeTool()` without interceptor chain works unchanged (backward compatibility)
- [ ] Test `invokeTool()` with interceptor chain calls `runPre()` before execution
- [ ] Test `invokeTool()` with interceptor chain calls `runPost()` after execution
- [ ] Test pre-interceptor modified args are used for actual tool execution
- [ ] Test post-interceptor modified result is returned from `invokeTool()`
- [ ] Test `tool:invoked` event is emitted with intercepted args (not original args)
- [ ] Test `tool:completed` event reflects post-intercepted result's success status
- [ ] Test interceptor chain errors propagate (are not swallowed by invokeTool)
- [ ] Test pre-interceptor runs after argument validation (validation uses original schema, not intercepted args)
- [ ] Test pre-interceptor warnings are logged via `this.logger?.warn()` (F14)
- [ ] Test post-interceptor warnings are logged via `this.logger?.warn()` (F14)

### Validation Steps

1. [ ] Add import and field to client.ts
2. [ ] Add setter method
3. [ ] Modify invokeTool() to integrate pre/post interceptors
4. [ ] Run `pnpm --filter @tamma/mcp-client run typecheck` -- must pass
5. [ ] Run existing test suite `pnpm --filter @tamma/mcp-client test` -- all existing tests must still pass
6. [ ] Add new tests for interceptor chain integration

## Notes & Considerations

- The preferred approach is to accept the interceptor chain in `MCPClientOptions` at construction time to avoid mutability concerns (F04). The setter approach is also supported for backward compatibility.
- Either add `setInterceptorChain()` to `IMCPClient` or use `MCPClientOptions` -- the preferred path for new integrations is `MCPClientOptions` (F04).
- Interceptor warnings (from both `runPre` and `runPost`) are logged via `this.logger?.warn()` to ensure visibility into sanitization actions, blocked URLs, and interceptor failures (F14). This replaces the earlier pattern where warnings were silently discarded.
- Pre-interceptor runs AFTER schema validation but BEFORE the `tool:invoked` event. This means validation catches schema errors on the original args, but the `tool:invoked` event and the actual execution use the intercepted args.
- Post-interceptor runs AFTER result construction but BEFORE the `tool:completed` event and return. This means the returned result and the event both reflect the post-intercepted state.
- In the error path (catch block), post-interceptors do NOT run. The error is thrown directly. This is consistent with the design: post-interceptors transform results, not errors.
- The audit logger continues to receive the raw server/tool names and the intercepted success status. The audit log does not record interceptor warnings -- that is the caller's responsibility.

## Completion Checklist

- [ ] `ToolInterceptorChain` imported in client.ts
- [ ] `interceptorChain` private field added
- [ ] Interceptor chain accepted via `MCPClientOptions` or `setInterceptorChain()` (F04)
- [ ] Pre-interceptor integration in `invokeTool()` with warning logging (F14)
- [ ] Post-interceptor integration in `invokeTool()` with warning logging (F14)
- [ ] `finalArgs` used for tool execution
- [ ] `finalResult` used for return value
- [ ] Backward compatible -- no interceptor chain means no change in behavior
- [ ] `IMCPClient` interface updated or `MCPClientOptions` extended (F04)
- [ ] TypeScript strict mode compilation passes
- [ ] Existing tests still pass
