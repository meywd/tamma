# Workflow: Triage Item Cycle

**Definition ID:** `triage-item-cycle`
**Class:** `TriageItemCycleWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs`

> **Epic 39 (Story 39-15) — non-binding orchestrator; panel nodes removed.** This workflow is NOT itself a `document-lifecycle` binding — it orchestrates the migrated triage bindings. The separate `triage-panel-review` dispatch (plus its `extractPanelResult` / `panelUsable` nodes) is **deleted**: the 4-role panel is now the REVIEW stage INSIDE the [Triage PO Decision](Workflow-Triage-PO-Decision) lifecycle (the 39-7 doc-type-aware panel). This cycle now only dispatches the `triage-context-gathering` `Findings` binding and the `triage-po-decision` `TriageDecision` binding, then applies labels/comment. See [Document Lifecycle](Document-Lifecycle).

## Purpose

Processes a single untriaged item through the triage pipeline: gather context, get a reviewed PO decision (the panel runs inside that decision's lifecycle), then apply labels and post a comment.

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
| PO Decision                |
| (triage-po-decision)       |
| priority, labels, type;    |
| 4-role panel runs INSIDE   |
| this lifecycle's REVIEW    |
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
| `triage-context-gathering` | Yes | Code usage, deps, CVE details (produces `Findings`) |
| `triage-po-decision` | Yes | Reviewed PO decision on priority/labels (produces `TriageDecision`; the 4-role panel is this lifecycle's REVIEW stage) |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `itemJson` | string | JSON of the untriaged item |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `ContextJson` | string | Context gathered for the item (accepted `Findings` body) |
| `PODecisionJson` | string | Reviewed PO triage decision (accepted `TriageDecision` body) |

---

_See also: [Issue Triage](Workflow-Triage) | [Workflows Index](Workflows)_
