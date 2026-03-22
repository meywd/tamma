# Epic 7: Autonomous Mentorship Workflow

## Overview

This epic covers the implementation of the autonomous mentorship workflow system that guides junior developers through story implementation. The workflow is driven by a 28-state state machine implemented as ELSA activities in the .NET engine (`apps/tamma-elsa/`), with a TypeScript bridge layer for integration with the main Tamma engine.

The mentorship workflow autonomously handles the full lifecycle: assessing a junior developer's understanding of a story, gathering project context, using Claude for AI-powered analysis, decomposing stories into implementation plans, monitoring progress, diagnosing blockers, running quality gates, managing code reviews, and completing the merge. The system adapts dynamically to each developer's skill level, detects circular behavior patterns, and escalates when necessary.

## Stories

### Core Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 7-1 | Mentorship State Machine Core | P1 | Planned |
| 7-2 | Skill Assessment Activity | P1 | Planned |
| 7-3 | Context Gathering Activity | P1 | Planned |
| 7-4 | Claude Analysis Activity | P1 | Planned |
| 7-5 | Plan Decomposition Activity | P1 | Planned |
| 7-6 | Progress Monitoring & Pattern Detection | P1 | Planned |
| 7-7 | Blocker Diagnosis & Resolution Activity | P2 | Planned |
| 7-8 | Quality Gate & Auto-Fix Pipeline | P2 | Planned |
| 7-9 | Code Review & Merge Workflow | P2 | Planned |
| 7-10 | TypeScript Engine Bridge & Session API | P1 | Planned |

### ELSA Sub-Workflow Stories

These stories elevate the TypeScript-level logic from the core stories into code-first ELSA workflows — composable, auditable, pausable/resumable, and visible in ELSA Studio.

| Story | Title | Enhances | Priority | Status |
|-------|-------|----------|----------|--------|
| 7-1A | Main Mentorship Workflow (Code-First Flowchart) | 7-1 | P1 | Planned |
| 7-1B | LLM Call Sub-Workflow | 9-1, 9-3, 9-5 | P1 | Planned |
| 7-1C | Testing Sub-Workflow | 7-7, 7-8 | P1 | Planned |
| 7-1D | Code Review Sub-Workflow | 7-9 | P2 | Planned |
| 7-1E | Assessment Sub-Workflow | 7-2, 7-4 | P1 | Planned |
| 7-1F | Context Gathering Sub-Workflow | 7-3 | P1 | Planned |
| 7-1G | Blocker Diagnosis Sub-Workflow | 7-6, 7-5 | P2 | Planned |
| 7-1H | TDD Sub-Workflow | New | P1 | Planned |
| 7-1I | Debugging Sub-Workflow | New | P1 | Planned |

#### Sub-Workflow Dependency Graph

```
7-1B (LLM Call) ← foundation, no deps
7-1F (Context)  ← no deps (uses GitHub/DB directly)

7-1C (Testing)     ← needs 7-1B
7-1E (Assessment)  ← needs 7-1B, 7-1F
7-1D (Code Review) ← needs 7-1B
7-1G (Blocker)     ← needs 7-1B, 7-1F
7-1I (Debugging)   ← needs 7-1B, 7-1C, 7-1F
7-1H (TDD)         ← needs 7-1B, 7-1C, 7-1I

7-1A (Main Workflow) ← needs all of the above
```

#### Implementation Order

1. **Parallel**: 7-1B (LLM Call) + 7-1F (Context Gathering) — no dependencies
2. **Parallel**: 7-1C (Testing) + 7-1E (Assessment) + 7-1D (Code Review) + 7-1G (Blocker) — depend on 7-1B/7-1F
3. **Sequential**: 7-1I (Debugging) — needs Testing
4. **Sequential**: 7-1H (TDD) — needs Testing + Debugging
5. **Final**: 7-1A (Main Workflow) — wires everything together

## Architecture

```
+-----------------------------------------------------------------------------+
|                  EPIC 7: AUTONOMOUS MENTORSHIP WORKFLOW                      |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-----------------------------------------------------------------------+  |
|  |                    STATE MACHINE CORE (7-1)                            |  |
|  |                                                                        |  |
|  |  INIT --> ASSESS --> PLAN --> IMPLEMENT --> QUALITY --> REVIEW --> MERGE |  |
|  |    |        |         |          |            |          |              |  |
|  |    +--------+---------+----------+-- DIAGNOSE BLOCKER --+              |  |
|  |                                      |                                 |  |
|  |                               DETECT PATTERN --> STRATEGIC REDIRECT    |  |
|  |                                                                        |  |
|  +-----------------------------------------------------------------------+  |
|                          |                                                   |
|         +----------------+------------------+------------------+             |
|         v                v                  v                  v             |
|  +--------------+  +--------------+  +--------------+  +-------------+      |
|  |    SKILL     |  |   CONTEXT    |  |    CLAUDE    |  |    PLAN     |      |
|  |  ASSESSMENT  |  |  GATHERING   |  |   ANALYSIS   |  | DECOMPOSE   |      |
|  |    (7-2)     |  |    (7-3)     |  |    (7-4)     |  |   (7-5)     |      |
|  |              |  |              |  |              |  |             |      |
|  | Evaluate     |  | Repo context |  | AI-powered   |  | Break down  |      |
|  | understanding|  | File changes |  | assessment   |  | into steps  |      |
|  | Skill level  |  | Test context |  | Code review  |  | Guided plan |      |
|  | Gaps         |  | Patterns     |  | Blocker diag |  | Templates   |      |
|  +--------------+  +--------------+  +--------------+  +-------------+      |
|                                                                             |
|  +-----------------------------------------------------------------------+  |
|  |                    MONITORING & RESOLUTION LAYER                       |  |
|  |                                                                        |  |
|  |  +----------------+  +----------------+  +------------------+          |  |
|  |  |   PROGRESS     |  |    BLOCKER     |  |   QUALITY GATE   |          |  |
|  |  |   MONITORING   |  |   DIAGNOSIS    |  |   & AUTO-FIX     |          |  |
|  |  |    (7-6)       |  |    (7-7)       |  |    (7-8)         |          |  |
|  |  |                |  |                |  |                  |          |  |
|  |  | Git tracking   |  | Categorize     |  | Tests, lint,     |          |  |
|  |  | Pattern detect |  | Root cause     |  | coverage, build  |          |  |
|  |  | Stall detect   |  | Auto-resolve   |  | Auto-fix minor   |          |  |
|  |  | Encouragement  |  | Escalation     |  | Block critical   |          |  |
|  |  +----------------+  +----------------+  +------------------+          |  |
|  |                                                                        |  |
|  +-----------------------------------------------------------------------+  |
|                                                                             |
|  +-----------------------------------------------------------------------+  |
|  |                    REVIEW & INTEGRATION LAYER                          |  |
|  |                                                                        |  |
|  |  +---------------------+  +----------------------------------------+  |  |
|  |  |   CODE REVIEW &     |  |   TS ENGINE BRIDGE & SESSION API       |  |  |
|  |  |   MERGE (7-9)       |  |   (7-10)                               |  |  |
|  |  |                     |  |                                         |  |  |
|  |  | PR creation         |  | ElsaClient TypeScript wrapper          |  |  |
|  |  | Review monitoring   |  | REST API endpoints for sessions        |  |  |
|  |  | Fix guidance        |  | Webhook handlers for state changes     |  |  |
|  |  | Merge automation    |  | Real-time SSE for dashboard            |  |  |
|  |  +---------------------+  +----------------------------------------+  |  |
|  |                                                                        |  |
|  +-----------------------------------------------------------------------+  |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## State Machine Overview

The mentorship workflow is driven by a 28-state machine defined in `Tamma.Core.Enums.MentorshipState`. The states are organized into groups:

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

## Dependencies

### On Other Epics

- **Epic 1**: Provider interfaces (`IAgentProvider`, `ILLMProvider`) for AI-powered analysis
- **Epic 2**: Engine integration for executing mentorship sessions through the orchestrator
- **Epic 5**: Observability package for monitoring workflow metrics and health
- **Epic 6**: Context & Knowledge Management (codebase indexer for context gathering, knowledge base for learning capture)

### External Dependencies

- **ELSA Workflows 3**: .NET workflow engine (`Elsa.Workflows.Core`, `Elsa.Workflows.Management`)
- **.NET 8.0**: Runtime for ELSA server and custom activities
- **PostgreSQL**: Persistence for workflow state and mentorship session data
- **Anthropic Claude API**: AI analysis for assessment, code review, and guidance
- **GitHub API**: Repository integration for PR creation, commit monitoring, review tracking

## Implementation Phases

### Phase 1: Core Foundation (Stories 7-1, 7-10)
- Define the complete state machine with all transitions and guards
- Build the TypeScript bridge layer so the TS engine can start/monitor ELSA workflows
- Establish REST API endpoints for session lifecycle management

### Phase 2: Assessment & Context (Stories 7-2, 7-3, 7-4)
- Implement skill assessment activity with configurable question banks
- Build context gathering with GitHub integration, file analysis, and pattern detection
- Integrate Claude API for AI-powered analysis (assessment, code review, blocker diagnosis, guidance)

### Phase 3: Planning & Monitoring (Stories 7-5, 7-6)
- Implement plan decomposition that breaks stories into guided steps
- Build progress monitoring with stall detection and circular behavior pattern recognition

### Phase 4: Resolution & Quality (Stories 7-7, 7-8, 7-9)
- Implement blocker diagnosis and resolution workflow
- Build quality gate pipeline with auto-fix capabilities
- Implement code review creation, monitoring, fix guidance, and merge automation

### Phase 5: ELSA Sub-Workflows (Stories 7-1A through 7-1I)
Elevate all core story logic into code-first ELSA workflows, replacing the JSON-based `WorkflowSeeder` approach.

- **Foundation** (7-1B, 7-1F): LLM Call with provider chain/circuit breaker, Context Gathering with parallel fetching
- **Capabilities** (7-1C, 7-1D, 7-1E, 7-1G): Testing pipeline, Code Review lifecycle, Assessment, Blocker Diagnosis
- **New Workflows** (7-1H, 7-1I): TDD red-green-refactor cycle, Systematic Debugging (3 modes)
- **Main Workflow** (7-1A): 28-state Flowchart wiring all sub-workflows together

Key design principles:
- All logic in ELSA workflows (not TypeScript classes) — every retry, fallback, and decision is a visible activity
- Config-driven: provider chains, prompts, tools, thresholds from `appsettings.json`/env vars
- Composable: each sub-workflow standalone AND callable as child workflow
- LLM Call (7-1B) is the universal building block — all AI interactions go through it
- Bookmark-based waiting at external events (CI results, review decisions, junior responses)
- Code-first C# definitions registered at startup, visible in ELSA Studio

## Existing Implementation

The following ELSA activities already exist in `apps/tamma-elsa/src/Tamma.Activities/`:

| Activity | File | Status |
|----------|------|--------|
| AssessJuniorCapabilityActivity | `Mentorship/AssessJuniorCapabilityActivity.cs` | Implemented (simulated) |
| ClaudeAnalysisActivity | `AI/ClaudeAnalysisActivity.cs` | Implemented (mock + real API) |
| ContextGatheringActivity | `AI/ContextGatheringActivity.cs` | Implemented (partial simulation) |
| MonitorImplementationActivity | `Mentorship/MonitorImplementationActivity.cs` | Implemented |
| DiagnoseBlockerActivity | `Mentorship/DiagnoseBlockerActivity.cs` | Implemented |
| ProvideGuidanceActivity | `Mentorship/ProvideGuidanceActivity.cs` | Implemented |
| QualityGateCheckActivity | `Mentorship/QualityGateCheckActivity.cs` | Implemented |
| CodeReviewActivity | `Mentorship/CodeReviewActivity.cs` | Implemented |
| MergeCompleteActivity | `Mentorship/MergeCompleteActivity.cs` | Implemented |
| GitHubActivity | `Integration/GitHubActivity.cs` | Implemented |
| SlackActivity | `Integration/SlackActivity.cs` | Implemented |
| SuggestionGeneratorActivity | `AI/SuggestionGeneratorActivity.cs` | Implemented |

The stories in this epic define the expected behavior, acceptance criteria, and testing requirements for these existing implementations. Several activities use simulated/mock logic and need to be replaced with real integrations.

## Success Metrics

- State machine coverage: all 28 states reachable and tested
- State transition correctness: 100% of valid transitions handled, invalid transitions rejected
- Assessment accuracy: AI-powered assessment agrees with human evaluation >80% of the time
- Context relevance: gathered context rated relevant >85% by downstream analysis
- Plan decomposition quality: >90% of decomposed plans are actionable without modification
- Blocker resolution: >70% of blockers auto-resolved without human escalation
- Quality gate pass rate: >85% of implementations pass all gates on first or second attempt
- Code review cycle time: <2 review iterations average before merge
- Session completion rate: >80% of mentorship sessions reach COMPLETED state
- TypeScript bridge latency: <500ms for session start, <200ms for state queries
