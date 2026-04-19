# Finding 002: User-visible literal "(stub …)" strings in API responses

**Scope**: kb
**Severity**: P1 (user-visible, four API endpoints)
**Status**: Behavioral drift (ported but semantics diverged — TS threw, sidecar leaks developer strings)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/`.

The deleted TS services threw explicit errors when a real dependency was missing for a write / lifecycle operation:

```typescript
// packages/api/src/services/knowledge-base/IndexManagementService.ts (9e9a57c~1)
async triggerIndex(_request?: TriggerIndexRequest): Promise<void> {
  if (this.currentStatus.status === 'indexing') {
    throw new Error('Indexing is already in progress');
  }
  let effectivePath = _request?.repositoryPath ?? this.projectPath;
  if (!this.indexer || !effectivePath) {
    throw new Error('No indexer or project path configured');
  }
  // ...
}
```

```typescript
// packages/api/src/services/knowledge-base/MCPManagementService.ts (9e9a57c~1)
async startServer(name: string): Promise<void> {
  if (!this.client) {
    throw new Error(`MCP server not found: ${name}`);
  }
  await this.client.connectServer(name);
  // ...
}

async stopServer(name: string): Promise<void> {
  if (!this.client) {
    throw new Error(`MCP server not found: ${name}`);
  }
  // ...
}

async invokeTool(request: MCPToolInvokeRequest): Promise<MCPToolInvokeResult> {
  if (!this.client) {
    throw new Error('MCP client is not configured');
  }
  // ...
}
```

The TS VectorDB service did not have explicit `upsert` / `delete` stubs — it simply never called the null store (behavior was: fail silently or throw at the null-dereference, depending on path).

- Dependencies: TS `TammaError` conventions (CLAUDE.md § "Error Handling").
- Tests: `packages/api/src/__tests__/services/knowledge-base/*.test.ts` asserted the thrown errors.

## 2. What's in C#

Current state on `feat/auth-foundation`.

### C# layer
The C# side never sees the stub string as a distinct code path — it receives the sidecar's JSON response and forwards it verbatim:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs (current)
public static async Task<IResult> TriggerIndex(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] TriggerIndexRequest? body,
    CancellationToken ct)
    => Results.Ok(await client.TriggerIndexAsync(body, ct));
```

### Sidecar layer — four stub leaks

```typescript
// packages/intelligence-server/src/services/IndexManagementService.ts:121-126 (current)
async triggerIndex(
  body?: { fullReindex?: boolean; repositoryPath?: string; changedFiles?: string[] },
): Promise<{ message: string }> {
  if (!this.indexer) {
    return { message: 'Indexing triggered (stub — no indexer configured)' };
  }
  // ...
}
```

```typescript
// packages/intelligence-server/src/services/VectorDbManagementService.ts:92-106 (current)
async upsert(req: VectorUpsertRequest): Promise<{ message: string; count: number }> {
  if (!this.store) {
    return { message: 'Vectors upserted (stub — no store configured)', count: 0 };
  }
  // ...
}

async delete(req: VectorDeleteRequest): Promise<{ message: string }> {
  if (!this.store) {
    return { message: 'Vectors deleted (stub — no store configured)' };
  }
  // ...
}
```

```typescript
// packages/intelligence-server/src/services/McpManagementService.ts:111-125 (current)
async startServer(id: string): Promise<{ message: string }> {
  if (!this.client) {
    return { message: `MCP server ${id} start requested (stub)` };
  }
  // ...
}

async stopServer(id: string): Promise<{ message: string }> {
  if (!this.client) {
    return { message: `MCP server ${id} stop requested (stub)` };
  }
  // ...
}
```

- Dependencies: no Tamma error convention — these are just string literals.
- Tests: `packages/intelligence-server/src/__tests__/services/*.test.ts` assert on these strings as "expected stub behavior".

## 3. The gap

- TS did: throw `Error('No indexer or project path configured')` or equivalent — HTTP 500 reached the dashboard, user saw "something failed".
- C# + sidecar does: respond HTTP 200 with JSON body `{"message":"Indexing triggered (stub — no indexer configured)"}` — dashboard sees a success, renders a toast reading "Indexing triggered (stub — no indexer configured)".

For a user clicking "Trigger Indexing" in the dashboard:
- TS: error toast ("index trigger failed"), logs captured, on-call paged (eventually).
- C# + sidecar: success toast showing a developer-oriented debug string. No error. Indexing never runs. Dashboard state silently diverges from reality.

Error paths:
- TS: HTTP 500, body `{"error":"No indexer or project path configured"}`.
- Sidecar: HTTP 200, body `{"message":"… (stub — no … configured)"}`.

This is user-visible in four places:
1. `POST /api/kb/index/trigger`
2. `POST /api/kb/vector-db/upsert`
3. `DELETE /api/kb/vector-db/delete`
4. `POST /api/kb/mcp/servers/:id/start`
5. `POST /api/kb/mcp/servers/:id/stop`

(Five strings total. Issue counts as one finding because the fix is the same everywhere.)

## 4. Gap from stories

No story explicitly governs error vs. stub-string shape for a null-dep fallback — but:
- `CLAUDE.md` § "Error Handling" prescribes `TammaError` with structured context and explicit severity. Stub strings are not compatible with that model.
- Epic 6 ACs describe real backend behavior; they implicitly assume missing deps is an operator error, not a successful-no-op.

Story alignment:
- [ ] Matches TS behavior (TS threw; sidecar returns success)
- [ ] Matches C# behavior (C# passes through; sidecar is the divergence)
- [x] Describes a third behavior — Epic 6 implicitly assumes real deps are always wired, so neither the TS throw nor the sidecar stub is spec'd.
- [ ] No story — spec gap

## 5. Status

- **Classification**: Behavioral drift. The sidecar ported the "null-fallback" pattern from the deleted TS services but replaced errors with user-visible debug strings.
- **What's needed to finish**:
  1. Replace each `return { message: '… (stub …)' }` with `reply.status(503).send({ error: 'KB feature unavailable: no X configured' })`.
  2. Add a Fastify decorator or common helper (`assertDep()`) so the pattern is consistent across all 6 services.
  3. Update sidecar unit tests that currently assert on the stub strings to expect 503 instead.
- **Is it "just a stub" or is scope missing?** This is "just a stub" — the scope is well-understood but the fallback was written as a return-value rather than a proper error. Once the composition root wires real backends (#001), these branches become dead code anyway, but until then they should surface as 503s.
- **Blockers**: none; independent of #001. Can be done in isolation as a 2h fix.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/IndexManagementService.ts:121-126`
  - `packages/intelligence-server/src/services/VectorDbManagementService.ts:92-106`
  - `packages/intelligence-server/src/services/McpManagementService.ts:111-125`
  - `packages/intelligence-server/src/server.ts` (wrap route handlers to translate `503` service errors into Fastify reply)
- Files to create:
  - `packages/intelligence-server/src/services/assert-dep.ts` (tiny helper throwing a custom `DependencyNotConfiguredError`)
- Tests to add:
  - Unit tests in each service's `__tests__` to assert that `triggerIndex()` / `upsert()` / `delete()` / `startServer()` / `stopServer()` throw `DependencyNotConfiguredError` when the dep is null.
  - Route test: `POST /kb/index/trigger` with no bundle → HTTP 503, body `{"error":"KB feature unavailable: no indexer configured"}`.
- Estimated effort: 2h
  - Helper + service changes: 1h
  - Test updates (including replacing existing assertions on stub strings): 1h

## References

- TS source: `packages/api/src/services/knowledge-base/{IndexManagementService,MCPManagementService}.ts` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/*.ts`
- CLAUDE.md section: "Error Handling" — custom `TammaError` with `retryable` and `severity`.
- Related findings: #001 (makes this dead code once wired), #006 (index-specific detail), #007, #008 (vector-db-specific detail), #009 (MCP-specific detail)

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

The literal `(stub …)` strings are emitted from inside the TypeScript sidecar
(`packages/intelligence-server/src/services/{Index,VectorDb,Mcp}ManagementService.ts`),
not from any C# code. The C# layer is a verbatim passthrough: it cannot
detect the substring without inspecting JSON bodies (which the audit
explicitly says it should not do — see `IntelligenceHttpClient.cs` doc
comment) and it cannot rewrite responses without breaking the
"contract-faithful passthrough" property. Translating these into 503
responses requires changes inside the sidecar's Fastify route handlers and
service classes. No C# work would fix the user-visible regression.

**To unblock:** addressed naturally by the sidecar composition-root work
(finding 001) — once real backends are wired the stub branches become dead
code. Until then, a 2h sidecar-side fix can replace the literal strings
with `503 service_unavailable` responses (the C# `IntelligenceHttpClient`
already returns a `degraded=true` envelope on 5xx, so this would surface
correctly upstream without further C# changes).
