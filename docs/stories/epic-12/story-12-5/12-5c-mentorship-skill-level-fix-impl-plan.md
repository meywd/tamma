# Story 12-5c: Mentorship Skill-Level Adaptation Fix -- Implementation Plan

**Parent story:** [12-5-prompt-engineering-framework.md](./12-5-prompt-engineering-framework.md) (Sub-Story 12-5c)
**Effort:** 4h (quick-win)
**Dependencies:** None (self-contained Elsa workflow fix)

## Problem

`MentorshipWorkflow.cs` declares `skillLevel` as a workflow variable initialized to `3` (Intermediate) and passes it into every sub-workflow dispatch (assessment, testing, TDD, debugging, blocker diagnosis, LLM call). The value is extracted exactly once -- `extractSkillLevel` runs after the first `assessmentWorkflow` dispatch (line 795) and maps `Confidence` (0.0-1.0) from the assessment result to a 1-5 bucket.

However, the assessment loop re-runs. `assessJunior` (AssessJuniorFlowActivity) emits `Correct | Partial | Incorrect | Error` outcomes that drive the retry loop (lines 820-848), and the Partial path re-routes back through `clarifyRequirements -> assessJunior`, while Incorrect re-routes through `reExplainStory -> assessJunior`. Neither retry path updates `skillLevel`. Downstream sub-workflows receive the stale value, so mentor guidance never adapts to the junior's demonstrated understanding during the session.

## Root cause

The `extractSkillLevel` SetVariable activity is wired only off `assessmentWorkflow` (the separate Elsa sub-workflow dispatch), not off `assessJunior` (the in-process activity that fires on each retry). The AssessmentOutput produced by `AssessJuniorCapabilityActivity` contains `Status` (Correct/Partial/Incorrect) and `Confidence`, but the workflow graph never reads those results back into `skillLevel`.

Per the parent story acceptance criteria:
- Correct -> increment `skillLevel` (capped at 5)
- Partial -> no change
- Incorrect -> decrement `skillLevel` (floored at 1)

## Tasks

### Task 1: Read and confirm the current graph

Files:
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs` (lines 55-57, 370-470, 780-850)
- `apps/tamma-elsa/src/Tamma.Activities/Mentorship/AssessJuniorCapabilityActivity.cs` (confirm `AssessmentOutput` shape -- `Status`, `Confidence`, `NextState`)
- `apps/tamma-elsa/src/Tamma.Activities/Mentorship/ProvideGuidanceActivity.cs` (verify `GenerateGuidance` already branches on `skillLevel` -- it does, lines 113/175/268/430, so no downstream template changes needed)

Confirm: the existing `extractSkillLevel` only handles the first assessment dispatch and does not catch `assessJunior` retry outcomes.

### Task 2: Add a `ClampSkillLevelFromOutcome` SetVariable activity

In `MentorshipWorkflow.cs`, after the existing `extractSkillLevel` declaration (~line 470), add a second `SetVariable<int>` bound to the `skillLevel` variable that reads the `AssessJuniorFlowActivity` result from the workflow context and applies the increment/decrement rule:

```csharp
var adjustSkillFromOutcome = new SetVariable<int>(
    skillLevel,
    context =>
    {
        var current = skillLevel.Get(context);
        // AssessJuniorFlowActivity exposes its last AssessmentOutput via
        // context.GetLastResult<AssessmentOutput>() or a named workflow output.
        // Read the Status and adjust:
        var status = TryReadLastAssessmentStatus(context);
        return status switch
        {
            AssessmentStatus.Correct   => Math.Min(5, current + 1),
            AssessmentStatus.Incorrect => Math.Max(1, current - 1),
            _                          => current, // Partial | Error | null -> no change
        };
    })
{
    Id = "AdjustSkillFromAssessment",
    Name = "Adjust Skill Level from Assessment Outcome"
};
```

Helper `TryReadLastAssessmentStatus` lives in the same file (private static) and pulls from whichever Elsa context surface `AssessJuniorFlowActivity` writes to. Prefer the workflow variable approach already used for `assessmentDispatchResult` if `AssessJuniorFlowActivity` sets one; otherwise introduce a typed `Variable<AssessmentOutput>` (`lastAssessmentOutcome`) and wire `assessJunior.Result = new(lastAssessmentOutcome)`.

### Task 3: Wire the new activity into every outcome edge

Edit the `Connections` list (`MentorshipWorkflow.cs` ~lines 820-848):

- `assessJunior[Correct]` -> `adjustSkillFromOutcome` -> `llmCallWorkflow`
- `assessJunior[Partial]` -> `adjustSkillFromOutcome` -> `incrementAssessmentAttempt`
- `assessJunior[Incorrect]` -> `adjustSkillFromOutcome` -> `reExplainStory`
- `assessJunior[Error]` edge is unchanged (goes straight to `failed`)

The `adjustSkillFromOutcome` node sits between `assessJunior` outcomes and the next step. All downstream DispatchWorkflow activities already read `skillLevel.Get(context)` at dispatch time, so no sub-workflow input wiring changes are needed.

### Task 4: Preserve the existing bootstrap extraction

Keep the existing `extractSkillLevel` node (confidence-based seeding from the first assessment sub-workflow dispatch). It runs on the very first pass and gives a data-driven starting point; the new `adjustSkillFromOutcome` node handles every subsequent loop iteration. Document the two-phase behaviour with a comment above both nodes.

### Task 5: Tests

New file: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/MentorshipSkillLevelAdaptationTests.cs`

Cases (use `WorkflowTestHelper` pattern from sibling tests in the same folder):

1. **Correct outcome increments** -- start `skillLevel = 3`, dispatch `assessJunior` returning `Correct`, assert `skillLevel == 4` after the adjust node runs.
2. **Correct caps at 5** -- start `skillLevel = 5`, Correct outcome, assert `skillLevel == 5`.
3. **Incorrect outcome decrements** -- start `skillLevel = 3`, Incorrect, assert `skillLevel == 2`.
4. **Incorrect floors at 1** -- start `skillLevel = 1`, Incorrect, assert `skillLevel == 1`.
5. **Partial outcome leaves unchanged** -- start `skillLevel = 3`, Partial, assert `skillLevel == 3`.
6. **Downstream dispatch receives updated value** -- after an Incorrect outcome in the retry loop, assert the next `reExplainStory` / subsequent dispatch sees the decremented value (integration-style, use a fake `DispatchWorkflow` or intercept input dictionary).

### Task 6: Event emission

Add a `MentorshipEvent` log (`EventTypes.SkillLevelAdjusted`, new const in `Tamma.Core.Entities.EventTypes`) inside the `adjustSkillFromOutcome` lambda so the adjustment is visible in the DCB event stream. Include `from`, `to`, and `triggerOutcome` in the event data.

## Files to modify

| File | Change |
|---|---|
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs` | Add `adjustSkillFromOutcome` SetVariable + 3 graph edges |
| `apps/tamma-elsa/src/Tamma.Core/Entities/EventTypes.cs` | Add `SkillLevelAdjusted` const |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/MentorshipSkillLevelAdaptationTests.cs` | New test file, 6 cases |

## Test commands

```bash
# Run only the new test class
dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/Tamma.Activities.Tests.csproj \
  --filter "FullyQualifiedName~MentorshipSkillLevelAdaptationTests"

# Full activity test suite (regression)
dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/Tamma.Activities.Tests.csproj

# Full Elsa server build to catch graph wiring errors
dotnet build apps/tamma-elsa/Tamma.sln
```

## Acceptance criteria (from parent story)

- [x] Assessment result updates the `skillLevel` variable
- [x] Correct -> increment (max 5), Partial -> no change, Incorrect -> decrement (min 1)
- [x] Updated skill level propagated to all downstream sub-workflow dispatches
- [x] Mentor prompt uses conditional sections based on skill level (already handled in `ProvideGuidanceActivity.GenerateTechnicalGuidance` / `GenerateMotivationGuidance`)

## Out of scope

- User-profile-backed default skill level (no `UserSkillLevel` column exists in `packages/api/src/persistence/user-store.ts` today; adding persistent user skill profiles is a separate story)
- Re-tuning the confidence-to-skill-level mapping in `extractSkillLevel`
- Changes to `ProvideGuidanceActivity` guidance templates

---

**Last updated:** 2026-04-15
**Owner:** Team E (quick-wins)
