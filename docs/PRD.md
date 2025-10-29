# Tamma Product Requirements Document (PRD)

**Author:** meywd
**Date:** 2025-10-27
**Project Level:** 3
**Target Scale:** Complex System - 15-40 stories across 2-5 epics

---

## Goals and Background Context

### Goals

1. **Enable Autonomous Development Workflows** - Achieve 70%+ autonomous completion rate for development issues through intelligent 14-step lifecycle automation (issue → plan → design → code → build → test → review → merge → deploy)

2. **Achieve Self-Maintenance Capability (MVP Goal)** - Tamma must be capable of fully maintaining itself (fixing bugs, implementing features, updating dependencies) post-MVP, validating production-readiness by autonomously completing 70%+ of its own backlog stories without human intervention. This demonstrates Tamma can handle complex, mission-critical codebases safely.

3. **Establish Multi-Provider Ecosystem** - Support 8+ AI providers (Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs) AND 7+ Git platforms (GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git) with intelligent routing to optimize costs (20-30% reduction via subscription plans) and capability matching while avoiding vendor lock-in

4. **Deliver Hybrid Orchestrator/Worker Architecture** - Provide seamless orchestrator (monitoring, assigning, triggering) and worker (CI/CD invokable) modes with 80%+ of users leveraging both deployment patterns effectively

5. **Accelerate Developer Productivity** - Reduce time spent on repetitive development toil from 40-60% to <20% of development time, enabling 3x productivity gains through automated quality gates and intelligent issue prevention

6. **Build Thriving Open-Source Ecosystem** - Grow community to 100+ GitHub stars and 10+ active contributors within 6 months post-MVP through open-core model (core features open-source, enterprise features commercial)

### Background Context

Tamma addresses a critical gap in modern software development: development teams spend 40-60% of their time on repetitive, low-creativity toil (writing boilerplate tests, fixing linting errors, addressing review comments, managing CI/CD coordination), while lacking transparency into system behavior and facing vendor lock-in with both AI providers and Git platforms. Existing tools either provide basic automation without end-to-end workflow orchestration, are tightly coupled to specific platforms (GitHub-only solutions), or lack the transparency and multi-provider flexibility that teams need for production use. Business stakeholders fear autonomous systems making breaking changes without control, while open-source maintainers struggle with review burden that doesn't scale.

Tamma is a hybrid development automation platform that orchestrates autonomous development workflows across 8+ AI providers (Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs) and 7+ Git platforms (GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git) through abstraction layers. It simultaneously executes as a worker when invoked by CI/CD runners. The platform provides complete transparency through CQRS event sourcing, enabling time-travel debugging and audit compliance. Tamma adapts its behavior based on context (Dev Mode for speed vs Business Mode for control) and implements smart quality gates that prevent issues rather than just detecting them. The 14-step autonomous loop (issue → plan → design → code → build → test → review → merge → deploy) is designed to complete 70%+ of development issues without human escalation while maintaining strategic human checkpoints at business decisions, breaking changes, and deployments. Tamma's MVP goal is self-maintenance: the platform must be robust enough to maintain its own codebase autonomously, demonstrating production-readiness for mission-critical software.

---

## Requirements

### Functional Requirements

**Autonomous Development Loop:**

**FR-1:** System shall execute a 14-step autonomous development loop: issue assignment → plan/design → PR creation → code implementation → build → test → push → CI/CD checks → review → address comments → completion check → merge → pick next issue

**FR-2:** System shall support manual issue assignment and auto-next issue selection for continuous autonomous operation

**FR-3:** System shall generate clarifying questions when encountering ambiguous specifications and wait for user approval before proceeding with design

**FR-4:** System shall propose multiple design options with tradeoffs during the planning phase for user evaluation

**FR-5:** System shall follow TDD workflow: write tests first, implement code to pass tests, then refactor

**FR-6:** System shall create Git branches, commits, pushes, and merge operations automatically with proper commit messages

**AI Provider Integration:**

**FR-7:** System shall provide abstract interface supporting 8+ AI providers (Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs) with seamless provider switching

**FR-8:** System shall maintain provider feature matrix documentation for capability comparison and intelligent provider selection based on workflow step (issue analysis vs code generation vs testing vs review)

**FR-9:** System shall route tasks to optimal AI provider based on cost optimization (target 20-30% cost reduction via subscription plans vs pay-as-you-go) and capability matching per workflow step

**Git Platform Integration:**

**FR-10:** System shall provide abstract interface for Git platform operations supporting 7+ platforms (GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git) with extensibility for additional platforms

**FR-11:** System shall integrate with platform-specific CI/CD APIs (GitHub Actions, GitLab CI, Gitea Actions, Forgejo Actions) for workflow automation

**FR-12:** System shall integrate with platform-specific review workflow APIs for automated code review triggering and feedback across all supported platforms

**Hybrid Architecture:**

**FR-13:** System shall operate as orchestrator (monitoring, assigning, triggering workflows) for centralized coordination

**FR-14:** System shall operate as worker (CLI/service invokable by CI/CD runners) for parallel task execution

**FR-15:** System shall manage state across parallel worker instances to prevent conflicts and ensure consistency

**Quality Gates & Testing:**

**FR-16:** System shall enforce quality gates at build, test, and CI/CD stages with 3-retry limit and mandatory escalation on failure (no bypass allowed)

**FR-17:** System shall execute automated testing (unit tests, integration tests) with retry logic and escalation workflow

**FR-18:** System shall perform automated code review and generate structured feedback for PR improvements

**FR-19:** System shall require user approval at design checkpoint and test/merge completion checkpoint

**Error Recovery & Self-Healing (MVP Critical):**

**FR-19a:** System shall never get stuck - all workflow states must have exit conditions (success, failure, escalation) with maximum 3 retry attempts before mandatory escalation

**FR-19b:** System shall never crash - all exceptions must be caught, logged, and handled gracefully with fallback behaviors (retry with different provider, escalate to human, skip optional steps)

**FR-19c:** System shall be able to fix issues in its own codebase - autonomous loop must work on Tamma repository itself, completing bug fixes and feature implementations without manual intervention

**FR-19d:** System shall validate all changes against comprehensive test suite (unit, integration, end-to-end) with mandatory test passage before merge - no bypassing quality gates

**FR-19e:** System shall implement graceful degradation when optional dependencies fail (UI dashboard failure does not block core autonomous loop, observability failures do not block PR creation)

**Event Sourcing & Audit Trail:**

**FR-20:** System shall capture all user actions, AI actions, and system state changes as immutable events with millisecond-precision timestamps and user attribution

**FR-21:** System shall store events in append-only log with event versioning and migration support

**FR-22:** System shall reconstruct complete system state at any point in time (black-box replay capability) for debugging and compliance

**FR-23:** System shall provide minute-by-minute event playback and differential diagnosis support

**FR-24:** System shall export audit logs in standard formats (JSON, CSV) for external compliance systems

**Observability:**

**FR-25:** System shall implement structured logging with debug and trace levels, real-time log streaming, and log aggregation/search **(MVP Critical - Essential for debugging stuck workflows and self-maintenance)**

**FR-26:** System shall collect metrics (counters, gauges, histograms) for system health, development velocity, and quality indicators **(MVP Critical - Essential for monitoring autonomous loop health)**

**FR-27:** System shall track analytics on every user interaction (page views, clicks, commands, operations) and user behavior journeys **(Optional - Post-MVP)**

**FR-28:** System shall provide real-time monitoring dashboards showing system health, development velocity, and quality metrics **(Optional - UI dashboards deferred to post-MVP, CLI/log-based monitoring sufficient for MVP)**

**FR-29:** System shall implement alert system for threshold violations and anomaly detection **(Partial MVP - Basic alerts via email/Slack sufficient, full dashboard integration post-MVP)**

**Context-Aware Modes:**

**FR-30:** System shall support Dev Mode (Consultant + Autopilot) for high automation with minimal friction and fast deployment gates

**FR-31:** System shall support Business Mode (Advisor + Guardian) for maximum control with additional checkpoints and guided decisions

**FR-32:** System shall adapt behavior, approval gates, and deployment speeds based on selected mode and environment (dev vs production)

**Security & Compliance:**

**FR-33:** System shall scan code and dependencies for security vulnerabilities and auto-fix issues through fork/fix/PR workflow

**FR-34:** System shall require manual approval for breaking changes (NEVER auto-approve), large refactors, and deletions

**Website & Documentation:**

**FR-35:** System shall provide initial marketing website (hosted on Cloudflare Workers) with project overview, key features, roadmap, email signup for launch notifications, and GitHub repository link **(MVP Critical - Epic 1.5 Story 1-12)**

**FR-36:** System shall provide comprehensive documentation website with searchable docs, installation guides, configuration reference, API documentation, tutorials, and troubleshooting guides **(MVP Critical - Epic 5 Stories 5.9a-5.9d)**

**Deployment & Packaging:**

**FR-37:** System shall support multiple deployment modes: CLI (interactive terminal), Service Mode (background daemon), Web Server (REST API), Container (Docker), and Kubernetes (cluster) with unified configuration across all modes **(MVP Critical - Epic 1.5 Stories 1.5-1 through 1.5-5, 1.5-10 optional)**

**FR-38:** System shall publish npm packages (@tamma/cli, @tamma/core, @tamma/server) to npm registry with semantic versioning and automated release pipeline **(MVP Critical - Epic 1.5 Story 1.5-8)**

**FR-39:** System shall provide standalone binary releases (Windows, macOS, Linux) and OS-specific installers (Homebrew, Chocolatey, APT, Snap) for non-Node.js users **(MVP Critical - Epic 1.5 Story 1.5-9)**

**FR-40:** System shall integrate with GitHub and GitLab webhooks for automatic triggering on issue assignment, PR creation, or issue comment events with webhook signature verification **(MVP Critical - Epic 1.5 Story 1.5-6)**

### Non-Functional Requirements

**NFR-1: Performance & Scalability**
- Autonomous loop completion time: <2 hours for standard feature implementation (issue → merged PR)
- Event store write latency: <10ms for event persistence
- Log query response time: <1 second for typical searches across 30 days of logs
- System shall support 10+ concurrent autonomous loops without degradation
- Real-time dashboards shall refresh within 500ms

**NFR-2: Reliability & Availability**
- System uptime: 99.5% availability target (approximately 3.6 hours downtime per month)
- Autonomous completion rate: 70%+ of issues complete without human escalation
- PR rework rate: <5% after quality gates enforcement
- Event store durability: 99.99% (no event loss)
- Graceful degradation: Continue operation in degraded mode if AI provider unavailable (queue tasks for retry)

**NFR-3: Security & Compliance**
- All events stored with encryption at rest and encryption in transit
- 100% action traceability in audit trail with millisecond precision timestamps
- Audit log retention: Minimum 90 days, configurable up to 7 years
- Vulnerability scan results shall be available within 5 minutes of code commit
- Security patches shall be auto-applied (with testing) within 24 hours of CVE disclosure for critical vulnerabilities

---

## User Journeys

### Journey 1: Developer - Autonomous Feature Development (Dev Mode)

**Persona:** Sarah, Senior Developer at a 15-person startup using Gitea for version control and AI-assisted development

**Scenario:** Sarah needs to implement a new API endpoint for user authentication while maintaining velocity and quality standards

**Journey Steps:**

1. **Issue Assignment**
   - Sarah selects issue #247 ("Add OAuth2 authentication endpoint") from the backlog
   - Tamma confirms assignment and enters PLAN phase
   - **Decision Point:** Continue with autonomous plan or provide additional context?
   - Sarah chooses: "Continue autonomously"

2. **Planning & Design**
   - Tamma researches existing authentication patterns in codebase
   - Tamma detects ambiguity: "Should this support refresh tokens or session-based auth?"
   - **Decision Point:** Tamma presents 2 options with tradeoffs
   - Sarah selects: "Option 2: Refresh tokens (better for mobile clients)"
   - Tamma generates design proposal showing endpoints, database schema changes, security considerations
   - **Decision Point:** Approve design or request modifications?
   - Sarah reviews and approves design

3. **Autonomous Implementation**
   - Tamma creates feature branch and PR
   - Tamma implements TDD workflow: writes tests first, then code
   - Tamma commits and pushes to PR
   - Build runs automatically (passes on first attempt)
   - Tests run automatically (2 tests fail on first run, Tamma fixes and retries - pass on second attempt)
   - CI/CD checks complete (linting passes, security scan passes)

4. **Review & Completion**
   - Tamma performs automated code review and finds no issues
   - **Decision Point:** "Test in staging or merge directly?"
   - Sarah chooses: "Test in staging first"
   - Sarah manually tests in staging environment, finds endpoint working correctly
   - Sarah approves merge
   - Tamma merges PR to main

5. **Auto-Next Iteration**
   - Tamma automatically picks next issue from backlog
   - Cycle repeats

**Success Outcomes:**
- Feature completed in 45 minutes (vs 3+ hours manually)
- Zero rework required after merge
- Complete audit trail of all decisions and changes
- Sarah focused on high-value decisions (design choices, testing strategy) rather than boilerplate code

**Pain Points Addressed:**
- Eliminated manual test writing and boilerplate code
- Automated coordination between CI/CD and testing
- Intelligent retry logic prevented escalation for minor failures
- Audit trail provides compliance documentation automatically

---

### Journey 2: Business Stakeholder - Audit and Compliance Review (Business Mode)

**Persona:** Marcus, Engineering Manager responsible for SOC2 compliance at a 50-person development team

**Scenario:** Marcus needs to demonstrate complete change control for an auditor reviewing Q4 deployments

**Journey Steps:**

1. **Audit Request**
   - Auditor asks: "Show me all changes to authentication system deployed in Q4"
   - Marcus opens Tamma observability dashboard
   - Filters events by: date range (Oct-Dec), subsystem (authentication), event type (deployments)

2. **Event Trail Review**
   - Tamma displays 23 authentication-related deployments in Q4
   - Marcus selects deployment #AUTH-847 (suspicious timing - 11pm on a Friday)
   - **Decision Point:** Drill down into specific deployment or review summary?
   - Marcus chooses: "Show complete event sequence"

3. **Black-Box Replay**
   - Tamma reconstructs complete system state at deployment time
   - Shows minute-by-minute event playback:
     - 10:47pm: Issue #847 assigned (emergency security patch for CVE-2024-1234)
     - 10:52pm: Tamma detected breaking change, escalated for manual approval
     - **Decision Point recorded:** Senior engineer Alex approved deployment (Business Mode required approval)
     - 10:58pm: Tamma ran full test suite (passed)
     - 11:03pm: Tamma deployed to staging, requested stakeholder approval
     - 11:15pm: Marcus (Engineering Manager) approved production deployment
     - 11:18pm: Deployed to production, rollback plan activated
   - Marcus exports audit log (JSON) showing complete attribution chain

4. **Compliance Verification**
   - Auditor asks: "Who approved this late-night deployment?"
   - Marcus shows event #AUTH-847-APPROVAL with millisecond timestamp, IP address, authentication method
   - **Compliance satisfied:** Clear approval chain, emergency justification documented, rollback plan in place

5. **Retrospective Analysis**
   - Marcus reviews all Q4 deployments
   - Tamma analytics show: 89% autonomous completion rate, 3% rework rate, zero deployments without approval in Business Mode
   - **Decision Point:** Adjust Business Mode gates or maintain current settings?
   - Marcus maintains current settings (proven effective)

**Success Outcomes:**
- Audit completed in 20 minutes (vs 2+ hours of manual log archaeology)
- 100% traceability with millisecond precision
- Clear approval chains for all sensitive changes
- Proactive compliance monitoring prevents issues

**Pain Points Addressed:**
- No more "who approved this?" mysteries
- Complete change control audit trail
- Fear of autonomous systems resolved through mandatory approval gates
- Time-travel debugging capability provides differential diagnosis

---

### Journey 3: Open-Source Maintainer - Contributor PR Review

**Persona:** Dev, maintainer of an open-source API framework with 200+ contributors, limited time for reviews

**Scenario:** Dev receives 5 PRs from external contributors on Monday morning, needs to review quality before maintainer attention

**Journey Steps:**

1. **PR Triage**
   - Tamma detects 5 new PRs from external contributors
   - Tamma automatically triggers quality gates for each PR
   - **Decision Point:** Review all PRs manually or let Tamma filter?
   - Dev chooses: "Run Tamma quality gates first"

2. **Automated Quality Enforcement**
   - PR #891 (from new contributor): Adds caching feature
     - Tamma runs build: ❌ FAILED (import statement missing)
     - Tamma retries 3 times, fails to auto-fix
     - Tamma posts comment: "Build failed due to missing import. Please add `import redis` to cache.py:3"
     - Status: **Escalated to contributor** (no maintainer time required yet)

   - PR #892 (from regular contributor): Fixes typo in docs
     - Tamma runs build: ✅ PASSED
     - Tamma runs tests: ✅ PASSED (no new tests required for docs)
     - Tamma security scan: ✅ PASSED
     - Tamma posts comment: "All checks passed! Ready for maintainer review."
     - Status: **Ready for merge** (1 minute maintainer time to approve)

   - PR #893 (from experienced contributor): Refactors authentication module
     - Tamma runs build: ✅ PASSED
     - Tamma detects breaking change: "Method signature changed for `authenticate()`"
     - **Decision Point:** Tamma escalates (breaking changes require manual approval)
     - Status: **Requires maintainer attention** (flagged as breaking change)

3. **Maintainer Focus**
   - Dev reviews Tamma summary:
     - 2 PRs auto-rejected (quality issues, escalated to contributors)
     - 1 PR ready for merge (no maintainer time needed)
     - 2 PRs require maintainer review (breaking changes or design decisions)
   - **Decision Point:** Which PRs need immediate attention?
   - Dev focuses on PR #893 (breaking change) and PR #894 (design question)

4. **Merge & Feedback Loop**
   - Dev approves PR #892 (docs typo) in 30 seconds - Tamma merges automatically
   - Dev reviews PR #893 breaking change, requests contributor to add migration guide
   - Tamma captures feedback and notifies contributor
   - Dev provides design feedback on PR #894
   - Tamma tracks all interactions in audit trail

**Success Outcomes:**
- Review time reduced from 2+ hours to 15 minutes of focused maintainer time
- 40% of PRs handled completely autonomously (passed quality gates, merged)
- 40% of PRs rejected with clear feedback (no maintainer time required)
- 20% of PRs require maintainer attention (complex decisions)
- Consistent quality standards enforced across all contributors
- Complete transparency - community can see all Tamma decisions and rationale

**Pain Points Addressed:**
- Review burden scaled beyond human capacity
- Consistent standards enforced automatically regardless of contributor skill
- Maintainer focuses on architecture and complex decisions, not boilerplate review
- Transparent audit trail builds community trust (all decisions documented)

---

## UX Design Principles

**Core UX Principles:**

1. **Radical Transparency** - Every action, decision, and state change must be visible and traceable. Users should never wonder "what is the system doing?" or "why did it make this choice?" Provide real-time status, decision rationale, and complete audit trails.

2. **Progressive Disclosure** - Start simple, reveal complexity only when needed. Dev Mode shows minimal friction with essential checkpoints. Business Mode adds layers of control and approval gates. Users choose their comfort level.

3. **Smart Friction** - Strategic checkpoints at critical decision points (design approval, breaking changes, production deployments) where human judgment adds value. Eliminate friction on routine tasks (tests, builds, linting) where automation excels.

4. **Contextual Adaptation** - System adapts UI complexity, automation level, and approval gates based on user role (developer vs stakeholder), mode (Dev vs Business), and environment (dev vs production). Same underlying system, different experience tailored to needs.

5. **Trustworthy Autonomy** - Build confidence through predictability. Show what the system will do before it acts. Provide "undo" and rollback capabilities. Never surprise users with unexpected changes.

6. **Error Recovery Focus** - When failures occur (build failures, test failures), provide actionable context: "What failed? Why? What are my options?" 3-retry logic with intelligent fixes before escalation. Users should feel supported, not abandoned.

---

## User Interface Design Goals

**Supported Platforms:**
- **CLI (Command-Line Interface)** - Primary interface for developers during active development workflow
- **Web Dashboard** - Secondary interface for monitoring, observability, and audit trail review
- **API** - For CI/CD integration and programmatic control

**Platform-Specific Goals:**

**CLI Interface:**
- **Real-time status streaming** - Show autonomous loop progress with live updates
- **Interactive prompts** - Clear decision points with numbered options and default suggestions
- **Rich terminal output** - Use colors, symbols, and formatting to convey status (✅ pass, ❌ fail, ⚠️ warning, 🔄 in progress)
- **Command autocomplete** - Discoverable commands with tab completion
- **Minimal cognitive load** - Single command to start autonomous loop, system handles coordination

**Web Dashboard:**
- **Real-time monitoring** - Live dashboards showing system health, development velocity, quality metrics
- **Event exploration** - Interactive timeline for event trail navigation with filtering and search
- **Black-box replay** - Visual playback of system state at any point in time
- **Responsive design** - Mobile-friendly for stakeholder reviews on-the-go
- **Dark mode** - Reduce eye strain for extended monitoring sessions

**Design Constraints:**

1. **Accessibility** - WCAG 2.1 AA compliance for web dashboard (keyboard navigation, screen reader support, sufficient color contrast)
2. **Performance** - Dashboard loads in <2 seconds, real-time updates within 500ms, log queries complete in <1 second
3. **Cross-Platform** - CLI works on Windows, macOS, Linux. Web dashboard supports modern browsers (Chrome, Firefox, Safari, Edge)
4. **Offline Resilience** - CLI gracefully handles network interruptions, queues operations for retry when connection restored
5. **Secure by Default** - All API endpoints require authentication, all data transmitted over HTTPS, sensitive information redacted from logs

**Visual Design Direction:**

- **Clean and minimal** - Focus on information hierarchy, avoid visual clutter
- **Status-driven color palette** - Green (success), Red (failure), Yellow (warning), Blue (in progress), Gray (pending)
- **Monospace fonts for code/logs** - Maintain readability and alignment
- **Card-based layouts** - Group related information (issue details, build status, test results) into scannable cards
- **Progressive enhancement** - Core functionality works without JavaScript, enhanced interactions when available

---

## Epic List

**Epic 1: Foundation & Core Infrastructure** (Weeks 0-2)
Establishes foundational architecture decisions and integration capabilities before feature development begins. This epic delivers AI provider abstraction layer, Gitea/Forgejo integration, and hybrid orchestrator/worker architecture definition.

**Value Delivered:** Multi-provider flexibility (no vendor lock-in), Gitea/Forgejo platform support, architectural foundation for autonomous loops

**Estimated Stories:** 8-10 stories (provider interface contracts, Gitea API integration, runner architecture design)

---

**Epic 2: Autonomous Development Loop - Core** (Weeks 2-4)
Implements the fundamental 14-step autonomous development loop with basic code generation, Git operations, and user approval checkpoints. This epic enables end-to-end issue → PR → merge workflow with simple retry logic.

**Value Delivered:** Basic autonomous development capability (issue selection, PR creation, code generation, merge operations, auto-next issue)

**Estimated Stories:** 10-12 stories (issue assignment, plan/design with approval, PR creation, TDD workflow, Git operations, completion checkpoint)

---

**Epic 3: Quality Gates & Intelligence Layer** (Weeks 5-7)
Adds build automation, test execution, CI/CD integration with 3-retry limits and mandatory escalation. Implements research capability, clarifying questions, ambiguity detection, and multi-option design proposals.

**Value Delivered:** Quality enforcement through automated gates (no bypass), intelligent handling of ambiguous requirements, prevention-first mindset

**Estimated Stories:** 8-10 stories (build automation with retry, test execution with retry, CI/CD integration, escalation workflow, research capability, clarifying questions, design options)

---

**Epic 4: Event Sourcing & Audit Trail** (Weeks 8-10)
Implements CQRS event sourcing for complete transparency and audit compliance. Captures all user actions, AI actions, and system state changes with millisecond precision. Enables black-box replay and time-travel debugging.

**Value Delivered:** Complete audit trail (100% traceability), compliance readiness (SOC2, ISO27001, GDPR), time-travel debugging, differential diagnosis

**Estimated Stories:** 7-9 stories (event schema design, event store backend, event versioning, capture all actions, black-box replay, audit log export)

---

**Epic 5: Observability & Production Readiness** (Weeks 11-15)
Adds structured logging, metrics collection, real-time monitoring dashboards, analytics tracking, alert system, feedback loops, and integration testing for production launch.

**Value Delivered:** System health visibility, development velocity tracking, user behavior insights, production monitoring, feedback capture, alpha release readiness

**Estimated Stories:** 9-11 stories (structured logging, metrics collection, real-time dashboards, analytics tracking, alert system, feedback loops, integration testing, documentation, alpha release)

---

**Epic Sequencing Rationale:**
- **Epic 1 establishes foundation:** Infrastructure and integrations must exist before autonomous loops can function
- **Epic 2 delivers core value:** Basic autonomous workflow enables immediate productivity gains
- **Epic 3 enhances quality:** Quality gates build on core loop, preventing issues before they occur
- **Epic 4 adds transparency:** Event sourcing captures loop operations, enabling audit and debugging
- **Epic 5 prepares for scale:** Observability and polish prepare system for production use and community feedback

**Total Estimated Stories:** 42-52 stories (slightly above Level 3 target of 15-40, but within acceptable range for comprehensive MVP)

> **Note:** Detailed epic breakdown with full story specifications is available in [epics.md](./epics.md)

---

## MVP Strategy & Bootstrap Approach

### Self-Maintenance Goal

**Primary MVP Success Criterion:** Tamma must be capable of maintaining its own codebase post-MVP, autonomously completing 70%+ of its own backlog stories (bug fixes, features, dependency updates) without human intervention. This validates production-readiness for mission-critical software.

**Validation Process:**
1. After MVP release, point Tamma at its own repository (github.com/tamma/tamma)
2. Assign Tamma stories from its own backlog (Epics 1-5 enhancements, bug fixes, post-alpha features)
3. Measure autonomous completion rate over 30 days
4. Success: ≥70% of assigned stories merged without manual intervention (only human approval checkpoints)

### Bootstrap Strategy

Tamma follows a **phased self-implementation approach** where humans build the minimal core, then Tamma completes its own implementation:

**Phase 0: Human Bootstrap (Weeks 0-2) - Epic 1**
- **Scope:** Humans implement 100% of Epic 1 (Foundation & Core Infrastructure)
- **Rationale:** Tamma cannot run without AI/Git abstractions and CLI scaffolding
- **Stories:** All 12 stories in Epic 1 (Stories 1-0 through 1-11)
- **Deliverable:** Working CLI that can execute autonomous loops on any repository

**Phase 1: Hybrid Implementation (Weeks 2-4) - Epic 2 Partial**
- **Scope:** Humans implement 50% of Epic 2 (Stories 2.1-2.6: issue selection, context analysis, plan generation, branch creation, test-first development, code generation)
- **Rationale:** Tamma needs basic autonomous loop to work on itself
- **Then:** Tamma implements remaining 50% of Epic 2 (Stories 2.7-2.11: refactoring, PR creation, PR monitoring, PR merge, auto-next issue)
- **Validation:** Tamma successfully merges its first self-implemented PR

**Phase 2: Self-Implementation (Weeks 4-6) - Epic 3**
- **Scope:** Tamma implements 80% of Epic 3 (Quality Gates & Intelligence)
- **Human Implementation:** Stories 3.1-3.2 (build automation, test execution) - required for Tamma to validate its own changes
- **Tamma Implementation:** Stories 3.3-3.9 (mandatory escalation, research capability, clarifying questions, ambiguity detection, multi-option proposals, static analysis, security scanning)
- **Validation:** Tamma's changes pass its own quality gates without manual intervention

**Phase 3: Self-Implementation (Weeks 6-8) - Epics 4-5**
- **Scope:** Tamma implements 90% of Epics 4-5 (Event Sourcing & Observability)
- **Human Implementation:** Story 5.1 (structured logging) - required for debugging Tamma's execution
- **Tamma Implementation:** All other stories (event capture, event query, black-box replay, metrics collection, basic alerts)
- **Validation:** Tamma can debug its own execution using event trail and logs

**MVP Completion Criteria:**
- ✅ All 5 epics implemented (combination of human + Tamma work)
- ✅ Tamma successfully implements 60-70% of its own codebase
- ✅ Tamma passes all quality gates on self-implemented code
- ✅ Tamma demonstrates self-maintenance on 10+ stories from its own backlog
- ✅ No critical bugs preventing autonomous loop execution
- ✅ Comprehensive test suite with ≥80% code coverage
- ✅ Documentation complete (setup, configuration, troubleshooting)

### Critical MVP Requirements (Cannot Be Deferred)

Based on self-maintenance goal, the following are **mandatory for MVP**:

1. **Epic 1 (All Stories)** - Foundation cannot be bypassed
2. **Epic 2 (All Stories)** - Core autonomous loop required for self-maintenance
3. **Epic 3 (All Stories)** - Quality gates prevent Tamma from breaking itself:
   - Build automation (Story 3.1) - Validates changes don't break build
   - Test execution (Story 3.2) - Validates changes don't break tests
   - Mandatory escalation (Story 3.3) - Prevents infinite loops when stuck
   - Research capability (Story 3.4) - Handles unfamiliar concepts in own codebase
   - Clarifying questions (Story 3.5) - Detects ambiguous requirements in own backlog
4. **Epic 4 (Core Stories)** - Event sourcing for debugging stuck workflows:
   - Stories 4.1-4.3 (event schema, event store, event capture) - Essential
   - Stories 4.4-4.8 (additional capture, query API, replay) - Optional for MVP
5. **Epic 5 (Logging Only, UI Optional)** - Observability for debugging:
   - Story 5.1 (structured logging) - **Essential** for debugging
   - Story 5.2 (metrics collection) - **Essential** for monitoring health
   - Stories 5.3-5.4 (dashboards) - **Optional** (CLI/log-based monitoring sufficient)
   - Story 5.6 (alert system) - **Partial** (email/Slack alerts sufficient, full dashboard integration optional)
   - Story 5.9 (documentation) - **Essential** for alpha release

### MVP vs Post-MVP Scope Clarification

**MVP (Required for Self-Maintenance):**
- All core autonomous loop features (Epics 1-3 complete)
- Event sourcing for debugging (Epic 4 core: Stories 4.1-4.3)
- Logging and basic alerts (Epic 5 partial: Stories 5.1, 5.2, 5.6 basic, 5.9)
- Error recovery and mandatory escalation (never get stuck, never crash)
- Comprehensive test suite (validates self-implemented changes)

**Post-MVP (Not Required for Self-Maintenance):**
- UI dashboards (Epic 5 Stories 5.3-5.4) - CLI/log-based monitoring sufficient
- Advanced analytics (Epic 5 Story 5.7) - User behavior tracking not critical
- Black-box replay full implementation (Epic 4 Stories 4.7-4.8) - Nice-to-have for advanced debugging
- Additional AI providers (Story 1-10) - Anthropic Claude sufficient for MVP, others add flexibility
- Additional Git platforms (Story 1-11) - GitHub + GitLab sufficient for MVP, others add flexibility

---

## Out of Scope

The following features are explicitly **deferred to future releases** to maintain focus on core MVP capabilities:

**1. Parallel Agent Orchestra**
Multiple Tamma instances working concurrently on independent issues with coordination logic.

**Rationale:** Complex coordination logic; MVP focuses on proving single-agent autonomous workflow first. Once core loop demonstrates 70%+ completion rate, parallel orchestration adds scale.

---

**2. Living Documentation Auto-Generation**
Automatic generation of Software Design Documents (SDDs), C4 diagrams, Entity-Relationship Diagrams (ERDs), and sequence diagrams.

**Rationale:** High value for maintenance but not critical for core autonomous loop functionality. Can be added iteratively as enhancement once autonomous development workflow is validated.

---

**3. Advanced Cost Intelligence**
Multi-cloud cost comparison, real-time budget tracking, cost-per-feature analysis, and ROI dashboards.

**Rationale:** MVP includes basic cost tracking through provider selection. Advanced cost analytics require extensive cloud provider integrations and historical data collection - better suited for post-MVP enhancement.

---

**4. Comprehensive Zero-Trust Security Layer**
File integrity monitoring everywhere, input validation on all endpoints, encryption by default for all data at rest and in transit, least-privilege access controls.

**Rationale:** MVP includes vulnerability scanning, security gates, and basic compliance features. Comprehensive zero-trust hardening is iterative security enhancement phase after core functionality is proven.

---

**5. No-Code Visual Layer**
Drag-and-drop feature builder, visual workflow composition, flowchart-based automation design.

**Rationale:** Target users are developers comfortable with CLI and code-based configuration. Non-developer abstraction layer represents future market expansion, not MVP requirement.

---

**6. Proactive Performance Monitoring**
Predict performance bottlenecks before they occur, auto-scaling recommendations, capacity planning automation.

**Rationale:** MVP focuses on functional correctness and basic observability. Performance optimization and predictive analytics require baseline data collection and usage patterns - better addressed post-launch.

---

**7. Cross-Platform Testing Orchestra**
Automated testing on all platforms simultaneously (Windows, macOS, Linux, mobile) with visual regression testing and screenshot comparison.

**Rationale:** MVP includes basic end-to-end testing on primary platform. Comprehensive cross-platform testing matrix requires significant infrastructure investment - phased approach post-MVP.

---

**8. AI-Driven UX Enhancement**
Analyze user behavior patterns, automatically suggest UX improvements, A/B test variations automatically, optimize workflows based on usage data.

**Rationale:** Requires significant user base and behavior data collection. MVP focuses on core workflow functionality. UX optimization based on real user feedback is post-MVP enhancement.

---

**9. Enterprise Features** (Open-Core Model)
SSO/LDAP integration, multi-tenancy with team isolation, priority support with SLA guarantees, advanced compliance reporting (SOC2/ISO27001 dashboards), role-based access control (RBAC) with fine-grained permissions.

**Rationale:** Core features (autonomous loop, event sourcing, observability) released open-source for community adoption. Enterprise features commercialized for revenue generation and business sustainability - developed based on enterprise customer feedback.
