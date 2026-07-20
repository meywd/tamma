# Story 39-17: Orchestrator Agent — long-running LLM process with platform context and tools

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

As the **platform running at high autonomy (and the operators and users it works with)**,
I want the **orchestrator to be a long-running LLM agent process — a persistent session whose context spans the platform's state and whose tools reach everything: git, the DCB event store, logging, workflow control, the document store and lineage, and the acceptance rules — reachable by workflows and users over the 39-18 real-time channels**,
So that decisions (document acceptance, escalation disposition, guidance) are made by one stateful actor that already sees the whole system, instead of stateless per-turn `llm-call`s that must be hand-fed context each time — and so the accept step becomes a conversation with the orchestrator over its channel, not an embedded prompt dispatch.

## Priority

P0 — This story defines WHO routes and decides every 39-5 `AcceptanceRequest`. Without it, 39-6's ACCEPT stage has a channel (39-18) with nobody listening, and no one to assign tasks to users. It also becomes the resident consumer of every 39-8 escalation and the agent behind the 39-19 chat.

## Architectural Context (READ FIRST)

**The orchestrator agent does not replace the Elsa orchestration workflows.** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs` (`adl-orchestrator`) and its siblings remain the execution substrate — deterministic loop mechanics, dispatch, state. The agent is the **decision-maker riding above them**: it consumes requests off the 39-18 workflow channel, reasons with its tools, and answers with typed decisions that resume suspended workflows through the 39-8 surface.

**Relation to the llm-call mediation invariant — a deliberate, guarded exception.** The invariant ("every workflow-embedded LLM turn goes through `llm-call` dispatch") governs turns *inside* workflows and stands untouched. The orchestrator agent is not a workflow turn: it is a separate, first-class audited principal running an agentic tool loop (the ManagedAgent/agent-dispatch precedent: `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/*` executors, `AgentMonitorService`, `WebhookSignalRegistry`). The guardrail tests that police LLM access (`ActivitiesGuardrailTests`, `EngineExternalCallAnalyzerTests` style) must allowlist the agent host **explicitly and narrowly**, with a comment recording this story as the authority.

**Provider reality.** The agent runs on the existing multi-provider abstraction and is only fully exercisable against a configured real LLM (same constraint that parks 39-9). Everything else — the tool loop harness, the toolset contracts, channel consumption, decision emission, audit — is testable with a scripted fake provider in the established test style.

**Statefulness.** "Long-running" means a persistent session per orchestrator identity: context survives across decisions, with explicit context management (compaction/summarization of older turns; re-hydration from the event store after restart — the agent's memory of record is the DCB stream + document store, not its context window). Restart must not lose pending work: pending requests live in the 39-18 persisted outbox, not in the agent's head.

## Acceptance Criteria

1. **Agent host.** A hosted long-running service (its own process or a hosted service in the engine deployment — decided and documented here) runs the orchestrator agent: a persistent LLM tool-loop session with a stable orchestrator identity, connected as the consumer of the 39-18 workflow↔orchestrator channel. It starts, reconnects, and resumes consuming after crashes; a restart re-hydrates working context from the event store/document store and drains the persisted request outbox — no request is lost or double-answered (idempotent decision delivery via the 39-8 resume discipline).

2. **The toolset — closed, typed, audited.** The agent's tools are a closed registry (drift-tested like the taxonomy), covering at minimum: **git** (read: log/diff/blame/file content for the worked repos); **event store** (DCB queries by tags/issueId — the Story 4-7 query surface); **logging** (structured log search); **workflows** (list/inspect instances, dispatch, resume, cancel via the Elsa management APIs); **documents** (39-11 store + lineage reads); **acceptance rules** (`get_acceptance_rules` over 39-5's `IAcceptanceRulesResolver`); **issue/platform reads**. Every tool invocation emits an `ORCHESTRATOR.TOOL_INVOKED` DCB event (tool name, args digest, correlation) so the agent's reasoning trail is reconstructable. Write-capable tools (dispatch/resume/cancel) are individually allowlisted and RBAC-scoped; everything else is read-only.

3. **Acceptance + routing duty — the agent is the single routing point.** The agent consumes EVERY `AcceptanceRequest` from the workflow channel, reads the effective acceptance rules + **autonomy level (70–100)** via its tool (and receives the resolved payload embedded in the request for audit pinning), inspects the document + `Review` + lineage, and makes the ROUTING decision first: **decide itself** (the more so the higher the autonomy level) or **assign the decision to a user** — picking an assignee from the 39-20 `EligibleAssignees` set (initiator or repo access; teams/roles/permissions respected; server-validated so an out-of-set assignment is refused), which creates a 39-19 Task View task. A self-decision is a typed `AcceptanceDecision` (`Accept | RequestRevision(notes) | Escalate(reason)`) through the 39-8 resume path; an assignment defers to the human's decision through the same path. The 39-5 guardrail function wraps whichever answer arrives — a blocking review cannot be accepted, round bounds cannot be exceeded — and a test pins that. Self-decisions emit `APPROVAL.PROVIDED` with `channel=orchestrator` + the rules version; assignments emit the 39-20 `TASK.ASSIGNED` with the eligibility basis and autonomy context. At autonomy 70 the agent assigns nearly everything; at 100 it decides everything the rules allow — same code path, different dial reading.

4. **Escalation + guidance + chat duties.** The agent consumes 39-8 `ESCALATION.TRIGGERED` deliveries (full lineage payload) and can disposition them (`ESCALATION.RESOLVED`) or assign them to an eligible user; workflows can send it guidance queries; and it serves the 39-19 Orchestrator Chat — answering user questions and initiating workflows on a user's behalf — always within that user's server-enforced access scope (per-user tool attenuation: tools invoked while serving user X carry X's authorization, pinned by a cross-user test).

5. **Auditable mind.** From the DCB stream alone one can reconstruct, for any decision: the request, the rules version, the tools invoked (in order), and the decision with reasoning summary — and for any conversation, every turn (the 39-19 `CHAT.*` events cover the agent's replies as well as user messages, and workflow guidance exchanges are evented likewise). An integration test replays a decision's event trail and asserts completeness. No decision, reply, or guidance path exists that bypasses event emission.

6. **Per-mode ownership.** The agent's identity, provider binding, and configuration answer the CLAUDE.md two-scoping-models rule: single-user mode — one agent owned by the sole user; SaaS — per-tenant agent identity and configuration (which provider/model, context budget), tenant-owned, admin-editable. Written down before code.

7. **Deterministic test harness.** A scripted fake provider drives the tool loop end-to-end in tests: request in → scripted tool calls → decision out → suspended workflow resumed — plus restart-mid-request (outbox redelivery, no double resume), guardrail-override (fake agent says Accept on a blocking review ⇒ server converts to Escalate), routing (scripted assign-to-user lands a task for exactly the eligible user; an out-of-set assignment is refused server-side), and autonomy-dial behavior (same scripted scenario at level 70 vs 100 exercises assign vs self-decide). Real-LLM behavior is exercised only when a provider is configured (same posture as 39-9), and the story documents what remains unverified without one.

## Technical Notes

- Reuse before building: the agent-dispatch executor stack (`Tamma.Activities/AgentDispatch/*`) already runs external agentic processes with monitoring — evaluate hosting the orchestrator agent on that seam versus a new hosted service, and record the decision as an ADR in `.dev/decisions/`.
- Context management is part of this story's definition of done at the design level (what is summarized, when, what is always re-derivable from the stream), even if sophisticated compaction lands iteratively.
- The toolset should be exposed via the same mechanism 39-5's MCP channel names (`get_acceptance_rules` as an MCP-style tool) so the rules tool is one registry entry among many, not a special case.
- Cost/budget: a long-running LLM process needs spend guardrails (per-decision token budget, idle behavior = no burn). State them in config with safe defaults.
- CLAUDE.md's "SSE over WebSocket" decision governs one-way streaming and stands; the agent's bidirectional conversation rides the 39-18 SignalR channels — see that story's ADR note.

## Dependencies

- **Prerequisite:** 39-5 (acceptor contract + rules resolver + autonomy level + guardrails), 39-18 (the channels it consumes — lockstep), 39-8 (resume path + APPROVAL/ESCALATION events), 39-11 (document/lineage reads), 39-20 (eligible-assignee resolver + task events — lockstep), Story 4-7 event query surface.
- **Prerequisite (in place):** multi-provider abstraction; agent-dispatch executor stack; DCB store.
- **Feeds:** 39-6 (the routing point behind the ACCEPT stage), 39-19 (the chat it serves; the tasks it assigns), 39-12..39-15 (migrated lifecycles routed by it).

## Estimated Effort

6–8 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-20 | 1.0.0   | Initial story creation | Claude |
| 2026-07-20 | 1.1.0   | Routing duty per review: the agent is the single routing point for every `AcceptanceRequest` — decide itself vs assign to a 39-20-eligible user per the autonomy level (70–100); chat duty (39-19) with per-user tool attenuation | Claude |
