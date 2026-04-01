---
title: "Story 7-1: Mentorship State Machine Core"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need a well-defined state machine with validated transitions, guard conditions, and event handling so that mentorship sessions follow a predictable, auditable workflow from story assignment through completion.

## Description

Implement the core 28-state mentorship state machine that orchestrates the entire autonomous mentorship workflow. This state machine is the backbone of the ELSA workflow: it defines every legal state, every valid transition between states, the events that trigger those transitions, guard conditions that must be satisfied, and timeout/error handling for each state. The state machine must be fully deterministic -- given a current state and an event, the next state is unambiguous.

The state machine is already partially defined in `apps/tamma-elsa/src/Tamma.Core/Enums/MentorshipState.cs` with 28 states across 8 groups (Initialization, Assessment, Planning, Implementation, Blockers, Quality, Review, Completion, and Exception states). This story formalizes the complete transition table, adds guard conditions, implements the transition engine, and provides full test coverage.

## Acceptance Criteria

### AC1: State Enumeration
- [ ] All 28 states defined in `MentorshipState` enum are supported
- [ ] States are grouped into logical categories: Initialization, Assessment, Planning, Implementation, Blockers, Quality, Review, Completion, Exception
- [ ] Each state has a human-readable display name and description
- [ ] State metadata includes: group, timeout duration, max retry count, allowed events

### AC2: Transition Table
- [ ] Complete transition table defined covering all valid state + event combinations
- [ ] Minimum 60 valid transitions across the 28 states
- [ ] Invalid transitions are explicitly rejected with descriptive error messages
- [ ] Transition table is defined declaratively (data-driven, not hardcoded switch statements)
- [ ] Transition table is serializable for export to documentation and dashboard

### AC3: Guard Conditions
- [ ] Transitions support optional guard conditions that must evaluate to true
- [ ] Guard conditions receive the current session context (session data, junior profile, story metadata)
- [ ] Built-in guards include:
  - `MaxRetriesNotExceeded`: blocks transition if retry count >= configured max
  - `SkillLevelSufficient`: blocks advanced paths for low-skill developers
  - `TimeoutNotExpired`: blocks progression if timeout window has not elapsed
  - `AssessmentScoreAboveThreshold`: requires minimum confidence score
  - `QualityGatesPass`: requires all quality checks to pass
- [ ] Custom guards can be registered at runtime

### AC4: Event Handling
- [ ] Events are strongly typed with associated payload schemas
- [ ] Core events include: `StoryLoaded`, `AssessmentCompleted`, `PlanApproved`, `TaskCompleted`, `ProgressUpdate`, `BlockerDetected`, `QualityCheckResult`, `ReviewSubmitted`, `ReviewApproved`, `MergeCompleted`, `Timeout`, `Error`, `UserPause`, `UserCancel`
- [ ] Events are validated against allowed events for the current state
- [ ] Events that arrive for invalid states are logged and discarded (not crash)
- [ ] Event history is persisted for audit trail

### AC5: Timeout Handling
- [ ] Each state has a configurable timeout duration
- [ ] Default timeouts: simple tasks 15 min, complex tasks 30 min, research tasks 45 min
- [ ] When a timeout fires, the state machine transitions to the appropriate fallback state (typically DIAGNOSE_BLOCKER or PROVIDE_HINT)
- [ ] Timeout escalation chain: Hint (15 min) -> Guidance (30 min) -> Direct Assistance (45 min) -> Escalate to Human (60 min) -> Session Timeout (120 min)
- [ ] Timeouts are cancelled when the state transitions normally

### AC6: Session Lifecycle
- [ ] Session creation initializes state to INIT_STORY_PROCESSING with full context
- [ ] Session pause transitions to PAUSED from any active state, preserving context
- [ ] Session resume transitions back to the state that was active before pause
- [ ] Session cancel transitions to CANCELLED with cleanup
- [ ] Session failure transitions to FAILED with error details
- [ ] Completed sessions transition to COMPLETED and are immutable

### AC7: State Persistence
- [ ] Current state is persisted to the `mentorship_sessions` table on every transition
- [ ] Previous state is stored for rollback scenarios
- [ ] State transition events are logged to `mentorship_events` table
- [ ] ELSA workflow instance state is synchronized with session state
- [ ] State recovery works after server restart (bookmark-based resumption)

### AC8: Transition Logging & Observability
- [ ] Every transition emits a structured log entry with: sessionId, fromState, toState, event, timestamp, duration
- [ ] Metrics emitted: `mentorship.transitions.total`, `mentorship.state.duration`, `mentorship.timeouts.total`
- [ ] Failed transitions (guard failures, invalid events) are counted separately
- [ ] Dashboard-consumable state history is available via REST API

## Technical Design

### State Transition Definition

```typescript
// TypeScript mirror types for the TS engine bridge (Story 7-10)
export enum MentorshipState {
  // Initialization
  INIT_STORY_PROCESSING = 'INIT_STORY_PROCESSING',
  VALIDATE_STORY = 'VALIDATE_STORY',

  // Assessment
  ASSESS_JUNIOR_CAPABILITY = 'ASSESS_JUNIOR_CAPABILITY',
  CLARIFY_REQUIREMENTS = 'CLARIFY_REQUIREMENTS',
  RE_EXPLAIN_STORY = 'RE_EXPLAIN_STORY',

  // Planning
  PLAN_DECOMPOSITION = 'PLAN_DECOMPOSITION',
  REVIEW_PLAN = 'REVIEW_PLAN',
  ADJUST_PLAN = 'ADJUST_PLAN',

  // Implementation
  START_IMPLEMENTATION = 'START_IMPLEMENTATION',
  MONITOR_PROGRESS = 'MONITOR_PROGRESS',
  PROVIDE_GUIDANCE = 'PROVIDE_GUIDANCE',
  DETECT_PATTERN = 'DETECT_PATTERN',

  // Blockers
  DIAGNOSE_BLOCKER = 'DIAGNOSE_BLOCKER',
  PROVIDE_HINT = 'PROVIDE_HINT',
  PROVIDE_ASSISTANCE = 'PROVIDE_ASSISTANCE',
  ESCALATE_TO_SENIOR = 'ESCALATE_TO_SENIOR',

  // Quality
  QUALITY_GATE_CHECK = 'QUALITY_GATE_CHECK',
  AUTO_FIX_ISSUES = 'AUTO_FIX_ISSUES',
  MANUAL_FIX_REQUIRED = 'MANUAL_FIX_REQUIRED',

  // Review
  PREPARE_CODE_REVIEW = 'PREPARE_CODE_REVIEW',
  MONITOR_REVIEW = 'MONITOR_REVIEW',
  GUIDE_FIXES = 'GUIDE_FIXES',
  RE_REQUEST_REVIEW = 'RE_REQUEST_REVIEW',

  // Completion
  MERGE_AND_COMPLETE = 'MERGE_AND_COMPLETE',
  GENERATE_REPORT = 'GENERATE_REPORT',
  UPDATE_SKILL_PROFILE = 'UPDATE_SKILL_PROFILE',
  COMPLETED = 'COMPLETED',

  // Exception
  PAUSED = 'PAUSED',
  CANCELLED = 'CANCELLED',
  FAILED = 'FAILED',
  TIMEOUT = 'TIMEOUT',
}

export enum MentorshipEvent {
  STORY_LOADED = 'STORY_LOADED',
  STORY_VALIDATED = 'STORY_VALIDATED',
  ASSESSMENT_COMPLETED = 'ASSESSMENT_COMPLETED',
  UNDERSTANDING_CONFIRMED = 'UNDERSTANDING_CONFIRMED',
  UNDERSTANDING_PARTIAL = 'UNDERSTANDING_PARTIAL',
  UNDERSTANDING_INCORRECT = 'UNDERSTANDING_INCORRECT',
  PLAN_CREATED = 'PLAN_CREATED',
  PLAN_APPROVED = 'PLAN_APPROVED',
  PLAN_NEEDS_ADJUSTMENT = 'PLAN_NEEDS_ADJUSTMENT',
  TASK_COMPLETED = 'TASK_COMPLETED',
  PROGRESS_STEADY = 'PROGRESS_STEADY',
  PROGRESS_STALLED = 'PROGRESS_STALLED',
  CIRCULAR_DETECTED = 'CIRCULAR_DETECTED',
  PATTERN_RESOLVED = 'PATTERN_RESOLVED',
  BLOCKER_DETECTED = 'BLOCKER_DETECTED',
  BLOCKER_RESOLVED = 'BLOCKER_RESOLVED',
  HINT_PROVIDED = 'HINT_PROVIDED',
  ESCALATION_REQUIRED = 'ESCALATION_REQUIRED',
  QUALITY_ALL_PASS = 'QUALITY_ALL_PASS',
  QUALITY_MINOR_ISSUES = 'QUALITY_MINOR_ISSUES',
  QUALITY_MAJOR_ISSUES = 'QUALITY_MAJOR_ISSUES',
  QUALITY_CRITICAL = 'QUALITY_CRITICAL',
  AUTO_FIX_APPLIED = 'AUTO_FIX_APPLIED',
  PR_SUBMITTED = 'PR_SUBMITTED',
  REVIEW_APPROVED = 'REVIEW_APPROVED',
  REVIEW_CHANGES_REQUESTED = 'REVIEW_CHANGES_REQUESTED',
  FIXES_COMPLETED = 'FIXES_COMPLETED',
  MERGE_COMPLETED = 'MERGE_COMPLETED',
  REPORT_GENERATED = 'REPORT_GENERATED',
  PROFILE_UPDATED = 'PROFILE_UPDATED',
  TIMEOUT_FIRED = 'TIMEOUT_FIRED',
  ERROR_OCCURRED = 'ERROR_OCCURRED',
  USER_PAUSE = 'USER_PAUSE',
  USER_RESUME = 'USER_RESUME',
  USER_CANCEL = 'USER_CANCEL',
}

export interface StateTransitionDef {
  from: MentorshipState;
  event: MentorshipEvent;
  to: MentorshipState;
  guards?: string[];
  action?: string;
  description?: string;
}

export interface GuardContext {
  session: MentorshipSession;
  event: MentorshipEvent;
  payload: Record<string, unknown>;
}

export type GuardFn = (ctx: GuardContext) => boolean | Promise<boolean>;

export interface MentorshipSession {
  id: string;
  storyId: string;
  juniorId: string;
  currentState: MentorshipState;
  previousState?: MentorshipState;
  pausedFromState?: MentorshipState;
  retryCount: number;
  maxRetries: number;
  context: Record<string, unknown>;
  createdAt: Date;
  updatedAt: Date;
  completedAt?: Date;
  workflowInstanceId?: string;
}

export interface TransitionResult {
  success: boolean;
  fromState: MentorshipState;
  toState: MentorshipState;
  event: MentorshipEvent;
  guardsFailed?: string[];
  error?: string;
  timestamp: Date;
}
```

### State Machine Engine

```typescript
export interface IMentorshipStateMachine {
  // Transition
  transition(
    session: MentorshipSession,
    event: MentorshipEvent,
    payload?: Record<string, unknown>
  ): Promise<TransitionResult>;

  // Query
  getValidEvents(state: MentorshipState): MentorshipEvent[];
  getTransition(state: MentorshipState, event: MentorshipEvent): StateTransitionDef | undefined;
  isTerminalState(state: MentorshipState): boolean;

  // Guards
  registerGuard(name: string, fn: GuardFn): void;

  // Lifecycle
  createSession(storyId: string, juniorId: string): Promise<MentorshipSession>;
  pauseSession(sessionId: string): Promise<TransitionResult>;
  resumeSession(sessionId: string): Promise<TransitionResult>;
  cancelSession(sessionId: string): Promise<TransitionResult>;

  // History
  getTransitionHistory(sessionId: string): Promise<TransitionResult[]>;
}
```

## Dependencies

- `Tamma.Core.Enums.MentorshipState` (already defined, 28 states)
- `Tamma.Core.Entities.MentorshipSession` (already defined)
- `Tamma.Core.Entities.MentorshipEvent` (already defined)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (already defined)
- ELSA Workflows 3 bookmark system for state persistence
- Story 7-10: TypeScript bridge consumes the state machine definition

## Testing Strategy

### Unit Tests
- Verify all 28 states are present in the enum
- Verify every valid transition in the transition table produces the correct target state
- Verify invalid transitions are rejected with appropriate error
- Verify guard conditions block transitions when unsatisfied
- Verify guard conditions allow transitions when satisfied
- Verify timeout escalation chain fires in correct order
- Verify pause/resume preserves and restores state correctly
- Verify cancel from every active state transitions to CANCELLED

### Integration Tests
- Full happy-path session: INIT -> ASSESS -> PLAN -> IMPLEMENT -> MONITOR -> QUALITY -> REVIEW -> MERGE -> COMPLETE
- Blocker resolution loop: MONITOR -> DIAGNOSE -> FIX -> VERIFY -> RESUME
- Circular pattern detection: MONITOR -> DETECT_PATTERN -> STRATEGIC_REDIRECT -> MONITOR
- Quality gate retry: QUALITY_GATE_CHECK -> AUTO_FIX -> QUALITY_GATE_CHECK -> PASS
- Review iteration: MONITOR_REVIEW -> GUIDE_FIXES -> RE_REQUEST_REVIEW -> MONITOR_REVIEW -> APPROVED
- Server restart recovery: session resumes from persisted state after restart

### Performance Tests
- Transition throughput: >1000 transitions/second
- State lookup: <1ms per query
- Transition table loading: <100ms on startup

## Configuration

```yaml
mentorship:
  state_machine:
    max_retries: 3
    timeouts:
      simple_task_minutes: 15
      complex_task_minutes: 30
      research_task_minutes: 45
      escalation_minutes: 60
      session_timeout_minutes: 120
    guards:
      min_assessment_confidence: 0.6
      min_quality_score: 80
      max_review_iterations: 5
    persistence:
      sync_interval_ms: 0  # 0 = synchronous
      log_transitions: true
      log_guard_failures: true
```

## Success Metrics

- 100% of the 28 states reachable in tests
- 100% of valid transitions produce correct target state
- 0 invalid transitions accepted
- Guard condition evaluation <5ms per guard
- State persistence survives server restart
- Full session lifecycle (INIT to COMPLETED) completes in <1 second in test (excluding activity execution)

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
