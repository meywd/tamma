# Tamma - Epic Breakdown

**Author:** BMad
**Date:** 2025-10-27
**Project Level:** 3
**Target Scale:** Complex System - 42-52 stories across 5 epics

---

## Overview

This document provides the detailed epic breakdown for Tamma, expanding on the high-level epic list in the [PRD](./PRD.md).

Each epic includes:

- Expanded goal and value proposition
- Complete story breakdown with user stories
- Acceptance criteria for each story
- Story sequencing and dependencies

**Epic Sequencing Principles:**

- Epic 1 establishes foundational infrastructure and initial functionality
- Subsequent epics build progressively, each delivering significant end-to-end value
- Stories within epics are vertically sliced and sequentially ordered
- No forward dependencies - each story builds only on previous work

---

## Epic 1: Foundation & Core Infrastructure (Weeks 0-2)

**Goal:** Establish foundational architecture decisions and integration capabilities before feature development begins.

**Value Delivered:** Multi-provider AI flexibility (8 providers: Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs), multi-platform Git support (7 platforms: GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git), architectural foundation for autonomous loops.

**Estimated Stories:** 12 stories

---

### **Story 1-0: AI Provider Strategy Research**

As a **technical architect**,
I want to research AI provider options across cost models, capabilities, and workflow fit,
So that I can make informed decisions about which AI providers to support and when to use each.

**Acceptance Criteria:**
1. Research document compares at least 5 AI providers: Anthropic Claude, OpenAI GPT, GitHub Copilot, Google Gemini, local models (Ollama/LM Studio)
2. Cost analysis includes: subscription plans, pay-as-you-go rates, volume discounts, free tiers
3. Capability matrix maps providers to Tamma workflow steps: issue analysis, code generation, test generation, code review, refactoring, documentation
4. Integration approach evaluated: SDK/API (headless), IDE extensions, CLI tools, self-hosted models
5. Deployment compatibility assessed: orchestrator mode, worker mode, CI/CD environments, developer workstations
6. Recommendation matrix produced: Primary provider for MVP, secondary providers for specific workflows, long-term extensibility strategy
7. Cost projection calculated: estimated monthly spend for 10 users, 100 issues/month, 3 workflows/issue

**Prerequisites:** None (foundational research story)

---

### **Story 1.1: AI Provider Interface Definition**

As a **system architect**,
I want to define abstract interface contracts for AI provider operations,
So that the system can support multiple AI providers without tight coupling.

**Acceptance Criteria:**
1. Interface defines core operations: `generateCode()`, `analyzeContext()`, `suggestFix()`, `reviewChanges()`
2. Interface includes provider capabilities discovery (supports streaming, token limits, model versions)
3. Interface includes error handling contracts (rate limits, timeouts, context overflow)
4. Documentation includes integration guide for adding new providers
5. Interface supports both synchronous and asynchronous invocation patterns

**Prerequisites:** None (foundational story)

---

### **Story 1.2: Anthropic Claude Provider Implementation**

As a **developer**,
I want the Anthropic Claude API implemented as the first AI provider,
So that I can validate the provider abstraction with a real implementation.

**Note:** Implementation uses Anthropic Claude API via SDK (`@anthropic-ai/sdk`) for programmatic/headless access. Story 1-0 research will validate this is the optimal provider for MVP.

**Acceptance Criteria:**
1. Anthropic Claude provider implements all interface operations from Story 1.1
2. Provider handles authentication via API key configuration
3. Provider supports streaming responses for real-time feedback
4. Provider includes retry logic with exponential backoff for transient failures
5. Unit tests cover happy path, error cases, and edge cases (context limits, rate limiting)
6. Integration test demonstrates end-to-end code generation request

**Prerequisites:** Story 1-0 (research informs provider selection), Story 1.1 (interface must exist)

---

### **Story 1.3: Provider Configuration Management**

As a **DevOps engineer**,
I want centralized configuration for AI provider settings,
So that I can easily switch providers or configure provider-specific parameters.

**Acceptance Criteria:**
1. Configuration file supports multiple provider entries (Claude Code, OpenCode, GLM, local LLM)
2. Each provider entry includes: name, API endpoint, API key reference, capabilities, priority
3. Configuration validates on load (required fields, valid URLs, accessible credentials)
4. System supports environment variable overrides for sensitive values (API keys)
5. Configuration reload without restart for non-critical settings changes
6. Documentation includes example configurations for all planned providers

**Prerequisites:** Story 1.1 (interface defines configuration schema)

---

### **Story 1.4: Git Platform Interface Definition**

As a **system architect**,
I want to define abstract interface contracts for Git platform operations,
So that the system can support GitHub, GitLab, Gitea, and Forgejo without platform-specific logic in core workflows.

**Acceptance Criteria:**
1. Interface defines core operations: `createPR()`, `commentOnPR()`, `mergePR()`, `getIssue()`, `createBranch()`, `triggerCI()`
2. Interface includes platform capabilities discovery (review workflows, CI/CD integration, webhook support)
3. Interface normalizes platform-specific models (PR structure, issue format, CI status)
4. Documentation includes integration guide for adding new platforms
5. Interface supports pagination and rate limit handling

**Prerequisites:** None (foundational story, parallel to Story 1.1)

---

### **Story 1.5: GitHub Platform Implementation**

As a **developer**,
I want GitHub implemented as the first Git platform,
So that I can validate the platform abstraction with the most popular Git hosting service.

**Acceptance Criteria:**
1. GitHub provider implements all interface operations from Story 1.4
2. Provider handles authentication via Personal Access Token (PAT) or GitHub App
3. Provider integrates with GitHub Actions API for CI/CD triggering
4. Provider integrates with GitHub Review API for automated review workflows
5. Unit tests cover happy path, error cases, and GitHub-specific quirks
6. Integration test demonstrates end-to-end PR creation and merge

**Prerequisites:** Story 1.4 (interface must exist)

---

### **Story 1.6: GitLab Platform Implementation**

As a **developer**,
I want GitLab implemented as the second Git platform,
So that teams using GitLab can adopt the system without platform migration.

**Acceptance Criteria:**
1. GitLab provider implements all interface operations from Story 1.4
2. Provider handles authentication via Personal Access Token or OAuth
3. Provider integrates with GitLab CI API for pipeline triggering
4. Provider integrates with GitLab Merge Request API for review workflows
5. Unit tests cover happy path, error cases, and GitLab-specific differences from GitHub
6. Integration test demonstrates end-to-end Merge Request creation and merge

**Prerequisites:** Story 1.4 (interface must exist)

---

### **Story 1.7: Git Platform Configuration Management**

As a **DevOps engineer**,
I want centralized configuration for Git platform settings,
So that I can easily specify which platform to use and configure platform-specific parameters.

**Acceptance Criteria:**
1. Configuration file supports platform entries (GitHub, GitLab, Gitea, Forgejo)
2. Each platform entry includes: type, base URL, authentication method, webhook secret
3. Configuration validates on load (reachable endpoints, valid credentials)
4. System supports environment variable overrides for sensitive values (tokens)
5. Configuration includes default branch name, PR template path, and label conventions
6. Documentation includes example configurations for all supported platforms

**Prerequisites:** Story 1.4 (interface defines configuration schema)

---

### **Story 1.8: Hybrid Orchestrator/Worker Architecture Design**

As a **system architect**,
I want documented architecture for orchestrator mode and worker mode,
So that the system can operate both as autonomous coordinator and as CI/CD-invoked worker.

**Acceptance Criteria:**
1. Architecture document defines orchestrator mode responsibilities (issue selection, loop coordination, state management)
2. Architecture document defines worker mode responsibilities (CI/CD integration, single-task execution, exit codes)
3. Document includes sequence diagrams for both modes
4. Document specifies shared components (AI abstraction, Git abstraction, quality gates)
5. Document defines state persistence strategy for graceful shutdown/restart
6. Architecture reviewed and approved by technical lead

**Prerequisites:** Stories 1.1-1.7 (abstractions inform architecture decisions)

---

### **Story 1.9: Basic CLI Scaffolding with Mode Selection**

As a **developer**,
I want a basic CLI entry point that supports both orchestrator and worker modes,
So that I can test mode switching and validate the hybrid architecture design.

**Acceptance Criteria:**
1. CLI supports `--mode orchestrator` flag for autonomous coordinator behavior
2. CLI supports `--mode worker` flag for CI/CD-invoked single-task execution
3. CLI loads configuration from config file and environment variables
4. CLI initializes AI provider abstraction and Git platform abstraction
5. CLI outputs mode selection to logs for debugging
6. CLI includes `--version` and `--help` commands with usage examples
7. Integration test demonstrates launching in both modes

**Prerequisites:** Story 1.8 (architecture defines mode behavior)

---

### **Story 1-10: Additional AI Provider Implementations**

As a **Tamma operator**,
I want support for multiple AI providers (OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, and local LLMs),
So that I can choose the optimal provider based on cost, capability, and deployment requirements.

**Acceptance Criteria:**
1. OpenAI provider implements IAIProvider interface with support for GPT-4, GPT-3.5-turbo, and o1 models
2. GitHub Copilot provider implements IAIProvider interface with Copilot API integration
3. Google Gemini provider implements IAIProvider interface with support for Gemini Pro and Ultra models
4. OpenCode provider implements IAIProvider interface with OpenCode API integration
5. z.ai provider implements IAIProvider interface with z.ai API integration
6. Zen MCP provider implements IAIProvider interface with Model Context Protocol support
7. OpenRouter provider implements IAIProvider interface with multi-model routing support
8. Local LLM provider implements IAIProvider interface with support for Ollama, LM Studio, and vLLM backends
9. Each provider includes comprehensive error handling, retry logic, and streaming support
10. Provider selection configurable via config file or environment variables
11. Integration tests validate each provider with real API calls (or mocked for local LLMs)
12. Documentation includes provider comparison matrix and setup instructions for each provider

**Prerequisites:** Story 1-0 (research informs provider selection), Story 1.1 (interface must exist), Story 1.2 (reference implementation)

---

### **Story 1-11: Additional Git Platform Implementations**

As a **Tamma operator**,
I want support for multiple Git platforms (Gitea, Forgejo, Bitbucket, Azure DevOps, and plain Git),
So that I can use Tamma with my preferred Git hosting service regardless of vendor.

**Acceptance Criteria:**
1. Gitea provider implements IGitPlatform interface with Gitea API integration
2. Forgejo provider implements IGitPlatform interface with Forgejo API integration
3. Bitbucket provider implements IGitPlatform interface with Bitbucket Cloud and Server API support
4. Azure DevOps provider implements IGitPlatform interface with Azure DevOps Services and Server API support
5. Plain Git provider implements IGitPlatform interface with local Git operations (no platform features)
6. Each provider includes comprehensive error handling, retry logic, and pagination support
7. Provider selection configurable via config file or environment variables
8. Integration tests validate each provider with real API calls (or local Git for plain Git provider)
9. Documentation includes platform comparison matrix and setup instructions for each platform

**Prerequisites:** Story 1.4 (interface must exist), Story 1.5 (GitHub reference implementation), Story 1.6 (GitLab reference implementation)

---

## Epic 2: Autonomous Development Loop - Core (Weeks 2-4)

**Goal:** Implement the fundamental 14-step autonomous development loop with basic code generation, Git operations, and user approval checkpoints.

**Value Delivered:** Basic autonomous development capability (issue selection, PR creation, code generation, merge operations, auto-next issue).

**Estimated Stories:** 11 stories

---

### **Story 2.1: Issue Selection with Filtering**

As a **developer**,
I want the system to select the next unassigned issue from the configured repository,
So that the autonomous loop can start without manual issue specification.

**Acceptance Criteria:**
1. System queries Git platform API for open issues in configured repository
2. System filters issues by labels (configured inclusion/exclusion labels)
3. System prioritizes issues by age (oldest first) as default strategy
4. System assigns selected issue to configured bot user account
5. System logs issue selection with issue number, title, and URL
6. If no issues match criteria, system enters idle state and polls every 5 minutes
7. Integration test with mock Git platform API

**Prerequisites:** Story 1.5 or 1.6 (Git platform implementation must exist)

---

### **Story 2.2: Issue Context Analysis**

As a **developer**,
I want the system to analyze selected issue content and related context,
So that code generation has complete understanding of requirements.

**Acceptance Criteria:**
1. System reads issue title, body, labels, and comments
2. System identifies related issues via issue references (#123 format)
3. System loads recent commit history (last 10 commits) for project context
4. System loads relevant file paths mentioned in issue body
5. System constructs context summary (500-1000 words) for AI provider
6. Context summary logged to event trail for transparency
7. Unit test validates context extraction from mock issue data

**Prerequisites:** Story 2.1 (issue selection must complete first)

---

### **Story 2.3: Development Plan Generation with Approval Checkpoint**

As a **developer**,
I want the system to generate a development plan and wait for my approval,
So that I can review the approach before code is written.

**Acceptance Criteria:**
1. System sends issue context to AI provider with prompt: "Generate step-by-step development plan"
2. System receives plan with 3-7 implementation steps
3. System presents plan to user via CLI output with formatted steps
4. System prompts user: "Approve plan? [Y/n/edit]"
5. If user enters 'Y' or 'y', proceed to next step
6. If user enters 'n', abort loop and unassign issue
7. If user enters 'edit', allow inline plan modification before proceeding
8. Plan and approval decision logged to event trail

**Prerequisites:** Story 2.2 (context analysis provides input for plan)

---

### **Story 2.4: Git Branch Creation**

As a **developer**,
I want the system to create a feature branch for the issue,
So that development happens in isolation from main branch.

**Acceptance Criteria:**
1. System generates branch name format: `Tamma/issue-{number}-{sanitized-title}`
2. System creates branch from latest main/master branch via Git platform API
3. System handles conflict if branch already exists (append timestamp suffix)
4. System logs branch creation with branch name and base SHA
5. Branch creation failure triggers graceful abort with error logging
6. Integration test with mock Git platform API

**Prerequisites:** Story 2.3 (plan approval indicates readiness to start development)

---

### **Story 2.5: Test-First Development - Write Failing Tests**

As a **developer**,
I want the system to generate tests before implementation code,
So that development follows TDD principles.

**Acceptance Criteria:**
1. System sends plan to AI provider with prompt: "Generate failing tests for step 1"
2. System receives test code with clear test cases
3. System writes test files to appropriate test directory (following project conventions)
4. System commits tests to feature branch with message: "Add tests for [issue title]"
5. System runs tests locally and verifies they fail (red phase)
6. Test output logged to event trail
7. If tests unexpectedly pass, system flags for human review

**Prerequisites:** Story 2.4 (branch must exist for commits)

---

### **Story 2.6: Implementation Code Generation**

As a **developer**,
I want the system to generate implementation code to pass the tests,
So that the feature is developed following TDD workflow.

**Acceptance Criteria:**
1. System sends plan and failing tests to AI provider with prompt: "Implement code to pass tests"
2. System receives implementation code with necessary changes
3. System writes implementation files to appropriate source directories
4. System commits implementation to feature branch with message: "Implement [issue title]"
5. System runs tests locally and verifies they pass (green phase)
6. Test output logged to event trail
7. If tests still fail, system enters retry loop (max 3 attempts) with error feedback to AI

**Prerequisites:** Story 2.5 (tests must be written first)

---

### **Story 2.7: Code Refactoring Pass**

As a **developer**,
I want the system to perform optional refactoring after tests pass,
So that code quality is maintained (TDD refactor phase).

**Acceptance Criteria:**
1. System sends implementation code to AI provider with prompt: "Suggest refactoring for improved readability/maintainability"
2. If AI suggests refactoring, system presents to user: "Apply refactoring? [Y/n]"
3. If user approves, system applies refactoring and commits with message: "Refactor [issue title]"
4. System re-runs tests to verify refactoring didn't break functionality
5. If user rejects or AI suggests no refactoring, proceed to next step
6. Refactoring decision logged to event trail

**Prerequisites:** Story 2.6 (implementation must pass tests first)

---

### **Story 2.8: Pull Request Creation**

As a **developer**,
I want the system to create a Pull Request for the feature branch,
So that changes can be reviewed and merged.

**Acceptance Criteria:**
1. System generates PR title format: "[Tamma] {issue title}"
2. System generates PR body including: issue reference, development plan summary, test results summary
3. System creates PR via Git platform API (base: main/master, head: feature branch)
4. System adds labels to PR (e.g., "automated", "Tamma-generated")
5. System requests review from configured reviewers (if configured)
6. System logs PR creation with PR URL
7. Integration test with mock Git platform API

**Prerequisites:** Story 2.7 (code must be committed to feature branch)

---

### **Story 2.9: PR Status Monitoring**

As a **developer**,
I want the system to monitor the PR for CI/CD status and review feedback,
So that the system can respond to build failures or review comments.

**Acceptance Criteria:**
1. System polls PR status every 30 seconds (configurable interval)
2. System checks CI/CD status via Git platform API (pending, success, failure)
3. System checks review status (approved, changes requested, commented)
4. System logs status changes to event trail
5. If CI/CD fails, system retrieves failure logs and presents to user
6. If reviews request changes, system presents feedback to user
7. System supports manual intervention: "Continue monitoring? [Y/retry/abort]"

**Prerequisites:** Story 2.8 (PR must be created first)

---

### **Story 2.10: PR Merge with Completion Checkpoint**

As a **developer**,
I want the system to merge the PR after CI passes and reviews approve,
So that the feature is integrated into main branch.

**Acceptance Criteria:**
1. System waits for CI/CD success and required review approvals
2. System presents merge checkpoint: "PR ready to merge. Proceed? [Y/n]"
3. If user approves, system merges PR via Git platform API (using configured merge strategy: merge commit, squash, rebase)
4. System deletes feature branch after successful merge (if configured)
5. System updates issue status to closed with comment linking to merged PR
6. System logs merge completion with merge SHA
7. If merge fails (conflicts), system alerts user and waits for manual resolution

**Prerequisites:** Story 2.9 (PR status must be monitored)

---

### **Story 2.11: Auto-Next Issue Selection**

As a **developer**,
I want the system to automatically select the next issue after completing current one,
So that the autonomous loop continues without manual intervention.

**Acceptance Criteria:**
1. After successful merge (Story 2.10), system waits 10 seconds (cooldown period)
2. System returns to Story 2.1 (issue selection) logic
3. System maintains loop counter and logs iteration number
4. System supports max iterations limit (configurable, default: infinite)
5. System supports graceful shutdown signal (SIGINT/SIGTERM) to stop after current iteration
6. System logs loop continuation to event trail

**Prerequisites:** Story 2.10 (previous issue must complete)

---

## Epic 3: Quality Gates & Intelligence Layer (Weeks 5-7)

**Goal:** Add build automation, test execution, CI/CD integration with 3-retry limits and mandatory escalation. Implement research capability, clarifying questions, ambiguity detection, and multi-option design proposals.

**Value Delivered:** Quality enforcement through automated gates (no bypass), intelligent handling of ambiguous requirements, prevention-first mindset.

**MVP Critical:** All stories in Epic 3 are required for MVP - quality gates prevent Tamma from breaking itself during self-maintenance, mandatory escalation ensures Tamma never gets stuck.

**Estimated Stories:** 9 stories (all MVP critical)

---

### **Story 3.1: Build Automation with Retry Logic**

As a **developer**,
I want the system to automatically trigger builds and handle build failures intelligently,
So that build errors are resolved without manual intervention (within retry limits).

**Acceptance Criteria:**
1. System triggers build via platform-specific CI/CD API (GitHub Actions, GitLab CI, etc.) after each commit
2. System polls build status every 15 seconds until completion
3. If build fails, system retrieves build logs and error messages
4. System sends error logs to AI provider with prompt: "Analyze build failure and suggest fix"
5. System applies suggested fix, commits, and retriggers build (retry count incremented)
6. System allows maximum 3 retry attempts for build failures
7. After 3 failed retries, system escalates to human with full error context
8. All build attempts logged to event trail with status and retry count

**Prerequisites:** Story 2.8 (PR creation triggers first build)

---

### **Story 3.2: Test Execution with Retry Logic**

As a **developer**,
I want the system to automatically run tests and handle test failures intelligently,
So that test errors are resolved without manual intervention (within retry limits).

**Acceptance Criteria:**
1. System executes test suite locally after implementation (Story 2.6) and after each fix
2. System captures test output (pass/fail counts, error messages, stack traces)
3. If tests fail, system sends failures to AI provider with prompt: "Analyze test failures and suggest fix"
4. System applies suggested fix, commits, and re-runs tests (retry count incremented)
5. System allows maximum 3 retry attempts for test failures
6. After 3 failed retries, system escalates to human with full test output
7. All test attempts logged to event trail with results and retry count
8. System differentiates between test failures (expected behavior) and test errors (unexpected exceptions)

**Prerequisites:** Story 2.6 (implementation generates tests to run)

---

### **Story 3.3: Mandatory Escalation Workflow**

As a **team lead**,
I want the system to escalate to humans after retry limits are exhausted,
So that persistent issues are handled by humans rather than infinite loops.

**Acceptance Criteria:**
1. When retry limit reached (build, test, or any quality gate), system creates escalation event
2. System posts comment on PR: "❌ Escalation Required: [issue type] failed after 3 attempts. Review needed."
3. System adds "needs-human-review" label to PR
4. System sends notification via configured channel (CLI output, webhook, email)
5. System pauses autonomous loop for this issue (does not auto-select next issue)
6. Escalation includes: failure type, all retry attempts with logs, suggested next steps
7. System waits for human resolution marker before proceeding
8. Escalation events logged to event trail with full context

**Prerequisites:** Stories 3.1, 3.2 (retry logic must exhaust before escalation)

---

### **Story 3.4: Research Capability for Unfamiliar Concepts**

As a **developer**,
I want the system to research unfamiliar technologies or APIs before attempting implementation,
So that code generation is informed by accurate, up-to-date information.

**Acceptance Criteria:**
1. During plan generation (Story 2.3), system detects unfamiliar terms (not in known API list)
2. System sends research query to AI provider: "Research [concept]: provide API documentation, common patterns, gotchas"
3. System receives research summary (300-500 words) with code examples
4. System incorporates research findings into implementation context
5. System logs research queries and findings to event trail for audit
6. Research is cached for 24 hours to avoid redundant queries
7. System supports manual research trigger via CLI flag: `--research "[query]"`

**Prerequisites:** Story 2.3 (plan generation identifies research needs)

---

### **Story 3.5: Clarifying Questions for Ambiguous Requirements**

As a **product owner**,
I want the system to ask clarifying questions when requirements are ambiguous,
So that implementation aligns with actual intent rather than guessed assumptions.

**Acceptance Criteria:**
1. During issue analysis (Story 2.2), system detects ambiguity indicators (vague wording, missing details, conflicting statements)
2. System generates 2-5 clarifying questions with multiple-choice options where possible
3. System presents questions to user via CLI: "Requirements need clarification: [questions]"
4. User provides answers via interactive prompts
5. System incorporates answers into development context
6. Questions and answers logged to event trail and posted as PR comment for visibility
7. System skips question generation if issue has "skip-questions" label

**Prerequisites:** Story 2.2 (issue analysis identifies ambiguity)

---

### **Story 3.6: Ambiguity Detection Scoring**

As a **developer**,
I want the system to quantify requirement ambiguity with a confidence score,
So that high-risk issues are flagged for extra review before implementation.

**Acceptance Criteria:**
1. System analyzes issue content and assigns ambiguity score (0-100, higher = more ambiguous)
2. Scoring factors: vague language, missing acceptance criteria, conflicting requirements, unusual feature requests
3. If score > 70 (high ambiguity), system prompts: "⚠️ High ambiguity detected. Proceed with clarifying questions? [Y/n]"
4. If score > 90 (very high ambiguity), system suggests: "Consider breaking issue into smaller, clearer tasks"
5. Ambiguity score logged to event trail and displayed in PR description
6. System allows override via label "proceed-despite-ambiguity"

**Prerequisites:** Story 3.5 (clarifying questions are the mitigation for high ambiguity)

---

### **Story 3.7: Multi-Option Design Proposals**

As a **architect**,
I want the system to present multiple design approaches for complex features,
So that I can choose the best technical direction before implementation.

**Acceptance Criteria:**
1. For issues labeled "design-options-needed", system generates 2-3 alternative design approaches
2. Each option includes: description, pros/cons, implementation complexity, test strategy
3. System presents options via CLI with numbered list
4. User selects option via interactive prompt: "Select design [1/2/3/custom]"
5. If user selects "custom", allow inline design specification
6. Selected design incorporated into development plan (Story 2.3)
7. Design options and selection logged to event trail and posted as PR comment

**Prerequisites:** Story 2.3 (plan generation is where design is incorporated)

---

### **Story 3.8: Static Analysis Integration**

As a **developer**,
I want the system to run static analysis tools (linters, formatters, security scanners) automatically,
So that code quality issues are caught before PR creation.

**Acceptance Criteria:**
1. System detects project's static analysis tools (ESLint, Pylint, RuboCop, etc.) from config files
2. System runs static analysis after implementation (Story 2.6) and before commit
3. System captures analysis output (errors, warnings, suggestions)
4. If critical errors found, system applies auto-fixes (e.g., formatting) and re-runs analysis
5. If errors remain, system sends to AI provider for fix suggestions (subject to retry limits)
6. System includes static analysis results in PR description
7. All analysis runs logged to event trail

**Prerequisites:** Story 2.6 (implementation must exist before static analysis)

---

### **Story 3.9: Security Scanning Integration**

As a **security engineer**,
I want the system to run security vulnerability scans automatically,
So that known vulnerabilities are blocked before code reaches production.

**Acceptance Criteria:**
1. System runs dependency vulnerability scanner (npm audit, pip-audit, bundle-audit) before PR creation
2. System runs code security scanner (Semgrep, Bandit, Brakeman) on changed files
3. If critical vulnerabilities found, system blocks PR creation and escalates immediately
4. If medium/low vulnerabilities found, system adds PR comment with findings and recommended fixes
5. System applies recommended fixes if available (e.g., dependency updates) and re-scans
6. Security scan results included in PR description with severity counts
7. All security scans logged to event trail with findings

**Prerequisites:** Story 2.8 (PR creation is the checkpoint for security gates)

---

## Epic 4: Event Sourcing & Audit Trail (Weeks 8-10)

**Goal:** Implement CQRS event sourcing for complete transparency and audit compliance. Capture all user actions, AI actions, and system state changes with millisecond precision.

**Value Delivered:** Complete audit trail (100% traceability), compliance readiness (SOC2, ISO27001, GDPR), time-travel debugging, differential diagnosis.

**Estimated Stories:** 8 stories

---

### **Story 4.1: Event Schema Design**

As a **system architect**,
I want a comprehensive event schema covering all system actions and state changes,
So that event sourcing captures complete system history.

**Acceptance Criteria:**
1. Event schema defines base fields: `eventId`, `timestamp`, `eventType`, `actorType`, `actorId`, `payload`, `metadata`
2. Schema includes event types for: issue selection, AI requests/responses, code changes, Git operations, approvals, escalations, errors
3. Schema supports event versioning (schema version field) for future evolution
4. Schema includes correlation IDs for linking related events (e.g., all events for single PR)
5. Schema validated with JSON Schema or Protocol Buffers
6. Documentation includes event catalog with examples for each event type

**Prerequisites:** None (foundational story for Epic 4)

---

### **Story 4.2: Event Store Backend Selection**

As a **DevOps engineer**,
I want a persistent, append-only event store for storing all system events,
So that events are never lost and can be replayed for debugging or audit.

**Acceptance Criteria:**
1. Event store supports append-only writes (no updates or deletes)
2. Event store provides ordered reads by timestamp with efficient querying
3. Event store supports filtering by event type, actor, correlation ID
4. Event store handles high write throughput (100+ events/second)
5. Implementation supports multiple backends: local file (dev), PostgreSQL (prod), EventStore (optional)
6. Backend selection configurable via configuration file
7. Event store includes retention policy configuration (default: infinite retention)

**Prerequisites:** Story 4.1 (schema must exist before storage implementation)

---

### **Story 4.3: Event Capture - Issue Selection & Analysis**

As a **compliance officer**,
I want all issue selection and analysis actions captured as events,
So that I can audit which issues were selected and why.

**Acceptance Criteria:**
1. `IssueSelectedEvent` captured when issue is selected (Story 2.1) including issue ID, title, labels, selection criteria
2. `IssueAnalysisCompletedEvent` captured after analysis (Story 2.2) including context summary length, referenced issues
3. Events include actor (system in orchestrator mode, CI runner in worker mode)
4. Events include correlation ID linking entire development cycle
5. Events persisted to event store before proceeding to next step
6. Event write failures trigger retry (3 attempts) then halt autonomous loop for data integrity

**Prerequisites:** Story 4.2 (event store backend must exist)

---

### **Story 4.4: Event Capture - AI Provider Interactions**

As a **AI governance team**,
I want all AI provider requests and responses captured as events,
So that I can audit AI usage, costs, and decision-making processes.

**Acceptance Criteria:**
1. `AIRequestEvent` captured before each AI provider call including: provider name, model, prompt (truncated if >1000 chars), token count estimate
2. `AIResponseEvent` captured after response including: provider name, model, response (truncated), token count, latency, cost estimate
3. Events include full prompt/response in separate blob storage for detailed analysis (with retention policy)
4. Events mask sensitive data (API keys, passwords) before persistence
5. Events include provider selection rationale (why this provider was chosen)
6. Events persisted to event store synchronously (block on write completion)

**Prerequisites:** Story 4.2 (event store backend must exist)

---

### **Story 4.5: Event Capture - Code Changes & Git Operations**

As a **code reviewer**,
I want all code changes and Git operations captured as events,
So that I can see the complete evolution of code during autonomous development.

**Acceptance Criteria:**
1. `CodeFileWrittenEvent` captured for each file write including: file path, file size, change type (create/update/delete)
2. `CommitCreatedEvent` captured for each commit including: commit SHA, message, branch name, file count
3. `BranchCreatedEvent` captured when branch created (Story 2.4)
4. `PRCreatedEvent` captured when PR created (Story 2.8) including: PR number, URL, base/head branches
5. `PRMergedEvent` captured when PR merged (Story 2.10) including: merge strategy, merge SHA
6. Events include file diffs stored in blob storage (linked from event)
7. Events capture who triggered the action (user approval vs autonomous decision)

**Prerequisites:** Story 4.2 (event store backend must exist)

---

### **Story 4.6: Event Capture - Approvals & Escalations**

As a **audit team**,
I want all user approvals and system escalations captured as events,
So that I can verify human oversight and understand when system needed help.

**Acceptance Criteria:**
1. `ApprovalRequestedEvent` captured when system requests user approval (plan, merge, etc.) including: approval type, context summary
2. `ApprovalProvidedEvent` captured when user responds including: decision (approved/rejected/edited), timestamp, user identity
3. `EscalationTriggeredEvent` captured when retry limits exhausted (Story 3.3) including: escalation reason, retry history, current state
4. `EscalationResolvedEvent` captured when human resolves escalation including: resolution description, time to resolve
5. Events support approval audit trail for compliance (who approved what when)
6. Events capture approval channel (CLI interactive, API call, webhook)

**Prerequisites:** Story 4.2 (event store backend must exist)

---

### **Story 4.7: Event Query API for Time-Travel**

As a **developer**,
I want to query events by time range and filters to reconstruct system state at any point,
So that I can debug issues by replaying what system did in the past.

**Acceptance Criteria:**
1. API endpoint: `GET /events?since={timestamp}&until={timestamp}&type={type}&correlationId={id}`
2. API returns events in chronological order with pagination support (default 100 events per page)
3. API supports filtering by: event type, actor, correlation ID, issue number
4. API supports projection queries: "What was state of PR #123 at timestamp T?"
5. API includes efficient indexing for fast queries (query completes in <1 second for 1M events)
6. API requires authentication (prevent unauthorized event access)
7. API documentation includes usage examples and query patterns

**Prerequisites:** Story 4.2 (event store must support queries)

---

### **Story 4.8: Black-Box Replay for Debugging**

As a **developer**,
I want to replay system state at any point in time to understand past behavior,
So that I can diagnose why autonomous loop made specific decisions.

**Acceptance Criteria:**
1. CLI command: `Tamma replay --correlation-id {id} --timestamp {timestamp}`
2. Command reconstructs system state by replaying events up to specified timestamp
3. Command displays: issue context, AI provider decisions, code changes, approval points, errors
4. Command supports step-by-step replay (pause at each event) via `--interactive` flag
5. Command exports replay to HTML report for sharing with team
6. Replay includes diff view showing state changes between events
7. Replay performance: complete reconstruction in <5 seconds for typical development cycle (50-100 events)

**Prerequisites:** Story 4.7 (query API provides events for replay)

---

## Epic 5: Observability & Production Readiness (Weeks 11-15)

**Goal:** Add structured logging, metrics collection, alert system, integration testing, and documentation for production launch. UI dashboards optional for MVP - CLI/log-based monitoring sufficient for self-maintenance validation.

**Value Delivered:** Essential debugging capabilities (structured logging, metrics), production monitoring readiness, alpha release documentation.

**MVP Critical:** Stories 5.1, 5.2, 5.6 (partial - basic alerts), 5.8, 5.9, 5.10
**MVP Optional:** Stories 5.3, 5.4, 5.5 (UI dashboards), 5.7 (feedback collection)

**Estimated Stories:** 10 stories (6 required for MVP, 4 optional)

---

### **Story 5.1: Structured Logging Implementation** ⭐ **MVP CRITICAL**

As a **operations engineer**,
I want structured logs (JSON format) with log levels and context,
So that I can efficiently search, filter, and analyze logs in production.

**MVP Rationale:** Essential for debugging stuck workflows and validating self-maintenance capability. Tamma must log all workflow steps to enable diagnosis when autonomous loop encounters issues in its own codebase.

**Acceptance Criteria:**
1. All log statements use structured logging library (Winston, Bunyan, structlog)
2. Log format: `{"timestamp": ISO8601, "level": "info/warn/error", "message": "...", "context": {...}}`
3. Context includes: correlation ID, issue number, PR number, actor ID
4. Log levels properly assigned: DEBUG (verbose details), INFO (key milestones), WARN (recoverable issues), ERROR (failures)
5. Logs written to: stdout (for container environments), file (for local development), log aggregation service (optional: Datadog, ELK)
6. Log volume under control: <10 log statements per event for typical flow
7. Sensitive data (API keys, tokens) redacted from all logs

**Prerequisites:** None (foundational for Epic 5)

---

### **Story 5.2: Metrics Collection Infrastructure** ⭐ **MVP CRITICAL**

As a **product manager**,
I want metrics collected for key system behaviors and performance,
So that I can track development velocity, quality trends, and system health.

**MVP Rationale:** Essential for monitoring autonomous loop health and detecting anomalies. Metrics enable tracking of completion rates, escalation rates, and quality metrics critical for self-maintenance validation.

**Acceptance Criteria:**
1. Metrics library integrated (Prometheus client, StatsD, or similar)
2. Counter metrics: `issues_processed_total`, `prs_created_total`, `prs_merged_total`, `escalations_total`
3. Gauge metrics: `active_autonomous_loops`, `pending_approvals`, `queue_depth`
4. Histogram metrics: `issue_completion_duration_seconds`, `ai_request_duration_seconds`, `test_execution_duration_seconds`
5. Metrics exposed via HTTP endpoint: `GET /metrics` (Prometheus format)
6. Metrics include labels: provider name, Git platform, issue type, outcome (success/failure)
7. Metrics scraped by Prometheus (or pushed to metrics backend) every 15 seconds

**Prerequisites:** None (parallel to Story 5.1)

---

### **Story 5.3: Real-Time Dashboard - System Health** 🔵 **MVP OPTIONAL**

As a **operations engineer**,
I want a real-time dashboard showing system health and current operations,
So that I can monitor autonomous loops and detect issues immediately.

**MVP Rationale:** Optional - CLI-based monitoring and log tailing sufficient for MVP. UI dashboard provides better UX but not required for self-maintenance validation. Can be deferred to post-MVP.

**Acceptance Criteria:**
1. Web dashboard accessible at `http://localhost:3000/dashboard` (or configured port)
2. Dashboard displays: active loops count, pending approvals count, recent escalations list
3. Dashboard displays: current issue being processed, step in autonomous loop, estimated time remaining
4. Dashboard auto-refreshes every 10 seconds via WebSocket or SSE
5. Dashboard loads in <2 seconds on initial page load
6. Dashboard includes system status indicator: 🟢 Healthy, 🟡 Degraded, 🔴 Critical
7. Dashboard works in modern browsers (Chrome, Firefox, Safari, Edge)

**Prerequisites:** Story 5.2 (dashboard displays metrics)

---

### **Story 5.4: Real-Time Dashboard - Development Velocity** 🔵 **MVP OPTIONAL**

As a **engineering manager**,
I want a dashboard showing development velocity metrics over time,
So that I can track team productivity improvements and identify bottlenecks.

**MVP Rationale:** Optional - CLI-based metrics queries and log analysis sufficient for MVP. Velocity charts provide better visualization but not required for self-maintenance validation. Can be deferred to post-MVP.

**Acceptance Criteria:**
1. Dashboard page: `http://localhost:3000/dashboard/velocity`
2. Charts display: issues completed per day (last 30 days), average time-to-merge (last 30 days), PR success rate (first-time merge vs. retry)
3. Charts include filters: date range, issue labels, AI provider
4. Charts use line charts for time series, bar charts for comparisons
5. Dashboard calculates key metrics: throughput (issues/week), cycle time (issue-to-merge duration), quality (test pass rate)
6. Dashboard exports charts as PNG or PDF for reporting
7. Dashboard responsive for mobile viewing (stakeholder reviews on-the-go)

**Prerequisites:** Story 5.2 (metrics provide data for charts)

---

### **Story 5.5: Event Trail Exploration UI** 🔵 **MVP OPTIONAL**

As a **developer**,
I want an interactive UI for exploring the event trail with filtering and search,
So that I can investigate past development cycles without writing queries.

**MVP Rationale:** Optional - Event query API (Story 4.7) provides programmatic access sufficient for MVP debugging. UI provides better UX but not required for self-maintenance validation. Can be deferred to post-MVP.

**Acceptance Criteria:**
1. Dashboard page: `http://localhost:3000/dashboard/events`
2. Event list displays: timestamp, event type, actor, summary (first 100 chars of payload)
3. Event list supports filtering: date range, event type, correlation ID, issue number
4. Event list supports full-text search across event payloads
5. Clicking event row expands full event details (JSON formatted)
6. Event list supports "Follow correlation ID" action to load all related events
7. Event list pagination (100 events per page) with infinite scroll

**Prerequisites:** Story 4.7 (event query API provides data)

---

### **Story 5.6: Alert System for Critical Issues** ⚠️ **MVP PARTIAL**

As a **operations engineer**,
I want automatic alerts when system encounters critical issues or anomalies,
So that I can respond quickly before problems escalate.

**MVP Rationale:** Partial - Basic alerts via CLI output, email, or Slack webhooks required for MVP. Full dashboard integration optional. Essential for self-maintenance to detect and respond to escalations, errors, or stuck workflows.

**Acceptance Criteria:**
1. Alert triggers: escalation after 3 retries, system error (uncaught exception), API rate limit hit, event store write failure
2. Alert channels: CLI output (if running), webhook (POST to configured URL), email (if configured)
3. Alert payload includes: severity (critical/warning/info), title, description, correlation ID, timestamp, suggested action
4. Alert rate limiting: no more than 5 alerts per minute (prevent spam)
5. Alert history stored in database for review
6. Alert delivery tested with mock webhook endpoint
7. Alert system supports configuration of custom alert rules

**Prerequisites:** Story 5.2 (metrics trigger alerts)

---

### **Story 5.7: Feedback Collection System** 🔵 **MVP OPTIONAL**

As a **product manager**,
I want to collect user feedback on autonomous loop results,
So that I can measure user satisfaction and identify improvement areas.

**MVP Rationale:** Optional - User feedback valuable for post-MVP product improvement but not required for self-maintenance validation. Metrics and logs provide sufficient data for MVP. Can be deferred to post-MVP.

**Acceptance Criteria:**
1. After PR merge, system prompts: "Rate this autonomous development cycle: 👍 👎"
2. If user selects 👎, system asks: "What went wrong? [free text]"
3. Feedback stored in database with: timestamp, correlation ID, rating, comment
4. Feedback visible in dashboard: `http://localhost:3000/dashboard/feedback`
5. Dashboard shows: satisfaction rate over time, common negative feedback themes (via keyword analysis)
6. Feedback export to CSV for analysis in external tools
7. Feedback system respects user privacy (no PII collection without consent)

**Prerequisites:** Story 2.10 (PR merge is feedback trigger point)

---

### **Story 5.8: Integration Testing Suite** ⭐ **MVP CRITICAL**

As a **QA engineer**,
I want comprehensive integration tests covering end-to-end autonomous loop scenarios,
So that regressions are caught before production deployment.

**MVP Rationale:** Essential for validating self-maintenance capability. Integration tests ensure Tamma's self-implemented changes don't break core functionality. Critical for confidence in autonomous loop robustness.

**Acceptance Criteria:**
1. Integration tests use real AI provider (mock mode) and mock Git platform API
2. Test scenarios: happy path (issue → plan → code → PR → merge), build failure with retry, test failure with escalation, ambiguous requirements with clarifying questions
3. Tests run in CI/CD pipeline on every PR
4. Tests validate: correct event sequence, proper error handling, retry limits enforced, escalation triggered
5. Tests complete in <5 minutes for full suite
6. Test coverage report shows >80% code coverage
7. Tests include assertions on event trail contents (verify all events captured)

**Prerequisites:** All previous stories (integration tests validate complete system)

---

### **Story 5.9: Documentation - User Guide & API Reference** ⭐ **MVP CRITICAL**

As a **developer adopting Tamma**,
I want comprehensive documentation covering installation, configuration, usage, and troubleshooting,
So that I can successfully deploy and operate the system.

**MVP Rationale:** Essential for alpha release. Users cannot adopt Tamma without clear setup instructions, configuration reference, and troubleshooting guides. Documentation critical for self-maintenance validation (Tamma may need to reference its own docs).

**Acceptance Criteria:**
1. User guide includes: installation instructions (Docker, binary, source), configuration reference (all config options), usage examples (orchestrator mode, worker mode)
2. User guide includes: troubleshooting section (common errors, debug mode, log analysis)
3. API reference documents: event schema, metrics endpoints, webhook payloads, CLI commands
4. Documentation hosted on GitHub Pages or similar (publicly accessible)
5. Documentation includes architecture diagrams (C4 model: context, containers, components)
6. Documentation includes video walkthrough (5-10 minutes) demonstrating autonomous loop
7. Documentation reviewed by external beta tester for clarity

**Prerequisites:** All previous stories (documentation covers complete system)

---

### **Story 5.10: Alpha Release Preparation** ⭐ **MVP CRITICAL**

As a **release manager**,
I want a release checklist and deployment artifacts for alpha launch,
So that early adopters can test the system in real projects.

**MVP Rationale:** Essential for alpha release. Release artifacts, version tagging, and release notes required for users to adopt Tamma. Includes self-maintenance validation milestone.

**Acceptance Criteria:**
1. Release checklist completed: all acceptance criteria met, integration tests passing, documentation complete, security review passed
2. Release artifacts built: Docker image (multi-arch: amd64, arm64), binary releases (Windows, macOS, Linux), source tarball
3. Release notes drafted: features included, known limitations, breaking changes, upgrade path
4. GitHub release created with version tag (v0.1.0-alpha), release notes, artifact downloads
5. Release announcement prepared for: project README, Discord/Slack channels, mailing list
6. Telemetry consent mechanism implemented (opt-in for usage data collection)
7. Alpha release tagged as "prerelease" with warning: "Not production-ready, breaking changes expected"

**Prerequisites:** Story 5.9 (documentation must be complete for alpha users)

---

## Story Guidelines Reference

**Story Format:**

```
**Story [EPIC.N]: [Story Title]**

As a [user type],
I want [goal/desire],
So that [benefit/value].

**Acceptance Criteria:**
1. [Specific testable criterion]
2. [Another specific criterion]
3. [etc.]

**Prerequisites:** [Dependencies on previous stories, if any]
```

**Story Requirements:**

- **Vertical slices** - Complete, testable functionality delivery
- **Sequential ordering** - Logical progression within epic
- **No forward dependencies** - Only depend on previous work
- **AI-agent sized** - Completable in 2-4 hour focused session
- **Value-focused** - Integrate technical enablers into value-delivering stories

---

**For implementation:** Use the `create-story` workflow to generate individual story implementation plans from this epic breakdown.
