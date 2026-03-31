---
title: "Story 7-3: Context Gathering Activity"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need to gather comprehensive project context -- including repository structure, recent changes, test results, similar code patterns, and story metadata -- so that AI analysis and guidance activities have the information they need to make accurate, relevant decisions.

## Description

Implement the `ContextGatheringActivity` ELSA activity that collects and aggregates context from multiple sources before any AI analysis step. Context gathering is used at several points in the mentorship workflow: before assessment (to understand what the junior is working on), before plan decomposition (to identify relevant patterns), during blocker diagnosis (to analyze recent changes), and before code review (to understand the diff in context).

An existing implementation exists at `apps/tamma-elsa/src/Tamma.Activities/AI/ContextGatheringActivity.cs`. It currently simulates file contents and pattern matching. This story requires replacing simulations with real GitHub API integration, codebase search (via Epic 6 indexer if available), and intelligent context budgeting to stay within LLM token limits.

## Acceptance Criteria

### AC1: Story Context Extraction
- [ ] Load story metadata from the database: title, description, acceptance criteria, technical requirements, priority, complexity
- [ ] Parse acceptance criteria from JSON into structured checklist items
- [ ] Parse technical requirements from JSON into key-value pairs
- [ ] Handle missing or malformed story data gracefully (log warning, continue with available data)

### AC2: Repository Context
- [ ] Retrieve recent commits on the story's feature branch (last 7 days, configurable)
- [ ] Extract file paths, commit messages, authors, and timestamps from commits
- [ ] Deduplicate files by path, keeping the most recent change
- [ ] Retrieve file contents for up to 10 target files (configurable limit)
- [ ] Auto-detect programming language from file extension
- [ ] Support GitHub repositories via `IIntegrationService.GetGitHubCommitsAsync`
- [ ] Handle repository not found or branch not found errors gracefully

### AC3: Pattern Detection
- [ ] Search for similar code patterns in the repository based on the story title and description
- [ ] When Epic 6 codebase indexer is available, use vector similarity search
- [ ] When indexer is not available, fall back to filename/keyword matching
- [ ] Return up to 5 most relevant patterns with: pattern name, file path, description, relevance score (0.0-1.0)
- [ ] Patterns ranked by relevance score descending

### AC4: Test Context
- [ ] Retrieve test results for the story's feature branch
- [ ] Report: total tests, passing tests, failing tests, coverage percentage
- [ ] For failing tests, include: test name, error message, stack trace (truncated to 500 chars)
- [ ] Handle CI/CD not configured gracefully (return empty test context with warning)
- [ ] Support triggering test runs via `IIntegrationService.TriggerTestsAsync`

### AC5: Project Structure
- [ ] Analyze repository root to identify main directories, configuration files, and entry points
- [ ] Detect project type (Node.js, .NET, Python, etc.) from configuration files
- [ ] Identify key architectural patterns (MVC, service-repository, etc.) from directory structure
- [ ] Cache project structure per repository (invalidate on config file changes)

### AC6: Session History Context
- [ ] Load all previous events for the current session
- [ ] Extract state transitions with timestamps
- [ ] Extract recent events (last 10) with types and timestamps
- [ ] Calculate session duration and time spent in each state
- [ ] Identify recurring patterns (e.g., repeated DIAGNOSE_BLOCKER transitions)

### AC7: Context Budgeting
- [ ] Enforce a maximum context size in characters (default: 50,000, configurable)
- [ ] Priority-based trimming when context exceeds budget:
  1. Story metadata (highest priority, never trimmed)
  2. Acceptance criteria (high priority)
  3. Target file contents (medium priority, trim least relevant files first)
  4. Test context (medium priority, trim stack traces first)
  5. Similar patterns (lower priority, trim lowest relevance first)
  6. Session history (lowest priority, trim oldest events first)
- [ ] Report actual context size and whether trimming was applied
- [ ] Generate a context summary string for logging and debugging

### AC8: Context Output
- [ ] Output is a single `CodeContextOutput` object containing all gathered context
- [ ] Output includes a `Success` flag and error `Message` if gathering failed
- [ ] Output includes `ContextSummary` (human-readable one-liner) and `TotalContextSize` (character count)
- [ ] Output is serializable to JSON for passing to Claude analysis

## Technical Design

### Context Gathering Pipeline

```typescript
// TypeScript interface mirrors for the TS engine bridge
export interface ContextGatheringRequest {
  sessionId: string;
  storyId: string;
  targetFiles?: string[];
  maxContextSize: number;
  includeSimilarPatterns: boolean;
  includeTests: boolean;
  includeHistory: boolean;
  contextPurpose: ContextPurpose;
}

export type ContextPurpose =
  | 'assessment'
  | 'plan_decomposition'
  | 'blocker_diagnosis'
  | 'code_review'
  | 'guidance_generation';

export interface CodeContextOutput {
  success: boolean;
  message?: string;

  // Story context
  storyId: string;
  storyTitle?: string;
  storyDescription?: string;
  acceptanceCriteria: string[];
  technicalRequirements: Record<string, string>;

  // Repository context
  recentChanges: FileChange[];
  fileContents: FileContent[];
  projectStructure?: ProjectStructure;

  // Pattern context
  similarPatterns: SimilarPattern[];

  // Test context
  testContext?: TestContextInfo;

  // Session context
  sessionHistory?: SessionHistoryContext;

  // Meta
  contextSummary?: string;
  totalContextSize: number;
  trimmed: boolean;
}

export interface FileChange {
  filePath: string;
  commitSha: string;
  commitMessage: string;
  author: string;
  timestamp: Date;
}

export interface FileContent {
  filePath: string;
  content?: string;
  language: string;
  lineCount: number;
}

export interface SimilarPattern {
  patternName: string;
  filePath: string;
  description?: string;
  relevance: number;
}

export interface TestContextInfo {
  totalTests: number;
  passingTests: number;
  failingTests: number;
  coveragePercentage: number;
  failingTestDetails: FailingTestInfo[];
}

export interface FailingTestInfo {
  testName: string;
  errorMessage: string;
  stackTrace?: string;
}

export interface ProjectStructure {
  rootDirectory: string;
  mainDirectories: string[];
  configurationFiles: string[];
  entryPoints: string[];
  projectType?: string;
}

export interface SessionHistoryContext {
  totalEvents: number;
  stateTransitions: { from: string; to: string; timestamp: Date }[];
  recentEvents: { eventType: string; timestamp: Date }[];
  totalDurationMs?: number;
  timePerState?: Record<string, number>;
}
```

### Context Gathering Service Interface

```typescript
export interface IContextGatheringActivity {
  // Main entry point
  gatherContext(request: ContextGatheringRequest): Promise<CodeContextOutput>;

  // Individual context sources (for targeted gathering)
  gatherStoryContext(storyId: string): Promise<{
    title: string;
    description: string;
    acceptanceCriteria: string[];
    technicalRequirements: Record<string, string>;
  }>;

  gatherRepositoryContext(
    repositoryUrl: string,
    branchName: string,
    targetFiles?: string[]
  ): Promise<{
    recentChanges: FileChange[];
    fileContents: FileContent[];
    structure: ProjectStructure;
  }>;

  gatherTestContext(
    repositoryUrl: string,
    branchName: string
  ): Promise<TestContextInfo>;

  gatherSimilarPatterns(
    repositoryUrl: string,
    searchTerms: string
  ): Promise<SimilarPattern[]>;

  gatherSessionHistory(sessionId: string): Promise<SessionHistoryContext>;

  // Context budgeting
  trimToSize(context: CodeContextOutput, maxSize: number): CodeContextOutput;
}
```

## Dependencies

- Story 7-1: State machine core (session history requires transition log)
- `Tamma.Core.Entities.Story` (already defined with RepositoryUrl, AcceptanceCriteria, TechnicalRequirements)
- `Tamma.Core.Interfaces.IIntegrationService` (already defined: GetGitHubCommitsAsync, TriggerTestsAsync)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (already defined: GetEventsBySessionIdAsync)
- Epic 6 Story 6-1: Codebase Indexer (optional, for vector similarity search of patterns)
- GitHub API (via IntegrationService) for repository operations
- CI/CD integration (via IntegrationService) for test results

## Testing Strategy

### Unit Tests
- Story context extraction with valid JSON acceptance criteria
- Story context extraction with malformed/missing acceptance criteria
- File language detection for all supported extensions (.cs, .ts, .tsx, .js, .py, .java, .go, .rs, .sql, .json, .yaml, .md)
- Context trimming respects priority order (story metadata never trimmed)
- Context trimming stops as soon as size is within budget
- Context summary generation produces accurate one-liner
- File deduplication keeps most recent change per path
- Empty repository produces valid output with no changes

### Integration Tests
- Full context gathering with real GitHub repository (requires GITHUB_TOKEN env var)
- Test context gathering with CI/CD integration (requires CI configured)
- Session history gathering with populated event log
- Context budgeting with very large repository (>100 changed files)

### Performance Tests
- Context gathering for a typical story completes in <5 seconds
- Context trimming for oversized context completes in <100ms
- GitHub API calls are batched where possible (max 3 API calls per gathering)

## Configuration

```yaml
mentorship:
  context_gathering:
    max_context_size: 50000
    max_target_files: 10
    max_recent_commits: 20
    max_similar_patterns: 5
    max_failing_test_details: 10
    stack_trace_max_length: 500
    recent_changes_days: 7
    include_similar_patterns: true
    include_tests: true
    include_history: true
    cache:
      project_structure_ttl_minutes: 60
    github:
      timeout_seconds: 30
      max_retries: 3
```

## Success Metrics

- Context relevance: >85% of gathered context rated relevant by downstream AI analysis
- Gathering speed: <5 seconds for typical story context (p95)
- Budget compliance: 100% of outputs within configured max context size
- Source coverage: context from >= 3 different sources (story, repo, tests/patterns/history) for each request
- Error resilience: 0 crashes from malformed upstream data (graceful degradation instead)
- GitHub API efficiency: <5 API calls per context gathering operation

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
