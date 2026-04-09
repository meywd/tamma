# Plan Review Redesign

**When**: During sub-workflow optimization (new PlanReviewWorkflow)
**Related**: SingleIssueCycle step 4, Story 2-17

## Design

Multi-role LLM panel review with iterative discussion rounds.

### Reviewing Roles

| Role | Reviews For |
|------|------------|
| Architect | Design soundness, patterns, scalability |
| Developer | Implementability, edge cases, complexity |
| QA/Tester | Test strategy, coverage gaps |
| Security | Vulnerabilities, input validation, auth |
| DevOps | Deployment impact, infrastructure, config |
| Product Owner | Requirements match, scope correctness |
| Orchestrator | Task ordering, dependency graph |

### Flow

```
Round 1: Independent Review
  → Each role receives plan + role-specific context (from vector DB)
  → Each returns: verdict (approve/concerns/reject) + comments + suggestions

If all approve → output: approved

If any non-approval:
  Round 2: Group Discussion
    → All roles see ALL concerns from Round 1
    → For each concern, group decides resolution:
      - fix: modify the plan (planner rewrites affected section)
      - defer: create a new issue for this concern
      - split: original issue is too big, decompose
      - accept: acknowledge risk, proceed anyway
      - needsHuman: can't resolve autonomously, escalate
    → Produce modified plan + resolution list

  Round 3 (if needed): Re-review modified plan
    → Only roles that had concerns re-review
    → Repeat until consensus or max rounds (default 3)

Final output:
  decision: "approved" | "needsHuman"
  plan: { ...modified plan }
  deferred: [{ title, body, labels, reason }]   # new issues to create
  split: [{ title, body, labels }]               # sub-issues replacing original
  breakingChanges: [{ description, impact }]     # flagged items
  discussionLog: [{ round, role, verdict, comments }]  # full audit trail
```

### Parallel Execution
Round 1 reviews can run in parallel (each role is independent).
Round 2 discussion is sequential (each concern discussed in order).

### Auto-Approval Criteria
Skip the full panel review if ALL of:
- Complexity is trivial or simple
- No external dependencies added
- No breaking changes
- No security-related changes
- Work item type is chore or docs

### Issue Creation
When concerns are deferred or issues split:
- PlanReviewWorkflow creates GitHub issues via the platform API
- Labels from the priority config system (Story 26-4)
- Links back to the original issue

### Events
- CYCLE.PLAN.REVIEW.STARTED
- CYCLE.PLAN.REVIEW.ROUND (per round, per role)
- CYCLE.PLAN.REVIEW.DISCUSSION (per concern resolution)
- CYCLE.PLAN.REVIEW.COMPLETED (final decision)
