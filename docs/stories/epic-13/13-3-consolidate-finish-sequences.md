# Story 13.3: Consolidate Finish Sequences

Status: ready-for-dev

## Story

As a **workflow engineer**,
I want the 7 duplicated finish sequences in `SingleIssueCycleWorkflow` consolidated into 1 shared finish sequence with a parameterized reason,
so that the workflow has fewer nodes, the finish logic is defined once (DRY), and future changes to the finish path need only be made in one place.

## Acceptance Criteria

1. A single shared finish sequence exists in `SingleIssueCycleWorkflow` with a `finishReason` variable that describes why the workflow ended
2. Each of the 7 original finish paths is replaced with: 1 `SetVariable` (setting `finishReason` to the appropriate reason string) followed by a transition to the shared finish sequence
3. Net reduction: 6 activities removed from the flowchart (7 sequences replaced by 7 SetVariable + 1 shared sequence = 7 + 1 - 7 = +1, net -6 from removing 7 duplicate sequences)
4. The shared finish sequence performs all finish actions: recording workflow completion, emitting events, setting output variables
5. The `finishReason` is included in the workflow output and completion event for debugging and analytics
6. All 7 exit paths produce identical observable behavior (events, outputs) to the originals, with the addition of the `finishReason` field
7. Visual verification: the workflow graph in ELSA Studio is visually simpler with all finish paths converging on one node

## Technical Context

### Current State: 7 Finish Sequences

The `SingleIssueCycleWorkflow` currently has 7 distinct finish `Sequence` nodes, one per exit path:

1. **Limits exceeded** — story exceeds configured limits, workflow finishes early
2. **Plan rejected** — plan approval returned "reject", workflow finishes
3. **TDD failure** — TDD retry limit reached, workflow finishes with error
4. **CI failure** — CI retry limit reached, workflow finishes with error
5. **Review rejected** — code review returns "reject", workflow finishes
6. **Merge failure** — merge operation failed, workflow finishes with error
7. **Success** — full cycle completed, workflow finishes successfully

Each finish sequence contains near-identical logic (record completion, set outputs) with only minor differences in the reason string and success flag.

### After Consolidation

```
(any exit path)
  |
  v
SetVariable(finishReason = "limits_exceeded" | "plan_rejected" | "tdd_failure" | ...)
  |
  v
SharedFinishSequence
  |-- Set output: success = (finishReason == "success")
  |-- Set output: finishReason
  |-- Record workflow completion event
  |-- (any other finish actions)
  |
  v
END
```

### Finish Reason Values

| Exit Path | `finishReason` Value |
|-----------|---------------------|
| Limits exceeded | `"limits_exceeded"` |
| Plan rejected | `"plan_rejected"` |
| TDD failure | `"tdd_failure"` |
| CI failure | `"ci_failure"` |
| Review rejected | `"review_rejected"` |
| Merge failure | `"merge_failure"` |
| Success | `"success"` |

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — replace 7 finish sequences with 7 SetVariable + 1 shared finish sequence

## Implementation Notes

1. **Identify all 7 finish sequences first**: Search for all `Sequence` nodes that contain finish/completion logic. Document each one's unique behavior before consolidation.
2. **Diff the 7 sequences**: Compare all 7 to identify common vs. unique logic. The common logic goes into the shared sequence. Any unique logic per path must be handled via the `finishReason` variable (or additional variables if needed).
3. **SetVariable placement**: Each `SetVariable(finishReason = "...")` must be placed at the exact point where the original finish sequence was. The transition from SetVariable to the shared finish sequence uses a flowchart connection.
4. **Success flag derivation**: Instead of a separate `success` variable per path, derive it in the shared sequence: `success = finishReason == "success"`.
5. **Event enrichment**: Add `finishReason` to the workflow completion event payload. This is net-new information that improves observability.
6. **Test each path**: After consolidation, test every exit path to verify the shared sequence produces the same observable behavior (outputs, events) as the original dedicated sequence.

## Testing Strategy

- **Build verification**: Project compiles with no errors after consolidation
- **Path equivalence tests** (7): One test per exit path, verifying the workflow produces identical outputs and events as before consolidation
- **finishReason tests** (7): Verify each exit path sets the correct `finishReason` value
- **Success flag test**: Verify `success = true` only for `finishReason == "success"`, `false` for all others
- **Visual inspection**: Load workflow in ELSA Studio, verify all 7 paths converge on the shared finish node
- **Regression tests**: Run existing workflow tests (from Stories 13.1, 13.2) to verify no regressions
- **Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/SingleIssueCycleFinishTests.cs`

## Dependencies

- **Story 13.2** (CI Debug Retry Sub-Workflow) — the parent workflow must be in its final extracted state before consolidating finish sequences

## Estimated Effort

1 day

## Logging Requirements

### Existing Coverage

Line 79 mentions: "Add `finishReason` to the workflow completion event payload. This is net-new information that improves observability." This is about event payloads, not structured logging. The story has **no ILogger logging requirements** specified.

### Required Additions

The shared finish sequence in `SingleIssueCycleWorkflow` should log via the workflow's `ILogger<T>`.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Workflow finish reason set | INFO | `{WorkflowInstanceId}`, `{FinishReason}`, `{IssueNumber}` | Logged when `SetVariable(finishReason = "...")` executes — one of 7 values |
| Shared finish sequence entered | INFO | `{WorkflowInstanceId}`, `{FinishReason}`, `{Success}`, `{IssueNumber}`, `{TotalDurationMs}` | The single consolidated finish point — primary audit log for workflow completion |
| Workflow completion event emitted | DEBUG | `{WorkflowInstanceId}`, `{FinishReason}`, `{EventType}` | Confirmation that the completion event was recorded in the event store |
| Workflow output variables set | DEBUG | `{WorkflowInstanceId}`, `{FinishReason}`, `{Success}` | Confirmation that output variables were written |

### Sensitive Data Redaction

- `{FinishReason}` values are from a known enum (`limits_exceeded`, `plan_rejected`, `tdd_failure`, `ci_failure`, `review_rejected`, `merge_failure`, `success`) — safe to log.
- Do NOT log error message details in the finish log — those are already logged at the point of failure.

### Correlation IDs

- `{WorkflowInstanceId}` and `{IssueNumber}` must be included in all finish logs.
- `{FinishReason}` serves as the primary categorization for workflow outcome analytics.
- The shared finish sequence log is the canonical "workflow completed" signal — dashboards and alerting should key on this log entry.

### Execution Store Operations

- The `finishReason` field added to the workflow completion event payload (line 79) enhances the execution store record. Log at DEBUG level when this event is emitted to confirm it was recorded.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/single-issue-workflow-split.md` Phase 4 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
