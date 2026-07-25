# Story 44-5: DCB Events (`PROJECT.*` / `WORKITEM.*` / `ITERATION.*`) and the Event-Name Drift Ratchet

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

As a **compliance owner and as an operator debugging a tracker**,
I want every tracker mutation to append a DCB event to `domain_events` under the platform's `AGGREGATE.ACTION.STATUS` convention — and I want that convention to be enforced by a test rather than by reviewer memory,
So that a work item's whole history is reconstructible from the event stream like everything else in the platform, and so that adding three families does not quietly erode a naming convention that ~300 constants currently follow on trust.

## Priority

P1 — Wave 1. The events are the tracker's audit trail and its activity feed (the epic defers a comments table precisely because the stream *is* the feed). The ratchet is a small deliverable with repo-wide value that belongs to whoever adds a family.

## Architectural Context (READ FIRST)

- **The event store.** `domain_events` (`apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260610013731_InitialTenant.cs:96-113`): `Id` uuid, `Type` varchar(255), `TenantId` uuid null, `IssueNumber` integer null, `Tags` jsonb, `Metadata` jsonb, `Data` jsonb, `CreatedAt` timestamptz, `SequenceNumber` bigserial (`:107-108`) — the total-order cursor consumers use instead of `Id`. Indexes at `:458-468`; later `CorrelationId` and `UserId` indexes in `20260703134959` and `20260704083332`.
- **The append API.** `IEventRepository.AppendAsync(DomainEvent)` — `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:7`; impl `Repositories/EventRepository.cs:50`. A null-tenant event delegates to `platform_events` (`:56-75`) and throws with a named message if no platform repo is wired (`:65-68`). DI at `Tamma.Data/DependencyInjection.cs:168`.
- **`IssueNumber` cannot carry a native key.** It is `integer NULL`; a `PROJ-123` key is not an int. Native tracker events leave it `NULL` and carry `issueId` in `Tags`, which is what every other query already uses (epic README §2).
- **The declaration convention:** `public static class <X>Events` (or `<X>EventTypes`) holding `public const string`, colocated with the emitting activity or service. Examples: `Tamma.Activities/ADL/CycleEvents.cs:44-47`, `Tamma.Activities/Documents/DocumentEvents.cs:28`, `Tamma.Api/Services/Jira/JiraEventTypes.cs:11`, `Tamma.Api/Services/Git/GitEventTypes.cs:13`. **~300 dotted constants across ~62 prefixes.**
- **The emission-inside-the-service precedent:** `Tamma.Api/Services/AcceptanceRules/AcceptanceRulesEventsService.cs:14` — a dedicated events service the mutating service calls, rather than emission inline in the endpoint.
- **⚠ There is no test asserting event-type naming or family completeness anywhere in the repo.** `TaxonomyDriftBuildTests.cs:69` covers `(role, action)` dispatch pairs; `ConventionSeedDriftTests.cs:28` covers seed keysets; `SensitiveActionEmissionCoverageTests.cs:29` covers catalogued emitters. `SensitiveActionCatalog`'s own accuracy "was verified by grep at authoring time, not by a test" (`Tamma.Core/Audit/SensitiveActionCatalog.cs:16-28`). This is a real gap and this story closes it.
- **The ratchet discipline to copy:** `KnownContractViolations` in `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — a seeded allowlist whose entries may only be **removed**, with a staleness check that fails the build when an allowlisted item now complies.
- **Names that are taken and must not be used:** `CYCLE.*` (`Tamma.Activities/ADL/CycleEvents.cs:44-47`), `SPRINT.*` (41-6's document lifecycle), `TASK.*` (reserved for 39-20 at `Tamma.Activities/Documents/ChannelEvents.cs:6-7`), `ISSUE.*` (`Tamma.Activities/ADL/MergeEvents.cs:48`), `ISSUE_STATUS.*` (`ADL/IssueStatusEvents.cs:30`).

## Acceptance Criteria

1. **Three event catalogues** in `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/`: `ProjectEvents.cs`, `WorkItemEvents.cs`, `IterationEvents.cs`, each a `public static class` of `public const string`, following `AGGREGATE.ACTION.STATUS`.

2. **Every tracker mutation emits exactly one terminal event**, covering at minimum:
   - `PROJECT.CREATED.SUCCESS` / `.UPDATED.SUCCESS` / `.ARCHIVED.SUCCESS`
   - `WORKITEM.CREATED.SUCCESS` / `.UPDATED.SUCCESS` / `.STATUS_CHANGED.SUCCESS` / `.ASSIGNED.SUCCESS` / `.REPARENTED.SUCCESS` / `.MOVED.SUCCESS` / `.COMMITTED.SUCCESS` / `.UNCOMMITTED.SUCCESS` / `.DELETED.SUCCESS` / `.LINKED.SUCCESS` / `.ORDERING_APPLIED.SUCCESS` / `.ORDERING_APPLIED.FAILED`
   - `ITERATION.CREATED.SUCCESS` / `.STARTED.SUCCESS` / `.CLOSED.SUCCESS` / `.PLAN_APPLIED.SUCCESS` / `.PLAN_APPLIED.FAILED`
   Exactly one per call — a test drives each mutation and asserts a single append.

3. **Tags are uniform and queryable.** Every tracker event carries `tags.issueId` = the work item's `WorkItemRef.ToWire()` (or, for project/iteration events, `tags.projectKey`), plus `tenantId`, `projectId`, `userId` and `correlationId` where available. `IssueNumber` is **left NULL** and a test asserts it.

4. **`STATUS_CHANGED` carries `from` and `to`.** A status transition whose event records only the new value is not replayable. Same for `.REPARENTED` (`fromParentId`/`toParentId`), `.ASSIGNED` (`fromAssignee`/`toAssignee`) and `.MOVED` (`fromRank`/`toRank`).

5. **Emission is inside the services, not the endpoints** — a `TrackerEventsService` following `AcceptanceRulesEventsService.cs:14`, called from `TrackerService` / `TrackerHierarchyService` / `IterationService` / the two apply services. **No endpoint or DTO changes** in this story.

6. **Emission failure policy is explicit and split.** Mutations that change authorization-relevant or ordering-relevant state (`ASSIGNED`, `STATUS_CHANGED`, `ORDERING_APPLIED`, `PLAN_APPLIED`, `DELETED`) emit **inside** the write transaction and a failure rolls the write back. Everything else is best-effort with a logged warning. The split is documented per constant and pinned by a test.

7. **A work item's timeline is reconstructible.** `GET /api/work-items/{id:guid}/timeline` returns the ordered event stream for the item's `issueId` — **including** `DOCUMENT.*`, `APPROVAL.*` and `ESCALATION.*` rows produced by Epic 39 workflows against the same `issueId`. This is the payoff of the join-key decision (epic README §2) and the reason a comments table is deferred. Cursor-paged on `SequenceNumber`, never `Id` or `CreatedAt`.

8. **The event-name ratchet.** A test in `apps/tamma-elsa/tests/Tamma.Core.Tests/Events/` reflects over every `public const string` whose value contains a `.` in every `*Events.cs` / `*EventTypes.cs` across `Tamma.Core`, `Tamma.Activities`, `Tamma.Api` and `Tamma.Platforms*`, and asserts the shape `AGGREGATE[.SUB].ACTION.STATUS` — segments `^[A-Z][A-Z0-9_]*$`, three or four segments. Existing violators are seeded into a **shrink-only allowlist**; a stale entry (now compliant) fails the build. Entries may only be removed.

9. **The ratchet is seeded honestly.** The allowlist is generated from the current tree, each entry carrying its `file:line`, and the count is pinned. It is **not** permitted to add the three new families to it — a test asserts no `PROJECT.`/`WORKITEM.`/`ITERATION.` constant appears in the allowlist.

10. **A family-collision test.** Asserts that no constant introduced by Epic 44 begins `CYCLE.`, `SPRINT.`, `TASK.`, `ISSUE.` or `ISSUE_STATUS.`, naming each incumbent's file.

## Technical Notes

- The timeline endpoint (AC7) is why the epic defers a comments table: for a work item that has run through the loop, the stream already interleaves the tracker's own events with `DOCUMENT.PRODUCED`, `APPROVAL.REQUESTED`, `ESCALATION.TRIGGERED` and the rest, in `SequenceNumber` order, for free. A comments table earns its keep only when humans discuss items *outside* a workflow.
- The ratchet will find violations. Expected classes from the survey: the two-prefix `GATE` / `APPROVAL.GATE` pair (`Tamma.Activities/ADL/MergeApprovalEvents.cs:57-59`), `TOOL_LOOP.*` SSE progress constants, and the lowercase task-queue lifecycle strings (`Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:196,213,229,241`) — the last of which are **SSE bus messages, not DCB types**, and are excluded by the file-name filter rather than allowlisted. Do not "fix" existing families in this story; seed and move on.
- `WORKITEM` is one word, no underscore, matching `ISSUE_STATUS`'s use of `_` only *within* a compound aggregate name. `WORK_ITEM` would also be defensible; the choice is pinned by AC8's shape test either way, and one form must be chosen before three catalogues are written.

## Dependencies

- **Stories 44-1, 44-2, 44-3, 44-4** — the services that emit. Blocking (44-3 and 44-4 each reserve their constants in their own plans so this story does not re-derive them).
- **Existing, no change required:** `IEventRepository`, `domain_events`, `AcceptanceRulesEventsService` as the shape.
- **Blocks:** 44-6 (renders the timeline), 44-9 (the dogfood import's audit trail).

## Out of Scope

- Renaming or fixing any existing event family. The ratchet seeds them and shrinks over time; a bulk rename is a separate change with its own blast radius.
- A projection table or read model over tracker events. The events are the audit trail; `work_items` is the state. Two would need a rebuild story.
- Emitting to `platform_events`. Tracker events always carry a tenant.
- Notifications, SSE fan-out, or channel messages on tracker events — deferred (39-18's audience resolver is a no-op stub; wiring to it would ship silence).

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
