---
title: "Story 7-7: Mentorship Quality Gate Activity"
sidebar:
  order: 70
---

## User Story

As an **autonomous mentorship system**, I need quality gate checks tailored to mentored work so that code review includes teaching feedback, test verification is pedagogically constructive, and quality standards progressively increase as the junior's skill level improves.

## Description

Extend the existing `QualityGateCheckActivity` in the ELSA/.NET codebase with mentorship-aware quality checking. The current implementation runs standard quality gates (tests, coverage, build, linting, static analysis) and returns pass/fail results. This story adds skill-level-aware thresholds, teaching-oriented feedback, progressive quality standards, and integration with `ClaudeAnalysisActivity` for AI-powered code review with educational commentary. The activity should not just identify issues but explain *why* they matter and *how* to fix them in a way that builds understanding.

## Acceptance Criteria

### AC1: Skill-Level-Aware Quality Thresholds
- [ ] Define quality thresholds per skill level (1-5):
  - Level 1: Coverage >= 60%, warnings allowed, relaxed lint rules
  - Level 2: Coverage >= 70%, minor warnings allowed
  - Level 3: Coverage >= 80%, standard lint rules (current default)
  - Level 4: Coverage >= 85%, strict lint rules
  - Level 5: Coverage >= 90%, zero warnings, full static analysis
- [ ] Thresholds are configurable per project
- [ ] Thresholds automatically adjust as junior's skill level changes
- [ ] Record which threshold tier was applied to each quality gate run

### AC2: Teaching-Oriented Code Review
- [ ] Integrate with `ClaudeAnalysisActivity` using `AnalysisType.CodeReview`
- [ ] Generate review comments that explain the "why" behind each issue
- [ ] Provide concrete before/after code examples for each issue
- [ ] Categorize review feedback: `MustFix`, `ShouldFix`, `NiceToHave`, `LearningOpportunity`
- [ ] Limit feedback volume to avoid overwhelming the junior (max 5 `MustFix`, 3 `ShouldFix`, 3 `LearningOpportunity`)
- [ ] Adapt explanation complexity to junior's skill level
- [ ] Include positive reinforcement for well-written code sections

### AC3: Test Verification with Guidance
- [ ] Verify tests exist for all new/modified code
- [ ] Check test quality (not just presence): meaningful assertions, edge cases, error paths
- [ ] If tests are missing, generate test suggestions via `SuggestionGeneratorActivity`
- [ ] Provide test template examples relevant to the code being tested
- [ ] Check for common test anti-patterns (testing implementation details, no assertions, etc.)
- [ ] Report test coverage per file, not just aggregate

### AC4: Progressive Quality Standards
- [ ] Track quality gate results across sessions for each junior
- [ ] Automatically tighten thresholds when junior consistently passes (3 consecutive passes)
- [ ] Relax thresholds temporarily after a skill regression detection
- [ ] Show progress toward next quality tier in feedback
- [ ] Award "quality badges" for milestones (first 80% coverage, zero lint errors, etc.)

### AC5: Style and Convention Checking
- [ ] Verify code follows project-specific style conventions
- [ ] Check naming conventions (variable, function, class names)
- [ ] Check file organization and module structure
- [ ] Check for consistent error handling patterns
- [ ] Provide style guide references for each violation
- [ ] Auto-fix formatting issues when possible (via `AUTO_FIX_ISSUES` state)

### AC6: Security and Best Practices
- [ ] Check for common security issues (hardcoded secrets, SQL injection, XSS)
- [ ] Verify proper input validation
- [ ] Check for proper error handling (no swallowed exceptions)
- [ ] Verify async/await usage correctness
- [ ] Check for potential performance issues (N+1 queries, missing indexes)
- [ ] Adapt security checks to skill level (basic for level 1-2, full for level 4-5)

### AC7: Quality Gate Reporting
- [ ] Generate structured quality report with overall score (0-100)
- [ ] Break down score by category: Tests, Coverage, Build, Lint, Style, Security
- [ ] Include improvement suggestions ranked by impact
- [ ] Track quality score trend over time per junior
- [ ] Deliver report via Slack with summary and link to full details
- [ ] Record all quality gate data in `IAnalyticsService`

## Technical Design

### Enhanced Quality Gate Activity (C#)

```csharp
namespace Tamma.Activities.Mentorship;

[Activity(
    "Tamma.Mentorship",
    "Mentorship Quality Gate",
    "Run quality gate checks with mentorship-aware thresholds and teaching feedback",
    Kind = ActivityKind.Task
)]
public class MentorshipQualityGateActivity : CodeActivity<MentorshipQualityGateOutput>
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story being checked")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    [Input(Description = "Override quality tier (optional, normally auto-detected)")]
    public Input<int?> QualityTierOverride { get; set; } = default!;

    [Input(Description = "Include AI-powered code review", DefaultValue = true)]
    public Input<bool> IncludeAIReview { get; set; } = new(true);
}
```

### Output Models

```csharp
public class MentorshipQualityGateOutput
{
    public bool Passed { get; set; }
    public QualityGateStatus Status { get; set; }
    public MentorshipState NextState { get; set; }

    // Scoring
    public double OverallScore { get; set; }
    public Dictionary<string, double> CategoryScores { get; set; } = new();
    public int QualityTierApplied { get; set; }

    // Gate results
    public List<GateResult> GateResults { get; set; } = new();
    public List<QualityIssue> Issues { get; set; } = new();

    // Teaching feedback
    public List<TeachingFeedback> TeachingFeedback { get; set; } = new();
    public List<string> PositiveReinforcement { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();

    // Progressive standards
    public QualityProgressInfo ProgressInfo { get; set; } = new();

    // AI review
    public AICodeReviewResult? AIReview { get; set; }

    public string? Message { get; set; }
}

public class TeachingFeedback
{
    public string Category { get; set; } = string.Empty; // MustFix, ShouldFix, NiceToHave, LearningOpportunity
    public string Title { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty; // Why this matters
    public string? CodeBefore { get; set; }
    public string? CodeAfter { get; set; }
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? StyleGuideReference { get; set; }
}

public class QualityProgressInfo
{
    public int CurrentTier { get; set; }
    public int ConsecutivePasses { get; set; }
    public int PassesNeededForNextTier { get; set; }
    public Dictionary<string, double> CurrentThresholds { get; set; } = new();
    public Dictionary<string, double> NextTierThresholds { get; set; } = new();
    public List<string> EarnedBadges { get; set; } = new();
    public List<string> UpcomingBadges { get; set; } = new();
}

public class AICodeReviewResult
{
    public string OverallQuality { get; set; } = string.Empty; // Good, Acceptable, NeedsWork
    public int Score { get; set; }
    public List<CodeReviewIssue> Issues { get; set; } = new();
    public List<string> Positives { get; set; } = new();
    public List<string> LearningOpportunities { get; set; } = new();
    public double Confidence { get; set; }
}
```

### Quality Tier Configuration

```csharp
public interface IQualityTierService
{
    /// <summary>Get quality thresholds for a skill level</summary>
    QualityThresholds GetThresholds(int skillLevel, string? projectId = null);

    /// <summary>Check if junior qualifies for tier upgrade</summary>
    Task<TierUpgradeResult> CheckTierUpgradeAsync(string juniorId);

    /// <summary>Record a quality gate result for progressive tracking</summary>
    Task RecordQualityResultAsync(string juniorId, bool passed, double score);

    /// <summary>Get quality badges earned by junior</summary>
    Task<List<QualityBadge>> GetEarnedBadgesAsync(string juniorId);
}

public class QualityThresholds
{
    public int Tier { get; set; }
    public double MinCoverage { get; set; }
    public int MaxLintWarnings { get; set; }
    public bool AllowBuildWarnings { get; set; }
    public bool RequireStaticAnalysis { get; set; }
    public bool RequireSecurityScan { get; set; }
    public int MaxComplexity { get; set; }
    public List<string> EnabledChecks { get; set; } = new();
}

public class QualityBadge
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime EarnedAt { get; set; }
}
```

### TypeScript Integration Types

```typescript
// packages/shared/src/types/quality-gate.ts

export interface MentorshipQualityGateResult {
  passed: boolean;
  status: 'Passed' | 'PassedWithWarnings' | 'Failed' | 'Error';
  overallScore: number;
  categoryScores: Record<string, number>;
  qualityTierApplied: number;
  teachingFeedback: TeachingFeedback[];
  positiveReinforcement: string[];
  suggestions: string[];
  progressInfo: QualityProgressInfo;
  aiReview?: AICodeReviewResult;
}

export interface TeachingFeedback {
  category: 'MustFix' | 'ShouldFix' | 'NiceToHave' | 'LearningOpportunity';
  title: string;
  explanation: string;
  codeBefore?: string;
  codeAfter?: string;
  filePath?: string;
  lineNumber?: number;
  styleGuideReference?: string;
}

export interface QualityProgressInfo {
  currentTier: number;
  consecutivePasses: number;
  passesNeededForNextTier: number;
  currentThresholds: Record<string, number>;
  nextTierThresholds: Record<string, number>;
  earnedBadges: QualityBadge[];
  upcomingBadges: QualityBadge[];
}

export interface QualityBadge {
  id: string;
  name: string;
  description: string;
  earnedAt: string;
}
```

## Dependencies

- Story 7-1: Mentorship State Machine Core (state transitions: `QUALITY_GATE_CHECK`, `AUTO_FIX_ISSUES`, `PREPARE_CODE_REVIEW`)
- Story 7-4: Claude Analysis Activity (`ClaudeAnalysisActivity` for AI code review)
- Existing `QualityGateCheckActivity` in `apps/tamma-elsa/src/Tamma.Activities/Mentorship/`
- Existing `SuggestionGeneratorActivity` for test suggestions
- `IIntegrationService` for build/test/coverage data
- `IAnalyticsService` for recording quality metrics and trends

## Testing Strategy

### Unit Tests
- [ ] Quality threshold selection for each skill level (1-5)
- [ ] Gate result aggregation with mentorship thresholds
- [ ] Teaching feedback generation for each issue category
- [ ] Positive reinforcement selection logic
- [ ] Progressive tier upgrade/downgrade logic
- [ ] Badge earning criteria validation
- [ ] Feedback volume limiting (max per category)
- [ ] Score calculation formula accuracy

### Integration Tests
- [ ] Full quality gate flow with real test/build data via `IIntegrationService`
- [ ] AI code review via `ClaudeAnalysisActivity` integration
- [ ] Progressive threshold tracking across multiple sessions
- [ ] Badge awarding persistence via `IAnalyticsService`
- [ ] Slack notification with quality report summary

### Edge Case Tests
- [ ] No repository URL configured (simulated results)
- [ ] All gates pass on first attempt
- [ ] All gates fail on first attempt
- [ ] Skill level changes between quality gate runs
- [ ] Concurrent quality gate runs for same junior on different stories

## Configuration

```yaml
quality_gates:
  # Tier definitions
  tiers:
    - level: 1
      min_coverage: 60
      max_lint_warnings: 20
      allow_build_warnings: true
      require_static_analysis: false
      require_security_scan: false
    - level: 2
      min_coverage: 70
      max_lint_warnings: 10
      allow_build_warnings: true
      require_static_analysis: false
      require_security_scan: false
    - level: 3
      min_coverage: 80
      max_lint_warnings: 5
      allow_build_warnings: false
      require_static_analysis: true
      require_security_scan: false
    - level: 4
      min_coverage: 85
      max_lint_warnings: 2
      allow_build_warnings: false
      require_static_analysis: true
      require_security_scan: true
    - level: 5
      min_coverage: 90
      max_lint_warnings: 0
      allow_build_warnings: false
      require_static_analysis: true
      require_security_scan: true

  # Progressive standards
  progressive:
    consecutive_passes_for_upgrade: 3
    auto_relax_on_regression: true
    relax_duration_sessions: 2

  # AI review
  ai_review:
    enabled: true
    max_must_fix: 5
    max_should_fix: 3
    max_learning_opportunity: 3
    include_positive_reinforcement: true

  # Badges
  badges:
    - id: first_green_build
      name: "Green Builder"
      description: "First successful build with all tests passing"
    - id: coverage_80
      name: "Coverage Champion"
      description: "Achieved 80% code coverage"
    - id: zero_lint
      name: "Clean Coder"
      description: "Zero linting errors"
    - id: security_clear
      name: "Security Sentinel"
      description: "Passed security scan with no issues"
```

## Success Metrics

- Quality gate pass rate improves by 20% over first 10 sessions per junior
- Teaching feedback rated helpful > 4.0/5.0 by juniors
- Average quality score increases by 15 points over 30 days per junior
- Badge earning rate: 1+ badge per 3 sessions on average
- Time spent in `AUTO_FIX_ISSUES` state decreases by 30% over time
- Reduction in same-type quality failures across sessions > 40%

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
