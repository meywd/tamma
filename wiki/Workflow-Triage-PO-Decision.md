---
title: "Workflow: Triage PO Decision"
---

**Definition ID:** `triage-po-decision`
**Class:** `TriagePODecisionWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Triage PO Decision workflow will have the Product Owner make a final triage decision based on the panel review. The decision includes priority, type, complexity, automation level, labels to apply, and a triage comment to post. Currently outputs a default empty JSON object.

## Flow Diagram

```
+---------------------+
| Set Default         |
| decisionJson = "{}" |
+--------+------------+
         |
         v
+---------------------+
| Stub: PO            |
| Decision -- TODO    |
+---------------------+
```

## Planned Decision Fields

| Field | Values | Description |
|-------|--------|-------------|
| Priority | urgent, high, normal, low | Issue priority |
| Type | bug, feature, chore, security, docs | Issue type classification |
| Complexity | trivial, simple, medium, complex, epic | Estimated complexity |
| Automation | tamma-auto, tamma-assist, needs-human | Automation eligibility |
| Labels | string[] | Labels to apply to the issue |
| Comment | string | Triage comment to post on the issue |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON |
| `panelResultJson` | string | Panel review results from triage-panel-review |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decisionJson` | string | PO decision JSON (currently defaults to "{}") |

---

_See also: [Triage Panel Review](/workflows/triage-panel-review) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
