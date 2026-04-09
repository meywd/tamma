---
title: "Workflow: Triage PO Decision"
---

**Definition ID:** `triage-po-decision`
**Class:** `TriagePODecisionWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs`

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
| `PanelResultJson` | string | Panel review results from triage-panel-review |
| `DecisionJson` | string | Final PO decision JSON |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON |
| `panelResultJson` | string | Panel review results from triage-panel-review |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decisionJson` | string | PO decision JSON with priority, type, complexity, automation, labels, and comment |

## Decision Extraction

The LLM response is parsed for JSON. If valid JSON is found, each field is extracted with defaults applied for any missing fields. If the response is raw text (not JSON), it is wrapped as a default decision with the raw text as the `comment` field. If no response is received at all, a default decision is returned with `comment: "No PO decision received."`.

---

_See also: [Triage Panel Review](/workflows/triage-panel-review) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
