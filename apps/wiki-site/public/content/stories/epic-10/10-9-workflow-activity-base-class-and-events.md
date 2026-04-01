---
title: "Story 10-9: TammaActivity Base Class and Workflow Event Emission"
sidebar:
  order: 100
---

**Epic**: Epic 10 - Engine & Workflow Orchestration
**Priority**: High
**Status**: In Progress

## Summary

Create a `TammaActivity` base class that all Tamma ELSA activities inherit from. The base class automatically emits start/end events for every activity execution, providing a complete audit trail of workflow execution without manual event code in each activity.

## Problem

Currently, ELSA activities have no standardized event emission. There is no audit trail of which activities executed, how long they took, or what data they processed. Each activity uses ad-hoc `ILogger` calls with inconsistent formats.

## Solution

### TammaActivity Base Class

```
TammaActivity : CodeActivity
├── EventType (virtual string?) — e.g., "ADL.CONFIG.INIT"
├── BuildStartData() — custom data for start event
├── BuildEndData() — custom data for end event
├── Run() — activity logic (replaces Execute)
└── Execute() — wraps Run() with automatic event emission
```

Each activity execution emits:
1. `{EventType}.STARTED` — before `Run()` executes
2. `{EventType}.COMPLETED` — after `Run()` succeeds (includes duration)
3. `{EventType}.FAILED` — if `Run()` throws (includes error + duration)

### TammaEvent Model

```csharp
{
  EventType: "ADL.CONFIG.INIT.COMPLETED",
  Status: "success",
  Timestamp: "2026-04-01T...",
  Duration: "00:00:00.042",
  ActivityId: "InitAdlConfig",
  ActivityName: "Load Config",
  WorkflowInstanceId: "...",
  Data: { repository: "meywd/tamma", ... }
}
```

Events are collected in `WorkflowExecutionContext.TransientProperties["tamma:events"]` for the orchestrator to persist to the event store.

### Async Variant

`TammaAsyncActivity` provides the same pattern for async activities using `RunAsync()` instead of `Run()`.

## Acceptance Criteria

- [ ] `TammaActivity` base class in `Tamma.Activities.Core` namespace
- [ ] `TammaAsyncActivity` async variant
- [ ] `TammaEvent` model with EventType, Status, Duration, Data
- [ ] Automatic start/end event emission wrapping `Run()`/`RunAsync()`
- [ ] Events stored in workflow transient properties
- [ ] `InitAdlConfigActivity` migrated as reference implementation
- [ ] All ADL Orchestrator activities migrated to use base class
- [ ] Event naming follows `AGGREGATE.ACTION.STATUS` pattern (e.g., `ADL.CONFIG.INIT.COMPLETED`)

## Migration Plan

Activities to migrate (in order):
1. `InitAdlConfigActivity` (done — reference implementation)
2. `CheckLimitsActivity`
3. `SelectIssueActivity`
4. `CreateBranchActivity`
5. `WaitForPlanApprovalActivity`
6. All TDD activities (`WriteTestsActivity`, `WriteImplementationActivity`, etc.)
7. All Testing activities
8. All Code Review activities
9. All Context Gathering activities
10. All Blocker Diagnosis activities

## Files

- `Tamma.Activities/Core/TammaActivity.cs` — base classes + event model
- `Tamma.Activities/ADL/InitAdlConfigActivity.cs` — reference implementation
- All activity files in `Tamma.Activities/` — migration targets

## Dependencies

- Story 4-1 (Event Schema Design) — event model alignment
- Story 10-2 (Comprehensive Event Catalog) — event type naming
