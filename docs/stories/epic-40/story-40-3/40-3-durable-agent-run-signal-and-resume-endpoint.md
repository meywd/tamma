# Story 40-3: Durable Agent-Run Signal Plane + Resume Endpoint (cross-pod, restart-safe)

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

As a **platform operator running Tamma on more than one pod** (and across deploys),
I want the `workflow_run.completed` webhook that signals a finished coding run to **resume the
suspended workflow reliably — even when the webhook lands on a different pod than the one that
dispatched, and even after a restart**,
So that agent-run completion delivery is not a single-process, in-memory race that silently
degrades to polling (or fails) whenever the topology is not a single box.

## Priority

P0 — 40-2 makes the coding step suspend on a durable bookmark; this story is what *resumes* it
from the real webhook. Without it, 40-2's suspend has no production wake-up path other than the
poll fallback, and the multi-instance correctness gap the whole epic calls out stays open.

## Architectural Context (READ FIRST)

**Today the signal plane is in-memory and single-process.** `WebhookSignalRegistry`
(`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs`) is a
`ConcurrentDictionary<string, TaskCompletionSource<AgentWebhookSignal>>`. The waiting
`AgentMonitorService.WaitForWebhookAsync` (`AgentMonitorService.cs:97`) parks a TCS; the
webhook handler `InstallationRouterService.HandleWorkflowRunEvent`
(`apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:377`) calls
`PublishSignal` on `workflow_run.completed`. The registry's own XML doc is explicit about the
limit:

> *Scope: single process. … For distributed deployments where the ElsaServer process runs the
> activity, webhook mode falls back to poll (Auto) or fails (explicit Webhook).*

So on multi-pod deployments (or after a restart) the waiter's TCS and the webhook's publish can
live in **different processes** and never meet. The signal key is scoped by GitHub App
installation id (`AgentWebhookSignalKey`, review-finding-5) to prevent cross-tenant wakeups, but
it remains a process-local dictionary.

**40-2 changes the substrate — and that is the fix.** Once the coding step suspends on a real
**DB-persisted Elsa bookmark** (40-2) instead of an in-memory TCS, **any pod can resume it
through Elsa's bookmark store** — Elsa's persistence *is* the cross-pod backplane. What the
webhook still lacks is the Tamma **session id / bookmark name**: the `workflow_run.completed`
payload carries repo, head branch, run id, and installation id, but not the Tamma session id
that `LifecycleBookmarks.ForAgentRun(tenant, repo, branch, session)` needs. A small **persisted
signal/dispatch row**, written at dispatch time and matched by the webhook, bridges that gap
durably.

**The resume-endpoint precedent is 39-8.** `DocumentDecisionResumeEndpoint` (39-8) /
`DesignResumeEndpoint` / `ClarifyResumeEndpoint` are the tenant-folded resume endpoints to
mirror: recompute the bookmark name from the request, resume via Elsa's bookmark runtime, 404 on
no-bookmark, 409 on collision, tenant-fold so tenant A cannot resume tenant B.

## Acceptance Criteria

1. **Persisted agent-run signal row (durable, tenant-scoped).** A new table (e.g.
   `agent_run_waits`) records, written at dispatch (40-2's `Execute`): `{ tenant_id, repository,
   branch_name, session_id, installation_id, workflow_instance_id, bookmark_name, dispatched_at,
   status }` where `status ∈ { pending, received, timed_out }`. It survives process restart. One
   row per in-flight coding run; `(tenant_id, repository, branch_name, session_id)` unique.

2. **Webhook resolves the row and resumes the bookmark.** `HandleWorkflowRunEvent` (or a service
   it calls) matches an incoming `workflow_run.completed` by `(installation_id, repository,
   head_branch)` to the pending row(s), reads the `bookmark_name` + `session_id`, and **resumes
   the 40-2 bookmark through Elsa's persistent bookmark runtime** with the completion payload
   (run id, conclusion, artifacts url). This works regardless of which pod is handling the
   webhook vs. which pod dispatched. The row is marked `received`.

3. **Resume endpoint (mirrors 39-8).** An HTTP `AgentRunResumeEndpoint`
   (`apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/` or `Tamma.Api`) accepts a run-completion
   callback (auth per the existing webhook/HMAC path), recomputes the bookmark name via
   `LifecycleBookmarks.ForAgentRun`, resumes, and returns **404** when no matching bookmark/row
   exists, **409** on an ambiguous/duplicate match, tenant-folded so cross-tenant resume is
   impossible. It is the same resume the webhook path uses, exposed as an endpoint for
   retries/manual reconciliation.

4. **Poll fallback reconciled to be durable.** The existing poll path
   (`AgentMonitorService.PollAsync`) is repurposed as a **durable reconciler**: a periodic
   sweep (or the 40-2 `DelayFor` wake) reads `pending` rows past a threshold, polls
   `GET runs/{id}` via the mediated API, and if terminal, resumes the bookmark the same way the
   webhook does — so a missed webhook still completes without a live in-process poll loop. No
   `pending` row is left dangling past the row's timeout.

5. **Cross-pod delivery proven.** A test simulates the split: dispatch (suspend) on
   "instance/pod A", deliver the webhook on "instance/pod B" (a *second* host/service over the
   *same* store), and assert the workflow resumes correctly on B. The in-memory registry is NOT
   consulted for correctness.

6. **In-memory registry demoted, not required.** `WebhookSignalRegistry` may remain as a
   same-process fast-path (resume immediately without a store round-trip when the waiter is
   local), but correctness no longer depends on it: with the registry unwired, the durable path
   still resumes. The `AgentMonitorService` webhook/Auto/Poll mode logic is updated so "Webhook
   mode without the in-memory registry" is no longer a hard failure.

7. **Tenant-folded and installation-scoped.** The row match and the bookmark name both fold the
   tenant (and the installation id, preserving the review-finding-5 cross-tenant guard), so two
   tenants sharing an `owner/repo` + branch cannot resume each other's runs.

8. **Fail-loud, exactly-once resume.** A webhook matching an already-`received` row is a no-op
   (idempotent redelivery — GitHub redelivers). A resume of an already-burned bookmark is a
   logged no-op, not an error. A row with no resolvable bookmark after its timeout emits a loud
   diagnostic event and marks `timed_out` (the 40-2 `Timeout` edge handles the workflow side).

## Technical Notes

- **Elsa's bookmark store is the backplane — do not build a message bus.** The cross-pod
  delivery is achieved by resuming a persisted bookmark, which any pod can do against the shared
  DB. The signal row only supplies the missing `session_id`/`bookmark_name`; it is not a queue.
- **Keep the installation-id scoping.** `AgentWebhookSignalKey` already folds `installation_id`
  (review-finding-5); the persisted row must carry it and the match must use it, or the
  cross-tenant guard regresses.
- **Redelivery is expected.** GitHub redelivers webhooks; the `received` status + burned-bookmark
  no-op make resume idempotent (AC8).
- **The reconciler replaces the live poll loop, not the discover phase.** Discovery of the run id
  (dispatch returns 204 with no run URL) still happens; the *waiting* is durable now.

## Dependencies

- **Story 40-2 — HARD.** The durable bookmark this story resumes; the `ForAgentRun` name
  contract and completion payload shape are shared (lockstep). Blocking.
- **Story 39-10 (`LifecycleBookmarks`) — HARD (via 40-2).** The name builder recomputed on resume.
- **Story 39-8 — SOFT.** `DocumentDecisionResumeEndpoint` is the tenant-folded resume-endpoint
  pattern (404/409, tenant fold). Mirrored, not consumed.
- **Existing (verified):** `InstallationRouterService.HandleWorkflowRunEvent` (webhook receiver),
  `AgentMonitorService` (poll loop → reconciler), `AgentDispatchMediationService` (mediated
  `GET runs/{id}`), Elsa 3 persistent bookmark runtime + EF store, `TenantDbContext`.

## Estimated Effort

5-7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
