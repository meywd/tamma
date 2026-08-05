# Workflow: ADL Orchestrator

**Definition ID:** `adl-orchestrator`
**Class:** `AdlOrchestratorWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs`

## Purpose

The ADL (Autonomous Development Loop) Orchestrator is the **top-level entry point** for Tamma's autonomous development. It runs a continuous loop that:

1. Selects the highest-priority work item (issues, security alerts, CI failures, stale PRs)
2. Dispatches triage if untriaged items exist but nothing is ready
3. Checks concurrency limits by querying active workflow instances
4. Dispatches a [Single Issue Cycle](Workflow-Single-Issue-Cycle) as fire-and-forget
5. Applies a cooldown delay
6. Loops back or terminates

**Key design principle:** Issue selection lives inside the orchestrator. The Single Issue Cycle receives exactly one work item and processes it end-to-end.

## Flow Diagram

```
                    +------------------+
                    | Init ADL Config  |
                    +--------+---------+
                             |
                             v
               +-------------------------+
       +------>| Select Work Item        |
       |       | (priority-based)        |
       |       +---+--------+--------+---+
       |           |        |        |
       |     NothingFound NeedsTriage Selected
       |           |        |        |
       |           v        |        v
       |  +----------------+|  +----------------+
       |  | Set Exit Reason||  | Check Limits   |
       |  | (No Issues)    ||  | (query active  |
       |  +-------+--------+|  |  instances)    |
       |          |         |  +---+--------+---+
       |          v         |      |        |
       |     +---------+   |    Stop    Continue
       |     | Finish  |   |      |        |
       |     +---------+   |      v        v
       |                   | +----------+ +------------------+
       |                   | |Set Exit  | | Dispatch Cycle   |
       |                   | |Reason    | | (fire & forget)  |
       |                   | |(Limits)  | +--------+---------+
       |                   | +----+-----+          |
       |                   |      |                v
       |                   |      v          +----------+
       |                   | +---------+     | Cooldown |
       |                   | | Finish  |     +----+-----+
       |                   | +---------+          |
       |                   |                      |
       |                   v                      |
       |         +-------------------+            |
       |         | Dispatch Triage   |            |
       |         +--------+----------+            |
       |                  |                       |
       +------------------+-----------------------+
```

## Activities

All activities extend one of the base classes providing automatic event emission:

- **`TammaActivity`** -- synchronous activities
- **`TammaAsyncActivity`** -- async activities
- **`TammaOutcomeActivity`** -- activities with multiple outcomes

All implement the `ITammaActivity` interface.

| Activity | Base Class | Description |
|----------|------------|-------------|
| `InitAdlConfigActivity` | `TammaAsyncActivity` | Parses ADL configuration from inputs (replaces `SetVariable` with side effects) |
| `SelectWorkItemActivity` | `TammaOutcomeActivity` | Priority-based work item selection with 3 outcomes: `Selected`, `NothingFound`, `NeedsTriage` |
| `CheckLimitsActivity` | `TammaAsyncActivity` | Queries `IWorkflowInstanceStore` for active `SingleIssueCycle` instances to enforce concurrency limits |
| `DispatchCycleActivity` | `TammaAsyncActivity` | Wraps `IWorkflowDispatcher` to dispatch a Single Issue Cycle (fire-and-forget, no blocking) |
| `DispatchTriageActivity` | `TammaAsyncActivity` | Dispatches the [Triage Workflow](Workflow-Triage) for untriaged items |
| `CooldownActivity` | `TammaAsyncActivity` | Configurable delay between cycles with event emission |
| `SetExitReasonActivity` | `TammaActivity` | Sets the exit reason (`noIssues` or `limitsReached`) with event emission |

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

The `AdlConfig` JSON can set all of the above plus operational limits (max concurrent instances, budget cap, time limit).

## Priority-Based Work Item Selection

The `SelectWorkItemActivity` selects work items ranked by priority:

| Priority | Level | Examples |
|----------|-------|---------|
| **Urgent** | P0 | Security alerts, production incidents |
| **High** | P1 | CI failures, blocking bugs |
| **Normal** | P2 | Standard issues, feature requests |
| **Low** | P3 | Stale PRs, tech debt, housekeeping |

Work item types include:
- **GitHub Issues** -- filtered by labels and assignee
- **Security Alerts** -- Dependabot, CodeQL findings
- **CI Failures** -- broken builds on main branch
- **Stale PRs** -- PRs needing attention (reviews, merge conflicts)

### Three Outcomes

1. **Selected** -- a ready work item was found; proceed to `CheckLimits`
2. **NothingFound** -- no work items exist at all; exit the orchestrator
3. **NeedsTriage** -- untriaged items exist but none are ready; dispatch triage first, then re-select

## Concurrency-Based Limits

Instead of tracking a local counter, `CheckLimitsActivity` queries the ELSA `IWorkflowInstanceStore` for active `SingleIssueCycle` instances. This provides accurate concurrency enforcement even across restarts.

| Limit | Source | Description |
|-------|--------|-------------|
| Max concurrent cycles | `AdlConfig.maxConcurrentCycles` | Maximum active `SingleIssueCycle` instances |
| Budget cap | `AdlConfig.budgetCapUsd` | Maximum LLM spend per run |
| Time limit | `AdlConfig.timeLimitMinutes` | Maximum wall-clock time |

## Events Emitted

Every activity emits STARTED/COMPLETED events for full audit trail:

| Event Type | Activity | Description |
|------------|----------|-------------|
| `ADL.CONFIG.INIT.STARTED` | `InitAdlConfigActivity` | Config parsing began |
| `ADL.CONFIG.INIT.COMPLETED` | `InitAdlConfigActivity` | Config parsed successfully |
| `ADL.WORKITEM.SELECT.STARTED` | `SelectWorkItemActivity` | Work item selection began |
| `ADL.WORKITEM.SELECT.COMPLETED` | `SelectWorkItemActivity` | Selection result (includes outcome and selected item) |
| `ADL.LIMITS.CHECK.STARTED` | `CheckLimitsActivity` | Concurrency/budget/time check began |
| `ADL.LIMITS.CHECK.COMPLETED` | `CheckLimitsActivity` | Limits check result (includes active instance count) |
| `ADL.CYCLE.DISPATCH.STARTED` | `DispatchCycleActivity` | Cycle dispatch began |
| `ADL.CYCLE.DISPATCH.COMPLETED` | `DispatchCycleActivity` | Cycle dispatched (includes workflow instance ID) |
| `ADL.TRIAGE.DISPATCH.STARTED` | `DispatchTriageActivity` | Triage dispatch began |
| `ADL.TRIAGE.DISPATCH.COMPLETED` | `DispatchTriageActivity` | Triage dispatched |
| `ADL.COOLDOWN.STARTED` | `CooldownActivity` | Cooldown delay began |
| `ADL.COOLDOWN.COMPLETED` | `CooldownActivity` | Cooldown completed |
| `ADL.EXIT.STARTED` | `SetExitReasonActivity` | Exit reason being set |
| `ADL.EXIT.COMPLETED` | `SetExitReasonActivity` | Exit reason recorded |

## Exit Conditions

The orchestrator terminates when:

1. **No work items** -- `SelectWorkItemActivity` returns `NothingFound` (no issues, alerts, or PRs to process)
2. **Limits reached** -- `CheckLimitsActivity` returns `Stop` (max concurrent cycles, budget exhausted, or time limit)

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `exitReason` | string | Why the orchestrator stopped: `"limitsReached"` or `"noIssues"` |

## Error Handling

- `InitAdlConfigActivity` validates configuration and fails fast on invalid input
- `SelectWorkItemActivity` handles API errors gracefully (GitHub rate limits, network issues)
- `DispatchCycleActivity` is fire-and-forget -- cycle failures do not stop the orchestrator
- `DispatchTriageActivity` failures are logged but do not stop the loop
- The loop continues selecting new work items until limits are hit or nothing remains

## Loop durability -- why the restart edge matters

This is the property the bullets above depend on, and it is worth stating explicitly because it is easy to break.

**The orchestrator restarts itself.** Every terminal path runs `... -> cooldown -> DispatchAdl -> Finish`, and `DispatchAdlActivity` dispatches the *successor* instance. There is **no cron trigger and no watchdog** — nothing else in the system starts an `adl-orchestrator` instance. The restart is therefore the **last step of the instance it restarts**.

That has a sharp consequence: anything that faults the instance *before* it reaches that final step ends the autonomous loop **permanently**, until a human dispatches one by hand. Not a skipped cycle — a stopped platform, and a quiet one.

Three things protect against that:

1. **Continue-with-incidents.** The workflow declares `ContinueWithIncidentsStrategy`, so a throwing activity records an incident and the flow still reaches the restart edge. Without it, the engine's default is to fault the whole instance. Every long-running workflow in Tamma sets this; the orchestrator needs it most.
2. **The fire-and-forget dispatches never throw.** `DispatchCycleActivity` and `DispatchTriageActivity` sit upstream of the restart edge, so a failure to start one issue cycle or triage batch costs that item and not the loop. The issue stays selectable on the next tick.
3. **The restart dispatch retries.** `DispatchAdlActivity` retries with a short backoff and never propagates an exception. If every attempt fails it logs at **Critical**, naming the consequence — because that log line is the only explanation for the platform going silent.

### If the loop does go quiet

Look for the Critical log line from `DispatchAdlActivity`. If it is there, the restart genuinely failed and a new `adl-orchestrator` instance must be dispatched manually. If it is *not* there, the loop stopped for a different reason — check the workflow instance list for a faulted orchestrator instance.

## Sub-Workflows Dispatched

| Workflow | Wait | Purpose |
|----------|------|---------|
| [Single Issue Cycle](Workflow-Single-Issue-Cycle) | No (fire & forget) | Process one issue end-to-end |
| [Triage](Workflow-Triage) | No (fire & forget) | Classify and label untriaged items |

## Design Changes from Previous Version

The ADL Orchestrator was redesigned with several key changes:

1. **Issue selection moved into the orchestrator** -- previously, the Single Issue Cycle selected its own issue. Now the ADL orchestrator selects the work item and passes it to the cycle.
2. **Fire-and-forget dispatch** -- the orchestrator no longer blocks waiting for cycle completion. It dispatches and immediately moves to cooldown.
3. **Priority-based selection** -- work items are ranked by priority (urgent/high/normal/low) across multiple sources (issues, security alerts, CI failures, stale PRs).
4. **Triage integration** -- when no ready items are found but untriaged issues exist, the orchestrator dispatches a triage workflow before re-selecting.
5. **Concurrency-based limits** -- queries ELSA for active `SingleIssueCycle` instances instead of maintaining a local counter.
6. **Event emission on every step** -- all activities extend `TammaActivity`/`TammaAsyncActivity`/`TammaOutcomeActivity` base classes implementing `ITammaActivity`, ensuring complete audit trail.

---

_See also: [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Triage](Workflow-Triage) | [Workflows Index](Workflows)_
