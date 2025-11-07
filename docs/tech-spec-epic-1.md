# Epic Technical Specification: Foundation & Core Infrastructure

Date: 2025-11-07
Author: meywd
Epic ID: 1
Status: Updated

---

## Overview

Epic 1 establishes the foundational infrastructure for Tamma's autonomous development orchestration platform. This epic implements the core abstraction layers that enable multi-provider AI interactions (addressing PRD requirements FR-7 to FR-9) and multi-platform Git operations (addressing FR-10 to FR-12), while establishing the hybrid orchestrator/worker architecture pattern (FR-13 to FR-15) that allows Tamma to operate both as a direct CLI tool and as a distributed system. By decoupling the platform from specific AI providers and Git platforms through well-defined interfaces, Epic 1 creates the extensibility foundation required for Tamma's vision of 70%+ autonomous issue completion across diverse technology stacks and organizational contexts.

This epic delivers the scaffolding that all subsequent development workflows will depend upon: the AI provider abstraction enables seamless switching between 8 AI providers (Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs); the Git platform abstraction supports 7 Git platforms (GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git) without workflow changes; and the hybrid architecture design ensures Tamma can scale from individual developer use to team-wide deployment while maintaining consistent behavior and auditability through the DCB event sourcing pattern.

## Objectives and Scope

**In Scope:**

- Story 1-0: AI Provider Strategy Research - cost analysis, capability matrix, provider selection recommendations
- Story 1-1: AI Provider Interface definition with provider lifecycle, streaming, context management
- Story 1-2: Anthropic Claude API provider implementation as reference and default (via SDK, headless)
- Story 1-3: Provider configuration management with environment variables, JSON files, and runtime switching
- Story 1-4: Git Platform Interface definition for repository, branch, PR, and issue operations
- Story 1-5: GitHub platform implementation with comprehensive API integration
- Story 1-6: GitLab platform implementation with API parity to GitHub
- Story 1-7: Git platform configuration management with credential handling and platform selection
- Story 1-8: Hybrid orchestrator/worker architecture design document and decision records
- Story 1-9: Basic CLI scaffolding with mode selection (orchestrator vs worker) and config initialization
- Story 1-10: Additional AI Provider Implementations - OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs (Ollama/LM Studio/vLLM)
- Story 1-11: Additional Git Platform Implementations - Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git
- Story 1-13: Agent Customization System - Customizable AI agents with performance optimization and A/B testing framework
- Story 1-14: Performance Impact Analysis - Comprehensive analysis of agent customizations on development metrics

**Out of Scope:**

- Issue selection and analysis workflows (Epic 2)
- Development plan generation and code implementation (Epic 2)
- Quality gates, testing automation, and intelligence layer (Epic 3)
- Event sourcing backend implementation and audit trail (Epic 4)
- Observability dashboards and production readiness (Epic 5)
- Advanced CLI features like interactive workflows, TUI dashboards (deferred per PRD Phase 2)

## System Architecture Alignment

Epic 1 directly implements the core architectural components defined in sections 4.3 (Project Structure) and 5.0 (Epic-to-Architecture Mapping). The `packages/providers` package will house the AI provider abstraction layer with its plugin architecture and sandboxing model, implementing the interface-based design pattern from section 3.1. The `packages/platforms` package delivers the Git platform abstraction following the same plugin patterns. The `packages/orchestrator` package establishes the hybrid architecture with event-driven communication and conditional trigger support as specified in section 3.3 (Novel Patterns). All implementations will use the TypeScript 5.7+ strict mode, Node.js 22 LTS runtime, and follow the pnpm workspace monorepo structure defined in section 2.1. The CLI scaffolding aligns with the Fastify-based orchestrator service design, ensuring consistent configuration management through the shared `packages/config` package.

## Detailed Design

### Services and Modules

**1. AI Provider Abstraction (`packages/providers`)**

_AI Provider Strategy Research (Story 1-0):_

- Research deliverable: `docs/research/ai-provider-strategy-2024-10.md`
- Cost analysis comparing 8+ AI providers: Anthropic Claude, OpenAI GPT, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local models (Ollama/LM Studio/vLLM)
- Pricing models: subscription plans (Anthropic Teams, OpenAI Teams, GitHub Copilot Business) vs pay-as-you-go API rates
- Capability matrix mapping providers to Tamma workflow steps: issue analysis, code generation, test generation, code review, refactoring, documentation
- Integration approaches: SDK/API (headless), IDE extensions, CLI tools, self-hosted models
- Deployment compatibility assessment: orchestrator mode, worker mode, CI/CD environments, developer workstations
- Recommendation matrix: Primary provider for MVP, secondary providers for specialized workflows, long-term multi-provider strategy
- Cost projections: 10/100/1000 user scenarios with monthly spend estimates
- Informs provider selection decisions for Stories 1-2 and 1-10

_Core Interfaces (Story 1-1):_

```typescript
interface IAIProvider {
  initialize(config: ProviderConfig): Promise<void>;
  sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>>;
  getCapabilities(): ProviderCapabilities;
  dispose(): Promise<void>;
}

interface MessageRequest {
  messages: Message[];
  systemPrompt?: string;
  tools?: Tool[];
  maxTokens?: number;
  temperature?: number;
  model?: string;
}

interface ProviderCapabilities {
  supportsStreaming: boolean;
  supportsTools: boolean;
  supportedModels: string[];
  maxContextTokens: number;
  maxOutputTokens: number;
}
```

_Anthropic Claude API Provider (Story 1-2):_

- Implementation class: `AnthropicClaudeProvider implements IAIProvider`
- Uses Anthropic Claude API via `@anthropic-ai/sdk` for programmatic/headless access (NOT Claude Code IDE tool)
- Streaming response handler with Server-Sent Events (SSE) chunk parsing
- Context management with conversation history
- Error handling for rate limits (429), network failures, token limits (400)
- Retry logic with exponential backoff for transient failures
- Telemetry hooks for latency, token usage, error rates
- Tool integration approach TBD by Story 1-0 research (API native tools vs MCP)

_Provider Configuration Service (Story 1-3):_

- `ProviderConfigManager` class for loading/validating provider configs
- Configuration sources: environment variables (`TAMMA_AI_PROVIDER`), JSON files (`~/.tamma/providers.json`), runtime API
- Provider registry pattern for dynamic provider discovery
- Validation against JSON Schema
- Hot-reload capability for config changes
- Secrets management integration for API keys

_Additional AI Provider Implementations (Story 1-10):_

- **OpenAI Provider**: `OpenAIProvider implements IAIProvider` - supports GPT-4, GPT-3.5-turbo, o1 models via `openai@^4.67.0` SDK
- **GitHub Copilot Provider**: `GitHubCopilotProvider implements IAIProvider` - integrates with Copilot API (research integration approach)
- **Google Gemini Provider**: `GeminiProvider implements IAIProvider` - supports Gemini Pro/Ultra via `@google/generative-ai@^0.21.0` SDK
- **OpenCode Provider**: `OpenCodeProvider implements IAIProvider` - integrates with OpenCode API (research API capabilities)
- **z.ai Provider**: `ZAIProvider implements IAIProvider` - integrates with z.ai API (research API capabilities)
- **Zen MCP Provider**: `ZenMCPProvider implements IAIProvider` - Model Context Protocol support via `@modelcontextprotocol/sdk@^1.0.0`
- **OpenRouter Provider**: `OpenRouterProvider implements IAIProvider` - multi-model routing aggregator (100+ models) via REST API
- **Local LLM Provider**: `LocalLLMProvider implements IAIProvider` - supports Ollama (HTTP API), LM Studio (OpenAI-compatible), vLLM (high performance)
- All providers follow same patterns: error handling, retry logic, streaming support, telemetry hooks
- Provider selection configurable per workflow step (cost optimization: cheap models for simple tasks, premium for complex)
- Integration tests validate each provider with real API calls (or mocked for local LLMs)
- Documentation includes provider comparison matrix (cost, speed, quality, features) and setup instructions

_Agent Customization System (Story 1-13):_

- `AgentConfigManager` class for managing AI agent configurations with version control
- Agent customization framework supporting prompt engineering, tool selection, and parameter tuning
- A/B testing system for comparing agent configurations across development scenarios
- Performance measurement integration with Test Platform benchmark results
- Context window optimization algorithms for efficient token utilization
- Privacy-preserving learning from agent performance while protecting competitive advantages
- Configuration validation and rollback capabilities for failed customizations
- Cross-context agent capability testing (development vs code review vs testing scenarios)

_Performance Impact Analysis (Story 1-14):_

- `PerformanceAnalyzer` class for comprehensive analysis of agent customizations
- Statistical significance testing framework for measuring improvement effectiveness
- Multi-dimensional performance tracking: speed, quality, cost, success rate metrics
- Historical trend analysis for agent performance over time with regression detection
- Cost-benefit analysis algorithms for customizations vs stock configurations
- Automated insight generation identifying effective customization patterns
- Cross-agent comparison system showing relative performance of different configurations
- Integration with Test Platform's dual-purpose benchmarking results for data-driven decisions

**2. Git Platform Abstraction (`packages/platforms`)**

_Core Interfaces (Story 1-4):_

```typescript
interface IGitPlatform {
  initialize(config: PlatformConfig): Promise<void>;

  // Repository operations
  getRepository(owner: string, repo: string): Promise<Repository>;
  listIssues(repo: Repository, filters: IssueFilters): Promise<Issue[]>;
  getIssue(repo: Repository, issueNumber: number): Promise<Issue>;

  // Branch operations
  createBranch(repo: Repository, branchName: string, fromRef: string): Promise<Branch>;
  getBranch(repo: Repository, branchName: string): Promise<Branch>;

  // Pull Request operations
  createPullRequest(repo: Repository, params: PRCreateParams): Promise<PullRequest>;
  getPullRequest(repo: Repository, prNumber: number): Promise<PullRequest>;
  updatePullRequest(pr: PullRequest, updates: PRUpdate): Promise<PullRequest>;
  mergePullRequest(pr: PullRequest, method: MergeMethod): Promise<MergeResult>;

  // Status checks
  getPRStatus(pr: PullRequest): Promise<PRStatus>;
  getChecks(pr: PullRequest): Promise<Check[]>;
}

interface Repository {
  owner: string;
  name: string;
  defaultBranch: string;
  cloneUrl: string;
  platform: 'github' | 'gitlab' | 'gitea' | 'forgejo' | 'bitbucket' | 'azure-devops' | 'plain-git';
}

interface Issue {
  number: number;
  title: string;
  body: string;
  state: 'open' | 'closed';
  labels: string[];
  assignees: string[];
  createdAt: Date;
  updatedAt: Date;
}
```

_GitHub Platform Implementation (Story 1-5):_

- `GitHubPlatform implements IGitPlatform`
- Uses Octokit SDK for GitHub REST API v4 and GraphQL API
- Pagination handling for large result sets
- Rate limit awareness with exponential backoff
- Webhook signature verification for event handling
- GitHub Actions integration for PR checks

_GitLab Platform Implementation (Story 1-6):_

- `GitLabPlatform implements IGitPlatform`
- Uses GitLab Node SDK for REST API v4
- API parity mapping: GitLab merge requests → PR abstraction
- GitLab CI/CD integration for pipeline status
- Group/subgroup namespace handling
- Self-hosted GitLab support via custom base URL

_Platform Configuration Service (Story 1-7):_

- `PlatformConfigManager` class for credential and platform management
- OAuth token storage with encryption at rest
- Personal Access Token (PAT) validation and refresh
- Platform selection logic based on repository URL patterns
- Multi-account support (e.g., personal GitHub + work GitLab)
- Credential rotation and expiry handling

_Additional Git Platform Implementations (Story 1-11):_

- **Gitea Provider**: `GiteaPlatform implements IGitPlatform` - REST API v1 (similar to GitHub API v3), self-hosted, Gitea Actions CI/CD
- **Forgejo Provider**: `ForgejoPlatform implements IGitPlatform` - Gitea-compatible API (forked from Gitea), may reuse GiteaPlatform client
- **Bitbucket Provider**: `BitbucketPlatform implements IGitPlatform` - Cloud (REST API v2) and Server (REST API 1.0-8.0), handles API differences between versions
- **Azure DevOps Provider**: `AzureDevOpsPlatform implements IGitPlatform` - REST API 7.1+ via `azure-devops-node-api@^13.0.0`, Work Items (not issues), PR completion (not merge)
- **Plain Git Provider**: `PlainGitProvider implements IGitPlatform` - local Git operations via `simple-git@^3.27.0`, NO platform features (no issues, PRs, CI/CD)
- All providers follow same patterns: error handling, retry logic, pagination support
- Platform selection configurable via config file or environment variables
- Integration tests validate each provider with real API calls (or local Git for plain Git)
- Documentation includes platform comparison matrix (features, API maturity, rate limits) and setup instructions

**3. Orchestrator/Worker Architecture (`packages/orchestrator`, `packages/worker`)**

_Hybrid Architecture Design (Story 1-8):_

**Orchestrator Mode:**

- Fastify HTTP server on configurable port (default 3000)
- RESTful API for workflow submission, status queries, cancellation
- WebSocket endpoint for real-time progress streaming
- Task queue manager using PostgreSQL for persistence
- Worker pool management with health checks
- Event bus integration for DCB event sourcing

**Worker Mode:**

- Stateless execution engine
- Polling mechanism for task queue (alternative: push via message broker)
- Local file system access for repository cloning
- AI provider and Git platform client initialization
- Result reporting back to orchestrator via HTTP callback
- Graceful shutdown handling for in-flight tasks

**Shared Components:**

- Configuration loader from `packages/config`
- Event emitter for audit trail generation
- Logging infrastructure with structured output
- Health check endpoints (orchestrator: `/health`, worker: internal)

**4. CLI Application (`packages/cli`)**

_Basic CLI Scaffolding (Story 1-9):_

```typescript
// CLI entry point
interface CLIConfig {
  mode: 'orchestrator' | 'worker' | 'standalone';
  configFile?: string;
  verbose?: boolean;
}

class TammaCLI {
  async run(args: string[]): Promise<number> {
    const config = this.parseArgs(args);
    await this.initializeConfig(config);

    if (config.mode === 'orchestrator') {
      return this.startOrchestrator();
    } else if (config.mode === 'worker') {
      return this.startWorker();
    } else {
      return this.runStandalone();
    }
  }
}
```

_Features:_

- Command parsing using commander.js
- Mode selection via `--mode` flag or `TAMMA_MODE` env var
- Config initialization wizard for first-time setup
- Validation of provider and platform credentials
- Version and help commands
- Exit code conventions (0=success, 1=error, 2=config error)

### Data Models and Contracts

**1. Provider Models**

```typescript
interface ProviderConfig {
  providerId: string;
  providerType:
    | 'anthropic-claude'
    | 'openai'
    | 'github-copilot'
    | 'gemini'
    | 'opencode'
    | 'zai'
    | 'zen-mcp'
    | 'openrouter'
    | 'local-llm';
  apiKey?: string;
  baseUrl?: string;
  model?: string;
  defaultParams?: {
    temperature?: number;
    maxTokens?: number;
  };
}

interface Message {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string;
  toolCallId?: string;
  toolCalls?: ToolCall[];
}

interface Tool {
  name: string;
  description: string;
  parameters: JSONSchema;
}

interface MessageChunk {
  type: 'text' | 'tool_call' | 'error';
  content: string;
  delta?: string;
  done: boolean;
}
```

**2. Platform Models**

```typescript
interface PlatformConfig {
  platformId: string;
  platformType:
    | 'github'
    | 'gitlab'
    | 'gitea'
    | 'forgejo'
    | 'bitbucket'
    | 'azure-devops'
    | 'plain-git';
  baseUrl?: string; // For self-hosted instances
  authType: 'oauth' | 'pat' | 'app' | 'ssh'; // ssh for plain-git
  credentials: {
    accessToken?: string;
    refreshToken?: string;
    expiresAt?: Date;
  };
}

interface PRCreateParams {
  sourceBranch: string;
  targetBranch: string;
  title: string;
  body: string;
  draft?: boolean;
  labels?: string[];
  reviewers?: string[];
}

interface PullRequest {
  number: number;
  title: string;
  body: string;
  state: 'open' | 'closed' | 'merged';
  sourceBranch: string;
  targetBranch: string;
  author: string;
  createdAt: Date;
  updatedAt: Date;
  mergedAt?: Date;
  url: string;
}

interface Check {
  name: string;
  status: 'pending' | 'success' | 'failure' | 'error';
  conclusion?: string;
  detailsUrl?: string;
}
```

**3. Orchestrator Models**

````typescript
interface WorkflowTask {
  taskId: string;
  workflowType: 'autonomous-dev' | 'manual-assist';
  issueReference: {
    platform: string;
    repository: string;
    issueNumber: number;
  };
  status: 'queued' | 'running' | 'paused' | 'completed' | 'failed' | 'cancelled';
  assignedWorker?: string;
  createdAt: Date;
  startedAt?: Date;
  completedAt?: Date;
  error?: ErrorDetail;
}

interface WorkerRegistration {
  workerId: string;
  hostname: string;
  capabilities: {
    aiProviders: string[];
    gitPlatforms: string[];
    maxConcurrentTasks: number;
  };
  status: 'idle' | 'busy' | 'offline';
  lastHeartbeat: Date;
}

**5. Agent Configuration Models**

```typescript
interface AgentConfig {
  agentId: string;
  name: string;
  version: string;
  provider: string;
  model?: string;
  systemPrompt: string;
  tools: Tool[];
  parameters: {
    temperature?: number;
    maxTokens?: number;
    topP?: number;
    frequencyPenalty?: number;
    presencePenalty?: number;
  };
  capabilities: AgentCapabilities;
  optimizationSettings: OptimizationSettings;
  createdAt: Date;
  updatedAt: Date;
}

interface AgentCapabilities {
  supportedTasks: ('code-generation' | 'code-review' | 'testing' | 'documentation' | 'refactoring')[];
  contextWindowTokens: number;
  maxOutputTokens: number;
  supportsStreaming: boolean;
  supportsTools: boolean;
  averageLatency: number; // milliseconds
  costPerToken: number; // USD
}

interface OptimizationSettings {
  enableABTesting: boolean;
  performanceThreshold: number; // minimum improvement percentage
  statisticalSignificance: number; // p-value threshold (0.05 default)
  contextOptimization: boolean;
  costOptimization: boolean;
  qualityOptimization: boolean;
}

interface PerformanceMetrics {
  agentId: string;
  taskId: string;
  taskType: string;
  startTime: Date;
  endTime: Date;
  duration: number; // milliseconds
  tokensUsed: number;
  cost: number; // USD
  quality: number; // 0-100 score
  success: boolean;
  errorType?: string;
  contextEfficiency: number; // tokens used / context window size
}

interface ABTestResult {
  testId: string;
  agentA: string;
  agentB: string;
  taskType: string;
  sampleSize: number;
  confidence: number;
  effectSize: number;
  winner: 'agentA' | 'agentB' | 'inconclusive';
  improvement: number; // percentage
  statisticalSignificance: boolean;
  createdAt: Date;
}
````

````

**4. Configuration Models**

```typescript
interface TammaConfig {
  version: string;
  mode: 'orchestrator' | 'worker' | 'standalone';

  providers: {
    default: string;
    available: ProviderConfig[];
  };

  platforms: {
    available: PlatformConfig[];
  };

  agents?: {
    default: string;
    available: AgentConfig[];
    optimizationEnabled: boolean;
    abTestingEnabled: boolean;
  };

  orchestrator?: {
    port: number;
    host: string;
    maxWorkers: number;
    taskQueueSize: number;
  };

  worker?: {
    orchestratorUrl: string;
    pollInterval: number;
    maxConcurrentTasks: number;
  };

  database?: {
    host: string;
    port: number;
    database: string;
    user: string;
    password: string;
  };
}
````

### APIs and Interfaces

**1. AI Provider Plugin API**

```typescript
// Provider registration
interface ProviderPlugin {
  name: string;
  version: string;
  createProvider(config: ProviderConfig): IAIProvider;
  validateConfig(config: ProviderConfig): ValidationResult;
}

// Provider registry
class ProviderRegistry {
  register(plugin: ProviderPlugin): void;
  getProvider(name: string): IAIProvider;
  listProviders(): string[];
}
```

**2. Git Platform Plugin API**

```typescript
// Platform registration
interface PlatformPlugin {
  name: string;
  version: string;
  createPlatform(config: PlatformConfig): IGitPlatform;
  validateConfig(config: PlatformConfig): ValidationResult;
  detectFromUrl(repoUrl: string): boolean;
}

// Platform registry
class PlatformRegistry {
  register(plugin: PlatformPlugin): void;
  getPlatform(name: string): IGitPlatform;
  detectPlatform(repoUrl: string): IGitPlatform;
  listPlatforms(): string[];
}
```

**3. Orchestrator REST API**

```
POST   /api/v1/tasks              # Submit new task
GET    /api/v1/tasks              # List all tasks
GET    /api/v1/tasks/:id          # Get task details
DELETE /api/v1/tasks/:id          # Cancel task
WS     /api/v1/tasks/:id/stream   # Stream task progress

POST   /api/v1/workers/register   # Worker registration
POST   /api/v1/workers/:id/heartbeat  # Worker heartbeat
GET    /api/v1/workers            # List registered workers

GET    /health                    # Health check
GET    /metrics                   # Prometheus metrics
```

**4. Configuration API**

```typescript
interface IConfigService {
  load(configPath: string): Promise<TammaConfig>;
  save(config: TammaConfig, configPath: string): Promise<void>;
  validate(config: TammaConfig): ValidationResult;
  merge(base: TammaConfig, override: Partial<TammaConfig>): TammaConfig;
}
```

**5. Agent Configuration API**

```typescript
interface IAgentConfigManager {
  createAgent(config: AgentConfig): Promise<string>;
  updateAgent(agentId: string, updates: Partial<AgentConfig>): Promise<void>;
  deleteAgent(agentId: string): Promise<void>;
  getAgent(agentId: string): Promise<AgentConfig>;
  listAgents(): Promise<AgentConfig[]>;
  rollbackAgent(agentId: string, targetVersion: string): Promise<void>;
  validateAgent(config: AgentConfig): ValidationResult;
}

// Performance Analysis API
interface IPerformanceAnalyzer {
  recordMetrics(metrics: PerformanceMetrics): Promise<void>;
  getPerformanceReport(agentId: string, timeRange: TimeRange): Promise<PerformanceReport>;
  compareAgents(agentA: string, agentB: string, timeRange: TimeRange): Promise<ComparisonResult>;
  runABTest(
    configA: AgentConfig,
    configB: AgentConfig,
    testPlan: ABTestPlan
  ): Promise<ABTestResult>;
  generateOptimizationRecommendations(agentId: string): Promise<OptimizationRecommendation[]>;
  analyzeTrends(agentId: string, timeRange: TimeRange): Promise<TrendAnalysis>;
}

interface PerformanceReport {
  agentId: string;
  timeRange: TimeRange;
  metrics: {
    averageDuration: number;
    averageCost: number;
    averageQuality: number;
    successRate: number;
    throughput: number; // tasks per hour
    contextEfficiency: number;
  };
  trends: TrendData[];
  comparisons: AgentComparison[];
  recommendations: OptimizationRecommendation[];
}

interface ABTestPlan {
  taskTypes: string[];
  sampleSize: number;
  confidenceLevel: number; // 0.95 default
  minimumEffectSize: number; // 0.05 default
  duration: number; // days
}
```

### Workflows and Sequencing

**1. Provider Initialization Sequence**

```
1. CLI startup
2. ConfigService.load() → read ~/.tamma/config.json
3. ProviderConfigManager.initialize(config.providers)
4. For each provider in config.providers.available:
   a. ProviderRegistry.register(provider)
   b. Validate provider credentials (dry-run test)
   c. Log success/failure
5. Set default provider from config.providers.default
6. Ready for AI operations
```

**2. Platform Initialization Sequence**

```
1. ConfigService.load() → read platform configs
2. PlatformConfigManager.initialize(config.platforms)
3. For each platform in config.platforms.available:
   a. PlatformRegistry.register(platform)
   b. Test authentication (fetch user profile)
   c. Cache platform capabilities
4. Ready for Git operations
```

**3. Orchestrator Startup Sequence**

```
1. Parse CLI args (mode=orchestrator)
2. Load TammaConfig
3. Initialize database connection pool (PostgreSQL)
4. Initialize provider and platform registries
5. Start Fastify server
   a. Register REST API routes
   b. Register WebSocket handlers
   c. Register health check endpoints
6. Initialize task queue manager
7. Start worker health check loop (every 30s)
8. Emit 'orchestrator.started' event
9. Log "Orchestrator listening on :3000"
```

**4. Worker Startup Sequence**

```
1. Parse CLI args (mode=worker)
2. Load TammaConfig
3. Initialize provider and platform registries
4. Register with orchestrator:
   POST /api/v1/workers/register
   Body: { workerId, hostname, capabilities }
5. Start heartbeat loop (every 10s)
6. Start task polling loop (every 5s):
   a. Request next task from orchestrator
   b. If task available, execute workflow
   c. Report progress via callbacks
   d. Report completion/failure
7. Log "Worker registered with orchestrator"
```

**5. Standalone Mode Sequence**

```
1. Parse CLI args (mode=standalone or default)
2. Load TammaConfig
3. Initialize provider and platform registries (in-process)
4. Execute workflow directly (no orchestrator communication)
5. All operations synchronous, blocking CLI until complete
6. Exit with appropriate status code
```

**6. Configuration Initialization Workflow**

```
User runs: tamma init

1. Check if ~/.tamma/config.json exists
   If yes: prompt "Config exists. Overwrite? (y/N)"
2. Interactive wizard:
   a. "Select mode: [orchestrator|worker|standalone]"
   b. If orchestrator:
      - Prompt database connection details
      - Prompt server port (default 3000)
   c. If worker:
      - Prompt orchestrator URL
      - Prompt max concurrent tasks
   d. "Configure AI provider: [claude-code|skip]"
      - If claude-code: prompt API key
   e. "Configure Git platform: [github|gitlab|both|skip]"
      - For each selected: prompt credentials
3. Validate all configs:
   - Test database connection (if orchestrator)
   - Test provider API keys
   - Test platform credentials
4. Write config to ~/.tamma/config.json
5. Set file permissions (600 for security)
6. Output: "Configuration saved to ~/.tamma/config.json"
```

**7. Agent Configuration Initialization Workflow**

```
User runs: tamma agent create --name "custom-reviewer" --provider claude

1. AgentConfigManager.validateAgent() against schema
2. Prompt user for customization options:
   a. "Select task types: [code-generation, code-review, testing, documentation]"
   b. "Set temperature (0.0-1.0): [default: 0.7]"
   c. "Enable A/B testing? [y/N]"
   d. "Set performance threshold (5-50%): [default: 10%]"
3. Create AgentConfig with user selections
4. AgentConfigManager.createAgent(config) → returns agentId
5. Initialize performance tracking for new agent
6. Output: "Agent 'custom-reviewer' created with ID: agent-uuid-v7"
```

**8. Performance Analysis Workflow**

```
System runs automated analysis (daily/weekly):

1. PerformanceAnalyzer.getPerformanceReport() for all active agents
2. Compare current week vs previous week metrics
3. Run statistical significance tests on performance changes
4. Generate optimization recommendations:
   a. Context window efficiency improvements
   b. Cost optimization opportunities
   c. Quality enhancement suggestions
5. For agents with A/B testing enabled:
   a. RunABTest() between current and recommended config
   b. Analyze results for statistical significance
   c. Apply winning configuration if improvement > threshold
6. Store analysis results in database
7. Emit 'performance.analysis.completed' event
8. Log summary of findings and actions taken
```

**9. A/B Testing Execution Workflow**

```
Trigger: Manual request or automated optimization

1. Validate ABTestPlan parameters (sample size, duration, confidence level)
2. Create two agent configurations: control (A) and variant (B)
3. Initialize task distribution: 50% to A, 50% to B
4. For each task in test period:
   a. Randomly assign to A or B
   b. Record PerformanceMetrics for both
   c. Track task completion and quality scores
5. After sample size reached:
   a. Calculate statistical significance using t-test
   b. Compute effect size and confidence intervals
   c. Determine winner based on primary metric (quality, cost, or speed)
6. Generate ABTestResult with detailed analysis
7. If winner has significant improvement:
   a. Prompt user for approval to apply winning config
   b. Auto-apply if improvement > threshold and auto-approve enabled
8. Archive test data for future reference
9. Emit 'abtest.completed' event with results
```

## Non-Functional Requirements

### Performance

**Provider Response Time:**

- AI provider message initialization: < 500ms (p95)
- Streaming message chunks: < 100ms between chunks (p95)
- Provider configuration load: < 200ms (p95)
- Provider registry lookup: < 10ms (p99)

**Platform API Performance:**

- Git platform API calls: < 1000ms for single operations (p95)
- Issue list operations: < 2000ms for up to 100 issues (p95)
- PR creation: < 3000ms including validation (p95)
- Platform configuration load: < 200ms (p95)
- Platform registry lookup: < 10ms (p99)

**Orchestrator Scalability:**

- Support minimum 10 concurrent workers
- Handle minimum 50 queued tasks
- Task queue operations: < 50ms (p95)
- Worker registration: < 100ms (p95)
- WebSocket message delivery: < 50ms (p95)
- Database connection pool: 10-50 connections

**CLI Startup:**

- Cold start (config load + validation): < 1000ms (p95)
- Warm start (cached config): < 300ms (p95)
- Config initialization wizard: Interactive (no timeout)

**Memory Constraints:**

- Standalone mode: < 256MB RSS
- Worker mode: < 512MB RSS
- Orchestrator mode: < 1GB RSS (base) + 100MB per worker
- Provider context caching: < 100MB per provider instance

**Throughput:**

- Orchestrator: 5 tasks/second submission rate
- Worker: 1 concurrent task execution (configurable up to 3)
- Provider streaming: 50 chunks/second minimum

### Security

**Credential Management:**

- All API keys and tokens encrypted at rest using AES-256
- Credentials stored in OS-specific secure storage:
  - Windows: Credential Manager
  - macOS: Keychain
  - Linux: Secret Service API (or encrypted file with restrictive permissions)
- Config files with credentials: chmod 600 (owner read/write only)
- No credentials in logs, error messages, or debug output
- Credential rotation support with zero-downtime

**Authentication & Authorization:**

- Provider API keys validated on initialization
- Platform OAuth2 tokens with refresh token support
- Platform PATs validated via test API call on load
- Worker-to-orchestrator authentication via shared secret (JWT)
- Orchestrator API authentication: Bearer token required for all endpoints except `/health`

**Network Security:**

- All provider API calls over HTTPS/TLS 1.3+
- All platform API calls over HTTPS/TLS 1.3+
- Orchestrator-worker communication over HTTPS (or mTLS for production)
- Certificate validation enabled (no insecure SSL)
- Webhook signature verification for platform events

**Input Validation:**

- All user inputs sanitized against injection attacks
- Provider messages validated against schema before sending
- Platform API parameters validated before API calls
- Configuration files validated against JSON Schema
- File path inputs validated to prevent directory traversal

**Dependency Security:**

- All npm dependencies scanned with `npm audit` in CI
- Critical vulnerabilities: block PR merge
- High vulnerabilities: require justification + timeline for fix
- Automated dependency updates via Dependabot/Renovate
- Pinned dependencies in package-lock.json

**Secrets in Code:**

- No hardcoded secrets, API keys, or credentials
- Environment variable validation: reject if secrets detected in public env vars
- Pre-commit hooks to scan for accidentally committed secrets
- GitHub secret scanning enabled on repository

**Plugin Sandboxing:**

- Provider plugins: NO filesystem access beyond config
- Platform plugins: READ-ONLY filesystem access for Git operations
- Plugin resource limits: CPU 80%, memory 256MB per plugin
- Plugin execution timeout: 30s for initialization, 5min for operations
- Plugin crash isolation: does not crash main process

### Reliability/Availability

**Error Handling:**

- All async operations wrapped in try-catch with proper logging
- Provider API failures: retry with exponential backoff (3 attempts, 1s/2s/4s)
- Platform API failures: retry with exponential backoff (3 attempts, 2s/4s/8s)
- Rate limit handling: respect `Retry-After` headers, backoff up to 60s
- Graceful degradation: if provider unavailable, return user-friendly error

**Resilience Patterns:**

- Circuit breaker for provider API calls (5 failures in 60s → open for 300s)
- Circuit breaker for platform API calls (5 failures in 60s → open for 300s)
- Timeout configuration for all network calls (default 30s, configurable)
- Dead letter queue for failed tasks in orchestrator mode
- Worker failure detection via heartbeat timeout (3 missed = offline)

**Data Durability:**

- Orchestrator task queue persisted to PostgreSQL
- Task state updates atomic (no partial updates)
- Configuration file writes atomic (write to temp → rename)
- Database migrations with rollback capability
- No data loss on orchestrator restart (tasks resume from queue)

**Startup Validation:**

- Config validation on startup: fail fast if invalid
- Database connectivity test on orchestrator startup
- Provider credential test on initialization (can be skipped with `--skip-validation`)
- Platform credential test on initialization (can be skipped with `--skip-validation`)
- Clear error messages for misconfiguration

**Graceful Shutdown:**

- SIGTERM/SIGINT handlers for all modes
- Orchestrator: wait for in-flight tasks (up to 30s), then force shutdown
- Worker: complete current task (up to 5min), then shutdown
- Standalone: immediate shutdown after current operation
- Database connections closed properly
- Event emission for shutdown events

**Availability Targets:**

- Orchestrator uptime: 99.5% (allows 3.65 hours/month downtime)
- Worker availability: best effort (stateless, auto-restart on failure)
- Database availability: 99.9% (managed PostgreSQL service recommended)
- Configuration: degraded mode if config unavailable (use defaults, log warning)

**Monitoring Hooks:**

- Health check endpoints return 200 OK if healthy, 503 if degraded
- Health checks include: database connectivity, provider reachability, worker heartbeats
- Prometheus metrics endpoint: `/metrics` (orchestrator only)
- Structured logging with correlation IDs for request tracing

### Observability

**Logging:**

- Structured JSON logs using pino logger
- Log levels: trace, debug, info, warn, error, fatal
- Default log level: info (configurable via `TAMMA_LOG_LEVEL`)
- Log rotation: daily, max 7 days retention for file logs
- Correlation IDs: propagate through all operations for tracing
- Sensitive data redaction: automatically redact API keys, tokens, passwords

**Log Events:**

- Provider operations: initialization, message sent, message received, error, dispose
- Platform operations: API call start, API call success, API call failure, rate limit
- Orchestrator: task submitted, task assigned, task completed, task failed, worker registered, worker offline
- Worker: heartbeat, task received, task started, task completed, task failed
- Configuration: config loaded, config validated, config invalid
- CLI: command invoked, mode selected, initialization complete

**Metrics (Prometheus):**

- Counter: `tamma_provider_messages_total{provider, status}`
- Histogram: `tamma_provider_latency_seconds{provider, operation}`
- Counter: `tamma_platform_api_calls_total{platform, operation, status}`
- Histogram: `tamma_platform_api_latency_seconds{platform, operation}`
- Gauge: `tamma_orchestrator_workers_registered`
- Gauge: `tamma_orchestrator_workers_active`
- Gauge: `tamma_orchestrator_tasks_queued`
- Counter: `tamma_orchestrator_tasks_total{status}`
- Histogram: `tamma_orchestrator_task_duration_seconds{status}`
- Gauge: `tamma_worker_current_task{worker_id}`
- Counter: `tamma_config_loads_total{status}`

**Tracing:**

- OpenTelemetry instrumentation (optional, enabled via env var)
- Trace spans for: provider calls, platform API calls, task execution, workflow steps
- Span attributes: operation name, provider/platform ID, task ID, status, error
- Trace propagation: W3C Trace Context headers
- Export to OTLP endpoint (configurable, default: none)

**Error Tracking:**

- Error context includes: stack trace, correlation ID, operation name, user inputs (sanitized)
- Error aggregation: group by error type and stack trace fingerprint
- Integration hooks for Sentry, Bugsnag, or custom error tracking
- Error rate monitoring: alert if error rate > 5% over 5min window

**Debug Capabilities:**

- Debug mode: `TAMMA_DEBUG=1` or `--debug` flag
- Verbose mode: `--verbose` flag for CLI output
- Dry-run mode: `--dry-run` for operations without side effects
- Request/response logging: full payload logged at trace level
- Config dump: `tamma config show` command to display active config (redacted)

**Audit Trail:**

- All operations emit events for DCB event sourcing (Epic 4 dependency)
- Event schema: `{ eventType, timestamp, actor, operation, target, status, metadata }`
- Events persisted for compliance and debugging
- Event replay capability for debugging workflow issues

## Dependencies and Integrations

### External Dependencies

**Core Runtime:**

- Node.js 22 LTS (≥22.0.0): JavaScript runtime
- TypeScript 5.7+ (≥5.7.0): Type system and compiler
- pnpm 9+ (≥9.0.0): Package manager and workspace tool

**AI Provider SDKs:**

- `@anthropic-ai/sdk` (^0.20.0): Anthropic Claude API client
- `@modelcontextprotocol/sdk` (^1.0.0): MCP protocol for tool integration

**Git Platform SDKs:**

- `@octokit/rest` (^20.0.0): GitHub REST API client
- `@octokit/graphql` (^7.0.0): GitHub GraphQL API client
- `@gitbeaker/node` (^38.0.0): GitLab API client

**Web Framework & Server:**

- `fastify` (^4.26.0): High-performance web framework
- `@fastify/websocket` (^10.0.0): WebSocket support for Fastify
- `@fastify/cors` (^9.0.0): CORS handling

**Database:**

- `pg` (^8.11.0): PostgreSQL client
- `knex` (^3.1.0): SQL query builder and migration tool

**CLI & Configuration:**

- `commander` (^12.0.0): CLI argument parsing
- `inquirer` (^9.2.0): Interactive CLI prompts
- `cosmiconfig` (^9.0.0): Configuration file discovery and loading
- `ajv` (^8.12.0): JSON Schema validator

**Logging & Monitoring:**

- `pino` (^8.19.0): Fast JSON logger
- `pino-pretty` (^11.0.0): Pretty print for development
- `prom-client` (^15.1.0): Prometheus metrics

**Observability (Optional):**

- `@opentelemetry/api` (^1.8.0): OpenTelemetry API
- `@opentelemetry/sdk-node` (^0.48.0): OpenTelemetry SDK
- `@opentelemetry/exporter-trace-otlp-http` (^0.48.0): OTLP exporter

**Security & Crypto:**

- `keytar` (^7.9.0): OS keychain/credential manager access
- `crypto` (built-in): Encryption utilities

**Utilities:**

- `zod` (^3.22.0): Runtime type validation
- `date-fns` (^3.3.0): Date manipulation
- `uuid` (^9.0.0): UUID generation

### Internal Dependencies

**Workspace Packages (pnpm workspace):**

- `@tamma/config`: Shared configuration management
- `@tamma/types`: Shared TypeScript types and interfaces
- `@tamma/logger`: Shared logging infrastructure
- `@tamma/events`: Event emitter for DCB event sourcing
- `@tamma/providers`: AI provider abstraction (Epic 1 deliverable)
- `@tamma/platforms`: Git platform abstraction (Epic 1 deliverable)
- `@tamma/orchestrator`: Orchestrator service (Epic 1 deliverable)
- `@tamma/worker`: Worker service (Epic 1 deliverable)
- `@tamma/cli`: CLI application (Epic 1 deliverable)

### External Service Integrations

**AI Provider Services:**

- Anthropic Claude API (api.anthropic.com): Claude Code provider
  - Authentication: API key via header
  - Rate limits: Tier-dependent (1000-10000 RPM)
  - Endpoints: `/v1/messages` (streaming), `/v1/complete`

**Git Platform Services:**

- GitHub API (api.github.com): Issue tracking, PR management, repository operations
  - Authentication: Personal Access Token, OAuth App, or GitHub App
  - Rate limits: 5000 requests/hour (authenticated), 60 requests/hour (unauthenticated)
  - GraphQL: Higher rate limits, more efficient for complex queries

- GitLab API (gitlab.com or self-hosted): Issue tracking, merge request management, repository operations
  - Authentication: Personal Access Token or OAuth2
  - Rate limits: 300 requests/minute per user, 2000 requests/minute per IP
  - Self-hosted: Custom base URL support

**Database:**

- PostgreSQL 17: Task queue persistence, worker registry, configuration cache
  - Connection: Standard PostgreSQL wire protocol
  - Authentication: Username/password or peer authentication
  - Schema: Managed by Knex migrations
  - Access: Single application-level user with full schema privileges

### Integration Points

**1. Provider Integration (Epic 1 ← AI Services)**

```
packages/providers → Anthropic API
├── Authentication: API key in Authorization header
├── Request: POST /v1/messages with streaming
├── Response: Server-Sent Events (SSE) stream
└── Error handling: Retry on 429, 500-503; Fail on 4xx
```

**2. Platform Integration (Epic 1 ← Git Services)**

```
packages/platforms → GitHub/GitLab API
├── GitHub REST: CRUD operations for issues, PRs, branches
├── GitHub GraphQL: Efficient multi-resource queries
├── GitLab REST: CRUD operations for issues, MRs, branches
├── Webhooks: Incoming events for PR status, CI checks
└── Error handling: Retry on rate limits, network errors
```

**3. Orchestrator-Worker Integration (Epic 1 internal)**

```
packages/orchestrator ↔ packages/worker
├── Worker Registration: POST /api/v1/workers/register
├── Heartbeat: POST /api/v1/workers/:id/heartbeat (every 10s)
├── Task Assignment: Worker polls GET /api/v1/tasks/next
├── Progress Updates: Worker callbacks to orchestrator
└── Task Completion: POST /api/v1/tasks/:id/complete
```

**4. CLI-Orchestrator Integration (Epic 1 internal)**

```
packages/cli → packages/orchestrator
├── Standalone mode: Direct in-process calls (no HTTP)
├── Worker mode: HTTP client to remote orchestrator
├── Config sharing: Shared @tamma/config package
└── Event emission: Shared @tamma/events for audit trail
```

**5. Database Integration (Epic 1 → PostgreSQL)**

```
packages/orchestrator → PostgreSQL
├── Task Queue: tasks table with status, assignments, metadata
├── Worker Registry: workers table with capabilities, heartbeats
├── Config Cache: configs table (optional, for shared config)
└── Migrations: Knex migration files in packages/orchestrator/migrations
```

### Cross-Epic Dependencies

**Epic 1 Provides Foundation for:**

- Epic 2 (Autonomous Dev Loop): Requires AI provider abstraction, Git platform abstraction, orchestrator/worker architecture
- Epic 3 (Quality Gates): Requires platform integration for PR checks, test execution
- Epic 4 (Event Sourcing): Requires event emission hooks from all Epic 1 components
- Epic 5 (Observability): Requires logging, metrics, health check infrastructure

**Epic 1 Dependencies on Future Epics:**

- Epic 4 (Event Store Backend): Epic 1 emits events but doesn't persist them (Epic 4 will add persistence)
- Epic 5 (Dashboards): Epic 1 exposes metrics but doesn't visualize them (Epic 5 will add UI)

### Development Environment Dependencies

**Required Tools:**

- Git 2.40+: Version control
- Node.js 22 LTS: Runtime (with corepack enabled)
- PostgreSQL 17: Database (Docker or native)
- pnpm 9+: Package manager

**Optional Tools:**

- Docker 24+: For local PostgreSQL instance
- VS Code: Recommended IDE with TypeScript extensions
- Postman/Insomnia: API testing for orchestrator endpoints

**CI/CD Dependencies:**

- GitHub Actions: For automated testing and builds
- npm audit: Security scanning
- ESLint: Linting
- Prettier: Code formatting
- Jest: Unit testing
- Playwright: E2E testing (Epic 5)

### Configuration Dependencies

**Environment Variables Required:**

- `TAMMA_MODE`: orchestrator | worker | standalone (default: standalone)
- `TAMMA_LOG_LEVEL`: trace | debug | info | warn | error (default: info)
- `ANTHROPIC_API_KEY`: Claude API key (required for AI operations)
- `DATABASE_URL`: PostgreSQL connection string (orchestrator mode only)
- `GITHUB_TOKEN`: GitHub PAT (optional, for GitHub operations)
- `GITLAB_TOKEN`: GitLab PAT (optional, for GitLab operations)

**Optional Environment Variables:**

- `TAMMA_CONFIG_PATH`: Override default config file location
- `TAMMA_DEBUG`: Enable debug mode (1 or true)
- `TAMMA_ORCHESTRATOR_URL`: Orchestrator URL (worker mode only)
- `OTEL_EXPORTER_OTLP_ENDPOINT`: OpenTelemetry collector endpoint

## Acceptance Criteria (Authoritative)

### Story 1.1: AI Provider Interface Definition

1. Interface defines core operations: `generateCode()`, `analyzeContext()`, `suggestFix()`, `reviewChanges()`
2. Interface includes provider capabilities discovery (supports streaming, token limits, model versions)
3. Interface includes error handling contracts (rate limits, timeouts, context overflow)
4. Documentation includes integration guide for adding new providers
5. Interface supports both synchronous and asynchronous invocation patterns

### Story 1.2: Claude Code Provider Implementation

1. Claude Code provider implements all interface operations from Story 1.1
2. Provider handles authentication via API key configuration
3. Provider supports streaming responses for real-time feedback
4. Provider includes retry logic with exponential backoff for transient failures
5. Unit tests cover happy path, error cases, and edge cases (context limits, rate limiting)
6. Integration test demonstrates end-to-end code generation request

### Story 1.3: Provider Configuration Management

1. Configuration file supports multiple provider entries (Claude Code, OpenCode, GLM, local LLM)
2. Each provider entry includes: name, API endpoint, API key reference, capabilities, priority
3. Configuration validates on load (required fields, valid URLs, accessible credentials)
4. System supports environment variable overrides for sensitive values (API keys)
5. Configuration reload without restart for non-critical settings changes
6. Documentation includes example configurations for all planned providers

### Story 1.4: Git Platform Interface Definition

1. Interface defines core operations: `createPR()`, `commentOnPR()`, `mergePR()`, `getIssue()`, `createBranch()`, `triggerCI()`
2. Interface includes platform capabilities discovery (review workflows, CI/CD integration, webhook support)
3. Interface normalizes platform-specific models (PR structure, issue format, CI status)
4. Documentation includes integration guide for adding new platforms
5. Interface supports pagination and rate limit handling

### Story 1.5: GitHub Platform Implementation

1. GitHub provider implements all interface operations from Story 1.4
2. Provider handles authentication via Personal Access Token (PAT) or GitHub App
3. Provider integrates with GitHub Actions API for CI/CD triggering
4. Provider integrates with GitHub Review API for automated review workflows
5. Unit tests cover happy path, error cases, and GitHub-specific quirks
6. Integration test demonstrates end-to-end PR creation and merge

### Story 1.6: GitLab Platform Implementation

1. GitLab provider implements all interface operations from Story 1.4
2. Provider handles authentication via Personal Access Token or OAuth
3. Provider integrates with GitLab CI API for pipeline triggering
4. Provider integrates with GitLab Merge Request API for review workflows
5. Unit tests cover happy path, error cases, and GitLab-specific differences from GitHub
6. Integration test demonstrates end-to-end Merge Request creation and merge

### Story 1.7: Git Platform Configuration Management

1. Configuration file supports platform entries (GitHub, GitLab, Gitea, Forgejo)
2. Each platform entry includes: type, base URL, authentication method, webhook secret
3. Configuration validates on load (reachable endpoints, valid credentials)
4. System supports environment variable overrides for sensitive values (tokens)
5. Configuration includes default branch name, PR template path, and label conventions
6. Documentation includes example configurations for all supported platforms

### Story 1.8: Hybrid Orchestrator/Worker Architecture Design

1. Architecture document defines orchestrator mode responsibilities (issue selection, loop coordination, state management)
2. Architecture document defines worker mode responsibilities (CI/CD integration, single-task execution, exit codes)
3. Document includes sequence diagrams for both modes
4. Document specifies shared components (AI abstraction, Git abstraction, quality gates)
5. Document defines state persistence strategy for graceful shutdown/restart
6. Architecture reviewed and approved by technical lead

### Story 1.9: Basic CLI Scaffolding with Mode Selection

1. CLI supports `--mode orchestrator` flag for autonomous coordinator behavior
2. CLI supports `--mode worker` flag for CI/CD-invoked single-task execution
3. CLI loads configuration from config file and environment variables
4. CLI initializes AI provider abstraction and Git platform abstraction
5. CLI outputs mode selection to logs for debugging
6. CLI includes `--version` and `--help` commands with usage examples
7. Integration test demonstrates launching in both modes

### Story 1.13: Agent Customization System

1. Agent configuration management system with version control and rollback capabilities
2. Performance impact measurement for agent customizations across speed, quality, and cost
3. Cross-context agent capability testing (development vs code review vs testing scenarios)
4. Automated optimization recommendations based on Test Platform benchmark results
5. Integration with Test Platform's dual-purpose benchmarking system
6. Context window efficiency analysis and optimization recommendations
7. Privacy-preserving learning from customizations while protecting competitive advantages
8. A/B testing framework for agent configuration improvements

### Story 1.14: Performance Impact Analysis

1. Comprehensive performance impact analysis across speed, quality, cost, and success rate metrics
2. Statistical significance testing for agent customization improvements
3. Context window efficiency measurement and optimization recommendations
4. Cross-agent comparison showing relative performance of customizations
5. Historical trend analysis for agent performance over time
6. Cost-benefit analysis for agent customizations vs. stock configurations
7. Automated insight generation identifying effective customization patterns
8. Integration with Test Platform's dual-purpose benchmarking results

## Traceability Mapping

### PRD Requirements → Epic 1 Stories

**FR-7: Multi-Provider AI Abstraction**

- Story 1.1: AI Provider Interface Definition
  - Deliverable: `IAIProvider` interface in `packages/providers/src/types.ts`
  - Validation: Interface supports `generateCode()`, `analyzeContext()`, `suggestFix()`, `reviewChanges()`
- Story 1.2: Claude Code Provider Implementation
  - Deliverable: `ClaudeCodeProvider` class implementing `IAIProvider`
  - Validation: Integration test demonstrates code generation with Claude API
- Story 1.3: Provider Configuration Management
  - Deliverable: `ProviderConfigManager` supporting multiple providers
  - Validation: Config file loads multiple provider entries with validation

**FR-8: Provider Capability Discovery**

- Story 1.1: AI Provider Interface Definition
  - Deliverable: `ProviderCapabilities` interface with streaming, token limits, model versions
  - Validation: Interface method `getCapabilities()` returns structured metadata
- Story 1.2: Claude Code Provider Implementation
  - Deliverable: Claude-specific capabilities implementation
  - Validation: `ClaudeCodeProvider.getCapabilities()` returns accurate Claude limits

**FR-9: AI Provider Error Handling**

- Story 1.1: AI Provider Interface Definition
  - Deliverable: Error handling contracts for rate limits, timeouts, context overflow
  - Validation: Interface defines error types and retry semantics
- Story 1.2: Claude Code Provider Implementation
  - Deliverable: Retry logic with exponential backoff
  - Validation: Unit tests verify retry behavior for transient failures

**FR-10: Multi-Platform Git Abstraction**

- Story 1.4: Git Platform Interface Definition
  - Deliverable: `IGitPlatform` interface in `packages/platforms/src/types.ts`
  - Validation: Interface supports `createPR()`, `getIssue()`, `createBranch()`, `mergePR()`
- Story 1.5: GitHub Platform Implementation
  - Deliverable: `GitHubPlatform` class implementing `IGitPlatform`
  - Validation: Integration test demonstrates PR creation on GitHub
- Story 1.6: GitLab Platform Implementation
  - Deliverable: `GitLabPlatform` class implementing `IGitPlatform`
  - Validation: Integration test demonstrates MR creation on GitLab
- Story 1.7: Git Platform Configuration Management
  - Deliverable: `PlatformConfigManager` supporting multiple platforms
  - Validation: Config file loads GitHub, GitLab, Gitea, Forgejo entries

**FR-11: Platform Capability Normalization**

- Story 1.4: Git Platform Interface Definition
  - Deliverable: Normalized models for PR, Issue, Branch, CI status
  - Validation: Interface defines common data structures abstracting platform differences
- Story 1.5: GitHub Platform Implementation
  - Deliverable: GitHub API response mapping to normalized models
  - Validation: `GitHubPlatform` returns standard `PullRequest` model
- Story 1.6: GitLab Platform Implementation
  - Deliverable: GitLab API response mapping to normalized models
  - Validation: `GitLabPlatform` returns standard `PullRequest` model (mapped from Merge Request)

**FR-12: Platform Pagination and Rate Limiting**

- Story 1.4: Git Platform Interface Definition
  - Deliverable: Pagination and rate limit handling contracts
  - Validation: Interface methods support cursor-based pagination
- Story 1.5: GitHub Platform Implementation
  - Deliverable: GitHub-specific pagination (link headers) and rate limit handling
  - Validation: Unit test verifies pagination across multiple pages
- Story 1.6: GitLab Platform Implementation
  - Deliverable: GitLab-specific pagination and rate limit handling
  - Validation: Unit test verifies pagination and rate limit backoff

**FR-13: Hybrid Orchestrator Mode**

- Story 1.8: Hybrid Orchestrator/Worker Architecture Design
  - Deliverable: Architecture document defining orchestrator responsibilities
  - Validation: Document specifies issue selection, loop coordination, state management
- Story 1.9: Basic CLI Scaffolding with Mode Selection
  - Deliverable: CLI with `--mode orchestrator` flag
  - Validation: CLI starts orchestrator service, loads config, initializes abstractions

**FR-14: Hybrid Worker Mode**

- Story 1.8: Hybrid Orchestrator/Worker Architecture Design
  - Deliverable: Architecture document defining worker responsibilities
  - Validation: Document specifies CI/CD integration, single-task execution, exit codes
- Story 1.9: Basic CLI Scaffolding with Mode Selection
  - Deliverable: CLI with `--mode worker` flag
  - Validation: CLI executes single task, returns appropriate exit code

**FR-15: Shared Component Architecture**

- Story 1.8: Hybrid Orchestrator/Worker Architecture Design
  - Deliverable: Architecture document specifying shared components
  - Validation: Document identifies AI abstraction, Git abstraction, quality gates as shared
- Story 1.9: Basic CLI Scaffolding with Mode Selection
  - Deliverable: CLI initialization of provider and platform abstractions in both modes
  - Validation: Both orchestrator and worker modes successfully initialize shared components

**Agent Optimization Requirements (Stories 1.13, 1.14)**

- Story 1.13: Agent Customization System
  - Deliverable: `AgentConfigManager` with version control and A/B testing
  - Validation: System supports agent customization with performance measurement
- Story 1.14: Performance Impact Analysis
  - Deliverable: `PerformanceAnalyzer` with statistical analysis and trend tracking
  - Validation: System provides comprehensive performance analysis and optimization recommendations

### Architecture Alignment → Epic 1 Stories

**Architecture Section 2.1: Technology Stack → Stories 1.1-1.9**

- TypeScript 5.7+ strict mode: All packages use strict TypeScript
- Node.js 22 LTS: All services target Node.js 22
- pnpm workspaces: Monorepo structure with `@tamma/*` packages

**Architecture Section 3.1: Interface-Based Design Pattern → Stories 1.1, 1.4**

- Story 1.1: `IAIProvider` interface
- Story 1.4: `IGitPlatform` interface
- Both follow dependency inversion principle

**Architecture Section 3.2: Plugin Architecture → Stories 1.2, 1.5, 1.6**

- Story 1.2: Claude Code provider as plugin
- Story 1.5: GitHub platform as plugin
- Story 1.6: GitLab platform as plugin
- All implement defined interfaces for dynamic loading

**Architecture Section 3.3: Hybrid Architecture Pattern → Story 1.8, 1.9**

- Story 1.8: Architecture design document
- Story 1.9: CLI mode selection implementation
- Orchestrator mode: Stateful coordinator
- Worker mode: Stateless executor

**Architecture Section 4.3: Project Structure → Stories 1.1-1.9**

- `packages/providers`: Stories 1.1, 1.2, 1.3
- `packages/platforms`: Stories 1.4, 1.5, 1.6, 1.7
- `packages/orchestrator`: Story 1.8, 1.9 (orchestrator mode)
- `packages/worker`: Story 1.8, 1.9 (worker mode)
- `packages/cli`: Story 1.9
- `packages/config`: Stories 1.3, 1.7

**Architecture Section 5.0: Epic-to-Architecture Mapping**

- Epic 1 directly implements foundational packages
- Establishes patterns for Epic 2-5 to follow
- Delivers plugin system, hybrid architecture, configuration management

### NFR Requirements → Epic 1 Stories

**NFR-1: Performance Targets**

- Story 1.2: Provider response time < 500ms (p95)
- Story 1.5, 1.6: Platform API calls < 1000ms (p95)
- Story 1.9: CLI cold start < 1000ms (p95)

**NFR-2: Security Requirements**

- Story 1.3: Provider API keys encrypted at rest
- Story 1.7: Platform tokens stored in OS keychain
- Story 1.9: Config file permissions set to 600

**NFR-3: Reliability Requirements**

- Story 1.2: Retry logic with exponential backoff for provider failures
- Story 1.5, 1.6: Circuit breaker pattern for platform API failures
- Story 1.8: State persistence for graceful shutdown/restart

**NFR-4: Observability Requirements**

- Story 1.2, 1.5, 1.6: Structured logging for all operations
- Story 1.9: Health check endpoints for orchestrator mode
- All stories: Event emission for DCB event sourcing (Epic 4)

## Risks, Assumptions, Open Questions

### Risks

**Risk 1: AI Provider API Changes**

- Likelihood: Medium
- Impact: High
- Description: Anthropic may change Claude API contracts, breaking ClaudeCodeProvider implementation
- Mitigation:
  - Version all API calls explicitly (use `/v1/messages` not `/messages`)
  - Monitor Anthropic changelog and deprecation notices
  - Implement adapter pattern to isolate API-specific code
  - Maintain integration test suite to detect breaking changes early
- Contingency: Implement provider version negotiation; support multiple API versions simultaneously

**Risk 2: Git Platform Rate Limiting**

- Likelihood: High
- Impact: Medium
- Description: GitHub/GitLab rate limits may throttle operations, especially for free tier accounts
- Mitigation:
  - Implement exponential backoff with jitter
  - Cache frequently accessed data (issue metadata, repository info)
  - Use GraphQL for GitHub to reduce request count
  - Honor `Retry-After` headers religiously
- Contingency: Queue operations and batch where possible; provide clear user feedback on rate limit status

**Risk 3: Provider Configuration Complexity**

- Likelihood: Medium
- Impact: Medium
- Description: Multi-provider configuration may overwhelm users, especially with credential management
- Mitigation:
  - Provide `tamma init` wizard for guided setup
  - Offer sensible defaults (Claude Code as default provider)
  - Comprehensive documentation with examples
  - Validation with clear error messages
- Contingency: Provide migration tool from simple single-provider config to multi-provider config

**Risk 4: Platform Detection Ambiguity**

- Likelihood: Low
- Impact: Medium
- Description: Self-hosted GitLab instances on custom domains may not be auto-detected correctly
- Mitigation:
  - Allow explicit platform type in configuration
  - Provide URL pattern hints in config
  - Log platform detection decisions for debugging
- Contingency: Fallback to manual platform selection; provide clear error messages

**Risk 5: Orchestrator Database Availability**

- Likelihood: Medium
- Impact: High
- Description: PostgreSQL downtime blocks orchestrator operations, preventing task queue management
- Mitigation:
  - Use managed PostgreSQL with 99.9% SLA
  - Implement database connection pool with health checks
  - Implement circuit breaker for database operations
  - Fail gracefully with clear error messages
- Contingency: Implement in-memory fallback queue for temporary database outages (lossy mode)

**Risk 6: Worker Registration Race Conditions**

- Likelihood: Medium
- Impact: Medium
- Description: Multiple workers registering simultaneously may cause database conflicts
- Mitigation:
  - Use database-level unique constraints on worker_id
  - Implement retry logic for registration failures
  - Use optimistic locking for worker status updates
- Contingency: Implement registration queue with serialization

**Risk 7: CLI Mode Selection Confusion**

- Likelihood: High
- Impact: Low
- Description: Users may be confused about when to use orchestrator vs worker vs standalone mode
- Mitigation:
  - Clear documentation with decision tree
  - CLI provides helpful error messages if wrong mode selected
  - `--help` includes mode descriptions and use cases
  - Default to standalone for simplicity
- Contingency: Provide mode auto-detection based on environment (CI env vars indicate worker mode)

**Risk 8: Keychain/Credential Manager Unavailability**

- Likelihood: Low
- Impact: Medium
- Description: Some Linux environments may lack Secret Service API, blocking secure credential storage
- Mitigation:
  - Fallback to encrypted file storage with user warning
  - Document system requirements for credential management
  - Provide manual credential entry as alternative
- Contingency: Support plaintext credential storage with explicit user acknowledgment of security risk

### Assumptions

**A1: Claude API Stability**

- Assumption: Anthropic's Claude API remains backward compatible within major versions
- Validation: Monitor Anthropic release notes and deprecation timeline
- Risk if False: Risk 1 (API breaking changes)

**A2: PostgreSQL Availability**

- Assumption: Users can provide PostgreSQL 17 instance for orchestrator mode (Docker or managed)
- Validation: Document PostgreSQL setup in installation guide
- Risk if False: Risk 5 (database unavailability blocks operations)

**A3: Network Connectivity**

- Assumption: Orchestrator and workers have reliable network connectivity to AI providers and Git platforms
- Validation: Implement connection health checks on startup
- Risk if False: Operations fail, but handled by retry logic and circuit breakers

**A4: User Understanding of Hybrid Architecture**

- Assumption: Users understand difference between orchestrator/worker/standalone modes
- Validation: User testing of documentation and CLI help text
- Risk if False: Risk 7 (mode selection confusion)

**A5: API Key Availability**

- Assumption: Users have access to Anthropic API keys and Git platform tokens
- Validation: Setup wizard validates credentials before proceeding
- Risk if False: System cannot function; clear error messages guide user to obtain credentials

**A6: TypeScript Ecosystem Maturity**

- Assumption: TypeScript 5.7+ and Node.js 22 LTS provide stable foundation
- Validation: Use LTS versions only; monitor security advisories
- Risk if False: Runtime errors or security vulnerabilities require rapid patching

**A7: pnpm Workspace Support**

- Assumption: pnpm 9+ workspace features work reliably for monorepo management
- Validation: CI pipeline validates build and test across all packages
- Risk if False: Build failures or dependency resolution issues; fallback to npm workspaces

**A8: OS Credential Storage Compatibility**

- Assumption: keytar library works across Windows, macOS, Linux
- Validation: Test on all three platforms in CI
- Risk if False: Risk 8 (credential storage unavailability)

### Open Questions

**Q1: Should we support additional AI providers beyond Claude in Epic 1?**

- Options:
  1. Only Claude Code in Epic 1; defer other providers to future epics
  2. Add GitHub Copilot in Story 1.2b (parallel to Claude implementation)
  3. Add OpenAI GPT-4 in Story 1.2c (parallel to Claude implementation)
- Recommendation: Option 1 - Focus on single provider to validate abstraction; add providers in future epics
- Decision Needed By: Sprint planning (before Story 1.2 starts)

**Q2: Should GitLab MR API differences justify a separate abstraction layer?**

- Context: GitLab Merge Requests have different approval workflow than GitHub PRs
- Options:
  1. Normalize all differences in `IGitPlatform` interface (current approach)
  2. Create separate `IMergeRequest` vs `IPullRequest` interfaces
  3. Use union types to represent platform-specific features
- Recommendation: Option 1 - Start with normalization; refactor if limitations discovered
- Decision Needed By: Before Story 1.4 (interface design)

**Q3: Should configuration support hot-reload in Epic 1?**

- Context: Story 1.3 AC 5 requires "configuration reload without restart"
- Options:
  1. Full hot-reload support (watch config file for changes)
  2. Reload on SIGHUP signal only
  3. Defer hot-reload to Epic 5 (observability); require restart in Epic 1
- Recommendation: Option 2 - SIGHUP reload is simpler than file watching; provides restart alternative
- Decision Needed By: Story 1.3 implementation

**Q4: Should orchestrator mode support horizontal scaling in Epic 1?**

- Context: Current design assumes single orchestrator instance
- Options:
  1. Single orchestrator instance only (current design)
  2. Multi-instance with leader election (using PostgreSQL advisory locks)
  3. Multi-instance with task partitioning (hash-based work distribution)
- Recommendation: Option 1 - Single instance sufficient for initial release; add scaling in future epic
- Decision Needed By: Story 1.8 (architecture design)

**Q5: Should worker authentication use JWT or shared secret?**

- Context: Workers need to authenticate with orchestrator
- Options:
  1. Shared secret (symmetric key) configured in environment
  2. JWT with asymmetric keys (orchestrator issues tokens to workers)
  3. mTLS (mutual TLS authentication)
- Recommendation: Option 1 for Epic 1 (simplest); upgrade to Option 2 in Epic 3 (security hardening)
- Decision Needed By: Story 1.8 (architecture design)

**Q6: Should CLI support interactive mode in Epic 1?**

- Context: Story 1.9 creates basic CLI; user approval checkpoints come in Epic 2
- Options:
  1. CLI output only in Epic 1; defer interactive prompts to Epic 2
  2. Add basic interactive support using inquirer library in Story 1.9
- Recommendation: Option 2 - Add inquirer early for config wizard; enables faster Epic 2 development
- Decision Needed By: Story 1.9 implementation

**Q7: Should we use Docker for local PostgreSQL in development?**

- Context: Developers need PostgreSQL for orchestrator mode testing
- Options:
  1. Require native PostgreSQL installation (documented in setup guide)
  2. Provide docker-compose.yml for one-command PostgreSQL setup
  3. Use SQLite as development alternative (PostgreSQL for production)
- Recommendation: Option 2 - Docker compose provides best developer experience
- Decision Needed By: Development environment setup (before Story 1.8 testing)

## Test Strategy Summary

### Unit Testing Strategy

**Scope:** All packages (providers, platforms, orchestrator, worker, CLI, config)

**Framework:** Jest 29+ with TypeScript support

**Coverage Targets:**

- Line coverage: 80% minimum
- Branch coverage: 75% minimum
- Function coverage: 85% minimum
- Critical paths (error handling, retry logic): 100%

**Key Test Categories:**

1. **Interface Contract Tests (Stories 1.1, 1.4)**
   - Validate all interface methods have correct signatures
   - Validate interface compliance for implementations
   - Mock implementations for testing consumers

2. **Provider Tests (Stories 1.2, 1.3)**
   - Happy path: successful code generation, context analysis
   - Error cases: rate limits, timeouts, invalid API keys, context overflow
   - Edge cases: empty responses, malformed JSON, network failures
   - Retry logic: exponential backoff behavior, max retry limits
   - Streaming: chunk parsing, error mid-stream, connection drops
   - Configuration: load, validate, environment variable overrides

3. **Platform Tests (Stories 1.5, 1.6, 1.7)**
   - Happy path: PR creation, issue retrieval, branch operations
   - Error cases: authentication failures, API errors, network timeouts
   - Pagination: multi-page result sets, cursor-based pagination
   - Rate limiting: backoff behavior, retry-after header handling
   - Normalization: GitHub → standard model, GitLab → standard model
   - Configuration: load, validate, platform detection

4. **Orchestrator Tests (Story 1.8)**
   - Task queue operations: enqueue, dequeue, status updates
   - Worker management: registration, heartbeat, offline detection
   - API endpoints: request validation, response formatting, error handling
   - Database operations: connection pool, transactions, rollback

5. **Worker Tests (Story 1.8)**
   - Task polling: successful poll, empty queue, network failure
   - Task execution: mock workflow execution, progress reporting
   - Heartbeat: successful heartbeat, retry on failure
   - Shutdown: graceful shutdown with task completion

6. **CLI Tests (Story 1.9)**
   - Argument parsing: valid args, invalid args, help text
   - Mode selection: orchestrator mode, worker mode, standalone mode
   - Config loading: file load, env var overrides, validation failures
   - Initialization: provider setup, platform setup, logging setup

**Mocking Strategy:**

- Mock external APIs (Anthropic, GitHub, GitLab) using MSW (Mock Service Worker)
- Mock database using in-memory SQLite for fast tests
- Mock filesystem operations using memfs
- Provide test fixtures for common API responses

### Integration Testing Strategy

**Scope:** Provider-to-API, Platform-to-API, Orchestrator-to-Worker, CLI-to-Services

**Framework:** Jest with real HTTP clients (no mocks)

**Environment:** Dedicated test accounts for Anthropic, GitHub, GitLab

**Key Test Categories:**

1. **Provider Integration (Story 1.2)**
   - End-to-end code generation request to Anthropic API
   - Streaming response handling with real SSE stream
   - Authentication validation with real API key
   - Rate limit handling with intentional limit trigger
   - Prerequisites: `ANTHROPIC_API_KEY_TEST` environment variable

2. **Platform Integration (Stories 1.5, 1.6)**
   - End-to-end PR creation on test GitHub repository
   - End-to-end MR creation on test GitLab project
   - Issue retrieval and filtering on test repositories
   - Branch creation and deletion on test repositories
   - Prerequisites: `GITHUB_TOKEN_TEST`, `GITLAB_TOKEN_TEST` environment variables

3. **Orchestrator-Worker Integration (Story 1.8)**
   - Worker registration with real orchestrator instance
   - Task assignment from orchestrator to worker
   - Heartbeat flow over HTTP
   - Task completion reporting
   - Prerequisites: Running PostgreSQL instance, orchestrator service

4. **CLI Integration (Story 1.9)**
   - CLI launches orchestrator mode successfully
   - CLI launches worker mode successfully
   - CLI loads config from file and env vars
   - CLI initializes providers and platforms
   - Prerequisites: Valid config file, API credentials

**Test Data Management:**

- Test repositories: `tamma-test-github`, `tamma-test-gitlab`
- Test issues: Create before test run, cleanup after
- Test branches: Prefix with `test-` for easy identification and cleanup
- Test database: Separate schema for integration tests, dropped after each run

### End-to-End (E2E) Testing Strategy

**Scope:** Complete workflow scenarios (deferred to Epic 2+)

**Framework:** Playwright (Epic 5)

**Note:** Epic 1 has no user-facing workflows yet; E2E testing begins in Epic 2 when autonomous dev loop is implemented.

### Performance Testing Strategy

**Scope:** Latency, throughput, resource usage for all services

**Framework:** Artillery.io for load testing, clinic.js for profiling

**Key Performance Tests:**

1. **Provider Performance (Story 1.2)**
   - Test: 100 concurrent code generation requests
   - Metric: p95 latency < 500ms, p99 latency < 1000ms
   - Load: Ramp from 1 to 100 concurrent over 60s, sustain for 300s

2. **Platform API Performance (Stories 1.5, 1.6)**
   - Test: 500 concurrent issue list requests
   - Metric: p95 latency < 1000ms, p99 latency < 2000ms
   - Load: Ramp from 1 to 500 concurrent over 120s, sustain for 300s

3. **Orchestrator Throughput (Story 1.8)**
   - Test: Task submission rate
   - Metric: 5 tasks/second sustained for 600s
   - Validation: No task loss, queue drains successfully

4. **CLI Startup Performance (Story 1.9)**
   - Test: CLI cold start (no cached config)
   - Metric: p95 < 1000ms, p99 < 1500ms
   - Iterations: 100 cold starts

**Resource Usage Benchmarks:**

- Memory profiling: heap snapshots at steady state, leak detection
- CPU profiling: flame graphs for hot paths, optimization targets
- Network monitoring: request counts, payload sizes, bandwidth usage

### Security Testing Strategy

**Scope:** Credential handling, input validation, dependency vulnerabilities

**Framework:** npm audit, OWASP ZAP, custom security tests

**Key Security Tests:**

1. **Credential Security (Stories 1.3, 1.7)**
   - Test: API keys not logged to stdout/stderr
   - Test: Config files have correct permissions (600)
   - Test: Keychain storage used on supported platforms
   - Test: Encrypted file storage used when keychain unavailable

2. **Input Validation (All Stories)**
   - Test: Malicious inputs rejected (SQL injection, XSS, path traversal)
   - Test: Overly large inputs rejected (DoS prevention)
   - Test: Invalid JSON rejected with clear error messages

3. **Dependency Scanning (All Stories)**
   - Test: `npm audit` passes with no critical vulnerabilities
   - Test: Snyk scan passes with no high vulnerabilities
   - Frequency: On every PR, weekly scheduled scans

4. **API Security (Story 1.8)**
   - Test: Unauthenticated requests to orchestrator API rejected (401)
   - Test: Invalid JWT tokens rejected (401)
   - Test: HTTPS enforced (no plain HTTP allowed)

### Continuous Integration Strategy

**Platform:** GitHub Actions

**Workflow Triggers:** Push to main, pull request, daily schedule

**Pipeline Stages:**

1. **Build Stage**
   - Install dependencies (`pnpm install`)
   - TypeScript compilation (`pnpm build`)
   - Lint check (`pnpm lint`)
   - Format check (`pnpm format:check`)

2. **Unit Test Stage**
   - Run unit tests (`pnpm test:unit`)
   - Generate coverage report
   - Upload coverage to Codecov
   - Fail if coverage < 80%

3. **Integration Test Stage** (requires secrets)
   - Run integration tests (`pnpm test:integration`)
   - Uses test API keys from GitHub Secrets
   - Skipped on external PRs (security)

4. **Security Scan Stage**
   - npm audit (`npm audit --audit-level=moderate`)
   - Snyk scan (`snyk test`)
   - Secret scanning (GitHub native)

5. **Performance Benchmark Stage** (main branch only)
   - Run performance tests (`pnpm test:perf`)
   - Compare results to baseline
   - Comment on PR if regression detected (> 10% slowdown)

**Test Environment Matrix:**

- OS: Ubuntu 22.04, macOS 14, Windows Server 2022
- Node.js: 22.0.0, 22.x (latest LTS)
- Database: PostgreSQL 17 (Docker container)

### Test Execution Guidance

**Pre-Commit:**

- Run unit tests for changed packages only
- Run linting and formatting
- Estimated time: 30-60 seconds

**Pre-Push:**

- Run full unit test suite
- Run integration tests (if credentials available)
- Estimated time: 3-5 minutes

**CI Pipeline:**

- Full test suite (unit + integration + security)
- All OS/Node combinations
- Estimated time: 10-15 minutes

**Release Candidate:**

- Full test suite + performance tests
- Manual smoke testing of CLI in all three modes
- Estimated time: 30-45 minutes
