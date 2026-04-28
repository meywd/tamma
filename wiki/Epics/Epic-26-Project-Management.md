# Epic 26: Project Management & Triage

**Status:** Partially implemented — the triage workflow family (ported to C# Elsa) has landed; scrum, release, and priority-configuration stories remain drafted.
**Stories:** 4 (26-1 through 26-4)
**Packages:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`, `apps/tamma-elsa/src/Tamma.Activities/ADL/`, `apps/tamma-elsa/src/Tamma.Activities/Triage/`

## Overview

Epic 26 gives Tamma an autonomous project manager. The ADL (Autonomous Development Loop) Orchestrator picks the highest-priority work item from a tenant's repositories; when it finds untriaged items, it dispatches a triage workflow that classifies them; periodic scrum workflows plan sprints, generate standup summaries, and run retrospectives; release workflows bundle changes into changelogs and create releases. Priority rules are tenant-configurable so different orgs can encode their own "what should Tamma pick next" logic.

The triage side of this epic has shipped. `AdlOrchestratorWorkflow` (the priority-based selector), `IssueTriageWorkflow` (fan-out loop), `TriageItemCycleWorkflow` (per-item singleton pipeline), `TriageContextGatheringWorkflow`, `TriagePanelReviewWorkflow`, `TriagePODecisionWorkflow`, and the ADL `SelectWorkItemActivity` are all in C#. Scrum management (26-2), release management (26-3), and the priority configuration system (26-4) are still drafted.

## Architecture

```
                     ┌─────────────────────────────────────────┐
                     │         AdlOrchestratorWorkflow          │
                     │        (tenant-scoped singleton)         │
                     │   InitAdlConfigActivity → loads rules    │
                     │   SelectWorkItemActivity →  3 outcomes:  │
                     │     Selected / NothingFound / NeedsTriage│
                     └──────┬────────┬────────────────┬─────────┘
                            │        │                │
                  Selected  │        │ NothingFound   │ NeedsTriage
                            │        │                │
                ┌───────────▼──┐   ┌─▼──────────┐   ┌─▼────────────────────────┐
                │ Single Issue │   │ Sleep/Back │   │ IssueTriageWorkflow       │
                │ Cycle #2     │   │ off loop   │   │  Fetch untriaged items →  │
                │ (existing)   │   │            │   │  dispatch singleton cycle │
                └──────────────┘   └────────────┘   │  per item (fire & forget) │
                                                    └───────────────┬──────────┘
                                                                    │
                                                                    ▼
                                    ┌───────────────────────────────────────────┐
                                    │  TriageItemCycleWorkflow (singleton)       │
                                    │  ctx gather → panel review (4 roles) →    │
                                    │  PO decision → apply labels & comment      │
                                    └───────────────────────────────────────────┘

                     ┌─────────────────────────────────────────┐
                     │        Scrum / Release (planned)         │
                     │  Sprint Planning, Standup, Retrospective,│
                     │  Changelog, Version Bump, Release PR     │
                     └─────────────────────────────────────────┘
```

**Key design**:

- **Triage is fire-and-forget from the ADL.** The orchestrator doesn't wait; on the next loop iteration it will see the now-triaged items as ready work.
- **Per-item triage runs as an Elsa singleton.** If 50 untriaged items land at once, Elsa queues dispatches; items are triaged sequentially without overloading the LLM.
- **Priority drives everything downstream.** `AdlConfig.priority.labels` + `.sources` + `.complexity` are loaded by `InitAdlConfigActivity` and used by `SelectWorkItemActivity` to rank work items.
- **Labels are the shared vocabulary.** Type (bug/feature/chore/...), priority (P0–P3), complexity (trivial/simple/medium/complex/epic), autonomy (tamma-auto / tamma-assist / needs-human). Every workflow reads and writes labels.

## Components

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `AdlOrchestratorWorkflow` | `Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs` | Outer loop. Loads config, selects a work item, branches to the matching child workflow. |
| `InitAdlConfigActivity` | `Tamma.Activities/ADL/` | Loads and validates `AdlConfig` (including priority rules); writes to workflow variable. |
| `SelectWorkItemActivity` | `Tamma.Activities/ADL/SelectWorkItemActivity.cs` | Polls 4 sources (security alerts → CI failures → tamma-auto issues → untriaged issues); returns outcome `Selected` / `NothingFound` / `NeedsTriage`. |
| `IssueTriageWorkflow` | `Tamma.ElsaServer/Workflows/IssueTriageWorkflow.cs` | Fetches untriaged items + fan-out dispatch of per-item cycles. |
| `TriageItemCycleWorkflow` | `Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs` | Singleton per-item pipeline: context → panel → PO → labels. |
| `TriageContextGatheringWorkflow` | `Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs` | Collects issue title, body, comments, similar issues, and repo context for the LLM. |
| `TriagePanelReviewWorkflow` | `Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs` | Four-role LLM review (architect, implementer, security, tester) for balanced classification. |
| `TriagePODecisionWorkflow` | `Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs` | Product-owner synthesis activity: merges panel outputs, emits final labels + priority + autonomy. |
| `FetchUntriagedItemsActivity` | `Tamma.Activities/Triage/` | Queries GitHub for items without triage labels. |
| `ApplyLabelsActivity` | `Tamma.Activities/Triage/` | Idempotently applies labels + milestone via GitHub API. |
| `PostTriageCommentActivity` | `Tamma.Activities/Triage/` | Posts the triage summary comment on the issue. |
| Scrum activities (26-2, planned) | — | Sprint planning, standup summary, retrospective. |
| Release activities (26-3, planned) | — | Changelog generation, version bump, release PR, GitHub release, deployment orchestration. |
| Priority configuration (26-4) | `AdlConfig` (shared config) | Label-to-priority mapping, source-to-priority mapping, complexity weights, time-based escalation. |

## Class diagram

```
                      ┌──────────────────────────────────┐
                      │     AdlOrchestratorWorkflow      │
                      │  singleton per tenant            │
                      └──────┬───────────────────────────┘
                             │ uses
          ┌──────────────────┼──────────────────┐
          ▼                  ▼                  ▼
 ┌─────────────────┐ ┌─────────────────┐ ┌─────────────────────┐
 │InitAdlConfig    │ │SelectWorkItem   │ │ExistingSingleIssue  │
 │Activity         │ │Activity         │ │CycleWorkflow (#2)    │
 │  loads AdlConfig│ │  scans sources  │ │                      │
 │  + priority rules│ │  ranks by prio  │ │                      │
 └─────────────────┘ │  outcomes:      │ └─────────────────────┘
                     │   Selected      │
                     │   NothingFound  │
                     │   NeedsTriage   │
                     └────────┬────────┘
                              │ NeedsTriage
                              ▼
                    ┌──────────────────────┐
                    │ IssueTriageWorkflow  │
                    │  fan-out, fire&forget │
                    └──────────┬────────────┘
                               │ dispatches (queued by Elsa)
                               ▼
                    ┌──────────────────────────────┐
                    │ TriageItemCycleWorkflow      │
                    │  (singleton)                 │
                    │  Gather Context ──▶ Panel    │
                    │  Panel (4 roles) ──▶ PO      │
                    │  PO Decision ──▶ Apply Labels│
                    └────┬────────────────┬─────────┘
                         │                │
              ┌──────────▼─┐    ┌─────────▼────────┐
              │TriageContext│    │TriagePanelReview│  (4 × LlmCallWorkflow)
              │Gathering    │    │                  │
              └─────────────┘    └──────────────────┘
                                          │
                                ┌─────────▼──────────┐
                                │TriagePODecision    │
                                └─────────┬──────────┘
                                          ▼
                                ┌──────────────────────┐
                                │ApplyLabels + Comment │
                                └──────────────────────┘

        ┌─────────────────────┐        ┌──────────────────────┐
        │ Scrum (planned)     │        │ Release (planned)    │
        │  SprintPlanning     │        │  ValidateReadiness   │
        │  DailyStandup       │        │  GenerateChangelog   │
        │  Retrospective      │        │  BumpVersion         │
        └─────────────────────┘        │  CreateReleasePR     │
                                       │  WaitForCi           │
                                       │  CreateGithubRelease │
                                       └──────────────────────┘
```

## Sequence diagram — ADL triggers triage, completes work

```
ADL scheduler     AdlOrchestrator   SelectWorkItem   IssueTriage   TriageItemCycle   GitHub
     │                   │                 │               │              │              │
     │ tick (per tenant) │                 │               │              │              │
     │──────────────────▶│                 │               │              │              │
     │                   │ InitAdlConfig   │               │              │              │
     │                   │────────────────▶│               │              │              │
     │                   │ SelectWorkItem  │               │              │              │
     │                   │────────────────▶│               │              │              │
     │                   │                 │ scan security │              │              │
     │                   │                 │ scan CI       │              │              │
     │                   │                 │ scan tamma-auto issues       │              │
     │                   │                 │ scan untriaged│              │              │
     │                   │ NeedsTriage     │               │              │              │
     │                   │◀────────────────│               │              │              │
     │                   │ dispatch IssueTriageWorkflow    │              │              │
     │                   │─────────────────────────────────▶              │              │
     │                   │                                 │ fetch list   │              │
     │                   │                                 │─────────────────────────────▶
     │                   │                                 │◀─────────────────────────────
     │                   │                                 │ for each item: dispatch      │
     │                   │                                 │ TriageItemCycle (queued)     │
     │                   │                                 │─────────────▶│              │
     │                   │                                 │              │ ctx gather   │
     │                   │                                 │              │─────────────▶│
     │                   │                                 │              │ panel x4     │
     │                   │                                 │              │ (LlmCall WF) │
     │                   │                                 │              │ PO decision  │
     │                   │                                 │              │ apply labels │
     │                   │                                 │              │─────────────▶│
     │                   │                                 │              │ post comment │
     │                   │                                 │              │─────────────▶│
     │ (next tick)       │                                 │              │              │
     │──────────────────▶│ SelectWorkItem (again)          │              │              │
     │                   │ Selected (tamma-auto, P1)       │              │              │
     │                   │ dispatch SingleIssueCycle       │              │              │
     │                   │─────────────────────────────────────▶ (Workflow #2, existing)  │
```

## Use cases

1. **Automatic issue classification on webhook** — new GitHub issue fires `issues.opened`; `IssueTriageWorkflow` is invoked; within 30s the item has type/priority/complexity/autonomy labels and a triage-summary comment.
2. **ADL finds no ready work but has untriaged items** — next tick dispatches triage; the tick after, the newly-labelled items become ready (`tamma-auto` + priority-P1 selected).
3. **Priority escalation** — an issue aged 30 days without work gets bumped up; `SelectWorkItemActivity` re-ranks it on the next tick (26-4 scope).
4. **Scrum sprint planning (planned)** — weekly cron dispatches `SprintPlanningWorkflow`, LLM balances bugs vs features vs chores against team velocity, posts sprint plan to GitHub Project v2.
5. **Daily standup (planned)** — morning cron emits "what completed / what's in progress / what's blocked" summary comment on the sprint tracking issue.
6. **Release train (planned)** — when a milestone closes, `ReleaseManagementWorkflow` validates readiness, generates changelog, bumps version (semver from change types), creates release PR, waits for CI, merges, creates GitHub Release, kicks deployment.
7. **Per-tenant priority override** — tenant admin edits `priority.labels` in `AdlConfig` to make their own `priority-critical` label map to `urgent` — no code change.
8. **Manual triage re-run** — commenting `/triage` on an issue re-dispatches `TriageItemCycleWorkflow` for that item.

## Stories

| Story | Title | Priority | Status | Notes |
|-------|-------|----------|--------|-------|
| 26-1 | Issue Triage Workflow | High | **Partially shipped** | `IssueTriageWorkflow`, `TriageItemCycleWorkflow`, `TriageContextGatheringWorkflow`, `TriagePanelReviewWorkflow`, `TriagePODecisionWorkflow` all in C# Elsa; `FetchUntriagedItemsActivity` + `SelectWorkItemActivity` implemented. Label application + comment posting wired. |
| 26-2 | Scrum Management Workflow | Medium | Drafted | Sprint planning, daily standup, retrospective; needs GitHub Projects v2 integration. |
| 26-3 | Release Management Workflow | Medium | Drafted | Changelog gen, version bump (semver), release PR, GitHub release, deployment trigger. |
| 26-4 | Priority Configuration System | High | Drafted | Rule engine with configurable label/source/complexity mappings and time-based escalation; loaded by `InitAdlConfigActivity`. |

## Labels system

### Type labels
- `bug` — something is broken
- `feature` — new functionality
- `chore` — maintenance, refactoring, deps
- `question` — needs clarification
- `security` — vulnerability or security concern
- `docs` — documentation

### Priority labels
- `priority-critical` — production down, security vulnerability (→ `urgent`)
- `priority-high` — significant impact
- `priority-medium` — normal (default)
- `priority-low` — nice to have

### Complexity labels
- `complexity-trivial` — < 1 hour
- `complexity-simple` — 1–4 hours
- `complexity-medium` — 4–16 hours
- `complexity-complex` — 16+ hours
- `complexity-epic` — needs decomposition

### Autonomy labels
- `tamma-auto` — Tamma can handle autonomously
- `tamma-assist` — Tamma helps but needs human review
- `needs-human` — requires human decision

## Priority configuration (26-4 target schema)

```json
{
  "priority": {
    "sources": {
      "security-alert-critical": "urgent",
      "security-alert-high":     "urgent",
      "ci-failure-main":         "urgent",
      "ci-failure-branch":       "normal",
      "issue":                   "normal",
      "stale-pr":                "low"
    },
    "labels": {
      "priority-critical": "urgent",
      "priority-high":     "high",
      "priority-medium":   "normal",
      "priority-low":      "low",
      "bug":               "high",
      "security":          "urgent",
      "hotfix":            "urgent",
      "blocked":           "skip"
    },
    "complexity": {
      "complexity-trivial": 1,
      "complexity-simple":  2,
      "complexity-medium":  4,
      "complexity-complex": 8
    },
    "escalation": {
      "maxAgeDays": 30,
      "bumpLevels": 1
    }
  }
}
```

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| GitHub Platform | Epic 1 | `IGitPlatform.fetchIssues`, `applyLabels`, `postComment` |
| LLM Call workflow | #17 (Epic 13 decomposition) | Classification + panel review + PO synthesis |
| Event Sourcing | Epic 4 | `TRIAGE.*`, `ADL.*`, `RELEASE.*` events for audit trail |
| Elsa Workflows | Epic 7 / Epic 13 | Platform workflow engine |
| Prompt Store | Epic 27 | Triage classification prompts (system + tenant overrides) |
| Security Sanitization | Epic 11 | Incoming issue bodies sanitized before LLM injection |

## Current state

- **Shipped**: `AdlOrchestratorWorkflow`, `IssueTriageWorkflow`, `TriageItemCycleWorkflow`, `TriageContextGatheringWorkflow`, `TriagePanelReviewWorkflow`, `TriagePODecisionWorkflow` plus their supporting activities. `SelectWorkItemActivity` returns the three outcomes and is consumed by the outer loop.
- **Drafted**: 26-2 (scrum), 26-3 (release), 26-4 (priority configuration system). Schema and flows are written in the story docs; no code yet.
- **Open questions**:
  - Should scrum workflows run per-repo or per-tenant? Leaning per-repo for MVP.
  - Should the release workflow write CHANGELOG.md inside the PR, or emit a pre-prepared draft? Leaning "PR with draft, human confirms".
  - Time-based priority escalation (26-4) needs idempotent back-off so the same issue doesn't bounce across two levels.

## See also

- [Workflow — ADL Orchestrator](Workflow-ADL-Orchestrator) — the per-tenant outer loop.
- [Workflow — LLM Call](Workflow-LLM-Call) — classification and panel review consume it.
- [Epic 4 — Event Sourcing](Epic-4-Event-Sourcing.md) — audit trail.
- [Epic 27 — Prompt Store](Epic-27-Prompt-Store.md) — triage prompts live here with tenant overrides.
- [Epic 13 — Workflow Decomposition](Epic-13-Workflow-Decomposition.md) — all 30 workflows in context.
- [Roadmap](Roadmap.md) — overall plan.

## Story files

[Epic 26 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-26)

---

_Last updated: 2026-04-22_
