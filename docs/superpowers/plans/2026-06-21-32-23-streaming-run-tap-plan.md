# Story 32-23 — Streaming Run Tap (SSE for dashboard / CLI)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Add a human-facing **streaming run tap** — `GET /api/v1/llm/runs/{correlationId}/stream`
(SSE) — fed by an in-process `ILlmRunStreamBus` that 32-5's `ManagedAgent.RunAsync` and the
`IInlineToolLoopRunner` publish to **as a side-effect** while they run the buffered tool loop
server-side. Turn the inert `IToolLoopEventSink` seam LIVE by replacing `NullToolLoopEventSink` with a
`BusToolLoopEventSink` (gated by `EnableStreaming`). Frame vocabulary: `token`, `tool_call`,
`tool_result`, `question`, `answer`, `final`, correlated by `correlationId` (= workflow instance id).
**The engine's buffered `/llm/call` contract does NOT change** — observers are fully decoupled from the
engine's request/response call, so `ForEach`/`RetryCheck`/`SkipIfSucceeded` stay byte-for-byte
unchanged.

**Story file:** `docs/stories/epic-32/story-32-23/32-23-streaming-run-tap.md`
**Design specs:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§3 Streaming;
§6.4 the tap follow-on; §5 question-back) · `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§2 the call-LLM endpoint)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Api` + `Tamma.Activities`). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). `packages/api` is DELETED — all of this is C#.

---

## Non-goals (YAGNI guard)

- **NO change to the buffered `/llm/call` contract.** The engine path stays `application/json`
  request/response. The tap is an additive, decoupled observer. The single load-bearing invariant is
  **the buffered `LlmCallResponse` is identical with 0 vs N subscribers** (AC6).
- **NO new DB table, no migration, no Program.cs DROP-list entry, no `ControlPlaneDbContextModelTests`
  edit.** The bus is in-memory; the durable audit is the DCB `AGENT.RUN.*`/`TOOL_LOOP.*` events 32-5
  already writes.
- **NO distributed/multi-instance bus.** In-process only (mirrors `WebhookSignalRegistry`).
  Cross-instance fan-out (Redis/Postgres LISTEN-NOTIFY) is a documented deferred open decision.
- **NO new SSE infra.** Reuse `SseWriter` + the `AdminTenantEventsSseEndpoint` hardening (headers,
  heartbeats, max-duration, error budget, scrub allowlist, `Last-Event-ID`/replay).
- **NO production of `question`/`answer` content.** That is 32-20 (`request_input` + `IQuestionRouter`).
  This story defines the `question`/`answer` frame **shape + transport** so 32-20 plugs in.
- **NO new tool loop / sanitizer / metering.** All owned by 32-5. This story only publishes side-effect
  frames from the existing emitter + runner.

---

## Current-state findings (verified 2026-06-21, worktree @ epic32-specs)

| Seam | Where it is today | How 32-23 uses it |
|---|---|---|
| **Tool-loop event emitter** | `Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs` — emits `TOOL_LOOP.TURN_STARTED/TOOL_EXECUTING/TOOL_COMPLETED/TURN_COMPLETED/COMPLETED` to an injected `IToolLoopEventSink`; threads `workflowInstanceId` (= correlationId) through every call. | Unchanged. Its sink becomes `BusToolLoopEventSink` (was `NullToolLoopEventSink`). |
| **The inert sink** | `Tamma.Activities/ToolExecution/IToolLoopEventSink.cs` — only `NullToolLoopEventSink.Instance` registered; all events dropped; gated behind `EnableStreaming`. | Replaced (behind `EnableStreaming`) by `BusToolLoopEventSink` that publishes to the bus. |
| **SSE writer** | `Tamma.Api/Services/Engine/Lifecycle/SseWriter.cs` — `WriteHeaders` (`text/event-stream`, `no-cache`, `X-Accel-Buffering: no`) / `WriteEventAsync(eventName, payload)` / `WriteCommentAsync(comment)`. | Reused verbatim by the tap endpoint. |
| **SSE hardening template** | `Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs` — 30s keepalive, `MaxStreamDurationSeconds` (30m), `MaxConsecutiveErrors`, `Last-Event-ID` resume, `ScrubEvent` allowlist, clean `event: end`. | Copy/factor the heartbeat + max-duration + scrub + resume idioms into the tap. |
| **In-process registry discipline** | `Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` — `ConcurrentDictionary` per-key in-process fan-out (TCS signal model). | The bus mirrors this (per-correlationId set of bounded channels). |
| **Buffered endpoint + runner + sink seam** | 32-5: `Tamma.Api/Endpoints/LlmCallEndpoints.cs`, `Services/Agents/ManagedAgent.cs`, `Tamma.Activities/LlmCall/InlineToolLoopRunner.cs`. | Hard prerequisite. This story adds side-effect publishes + the `Accept` branch. |
| **Tenant event store (replay)** | `Tamma.Data/Repositories/IEventRepository.cs` (tenant-scoped `QueryAsync`). | `?replay=true` reads the run's `TOOL_LOOP.*`/`AGENT.RUN.*` DCB events (scrubbed) for catch-up. |
| **Human auth plane** | `AuthenticatedAny` / `MemberAccess` policies (JWT + `ApiKey` schemes) in `Tamma.Api/Program.cs`. | The tap requires this, **not** the engine bearer. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider`. | SaaS ownership guard vs single-user "any local run". |

**Key insight:** the only genuinely new code is the **bus** (`ILlmRunStreamBus`/`LlmRunStreamBus`),
the **live sink** (`BusToolLoopEventSink`), the **tap endpoint** (`LlmRunStreamEndpoints`), the
**frame records + scrubber**, and a few **side-effect publish calls** in `ManagedAgent` + the `Accept`
branch in `LlmCallEndpoints`. All SSE plumbing and the emitter already exist.

---

## Architecture

```
                 (engine path — UNCHANGED)
CallLlmInlineActivity --TammaApiClient.CallLlmAsync--> POST /api/v1/llm/call (Accept: application/json)
                                                              |
                                                    ManagedAgent.RunAsync (32-5, buffered)
                                                       |          \
                                          IInlineToolLoopRunner    \ side-effect publishes (fire-and-forget)
                                          (ToolLoopEventEmitter)     \
                                                   |                  v
                                          BusToolLoopEventSink ---> ILlmRunStreamBus  (in-process, per-correlationId
                                          (was NullToolLoopEventSink)      |           bounded channels, drop-oldest)
                                                                           |
              (human path — NEW, decoupled)                               |
  dashboard / tamma CLI --JWT/ApiKey--> GET /api/v1/llm/runs/{cid}/stream --subscribe--> live SSE frames
                                          (SseWriter + AdminTenantEventsSseEndpoint hardening)
                                          [?replay=true => DCB catch-up first, then live tail]
```

Producer side never blocks on, retries for, or fails because of observers. Publishing with 0
subscribers is a no-op. The buffered response the engine receives is identical regardless of taps.

Per-mode ownership (CLAUDE.md two-scoping-model): single-user = the sole user taps any local run, replay
from the user's store; SaaS = tenant member taps only the tenant's runs (foreign `correlationId` →
404), replay from the tenant `t_<hex>` store via the tenant-scoped `IEventRepository`, never
cross-tenant. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: T1 (frames + bus) → T2 (live sink) → T3 (tap endpoint + auth) → T4 (buffered non-regression +
producer publishes) → T5 (`Accept` second mode) → T6 (replay + mode/isolation). T1 is the foundation;
T4 is the load-bearing guardrail — write its 0-vs-N test before T2/T3 wiring if practical.

### T1 — Frame records + the in-process bus (`RunStreamFrame`, `ILlmRunStreamBus`)

**Scope:** The data shapes + the decoupled pub/sub. No SSE, no endpoint yet.

**Files (new):** `Services/Streaming/RunStreamFrame.cs`, `Services/Streaming/RunStreamFrameType.cs`
(`token`/`tool_call`/`tool_result`/`question`/`answer`/`final` constants),
`Services/Streaming/ILlmRunStreamBus.cs`, `Services/Streaming/LlmRunStreamBus.cs` (singleton;
`ConcurrentDictionary<correlationId, ConcurrentBag<Channel<RunStreamFrame>>>`; bounded channel
capacity ~256, `BoundedChannelFullMode.DropOldest`; per-run monotonic `seq`).

**Tests (first):** `tests/Tamma.Api.Tests/Streaming/LlmRunStreamBusTests.cs` —
- `PublishAsync` with 0 subscribers is a no-op and never throws.
- N subscribers each receive every published frame, in order.
- a slow subscriber's bounded channel drops oldest (no producer stall); `PublishAsync` returns
  promptly even when a channel is full.
- `seq` is per-run monotonic and independent per `correlationId`.

**Acceptance:**
- [ ] `RunStreamFrame { Type, CorrelationId, Seq, Payload }`; `RunStreamFrameType` has the six
      constants.
- [ ] Bus: publish never throws into the producer; 0-subscriber no-op; drop-oldest back-pressure;
      monotonic per-run `seq`.
- [ ] Builds clean; no analyzer warnings.

### T2 — Live sink (`BusToolLoopEventSink`) replacing the null sink (AC3)

**Scope:** `BusToolLoopEventSink : IToolLoopEventSink` that maps `TOOL_LOOP.*` event types to the frame
vocabulary and publishes to the bus. Registered in `Tamma.Api` behind `EnableStreaming`.

**Files:** new `Services/Streaming/BusToolLoopEventSink.cs`,
`Services/Streaming/RunStreamFrameScrubber.cs` (allowlist scrub mirroring
`AdminTenantEventsSseEndpoint.ScrubEvent`); modify `Tamma.Api/Program.cs` DI:
`if (EnableStreaming) AddSingleton<IToolLoopEventSink, BusToolLoopEventSink>(); else keep
NullToolLoopEventSink`.

**Mapping:** `TOOL_LOOP.TOOL_EXECUTING`→`tool_call {toolName,toolCallId,turn}`;
`TOOL_LOOP.TOOL_COMPLETED`→`tool_result {toolName,toolCallId,success,durationMs}`;
`TOOL_LOOP.COMPLETED`→`final {success?,totalTurns,totalTokens,exhausted,durationMs}`;
`TURN_STARTED`/`TURN_COMPLETED` ignored (or surfaced as no-payload progress). `correlationId` =
`workflowInstanceId` from the emitter event.

**Tests (first):** `tests/Tamma.Api.Tests/Streaming/BusToolLoopEventSinkTests.cs` — each `TOOL_LOOP.*`
maps to the right frame type + key-free payload; unmapped events publish nothing; `EnableStreaming=false`
path keeps `NullToolLoopEventSink` (graceful no-op).

**Acceptance:**
- [ ] `BusToolLoopEventSink` publishes `tool_call`/`tool_result`/`final` with correct payloads.
- [ ] DI swaps the sink only when `EnableStreaming=true`; `grep` confirms `Null` sink is no longer the
      sole registration in that branch.
- [ ] Payloads are key-free (scrubber applied).

### T3 — The tap endpoint + auth + SSE protocol (AC1, AC2)

**Scope:** `GET /api/v1/llm/runs/{correlationId}/stream`. `SseWriter.WriteHeaders`; subscribe to the
bus; write one frame per event; 30s `: keepalive`; `MaxStreamDurationSeconds` ceiling; clean
`event: end` on `final`. Auth on `AuthenticatedAny` (JWT + ApiKey) — NOT the engine bearer. SaaS
ownership guard → 404 on foreign `correlationId`.

**Files:** new `Tamma.Api/Endpoints/LlmRunStreamEndpoints.cs`; modify `Tamma.Api/Program.cs` (map
`app.MapGet("/api/v1/llm/runs/{correlationId}/stream", ...).RequireAuthorization("AuthenticatedAny")`).

**Tests (first):** `tests/Tamma.Api.Tests/Endpoints/LlmRunStreamEndpointsTests.cs` —
- missing/invalid auth → 401.
- valid auth + foreign-tenant `correlationId` → 404 (no cross-tenant existence oracle).
- valid auth + own run → 200 + `text/event-stream` + `X-Accel-Buffering: no`.
- `: keepalive` emitted during a quiet run; clean close `event: end {"reason":"run_complete"}` on
  `final`; `MaxStreamDurationSeconds` kicks an abandoned stream.

**Acceptance:**
- [ ] Endpoint streams live frames for an owned run; ends cleanly on `final`.
- [ ] Auth + ownership matrix passes (401 / 404 / 200).
- [ ] SSE headers + heartbeat + max-duration reused from the admin SSE template.

### T4 — Buffered non-regression + producer-side publishes (AC5, AC6)

**Scope:** Prove the buffered contract is untouched, then wire the side-effect publishes in
`ManagedAgent` (32-5-owned file): after the buffered loop returns, publish `final`; (32-20 will publish
`question`/`answer`). All wrapped log-and-swallow so a publish failure NEVER fails the run.

**Files:** modify `Services/Agents/ManagedAgent.cs` (add fire-and-forget publish calls — NOT control
flow).

**Tests (first):** `tests/Tamma.Api.Tests/Streaming/BufferedNonRegressionTests.cs` —
- the buffered `LlmCallResponse` from `ManagedAgent.RunAsync` is byte-for-byte identical with **0 vs N**
  subscribers attached (the guardrail).
- a publish that throws (injected failing bus) does NOT fail or alter the buffered run; it is logged.
- a minimal `LlmCallWorkflow` `ForEach`/`RetryCheck` run advances identically with taps attached.

**Acceptance:**
- [ ] 0-vs-N-subscribers identical-buffered-response test green (THE load-bearing invariant).
- [ ] Producer publishes are side-effects only; injected publish failure is swallowed + logged, never
      reaches the run.
- [ ] `final` frame carries the buffered turn summary.

### T5 — Optional `Accept: text/event-stream` second mode on `/llm/call` (AC7)

**Scope:** In `LlmCallEndpoints` (32-5-owned), add: when a **non-engine** caller sends
`Accept: text/event-stream`, stream the same bus frames inline and end on `final`, instead of buffered
JSON. The engine path (engine bearer + `application/json`) is explicitly excluded — buffered, unchanged.

**Files:** modify `Tamma.Api/Endpoints/LlmCallEndpoints.cs` (the `WantsEventStream && !IsEngineCaller`
branch).

**Tests (first):** extend `LlmRunStreamEndpointsTests` / a small `LlmCallAcceptModeTests` —
- non-engine caller + `Accept: text/event-stream` → live SSE ending in `final`.
- engine-shaped request (engine bearer + `application/json`) → buffered JSON, regardless of any `Accept`
  munging (AC7 guardrail).

**Acceptance:**
- [ ] Second mode works for direct human callers; engine always gets buffered JSON.

### T6 — Replay catch-up + mode/isolation (AC8, AC2)

**Scope:** `?replay=true` replays the run's `TOOL_LOOP.*`/`AGENT.RUN.*` DCB events (scrubbed) from the
tenant store as catch-up frames, then switches to the live tail. Prove SaaS isolation (foreign run →
404; replay reads only the tenant store) and single-user "any local run".

**Files:** extend `LlmRunStreamEndpoints.cs` (replay branch reusing the `AdminTenantEventsSseEndpoint`
resume idiom against `IEventRepository`); extend tests.

**Tests (first):** extend `LlmRunStreamEndpointsTests` —
- mid-run connect (no replay) sees only live frames from connect time.
- `?replay=true` replays the run's DCB events (scrubbed, key-free) then live frames.
- SaaS: two tenants — each can only tap its own run; replay reads its own `t_<hex>` store; no leakage.
- single-user: the sole user taps any local run.

**Acceptance:**
- [ ] Mid-run connect + `?replay` behaviours pass; replay is scrubbed + tenant-scoped.
- [ ] Cross-tenant isolation holds (404 + tenant-scoped replay).

---

## Story order & dependencies

External prereq (must land first): **32-5** (buffered `/llm/call` + `ManagedAgent.RunAsync` + the
`IInlineToolLoopRunner` in the API + the `IToolLoopEventSink` seam). Soft/co-evolving: **32-20**
(produces `question`/`answer` frame content; the tap ships the shape + transport dormant until then).
Internal: T1 → T2 → T3 → T4 (guardrail) → T5 → T6. Downstream consumers (dashboard, `tamma` CLI) are
NOT blockers.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Streaming"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~LlmRunStream"
# AC9 credential-safety check: no key/prompt/tool-args in any frame payload path
grep -rn "ApiKey\|BaseUrl\|/v1/messages\|prompt" apps/tamma-elsa/src/Tamma.Api/Services/Streaming
# AC3 sink-swap check: BusToolLoopEventSink is registered (not only the Null sink)
grep -rn "BusToolLoopEventSink\|NullToolLoopEventSink" apps/tamma-elsa/src/Tamma.Api/Program.cs
```

## Risks

- **Breaking the buffered contract (T4, AC6):** any control-flow coupling between the bus and the run
  fails the engine boundary. Mitigation: bus is fire-and-forget; publishes wrap log-and-swallow; the
  **0-vs-N-subscribers identical-response** test is the guardrail — write it first.
- **`Accept` second mode leaking into the engine path (T5, AC7):** Mitigation: `IsEngineCaller` (engine
  bearer) forces buffered; the thin step never sends the SSE Accept; explicit engine-shaped-request test.
- **Slow subscriber stalls the run (T1/T4, AC5):** Mitigation: bounded DropOldest channels +
  `MaxStreamDurationSeconds`; publish never blocks.
- **Secret leak in a frame (T2, AC9):** Mitigation: `RunStreamFrameScrubber` allowlist; tool
  args/outputs NOT streamed (only names/ids/durations); key-free assertions.
- **Cross-tenant run visibility (T3/T6, AC2):** Mitigation: SaaS ownership guard → 404; tenant-scoped
  `IEventRepository` for replay; two-tenant isolation test.
- **Multi-instance gap:** documented deferred open decision; single-instance is today's topology.
- **32-20 not landed:** the tap ships the other four frame types; `question`/`answer` dormant until
  32-20 produces content.

## Notes for the implementer

- **No new table.** The bus is in-memory; durable audit is the existing DCB events. Do **not** touch
  `Program.cs`'s startup-reset DROP list or `ControlPlaneDbContextModelTests` — adding either is a sign
  you over-scoped.
- **Reuse, don't rebuild.** `SseWriter` + the `AdminTenantEventsSseEndpoint` hardening are the template
  — copy the heartbeat/max-duration/scrub/resume idioms; don't invent a parallel SSE stack.
- **`seq` ≠ `domain_events.SequenceNumber`.** Use a per-run monotonic counter the bus assigns; the
  per-tenant BIGSERIAL is a separate concept and meaningless as a stream cursor.
- **Producer side is a side-effect.** `ManagedAgent`/runner publish to the bus; they never read from it
  or wait on it. If a test needs the run to behave differently when a subscriber is attached, the
  decoupling is wrong — stop and fix it.
