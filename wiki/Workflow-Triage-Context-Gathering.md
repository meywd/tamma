---
title: "Workflow: Triage Context Gathering"
---

**Definition ID:** `triage-context-gathering`
**Class:** `TriageContextGatheringWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs`

## Purpose

The Triage Context Gathering workflow gathers context for issue/alert triage. It dispatches `llm-call` with `role=developer` and `action=context-scan` to analyze code usage of the affected package/module, dependency graphs, CVE details (for security alerts), changelogs, and migration guides.

The workflow auto-detects the item type (issue, security alert, or dependency update) from the item JSON and adjusts the context scan focus accordingly.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs,    |
|  detect item     |
|  type)           |
+--------+---------+
         |
         v
+------------------+
| Gather Context   |
| (llm-call:       |
|  developer,      |
|  context-scan,   |
|  scanFocus=      |
|  triage)         |
+--------+---------+
         |
         v
+------------------+
| Extract Result   |
| (parse JSON or   |
|  wrap raw text)  |
+--------+---------+
         |
         v
+------------------+
| Output Context   |
+--------+---------+
         |
         v
+------------------+
| Finish           |
+------------------+
```

## Item Type Detection

The workflow detects the triage item type by inspecting the `itemJson` content:

| Detected Type | Trigger Patterns | Scan Focus |
|---------------|------------------|------------|
| `security` | Contains `"type":"security"`, `"advisory"`, or `"cve"` | CVE impact, attack surface |
| `dependency` | Contains `"type":"dependabot"` or `"dependency"` | Dependency chain, breaking changes |
| `issue` | Default (no security/dependency patterns found) | Code usage, module impact |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `ItemJson` | string | Triage item JSON |
| `ContextJson` | string | Gathered context result JSON |
| `ItemType` | string | Detected item type: `issue`, `security`, or `dependency` |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON (issue or security alert) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `contextJson` | string | Gathered context JSON |

## Result Extraction

The LLM response is parsed for JSON. If valid JSON is found, it is returned directly. If not, the raw text is wrapped in a `{"rawContext": "..."}` object.

---

_See also: [Triage Panel Review](/workflows/triage-panel-review) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
