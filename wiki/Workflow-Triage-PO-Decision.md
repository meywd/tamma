---
title: "Workflow: Triage PO Decision"
---

**Definition ID:** `triage-po-decision`
**Class:** `TriagePODecisionWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs`

> **Epic 39 (Story 39-15) — now a `document-lifecycle` binding (produces `TriageDecision`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). It dispatches `document-lifecycle` with `documentType = triage-decision` and the `(product_owner, triage-intake)` producer cell. **The 4-role panel is now the lifecycle's REVIEW stage** (the 39-7 doc-type-aware panel over the draft, using the TRIAGE roster) — the previously separate `triage-panel-review` workflow is deleted and its `panelResultJson` input is no longer supplied. Closed-enum validity is now a validator failure, not a parse branch; the old `Extract Decision` (parse-with-defaults) → `Finish` terminal is **deleted**. The legacy `decisionJson` output is the accepted `TriageDecision` body. The Flow Diagram and "Decision Extraction" section below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Triage PO Decision workflow has the Product Owner make a final triage decision based on the panel review results. It dispatches `llm-call` with `role=product_owner` and `action=triage`, passing the item JSON and panel result. The PO's response is parsed for priority, type, complexity, automation level, labels, and a triage comment.

Tools are disabled for this call (`enableTools=false`) since the PO decision is a pure assessment requiring no tool use.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| PO Decision      |
| (llm-call:       |
|  product_owner,  |
|  triage,         |
|  no tools)       |
+--------+---------+
         |
         v
+------------------+
| Extract Decision |
| (parse JSON,     |
|  apply defaults) |
+--------+---------+
         |
         v
+------------------+
| Output Decision  |
+--------+---------+
         |
         v
+------------------+
| Finish           |
+------------------+
```

## Decision Fields

| Field | Values | Default | Description |
|-------|--------|---------|-------------|
| `priority` | `urgent`, `high`, `normal`, `low` | `normal` | Issue priority |
| `type` | `bug`, `feature`, `chore`, `security`, `docs` | `feature` | Issue type classification |
| `complexity` | `trivial`, `simple`, `medium`, `complex`, `epic` | `medium` | Estimated complexity |
| `automation` | `tamma-auto`, `tamma-assist`, `needs-human` | `needs-human` | Automation eligibility |
| `labels` | string[] | `[]` | Labels to apply to the issue |
| `comment` | string | `""` | Triage comment to post on the issue |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `ItemJson` | string | Triage item JSON |
| `ContextJson` | string | Gathered triage context (the accepted `Findings` body) fed into the produce step |
| `DecisionJson` | string | Final PO decision JSON (the accepted `TriageDecision` body) |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON |
| `contextJson` | string | Gathered triage context (accepted `Findings` body); the panel now runs inside the lifecycle's REVIEW stage, so no separate `panelResultJson` input is supplied |
| `findingsDocumentId` | string | Store id of the consumed `Findings` document (lineage) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decisionJson` | string | PO decision JSON with priority, type, complexity, automation, labels, and comment |

## Decision Extraction

The LLM response is parsed for JSON. If valid JSON is found, each field is extracted with defaults applied for any missing fields. If the response is raw text (not JSON), it is wrapped as a default decision with the raw text as the `comment` field. If no response is received at all, a default decision is returned with `comment: "No PO decision received."`.

---

_See also: [Triage Context Gathering](/workflows/triage-context-gathering) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
