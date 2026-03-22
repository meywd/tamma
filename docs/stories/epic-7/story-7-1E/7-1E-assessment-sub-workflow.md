# Story 7-1E: Assessment Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that assesses a junior developer's understanding of story requirements through AI-generated questions, response analysis, and skill profiling so that the mentorship system can route the junior to the correct next step (plan, clarify, or re-explain).

## Description

Implement an ELSA code-first workflow (`AssessmentWorkflow`) that evaluates a junior developer's comprehension of assigned requirements. The workflow gathers context (via 7-1F), generates targeted questions using the LLM Call sub-workflow (7-1B), delivers questions via the configured channel, waits for the junior's response via bookmark, and analyzes the response using AI to classify understanding level and identify gaps.

**Enhances**: Stories 7-2 (Skill Assessment), 7-4 (Claude Analysis)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<AssessmentWorkflow>()`
- [ ] Visible in ELSA Studio as "Assessment" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `juniorId` (string) — junior developer identifier
  - `skillLevel` (int, 1-5) — current skill level
  - `previousAttempt` (object, optional) — previous assessment result for retry context
- [ ] **Outputs**: `AssessmentResult` record containing:
  - `status` (enum: `Correct`, `Partial`, `Incorrect`, `Timeout`)
  - `confidence` (decimal, 0.0-1.0) — AI confidence in the classification
  - `gaps` (string[]) — identified knowledge gaps
  - `strengths` (string[]) — identified strengths
  - `nextState` (enum) — recommended next mentorship state
  - `questions` (string[]) — questions that were asked
  - `juniorResponse` (string) — the junior's response
  - `analysisRationale` (string) — AI's reasoning for the classification

### AC3: Context Gathering
- [ ] First step: RunWorkflow: ContextGathering (7-1F, purpose=`Assessment`)
- [ ] Context includes: story metadata, file contents, patterns, session history
- [ ] Context used to generate relevant, specific questions

### AC4: Question Generation
- [ ] `GenerateQuestions` activity: RunWorkflow: LlmCall (7-1B, role=`analyst`)
  - Prompt includes: story context, skill level, previous attempt results (if retry)
  - Skill-level adaptation:
    - Level 1-2: 2-3 simple comprehension questions
    - Level 3: 3-4 questions including design considerations
    - Level 4-5: 4-5 questions including edge cases and architectural implications
  - Questions avoid yes/no — require explanation
  - If retrying: questions target previously identified gaps
- [ ] Questions stored in workflow variables

### AC5: Question Delivery and Response Wait
- [ ] `DeliverQuestions` activity: sends questions via configured channel
  - Channels: Slack DM, API response, email (configurable)
  - Includes context summary so junior can reference
  - Clear instructions on how to respond
- [ ] `WaitForResponse` activity: bookmark-based wait
  - Bookmark name: `assessment-{sessionId}-{attemptNumber}`
  - Timeout: 5 minutes default (configurable, per skill level)
    - Level 1-2: 10 minutes
    - Level 3: 7 minutes
    - Level 4-5: 5 minutes
  - Timeout → `Timeout` outcome
  - Response received → proceed to analysis

### AC6: Response Analysis
- [ ] `AnalyzeResponse` activity: RunWorkflow: LlmCall (7-1B, role=`analyst`, AnalysisType=`Assessment`)
  - Sends: questions, junior's response, story context, skill level
  - LLM returns structured analysis: classification, confidence, gaps, strengths, rationale
  - Analysis prompt instructs LLM to be encouraging but honest

### AC7: Result Classification
- [ ] `ClassifyResult` activity routes based on analysis:
  - **Correct** (confidence >= 0.7): junior understands requirements well
    - `nextState` = `PLAN_DECOMPOSITION`
  - **Partial** (confidence >= 0.4): junior understands some but has gaps
    - `nextState` = `CLARIFY_REQUIREMENTS`
  - **Incorrect** (confidence < 0.4): fundamental misunderstanding
    - `nextState` = `RE_EXPLAIN_STORY`
  - **Timeout**: no response received
    - `nextState` = `DIAGNOSE_BLOCKER`
- [ ] Confidence thresholds configurable via `appsettings.json`

### AC8: Skill Profile Update
- [ ] `UpdateSkillProfile` activity: updates the junior's skill profile with:
  - Assessment result and confidence
  - Identified gaps and strengths
  - Timestamp and story context
  - Running average confidence across assessments
- [ ] Profile data persisted to `mentorship_sessions` or dedicated skill tracking table

### AC9: Observability
- [ ] Each step logged: question generation time, response wait time, analysis time
- [ ] Assessment result logged with confidence and classification
- [ ] Metrics: `assessment.total`, `assessment.correct_rate`, `assessment.avg_confidence`, `assessment.timeout_rate`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: AssessmentWorkflow
├── ValidateInputs
├── RunWorkflow: ContextGathering (7-1F, purpose=Assessment)
├── GenerateQuestions (RunWorkflow: LlmCall, role=analyst)
├── DeliverQuestions (channel delivery)
├── WaitForResponse (bookmark — timeout per skill level)
│   ├── Response received → AnalyzeResponse
│   └── Timeout → SetOutputs(status=Timeout, nextState=DIAGNOSE_BLOCKER)
├── AnalyzeResponse (RunWorkflow: LlmCall, role=analyst, AnalysisType=Assessment)
├── ClassifyResult
│   ├── Correct (confidence >= 0.7) → nextState=PLAN_DECOMPOSITION
│   ├── Partial (confidence >= 0.4) → nextState=CLARIFY_REQUIREMENTS
│   └── Incorrect (confidence < 0.4) → nextState=RE_EXPLAIN_STORY
├── UpdateSkillProfile
└── SetOutputs
```

### Custom Activities

```csharp
[Activity("Tamma.Assessment", "Generate Questions",
    "Generate assessment questions adapted to skill level")]
public class GenerateQuestionsActivity : CodeActivity<QuestionSet> { ... }

[Activity("Tamma.Assessment", "Deliver Questions",
    "Send questions to junior via configured channel")]
public class DeliverQuestionsActivity : CodeActivity<DeliveryResult> { ... }

[Activity("Tamma.Assessment", "Wait For Response",
    "Pause workflow until junior responds or timeout")]
public class WaitForResponseActivity : Activity { ... }  // bookmark-based

[Activity("Tamma.Assessment", "Analyze Response",
    "AI analysis of junior's response")]
public class AnalyzeResponseActivity : CodeActivity<AnalysisResult> { ... }

[Activity("Tamma.Assessment", "Classify Result",
    "Route based on analysis confidence")]
[FlowNode("Correct", "Partial", "Incorrect", "Timeout")]
public class ClassifyResultActivity : Activity { ... }

[Activity("Tamma.Assessment", "Update Skill Profile",
    "Update junior's skill profile with assessment results")]
public class UpdateSkillProfileActivity : CodeActivity { ... }
```

### Output Schema

```csharp
public record AssessmentResult
{
    public AssessmentStatus Status { get; init; }
    public decimal Confidence { get; init; }
    public List<string> Gaps { get; init; } = new();
    public List<string> Strengths { get; init; } = new();
    public MentorshipState NextState { get; init; }
    public List<string> Questions { get; init; } = new();
    public string JuniorResponse { get; init; } = string.Empty;
    public string AnalysisRationale { get; init; } = string.Empty;
}

public enum AssessmentStatus { Correct, Partial, Incorrect, Timeout }
```

## Dependencies

- **7-1B (LLM Call)**: for question generation and response analysis
- **7-1F (Context Gathering)**: for gathering story and project context
- `Tamma.Activities.Integration.SlackActivity` (existing) — for question delivery
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (existing) — for skill profile
- ELSA 3.x `Flowchart`, `FlowDecision`, `RunWorkflow`, bookmark system

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/Assessment/GenerateQuestionsActivity.cs` | Create | Question generation |
| `Tamma.Activities/Assessment/DeliverQuestionsActivity.cs` | Create | Question delivery |
| `Tamma.Activities/Assessment/WaitForResponseActivity.cs` | Create | Bookmark-based wait |
| `Tamma.Activities/Assessment/AnalyzeResponseActivity.cs` | Create | AI response analysis |
| `Tamma.Activities/Assessment/ClassifyResultActivity.cs` | Create | Result routing |
| `Tamma.Activities/Assessment/UpdateSkillProfileActivity.cs` | Create | Skill profile update |
| `Tamma.Activities/Assessment/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |

## Testing Strategy

### Unit Tests
- Question count adapts to skill level (1-2: 2-3 questions, 4-5: 4-5 questions)
- Classification routing: confidence >= 0.7 → Correct, >= 0.4 → Partial, < 0.4 → Incorrect
- Timeout routing: no response → DIAGNOSE_BLOCKER
- Retry context: previous gaps included in regenerated questions
- Skill profile update: running average computed correctly

### Integration Tests
- Full workflow: context → questions → response → analysis → classification (mock LLM)
- Bookmark resume: workflow pauses at response wait, resumes on callback
- Timeout: no response within window → Timeout classification
- Standalone invocation via ELSA REST API

## Configuration

```json
{
  "Assessment": {
    "ConfidenceThresholds": {
      "Correct": 0.7,
      "Partial": 0.4
    },
    "TimeoutMinutes": {
      "1": 10,
      "2": 10,
      "3": 7,
      "4": 5,
      "5": 5
    },
    "QuestionsPerLevel": {
      "1": 2,
      "2": 3,
      "3": 4,
      "4": 4,
      "5": 5
    },
    "DeliveryChannel": "slack"
  }
}
```

## Success Metrics

- Assessment classification accuracy >80% agreement with human evaluation
- Question generation completes within 15 seconds
- Response analysis completes within 20 seconds
- Bookmark-based wait survives server restart
- Skill profile updates persist across sessions
- All assessment steps visible in ELSA Studio
