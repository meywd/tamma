# Story 7-1A: Main Mentorship Workflow (Code-First Flowchart)

## User Story

As the **Tamma mentorship engine**, I need the 28-state mentorship workflow implemented as a code-first ELSA `Flowchart` with outcome-based transitions and sub-workflow invocations so that the entire mentorship lifecycle is visible in ELSA Studio, pausable/resumable across server restarts, and composable from well-defined sub-workflows.

## Description

Replace the flat JSON-based `autonomous-mentorship.json` Sequence with a proper C# `IWorkflow` implementation using ELSA's `Flowchart` activity. Each of the 28 mentorship states becomes a node (custom activity) with `[FlowNode]` outcome-based transitions. Guard conditions are `FlowDecision` activities. Sub-workflows (7-1B through 7-1I) are invoked via `RunWorkflow` activities at the appropriate states.

The `WorkflowSeeder` class and any JSON workflow files become unnecessary — code-first workflows register at startup through the DI container.

**Enhances**: Story 7-1 (State Machine Core)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs`
- [ ] Uses `Flowchart` as root activity (not `Sequence` or `StateMachine`)
- [ ] Registered at startup via `services.AddWorkflow<MentorshipWorkflow>()`
- [ ] Visible and navigable in ELSA Studio designer
- [ ] `WorkflowSeeder` class deleted — no longer needed
- [ ] `autonomous-mentorship.json` deleted — replaced by code

### AC2: All 28 States as Activities
- [ ] Each state from `MentorshipState` enum is a custom activity node in the flowchart
- [ ] Activities organized by group:
  - **Initialization**: `InitStoryProcessingActivity`, `ValidateStoryActivity`
  - **Assessment**: `AssessJuniorCapabilityActivity`, `ClarifyRequirementsActivity`, `ReExplainStoryActivity`
  - **Planning**: `PlanDecompositionActivity`, `ReviewPlanActivity`, `AdjustPlanActivity`
  - **Implementation**: `StartImplementationActivity`, `MonitorProgressActivity`, `ProvideGuidanceActivity`, `DetectPatternActivity`
  - **Blockers**: `DiagnoseBlockerActivity`, `ProvideHintActivity`, `ProvideAssistanceActivity`, `EscalateToSeniorActivity`
  - **Quality**: `QualityGateCheckActivity`, `AutoFixIssuesActivity`, `ManualFixRequiredActivity`
  - **Review**: `PrepareCodeReviewActivity`, `MonitorReviewActivity`, `GuideFixesActivity`, `ReRequestReviewActivity`
  - **Completion**: `MergeAndCompleteActivity`, `GenerateReportActivity`, `UpdateSkillProfileActivity`, `CompletedActivity`
  - **Exception**: `PausedActivity`, `CancelledActivity`, `FailedActivity`, `TimeoutActivity`
- [ ] Each activity has outcome-based transitions (e.g., `AssessJuniorCapabilityActivity` → outcomes: `Correct`, `Partial`, `Incorrect`, `Timeout`)

### AC3: State Transitions (60+ Transitions)
- [ ] Minimum 60 valid transitions defined as flowchart connections between activity outcomes
- [ ] Transitions are declarative connections, not hardcoded switch statements
- [ ] Key transition paths:
  - **Happy path**: INIT → VALIDATE → ASSESS → PLAN → START_IMPL → MONITOR → QUALITY → REVIEW → MERGE → REPORT → PROFILE → COMPLETED
  - **Assessment loop**: ASSESS → CLARIFY → ASSESS (max 3 cycles)
  - **Planning loop**: PLAN → REVIEW → ADJUST → PLAN (max 2 adjustments)
  - **Blocker escalation**: DIAGNOSE → HINT → GUIDANCE → ASSISTANCE → ESCALATE
  - **Quality retry**: QUALITY → AUTO_FIX → QUALITY (max 3 auto-fix attempts)
  - **Review iteration**: MONITOR_REVIEW → GUIDE_FIXES → RE_REQUEST → MONITOR_REVIEW (max 5 iterations)
  - **Bug fast path**: INIT → VALIDATE → [bug?] → RunWorkflow:Debugging(7-1I, bug_investigation) → QUALITY → REVIEW → MERGE
- [ ] Every state has a path to at least one terminal state (COMPLETED, CANCELLED, FAILED, TIMEOUT)

### AC4: Guard Conditions as FlowDecision Activities
- [ ] Guard conditions implemented as `FlowDecision` activities placed between states:
  - `MaxRetriesNotExceeded`: blocks transition if retry count >= configured max
  - `SkillLevelSufficient`: blocks advanced paths for low-skill developers
  - `AssessmentScoreAboveThreshold`: requires minimum confidence score (default 0.7)
  - `QualityGatesPass`: requires all quality checks to pass
  - `ReviewApproved`: requires review approval status
  - `IsBugIssue`: routes bug issues to debugging fast path
  - `IsImplementerStuck`: detects stall for blocker diagnosis
- [ ] Guard conditions receive session context via workflow variables
- [ ] Failed guards redirect to appropriate fallback state

### AC5: Sub-Workflow Invocations
- [ ] Sub-workflows invoked at appropriate states via `RunWorkflow` activities:
  - **7-1B (LLM Call)**: invoked from assessment, planning, guidance, and numerous other states
  - **7-1C (Testing)**: invoked from QUALITY_GATE_CHECK
  - **7-1D (Code Review)**: invoked from PREPARE_CODE_REVIEW
  - **7-1E (Assessment)**: invoked from ASSESS_JUNIOR_CAPABILITY
  - **7-1F (Context Gathering)**: invoked from INIT_STORY_PROCESSING, DIAGNOSE_BLOCKER
  - **7-1G (Blocker Diagnosis)**: invoked from DIAGNOSE_BLOCKER
  - **7-1H (TDD)**: invoked from START_IMPLEMENTATION (per task in plan)
  - **7-1I (Debugging)**: invoked from INIT (bug fast path), TDD failures, MONITOR_PROGRESS errors
- [ ] Sub-workflow results flow back into main workflow variables
- [ ] Sub-workflow failures handled gracefully (do not crash main workflow)

### AC6: Bookmark-Based Pausing
- [ ] Workflow pauses at states that wait for external events:
  - `MONITOR_PROGRESS` — waits for code submission or timeout
  - `MONITOR_REVIEW` — waits for review webhook
  - `PROVIDE_HINT` / `PROVIDE_GUIDANCE` / `PROVIDE_ASSISTANCE` — waits for junior response
  - `MANUAL_FIX_REQUIRED` — waits for manual fix completion
  - `ESCALATE_TO_SENIOR` — waits for senior response
- [ ] Bookmarks are resumable via ELSA REST API (webhook callback)
- [ ] Pause/resume survives server restarts (bookmark persisted to DB)
- [ ] `USER_PAUSE` event pauses from any active state, preserving bookmark
- [ ] `USER_RESUME` event resumes to the paused state

### AC7: Timeout Handling
- [ ] Each state has a configurable timeout via `Elsa.Scheduling` timer activities
- [ ] Default timeouts: simple tasks 15min, complex tasks 30min, research tasks 45min
- [ ] Timeout escalation chain: Hint (15min) → Guidance (30min) → Assistance (45min) → Escalate (60min) → Session Timeout (120min)
- [ ] Timeouts cancelled when state transitions normally
- [ ] Session-level timeout (120min default) applies to entire workflow

### AC8: Issue Type Routing
- [ ] `INIT_STORY_PROCESSING` checks issue type (bug vs feature/story)
- [ ] **Bug issues**: route to Debugging sub-workflow (7-1I, mode=`bug_investigation`) immediately after validation
  - Debugging workflow investigates, writes regression test, fixes
  - Then jumps to QUALITY_GATE_CHECK (skip normal assessment/planning/implementation)
- [ ] **Feature/story issues**: follow normal assessment → planning → implementation path

### AC9: Execution Log and Observability
- [ ] Every state transition emits a structured log: sessionId, fromState, toState, event, timestamp, duration
- [ ] Full execution log visible in ELSA Studio (each activity shows input/output)
- [ ] Workflow navigable in Studio designer — all 28 states and connections visible
- [ ] Metrics emitted: `mentorship.transitions.total`, `mentorship.state.duration`, `mentorship.timeouts.total`

### AC10: Cleanup
- [ ] `WorkflowSeeder.cs` deleted
- [ ] `autonomous-mentorship.json` (and any other JSON workflow files) deleted
- [ ] `Program.cs` updated to remove `WorkflowSeeder` registration, add code-first workflow registrations

## Technical Design

### Workflow Structure (High-Level)

```
Flowchart: MentorshipWorkflow
│
├── [Initialization]
│   ├── InitStoryProcessing
│   │   └── RunWorkflow: ContextGathering (7-1F, purpose=Planning)
│   ├── ValidateStory
│   └── FlowDecision: IsBugIssue?
│       ├── Yes → RunWorkflow: Debugging (7-1I, mode=bug_investigation) → QualityGateCheck
│       └── No → AssessJuniorCapability
│
├── [Assessment]
│   ├── AssessJuniorCapability
│   │   └── RunWorkflow: Assessment (7-1E)
│   ├── FlowDecision: AssessmentResult
│   │   ├── Correct → PlanDecomposition
│   │   ├── Partial → ClarifyRequirements → AssessJuniorCapability
│   │   ├── Incorrect → ReExplainStory → AssessJuniorCapability
│   │   └── Timeout → DiagnoseBlocker
│   └── FlowDecision: MaxRetriesNotExceeded (3 assessment cycles)
│
├── [Planning]
│   ├── PlanDecomposition
│   │   └── RunWorkflow: LlmCall (7-1B, role=analyst, "decompose this story")
│   ├── ReviewPlan (bookmark — wait for junior confirmation)
│   ├── FlowDecision: PlanApproved?
│   │   ├── Yes → StartImplementation
│   │   └── No → AdjustPlan → ReviewPlan
│   └── FlowDecision: MaxAdjustments (2)
│
├── [Implementation]
│   ├── StartImplementation
│   │   └── ForEach task in plan:
│   │       └── RunWorkflow: TDD (7-1H)
│   ├── MonitorProgress (bookmark — wait for activity)
│   │   ├── ProgressSteady → continue monitoring
│   │   ├── TaskCompleted → next task or QualityGateCheck
│   │   ├── ProgressStalled → DiagnoseBlocker
│   │   ├── UnexpectedError → RunWorkflow: Debugging (7-1I, mode=runtime_error)
│   │   └── Timeout → ProvideHint
│   ├── ProvideGuidance
│   │   └── RunWorkflow: LlmCall (7-1B, role=analyst, "guide the junior")
│   └── DetectPattern
│       ├── CircularDetected → Strategic redirect
│       └── PatternResolved → MonitorProgress
│
├── [Blockers]
│   ├── DiagnoseBlocker
│   │   └── RunWorkflow: BlockerDiagnosis (7-1G)
│   ├── ProvideHint (bookmark — wait 15min)
│   ├── ProvideAssistance (bookmark — wait 45min)
│   └── EscalateToSenior (bookmark — wait for senior)
│
├── [Quality]
│   ├── QualityGateCheck
│   │   └── RunWorkflow: Testing (7-1C)
│   ├── FlowDecision: QualityResult
│   │   ├── AllPass → PrepareCodeReview
│   │   ├── MinorIssues → AutoFixIssues → QualityGateCheck (max 3)
│   │   ├── MajorIssues → ManualFixRequired (bookmark)
│   │   └── Critical → Failed
│   └── AutoFixIssues
│       └── RunWorkflow: LlmCall (7-1B, role=implementer, "fix these issues")
│
├── [Review]
│   ├── PrepareCodeReview
│   │   └── RunWorkflow: CodeReview (7-1D)
│   ├── MonitorReview (bookmark — wait for webhook)
│   ├── FlowDecision: ReviewResult
│   │   ├── Approved → MergeAndComplete
│   │   ├── ChangesRequested → GuideFixes → ReRequestReview → MonitorReview
│   │   └── Timeout → Escalate
│   └── FlowDecision: MaxReviewIterations (5)
│
├── [Completion]
│   ├── MergeAndComplete
│   ├── GenerateReport
│   ├── UpdateSkillProfile
│   └── Completed (terminal)
│
└── [Exception - reachable from any active state]
    ├── Paused (USER_PAUSE from any state)
    ├── Cancelled (USER_CANCEL from any state)
    ├── Failed (unrecoverable error)
    └── Timeout (session-level timeout)
```

### Activity Outcome Pattern

```csharp
[Activity("Tamma.Mentorship", "Assess Junior Capability",
    "Evaluate junior developer's understanding of requirements",
    Kind = ActivityKind.Task)]
[FlowNode("Correct", "Partial", "Incorrect", "Timeout", "Error")]
public class AssessJuniorCapabilityActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // Invoke Assessment sub-workflow (7-1E) and route based on result
        var result = context.GetVariable<AssessmentResult>("assessmentResult");

        var outcome = result?.Status switch
        {
            "Correct" when result.Confidence >= 0.7m => "Correct",
            "Partial" when result.Confidence >= 0.4m => "Partial",
            "Incorrect" => "Incorrect",
            "Timeout" => "Timeout",
            _ => "Error"
        };

        await context.CompleteActivityWithOutcomesAsync(new[] { outcome });
    }
}
```

### Flowchart Connection Pattern

```csharp
public class MentorshipWorkflow : IWorkflow
{
    public void Build(IWorkflowBuilder builder)
    {
        // Define activities
        var initStory = new InitStoryProcessingActivity();
        var validateStory = new ValidateStoryActivity();
        var isBug = new FlowDecision(/* expression */);
        var assess = new AssessJuniorCapabilityActivity();
        // ... all 28 states + decisions

        builder.Root = new Flowchart
        {
            Activities = { initStory, validateStory, isBug, assess, /* ... */ },
            Connections =
            {
                new Connection(initStory, validateStory),
                new Connection(validateStory, isBug),
                new Connection(isBug, "True", debugWorkflow),   // bug fast path
                new Connection(isBug, "False", assess),          // normal path
                new Connection(assess, "Correct", planDecomp),
                new Connection(assess, "Partial", clarify),
                new Connection(assess, "Incorrect", reExplain),
                new Connection(assess, "Timeout", diagnoseBlocker),
                // ... 60+ connections
            }
        };
    }
}
```

## Dependencies

- All sub-workflows must be implemented first:
  - 7-1B (LLM Call) — foundation
  - 7-1C (Testing) — quality gate
  - 7-1D (Code Review) — review lifecycle
  - 7-1E (Assessment) — skill assessment
  - 7-1F (Context Gathering) — context collection
  - 7-1G (Blocker Diagnosis) — blocker resolution
  - 7-1H (TDD) — implementation cycle
  - 7-1I (Debugging) — debugging pipeline
- `Tamma.Core.Enums.MentorshipState` (existing)
- `Tamma.Core.Entities.MentorshipSession` (existing)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (existing)
- ELSA 3.x `Flowchart`, `FlowDecision`, `RunWorkflow`, `Fork`/`Join`, `Timer` activities
- Story 7-1: State machine definition (transition table, guard conditions)
- Story 7-10: TypeScript bridge (session lifecycle API)

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs` | Create | Main flowchart workflow |
| `Tamma.Activities/Mentorship/` (existing activities) | Modify | Add `[FlowNode]` outcomes |
| `Tamma.ElsaServer/WorkflowSeeder.cs` | Delete | Replaced by code-first registration |
| `workflows/autonomous-mentorship.json` | Delete | Replaced by code-first workflow |
| `Tamma.ElsaServer/Program.cs` | Modify | Register all code-first workflows |

## Testing Strategy

### Unit Tests
- All 28 states present as activities in the flowchart
- All 60+ transitions produce correct target activities
- Guard conditions block transitions when unsatisfied
- Guard conditions allow transitions when satisfied
- Bug vs feature routing: bug issues → debugging fast path
- Timeout escalation chain fires in correct order
- Pause/resume preserves state across restart

### Integration Tests
- **Happy path**: INIT → ASSESS → PLAN → IMPLEMENT → QUALITY → REVIEW → MERGE → COMPLETED
- **Bug fast path**: INIT → VALIDATE → Debugging(bug_investigation) → QUALITY → REVIEW → MERGE
- **Blocker loop**: MONITOR → DIAGNOSE → HINT → GUIDANCE → ASSISTANCE → ESCALATE
- **Quality retry**: QUALITY → AUTO_FIX → QUALITY → PASS
- **Review iteration**: MONITOR_REVIEW → GUIDE_FIXES → RE_REQUEST → APPROVED
- **Server restart recovery**: workflow resumes from bookmark after restart

### Visual Verification
- Workflow renders correctly in ELSA Studio designer
- All 28 nodes visible with labeled connections
- Sub-workflow invocations displayed as nested activities

## Success Metrics

- All 28 states reachable via at least one test path
- 60+ transitions correctly wired
- Workflow renders in ELSA Studio with all nodes and connections
- Pause/resume survives server restart
- Bug fast path skips assessment/planning correctly
- Full happy-path session completes in <2 seconds (excluding sub-workflow execution)

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
