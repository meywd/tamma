---
title: "Story 2.19: Intelligent Issue Orchestration"
sidebar:
  order: 20
---

**Epic**: Epic 2 - Autonomous Development Loop - Core
**Status**: Ready for Development
**Priority**: High
**Prerequisites**: Story 2.1 (Issue Selection with Filtering), Story 2.11 (Auto Next Issue Selection), Story 2.14 (Issue Decomposition Engine), Story 2.15 (Task Dependency Mapping)

---

## Audit Findings: Current State Analysis

This story was produced from a deep audit of the three top-level orchestration workflows (ADL Orchestrator, Single Issue Cycle, Issue Selection) and their backing activities. The audit compared the Elsa C# workflow engine against the TypeScript `TammaEngine` and identified the following gaps.

### What currently exists

**SelectIssueActivity (Elsa):**
- Queries GitHub for issues matching configured labels.
- Picks the first unassigned issue (`FirstOrDefault(i => string.IsNullOrEmpty(i.Assignee))`).
- Assigns it to the bot and returns issue JSON/number/title.
- Has three outcomes: Selected, NoIssues, Error.

**IssueSelectionWorkflow (Elsa):**
- Thin wrapper: calls `SelectIssueActivity`, then sets four outputs (success, issueJson, issueNumber, issueTitle).
- No pre-processing, scoring, or enrichment of any kind.

**SingleIssueCycleWorkflow (Elsa):**
- Full 14-step flow: SelectIssue, GatherContext, GeneratePlan, PlanApproval, CreateBranch, TDD+DebugRetry, CreatePR, CI+DebugRetry, ReviewFix, MergeApproval, MergePR.
- Eight distinct exit-reason nodes feeding into a shared finish sequence.
- Known bug documented in code: `ciRetryCount` persists across re-entries (review-fix, merge re-test) instead of resetting.
- No check whether the issue is actually solvable autonomously before committing to the full pipeline.
- No progress reporting back to the GitHub issue during processing.

**AdlOrchestratorWorkflow (Elsa):**
- Loop: InitConfig, CheckLimits, DispatchCycle, ParseResult, ShouldContinue (noIssues check), Cooldown, then loop back to CheckLimits.
- CheckLimitsActivity checks: emergency stop flag, daily issue quota, max issues per run.
- Cooldown is a simple fixed-delay timer (configurable, default 10s).
- Exit conditions: limits reached or noIssues.

**TammaEngine (TypeScript):**
- Pipeline: selectIssue, analyzeIssue, generatePlan, awaitApproval, createBranch, implementCode, createPR, monitorAndMerge.
- Issue selection: sorts by `created asc` (oldest first), filters by labels, filters out exclude labels, picks `candidates[0]`.
- No complexity scoring, no solvability check, no decomposition.
- analyzeIssue builds a markdown context document from issue body, comments, related issues, and recent commits.
- Cost tracking and event store recording throughout.

### Identified Gaps

| Gap | Elsa | TS Engine | Impact |
|-----|------|-----------|--------|
| **Issue complexity scoring** | None | None | Tamma picks up issues it cannot handle, wasting cycles and API budget |
| **Autonomous solvability assessment** | None | None | No filter prevents human-only issues (architecture decisions, security audits, design reviews) from entering the pipeline |
| **Issue decomposition at selection time** | None (Story 2.14 exists but is not wired into selection) | None | Complex issues enter as monoliths, causing TDD failures and oversized PRs |
| **Dependency-aware ordering** | None | None | Issues that depend on incomplete work get picked, fail at implementation, waste budget |
| **Progress reporting to issue** | Only pickup comment and PR-created comment (TS engine) | Pickup + PR link | Users have no visibility into what Tamma is doing between pickup and PR |
| **Configurable selection strategies** | First-unassigned only (Elsa) | Oldest-first only (TS) | No ability to prioritize by urgency, complexity, or business value |
| **Rate limiting / capacity awareness** | CheckLimitsActivity has quota + emergency stop | pollIntervalMs + issuesProcessed counter | No awareness of current processing load, no per-hour rate limiting, no budget-aware throttling |
| **Context enrichment before processing** | GatherContext is a separate step after selection | analyzeIssue after selection | The selection decision itself has no context - a minimal issue body could indicate anything from a typo fix to a major refactor |

---

## User Story

As a **platform operator**,
I want Tamma to intelligently evaluate, score, and order issues before committing them to the autonomous development pipeline,
so that the system prioritizes issues it can actually solve, decomposes complex ones into manageable subtasks, respects dependency ordering, and provides continuous visibility into its progress.

---

## Acceptance Criteria

### AC-1: Issue Complexity Scoring
1. Before selection, each candidate issue receives a complexity score (simple / medium / complex / too-complex).
2. Scoring considers: issue body length, number of files likely touched, label signals (e.g., `refactor`, `architecture`), referenced issues count, comment thread length.
3. AI-assisted scoring uses a lightweight LLM call (not the full planning model) to classify complexity from issue text.
4. Complexity score is stored as metadata on the issue selection event and accessible via API.
5. Configuration allows setting a maximum complexity threshold -- issues above it are skipped or flagged for human triage.

### AC-2: Autonomous Solvability Assessment
1. Each candidate issue is assessed for autonomous solvability with a confidence score (0-100).
2. Assessment checks for: clear acceptance criteria, reproducible problem statement, no requirement for external access (databases, third-party services), no requirement for human judgment (design decisions, UX reviews).
3. Issues scoring below configurable threshold (default: 60) are skipped and labeled `needs-human-review`.
4. Assessment rationale is logged and optionally posted as a comment on the issue.
5. Solvability model is tunable via prompt configuration without code changes.

### AC-3: Issue Decomposition at Selection Time
1. Issues classified as "complex" or "too-complex" are automatically decomposed into subtasks before entering the pipeline.
2. Decomposition creates GitHub sub-issues (or checklist items) linked to the parent issue.
3. Each subtask is independently scorable and selectable by the orchestrator.
4. Decomposition requires human approval when configured (default: auto-approve for "complex", require approval for "too-complex").
5. Integration with existing Story 2.14 decomposition engine -- this story adds the wiring into the selection workflow, not a new decomposition algorithm.

### AC-4: Dependency-Aware Issue Ordering
1. Before selection, the system builds a lightweight dependency graph of candidate issues.
2. Dependencies are detected from: explicit `depends on #N` / `blocked by #N` references in issue body/comments, label-based relationships (e.g., `depends-on:issue-123`), AI-inferred dependencies from issue descriptions.
3. Issues with unresolved blocking dependencies are excluded from selection.
4. Within eligible issues, ordering respects a topological sort so prerequisites are processed first.
5. Circular dependency detection logs a warning and falls back to age-based ordering for the cycle.

### AC-5: Progress Reporting to Issue
1. Tamma posts structured status comments on the GitHub issue at each major pipeline stage: context gathered, plan generated, plan approved, branch created, TDD started, TDD passed, PR created, CI passed, review complete, merged.
2. Comments use a consistent format with stage name, timestamp, and brief status.
3. Comment frequency is configurable (every stage, major milestones only, or silent).
4. On failure, a diagnostic comment is posted explaining what went wrong and whether Tamma will retry.
5. Progress comments are collapsible (using `<details>` tags) to avoid cluttering the issue thread.

### AC-6: Configurable Selection Strategies
1. The following selection strategies are supported and configurable:
   - `oldest-first` (current default): process issues by creation date ascending.
   - `priority-weighted`: score based on labels (p0/p1/p2/p3), age, and complexity.
   - `simplest-first`: prefer issues with lowest complexity score (maximize throughput).
   - `hardest-first`: prefer issues with highest complexity score (tackle risk early).
   - `round-robin`: cycle through issue labels/categories to distribute effort.
   - `dependency-optimal`: topological ordering for maximum parallel safety.
2. Strategy is selectable via configuration (`tamma.config.yaml` or API parameter).
3. Strategy can be overridden per-run via CLI flag or API input.
4. Custom strategies are pluggable via a `ISelectionStrategy` interface.

### AC-7: Rate Limiting and Capacity Awareness
1. Per-hour issue rate limit is enforced in addition to existing daily quota.
2. Budget-aware throttling: if cumulative AI spend approaches configurable daily budget limit, issue selection pauses and logs a budget warning.
3. Active-issue concurrency check: in distributed mode, the system queries how many issues are currently in-flight across all workers before selecting new ones.
4. Cooldown duration is adaptive: increases after failures, resets after successes.
5. All rate-limiting decisions are logged as events with reason codes.

---

## Technical Context

### Relationship to Existing Stories

| Story | Relationship |
|-------|-------------|
| **2.1** (Issue Selection with Filtering) | This story extends 2.1's `IIssueSelector` with scoring, assessment, and strategy capabilities. It does not replace 2.1 -- it wraps it. |
| **2.11** (Auto Next Issue Selection) | This story enhances the inter-issue transition logic in 2.11 with capacity-aware selection and adaptive cooldown. |
| **2.14** (Issue Decomposition Engine) | This story wires 2.14's decomposition capability into the selection workflow. 2.14 provides the algorithm; this story provides the orchestration integration. |
| **2.15** (Task Dependency Mapping) | This story uses 2.15's dependency graph for ordering. 2.15 provides the graph construction; this story provides the selection-time query. |
| **2.16** (Incremental Task Sequencing) | Complementary. 2.16 handles sequencing within a decomposed issue. This story handles sequencing across issues. |

### Architecture: Where the New Logic Lives

The intelligent orchestration logic is added as a new layer between the existing issue query (SelectIssueActivity / engine.selectIssue) and the dispatch-to-pipeline step. It does not modify existing activities -- it introduces new ones and a new sub-workflow.

```
BEFORE:
  SelectIssue → [first unassigned] → Pipeline

AFTER:
  QueryCandidates → ScoreCandidates → AssessSolvability
    → FilterByDependencies → ApplyStrategy → [selected issue]
    → [if complex: Decompose → select subtask]
    → Pipeline
    → [at each stage: PostProgress]
```

### New Elsa Activities

1. **ScoreIssueCoplexityActivity** -- Calls a lightweight LLM to classify issue complexity.
2. **AssessSolvabilityActivity** -- Calls LLM to determine if the issue is autonomously solvable.
3. **BuildDependencyGraphActivity** -- Scans candidate issues for dependency references, builds a DAG.
4. **ApplySelectionStrategyActivity** -- Takes scored, filtered candidates and applies the configured strategy.
5. **DecomposeAndCreateSubtasksActivity** -- Wraps Story 2.14's engine, creates GitHub sub-issues.
6. **PostProgressCommentActivity** -- Posts structured status comments to the GitHub issue.
7. **CheckBudgetLimitsActivity** -- Extends CheckLimitsActivity with budget-aware throttling and per-hour rate limits.

### New TypeScript Components

1. **IssueScorer** -- Complexity scoring service (LLM-assisted + heuristic fallback).
2. **SolvabilityAssessor** -- Autonomous solvability assessment service.
3. **DependencyResolver** -- Builds and queries issue dependency graph.
4. **SelectionStrategyRegistry** -- Registry of pluggable `ISelectionStrategy` implementations.
5. **ProgressReporter** -- Posts structured status comments to issues.
6. **BudgetAwareThrottler** -- Budget and rate-limit enforcement.

### Data Models

```typescript
interface IssueScoringResult {
  issueNumber: number;
  complexity: 'simple' | 'medium' | 'complex' | 'too-complex';
  complexityScore: number;          // 0-100 numeric
  solvabilityScore: number;         // 0-100 confidence
  solvabilityRationale: string;     // LLM explanation
  estimatedEffortHours: number;     // rough estimate
  estimatedFilesTouch: number;      // how many files
  signals: ComplexitySignal[];      // what drove the score
  assessedAt: string;               // ISO 8601
}

interface ComplexitySignal {
  signal: string;                   // e.g., "large_body", "multi_component", "architecture_label"
  weight: number;                   // contribution to score
  evidence: string;                 // what triggered this signal
}

interface IssueDependency {
  issueNumber: number;
  dependsOn: number[];              // issue numbers this depends on
  blockedBy: number[];              // issue numbers that block this
  detectionMethod: 'explicit' | 'label' | 'ai-inferred';
  confidence: number;               // 0-100
}

interface SelectionStrategyConfig {
  strategy: 'oldest-first' | 'priority-weighted' | 'simplest-first'
    | 'hardest-first' | 'round-robin' | 'dependency-optimal';
  maxComplexity: 'simple' | 'medium' | 'complex' | 'too-complex';
  minSolvabilityScore: number;      // default: 60
  enableDecomposition: boolean;     // default: true
  decompositionApproval: 'auto' | 'require-human';
  progressReporting: 'every-stage' | 'milestones-only' | 'silent';
}

interface ProgressReport {
  issueNumber: number;
  stage: PipelineStage;
  status: 'started' | 'completed' | 'failed' | 'skipped';
  timestamp: string;
  message: string;
  details?: string;                 // collapsible detail block
  durationMs?: number;              // time spent in this stage
}

type PipelineStage =
  | 'context-gathering'
  | 'plan-generation'
  | 'plan-approval'
  | 'branch-creation'
  | 'tdd-cycle'
  | 'pr-creation'
  | 'ci-pipeline'
  | 'review-fix'
  | 'merge-approval'
  | 'merge-complete';
```

### Configuration Schema

```yaml
intelligent_orchestration:
  # Complexity scoring
  complexity:
    enabled: true
    max_complexity: complex          # simple | medium | complex | too-complex
    scoring_model: lightweight       # which LLM to use for scoring
    heuristic_fallback: true         # fall back to heuristics if LLM unavailable

  # Solvability assessment
  solvability:
    enabled: true
    min_score: 60                    # 0-100, issues below this are skipped
    label_on_skip: needs-human-review
    post_rationale_comment: false    # post assessment as issue comment

  # Decomposition integration
  decomposition:
    enabled: true
    trigger_complexity: complex      # decompose at this level and above
    auto_approve_level: complex      # auto-approve decomposition up to this level
    require_approval_level: too-complex
    max_subtasks: 8                  # cap number of subtasks per decomposition

  # Dependency ordering
  dependencies:
    enabled: true
    detection_methods:
      - explicit                     # "depends on #N" in body
      - label                        # "depends-on:issue-N" labels
      - ai-inferred                  # LLM inference from descriptions
    circular_fallback: oldest-first  # strategy when cycle detected

  # Selection strategy
  strategy: priority-weighted        # default strategy
  strategies:
    oldest-first: {}
    priority-weighted:
      age_weight: 0.3
      label_weight: 0.4
      complexity_weight: 0.3
    simplest-first: {}
    hardest-first: {}
    round-robin:
      categories: [bug, feature, enhancement, refactor]
    dependency-optimal: {}

  # Progress reporting
  progress:
    mode: milestones-only            # every-stage | milestones-only | silent
    collapsible: true                # use <details> tags
    include_timing: true             # include duration info

  # Rate limiting (extends existing limits)
  rate_limits:
    per_hour_max: 5
    budget_daily_usd: 50.0
    budget_warning_threshold: 0.8    # warn at 80% of budget
    adaptive_cooldown:
      enabled: true
      base_seconds: 10
      failure_multiplier: 2.0
      max_seconds: 300
      reset_on_success: true
    max_concurrent_issues: 1         # for distributed mode
```

### Error Handling

- **LLM scoring failure**: Fall back to heuristic scoring (body length, label analysis, comment count). Log warning. Never block selection on scoring failure.
- **Solvability assessment failure**: Default to solvable (optimistic). Log warning. Issue enters pipeline normally.
- **Dependency graph failure**: Fall back to no-dependency ordering. Log warning.
- **Decomposition failure**: Skip decomposition, process issue as-is. Post warning comment on issue.
- **Progress comment failure**: Swallow error, log it. Never fail a pipeline step because a comment could not be posted.
- **Budget check failure**: Fail closed (stop selecting issues). This is a safety measure.

### Testing Strategy

**Unit Tests:**
- Complexity scorer: test each signal type, test combined scoring, test threshold behavior.
- Solvability assessor: test common patterns (clear bug report = solvable, "redesign our architecture" = not solvable).
- Dependency resolver: test explicit references, label-based detection, circular dependency handling.
- Selection strategies: each strategy tested with various candidate sets.
- Progress reporter: test comment formatting, collapsible sections, failure comments.
- Budget throttler: test per-hour limits, daily budget, adaptive cooldown.

**Integration Tests:**
- Full selection pipeline with mock GitHub API: score, assess, filter, select.
- Decomposition trigger: issue classified as complex triggers decomposition sub-workflow.
- Progress reporting: verify comments posted to correct issues at correct stages.

**Performance Targets:**
- Scoring + assessment for 50 candidate issues: < 30 seconds (parallel LLM calls).
- Dependency graph construction for 100 issues: < 5 seconds.
- Strategy application: < 100ms.
- Progress comment posting: < 2 seconds per comment.

### Monitoring and Observability

**Events emitted:**
- `ISSUE.SCORED.SUCCESS` -- complexity + solvability scores attached
- `ISSUE.SCORED.FALLBACK` -- heuristic fallback used
- `ISSUE.SKIPPED.UNSOLVABLE` -- issue skipped due to low solvability
- `ISSUE.SKIPPED.TOO_COMPLEX` -- issue skipped due to complexity threshold
- `ISSUE.SKIPPED.BLOCKED` -- issue skipped due to unresolved dependency
- `ISSUE.DECOMPOSED.SUCCESS` -- issue decomposed into N subtasks
- `ISSUE.DECOMPOSED.FAILED` -- decomposition attempted but failed
- `ISSUE.PROGRESS.POSTED` -- status comment posted to issue
- `RATE_LIMIT.BUDGET.WARNING` -- approaching daily budget limit
- `RATE_LIMIT.BUDGET.EXCEEDED` -- daily budget exceeded, selection paused
- `RATE_LIMIT.HOURLY.EXCEEDED` -- per-hour rate limit exceeded

**Metrics:**
- Complexity score distribution (histogram)
- Solvability score distribution (histogram)
- Issues skipped per reason (counter)
- Decomposition trigger rate (counter)
- Strategy selection distribution (counter)
- Budget utilization percentage (gauge)
- Adaptive cooldown current value (gauge)

---

## Logging Requirements

All components MUST log via `ILogger` (C#) or `ILogger` from `@tamma/shared/contracts` (TypeScript).

- **INFO**: Issue scored (with scores), issue skipped (with reason), strategy applied, decomposition triggered, progress comment posted, rate limit enforced
- **DEBUG**: Individual complexity signals, solvability reasoning, dependency edges detected, strategy weight calculations
- **WARN**: LLM scoring fallback to heuristics, decomposition skipped, progress comment failed, budget approaching limit, circular dependency detected
- **ERROR**: Budget exceeded (selection paused), dependency graph construction failed, solvability assessment crashed
- **Structured context**: Always include `{ issueNumber, repository, strategy, complexityScore, solvabilityScore, workflowInstanceId }`
- **Audit trail**: Every selection decision must emit a corresponding DCB event

---

## Implementation Notes

### Key Design Decisions

1. **Scoring is best-effort, never blocking.** If the LLM is unavailable, heuristics are used. If heuristics fail, the issue proceeds with no score. The system should never refuse to work because scoring is broken.

2. **Solvability defaults to optimistic.** When assessment fails, the issue is assumed solvable. This prevents false negatives from blocking legitimate work. The pipeline itself will catch truly unsolvable issues at the plan-approval checkpoint.

3. **Decomposition is opt-in per complexity level.** Operators can disable decomposition entirely, or only trigger it for "too-complex" issues. This prevents over-decomposition of issues that are better handled as a single unit.

4. **Progress comments use collapsible sections.** This respects the issue thread readability. Only the current status line is visible; historical progress is tucked inside `<details>` blocks.

5. **Budget throttling fails closed.** When budget limits are hit, the system stops selecting issues rather than continuing to spend. This is a critical safety guardrail.

6. **The selection pipeline is a new Elsa sub-workflow** (`intelligent-issue-selection`), not modifications to the existing `IssueSelectionWorkflow`. The existing workflow continues to work for simple deployments. The ADL orchestrator dispatches to the intelligent variant when configured.

### ciRetryCount Bug (Pre-existing)

The audit identified a bug in `SingleIssueCycleWorkflow.cs` (line 347-351): `ciRetryCount` is passed into the CI sub-workflow and persists across re-entries from review-fix and merge-test paths. This means the retry budget decreases across multiple CI runs for the same issue. This is documented in the code as a known issue. This story does not fix it -- it should be tracked separately.

### References

- **Existing Stories:** 2.1, 2.11, 2.14, 2.15, 2.16
- **Elsa Workflows:** `AdlOrchestratorWorkflow.cs`, `SingleIssueCycleWorkflow.cs`, `IssueSelectionWorkflow.cs`
- **Elsa Activities:** `SelectIssueActivity.cs`, `CheckLimitsActivity.cs`
- **TS Engine:** `packages/orchestrator/src/engine.ts`
- **ADL Models:** `Tamma.Activities/ADL/Models/AdlModels.cs`
