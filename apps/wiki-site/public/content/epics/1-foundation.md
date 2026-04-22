---
title: "Epic 1: Foundation & Core Infrastructure"
sidebar:
  order: 1
---

**Status:** Near Complete (10/15 done; 1-10 in progress with OpenCode/OpenRouter/Zen MCP done; remaining providers planned)
**Stories:** 15 (1-0 through 1-14)
**Milestone:** [Epic 1 Milestone](https://github.com/meywd/tamma/milestone/1)

## Overview

Epic 1 establishes the foundational abstractions that enable Tamma's multi-provider, multi-platform architecture. By decoupling AI providers and Git platforms through interface-based design, Tamma can support multiple providers without vendor lock-in.

## Goals

1. Define abstract interfaces for AI providers and Git platforms
2. Implement reference implementations (Claude Code for AI, GitHub for Git)
3. Add support for multiple AI providers (OpenCode, OpenRouter, Zen MCP)
4. Create hybrid orchestrator/worker architecture
5. Build basic CLI with mode selection
6. Deploy initial marketing website

## Implementation Summary

### Packages Created

| Package | Purpose | Source Files | Test Files |
|---------|---------|-------------|-----------|
| `@tamma/providers` | AI provider abstraction layer | 21 | 20 |
| `@tamma/platforms` | Git platform abstraction layer | 14 | 7 |
| `@tamma/cli` | Command-line interface | 17 | 13 |
| `@tamma/orchestrator` | Orchestrator mode service | 7 | 4 |
| `@tamma/shared` | Shared utilities and types | 43 | 21 |
| `@tamma/observability` | Logging (Pino) | 3 | 1 |

### Key Interfaces

- `IAIProvider` -- Standard LLM operations (synchronous and streaming messages)
- `IAgentProvider` -- Task-based agent operations (tool-calling CLI agents)
- `ICLIAgentProvider` -- Providers managing their own subprocess execution
- `IGitPlatform` -- Git platform operations (PRs, issues, branches, CI)

### AI Providers Implemented

| Provider | Class | Type | Package |
|----------|-------|------|---------|
| Claude Code | `ClaudeAgentProvider` | CLI agent | `@tamma/providers` |
| OpenCode | `OpenCodeProvider` | CLI agent | `@tamma/providers` |
| OpenRouter | `OpenRouterProvider` | LLM API | `@tamma/providers` |
| Zen MCP | `ZenMCPProvider` | LLM API | `@tamma/providers` |

### Git Platforms Implemented

| Platform | Class | Status | Package |
|----------|-------|--------|---------|
| GitHub | `GitHubPlatform` | Implemented (reference) | `@tamma/platforms` |
| GitLab | -- | Story ready, not yet implemented | -- |
| Gitea/Forgejo/Bitbucket/Azure DevOps | -- | Stories ready | -- |

## Stories

### Story 1-0: AI Provider Strategy Research
**Status:** Done | **Tasks:** 6

Research AI provider options across cost models, capabilities, and workflow fit.

- [Story Document](/stories/epic-1//story-1-0)

---

### Story 1-1: AI Provider Interface Definition
**Status:** Done | **Tasks:** 5

Define abstract interface contracts for AI provider operations.

- [Story Document](/stories/epic-1//story-1-1)

---

### Story 1-2: Claude Code Provider Implementation
**Status:** Done | **Tasks:** 6

Implement Anthropic Claude as the first AI provider (reference implementation).

- [Story Document](/stories/epic-1//story-1-2)

---

### Story 1-3: Provider Configuration Management
**Status:** Done | **Tasks:** 7

Centralized configuration for AI provider settings.

- [Story Document](/stories/epic-1//story-1-3)

---

### Story 1-4: Git Platform Interface Definition
**Status:** Done | **Tasks:** 6

Define abstract interface contracts for Git platform operations.

- [Story Document](/stories/epic-1//story-1-4)

---

### Story 1-5: GitHub Platform Implementation
**Status:** Done | **Tasks:** 8

Implement GitHub as the first Git platform (reference implementation).

- [Story Document](/stories/epic-1//story-1-5)

---

### Story 1-6: GitLab Platform Implementation
**Status:** Story ready | **Tasks:** 6

Implement GitLab as second Git platform. Story documentation complete, code not yet implemented.

- [Story Document](/stories/epic-1//story-1-6)

---

### Story 1-7: Git Platform Configuration Management
**Status:** Done | **Tasks:** 5

Centralized configuration for Git platform settings.

- [Story Document](/stories/epic-1//story-1-7)

---

### Story 1-8: Hybrid Orchestrator/Worker Architecture Design
**Status:** Done | **Tasks:** 7

Document architecture for orchestrator mode and worker mode.

- [Story Document](/stories/epic-1//story-1-8)

---

### Story 1-9: Basic CLI Scaffolding with Mode Selection
**Status:** Done | **Tasks:** 5

Build basic CLI entry point supporting multiple modes.

CLI modes implemented:
- `tamma start` -- Self-hosted engine (CLI mode)
- `tamma server` -- Self-hosted HTTP server
- `tamma api` -- SaaS/GitHub App mode

- [Story Document](/stories/epic-1//story-1-9)

---

### Story 1-10: Additional AI Provider Implementations
**Status:** In Progress | **Tasks:** 10

OpenCode, OpenRouter, and Zen MCP providers implemented and tested (108 tests passing across all three). OpenAI, Copilot, Gemini, z.ai, and local LLMs still planned. See `packages/providers/src/` for source.

Key lessons (from project memory):
- `vi.mock()` factory must be self-contained (hoisted) — put mock classes inside factory, use async helper to retrieve them
- Two provider hierarchies: `IAIProvider` (LLM APIs) and `IAgentProvider` / `ICLIAgentProvider` (CLI agents). Tests cover both surfaces independently.

- [Story Document](/stories/epic-1//story-1-10)

---

### Story 1-11: Additional Git Platform Implementations
**Status:** Story ready | **Tasks:** 7

Stories for Gitea, Forgejo, Bitbucket, Azure DevOps, and Plain Git are documented. Only GitHub is implemented in code.

- [Story Document](/stories/epic-1//story-1-11)

---

### Story 1-12: Initial Marketing Website
**Status:** Done | **Tasks:** 8

Marketing website deployed at tamma.dev on Cloudflare Workers.

Location: `apps/marketing-site/`

- [Story Document](/stories/epic-1//story-1-12)

---

### Story 1-13: Agent Customization System
**Status:** In Progress | **Tasks:** 8

AgentPromptRegistry, RoleBasedAgentResolver, and agent configs exist. A/B testing, benchmarks, and learning not yet done.

- [Story Document](/stories/epic-1//story-1-13)

---

### Story 1-14: Performance Impact Analysis
**Status:** Ready for Dev | **Tasks:** 8

Performance impact analysis for agent customizations. Context XML exists, no implementation yet.

- [Story Document](/stories/epic-1//story-1-14)

---

## Technical Notes

### TypeScript Strict Mode Gotchas

Lessons learned from implementation:
- `exactOptionalPropertyTypes: true` -- cannot assign `undefined` to optional props; use conditional assignment: `if (val !== undefined) obj.prop = val;`
- `noUncheckedIndexedAccess: true` -- indexed access returns `T | undefined`
- Cast through `unknown` first when type narrowing fails: `(m as unknown as Record<string, unknown>)['context_length']`

### Testing Pattern

- `vi.mock()` factory must be self-contained (hoisted) -- put mock classes inside factory, use async helper to retrieve them
- All tests use Vitest 3.x with colocated `*.test.ts` files

---

## Dependencies

**Prerequisite Epics:** None (foundational epic)

**Dependent Epics:**
- Epic 1.5 (Deployment) depends on Epic 1
- Epic 2 (Autonomous Loop) depends on Epic 1
- Epic 9 (Multi-Agent) depends on Epic 1

---

_For detailed technical specifications, see [Tech Spec Epic 1](/stories/epic-1//tech-spec-epic-1.md)._
