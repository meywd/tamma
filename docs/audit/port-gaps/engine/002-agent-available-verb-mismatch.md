# Finding 002: `/api/engine/agent-available` verb + body mismatch

**Scope**: engine
**Severity**: P1 (feature broken)
**Status**: Semantic rewrite (verb flipped, shape invented)
**Estimated port effort**: 1h

## 1. What's in TS

- File: `packages/api/src/routes/engine-callback.ts:161-173` (9e9a57c~1)
- Contract: `GET /api/engine/agent-available` — a liveness probe for the agent provider. No request body. Returns `{available: boolean}`.

```typescript
// packages/api/src/routes/engine-callback.ts:161-173 (9e9a57c~1)
app.get(
  '/api/engine/agent-available',
  async (_request: FastifyRequest, reply: FastifyReply) => {
    try {
      const available = await agent.isAvailable();
      const response: AgentAvailableResponse = { available };
      return reply.send(response);
    } catch {
      const response: AgentAvailableResponse = { available: false };
      return reply.send(response);
    }
  },
);
```

- Dependencies: `IAgentProvider.isAvailable()` — a simple health/capability check.
- Tests: liveness smoke test in `packages/api/src/__tests__/engine-callback.test.ts`.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:114-115`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:12`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:12
public record AgentAvailableRequest(string EngineId, string[] Capabilities);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:114-115
public static Task<IResult> AgentAvailable(AgentAvailableRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Agent registered", engineId = req.EngineId }));
```

Routing in `Program.cs` (or wherever endpoints are wired) registers this under the HTTP verb that matches the handler signature — ASP.NET binds a record parameter from the body by default, so this has silently been registered as `POST`, not `GET`. The response shape (`{message, engineId}`) is not a boolean.

- Tests: none cover this endpoint. No test asserts the HTTP verb.

## 3. The gap

- TS did: `GET /api/engine/agent-available` → `{available: true|false}`. No body.
- C# does: `POST /api/engine/agent-available` with body `{engineId, capabilities[]}` → `{message: "Agent registered", engineId}`. The handler semantically treats this as an **engine registration** endpoint, not an agent availability probe.

For a caller issuing `GET /api/engine/agent-available` (which is what the TS contract documents and what a probe would naturally do):

- TS: 200 `{available: true}`
- C#: 405 Method Not Allowed, or 404 depending on routing.

The semantic drift is worse than the verb mismatch. The C# handler's name ("AgentAvailable") implies a probe, but the body (`string[] Capabilities`) and response (`"Agent registered"`) suggest registration. No caller — TS client or deployed Elsa activity — asks the engine to register via this endpoint; registration is the concern of the missing engine registry (see finding 013).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` lists the engine callback endpoints but does not include `agent-available` explicitly. Its original story is the TS file header comment referencing "the callback half of the ELSA integration".
- The audit summary's item #13 ("Engine Registry doesn't exist") suggests the C# author was partly conflating this endpoint with engine registration. The correct home for registration is a dedicated registry endpoint, not a probe.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Semantic rewrite — the endpoint was silently repurposed from a liveness probe into a half-baked registration endpoint.
- **What's needed to finish**:
  1. Change the handler to `HttpGet`.
  2. Delete the `AgentAvailableRequest` DTO.
  3. Return `{available: bool}` by calling an injected `IAgentProvider.IsAvailableAsync()` — short-term this can be a hard-coded `true` when an LLM proxy is configured.
- **Is it "just a stub" or is scope missing?** The verb and DTO are wrong, and the agent-availability scope was not implemented. Moderate scope — an `IAgentProvider` port is required for a real implementation but a minimal one (`HttpClient.GetAsync("/v1/models")` against Anthropic) suffices.
- **Blockers**: none. Not blocked on finding 013 because this is a probe, not registration.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:114-115`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:12` — delete `AgentAvailableRequest`.
  - Endpoint-wire-up in `Program.cs` — switch from `MapPost` to `MapGet` on the route.
- Tests to add:
  - `AgentAvailable_Get_Returns200WithAvailableTrue` — GET with no body.
  - `AgentAvailable_Post_Returns405` — guard against the original mis-registration.
- Estimated effort: 1h
  - Code change: 15m
  - Tests: 30m
  - Registration fix in `Program.cs`: 15m

## References

- TS source: `packages/api/src/routes/engine-callback.ts:161-173`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:114-115`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: `013-engine-registry-missing.md` (the correct home for registration), `001-execute-task-stub.md`
