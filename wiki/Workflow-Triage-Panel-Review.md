---
title: "Workflow: Triage Panel Review"
---

**Definition ID:** `triage-panel-review`
**Class:** `TriagePanelReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Triage Panel Review workflow will run a 4-role panel (Security Analyst, Developer, DevOps, QA) to assess a triage item from their respective perspectives. For security alerts, this includes CVE impact, attack surface, breaking changes, dependency chain, and compatibility. For issues, this includes type classification, complexity estimate, and scope. Currently outputs a default empty JSON object.

## Flow Diagram

```
+---------------------+
| Set Default         |
| panelResultJson     |
| = "{}"              |
+--------+------------+
         |
         v
+---------------------+
| Stub: Triage        |
| Panel -- TODO       |
+---------------------+
```

## Planned Review Roles

| Role | Focus |
|------|-------|
| Security Analyst | CVE impact, attack surface, vulnerability assessment |
| Developer | Implementation complexity, breaking changes, compatibility |
| DevOps | Deployment impact, infrastructure concerns, dependency chain |
| QA | Test coverage impact, regression risk, scope |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON |
| `contextJson` | string | Context from triage-context-gathering |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `panelResultJson` | string | Panel review results JSON (currently defaults to "{}") |

---

_See also: [Triage Context Gathering](/workflows/triage-context-gathering) | [Triage PO Decision](/workflows/triage-po-decision) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
