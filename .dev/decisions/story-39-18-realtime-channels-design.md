# Story 39-18 — Real-Time Channels Design Decisions

**Status**: Locked (2026-07-22)
**Branch**: `claude/wiki-docs-sync-r31nvo`
**Author**: implementation pass on the impl-plan.

This ADR records the two decisions that could read as contradictory with
prior choices unless written down: the SSE-vs-SignalR scope split (D1)
and hosting BOTH hubs in `Tamma.Api` as a trust posture rather than a
process boundary (D2). It also states the single-instance fan-out stance
loudly, as the story requires.

---

## 1. SignalR for bidirectional channels; SSE stands for one-way streaming

**Decision**: The two real-time channels use **SignalR (ASP.NET Core
native)**, not raw WebSocket and not SSE. Server-side SignalR ships in the
shared framework (`Microsoft.NET.Sdk.Web`) — `AddSignalR()` / `MapHub<T>()`
need **no new server package**; only the test/client side takes
`Microsoft.AspNetCore.SignalR.Client`.

**Why this does not contradict CLAUDE.md's "SSE over WebSocket"**: that
decision governs **one-way event streaming** and **stands unchanged** —
`AdminTenantEventsSseEndpoint` and `/api/engine/events/state|logs` remain
SSE. These channels are **bidirectional request/decision conversation**
(the lifecycle sends an `AcceptanceRequest` and suspends; the orchestrator
answers with a decision that resumes the gate), which SSE cannot carry.
Two different problems, two different transports — no contradiction.

**SignalR JSON protocol**: the hub's payload serializer is taught the
`DocumentJson.Options` converters (the `DocumentState` wire enum + the
millisecond ISO-8601 timestamp), so a `ChannelEnvelope` round-trips over
the hub exactly as it does over the engine→API hop. `ChannelAudience` and
the polymorphic `ChannelMessage` carry their own
`[JsonConverter]`/`[JsonPolymorphic]` attributes, so they need no options
help.

## 2. Both hubs hosted in `Tamma.Api`; engine-internal surface honored as a TRUST POSTURE

**Decision**: BOTH hubs live in `Tamma.Api`, as **two SEPARATE hub
classes** (never one hub with per-method role checks):

- `/hubs/orchestrator` — the workflow↔orchestrator channel. Gated by the
  new `OrchestratorChannel` policy (authenticated AND (service-principal
  OR the 39-8 D6 `tamma:principal-type = orchestrator` claim) — the same
  trust class as `EngineServiceOnly`, i.e. the resume-family posture). A
  tenant member/admin/owner JWT authenticates but is neither, so it is
  **rejected** (AC5). This hub MUST be excluded from public API docs and
  nginx exposure, exactly like the `/api/engine` callbacks.
- `/hubs/user` — the user↔orchestrator/platform channel. `MemberAccess`;
  groups (`tenant:{t}`, `user:{t}:{u}`) are derived server-side from the
  JWT claims. No client method takes a group name, so a forged group-join
  is structurally impossible (a reflection test pins this).

**The story-vs-repo tension, resolved**: the story's technical note reads
"the workflow/orchestrator hub sits on the engine-internal surface like
the `*ResumeEndpoint` family." Taken literally that means hosting the hub
in `Tamma.ElsaServer`. But the engine references only `Tamma.Activities`
(no tenant DB, no user auth) and **already crosses to the API for durable
writes** (`EventPersistenceMiddleware` → `POST /api/engine/events`). An
engine-hosted hub could not persist or replay the `channel_outbox`.
Resolution honoring the note's **intent**: workflows still "publish via the
engine" — 39-6's `PublishAcceptanceRequestActivity` →
`EngineChannelPublisher` (in `Tamma.Activities`) →
`POST /api/engine/channel/outbox` (`EngineServiceOnly`) → outbox row +
hub fan-out. The engine-internal trust posture is preserved via the
dedicated policy + the separate hub + the nginx exclusion, not via the
process boundary. If review insists on an engine-hosted hub, only the hub
host and the transport swap change — the message set, outbox, and user
hub are unaffected.

## 3. Single-instance fan-out (said loudly)

Fan-out is **in-process, single-instance**, the same as `ILlmRunStreamBus`.
On a multi-pod deployment an agent connected to pod A misses a publish
from pod B **at write time** — but the `channel_outbox` is the source of
truth, so the DB-driven `ChannelOutboxSweeper` re-delivers it eventually,
and connect-time replay covers reconnects. **The outbox makes
single-instance safe, just slower on failover.** Cross-process fan-out
(Redis backplane / Postgres LISTEN-NOTIFY) is the SAME deferred open
decision as `ILlmRunStreamBus`'s — decided **once, for both** surfaces,
not here.

## 4. Transport is never the source of truth

Every request/escalation/guidance/task message is persisted to
`channel_outbox` **before** any hub send; consumers ack (idempotent,
per-recipient); on reconnect, unacked rows replay in UUID-v7 (time) order.
A duplicate hub delivery is safe by construction: consumers dedupe on
message id, ack is idempotent, and a duplicate decision submission hits
the 39-8 404/409 discipline — no double-resume. **No timeout anywhere
converts an unanswered request into a decision** (AC7). A dead channel
degrades to "decision arrives later," never to lost work.

## 5. Access-token-in-query leakage is bounded to `/hubs`

Browser WebSockets cannot set `Authorization`, so the JWT arrives as the
`?access_token=` query param. The `JwtBearerEvents.OnMessageReceived` read
of it is **scoped strictly to `/hubs` paths**, so the query token never
lands on other routes (or their request logs).

## 6. Deferrals recorded (dependency stories not yet landed)

- **39-17 (`PersistedOrchestratorInbox`)** — DEFERRED. It implements 39-17's
  `IOrchestratorInbox`, which does not exist yet. The `channel_outbox` this
  story builds is exactly what that future inbox will read; the SignalR hub
  is the delivery path this story ships.
- **39-19 (chat)** — chat relay is OFF. `UserChannelHub.SendAgentMessage` /
  `OrchestratorChannelHub.SendAgentReply` hand off to `IOrchestratorChatRelay`,
  whose stand-in (`AgentOfflineChatRelay`) refuses with an agent-offline
  result and records nothing. No `CHAT.*` events here; the outbox refuses a
  direct conversation-kind enqueue. AC6 over feature completeness.
- **39-20 (audience resolver)** — STUBBED fail-closed
  (`InitiatorOnlyTaskAudienceResolver`): only the issue initiator sees
  anything. 39-20 replaces the implementation behind the canonical
  `ITaskAudienceResolver` shape; this story's tests run against a capturing
  fake, so no test churn.
