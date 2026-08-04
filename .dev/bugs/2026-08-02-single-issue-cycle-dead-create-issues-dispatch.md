# `SingleIssueCycleWorkflow` defer/split branches dispatch `"create-issues"`, a workflow definition that does not exist — both triage outcomes dead-end

- **Date:** 2026-08-02
- **Status:** RESOLVED (2026-08-03, Story [40-8](../../docs/stories/epic-40/story-40-8/40-8-triage-outcome-dead-ends-and-the-create-issues-workflow.md)) —
  `CreateIssuesWorkflow` (`DefinitionId = "create-issues"`, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CreateIssuesWorkflow.cs`)
  now declares the id both dispatch sites target: it creates one platform issue per draft via the
  mediated `POST /api/engine/create-issue` route (`CreateIssuesActivity`, activity-side
  `IIssueCreateClient` seam), always completes (malformed/empty input → 0 created + warning; per-item
  failure → loud `ISSUES.CREATE_ITEM.FAILED` + Failure outcome routed to Finish), and is idempotent on
  re-run (platform-side exact-title dedupe — a crash re-run never double-creates). The CLASS is closed
  by the structural guard `DispatchTargetStructuralTests.EveryDispatchedDefinitionId_ResolvesToADeclaredWorkflow`
  (`tests/Tamma.Activities.Tests/Workflows/DispatchTargetStructuralTests.cs`), which was run RED against
  the pre-fix tree on exactly these two sites — the test that would have caught this at introduction.
  The `MentorshipController` second instance is capture-pinned there on a shrink-only known-mismatch
  allowlist (the one-word fix is owned by the Api lane; the entry fails the build once fixed until deleted).
- **Found by:** Story 39-24's acceptance audit (recorded there as "separate defect, file separately",
  `docs/stories/epic-39/story-39-24/39-24-acceptance-step-coverage.md:494-500`) and independently by
  the Epic 41 README's definition-id sweep (`docs/stories/epic-41/README.md:592-597`). Filed during
  Story 43-11's amendment follow-up, 2026-08-02.
- **Severity:** high. Two of the five plan-review outcomes are unusable: a reviewer choosing
  `defer` or `split` sends the issue cycle into a dispatch that can never complete. No error
  surfaces to the operator; the cycle just never finishes.

## The defect

`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

- `:283` — `CreateDeferredIssues` (the `Defer` branch of the `ReviewOutcome` `FlowSwitch` at `:261-274`)
  dispatches `WorkflowDefinitionId = new("create-issues")` with `WaitForCompletion = new(true)`.
- `:300` — `CreateSplitIssues` (the `Split` branch) dispatches the same id, same wait.

**No workflow in the tree declares `DefinitionId = "create-issues"`.** A full-tree grep for
`"create-issues"` returns only the two call sites above (re-verified 2026-08-02; same result as
39-24's audit). With `WaitForCompletion = true` and no definition to run and publish completion,
the parent suspends on a completion that can never arrive — the defer/split branches hang the
cycle indefinitely.

## Expected behavior

`defer` → the deferred items (`subResult["deferred"]`) are created as issues on the platform and
the cycle finishes. `split` → the sub-issues (`subResult["split"]`) are created and the cycle
finishes.

## Actual behavior

The dispatch targets a nonexistent definition; the branch never completes; the cycle instance
stays suspended with no escalation, no event, and no operator-visible error.

## Root cause

The `create-issues` workflow was referenced but never built. Contributing cause, named by the
Epic 41 README (`:594-595`): there is **no definition-id constants file** — ~105 magic-string
dispatch sites — so a dispatch to a nonexistent id compiles, seeds, and runs without any check.
The same failure shape exists at `MentorshipController.cs:79` (`"tamma-autonomous-mentorship"`
vs the real `mentorship`).

## Fix

Story **[40-8](../../docs/stories/epic-40/story-40-8/40-8-triage-outcome-dead-ends-and-the-create-issues-workflow.md)**
(Epic 40 owns `SingleIssueCycleWorkflow`): build the `create-issues` workflow on the mediated
engine issue-create route — the same surface where Story 31-13's `git.issue.create` catalog key
lands (`docs/stories/epic-31/31-13-full-pr-operations.md`, Scope 4) — with a rerouting fallback
decision recorded in the story if the product owner prefers not to build it.

## Related

- Story: `docs/stories/epic-40/story-40-8/40-8-triage-outcome-dead-ends-and-the-create-issues-workflow.md`
- Story: `docs/stories/epic-31/31-13-full-pr-operations.md` (catalogs `git.issue.create` — the governance key for what this workflow performs)
- Audit that found it: `docs/stories/epic-39/story-39-24/39-24-acceptance-step-coverage.md:494-500`
- The magic-string argument: `docs/stories/epic-41/README.md:592-597`

## Lessons

A `DispatchWorkflow` id is an unchecked string. Until a write-time definition-id allowlist or a
constants file exists (the 41-30/41-32 idiom), every dispatch site is one typo away from a silent
permanent suspend. 40-8 adds a structural test enumerating dispatched ids against declared ids so
this class of defect fails the build.
