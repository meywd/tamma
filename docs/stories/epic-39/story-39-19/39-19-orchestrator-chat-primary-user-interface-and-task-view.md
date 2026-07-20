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
- **Task View** — the WORK inbox. A list of tasks assigned to this user: acceptance decisions, reviews, approvals, clarification answers — each backed by a suspended workflow (a 39-8 bookmark) and resolved through the idempotent resume surface. Tasks arrive here when the orchestrator's autonomy routing (39-5/39-17) decides a human must decide, or when an escalation is assigned.

**Access is enforced server-side on both surfaces (39-20 is the authority):** a user sees tasks only for workflows they initiated or repos they can access; chat answers and chat-initiated actions are filtered/authorized by the same resolver. The agent must never be the enforcement point — it operates *within* a server-enforced permission envelope (the tools it wields on a user's behalf carry that user's scope), so a jailbroken prompt cannot widen access.

**Precedents:** the dashboards (`packages/dashboard`, `packages/dashboard-user`) are the host UI; the 39-18 user channel is the transport; `Permissions.Matrix` (`apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`) is the permission seam 39-20 extends; the resume surface (39-8) is how every Task View action lands.

## Acceptance Criteria

1. **Chat surface.** A conversation UI in the user dashboard connected over the 39-18 user channel to the 39-17 agent: per-user conversation sessions (scoped `user_id` + `tenant_id`, never shared between users), streamed replies, history persisted and reloadable. The agent answers with data reachable through its tools **restricted to the caller's access scope** — a question about a repo the user cannot access returns a permission-shaped refusal, asserted by test.

2. **Workflow initiation from chat.** The user can initiate workflows conversationally; the agent maps intent to a dispatch (issue decomposition, plan review, etc.) and confirms what it is about to start before dispatching. Server-side authorization checks the user's permission for that workflow + repo (39-20) — the dispatch records the initiating user (`initiatedBy`) in the workflow's context and DCB tags, which is what drives the "tasks for workflows they initiated" visibility rule. Unauthorized initiation attempts are refused server-side and the refusal is auditable.

3. **Task View surface.** A distinct inbox listing this user's assigned/eligible tasks: task type (acceptance decision, review, approval, clarification), the subject document + lineage link, the workflow it resumes, age, and autonomy context (why it was assigned — e.g. "autonomy level 82: Design documents require human acceptance"). Live-updates over the user channel; acting on a task drives the 39-8 resume surface; a completed task leaves the inbox for every eligible user (single completion, idempotent).

4. **Scoped delivery.** Task delivery respects 39-20 visibility exactly: initiator of the workflow OR access to the task's repo (through teams/roles/permissions). A test drives two users with disjoint repo access + one shared workflow and asserts each sees exactly their own set; single-user mode trivially scopes to the sole user.

5. **Chat cannot do what the Task View owner hasn't done.** A pending task can be discussed in chat ("what's this decision about?") but completing it always resolves through the same authorized resume path with the decider identity server-derived — no chat-side backdoor that resumes a workflow as someone else, pinned by test.

6. **Events.** Chat-initiated dispatches and task assignments/completions are visible in the DCB stream (`initiatedBy` tags; the 39-20 task events) — the audit story of "who asked for this and who decided" is reconstructable.

## Technical Notes

- Chat history storage is per-principal (two-scoping-models rule: `user_id` single-user, `user_id`+`tenant_id` SaaS) and is NOT the agent's memory of record — the agent re-grounds from the event store/tools; history is a UI affordance.
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
