---
title: "Story 7-8: Skill Progress Tracking & Analytics"
sidebar:
  order: 70
---

## User Story

As a **mentorship program manager**, I need to track junior developer skill progression over time, generate progress reports, identify strengths and weaknesses, and automatically adjust task difficulty so that each junior receives an optimally challenging mentorship experience.

## Description

Implement a comprehensive skill progress tracking system that aggregates data from all mentorship activities (`AssessJuniorCapabilityActivity`, `DiagnoseBlockerActivity`, `QualityGateCheckActivity`, `MergeCompleteActivity`) to build a detailed skill profile for each junior developer. The system tracks progression across multiple skill areas (frontend, backend, testing, etc.), generates periodic progress reports, detects skill plateaus and regressions, and provides data to the task assignment system for adaptive difficulty matching. This extends the existing `IAnalyticsService` and `JuniorDeveloper` entity with richer skill tracking capabilities.

## Acceptance Criteria

### AC1: Multi-Dimensional Skill Tracking
- [ ] Track skill levels across all defined areas from `SkillAreas`: Frontend, Backend, Database, DevOps, Testing, Security, Architecture, Documentation, Communication, ProblemSolving
- [ ] Each skill area has an independent score (0.0-5.0, matching the 1-5 skill level scale)
- [ ] Calculate aggregate skill level from weighted area scores
- [ ] Record skill data points with timestamps for trend analysis
- [ ] Support custom skill areas per project

### AC2: Data Collection from Activities
- [ ] Collect assessment results from `AssessJuniorCapabilityActivity` (understanding accuracy)
- [ ] Collect blocker data from `DiagnoseBlockerActivity` (blocker types indicate skill gaps)
- [ ] Collect quality gate results from `QualityGateCheckActivity` (test coverage, lint, code quality)
- [ ] Collect code review outcomes from `CodeReviewActivity` (review pass rate, change request count)
- [ ] Collect session completion data from `MergeCompleteActivity` (time, score, skill update)
- [ ] Record guidance frequency and level from `ProvideGuidanceActivity`
- [ ] Weight recent data more heavily than older data (exponential decay)

### AC3: Skill Gap Identification
- [ ] Identify skill areas where junior consistently scores below threshold
- [ ] Detect skill areas with high blocker frequency
- [ ] Compare skill profile to requirements of assigned stories
- [ ] Generate targeted learning recommendations based on gaps
- [ ] Track gap closure rate over time
- [ ] Alert when gap persists after 5+ sessions

### AC4: Progress Reports
- [ ] Generate per-session summary reports (auto-generated at session completion)
- [ ] Generate weekly progress reports aggregating all sessions
- [ ] Generate monthly skill trend reports with visualizable data
- [ ] Reports include: skill area scores, trends, blockers overcome, quality improvements
- [ ] Reports include comparison to cohort averages (anonymized)
- [ ] Deliver reports via Slack and store for dashboard consumption

### AC5: Plateau and Regression Detection
- [ ] Detect skill plateaus: no improvement in an area for 3+ sessions
- [ ] Detect regressions: skill score drops for 2+ consecutive sessions
- [ ] Differentiate between temporary regression (new topic) and genuine regression
- [ ] Trigger proactive intervention on persistent plateau (adjust guidance strategy)
- [ ] Trigger re-assessment on regression detection
- [ ] Notify mentor/manager on significant regression

### AC6: Adaptive Difficulty Adjustment
- [ ] Calculate optimal story complexity for each junior based on current skill profile
- [ ] Recommend stories that target weakest skill areas
- [ ] Avoid assigning stories that exceed skill level by more than 2 levels
- [ ] Gradually increase complexity as skills improve (zone of proximal development)
- [ ] Factor in recent session outcomes for difficulty calibration
- [ ] Provide difficulty recommendation to story assignment system

### AC7: Skill Persistence and History
- [ ] Persist all skill data in the database via `IMentorshipSessionRepository` extensions
- [ ] Maintain full history of skill measurements (never delete, append-only)
- [ ] Support skill profile export (JSON format)
- [ ] Support skill profile import (for developer transfers)
- [ ] Retain data for compliance and audit purposes (configurable retention period)

## Technical Design

### Skill Tracking Service (C#)

```csharp
namespace Tamma.Core.Interfaces;

public interface ISkillTrackingService
{
    /// <summary>Record a skill data point from an activity</summary>
    Task RecordSkillDataPointAsync(SkillDataPoint dataPoint);

    /// <summary>Get current skill profile for a junior</summary>
    Task<SkillProfile> GetSkillProfileAsync(string juniorId);

    /// <summary>Get skill trend data for visualization</summary>
    Task<SkillTrendData> GetSkillTrendAsync(
        string juniorId, DateTime from, DateTime to);

    /// <summary>Identify skill gaps for a junior</summary>
    Task<List<SkillGap>> IdentifySkillGapsAsync(string juniorId);

    /// <summary>Generate a progress report</summary>
    Task<ProgressReport> GenerateProgressReportAsync(
        string juniorId, ReportType type);

    /// <summary>Detect plateaus and regressions</summary>
    Task<List<SkillAnomaly>> DetectAnomaliesAsync(string juniorId);

    /// <summary>Get difficulty recommendation for story assignment</summary>
    Task<DifficultyRecommendation> GetDifficultyRecommendationAsync(
        string juniorId);

    /// <summary>Recalculate aggregate skill level</summary>
    Task<int> RecalculateSkillLevelAsync(string juniorId);
}
```

### Skill Data Models (C#)

```csharp
public class SkillDataPoint
{
    public Guid Id { get; set; }
    public string JuniorId { get; set; } = string.Empty;
    public string SkillArea { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Source { get; set; } = string.Empty; // Activity name
    public Guid? SessionId { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public class SkillProfile
{
    public string JuniorId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int OverallSkillLevel { get; set; }
    public Dictionary<string, double> AreaScores { get; set; } = new();
    public Dictionary<string, SkillTrend> AreaTrends { get; set; } = new();
    public List<SkillGap> IdentifiedGaps { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public List<string> RecommendedLearning { get; set; } = new();
    public DateTime LastUpdated { get; set; }
    public int TotalDataPoints { get; set; }
}

public enum SkillTrend
{
    Improving,
    Stable,
    Declining,
    Plateau,
    NewArea // Not enough data
}

public class SkillGap
{
    public string SkillArea { get; set; } = string.Empty;
    public double CurrentScore { get; set; }
    public double TargetScore { get; set; }
    public double GapSize { get; set; }
    public int SessionsWithGap { get; set; }
    public string? RecommendedAction { get; set; }
    public List<string> RelatedBlockerTypes { get; set; } = new();
}

public class SkillAnomaly
{
    public string SkillArea { get; set; } = string.Empty;
    public AnomalyType Type { get; set; }
    public int DurationSessions { get; set; }
    public double CurrentScore { get; set; }
    public double PreviousScore { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? RecommendedIntervention { get; set; }
}

public enum AnomalyType
{
    Plateau,
    Regression,
    RapidImprovement,
    SkillMismatch
}

public class ProgressReport
{
    public string JuniorId { get; set; } = string.Empty;
    public ReportType Type { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    // Summary
    public int SessionsCompleted { get; set; }
    public double SuccessRate { get; set; }
    public double AverageQualityScore { get; set; }
    public int BlockersOvercome { get; set; }

    // Skills
    public Dictionary<string, double> SkillScores { get; set; } = new();
    public Dictionary<string, double> SkillChanges { get; set; } = new();
    public List<string> ImprovedAreas { get; set; } = new();
    public List<string> FocusAreas { get; set; } = new();

    // Achievements
    public List<string> NewBadges { get; set; } = new();
    public List<string> Milestones { get; set; } = new();

    // Recommendations
    public List<string> LearningRecommendations { get; set; } = new();
    public DifficultyRecommendation? NextDifficulty { get; set; }

    // Cohort comparison (anonymized)
    public CohortComparison? CohortComparison { get; set; }
}

public enum ReportType
{
    Session,
    Weekly,
    Monthly,
    Quarterly
}

public class DifficultyRecommendation
{
    public int OptimalComplexity { get; set; }
    public int MinComplexity { get; set; }
    public int MaxComplexity { get; set; }
    public List<string> RecommendedSkillAreas { get; set; } = new();
    public string Rationale { get; set; } = string.Empty;
}

public class CohortComparison
{
    public double CohortAverageScore { get; set; }
    public string Percentile { get; set; } = string.Empty; // "Top 25%", "Average", etc.
    public Dictionary<string, double> CohortAreaAverages { get; set; } = new();
}
```

### TypeScript Integration Types

```typescript
// packages/shared/src/types/skill-tracking.ts

export interface SkillProfile {
  juniorId: string;
  name: string;
  overallSkillLevel: number;
  areaScores: Record<string, number>;
  areaTrends: Record<string, SkillTrend>;
  identifiedGaps: SkillGap[];
  strengths: string[];
  recommendedLearning: string[];
  lastUpdated: string;
  totalDataPoints: number;
}

export type SkillTrend = 'Improving' | 'Stable' | 'Declining' | 'Plateau' | 'NewArea';

export interface SkillGap {
  skillArea: string;
  currentScore: number;
  targetScore: number;
  gapSize: number;
  sessionsWithGap: number;
  recommendedAction?: string;
  relatedBlockerTypes: string[];
}

export interface ProgressReport {
  juniorId: string;
  type: 'Session' | 'Weekly' | 'Monthly' | 'Quarterly';
  periodStart: string;
  periodEnd: string;
  sessionsCompleted: number;
  successRate: number;
  averageQualityScore: number;
  blockersOvercome: number;
  skillScores: Record<string, number>;
  skillChanges: Record<string, number>;
  improvedAreas: string[];
  focusAreas: string[];
  newBadges: string[];
  milestones: string[];
  learningRecommendations: string[];
  nextDifficulty?: DifficultyRecommendation;
}

export interface DifficultyRecommendation {
  optimalComplexity: number;
  minComplexity: number;
  maxComplexity: number;
  recommendedSkillAreas: string[];
  rationale: string;
}

export interface SkillTrendData {
  juniorId: string;
  from: string;
  to: string;
  dataPoints: SkillTrendPoint[];
}

export interface SkillTrendPoint {
  date: string;
  area: string;
  score: number;
  source: string;
  sessionId?: string;
}
```

## Dependencies

- Story 7-2: Skill Assessment Activity (provides assessment scores)
- Story 7-6: Blocker Diagnosis Activity (provides blocker type data)
- Story 7-7: Quality Gate Activity (provides quality scores)
- Existing `MergeCompleteActivity` (provides session completion data)
- Existing `IAnalyticsService` interface and implementations
- Existing `JuniorDeveloper` entity and `SkillAreas` constants
- `IMentorshipSessionRepository` for persisting skill data

## Testing Strategy

### Unit Tests
- [ ] Skill score calculation from individual data points
- [ ] Weighted average with exponential decay for recent data
- [ ] Aggregate skill level recalculation from area scores
- [ ] Skill gap identification with configurable thresholds
- [ ] Plateau detection (3+ sessions without improvement)
- [ ] Regression detection (2+ sessions of decline)
- [ ] Difficulty recommendation algorithm accuracy
- [ ] Progress report generation with correct aggregation
- [ ] Cohort comparison percentile calculation

### Integration Tests
- [ ] End-to-end data flow from activity completion to skill profile update
- [ ] Skill tracking across multiple sessions for one junior
- [ ] Report generation with real session data
- [ ] Anomaly detection triggering notifications
- [ ] Skill profile persistence and retrieval

### Edge Case Tests
- [ ] First session (no historical data for trends)
- [ ] Junior with only 1 skill area having data
- [ ] Rapid skill improvement (large score jumps)
- [ ] All skills at maximum level (5.0)
- [ ] Cohort of size 1 (no meaningful comparison)

## Configuration

```yaml
skill_tracking:
  # Score calculation
  exponential_decay_factor: 0.85  # Weight factor for older data points
  min_data_points_for_trend: 3
  max_data_points_for_calculation: 50

  # Skill level mapping
  level_thresholds:
    1: 0.0    # 0.0 - 1.0
    2: 1.0    # 1.0 - 2.0
    3: 2.0    # 2.0 - 3.0
    4: 3.0    # 3.0 - 4.0
    5: 4.0    # 4.0 - 5.0

  # Area weights for aggregate calculation
  area_weights:
    backend: 1.0
    frontend: 1.0
    testing: 0.9
    problem_solving: 0.9
    architecture: 0.8
    database: 0.8
    security: 0.7
    devops: 0.6
    documentation: 0.5
    communication: 0.5

  # Anomaly detection
  plateau_threshold_sessions: 3
  regression_threshold_sessions: 2
  regression_score_drop: 0.3

  # Difficulty adjustment
  difficulty:
    optimal_gap: 1          # Optimal story complexity above current skill
    max_gap: 2              # Never exceed skill by more than this
    min_gap: 0              # At least match current skill
    weak_area_weight: 1.5   # Prefer stories targeting weak areas

  # Reports
  reports:
    weekly_day: "Monday"
    monthly_day: 1
    delivery_channels:
      - slack
    include_cohort_comparison: true
    min_cohort_size: 3

  # Retention
  data_retention_days: 730  # 2 years
```

## Success Metrics

- Skill profile accuracy validated by mentor review > 85%
- Plateau detection rate > 90% (confirmed by subsequent session outcomes)
- Regression detection false positive rate < 10%
- Difficulty recommendation followed in > 70% of story assignments
- Junior self-reported progress perception matches tracked data > 75% correlation
- Average skill level improvement of 0.5 points per month across all juniors

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
