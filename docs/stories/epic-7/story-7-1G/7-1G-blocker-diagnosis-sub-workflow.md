# Story 7-1G: Blocker Diagnosis Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that diagnoses why a junior developer is stuck and applies progressive resolution strategies (hint → guidance → assistance → escalation) so that blockers are resolved with the minimum level of hand-holding while maintaining full audit trail of each resolution attempt.

## Description

Implement an ELSA code-first workflow (`BlockerDiagnosisWorkflow`) that collects signals from multiple sources in parallel (git activity, CI status, inactivity timers, communication history), uses AI to diagnose the blocker type and severity, then executes a progressive resolution loop. Each resolution level waits via bookmark for the junior to make progress before escalating. The Socratic method is used at early levels to maximize learning.

**Enhances**: Stories 7-6 (Blocker Diagnosis), 7-5 (Plan Decomposition)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<BlockerDiagnosisWorkflow>()`
- [ ] Visible in ELSA Studio as "Blocker Diagnosis" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `juniorId` (string) — junior developer identifier
  - `skillLevel` (int, 1-5) — current skill level
  - `blockerContext` (object, optional) — additional context about the blocker
- [ ] **Outputs**: `BlockerResolution` record containing:
  - `status` (enum: `Resolved`, `Escalated`, `Timeout`)
  - `blockerType` (enum: 8 categories — see AC5)
  - `blockerSeverity` (enum: `Low`, `Medium`, `High`, `Critical`)
  - `attempts` (int) — resolution attempts made
  - `resolutionLevel` (enum: `Hint`, `Guidance`, `Assistance`, `Escalation`)
  - `resolutionTime` (TimeSpan) — total time from diagnosis to resolution
  - `diagnosisDetails` (string) — AI's diagnosis explanation
  - `feedbackProvided` (string[]) — all feedback/guidance given

### AC3: Signal Collection (Parallel)
- [ ] ELSA `Fork` activity launches signal collectors in parallel:
  1. `CollectGitActivity`: commit frequency, file changes, time since last commit
  2. `CollectCIStatus`: build/test results, failure history
  3. `CollectInactivityTimer`: time since last meaningful activity
  4. `CollectCommunicationHistory`: Slack messages, questions asked (if available)
- [ ] ELSA `Join` waits for all signals (timeout: 15 seconds)
- [ ] Each collector is a separate activity (visible in Studio)
- [ ] Failed collectors don't block — partial signals are sufficient

### AC4: AI Diagnosis
- [ ] `AIDiagnosis` activity: RunWorkflow: LlmCall (7-1B, role=`analyst`, AnalysisType=`BlockerDiagnosis`)
  - Sends: collected signals, session history, story context, skill level
  - LLM returns: blocker type classification, severity, root cause hypothesis, recommended resolution approach
- [ ] Diagnosis result stored in workflow variables

### AC5: Blocker Classification (8 Categories)
- [ ] `ClassifyBlocker` activity categorizes into one of 8 types:
  1. **ConceptualMisunderstanding**: doesn't understand the requirement
  2. **TechnicalKnowledgeGap**: lacks specific technical skill (e.g., async/await, SQL)
  3. **EnvironmentIssue**: tooling, build, or environment problem
  4. **DesignDecisionParalysis**: can't decide on approach
  5. **DebuggingStuck**: can't find or fix a bug
  6. **IntegrationIssue**: components don't work together
  7. **ExternalDependency**: blocked by external team, API, or service
  8. **PersonalBlocker**: motivation, distraction, or capacity issue
- [ ] Each type has a recommended resolution strategy
- [ ] Severity determined by: time stuck, impact on timeline, skill level mismatch

### AC6: Progressive Resolution Loop
- [ ] Resolution proceeds through 4 levels, each with increasing directness:

**Attempt 1 — Hint (Socratic Method)**:
  - RunWorkflow: LlmCall (7-1B, role=`analyst`, "provide Socratic hints for: {blockerDiagnosis}")
  - Deliver hint via channel (Slack/API)
  - Bookmark: wait 15 minutes for progress
  - If progress detected → `Resolved`
  - If no progress → escalate to Attempt 2

**Attempt 2 — Direct Guidance**:
  - RunWorkflow: LlmCall (7-1B, role=`analyst`, "provide direct guidance for: {blockerDiagnosis}")
  - Deliver detailed guidance
  - Bookmark: wait 30 minutes for progress
  - If progress detected → `Resolved`
  - If no progress → escalate to Attempt 3

**Attempt 3 — Code Assistance**:
  - RunWorkflow: LlmCall (7-1B, role=`implementer`, "provide code example for: {blockerDiagnosis}")
  - Deliver code example with explanation
  - Bookmark: wait 45 minutes for progress
  - If progress detected → `Resolved`
  - If no progress → escalate to Attempt 4

**Attempt 4 — Senior Escalation**:
  - Compile context dump: all signals, diagnosis, previous attempts, code state
  - Notify senior developer via configured channel
  - Bookmark: wait for senior response
  - Return `Escalated`

### AC7: Progress Detection
- [ ] After each resolution attempt, detect progress via:
  - New commits on the branch
  - CI triggered with new results
  - Junior sends "resolved" signal via API/Slack
  - File changes in relevant files
- [ ] Progress detection is a separate activity (`DetectProgress`) with bookmark

### AC8: Skill-Level Adaptation
- [ ] Resolution approach adapts to skill level:
  - Level 1-2: skip Hint level (Socratic too frustrating for beginners), start with Guidance
  - Level 3: full 4-level progression
  - Level 4-5: extended Hint timeout (30 min) — give more time for self-resolution
- [ ] Wait times adjusted by skill level (configurable)

### AC9: Observability
- [ ] Each resolution attempt logged: level, content provided, wait time, outcome
- [ ] Blocker type distribution tracked: which types occur most frequently
- [ ] Resolution rate per level: what % resolved at hint vs guidance vs assistance
- [ ] Metrics: `blocker.total`, `blocker.resolved_rate`, `blocker.avg_resolution_time`, `blocker.escalation_rate`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: BlockerDiagnosisWorkflow
├── ValidateInputs
├── Fork (parallel signal collection):
│   ├── CollectGitActivity
│   ├── CollectCIStatus
│   ├── CollectInactivityTimer
│   └── CollectCommunicationHistory
├── Join (timeout 15s)
├── AIDiagnosis (RunWorkflow: LlmCall, role=analyst)
├── ClassifyBlocker (8 categories + severity)
├── AdaptToSkillLevel (determine starting level)
├── ResolutionLoop:
│   ├── [Level 1] ProvideHint (Socratic, skip for Level 1-2)
│   │   ├── RunWorkflow: LlmCall (role=analyst, Socratic hints)
│   │   ├── DeliverHint
│   │   ├── DetectProgress (bookmark, 15min / 30min for L4-5)
│   │   │   ├── Progress → Resolved
│   │   │   └── No progress → next level
│   ├── [Level 2] ProvideGuidance
│   │   ├── RunWorkflow: LlmCall (role=analyst, direct guidance)
│   │   ├── DeliverGuidance
│   │   ├── DetectProgress (bookmark, 30min)
│   │   │   ├── Progress → Resolved
│   │   │   └── No progress → next level
│   ├── [Level 3] ProvideAssistance
│   │   ├── RunWorkflow: LlmCall (role=implementer, code example)
│   │   ├── DeliverAssistance
│   │   ├── DetectProgress (bookmark, 45min)
│   │   │   ├── Progress → Resolved
│   │   │   └── No progress → next level
│   └── [Level 4] EscalateToSenior
│       ├── CompileContextDump
│       ├── NotifySenior
│       └── WaitForSenior (bookmark)
└── SetOutputs (BlockerResolution)
```

### Custom Activities

```csharp
[Activity("Tamma.Blocker", "Collect Git Activity", "Check commit frequency and file changes")]
public class CollectGitActivityActivity : CodeActivity<GitActivitySignal> { ... }

[Activity("Tamma.Blocker", "Collect CI Status", "Check build/test results")]
public class CollectCIStatusActivity : CodeActivity<CIStatusSignal> { ... }

[Activity("Tamma.Blocker", "Collect Inactivity", "Measure time since last activity")]
public class CollectInactivityActivity : CodeActivity<InactivitySignal> { ... }

[Activity("Tamma.Blocker", "Collect Communication", "Check communication patterns")]
public class CollectCommunicationActivity : CodeActivity<CommunicationSignal> { ... }

[Activity("Tamma.Blocker", "Classify Blocker", "Categorize blocker type and severity")]
[FlowNode("ConceptualMisunderstanding", "TechnicalKnowledgeGap", "EnvironmentIssue",
           "DesignDecisionParalysis", "DebuggingStuck", "IntegrationIssue",
           "ExternalDependency", "PersonalBlocker")]
public class ClassifyBlockerActivity : Activity { ... }

[Activity("Tamma.Blocker", "Detect Progress", "Wait for and detect junior's progress")]
public class DetectProgressActivity : Activity { ... }  // bookmark-based

[Activity("Tamma.Blocker", "Escalate To Senior", "Compile context and notify senior")]
public class EscalateToSeniorActivity : Activity { ... }  // bookmark-based
```

### Output Schema

```csharp
public record BlockerResolution
{
    public BlockerResolutionStatus Status { get; init; }
    public BlockerType BlockerType { get; init; }
    public BlockerSeverity BlockerSeverity { get; init; }
    public int Attempts { get; init; }
    public ResolutionLevel ResolutionLevel { get; init; }
    public TimeSpan ResolutionTime { get; init; }
    public string DiagnosisDetails { get; init; } = string.Empty;
    public List<string> FeedbackProvided { get; init; } = new();
}

public enum BlockerResolutionStatus { Resolved, Escalated, Timeout }
public enum BlockerType
{
    ConceptualMisunderstanding, TechnicalKnowledgeGap, EnvironmentIssue,
    DesignDecisionParalysis, DebuggingStuck, IntegrationIssue,
    ExternalDependency, PersonalBlocker
}
public enum BlockerSeverity { Low, Medium, High, Critical }
public enum ResolutionLevel { Hint, Guidance, Assistance, Escalation }
```

## Dependencies

- **7-1B (LLM Call)**: for AI diagnosis, hint/guidance/assistance generation
- **7-1F (Context Gathering)**: implicitly via signals (git, CI, history)
- `Tamma.Activities.Integration.GitHubActivity` (existing) — for git signal collection
- `Tamma.Activities.Integration.SlackActivity` (existing) — for delivery and communication history
- ELSA 3.x `Flowchart`, `Fork`/`Join`, `FlowDecision`, `RunWorkflow`, bookmark system

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/Blocker/CollectGitActivityActivity.cs` | Create | Git signal collector |
| `Tamma.Activities/Blocker/CollectCIStatusActivity.cs` | Create | CI signal collector |
| `Tamma.Activities/Blocker/CollectInactivityActivity.cs` | Create | Inactivity timer |
| `Tamma.Activities/Blocker/CollectCommunicationActivity.cs` | Create | Communication patterns |
| `Tamma.Activities/Blocker/ClassifyBlockerActivity.cs` | Create | 8-category classification |
| `Tamma.Activities/Blocker/DetectProgressActivity.cs` | Create | Progress detection bookmark |
| `Tamma.Activities/Blocker/EscalateToSeniorActivity.cs` | Create | Senior escalation |
| `Tamma.Activities/Blocker/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |

## Testing Strategy

### Unit Tests
- Signal collection: each collector handles API failures gracefully
- Blocker classification: all 8 types correctly identified from signal patterns
- Skill-level adaptation: Level 1-2 skip Hint, Level 4-5 extended timeouts
- Progressive resolution: each level escalates correctly on no-progress
- Progress detection: new commits, CI results, explicit signals

### Integration Tests
- Full workflow: signals → diagnosis → hint → progress → resolved (mock APIs)
- Full escalation: signals → diagnosis → hint → guidance → assistance → escalation
- Bookmark resume: workflow pauses at progress wait, resumes on activity detection
- Parallel signal collection within Fork timeout
- Standalone invocation via ELSA REST API

## Configuration

```json
{
  "BlockerDiagnosis": {
    "SignalCollectionTimeoutSeconds": 15,
    "WaitTimeMinutes": {
      "Hint": { "default": 15, "4": 30, "5": 30 },
      "Guidance": { "default": 30 },
      "Assistance": { "default": 45 }
    },
    "SkipHintForLevels": [1, 2],
    "EscalationChannel": "slack",
    "ProgressDetection": {
      "CheckInterval": "5m",
      "MinCommitsForProgress": 1,
      "MinFileChangesForProgress": 1
    }
  }
}
```

## Success Metrics

- Blocker type classification accuracy >75% agreement with human diagnosis
- >70% of blockers resolved without senior escalation
- Average resolution time <60 minutes for Level 1-3 blockers
- Socratic hints effective (progress detected) >40% of the time
- All 4 resolution levels visible in ELSA Studio
- Signal collection completes within 15 seconds
