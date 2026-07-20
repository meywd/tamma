# Story 39-18: Real-Time Channels — workflow↔orchestrator and user↔orchestrator (SignalR)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **lifecycle workflow needing a decision, an orchestrator agent making them, and a user supervising or conversing with the platform**,
I want **two real-time bidirectional channels (SignalR/WebSocket): a workflow↔orchestrator channel carrying acceptance requests, escalations, guidance queries, and decisions; and a separate user↔orchestrator/platform channel carrying pending-decision notifications, human decisions, and user↔agent conversation**,
So that the accept step is a live conversation with its acceptor — the lifecycle sends an `AcceptanceRequest` on the acceptor's channel and suspends; the decision arrives on the channel and resumes it — identically for the orchestrator (full-auto) and a human (supervised), with the channel as transport and persisted state as the source of truth.

## Priority

P0 — 39-6's ACCEPT stage and 39-8's escalation delivery ride these channels; 39-17's agent is their primary consumer. Without them the acceptor contract has no transport.

## Architectural Context (READ FIRST)

**Transport, not truth.** The channels accelerate delivery; they never own state. Every `AcceptanceRequest`/escalation is persisted (outbox row + the 39-8 bookmark suspend) before any channel send; a disconnected consumer receives undelivered items on reconnect by outbox replay; decisions land through the 39-8 resume surface, which is idempotent (the 409/404 discipline) — so a duplicate channel delivery can never double-resume. A dead channel degrades to "decision arrives later," never to lost work.

**Two channels, two audiences, two auth postures:**

- **Workflow↔orchestrator channel** — engine/workflows on one side, the 39-17 agent on the other. Engine-internal trust posture (the `Tamma.Api` → engine hop precedent used by the resume endpoints): the agent authenticates as the orchestrator principal; workflows publish via the engine.
- **User↔orchestrator/platform channel** — dashboard/users. Tenant-scoped RBAC: group membership derived server-side from the authenticated principal (the bookmark tenant-folding posture applied to hub groups — a client can never subscribe itself into another tenant's traffic). Per-mode: SaaS folds `tenant_id`; single-user folds the sole user.

**Existing streaming surface and the SSE decision.** Today's real-time surface is one-way SSE (`apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs`, `Services/Streaming/ILlmRunStreamBus.cs`) plus the in-process `WebhookSignalRegistry`. CLAUDE.md's "SSE over WebSocket" decision governs **one-way event streaming and stands unchanged**; these channels are **bidirectional request/decision conversation**, which SSE cannot carry — record this scope split as an ADR in `.dev/decisions/` so the two decisions don't read as contradictory. `ILlmRunStreamBus`'s single-instance caveat (cross-process fan-out deferred: Redis / Postgres LISTEN-NOTIFY) applies here too — same open decision, same seam, decide once for both.

## Acceptance Criteria

1. **Typed message contracts in `Tamma.Core`.** A closed, drift-tested message set: `AcceptanceRequest` (document + `Review` + lineage refs + resolved rules payload + correlation/decision-session id), `AcceptanceDecision` (the 39-5 type + decider + rules version), `EscalationRaised` (the 39-8 lineage payload), `EscalationDisposition`, `GuidanceQuery`/`GuidanceReply`, `AgentConversationMessage`. No stringly-typed message bodies; serialization round-trip tests.

2. **Workflow↔orchestrator hub.** Workflows (via the engine) publish `AcceptanceRequest`/`EscalationRaised`/`GuidanceQuery`; the orchestrator agent subscribes, answers with `AcceptanceDecision`/`EscalationDisposition`/`GuidanceReply`; decisions are routed into the 39-8 resume surface (never applied directly by the hub). The 39-6 ACCEPT stage's full-auto path is exactly: persist request → publish → suspend on the 39-8 gate → decision arrives → guardrails → resume.

3. **User channel.** Supervised-mode acceptors receive live pending-decision notifications (their tenant's only) and decide from the dashboard — the decision travels the same resume surface as today's approve endpoints; users can converse with the 39-17 agent (`AgentConversationMessage` relay both ways). The dashboard (`packages/dashboard`) connects, shows a pending-approvals view fed by the channel, and hosts the conversation UI.

4. **Persisted outbox + reconnect replay.** Requests/escalations write an outbox row before publish; consumers ack; on reconnect, unacked items replay in order. A test kills the consumer mid-stream and asserts replay-without-loss and no double-resume (idempotent ack + the 39-8 discipline).

5. **Tenant isolation enforced server-side.** Hub group assignment is derived from the authenticated principal only; a test drives two tenants' clients concurrently and asserts zero cross-tenant delivery, and that a forged group-join attempt is refused. Single-user mode scopes to the sole user. Engine/agent hub methods reject non-orchestrator principals.

6. **Audit alignment.** Channel traffic does not invent a parallel audit trail: `APPROVAL.REQUESTED/PROVIDED` and `ESCALATION.TRIGGERED/RESOLVED` (39-8) remain the events of record, with the channel recorded in their existing `channel` field; the outbox is operational plumbing, queryable but not a second source of truth.

7. **Degraded-mode behavior stated and tested.** With no orchestrator connected (full-auto) or no user connected (supervised), the lifecycle still suspends cleanly, the request waits in the outbox, and delivery happens on connect — asserted by test. No timeout in this story silently converts an unanswered request into a decision.

## Technical Notes

- SignalR (ASP.NET Core native) is the default implementation; raw WebSocket only if a concrete constraint rules SignalR out — record the choice in the ADR alongside the SSE scope split.
- Hub placement follows the existing split personality: the public user hub lives in `Tamma.Api` (RBAC middleware available); the workflow/orchestrator hub sits on the engine-internal surface like the `*ResumeEndpoint` family. Do not put both behind one hub with role checks per method.
- The decision-session id inside `AcceptanceRequest` IS the 39-8 bookmark session id — one identifier from suspend to resume, so correlation never needs a join table.
- Scale-out (multiple engine/API instances) inherits the `ILlmRunStreamBus` open decision; if it stays single-instance for now, say so loudly in the ADR (the outbox makes single-instance safe, just slower on failover).
- Notification fan-out to email/push stays out of scope (39-8's note) — these channels are the live surfaces, not the notification system.

## Dependencies

- **Prerequisite:** 39-5 (`AcceptanceDecision`, rules payload), 39-8 (resume surface + events — lockstep), 39-2 (lineage refs in messages).
- **Lockstep:** 39-17 (the agent consumer), 39-6 (the ACCEPT stage publisher).
- **Prerequisite (in place):** SSE endpoints + `ILlmRunStreamBus` (scope-split precedent), resume-endpoint auth postures, `ITammaModeProvider`.
- **Feeds:** 39-12..39-15 (migrated lifecycles ride the channels), dashboard approvals/conversation UX.

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-20 | 1.0.0   | Initial story creation | Claude |
