---
title: "Workflow: Triage Panel Review"
---

**Definition ID:** `triage-panel-review`
**Class:** `TriagePanelReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`

## Purpose

The Triage Panel Review workflow runs a 4-role LLM panel to assess a triage item from multiple perspectives. Each role dispatches `llm-call` with `role=<role>` and `action=triage`, providing the item JSON and gathered context. Results are aggregated into a panel result JSON containing all assessments.

For security alerts, this includes CVE impact, attack surface, breaking changes, dependency chain, and compatibility. For issues, this includes type classification, complexity estimate, and scope.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| Security Review  |
| (llm-call:       |
|  security,       |
|  triage)         |
+--------+---------+
         |
         v
+------------------+
| Extract Security |
| Review           |
+--------+---------+
         |
         v
+------------------+
| Developer Review |
| (llm-call:       |
|  developer,      |
|  triage)         |
+--------+---------+
         |
         v
+------------------+
| Extract Developer|
| Review           |
+--------+---------+
         |
         v
+------------------+
| DevOps Review    |
| (llm-call:       |
|  devops,         |
|  triage)         |
+--------+---------+
         |
         v
+------------------+
| Extract DevOps   |
| Review           |
+--------+---------+
         |
         v
+------------------+
| Tester Review    |
| (llm-call:       |
|  tester,         |
|  triage)         |
+--------+---------+
         |
         v
+------------------+
| Extract Tester   |
| Review           |
+--------+---------+
         |
         v
+------------------+
| Aggregate        |
| Results          |
+--------+---------+
         |
         v
+------------------+
| Output Panel     |
| Result           |
+--------+---------+
         |
         v
+------------------+
| Finish           |
+------------------+
```

## Review Roles

| Role | Focus |
|------|-------|
| Security | CVE impact, attack surface, vulnerability assessment |
| Developer | Implementation complexity, breaking changes, compatibility |
| DevOps | Deployment impact, infrastructure concerns, dependency chain |
| Tester | Test coverage impact, regression risk, scope |

Each role receives `itemJson`, `contextJson`, and `repository` via the LLM call variables.

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `ItemJson` | string | Triage item JSON |
| `ContextJson` | string | Context from triage-context-gathering |
| `SecurityReview` | string | Security analyst review JSON |
| `DeveloperReview` | string | Developer review JSON |
| `DevOpsReview` | string | DevOps review JSON |
| `TesterReview` | string | Tester review JSON |
| `PanelResultJson` | string | Aggregated panel result JSON |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `itemJson` | string | Triage item JSON |
| `contextJson` | string | Context from triage-context-gathering |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `panelResultJson` | string | Panel review results JSON |

## Output Format

The aggregated panel result is a JSON object:

```json
{
  "reviews": [
    { "role": "security", "assessment": "..." },
    { "role": "developer", "assessment": "..." },
    { "role": "devops", "assessment": "..." },
    { "role": "tester", "assessment": "..." }
  ],
  "reviewCount": 4
}
```

## Review Extraction

Each role's LLM response is parsed for JSON. If valid JSON is found, it is used as the assessment. If not, the raw text is wrapped in a `{"rawAssessment": "..."}` object.

---

_See also: [Triage Context Gathering](/workflows/triage-context-gathering) | [Triage PO Decision](/workflows/triage-po-decision) | [Issue Triage](/workflows/triage) | [Workflows Index](/workflows)_
