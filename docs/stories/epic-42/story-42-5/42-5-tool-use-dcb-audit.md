# Story 42-5: Tool-Use DCB Audit

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **platform audit trail**, I want **every tool invocation** to emit a durable, secret-free DCB
event — invoked, succeeded, failed — tagged with `issueId`/`tenantId`/`toolName`/`permissionClass`, so
that a tool call carries the same 100%-audit, time-travel-debuggable trail as a document transition, and
"the agent deleted a VPS at 14:03 under autonomy 95, authorized by the orchestrator" is a queryable row.

## Priority

P0 / Wave 1 — the audit half of the security envelope. A `Destructive` capability without a durable
trail is unacceptable; this must precede any family shipping.

## The gap (READ FIRST)

Tool progress is emitted **ephemerally**: `ToolLoopEventEmitter` (`Tamma.Activities/ToolExecution/`)
writes `TOOL_LOOP.TURN_STARTED` / `TOOL_LOOP.TOOL_EXECUTING` / `TOOL_LOOP.TOOL_COMPLETED` to
`IToolLoopEventSink`, whose default is `NullToolLoopEventSink` (**discards everything**); the live sink
is an SSE/bus stream for the dashboard. None of that is durable.

**Corrected — a durable tool-call family already exists, and it never fires.** Story 32-6 landed
`IAgentTrailEmitter.ToolCallAsync(ctx, ToolCallRecord, ct)` → `AGENT.TOOL_CALL.SUCCESS` /
`AGENT.TOOL_CALL.FAILED` appended to the tenant's `domain_events` via `IEventRepository`, with the flat
tag contract from `AgentTrailTags.Build` (`agentId`, `role`, `provider`, `model`, `promptRef`,
`issueId`, `iteration`, `correlationId`, `credentialSource`), refs-not-bodies discipline
(`ArgsRef`/`ResultRef` are sanitized references, never raw payloads) and a never-throw-into-the-run
contract. It is DI-registered (`Program.cs` L829). But **zero rows are ever written**, for three
structural reasons: `InlineToolLoopResult.ToolCalls` is documented as *"empty today: the verbatim loop
tracks only a tool-call count, not per-call summaries"*; `ManagedAgent.EmitTrailToolCallsAsync` returns
immediately on an empty list; and it is called **only on the success path** (`ManagedAgent.cs` L453),
after the loop, so a failed or cancelled run emits nothing at all. It is also structurally post-hoc —
it can never carry a *pre-invocation* row, which is exactly what a `Destructive` tool that crashes
mid-call needs.

**Corrected — the emit mechanism this story specified cannot work.** An earlier draft routed `TOOL.*`
through `TammaEventEmitter` → the workflow's `tamma:events` transient list → `EventDrain`, "not a direct
repository call (the engine holds none)". Both halves are wrong:

- `TammaEventEmitter.Emit(ActivityExecutionContext context, IActivity source, …)` **requires** an Elsa
  activity context — it stamps `source.Id`, `source.Name` and `context.WorkflowExecutionContext.Id`, and
  appends to `context.WorkflowExecutionContext.TransientProperties`. `ParallelToolExecutor` is a plain
  class whose constructor takes only `ILogger<ParallelToolExecutor>`; no `ActivityExecutionContext`
  exists anywhere on the Api-side agent path.
- The rationale is inverted: the tool loop **no longer runs in the engine** (`Tamma.ElsaServer/Program.cs`
  L286–292 — the catalog was removed; the executors are registered in `Tamma.Api`), and `Tamma.Api`
  **does** hold `IEventRepository` directly — `AgentTrailEmitter`, `ManagedAgent`, `AlertEventEmitter`,
  `PromptEventsService`, `EscalationDispositionService` and ~15 others inject it.

**Corrected — the specified hook point misses the default execution path.**
`ParallelToolExecutor.ExecuteSingleToolAsync` is reached only when
`loopConfig.EnableParallelTools && _parallelExecutor != null && _toolRegistry != null`
(`InlineToolLoopRunner.cs` L335). `ToolLoopConfig.EnableParallelTools` defaults to **`false`** and
`ManagedAgent` passes `request.ToolLoopConfig ?? new ToolLoopConfig()`, so unless a caller opts in the
**sequential** branch (`InlineToolLoopRunner.cs` L406–431, executing at L462) is the *only* path taken.
Hooking the parallel executor alone would audit the opt-in path and leave the default one silent.

## Scope

**Assembly placement.** The emitter and its hook are **Api-side**. `ParallelToolExecutor` lives in
`Tamma.Activities` (the `TAMMA001`-analyzed engine surface) and stays untouched by this story; all
emission happens in `Tamma.Api.Services.Agents`.

1. **A durable `TOOL.*` DCB family, emitted from `InlineToolLoopRunner` across both branches.** Add a
   small Api-side `IToolAuditEmitter` in `Tamma.Api.Services.Agents`, modelled on `AgentTrailEmitter`:
   a **direct `IEventRepository.AppendAsync`**, tags built through `AgentTrailTags.Build`, never throwing
   into the run (a write failure logs WARN + a breadcrumb). Inject it into `InlineToolLoopRunner`
   alongside the existing optional collaborators and hook it at **both** branches:

   | Branch | Hook |
   |---|---|
   | **Sequential** (default — `EnableParallelTools == false`) | `TOOL.INVOKED` immediately before `executor.ExecuteAsync` (L462); terminal event after it, and in both the timeout and exception handlers. |
   | **Parallel** (opt-in) | `TOOL.INVOKED` per entry of `validForExecution` before `ExecuteToolsInParallelAsync`; terminal event per returned result (the executor returns one result per call, in input order, and never throws). |

   | Event | When | Key payload |
   |---|---|---|
   | `TOOL.INVOKED` | before execution | toolName, permissionClass, autonomyAtCall, toolCallId, argsRedacted |
   | `TOOL.SUCCEEDED` | `Success == true` | toolName, durationMs, outputSizeBytes |
   | `TOOL.FAILED` | `Success == false` / timeout / exception / rejected-by-gate | toolName, durationMs, failureReason (redacted) |

   These sit **alongside** (not replacing) the ephemeral `TOOL_LOOP.*` SSE events — SSE is the live UI
   feed; `TOOL.*` is the permanent record. (42-3 owns the pre-invocation governance events
   `TOOL.RESOLVED`/`DENIED`/`ESCALATED`/`AUTHORIZED`; 42-4 owns `TOOL.SECRET_ACCESSED`. This story owns
   the **invocation** trio and the shared emit path + redaction rule those events reuse.)

2. **The run context the emitter needs does not reach the runner — and `issueId` is dropped one level
   above it.** `IInlineToolLoopRunner.RunAsync` takes only a `correlationId` (no `tenantId`, no
   `issueId`). One level up, `ManagedAgent.RunContext` carries `TenantId`/`Role`/`CorrelationId` but
   **no `IssueId`**, and `ToTrailContext` never projects one — so `AgentTrailContext.IssueId` is null on
   every trail event emitted today, even though `ManagedAgentRequest.IssueId` is in scope (the repair
   -ring emitter uses it directly at `ManagedAgent.cs` L751). This story must (a) thread `request.IssueId`
   into `ToTrailContext`, and (b) extend `RunAsync` to take the resulting context. (b) is a real
   signature change: the `Mock<IInlineToolLoopRunner>` fixtures in `ManagedAgentTests` and
   `ManagedAgentContentValidationTests` (both `MockBehavior.Strict`) and `BufferedNonRegressionTests`
   must be updated.

3. **Redaction is never-hold + pattern, not value-match.** *Corrected:* an earlier draft specified
   "a value-match denylist (any substring equal to a resolved secret value)". That is retired — it
   requires holding the plaintext credential at the audit boundary, which 42-4 forbids by design (the
   credential is fetched immediately before the external call and never handed to the emitter). The
   layered rule instead is:
   - **never-hold** — the emit call site receives `(toolCall, descriptor, result-metadata)` only; there
     is no parameter through which a credential could arrive;
   - **field suppression** — argument fields named by the descriptor's `RequiredSecret` are dropped, not
     printed;
   - **pattern redaction** — everything that does cross goes through `ToolOutputHelper.RedactSecrets`
     (already applied to every tool output on both branches) and `CredentialRedactor.Clean`
     (`Tamma.Core.Redaction`, already the pre-persistence scrub for DCB event fields);
   - **truncation** — oversized args/outputs via `ToolOutputHelper.Truncate` (which itself redacts first).
   A tool with no `RequiredSecret` still passes through the pattern path.

4. **Tagging for lineage — reuse the landed contract.** Every `TOOL.*` event is tagged via
   `AgentTrailTags.Build(ctx, extra)` with `extra` adding `toolName`, `permissionClass`, `toolCallId`
   and `autonomyAtCall`; `issueId`, `tenantId`, `correlationId`, `agentId` and `role` come free from
   `AgentTrailContext`. `IEventRepository.AppendAsync` writes the row into the resolving tenant's
   `t_<hex>.domain_events`. Do **not** invent a second tag vocabulary.

5. **Reconcile the three sinks — and revive the dead one.** Document the invariant: `IToolLoopEventSink`
   = ephemeral live progress; `TOOL.*` = the durable audit of record; `AGENT.TOOL_CALL.*` (32-6) = the
   per-agent analytics trail. Because this story now collects a per-call summary anyway, it must also
   **populate `InlineToolLoopResult.ToolCalls`** (`ToolCallSummary(ToolCallId, ToolName, Success,
   DurationMs)`) from the same collection, so 32-6's `EmitTrailToolCallsAsync` stops being structurally
   dead. Leaving two families that both claim to record tool calls while one silently emits nothing is
   not an acceptable end state.

## Acceptance Criteria

1. A tool invocation emits `TOOL.INVOKED` then exactly one of `TOOL.SUCCEEDED`/`TOOL.FAILED` to
   `domain_events` (integration test asserts rows with matching `toolCallId`).
2. **Both branches are covered.** Two integration tests — one with `EnableParallelTools == false`
   (the default) and one with it `true` — each assert the full `TOOL.*` trio. The default-path test is
   mandatory; without it the suite can pass while the path every run actually takes emits nothing.
3. A failing / timing-out / throwing tool emits `TOOL.FAILED`, never a silent success — including the
   parallel executor's internal timeout and exception paths, which surface as
   `ToolExecutionResult.Success == false` rather than a throw.
4. **Redaction.** A tool call whose args carry a credential-shaped literal (e.g. `ghp_…`,
   `Authorization: Bearer …`, `api_key=…`) emits `TOOL.INVOKED` with the value replaced by the
   redaction placeholder; a field named by the descriptor's `RequiredSecret` is absent entirely. Plus a
   **structural** assertion that no emitter method accepts a credential parameter — the never-hold
   guarantee is a signature property, not a string search.
5. Every `TOOL.*` event carries a **non-null** `issueId` when the run has one (the test asserts the value
   round-trips from `ManagedAgentRequest.IssueId`, since `ToTrailContext` drops it today) plus
   `tenantId`/`toolName`/`permissionClass`/`toolCallId`, all built through `AgentTrailTags`; a replay
   test reconstructs the tool timeline for an issue from `domain_events` alone.
6. Durable emission is **sink-independent**: with `NullToolLoopEventSink` wired (no SSE) and
   `EnableStreaming == false`, the `TOOL.*` rows are still persisted.
7. The ephemeral `TOOL_LOOP.*` SSE events are unchanged (no regression to 32-x dashboard behavior).
8. `InlineToolLoopResult.ToolCalls` is populated for both branches, and a run that executes N tools
   produces N `AGENT.TOOL_CALL.*` rows via the existing 32-6 emitter (asserting the previously dead path
   now fires).
9. An event-store append failure never fails the run: a test with a throwing `IEventRepository` asserts
   the tool result is still returned and a WARN is logged (the `AgentTrailEmitter` AC7 posture).

## Events

Defines `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED`, and the shared Api-side emit path + redaction
rule inherited by every other `TOOL.*` event in the epic (42-3's governance events, 42-4's
`TOOL.SECRET_ACCESSED`). Uses the `AGGREGATE.ACTION.STATUS` convention (`TOOL` aggregate), matching
`AgentTrailEventTypes`.

## Single-user vs SaaS

Identical emission in both modes; the `tenantId` tag and `DomainEvent.TenantId` are populated in SaaS
(routing the row into the tenant schema's `domain_events`) and null/platform in single-user. No per-mode
behavior beyond the tag.

## Dependencies

- **42-1** — `PermissionClass` for the tag, `RequiredSecret` for the field-suppression set.
- **Story 32-6** — `IAgentTrailEmitter` / `AgentTrailContext` / `AgentTrailTags` (the tag contract,
  never-throw posture, and the `AGENT.TOOL_CALL.*` family this story revives). *Corrected: the
  `TammaEventEmitter` → `tamma:events` → `EventDrain` path is **not** a dependency — it is unusable
  here.*
- **42-4** — supplies the redaction rules; explicitly does **not** supply secret values to match against.
- **Wave 0.5 cleanup** — the two independently-sourced allowlists in `InlineToolLoopRunner` (the
  resolved-tool name list at L262 vs `loopConfig.AllowedTools` at L342/L419) both produce rejections that
  must be audited; 42-3 owns reconciling them, this story must emit for whichever survives.
- **Unblocks:** every family — no `Destructive` tool ships without this trail.

## Risks

- **Branch drift.** A future refactor adds a third execution path and the audit silently misses it —
  the exact failure this story is correcting. Mitigation: AC2's per-branch tests, and prefer one shared
  private emit helper called from both branches over duplicated inline appends.
- **Hot-path cost.** Two durable appends per tool call on a per-call (not batched) path — unlike the
  engine's `tamma:events` drain, there is no batching seam here. Mitigation: the emitter is
  fire-and-forget-shaped (never throws, never blocks the result) and tool calls are already
  DB-round-trip-scale operations; if volume becomes a problem the fix is a buffered writer behind the
  same interface, not fewer events.
- **Redaction gaps.** Pattern redaction is not DLP — a novel credential shape in a nested arg can slip
  through. Mitigation: never-hold makes the *bound* secret structurally unreachable (the only class the
  platform issues), and pattern redaction is the second line for user-supplied strings.

## Estimated Effort

Medium. ~3–4 days — a new Api-side emitter on the established `AgentTrailEmitter` pattern, two hook
points, the `RunAsync` signature change with its mock-fixture fallout, redaction wiring, and per-branch
replay tests.
