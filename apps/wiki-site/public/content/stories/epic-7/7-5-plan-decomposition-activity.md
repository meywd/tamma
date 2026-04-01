---
title: "Story 7-5: Plan Decomposition Activity"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need to decompose stories into step-by-step implementation plans tailored to a junior developer's skill level so that they have clear, actionable guidance for building the feature without being overwhelmed.

## Description

Implement the plan decomposition activity that takes a story (with its context from Story 7-3) and the junior developer's assessment results (from Story 7-2) and produces a structured implementation plan. The plan breaks the story into ordered tasks, where each task has clear instructions, expected outputs, relevant file references, and estimated time. Plans are adaptive: a level-1 developer gets more granular steps with detailed explanations, while a level-5 developer gets higher-level tasks with minimal hand-holding.

The plan flows through three sub-states in the state machine: `PLAN_DECOMPOSITION` (generate the plan), `REVIEW_PLAN` (validate and approve), and `ADJUST_PLAN` (refine based on feedback). If the junior cannot produce a plan within the timeout window, the system provides a template plan via the `PROVIDE_TEMPLATE_PLAN` state (defined in the UML but mapped to ADJUST_PLAN in the 28-state implementation).

This activity uses Claude (via Story 7-4) to generate the plan and to evaluate junior-submitted plans. The output feeds directly into `START_IMPLEMENTATION` and `MONITOR_PROGRESS` to track completion of each step.

## Acceptance Criteria

### AC1: Plan Generation from Story Context
- [ ] Accept story context (title, description, acceptance criteria, technical requirements) and junior profile (skill level, assessment result)
- [ ] Use Claude Analysis (GuidanceGeneration mode) to produce an ordered list of implementation tasks
- [ ] Each task includes: sequential number, title, detailed description, expected output (what the junior should produce), relevant files (from context gathering), estimated time in minutes, dependencies on other tasks
- [ ] Plans include setup tasks (environment, branch creation) as first steps
- [ ] Plans include testing tasks (write tests, run tests) interspersed with implementation
- [ ] Plans include cleanup tasks (documentation, commit message) as final steps
- [ ] Total estimated time is reasonable relative to story complexity

### AC2: Skill-Level Adaptation
- [ ] Level 1-2 (Beginner):
  - 8-15 highly granular tasks
  - Each task describes exactly what to type/click
  - Include "verify your work" checkpoints after each task
  - Provide code snippets and examples in task descriptions
  - Estimated time per task: 10-30 minutes
- [ ] Level 3 (Intermediate):
  - 5-10 tasks at moderate granularity
  - Describe what to do, not exactly how
  - Include testing expectations per task
  - Estimated time per task: 15-45 minutes
- [ ] Level 4-5 (Advanced):
  - 3-7 high-level tasks
  - Focus on architecture and edge cases, not step-by-step
  - Assume knowledge of standard patterns
  - Estimated time per task: 30-90 minutes

### AC3: Plan Review (Junior-Submitted Plans)
- [ ] Accept a plan submitted by the junior developer (when they create their own plan)
- [ ] Use Claude Analysis (Assessment mode) to evaluate the plan against the story requirements
- [ ] Evaluation criteria: completeness (all acceptance criteria covered), logical ordering, appropriate granularity, missing steps, wrong approach
- [ ] Return evaluation result: `GoodPlan` (approve as-is), `MissingSteps` (minor adjustments needed), `WrongApproach` (fundamental rethink needed)
- [ ] For `MissingSteps`: provide specific suggestions for what to add
- [ ] For `WrongApproach`: explain why and suggest the correct approach

### AC4: Plan Adjustment
- [ ] When plan review returns `MissingSteps`, merge suggestions into the existing plan
- [ ] When plan review returns `WrongApproach`, generate a new plan from scratch
- [ ] Track adjustment count (max 3 adjustments before providing template plan)
- [ ] Each adjustment preserves completed tasks (do not regenerate already-finished steps)
- [ ] Log each adjustment with reason and diff

### AC5: Template Plan Fallback
- [ ] When the junior cannot create a plan within the timeout (default: 10 minutes)
- [ ] When plan adjustments exceed max retries (default: 3)
- [ ] Generate a complete template plan using Claude with the most granular detail level
- [ ] Template plan is marked as system-generated (for analytics)
- [ ] Junior can still modify the template plan before proceeding

### AC6: Plan Output Structure
- [ ] Plan is stored in the session context as a structured `ImplementationPlan` object
- [ ] Plan includes: planId, version, tasks (ordered list), totalEstimatedMinutes, generatedBy (ai/junior/template), adjustmentCount
- [ ] Each task includes: taskNumber, title, description, expectedOutput, relevantFiles, estimatedMinutes, dependencies, status (pending/in_progress/completed/skipped)
- [ ] Plan is serializable to JSON for storage and dashboard display
- [ ] Plan version increments on each adjustment

### AC7: State Machine Integration
- [ ] After plan generation/approval, transition to `START_IMPLEMENTATION`
- [ ] The first task in the plan becomes the current implementation task
- [ ] Plan tasks are tracked through `MONITOR_PROGRESS` (Story 7-6)
- [ ] When a task is completed, the next task is assigned via `NEXT_IMPLEMENTATION_STEP` transition
- [ ] When all tasks are completed, transition to `QUALITY_GATE_CHECK`

### AC8: Plan Persistence
- [ ] Plans are stored in the `mentorship_sessions.context` JSONB field
- [ ] Plan history (all versions) is preserved for learning capture
- [ ] Plan task completion status is updated in real-time as the junior progresses
- [ ] Completed plans are available for analytics and pattern extraction

## Technical Design

### Plan Data Structures

```typescript
export interface ImplementationPlan {
  planId: string;
  version: number;
  sessionId: string;
  storyId: string;
  juniorId: string;

  // Plan metadata
  generatedBy: 'ai' | 'junior' | 'template';
  generatedAt: Date;
  adjustmentCount: number;
  skillLevel: number;

  // Tasks
  tasks: ImplementationTask[];
  totalEstimatedMinutes: number;

  // Review
  reviewResult?: PlanReviewResult;
  reviewHistory: PlanReviewResult[];
}

export interface ImplementationTask {
  taskNumber: number;
  title: string;
  description: string;
  expectedOutput: string;
  relevantFiles: string[];
  estimatedMinutes: number;
  dependencies: number[];  // taskNumbers this depends on
  status: TaskStatus;
  category: TaskCategory;
  startedAt?: Date;
  completedAt?: Date;
  notes?: string;
}

export type TaskStatus = 'pending' | 'in_progress' | 'completed' | 'skipped' | 'blocked';

export type TaskCategory =
  | 'setup'           // Environment setup, branch creation
  | 'implementation'  // Core coding work
  | 'testing'         // Writing and running tests
  | 'integration'     // Connecting components
  | 'documentation'   // Comments, docs, commit messages
  | 'review_prep';    // Preparing for code review

export interface PlanReviewResult {
  result: 'GoodPlan' | 'MissingSteps' | 'WrongApproach' | 'Timeout';
  confidence: number;
  feedback: string;
  missingSteps?: string[];
  suggestions?: string[];
  timestamp: Date;
}

export interface PlanDecompositionRequest {
  sessionId: string;
  storyId: string;
  juniorId: string;
  skillLevel: number;
  assessmentResult: {
    status: string;
    confidence: number;
    gaps: string[];
    strengths: string[];
  };
  codeContext: {
    storyTitle: string;
    storyDescription: string;
    acceptanceCriteria: string[];
    technicalRequirements: Record<string, string>;
    relevantFiles: string[];
    similarPatterns: { patternName: string; filePath: string }[];
    projectStructure: { mainDirectories: string[]; configFiles: string[] };
  };
  existingPlan?: ImplementationPlan;  // for adjustments
}
```

### Plan Decomposition Service Interface

```typescript
export interface IPlanDecompositionActivity {
  // Generate a new plan
  generatePlan(request: PlanDecompositionRequest): Promise<ImplementationPlan>;

  // Review a junior-submitted plan
  reviewPlan(
    plan: ImplementationPlan,
    storyContext: PlanDecompositionRequest['codeContext']
  ): Promise<PlanReviewResult>;

  // Adjust an existing plan based on review feedback
  adjustPlan(
    plan: ImplementationPlan,
    reviewResult: PlanReviewResult,
    request: PlanDecompositionRequest
  ): Promise<ImplementationPlan>;

  // Generate a template plan (fallback)
  generateTemplatePlan(request: PlanDecompositionRequest): Promise<ImplementationPlan>;

  // Task management
  markTaskCompleted(plan: ImplementationPlan, taskNumber: number): ImplementationPlan;
  getNextTask(plan: ImplementationPlan): ImplementationTask | undefined;
  getPlanProgress(plan: ImplementationPlan): {
    completed: number;
    total: number;
    percentage: number;
    currentTask?: ImplementationTask;
    estimatedRemainingMinutes: number;
  };
}
```

## Dependencies

- Story 7-1: State machine core (PLAN_DECOMPOSITION, REVIEW_PLAN, ADJUST_PLAN states and transitions)
- Story 7-2: Skill Assessment Activity (provides assessment result and skill level)
- Story 7-3: Context Gathering Activity (provides code context for plan generation)
- Story 7-4: Claude Analysis Activity (AI-powered plan generation and review)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (for session context persistence)

## Testing Strategy

### Unit Tests
- Plan generation produces correct number of tasks per skill level:
  - Level 1: 8-15 tasks
  - Level 3: 5-10 tasks
  - Level 5: 3-7 tasks
- Each generated task has all required fields (title, description, expectedOutput, estimatedMinutes)
- Task dependencies form a valid DAG (no circular dependencies)
- First task is always a setup task
- Last task is always a cleanup/documentation task
- Testing tasks are interspersed (not all at the end)
- Total estimated time is positive and non-zero
- Plan version increments on adjustment
- Template plan is marked as `generatedBy: 'template'`
- `getNextTask` returns first pending task with all dependencies completed
- `getNextTask` returns undefined when all tasks completed
- `getPlanProgress` returns correct percentage
- `markTaskCompleted` updates task status and timestamps

### Integration Tests
- Full plan generation using Claude API (requires ANTHROPIC_API_KEY)
- Plan review of a well-formed junior-submitted plan returns GoodPlan
- Plan review of an incomplete plan returns MissingSteps with specific suggestions
- Plan review of a fundamentally flawed plan returns WrongApproach
- Plan adjustment merges suggestions without duplicating existing tasks
- Template plan generation when timeout fires
- Plan persistence in session context JSONB field
- Multi-version plan history preservation

### Edge Case Tests
- Story with no acceptance criteria (plan should still be generated from description)
- Story with no technical requirements (plan infers from description)
- Very large story (>20 acceptance criteria) produces a manageable number of tasks
- Very simple story (1 acceptance criterion) produces at least 3 tasks (setup, implement, test)
- Junior-submitted plan with 0 tasks (invalid, should trigger template)
- Junior-submitted plan with >50 tasks (too granular, should suggest consolidation)
- Concurrent plan adjustments for the same session (last write wins with version check)

## Configuration

```yaml
mentorship:
  plan_decomposition:
    max_adjustments: 3
    timeout_minutes: 10
    task_count:
      beginner_min: 8
      beginner_max: 15
      intermediate_min: 5
      intermediate_max: 10
      advanced_min: 3
      advanced_max: 7
    categories:
      require_setup: true
      require_testing: true
      require_documentation: true
    template:
      always_most_granular: true
      include_code_snippets: true
    review:
      completeness_weight: 0.4
      ordering_weight: 0.2
      granularity_weight: 0.2
      approach_weight: 0.2
```

## Success Metrics

- Plan actionability: >90% of generated plans are actionable without modification (based on user feedback or review approval rate)
- Skill adaptation: task count correlates inversely with skill level (measurable difference between level 1 and level 5 plans)
- Plan review accuracy: AI plan review agrees with human evaluation >80%
- Template usage rate: <20% of sessions require template plan fallback
- Adjustment convergence: >80% of plans approved within 2 adjustments
- Task completion correlation: >85% of planned tasks are actually completed during implementation (not skipped or blocked)
- Time estimate accuracy: actual task duration within 50% of estimated duration for >70% of tasks

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
