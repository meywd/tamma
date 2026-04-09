# Epic 7: Autonomous Mentorship Workflow

**Status:** Near Complete (18/19 done, TDD sub-workflow in progress)
**Stories:** 19 (7-1 through 7-10, 7-1A through 7-1I)
**Location:** `apps/tamma-elsa/` (C# / .NET 8), `packages/orchestrator/` (TypeScript bridge)

## Overview

Epic 7 implements an autonomous mentorship workflow system that guides developers through story implementation. The workflow is driven by a 28-state state machine implemented as ELSA activities in the .NET engine, with a TypeScript bridge layer for integration with the main Tamma engine.

The mentorship workflow handles the full lifecycle: assessing a developer's understanding, gathering project context, using Claude for AI-powered analysis, decomposing stories into plans, monitoring progress, diagnosing blockers, running quality gates, managing code reviews, and completing the merge. The system adapts dynamically to each developer's skill level and detects circular behavior patterns.

## 28-State Machine

| Group | States | Purpose |
|-------|--------|---------|
| Initialization | INIT_STORY_PROCESSING, VALIDATE_STORY | Load and validate story context |
| Assessment | ASSESS_JUNIOR_CAPABILITY, CLARIFY_REQUIREMENTS, RE_EXPLAIN_STORY | Evaluate developer understanding |
| Planning | PLAN_DECOMPOSITION, REVIEW_PLAN, ADJUST_PLAN | Create and refine implementation plan |
| Implementation | START_IMPLEMENTATION, MONITOR_PROGRESS, PROVIDE_GUIDANCE, DETECT_PATTERN | Guide and monitor coding work |
| Blockers | DIAGNOSE_BLOCKER, PROVIDE_HINT, PROVIDE_ASSISTANCE, ESCALATE_TO_SENIOR | Resolve impediments |
| Quality | QUALITY_GATE_CHECK, AUTO_FIX_ISSUES, MANUAL_FIX_REQUIRED | Validate code quality |
| Review | PREPARE_CODE_REVIEW, MONITOR_REVIEW, GUIDE_FIXES, RE_REQUEST_REVIEW | Manage code review cycle |
| Completion | MERGE_AND_COMPLETE, GENERATE_REPORT, UPDATE_SKILL_PROFILE, COMPLETED | Finalize and learn |
| Exception | PAUSED, CANCELLED, FAILED, TIMEOUT | Handle abnormal conditions |

## ELSA Activities (Implemented)

Located in `apps/tamma-elsa/src/Tamma.Activities/`:

### Mentorship Activities
| Activity | File | Purpose |
|----------|------|---------|
| AssessJuniorCapabilityActivity | `Mentorship/` | Evaluate developer skill level |
| MonitorImplementationActivity | `Mentorship/` | Track implementation progress |
| DiagnoseBlockerActivity | `Mentorship/` | Identify and categorize blockers |
| ProvideGuidanceActivity | `Mentorship/` | Generate contextual guidance |
| QualityGateCheckActivity | `Mentorship/` | Run quality checks |
| CodeReviewActivity | `Mentorship/` | Manage code review process |
| MergeCompleteActivity | `Mentorship/` | Handle merge and completion |

### AI Activities
| Activity | File | Purpose |
|----------|------|---------|
| ClaudeAnalysisActivity | `AI/` | AI-powered assessment and analysis |
| ContextGatheringActivity | `AI/` | Gather project and code context |
| SuggestionGeneratorActivity | `AI/` | Generate improvement suggestions |

### Assessment Activities
| Activity | File | Purpose |
|----------|------|---------|
| GenerateQuestionsActivity | `Assessment/` | Generate skill assessment questions |
| DeliverQuestionsActivity | `Assessment/` | Deliver questions to developer |
| WaitForResponseActivity | `Assessment/` | Await developer answers |
| AnalyzeResponseActivity | `Assessment/` | Analyze quality of responses |
| ClassifyResultActivity | `Assessment/` | Classify assessment outcome |
| UpdateSkillProfileActivity | `Assessment/` | Update developer skill profile |

### Blocker Activities
| Activity | File | Purpose |
|----------|------|---------|
| ClassifyBlockerActivity | `Blocker/` | Categorize blocker type |
| CollectCIStatusActivity | `Blocker/` | Gather CI pipeline status |
| CollectGitActivityActivity | `Blocker/` | Analyze git commit patterns |
| CollectInactivityActivity | `Blocker/` | Detect developer inactivity |
| CollectCommunicationActivity | `Blocker/` | Gather communication context |
| DetectProgressActivity | `Blocker/` | Assess overall progress |
| EscalateToSeniorActivity | `Blocker/` | Escalate unresolvable blockers |

### Context Activities
| Activity | File | Purpose |
|----------|------|---------|
| FetchFileContentsActivity | `Context/` | Load relevant source files |
| FetchRecentCommitsActivity | `Context/` | Get recent commit history |
| FetchSessionHistoryActivity | `Context/` | Load mentorship session history |
| FetchSimilarPatternsActivity | `Context/` | Find similar code patterns |
| FetchStoryMetadataActivity | `Context/` | Load story details |
| FetchTestResultsActivity | `Context/` | Get test execution results |
| AssembleContextActivity | `Context/` | Combine context sources |
| ApplyBudgetActivity | `Context/` | Apply token budget limits |

### Debug Activities
| Activity | File | Purpose |
|----------|------|---------|
| CollectErrorMessagesActivity | `Debug/` | Gather error messages |
| CollectGitHistoryActivity | `Debug/` | Relevant git history |
| CollectRelevantCodeActivity | `Debug/` | Related source code |
| CollectReproductionStepsActivity | `Debug/` | Reproduction steps |
| CollectTestResultsActivity | `Debug/` | Test failure details |
| ClassifyDebugContextActivity | `Debug/` | Classify debugging context |
| SelectHypothesisActivity | `Debug/` | Generate hypotheses |
| RefineHypothesisActivity | `Debug/` | Refine based on evidence |
| AIDiagnosisActivity | `Debug/` | AI-powered diagnosis |
| WriteRegressionTestActivity | `Debug/` | Generate regression tests |
| CompileDebugReportActivity | `Debug/` | Compile debug findings |
| RecordResolutionActivity | `Debug/` | Record resolution for learning |

### Tool Execution Activities (Epic 12)
| Activity | File | Purpose |
|----------|------|---------|
| IToolExecutor / ToolExecutorRegistry | `LlmCall/Tools/` | Tool execution framework |
| FileReadTool | `LlmCall/Tools/` | Read files |
| FileWriteTool | `LlmCall/Tools/` | Write files |
| SearchCodeTool | `LlmCall/Tools/` | Search codebase |
| ShellExecuteTool | `LlmCall/Tools/` | Execute shell commands |
| RunTestsTool | `LlmCall/Tools/` | Run test suites |
| GitOperationsTool | `LlmCall/Tools/` | Git operations |
| CommandValidator | `LlmCall/Tools/` | Validate shell commands |
| PathValidator | `LlmCall/Tools/` | Validate file paths |
| TokenEstimator | `LlmCall/Tools/` | Estimate token counts |
| ContextCompactor | `LlmCall/Tools/` | Compact context when near limits |

## Code-First ELSA Workflows

Located in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`:

| Workflow | File | Purpose |
|----------|------|---------|
| AdlOrchestratorWorkflow | `AdlOrchestratorWorkflow.cs` | Main ADL (Autonomous Development Loop) orchestrator |
| SingleIssueCycleWorkflow | `SingleIssueCycleWorkflow.cs` | Full issue lifecycle (largest workflow) |
| MentorshipWorkflow | `MentorshipWorkflow.cs` | 28-state mentorship flow |
| LlmCallWorkflow | `LlmCallWorkflow.cs` | LLM call with provider chain, budget, circuit breaker |
| TddWorkflow | `TddWorkflow.cs` | Test-driven development cycle |
| TddWithDebugRetryWorkflow | `TddWithDebugRetryWorkflow.cs` | TDD with debug retry (Epic 13) |
| TestingWorkflow | `TestingWorkflow.cs` | Test execution pipeline |
| CiWithDebugRetryWorkflow | `CiWithDebugRetryWorkflow.cs` | CI with debug retry (Epic 13) |
| ContextGatheringWorkflow | `ContextGatheringWorkflow.cs` | Context gathering pipeline |
| PlanGenerationWorkflow | `PlanGenerationWorkflow.cs` | Development plan generation |
| CodeReviewWorkflow | `CodeReviewWorkflow.cs` | Code review lifecycle |
| ReviewFixWorkflow | `ReviewFixWorkflow.cs` | Review fix iteration |
| BranchCreationWorkflow | `BranchCreationWorkflow.cs` | Git branch creation |
| PullRequestWorkflow | `PullRequestWorkflow.cs` | PR creation and management |
| MergeWorkflow | `MergeWorkflow.cs` | PR merge process |
| MergeApprovalWorkflow | `MergeApprovalWorkflow.cs` | Merge approval gate |
| AssessmentWorkflow | `AssessmentWorkflow.cs` | Developer assessment flow |
| BlockerDiagnosisWorkflow | `BlockerDiagnosisWorkflow.cs` | Blocker diagnosis sub-workflow |
| DebuggingWorkflow | `DebuggingWorkflow.cs` | Systematic debugging pipeline |

## TypeScript Bridge

`packages/orchestrator/src/elsa-client.ts` provides the TypeScript-to-ELSA bridge:
- HTTP client for ELSA REST API
- Workflow dispatch and signal operations
- Status querying
- Session lifecycle management

## Stories

### Core Stories

| Story | Title | Status |
|-------|-------|--------|
| 7-1 | Mentorship State Machine Core | Done |
| 7-2 | Skill Assessment Activity | Done |
| 7-3 | Context Gathering Activity | Done |
| 7-4 | Claude Analysis Activity | Done |
| 7-5 | Plan Decomposition Activity | Done |
| 7-6 | Progress Monitoring & Pattern Detection | Done |
| 7-7 | Blocker Diagnosis & Resolution | Done |
| 7-8 | Quality Gate & Auto-Fix Pipeline | Done |
| 7-9 | Code Review & Merge Workflow | Done |
| 7-10 | TypeScript Engine Bridge & Session API | Done |

### ELSA Sub-Workflow Stories

| Story | Title | Status |
|-------|-------|--------|
| 7-1A | Main Mentorship Workflow (Code-First Flowchart) | Done |
| 7-1B | LLM Call Sub-Workflow | Done |
| 7-1C | Testing Sub-Workflow | Done |
| 7-1D | Code Review Sub-Workflow | Done |
| 7-1E | Assessment Sub-Workflow | Done |
| 7-1F | Context Gathering Sub-Workflow | Done |
| 7-1G | Blocker Diagnosis Sub-Workflow | Done |
| 7-1H | TDD Sub-Workflow | In Progress |
| 7-1I | Debugging Sub-Workflow | Done |

---

_For story details, see [docs/stories/epic-7/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-7) in the repository._
