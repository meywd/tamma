# Finding 012: Engine lifecycle endpoints — SSE → one-shot JSON, no engine binding

**Scope**: engine
**Severity**: P1 (feature broken — dashboard live updates dead, commands no-op)
**Status**: Semantic rewrite (all 7 endpoints replaced with event-store shims)
**Estimated port effort**: 12–16h

## 1. What's in TS

- File: `packages/api/src/routes/engine/index.ts:101-313` (9e9a57c~1)
- Seven endpoints bound to a live `TammaEngine` instance:

```
POST /api/engine/command         — dispatch EngineCommand (start/stop/pause/resume/approve/reject/skip)
GET  /api/engine/state           — current state snapshot (JSON)
GET  /api/engine/events/state    — SSE stream of state updates
GET  /api/engine/events/logs     — SSE stream of log entries
GET  /api/engine/stats           — EngineStats
GET  /api/engine/plan            — current development plan or null
GET  /api/engine/history         — paginated event history
```

The SSE streams use Fastify's `reply.hijack()` + raw socket writes with a 1-second state poll, a 500ms log poll, and a 15-second heartbeat comment:

```typescript
// packages/api/src/routes/engine/index.ts:164-189 (9e9a57c~1) — /events/state
fastify.get('/api/engine/events/state', async (_request, reply) => {
  reply.hijack();
  sseHeaders(reply);
  sendSSE(reply, 'state', buildSnapshot(engine));
  const interval = setInterval(() => {
    try { sendSSE(reply, 'state', buildSnapshot(engine)); }
    catch { clearInterval(interval); clearInterval(heartbeat); }
  }, 1000);
  const heartbeat = setInterval(() => {
    try { reply.raw.write(':heartbeat\n\n'); }
    catch { clearInterval(interval); clearInterval(heartbeat); }
  }, 15_000);
  reply.raw.on('close', () => { clearInterval(interval); clearInterval(heartbeat); });
});
```

Commands are dispatched to the live engine:

```typescript
// packages/api/src/routes/engine/index.ts:118-152 (9e9a57c~1) — /command (excerpted)
switch (cmd.type) {
  case 'start':
    engine.run().catch((err) => fastify.log.error(err, 'Engine run failed'));
    return reply.send({ ok: true, type: 'start' });
  case 'stop':
    await engine.dispose();
    return reply.send({ ok: true, type: 'stop' });
  // ...
}
```

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:10-44`

```csharp
// EngineEndpoints.cs:10-11
public static Task<IResult> SendCommand(SendCommandRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Command accepted", command = req.Command }));

// EngineEndpoints.cs:13-17
public static async Task<IResult> GetState(IEventRepository eventRepo, ITenantContext tc)
{
    var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 10);
    return Results.Ok(new { state = "idle", events = events.Count });
}

// EngineEndpoints.cs:19-23
public static async Task<IResult> GetStats(IEventRepository eventRepo, ITenantContext tc)
{
    var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 1000);
    return Results.Ok(new { totalEvents = events.Count, timestamp = DateTime.UtcNow });
}

// EngineEndpoints.cs:25-26
public static Task<IResult> GetPlan() =>
    Task.FromResult(Results.Ok(new { plan = (object?)null, message = "No active plan" }));

// EngineEndpoints.cs:28-32
public static async Task<IResult> GetHistory(IEventRepository eventRepo, ITenantContext tc, int? limit) { ... }

// EngineEndpoints.cs:34-38 — /events/state: one-shot JSON, NOT SSE
public static async Task<IResult> GetEventsState(IEventRepository eventRepo, ITenantContext tc, int? limit)
{
    var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 20);
    return Results.Ok(events.Select(e => new { e.Id, e.Type, e.CreatedAt }));
}

// EngineEndpoints.cs:40-44 — /events/logs: one-shot JSON, NOT SSE
public static async Task<IResult> GetEventsLogs(IEventRepository eventRepo, ITenantContext tc, int? limit) { ... }
```

Every endpoint is either (a) a trivial event-store query masquerading as engine state, or (b) a no-op that returns a hardcoded shape. There is no `TammaEngine` in C# — the orchestrator was never ported. Commands go nowhere.

## 3. The gap

- TS did: bound to a live engine instance. Dispatched commands mutated real state. SSE streams pushed live updates to the dashboard. State/stats/plan were computed from the engine's in-memory state machine.
- C# does:
  - `POST /command` — returns "Command accepted" but dispatches nothing. No engine exists to start/stop/pause.
  - `GET /state` — returns `{state: "idle", events: <count>}`. "idle" is a string literal, not an enum derived from a running engine.
  - `GET /stats` — returns event count + timestamp.
  - `GET /plan` — returns `{plan: null, message: "No active plan"}`.
  - `GET /events/state` — returns an array of recent events, one-shot JSON (not `text/event-stream`). The dashboard's EventSource listener will fail to parse.
  - `GET /events/logs` — same one-shot JSON.
  - `GET /history` — works-ish (wraps `eventRepo.QueryAsync`).

For the dashboard's live "engine state" tile polling `EventSource('/api/engine/events/state')`:

- TS: opens a long-lived SSE connection. Receives `event: state\ndata: {...}\n\n` frames every second.
- C#: one HTTP GET returning `application/json`. The browser's EventSource rejects this (wrong Content-Type, wrong body format) and throws. Live tiles stay stuck on the initial poll.

For a user clicking "Stop engine" in the dashboard:

- TS: engine disposes. Background loops exit. Status flips to stopped.
- C#: request 200s. Nothing happens. Engine was never running (it doesn't exist as a service).

## 4. Gap from stories

- Referenced stories:
  - `docs/stories/epic-10/story-10-1/10-1-engine-static-workflow-and-brain.md` — the engine itself.
  - `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md` / `5-4-real-time-dashboard-development-velocity.md` — dashboard consumers of SSE.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs TS and stories)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Semantic rewrite. The endpoints exist at the same URLs but the underlying abstraction (running engine) is gone.
- **What's needed to finish**:
  1. Decide whether to port `TammaEngine` to C# or deprecate the concept. Epic 10 story 10-1 assumes a real engine.
  2. Add a proper `TammaEngine` service (long-lived, hosted) — or a wrapper around Elsa workflow instances, if the "engine" concept collapses into "workflow runtime" in the C# world.
  3. Convert `/events/state` and `/events/logs` to real SSE endpoints using `HttpResponse.Body.WriteAsync` with `text/event-stream` content type and periodic heartbeats.
  4. Wire `/command` to the engine's command bus.
  5. Expose actual state, plan, and stats from the engine — not event-count shims.
- **Is it "just a stub" or is scope missing?** Scope is missing. The `TammaEngine` abstraction was never ported.
- **Blockers**: depends on finding 013 (engine registry) — if there are multiple engines, each endpoint needs an `?engineId=` selector. In the current C# world there's no registry, so the endpoints can only represent the whole-process state.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:10-44` (all 7 handlers).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/ITammaEngine.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/TammaEngine.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/EngineCommandBus.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineSseExtensions.cs` — helper for SSE responses in Minimal APIs.
- Tests to add:
  - `GetEventsState_StreamsSseFrames` — asserts `text/event-stream` content-type, `event: state\ndata: {...}\n\n` framing, periodic heartbeats.
  - `GetEventsLogs_StreamsSseFrames`
  - `SendCommand_StartCommand_InvokesEngineRun`
  - `SendCommand_StopCommand_InvokesEngineDispose`
  - `SendCommand_UnknownCommand_Returns400`
  - `GetState_ReturnsEngineStateNotEventCount`
- Estimated effort: 12–16h
  - `TammaEngine` port: 6–8h
  - SSE helper + `/events/*` endpoints: 3h
  - Command wiring: 2h
  - `/state`, `/stats`, `/plan` rewrite: 2h
  - Tests: 3h

## References

- TS source: `packages/api/src/routes/engine/index.ts:101-313`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:10-44`
- Story: `docs/stories/epic-10/story-10-1/10-1-engine-static-workflow-and-brain.md`, `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`
- Related findings: `013-engine-registry-missing.md`, `023-dashboard-engines-empty.md`, `022-dashboard-summary-shape-drift.md`
