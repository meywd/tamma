# Finding 009: `McpManagementService.startServer` / `stopServer` return "(stub)" strings

**Scope**: kb
**Severity**: P1 (user-visible regression vs TS throw)
**Status**: Behavioral drift (TS threw; sidecar returns success-with-debug-string)
**Estimated port effort**: 0.25h (subset of #002 fix)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/MCPManagementService.ts`.

```typescript
// packages/api/src/services/knowledge-base/MCPManagementService.ts (9e9a57c~1)
async startServer(name: string): Promise<void> {
  if (!this.client) {
    throw new Error(`MCP server not found: ${name}`);
  }

  await this.client.connectServer(name);

  this.appendLog(name, {
    timestamp: new Date().toISOString(),
    level: 'info',
    message: 'Server started successfully',
  });
}

async stopServer(name: string): Promise<void> {
  if (!this.client) {
    throw new Error(`MCP server not found: ${name}`);
  }

  await this.client.disconnectServer(name);

  this.appendLog(name, {
    timestamp: new Date().toISOString(),
    level: 'info',
    message: 'Server stopped',
  });
}
```

The TS service:
- Threw a typed error on null client with a name-scoped message.
- Emitted a log entry on success, recorded in an in-process `logs: Map<string, MCPServerLog[]>` so a later `GET /api/knowledge-base/mcp/servers/:name/logs` would return it.

- Dependencies: `IMCPClientService` from `packages/mcp-client/`.
- Tests: `packages/api/src/__tests__/services/knowledge-base/MCPManagementService.test.ts` asserted the error throw and the log-append side-effect.

## 2. What's in C#

### C# side
Two separate endpoints, each forwarding verbatim:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:128-138 (current)
public static async Task<IResult> StartMcpServer(
    [FromServices] IIntelligenceHttpClient client,
    string id,
    CancellationToken ct)
    => Results.Ok(await client.StartMcpServerAsync(id, ct));

public static async Task<IResult> StopMcpServer(
    [FromServices] IIntelligenceHttpClient client,
    string id,
    CancellationToken ct)
    => Results.Ok(await client.StopMcpServerAsync(id, ct));
```

### Sidecar side — stubs

```typescript
// packages/intelligence-server/src/services/McpManagementService.ts:111-125 (current)
async startServer(id: string): Promise<{ message: string }> {
  if (!this.client) {
    return { message: `MCP server ${id} start requested (stub)` };
  }
  await this.client.connectServer(id);
  return { message: `MCP server ${id} started` };
}

async stopServer(id: string): Promise<{ message: string }> {
  if (!this.client) {
    return { message: `MCP server ${id} stop requested (stub)` };
  }
  await this.client.disconnectServer(id);
  return { message: `MCP server ${id} stopped` };
}
```

- Dependencies: `IMcpClient` (narrow type from `packages/intelligence-server/src/types.ts`).
- Tests: `packages/intelligence-server/src/__tests__/services/McpManagementService.test.ts` asserts on the stub strings.

Also missing from the port: the log-append side-effect. Even when a real client is wired, `startServer` / `stopServer` in the sidecar do not record success logs. The sidecar has no `logs` map at all — the TS feature of in-process MCP server logs is dropped.

## 3. The gap

- TS did: throw `Error('MCP server not found: ${name}')` → HTTP 500 from the route layer.
- C# + sidecar does: respond HTTP 200 with `{ "message": "MCP server foo start requested (stub)" }`.

For a user clicking "Start GitHub MCP server" in the dashboard:
- TS: error toast "MCP server not found: github". User knows something is wrong.
- C# + sidecar: success toast "MCP server github start requested (stub)". The literal word "stub" is user-visible. The server never starts. Downstream MCP tool invocations fail (see #010), producing a second-order user bug that's hard to trace back to this silent no-op.

Error paths:
- TS: HTTP 500, body `{"error":"MCP server not found: github"}`.
- C# + sidecar: HTTP 200 with developer-debug string in `message`.

Secondary gap: MCP server logs are dropped entirely. Port story 6-4 explicitly calls for log capture.

## 4. Gap from stories

`docs/stories/epic-6/story-6-4/6-4-mcp-client-integration.md` AC (approximate):

> - MCP server lifecycle management (connect, disconnect)
> - Error reporting with server-name context
> - Log capture for debugging

Both the stub-return and the missing logs map regress against this story.

Story alignment:
- [x] Matches TS behavior (C# + sidecar regresses against both TS and the story on the throw path)
- [ ] Matches C# behavior
- [ ] Describes a third behavior
- [ ] No story — story exists (6-4) and explicitly governs this.

## 5. Status

- **Classification**: Behavioral drift. Two separate regressions: (a) return-string vs throw, (b) dropped logs.
- **What's needed to finish**:
  1. Replace each stub-return with `throw new DependencyNotConfiguredError('mcpClient')` (see #002).
  2. Re-introduce the logs map from the deleted TS service (or delegate to real MCP client's native log stream if it has one).
  3. Expose `GET /kb/mcp/servers/:id/logs` — **missing endpoint**. TS had it; sidecar doesn't.
- **Is it "just a stub" or is scope missing?** Scope is story'd (6-4). Both drift and scope miss (logs).
- **Blockers**: #010 (invokeTool gap is same root cause). Logs endpoint requires contract expansion.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/McpManagementService.ts:111-125` — throw for start/stop.
  - `packages/intelligence-server/src/services/McpManagementService.ts` — add `logs: Map<string, McpLogEntry[]>` field and append on success paths.
  - `packages/intelligence-server/src/server.ts:125-132` — wrap start/stop routes with 503 translation.
  - `packages/intelligence-server/src/__tests__/services/McpManagementService.test.ts` — update assertions.
- Files to create:
  - New route `GET /kb/mcp/servers/:id/logs` + C# endpoint (note: expands the 30-route contract to 31).
- Tests to add:
  - `POST /kb/mcp/servers/foo/start` with null client → HTTP 503.
  - `POST /kb/mcp/servers/foo/start` with real client → HTTP 200 + log entry recorded.
  - `GET /kb/mcp/servers/foo/logs` with two prior starts → returns 2 log entries.
- Estimated effort: 0.5-1h
  - Service changes: 20m
  - Route wrap + 503: 10m
  - Logs endpoint + C# forward + DTO: 30m

## References

- TS source: `packages/api/src/services/knowledge-base/MCPManagementService.ts:82-111` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/McpManagementService.ts:111-125`
- Story: `docs/stories/epic-6/story-6-4/6-4-mcp-client-integration.md`
- Related findings: #001, #002, #010 (invoke) — all same root cause.
