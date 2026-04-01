---
title: "Implementation Plan: Story 2.19 - Intelligent Issue Orchestration"
sidebar:
  order: 20
---

## Overview

This plan describes how to build the intelligent issue orchestration layer that sits between issue querying and pipeline dispatch. It adds complexity scoring, solvability assessment, dependency-aware ordering, configurable selection strategies, progress reporting, and budget-aware rate limiting.

The implementation is organized in 6 phases. Phases 1-3 can be developed in parallel by different contributors. Phases 4-6 are sequential and depend on earlier phases.

---

## Phase 1: Complexity Scoring and Solvability Assessment

**Goal:** Score every candidate issue before it enters the selection pipeline.

### 1A: Heuristic Complexity Scorer

**New file:** `packages/orchestrator/src/scoring/complexity-scorer.ts`

Heuristic scoring runs without an LLM and serves as the fallback when AI scoring is unavailable.

**Algorithm:**

```
baseScore = 0

# Body length signal
if body.length < 200:    baseScore += 5    # trivial
elif body.length < 800:  baseScore += 15   # moderate
elif body.length < 2000: baseScore += 30   # substantial
else:                    baseScore += 50   # large

# Label signals
for label in issue.labels:
  if label in ['bug', 'typo', 'docs']:          baseScore += 0    # typically simple
  if label in ['enhancement', 'feature']:        baseScore += 10
  if label in ['refactor', 'tech-debt']:         baseScore += 20
  if label in ['architecture', 'breaking-change']: baseScore += 40
  if label in ['security', 'performance']:       baseScore += 25

# Reference count signal
refCount = count of "#N" references in body + comments
baseScore += min(refCount * 5, 25)

# Comment thread length signal
baseScore += min(issue.commentCount * 3, 15)

# Normalize to 0-100
score = min(baseScore, 100)

# Map to category
if score <= 20:   return 'simple'
elif score <= 45: return 'medium'
elif score <= 75: return 'complex'
else:             return 'too-complex'
```

**Interface:**

```typescript
interface IComplexityScorer {
  score(issue: CandidateIssue): Promise<IssueScoringResult>;
  scoreAll(issues: CandidateIssue[]): Promise<IssueScoringResult[]>;
}

class HeuristicComplexityScorer implements IComplexityScorer {
  // Pure heuristic, no LLM dependency
}
```

### 1B: AI-Assisted Complexity Scorer

**New file:** `packages/orchestrator/src/scoring/ai-complexity-scorer.ts`

Wraps the heuristic scorer and enhances with an LLM call for more accurate classification.

**LLM Prompt:**

```
You are evaluating a GitHub issue for autonomous development.

Issue #{{number}}: {{title}}

Body:
{{body}}

Labels: {{labels}}

Comment count: {{commentCount}}

Classify this issue's complexity for an AI coding agent:

1. SIMPLE: Single file change, clear fix, minimal testing (typo, config change, small bug fix)
2. MEDIUM: 2-5 file changes, clear requirements, standard patterns (feature addition, moderate bug fix)
3. COMPLEX: 5-15 file changes, cross-cutting concerns, needs design decisions (new feature, refactor)
4. TOO_COMPLEX: 15+ files, architectural changes, external dependencies, ambiguous requirements

Also assess: can an AI agent solve this autonomously without human design input?
Score from 0-100 where 100 = fully automatable.

Respond as JSON:
{
  "complexity": "simple|medium|complex|too-complex",
  "complexityScore": <0-100>,
  "solvabilityScore": <0-100>,
  "solvabilityRationale": "<1-2 sentence explanation>",
  "estimatedFiles": <number>,
  "estimatedHours": <number>,
  "signals": ["<signal1>", "<signal2>", ...]
}
```

**Key design:** Uses a lightweight/fast model (e.g., Claude Haiku, GPT-4o-mini) not the full planning model. This keeps scoring cheap (< $0.01 per issue). Multiple issues are scored in parallel with `Promise.all()`, bounded by a concurrency limit.

**Fallback chain:** AI scorer -> heuristic scorer -> no score (pass through).

### 1C: Solvability Assessor

**New file:** `packages/orchestrator/src/scoring/solvability-assessor.ts`

The solvability assessment is embedded in the AI complexity scorer prompt (above). For the heuristic fallback, solvability is estimated from labels and body keywords.

**Heuristic solvability signals (negative -- reduce score):**

| Signal | Score Reduction | Detection |
|--------|----------------|-----------|
| No acceptance criteria | -20 | Body does not contain words like "should", "must", "expected", "acceptance" |
| Requires external access | -30 | Body mentions "database migration", "third-party API", "production data" |
| Design decision needed | -25 | Body mentions "design", "architecture", "RFC", "proposal" |
| UX/UI review needed | -20 | Labels include "design-review", "ux", "ui" |
| Security audit | -30 | Labels include "security-review", "pentest", "audit" |
| Vague/unclear | -15 | Body length < 50 characters and no linked issues |

**Skip behavior:**

```typescript
if (solvabilityScore < config.minSolvabilityScore) {
  // Add label "needs-human-review" to issue
  await platform.addLabel(owner, repo, issueNumber, 'needs-human-review');

  // Optionally post rationale comment
  if (config.postRationaleComment) {
    await platform.addIssueComment(owner, repo, issueNumber,
      `Tamma assessed this issue as requiring human intervention.\n\n` +
      `Solvability score: ${score}/100 (threshold: ${config.minSolvabilityScore})\n` +
      `Reason: ${rationale}`
    );
  }

  // Skip this issue
  return null;
}
```

### 1D: Elsa Activity - ScoreIssueCoplexityActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/ScoreIssueComplexityActivity.cs`

```csharp
[Activity("Tamma.ADL", "Score Issue Complexity",
    "Assess issue complexity and autonomous solvability")]
[FlowNode("Scored", "Skipped", "Error")]
public class ScoreIssueComplexityActivity : Activity
{
    [Input] public Input<string> IssueJson { get; set; }
    [Input] public Input<string> Repository { get; set; }
    [Input] public Input<int> MinSolvabilityScore { get; set; } = new(60);
    [Input] public Input<string> MaxComplexity { get; set; } = new("complex");

    [Output] public Output<string?> ScoringResultJson { get; set; }
    [Output] public Output<string?> Complexity { get; set; }
    [Output] public Output<int> SolvabilityScore { get; set; }
    [Output] public Output<string?> SkipReason { get; set; }
}
```

**Outcomes:**
- `Scored` -- issue scored and passes thresholds
- `Skipped` -- issue fails solvability or complexity threshold
- `Error` -- scoring service unavailable (should still allow fallback to unscored selection)

---

## Phase 2: Dependency-Aware Issue Ordering

**Goal:** Build a dependency graph of candidate issues and use it to order selection.

### 2A: Dependency Detector

**New file:** `packages/orchestrator/src/dependencies/dependency-detector.ts`

Scans issue bodies and comments for dependency references.

**Detection methods:**

1. **Explicit references:** Regex patterns in issue body and comments:
   - `depends on #(\d+)` / `blocked by #(\d+)` / `requires #(\d+)`
   - `after #(\d+)` / `prerequisite: #(\d+)`
   - Pattern: `/(?:depends?\s+on|blocked?\s+by|requires?|after|prerequisite:?)\s+#(\d+)/gi`

2. **Label-based:** Labels matching pattern `depends-on:(\d+)` or `blocked-by:(\d+)`.

3. **AI-inferred (optional):** When enabled, pass candidate issue descriptions to LLM and ask it to identify likely dependencies. This is expensive and should be opt-in.

**Interface:**

```typescript
interface IDependencyDetector {
  detectDependencies(issues: CandidateIssue[]): Promise<IssueDependency[]>;
}
```

### 2B: Dependency Graph Builder

**New file:** `packages/orchestrator/src/dependencies/dependency-graph.ts`

Builds a directed acyclic graph (DAG) from detected dependencies and provides ordering.

```typescript
class IssueDependencyGraph {
  private adjacency: Map<number, Set<number>>;  // issueNumber -> depends-on set

  addDependency(from: number, to: number): void;
  hasCycle(): boolean;
  getBlockedIssues(): number[];                   // issues with unresolved deps
  topologicalSort(): number[];                    // ordered issue numbers
  getEligibleIssues(resolved: Set<number>): number[];  // issues whose deps are all resolved
}
```

**Circular dependency handling:**
1. Run cycle detection (Kahn's algorithm or DFS-based).
2. If cycle detected, log warning with cycle path.
3. Remove the weakest edge (lowest confidence detection) to break the cycle.
4. If still cyclic, fall back to configured `circular_fallback` strategy.

### 2C: Elsa Activity - BuildDependencyGraphActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/BuildDependencyGraphActivity.cs`

```csharp
[Activity("Tamma.ADL", "Build Dependency Graph",
    "Analyze candidate issues for inter-issue dependencies")]
[FlowNode("Done", "Error")]
public class BuildDependencyGraphActivity : Activity
{
    [Input] public Input<string> CandidateIssuesJson { get; set; }
    [Input] public Input<string> Repository { get; set; }

    [Output] public Output<string?> DependencyGraphJson { get; set; }
    [Output] public Output<string?> BlockedIssuesJson { get; set; }
    [Output] public Output<int> EligibleCount { get; set; }
}
```

---

## Phase 3: Selection Strategies and Progress Reporting

### 3A: Selection Strategy Interface and Registry

**New file:** `packages/orchestrator/src/strategies/selection-strategy.ts`

```typescript
interface ISelectionStrategy {
  name: string;
  select(candidates: ScoredCandidate[], context: SelectionContext): ScoredCandidate | null;
}

interface ScoredCandidate {
  issue: CandidateIssue;
  scoring: IssueScoringResult;
  dependency: IssueDependency;
  eligible: boolean;                 // not blocked by dependencies
}

interface SelectionContext {
  previouslySelected: number[];      // issue numbers selected this session
  currentBudgetUsd: number;          // budget spent so far today
  currentHourlyCount: number;        // issues processed this hour
  recentIssues: number[];            // recently worked issue numbers
}
```

**Built-in strategies:**

| Strategy | File | Selection Logic |
|----------|------|-----------------|
| `OldestFirstStrategy` | `strategies/oldest-first.ts` | Sort by `createdAt` ascending, pick first eligible |
| `PriorityWeightedStrategy` | `strategies/priority-weighted.ts` | Weighted score: `ageWeight * ageScore + labelWeight * labelScore + complexityWeight * (100 - complexityScore)`. Pick highest. |
| `SimplestFirstStrategy` | `strategies/simplest-first.ts` | Sort by `complexityScore` ascending, pick first eligible |
| `HardestFirstStrategy` | `strategies/hardest-first.ts` | Sort by `complexityScore` descending (within eligible), pick first |
| `RoundRobinStrategy` | `strategies/round-robin.ts` | Track last-selected category. Pick next category in rotation. Within category, use oldest-first. |
| `DependencyOptimalStrategy` | `strategies/dependency-optimal.ts` | Use topological sort from dependency graph. Pick first in topo order. |

**Registry:**

```typescript
class SelectionStrategyRegistry {
  private strategies: Map<string, ISelectionStrategy> = new Map();

  register(strategy: ISelectionStrategy): void;
  get(name: string): ISelectionStrategy;
  list(): string[];
}
```

### 3B: Elsa Activity - ApplySelectionStrategyActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/ApplySelectionStrategyActivity.cs`

```csharp
[Activity("Tamma.ADL", "Apply Selection Strategy",
    "Select the best issue from scored candidates using configured strategy")]
[FlowNode("Selected", "NoEligible", "Error")]
public class ApplySelectionStrategyActivity : Activity
{
    [Input] public Input<string> ScoredCandidatesJson { get; set; }
    [Input] public Input<string> Strategy { get; set; } = new("oldest-first");
    [Input] public Input<string?> StrategyConfigJson { get; set; }

    [Output] public Output<string?> SelectedIssueJson { get; set; }
    [Output] public Output<int> SelectedIssueNumber { get; set; }
    [Output] public Output<string?> SelectionReason { get; set; }
}
```

### 3C: Progress Reporter

**New file:** `packages/orchestrator/src/progress/progress-reporter.ts`

```typescript
class ProgressReporter {
  constructor(
    private platform: IGitPlatform,
    private config: ProgressConfig,
    private logger: ILogger
  ) {}

  async reportStage(
    owner: string,
    repo: string,
    issueNumber: number,
    stage: PipelineStage,
    status: 'started' | 'completed' | 'failed' | 'skipped',
    details?: string
  ): Promise<void> {
    if (this.config.mode === 'silent') return;
    if (this.config.mode === 'milestones-only' && !this.isMilestone(stage)) return;

    const emoji = this.getStatusEmoji(status);
    const timestamp = dayjs.utc().format('HH:mm:ss UTC');

    let comment = `${emoji} **${this.getStageName(stage)}** - ${status} (${timestamp})`;

    if (details && this.config.collapsible) {
      comment += `\n\n<details><summary>Details</summary>\n\n${details}\n\n</details>`;
    } else if (details) {
      comment += `\n\n${details}`;
    }

    try {
      await this.platform.addIssueComment(owner, repo, issueNumber, comment);
    } catch (err) {
      // Never fail the pipeline because a comment could not be posted
      this.logger.warn('Failed to post progress comment', {
        issueNumber,
        stage,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private isMilestone(stage: PipelineStage): boolean {
    const milestones: PipelineStage[] = [
      'plan-approval', 'tdd-cycle', 'pr-creation', 'merge-complete'
    ];
    return milestones.includes(stage);
  }

  private getStatusEmoji(status: string): string {
    // Using text indicators instead of emoji for log compatibility
    const indicators: Record<string, string> = {
      started: '[START]',
      completed: '[DONE]',
      failed: '[FAIL]',
      skipped: '[SKIP]',
    };
    return indicators[status] ?? '[INFO]';
  }

  private getStageName(stage: PipelineStage): string {
    const names: Record<PipelineStage, string> = {
      'context-gathering': 'Context Gathering',
      'plan-generation': 'Plan Generation',
      'plan-approval': 'Plan Approval',
      'branch-creation': 'Branch Creation',
      'tdd-cycle': 'TDD Implementation',
      'pr-creation': 'PR Creation',
      'ci-pipeline': 'CI Pipeline',
      'review-fix': 'Review Fix',
      'merge-approval': 'Merge Approval',
      'merge-complete': 'Merge Complete',
    };
    return names[stage] ?? stage;
  }
}
```

### 3D: Elsa Activity - PostProgressCommentActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/PostProgressCommentActivity.cs`

```csharp
[Activity("Tamma.ADL", "Post Progress Comment",
    "Post a structured status update comment on the GitHub issue")]
[FlowNode("Done", "Error")]
public class PostProgressCommentActivity : Activity
{
    [Input] public Input<string> Repository { get; set; }
    [Input] public Input<int> IssueNumber { get; set; }
    [Input] public Input<string> Stage { get; set; }
    [Input] public Input<string> Status { get; set; }   // started|completed|failed|skipped
    [Input] public Input<string?> Details { get; set; }
    [Input] public Input<string> ReportingMode { get; set; } = new("milestones-only");
}
```

---

## Phase 4: Decomposition Integration

**Goal:** Wire Story 2.14's decomposition engine into the selection workflow so that complex issues are broken down before entering the pipeline.

**Prerequisite:** Story 2.14 (Issue Decomposition Engine) must provide a callable decomposition service.

### 4A: Decomposition Trigger Logic

**New file:** `packages/orchestrator/src/decomposition/decomposition-trigger.ts`

```typescript
class DecompositionTrigger {
  constructor(
    private decompositionEngine: IDecompositionEngine,  // from Story 2.14
    private platform: IGitPlatform,
    private config: DecompositionConfig,
    private logger: ILogger
  ) {}

  async shouldDecompose(scoring: IssueScoringResult): boolean {
    if (!this.config.enabled) return false;
    const triggerLevel = this.config.triggerComplexity;
    const levels = ['simple', 'medium', 'complex', 'too-complex'];
    return levels.indexOf(scoring.complexity) >= levels.indexOf(triggerLevel);
  }

  async decompose(
    owner: string,
    repo: string,
    issue: CandidateIssue,
    scoring: IssueScoringResult
  ): Promise<DecompositionResult> {
    // Check if approval needed
    const needsApproval = this.needsApproval(scoring.complexity);

    // Call Story 2.14's decomposition engine
    const subtasks = await this.decompositionEngine.decompose({
      issueNumber: issue.number,
      title: issue.title,
      body: issue.body,
      labels: issue.labels,
      complexity: scoring.complexity,
      maxSubtasks: this.config.maxSubtasks,
    });

    if (needsApproval) {
      // Create a decomposition approval bookmark (Elsa) or prompt (TS engine)
      return { subtasks, approved: false, needsApproval: true };
    }

    // Auto-approved: create sub-issues on GitHub
    const createdSubtasks = await this.createSubIssues(owner, repo, issue, subtasks);

    return { subtasks: createdSubtasks, approved: true, needsApproval: false };
  }

  private async createSubIssues(
    owner: string,
    repo: string,
    parentIssue: CandidateIssue,
    subtasks: Subtask[]
  ): Promise<CreatedSubtask[]> {
    const created: CreatedSubtask[] = [];

    for (const subtask of subtasks) {
      const body = [
        `Parent issue: #${parentIssue.number}`,
        '',
        subtask.description,
        '',
        '## Acceptance Criteria',
        ...subtask.acceptanceCriteria.map(ac => `- [ ] ${ac}`),
        '',
        '---',
        '_Auto-decomposed by Tamma from #' + parentIssue.number + '_',
      ].join('\n');

      const newIssue = await this.platform.createIssue(owner, repo, {
        title: `[${parentIssue.number}] ${subtask.title}`,
        body,
        labels: [...parentIssue.labels, 'tamma-subtask', 'tamma-auto'],
      });

      created.push({
        issueNumber: newIssue.number,
        title: subtask.title,
        parentIssueNumber: parentIssue.number,
      });
    }

    // Post summary on parent issue
    const subtaskList = created.map(s => `- #${s.issueNumber}: ${s.title}`).join('\n');
    await this.platform.addIssueComment(owner, repo, parentIssue.number,
      `Tamma has decomposed this issue into ${created.length} subtasks:\n\n${subtaskList}\n\n` +
      `Each subtask will be processed independently. Progress will be tracked here.`
    );

    return created;
  }

  private needsApproval(complexity: string): boolean {
    const levels = ['simple', 'medium', 'complex', 'too-complex'];
    const approvalLevel = this.config.requireApprovalLevel;
    return levels.indexOf(complexity) >= levels.indexOf(approvalLevel);
  }
}
```

### 4B: Elsa Activity - DecomposeAndCreateSubtasksActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/DecomposeAndCreateSubtasksActivity.cs`

```csharp
[Activity("Tamma.ADL", "Decompose Issue",
    "Break a complex issue into subtasks and create GitHub sub-issues")]
[FlowNode("Decomposed", "NeedsApproval", "Skipped", "Error")]
public class DecomposeAndCreateSubtasksActivity : Activity
{
    [Input] public Input<string> IssueJson { get; set; }
    [Input] public Input<string> ScoringResultJson { get; set; }
    [Input] public Input<string> Repository { get; set; }
    [Input] public Input<string> DecompositionConfigJson { get; set; }

    [Output] public Output<string?> SubtasksJson { get; set; }
    [Output] public Output<int> SubtaskCount { get; set; }
    [Output] public Output<bool> DecompositionApplied { get; set; }
}
```

**Outcomes:**
- `Decomposed` -- issue broken into subtasks, sub-issues created
- `NeedsApproval` -- decomposition generated but requires human approval
- `Skipped` -- issue not complex enough to decompose
- `Error` -- decomposition service failed

---

## Phase 5: Budget-Aware Rate Limiting

**Goal:** Extend the existing `CheckLimitsActivity` with per-hour rate limits, budget tracking, and adaptive cooldown.

### 5A: Budget Tracker

**New file:** `packages/orchestrator/src/limits/budget-tracker.ts`

```typescript
class BudgetTracker {
  private dailySpend: number = 0;
  private hourlyIssueTimestamps: number[] = [];  // timestamps of issues started this hour

  recordSpend(amountUsd: number): void {
    this.dailySpend += amountUsd;
  }

  recordIssueStart(): void {
    this.hourlyIssueTimestamps.push(Date.now());
    // Prune timestamps older than 1 hour
    const oneHourAgo = Date.now() - 3600000;
    this.hourlyIssueTimestamps = this.hourlyIssueTimestamps.filter(t => t > oneHourAgo);
  }

  getDailySpend(): number { return this.dailySpend; }

  getHourlyIssueCount(): number {
    const oneHourAgo = Date.now() - 3600000;
    return this.hourlyIssueTimestamps.filter(t => t > oneHourAgo).length;
  }

  isApproachingBudget(limit: number, threshold: number): boolean {
    return this.dailySpend >= limit * threshold;
  }

  isBudgetExceeded(limit: number): boolean {
    return this.dailySpend >= limit;
  }

  isHourlyLimitExceeded(limit: number): boolean {
    return this.getHourlyIssueCount() >= limit;
  }

  resetDaily(): void {
    this.dailySpend = 0;
  }
}
```

### 5B: Adaptive Cooldown

**New file:** `packages/orchestrator/src/limits/adaptive-cooldown.ts`

```typescript
class AdaptiveCooldown {
  private currentCooldownSeconds: number;
  private consecutiveFailures: number = 0;

  constructor(private config: AdaptiveCooldownConfig) {
    this.currentCooldownSeconds = config.baseSeconds;
  }

  recordSuccess(): void {
    if (this.config.resetOnSuccess) {
      this.currentCooldownSeconds = this.config.baseSeconds;
      this.consecutiveFailures = 0;
    }
  }

  recordFailure(): void {
    this.consecutiveFailures++;
    this.currentCooldownSeconds = Math.min(
      this.config.baseSeconds * Math.pow(this.config.failureMultiplier, this.consecutiveFailures),
      this.config.maxSeconds
    );
  }

  getCooldownMs(): number {
    return this.currentCooldownSeconds * 1000;
  }

  getCooldownSeconds(): number {
    return this.currentCooldownSeconds;
  }
}
```

### 5C: Extended Elsa Activity - CheckBudgetLimitsActivity

**New file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/CheckBudgetLimitsActivity.cs`

Extends the existing `CheckLimitsActivity` with budget and hourly checks.

```csharp
[Activity("Tamma.ADL", "Check Budget Limits",
    "Check daily budget, hourly rate, and operational limits")]
[FlowNode("Continue", "BudgetWarning", "Stop")]
public class CheckBudgetLimitsActivity : Activity
{
    [Input] public Input<int> IssuesCompleted { get; set; }
    [Input] public Input<string?> ConfigJson { get; set; }
    [Input] public Input<decimal> CurrentDailySpendUsd { get; set; } = new(0m);
    [Input] public Input<int> CurrentHourlyCount { get; set; } = new(0);

    [Output] public Output<string?> StopReason { get; set; }
    [Output] public Output<int> AdaptiveCooldownSeconds { get; set; }
    [Output] public Output<bool> BudgetWarning { get; set; }
}
```

**Outcomes:**
- `Continue` -- within all limits
- `BudgetWarning` -- approaching budget threshold but not exceeded (proceed with caution)
- `Stop` -- hard limit exceeded, do not proceed

---

## Phase 6: Workflow Integration

**Goal:** Wire all components into a new Elsa sub-workflow and update the TypeScript engine.

### 6A: New Elsa Workflow - IntelligentIssueSelectionWorkflow

**New file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IntelligentIssueSelectionWorkflow.cs`

```
Flow:
  QueryCandidateIssues (existing SelectIssueActivity, modified to return ALL candidates)
    → ScoreCandidates (batch scoring via ScoreIssueComplexityActivity)
    → BuildDependencyGraph (BuildDependencyGraphActivity)
    → ApplyStrategy (ApplySelectionStrategyActivity)
    → [Issue selected?]
      Yes → [Complex enough to decompose?]
        Yes → DecomposeIssue (DecomposeAndCreateSubtasksActivity)
          → [NeedsApproval?]
            Yes → WaitForDecompositionApproval (bookmark)
            No  → SelectFirstSubtask → Output selected subtask
          → [Approved?]
            Yes → SelectFirstSubtask → Output selected subtask
            No  → Skip this issue, loop back to ApplyStrategy with remaining candidates
        No  → Output selected issue
      No → Output noIssues
```

**DefinitionId:** `intelligent-issue-selection`

### 6B: Modify AdlOrchestratorWorkflow

Update the ADL orchestrator to dispatch to the intelligent selection workflow instead of the basic one when configured.

The `SingleIssueCycleWorkflow` already dispatches to `issue-selection`. The change is:

1. Add an input `useIntelligentSelection` (boolean, default false).
2. When true, the single-issue-cycle dispatches to `intelligent-issue-selection` instead of `issue-selection`.
3. The intelligent workflow returns the same outputs as the basic one (issueJson, issueNumber, issueTitle) so no downstream changes are needed.

### 6C: Wire Progress Reporting into SingleIssueCycleWorkflow

Add `PostProgressCommentActivity` calls at each major step in the single-issue-cycle. These are fire-and-forget -- they do not block the pipeline and their failure does not affect the main flow.

**Insertion points:**

| After Activity | Stage | Status |
|---------------|-------|--------|
| extractContext | context-gathering | completed |
| extractPlan | plan-generation | completed |
| planApproved (True) | plan-approval | completed |
| extractBranch | branch-creation | completed |
| tddRetrySuccess (True) | tdd-cycle | completed |
| tddRetrySuccess (False) | tdd-cycle | failed |
| extractPr (prCreated True) | pr-creation | completed |
| ciRetryPassed (True) | ci-pipeline | completed |
| ciRetryPassed (False) | ci-pipeline | failed |
| hasReviewComments | review-fix | completed |
| mergeDecision (True) | merge-approval | completed |
| mergeSuccess (True) | merge-complete | completed |
| mergeSuccess (False) | merge-complete | failed |

Implementation: Each progress call is wrapped in a try/catch at the activity level so failures are swallowed.

### 6D: Update TypeScript Engine

**Modified file:** `packages/orchestrator/src/engine.ts`

Add intelligent selection to the existing `selectIssue` method:

```typescript
async selectIssue(): Promise<IssueData | null> {
  this.setState(EngineState.SELECTING_ISSUE);

  const candidates = await this.queryCandidates();
  if (candidates.length === 0) {
    this.setState(EngineState.IDLE);
    return null;
  }

  // NEW: Score candidates if intelligent selection is enabled
  let scoredCandidates = candidates;
  if (this.config.engine.intelligentSelection?.enabled) {
    scoredCandidates = await this.scoreAndFilter(candidates);
    if (scoredCandidates.length === 0) {
      this.logger.info('No issues passed scoring/solvability filters');
      this.setState(EngineState.IDLE);
      return null;
    }
  }

  // NEW: Apply configured strategy
  const strategy = this.strategyRegistry.get(
    this.config.engine.intelligentSelection?.strategy ?? 'oldest-first'
  );
  const selected = strategy.select(scoredCandidates, this.getSelectionContext());

  if (!selected) {
    this.setState(EngineState.IDLE);
    return null;
  }

  // Existing: assign and comment
  await this.assignAndComment(selected);

  return selected;
}
```

Add progress reporting calls to each pipeline step in `runPipeline()`.

### 6E: Update AdlModels

**Modified file:** `apps/tamma-elsa/src/Tamma.Activities/ADL/Models/AdlModels.cs`

Add new models:

```csharp
public class IssueScoringResult
{
    public int IssueNumber { get; set; }
    public string Complexity { get; set; } = "medium";  // simple|medium|complex|too-complex
    public int ComplexityScore { get; set; }              // 0-100
    public int SolvabilityScore { get; set; }             // 0-100
    public string? SolvabilityRationale { get; set; }
    public int EstimatedFiles { get; set; }
    public decimal EstimatedHours { get; set; }
    public List<string> Signals { get; set; } = new();
    public DateTime AssessedAt { get; set; } = DateTime.UtcNow;
}

public class IssueDependencyInfo
{
    public int IssueNumber { get; set; }
    public List<int> DependsOn { get; set; } = new();
    public List<int> BlockedBy { get; set; } = new();
    public string DetectionMethod { get; set; } = "explicit";
    public int Confidence { get; set; } = 100;
}

public class SelectionStrategyConfig
{
    public string Strategy { get; set; } = "oldest-first";
    public string MaxComplexity { get; set; } = "complex";
    public int MinSolvabilityScore { get; set; } = 60;
    public bool EnableDecomposition { get; set; } = true;
    public string DecompositionApproval { get; set; } = "auto";
    public string ProgressReporting { get; set; } = "milestones-only";
}
```

---

## Flow Diagrams

### Intelligent Issue Selection Flow (replaces basic SelectIssue)

```
                    +-------------------+
                    | Query Candidates  |
                    | (GitHub API)      |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Score Each Issue   |
                    | (complexity +      |
                    |  solvability)      |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Filter: Remove    |
                    | unsolvable +      |
                    | too-complex       |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Build Dependency  |
                    | Graph             |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Filter: Remove    |
                    | blocked issues    |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Apply Strategy    |
                    | (configurable)    |
                    +--------+----------+
                             |
                    +--------v----------+
                    | Selected Issue    |
                    +--------+----------+
                             |
                    +--------v----------+
               No   | Complex enough   |  Yes
           +--------+ to decompose?    +--------+
           |        +------------------+        |
           |                                    |
   +-------v-------+              +-------------v-----------+
   | Return issue   |              | Decompose into subtasks |
   | to pipeline    |              +-------------+-----------+
   +---------------+                             |
                                   +-------------v-----------+
                              No   | Needs human approval?   | Yes
                          +--------+                         +--------+
                          |        +-------------------------+        |
                  +-------v-------+                       +-----------v------+
                  | Create GitHub  |                       | Bookmark: await  |
                  | sub-issues     |                       | approval         |
                  +-------+-------+                       +-----------+------+
                          |                                           |
                  +-------v-------+                       +-----------v------+
                  | Select first   |                       | Approved?        |
                  | subtask        |             +---------+--------+---------+
                  +-------+-------+              |                  |
                          |                 Yes  |             No   |
                  +-------v-------+     +--------v---+     +--------v--------+
                  | Return subtask|     | Create     |     | Skip, try next  |
                  | to pipeline   |     | sub-issues |     | candidate       |
                  +--------------+      +--------+---+     +-----------------+
                                                 |
                                        +--------v---+
                                        | Select     |
                                        | first      |
                                        | subtask    |
                                        +--------+---+
                                                 |
                                        +--------v--------+
                                        | Return subtask  |
                                        | to pipeline     |
                                        +----------------+
```

### Updated ADL Orchestrator Flow

```
  InitConfig
      |
      v
  CheckBudgetLimits ----[Stop]----> SetOutputs → Finish
      |
  [Continue/BudgetWarning]
      |
      v
  DispatchCycle (single-issue-cycle)
      |                    uses intelligent-issue-selection
      v                    when configured
  ParseResult
      |
      v
  ShouldContinue?
      |           |
   [Yes]       [No]
      |           |
      v           v
  AdaptiveCooldown    SetOutputs → Finish
      |
      +---> loop back to CheckBudgetLimits
```

---

## File Summary

### New Files (TypeScript)

| File | Purpose |
|------|---------|
| `packages/orchestrator/src/scoring/complexity-scorer.ts` | Heuristic complexity scoring |
| `packages/orchestrator/src/scoring/ai-complexity-scorer.ts` | LLM-assisted complexity scoring |
| `packages/orchestrator/src/scoring/solvability-assessor.ts` | Solvability assessment |
| `packages/orchestrator/src/dependencies/dependency-detector.ts` | Dependency detection from issue text |
| `packages/orchestrator/src/dependencies/dependency-graph.ts` | DAG construction and topological sort |
| `packages/orchestrator/src/strategies/selection-strategy.ts` | Strategy interface and registry |
| `packages/orchestrator/src/strategies/oldest-first.ts` | Oldest-first strategy |
| `packages/orchestrator/src/strategies/priority-weighted.ts` | Priority-weighted strategy |
| `packages/orchestrator/src/strategies/simplest-first.ts` | Simplest-first strategy |
| `packages/orchestrator/src/strategies/hardest-first.ts` | Hardest-first strategy |
| `packages/orchestrator/src/strategies/round-robin.ts` | Round-robin strategy |
| `packages/orchestrator/src/strategies/dependency-optimal.ts` | Dependency-optimal strategy |
| `packages/orchestrator/src/progress/progress-reporter.ts` | Issue progress comment posting |
| `packages/orchestrator/src/decomposition/decomposition-trigger.ts` | Decomposition trigger and sub-issue creation |
| `packages/orchestrator/src/limits/budget-tracker.ts` | Budget tracking |
| `packages/orchestrator/src/limits/adaptive-cooldown.ts` | Adaptive cooldown logic |

### New Files (C# / Elsa)

| File | Purpose |
|------|---------|
| `Tamma.Activities/ADL/ScoreIssueComplexityActivity.cs` | Complexity + solvability scoring activity |
| `Tamma.Activities/ADL/BuildDependencyGraphActivity.cs` | Dependency graph construction activity |
| `Tamma.Activities/ADL/ApplySelectionStrategyActivity.cs` | Strategy application activity |
| `Tamma.Activities/ADL/PostProgressCommentActivity.cs` | Progress comment posting activity |
| `Tamma.Activities/ADL/CheckBudgetLimitsActivity.cs` | Extended budget-aware limits activity |
| `Tamma.Activities/ADL/DecomposeAndCreateSubtasksActivity.cs` | Decomposition trigger activity |
| `Tamma.ElsaServer/Workflows/IntelligentIssueSelectionWorkflow.cs` | New intelligent selection sub-workflow |

### Modified Files

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/Models/AdlModels.cs` | Add `IssueScoringResult`, `IssueDependencyInfo`, `SelectionStrategyConfig` models |
| `Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Add progress comment calls at each step; add `useIntelligentSelection` input routing |
| `Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs` | Replace `CheckLimitsActivity` with `CheckBudgetLimitsActivity`; use adaptive cooldown |
| `packages/orchestrator/src/engine.ts` | Add scoring, strategy, and progress reporting to `selectIssue` and `runPipeline` |

### Test Files (to create alongside each new file)

Every new `.ts` file gets a colocated `.test.ts` file. Every new C# activity gets a corresponding test class. Tests should be written before implementation (TDD).

---

## Estimated Effort

| Phase | Effort | Dependencies |
|-------|--------|--------------|
| Phase 1: Scoring + Solvability | 3-4 days | None |
| Phase 2: Dependency Ordering | 2-3 days | None |
| Phase 3: Strategies + Progress | 3-4 days | None |
| Phase 4: Decomposition Integration | 2-3 days | Phase 1, Story 2.14 |
| Phase 5: Budget Rate Limiting | 1-2 days | None |
| Phase 6: Workflow Integration | 3-4 days | Phases 1-5 |
| **Total** | **14-20 days** | |

Phases 1, 2, 3, and 5 can be developed in parallel. Phase 4 requires Phase 1 (for scoring) and Story 2.14 (for the decomposition engine). Phase 6 requires all previous phases.
