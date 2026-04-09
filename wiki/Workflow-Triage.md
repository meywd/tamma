# Workflow: Issue Triage

**Definition ID:** `issue-triage`
**Class:** `IssueTriageWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueTriageWorkflow.cs`

## Purpose

The Issue Triage workflow fetches **untriaged items** (GitHub issues, Dependabot alerts, CodeQL alerts) and dispatches a **singleton triage cycle** for each one. The per-item processing (context → panel review → PO decision → labels) is handled by the `TriageItemCycleWorkflow` sub-workflow, which runs as a singleton — Elsa queues dispatches so items are triaged sequentially without overloading LLM resources.

## Triggers

- **ADL Orchestrator** -- Dispatched when the orchestrator detects `NeedsTriage` outcome
- **GitHub webhook** -- `issues.opened` event
- **Manual dispatch** -- Via ELSA Studio or API

## Flow Diagram

```
+----------------------------+
| Fetch Untriaged Items      |
| (issues + Dependabot +     |
|  CodeQL alerts)            |
+-----------+----------------+
            |
            v
+----------------------------+
| Has Items?                 |
+---+--------------------+---+
   YES                    NO
    |                      |
    v                      v
+----------------------------+   +------------------+
| Extract Current Item       |   | Report Complete  |
+-----------+----------------+   +--------+---------+
            |                             |
            v                             v
+----------------------------+   +------------------+
| Dispatch Triage Cycle      |   | Finish           |
| (fire & forget, singleton) |   +------------------+
+-----------+----------------+
            |
            v
+----------------------------+
| Next Item                  |
+-----------+----------------+
            |
            v
+----------------------------+
| More Items?                |
+---+--------------------+---+
   YES                    NO
    |                      |
    +-- (loop back to      +-- Report Complete
        Extract Current        → Finish
        Item)
```

## Sub-Workflows

### Triage Item Cycle (singleton)

**Definition ID:** `triage-item-cycle`
**Class:** `TriageItemCycleWorkflow`

Processes a single untriaged item sequentially. Runs as a **singleton** — only one instance at a time. Dispatches are queued by Elsa.

**Flow:** Init → Gather Context → Panel Review → PO Decision → Apply Labels → Finish

**Inputs:** `repository`, `itemJson`

Each step dispatches an LLM call sub-workflow. The LLM call workflow self-throttles on budget and concurrency limits, providing natural backpressure.

### Triage Context Gathering

**Definition ID:** `triage-context-gathering`
**Class:** `TriageContextGatheringWorkflow`

Gathers context specific to triage decisions:
- Code usage of affected package/module
- Dependency graph analysis
- CVE details (for security alerts)
- Changelog and migration guides
- Related issues and PRs

**Inputs:** `repository`, `itemJson`
**Outputs:** `contextJson`

Dispatches `llm-call` with `role=developer`, `action=context-scan`, `scanFocus=triage`. Auto-detects item type (issue/security/dependency) from the item JSON.

See [Triage Context Gathering](Workflow-Triage-Context-Gathering) for full details.

### Triage Panel Review

**Definition ID:** `triage-panel-review`
**Class:** `TriagePanelReviewWorkflow`

Four-role LLM panel assesses the item:

| Role | Focus |
|------|-------|
| Security Analyst | CVE impact, attack surface, breaking changes |
| Developer | Type classification, complexity estimate, implementation scope |
| DevOps | Infrastructure impact, deployment considerations, dependency chain |
| QA | Test impact, compatibility, regression risk |

**Inputs:** `repository`, `itemJson`, `contextJson`
**Outputs:** `panelResultJson`

Each role dispatches `llm-call` with `role=<role>`, `action=triage`. Results are aggregated into a JSON object with all 4 assessments.

See [Triage Panel Review](Workflow-Triage-Panel-Review) for full details.

### Triage PO Decision

**Definition ID:** `triage-po-decision`
**Class:** `TriagePODecisionWorkflow`

The Product Owner makes the final triage decision:

| Decision Field | Values |
|----------------|--------|
| Priority | urgent, high, normal, low |
| Type | bug, feature, chore, security, docs |
| Complexity | trivial, simple, medium, complex, epic |
| Automation | tamma-auto, tamma-assist, needs-human |
| Labels | Array of labels to apply |
| Comment | Triage summary comment to post on the issue |

**Inputs:** `repository`, `itemJson`, `panelResultJson`
**Outputs:** `decisionJson`

Dispatches `llm-call` with `role=product_owner`, `action=triage` (tools disabled). Parses the PO response for priority, type, complexity, automation, labels, and comment fields with sensible defaults.

See [Triage PO Decision](Workflow-Triage-PO-Decision) for full details.

## Concurrency Model

```
IssueTriageWorkflow          TriageItemCycleWorkflow (singleton)
┌──────────────┐             ┌──────────────────────┐
│ Fetch 5 items│             │ Item 1: context →    │
│ Dispatch #1  │──f&f──────→ │   panel → PO → label │
│ Dispatch #2  │──f&f──┐     │                      │
│ Dispatch #3  │──f&f──┤     └──────────────────────┘
│ Dispatch #4  │──f&f──┤          ↓ (queued)
│ Dispatch #5  │──f&f──┤     ┌──────────────────────┐
│ Report Done  │       ├────→│ Item 2: context →    │
└──────────────┘       │     │   panel → PO → label │
                       │     └──────────────────────┘
                       │          ↓ (queued)
                       └───→ Items 3, 4, 5 ...
```

The triage workflow completes quickly (just dispatches). The actual work is serialized by the singleton constraint on `triage-item-cycle`. Each LLM call within the cycle self-throttles via budget and concurrency guards.

## Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `repository` | string | required | Repository identifier (owner/repo) |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `ItemsJson` | string | JSON array of untriaged items |
| `TotalItems` | int | Count of items to process |
| `CurrentItemIndex` | int | Current loop index |

---

_See also: [ADL Orchestrator](Workflow-ADL-Orchestrator) | [Triage Item Cycle](Workflow-Triage-Item-Cycle) | [Workflows Index](Workflows)_
