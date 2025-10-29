# Epic 1: Foundation & Core Infrastructure

**Duration:** Weeks 0-2
**Status:** 🚧 In Progress
**Issues:** [View on GitHub](https://github.com/meywd/tamma/issues?q=is%3Aissue+milestone%3A%22Epic+1%3A+Foundation+%26+Core+Infrastructure%22)
**Milestone:** [Epic 1 Milestone](https://github.com/meywd/tamma/milestone/1)

## Overview

Epic 1 establishes the foundational abstractions that enable Tamma's multi-provider, multi-platform architecture. By decoupling AI providers and Git platforms through interface-based design, Tamma can support multiple providers without vendor lock-in.

## Goals

1. ✅ Define abstract interfaces for AI providers and Git platforms
2. ✅ Implement reference implementations (Anthropic Claude, GitHub)
3. ✅ Add support for multiple AI providers and Git platforms
4. ✅ Create hybrid orchestrator/worker architecture
5. ✅ Build basic CLI with mode selection
6. ✅ Deploy initial marketing website

## Stories & Tasks (13 stories, 86 tasks)

### Story 1-0: AI Provider Strategy Research
**Status:** ready-for-dev | **Tasks:** 6

Research AI provider options to inform implementation strategy.

- [#2](https://github.com/meywd/tamma/issues/2) - Task 1: Research AI provider cost models
- [#3](https://github.com/meywd/tamma/issues/3) - Task 2: Evaluate provider capabilities per workflow step
- [#4](https://github.com/meywd/tamma/issues/4) - Task 3: Assess integration approaches
- [#5](https://github.com/meywd/tamma/issues/5) - Task 4: Validate deployment compatibility
- [#6](https://github.com/meywd/tamma/issues/6) - Task 5: Create recommendation matrix and strategy
- [#7](https://github.com/meywd/tamma/issues/7) - Task 6: Document findings and recommendations

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-0-ai-provider-strategy-research.md)

---

### Story 1-1: AI Provider Interface Definition
**Status:** ready-for-dev | **Tasks:** 5

Define abstract interface contracts for AI provider operations.

- [#8](https://github.com/meywd/tamma/issues/8) - Task 1: Define core AI provider interface structure
- [#9](https://github.com/meywd/tamma/issues/9) - Task 2: Implement provider capabilities discovery
- [#10](https://github.com/meywd/tamma/issues/10) - Task 3: Define error handling contracts
- [#11](https://github.com/meywd/tamma/issues/11) - Task 4: Create integration documentation
- [#12](https://github.com/meywd/tamma/issues/12) - Task 5: Implement synchronous and asynchronous patterns

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-1-ai-provider-interface-definition.md)

---

### Story 1-2: Claude Code Provider Implementation
**Status:** ready-for-dev | **Tasks:** 6

Implement Anthropic Claude API as the first AI provider (reference implementation).

- [#13](https://github.com/meywd/tamma/issues/13) - Task 1: Implement Claude Code provider class structure
- [#14](https://github.com/meywd/tamma/issues/14) - Task 2: Implement core message handling
- [#15](https://github.com/meywd/tamma/issues/15) - Task 3: Add provider capabilities discovery
- [#16](https://github.com/meywd/tamma/issues/16) - Task 4: Implement error handling and retry logic
- [#17](https://github.com/meywd/tamma/issues/17) - Task 5: Add telemetry and monitoring
- [#18](https://github.com/meywd/tamma/issues/18) - Task 6: Create comprehensive test suite

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-2-claude-code-provider-implementation.md)

---

### Story 1-3: Provider Configuration Management
**Status:** ready-for-dev | **Tasks:** 7

Centralized configuration for AI provider settings.

- [#19](https://github.com/meywd/tamma/issues/19) - Task 1: Design configuration schema and data structures
- [#20](https://github.com/meywd/tamma/issues/20) - Task 2: Implement configuration loading and parsing
- [#21](https://github.com/meywd/tamma/issues/21) - Task 3: Add environment variable override support
- [#22](https://github.com/meywd/tamma/issues/22) - Task 4: Implement configuration hot-reload functionality
- [#23](https://github.com/meywd/tamma/issues/23) - Task 5: Create provider discovery and selection logic
- [#24](https://github.com/meywd/tamma/issues/24) - Task 6: Create comprehensive documentation and examples
- [#25](https://github.com/meywd/tamma/issues/25) - Task 7: Add comprehensive testing

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-3-provider-configuration-management.md)

---

### Story 1-4: Git Platform Interface Definition
**Status:** ready-for-dev | **Tasks:** 6

Define abstract interface contracts for Git platform operations.

- [#26](https://github.com/meywd/tamma/issues/26) - Task 1: Design core Git platform interface structure
- [#27](https://github.com/meywd/tamma/issues/27) - Task 2: Implement platform capabilities discovery
- [#28](https://github.com/meywd/tamma/issues/28) - Task 3: Normalize platform-specific data models
- [#29](https://github.com/meywd/tamma/issues/29) - Task 4: Add pagination and rate limit support
- [#30](https://github.com/meywd/tamma/issues/30) - Task 5: Create integration documentation
- [#31](https://github.com/meywd/tamma/issues/31) - Task 6: Add comprehensive interface testing

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-4-git-platform-interface-definition.md)

---

### Story 1-5: GitHub Platform Implementation
**Status:** drafted | **Tasks:** 8

Implement GitHub as the first Git platform (reference implementation).

- [#32](https://github.com/meywd/tamma/issues/32) - Task 1: Implement GitHub platform class structure
- [#33](https://github.com/meywd/tamma/issues/33) - Task 2: Implement repository operations
- [#34](https://github.com/meywd/tamma/issues/34) - Task 3: Implement branch operations
- [#35](https://github.com/meywd/tamma/issues/35) - Task 4: Implement pull request operations
- [#36](https://github.com/meywd/tamma/issues/36) - Task 5: Add authentication and security
- [#37](https://github.com/meywd/tamma/issues/37) - Task 6: Integrate GitHub Actions and Review APIs
- [#38](https://github.com/meywd/tamma/issues/38) - Task 7: Add pagination and rate limit handling
- [#39](https://github.com/meywd/tamma/issues/39) - Task 8: Create comprehensive test suite

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-5-github-platform-implementation.md)

---

### Story 1-6: GitLab Platform Implementation
**Status:** drafted | **Tasks:** 6

Implement GitLab as second Git platform.

- [#40](https://github.com/meywd/tamma/issues/40) - Task 1: Implement GitLabPlatform class with IGitPlatform interface
- [#41](https://github.com/meywd/tamma/issues/41) - Task 2: Implement authentication handling
- [#42](https://github.com/meywd/tamma/issues/42) - Task 3: Integrate GitLab CI/CD API
- [#43](https://github.com/meywd/tamma/issues/43) - Task 4: Integrate GitLab Merge Request API for review workflows
- [#44](https://github.com/meywd/tamma/issues/44) - Task 5: Implement comprehensive unit testing
- [#45](https://github.com/meywd/tamma/issues/45) - Task 6: Implement integration testing

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-6-gitlab-platform-implementation.md)

---

### Story 1-7: Git Platform Configuration Management
**Status:** ready-for-dev | **Tasks:** 5

Centralized configuration for Git platform settings.

- [#46](https://github.com/meywd/tamma/issues/46) - Task 1: Design configuration schema and interfaces
- [#47](https://github.com/meywd/tamma/issues/47) - Task 2: Implement configuration loading and validation
- [#48](https://github.com/meywd/tamma/issues/48) - Task 3: Implement platform registry and selection
- [#49](https://github.com/meywd/tamma/issues/49) - Task 4: Create comprehensive documentation and examples
- [#50](https://github.com/meywd/tamma/issues/50) - Task 5: Implement comprehensive testing

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-7-git-platform-configuration-management.md)

---

### Story 1-8: Hybrid Orchestrator/Worker Architecture Design
**Status:** ready-for-dev | **Tasks:** 7

Document architecture for orchestrator mode and worker mode.

- [#51](https://github.com/meywd/tamma/issues/51) - Task 1: Define orchestrator mode architecture
- [#52](https://github.com/meywd/tamma/issues/52) - Task 2: Define worker mode architecture
- [#53](https://github.com/meywd/tamma/issues/53) - Task 3: Design shared components and interfaces
- [#54](https://github.com/meywd/tamma/issues/54) - Task 4: Create sequence diagrams and workflows
- [#55](https://github.com/meywd/tamma/issues/55) - Task 5: Define state persistence and recovery strategy
- [#56](https://github.com/meywd/tamma/issues/56) - Task 6: Document integration points and APIs
- [#57](https://github.com/meywd/tamma/issues/57) - Task 7: Architecture review and approval

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-8-hybrid-orchestrator-worker-architecture-design.md)

---

### Story 1-9: Basic CLI Scaffolding with Mode Selection
**Status:** ready-for-dev | **Tasks:** 5

Build basic CLI entry point supporting orchestrator and worker modes.

- [#58](https://github.com/meywd/tamma/issues/58) - Task 1: CLI argument parsing and mode selection
- [#59](https://github.com/meywd/tamma/issues/59) - Task 2: Configuration loading and validation
- [#60](https://github.com/meywd/tamma/issues/60) - Task 3: Abstraction initialization
- [#61](https://github.com/meywd/tamma/issues/61) - Task 4: Mode-specific startup logic
- [#62](https://github.com/meywd/tamma/issues/62) - Task 5: Integration testing

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-9-basic-cli-scaffolding-with-mode-selection.md)

---

### Story 1-10: Additional AI Provider Implementations
**Status:** ready-for-dev | **Tasks:** 10

Support for multiple AI providers (OpenAI, GitHub Copilot, Google Gemini, OpenRouter, local LLMs, etc.).

- [#63](https://github.com/meywd/tamma/issues/63) - Task 1: OpenAI Provider Implementation
- [#64](https://github.com/meywd/tamma/issues/64) - Task 2: GitHub Copilot Provider Implementation
- [#65](https://github.com/meywd/tamma/issues/65) - Task 3: Google Gemini Provider Implementation
- [#70](https://github.com/meywd/tamma/issues/70) - Task 4: OpenCode Provider Implementation
- [#71](https://github.com/meywd/tamma/issues/71) - Task 5: z.ai Provider Implementation
- [#72](https://github.com/meywd/tamma/issues/72) - Task 6: Zen MCP Provider Implementation
- [#66](https://github.com/meywd/tamma/issues/66) - Task 7: OpenRouter Provider Implementation
- [#67](https://github.com/meywd/tamma/issues/67) - Task 8: Local LLM Provider Implementation
- [#68](https://github.com/meywd/tamma/issues/68) - Task 9: Provider Selection and Configuration
- [#69](https://github.com/meywd/tamma/issues/69) - Task 10: Documentation and Provider Comparison

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-10-additional-ai-provider-implementations.md)

---

### Story 1-11: Additional Git Platform Implementations
**Status:** ready-for-dev | **Tasks:** 7

Support for multiple Git platforms (Gitea, Forgejo, Bitbucket, Azure DevOps, Plain Git).

- [#73](https://github.com/meywd/tamma/issues/73) - Task 1: Gitea Provider Implementation
- [#74](https://github.com/meywd/tamma/issues/74) - Task 2: Forgejo Provider Implementation
- [#75](https://github.com/meywd/tamma/issues/75) - Task 3: Bitbucket Provider Implementation
- [#76](https://github.com/meywd/tamma/issues/76) - Task 4: Azure DevOps Provider Implementation
- [#77](https://github.com/meywd/tamma/issues/77) - Task 5: Plain Git Provider Implementation
- [#78](https://github.com/meywd/tamma/issues/78) - Task 6: Platform Selection and Configuration
- [#79](https://github.com/meywd/tamma/issues/79) - Task 7: Documentation and Platform Comparison

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-11-additional-git-platform-implementations.md)

---

### Story 1-12: Initial Marketing Website
**Status:** ready-for-dev | **Tasks:** 8

Deploy initial marketing website on Cloudflare Workers.

- [#80](https://github.com/meywd/tamma/issues/80) - Task 1: Design and content creation
- [#81](https://github.com/meywd/tamma/issues/81) - Task 2: Cloudflare Workers setup
- [#82](https://github.com/meywd/tamma/issues/82) - Task 3: Email signup implementation
- [#83](https://github.com/meywd/tamma/issues/83) - Task 4: Content pages
- [#84](https://github.com/meywd/tamma/issues/84) - Task 5: Responsive design and performance
- [#85](https://github.com/meywd/tamma/issues/85) - Task 6: SEO and social sharing
- [#86](https://github.com/meywd/tamma/issues/86) - Task 7: Analytics and monitoring
- [#87](https://github.com/meywd/tamma/issues/87) - Task 8: Testing and deployment

[📄 Story Document](https://github.com/meywd/tamma/blob/main/docs/stories/1-12-initial-marketing-website.md)

---

## Technical Deliverables

### Packages Created

- `@tamma/providers` - AI provider abstraction layer
- `@tamma/platforms` - Git platform abstraction layer
- `@tamma/config` - Configuration management
- `@tamma/orchestrator` - Orchestrator mode service
- `@tamma/worker` - Worker mode service
- `@tamma/cli` - Command-line interface

### Key Interfaces

- `IAIProvider` - AI provider contract
- `IGitPlatform` - Git platform contract
- `ProviderConfig` - Configuration schema
- `PlatformConfig` - Platform configuration schema

### Documentation

- [Technical Specification](https://github.com/meywd/tamma/blob/main/docs/tech-spec-epic-1.md)
- [Architecture Document](https://github.com/meywd/tamma/blob/main/docs/architecture.md)
- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md)

---

## Dependencies

**Prerequisite Epics:** None (foundational epic)

**Dependent Epics:**
- Epic 1.5 (Deployment) depends on Epic 1
- Epic 2 (Autonomous Loop) depends on Epic 1

---

_For detailed technical specifications, see [Tech Spec Epic 1](https://github.com/meywd/tamma/blob/main/docs/tech-spec-epic-1.md)._
