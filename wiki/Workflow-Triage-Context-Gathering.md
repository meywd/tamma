---
title: "Workflow: Triage Context Gathering"
---

**Definition ID:** `triage-context-gathering`
**Class:** `TriageContextGatheringWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Triage Context Gathering workflow will gather context for issue/alert triage including code usage of the affected package, dependency graph, CVE details, changelog, and migration guide. Currently outputs a default empty JSON object.

## Flow Diagram

```
+---------------------+
| Set Default         |
| contextJson = "{}"  |
+--------+------------+
         |
         v
+---------------------+
| Stub: Triage        |
| Context -- TODO     |
+---------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON (issue or security alert) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `contextJson` | string | Gathered context JSON (currently defaults to "{}") |

---

_See also: [Triage Panel Review](/workflows/triage-panel-review) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
