---
title: "Story 7-9: Mentorship Session Management"
sidebar:
  order: 70
---

## User Story

As a **mentorship coordinator**, I need to manage mentorship sessions (start, pause, resume, complete, cancel), track session history, assign mentor-mentee pairs, and configure session parameters so that the mentorship program runs smoothly and sessions are properly governed.

## Description

Implement a comprehensive session management layer on top of the existing ELSA workflow engine. The existing `MentorshipSession` entity and `IMentorshipService` interface provide basic CRUD operations. This story adds full lifecycle management including session state governance, pause/resume with state preservation, session configuration, mentor-mentee matching, concurrent session limits, and a REST API for external management. The system coordinates with the ELSA `IElsaWorkflowService` to start, suspend, and cancel workflow instances corresponding to each session.

## Acceptance Criteria

### AC1: Session Lifecycle Management
- [ ] **Start**: Create session, validate inputs, start ELSA workflow instance
- [ ] **Pause**: Suspend ELSA workflow, preserve all state, record pause reason and timestamp
- [ ] **Resume**: Restore ELSA workflow from suspended state, resume from last active state
- [ ] **Complete**: Triggered by `MergeCompleteActivity`, finalize session, generate report
- [ ] **Cancel**: Cancel ELSA workflow, record cancellation reason, clean up resources
- [ ] **Timeout**: Auto-cancel sessions exceeding maximum duration
- [ ] **Fail**: Handle unrecoverable errors, record failure context
- [ ] All state transitions validated (e.g., cannot resume a completed session)

### AC2: Session State Governance
- [ ] Define valid session status transitions:
  - `Active` -> `Paused`, `Completed`, `Failed`, `Cancelled`
  - `Paused` -> `Active`, `Cancelled`
  - `Completed`, `Failed`, `Cancelled` -> (terminal states, no transitions)
- [ ] Enforce transition rules at the service layer
- [ ] Log all status changes as `MentorshipEvent` entries
- [ ] Record who initiated each status change (system vs user)
- [ ] Support reason/notes for each status change

### AC3: Session Configuration
- [ ] Configure per-session parameters:
  - Maximum duration (default: 8 hours)
  - Check-in interval (default: 30 minutes)
  - Escalation policy (default: progressive)
  - Quality gate tier override (optional)
  - Notification preferences (Slack, email, both)
- [ ] Support configuration templates (predefined configs for common scenarios)
- [ ] Allow configuration changes on active sessions (where safe)
- [ ] Validate configuration against business rules

### AC4: Mentor-Mentee Assignment
- [ ] Auto-assign mentor based on story skill area and mentor availability
- [ ] Support manual mentor override
- [ ] Track mentor workload (active sessions per mentor)
- [ ] Enforce maximum concurrent sessions per mentor (configurable, default: 3)
- [ ] Support mentor re-assignment on active sessions
- [ ] Record mentor assignment history

### AC5: Concurrent Session Limits
- [ ] Enforce maximum concurrent active sessions per junior (default: 1)
- [ ] Enforce maximum concurrent sessions across the system (configurable)
- [ ] Queue sessions when limits are reached
- [ ] Auto-start queued sessions when slots become available
- [ ] Notify when session is queued with estimated wait time

### AC6: Session History and Audit
- [ ] Full session history queryable by junior, mentor, story, date range, status
- [ ] Session timeline showing all state transitions and events
- [ ] Session duration breakdown by mentorship state
- [ ] Session comparison (compare two sessions side by side)
- [ ] Export session history as JSON or CSV
- [ ] Retention policy for completed session data

### AC7: REST API for Session Management
- [ ] `POST /api/mentorship/sessions` - Start new session
- [ ] `GET /api/mentorship/sessions` - List sessions with filters and pagination
- [ ] `GET /api/mentorship/sessions/{id}` - Get session details
- [ ] `POST /api/mentorship/sessions/{id}/pause` - Pause session
- [ ] `POST /api/mentorship/sessions/{id}/resume` - Resume session
- [ ] `POST /api/mentorship/sessions/{id}/cancel` - Cancel session
- [ ] `GET /api/mentorship/sessions/{id}/events` - Get session events
- [ ] `GET /api/mentorship/sessions/{id}/timeline` - Get session timeline
- [ ] `PUT /api/mentorship/sessions/{id}/config` - Update session configuration
- [ ] All endpoints require authentication and authorization

## Technical Design

### Enhanced Session Service (C#)

```csharp
namespace Tamma.Core.Interfaces;

public interface IMentorshipSessionService
{
    // Lifecycle
    Task<SessionStartResult> StartSessionAsync(StartSessionRequest request);
    Task<SessionResult> PauseSessionAsync(Guid sessionId, string? reason = null);
    Task<SessionResult> ResumeSessionAsync(Guid sessionId);
    Task<SessionResult> CancelSessionAsync(Guid sessionId, string reason);
    Task<SessionResult> CompleteSessionAsync(Guid sessionId, SessionReport report);
    Task<SessionResult> FailSessionAsync(Guid sessionId, string error);

    // Query
    Task<MentorshipSession?> GetSessionAsync(Guid sessionId);
    Task<SessionDetails> GetSessionDetailsAsync(Guid sessionId);
    Task<PagedResult<SessionSummary>> ListSessionsAsync(SessionFilter filter);
    Task<List<MentorshipEvent>> GetSessionEventsAsync(Guid sessionId);
    Task<SessionTimeline> GetSessionTimelineAsync(Guid sessionId);

    // Configuration
    Task<SessionConfig> GetSessionConfigAsync(Guid sessionId);
    Task UpdateSessionConfigAsync(Guid sessionId, SessionConfigUpdate update);

    // Assignment
    Task<string> AssignMentorAsync(Guid sessionId, string? preferredMentorId = null);
    Task ReassignMentorAsync(Guid sessionId, string newMentorId, string reason);

    // Queue
    Task<QueueStatus> GetQueueStatusAsync();
    Task<int> GetQueuePositionAsync(Guid sessionId);
}

public class StartSessionRequest
{
    public string StoryId { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public string? MentorId { get; set; }
    public SessionConfig? Config { get; set; }
    public string? ConfigTemplateId { get; set; }
}

public class SessionStartResult
{
    public bool Success { get; set; }
    public Guid? SessionId { get; set; }
    public string? WorkflowInstanceId { get; set; }
    public string Status { get; set; } = string.Empty; // "started" or "queued"
    public int? QueuePosition { get; set; }
    public string? Error { get; set; }
}

public class SessionResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}
```

### Session Configuration Model

```csharp
public class SessionConfig
{
    public Guid SessionId { get; set; }
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan CheckInInterval { get; set; } = TimeSpan.FromMinutes(30);
    public string EscalationPolicy { get; set; } = "progressive"; // progressive, immediate, relaxed
    public int? QualityTierOverride { get; set; }
    public NotificationPreferences Notifications { get; set; } = new();
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

public class NotificationPreferences
{
    public bool SlackEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; } = false;
    public List<string> AdditionalRecipients { get; set; } = new();
    public bool NotifyOnStateChange { get; set; } = true;
    public bool NotifyOnBlocker { get; set; } = true;
    public bool NotifyOnEscalation { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
}
```

### Session Timeline Model

```csharp
public class SessionTimeline
{
    public Guid SessionId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public List<TimelineEntry> Entries { get; set; } = new();
    public Dictionary<MentorshipState, TimeSpan> TimePerState { get; set; } = new();
}

public class TimelineEntry
{
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public MentorshipState? FromState { get; set; }
    public MentorshipState? ToState { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Actor { get; set; } // "system", "junior", "mentor", user ID
    public TimeSpan DurationInState { get; set; }
}

public class SessionSummary
{
    public Guid Id { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string JuniorId { get; set; } = string.Empty;
    public string JuniorName { get; set; } = string.Empty;
    public string? MentorId { get; set; }
    public MentorshipState CurrentState { get; set; }
    public SessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public double? OverallScore { get; set; }
}

public class SessionDetails : SessionSummary
{
    public List<MentorshipEvent> Events { get; set; } = new();
    public SessionConfig Config { get; set; } = new();
    public SessionReport? Report { get; set; }
    public SkillUpdateResult? SkillUpdate { get; set; }
}

public class SessionFilter
{
    public string? JuniorId { get; set; }
    public string? MentorId { get; set; }
    public string? StoryId { get; set; }
    public SessionStatus? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string SortBy { get; set; } = "createdAt";
    public string SortDirection { get; set; } = "desc";
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}
```

### TypeScript API Client Types

```typescript
// packages/shared/src/types/session-management.ts

export interface StartSessionRequest {
  storyId: string;
  juniorId: string;
  mentorId?: string;
  config?: SessionConfig;
  configTemplateId?: string;
}

export interface SessionConfig {
  maxDurationHours: number;
  checkInIntervalMinutes: number;
  escalationPolicy: 'progressive' | 'immediate' | 'relaxed';
  qualityTierOverride?: number;
  notifications: NotificationPreferences;
}

export interface NotificationPreferences {
  slackEnabled: boolean;
  emailEnabled: boolean;
  additionalRecipients: string[];
  notifyOnStateChange: boolean;
  notifyOnBlocker: boolean;
  notifyOnEscalation: boolean;
  notifyOnCompletion: boolean;
}

export interface SessionSummary {
  id: string;
  storyId: string;
  storyTitle: string;
  juniorId: string;
  juniorName: string;
  mentorId?: string;
  currentState: string;
  status: 'Active' | 'Paused' | 'Completed' | 'Failed' | 'Cancelled';
  createdAt: string;
  completedAt?: string;
  duration: string;
  overallScore?: number;
}

export interface SessionTimeline {
  sessionId: string;
  startTime: string;
  endTime?: string;
  totalDuration: string;
  entries: TimelineEntry[];
  timePerState: Record<string, string>;
}

export interface TimelineEntry {
  timestamp: string;
  eventType: string;
  fromState?: string;
  toState?: string;
  description: string;
  actor?: string;
  durationInState: string;
}

export interface QueueStatus {
  totalQueued: number;
  activeSessionCount: number;
  maxConcurrentSessions: number;
  estimatedWaitMinutes: number;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}
```

## Dependencies

- Story 7-1: Mentorship State Machine Core (state transition definitions)
- Existing `MentorshipSession` entity in `Tamma.Core.Entities`
- Existing `IMentorshipService` interface in `Tamma.Core.Interfaces`
- Existing `IElsaWorkflowService` for workflow lifecycle management
- Existing `MentorshipController` in `Tamma.Api`
- `IMentorshipSessionRepository` for data persistence
- `IIntegrationService` for Slack/email notifications

## Testing Strategy

### Unit Tests
- [ ] Session status transition validation (all valid and invalid transitions)
- [ ] Session configuration validation rules
- [ ] Concurrent session limit enforcement
- [ ] Queue position calculation
- [ ] Auto-timeout detection logic
- [ ] Mentor assignment algorithm (workload balancing)
- [ ] Session filter and pagination logic
- [ ] Timeline generation from event data

### Integration Tests
- [ ] Full session lifecycle: start -> pause -> resume -> complete
- [ ] Session lifecycle: start -> cancel
- [ ] Session lifecycle: start -> fail
- [ ] ELSA workflow synchronization (start/suspend/cancel)
- [ ] Queue behavior: session queued when limit reached, auto-started when slot opens
- [ ] REST API endpoints with authentication
- [ ] Concurrent session limit enforcement across multiple requests

### Edge Case Tests
- [ ] Start session with nonexistent story or junior
- [ ] Pause an already paused session (idempotent)
- [ ] Resume a completed session (rejected)
- [ ] Cancel a queued session (removed from queue)
- [ ] Two concurrent start requests for same junior
- [ ] Session timeout during pause state
- [ ] ELSA workflow failure during session start

## Configuration

```yaml
session_management:
  # Limits
  max_concurrent_sessions_per_junior: 1
  max_concurrent_sessions_per_mentor: 3
  max_concurrent_sessions_system: 50
  max_queue_size: 100

  # Timeouts
  max_session_duration_hours: 8
  pause_timeout_hours: 24        # Auto-cancel paused sessions after this
  queue_timeout_hours: 4         # Auto-cancel queued sessions after this

  # Defaults
  default_check_in_interval_minutes: 30
  default_escalation_policy: "progressive"

  # Templates
  config_templates:
    - id: "quick-fix"
      name: "Quick Fix"
      max_duration_hours: 2
      check_in_interval_minutes: 15
      escalation_policy: "immediate"
    - id: "standard"
      name: "Standard Session"
      max_duration_hours: 8
      check_in_interval_minutes: 30
      escalation_policy: "progressive"
    - id: "complex-feature"
      name: "Complex Feature"
      max_duration_hours: 16
      check_in_interval_minutes: 60
      escalation_policy: "relaxed"

  # Mentor assignment
  mentor_assignment:
    strategy: "workload_balanced"  # workload_balanced, skill_matched, round_robin
    fallback_to_auto: true

  # Audit
  audit:
    log_all_state_changes: true
    retention_days: 365
    export_format: "json"
```

## Success Metrics

- Session start-to-active time < 5 seconds (p95)
- Pause/resume state preservation accuracy: 100%
- Queue wait time < 15 minutes (p95)
- Zero invalid state transitions in production
- Session timeout enforcement accuracy: 100%
- API response time < 200ms (p95) for all endpoints
- Audit trail completeness: 100% of state changes logged

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
