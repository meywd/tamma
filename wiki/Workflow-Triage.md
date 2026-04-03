# Workflow: Issue Triage

**Definition ID:** `issue-triage`
**Class:** `IssueTriageWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueTriageWorkflow.cs`

## Purpose

The Issue Triage workflow fetches **untriaged items** (GitHub issues, Dependabot alerts, CodeQL alerts) and processes each one through a structured pipeline: gather context, run a 4-role panel review, get a PO decision, then apply labels and post a triage comment.

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
| Gather Triage Context      |   | Finish           |
| (triage-context-gathering) |   +------------------+
+-----------+----------------+
            |
            v
+----------------------------+
| Panel Review               |
| (triage-panel-review)      |
| security/dev/devops/qa     |
+-----------+----------------+
            |
            v
+----------------------------+
| PO Decision                |
| (triage-po-decision)       |
| priority, labels, type     |
+-----------+----------------+
            |
            v
+----------------------------+
| Apply Labels & Comment     |
| (ApplyTriageResultActivity)|
+-----------+----------------+
            |
            v
+----------------------------+
| Increment Triaged          |
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

**Status:** Stub -- workflow structure defined, implementation pending.

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

For security alerts, the panel specifically evaluates: CVE severity, attack surface exposure, breaking changes from upgrades, dependency chain depth, and compatibility constraints.

**Inputs:** `repository`, `itemJson`, `contextJson`
**Outputs:** `panelResultJson`

**Status:** Stub -- workflow structure defined, implementation pending.

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

**Status:** Stub -- workflow structure defined, implementation pending.

## Item Processing

The workflow processes items in a loop:

1. **FetchUntriagedItemsActivity** -- Queries GitHub for issues without triage labels, plus Dependabot and CodeQL security alerts
2. For each item:
   - Extract the item from the JSON array by index
   - Dispatch `triage-context-gathering` (wait for completion)
   - Dispatch `triage-panel-review` (wait for completion)
   - Dispatch `triage-po-decision` (wait for completion)
   - **ApplyTriageResultActivity** -- Applies labels and posts the triage comment via GitHub API
3. Increment counters and loop until all items are processed
4. **ReportCycleResultActivity** -- Logs triage completion

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
| `TriagedCount` | int | Number of items successfully triaged |

---

_See also: [ADL Orchestrator](Workflow-ADL-Orchestrator) | [Context Gathering](Workflow-Context-Gathering) | [Workflows Index](Workflows)_
