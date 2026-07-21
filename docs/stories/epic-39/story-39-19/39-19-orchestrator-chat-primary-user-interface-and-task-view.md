# Story 39-19: Orchestrator Chat — primary user interface, and the Task View

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

As a **user of the platform (any role, single-user or SaaS)**,
I want **to talk with the orchestrator as my main interface to the system — a chat where I ask questions about anything I'm allowed to see and initiate workflows — and, separately, a Task View that lists the concrete work items assigned to me (reviews, approvals, required inputs)**,
So that conversing with the system and doing the system's assigned work are two distinct surfaces: chat is open-ended and orchestrator-mediated; the Task View is a scoped inbox of things workflows are actually suspended on, and neither surface ever shows me anything my access doesn't cover.

## Priority

P0 — This is the epic's user-facing face: 39-17's agent needs a front door, and the orchestrator's task-assignment decisions (autonomy routing, 39-5/39-17) need an inbox to land in. Without the Task View, an assigned decision has nowhere to be seen or acted on.

## Architectural Context (READ FIRST)

**Two surfaces, deliberately distinct:**

- **Orchestrator Chat** — the PRIMARY interface. A conversational UI where the user talks to the 39-17 agent over the 39-18 user channel: ask questions ("why did issue #42 escalate?", "what's running right now?"), get answers grounded in the agent's tools (event store, documents, workflows, logs), and **initiate workflows** ("decompose issue #57", "run a plan review on PR #12") — the agent translates intent into workflow dispatches it is permitted to make on the user's behalf.
- **Task View** — the WORK inbox. A list of the tasks this user can act on: acceptance decisions, reviews, approvals, clarification answers — each backed by a suspended workflow (a 39-8 bookmark) and resolved through the idempotent resume surface. **Tasks are addressed to a tenant role, not an exact user** (settled design review 2026-07-21): the orchestrator's autonomy routing (39-5/39-17) assigns to a role, and the task appears in the inbox of every role-holder within their visibility scope; the same task can equally be handled from chat by asking the orchestrator. First authorized completion wins, everyone's inbox clears.

**Access is enforced server-side on both surfaces (39-20 is the authority):** a user sees tasks only for workflows they initiated or repos they can access; chat answers and chat-initiated actions are filtered/authorized by the same resolver. The agent must never be the enforcement point — it operates *within* a server-enforced permission envelope (the tools it wields on a user's behalf carry that user's scope), so a jailbroken prompt cannot widen access.

**Precedents:** the dashboards (`packages/dashboard`, `packages/dashboard-user`) are the host UI; the 39-18 user channel is the transport; `Permissions.Matrix` (`apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`) is the permission seam 39-20 extends; the resume surface (39-8) is how every Task View action lands.

## Acceptance Criteria

1. **Chat surface.** A conversation UI in the user dashboard connected over the 39-18 user channel to the 39-17 agent: per-user conversation sessions (scoped `user_id` + `tenant_id`, never shared between users), streamed replies, history persisted and reloadable. The agent answers with data reachable through its tools **restricted to the caller's access scope** — a question about a repo the user cannot access returns a permission-shaped refusal, asserted by test.

2. **Workflow initiation from chat.** The user can initiate workflows conversationally; the agent maps intent to a dispatch (issue decomposition, plan review, etc.) and confirms what it is about to start before dispatching. Server-side authorization checks the user's permission for that workflow + repo (39-20) — the dispatch records the initiating user (`initiatedBy`) in the workflow's context and DCB tags, which is what drives the "tasks for workflows they initiated" visibility rule. Unauthorized initiation attempts are refused server-side and the refusal is auditable.

3. **Task View surface.** A distinct inbox listing the role-addressed tasks this user is in the audience for (role-holder ∩ visibility scope): task type (acceptance decision, review, approval, clarification), the addressed role, the subject document + lineage link, the workflow it resumes, age, and autonomy context (why it was assigned — e.g. "autonomy level 82: Design documents require human acceptance — role: architect"). Live-updates over the user channel; acting on a task drives the 39-8 resume surface; a completed task leaves the inbox for every eligible role-holder (single completion, idempotent).

4. **Scoped delivery.** Task delivery respects 39-20 visibility exactly: initiator of the workflow OR access to the task's repo (through teams/roles/permissions). A test drives two users with disjoint repo access + one shared workflow and asserts each sees exactly their own set; single-user mode trivially scopes to the sole user.

5. **Chat is a full task surface — through the same door.** A pending task can be discussed ("what's this decision about?") AND completed conversationally ("approve it") by any user in its role audience — the agent maps the intent onto the SAME authorized resume path the Task View uses, with the decider identity server-derived from the chatting user and completion authorized against the audience at act time. No chat-side backdoor that resumes a workflow as someone else or as an out-of-audience user, pinned by test; a chat completion emits the same `TASK.COMPLETED` (surface `chat`) and clears every role-holder's inbox.

6. **Everything is events — chat included.** Every conversation turn is recorded in the DCB event store like everything else in the system: a `CHAT.*` event family (`AGGREGATE.ACTION.STATUS` convention — e.g. `CHAT.MESSAGE.RECEIVED` for a user turn, `CHAT.MESSAGE.SENT` for an agent reply, `CHAT.WORKFLOW.INITIATED` linking a chat turn to the dispatch it caused) flowing through the standard persistence path, tagged `userId`, `tenantId`, `conversationId`, and `issueId`/`correlationId` where the turn concerns one. Chat-initiated dispatches and task assignments/completions correlate to their originating turn (`initiatedBy` + the 39-20 task events), so "who asked for this, what was said, and who decided" is reconstructable from the stream alone — asserted by a replay test. Secrets/credentials never enter event payloads (the existing redaction rules apply to chat content too).

## Technical Notes

- Chat history is a **projection of the `CHAT.*` event stream**, not a parallel store — the event store is the record (per the DCB principle), and the history view is rebuildable from it. Read scoping is per-principal (two-scoping-models rule: `user_id` single-user, `user_id`+`tenant_id` SaaS). Neither is the agent's memory of record — the agent re-grounds from the event store/tools.
- The agent's per-user tool scoping is the critical design point: tool invocations made while serving user X execute with X's authorization context (impersonation-with-attenuation), not the agent's own broader scope. Decide the mechanism (scoped tool tokens vs per-call context) in 39-17's toolset design and record it in the ADR.
- The Task View reads the same pending-approvals data 39-18 AC3 sketched — this story upgrades that sketch into the full inbox with visibility scoping and autonomy context.
- Keep chat availability degradation graceful: agent offline ⇒ chat says so and queues nothing silently; the Task View is fully functional without the agent (tasks come from the outbox/store, actions go to the resume surface).

## Dependencies

- **Prerequisite:** 39-17 (the agent behind the chat), 39-18 (user channel), 39-8 (resume surface for task actions), 39-20 (visibility/eligibility resolver — lockstep), 39-11 (lineage links in tasks).
- **Prerequisite (in place):** dashboards, `Permissions.Matrix`, auth middleware.
- **Feeds:** the autonomy routing loop (39-5/39-17) — assigned tasks land here; escalation handling UX.

## Estimated Effort

6–8 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-20 | 1.0.0   | Initial story creation | Claude |
