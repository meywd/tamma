# Story 7-10: Mentorship Dashboard & Reporting

## User Story

As a **mentorship program manager**, I need a dashboard showing active mentorship sessions, progress charts, skill heatmaps, session analytics, mentor workload, and learning outcomes so that I can monitor program health, identify struggling juniors, and make data-driven decisions about the mentorship program.

## Description

Build a React-based mentorship dashboard that visualizes data from all mentorship activities and services. The dashboard consumes data from the REST API (Story 7-9), `IAnalyticsService`, and `ISkillTrackingService` (Story 7-8) to present real-time session monitoring, historical analytics, skill progression visualizations, and program-level KPIs. The dashboard follows the existing component patterns from the planned `Tamma.Dashboard` project and the monitoring dashboard layout defined in the research docs.

## Acceptance Criteria

### AC1: Active Sessions Overview
- [ ] Real-time list of all active mentorship sessions
- [ ] Each session card shows: junior name, story title, current state, duration, progress percentage
- [ ] Color-coded state indicators (green=steady, yellow=slowing, red=stalled/blocked)
- [ ] Quick-action buttons: Pause, Cancel, View Details
- [ ] Auto-refresh with Server-Sent Events (SSE) or polling (configurable, default 10s)
- [ ] Filter by: junior, mentor, status, state, date range
- [ ] Sort by: start time, duration, progress, state

### AC2: Session Detail View
- [ ] State flow visualization showing the path through the mentorship state machine
- [ ] Timeline of all events with timestamps and descriptions
- [ ] Current state details with context (what the junior is working on)
- [ ] Blocker history with diagnosis and resolution outcomes
- [ ] Quality gate results with teaching feedback
- [ ] Session controls: Pause, Resume, Cancel, Reassign Mentor
- [ ] Real-time state updates via SSE

### AC3: Skill Progress Charts
- [ ] Radar/spider chart showing skill area scores for a selected junior
- [ ] Line chart showing skill progression over time per area
- [ ] Heatmap showing skill strengths and weaknesses across all juniors
- [ ] Comparison view: overlay two juniors' skill profiles
- [ ] Trend indicators (arrows) for each skill area
- [ ] Filter by time range: last week, last month, last quarter, all time
- [ ] Drill-down from area to individual data points

### AC4: Session Analytics
- [ ] Summary KPIs: total sessions, completion rate, average duration, average quality score
- [ ] Session outcome distribution (completed, failed, cancelled, timed out)
- [ ] Average time per mentorship state (identify bottleneck states)
- [ ] State transition sankey diagram (flow between states)
- [ ] Blocker type distribution pie chart
- [ ] Quality gate pass/fail trends over time
- [ ] Session duration histogram
- [ ] Date range selector for all analytics views

### AC5: Mentor Workload View
- [ ] Mentor roster with current active session count
- [ ] Mentor capacity utilization (active/max)
- [ ] Mentor-mentee assignment history
- [ ] Average session outcome per mentor
- [ ] Mentor availability calendar integration (optional)
- [ ] Workload balancing recommendations

### AC6: Learning Outcomes Dashboard
- [ ] Program-wide skill level distribution (histogram)
- [ ] Skill level improvement rate across the program
- [ ] Most common blocker types and resolution success rates
- [ ] Top skill areas improving and declining
- [ ] Badge and milestone leaderboard
- [ ] Learning recommendation effectiveness (how often followed, impact on skill)
- [ ] Cohort comparison across time periods

### AC7: Reporting and Export
- [ ] Generate PDF reports for individual juniors or the full program
- [ ] Schedule automated weekly/monthly report emails
- [ ] Export raw data as CSV or JSON for custom analysis
- [ ] Configurable report templates
- [ ] Share dashboard views via permalink
- [ ] Print-friendly layout for reports

## Technical Design

### Dashboard Component Architecture

```typescript
// apps/tamma-engine/src/Tamma.Dashboard/src/types/dashboard.ts

export interface DashboardState {
  activeSessions: SessionSummary[];
  analytics: DashboardAnalytics;
  skillProfiles: Map<string, SkillProfile>;
  mentorWorkload: MentorWorkload[];
  learningOutcomes: LearningOutcomes;
  filters: DashboardFilters;
  isLoading: boolean;
  error?: string;
}

export interface DashboardAnalytics {
  period: DateRange;
  totalSessions: number;
  completedSessions: number;
  failedSessions: number;
  cancelledSessions: number;
  averageDurationHours: number;
  averageQualityScore: number;
  completionRate: number;
  averageTimePerState: Record<string, number>; // state -> minutes
  blockerDistribution: Record<string, number>; // blocker type -> count
  qualityTrends: QualityTrendPoint[];
  sessionDurationDistribution: HistogramBucket[];
  stateTransitionCounts: StateTransitionCount[];
}

export interface MentorWorkload {
  mentorId: string;
  mentorName: string;
  activeSessions: number;
  maxSessions: number;
  utilizationPercent: number;
  totalSessionsCompleted: number;
  averageOutcomeScore: number;
  currentMentees: string[];
}

export interface LearningOutcomes {
  skillLevelDistribution: Record<number, number>; // skill level -> count
  skillImprovementRate: number; // average points per month
  topImprovingAreas: SkillAreaStat[];
  topDecliningAreas: SkillAreaStat[];
  commonBlockers: BlockerStat[];
  badgeLeaderboard: BadgeLeaderEntry[];
  recommendationEffectiveness: number; // percentage followed
}

export interface SkillAreaStat {
  area: string;
  averageScore: number;
  trend: 'up' | 'down' | 'stable';
  changePercent: number;
}

export interface BlockerStat {
  type: string;
  count: number;
  resolutionRate: number;
  averageResolutionMinutes: number;
}

export interface BadgeLeaderEntry {
  juniorId: string;
  juniorName: string;
  badgeCount: number;
  recentBadge: string;
}

export interface QualityTrendPoint {
  date: string;
  averageScore: number;
  passRate: number;
}

export interface HistogramBucket {
  rangeLabel: string;
  count: number;
}

export interface StateTransitionCount {
  from: string;
  to: string;
  count: number;
}

export interface DashboardFilters {
  juniorId?: string;
  mentorId?: string;
  status?: string;
  dateRange: DateRange;
  skillArea?: string;
}

export interface DateRange {
  from: string;
  to: string;
}
```

### React Component Structure

```typescript
// Component hierarchy
// apps/tamma-engine/src/Tamma.Dashboard/src/components/

// Top-level layout
// MentorshipDashboard.tsx - Main dashboard container with tabs

// Tab: Active Sessions
// ActiveSessionsPanel.tsx
//   SessionCard.tsx - Individual session summary card
//   SessionFilters.tsx - Filter controls
//   SessionDetailModal.tsx - Full session detail overlay
//     StateFlowVisualization.tsx - State machine path visualization
//     EventTimeline.tsx - Chronological event list
//     BlockerHistory.tsx - Blocker diagnosis history
//     QualityGateResults.tsx - Quality gate details

// Tab: Skill Progress
// SkillProgressPanel.tsx
//   SkillRadarChart.tsx - Spider/radar chart for skill areas
//   SkillTrendChart.tsx - Line chart for skill over time
//   SkillHeatmap.tsx - Heatmap across all juniors
//   SkillComparisonView.tsx - Side-by-side comparison

// Tab: Analytics
// AnalyticsPanel.tsx
//   KPICards.tsx - Summary metric cards
//   SessionOutcomeChart.tsx - Pie/donut chart
//   StateTimeBreakdown.tsx - Bar chart of time per state
//   StateSankeyDiagram.tsx - Sankey flow diagram
//   BlockerDistribution.tsx - Pie chart
//   QualityTrendChart.tsx - Line chart
//   DurationHistogram.tsx - Histogram

// Tab: Mentors
// MentorWorkloadPanel.tsx
//   MentorCard.tsx - Individual mentor workload card
//   WorkloadChart.tsx - Utilization bar chart
//   AssignmentHistory.tsx - Mentor-mentee history table

// Tab: Learning Outcomes
// LearningOutcomesPanel.tsx
//   SkillDistribution.tsx - Histogram of skill levels
//   ImprovementRate.tsx - Trend chart
//   BadgeLeaderboard.tsx - Leaderboard table
//   RecommendationEffectiveness.tsx - Metrics display
```

### Dashboard API Service

```typescript
// apps/tamma-engine/src/Tamma.Dashboard/src/services/dashboardApi.ts

export interface IDashboardApi {
  // Sessions
  getActiveSessions(filters?: DashboardFilters): Promise<SessionSummary[]>;
  getSessionDetails(sessionId: string): Promise<SessionDetails>;
  getSessionTimeline(sessionId: string): Promise<SessionTimeline>;
  pauseSession(sessionId: string): Promise<void>;
  resumeSession(sessionId: string): Promise<void>;
  cancelSession(sessionId: string, reason: string): Promise<void>;

  // Analytics
  getDashboardAnalytics(dateRange: DateRange): Promise<DashboardAnalytics>;
  getStateTransitionAnalytics(dateRange: DateRange): Promise<StateTransitionCount[]>;
  getBlockerAnalytics(dateRange: DateRange): Promise<BlockerStat[]>;

  // Skills
  getSkillProfile(juniorId: string): Promise<SkillProfile>;
  getSkillTrend(juniorId: string, dateRange: DateRange): Promise<SkillTrendData>;
  getAllSkillProfiles(): Promise<SkillProfile[]>;

  // Mentors
  getMentorWorkload(): Promise<MentorWorkload[]>;

  // Learning
  getLearningOutcomes(dateRange: DateRange): Promise<LearningOutcomes>;

  // Reporting
  generateReport(params: ReportParams): Promise<Blob>;
  exportData(params: ExportParams): Promise<Blob>;

  // Real-time
  subscribeToSessionUpdates(
    callback: (event: SessionUpdateEvent) => void
  ): EventSource;
}

export interface ReportParams {
  type: 'individual' | 'program';
  juniorId?: string;
  dateRange: DateRange;
  format: 'pdf' | 'html';
  template?: string;
}

export interface ExportParams {
  dataType: 'sessions' | 'skills' | 'analytics' | 'blockers';
  dateRange: DateRange;
  format: 'csv' | 'json';
  filters?: DashboardFilters;
}

export interface SessionUpdateEvent {
  type: 'state_change' | 'progress_update' | 'blocker_detected' | 'session_completed';
  sessionId: string;
  data: Record<string, unknown>;
  timestamp: string;
}
```

### Backend API Controller Extensions (C#)

```csharp
namespace Tamma.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IMentorshipSessionService _sessionService;
    private readonly IAnalyticsService _analyticsService;
    private readonly ISkillTrackingService _skillService;

    [HttpGet("analytics")]
    public async Task<ActionResult<DashboardAnalytics>> GetAnalytics(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        var analytics = await _analyticsService.GetAggregatedAnalyticsAsync(from, to);
        return Ok(analytics);
    }

    [HttpGet("mentors/workload")]
    public async Task<ActionResult<List<MentorWorkload>>> GetMentorWorkload()
    {
        // Implementation returns workload for all active mentors
    }

    [HttpGet("learning-outcomes")]
    public async Task<ActionResult<LearningOutcomes>> GetLearningOutcomes(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        // Aggregate learning outcome data across all juniors
    }

    [HttpGet("skills/heatmap")]
    public async Task<ActionResult<List<SkillProfile>>> GetSkillHeatmap()
    {
        // Return skill profiles for all active juniors
    }

    [HttpGet("sessions/stream")]
    public async Task StreamSessionUpdates(CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Stream real-time session updates via SSE
    }

    [HttpPost("reports/generate")]
    public async Task<FileResult> GenerateReport(
        [FromBody] ReportRequest request)
    {
        // Generate PDF or HTML report
    }

    [HttpPost("export")]
    public async Task<FileResult> ExportData(
        [FromBody] ExportRequest request)
    {
        // Export data as CSV or JSON
    }
}
```

## Dependencies

- Story 7-8: Skill Progress Tracking (`ISkillTrackingService` for skill data)
- Story 7-9: Session Management (REST API for session operations)
- Existing `IAnalyticsService` for aggregated analytics
- Existing `MentorshipController` and API infrastructure
- Planned `Tamma.Dashboard` React application structure
- Charting library (e.g., Recharts, Chart.js, or D3)

## Testing Strategy

### Unit Tests (Frontend)
- [ ] Component rendering for each dashboard panel
- [ ] Filter state management and URL synchronization
- [ ] Chart data transformation functions
- [ ] Real-time update handling (SSE event processing)
- [ ] Date range calculations
- [ ] Export data formatting (CSV, JSON)

### Integration Tests (Frontend)
- [ ] API service calls return expected data shapes
- [ ] Session control actions (pause, resume, cancel) trigger API calls
- [ ] SSE connection lifecycle (connect, receive events, reconnect)
- [ ] Report generation and download

### Unit Tests (Backend)
- [ ] Analytics aggregation logic
- [ ] Mentor workload calculation
- [ ] Learning outcomes computation
- [ ] SSE event serialization
- [ ] Report generation content accuracy
- [ ] Export data filtering and pagination

### Integration Tests (Backend)
- [ ] Dashboard API endpoints return valid responses
- [ ] SSE endpoint streams events in real-time
- [ ] Report PDF generation produces valid documents
- [ ] Export endpoints produce valid CSV/JSON files
- [ ] Authentication and authorization enforcement on all endpoints

### End-to-End Tests
- [ ] Dashboard loads with active session data
- [ ] Click session card opens detail view with timeline
- [ ] Skill progress charts render with real data
- [ ] Pause/resume session from dashboard updates state in real-time
- [ ] Analytics date range filter updates all charts

## Configuration

```yaml
dashboard:
  # Real-time updates
  realtime:
    method: "sse"              # sse or polling
    polling_interval_seconds: 10
    sse_heartbeat_seconds: 30

  # Default views
  defaults:
    date_range_days: 30
    sessions_per_page: 20
    auto_refresh: true

  # Charts
  charts:
    animation_enabled: true
    color_scheme: "tamma"      # tamma, dark, light
    max_data_points: 100       # Limit for performance

  # Reports
  reports:
    templates:
      - id: "individual-progress"
        name: "Individual Progress Report"
        sections: ["summary", "skills", "sessions", "recommendations"]
      - id: "program-overview"
        name: "Program Overview"
        sections: ["kpis", "skill_distribution", "blockers", "outcomes"]
    scheduled:
      weekly:
        enabled: true
        day: "Monday"
        time: "09:00"
        recipients: ["manager@company.com"]
      monthly:
        enabled: true
        day: 1
        time: "09:00"
        recipients: ["manager@company.com", "director@company.com"]

  # Export
  export:
    max_rows: 10000
    formats: ["csv", "json"]

  # Access control
  access:
    require_auth: true
    roles:
      viewer: ["view_sessions", "view_analytics", "view_skills"]
      manager: ["viewer", "pause_session", "cancel_session", "generate_reports"]
      admin: ["manager", "reassign_mentor", "modify_config"]
```

## Success Metrics

- Dashboard page load time < 2 seconds (p95)
- Real-time update latency < 1 second from event to display
- Report generation time < 10 seconds for individual, < 30 seconds for program
- User engagement: > 5 dashboard views per manager per week
- Data accuracy: dashboard metrics match raw data within 1% tolerance
- Zero data visualization errors (charts render correctly)
- Export functionality used by > 50% of managers monthly

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
