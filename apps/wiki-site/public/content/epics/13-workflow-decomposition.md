---
title: "Epic 13: Workflow Decomposition"
sidebar:
  order: 13
---

**Status:** Done
**Stories:** 3 (13-1 through 13-3)

## Overview

Epic 13 decomposes the monolithic ELSA mentorship workflow into reusable sub-workflows for TDD debug retry, CI debug retry, and consolidated finish sequences. This improves maintainability, testability, and reusability of workflow components.

## Goals

1. Extract TDD debug retry logic into a standalone sub-workflow
2. Extract CI debug retry logic into a standalone sub-workflow
3. Consolidate duplicated finish sequences into a shared sub-workflow

## Stories

| Story | Title | Status |
|-------|-------|--------|
| 13-1 | TDD Debug Retry Sub-Workflow | Done |
| 13-2 | CI Debug Retry Sub-Workflow | Done |
| 13-3 | Consolidate Finish Sequences | Done |

## Key Technical Details

- **TDD Debug Retry**: Extracted from the main mentorship workflow; handles test-driven development cycles with retry logic when tests fail
- **CI Debug Retry**: Handles CI pipeline failures with debug analysis and retry; extracted to enable reuse across different workflow contexts
- **Consolidated Finish Sequences**: Duplicated code paths for completing workflows (success, failure, escalation) merged into a single parameterized sub-workflow

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Mentorship Workflow | Epic 7 | Sub-workflows extracted from main workflow |
| Agentic Tool Loop | Epic 12 | Sub-workflows use the tool loop for LLM interactions |

## Related Epics

This epic is part of the ELSA workflow engine group (Epics 11-14). See also:
- [Epic 11: Security Hardening](/epics/11-security/)
- [Epic 12: Agentic Tool Loop](/epics/12-tool-loop/)
- [Epic 14: Custom ELSA Studio](/epics/14-elsa-studio/)
- [Combined page: Epics 11-14](/epics/11-14-elsa/)

## Story Files

[Story documents on GitHub](/stories/epic-13/)
