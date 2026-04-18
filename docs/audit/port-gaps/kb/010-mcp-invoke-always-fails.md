# Finding 010: `McpManagementService.invokeTool` always returns `success: false, error: "MCP client not configured"`

**Scope**: kb
**Severity**: P1 (orchestrator tool-use path broken in production)
**Status**: Not-yet-implemented (MCP client never constructed in sidecar)
**Estimated port effort**: 3-4h (depends on MCP client bootstrap)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/MCPManagementService.ts`.

```typescript
// packages/api/src/services/knowledge-base/MCPManagementService.ts (9e9a57c~1)
async invokeTool(request: MCPToolInvokeRequest): Promise<MCPToolInvokeResult> {
  if (!this.client) {
    throw new Error('MCP client is not configured');
  }

  const startTime = Date.now();

  try {
    const result = await this.client.invokeTool(
      request.serverName,
      request.toolName,
      request.arguments,
    );

    const durationMs = Date.now() - startTime;

    const invokeResult: MCPToolInvokeResult = {
      success: result.success,
      content: result.content,
      durationMs,
    };
    if (result.error) {
      invokeResult.error = result.error;
    }
    return invokeResult;
  } catch (error) {
    return {
      success: false,
      content: null,
      error: error instanceof Error ? error.message : String(error),
      durationMs: Date.now() - startTime,
    };
  }
}
```

The TS service:
- Threw an explicit `Error` when no client was configured (consistent with #009).
- Caught errors from the real client and converted to `{ success: false, error: <msg>, durationMs }` — so transient MCP failures yielded structured responses.

- Dependencies: `IMCPClientService` from `packages/mcp-client/src/types.ts`.
- Tests: `packages/api/src/__tests__/services/knowledge-base/MCPManagementService.test.ts`.

## 2. What's in C#

### C# side

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:157-161 (current)
public static async Task<IResult> InvokeMcpTool(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] McpInvokeRequest body,
    CancellationToken ct)
    => Results.Ok(await client.InvokeMcpToolAsync(body, ct));
```

### Sidecar side

```typescript
// packages/intelligence-server/src/services/McpManagementService.ts:163-190 (current)
async invokeTool(req: McpInvokeRequest): Promise<McpInvokeResponse> {
  if (!this.client) {
    return {
      success: false,
      content: null,
      error: 'MCP client not configured',
      durationMs: 0,
    };
  }
  const start = Date.now();
  try {
    const result = await this.client.invokeTool(req.serverName, req.toolName, req.arguments);
    const res: McpInvokeResponse = {
      success: result.success,
      content: result.content,
      durationMs: Date.now() - start,
    };
    if (result.error) res.error = result.error;
    return res;
  } catch (err) {
    return {
      success: false,
      content: null,
      error: err instanceof Error ? err.message : String(err),
      durationMs: Date.now() - start,
    };
  }
}
```

- Dependencies: `IMcpClient` (narrow type). In production, `this.client` is always null because the composition root never constructs an MCP client instance (see #001, #004).
- Tests: `packages/intelligence-server/src/__tests__/services/McpManagementService.test.ts` — asserts on the "not configured" response.

Notably, unlike #009 (start/stop) where the stub path returns a success message, `invokeTool`'s stub path is at least **honest**: `success: false`. The caller knows the call didn't work. But the result is still that **every** MCP tool invocation fails on the deployed system.

## 3. The gap

- TS did: throw `Error('MCP client is not configured')` on null; on configured client, return structured `MCPToolInvokeResult` with success/error/content/durationMs.
- C# + sidecar does: same `success: false` response every time — across all tool names, all arguments, all servers. The error string "MCP client not configured" is a clear signal BUT since C#'s `IntelligenceHttpClient` returns 200 for 2xx sidecar responses, the caller has no HTTP-level error.

For the orchestrator's agentic tool loop (Epic 12) calling `POST /api/kb/mcp/tools/invoke`:
- TS: tool invocation succeeded (when configured). Orchestrator integrated tool output into the next LLM call.
- C# + sidecar: every tool call returns `success: false`. The orchestrator either (a) detects the failure and retries / falls back, or (b) silently skips tool use.

This is the most consequential KB gap for the **self-maintenance goal** in CLAUDE.md: Tamma's own workflows depend on MCP tools (GitHub API, file operations). Without a wired MCP client, every tool-use step degrades to "pretend it worked".

Error paths:
- TS: `throw` when null → HTTP 500. Structured result when configured.
- C# + sidecar: HTTP 200 with `success: false` regardless. No distinction between "misconfigured" and "invoked and failed".

## 4. Gap from stories

`docs/stories/epic-6/story-6-4/6-4-mcp-client-integration.md`:

> - MCP tool discovery and invocation
> - Structured tool result reporting (success, content, error, duration)
> - Support multiple transport types (stdio, SSE, websocket)

And `docs/stories/epic-12/12-1-tool-executor-interface-and-registry.md` and `12-2-agentic-tool-loop-in-call-llm.md` both depend on MCP-tool availability for their test cases.

Story alignment:
- [x] Matches TS behavior (TS was closest to spec; C# + sidecar regresses)
- [ ] Matches C# behavior
- [ ] Describes a third behavior
- [ ] No story — well-spec'd across 6-4 and 12-1/2.

## 5. Status

- **Classification**: Not-yet-implemented. The MCP client is never constructed, so 100% of tool invocations return the configured-error.
- **What's needed to finish**:
  1. Sidecar composition root constructs an `IMcpClient` (e.g. from `@tamma/mcp-client`).
  2. Read MCP server config from file / env (common: `.tamma/mcp-servers.json` or per-tenant database records).
  3. Pass the constructed client to the bundle (`startServer({ services: { mcpClient: ... } })`).
  4. On null client, throw instead of returning `success: false` — aligns with TS contract.
- **Is it "just a stub" or is scope missing?** Scope is fully spec'd. Pure wiring gap — same pattern as #001.
- **Blockers**:
  - #001 (composition root).
  - MCP server discovery: how does the sidecar know which servers to connect to? Needs a config file contract (not yet documented).

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/adapters.ts` — add `createMcpClientFromEnv()`.
  - `packages/intelligence-server/src/server.ts` — wire into composition root.
  - `packages/intelligence-server/src/services/McpManagementService.ts:163-170` — throw instead of structured error on null.
- Files to create:
  - `packages/intelligence-server/src/mcp-config-loader.ts` — loads MCP server config from `.tamma/mcp-servers.json` or equivalent.
  - Story: `docs/stories/epic-6/story-6-4/6-4-mcp-composition-root.md` (backfill subtask).
- Tests to add:
  - `POST /kb/mcp/tools/invoke` with a real stdio MCP server (e.g. `@modelcontextprotocol/server-filesystem`) returns real content.
  - `POST /kb/mcp/tools/invoke` with a failing server returns `{ success: false, error: <transport error>, durationMs: N }`.
  - Sidecar starts cleanly even if MCP config file is absent (graceful degradation, log WARN).
- Estimated effort: 3-4h
  - Composition root + config loader: 2h
  - End-to-end test with real MCP server: 1-2h
  - Doc / story update: 0.5h

## References

- TS source: `packages/api/src/services/knowledge-base/MCPManagementService.ts:131-164` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/McpManagementService.ts:163-190`
- Story: `docs/stories/epic-6/story-6-4/6-4-mcp-client-integration.md`
- CLAUDE.md section: "Self-Maintenance Goal" — requires MCP tools for Tamma's own workflow autonomy.
- Related findings: #001, #002, #009
