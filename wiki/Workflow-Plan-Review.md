---
title: "Workflow: Plan Review"
---

**Definition ID:** `plan-review`
**Class:** `PlanReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs`

> **Epic 39 (Story 39-14) — now a deterministic read-through shim (no LLM).** Plan review no longer exists as an independent produce-verdict pipeline: it runs **inside the Plan lifecycle's REVIEW stage** (the 39-7 unified `Review`, produced by the doc-type-aware panel) — see [Document Lifecycle](Document-Lifecycle) and [Plan Generation](Plan-Generation). This workflow now only keeps the `plan-review` call site alive: it is a pure store read-through that fetches the latest accepted `Plan` plus its review lineage and maps them onto the legacy review outputs. It has **no `llm-call`, no panel, no `DispatchWorkflow`, and no `Finish` terminal** — the 7-role panel, PO-led discussion rounds, and iterative-round escalation described below are **all removed**. The entire body below (panel, discussion, rounds) describes the retired pre-Epic-39 workflow, kept for historical reference.

## Purpose

The Plan Review workflow runs a 7-role LLM panel review of the implementation plan. Each role (Architect, Developer, QA/Tester, Security, DevOps, Product Owner, Senior Developer) reviews sequentially via the LLM Call workflow. If not all roles approve, a PO-led discussion round attempts to resolve concerns. The review supports up to 3 iterative rounds before escalating to a human.

## Flow Diagram

```
+------------------+
| Initialize       |
| (read inputs,    |
|  set round=1)    |
+--------+---------+
         |
         v
+------------------+<--------------------------+
| Architect Review |                            |
| (llm-call)       |                            |
+--------+---------+                            |
         |                                      |
         v                                      |
| Extract Arch     |                            |
+--------+---------+                            |
         |                                      |
         v                                      |
| Developer Review | --> Extract Dev             |
| Tester Review    | --> Extract Tester          |
| Security Review  | --> Extract Security        |
| DevOps Review    | --> Extract DevOps          |
| PO Review        | --> Extract PO              |
| Senior Dev Review| --> Extract Sr Dev          |
+--------+---------+                            |
         |                                      |
         v                                      |
+------------------+                            |
| Aggregate        |                            |
| Verdicts         |                            |
+--------+---------+                            |
         |                                      |
         v                                      |
+------------------+                            |
| All Approved?    |                            |
+--+------------+--+                            |
  YES            NO                             |
   |              |                             |
   v              v                             |
+----------+ +------------------+              |
| Set      | | Discussion Round |              |
| Approved | | (PO: plan-review |              |
+----+-----+ |  -discussion)    |              |
     |       +--------+---------+              |
     |                |                        |
     |                v                        |
     |       +------------------+              |
     |       | Extract          |              |
     |       | Discussion       |              |
     |       +--------+---------+              |
     |                |                        |
     |                v                        |
     |       +------------------+              |
     |       | Needs Re-review? |              |
     |       +--+------------+--+              |
     |         YES            NO               |
     |          |              |                |
     |          v              |                |
     |  +---------------+     |                |
     |  | Increment     |     |                |
     |  | Round         |     |                |
     |  +-------+-------+     |                |
     |          |              |                |
     |          v              |                |
     |  +---------------+     |                |
     |  | Round <= 3?   |     |                |
     |  +--+--------+--+     |                |
     |    YES        NO       |                |
     |     |          |       |                |
     |     |          v       |                |
     |     |  +----------+   |                |
     |     |  | Force     |   |                |
     |     |  | needsHuman|   |                |
     |     |  +----+------+   |                |
     |     |       |          |                |
     |     +-------+----------+                |
     |     |                                   |
     |     +-----------------------------------+
     |                    |
     v                    v
+----------------------------------+
| Set Outputs                      |
+----------------------------------+
         |
         v
+------------------+
| Complete         |
+------------------+
```

## Review Roles

| Role | Perspective |
|------|-------------|
| Architect | System design, architecture patterns |
| Developer | Implementation feasibility, code quality |
| Tester (QA) | Test coverage, testability |
| Security | Security implications, vulnerabilities |
| DevOps | Deployment, infrastructure concerns |
| Product Owner | Business value, scope alignment |
| Senior Developer | Orchestrator perspective, overall quality |

## Discussion Resolution Types

When not all roles approve, the PO-led discussion can produce resolutions of type:
- `fix` -- modify the plan to address the concern
- `defer` -- create a separate issue for the concern
- `split` -- split the issue into multiple sub-issues
- `accept` -- accept the concern as-is (acknowledged risk)
- `needsHuman` -- escalate to human decision

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `planJson` | string | The implementation plan JSON |
| `contextIds` | string | Context IDs JSON array |
| `workItemJson` | string | Work item JSON |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `ArchitectReview` | string | Architect's review JSON |
| `DeveloperReview` | string | Developer's review JSON |
| `TesterReview` | string | Tester's review JSON |
| `SecurityReview` | string | Security reviewer's JSON |
| `DevOpsReview` | string | DevOps reviewer's JSON |
| `ProductOwnerReview` | string | PO's review JSON |
| `SeniorDeveloperReview` | string | Senior dev's review JSON |
| `AllReviewsJson` | string | Aggregated reviews array |
| `AllApproved` | bool | Whether all 7 roles approved |
| `RoundCount` | int | Current discussion round (1-3) |
| `DiscussionLog` | string | Full discussion log as JSON array |
| `Decision` | string | Final decision |
| `ReviewNotes` | string | Summary notes |
| `Deferred` | string | Deferred items JSON array |
| `Split` | string | Split items JSON array |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decision` | string | Final decision: approved, needsModification, defer, split, or needsHuman |
| `planJson` | string | The (potentially modified) plan JSON |
| `reviewNotes` | string | Summary review notes |
| `deferred` | string | JSON array of deferred items |
| `split` | string | JSON array of split items |
| `discussionLog` | string | Full discussion log JSON |

---

_See also: [Plan Generation](/workflows/plan-generation) | [LLM Call](/workflows/llm-call) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
