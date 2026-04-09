# Tamma - Epic Breakdown

**Author:** meywd
**Date:** 2025-11-07
**Project Level:** 3
**Target Scale:** Complex System - 48-58 stories across 5 epics

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

**Value Delivered:** Multi-provider AI flexibility (8 providers: Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs), multi-platform Git support (7 platforms: GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git), architectural foundation for autonomous loops, initial web presence for community building.

**Estimated Stories:** 13 stories

**Technical Specification:** See `tech-spec-epic-1.md` for detailed implementation guidance.

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

### **Story 1-12: Initial Marketing Website (Cloudflare Workers)**

As a **project maintainer**,
I want an initial marketing website hosted on Cloudflare Workers,
So that early adopters can learn about Tamma and sign up for updates before the full documentation site launches.

**Acceptance Criteria:**

1. Static website hosted on Cloudflare Workers with custom domain (tamma.dev or similar)
2. Homepage includes: project name, tagline, key features overview, "Coming Soon" message
3. Email signup form for launch notifications (stores emails in Cloudflare KV or external service)
4. Link to GitHub repository for early access
5. Roadmap section showing Epic 1-5 timeline and MVP goals
6. "Why Tamma?" section explaining self-maintenance goal and multi-provider support
7. Responsive design (mobile, tablet, desktop) with fast load times (<1 second)
8. SEO optimization (meta tags, Open Graph, Twitter Cards)
9. Privacy policy and terms of service pages (basic)
10. Analytics integration (privacy-respecting: Cloudflare Web Analytics or Plausible)

**Prerequisites:** None (foundational marketing story, can be done early in Epic 1)

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

### **Story 1.13: Agent Customization System**

As a **Tamma system architect**,
I want to customize AI agents based on benchmark performance data,
So that I can optimize autonomous development for specific contexts and maximize success rates.

**Acceptance Criteria:**

1. Agent configuration management system with version control and rollback capabilities
2. Performance impact measurement for agent customizations across speed, quality, and cost
3. Cross-context agent capability testing (development vs code review vs testing scenarios)
4. Automated optimization recommendations based on Test Platform benchmark results
5. Integration with Test Platform's dual-purpose benchmarking system
6. Context window efficiency analysis and optimization recommendations
7. Privacy-preserving learning from customizations while protecting competitive advantages
8. A/B testing framework for agent configuration improvements

**Prerequisites:** Story 1.1 (AI provider interface), Story 1.2 (provider implementation), Story 1.10 (multiple providers)

---

### **Story 1.14: Performance Impact Analysis**

As a **Tamma system optimizer**,
I want to analyze how agent customizations affect development performance metrics,
So that I can make data-driven decisions about agent optimization and measure ROI of customizations.

**Acceptance Criteria:**

1. Comprehensive performance impact analysis across speed, quality, cost, and success rate metrics
2. Statistical significance testing for agent customization improvements
3. Context window efficiency measurement and optimization recommendations
4. Cross-agent comparison showing relative performance of customizations
5. Historical trend analysis for agent performance over time
6. Cost-benefit analysis for agent customizations vs. stock configurations
7. Automated insight generation identifying effective customization patterns
8. Integration with Test Platform's dual-purpose benchmarking results

**Prerequisites:** Story 1.13 (agent customization system), Story 1.10 (multiple providers for comparison)

---

## Epic 1.5: Deployment, Packaging & Operations (Weeks 2-3, Parallel with Epic 2)

**Goal:** Enable flexible deployment of Tamma across multiple hosting environments (CLI, service, web, container, cluster) and package for distribution via npm, binaries, and installers.

**Value Delivered:** Production-ready deployment options, npm package distribution, standalone binaries, webhook integration, system configuration management, enabling self-hosting and CI/CD integration.

**MVP Critical:** Stories 1.5-1 through 1.5-9 are **MVP CRITICAL** - Tamma cannot self-maintain without deployment infrastructure, webhooks for triggering, and packaging for distribution.

**MVP Optional:** Story 1.5-10 (Kubernetes deployment) deferred to post-MVP.

**Estimated Stories:** 15 stories (14 MVP critical, 1 optional)

---

### **Story 1.5-1: Core Engine Separation** ⭐ **MVP CRITICAL**

As a **system architect**,
I want the core orchestration engine separated from launch mechanisms,
So that Tamma can be deployed flexibly across CLI, service, web, and container environments.

**Acceptance Criteria:**

1. Core engine extracted into `@tamma/core` package (workflow, quality gates, providers)
2. Launch wrappers created: `@tamma/cli`, `@tamma/server`, `@tamma/worker`
3. Core engine has no dependencies on launch mechanism (HTTP server, CLI parsing)
4. Core engine exports: `TammaEngine` class, `WorkflowOrchestrator`, `QualityGates`
5. Launch wrappers are thin adapters (CLI parses args → calls core, server handles HTTP → calls core)
6. All existing tests pass after refactoring (no functionality changes)

**Prerequisites:** Story 1.9 (CLI scaffolding must exist to refactor)

---

### **Story 1.5-2: CLI Mode Enhancement** ⭐ **MVP CRITICAL**

As a **developer**,
I want enhanced CLI with interactive setup wizard and single-command execution,
So that I can quickly configure and run Tamma without manual config file editing.

**Acceptance Criteria:**

1. `tamma init` command launches interactive setup wizard (AI provider, Git platform, config file location)
2. `tamma run issue-123` executes autonomous loop for specific issue
3. `tamma config list` displays current configuration
4. `tamma config set key=value` updates configuration without editing files
5. `tamma logs` displays structured logs with filtering (level, timestamp, correlation ID)
6. `tamma status` shows orchestrator/worker status (running, stopped, jobs in queue)
7. All CLI commands include `--help` with usage examples

**Prerequisites:** Story 1.5-1 (core engine separation)

---

### **Story 1.5-3: Service Mode Implementation** ⭐ **MVP CRITICAL**

As a **system administrator**,
I want Tamma to run as a background service (daemon),
So that it can continuously monitor for new issues and execute autonomous loops without manual invocation.

**Acceptance Criteria:**

1. `tamma service install` installs system service (systemd on Linux, Windows Service on Windows, launchd on macOS)
2. `tamma service start|stop|restart|status` manages service lifecycle
3. Service runs as non-root user with appropriate permissions
4. Service logs to system journal (Linux: journalctl, Windows: Event Viewer, macOS: Console)
5. Service survives system reboot (auto-start enabled)
6. Service handles graceful shutdown (SIGTERM waits for current job completion, max 30s)
7. Service PID file prevents multiple instances (`/var/run/tamma.pid` or equivalent)

**Prerequisites:** Story 1.5-2 (CLI enhancement provides service commands)

---

### **Story 1.5-4: Web Server & API** ⭐ **MVP CRITICAL**

As a **DevOps engineer**,
I want a RESTful API for job submission and webhook receiver,
So that Tamma can be triggered remotely from CI/CD pipelines or Git platform webhooks.

**Acceptance Criteria:**

1. `tamma server` starts Fastify HTTP server on configurable port (default: 3000)
2. `POST /api/v1/jobs` creates new autonomous loop job (body: `{issueId, repositoryUrl}`)
3. `GET /api/v1/jobs/:id` returns job status (pending, running, completed, failed)
4. `POST /webhooks/github` receives GitHub webhooks (issue created, issue assigned)
5. `POST /webhooks/gitlab` receives GitLab webhooks (issue created, issue assigned)
6. `GET /health` returns health status (200 OK if healthy, 503 if degraded)
7. `GET /ready` returns readiness status (200 OK if ready to accept jobs)
8. JWT authentication required for `/api/v1/*` endpoints (configurable secret)
9. HMAC signature verification for webhooks (GitHub/GitLab secrets)

**Prerequisites:** Story 1.5-1 (core engine separation)

---

### **Story 1.5-5: Docker Packaging** ⭐ **MVP CRITICAL**

As a **DevOps engineer**,
I want Docker images and Docker Compose configuration,
So that I can deploy Tamma with containers alongside PostgreSQL and workers.

**Acceptance Criteria:**

1. Multi-stage Dockerfile builds optimized production image (Node.js Alpine base, <500MB)
2. Docker image published to Docker Hub (`tamma/tamma:latest`, `tamma/tamma:v0.1.0-alpha`)
3. Docker Compose file includes: orchestrator service, PostgreSQL service, worker service (3 replicas)
4. Environment variable configuration (AI_PROVIDER_KEY, DATABASE_URL, PORT, etc.)
5. Volume mounts for configuration persistence (`/etc/tamma/config.yaml`)
6. Health checks configured (HEALTHCHECK instruction in Dockerfile)
7. Restart policies configured (`restart: unless-stopped`)
8. Docker Compose `docker-compose up` starts full Tamma stack
9. CD workflow deploys to VPS via SSH after Docker images are built on `main` — syncs compose files, pulls GHCR images, runs migrations, health checks
10. Deploy supports manual trigger with environment selection (production/staging)

**Prerequisites:** Story 1.5-4 (web server for orchestrator)

---

### **Story 1.5-6: Webhook Integration** ⭐ **MVP CRITICAL**

As a **repository administrator**,
I want Tamma to automatically respond to GitHub/GitLab webhooks,
So that autonomous loops are triggered when issues are created or assigned without manual intervention.

**Acceptance Criteria:**

1. GitHub webhook verification (HMAC-SHA256 signature validation with shared secret)
2. GitLab webhook verification (secret token validation)
3. Event filtering: only process `issues.opened`, `issues.assigned` events (ignore others)
4. Webhook payload parsing: extract issue ID, repository URL, assignee
5. Automatic job creation: create autonomous loop job when issue assigned to Tamma bot account
6. Webhook retry handling: return 200 OK immediately, process async (avoid webhook timeout)
7. Webhook configuration UI or CLI command (`tamma webhook add github|gitlab --url --secret`)
8. Webhook event logging (all received webhooks logged with timestamp, event type, result)

**Prerequisites:** Story 1.5-4 (web server provides webhook endpoints)

---

### **Story 1.5-7: System Configuration Management** ⭐ **MVP CRITICAL**

As a **system administrator**,
I want unified configuration management across all deployment modes,
So that I can configure Tamma consistently whether running CLI, service, or container.

**Acceptance Criteria:**

1. Configuration file formats supported: YAML (preferred), JSON, TOML
2. Configuration file locations (priority order): `./tamma.yaml`, `~/.tamma/config.yaml`, `/etc/tamma/config.yaml`
3. Environment variable overrides (e.g., `TAMMA_AI_PROVIDER_KEY` overrides config file value)
4. Configuration schema with validation (JSON Schema or Zod validation)
5. Configuration migration tool for version upgrades (`tamma config migrate`)
6. Configuration includes: database connection, AI providers, Git platforms, orchestrator port, worker count, logging level
7. Sensitive values encrypted at rest (API keys encrypted with master key)
8. Configuration documentation with all available options

**Prerequisites:** Stories 1.3, 1.7 (provider/platform config must be integrated)

---

### **Story 1.5-8: NPM Package Publishing** ⭐ **MVP CRITICAL**

As a **developer**,
I want to install Tamma via npm,
So that I can quickly set up Tamma with a single command without manual compilation.

**Acceptance Criteria:**

1. `@tamma/cli` package published to npm registry (public)
2. `@tamma/core`, `@tamma/server`, `@tamma/worker` published as libraries
3. Semantic versioning strategy (e.g., v0.1.0-alpha, v0.2.0, v1.0.0)
4. Package.json with proper dependencies (production vs dev)
5. Installation via `npm install -g @tamma/cli` (global CLI)
6. Installation via `npm install @tamma/core` (library usage)
7. Package includes TypeScript type definitions (.d.ts files)
8. README with installation and usage instructions

**Prerequisites:** Story 1.5-1 (packages must be separated)

---

### **Story 1.5-9: Binary Releases & Installers** ⭐ **MVP CRITICAL**

As a **non-Node.js developer**,
I want standalone Tamma binaries and OS-specific installers,
So that I can install Tamma without Node.js runtime or npm.

**Acceptance Criteria:**

1. Standalone binaries built with pkg/nexe/esbuild (Windows .exe, macOS binary, Linux binary)
2. Binaries include bundled Node.js runtime (no external dependencies)
3. Installers created: Windows MSI (WiX Toolset), macOS DMG (create-dmg), Linux .deb/.rpm (fpm)
4. Installers add Tamma to PATH automatically
5. Auto-update mechanism (check for new versions on startup, prompt user)
6. Code signing: Windows Authenticode (optional for alpha, required for GA), macOS notarization (required)
7. GitHub Releases page with download links for all platforms
8. Installation instructions for each platform in documentation

**Prerequisites:** Story 1.5-8 (npm package must exist for binary bundling)

---

### **Story 1.5-10: Kubernetes Deployment** 🔵 **MVP OPTIONAL**

As a **platform engineer**,
I want Helm chart and Kubernetes manifests for Tamma,
So that I can deploy Tamma in a Kubernetes cluster with auto-scaling and high availability.

**Acceptance Criteria:**

1. Helm chart published to Helm repository (e.g., Artifact Hub)
2. Kubernetes manifests: Deployments (orchestrator, worker), Services (orchestrator API), ConfigMaps (config), Secrets (API keys)
3. Orchestrator StatefulSet with persistent volume for state
4. Worker HorizontalPodAutoscaler (scale based on queue depth: 1-10 replicas)
5. Ingress configuration for external access (with TLS/HTTPS)
6. PostgreSQL deployment (StatefulSet or external database reference)
7. Resource limits configured (CPU, memory requests and limits)
8. Liveness and readiness probes configured
9. Helm values.yaml with configurable options (replicas, resources, database URL)

**Prerequisites:** Story 1.5-5 (Docker images must exist)

---

### **Story 1.5-11: GitHub App Authentication & Installation Management** ⭐ **MVP CRITICAL (SaaS)**

As a **platform operator deploying Tamma as a SaaS service**,
I want GitHub App authentication with installation lifecycle management,
so that multiple organizations can install Tamma from the GitHub Marketplace without sharing personal access tokens.

**Acceptance Criteria:**

1. GitHub platform supports dual auth modes: PAT (existing) and GitHub App (new), selected via configuration
2. GitHub App auth generates JWT signed with RSA private key using `@octokit/auth-app`
3. Installation tokens auto-refreshed before expiration (configurable buffer, default 5 minutes)
4. Installation callback endpoint (`GET /api/github/callback`) handles post-install redirect and persists installation
5. Installation database schema stores: `installation_id`, `account_login`, `account_type`, `app_id`, `permissions`, `repository_selection`, timestamps
6. Private key provided via env var (`GITHUB_APP_PRIVATE_KEY`) or file path (`GITHUB_APP_PRIVATE_KEY_PATH`)
7. Platform factory creates correct platform instance based on auth mode
8. Unit tests cover JWT generation, token refresh, persistence, callback, config validation
9. Integration tests cover end-to-end installation callback flow
10. GitHub App config validated at startup with clear error messages for missing fields

**Prerequisites:** Story 1-5 (GitHub platform), Story 1.5-4 (web server for callback endpoint)

---

### **Story 1.5-12: SaaS Coordinator — Multi-Installation Engine Orchestration** ⭐ **MVP CRITICAL (SaaS)**

As a **SaaS platform operator**,
I want a coordinator that discovers GitHub App installations and dispatches work to GitHub Actions runners,
so that Tamma can serve multiple organizations from a single orchestrator instance.

**Acceptance Criteria:**

1. Coordinator discovers active installations from database on startup
2. Coordinator dispatches `workflow_dispatch` events to user repositories for GitHub Actions workers
3. Handles installation lifecycle: new → start engines, removed → prune engines, suspended → pause
4. Reconciliation loop runs on configurable interval (default 60s)
5. Uses installation-scoped tokens for per-installation API calls
6. Emits DCB events for lifecycle actions: `INSTALLATION.ENGINE_STARTED`, `ENGINE_STOPPED`, `SUSPENDED`, `REMOVED`
7. Graceful shutdown with 30s timeout for in-flight dispatches
8. Unit and integration tests cover discovery, dispatch, reconciliation, and lifecycle

**Prerequisites:** Story 1.5-11 (GitHub App auth and installation store)

---

### **Story 1.5-13: GitHub Actions Worker Mode** ⭐ **MVP CRITICAL (SaaS)**

As a **GitHub App user who has installed Tamma**,
I want autonomous development workflows to execute in my repository's GitHub Actions runners,
so that issue processing runs in my own CI/CD environment with proper isolation.

**Acceptance Criteria:**

1. GitHub Actions workflow template (`tamma.yml`) triggered via `workflow_dispatch` with inputs
2. Worker acquires installation token using `actions/create-github-app-token`
3. Worker executes `tamma processOneIssue --issue <number>` with installation token
4. Worker reports result to orchestrator via HTTP POST callback
5. Correct exit codes: `0` (success), `1` (failure), `78` (skip)
6. Workflow includes timeout (default 30m), concurrency control, error handling
7. Token re-acquisition for long-running jobs
8. Unit and integration tests cover worker entry point, callback, exit codes

**Prerequisites:** Story 1.5-12 (SaaS Coordinator dispatches work), Story 1.5-11 (App credentials)

---

### **Story 1.5-14: Multi-Tenant Task Queue & Webhook Routing** ⭐ **MVP CRITICAL (SaaS)**

As a **SaaS platform operator**,
I want webhook events and task queue entries tagged and routed by installation ID,
so that each GitHub App installation's work is isolated and correctly routed.

**Acceptance Criteria:**

1. All webhook events from GitHub App installations include `installation_id` in task payload
2. Task queue entries partitioned by `installation_id`
3. Webhook routing resolves `installation_id` → engine via coordinator's registry
4. Installation lookup < 10ms (cached)
5. Per-tenant task isolation: no cross-tenant task leakage
6. Task queue schema includes `installation_id` column with index
7. Backward compatible: self-hosted mode uses null `installation_id`
8. Unit and integration tests cover routing, isolation, and backward compatibility

**Prerequisites:** Story 1.5-6 (webhooks), Story 1.5-11 (installation store), Story 1.5-12 (engine registry)

---

### **Story 1.5-15: SaaS API Key Provisioning & GitHub Secrets Setup** ⭐ **MVP CRITICAL (SaaS)**

As a **user who has installed the Tamma GitHub App**,
I want Tamma to automatically provision an API key into my repository's GitHub Actions secrets,
so that my Actions workers can authenticate to the Tamma API for LLM access and workflow updates without any manual key configuration.

**Acceptance Criteria:**

1. Installation callback generates a per-installation API key (256-bit, base64url) and stores its SHA-256 hash
2. API key written as `TAMMA_API_KEY` to every repo's GitHub Actions secrets via libsodium sealed-box encryption
3. When repos are added to installation, `TAMMA_API_KEY` provisioned to new repos automatically
4. SaaS API endpoints authenticated by API key: LLM proxy (`/api/v1/llm/chat`), workflow status, result callback
5. LLM proxy uses SaaS operator's provider keys — users never need LLM API keys
6. API key rotation endpoint re-provisions all repo secrets and invalidates old key
7. GitHub App requires `secrets: write` permission
8. Unit and integration tests cover key generation, encryption, provisioning, auth, and LLM proxy

**Prerequisites:** Story 1.5-11 (GitHub App auth), Story 1.5-13 (Actions worker uses the key)

---

## Epic 2: Autonomous Development Loop - Core (Weeks 2-4)

**Goal:** Implement the fundamental 14-step autonomous development loop with basic code generation, Git operations, and user approval checkpoints.

**Value Delivered:** Basic autonomous development capability (issue selection, PR creation, code generation, merge operations, auto-next issue).

**Estimated Stories:** 16 stories

**Technical Specification:** See `tech-spec-epic-2.md` for detailed implementation guidance.

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

### **Story 2.12: Intelligent Provider Selection**

As a **system operator**,
I want Tamma to automatically select the optimal AI provider based on task type, cost, and availability,
so that development tasks are completed efficiently while staying within budget constraints.

**Acceptance Criteria:**

1. System analyzes task characteristics (code generation, review, research, testing) to determine optimal provider
2. Provider selection algorithm considers: task complexity, required capabilities, cost per token, response speed, current load
3. System maintains provider performance metrics (success rate, average response time, cost efficiency) for each task type
4. Fallback logic automatically switches providers when primary provider is unavailable or rate-limited
5. Cost-aware routing prioritizes cheaper providers for simple tasks, premium providers for complex tasks
6. Provider selection logged to event trail with rationale (why this provider was chosen)
7. Configuration allows override of automatic selection per task type or provider

**Prerequisites:** Story 1.1 (AI provider interface), Story 1.10 (multiple providers), Story 2.1 (issue selection)

---

### **Story 2.13: Prompt Engineering Optimization**

As a **system architect**,
I want Tamma to maintain and optimize prompt templates for different task types,
so that AI responses are consistently high-quality and task-appropriate.

**Acceptance Criteria:**

1. System maintains a library of optimized prompt templates for each task type (code generation, review, research, testing, refactoring)
2. Prompt templates include variable placeholders for context injection (issue details, code snippets, requirements)
3. System tracks prompt effectiveness metrics (success rate, revision count, user satisfaction) per template
4. A/B testing framework compares prompt variations and automatically selects best-performing templates
5. Prompt templates support versioning with rollback capability for degraded performance
6. Context window optimization ensures prompts fit within provider limits while maintaining effectiveness
7. System includes prompt engineering best practices (few-shot examples, chain-of-thought, role specification)
8. CLI commands allow prompt template inspection, testing, and manual optimization

**Prerequisites:** Story 1.1 (AI provider interface), Story 2.12 (provider selection), Story 2.3 (development planning)

---

### **Story 2.14: Issue Decomposition Engine**

As a **development team lead**,
I want Tamma to automatically break large issues into smaller, implementable tasks,
so that complex features can be developed incrementally with continuous integration and delivery.

**Acceptance Criteria:**

1. System analyzes issue complexity and determines when decomposition is needed (based on size, scope, dependencies)
2. Decomposition algorithm breaks issues into logical subtasks with clear acceptance criteria for each
3. Task dependencies identified and mapped (sequential, parallel, blocking relationships)
4. Each subtask sized appropriately (2-8 hours of work) with clear definition of done
5. Decomposition preserves original issue intent and business value
6. Subtasks linked to parent issue with traceability and rollup reporting
7. Human approval required before executing decomposed tasks, with ability to modify decomposition
8. System learns from decomposition patterns to improve future breakdown quality

**Prerequisites:** Story 2.2 (issue analysis), Story 2.3 (development planning), Story 3.6 (ambiguity detection)

---

### **Story 2.15: Task Dependency Mapping**

As a **project manager**,
I want Tamma to identify and manage dependencies between development tasks,
so that tasks are executed in the correct order and integration conflicts are avoided.

**Acceptance Criteria:**

1. System automatically detects dependencies between tasks (code dependencies, data model changes, API modifications)
2. Dependency types classified: blocking (must complete first), parallel (can run simultaneously), optional (nice to have)
3. Visual dependency graph shows task relationships and critical path analysis
4. Dependency validation ensures task prerequisites are met before execution begins
5. Circular dependency detection prevents infinite loops and deadlock situations
6. Impact analysis identifies downstream effects when tasks are modified or delayed
7. Dependency-aware scheduling optimizes task execution order for maximum parallelism
8. Dependency updates automatically propagate when tasks change scope or requirements

**Prerequisites:** Story 2.14 (issue decomposition), Story 2.3 (development planning), Story 3.4 (research capability)

---

### **Story 2.16: Incremental Task Sequencing**

As a **DevOps engineer**,
I want Tamma to sequence small tasks for continuous integration and delivery,
so that value is delivered incrementally with minimal integration risk.

**Acceptance Criteria:**

1. System creates optimal task execution sequences based on dependencies, risk, and value delivery
2. Incremental delivery strategy ensures each task provides measurable value when completed
3. Integration checkpoints validate that completed tasks work together before proceeding
4. Rollback capability exists for each incremental step to maintain system stability
5. Feature flags enable/disable completed tasks for controlled rollout
6. Continuous integration pipeline automatically tests each incremental task
7. Progress tracking shows cumulative value delivery and remaining work
8. Task sequencing adapts based on feedback and changing priorities

**Prerequisites:** Story 2.14 (issue decomposition), Story 2.15 (dependency mapping), Story 3.1 (build automation)

---

## Epic 3: Quality Gates & Intelligence Layer (Weeks 5-7)

**Goal:** Add build automation, test execution, CI/CD integration with 3-retry limits and mandatory escalation. Implement research capability, clarifying questions, ambiguity detection, and multi-option design proposals.

**Value Delivered:** Quality enforcement through automated gates (no bypass), intelligent handling of ambiguous requirements, prevention-first mindset.

**MVP Critical:** All stories in Epic 3 are required for MVP - quality gates prevent Tamma from breaking itself during self-maintenance, mandatory escalation ensures Tamma never gets stuck.

**Estimated Stories:** 12 stories (all MVP critical)

**Technical Specification:** See `tech-spec-epic-3.md` for detailed implementation guidance.

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

### **Story 3.10: Agent Performance Monitoring**

As a **system operator**,
I want to monitor AI agent performance metrics and response quality,
so that I can identify issues, optimize performance, and ensure consistent autonomous development quality.

**Acceptance Criteria:**

1. System tracks comprehensive performance metrics for each AI provider and task type combination
2. Metrics include: response time, success rate, token usage, cost per task, revision count, quality score
3. Real-time dashboard displays current performance with historical trends and alerts for anomalies
4. Performance baselines established per provider/task type with automatic deviation detection
5. Quality scoring system evaluates AI responses based on code quality, test coverage, and user feedback
6. Automated alerts trigger when performance degrades beyond thresholds (response time, success rate, cost)
7. Performance reports generated daily/weekly with insights and optimization recommendations
8. Historical performance data used to inform provider selection and prompt optimization

**Prerequisites:** Story 1.1 (AI provider interface), Story 2.12 (provider selection), Story 2.13 (prompt optimization), Story 5.2 (metrics collection)

---

### **Story 3.11: Cost-Aware AI Usage**

As a **project manager**,
I want Tamma to optimize AI usage to stay within budget constraints while maintaining development quality,
so that autonomous development remains cost-effective and predictable.

**Acceptance Criteria:**

1. System tracks AI costs in real-time with breakdown by provider, task type, and project
2. Budget management system supports daily, weekly, and monthly spending limits with configurable alerts
3. Cost optimization strategies automatically reduce usage when approaching budget limits (cheaper providers, fewer retries, simplified prompts)
4. Cost forecasting predicts future spending based on current usage patterns and upcoming tasks
5. Cost-benefit analysis evaluates whether AI usage for specific tasks provides sufficient value
6. Spending reports provide detailed breakdown with insights and cost-saving recommendations
7. Emergency cost controls can immediately halt AI usage when critical budget thresholds are exceeded
8. Cost optimization doesn't compromise critical quality gates (security, testing, code review)

**Prerequisites:** Story 1.1 (AI provider interface), Story 2.12 (provider selection), Story 3.10 (performance monitoring), Story 5.2 (metrics collection)

---

### **Story 3.12: Task Complexity Assessment**

As a **development team lead**,
I want Tamma to estimate task complexity and determine appropriate decomposition level,
so that tasks are sized optimally for autonomous development and reliable completion.

**Acceptance Criteria:**

1. System analyzes multiple complexity dimensions (technical difficulty, integration points, uncertainty, scope)
2. Complexity scoring algorithm provides quantitative assessment (0-100 scale) with qualitative descriptors
3. Historical accuracy tracking compares estimated vs actual complexity to improve predictions
4. Decomposition recommendations suggest optimal task breakdown based on complexity scores
5. Complexity factors identified and explained (why task is complex/simplistic)
6. Risk assessment identifies potential blockers and failure points for each complexity level
7. Time estimation correlates with complexity scores for planning and scheduling
8. Complexity assessment integrates with provider selection and resource allocation

**Prerequisites:** Story 2.14 (issue decomposition), Story 2.12 (provider selection), Story 3.6 (ambiguity detection)

---

## Epic 4: Event Sourcing & Audit Trail (Weeks 8-10)

**Goal:** Implement CQRS event sourcing for complete transparency and audit compliance. Capture all user actions, AI actions, and system state changes with millisecond precision.

**Value Delivered:** Complete audit trail (100% traceability), compliance readiness (SOC2, ISO27001, GDPR), time-travel debugging, differential diagnosis.

**Estimated Stories:** 8 stories

**Technical Specification:** See `tech-spec-epic-4.md` for detailed implementation guidance.

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

**Technical Specification:** See `tech-spec-epic-5.md` for detailed implementation guidance.

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

### **Story 5.9a: Installation & Setup Documentation** ⭐ **MVP CRITICAL**

As a **developer adopting Tamma**,
I want clear installation and setup documentation,
So that I can quickly install Tamma via npm, Docker, or binaries and complete first-time configuration.

**MVP Rationale:** Essential for alpha release - users cannot adopt Tamma without installation instructions.

**Acceptance Criteria:**

1. Installation via npm documented (`npm install -g @tamma/cli`, prerequisites, troubleshooting)
2. Installation via Docker documented (`docker run`, Docker Compose setup, volume mounts)
3. Installation via binaries documented (download, extract, PATH setup for Windows/macOS/Linux)
4. Service mode setup documented (systemd, Windows Service, launchd)
5. First-time configuration wizard walkthrough (`tamma init`)
6. Common installation errors documented with solutions

**Prerequisites:** Stories 1.5-5, 1.5-8, 1.5-9 (installation methods must exist)

---

### **Story 5.9b: Usage & Configuration Documentation** ⭐ **MVP CRITICAL**

As a **Tamma operator**,
I want comprehensive usage and configuration documentation,
So that I can configure AI providers, Git platforms, and operate Tamma effectively.

**MVP Rationale:** Essential for alpha release - users need configuration reference and usage examples.

**Acceptance Criteria:**

1. CLI command reference documented (all `tamma` commands with examples)
2. Configuration file reference documented (all options with examples)
3. AI provider setup guides (Anthropic, OpenAI, GitHub Copilot, local LLMs)
4. Git platform setup guides (GitHub, GitLab, webhooks)
5. Orchestrator mode vs worker mode explained with use cases
6. Webhook configuration documented
7. Environment variables documented

**Prerequisites:** Stories 1.3, 1.7, 1.5-2, 1.5-6 (config and CLI must exist)

---

### **Story 5.9c: API Reference Documentation** ⭐ **MVP CRITICAL**

As a **developer integrating with Tamma**,
I want API reference documentation,
So that I can programmatically interact with Tamma's REST API and webhooks.

**MVP Rationale:** Essential for CI/CD integration and webhook setup.

**Acceptance Criteria:**

1. REST API endpoints documented (`POST /api/v1/jobs`, `GET /api/v1/jobs/:id`, etc.)
2. Webhook payloads documented (GitHub, GitLab event formats)
3. Event schema documented (all event types with examples)
4. Metrics endpoint documented (`/metrics` Prometheus format)
5. Authentication documented (JWT tokens, API keys)
6. Error responses documented (status codes, error formats)
7. Code examples provided (curl, JavaScript, Python)

**Prerequisites:** Stories 1.5-4, 1.5-6, 4.1 (API and events must exist)

---

### **Story 5.9d: Full Documentation Website** ⭐ **MVP CRITICAL**

As a **Tamma community member**,
I want a comprehensive documentation website,
So that I can easily search and navigate all Tamma documentation.

**MVP Rationale:** Essential for alpha release - replaces "Coming Soon" marketing site with full docs.

**Acceptance Criteria:**

1. Documentation hosted on GitHub Pages or Cloudflare Pages
2. Searchable documentation (Algolia DocSearch or similar)
3. Navigation organized by sections (Getting Started, Configuration, API, Troubleshooting)
4. Architecture diagrams included (C4 model: context, containers, components)
5. Tutorials and guides (First Autonomous PR, CI/CD Integration, Self-Hosting)
6. Troubleshooting section (common errors, debug mode, log analysis)
7. Replaces Story 1-12 marketing site (updates domain to full docs site)
8. Documentation reviewed by external beta tester for clarity

**Prerequisites:** Stories 5.9a, 5.9b, 5.9c (content must exist), Story 1-12 (initial site exists)

---

### **Story 5.9e: Video Walkthrough** 🔵 **MVP OPTIONAL**

As a **new Tamma user**,
I want a video walkthrough demonstrating Tamma setup and usage,
So that I can learn Tamma quickly through visual demonstration.

**MVP Rationale:** Optional - written documentation sufficient for MVP. Video improves onboarding experience but not required for self-maintenance validation.

**Acceptance Criteria:**

1. Video created (5-10 minutes, high quality)
2. Video covers: installation, configuration, first autonomous PR
3. Video demonstrates self-maintenance goal (Tamma working on itself)
4. Video hosted on YouTube with unlisted or public link
5. Video embedded in documentation website
6. Transcript provided for accessibility

**Prerequisites:** Story 5.9d (documentation site for embedding)

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

## Epic 7: Autonomous Mentorship Workflow (Post-MVP)

**Goal:** Implement an AI-powered mentorship system that guides junior developers through story implementation using a 28-state workflow machine, providing skill assessment, guided implementation, blocker diagnosis, and progressive quality gates.

**Value Delivered:** Autonomous junior developer mentorship capability, skill progression tracking, adaptive guidance, teaching-focused code review, reduced onboarding time for new team members.

**Estimated Stories:** 10 stories

**Technical Specification:** See `docs/stories/epic-7/README.md` for detailed story breakdown.

---

### **Story 7-1: Mentorship State Machine Core**

As a **system architect**,
I want a 28-state workflow machine that orchestrates the entire mentorship lifecycle,
So that junior developer guidance follows a structured, predictable process with clear transitions and error handling.

**Acceptance Criteria:**

1. State machine defines all 28 states from UML specification (INIT_STORY_PROCESSING through STORY_COMPLETE) with typed state transitions
2. State machine supports event-driven transitions with guard conditions (e.g., correctUnderstanding, partialUnderstanding, misunderstanding, timeout)
3. State persistence enables session recovery after interruption without losing mentorship progress
4. Timeout handling triggers automatic transitions (e.g., ASSESS_JUNIOR_CAPABILITY → ESCALATE_DIFFICULTY on timeout)
5. State machine emits events for all transitions enabling observability and audit trail integration (Epic 4 compatible)
6. Circular transition detection prevents infinite loops in states like MONITOR_PROGRESS → DETECT_PATTERN → STRATEGIC_REDIRECT
7. Unit tests cover all 28 states, all valid transitions, and all guard conditions with >90% branch coverage

**Prerequisites:** Story 1.8 (architecture design), Story 2.3 (development plan generation)

---

### **Story 7-2: Skill Assessment Activity**

As a **engineering manager**,
I want the system to assess a junior developer's capability level before assigning mentorship tasks,
So that guidance is calibrated to the developer's current skill level and learning pace.

**Acceptance Criteria:**

1. Assessment activity evaluates developer skills across dimensions: language proficiency, framework knowledge, testing capability, Git workflow familiarity, and architecture understanding
2. Assessment generates a skill profile with scores (0-100) per dimension and an overall readiness score
3. Assessment adapts difficulty based on prior session history (returning developers skip basic questions)
4. Assessment results determine initial state transitions: correctUnderstanding → PLAN_DECOMPOSITION, partialUnderstanding → CLARIFY_REQUIREMENTS, misunderstanding → RE_EXPLAIN_STORY
5. Assessment timeout (configurable, default 10 minutes) triggers ESCALATE_DIFFICULTY transition with partial results preserved
6. Assessment results stored persistently and linked to developer profile for longitudinal tracking
7. Unit tests validate scoring algorithm accuracy and transition decision logic for all skill level combinations

**Prerequisites:** Story 7-1 (state machine must exist for transition integration)

---

### **Story 7-3: Context Gathering Activity**

As a **mentor AI agent**,
I want to gather comprehensive project and codebase context before guiding a junior developer,
So that mentorship guidance is grounded in the actual project conventions, patterns, and constraints.

**Acceptance Criteria:**

1. Context gathering loads project structure, coding conventions, test patterns, and dependency information from the target repository
2. Context includes recent commit history (last 20 commits) to understand current development velocity and patterns
3. Context identifies relevant documentation (README, CONTRIBUTING, architecture docs) and extracts key guidelines
4. Context maps the story requirements to specific files and modules that will need modification
5. Context gathering completes within 30 seconds for repositories up to 100K LOC
6. Context summary formatted for AI consumption includes: project overview, relevant file paths, coding standards, and test conventions
7. Integration test validates context accuracy against a known test repository with expected outputs

**Prerequisites:** Story 7-1 (state machine core), Story 2.2 (issue context analysis patterns)

---

### **Story 7-4: Claude Analysis Activity**

As a **mentor AI agent**,
I want AI-powered analysis of junior developer code submissions and plan proposals,
So that feedback is constructive, teaching-focused, and aligned with project standards.

**Acceptance Criteria:**

1. Analysis activity reviews code submissions against project conventions, test coverage, and acceptance criteria from the story
2. Analysis generates teaching-oriented feedback that explains the "why" behind suggestions, not just the "what"
3. Analysis detects common junior developer mistakes (off-by-one errors, missing edge cases, improper error handling) and provides educational explanations
4. Analysis supports plan assessment: evaluating whether a junior's proposed implementation plan is viable before they begin coding
5. Analysis integrates with the APPROVE_PLAN / REFINE_PLAN / CORRECT_PLAN state transitions based on plan quality scoring
6. Analysis response time under 15 seconds for typical code review (500 LOC or fewer)
7. Unit tests validate feedback quality using golden-file comparisons against known code submissions with expected feedback patterns

**Prerequisites:** Story 7-2 (skill assessment informs analysis depth), Story 7-3 (context gathering provides project standards)

---

### **Story 7-5: Plan Decomposition Activity**

As a **junior developer**,
I want the system to break down a story into small, guided implementation steps,
So that I can tackle the work incrementally without feeling overwhelmed by the full scope.

**Acceptance Criteria:**

1. Decomposition breaks a story into 3-10 sequential implementation steps, each completable in 15-45 minutes
2. Each step includes: clear objective, files to modify, hints for approach, and expected outcome (test to pass or behavior to verify)
3. Steps are ordered by dependency: foundational changes first, then features, then tests, then cleanup
4. Decomposition adapts granularity based on skill assessment results (more steps for lower skill levels, fewer for higher)
5. If junior proposes their own plan, system evaluates it and provides APPROVE_PLAN, REFINE_PLAN, or CORRECT_PLAN transition
6. Template plans (PROVIDE_TEMPLATE_PLAN state) generated automatically when junior cannot produce a viable plan after timeout
7. Unit tests validate decomposition produces valid step sequences for sample stories of varying complexity

**Prerequisites:** Story 7-2 (skill level determines granularity), Story 7-4 (AI analysis evaluates plans)

---

### **Story 7-6: Blocker Diagnosis & Resolution**

As a **junior developer**,
I want the system to diagnose why I am stuck and provide targeted help,
So that I can overcome blockers quickly without waiting for a human mentor to become available.

**Acceptance Criteria:**

1. Blocker diagnosis categorizes issues into: technicalError, conceptualConfusion, environmentIssue, analysisParalysis, and unknownIssue
2. Technical error diagnosis analyzes error output, stack traces, and recent code changes to suggest specific fixes
3. Conceptual confusion diagnosis identifies knowledge gaps and provides targeted explanations with code examples
4. Environment issue diagnosis checks common setup problems (missing dependencies, wrong versions, configuration errors) and provides fix steps
5. Analysis paralysis detection triggers BREAK_DOWN_TASK transition, decomposing the current step into smaller sub-steps
6. Diagnosis completes within 20 seconds and provides actionable next steps for all blocker categories
7. Unit tests validate diagnosis accuracy for each blocker category using representative error scenarios

**Prerequisites:** Story 7-1 (DIAGNOSE_BLOCKER state transitions), Story 7-4 (AI analysis for diagnosis)

---

### **Story 7-7: Mentorship Quality Gate**

As a **code reviewer**,
I want teaching-focused quality checks that evaluate both code correctness and learning outcomes,
So that junior developers produce quality code while also building understanding of why standards matter.

**Acceptance Criteria:**

1. Quality gate evaluates: code correctness, test coverage, adherence to project conventions, and documentation completeness
2. Quality gate provides educational feedback for each issue found, explaining the principle behind the standard (not just flagging violations)
3. Minor issues (formatting, naming) trigger AUTO_FIX_ISSUES state with automated corrections and explanations of what was changed
4. Major issues (logic errors, missing tests) trigger REQUIRE_FIXES state with guided remediation steps
5. Critical issues (security vulnerabilities, data loss risks) trigger BLOCK_PROGRESS state with mandatory human review
6. Quality gate tracks improvement: same issues flagged in subsequent reviews generate escalating guidance
7. Integration test validates quality gate correctly categorizes and routes issues across minor, major, and critical severity levels

**Prerequisites:** Story 7-4 (AI analysis powers quality feedback), Story 3.8 (static analysis integration)

---

### **Story 7-8: Skill Progress Tracking**

As a **engineering manager**,
I want to track junior developer skill progression over multiple mentorship sessions,
So that I can measure learning velocity, identify persistent skill gaps, and demonstrate team growth.

**Acceptance Criteria:**

1. Skill tracker records per-session metrics: time to complete, number of blockers, blocker categories, quality gate pass rate, and retry count
2. Skill progression calculated across dimensions (language, framework, testing, Git, architecture) using weighted moving average
3. Learning velocity metric shows rate of improvement per dimension with trend indicators (improving, plateauing, declining)
4. Persistent skill gaps identified when the same blocker category appears across 3+ sessions with targeted learning resource recommendations
5. Skill profile dashboard data exportable as JSON for integration with external HR/learning management systems
6. Anonymized aggregate analytics available for team-level skill distribution and growth trends
7. Unit tests validate progression calculations, trend detection, and gap identification algorithms with synthetic session data

**Prerequisites:** Story 7-2 (initial skill assessment provides baseline), Story 7-6 (blocker data feeds into tracking)

---

### **Story 7-9: Mentorship Session Management**

As a **junior developer**,
I want to start, pause, resume, and complete mentorship sessions with automatic state preservation,
So that I can work on mentored tasks across multiple sittings without losing progress.

**Acceptance Criteria:**

1. Session lifecycle supports: create, start, pause, resume, complete, and cancel operations with appropriate state transitions
2. Session state serialized and persisted on every state transition, enabling recovery within 5 seconds of session resume
3. Concurrent session limit enforced (configurable, default: 1 active session per developer) to maintain focus
4. Session timeout detection pauses inactive sessions after configurable idle period (default: 30 minutes) with notification
5. Session history maintained with full event trail for retrospective analysis and mentorship quality assessment
6. Session metadata includes: story reference, developer profile link, start time, elapsed active time, and current state
7. Integration test validates full session lifecycle including pause/resume across simulated process restarts

**Prerequisites:** Story 7-1 (state machine core for transitions), Story 4.2 (event store for session persistence)

---

### **Story 7-10: Mentorship Dashboard & Reporting**

As a **engineering manager**,
I want a monitoring dashboard and reports for mentorship program effectiveness,
So that I can track program health, identify at-risk developers, and demonstrate ROI of the mentorship system.

**Acceptance Criteria:**

1. Dashboard displays active mentorship sessions with current state, developer, story, elapsed time, and blocker status
2. Dashboard shows aggregate metrics: sessions completed this week, average completion time, quality gate pass rate, and escalation rate
3. Developer leaderboard (opt-in) shows skill progression rankings and achievement badges for completed milestones
4. At-risk developer alerts trigger when a developer has 3+ consecutive sessions with critical blockers or declining quality scores
5. Monthly report generation includes: sessions conducted, skills improved, common blockers, time savings vs. human mentorship baseline
6. Dashboard data served via REST API endpoints for integration with external dashboards (Grafana, Datadog)
7. Unit tests validate metric aggregation accuracy and alert threshold logic with synthetic dashboard data

**Prerequisites:** Story 7-8 (skill tracking provides metrics), Story 7-9 (session management provides session data), Story 5.3 (dashboard patterns)

---

## Epic 8: Distribution & Installation (Post-MVP)

**Goal:** Enable users to install and run Tamma without cloning the monorepo, providing three tiers of distribution: npm package, standalone binary, and Docker full-stack deployment.

**Value Delivered:** Zero-friction installation for all user types — developers (`npx`), end users (curl binary), and operators (Docker). Automated release pipelines, self-updating binaries, and one-command full-stack deployment.

**Estimated Stories:** 8 stories across 3 tiers

**Technical Specification:** See `docs/stories/epic-8/README.md` and `docs/architecture/installer-tier{1,2,3}-*.md` for detailed plans.

---

### **Story 8-1: esbuild Bundle & Package Structure**

As a **Tamma user**,
I want to install the CLI via `npx @tamma/cli init` without cloning the monorepo,
So that I can set up and run Tamma on any machine with just Node.js installed.

**Acceptance Criteria:**

1. esbuild bundles all 7 workspace packages into a single ESM file at `packages/cli/dist/index.js`
2. Third-party dependencies kept external and listed in published package's `dependencies`
3. `createRequire` pattern replaced with build-time `TAMMA_VERSION` constant via esbuild `define`
4. JSX transform works correctly for all Ink components
5. `prepare-package.mjs` generates publish-ready `package.json` replacing `workspace:*` references
6. Smoke test validates `--version`, `--help`, `init`, `start --help`
7. Bundle size under 500KB uncompressed JS
8. Existing `tsc --build` development workflow unchanged

**Prerequisites:** None (first story in Epic 8)

---

### **Story 8-2: npm Publish CI/CD Pipeline**

As a **release engineer**,
I want an automated pipeline that publishes `@tamma/cli` to npm on release tags,
So that new versions are available to users immediately after tagging.

**Acceptance Criteria:**

1. GitHub Actions workflow triggers on `cli-v*` tag push
2. Workflow: install → build → typecheck → test → bundle → smoke test → publish with provenance
3. Post-publish verification installs from npm and runs `npx @tamma/cli --version`
4. Bundle build added to existing CI so every PR validates the bundle
5. `NPM_TOKEN` secret configured; npm org `@tamma` created with public access
6. Release process documented: bump version → commit → tag → push

**Prerequisites:** Story 8-1

---

### **Story 8-3: Standalone Binary Compilation**

As a **Tamma user**,
I want a single standalone binary that runs without Node.js or any runtime dependencies,
So that I can install Tamma on any machine regardless of available development tools.

**Acceptance Criteria:**

1. `bun build --compile` produces executables for 4 platforms: darwin-arm64, darwin-x64, linux-x64, linux-arm64
2. Build script generates SHA256 checksums and `manifest.json`
3. Ink/React TUI renders correctly in compiled binary
4. Synchronous logger fallback replaces pino worker threads for binary mode
5. `isStandaloneBinary()` detection skips Node.js version check in preflight
6. Binary size under 60MB uncompressed
7. Smoke tests pass on each platform

**Prerequisites:** Story 8-1 (version injection pattern)

---

### **Story 8-4: Install Scripts & GitHub Releases**

As a **Tamma user**,
I want to install Tamma with `curl install.sh | bash` that downloads the correct binary for my platform,
So that I can get started without managing Node.js, npm, or other prerequisites.

**Acceptance Criteria:**

1. `install.sh` detects OS/arch, downloads binary, verifies SHA256, installs to `~/.local/bin/tamma`
2. Script supports proxy, version pinning, custom install dir, and `NO_COLOR`
3. `install.ps1` provides equivalent Windows PowerShell experience
4. GitHub Actions release workflow builds binaries on 4 platforms and creates GitHub Release
5. Post-release verification runs install script on CI matrix
6. `shellcheck` passes on `install.sh`

**Prerequisites:** Story 8-3

---

### **Story 8-5: Auto-Update & Package Manager Distribution**

As a **Tamma user**,
I want the CLI to check for updates and upgrade with `tamma upgrade`,
So that I always have the latest version without manual tracking.

**Acceptance Criteria:**

1. `tamma upgrade` downloads and atomically replaces current binary from GitHub Releases
2. Background update check (24h cooldown, 3s timeout) prints notification on startup
3. SHA256 verified before replacement
4. Homebrew tap (`meywd/homebrew-tap`) with auto-updating formula
5. `brew tap meywd/tap && brew install tamma` works

**Prerequisites:** Story 8-4

---

### **Story 8-6: TypeScript & Dashboard Dockerfiles**

As a **DevOps engineer**,
I want Dockerfiles for the TypeScript engine, API server, and dashboard,
So that the full Tamma platform can be deployed as containers.

**Acceptance Criteria:**

1. Multi-stage `Dockerfile.ts` builds both `tamma-api` and `tamma-engine` targets
2. `Dockerfile.dashboard` builds Vite SPA and serves via nginx with API proxy
3. All images use non-root users, tini as PID 1, Alpine base
4. Engine supports `--mode service` flag for headless container operation
5. Image sizes: TS < 300MB, Dashboard < 30MB

**Prerequisites:** None (parallel with Tier 1/2)

---

### **Story 8-7: Docker Compose Full Stack**

As a **platform operator**,
I want a Docker Compose configuration that runs the entire Tamma platform with `docker compose up -d`,
So that I can deploy and manage all 7 services as a cohesive unit.

**Acceptance Criteria:**

1. Compose defines: postgres, rabbitmq, elsa-server, tamma-api-dotnet, tamma-api, tamma-engine, tamma-dashboard
2. Health checks and startup ordering via `depends_on` conditions
3. Named volumes for persistent data; `.env.example` documents all variables
4. Dev/prod profiles via compose override files
5. All services healthy within 3 minutes; `_FILE` suffix for Docker secrets

**Prerequisites:** Story 8-6

---

### **Story 8-8: Docker CI/CD & CLI Integration**

As a **Tamma user**,
I want `tamma init --full-stack` to generate a ready-to-use Docker deployment,
So that I can deploy the full platform without cloning the repository.

**Acceptance Criteria:**

1. CI workflow builds and pushes all 5 images to GHCR on merge to main and version tags
2. Three tag forms: `latest`, semver, `sha-{commit}`
3. Smoke test workflow verifies full stack health in CI
4. `tamma init --full-stack` generates compose, .env, init-db.sql, nginx conf using pre-built images
5. Interactive prompt collects secrets and writes to `.env`

**Prerequisites:** Stories 8-6, 8-7

---

## Epic 27: Prompt Store — Multi-Tenant Prompt Management

**Goal:** Replace the file-based, single-tenant `PromptStore` with a PostgreSQL-backed, multi-tenant prompt management system that supports system defaults, account-level overrides, admin/account UIs, Elsa workflow integration, and full DCB event audit trail.

**Value Delivered:** Multi-tenant prompt isolation, two-tier resolution (account override then system default), platform admin control over 80+ system default templates, account admin self-service for customization, full audit trail via DCB events, Elsa workflow per-account prompt resolution.

**Estimated Stories:** 7 stories

**Detailed Story Documentation:** See `docs/stories/epic-27/README.md`

---

### **Story 27-1: Prompt Store Database Schema + Migration**

As a **platform engineer**,
I want PostgreSQL tables for storing prompt templates, system prompts, and action prompts with multi-tenant support,
So that prompts are persisted in the database with account-level isolation and two-tier fallback resolution.

**Acceptance Criteria:**

1. `prompts`, `system_prompts`, and `action_prompts` tables created with correct columns, types, and constraints
2. Partial unique indexes handle NULL `account_id` correctly for system defaults
3. Seed migration inserts 80 role+action templates, 8 system prompts, 10 action defaults from `default-prompts.ts`
4. All seed inserts use `ON CONFLICT DO NOTHING` for idempotency
5. B-tree indexes on lookup columns (`account_id`, `role`, `action`)

**Prerequisites:** Epic 17 (Story 17-1: tenants table must exist for FK references)

---

### **Story 27-2: Prompt Store Service (TypeScript)**

As a **backend developer**,
I want a PostgreSQL-backed `PromptStore` class that resolves prompts using account-level overrides with system default fallback,
So that each account gets its own prompt configuration while inheriting sensible defaults.

**Acceptance Criteria:**

1. `IPromptStore` interface defined with account-scoped methods: `get`, `upsert`, `delete`, `list`, `render`
2. `PgPromptStore` implementation resolves account override then system default
3. `InMemoryPromptStore` implementation for testing
4. `resetSystemDefault()` restores hardcoded defaults from `default-prompts.ts`
5. Backward compatible: existing callers without accountId resolve system defaults

**Prerequisites:** Story 27-1

---

### **Story 27-3: Prompt Store API Endpoints**

As an **API consumer**,
I want REST API endpoints for prompt CRUD with account-scoped resolution and platform admin access to system defaults,
So that prompts can be managed programmatically with proper authorization.

**Acceptance Criteria:**

1. Account-scoped routes: `GET/PUT/DELETE /api/prompts/:role/:action`, `GET /api/prompts`
2. System default routes: `GET/PUT/DELETE /api/prompts/system/:role/:action`, `GET /api/prompts/system`
3. Render endpoint: `POST /api/prompts/:role/:action/render` (account-aware)
4. Platform admin required for system default mutations; account admin for account overrides
5. Request body validation on all mutating endpoints

**Prerequisites:** Story 27-2

---

### **Story 27-4: Prompt Store Admin UI**

As a **platform administrator**,
I want an admin panel page for managing system default prompts,
So that I can view, edit, and reset the 80+ templates that ship with Tamma.

**Acceptance Criteria:**

1. Table of all system default prompts with role/action filtering and search
2. Edit dialog with template editor, variable list, system prompt, tools toggle, max tokens
3. Reset to hardcoded default button
4. Convention template preview and copy
5. Platform admin role required

**Prerequisites:** Story 27-3, Epic 16 (Story 16.3: Admin Dashboard)

---

### **Story 27-5: Prompt Store Account UI**

As an **account administrator**,
I want a page to manage my organization's prompt overrides,
So that I can customize how Tamma's AI agents behave for my team.

**Acceptance Criteria:**

1. Shows resolved prompts with account overrides visually highlighted
2. Override any prompt — saves to account bucket
3. Delete override — falls back to system default
4. Convention template selector for quick setup
5. Preview/test prompt rendering with sample variables
6. Override count indicator

**Prerequisites:** Story 27-3, Epic 16 (Story 16.1: OAuth, Story 16.5: RBAC)

---

### **Story 27-6: Elsa Workflow Integration**

As a **workflow engine developer**,
I want Elsa workflows to resolve prompts per-account from the PostgreSQL prompt store,
So that different organizations get their own customized prompt templates.

**Acceptance Criteria:**

1. `ResolvePromptFromRegistryActivity` accepts `AccountId` input and passes it to the render API
2. `LlmCallWorkflow` propagates `accountId` to prompt resolution
3. `SingleIssueCycleWorkflow` propagates `accountId` to all sub-workflow dispatches
4. Installation context maps `installation_id` to `tenant_id` to `accountId`
5. Backward compatible when accountId is not provided

**Prerequisites:** Story 27-2, Story 27-3, Epic 17 (Story 17-1: Tenant Model)

---

### **Story 27-7: Prompt Store Event Sourcing**

As a **compliance officer / platform operator**,
I want all prompt changes to emit DCB events with full audit metadata,
So that I can trace who changed which prompts, when, and why.

**Acceptance Criteria:**

1. Events emitted: `PROMPT.CREATED.SUCCESS`, `PROMPT.UPDATED.SUCCESS`, `PROMPT.DELETED.SUCCESS`, `PROMPT.RESET.SUCCESS`
2. Tags include: `accountId`, `role`, `action`, `userId`
3. Data includes: version numbers, changed fields (diff summary)
4. Best-effort emission (does not block prompt mutations)
5. Events queryable via existing event store API

**Prerequisites:** Story 27-2, Epic 4 (Event Sourcing)

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
