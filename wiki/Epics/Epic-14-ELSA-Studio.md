# Epic 14: Custom ELSA Studio

**Status:** Done
**Stories:** 3 (14-1 through 14-3)

## Overview

Epic 14 builds a custom ELSA Studio interface using Blazor WebAssembly for visual workflow design and monitoring. The studio provides a web-based UI for inspecting, debugging, and managing ELSA workflows with custom UI hints specific to Tamma's activity types.

## Goals

1. Scaffold the Blazor WASM application for ELSA Studio
2. Set up Docker packaging and CI/CD for the studio
3. Add custom UI hints for Tamma-specific activities

## Stories

| Story | Title | Status |
|-------|-------|--------|
| 14-1 | Studio Blazor WASM Scaffold | Done |
| 14-2 | Studio Docker & CI | Done |
| 14-3 | Studio Custom UI Hints | Done |

## Key Technical Details

- **Technology**: Blazor WebAssembly (C#/.NET)
- **Deployment**: Docker container, accessible at `elsa.tamma.dev`
- **Authentication**: Auto-login via ELSA Identity (Epic 16 adds unified auth)
- **Custom UI Hints**: Activity-specific visual representations for Tamma's workflow steps (LLM calls, tool execution, code review, etc.)

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| ELSA Workflows | Epic 7 | Studio visualizes and manages ELSA workflows |
| Unified Auth | Epic 16 | ELSA Studio auto-login via GitHub OAuth |

## Related Epics

This epic is part of the ELSA workflow engine group (Epics 11-14). See also:
- [Epic 11: Security Hardening](Epics/Epic-11-Security)
- [Epic 12: Agentic Tool Loop](Epics/Epic-12-Tool-Loop)
- [Epic 13: Workflow Decomposition](Epics/Epic-13-Workflow-Decomposition)
- [Combined page: Epics 11-14](Epics/Epic-11-14-ELSA)

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-14)
