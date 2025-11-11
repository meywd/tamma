# Story 4.8: Black-Box Replay for Debugging

Status: ready-for-dev

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:

- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

As a **developer**,
I want to replay system state at any point in time to understand past behavior,
so that I can diagnose why autonomous loop made specific decisions.

## Acceptance Criteria

1. CLI command: `tamma replay --correlation-id {id} --timestamp {timestamp}`
2. Command reconstructs system state by replaying events up to specified timestamp
3. Command displays: issue context, AI provider decisions, code changes, approval points, errors
4. Command supports step-by-step replay (pause at each event) via `--interactive` flag
5. Command exports replay to HTML report for sharing with team
6. Replay includes diff view showing state changes between events
7. Replay performance: complete reconstruction in <5 seconds for typical development cycle (50-100 events)

## Tasks / Subtasks

- [ ] Task 1: Create CLI replay command interface (AC: 1)
  - [ ] Subtask 1.1: Implement command line argument parsing for correlation-id and timestamp
  - [ ] Subtask 1.2: Add interactive flag support for step-by-step replay
  - [ ] Subtask 1.3: Create help documentation and usage examples

- [ ] Task 2: Implement event replay engine (AC: 2)
  - [ ] Subtask 2.1: Create event replay orchestrator that processes events chronologically
  - [ ] Subtask 2.2: Implement state reconstruction from event payloads
  - [ ] Subtask 2.3: Add filtering by correlation-id and timestamp

- [ ] Task 3: Build replay visualization system (AC: 3, 4, 6)
  - [ ] Subtask 3.1: Create state display components for different event types
  - [ ] Subtask 3.2: Implement interactive pause and resume functionality
  - [ ] Subtask 3.3: Add diff visualization between state snapshots

- [ ] Task 4: Add HTML report export (AC: 5)
  - [ ] Subtask 4.1: Create HTML template for replay reports
  - [ ] Subtask 4.2: Implement report generation with embedded state snapshots
  - [ ] Subtask 4.3: Add sharing and export functionality

- [ ] Task 5: Optimize replay performance (AC: 7)
  - [ ] Subtask 5.1: Implement efficient event processing pipeline
  - [ ] Subtask 5.2: Add state caching for large replays
  - [ ] Subtask 5.3: Optimize for typical development cycle sizes

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**

1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)

### Requirements Context Summary

**Epic 4 Event Sourcing:** This story provides the debugging and analysis capabilities that make event sourcing valuable for understanding system behavior and diagnosing issues.

**Time-Travel Debugging:** The ability to reconstruct system state at any point in time is a key benefit of event sourcing, enabling powerful debugging capabilities.

**Black-Box Analysis:** Replay works without access to internal system state, using only the event stream - this is essential for auditing and compliance.

**Performance Requirements:** Replay must be fast enough to be practical for debugging typical development cycles (50-100 events).

### Project Structure Notes

**Package Location:** `packages/events/src/replay/` for replay functionality.

**CLI Integration:** Uses the CLI framework from Epic 1 for command interface.

**Integration Points:** Integrates with event store (Story 4.2) and query API (Story 4.7) for event access.

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions

- [Source: docs/epics.md#Epic-4-Event-Sourcing-Audit-Trail](docs/epics.md#Epic-4-Event-Sourcing-Audit-Trail)
- [Source: docs/tech-spec-epic-4.md#Event-Replay](docs/tech-spec-epic-4.md#Event-Replay)

## Change Log

| Date       | Version | Changes                | Author                  |
| ---------- | ------- | ---------------------- | ----------------------- |
| 2025-11-09 | 1.0.0   | Initial story creation | Mary (Business Analyst) |

## Dev Agent Record

### Context Reference

- docs/stories/epic-4/story-4-8/4-8-black-box-replay-debugging.context.xml

### Agent Model Used

Claude-3.5-Sonnet

### Debug Log References

### Completion Notes List

### File List
