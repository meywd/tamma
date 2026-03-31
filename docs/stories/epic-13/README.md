# Epic 13: Workflow Decomposition

## Overview

**Goal**: Split the 783-line / 39-activity `SingleIssueCycleWorkflow` into smaller, composable sub-workflows. Target: ~500 lines / ~29 activities in the parent workflow.

**Value Delivered**:
- TDD retry loop extracted into reusable `TddWithDebugRetryWorkflow`
- CI retry loop extracted into reusable `CiWithDebugRetryWorkflow`
- 7 duplicated finish sequences consolidated into 1 shared sequence
- Easier visual debugging in ELSA Studio (smaller graph per workflow)
- Sub-workflows independently testable and versionable

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 13.1 | TDD Debug Retry Sub-Workflow | P1 | None | Planned |
| 13.2 | CI Debug Retry Sub-Workflow | P1 | Story 13.1 | Planned |
| 13.3 | Consolidate Finish Sequences | P2 | Story 13.2 | Planned |

## Sequencing

Stories are strictly sequential. Each produces a separate commit. Phase 1 (delete dead variable) from the plan is folded into Story 13.1 as a prerequisite task.

## Source Plan

`.dev/plans/single-issue-workflow-split.md`

---

**Last Updated**: 2026-03-28
**Epic Owner**: Workflow Team
