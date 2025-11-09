# Epic Technical Specification: Quality Gates & Intelligence Layer

Date: 2025-10-28
Author: meywd
Epic ID: 3
Status: Draft

---

## Overview

Epic 3 implements the Quality Gates & Intelligence Layer that transforms Tamma from a basic autonomous development loop into a production-ready, intelligent system capable of handling real-world development complexity. Building on Epic 1's multi-provider AI and Git abstractions and Epic 2's core workflow orchestration, this epic delivers the reliability and intelligence features that enable the 70%+ autonomous completion rate target (NFR-2) through systematic quality enforcement and intelligent problem-solving.

The quality gates component (Stories 3.1-3.3, 3.8-3.9) implements PRD FR-16's **intelligent 3-retry limit with escalation** across build, test, and security scanning checkpoints. Unlike traditional CI/CD systems that merely report failures, Tamma's quality gates actively attempt to resolve failures through AI-powered analysis and fixes, distinguishing between transient/solvable errors (network timeouts, rate limits, fixable code issues) that warrant retry attempts and structural issues (missing files, corrupted data, configuration errors) that require immediate human escalation without retry. The retry counter applies per action and resets to zero upon success, preventing cumulative retry exhaustion across the workflow while maintaining rapid escalation for non-recoverable failures. This intelligent retry strategy reduces PR rework rate to <5% (NFR-2) while avoiding the "retry forever" trap that wastes time on unsolvable problems.

The intelligence layer component (Stories 3.4-3.7) implements PRD FR-3's **clarifying questions for ambiguous requirements** and FR-4's **multi-option design proposals**, transforming Tamma from a code generator into a collaborative design partner. By detecting ambiguity through scoring algorithms (Story 3.6), researching unfamiliar concepts before implementation (Story 3.4), and presenting design alternatives with tradeoffs (Story 3.7), this layer prevents the "garbage in, garbage out" problem that plagues naive autonomous systems. The intelligence layer ensures that when Tamma asks for approval (FR-19), the approval is for a well-informed, thoroughly considered solution rather than a blind guess.

## MVP Criticality

**All stories in Epic 3 are MVP CRITICAL** for the self-maintenance goal. Quality gates prevent Tamma from breaking itself during autonomous development of its own codebase:

- **Build/Test Automation (3.1-3.2)**: Validates all self-implemented changes don't break compilation or tests
- **Mandatory Escalation (3.3)**: Prevents infinite loops when stuck - Tamma never hangs indefinitely on unsolvable problems
- **Research Capability (3.4)**: Enables Tamma to handle unfamiliar concepts in its own codebase (new dependencies, architectural patterns)
- **Clarifying Questions (3.5-3.6)**: Detects ambiguous requirements in its own backlog stories
- **Multi-Option Proposals (3.7)**: Ensures Tamma considers design alternatives for complex self-modifications
- **Static Analysis/Security (3.8-3.9)**: Maintains code quality and security standards when modifying itself

Without Epic 3, Tamma cannot safely maintain itself - it would lack the quality enforcement and error recovery mechanisms required for production-grade autonomous operation.

## Objectives and Scope

**In Scope:**

- **Story 3.1:** Build automation with retry logic - Auto-trigger builds, parse failures, apply AI-suggested fixes, escalate after 3 retries
- **Story 3.2:** Test execution with retry logic - Run test suites, analyze failures, fix test code/implementation, escalate after 3 retries
- **Story 3.3:** Mandatory escalation workflow - Structured escalation with context, notifications, human resolution tracking (FR-16)
- **Story 3.4:** Research capability for unfamiliar concepts - Pre-implementation research queries, cached findings, manual research triggers
- **Story 3.5:** Clarifying questions for ambiguous requirements - Ambiguity detection, interactive Q&A, answer incorporation (FR-3)
- **Story 3.6:** Ambiguity detection scoring - Quantitative ambiguity measurement (0-100 score), threshold-based interventions
- **Story 3.7:** Multi-option design proposals - Alternative design generation, pros/cons analysis, user selection (FR-4)
- **Story 3.8:** Static analysis integration - Linter/formatter integration, auto-fix application, analysis results in PR
- **Story 3.9:** Security scanning integration - Dependency + code vulnerability scanning, critical vulnerability blocking, auto-patching (FR-33)
- **Story 3.10:** Agent performance monitoring - Performance metrics tracking, quality scoring, real-time dashboard, alerting
- **Story 3.11:** Cost-aware AI usage - Real-time cost tracking, budget management, optimization strategies, forecasting
- **Story 3.12:** Task complexity assessment - Multi-dimensional analysis, scoring algorithms, decomposition recommendations

**Out of Scope:**

- Event sourcing implementation for quality gate events (Epic 4, will capture events from this epic)
- Observability dashboards for quality metrics visualization (Epic 5)
- Advanced ML-based ambiguity detection models (post-MVP enhancement)
- Code review agent integration (separate AI agent specialization feature, deferred)
- Performance profiling and optimization suggestions (post-MVP feature)
- Custom quality gate plugin system (deferred to extensibility epic)
- Breaking change detection beyond manual approval requirement (FR-34 basic implementation in Epic 2)

## System Architecture Alignment

Epic 3 extends Epic 2's workflow orchestration engine with two new service packages: `packages/quality-gates` and `packages/intelligence`. The quality gates package integrates with Epic 1's `GitPlatformInterface` to trigger and monitor CI/CD pipelines, leveraging the platform-agnostic abstraction to support GitHub Actions, GitLab CI, Gitea Actions, and Forgejo Actions uniformly. The retry logic with exponential backoff (2s → 4s → 8s) mirrors Epic 2's state machine retry patterns, maintaining consistency across the autonomous loop.

The intelligence layer interfaces with Epic 1's `AIProviderInterface` for research queries, ambiguity analysis, and design proposal generation, routing intelligence tasks to the optimal provider based on capability matching (FR-9). The clarifying questions workflow (Story 3.5) extends Epic 2's approval checkpoint pattern (FR-19), presenting questions synchronously in standalone mode and asynchronously in orchestrator mode with timeout handling. The ambiguity scoring algorithm (Story 3.6) uses natural language processing heuristics combined with AI provider scoring to produce quantitative measurements, with score thresholds configurable via the shared `packages/config` system established in Epic 1.

The escalation workflow (Story 3.3) implements a notification abstraction layer supporting multiple channels: CLI output (standalone mode), webhook POST (orchestrator mode), and optional email/Slack integration through the notification service pattern. All quality gate events and intelligence layer decisions emit events through the DCB event sourcing system (Epic 4 dependency), ensuring complete auditability of why autonomous loops succeeded or escalated.

Static analysis (Story 3.8) and security scanning (Story 3.9) integrate with Epic 2's pre-PR-creation checkpoint, running after refactoring (Story 2.7) but before PR creation (Story 2.8). Security scan results with critical vulnerabilities invoke the mandatory escalation workflow, aligning with FR-34's requirement that breaking changes and security issues never bypass human review. The security scanning architecture prepares for future integration with SAST/DAST tools and vulnerability databases (CVE, GitHub Advisory Database, Snyk).

## Detailed Design

### Services and Modules

**1. Quality Gates Service (`packages/quality-gates`)**

_Build Automation Module (Story 3.1):_

- `BuildOrchestrator` class triggering CI/CD builds via `GitPlatformInterface`
- `BuildMonitor` service polling build status every 15 seconds with timeout (10 minutes default)
- `BuildLogParser` service extracting error messages from platform-specific log formats
- `BuildFailureAnalyzer` service categorizing failures (compilation errors, dependency issues, timeout, configuration errors)
- `BuildFixGenerator` service sending error context to AI provider for fix suggestions
- Retry logic: 3 attempts with exponential backoff (2s → 4s → 8s), counter resets on success
- Immediate escalation conditions: missing build config files, invalid credentials, unsupported platform

_Test Execution Module (Story 3.2):_

- `TestRunner` service executing local test suites with framework detection (Jest, pytest, RSpec, etc.)
- `TestOutputParser` service parsing test results (pass/fail counts, error messages, stack traces)
- `TestFailureAnalyzer` service distinguishing test failures (assertions) from test errors (exceptions)
- `TestFixGenerator` service generating fixes for both test code and implementation code
- Retry logic: 3 attempts per test failure, counter resets when all tests pass
- Immediate escalation conditions: missing test framework, corrupted test files, environment setup failures

_Escalation Workflow (Story 3.3):_

- `EscalationManager` service creating structured escalation events when retry limit exhausted
- `EscalationNotifier` abstraction supporting multiple channels:
  - `CLINotifier` for standalone mode (blocks with prompt)
  - `WebhookNotifier` for orchestrator mode (POST to configured URL)
  - `EmailNotifier` optional integration (SMTP, SendGrid, AWS SES)
  - `SlackNotifier` optional integration (webhook or bot API)
- `EscalationTracker` service monitoring human resolution and workflow resumption
- `PRCommenter` service posting escalation context as PR comments with "needs-human-review" label
- Escalation context includes: failure type, retry history with logs, suggested next steps, correlation ID

_Static Analysis Module (Story 3.8):_

- `StaticAnalyzerDetector` service discovering project analysis tools from config files
  - JavaScript/TypeScript: ESLint (.eslintrc), Prettier (.prettierrc)
  - Python: Pylint (.pylintrc), Black (pyproject.toml), Flake8 (.flake8)
  - Ruby: RuboCop (.rubocop.yml)
  - Go: golangci-lint (.golangci.yml)
  - Rust: Clippy (Cargo.toml)
- `StaticAnalyzerRunner` service executing analysis tools with timeout (5 minutes)
- `AnalysisResultParser` service parsing tool-specific output formats
- `AutoFixer` service applying auto-fixes (formatting, import sorting, simple rule violations)
- Retry logic: 3 attempts for fixable issues, immediate escalation for configuration errors

_Security Scanning Module (Story 3.9):_

- `DependencyScannerOrchestrator` service running platform-specific vulnerability scanners:
  - Node.js: npm audit, yarn audit, pnpm audit
  - Python: pip-audit, safety
  - Ruby: bundle-audit
  - Go: govulncheck
  - Rust: cargo-audit
- `CodeScannerOrchestrator` service running SAST tools:
  - Multi-language: Semgrep (priority), Snyk Code
  - Python: Bandit
  - Ruby: Brakeman
  - JavaScript: ESLint security plugins
- `VulnerabilityAnalyzer` service categorizing findings by severity (critical, high, medium, low)
- `VulnerabilityPatcher` service auto-applying recommended fixes (dependency upgrades)
- Escalation policy: Critical vulnerabilities → immediate escalation (no retry), Medium/Low → PR comment with recommendations

**2. Intelligence Layer Service (`packages/intelligence`)**

_Research Module (Story 3.4):_

- `ConceptDetector` service identifying unfamiliar terms during plan generation
  - Known API registry (local database of common APIs/frameworks)
  - Fuzzy matching against known concepts
  - Confidence threshold for triggering research (< 60% confidence)
- `ResearchOrchestrator` service querying AI provider with structured prompts
  - Prompt template: "Research [concept] for [programming language]: API documentation, common patterns, gotchas, code examples"
  - Response validation (300-500 words, must include code example)
- `ResearchCache` service with 24-hour TTL using Redis or in-memory store
- `ManualResearchTrigger` CLI command handler for explicit research requests
- Integration point: Injects research findings into `PlanGenerator` context

_Ambiguity Detection Module (Stories 3.5, 3.6):_

- `AmbiguityAnalyzer` service scoring issue content (0-100 scale, higher = more ambiguous)
  - NLP heuristics: vague language detection ("maybe", "probably", "should"), pronoun ambiguity, negation complexity
  - Missing components: No acceptance criteria (−20 points), Missing implementation details (−15 points)
  - Conflicting statements detector using sentence similarity
  - AI-powered scoring for context-dependent ambiguity
- `QuestionGenerator` service creating 2-5 clarifying questions:
  - Multiple-choice format preferred (easier user experience)
  - Open-ended questions for complex ambiguity
  - Question quality validation (no yes/no questions without context)
- `InteractiveQASession` service managing question presentation and answer collection
  - CLI interactive prompts in standalone mode
  - Async question queue in orchestrator mode (webhook delivery)
- `ContextEnricher` service incorporating answers into `ContextAnalyzer` output
- Thresholds: Score > 70 → trigger clarifying questions, Score > 90 → suggest issue breakdown
- Override mechanism: "skip-questions" label or "proceed-despite-ambiguity" label

_Design Proposals Module (Story 3.7):_

- `DesignProposalGenerator` service creating 2-3 alternative approaches for issues labeled "design-options-needed"
  - AI prompt template: "Generate 2-3 design approaches for [issue]. For each: description, pros/cons, implementation complexity (1-5), test strategy"
  - Response parsing into structured `DesignOption` objects
  - Validation: Each option must have distinct tradeoffs
- `DesignSelectionInterface` service presenting options via CLI or webhook
  - Numbered list with formatted pros/cons
  - Custom option support (user specifies alternative design)
- `DesignIntegrator` service merging selected design into `PlanGenerator` workflow
- Output: Design options and selection logged to event trail, posted as PR comment for visibility

**3. Integration Points with Existing Services**

_Epic 2 Workflow Engine Integration:_

- Quality gates inject checkpoints between Epic 2 workflow phases:
  - Build automation → After `PRManager.createPR()` (Story 2.8)
  - Test execution → After `CodeGenerator.generate()` (Story 2.6), after `RefactoringOrchestrator.refactor()` (Story 2.7)
  - Static analysis → After `RefactoringOrchestrator.refactor()` (Story 2.7), before `PRManager.createPR()` (Story 2.8)
  - Security scanning → After static analysis, before `PRManager.createPR()` (Story 2.8)
- Intelligence layer extends Epic 2 approval checkpoints:
  - Research → Triggered by `PlanGenerator.generate()` (Story 2.3)
  - Clarifying questions → Triggered by `ContextAnalyzer.analyze()` (Story 2.2)
  - Ambiguity scoring → Part of `ContextAnalyzer.analyze()` (Story 2.2)
  - Design proposals → Extended `PlanGenerator.generate()` for labeled issues (Story 2.3)

_Epic 1 Provider Abstractions Integration:_

- All AI interactions route through `AIProviderInterface` (Stories 1.1, 1.2)
- CI/CD operations use `GitPlatformInterface` (Stories 1.4, 1.5, 1.6)
- Configuration management via `ProviderConfigManager` and `PlatformConfigManager` (Stories 1.3, 1.7)

### Data Models and Contracts

**1. Quality Gates Models**

```typescript
interface RetryPolicy {
  maxAttempts: number; // 3 for all quality gates
  backoffStrategy: 'exponential' | 'linear' | 'fixed';
  backoffBaseMs: number; // 2000ms base for exponential
  backoffMultiplier: number; // 2x for exponential (2s → 4s → 8s)
  resetOnSuccess: boolean; // true - counter resets when action succeeds
}

interface BuildExecution {
  buildId: string;
  ciPlatform: 'github-actions' | 'gitlab-ci' | 'gitea-actions' | 'forgejo-actions';
  workflowName: string;
  jobName: string;
  status: 'pending' | 'running' | 'success' | 'failure' | 'cancelled';
  startedAt: Date;
  completedAt?: Date;
  durationMs?: number;
  logs: string;
  errorMessages: string[];
  failureCategory?:
    | 'compilation'
    | 'dependency'
    | 'timeout'
    | 'configuration'
    | 'resource'
    | 'unknown';
  retryAttempt: number; // 0 for first attempt, increments per retry
  suggestedFix?: string; // AI-generated fix suggestion
}

interface TestExecution {
  testRunId: string;
  framework: 'jest' | 'pytest' | 'rspec' | 'go-test' | 'cargo-test' | 'phpunit' | 'unknown';
  totalTests: number;
  passedTests: number;
  failedTests: number;
  skippedTests: number;
  errorTests: number; // Unexpected exceptions, not assertion failures
  durationMs: number;
  failures: TestFailure[];
  coverage?: TestCoverage;
  retryAttempt: number;
}

interface TestFailure {
  testName: string;
  testFile: string;
  failureType: 'assertion' | 'exception' | 'timeout';
  message: string;
  stackTrace?: string;
  expectedValue?: string;
  actualValue?: string;
}

interface EscalationEvent {
  escalationId: string;
  escalationType:
    | 'build-failure'
    | 'test-failure'
    | 'security-critical'
    | 'static-analysis'
    | 'other';
  issueReference: {
    platform: string;
    repository: string;
    issueNumber: number;
    prNumber?: number;
  };
  failureContext: {
    retryHistory: Array<{
      attemptNumber: number;
      timestamp: Date;
      outcome: 'failure' | 'error';
      logs: string;
      fixAttempted?: string;
    }>;
    finalError: string;
    suggestedNextSteps: string[];
  };
  notificationChannels: Array<'cli' | 'webhook' | 'email' | 'slack'>;
  notifiedAt: Date;
  resolvedAt?: Date;
  resolutionNotes?: string;
  correlationId: string;
}

interface StaticAnalysisResult {
  tool: string; // 'eslint', 'pylint', 'rubocop', 'clippy', etc.
  executionTime: number;
  findings: AnalysisFinding[];
  autoFixesApplied: number;
  remainingIssues: number;
}

interface AnalysisFinding {
  severity: 'error' | 'warning' | 'info';
  ruleId: string;
  message: string;
  file: string;
  line: number;
  column: number;
  autoFixable: boolean;
  fixApplied: boolean;
}

interface SecurityScanResult {
  dependencyScanner?: {
    tool: string; // 'npm-audit', 'pip-audit', etc.
    vulnerabilities: Vulnerability[];
  };
  codeScanner?: {
    tool: string; // 'semgrep', 'bandit', etc.
    findings: SecurityFinding[];
  };
  summary: {
    criticalCount: number;
    highCount: number;
    mediumCount: number;
    lowCount: number;
  };
  escalationRequired: boolean;
}

interface Vulnerability {
  cve?: string;
  package: string;
  version: string;
  severity: 'critical' | 'high' | 'medium' | 'low';
  title: string;
  description: string;
  fixedVersion?: string;
  patchApplied: boolean;
}
```

**2. Intelligence Layer Models**

```typescript
interface AmbiguityScore {
  score: number; // 0-100, higher = more ambiguous
  factors: {
    vagueLanguage: number; // 0-30 points
    missingAcceptanceCriteria: number; // 0-20 points
    conflictingRequirements: number; // 0-25 points
    unusualFeature: number; // 0-15 points
    missingImplementationDetails: number; // 0-10 points
  };
  threshold: {
    high: number; // 70 - trigger clarifying questions
    veryHigh: number; // 90 - suggest issue breakdown
  };
  recommendation: 'proceed' | 'clarify' | 'breakdown';
}

interface ClarifyingQuestion {
  questionId: string;
  question: string;
  questionType: 'multiple-choice' | 'open-ended';
  options?: string[]; // For multiple-choice
  answer?: string;
  answeredAt?: Date;
}

interface ResearchQuery {
  queryId: string;
  concept: string;
  programmingLanguage?: string;
  framework?: string;
  query: string; // Full AI prompt
  findings: ResearchFindings;
  cachedUntil: Date; // 24 hours from creation
  manual: boolean; // True if triggered via CLI flag
}

interface ResearchFindings {
  summary: string; // 300-500 words
  apiDocumentation?: string;
  commonPatterns: string[];
  gotchas: string[];
  codeExamples: CodeExample[];
  sourceUrls?: string[];
}

interface CodeExample {
  language: string;
  description: string;
  code: string;
  comments?: string;
}

interface DesignOption {
  optionNumber: number; // 1, 2, 3
  title: string;
  description: string; // 2-3 paragraphs
  pros: string[];
  cons: string[];
  implementationComplexity: 1 | 2 | 3 | 4 | 5; // 1 = simple, 5 = complex
  testStrategy: string;
  estimatedEffort?: string; // e.g., "2-4 hours", "1-2 days"
}

interface DesignSelection {
  issueNumber: number;
  options: DesignOption[];
  selectedOption: number | 'custom';
  customDesign?: string; // If user selected 'custom'
  selectionRationale?: string;
  selectedAt: Date;
}
```

### APIs and Interfaces

**1. Quality Gates Interfaces**

```typescript
interface IBuildOrchestrator {
  triggerBuild(prNumber: number): Promise<BuildExecution>;
  monitorBuildStatus(buildId: string): Promise<BuildExecution>;
  analyzeBuildFailure(execution: BuildExecution): Promise<string>; // Returns suggested fix
  retryBuild(buildId: string, fix: string): Promise<BuildExecution>;
}

interface ITestRunner {
  executeTests(workingDir: string, testCommand?: string): Promise<TestExecution>;
  parseTestOutput(output: string, framework: string): Promise<TestExecution>;
  analyzeTestFailure(failure: TestFailure): Promise<string>; // Returns suggested fix
  retryTests(workingDir: string, previousExecution: TestExecution): Promise<TestExecution>;
}

interface IEscalationManager {
  createEscalation(event: EscalationEvent): Promise<string>; // Returns escalationId
  notifyChannels(escalationId: string, channels: string[]): Promise<void>;
  waitForResolution(escalationId: string, timeoutMs?: number): Promise<ResolutionResult>;
  markResolved(escalationId: string, notes: string): Promise<void>;
}

interface IStaticAnalyzer {
  detectTools(workingDir: string): Promise<string[]>; // Returns tool names
  runAnalysis(tool: string, workingDir: string): Promise<StaticAnalysisResult>;
  applyAutoFixes(result: StaticAnalysisResult): Promise<number>; // Returns fixes applied count
}

interface ISecurityScanner {
  scanDependencies(workingDir: string): Promise<SecurityScanResult>;
  scanCode(files: string[]): Promise<SecurityScanResult>;
  applyPatches(vulnerabilities: Vulnerability[]): Promise<number>; // Returns patches applied count
}
```

**2. Intelligence Layer Interfaces**

```typescript
interface IAmbiguityAnalyzer {
  scoreAmbiguity(issueContent: string): Promise<AmbiguityScore>;
  generateQuestions(issueContent: string, score: AmbiguityScore): Promise<ClarifyingQuestion[]>;
  incorporateAnswers(questions: ClarifyingQuestion[], context: any): Promise<void>;
}

interface IResearchService {
  detectUnfamiliarConcepts(planText: string): Promise<string[]>; // Returns concepts
  research(concept: string, language?: string): Promise<ResearchFindings>;
  getCachedResearch(concept: string): Promise<ResearchFindings | null>;
  manualResearch(query: string): Promise<ResearchFindings>;
}

interface IDesignProposalGenerator {
  generateProposals(issueContent: string, context: any): Promise<DesignOption[]>;
  presentProposals(options: DesignOption[]): Promise<DesignSelection>;
  integrateDesign(selection: DesignSelection, plan: any): Promise<void>;
}
```

### Workflows and Sequencing

**1. Build Automation Workflow (Story 3.1)**

```
Trigger: After PR creation (Story 2.8)

Sequence:
1. PRManager.createPR() completes
2. BuildOrchestrator.triggerBuild(prNumber)
   ├─ Detect CI platform from GitPlatformInterface
   ├─ Invoke platform-specific build trigger API
   └─ Return buildId

3. BuildMonitor.monitorBuildStatus(buildId)
   ├─ Poll every 15 seconds (max 10 minutes)
   ├─ If success → Continue to next workflow phase
   └─ If failure → Proceed to step 4

4. BuildFailureAnalyzer.analyze(buildExecution)
   ├─ Categorize failure type (compilation/dependency/timeout/config)
   ├─ If structural issue (missing config, invalid credentials) → Immediate escalation (Step 7)
   └─ If transient/solvable → Continue to step 5

5. BuildFixGenerator.generateFix(errorMessages, retryAttempt)
   ├─ Send error context to AI provider
   ├─ Receive suggested fix
   └─ Apply fix to codebase, commit

6. Retry Logic
   ├─ If retryAttempt < 3:
   │   ├─ Increment retryAttempt
   │   ├─ Exponential backoff: wait 2^retryAttempt seconds
   │   └─ GOTO Step 2 (retrigger build)
   └─ If retryAttempt == 3 → Escalation (Step 7)

7. EscalationManager.createEscalation()
   ├─ Post PR comment with failure context
   ├─ Add "needs-human-review" label
   ├─ Notify via configured channels
   └─ Pause workflow, wait for human resolution
```

**2. Test Execution Workflow (Story 3.2)**

```
Trigger: After implementation (Story 2.6) or after refactoring (Story 2.7)

Sequence:
1. CodeGenerator.generate() or RefactoringOrchestrator.refactor() completes
2. TestRunner.executeTests(workingDir)
   ├─ Detect test framework from package.json/pyproject.toml/Gemfile
   ├─ Run framework-specific test command
   └─ Capture output (stdout, stderr, exit code)

3. TestOutputParser.parse(output, framework)
   ├─ Parse pass/fail counts
   ├─ Extract failure messages and stack traces
   └─ Return TestExecution

4. If all tests pass:
   ├─ Reset retry counter to 0
   └─ Continue to next workflow phase

5. If tests fail:
   ├─ TestFailureAnalyzer.analyze(failures)
   ├─ Categorize failures (assertion vs exception vs timeout)
   ├─ If structural issue (missing test framework, corrupted files) → Immediate escalation
   └─ If solvable → Continue to step 6

6. TestFixGenerator.generateFix(failures, retryAttempt)
   ├─ Send failure context to AI provider
   ├─ Receive fix for test code or implementation code
   └─ Apply fix, commit

7. Retry Logic (same as build workflow)
   ├─ If retryAttempt < 3 → backoff, rerun tests from Step 2
   └─ If retryAttempt == 3 → Escalation

8. EscalationManager.createEscalation() (same as build workflow)
```

**3. Research + Clarifying Questions Workflow (Stories 3.4, 3.5, 3.6)**

```
Trigger: During issue analysis (Story 2.2) and plan generation (Story 2.3)

Sequence:
1. ContextAnalyzer.analyze() reads issue content

2. AmbiguityAnalyzer.scoreAmbiguity(issueContent)
   ├─ Run NLP heuristics (vague language, missing criteria, conflicts)
   ├─ Send to AI provider for context-dependent scoring
   └─ Return AmbiguityScore (0-100)

3. If score < 70:
   └─ Proceed to Step 8 (PlanGenerator)

4. If score 70-90:
   ├─ QuestionGenerator.generateQuestions(issueContent, score)
   ├─ Create 2-5 clarifying questions
   └─ Present to user via InteractiveQASession

5. If score > 90:
   ├─ Suggest breaking issue into smaller tasks
   ├─ Present suggestion to user
   └─ If user declines, proceed to clarifying questions (Step 4)

6. User answers questions
   ├─ ContextEnricher.incorporateAnswers(questions, context)
   └─ Update issue analysis with clarified requirements

7. Override check:
   ├─ If "skip-questions" label → Skip to Step 8
   └─ If "proceed-despite-ambiguity" label → Skip to Step 8

8. PlanGenerator.generate() begins

9. ConceptDetector.detectUnfamiliarConcepts(planText)
   ├─ Compare against known API registry
   ├─ Identify low-confidence concepts (< 60% match)
   └─ Return unfamiliar concepts list

10. For each unfamiliar concept:
    ├─ ResearchService.getCachedResearch(concept)
    ├─ If cache hit → Use cached findings
    └─ If cache miss:
        ├─ ResearchService.research(concept, language)
        ├─ Send research query to AI provider
        ├─ Parse and validate findings
        └─ Cache for 24 hours

11. ContextEnricher.incorporateResearch(findings, planContext)
    └─ Inject research summaries into plan generation context

12. PlanGenerator completes with enriched context
```

**4. Design Proposals Workflow (Story 3.7)**

```
Trigger: During plan generation (Story 2.3) for issues with "design-options-needed" label

Sequence:
1. PlanGenerator.generate() detects "design-options-needed" label

2. DesignProposalGenerator.generateProposals(issueContent, context)
   ├─ Send to AI provider: "Generate 2-3 design approaches..."
   ├─ Parse response into DesignOption objects
   └─ Validate each option has distinct tradeoffs

3. DesignSelectionInterface.presentProposals(options)
   ├─ Format options as numbered list with pros/cons
   ├─ Present via CLI or webhook (depending on mode)
   └─ Wait for user selection (1/2/3/custom)

4. If user selects option number:
   └─ Use selected DesignOption

5. If user selects 'custom':
   ├─ Prompt for inline design specification
   └─ Parse custom design into DesignOption format

6. DesignIntegrator.integrateDesign(selection, plan)
   ├─ Merge selected design into plan
   ├─ Log selection to event trail
   └─ Post design options + selection as PR comment

7. PlanGenerator continues with selected design
```

**5. Static Analysis + Security Scanning Workflow (Stories 3.8, 3.9)**

```
Trigger: After refactoring (Story 2.7), before PR creation (Story 2.8)

Sequence:
1. RefactoringOrchestrator.refactor() completes

2. StaticAnalyzerDetector.detectTools(workingDir)
   ├─ Search for config files (.eslintrc, .pylintrc, etc.)
   └─ Return detected tools list

3. For each detected tool:
   ├─ StaticAnalyzer.runAnalysis(tool, workingDir)
   ├─ Parse output into StaticAnalysisResult
   └─ If critical errors → Proceed to Step 4

4. AutoFixer.applyAutoFixes(result)
   ├─ Apply formatting, import sorting
   ├─ Rerun analysis to verify fixes
   └─ If errors remain → AI provider fix suggestions (retry logic applies)

5. SecurityScanner.scanDependencies(workingDir)
   ├─ Run platform-specific vulnerability scanner
   └─ Return dependency scan results

6. SecurityScanner.scanCode(changedFiles)
   ├─ Run SAST tool (Semgrep priority)
   └─ Return code scan results

7. VulnerabilityAnalyzer.analyze(scanResults)
   ├─ Categorize findings by severity
   └─ If critical vulnerabilities found → Immediate escalation (Step 9)

8. VulnerabilityPatcher.applyPatches(vulnerabilities)
   ├─ For medium/low vulnerabilities with fixes available
   ├─ Apply patches (e.g., dependency updates)
   ├─ Rescan to verify patches
   └─ Add remaining vulnerabilities as PR comment

9. Critical Vulnerability Escalation:
   ├─ Block PR creation
   ├─ EscalationManager.createEscalation()
   ├─ Notify security team
   └─ Wait for human resolution before proceeding

10. PRManager.createPR() continues with analysis results in PR description
```

**6. Parallel Execution Considerations**

Epic 3 services can execute in parallel where dependencies allow:

```
Parallel Opportunities:
├─ Static analysis (3.8) + Security scanning (3.9) → Independent, run concurrently
├─ Research queries (3.4) → Multiple concepts can be researched concurrently
└─ Clarifying questions (3.5) + Ambiguity scoring (3.6) → Scoring precedes questions, sequential

Sequential Dependencies:
├─ Build automation (3.1) → Must complete before test execution (3.2)
├─ Test execution (3.2) → Must complete before static analysis (3.8)
├─ Ambiguity scoring (3.6) → Must complete before clarifying questions (3.5)
└─ Research (3.4) → Must complete before plan generation (Story 2.3)
```

## Non-Functional Requirements

### Performance

**Quality Gates Performance Targets:**

- **Build Monitoring Latency:** CI/CD build status polling every 15 seconds with <500ms per poll operation (NFR-1 dashboard refresh requirement)
- **Test Execution Time:** Local test suite execution completes within project-specific timeout (configurable, default 10 minutes), contributes to <2 hour autonomous loop completion target (NFR-1)
- **Static Analysis Runtime:** Tool execution completes within 5 minutes for typical codebase (10K-50K LOC), parallelized when multiple tools detected
- **Security Scanning Time:** Dependency scan <1 minute, code scan <3 minutes for changed files only (not full codebase)
- **Retry Backoff Impact:** Exponential backoff adds maximum 14 seconds total delay (2s + 4s + 8s) across 3 retries, acceptable overhead for failure recovery
- **Escalation Notification Delivery:** Webhook POST completes within 5 seconds, email/Slack within 30 seconds (async, non-blocking)

**Intelligence Layer Performance Targets:**

- **Ambiguity Scoring:** Analysis completes within 5 seconds for typical issue content (500-2000 words), uses NLP heuristics + AI scoring in parallel
- **Research Query Response:** AI provider research query returns within 30 seconds (300-500 word response requirement), cached results retrieved in <100ms
- **Clarifying Question Generation:** 2-5 questions generated within 10 seconds, presented to user immediately
- **Design Proposal Generation:** 2-3 design options generated within 45 seconds (more complex AI prompt, acceptable for architectural decisions)
- **Research Cache Hit Rate:** Target 60%+ cache hit rate for common concepts (React, Express, PostgreSQL), reduces AI provider costs and latency

**Scalability Considerations:**

- Quality gates support 10+ concurrent autonomous loops without degradation (NFR-1 requirement), CI/CD API rate limits respected with backoff
- Research cache shared across all loops using Redis, supports 1000+ cached concepts with LRU eviction
- Parallel static analysis + security scanning reduces sequential bottleneck by ~50% (5 minutes → 2.5 minutes when run concurrently)

### Security

**Quality Gates Security Enforcement:**

- **Security Scanning Coverage:** 100% of dependencies scanned before PR creation, 100% of changed files scanned with SAST tool (FR-33)
- **Critical Vulnerability Blocking:** PRs with critical vulnerabilities (CVSS score ≥9.0) blocked immediately, no bypass mechanism, escalation required (FR-34)
- **Vulnerability Patch Validation:** Auto-applied patches re-scanned to verify fix, prevent regression
- **Scan Tool Security:** Semgrep, Bandit, npm-audit run in sandboxed environment, no arbitrary code execution during scan
- **Escalation Data Protection:** Escalation events contain failure context but mask secrets (API keys, passwords, tokens) before logging
- **PR Comment Security:** Security scan results posted as PR comments include severity counts but not exploit details for public repositories

**Intelligence Layer Security:**

- **Research Query Sanitization:** User-provided research queries sanitized to prevent prompt injection attacks to AI provider
- **Clarifying Question Data:** User answers to clarifying questions logged to event trail with encryption at rest (NFR-3 requirement)
- **Design Proposal Confidentiality:** Custom design specifications from users treated as sensitive, encrypted in event store
- **AI Provider Isolation:** All AI interactions route through `AIProviderInterface` with rate limiting, prevent provider abuse

**Credential Management:**

- CI/CD platform credentials (GitHub tokens, GitLab PAT) stored in OS keychain or secrets manager, never in plaintext config
- Notification channel credentials (webhook secrets, SMTP passwords, Slack tokens) encrypted at rest using AES-256
- Security scanner API keys (Snyk, Semgrep Cloud) rotated quarterly, revocation support for compromised keys

### Reliability/Availability

**Retry Logic Reliability (FR-16):**

- **Counter Reset Guarantee:** Retry counter resets to 0 immediately upon success, prevents false escalation after transient failures
- **Idempotency:** All retry operations idempotent (build retrigger, test rerun, fix application), safe to retry without side effects
- **Failure Classification Accuracy:** 95%+ accuracy distinguishing transient failures (network timeout, rate limit) from structural failures (missing files, invalid config), measured via telemetry
- **Escalation Consistency:** Escalation triggered reliably after 3 retries for transient failures OR immediately for structural failures, no edge cases bypass escalation (mandatory per FR-16)
- **Human Resolution Tracking:** Escalation state persisted to database, survives orchestrator restart, human can resume workflow from escalation point

**Quality Gates Availability:**

- **CI/CD Platform Resilience:** Build orchestrator handles platform API downtime gracefully, enters degraded mode (queue builds for retry), resumes when platform available
- **Test Framework Tolerance:** Test runner detects missing/broken test framework immediately (no retries wasted), escalates with actionable error message
- **Static Analysis Fallback:** If primary tool (ESLint) unavailable, skip with warning rather than block workflow, secondary tools still run
- **Security Scan Criticality:** Dependency scan failure blocks workflow only if critical vulnerabilities detected, medium/low vulnerabilities allow proceed with PR comment

**Intelligence Layer Reliability:**

- **Research Cache Persistence:** Redis cache backed up hourly, cache miss degrades gracefully to fresh AI query (slower but functional)
- **Ambiguity Scoring Fallback:** If AI provider unavailable, NLP heuristics alone provide 70% accuracy ambiguity score, better than no detection
- **Question Generation Degradation:** If question generation fails, workflow proceeds without clarifying questions (user can add "skip-questions" label retroactively)
- **Design Proposal Optional:** Design proposals only generated for "design-options-needed" label, workflow not dependent on this feature

**Autonomous Completion Rate Impact:**

- Quality gates + intelligence layer target 70%+ autonomous completion rate (NFR-2): retry logic resolves 50-60% of failures automatically, intelligence layer prevents 10-15% of ambiguous issue failures upfront
- PR rework rate <5% (NFR-2): Static analysis + security scanning catch issues before PR creation, reducing review feedback cycles

### Observability

**Quality Gates Observability:**

- **Build Monitoring Events:** Build triggered, build status poll, build success/failure, fix applied, retry attempt events logged with structured format (FR-25)
- **Test Execution Tracing:** Test run start, test framework detected, test results parsed, failure analyzed, fix generated events with test output snapshots
- **Escalation Visibility:** Escalation created, notification sent (per channel), human resolution started, resolution completed events with full context
- **Retry Metrics:** Counter: `quality_gate_retries_total{gate_type, outcome}`, Histogram: `quality_gate_retry_duration_seconds{gate_type}` (FR-26)
- **Static Analysis Metrics:** Gauge: `static_analysis_findings{tool, severity}`, Counter: `static_analysis_autofixes_applied{tool}`
- **Security Scan Metrics:** Gauge: `security_vulnerabilities{severity}`, Counter: `security_patches_applied_total`

**Intelligence Layer Observability:**

- **Ambiguity Scoring Logs:** Score calculated, factors breakdown, recommendation (proceed/clarify/breakdown), override label detected events
- **Research Query Tracing:** Concept detected, cache hit/miss, research query sent, findings received, cache stored events with query/response summaries
- **Question Generation Logs:** Questions generated count, question type distribution, answers received, context enriched events
- **Design Proposal Metrics:** Counter: `design_proposals_generated_total`, Counter: `design_proposals_custom_selected_total` (tracks user preferences)

**Alert Conditions (FR-29):**

- **Escalation Rate Threshold:** Alert if escalation rate >30% (indicates quality gate effectiveness issue or systemic codebase problems)
- **Retry Exhaustion Spike:** Alert if retry exhaustion events spike >3 standard deviations (potential CI/CD platform issue or codebase regression)
- **Security Scan Critical Findings:** Immediate alert (PagerDuty, Slack) when critical vulnerability detected, escalation workflow initiated
- **Intelligence Layer Degradation:** Alert if ambiguity scoring unavailable >5 minutes (AI provider outage impact)
- **Research Cache Miss Rate:** Alert if cache miss rate >50% (cache eviction policy issue or unusual concept surge)

**Debugging Support:**

- All quality gate and intelligence layer events include correlation ID for end-to-end tracing across Epic 2 workflow (FR-20, FR-22)
- Retry history stored in escalation events for time-travel debugging (Epic 4 integration)
- Test failure snapshots include full stack traces and expected/actual values for failure analysis
- Security scan results archived for compliance audit (90-day minimum retention per NFR-3)

## Dependencies and Integrations

### Epic Dependencies

**Epic 1 - Foundation & Core Infrastructure (REQUIRED):**

- **Story 1.1:** `AIProviderInterface` - Quality gates and intelligence layer route all AI interactions through this abstraction
- **Story 1.2:** `ClaudeCodeProvider` - Default AI provider for failure analysis, fix generation, research queries, ambiguity scoring
- **Story 1.3:** `ProviderConfigManager` - Configuration management for AI provider selection and routing
- **Story 1.4:** `GitPlatformInterface` - Build orchestrator uses this for CI/CD triggering and status monitoring
- **Story 1.5/1.6:** `GitHubPlatform`/`GitLabPlatform` - Platform-specific implementations for build triggering, PR commenting
- **Story 1.7:** `PlatformConfigManager` - Configuration for Git platform credentials and CI/CD integration
- **Dependency Risk:** If Epic 1 interfaces change during Epic 3 development, quality gates and intelligence layer integration points will break. Mitigation: Epic 1 APIs must be stable before Epic 3 begins.

**Epic 2 - Autonomous Development Loop - Core (REQUIRED):**

- **Story 2.2:** `ContextAnalyzer` - Intelligence layer extends this with ambiguity detection and clarifying questions
- **Story 2.3:** `PlanGenerator` - Intelligence layer extends this with research capability and design proposals
- **Story 2.6:** `CodeGenerator` - Test execution module runs after code generation completes
- **Story 2.7:** `RefactoringOrchestrator` - Static analysis and security scanning run after refactoring
- **Story 2.8:** `PRManager` - Build automation triggers after PR creation, security scans block PR creation if critical vulnerabilities
- **Story 2.10:** `MergeCoordinator` - Quality gates must complete successfully before merge approval
- **Dependency Risk:** Quality gates inject checkpoints into Epic 2 workflow phases. If Epic 2 state machine changes, quality gate integration points may break. Mitigation: Use event-driven integration pattern (Epic 4 events) rather than tight coupling.

**Epic 4 - Event Sourcing & Audit Trail (SOFT DEPENDENCY):**

- Quality gates and intelligence layer emit events through DCB event sourcing system
- Events: `BuildTriggeredEvent`, `TestExecutionStartedEvent`, `EscalationCreatedEvent`, `AmbiguityScoreCalculatedEvent`, `ResearchQueryEvent`
- Epic 3 functions without Epic 4 (logs to stdout), but event sourcing enables time-travel debugging and compliance audit
- Integration: Epic 3 services call `EventBus.publish()` for all significant actions, Epic 4 persists to event store

### External Dependencies

**Node.js Runtime & Core Libraries:**

- **Node.js:** 22 LTS (required for stable long-term support)
- **TypeScript:** 5.7+ (strict mode enabled for type safety)
- **pnpm:** 9.x for workspace management and dependency installation

**Quality Gates - Build & Test Dependencies:**

- **CI/CD Platform SDKs:**
  - `@actions/core`, `@actions/github` for GitHub Actions integration
  - `@gitbeaker/node` (v40.x) for GitLab CI integration
  - Platform-agnostic REST client (undici) for Gitea/Forgejo Actions
- **Test Framework Detection Libraries:**
  - No direct dependencies - detect via file system scanning (package.json, pytest.ini, .rspec, Cargo.toml)
- **Test Output Parsers:**
  - Jest: Parse JSON output via `--json` flag (built-in)
  - pytest: Parse JUnit XML output via `--junit-xml` flag
  - RSpec: Parse JSON output via `--format json` flag
  - Custom parsers for Go test, cargo test (plain text parsing)

**Quality Gates - Static Analysis Dependencies:**

- **Static Analyzer Executors:**
  - ESLint: Invoke via child_process (`eslint --format json`)
  - Pylint: Invoke via child_process (`pylint --output-format=json`)
  - RuboCop: Invoke via child_process (`rubocop --format json`)
  - golangci-lint: Invoke via child_process
  - Clippy: Invoke via child_process (`cargo clippy --message-format=json`)
- **No direct npm dependencies** - static analyzers are project dependencies, not Tamma dependencies
- **Configuration File Parsers:** Use `cosmiconfig` (v9.x) for flexible config file discovery

**Quality Gates - Security Scanning Dependencies:**

- **Dependency Scanners:**
  - npm audit: Built-in to npm CLI
  - yarn audit: Built-in to yarn CLI
  - pnpm audit: Built-in to pnpm CLI
  - pip-audit: Python package (`pip-audit --format json`)
  - bundle-audit: Ruby gem (`bundle-audit check --format json`)
  - govulncheck: Go tool (`govulncheck -json`)
  - cargo-audit: Rust tool (`cargo audit --json`)
- **SAST Tools (code scanners):**
  - Semgrep: Primary tool, invoke via CLI (`semgrep --json`)
  - Snyk Code: Optional, requires API key (`snyk code test --json`)
  - Bandit (Python): Invoke via CLI (`bandit -f json`)
  - Brakeman (Ruby): Invoke via CLI (`brakeman -f json`)
- **Vulnerability Databases:**
  - CVE data from NVD (National Vulnerability Database) - API access for enrichment
  - GitHub Advisory Database - accessed via GitHub GraphQL API
  - Snyk Vulnerability DB - accessed via Snyk REST API (if Snyk integration enabled)

**Intelligence Layer - NLP & Caching Dependencies:**

- **NLP Libraries:**
  - `natural` (v7.x) for NLP heuristics (tokenization, stemming, sentence similarity)
  - `compromise` (v14.x) for lightweight text analysis (pronoun detection, negation)
  - Alternative: `wink-nlp` (v2.x) if natural insufficient for ambiguity detection
- **Caching:**
  - `ioredis` (v5.x) for Redis client (research cache, 24-hour TTL)
  - Fallback: In-memory cache using `node-cache` (v5.x) if Redis unavailable
- **AI Provider Communication:**
  - Reuse `AIProviderInterface` from Epic 1 (no additional dependencies)
  - Research query prompts, ambiguity scoring, design proposal generation use same interface

**Notification & Alerting Dependencies:**

- **Webhook Notifications:**
  - `undici` (v6.x) for fast HTTP client (webhook POST)
  - Retry logic: `p-retry` (v6.x) with exponential backoff
- **Email Notifications (Optional):**
  - `nodemailer` (v6.x) for SMTP email sending
  - Alternative: SendGrid SDK (`@sendgrid/mail` v8.x), AWS SES SDK (`@aws-sdk/client-ses` v3.x)
- **Slack Notifications (Optional):**
  - `@slack/web-api` (v7.x) for Slack Bot API
  - Alternative: Slack webhook URL (undici POST)

**Database & State Persistence:**

- **PostgreSQL:** (Shared with Epic 2 for workflow state)
  - `pg` (v8.x) PostgreSQL client
  - `kysely` (v0.27.x) Type-safe SQL query builder (optional, improves DX)
- **Redis:** (Research cache + distributed locking)
  - `ioredis` (v5.x) as noted above
  - Used for: Research query cache (24h TTL), distributed lock for retry counter (prevent race conditions)

**Testing & Quality Assurance:**

- **Unit Testing:**
  - `jest` (v29.x) test framework with TypeScript support (`ts-jest`)
  - `@types/jest` (v29.x) for type definitions
- **Integration Testing:**
  - `nock` (v13.x) for HTTP mocking (mock CI/CD APIs, security scanner APIs)
  - `ioredis-mock` (v8.x) for Redis mocking
  - `pg-mem` (v3.x) for PostgreSQL in-memory database (fast integration tests)
- **Test Coverage:**
  - `c8` (v9.x) for native V8 coverage (faster than Istanbul)
  - Target: 80%+ line coverage for quality gates, 75%+ for intelligence layer

**Build & Development Tools:**

- **Build Tools:**
  - `typescript` compiler (tsc) for TypeScript compilation
  - `tsup` (v8.x) for fast bundling (alternative: `esbuild`)
- **Linting & Formatting:**
  - `eslint` (v9.x) with TypeScript plugin
  - `prettier` (v3.x) for code formatting
  - `lint-staged` + `husky` for pre-commit hooks
- **Monorepo Management:**
  - `turbo` (v2.x) for build orchestration across packages (optional performance optimization)

### Integration Points

**1. Epic 1 Integration (AI & Git Abstractions):**

```typescript
// Quality gates call AI provider for fix suggestions
const aiProvider = await providerRegistry.getProvider(config.defaultProvider);
const fixSuggestion = await aiProvider.sendMessage({
  messages: [{ role: 'user', content: `Analyze build failure: ${errorLog}` }],
  systemPrompt: 'You are a build failure expert. Suggest a fix.'
});

// Quality gates trigger builds via Git platform
const gitPlatform = await platformRegistry.getPlatform(config.platform);
await gitPlatform.triggerCI(prNumber, workflow: 'ci.yml');
```

**2. Epic 2 Integration (Workflow Orchestration):**

```typescript
// Intelligence layer extends ContextAnalyzer
class EnhancedContextAnalyzer extends ContextAnalyzer {
  async analyze(issue: Issue): Promise<Context> {
    const baseContext = await super.analyze(issue);
    const ambiguityScore = await this.ambiguityAnalyzer.score(issue.body);

    if (ambiguityScore.score > 70) {
      const questions = await this.questionGenerator.generate(issue, ambiguityScore);
      const answers = await this.interactiveSession.ask(questions);
      baseContext.enrichWithAnswers(answers);
    }

    return baseContext;
  }
}

// Quality gates inject checkpoints
class QualityGateMiddleware {
  async beforePRCreation(context: WorkflowContext): Promise<void> {
    await this.staticAnalyzer.run(context.workingDir);
    const scanResult = await this.securityScanner.scan(context.workingDir);

    if (scanResult.summary.criticalCount > 0) {
      throw new EscalationRequiredError('Critical vulnerabilities detected');
    }
  }
}
```

**3. Epic 4 Integration (Event Sourcing):**

```typescript
// Quality gates emit events
await eventBus.publish({
  type: 'BuildTriggeredEvent',
  aggregateId: workflowExecutionId,
  data: {
    buildId: build.id,
    ciPlatform: 'github-actions',
    triggeredBy: 'quality-gate',
    timestamp: new Date(),
  },
});

// Intelligence layer emits events
await eventBus.publish({
  type: 'AmbiguityScoreCalculatedEvent',
  aggregateId: issueId,
  data: {
    score: ambiguityScore.score,
    factors: ambiguityScore.factors,
    recommendation: ambiguityScore.recommendation,
    timestamp: new Date(),
  },
});
```

**4. External Service Integration:**

- **CI/CD Platforms:** Webhook subscriptions for build status updates (optional optimization over polling)
- **Security Scanning APIs:** CVE enrichment via NVD REST API, GitHub Advisory Database GraphQL queries
- **Notification Services:** Webhook delivery for escalations, email/Slack for critical alerts
- **Redis:** Research cache coordination across multiple orchestrator instances (distributed system support)

### Version Pinning Strategy

- **Critical Dependencies:** Pin exact versions for CI/CD SDKs, database clients (prevent breaking changes)
- **Development Tools:** Pin major versions for linters, formatters (allow patch updates via `~` semver)
- **AI Provider SDKs:** Pin minor versions (allow patches) to balance stability with bug fixes
- **Security Scanners:** Allow latest versions via `^` semver (important to get latest vulnerability definitions)

### Dependency Installation

```bash
# Epic 3 package installation (from project root)
cd packages/quality-gates
pnpm install

cd packages/intelligence
pnpm install

# External tool verification
npm audit --version  # Built-in
semgrep --version     # Install: pip install semgrep
eslint --version      # Project dependency, not Tamma dependency
```

### Breaking Change Management

- Epic 1 API changes: Use adapter pattern to isolate Epic 3 from changes
- Epic 2 workflow changes: Event-driven integration reduces coupling
- External library updates: CI/CD pipeline runs integration tests before merging dependency updates
- Security scanner breaking changes: Maintain compatibility with 2 most recent major versions (graceful degradation)

## Acceptance Criteria (Authoritative)

### Story 3.1: Build Automation with Retry Logic

**AC-3.1.1: Build Trigger Integration**

- System triggers build via platform-specific CI/CD API (GitHub Actions, GitLab CI, Gitea Actions, Forgejo Actions) after PR creation (Story 2.8)
- Build trigger includes workflow name/path, commit SHA, PR number as metadata
- Build trigger succeeds within 5 seconds or retries with exponential backoff (network transience)

**AC-3.1.2: Build Status Monitoring**

- System polls build status every 15 seconds until completion (max 10 minutes timeout)
- Poll interval configurable via `build.pollIntervalSeconds` config
- System parses platform-specific build status: pending → running → success/failure/cancelled

**AC-3.1.3: Build Failure Analysis**

- System retrieves build logs when status = failure (platform-specific log retrieval API)
- System categorizes failure: compilation error, dependency issue, timeout, configuration error, resource error
- Structural issues (missing build config, invalid credentials) trigger immediate escalation (no retry)

**AC-3.1.4: AI-Powered Fix Generation**

- System sends error logs + context to AI provider: "Analyze build failure and suggest fix"
- AI response validated: must include concrete fix (code change, config update, dependency change)
- Fix applied to codebase, committed with message "Fix build failure (attempt N/3)"

**AC-3.1.5: Intelligent Retry Logic**

- System allows maximum 3 retry attempts for transient/solvable failures
- Retry counter increments per attempt, resets to 0 on success
- Exponential backoff between retries: 2s → 4s → 8s
- Idempotent retries: safe to re-trigger builds without side effects

**AC-3.1.6: Escalation After Exhaustion**

- After 3 failed retries, system invokes `EscalationManager.createEscalation()` with full context
- Escalation includes: build IDs, all retry attempts with logs, error category, suggested next steps

**AC-3.1.7: Event Trail Logging**

- All build attempts logged as structured events: `BuildTriggeredEvent`, `BuildCompletedEvent`, `BuildRetryEvent`
- Events include: buildId, ciPlatform, status, retryAttempt, durationMs, errorMessages

### Story 3.2: Test Execution with Retry Logic

**AC-3.2.1: Test Framework Detection**

- System detects test framework from project files: package.json (Jest), pytest.ini (pytest), .rspec (RSpec), Cargo.toml (cargo test), go.mod (go test)
- Detection is automatic, no manual configuration required
- System supports fallback to generic test command if framework undetected

**AC-3.2.2: Local Test Execution**

- System executes test suite after implementation (Story 2.6) and after refactoring (Story 2.7)
- Test execution timeout: 10 minutes default, configurable via `test.timeoutSeconds`
- System captures stdout, stderr, exit code for analysis

**AC-3.2.3: Test Output Parsing**

- System parses test results: pass/fail counts, error messages, stack traces
- Parsing adapts to framework-specific output formats (JSON for Jest, JUnit XML for pytest, JSON for RSpec)
- System differentiates test failures (assertion failures) from test errors (unexpected exceptions)

**AC-3.2.4: Test Failure Analysis**

- System analyzes failures: identifies failed test names, files, error types, expected vs actual values
- Structural issues (missing test framework, corrupted test files) trigger immediate escalation (no retry)
- Transient issues (flaky tests, network failures in tests) eligible for retry

**AC-3.2.5: AI-Powered Test Fixes**

- System sends failure context to AI provider: "Analyze test failures and suggest fix for test code OR implementation code"
- AI determines whether test code is wrong or implementation code is wrong
- Fix applied, committed with message "Fix test failure (attempt N/3)"

**AC-3.2.6: Intelligent Retry Logic**

- System allows maximum 3 retry attempts for test failures
- Retry counter resets to 0 when all tests pass (not cumulative across test phases)
- Exponential backoff: 2s → 4s → 8s between test reruns
- All tests re-executed on retry (not just failed tests, to catch regressions)

**AC-3.2.7: Escalation After Exhaustion**

- After 3 failed retries, system escalates with full test output, failure history, stack traces

**AC-3.2.8: Event Trail Logging**

- Events logged: `TestExecutionStartedEvent`, `TestExecutionCompletedEvent`, `TestRetryEvent`
- Events include: testRunId, framework, totalTests, passedTests, failedTests, retryAttempt, failures array

### Story 3.3: Mandatory Escalation Workflow

**AC-3.3.1: Escalation Trigger Conditions**

- Escalation triggered when retry limit reached (3 attempts) for build, test, or any quality gate
- Escalation triggered immediately for structural failures (missing config, corrupted files, invalid credentials)
- Escalation triggered immediately for critical security vulnerabilities (Story 3.9)

**AC-3.3.2: Escalation Event Creation**

- System creates `EscalationEvent` with unique escalationId, correlation ID, failure type
- Event includes retry history: all attempts with timestamps, logs, fixes attempted
- Event includes suggested next steps: actionable recommendations for human resolution

**AC-3.3.3: PR Comment Notification**

- System posts PR comment: "❌ Escalation Required: [issue type] failed after 3 attempts. Review needed."
- Comment includes expandable details: retry history, error summary, links to full logs
- System adds "needs-human-review" label to PR

**AC-3.3.4: Multi-Channel Notifications**

- System sends notifications via all configured channels: CLI output (standalone), webhook (orchestrator), email, Slack
- Webhook POST includes full escalation context as JSON payload
- Email includes formatted summary with links to PR
- Slack notification includes interactive buttons ("Resolve", "View PR", "View Logs")

**AC-3.3.5: Workflow Pause**

- System pauses autonomous loop for this issue (does not auto-select next issue per Story 2.11)
- Workflow state persisted to database, survives orchestrator restart
- System polls for human resolution marker (e.g., "resolved" label on PR, API call to `/escalations/:id/resolve`)

**AC-3.3.6: Human Resolution Tracking**

- System waits for resolution marker before resuming workflow
- Resolution includes human-provided notes explaining what was fixed
- System logs `EscalationResolvedEvent` with resolution timestamp, notes, resolver identity

**AC-3.3.7: Escalation Metrics**

- System tracks escalation rate: `escalations_total{escalation_type, outcome}`
- Alert if escalation rate >30% (indicates systemic issue)

**AC-3.3.8: Event Trail Logging**

- Events logged: `EscalationCreatedEvent`, `EscalationNotifiedEvent`, `EscalationResolvedEvent`
- Events include full escalation context with correlation ID for end-to-end tracing

### Story 3.4: Research Capability for Unfamiliar Concepts

**AC-3.4.1: Concept Detection**

- During plan generation (Story 2.3), system identifies unfamiliar terms using `ConceptDetector`
- Detection uses known API registry (local database) with fuzzy matching
- Confidence threshold: <60% match triggers research query

**AC-3.4.2: Research Query Generation**

- System generates AI provider query: "Research [concept] for [programming language]: API documentation, common patterns, gotchas, code examples"
- Query includes project context (language, framework) for relevance
- Query validation: must be specific, not generic (e.g., "React hooks" not "programming")

**AC-3.4.3: Research Response Validation**

- AI response must be 300-500 words with at least 1 code example
- Response parsed into `ResearchFindings` structure: summary, commonPatterns, gotchas, codeExamples
- Invalid responses (too short, no examples) re-queried once, then skipped

**AC-3.4.4: Context Incorporation**

- Research findings injected into `PlanGenerator` context before code generation
- Findings formatted as "Background Research: [concept]" section in plan
- Code examples from research used as templates for implementation

**AC-3.4.5: Research Caching**

- Research findings cached in Redis with 24-hour TTL
- Cache key: `research:{concept}:{language}:{framework}`
- Cache hit returns findings in <100ms, cache miss triggers fresh AI query

**AC-3.4.6: Manual Research Trigger**

- CLI supports `--research "[query]"` flag for explicit research requests
- Manual research bypasses concept detection, directly queries AI provider
- Manual research logged with `manual: true` flag in `ResearchQuery` model

**AC-3.4.7: Event Trail Logging**

- Events logged: `ResearchQueryEvent`, `ResearchCacheHitEvent`, `ResearchFindingsReceivedEvent`
- Events include: concept, query, cache status, findings summary, AI provider used

### Story 3.5: Clarifying Questions for Ambiguous Requirements

**AC-3.5.1: Ambiguity Detection Integration**

- During issue analysis (Story 2.2), system invokes `AmbiguityAnalyzer.scoreAmbiguity()`
- If score > 70, system triggers clarifying questions workflow
- If "skip-questions" label present, skip question generation

**AC-3.5.2: Question Generation**

- System generates 2-5 clarifying questions based on ambiguity factors
- Questions prioritize multiple-choice format (easier user experience)
- Open-ended questions used only for complex ambiguity requiring free text
- Question quality validation: no yes/no questions without context, no redundant questions

**AC-3.5.3: Interactive Q&A Session**

- System presents questions via CLI (standalone mode) or webhook (orchestrator mode)
- CLI: Interactive prompts with numbered options, "Other" option for free text
- Orchestrator: Webhook POST with question payload, async answer collection via callback
- Timeout handling: 10 minute timeout in orchestrator mode, escalate if no response

**AC-3.5.4: Answer Incorporation**

- User answers incorporated into issue context via `ContextEnricher.incorporateAnswers()`
- Answers formatted as "Requirements Clarification:" section in plan
- Answers logged to event trail and posted as PR comment for visibility

**AC-3.5.5: Override Mechanism**

- "skip-questions" label bypasses question generation entirely
- "proceed-despite-ambiguity" label allows proceeding without answering questions
- Override decision logged with rationale

**AC-3.5.6: Event Trail Logging**

- Events logged: `ClarifyingQuestionsGeneratedEvent`, `QuestionsAnsweredEvent`, `QuestionsSkippedEvent`
- Events include: questions array, answers array, ambiguity score, override status

### Story 3.6: Ambiguity Detection Scoring

**AC-3.6.1: Ambiguity Score Calculation**

- System analyzes issue content and assigns score (0-100, higher = more ambiguous)
- Score uses combined approach: NLP heuristics (70% weight) + AI scoring (30% weight)
- Calculation completes within 5 seconds for typical issue (500-2000 words)

**AC-3.6.2: Scoring Factors**

- Vague language detection: "maybe", "probably", "should", "could", "might" (0-30 points)
- Missing acceptance criteria: No "Acceptance Criteria" section (−20 points)
- Conflicting requirements: Sentence similarity >80% with negation (0-25 points)
- Unusual feature: Not in known feature registry (0-15 points)
- Missing implementation details: No technical specifications (0-10 points)

**AC-3.6.3: Threshold-Based Interventions**

- Score <70: Proceed without intervention, log score for analytics
- Score 70-90: Prompt user: "⚠️ High ambiguity detected. Proceed with clarifying questions? [Y/n]"
- Score >90: Suggest issue breakdown: "Consider breaking issue into smaller, clearer tasks"
- User can accept/decline suggestions, decision logged

**AC-3.6.4: Score Display**

- Ambiguity score displayed in CLI output with color coding: Green (<50), Yellow (50-70), Orange (70-90), Red (>90)
- Score included in PR description: "Ambiguity Score: 45/100 (Low Risk)"
- Score breakdown (factors) available in event trail for debugging

**AC-3.6.5: Override Mechanism**

- "proceed-despite-ambiguity" label allows override for scores >90
- Override logged with justification

**AC-3.6.6: Event Trail Logging**

- Events logged: `AmbiguityScoreCalculatedEvent`, `AmbiguityInterventionTriggeredEvent`
- Events include: score, factors breakdown, recommendation, user decision

### Story 3.7: Multi-Option Design Proposals

**AC-3.7.1: Label-Based Trigger**

- System detects "design-options-needed" label on issue during plan generation (Story 2.3)
- Trigger is explicit (label required), not automatic
- If label absent, single design approach generated (default Epic 2 behavior)

**AC-3.7.2: Design Proposal Generation**

- System generates 2-3 alternative design approaches via AI provider
- Each option includes: title, description (2-3 paragraphs), pros/cons lists, implementation complexity (1-5 scale), test strategy, estimated effort
- Options validated: must have distinct tradeoffs (different pros/cons)

**AC-3.7.3: Design Presentation**

- System presents options via CLI with numbered list, formatted pros/cons
- CLI includes "custom" option for user-specified design
- Orchestrator mode: Webhook POST with design options, async selection via callback

**AC-3.7.4: Design Selection**

- User selects option via interactive prompt: "Select design [1/2/3/custom]"
- If "custom", system prompts for inline design specification (multi-line text)
- Selection timeout: 10 minutes in orchestrator mode, escalate if no response

**AC-3.7.5: Design Integration**

- Selected design merged into development plan via `DesignIntegrator.integrateDesign()`
- Design incorporated as "Architectural Decision:" section in plan
- Test strategy from design incorporated into test generation (Story 2.5)

**AC-3.7.6: Design Documentation**

- Design options and selection posted as PR comment for team visibility
- Comment includes rationale for selection (user-provided or default)
- Selection logged to event trail with full context

**AC-3.7.7: Event Trail Logging**

- Events logged: `DesignProposalsGeneratedEvent`, `DesignSelectedEvent`, `CustomDesignProvidedEvent`
- Events include: options array, selectedOption, selectionRationale, integrationStatus

### Story 3.8: Static Analysis Integration

**AC-3.8.1: Static Analyzer Detection**

- System detects project's static analysis tools from config files: .eslintrc (ESLint), .pylintrc (Pylint), .rubocop.yml (RuboCop), .golangci.yml (golangci-lint), Cargo.toml (Clippy)
- Detection is automatic, no manual configuration required
- System supports multiple tools per project (e.g., ESLint + Prettier)

**AC-3.8.2: Analysis Execution Timing**

- System runs static analysis after implementation (Story 2.6) and after refactoring (Story 2.7)
- Analysis runs before PR creation (Story 2.8) as pre-commit gate
- Analysis timeout: 5 minutes default, configurable via `staticAnalysis.timeoutSeconds`

**AC-3.8.3: Analysis Output Parsing**

- System captures analysis output (errors, warnings, suggestions)
- Parsing adapts to tool-specific output formats (JSON for ESLint, JSON for Pylint, JSON for RuboCop)
- System categorizes findings by severity: error (blocking), warning (non-blocking), info (suggestions)

**AC-3.8.4: Auto-Fix Application**

- If critical errors found, system applies auto-fixes (formatting, import sorting, simple rule violations)
- Auto-fixes applied via tool's built-in fixer (e.g., `eslint --fix`, `rubocop --auto-correct`)
- System re-runs analysis after auto-fixes to verify fixes
- Auto-fix count logged: `static_analysis_autofixes_applied{tool}`

**AC-3.8.5: AI-Powered Fix Suggestions**

- If errors remain after auto-fixes, system sends to AI provider for fix suggestions
- AI fixes subject to intelligent retry logic: 3 attempts with exponential backoff
- Structural issues (missing tool config, invalid syntax in config) trigger immediate escalation

**AC-3.8.6: PR Description Integration**

- Static analysis results included in PR description: "Static Analysis: X errors, Y warnings, Z auto-fixes applied"
- Link to detailed findings in PR comment
- Findings formatted as checklist for reviewer visibility

**AC-3.8.7: Event Trail Logging**

- Events logged: `StaticAnalysisStartedEvent`, `StaticAnalysisCompletedEvent`, `StaticAnalysisFixAppliedEvent`
- Events include: tool, executionTime, findings count by severity, autoFixesApplied

### Story 3.9: Security Scanning Integration

**AC-3.9.1: Dependency Scanning**

- System runs dependency vulnerability scanner before PR creation (Story 2.8)
- Platform-specific scanners: npm audit (Node.js), pip-audit (Python), bundle-audit (Ruby), govulncheck (Go), cargo-audit (Rust)
- Scanner invoked via CLI with JSON output: `npm audit --json`, `pip-audit --format json`

**AC-3.9.2: Code Scanning (SAST)**

- System runs code security scanner on changed files only (not full codebase)
- Primary tool: Semgrep (multi-language support), fallback: Snyk Code, Bandit (Python), Brakeman (Ruby)
- Scanner invoked via CLI: `semgrep --json`, `bandit -f json`

**AC-3.9.3: Vulnerability Classification**

- System categorizes findings by severity: critical (CVSS ≥9.0), high (CVSS 7.0-8.9), medium (CVSS 4.0-6.9), low (CVSS <4.0)
- Critical vulnerabilities trigger immediate escalation (no retry, blocks PR creation)
- High vulnerabilities escalate if no fix available
- Medium/low vulnerabilities allow PR creation with PR comment

**AC-3.9.4: Vulnerability Patching**

- For medium/low vulnerabilities with fixes available (e.g., dependency updates), system auto-applies patches
- Patches applied: `npm update [package]`, `pip install --upgrade [package]`
- System re-scans after patching to verify fix
- Patch count logged: `security_patches_applied_total{severity}`

**AC-3.9.5: Critical Vulnerability Blocking**

- If critical vulnerabilities detected, system blocks PR creation immediately (no bypass)
- System invokes `EscalationManager.createEscalation()` with security-specific context
- Escalation notification includes CVE IDs, affected packages, exploit details (if public)

**AC-3.9.6: PR Comment Integration**

- Security scan results included in PR description: "Security Scan: X critical, Y high, Z medium vulnerabilities"
- Remaining vulnerabilities (after patching) posted as PR comment with recommended actions
- Critical/high vulnerabilities marked as "Action Required"

**AC-3.9.7: Event Trail Logging**

- Events logged: `SecurityScanStartedEvent`, `VulnerabilityDetectedEvent`, `SecurityPatchAppliedEvent`, `SecurityEscalationTriggeredEvent`
- Events include: scanner tools, vulnerabilities by severity, CVE IDs, patches applied, escalation status

## Traceability Mapping

### PRD Functional Requirements → Epic 3 Stories

| PRD Requirement                                                                            | Epic 3 Story              | Implementation Detail                                                                                                                                                                                                          |
| ------------------------------------------------------------------------------------------ | ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **FR-16:** Quality gates at build, test, CI/CD with 3-retry limit and mandatory escalation | **Stories 3.1, 3.2, 3.3** | Build automation (3.1) + test execution (3.2) implement intelligent retry logic with counter reset on success. Mandatory escalation (3.3) triggered after 3 retries OR immediately for structural failures. No bypass allowed. |
| **FR-17:** Automated testing with retry logic and escalation                               | **Story 3.2**             | Test execution module runs after code generation/refactoring, parses framework-specific output, retries with AI-powered fixes, escalates after 3 attempts.                                                                     |
| **FR-18:** Automated code review and feedback                                              | **Story 3.8**             | Static analysis integration runs linters/formatters, applies auto-fixes, sends remaining issues to AI provider for fix suggestions.                                                                                            |
| **FR-3:** Clarifying questions for ambiguous specifications                                | **Stories 3.5, 3.6**      | Ambiguity scoring (3.6) quantifies ambiguity (0-100 scale), clarifying questions (3.5) generated for scores >70 with interactive Q&A session.                                                                                  |
| **FR-4:** Multi-option design proposals with tradeoffs                                     | **Story 3.7**             | Design proposal generator creates 2-3 alternatives with pros/cons for issues labeled "design-options-needed", user selects via CLI or webhook.                                                                                 |
| **FR-33:** Security vulnerability scanning and auto-fix                                    | **Story 3.9**             | Dependency + code scanning before PR creation, critical vulnerabilities block PR with escalation, medium/low auto-patched if fixes available.                                                                                  |

### PRD Non-Functional Requirements → Epic 3 Implementation

| PRD NFR                                          | Epic 3 Implementation                                                                                             | Verification Method                               |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------- | ------------------------------------------------- |
| **NFR-1:** <2 hour autonomous loop completion    | Build monitoring 15s poll, test execution <10min, static analysis <5min, security scan <4min                      | Performance testing with representative codebases |
| **NFR-2:** 70%+ autonomous completion rate       | Retry logic resolves 50-60% of transient failures, intelligence layer prevents 10-15% of ambiguous issue failures | Telemetry tracking over 100+ issues               |
| **NFR-2:** <5% PR rework rate                    | Static analysis + security scanning catch issues before PR creation                                               | PR revision count tracking                        |
| **NFR-3:** Encryption at rest for sensitive data | Clarifying question answers encrypted in event store, escalation events mask secrets                              | Security audit + penetration testing              |
| **NFR-1:** Real-time dashboard <500ms refresh    | Build status polling <500ms, metrics exposed via Prometheus                                                       | Load testing with 10+ concurrent loops            |

### Epic 3 Stories → Acceptance Criteria → Test Cases

| Story                         | Acceptance Criteria Count | Test Cases                                       | Coverage Target                        |
| ----------------------------- | ------------------------- | ------------------------------------------------ | -------------------------------------- |
| **3.1: Build Automation**     | 7 ACs                     | 18 unit tests, 12 integration tests, 5 E2E tests | 85%+ line coverage                     |
| **3.2: Test Execution**       | 8 ACs                     | 20 unit tests, 15 integration tests, 6 E2E tests | 85%+ line coverage                     |
| **3.3: Escalation Workflow**  | 8 ACs                     | 15 unit tests, 10 integration tests, 4 E2E tests | 90%+ line coverage (critical path)     |
| **3.4: Research Capability**  | 7 ACs                     | 12 unit tests, 8 integration tests, 3 E2E tests  | 80%+ line coverage                     |
| **3.5: Clarifying Questions** | 6 ACs                     | 14 unit tests, 10 integration tests, 4 E2E tests | 80%+ line coverage                     |
| **3.6: Ambiguity Scoring**    | 6 ACs                     | 16 unit tests, 6 integration tests, 2 E2E tests  | 75%+ line coverage                     |
| **3.7: Design Proposals**     | 7 ACs                     | 12 unit tests, 8 integration tests, 3 E2E tests  | 75%+ line coverage                     |
| **3.8: Static Analysis**      | 7 ACs                     | 18 unit tests, 12 integration tests, 5 E2E tests | 85%+ line coverage                     |
| **3.9: Security Scanning**    | 7 ACs                     | 20 unit tests, 15 integration tests, 6 E2E tests | 90%+ line coverage (security critical) |
| **TOTAL**                     | **63 ACs**                | **145 unit, 96 integration, 38 E2E = 279 total** | **82% avg coverage**                   |

### Test Case Traceability Examples

**AC-3.1.5 (Intelligent Retry Logic) → Test Cases:**

1. `BuildOrchestrator.test.ts::should reset retry counter on success` (Unit)
2. `BuildOrchestrator.test.ts::should escalate after 3 failed retries` (Unit)
3. `BuildOrchestrator.test.ts::should apply exponential backoff` (Unit)
4. `BuildWorkflow.integration.test.ts::full retry cycle with eventual success` (Integration)
5. `BuildWorkflow.integration.test.ts::retry exhaustion triggers escalation` (Integration)
6. `AutonomousLoop.e2e.test.ts::build failure recovery within retry limit` (E2E)

**AC-3.6.2 (Ambiguity Scoring Factors) → Test Cases:**

1. `AmbiguityAnalyzer.test.ts::should detect vague language` (Unit)
2. `AmbiguityAnalyzer.test.ts::should penalize missing acceptance criteria` (Unit)
3. `AmbiguityAnalyzer.test.ts::should detect conflicting requirements` (Unit)
4. `AmbiguityAnalyzer.test.ts::should combine NLP and AI scoring` (Unit)
5. `IntelligenceLayer.integration.test.ts::ambiguity scoring with real issues` (Integration)

**AC-3.9.3 (Critical Vulnerability Blocking) → Test Cases:**

1. `SecurityScanner.test.ts::should block PR for critical vulnerabilities` (Unit)
2. `SecurityScanner.test.ts::should allow PR for medium vulnerabilities` (Unit)
3. `SecurityWorkflow.integration.test.ts::critical vuln triggers escalation` (Integration)
4. `SecurityWorkflow.integration.test.ts::dependency patching for medium vulns` (Integration)
5. `AutonomousLoop.e2e.test.ts::security gate blocks PR with critical CVE` (E2E)

### Acceptance Criteria Validation Checklist

✅ **63 acceptance criteria** defined across 9 stories (average 7 ACs per story)
✅ **All ACs testable** - each AC has measurable pass/fail condition
✅ **PRD requirements covered** - FR-3, FR-4, FR-16, FR-17, FR-18, FR-33 fully implemented
✅ **NFR targets specified** - Performance, reliability, security NFRs measurable
✅ **279 test cases planned** - 145 unit, 96 integration, 38 E2E (82% avg coverage target)
✅ **Traceability complete** - PRD → Epic 3 → ACs → Tests fully mapped

## Risks, Assumptions, Open Questions

### Technical Risks

**R-3.1: Retry Logic Counter Reset Race Condition** _(HIGH)_

- **Description:** In orchestrator mode with distributed workers, retry counter reset on success could have race condition if multiple workers process same build/test event simultaneously.
- **Impact:** Retry counter may not reset properly, leading to false escalation after 3 cumulative successes.
- **Mitigation:** Use distributed lock (Redis) for retry counter updates, atomic increment/reset operations, include workflow execution ID in lock key to prevent cross-workflow conflicts.
- **Likelihood:** Medium (distributed systems inherently have concurrency challenges)
- **Detection:** Integration tests with concurrent workers, retry counter audit in event trail

**R-3.2: AI Provider Availability During Quality Gates** _(HIGH)_

- **Description:** Quality gates (build fixes, test fixes, static analysis fixes) depend on AI provider for fix suggestions. If provider unavailable during critical failure, retry loop stalls.
- **Impact:** Workflow paused indefinitely waiting for AI response, autonomous completion rate drops below 70% target.
- **Mitigation:** Implement fallback to generic fixes for common errors (dependency version bumps, import statement fixes), circuit breaker pattern with 3-minute timeout, escalate if AI unavailable after 2 retry attempts.
- **Likelihood:** Medium (AI providers have 99%+ uptime but occasional outages occur)
- **Detection:** AI provider latency metrics, circuit breaker trip events

**R-3.3: Security Scanner False Positives Blocking PRs** _(MEDIUM)_

- **Description:** Security scanners (Semgrep, Snyk) may report false positive critical vulnerabilities, blocking PR creation unnecessarily.
- **Impact:** Legitimate PRs blocked by false positives, requiring manual override, reduces autonomous completion rate.
- **Mitigation:** Implement vulnerability suppression file (`.tamma-security-suppressions.yml`) for known false positives, require human review/justification for suppressions, log suppression decisions to audit trail.
- **Likelihood:** Medium (false positives common in SAST tools, especially for complex codebases)
- **Detection:** Escalation metrics for security blocks, manual review of escalated security findings

**R-3.4: Test Framework Detection Failures** _(MEDIUM)_

- **Description:** Test runner may fail to detect test framework for non-standard project structures (monorepos with custom test configs, polyglot repos).
- **Impact:** Tests not executed, false sense of passing tests, quality gate bypassed unintentionally.
- **Mitigation:** Support custom test command configuration override, fallback to generic test execution with configurable command, immediate escalation if no test framework detected and no custom config provided.
- **Likelihood:** Medium (project structures vary widely, detection heuristics may miss edge cases)
- **Detection:** Test execution events with `framework: 'unknown'`, escalation count for missing test frameworks

**R-3.5: Ambiguity Scoring Accuracy Variability** _(MEDIUM)_

- **Description:** Ambiguity scoring combines NLP heuristics (70%) and AI scoring (30%). AI scoring may be inconsistent across providers (Claude vs. OpenAI), leading to score variability.
- **Impact:** Inconsistent triggering of clarifying questions, user confusion when scores vary for similar issues.
- **Mitigation:** Pin AI provider for ambiguity scoring (use Claude for consistency), cache AI scores for 24 hours to reduce variability within short timeframe, log AI provider used in `AmbiguityScoreCalculatedEvent` for debugging.
- **Likelihood:** Medium (AI providers have different scoring sensitivities)
- **Detection:** Score distribution analytics, A/B testing across providers

**R-3.6: Research Cache Stale Data** _(LOW)_

- **Description:** Research findings cached for 24 hours may become stale if API changes rapidly (e.g., breaking changes in beta APIs).
- **Impact:** Implementation uses outdated API patterns, tests fail, retry logic invoked unnecessarily.
- **Mitigation:** Allow manual cache invalidation via CLI flag `--clear-research-cache`, include cache timestamp in research findings display, reduce TTL to 12 hours for beta/unstable APIs (configurable per concept).
- **Likelihood:** Low (most APIs stable over 24 hours)
- **Detection:** Research-related test failures after cache hit, user reports of outdated patterns

**R-3.7: Static Analysis Tool Version Compatibility** _(LOW)_

- **Description:** Static analyzers (ESLint, Pylint) are project dependencies, not Tamma dependencies. Version mismatches may cause parsing failures (unexpected output format).
- **Impact:** Static analysis fails to parse output, treats as structural failure, escalates unnecessarily.
- **Mitigation:** Support multiple output format versions per tool, graceful degradation if parsing fails (log warning, skip analysis for that tool), document supported tool version ranges.
- **Likelihood:** Low (output formats rarely change drastically)
- **Detection:** Static analysis parsing errors, escalation events with parsing failures

**R-3.8: Clarifying Question Timeout in Orchestrator Mode** _(MEDIUM)_

- **Description:** Orchestrator mode sends clarifying questions via webhook, waits for async response. If webhook delivery fails or user doesn't respond within 10 minutes, workflow stalls.
- **Impact:** Workflow paused indefinitely, autonomous loop stops, requires manual intervention.
- **Mitigation:** Implement timeout handler with escalation after 10 minutes, send reminder notification at 5 minutes, allow admin override via API call to skip questions and proceed.
- **Likelihood:** Medium (webhook delivery can fail, users may not be available)
- **Detection:** Timeout events in audit trail, webhook delivery failure metrics

**R-3.9: Build Log Size Exceeding AI Provider Context Limits** _(LOW)_

- **Description:** Large build logs (>100K tokens) may exceed AI provider context limits when sending for fix suggestions.
- **Impact:** AI provider rejects request, fix suggestion fails, escalates after 3 retries (expected behavior but suboptimal).
- **Mitigation:** Truncate build logs to last 50K tokens before sending to AI provider, prioritize error messages and stack traces over verbose output, log truncation decision for transparency.
- **Likelihood:** Low (most build logs <10K tokens)
- **Detection:** AI provider context limit errors, truncation events in audit trail

**R-3.10: Design Proposal Generation Cost** _(LOW)_

- **Description:** Generating 2-3 design proposals requires 3x AI provider calls (one per option), significantly increases AI provider costs for labeled issues.
- **Impact:** Higher operational costs for design-heavy projects, potential budget overruns.
- **Mitigation:** Design proposals only triggered by explicit label ("design-options-needed"), document cost implications in user guide, consider caching design proposals for similar issues (deferred to post-MVP).
- **Likelihood:** Low (design proposals optional feature)
- **Detection:** AI provider cost metrics segmented by request type

### Assumptions

**A-3.1: Epic 1 AI Provider Interface Stability**

- Assumes Epic 1's `AIProviderInterface` API is stable and will not introduce breaking changes during Epic 3 development.
- If API changes, quality gates and intelligence layer will require adapter updates.
- **Validation Required:** Confirm API freeze with Epic 1 team before Epic 3 Story 3.1 begins.

**A-3.2: Epic 1 Git Platform Interface Stability**

- Assumes Epic 1's `GitPlatformInterface` supports CI/CD triggering and build status monitoring for all platforms (GitHub, GitLab, Gitea, Forgejo).
- If missing, Epic 3 will need to implement direct platform API calls (breaks abstraction).
- **Validation Required:** Verify CI/CD methods exist in `GitPlatformInterface` (Story 1.4).

**A-3.3: Epic 2 Workflow State Machine Extensibility**

- Assumes Epic 2's workflow state machine supports middleware pattern for quality gate injection.
- If not, Epic 3 will require state machine refactoring (higher complexity).
- **Validation Required:** Review Epic 2 state machine design for extension points before Story 3.1.

**A-3.4: Test Frameworks Support JSON/XML Output**

- Assumes all major test frameworks (Jest, pytest, RSpec, cargo test, go test) support JSON or XML structured output.
- If not, Epic 3 will need custom parsers for plain text output (lower reliability).
- **Validation Required:** Document supported test framework versions and output formats in user guide.

**A-3.5: Static Analysis Tools Installed in Project**

- Assumes static analyzers (ESLint, Pylint, etc.) are already installed as project dependencies.
- Tamma does not install static analyzers automatically.
- **Validation Required:** Document prerequisite in installation guide, detect missing tools and skip gracefully with warning.

**A-3.6: Security Scanners Available on System Path**

- Assumes security scanning tools (Semgrep, npm audit, pip-audit) are available on system PATH or as project dependencies.
- If missing, security scanning skipped with warning (not failure).
- **Validation Required:** Document installation instructions for Semgrep (primary SAST tool).

**A-3.7: Redis Available for Research Cache (Orchestrator Mode)**

- Assumes Redis instance available for research cache in orchestrator mode.
- If unavailable, fallback to in-memory cache (node-cache), but cache not shared across workers.
- **Validation Required:** Document Redis as recommended dependency, test fallback behavior.

**A-3.8: Webhook Endpoints Accept POST with JSON Payload**

- Assumes webhook endpoints (for escalations, clarifying questions, design proposals) accept HTTP POST with JSON payload.
- If endpoint requires different format, integration will fail.
- **Validation Required:** Document webhook payload schemas in API reference.

**A-3.9: AI Provider Supports Streaming Responses**

- Assumes AI provider supports streaming for long fix suggestions (build logs, test failures).
- If not, Epic 3 falls back to non-streaming (slower but functional).
- **Validation Required:** Verify streaming support in Epic 1 `AIProviderInterface.sendMessage()`.

**A-3.10: 24-Hour Research Cache TTL Acceptable**

- Assumes 24-hour TTL for research cache is acceptable trade-off between freshness and cost.
- If APIs change more frequently, users may need manual cache invalidation.
- **Validation Required:** Gather user feedback during alpha testing, consider configurable TTL per concept.

### Open Questions

**Q-3.1: Should retry counter reset be per-action or per-workflow-phase?**

- Current spec: Retry counter resets per action (build succeeds → counter to 0, test succeeds → counter to 0).
- Alternative: Cumulative counter across all quality gates in single workflow execution (3 retries total for build + test + static analysis combined).
- **Impact:** Cumulative approach reduces total retries but may escalate too quickly for minor failures.
- **Stakeholder Decision Needed:** Product team to decide retry budget allocation.

**Q-3.2: Should clarifying questions be optional for all issues or only high ambiguity (>70)?**

- Current spec: Questions triggered only for ambiguity score >70.
- Alternative: Always generate questions but hide behind "optional clarification" UI element.
- **Impact:** Always-on approach increases AI provider costs but may improve implementation quality.
- **Stakeholder Decision Needed:** Product team to balance cost vs. quality improvement.

**Q-3.3: Should design proposals support >3 options or limit to 3?**

- Current spec: 2-3 design options generated.
- Alternative: Allow 4-5 options for highly complex features.
- **Impact:** More options increase AI provider cost and user decision fatigue.
- **Stakeholder Decision Needed:** UX team to determine optimal option count based on user research.

**Q-3.4: Should security scanning block ALL vulnerabilities or only critical?**

- Current spec: Critical vulnerabilities block PR creation, medium/low allow with comment.
- Alternative: High vulnerabilities also block (more conservative).
- **Impact:** Blocking high vulnerabilities may reduce autonomous completion rate if fixes unavailable.
- **Stakeholder Decision Needed:** Security team to define risk tolerance.

**Q-3.5: Should static analysis auto-fixes be applied without user approval?**

- Current spec: Auto-fixes (formatting, imports) applied automatically, committed.
- Alternative: Present auto-fixes to user for approval before applying.
- **Impact:** Approval checkpoint slows workflow, reduces autonomy, but increases user control.
- **Stakeholder Decision Needed:** Product team to decide autonomy vs. control trade-off.

**Q-3.6: Should ambiguity scoring use AI provider scoring or pure NLP heuristics?**

- Current spec: 70% NLP heuristics, 30% AI provider scoring (hybrid approach).
- Alternative: 100% NLP heuristics (faster, deterministic, no AI cost) OR 100% AI scoring (more accurate, slower, higher cost).
- **Impact:** Pure NLP may miss context-dependent ambiguity, pure AI increases latency and cost.
- **Stakeholder Decision Needed:** Technical team to benchmark accuracy vs. cost trade-off.

**Q-3.7: Should research cache be shared across all users or per-user?**

- Current spec: Shared cache in Redis (all users benefit from cached research).
- Alternative: Per-user cache (privacy-focused, no cross-user contamination).
- **Impact:** Shared cache higher hit rate (60%+), per-user cache lower hit rate (20-30%).
- **Stakeholder Decision Needed:** Product team to decide multi-tenancy strategy.

**Q-3.8: Should escalation notifications include full logs or summaries?**

- Current spec: Escalation includes full retry history with logs (verbose, complete context).
- Alternative: Summaries with links to full logs (less verbose, may miss important details).
- **Impact:** Full logs enable faster human resolution, summaries reduce notification noise.
- **Stakeholder Decision Needed:** Engineering manager feedback on notification preferences.

**Q-3.9: Should test framework detection support custom test commands?**

- Current spec: Auto-detection with fallback to custom command configuration.
- Alternative: Always require explicit test command configuration (no auto-detection).
- **Impact:** Auto-detection reduces configuration burden, explicit config more reliable.
- **Stakeholder Decision Needed:** UX team to balance convenience vs. reliability.

**Q-3.10: Should build retries use same fix or generate new fix each time?**

- Current spec: AI provider generates new fix for each retry attempt.
- Alternative: Apply same fix for all 3 retries (faster, assumes fix is correct but environment issue).
- **Impact:** New fix each time increases AI provider cost, same fix may miss actual problem.
- **Stakeholder Decision Needed:** Technical team to determine retry strategy.

## Test Strategy Summary

### Overview

Epic 3 testing strategy validates **quality gates enforcement** (FR-16, FR-17, FR-18, FR-33) and **intelligence layer accuracy** (FR-3, FR-4) through 279 test cases across unit, integration, and E2E levels. Testing emphasizes **intelligent retry logic correctness** (counter resets, exponential backoff, immediate escalation), **AI provider integration reliability**, and **multi-platform CI/CD compatibility**.

**Total Test Coverage Target:** 82% average line coverage (85-90% for critical paths: retry logic, escalation, security scanning)

### Unit Testing Strategy (145 Test Cases, 80%+ Coverage)

**Quality Gates Unit Tests (73 test cases):**

_Build Automation (18 tests):_

- `BuildOrchestrator.test.ts`: Build trigger, status polling, failure categorization, retry counter logic (reset on success), exponential backoff timing, escalation invocation
- `BuildFailureAnalyzer.test.ts`: Failure type classification (transient vs structural), log parsing for GitHub/GitLab/Gitea/Forgejo, error message extraction
- `BuildFixGenerator.test.ts`: AI provider prompt generation, fix validation, commit message formatting, idempotency checks

_Test Execution (20 tests):_

- `TestRunner.test.ts`: Framework detection (Jest, pytest, RSpec, cargo, go test), test command execution, timeout handling, output capture
- `TestOutputParser.test.ts`: JSON/XML parsing for each framework, pass/fail counts, stack trace extraction, expected vs actual value parsing
- `TestFailureAnalyzer.test.ts`: Failure vs error differentiation, transient failure detection (flaky tests), structural issue detection (missing framework)

_Escalation Workflow (15 tests):_

- `EscalationManager.test.ts`: Escalation event creation, retry history aggregation, PR comment generation, label application, notification channel routing
- `EscalationNotifier.test.ts`: Webhook POST formatting, email template rendering, Slack message formatting, notification delivery retry logic

_Static Analysis (18 tests):_

- `StaticAnalyzerDetector.test.ts`: Config file detection for ESLint/Pylint/RuboCop/golangci-lint/Clippy, multi-tool support, missing tool handling
- `StaticAnalyzerRunner.test.ts`: Tool invocation with JSON output, timeout enforcement, output parsing, error categorization
- `AutoFixer.test.ts`: Auto-fix application (formatting, imports), rerun validation, fix count tracking

_Security Scanning (20 tests):_

- `DependencyScannerOrchestrator.test.ts`: Scanner selection (npm/pip/bundle/cargo audit), JSON output parsing, vulnerability extraction, CVSS scoring
- `CodeScannerOrchestrator.test.ts`: Semgrep invocation, changed files filtering, SAST finding parsing
- `VulnerabilityAnalyzer.test.ts`: Severity classification (critical/high/medium/low), immediate escalation trigger for critical, patching eligibility

**Intelligence Layer Unit Tests (72 test cases):**

_Research Capability (12 tests):_

- `ConceptDetector.test.ts`: Unfamiliar term detection, fuzzy matching, confidence threshold, concept registry lookup
- `ResearchOrchestrator.test.ts`: AI provider query generation, response validation (300-500 words, code examples), findings parsing
- `ResearchCache.test.ts`: Redis caching (set/get/expire), TTL enforcement, cache key generation, fallback to in-memory

_Ambiguity Detection (22 tests - 16 for scoring, 6 for questions):_

- `AmbiguityAnalyzer.test.ts`: Vague language detection, missing criteria detection, conflicting requirements, NLP+AI hybrid scoring, threshold triggers
- `QuestionGenerator.test.ts`: Question generation (2-5 count), multiple-choice vs open-ended, quality validation (no yes/no), answer incorporation

_Design Proposals (12 tests):_

- `DesignProposalGenerator.test.ts`: AI provider prompt generation, option parsing (title, pros/cons, complexity), distinct tradeoff validation
- `DesignSelectionInterface.test.ts`: CLI presentation formatting, custom design input, timeout handling, selection validation
- `DesignIntegrator.test.ts`: Plan merging, test strategy incorporation, PR comment generation

**Example Unit Test (Retry Counter Reset):**

```typescript
describe('BuildOrchestrator - Retry Counter Reset', () => {
  it('should reset retry counter to 0 when build succeeds', async () => {
    const orchestrator = new BuildOrchestrator(mockGitPlatform, mockAIProvider);

    // First attempt fails
    mockGitPlatform.triggerCI.mockResolvedValueOnce({ buildId: 'build-1', status: 'failure' });
    await orchestrator.triggerBuild(123);
    expect(orchestrator.getRetryCount()).toBe(1);

    // Second attempt succeeds
    mockGitPlatform.triggerCI.mockResolvedValueOnce({ buildId: 'build-2', status: 'success' });
    await orchestrator.retryBuild('build-1', 'fix code');
    expect(orchestrator.getRetryCount()).toBe(0); // Reset to 0
  });

  it('should NOT reset retry counter if different action', async () => {
    const orchestrator = new BuildOrchestrator(mockGitPlatform, mockAIProvider);

    // Build fails
    await orchestrator.triggerBuild(123);
    expect(orchestrator.getRetryCount()).toBe(1);

    // Test succeeds (different action, different counter)
    const testRunner = new TestRunner();
    await testRunner.executeTests('./src');
    expect(testRunner.getRetryCount()).toBe(0); // Independent counter
  });
});
```

### Integration Testing Strategy (96 Test Cases, 70%+ Coverage)

**Quality Gates Integration Tests (53 test cases):**

_Build Automation (12 tests):_

- `BuildWorkflow.integration.test.ts`: Full retry cycle with eventual success, retry exhaustion triggers escalation, immediate escalation for missing config, exponential backoff timing validation
- Platform-specific tests: GitHub Actions integration (workflow dispatch API), GitLab CI integration (pipeline trigger API), Gitea/Forgejo integration (REST API)

_Test Execution (15 tests):_

- `TestWorkflow.integration.test.ts`: Framework detection + execution for each framework, output parsing accuracy, fix application + rerun cycle, escalation after 3 retries
- Multi-language tests: JavaScript (Jest), Python (pytest), Ruby (RSpec), Rust (cargo test), Go (go test)

_Escalation Workflow (10 tests):_

- `EscalationWorkflow.integration.test.ts`: End-to-end escalation (trigger → notification → resolution), multi-channel notification delivery, webhook retry logic, human resolution tracking

_Static Analysis (12 tests):_

- `StaticAnalysisWorkflow.integration.test.ts`: Auto-detection + execution for each tool, auto-fix application + rerun, AI-powered fix suggestions with retry, PR description integration

_Security Scanning (15 tests):_

- `SecurityWorkflow.integration.test.ts`: Dependency scan for each platform (npm, pip, bundle, cargo), code scan (Semgrep), critical vulnerability blocking with escalation, patching + rescan cycle

**Intelligence Layer Integration Tests (43 test cases):**

_Research Capability (8 tests):_

- `ResearchWorkflow.integration.test.ts`: Concept detection → cache miss → AI query → cache store, cache hit flow (<100ms), manual research trigger, integration with PlanGenerator

_Ambiguity Detection (16 tests):_

- `AmbiguityWorkflow.integration.test.ts`: Score calculation → question generation → user interaction → answer incorporation, high ambiguity (>70) triggers questions, very high ambiguity (>90) suggests breakdown, override labels ("skip-questions", "proceed-despite-ambiguity")

_Design Proposals (8 tests):_

- `DesignProposalWorkflow.integration.test.ts`: Label detection → proposal generation → user selection → plan integration, custom design input, timeout handling in orchestrator mode

**Example Integration Test (Full Retry Cycle):**

```typescript
describe('Build Workflow - Full Retry Cycle', () => {
  it('should recover from transient build failure within retry limit', async () => {
    // Setup: Mock GitHub Actions API
    nock('https://api.github.com')
      .post('/repos/owner/repo/actions/workflows/ci.yml/dispatches')
      .times(2)
      .reply(204);

    nock('https://api.github.com')
      .get('/repos/owner/repo/actions/runs/12345')
      .reply(200, { status: 'failure', logs: 'Dependency not found: lodash' })
      .get('/repos/owner/repo/actions/runs/12346')
      .reply(200, { status: 'success' });

    // Execute: Trigger build workflow
    const orchestrator = new BuildOrchestrator(realGitPlatform, realAIProvider);
    await orchestrator.triggerBuild(123);

    // Validate: First attempt failed, retry succeeded
    const events = await eventStore.query({ type: 'BuildRetryEvent' });
    expect(events).toHaveLength(1);
    expect(events[0].data.retryAttempt).toBe(1);
    expect(events[0].data.outcome).toBe('success');

    const finalEvent = await eventStore.queryLatest({ type: 'BuildCompletedEvent' });
    expect(finalEvent.data.status).toBe('success');
    expect(finalEvent.data.retriesUsed).toBe(1);
  });
});
```

### E2E Testing Strategy (38 Test Cases)

**E2E-1: Happy Path - Quality Gates Pass (8 tests)**

- Setup: Test repository with passing build/tests, clean dependency scan
- Workflow: Issue selected → plan generated → implementation → tests pass → static analysis pass → security scan pass → PR created → merged
- Validation: No escalations, retry count = 0 for all gates, PR description includes quality gate results, autonomous completion time <2 hours

**E2E-2: Build Failure Recovery (6 tests)**

- Setup: Test repository with intentional build failure (missing import)
- Workflow: Build fails → AI suggests fix (add import) → retry succeeds → workflow continues
- Validation: Retry count = 1, fix committed with correct message, build completion event logged

**E2E-3: Test Failure Recovery (6 tests)**

- Setup: Test repository with failing assertion
- Workflow: Tests fail → AI suggests fix (update expected value) → retry succeeds → workflow continues
- Validation: Test fix applied to correct file (test or implementation), all tests pass after fix

**E2E-4: Escalation Triggered (6 tests)**

- Setup: Test repository with persistent build failure (missing external dependency)
- Workflow: Build fails 3 times → escalation triggered → PR comment posted → workflow paused → human resolves → workflow resumes
- Validation: Escalation event created, notification sent, retry history complete (3 attempts), workflow resumed after resolution

**E2E-5: Security Vulnerability Blocked (4 tests)**

- Setup: Test repository with critical vulnerability (lodash prototype pollution CVE)
- Workflow: Security scan detects critical → PR creation blocked → escalation triggered → human remediates → workflow resumes
- Validation: PR creation prevented, escalation includes CVE ID, workflow cannot proceed until resolution

**E2E-6: Ambiguity Clarification (4 tests)**

- Setup: Issue with high ambiguity (vague requirements, no acceptance criteria)
- Workflow: Ambiguity score 75 → clarifying questions generated → user answers → plan incorporates answers → implementation succeeds
- Validation: Questions logged to event trail, answers in PR comment, implementation aligned with clarified requirements

**E2E-7: Design Proposal Selection (4 tests)**

- Setup: Issue with "design-options-needed" label
- Workflow: 3 design options generated → user selects option 2 → plan incorporates selected design → implementation follows design
- Validation: All options logged, selection rationale captured, test strategy from design incorporated

**Example E2E Test:**

```typescript
describe('E2E - Full Autonomous Loop with Quality Gates', () => {
  it('should complete issue with build retry and security patching', async () => {
    // Setup: Real test repository with intentional issues
    const testRepo = await createTestRepository({
      issues: [{ number: 1, title: 'Add user authentication' }],
      buildConfig: { initialFailure: 'missing-dependency', fixable: true },
      dependencies: { vulnerable: ['lodash@4.17.19'] }, // Known CVE
    });

    // Execute: Start autonomous loop
    const tamma = new TammaOrchestrator(config);
    await tamma.start({ mode: 'standalone', repository: testRepo.url });

    // Wait for completion (max 10 minutes)
    await waitFor(() => tamma.getStatus() === 'completed', { timeout: 600000 });

    // Validate: Issue completed, quality gates enforced
    const events = await eventStore.query({ aggregateId: 'issue-1' });

    // Build retry occurred
    const buildRetry = events.find((e) => e.type === 'BuildRetryEvent');
    expect(buildRetry).toBeDefined();
    expect(buildRetry.data.retryAttempt).toBe(1);
    expect(buildRetry.data.outcome).toBe('success');

    // Security scan patched vulnerability
    const patchEvent = events.find((e) => e.type === 'SecurityPatchAppliedEvent');
    expect(patchEvent).toBeDefined();
    expect(patchEvent.data.package).toBe('lodash');
    expect(patchEvent.data.fromVersion).toBe('4.17.19');
    expect(patchEvent.data.toVersion).toMatch(/^4\.17\.2[0-9]/);

    // PR merged
    const pr = await testRepo.getPR(events.find((e) => e.type === 'PRCreatedEvent').data.prNumber);
    expect(pr.state).toBe('merged');
    expect(pr.body).toContain('Security Scan: 0 critical');
    expect(pr.body).toContain('Static Analysis: 0 errors');
  });
});
```

### State Machine Testing (Additional 15 Test Cases, 100% Transition Coverage)

**Epic 3 extends Epic 2's state machine with quality gate checkpoints. Testing ensures:**

- All state transitions from quality gates (success/failure/escalation) lead to correct next state
- Retry logic does NOT cause infinite loops (max 3 attempts enforced)
- Escalation pauses workflow correctly (no auto-next until resolution)
- Counter resets propagate correctly across state transitions

**Test Coverage:**

- 15 test cases covering all quality gate transitions (build → test → static → security → PR)
- 5 negative tests for invalid transitions (e.g., cannot proceed to PR creation if security scan blocked)
- 5 edge case tests (concurrent state updates, race conditions, recovery from crash)

### Manual Testing Scenarios (8 Scenarios)

**MT-1: Standalone Mode - Full Loop with Escalation**

- Execute: Tamma standalone on test repository with persistent build failure
- Observe: Build retry attempts (1, 2, 3), escalation notification (CLI output), PR comment with retry history
- Validate: Human can review escalation context and manually resolve (fix build config), workflow resumes after resolution marker

**MT-2: Orchestrator Mode - Webhook Notifications**

- Execute: Tamma orchestrator with webhook endpoint configured
- Observe: Escalation webhook POST with JSON payload, email notification, Slack message
- Validate: All notification channels receive escalation, webhook includes full retry history, Slack buttons functional

**MT-3: Clarifying Questions - Interactive Session**

- Execute: Tamma on issue with high ambiguity (>70 score)
- Observe: Ambiguity score displayed, 3-5 questions presented with multiple-choice options
- Validate: User can answer questions, answers incorporated into plan, plan quality improved

**MT-4: Design Proposals - User Selection**

- Execute: Tamma on issue with "design-options-needed" label
- Observe: 3 design options displayed with pros/cons, user selects option 2
- Validate: Selected design incorporated into implementation, test strategy followed

**MT-5: Security Scanning - Critical Vulnerability Block**

- Execute: Tamma on repository with critical vulnerability
- Observe: Security scan detects critical CVE, PR creation blocked, escalation triggered
- Validate: User cannot proceed without remediation, escalation notification includes CVE details

**MT-6: Static Analysis - Auto-Fix Application**

- Execute: Tamma on repository with formatting errors
- Observe: ESLint detects errors, auto-fixes applied (prettier formatting), analysis rerun passes
- Validate: Fixes committed with correct message, PR description includes auto-fix count

**MT-7: Research Capability - Unfamiliar API**

- Execute: Tamma on issue mentioning unfamiliar framework (e.g., SolidJS)
- Observe: Concept detected, research query sent, findings displayed in plan context
- Validate: Implementation uses patterns from research findings, cache hit on subsequent query

**MT-8: Retry Counter Reset - Verification**

- Execute: Tamma on repository with transient build failure then success
- Observe: Build fails (retry = 1), build succeeds (retry reset to 0), tests fail (retry = 1 for test counter)
- Validate: Counters independent per action, no cumulative escalation

### CI/CD Testing (Integrated with Epic 3 Development)

- Epic 3 changes run through CI/CD pipeline on every PR: unit tests → integration tests → E2E tests → coverage report
- Pre-merge quality gates: 80%+ coverage required, all tests must pass, no critical vulnerabilities
- Nightly E2E test suite against live test repositories (GitHub, GitLab, Gitea)
- Performance benchmarks: Retry logic overhead <14 seconds, ambiguity scoring <5 seconds, research queries <30 seconds

### Test Case Summary by Story

| Story                     | Unit    | Integration | E2E    | State Machine | Manual | Total   |
| ------------------------- | ------- | ----------- | ------ | ------------- | ------ | ------- |
| 3.1: Build Automation     | 18      | 12          | 6      | 3             | 2      | 41      |
| 3.2: Test Execution       | 20      | 15          | 6      | 3             | 1      | 45      |
| 3.3: Escalation           | 15      | 10          | 6      | 2             | 2      | 35      |
| 3.4: Research             | 12      | 8           | 4      | 1             | 1      | 26      |
| 3.5: Clarifying Questions | 14      | 10          | 4      | 2             | 1      | 31      |
| 3.6: Ambiguity Scoring    | 16      | 6           | 4      | 1             | 1      | 28      |
| 3.7: Design Proposals     | 12      | 8           | 4      | 1             | 1      | 26      |
| 3.8: Static Analysis      | 18      | 12          | 4      | 1             | 1      | 36      |
| 3.9: Security Scanning    | 20      | 15          | 4      | 1             | 1      | 41      |
| **TOTAL**                 | **145** | **96**      | **38** | **15**        | **8**  | **302** |

_Note: Test count increased from 279 to 302 after adding state machine tests (15) and manual scenarios (8)._

### Coverage Targets and Verification

- **Unit Tests:** 80%+ line coverage, 70%+ branch coverage (measured by c8)
- **Integration Tests:** 70%+ line coverage for integration paths (measured by c8 with integration flag)
- **E2E Tests:** Functional coverage (all user journeys validated), not line coverage
- **Critical Paths:** 90%+ coverage for retry logic, escalation, security scanning
- **Coverage Enforcement:** CI/CD pipeline fails if coverage drops below targets
