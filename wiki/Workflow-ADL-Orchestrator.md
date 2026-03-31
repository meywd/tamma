# Workflow: ADL Orchestrator

**Definition ID:** `adl-orchestrator`
**Class:** `AdlOrchestratorWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs`

## Purpose

The ADL (Autonomous Development Loop) Orchestrator is the **top-level entry point** for Tamma's autonomous development. It runs a continuous loop that:

1. Checks operational limits (max issues, budget, time)
2. Dispatches a [Single Issue Cycle](Workflow-Single-Issue-Cycle) for the next available issue
3. Parses the result (success, no issues, error)
4. Applies a cooldown delay
5. Loops back or terminates

## Flow Diagram

```
                    +-------------+
                    | Load Config |
                    +------+------+
                           |
                           v
                  +----------------+
          +------>| Check Limits   |
          |       +-------+--------+
          |               |
          |               v
          |       +----------------+
          |       | Within Limits? |
          |       +--+----------+--+
          |          |          |
          |        YES         NO
          |          |          |
          |          v          v
          |  +---------------+  +------------------+
          |  | Dispatch      |  | Output (Limits)  |
          |  | Issue Cycle   |  +--------+---------+
          |  +-------+-------+           |
          |          |                   v
          |          v              +---------+
          |  +---------------+     | Finish  |
          |  | Parse Result  |     +---------+
          |  +-------+-------+
          |          |
          |          v
          |  +----------------+
          |  | More Issues?   |
          |  +--+----------+--+
          |     |          |
          |   YES          NO
          |     |          |
          |     v          v
          | +----------+  +------------------+
          | | Cooldown |  | Output (No Issues)|
          | +----+-----+  +--------+---------+
          |      |                  |
          +------+                  v
                               +---------+
                               | Finish  |
                               +---------+
```

## Configuration

The workflow accepts configuration through inputs or a JSON config object (`AdlConfig`):

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `repository` | string | `""` | GitHub repository (e.g., `owner/repo`) |
| `configJson` | string | `"{}"` | Serialized `AdlConfig` object |
| `issueLabels` | string[] | `[]` | Labels to filter issues by |
| `botAssignee` | string | `"tamma-bot"` | Bot user to assign issues to |
| `baseBranch` | string | `"main"` | Base branch for PRs |
| `cooldownSeconds` | int | `10` | Delay between cycles |

The `AdlConfig` JSON can set all of the above plus operational limits (max issues, budget cap, time limit).

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `issuesCompleted` | int | Running count of successfully completed issues |
| `lastExitReason` | string | Exit reason from the last cycle |
| `stopReason` | string | Reason the orchestrator should stop (from `CheckLimitsActivity`) |
| `cycleResult` | IDictionary | Result from the dispatched `single-issue-cycle` |

## Exit Conditions

The orchestrator terminates when:

1. **Limits reached** -- `CheckLimitsActivity` returns a non-empty `stopReason` (max issues, budget exhausted, time limit)
2. **No issues** -- The `single-issue-cycle` returns `exitReason: "noIssues"`, meaning no matching issues were found

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `totalIssuesCompleted` | int | Number of issues completed in this run |
| `exitReason` | string | Why the orchestrator stopped: `"limitsReached"` or `"noIssues"` |

## Error Handling

- The `CheckLimitsActivity` handles operational limit validation
- Individual cycle failures are captured in `lastExitReason` but do not stop the orchestrator
- The loop continues picking new issues until limits are hit or no issues remain

## Sub-Workflows Dispatched

| Workflow | Wait | Purpose |
|----------|------|---------|
| [Single Issue Cycle](Workflow-Single-Issue-Cycle) | Yes | Process one issue end-to-end |

---

_See also: [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Workflows Index](Workflows)_
