# Story 44-7: Loop Integration — Native Work Items as an Intake Source, the `issueId` Join, and the Broken-Selector Fix

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

As a **platform operator running Tamma unattended**,
I want the autonomous loop to select work from the native tracker as a first-class intake source, keyed on the same `issueId` string every document, event and approval already uses,
So that the tracker is where work *happens* rather than a second place to write things down — and so that a work item's documents, approvals and escalations attach to it with no adapter.

## Priority

P0 — **This is the story that makes the tracker matter.** Without it, Epic 44 ships a parallel record that duplicates the thing it was meant to replace. It also carries a live-bug fix worth landing on its own.

## Architectural Context (READ FIRST)

- **⚠ The current intake path is broken and always has been, in a way that fails silently.** `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs:186` and `:220` deserialize the response as `JsonSerializer.Deserialize<List<WorkItem>>(json, …)`, but `GET /api/engine/issues` returns a **wrapper object** — `Results.Ok(new { issues = r.Issues, total = r.Total })` (`apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:706`, mapped at `Tamma.Api/Program.cs:2823`). Deserializing a JSON object into `List<T>` throws `JsonException`, which is **swallowed by the catch at `:229-232`**, yielding `(candidates: empty, untriaged: 0)` — so the non-mock path *always* takes the `NothingFound` outcome (`:135`). Only `SimulateCandidates()` (`:254-265`, gated on `Anthropic:UseMock` at `:97`, or on an unset `Engine:CallbackUrl` at `:170-175`) functions. **The identical bug exists at `Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:92-93`.** There is **no unit test** for either activity.
- **A second shape mismatch sits behind the first.** Even unwrapped, the platform-neutral `Issue` record (`Tamma.Platforms.Abstractions/Models/Issue.cs:7-13`) has `string Number` and `string HtmlUrl`, while `WorkItem` (`SelectWorkItemActivity.cs:279-291`) expects `int Number` and `Url`.
- **`issueId` is a string everywhere it matters.** `DocumentInstance.IssueId` is `string` (`Tamma.Data/Entities/DocumentInstance.cs:37`); DCB events carry `issueId` in the `Tags` jsonb column (`Migrations/Tenant/20260610013731_InitialTenant.cs:103`); `TaskRef.IssueId` and `TaskAssigned.IssueId` are `string`/`string?` (`Tamma.Api/Services/Access/ITaskAudienceResolver.cs:34`, `Tamma.Core/Documents/Channels/ChannelMessages.cs:64`). **Nothing constrains it to a platform issue number.** This is the whole basis of the epic's D2.
- **`domain_events.IssueNumber` is `integer NULL`** with two indexes (`:102`, `:462-466`) and cannot hold `TAM-142`. Native items leave it null (44-5 D4).
- **The consuming workflow.** `Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:37` (`DefinitionId = "single-issue-cycle"`, `:42`) — 1 405 lines, real, tested (`tests/.../SingleIssueCycleRoutingTests.cs`, 615 lines; `SingleIssueCycleSafetyTests.cs`; `SingleIssueCycleMergeSlaTests.cs`). It consumes `WorkItemJson` (`:60`) and `IssueNumber` (`:62`) as workflow variables and persists no work-item row. It is dispatched by `Tamma.Activities/ADL/DispatchCycleActivity.cs:128` from `AdlOrchestratorWorkflow.cs:90-99`.
- **The status-write-back seam that already exists:** `Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs` and `Tamma.Activities/ADL/IssueStatusEvents.cs:30` (`ISSUE_STATUS.*`).
- **⚠ `SingleIssueCycleWorkflow.cs` is a heavily contended file.** `docs/sprint-status.yaml:583-585` and `docs/stories/epic-41/story-41-29/41-29-task-level-flow-router.md:262` record the merge order `40-2 → 40-4 → 40-5 → 41-29`. **This story must not add itself to that chain** — see Out of Scope and the plan's D3.

## Acceptance Criteria

1. **The selector bug is fixed and tested.** `SelectWorkItemActivity` and `FetchUntriagedItemsActivity` deserialize the wrapper shape `{ issues, total }` and map `Issue`'s `string Number`/`HtmlUrl` onto `WorkItem`'s `int Number`/`Url`, rejecting a non-numeric platform number loudly rather than defaulting to `0`. **Unit tests for both activities**, including a regression test that fails against the current code.

2. **The swallowed exception becomes a loud outcome.** The `catch` at `SelectWorkItemActivity.cs:229-232` no longer conflates "no candidates" with "the fetch failed": a fetch failure emits `ADL.WORKITEM.SELECT` with a failure status and takes a distinguishable path, so a broken intake is visible in the event stream instead of looking like an empty backlog. A test asserts a transport failure and a genuinely empty result produce different events.

3. **An intake-source abstraction.** `IWorkIntakeSource` in `Tamma.Activities/ADL/` with `Task<IReadOnlyList<WorkItem>> FetchCandidatesAsync(WorkIntakeQuery, CancellationToken)`, implemented twice: `PlatformIssueIntakeSource` (today's engine-callback behaviour, extracted unchanged apart from AC1) and `TrackerIntakeSource` (the native tracker over `GET /api/work-items`). Registration is a **fail-loud keyed registry** — an unknown configured source name throws at startup naming the accepted set.

4. **`SelectWorkItemActivity` selects across configured sources** and is otherwise unchanged in its outcomes (`Selected` / `NothingFound` / `NeedsTriage`), its label filtering, its priority resolution (`ResolvePriority`, `:237-243`) and its ordering. Its existing workflow-structure tests must pass unmodified.

5. **`WorkItem` gains `Key` (string) and `Source` (a `[Wire]` enum: `platform-issue | tracker`),** and `WorkItemJson` carries them. `Key` is the tracker's `WorkItemRef.ToWire()` for native items and the platform coordinate (`owner/repo#123`) for platform items — **one string that is always the `issueId`**.

6. **The `issueId` written into documents and events is `WorkItem.Key`.** For a native item that is `TAM-142`; for a platform item it is exactly the string used today, byte-for-byte, so no existing lineage changes. A test drives a native item through a lifecycle dispatch and asserts the resulting `DocumentInstance.IssueId` equals the work item's key, and that `GET /api/work-items/{key}/timeline` (44-5) shows the `DOCUMENT.*` rows.

7. **Tracker selection honours the tracker's own model.** `TrackerIntakeSource` selects by status (`ready` by default), an automation gate, ordering by `Rank` then priority — **not** by the `tamma-auto` label convention, which is a platform-issue mechanism. The equivalent native gate is a per-project `AutomationMode` field defaulting to off.

8. **Status write-back is opt-in per project and idempotent.** When enabled, the loop moves a native work item `ready → in_progress` on cycle start and `in_progress → in_review` when a PR opens, via the existing `POST /api/work-items/{id}/status`. Re-entry after a crash must not double-transition: a write-back to the status already held is a no-op, proven by a test that replays the transition twice. Default is **off** (epic README, Open question 4).

9. **No change to `SingleIssueCycleWorkflow.cs`.** The workflow already consumes `WorkItemJson` and `IssueNumber` as variables; native items flow through the same variables. `IssueNumber` is set to `0` for native items and every consumer that branches on it is audited and listed. A structure test asserts the file is untouched by this story.

10. **`ADL.WORKITEM.SELECT` gains `source` and `key` in its tags**, so an operator can tell which intake produced a cycle.

## Technical Notes

- AC1 and AC2 are independently valuable and should be reviewable as their own commit: they fix a live bug in a shipped activity with no test coverage, and they do not depend on anything else in Epic 44.
- The intake abstraction is deliberately at the *source* level, not the *item* level: `WorkItem` stays one shape, so `SingleIssueCycleWorkflow` and everything downstream sees no new type. That is what keeps AC9 achievable.
- `IssueNumber = 0` for native items is a compromise, and the audit in AC9 is the price. The alternative — widening the variable to a string — touches `SingleIssueCycleWorkflow.cs`, which this story must not do.
- Status write-back defaults off because it makes every workflow a tracker writer and needs an idempotency rule under re-entry (39-10's re-entry is `done` and real). Shipping it on by default would couple the tracker's correctness to workflow resumption on day one.

## Dependencies

- **Stories 44-0** (`WorkItemRef`), **44-1** (rows), **44-2** (`GET /api/work-items`, `POST /{id}/status`) — blocking.
- **Story 44-5** — AC6's timeline assertion. Blocking for that AC only.
- **Existing, no change required:** `SingleIssueCycleWorkflow`, `DispatchCycleActivity`, `AdlOrchestratorWorkflow`, `UpdateIssueStatusWorkflow`, `EngineEndpoints.GetIssues`.
- **Coordination:** `SingleIssueCycleWorkflow.cs` is contended by 40-2/40-4/40-5/41-29 (`docs/sprint-status.yaml:583-585`). AC9 keeps this story out of that chain; confirm before starting.

## Out of Scope

- **Any edit to `SingleIssueCycleWorkflow.cs`.** AC9 is a hard constraint, not an aspiration.
- Materializing `DecompositionTask`s or `PlanTask`s as work items. That would double-write every plan and give the same rows two sources of truth — deferred with reasons (epic README), and it is 41-29's adjacent territory.
- Agent-authored work items (an agent filing a bug it found). Gated on the product owner's answer to Open question 3, and it changes 44-2's catalog descriptors.
- Any platform API call — 44-8.
- Triage of native items. `IssueTriageWorkflow` and the `TRIAGE.*` families operate on platform issues; extending triage to native items is a follow-on.

## Estimated Effort

5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
