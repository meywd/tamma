# User Stories Index

This page provides an index of all user stories across all epics. Each story links to its GitHub issue and detailed story documentation.

## Story Structure

Each story includes:
- **Status:** ready-for-dev, in-progress, review, done
- **Acceptance Criteria:** Measurable success conditions
- **Tasks/Subtasks:** Detailed checklist of work items
- **Dev Notes:** Context, architecture patterns, references
- **GitHub Issues:** Linked tasks in GitHub project

---

## Epic 1: Foundation & Core Infrastructure

### Story 1-0: AI Provider Strategy Research
**Status:** ready-for-dev | **Tasks:** 6

Research AI provider options across cost models, capabilities, and workflow fit.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-0-ai-provider-strategy-research.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-0)

---

### Story 1-1: AI Provider Interface Definition
**Status:** ready-for-dev | **Tasks:** 5

Define abstract interface contracts for AI provider operations.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-1-ai-provider-interface-definition.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-1)

---

### Story 1-2: Claude Code Provider Implementation
**Status:** ready-for-dev | **Tasks:** 6

Implement Anthropic Claude API as the first AI provider (reference implementation).

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-2-claude-code-provider-implementation.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-2)

---

### Story 1-3: Provider Configuration Management
**Status:** ready-for-dev | **Tasks:** 7

Centralized configuration for AI provider settings.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-3-provider-configuration-management.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-3)

---

### Story 1-4: Git Platform Interface Definition
**Status:** ready-for-dev | **Tasks:** 6

Define abstract interface contracts for Git platform operations.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-4-git-platform-interface-definition.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-4)

---

### Story 1-5: GitHub Platform Implementation
**Status:** drafted | **Tasks:** 8

Implement GitHub as the first Git platform (reference implementation).

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-5-github-platform-implementation.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-5)

---

### Story 1-6: GitLab Platform Implementation
**Status:** drafted | **Tasks:** 6

Implement GitLab as second Git platform.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-6-gitlab-platform-implementation.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-6)

---

### Story 1-7: Git Platform Configuration Management
**Status:** ready-for-dev | **Tasks:** 5

Centralized configuration for Git platform settings.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-7-git-platform-configuration-management.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-7)

---

### Story 1-8: Hybrid Orchestrator/Worker Architecture Design
**Status:** ready-for-dev | **Tasks:** 7

Document architecture for orchestrator mode and worker mode.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-8-hybrid-orchestrator-worker-architecture-design.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-8)

---

### Story 1-9: Basic CLI Scaffolding with Mode Selection
**Status:** ready-for-dev | **Tasks:** 5

Build basic CLI entry point supporting orchestrator and worker modes.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-9-basic-cli-scaffolding-with-mode-selection.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-9)

---

### Story 1-10: Additional AI Provider Implementations
**Status:** ready-for-dev | **Tasks:** 10

Support for multiple AI providers (OpenAI, GitHub Copilot, Google Gemini, OpenRouter, local LLMs).

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-10-additional-ai-provider-implementations.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-10)

---

### Story 1-11: Additional Git Platform Implementations
**Status:** ready-for-dev | **Tasks:** 7

Support for multiple Git platforms (Gitea, Forgejo, Bitbucket, Azure DevOps, Plain Git).

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-11-additional-git-platform-implementations.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-11)

---

### Story 1-12: Initial Marketing Website
**Status:** ready-for-dev | **Tasks:** 8

Deploy initial marketing website on Cloudflare Workers.

- [📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-12-initial-marketing-website.md)
- [📊 GitHub Issues](https://github.com/meywd/tamma/issues?q=is%3Aissue+label%3Astory-1-12)

---

## Epic 2: Autonomous Development Loop

_Coming soon - Epic 2 stories will be created after Epic 1 completion._

---

## Epic 3: Quality Gates & Intelligence

_Coming soon - Epic 3 stories will be created after Epic 2 completion._

---

## Epic 4: Event Sourcing & Audit Trail

_Coming soon - Epic 4 stories will be created after Epic 3 completion._

---

## Epic 5: Observability & Production Readiness

_Coming soon - Epic 5 stories will be created after Epic 4 completion._

---

## Story Workflow

Stories progress through the following stages:

1. **drafted** - Story created, not yet ready for development
2. **ready-for-dev** - Story refined, ready to be picked up
3. **in-progress** - Developer actively working on story
4. **review** - Code review in progress
5. **done** - All acceptance criteria met, merged to main

---

## Creating New Stories

Stories are created following the BMAD (Bob's Managed Agile Development) methodology:

1. Product Manager defines user story with acceptance criteria
2. Scrum Master (Bob) breaks down into tasks/subtasks
3. Team estimates and prioritizes stories
4. Stories added to sprint backlog
5. Developers implement following TDD workflow

For more details, see [Contributing Guidelines](Contributing).
