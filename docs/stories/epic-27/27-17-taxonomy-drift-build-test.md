# Story 27-17: Taxonomy Drift Build Test

## Story

As the Tamma build, I need a test that fails the build when any compiled
workflow dispatch site emits a `(role, action)` not in the Story 27-15
taxonomy, so drift between workflows and the taxonomy is impossible to ship.

Canonical design: SPEC §3.4, §7.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (taxonomy), Story 27-19 (dispatch sites emit `AgentAction.X.ToWire()`).

## Acceptance Criteria

1. A test enumerates every `["action"]` / `["role"]` value passed at the ~21
   `llm-call` dispatch sites (after Story 27-19 migration these are
   `AgentAction.X.ToWire()` / `AgentRole.X.ToWire()` expressions).
2. The test asserts every emitted `(role, action)` ∈ the Story 27-15 taxonomy
   and that the role is eligible for the action per the rebuilt RolePhaseMap.
3. The test asserts the `Parse(x.ToWire()) == x` round-trip for every
   `AgentRole` and `AgentAction` value.
4. The test asserts the prompt seed key set == the convention seed key set
   (codegen output equality, SPEC §3.4).
5. Failure breaks the build (runs in the standard `dotnet test` CI gate).
6. The test lists, on failure, exactly which dispatch site / which pair drifted.

## Technical Context

- Test project: `apps/tamma-elsa/tests/Tamma.Activities.Tests/` (workflow
  structure test area, alongside existing `WorkflowStructureTests.cs`).
- Dispatch-site enumeration: reflect over the compiled workflow assembly or
  parse the known 21 sites; the design spec lists them as the audit set.

## Estimate

6 hours.
