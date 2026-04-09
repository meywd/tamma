# Story 26-2: Scrum Management Workflow

**Epic**: Epic 26 - Project Management & Triage
**Priority**: Medium
**Status**: Drafted

## Summary

An ELSA workflow that manages sprint cycles — planning sprints, tracking velocity, running standups, and conducting retrospectives. Operates as an autonomous scrum master.

## Trigger

- Scheduled (daily standup, sprint boundaries)
- Manual dispatch
- ADL callback (cycle completed)

## Workflows

### Sprint Planning (runs at sprint start)
```
Fetch Backlog (triaged, prioritized issues)
  → Calculate Team Velocity (from past sprints)
  → LLM Sprint Planning
       → Select issues that fit velocity
       → Balance: bugs vs features vs chores
       → Consider dependencies between issues
       → Assign to sprint milestone
  → Post Sprint Plan (GitHub project board / issue comment)
  → Create Sprint Tracking Issue
```

### Daily Standup (runs daily)
```
Fetch Sprint Issues
  → Check Progress (merged PRs, open PRs, blocked issues)
  → LLM Standup Summary
       → What was completed
       → What's in progress
       → What's blocked
       → Risks and concerns
  → Post Standup Summary (GitHub discussion or issue)
  → Flag Blocked Issues (notify, escalate)
```

### Sprint Review (runs at sprint end)
```
Fetch Sprint Results
  → Calculate Metrics
       → Velocity (story points completed)
       → Completion rate (issues done / planned)
       → Cycle time (average time from start to merge)
       → Autonomous rate (Tamma-completed / total)
  → LLM Sprint Review
       → What went well
       → What didn't
       → Recommendations for next sprint
  → Post Sprint Review
  → Archive Sprint
```

### Retrospective (runs after sprint review)
```
Fetch Sprint Events (from event store)
  → Analyze Failures
       → Which issues failed? Why?
       → Which workflows got stuck?
       → What was escalated?
  → LLM Retrospective
       → Patterns in failures
       → Process improvements
       → Workflow optimizations needed
  → Post Retrospective
  → Create Improvement Issues (actionable items)
```

## Velocity Tracking

```json
{
  "sprint": "2026-W14",
  "planned": 15,
  "completed": 12,
  "velocity": 12,
  "avgCycleTime": "4.2 hours",
  "autonomousRate": 0.83,
  "blockedCount": 2,
  "escalatedCount": 1
}
```

## Acceptance Criteria

- [ ] Sprint planning workflow selects issues based on velocity
- [ ] Daily standup summarizes progress and flags blockers
- [ ] Sprint review calculates metrics and posts summary
- [ ] Retrospective analyzes failures and creates improvement issues
- [ ] Velocity tracked across sprints
- [ ] Integration with GitHub Projects or Milestones
- [ ] Events: `SPRINT.PLANNED/STANDUP/REVIEW/RETRO`

## Dependencies

- Story 26-1: Issue Triage (triaged backlog)
- Story 26-4: Priority Configuration
- Story 4-7: Event Query API (event store for retrospectives)
