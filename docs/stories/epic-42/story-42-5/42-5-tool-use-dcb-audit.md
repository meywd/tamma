# Story 42-5: Tool-Use DCB Audit

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **platform audit trail**, I want **every tool invocation** to emit a durable, secret-redacted DCB
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
is an SSE/bus stream for the dashboard. **None of it is durable** — nothing writes a tool call to the
`domain_events` DCB store. Documents get a `DOCUMENT.*` trail via `TammaEventEmitter` → the workflow's
`tamma:events` transient list → `EventPersistenceMiddleware`/`EventDrain` → durable `domain_events`
(see `EmitTestingEventActivity` for the exact pattern — no `IEventRepository` is held inside the Elsa
engine). Tools have **no equivalent durable family**.

## Scope

1. **A durable `TOOL.*` DCB family**, emitted at the `ParallelToolExecutor.ExecuteSingleToolAsync` hook
   points (the same places `EmitToolExecuting`/`EmitToolCompleted` fire the ephemeral SSE events) via
   the `TammaEventEmitter` → `tamma:events` drain — **not** a direct repository call (the engine holds
   none, per the established pattern):

   | Event | When | Key payload (redacted) |
   |---|---|---|
   | `TOOL.INVOKED` | before `ExecuteAsync` | toolName, permissionClass, autonomyAtCall, argsRedacted |
   | `TOOL.SUCCEEDED` | `Success == true` | toolName, durationMs, outputSizeBytes |
   | `TOOL.FAILED` | `Success == false` / timeout / exception | toolName, durationMs, failureReason (redacted) |

   These sit **alongside** (not replacing) the ephemeral `TOOL_LOOP.*` SSE events — SSE is the live UI
   feed; `TOOL.*` is the permanent record. (42-3 already defines the pre-invocation governance events
   `TOOL.RESOLVED`/`DENIED`/`ESCALATED`/`AUTHORIZED`; 42-4 defines `TOOL.SECRET_ACCESSED`. This story
   owns the **invocation** trio and the shared redaction rule.)

2. **Redaction is mandatory and centralized.** Args are redacted before they enter any event: fields
   named by the tool's `RequiredSecret` (42-1) plus a value-match denylist (any substring equal to a
   resolved secret value) are replaced with `«redacted»` via `ErrorRedactor`/`SecurityHelpers`. A tool
   with no `RequiredSecret` still passes args through the sanitizer path. Oversized args are truncated
   (reuse `ToolOutputHelper.Truncate`).

3. **Tagging for lineage.** Every `TOOL.*` event carries `issueId` (the anchor — Epic 39: documents
   reference issues), `tenantId` (empty/platform-scope in single-user), `toolName`, `permissionClass`,
   and the `workflowInstanceId`/`toolCallId` correlation already threaded through the emitter (Story
   32-23). This makes the per-issue lineage `Issue → … → tool calls → outcome` queryable next to the
   document lineage.

4. **Reconcile the two sinks.** Document the invariant: `IToolLoopEventSink` = ephemeral live progress;
   `TOOL.*` DCB = durable audit. The hook fires both at the same point so a replay reconstructs the tool
   timeline from `domain_events` even when no SSE consumer was attached.

## Acceptance Criteria

1. A tool invocation emits `TOOL.INVOKED` then exactly one of `TOOL.SUCCEEDED`/`TOOL.FAILED` to the
   durable DCB store (integration test asserts rows in `domain_events` with matching `toolCallId`).
2. A failing/timing-out/throwing tool emits `TOOL.FAILED` (error-status), never a silent success — a
   degraded terminal is a loud audit row (mirrors `EmitTestingEventActivity`'s error-status posture).
3. Redaction: a tool call whose args contain a bound secret value emits `TOOL.INVOKED` with the value
   replaced by `«redacted»` (test greps the emitted event for the known value and asserts absence).
4. Every `TOOL.*` event carries `issueId`/`tenantId`/`toolName`/`permissionClass` tags; a replay test
   reconstructs the tool timeline for an issue from `domain_events` alone.
5. Durable emission happens **regardless of sink**: with `NullToolLoopEventSink` wired (no SSE), the
   `TOOL.*` rows are still persisted (test).
6. The ephemeral `TOOL_LOOP.*` SSE events are unchanged (no regression to 32-x dashboard behavior).

## Events

Defines `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED` and the shared redaction rule inherited by all
other `TOOL.*` events in the epic. Uses the `AGGREGATE.ACTION.STATUS` convention (`TOOL` aggregate).

## Single-user vs SaaS

Identical emission both modes; `tenantId` tag is populated in SaaS (routing the event into the tenant
schema's `domain_events`, per Epic 39 39-21 tenant-scoped stores) and empty/platform in single-user. No
per-mode behavior beyond the tag.

## Dependencies

- **42-1** (`PermissionClass` for the tag, `RequiredSecret` for the redaction field set).
- **Epic 39 / Story 32-23** the `TammaEventEmitter` → `EventDrain` durable path + the
  `workflowInstanceId`/`toolCallId` correlation already in `ToolLoopEventEmitter`.
- **42-4** (secret values to redact against).
- **Unblocks:** every family — no `Destructive` tool ships without this trail.

## Risks

- **Redaction gaps.** A secret embedded in a nested arg or reflected in output could slip through.
  Mitigation: a single centralized redactor at the emit boundary + a value-match denylist + the
  grep-for-value test (shared with 42-4 AC4) run against every family.
- **Hot-path cost.** Two extra durable events per tool call. Mitigation: emission is via the transient
  list + batched drain (no per-call DB round-trip inside the engine), matching the testing-pipeline's
  volume.

## Estimated Effort

Medium. ~3 days (new event family on an established emitter pattern + redaction + replay tests).
</content>
