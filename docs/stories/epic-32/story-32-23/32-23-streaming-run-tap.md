# Story 32-23: Streaming Run Tap (SSE for dashboard / CLI)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **developer watching an autonomous run from the Tamma dashboard or the `tamma` CLI**,
I want a `GET /api/v1/llm/runs/{correlationId}/stream` SSE endpoint that emits each token, tool call, tool result, question, answer, and final frame of a managed LLM run **as it happens** — fed by an in-process bus that 32-5's `ManagedAgent.RunAsync` publishes to while it runs the buffered tool loop server-side —
So that **humans get a live view of a run WITHOUT changing the engine's buffered `/llm/call` contract**: the Elsa step still issues one durable request/response call (so `ForEach`/`RetryCheck`/`SkipIfSucceeded` are untouched), while observers subscribe to a decoupled tap correlated by the workflow instance id.

## Priority

P1 — This turns the **inert streaming seam LIVE**. 32-5 ships `/llm/call` buffered-only and deliberately wires `ToolLoopEventEmitter` → `NullToolLoopEventSink` (events dropped, gated behind `EnableStreaming`). This story replaces the null sink with a real one and adds the human-facing tap. It is a **non-blocking enhancement** of the lynchpin: it adds observability without touching the engine boundary, the credential path, or the metering path. It depends on 32-5 (buffered endpoint + the sink seam) and cross-references 32-20 (interactive question-back, which produces the `question`/`answer` frames) and the dashboard/CLI consumers.

## Context

### What 32-5 ships (the buffered baseline this story builds on)

32-5 builds `POST /api/v1/llm/call` as **buffered (`application/json`) ONLY** (32-5 AC2, deep-dive §3). `ManagedAgent.RunAsync` runs the agentic tool loop to completion server-side via the extracted `IInlineToolLoopRunner` (32-5 AC4) and returns one `LlmCallResponse`. The thin `CallLlmInlineActivity` calls `TammaApiClient.CallLlmAsync(...)`, gets the buffered result, and writes the same `LastDiagnostic`/`LastResponse`/`ToolLoop*` variables — so `LlmCallWorkflow.cs`'s `BuildRetryLoop` → `ForEach<provider>` boundary is byte-for-byte unchanged. **That contract does not move in this story.**

### The inert seam (the gap this story closes)

The streaming machinery exists but is dead:

- `ToolLoopEventEmitter` (`Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs`) emits structured progress events (`TOOL_LOOP.TURN_STARTED`, `TOOL_LOOP.TOOL_EXECUTING`, `TOOL_LOOP.TOOL_COMPLETED`, `TOOL_LOOP.TURN_COMPLETED`, `TOOL_LOOP.COMPLETED`) to an injected `IToolLoopEventSink`.
- The only registered sink is `NullToolLoopEventSink.Instance` — every event is silently discarded (`IToolLoopEventSink.cs:23`). The emitter even threads `workflowInstanceId` through every call, but with nowhere to send it.
- The current provider call does **not** stream tokens — `CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` do a single blocking `PostAsync` + `ReadFromJsonAsync` (deep-dive §3). So "streaming" today = tool-loop progress, and even that is dropped.

The SSE infrastructure to deliver it also already exists and is the template:

- `AdminTenantEventsSseEndpoint` (`Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs`) — the canonical SSE handler: `ContentType=text/event-stream`, `X-Accel-Buffering: no`, 30s `: keepalive` heartbeats, `MaxStreamDurationSeconds` ceiling, `Last-Event-ID` resumption, per-tick error budget, body scrub allowlist.
- `SseWriter` (`Tamma.Api/Services/Engine/Lifecycle/SseWriter.cs`) — `WriteHeaders` / `WriteEventAsync(eventName, payload)` / `WriteCommentAsync` — the exact frame writer to reuse.

### What this story does (deep-dive §3, §6.4)

Turn the seam live and add a decoupled human tap, **without changing the engine's buffered call**:

1. **A real `IToolLoopEventSink`** — `BusToolLoopEventSink` — that, instead of dropping events, publishes them onto an **in-process run bus** (`ILlmRunStreamBus`) keyed by `correlationId` (= the workflow instance id, already threaded through the emitter and the `LlmCallRequest`). Registered in `Tamma.Api` (where the runner now executes per 32-5), gated behind the existing `EnableStreaming` flag.
2. **`GET /api/v1/llm/runs/{correlationId}/stream`** (`Tamma.Api/Endpoints/LlmRunStreamEndpoints.cs`) — a human-facing SSE endpoint that subscribes to the bus for that `correlationId` and writes each event as an SSE frame, reusing `SseWriter` and the `AdminTenantEventsSseEndpoint` hardening (headers, heartbeats, max-duration, error budget). Observers (dashboard, `tamma` CLI) are fully **decoupled** from the engine's buffered `/llm/call` — they never hold up, retry, or influence the buffered call.
3. **Frame vocabulary** — `token`, `tool_call`, `tool_result`, `question`, `answer`, `final` — correlated by `correlationId`. The `tool_call`/`tool_result` frames are produced by `ToolLoopEventEmitter` events bridged through the bus; `token` frames come from optional provider token streaming inside the runner; `question`/`answer` frames are produced by 32-20's interactive question-back (this story defines the frame shape + transport, 32-20 produces the content); `final` carries the buffered turn summary the engine already received.
4. **Optional `Accept: text/event-stream` on `/llm/call` itself** (deep-dive §3, the second response mode) for **direct human callers** — noted and wired as a thin opt-in that runs the same bus subscription against the in-flight run. **The engine path stays `application/json` and is unaffected** (the thin step never sends `Accept: text/event-stream`).
5. **Provider token streaming (optional, inside the runner)** — `stream:true` MAY run inside `IInlineToolLoopRunner` in the API to cut TTFB and allow mid-turn cancel; it is **collapsed to a buffered turn result before the buffered `/llm/call` response returns** — invisible to the step. When enabled it emits `token` frames onto the bus; when disabled the tap still works (turn-granular `tool_call`/`tool_result`/`final` frames). This story does not require token streaming to land — it makes the bus carry tokens *if* the runner produces them.

### Why the engine doesn't get SSE (the load-bearing decision)

Elsa activities are durable-checkpointed request/response. Holding an open socket across `MaxSteps` turns fights persistence and the `ForEach`-per-provider boundary (deep-dive §3). So: **engine = buffered; dashboard/CLI = SSE via the tap.** The tap is read-only observability fed by an in-process bus; it can never break `RetryCheck`/`SkipIfSucceeded`/the circuit breaker because it is not on the engine's call path.

### Explicitly out of scope (referenced, not implemented here)

- **The buffered endpoint, `ManagedAgent` composition, credential resolution, metering, the thin-client cutover** — all **32-5**. This story consumes 32-5's run, it does not re-implement it.
- **Producing `question`/`answer` content** (the `request_input` tool + `IQuestionRouter` + `WaitForAgentQuestionActivity`) — **32-20** (interactive question-back, deep-dive §5). This story defines the `question`/`answer` SSE frame shape + transport so 32-20 plugs in.
- **A distributed (cross-process) bus.** The bus is **in-process** (one `Tamma.Api` instance fans run events to its own subscribers). Multi-instance fan-out (Redis/Postgres `LISTEN/NOTIFY`) is an explicit open decision, deferred — single-instance covers the dashboard/CLI use case today and is consistent with `WebhookSignalRegistry`'s in-process model.
- **Persisting the stream.** The durable audit trail is the DCB `AGENT.RUN.*`/`TOOL_LOOP.*` events 32-5 already emits to the tenant store; the tap is **live-only** (an observer that connects mid-run sees events from connect time forward, plus an optional replay of the run's DCB events — see AC8). It does not become a second event store.

## Acceptance Criteria

1. **The tap endpoint exists.** `GET /api/v1/llm/runs/{correlationId}/stream` is served by a new `Tamma.Api/Endpoints/LlmRunStreamEndpoints.cs`. It sets SSE headers via `SseWriter.WriteHeaders` (`ContentType=text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no`), subscribes to `ILlmRunStreamBus` for the route `correlationId`, and writes one SSE frame per published run event until the run completes, the client disconnects, or `MaxStreamDurationSeconds` elapses. It emits `: keepalive` heartbeats every 30s (reusing the `AdminTenantEventsSseEndpoint` cadence) so proxies don't drop the connection during quiet turns.

2. **Auth is the human plane, NOT the engine plane.** The tap is consumed by humans (dashboard JWT, `tamma` CLI `ApiKey`), so it requires `RequireAuthorization` on the standard authenticated policy (`AuthenticatedAny` / `MemberAccess` — JWT + ApiKey schemes), **not** the engine bearer (`Tamma:ApiToken`). In **SaaS** the caller's `tenantId` is derived from their auth context and a run is only streamable if its `correlationId` belongs to a run the caller's tenant owns — a `correlationId` for another tenant → **404** (never confirm existence cross-tenant). In **single-user** the sole user may stream any local run. Missing/invalid auth → **401**.

3. **A real `IToolLoopEventSink` replaces the null sink.** A new `BusToolLoopEventSink : IToolLoopEventSink` publishes each `WriteEventAsync(eventType, data, ct)` to `ILlmRunStreamBus.PublishAsync(correlationId, frame, ct)`, mapping the `TOOL_LOOP.*` event types to the run-tap frame vocabulary (`tool_call`/`tool_result`/`final`). It is registered in `Tamma.Api` (where 32-5 moved the runner + its DI) **behind the existing `EnableStreaming` flag**; when `EnableStreaming` is false the registration stays `NullToolLoopEventSink` and the tap returns an immediate `final`/`event: end` with no live frames (graceful no-op, never an error). `grep` confirms `NullToolLoopEventSink` is no longer the only registered sink when streaming is enabled.

4. **The frame vocabulary is exactly `{ token, tool_call, tool_result, question, answer, final }`, correlated by `correlationId`.** Each SSE frame is `event: <type>\ndata: <json>\n\n` where the payload carries `{ correlationId, seq, ... }`. `seq` is a per-run monotonic counter (NOT the per-tenant `domain_events.SequenceNumber`, which is a separate per-schema BIGSERIAL — see Dev Notes). `tool_call` carries `{ toolName, toolCallId, turn }`; `tool_result` carries `{ toolName, toolCallId, success, durationMs }`; `token` carries `{ delta }`; `question`/`answer` carry the 32-20 shape (`{ question, kind, options?, answerer? }` / `{ answer, answerer }`); `final` carries the buffered turn summary (`{ success, totalTurns, totalTokens, exhausted, durationMs }`). Payloads are **key-free** (credential-safety, AC9).

5. **The bus is in-process and decoupled.** `ILlmRunStreamBus` (`Tamma.Api/Services/Streaming/`) is a singleton supporting `PublishAsync(correlationId, frame, ct)` (non-blocking, never throws into the producer) and `Subscribe(correlationId)` → an `IAsyncEnumerable<RunStreamFrame>` (or a bounded `Channel<RunStreamFrame>` per subscriber). Publishing when there are **zero subscribers is a no-op** — the buffered run is never blocked, slowed, or failed by the absence/slowness of observers. A slow/disconnected subscriber's bounded channel drops oldest frames (back-pressure) rather than stalling the run (modeled on `AdminTenantEventsSseEndpoint`'s 50-row per-tick cap). The producer side (`ManagedAgent`/runner) treats the bus as fire-and-forget.

6. **The engine's buffered contract is byte-for-byte unchanged.** No change to `LlmCallEndpoints` buffered response, `ManagedAgent.RunAsync`'s return value, `CallLlmInlineActivity`'s variable writes, or `LlmCallWorkflow.cs`. The only producer-side change is that `ManagedAgent`/the runner publish to the bus **alongside** the existing flow (a side-effect, not a control-flow change). A test asserts the buffered `LlmCallResponse` is identical whether 0 or N subscribers are attached.

7. **Optional `Accept: text/event-stream` on `/llm/call` (deep-dive §3, second response mode).** When a **non-engine** caller hits `POST /api/v1/llm/call` with `Accept: text/event-stream`, the endpoint streams the same bus frames for the run inline and ends with `final`, instead of returning buffered JSON. The **engine path is explicitly excluded**: the thin `CallLlmInlineActivity` sends `Accept: application/json` (the default), so `ForEach`/`RetryCheck`/`SkipIfSucceeded` see the unchanged buffered response. A test asserts the engine-shaped request (engine bearer + `application/json`) always gets buffered JSON regardless of any `Accept` munging.

8. **Mid-run connect + replay.** An observer connecting after a run started receives frames **from connect time forward** (live tail). Optionally (gated by `?replay=true`), the handler first replays the run's already-emitted `TOOL_LOOP.*`/`AGENT.RUN.*` DCB events for that `correlationId` from the tenant store (scrubbed, key-free) as catch-up frames, then switches to the live tail — reusing the `AdminTenantEventsSseEndpoint` resume idiom. Without `?replay`, only live frames are sent. Either way the stream terminates with `event: end\ndata: {"reason":"run_complete"}\n\n` when the run's `final` frame is published, matching `AdminTenantEventsSseEndpoint`'s clean-close convention.

9. **Credential safety (load-bearing).** No frame, log line, or bus payload EVER carries an API key, `BaseUrl` auth, raw provider header, or raw prompt body that may contain secrets. `token` deltas are model output (safe); `tool_call`/`tool_result` carry only `toolName`/`toolCallId`/`success`/`durationMs` (the tool **arguments/outputs are NOT streamed** — they may contain secrets and are sanitized/redacted only on the buffered path). The frame builder runs every payload through the same allowlist discipline as `AdminTenantEventsSseEndpoint.ScrubEvent`.

10. **Tests cover the tap, the sink, the bus, and the non-regression of the buffered path.** Endpoint auth (401 missing, 404 cross-tenant); SSE headers + heartbeat + clean close; `BusToolLoopEventSink` maps `TOOL_LOOP.*` → the frame vocabulary; the bus is a no-op with zero subscribers and drops-oldest under back-pressure; the buffered `LlmCallResponse` is identical with 0 vs N subscribers (AC6); the engine-shaped `/llm/call` request always gets buffered JSON (AC7); `EnableStreaming=false` degrades gracefully; no frame/log/payload contains a key (AC9); mid-run connect sees live frames and `?replay=true` catches up from the DCB store (AC8).

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  LlmRunStreamEndpoints.cs           # NEW — GET /api/v1/llm/runs/{correlationId}/stream (human SSE tap)

apps/tamma-elsa/src/Tamma.Api/Services/Streaming/
  ILlmRunStreamBus.cs                # NEW — in-process pub/sub keyed by correlationId
  LlmRunStreamBus.cs                 # NEW — singleton; bounded per-subscriber channels; publish = no-op w/ 0 subs
  RunStreamFrame.cs                  # NEW — { Type, CorrelationId, Seq, Payload } (frame vocabulary)
  RunStreamFrameType.cs              # NEW — token|tool_call|tool_result|question|answer|final constants
  BusToolLoopEventSink.cs            # NEW — IToolLoopEventSink that publishes to the bus (replaces Null sink)
  RunStreamFrameScrubber.cs          # NEW — allowlist scrub (mirrors AdminTenantEventsSseEndpoint.ScrubEvent)

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  ManagedAgent.cs                    # MODIFY (32-5-owned) — publish `final` (and `question`/`answer` via 32-20) to the bus; side-effect only

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  LlmCallEndpoints.cs                # MODIFY (32-5-owned) — Accept: text/event-stream branch for direct human callers (engine stays application/json)

apps/tamma-elsa/src/Tamma.Api/Program.cs
                                     # MODIFY — map the tap endpoint; register ILlmRunStreamBus (singleton) +
                                     #          swap NullToolLoopEventSink → BusToolLoopEventSink behind EnableStreaming
```

> Reuse, don't reinvent: `SseWriter` (`WriteHeaders`/`WriteEventAsync`/`WriteCommentAsync`) and the `AdminTenantEventsSseEndpoint` hardening (heartbeats, `MaxStreamDurationSeconds`, error budget, `Last-Event-ID`/replay, scrub allowlist) are copied/factored, not rebuilt.

### The tap endpoint (`LlmRunStreamEndpoints.cs`)

```csharp
// GET /api/v1/llm/runs/{correlationId}/stream — human-facing SSE tap.
// Auth: AuthenticatedAny (JWT + ApiKey) — NOT the engine bearer.
public static async Task StreamRun(
    string correlationId,
    [FromServices] ILlmRunStreamBus bus,
    [FromServices] ITenantContext tc,
    [FromServices] IEventRepository eventRepo,   // for ?replay=true catch-up
    [FromServices] TimeProvider timeProvider,
    HttpContext http,
    CancellationToken ct)
{
    // SaaS ownership guard: a correlationId not owned by this tenant => 404 (no cross-tenant existence oracle).
    if (!await OwnsRunAsync(tc, eventRepo, correlationId, ct)) { http.Response.StatusCode = 404; return; }

    SseWriter.WriteHeaders(http.Response);

    using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct, http.RequestAborted);
    streamCts.CancelAfter(TimeSpan.FromSeconds(MaxStreamDurationSeconds));   // reuse the admin SSE ceiling
    var token = streamCts.Token;

    if (IsReplayRequested(http))                                   // AC8 — catch up from the DCB store first
        await ReplayDcbFramesAsync(eventRepo, tc, correlationId, http, token);

    await foreach (var frame in bus.Subscribe(correlationId).WithCancellation(token))   // AC1/AC5 — live tail
    {
        var safe = RunStreamFrameScrubber.Scrub(frame);            // AC9 — key-free
        await SseWriter.WriteEventAsync(http.Response, frame.Type, safe, token);
        if (frame.Type == RunStreamFrameType.Final) break;         // AC8 — clean close on `final`
        // heartbeat cadence handled by a 30s timer race (AdminTenantEventsSseEndpoint idiom)
    }

    await SseWriter.WriteCommentAsync(http.Response, "stream-closing", CancellationToken.None);
    // event: end\ndata: {"reason":"run_complete"}\n\n
}
```

### The in-process bus (`ILlmRunStreamBus` / `LlmRunStreamBus`)

```csharp
public interface ILlmRunStreamBus
{
    // Producer side (ManagedAgent/runner): fire-and-forget. No-op when there are 0 subscribers.
    // NEVER throws into the caller — a streaming failure must never fail the buffered run (AC5/AC6).
    ValueTask PublishAsync(string correlationId, RunStreamFrame frame, CancellationToken ct = default);

    // Consumer side (the tap endpoint): a bounded channel per subscriber; drops oldest under back-pressure.
    IAsyncEnumerable<RunStreamFrame> Subscribe(string correlationId);
}

// Singleton. correlationId -> set of bounded Channel<RunStreamFrame> (capacity ~256, DropOldest).
// Mirrors WebhookSignalRegistry's in-process registry discipline (ConcurrentDictionary, per-key fan-out).
```

```csharp
public sealed record RunStreamFrame(
    string Type,              // RunStreamFrameType.*
    string CorrelationId,
    long Seq,                 // per-run monotonic — NOT domain_events.SequenceNumber (per-schema BIGSERIAL)
    object Payload);          // key-free, scrubbed before write

public static class RunStreamFrameType
{
    public const string Token      = "token";
    public const string ToolCall   = "tool_call";
    public const string ToolResult = "tool_result";
    public const string Question   = "question";   // produced by 32-20
    public const string Answer     = "answer";     // produced by 32-20
    public const string Final      = "final";
}
```

### The live sink (`BusToolLoopEventSink`) — replacing the null sink

```csharp
// Registered in Tamma.Api behind EnableStreaming (else stays NullToolLoopEventSink — graceful no-op).
public sealed class BusToolLoopEventSink : IToolLoopEventSink
{
    private readonly ILlmRunStreamBus _bus;

    public async Task WriteEventAsync(string eventType, object data, CancellationToken ct = default)
    {
        // The emitter threads workflowInstanceId == correlationId already (ToolLoopEventEmitter).
        var (correlationId, frameType, payload) = MapToolLoopEvent(eventType, data);   // TOOL_LOOP.* -> tool_call/tool_result/final
        if (frameType is null) return;                                                 // unmapped events ignored
        await _bus.PublishAsync(correlationId, new RunStreamFrame(frameType, correlationId, NextSeq(correlationId), payload), ct);
    }
}
```

`MapToolLoopEvent`: `TOOL_LOOP.TOOL_EXECUTING` → `tool_call`; `TOOL_LOOP.TOOL_COMPLETED` → `tool_result`; `TOOL_LOOP.COMPLETED` → `final`. `TURN_STARTED`/`TURN_COMPLETED` are turn-progress (optionally surfaced as `token`-less progress or ignored). `token` frames are published directly by the runner if provider token streaming is enabled; `question`/`answer` by 32-20.

### The `Accept: text/event-stream` second mode on `/llm/call` (deep-dive §3)

```csharp
// In LlmCallEndpoints (32-5-owned file; this story adds the branch):
if (WantsEventStream(http) && !IsEngineCaller(http))   // engine bearer + application/json => buffered (unchanged)
    return await StreamInlineAsync(request, bus, managed, http, ct);   // subscribe to the in-flight run, end on `final`
// else: existing buffered path — byte-for-byte unchanged for the engine.
```

The engine never sets `Accept: text/event-stream` (the thin `CallLlmInlineActivity` uses `TammaApiClient.CallLlmAsync` → `application/json`), so this branch is dormant for the engine and the buffered contract (AC6) is untouched.

### Producer-side hooks (side-effects only, in `ManagedAgent`)

`ManagedAgent.RunAsync` (32-5) gains **non-control-flow** publish calls: after the buffered loop returns, publish a `final` frame (`{ success, totalTurns, totalTokens, exhausted, durationMs }`); when 32-20 raises/answers a question, publish `question`/`answer`. These wrap in try/catch that logs-and-swallows (a streaming failure must never fail the run — AC5/AC6). The tool-loop `tool_call`/`tool_result` frames flow automatically through `BusToolLoopEventSink` because the runner already calls `ToolLoopEventEmitter`.

## Dependencies

**Internal (hard prerequisites):**

- **32-5** (Call-LLM endpoint + managed execution) — supplies `POST /api/v1/llm/call`, `ManagedAgent.RunAsync`, the `IInlineToolLoopRunner` (now in the API), and the `IToolLoopEventSink` seam this story makes live. The buffered contract this story must not break. **Hard prerequisite.**

**Internal (soft / co-evolving):**

- **32-20** (Interactive question-back) — produces the `question`/`answer` frame **content** (`request_input` tool + `IQuestionRouter`); this story owns the `question`/`answer` frame **shape + transport** so 32-20 plugs into the bus. Co-evolving; the tap ships with the other four frame types even if 32-20 lands later.
- **Epic 27** (prompt/convention render) — only transitively (via 32-5); not called here.
- **Epic 29** (cabinet) — only transitively (via 32-5 credential path); not reached by the tap.

**Reused infra (not blockers — already in the tree):**

- `SseWriter` (`Tamma.Api/Services/Engine/Lifecycle/SseWriter.cs`) — the SSE frame writer.
- `AdminTenantEventsSseEndpoint` — the hardening template (heartbeats, max-duration, error budget, scrub allowlist, `Last-Event-ID`/replay).
- `ToolLoopEventEmitter` + `IToolLoopEventSink` (`Tamma.Activities/ToolExecution/`) — the emitter whose null sink this story replaces.
- `WebhookSignalRegistry` (`Tamma.Activities/AgentDispatch/`) — the in-process registry discipline the bus mirrors.

**Consumers (downstream, not blockers):**

- **Dashboard** (`apps/dashboard` / the React observability dashboard) — subscribes to the tap to render a live run view.
- **`tamma` CLI** — `tamma` watch/tail command consumes the tap over the same JWT/ApiKey plane.
- **32-20** — its panel/human answerer surfaces `question`/`answer` frames in the same stream.

**External:** none new (SSE over the existing HTTP stack; in-process bus).

## Testing Strategy

1. **Endpoint auth.** Missing/invalid JWT+ApiKey → 401; valid auth + a `correlationId` owned by another tenant → 404 (no cross-tenant existence oracle); valid auth + own run → 200 + `text/event-stream`.
2. **SSE protocol.** Headers set via `SseWriter.WriteHeaders` (`text/event-stream`, `X-Accel-Buffering: no`); `: keepalive` emitted every 30s during a quiet run; clean close `event: end\ndata: {"reason":"run_complete"}` on `final`; `MaxStreamDurationSeconds` ceiling kicks an abandoned tab.
3. **Sink mapping (AC3).** `BusToolLoopEventSink.WriteEventAsync` maps `TOOL_LOOP.TOOL_EXECUTING`→`tool_call`, `TOOL_LOOP.TOOL_COMPLETED`→`tool_result`, `TOOL_LOOP.COMPLETED`→`final`; unmapped events ignored; `correlationId` threaded from `workflowInstanceId`.
4. **Bus semantics (AC5).** `PublishAsync` with 0 subscribers is a no-op and never throws; N subscribers each receive every frame; a slow subscriber's bounded channel drops oldest (no stall); `PublishAsync` never blocks the producer.
5. **Buffered non-regression (AC6).** The buffered `LlmCallResponse` from a `ManagedAgent.RunAsync` is byte-for-byte identical with 0 vs N subscribers attached; `LlmCallWorkflow.cs` `ForEach`/`RetryCheck` advance unchanged.
6. **`Accept` second mode (AC7).** A non-engine caller with `Accept: text/event-stream` gets a live SSE stream ending in `final`; the engine-shaped request (engine bearer + `application/json`) always gets buffered JSON, regardless of any `Accept` header munging.
7. **`EnableStreaming=false` (AC3).** The registration stays `NullToolLoopEventSink`; the tap returns an immediate clean close with no live frames (graceful, never an error).
8. **Mid-run connect + replay (AC8).** An observer connecting after start sees live frames from connect time; `?replay=true` first replays the run's `TOOL_LOOP.*`/`AGENT.RUN.*` DCB events (scrubbed) then switches to the live tail.
9. **Credential safety (AC9).** No frame, log, or bus payload contains a key / `BaseUrl` auth / raw provider header / raw prompt body / tool arguments or outputs; `token`/`tool_call`/`tool_result`/`final` payloads are asserted key-free; the scrub allowlist drops anything off-list.
10. **Frame vocabulary (AC4).** Each frame is `event: <type>\ndata: <json>` with `{ correlationId, seq, ... }`; `seq` is per-run monotonic (NOT the per-tenant `domain_events.SequenceNumber`); payload shapes per type asserted.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

3-4 days (the tap endpoint + the in-process bus + the live sink + the `Accept` second-mode branch + the replay catch-up; all SSE infra and the emitter already exist, so this is wiring + careful decoupling tests, not new subsystems).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmRunStreamEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/ILlmRunStreamBus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/LlmRunStreamBus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/RunStreamFrame.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/RunStreamFrameType.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/BusToolLoopEventSink.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/RunStreamFrameScrubber.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Modify (32-5-owned — add side-effect publish of `final`/`question`/`answer`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs` | Modify (32-5-owned — `Accept: text/event-stream` branch; engine stays buffered) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map tap endpoint; register `ILlmRunStreamBus`; swap `NullToolLoopEventSink`→`BusToolLoopEventSink` behind `EnableStreaming`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/LlmRunStreamEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Streaming/LlmRunStreamBusTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Streaming/BusToolLoopEventSinkTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Streaming/BufferedNonRegressionTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. SSE/streaming notes).
3. Read the managed-LLM deep-dive §3 (Streaming — buffered for the engine, SSE for humans) IN FULL and §6 item 4 (the "Streaming run tap" follow-on), and the call-LLM endpoint design §2.
4. Reviewed `AdminTenantEventsSseEndpoint.cs` (the hardening template), `SseWriter.cs` (the frame writer), `ToolLoopEventEmitter.cs` + `IToolLoopEventSink.cs` (the seam you make live), and `WebhookSignalRegistry.cs` (the in-process registry discipline).
5. Confirmed 32-5 has landed (buffered endpoint + the `IToolLoopEventSink` seam + the runner in the API) before wiring the bus.
6. Planned the TDD approach: the load-bearing invariant is **AC6 — the buffered contract is identical with 0 vs N subscribers.** Write that test first; it is the guardrail that the tap can never break the engine.

### Key Design Decisions

- **The tap is observability, never control flow.** The bus is fire-and-forget; publishing is a no-op with 0 subscribers and never throws into the producer. The buffered `/llm/call` path is the source of truth for the engine; the tap is a decoupled mirror. This is why the engine never needs SSE (deep-dive §3) and why `RetryCheck`/`SkipIfSucceeded`/the circuit breaker are structurally safe.
- **Human plane, not engine plane (AC2).** Unlike `/llm/call` (engine bearer), the tap is consumed by humans (dashboard JWT, CLI ApiKey). It rides `AuthenticatedAny`, with a SaaS ownership guard that 404s cross-tenant `correlationId`s — never a cross-tenant existence oracle.
- **In-process bus, single instance (open decision).** The bus mirrors `WebhookSignalRegistry`'s in-process model. Multi-instance fan-out (a tap connecting to instance B for a run on instance A) needs a distributed transport (Redis pub/sub or Postgres `LISTEN/NOTIFY`) — deferred as an explicit open decision (deep-dive §3 leaves this to the tap follow-on). Single-instance covers today's dashboard/CLI use.
- **Per-run `seq`, NOT `domain_events.SequenceNumber` (AC4).** The per-tenant `domain_events.SequenceNumber` is an independent per-schema BIGSERIAL — using it as a stream sequence would be wrong (and meaningless across tenants). The tap's `seq` is a per-run monotonic counter the bus assigns.
- **No new table; no Program.cs DROP-list entry.** This story adds **no** control-plane or tenant-schema table — the bus is in-memory, and the durable audit is the DCB `AGENT.RUN.*`/`TOOL_LOOP.*` events 32-5 already writes. So **no entry in `Program.cs`'s startup-reset "Wiping Tamma-managed public-schema tables" DROP list** and **no `ControlPlaneDbContextModelTests` edit** (the strict `BeEquivalentTo` list is untouched).
- **No-empty-fallback applies to ownership, not content.** The SaaS ownership guard fails closed (cross-tenant → 404, never a fall-through that leaks a foreign run). Consistent with `feedback_resolution_no_empty_fallback`.
- **`EnableStreaming` is the master switch.** When false, the sink stays `NullToolLoopEventSink` and the tap is a graceful no-op — never an error. The flag already gates the inert seam; this story makes it gate a live one.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who may open a run tap? | The sole user (keyed by `UserId`); any local run is streamable. | Any authenticated tenant member (JWT/ApiKey) — but only for runs the **tenant** owns. No per-user layer. |
| How is the run's owner determined? | The sole user owns all runs. | The `correlationId`'s run must belong to the caller's tenant (`X-Tenant-Id`/JWT tenant claim); a foreign `correlationId` → 404. |
| Where do the tapped events originate? | The user's (sole) run on the in-process bus; DCB replay from the user's event store. | The tenant's run on the in-process bus; `?replay` reads the tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`. Never cross-tenant. |
| Who can see another principal's run? | N/A (single user). | No one — members see only their tenant's runs; platform admin sees none (performance/action data is ALWAYS tenant-scoped, design ownership rule). |
| Does the engine ever consume the tap? | No — the engine uses the buffered `/llm/call`; the tap is human-only. | Same. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| A streaming failure breaks the buffered run (AC6) | Critical | The bus is fire-and-forget; `PublishAsync` never throws into the producer; all producer-side publishes wrap log-and-swallow; the **0-vs-N-subscribers identical-buffered-response** test is written first as the guardrail. |
| The `Accept: text/event-stream` branch leaks into the engine path (AC7) | High | `IsEngineCaller` (engine bearer) forces buffered `application/json`; the thin `CallLlmInlineActivity` never sets the SSE Accept; explicit engine-shaped-request test. |
| A slow/abandoned subscriber stalls the run (AC5) | High | Bounded per-subscriber channel (DropOldest, ~256); `MaxStreamDurationSeconds` ceiling; back-pressure drops frames, never blocks the producer (mirrors the admin SSE 50-row per-tick cap). |
| A frame leaks a secret (AC9) | High | `RunStreamFrameScrubber` allowlist (mirrors `AdminTenantEventsSseEndpoint.ScrubEvent`); tool **arguments/outputs are NOT streamed**, only names/ids/durations; key-free assertions in tests. |
| Cross-tenant run visibility (AC2) | High | SaaS ownership guard → 404 on foreign `correlationId`; tenant-scoped `IEventRepository` for replay; cross-tenant test. |
| Multi-instance: a tap can't see a run on another API instance | Medium | Documented open decision; single-instance is the supported topology today; distributed transport deferred to a follow-on. |
| `domain_events.SequenceNumber` misused as stream seq | Medium | Per-run monotonic `seq` assigned by the bus; never the per-schema BIGSERIAL; AC4 test. |
| 32-20 not yet landed → `question`/`answer` frames absent | Low | The tap ships with the other four frame types; `question`/`answer` shapes are defined and dormant until 32-20 produces content. |

### Success Metrics

- [ ] The buffered `LlmCallResponse` is byte-for-byte identical with 0 vs N tap subscribers (the engine boundary is untouched).
- [ ] `NullToolLoopEventSink` is no longer the registered sink when `EnableStreaming=true`; the live `BusToolLoopEventSink` carries `tool_call`/`tool_result`/`final` frames.
- [ ] A dashboard/CLI client can open `GET /api/v1/llm/runs/{correlationId}/stream` and observe a live run, ending cleanly on `final`.
- [ ] `grep` confirms zero secrets / tool arguments / prompt bodies in any SSE frame payload.
- [ ] No new DB table; `Program.cs` DROP list and `ControlPlaneDbContextModelTests` are unchanged.

## Related

- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§3 Streaming — buffered for the engine, SSE for humans; §6.4 the "Streaming run tap" follow-on; §5 interactive question-back for the `question`/`answer` frames)
- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§2 the call-LLM endpoint — buffered contract this tap mirrors)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-23-streaming-run-tap-plan.md`
- Sibling stories: `story-32-5/` (buffered endpoint + the `IToolLoopEventSink` seam this story makes live — **hard prerequisite**), `story-32-20/` (interactive question-back — produces the `question`/`answer` frame content)
- Reused infra: `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs` (hardening template), `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Lifecycle/SseWriter.cs` (frame writer), `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs` + `IToolLoopEventSink.cs` (the seam), `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` (in-process registry discipline)

## Logging Requirements

- **INFO**: tap opened (correlationId, tenantId, replay?); tap closed (correlationId, framesSent, durationMs, reason); live sink swapped in at startup (EnableStreaming=true).
- **DEBUG**: per-frame publish (frameType, correlationId, seq — **never the frame payload verbatim if it could carry model text**); subscriber attach/detach; back-pressure drop count.
- **WARN**: cross-tenant tap denial (404, correlationId, callerTenantId); subscriber channel saturated (drops occurring); a publish that hit a transient error (swallowed — never propagated to the run).
- **ERROR**: the tap endpoint failing to set SSE headers; a producer-side publish that threw despite the swallow guard (a bug — must be logged, never reach the run).
- **Structured context**: `{ correlationId, tenantId, frameType, seq, subscriberCount }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, stream, or buffer the resolved API key, `BaseUrl` auth, raw provider headers, raw prompt body, or tool arguments/outputs. `token` deltas (model output), `toolName`/`toolCallId`/`success`/`durationMs`, and `correlationId`/`seq` are safe; everything else is dropped by `RunStreamFrameScrubber`. Frame payloads, SSE wire bytes, and all logs are key-free by contract.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — the human-facing **streaming run tap** (`GET /api/v1/llm/runs/{correlationId}/stream` SSE) over an in-process `ILlmRunStreamBus`, fed by a live `BusToolLoopEventSink` replacing `NullToolLoopEventSink` (gated by `EnableStreaming`). Frame vocabulary `token`/`tool_call`/`tool_result`/`question`/`answer`/`final` correlated by `correlationId`. Reuses `SseWriter` + `AdminTenantEventsSseEndpoint` hardening. Optional `Accept: text/event-stream` second mode on `/llm/call` for direct human callers — engine stays buffered `application/json`, so the 32-5 buffered contract (and `ForEach`/`RetryCheck`/`SkipIfSucceeded`) is byte-for-byte unchanged. Depends on 32-5; cross-references 32-20 for the `question`/`answer` frame content. | Claude |
