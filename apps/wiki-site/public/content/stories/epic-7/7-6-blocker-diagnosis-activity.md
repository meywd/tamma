---
title: "Story 7-6: Blocker Diagnosis & Resolution Activity"
sidebar:
  order: 70
---

## User Story

As an **autonomous mentorship system**, I need to diagnose what is blocking a junior developer (technical error, conceptual confusion, environment issue, or analysis paralysis) and provide targeted help so that blockers are resolved quickly and the junior continues to learn.

## Description

Extend the existing `DiagnoseBlockerActivity` in the ELSA/.NET codebase with enhanced diagnostic capabilities, resolution workflows, and AI-powered root cause analysis. The current implementation performs basic pattern matching against build failures, test results, and inactivity. This story adds deeper diagnostic intelligence including Claude-powered analysis, multi-signal correlation, resolution tracking, and adaptive escalation paths. The activity integrates with `ClaudeAnalysisActivity` for AI-driven diagnosis, `ProvideGuidanceActivity` for delivering targeted help, and `IAnalyticsService` for recording diagnostic outcomes.

## Acceptance Criteria

### AC1: Enhanced Blocker Classification
- [ ] Classify blockers into primary categories: `TECHNICAL_ERROR`, `CONCEPTUAL_CONFUSION`, `ENVIRONMENT_ISSUE`, `ANALYSIS_PARALYSIS`, `DEPENDENCY_ISSUE`, `ARCHITECTURE_CONFUSION`, `TESTING_CHALLENGE`, `MOTIVATION_ISSUE`
- [ ] Support sub-categories within each primary type (e.g., `TECHNICAL_ERROR.SYNTAX`, `TECHNICAL_ERROR.LOGIC`, `TECHNICAL_ERROR.RUNTIME`)
- [ ] Assign severity levels: `Low`, `Medium`, `High`, `Critical`
- [ ] Calculate confidence score (0.0-1.0) for each diagnosis
- [ ] Support multiple concurrent blocker detection (junior may face more than one blocker)

### AC2: Multi-Signal Diagnostic Data Collection
- [ ] Collect GitHub commit history and frequency via `IIntegrationService`
- [ ] Analyze build status and error messages from CI/CD
- [ ] Parse test failure details including stack traces and assertion messages
- [ ] Track time-since-last-activity to detect stalls
- [ ] Collect file change patterns to detect circular behavior (same files edited repeatedly)
- [ ] Gather Slack/communication history for context clues
- [ ] Record junior's self-reported status if available

### AC3: AI-Powered Root Cause Analysis
- [ ] Integrate with `ClaudeAnalysisActivity` using `AnalysisType.BlockerDiagnosis`
- [ ] Pass diagnostic data, recent code changes, and error messages as context
- [ ] Receive structured diagnosis with `blocker_type`, `root_cause`, `evidence`, and `recommended_intervention`
- [ ] Fall back to rule-based diagnosis when AI analysis is unavailable
- [ ] Adapt analysis prompts based on junior's skill level (1-5)

### AC4: Resolution Workflow
- [ ] Map each blocker type to a resolution strategy:
  - `TECHNICAL_ERROR` -> `PROVIDE_HINT` or `PROVIDE_ASSISTANCE` based on severity
  - `CONCEPTUAL_CONFUSION` -> `PROVIDE_GUIDANCE` with examples and Socratic questions
  - `ENVIRONMENT_ISSUE` -> `PROVIDE_ASSISTANCE` with step-by-step fix commands
  - `ANALYSIS_PARALYSIS` -> `BREAK_DOWN_TASK` with smaller sub-tasks
  - `DEPENDENCY_ISSUE` -> `PROVIDE_ASSISTANCE` with dependency resolution steps
  - `ARCHITECTURE_CONFUSION` -> `PROVIDE_GUIDANCE` with architecture examples
  - `TESTING_CHALLENGE` -> `PROVIDE_GUIDANCE` with testing patterns
  - `MOTIVATION_ISSUE` -> encouragement message + check-in schedule
- [ ] Track resolution attempt count per blocker
- [ ] Escalate to `ESCALATE_TO_SENIOR` after 3 failed resolution attempts
- [ ] Auto-escalate critical blockers immediately

### AC5: Adaptive Escalation
- [ ] Progressive escalation: Hint (level 1) -> Guidance (level 2) -> Assistance (level 3) -> Senior escalation (level 4)
- [ ] Adjust escalation speed based on junior skill level (lower skill = faster escalation)
- [ ] Adjust escalation based on story complexity vs skill level gap
- [ ] Track time spent at each escalation level
- [ ] Notify via Slack/email at each escalation level

### AC6: Resolution Verification
- [ ] After providing resolution, monitor for progress resumption
- [ ] Verify fix by checking build status and test results via `IIntegrationService`
- [ ] Auto-verify if no response within timeout (configurable, default 15 min)
- [ ] Record resolution outcome: `resolved`, `partially_resolved`, `unresolved`, `escalated`
- [ ] Update session analytics with resolution metrics

### AC7: Blocker Analytics
- [ ] Record all diagnosed blockers with `IAnalyticsService.RecordMetricAsync`
- [ ] Track resolution time per blocker type
- [ ] Track most common blockers per junior developer
- [ ] Identify recurring blocker patterns across sessions
- [ ] Feed blocker data back to `SuggestionGeneratorActivity` for proactive guidance

## Technical Design

### Enhanced Blocker Diagnosis Activity (C#)

```csharp
namespace Tamma.Activities.Mentorship;

/// <summary>
/// Enhanced ELSA activity for blocker diagnosis with AI-powered analysis
/// and adaptive resolution workflows.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Enhanced Blocker Diagnosis",
    "Diagnose and resolve blockers with AI-powered analysis",
    Kind = ActivityKind.Task
)]
public class EnhancedDiagnoseBlockerActivity : CodeActivity<EnhancedBlockerDiagnosisOutput>
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story being worked on")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    [Input(Description = "Additional context about the blocker")]
    public Input<string?> BlockerContext { get; set; } = default!;

    [Input(Description = "Current escalation level (1-4)", DefaultValue = 1)]
    public Input<int> EscalationLevel { get; set; } = new(1);

    [Input(Description = "Previous resolution attempts count", DefaultValue = 0)]
    public Input<int> PreviousAttempts { get; set; } = new(0);
}
```

### Enhanced Output Model

```csharp
public class EnhancedBlockerDiagnosisOutput
{
    public BlockerType PrimaryBlocker { get; set; }
    public string? SubCategory { get; set; }
    public BlockerSeverity Severity { get; set; }
    public double Confidence { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? RootCause { get; set; }
    public List<string> Evidence { get; set; } = new();

    // Resolution
    public ResolutionStrategy RecommendedStrategy { get; set; }
    public int EscalationLevel { get; set; }
    public MentorshipState NextState { get; set; }
    public string? Message { get; set; }
    public List<string> ActionItems { get; set; } = new();
    public List<string> RelatedResources { get; set; } = new();

    // Multiple blockers
    public List<SecondaryBlocker> SecondaryBlockers { get; set; } = new();

    // AI analysis
    public bool AIAnalysisUsed { get; set; }
    public string? AIRawResponse { get; set; }
}

public class SecondaryBlocker
{
    public BlockerType Type { get; set; }
    public double Confidence { get; set; }
    public string Description { get; set; } = string.Empty;
}

public enum ResolutionStrategy
{
    ProvideHint,
    ProvideGuidance,
    ProvideAssistance,
    BreakDownTask,
    PairProgramming,
    EscalateToSenior,
    EnvironmentFix,
    MotivationSupport
}
```

### Blocker Resolution Service Interface

```csharp
public interface IBlockerResolutionService
{
    /// <summary>Diagnose blocker with multi-signal analysis</summary>
    Task<EnhancedBlockerDiagnosisOutput> DiagnoseAsync(
        DiagnosticContext context,
        int currentEscalationLevel);

    /// <summary>Get resolution strategy for a blocker type</summary>
    ResolutionStrategy GetResolutionStrategy(
        BlockerType blockerType,
        BlockerSeverity severity,
        int skillLevel,
        int previousAttempts);

    /// <summary>Verify if a previous resolution was effective</summary>
    Task<ResolutionVerificationResult> VerifyResolutionAsync(
        Guid sessionId,
        BlockerType originalBlocker);

    /// <summary>Get blocker history for a junior developer</summary>
    Task<BlockerHistory> GetBlockerHistoryAsync(string juniorId);
}

public class DiagnosticContext
{
    public Guid SessionId { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public int JuniorSkillLevel { get; set; }
    public int StoryComplexity { get; set; }
    public string? AdditionalContext { get; set; }
    public DiagnosticData CollectedData { get; set; } = new();
    public List<BehaviorPattern> DetectedPatterns { get; set; } = new();
}

public class ResolutionVerificationResult
{
    public bool Resolved { get; set; }
    public string Outcome { get; set; } = string.Empty; // resolved, partially_resolved, unresolved
    public TimeSpan ResolutionTime { get; set; }
    public string? RemainingIssue { get; set; }
}

public class BlockerHistory
{
    public string JuniorId { get; set; } = string.Empty;
    public int TotalBlockers { get; set; }
    public Dictionary<BlockerType, int> BlockersByType { get; set; } = new();
    public Dictionary<BlockerType, TimeSpan> AverageResolutionTime { get; set; } = new();
    public List<RecurringBlocker> RecurringBlockers { get; set; } = new();
}

public class RecurringBlocker
{
    public BlockerType Type { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public int Occurrences { get; set; }
    public string? RecommendedTraining { get; set; }
}
```

### TypeScript Integration Types

```typescript
// packages/shared/src/types/blocker-diagnosis.ts

export interface BlockerDiagnosisRequest {
  sessionId: string;
  storyId: string;
  juniorId: string;
  blockerContext?: string;
  escalationLevel: number;
  previousAttempts: number;
}

export interface BlockerDiagnosisResult {
  primaryBlocker: BlockerType;
  subCategory?: string;
  severity: 'Low' | 'Medium' | 'High' | 'Critical';
  confidence: number;
  description: string;
  rootCause?: string;
  evidence: string[];
  recommendedStrategy: ResolutionStrategy;
  escalationLevel: number;
  nextState: string;
  actionItems: string[];
  relatedResources: string[];
  secondaryBlockers: SecondaryBlocker[];
  aiAnalysisUsed: boolean;
}

export type BlockerType =
  | 'TECHNICAL_ERROR'
  | 'CONCEPTUAL_CONFUSION'
  | 'ENVIRONMENT_ISSUE'
  | 'ANALYSIS_PARALYSIS'
  | 'DEPENDENCY_ISSUE'
  | 'ARCHITECTURE_CONFUSION'
  | 'TESTING_CHALLENGE'
  | 'MOTIVATION_ISSUE'
  | 'UNKNOWN';

export type ResolutionStrategy =
  | 'ProvideHint'
  | 'ProvideGuidance'
  | 'ProvideAssistance'
  | 'BreakDownTask'
  | 'PairProgramming'
  | 'EscalateToSenior'
  | 'EnvironmentFix'
  | 'MotivationSupport';
```

## Dependencies

- Story 7-1: Mentorship State Machine Core (state transitions)
- Story 7-4: Claude Analysis Activity (`ClaudeAnalysisActivity` for AI diagnosis)
- Story 7-5: Plan Decomposition Activity (for `BreakDownTask` resolution)
- Existing `DiagnoseBlockerActivity` in `apps/tamma-elsa/src/Tamma.Activities/Mentorship/`
- Existing `ProvideGuidanceActivity` for delivering resolution guidance
- `IIntegrationService` for GitHub/Slack data collection
- `IAnalyticsService` for pattern detection and metric recording

## Testing Strategy

### Unit Tests
- [ ] Blocker classification correctness for each category
- [ ] Sub-category detection accuracy
- [ ] Severity assignment logic
- [ ] Confidence score calculation
- [ ] Resolution strategy mapping for each blocker type and skill level
- [ ] Escalation level progression logic
- [ ] Multi-blocker detection and priority ordering
- [ ] Fallback to rule-based diagnosis when AI unavailable

### Integration Tests
- [ ] Full diagnosis flow: data collection -> analysis -> classification -> resolution
- [ ] AI-powered diagnosis via `ClaudeAnalysisActivity` integration
- [ ] Resolution verification after fix applied
- [ ] Escalation workflow from hint through senior escalation
- [ ] Analytics recording for all diagnosis outcomes
- [ ] Slack notification delivery for each escalation level

### Edge Case Tests
- [ ] No diagnostic data available (no commits, no build status)
- [ ] Multiple simultaneous blockers detected
- [ ] Rapid re-diagnosis (same blocker diagnosed twice within 5 minutes)
- [ ] Skill level at extremes (1 and 5)
- [ ] Story complexity exceeds skill level by 3+ levels

## Configuration

```yaml
blocker_diagnosis:
  # Data collection
  commit_lookback_hours: 24
  inactivity_threshold_minutes: 30
  circular_behavior_threshold: 3  # repeated changes to same files

  # AI analysis
  use_ai_analysis: true
  ai_fallback_to_rules: true
  ai_timeout_seconds: 30

  # Escalation
  max_resolution_attempts: 3
  escalation_levels:
    - level: 1
      type: hint
      timeout_minutes: 15
    - level: 2
      type: guidance
      timeout_minutes: 20
    - level: 3
      type: assistance
      timeout_minutes: 30
    - level: 4
      type: senior_escalation
      timeout_minutes: 60

  # Skill-based adjustments
  fast_escalation_skill_threshold: 2  # Skill level below which escalation is faster
  escalation_speedup_factor: 0.5      # Reduce timeouts by 50% for low-skill juniors

  # Notifications
  notify_on_diagnosis: true
  notify_on_escalation: true
  notification_channels:
    - slack
    - email
```

## Success Metrics

- Blocker diagnosis accuracy > 85% (validated by resolution outcomes)
- Average blocker resolution time < 30 minutes
- Escalation to senior rate < 15% of all blockers
- AI-powered diagnosis used in > 70% of cases
- Recurring blocker reduction > 25% over 30 days
- Junior self-reported helpfulness score > 4.0/5.0

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
