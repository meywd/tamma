# Workflow: Mentorship

**Definition ID:** `mentorship`
**Class:** `MentorshipWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs`

## Purpose

The Mentorship Workflow orchestrates the **complete mentorship session lifecycle** for guiding a junior developer through implementing a story. It manages 28 states with outcome-based routing, guard conditions, and 8 sub-workflow invocations.

## Full Flow Diagram

```
+------------------------+
| INIT_STORY_PROCESSING  |
+---+----------------+---+
    |                |
   Done            Error
    |                |
    v                v
+--------------------+  +--------+
| Context Gathering  |  | FAILED |
| (7-1F)             |  +--------+
+---------+----------+
          |
          v
+-----------------+
| VALIDATE_STORY  |
+--+------+----+--+
   |      |    |
 Valid  BugIssue Invalid/Error
   |      |    |
   v      |    v
   |      v  +--------+
   |  +----------+ | FAILED |
   |  | Debugging| +--------+
   |  | (7-1I)   |
   |  +----+-----+
   |       |
   |       +---> QUALITY_GATE_CHECK (bug fast path)
   |
   v
+----------------------------+
| ASSESS_JUNIOR_CAPABILITY   |<-----+-----+-----+
+--+------+------+-------+--+      |     |     |
   |      |      |       |         |     |     |
 Correct Partial Incorrect Timeout  |     |     |
   |      |      |       |         |     |     |
   v      |      v       v         |     |     |
LLM Call  |  RE_EXPLAIN  DIAGNOSE  |     |     |
(7-1B)    |  +--+--+     BLOCKER   |     |     |
   |      |  |     |              |     |     |
   v      |  |  Explained         |     |     |
PLAN_     |  |     +---------------+     |     |
DECOMP.   |  |  MaxRetries              |     |
          |  |     +--> ESCALATE         |     |
          |  |  Error                    |     |
          |  |     +--> FAILED           |     |
          |  |                           |     |
          v  |                           |     |
   +------+--+                           |     |
   | Incr Assessment                     |     |
   +------+---+                          |     |
          |                              |     |
          v                              |     |
   +------------------+                 |     |
   | Retries < 3?     |                 |     |
   +--+------------+--+                 |     |
     YES            NO                  |     |
      |              |                  |     |
      v              v                  |     |
  CLARIFY        ESCALATE              |     |
  +--+--+                              |     |
  |     |                              |     |
Clarified MaxRetries/Error             |     |
  |     |                              |     |
  +-----+------------------------------+     |
        +--> ESCALATE/FAILED                  |
                                              |
+-------------------+                         |
| PLAN_DECOMPOSITION|                         |
+--+------------+---+                         |
   |            |                             |
 Planned      Error                           |
   |            |                             |
   v            v                             |
REVIEW_PLAN  FAILED                           |
+--+--------+--+                              |
   |        |                                 |
 Approved NeedsAdjustment                     |
   |        |                                 |
   v        v                                 |
START_   Incr Plan Iteration                  |
IMPL.    +----+----+                          |
              |                               |
              v                               |
         Plan Iters < 2?                      |
         +--+------+--+                       |
           YES      NO                        |
            |        |                        |
            v        v                        |
        ADJUST_PLAN  START_IMPL.              |
        +---+---+                             |
            |                                 |
          Adjusted                            |
            |                                 |
            +--> PLAN_DECOMPOSITION           |
                                              |
+-------------------+                        |
| START_IMPLEMENTATION|                       |
+--------+----------+                        |
         |                                    |
       Started                                |
         |                                    |
         v                                    |
     TDD (7-1H)                               |
         |                                    |
         v                                    |
+-------------------+                        |
| MONITOR_PROGRESS  |<--------+----+----+    |
+--+--+--+--+------+         |    |    |    |
   |  |  |  |  |             |    |    |    |
 Steady|Complete|Stalled      |    |    |    |
   |   |     |  |             |    |    |    |
   +---+     |  |  Circular   |    |    |    |
   (loop)    |  |     |       |    |    |    |
             |  |     v       |    |    |    |
             |  | DETECT_     |    |    |    |
             |  | PATTERN     |    |    |    |
             |  | +-+--+      |    |    |    |
             |  |  |   |      |    |    |    |
             |  | Pattern No  |    |    |    |
             |  | Found  Pat  |    |    |    |
             |  |  |     |    |    |    |    |
             |  |  v     +----+    |    |    |
             |  | DIAGNOSE         |    |    |
             |  | BLOCKER          |    |    |
             |  +--+               |    |    |
             |     |               |    |    |
             |   Hint/Guidance/    |    |    |
             |   Assistance/       |    |    |
             |   Escalate          |    |    |
             |     |               |    |    |
             |     v               |    |    |
             |   PROVIDE_HINT -----+    |    |
             |     (Done -> Monitor)    |    |
             |     (Error -> Guidance)  |    |
             |                          |    |
             |   PROVIDE_GUIDANCE ------+    |
             |     (Done -> Monitor)         |
             |     (Error -> Assistance)     |
             |                               |
             |   PROVIDE_ASSISTANCE ---------+
             |     (Done -> Start Impl.)
             |     (Error -> Escalate)
             |
             v  Slowing --> PROVIDE_GUIDANCE
         +---+---+
         | Reset |
         | Quality|
         +---+---+
             |
             v
+-------------------+
| QUALITY_GATE_CHECK|<------+
+--+------+-----+--+       |
   |      |     |           |
 Passed Failed Error        |
   |      |     |           |
   v      |     v           |
Testing   |  DIAGNOSE       |
(7-1C)    |  BLOCKER        |
   |      |                 |
   v      v                 |
Reset   Incr Quality Retry  |
Review  +----+----+         |
Iter.        |              |
   |         v              |
   v    Retries < 3?        |
PREPARE  +--+------+--+    |
CODE     YES        NO     |
REVIEW    |          |      |
   |      v          v      |
   |  AUTO_FIX   MANUAL_FIX |
   |  +--+--+   +--+--+    |
   |  |     |   |     |    |
   | Fixed Manual Fix NeedHelp
   |  |  Needed Applied |    |
   |  |     |   |      v    |
   |  +-----+   +------+   |
   |  |                     |
   |  +---------------------+
   |
   v
+-------------------+
| Code Review (7-1D)|
+---------+---------+
          |
          v
+-------------------+
| MONITOR_REVIEW    |<--------+
+--+------+-----+--+         |
   |      |     |             |
 Approved Changes Pending    |
   |      Requested  |       |
   |      |     +----+       |
   |      v     (loop)       |
   |  Incr Review Iter.      |
   |  +----+----+            |
   |       |                 |
   |       v                 |
   |  Iterations < 5?        |
   |  +--+------+--+        |
   |    YES      NO          |
   |     |        |          |
   |     v        v          |
   |  GUIDE_   MERGE_AND_   |
   |  FIXES    COMPLETE      |
   |  +--+--+               |
   |     |                   |
   |   Guided                |
   |     |                   |
   |     v                   |
   |  RE_REQUEST_REVIEW      |
   |  +--+------+--+        |
   |     |      |            |
   |  Requested MaxRetries   |
   |     |      |            |
   |     +------+------------+
   |            |
   |            v
   |      MERGE_AND_COMPLETE
   |
   v
+-------------------+
| MERGE_AND_COMPLETE|
+--------+----------+
         |
       Merged
         |
         v
+-------------------+
| GENERATE_REPORT   |
+--------+----------+
         |
       Generated
         |
         v
+-------------------+
| UPDATE_SKILL      |
| PROFILE           |
+--------+----------+
         |
       Updated
         |
         v
+-------------------+
| SESSION COMPLETED |
+-------------------+
```

## Key Paths

### Happy Path
`INIT -> VALIDATE -> ASSESS -> PLAN -> IMPLEMENT -> MONITOR -> QUALITY -> REVIEW -> MERGE -> REPORT -> PROFILE -> COMPLETED`

### Bug Fast Path
`INIT -> VALIDATE -> [BugIssue] -> Debugging -> QUALITY -> ...`

### Assessment Loop (max 3)
`ASSESS -> [Partial] -> CLARIFY -> ASSESS`
`ASSESS -> [Incorrect] -> RE_EXPLAIN -> ASSESS`

### Planning Loop (max 2)
`PLAN -> REVIEW -> [NeedsAdjustment] -> ADJUST -> PLAN`

### Blocker Escalation (4 levels)
`DIAGNOSE -> HINT -> GUIDANCE -> ASSISTANCE -> ESCALATE`

### Quality Retry (max 3)
`QUALITY -> [Failed] -> AUTO_FIX -> QUALITY`

### Review Iteration (max 5)
`REVIEW -> [ChangesRequested] -> GUIDE_FIXES -> RE_REQUEST -> REVIEW`

## State Inventory (28 states)

| Category | States |
|----------|--------|
| Initialization | INIT_STORY_PROCESSING, VALIDATE_STORY |
| Assessment | ASSESS_JUNIOR, CLARIFY_REQUIREMENTS, RE_EXPLAIN_STORY |
| Planning | PLAN_DECOMPOSITION, REVIEW_PLAN, ADJUST_PLAN |
| Implementation | START_IMPLEMENTATION, MONITOR_PROGRESS, DETECT_PATTERN |
| Blocker | DIAGNOSE_BLOCKER, PROVIDE_HINT, PROVIDE_GUIDANCE, PROVIDE_ASSISTANCE, ESCALATE_TO_SENIOR |
| Quality | QUALITY_GATE_CHECK, AUTO_FIX_ISSUES, MANUAL_FIX_REQUIRED |
| Review | PREPARE_CODE_REVIEW, MONITOR_REVIEW, GUIDE_FIXES, RE_REQUEST_REVIEW |
| Completion | MERGE_AND_COMPLETE, GENERATE_REPORT, UPDATE_SKILL_PROFILE, COMPLETED |
| Exception | PAUSED, CANCELLED, FAILED, TIMEOUT |

## Sub-Workflows Dispatched

| Sub-Workflow | Story | Used In |
|-------------|-------|---------|
| [LLM Call](Workflow-LLM-Call) | 7-1B | Assessment, Planning |
| [Context Gathering](Workflow-Context-Gathering) | 7-1F | Initialization |
| [Testing Pipeline](Workflow-Testing) | 7-1C | Quality gate |
| [Code Review](Workflow-Code-Review) | 7-1D | Review phase |
| [Assessment](Workflow-Mentorship#assessment) | 7-1E | Assessment phase |
| [Blocker Diagnosis](Workflow-Blocker-Diagnosis) | 7-1G | Blocker diagnosis |
| [TDD Cycle](Workflow-TDD-Cycle) | 7-1H | Implementation |
| [Debugging](Workflow-Debugging) | 7-1I | Bug fast path |

## Guard Conditions

| Guard | Max | Exceeded Action |
|-------|-----|-----------------|
| Assessment retries | 3 | Escalate to senior |
| Plan iterations | 2 | Proceed with best plan |
| Quality retries | 3 | Manual fix required |
| Review iterations | 5 | Force merge |
| Blocker escalation | 4 | Escalate to senior |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `SessionId` | Guid | Unique session identifier |
| `StoryId` | string | Story being implemented |
| `JuniorId` | string | Junior developer identifier |
| `AssessmentAttempt` | int | Current assessment attempt count |
| `PlanIteration` | int | Current plan iteration count |
| `QualityRetryCount` | int | Current quality retry count |
| `ReviewIteration` | int | Current review iteration count |
| `BlockerEscalationLevel` | int | Current blocker escalation level |

---

## Assessment

**Definition ID:** `assessment`
**Class:** `AssessmentWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`

### Purpose

Evaluates a junior developer's understanding of story requirements through AI-generated questions, response analysis, and skill profiling.

### Flow

```
Read Inputs
    |
    v
Context Gathering (7-1F)
    |
    v
Generate Questions (AI)
    |
    v
Deliver Questions
    |
    v
Wait For Response (bookmark)
    |
    +--> [Responded]          +--> [Timeout]
    |                         |
    v                         v
Store Response            Set Timeout Result
    |                         |
    v                         v
Analyze Response (AI)     Update Skill Profile
    |                         |
    v                         v
Classify Result           Set Output (timeout)
    |                         |
    v                         v
Update Skill Profile      Expose Outputs
    |
    v
Set Output (response)
    |
    v
Expose Outputs
```

### Classification Outcomes

| Status | Next State | Description |
|--------|-----------|-------------|
| Correct | PLAN_DECOMPOSITION | Junior understands the story |
| Partial | CLARIFY_REQUIREMENTS | Some gaps, needs clarification |
| Incorrect | RE_EXPLAIN_STORY | Major misunderstanding |
| Timeout | DIAGNOSE_BLOCKER | No response within window |

### Outputs

| Output | Type | Description |
|--------|------|-------------|
| `assessmentResult` | string | Full assessment result JSON |
| `nextState` | string | Recommended next mentorship state |
| `status` | string | Assessment status (Correct/Partial/Incorrect/Timeout) |

---

_See also: [Blocker Diagnosis](Workflow-Blocker-Diagnosis) | [Code Review](Workflow-Code-Review) | [Workflows Index](Workflows)_
