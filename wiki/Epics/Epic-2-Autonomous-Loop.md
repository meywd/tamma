# Epic 2: Autonomous Development Loop

**Status:** Near Complete (13/16 done)
**Stories:** 16 (2-1 through 2-16)
**Task Plans:** 3
**Tech Spec:** [tech-spec-epic-2.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-2/tech-spec-epic-2.md)
**Retrospective:** Completed

## Overview

Epic 2 implements the fundamental 14-step autonomous development loop with basic code generation, Git operations, and user approval checkpoints. This is the core of Tamma's autonomous capability -- the loop that takes an issue from selection to merged PR without manual intervention.

## Goals

1. Implement issue selection with configurable filtering
2. Analyze issue context and generate development plans
3. Follow TDD workflow: write failing tests, implement code, refactor
4. Create pull requests with CI/CD monitoring
5. Merge PRs with completion checkpoints
6. Auto-select next issue for continuous loop
7. Add intelligent provider selection and prompt optimization
8. Support issue decomposition for complex tasks

## Story Breakdown

### Core Autonomous Loop (2-1 through 2-11)

| Story | Title | Task Plans | Status |
|-------|-------|------------|--------|
| 2-1 | Issue Selection with Filtering | 1 | Done |
| 2-2 | Issue Context Analysis | 0 | Done |
| 2-3 | Development Plan Generation with Approval Checkpoint | 1 | Done |
| 2-4 | Git Branch Creation | 0 | Done |
| 2-5 | Test-First Development -- Write Failing Tests | 0 | Done |
| 2-6 | Implementation Code Generation | 0 | Done |
| 2-7 | Code Refactoring Pass | 0 | Done |
| 2-8 | Pull Request Creation | 0 | Done |
| 2-9 | PR Status Monitoring | 1 | Done |
| 2-10 | PR Merge with Completion Checkpoint | 0 | Done |
| 2-11 | Auto-Next Issue Selection | 0 | Done |

### Advanced Intelligence (2-12 through 2-16)

| Story | Title | Task Plans | Status |
|-------|-------|------------|--------|
| 2-12 | Intelligent Provider Selection | 0 | Done |
| 2-13 | Prompt Engineering Optimization | 0 | Done |
| 2-14 | Issue Decomposition Engine | 0 | Ready for Dev |
| 2-15 | Task Dependency Mapping | 0 | Ready for Dev |
| 2-16 | Incremental Task Sequencing | 0 | Ready for Dev |

## Key Technical Details

### Autonomous Loop Flow

```
Issue Selection (2-1)
    |
    v
Context Analysis (2-2)
    |
    v
Plan Generation + Approval (2-3)
    |
    v
Branch Creation (2-4)
    |
    v
Write Failing Tests (2-5)
    |
    v
Implementation (2-6)
    |
    v
Refactoring Pass (2-7)
    |
    v
PR Creation (2-8)
    |
    v
PR Monitoring (2-9)
    |
    v
Merge + Completion (2-10)
    |
    v
Auto-Next Issue (2-11) --> back to (2-1)
```

### Approval Checkpoints

- **Plan Approval** (2-3): User reviews development plan before code generation starts
- **Merge Approval** (2-10): User confirms merge after CI passes and reviews approve

### Provider Selection Intelligence

Story 2-12 introduces automatic provider selection based on task type, cost, and availability. The system maintains performance metrics per provider/task-type combination and routes tasks to the optimal provider.

### Issue Decomposition

Stories 2-14 through 2-16 add the ability to break large issues into smaller implementable tasks, map dependencies between them, and sequence execution for incremental delivery.

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Provider Interface | Epic 1 | `IAIProvider` for code generation and analysis |
| Git Platform Interface | Epic 1 | `IGitPlatform` for PR/branch/issue operations |
| CLI Scaffolding | Epic 1 | CLI provides user interaction for approvals |
| Core Engine Separation | Epic 1.5 | `TammaEngine` orchestrates the loop |
| Quality Gates | Epic 3 | Build/test retry logic gates code before merge |
| Event Sourcing | Epic 4 | All loop actions emit audit events |

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-2)
