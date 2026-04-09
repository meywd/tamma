# Workflow: Triage Item Cycle

**Definition ID:** `triage-item-cycle`
**Class:** `TriageItemCycleWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs`

## Purpose

Processes a single untriaged item through the full triage pipeline: gather context, run a 4-role panel review, get a PO decision, then apply labels and post a comment.

Runs as a **singleton workflow** — only one instance executes at a time. When `IssueTriageWorkflow` dispatches multiple items, Elsa queues them and processes them sequentially. This prevents overloading LLM resources while ensuring all items get triaged.

## Flow Diagram

```
+----------------------------+
| Initialize                 |
| (read repository, itemJson)|
+-----------+----------------+
            |
            v
+----------------------------+
| Gather Triage Context      |
| (triage-context-gathering) |
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
+-----------+----------------+
            |
            v
+----------------------------+
| Finish                     |
+----------------------------+
```

## Sub-Workflows Dispatched

| Sub-Workflow | Wait | Purpose |
|---|---|---|
| `triage-context-gathering` | Yes | Code usage, deps, CVE details |
| `triage-panel-review` | Yes | 4-role LLM panel assessment |
| `triage-po-decision` | Yes | PO final decision on priority/labels |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `itemJson` | string | JSON of the untriaged item |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `ContextJson` | string | Context gathered for the item |
| `PanelResultJson` | string | Panel review results |
| `PODecisionJson` | string | PO triage decision |

---

_See also: [Issue Triage](Workflow-Triage) | [Workflows Index](Workflows)_
