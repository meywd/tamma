---
title: "Workflow: Research"
---

**Definition ID:** `research`
**Class:** `ResearchWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ResearchWorkflow.cs`

> **Epic 39 (Story 39-13) — now a `document-lifecycle` binding (produces `Findings`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). It assembles the codebase/prior-art context and dispatches `document-lifecycle` with `documentType = findings` and the `(product_owner, research)` producer cell, then exposes typed outcomes. The old bespoke pipeline — `llm-call` → hand parser (`ResearchParsing`) → success-flag gate → error-`Finish` terminal — is **deleted**; the lifecycle's generic rings own all validation, review-with-notes, bounded revision, and typed escalation with full lineage instead of a dead terminal. The `RESEARCH.*` events still emit, now **alongside** the generic `DOCUMENT.*` events. The Flow Diagram and "Fail-Closed Parsing" section below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Research workflow (Story 3.4) autonomously investigates an issue/topic — typically when ambiguity is detected in a requirement. It gathers codebase/prior-art context by reusing the `context-gathering` sub-workflow, then synthesizes the gathered context into a ranked, confidence-scored research report via the MEDIATED `llm-call` path (role=`product_owner`, action=`research`) — the engine holds no LLM credential. Results are emitted as `RESEARCH.*` DCB events so the research is stored and linked to the originating issue for traceability.

Research is AUTONOMOUS — there is no human gate/bookmark. Requirement ambiguity itself is resolved by the sibling [Clarifying Questions](/workflows/clarifying-questions) workflow; research just investigates and reports. It mirrors the [Assessment](/workflows/assessment) skeleton (gather-context → llm-call → parse → fail-closed gate + error terminal).

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (topic, issueId, |
|  repo, tenant)   |
+--------+---------+
         |
         v
+------------------+
| Emit RESEARCH.   |
| STARTED          |
+--------+---------+
         |
         v
+------------------+
| Gather Context   |
| (context-        |
|  gathering)      |
+--------+---------+
         |
         v
+------------------+
| Store Context    |
| Result           |
+--------+---------+
         |
         v
+------------------+
| Emit RESEARCH.   |
| CONTEXT_GATHERED |
+--------+---------+
         |
         v
+------------------+
| Synthesize       |
| Research         |
| (llm-call:       |
|  product_owner/  |
|  research)       |
+--------+---------+
         |
         v
+------------------+
| Parse Research   |
| (fail-closed)    |
+--------+---------+
         |
         v
+------------------+
| Research LLM OK? |
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit RESEARCH.   |
| RESEARCH.| | FAILED (LOUD)    |
| COMPLETED| +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | Research Error   |
| Set      |   | (Finish)         |
| Output   |   +------------------+
| Result   |
+----+-----+
     |
     v
+------------------+
| Expose Output    |
| (report,         |
|  findingCount,   |
|  confidence)     |
+------------------+
```

## Sub-Workflows Dispatched

| Workflow | Wait? | Purpose |
|----------|-------|---------|
| `context-gathering` | Yes | Multi-role codebase/prior-art scan (same reuse as Assessment) |
| `llm-call` | Yes | Synthesis — role=`product_owner`, action=`research`, tools disabled |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier (a new one is minted if empty) |
| `issueId` | string | Issue identifier |
| `topic` | string | Research topic/question (wrapped into a minimal work-item JSON when no `workItemJson` is supplied) |
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `workItemJson` | string | Work item JSON (preferred over `topic` when present) |
| `tenantId` | string | Tenant id (GUID string, or empty in single-user mode) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `sessionId` | string | Session identifier |
| `status` | string | `completed` on success |
| `report` | string | The serialized `ResearchReport` JSON (`{}` on failure) |
| `findingCount` | int | Number of findings recovered |
| `confidence` | double | Overall confidence (0..1) across the findings |
| `contextIds` | string | Context IDs JSON array from context gathering |

## Events Emitted

| Event | Status | When |
|-------|--------|------|
| `RESEARCH.STARTED` | success | Investigation begins |
| `RESEARCH.CONTEXT_GATHERED` | success | Context-gathering sub-workflow returned |
| `RESEARCH.COMPLETED` | success | A valid ranked report was parsed (carries finding count + confidence) |
| `RESEARCH.FAILED` | error (LOUD) | The synthesis `llm-call` failed or output was empty/unparseable — never a false success |

## Fail-Closed Parsing

`ResearchParsing.ParseReport` returns `null` (routing to the error terminal — no fabricated report) on empty/unparseable output, a missing summary, or zero findings. Recovered findings are ranked most-relevant-first (relevance desc, then confidence desc).

## Report Shape

The dedicated system prompt template (`SystemPrompts.ResearchBody`, taxonomy pair `product_owner`/`research`) emits exactly the JSON the parser recovers:

```json
{
  "topic": "...",
  "summary": "...",
  "findings": [
    {
      "title": "...",
      "summary": "...",
      "relevance": 0.9,
      "confidence": 0.8,
      "citations": ["src/foo.cs", "docs/bar.md"]
    }
  ],
  "overallConfidence": 0.85
}
```

The report lives in DCB events + workflow state only — there is no dedicated table.

---

_See also: [Context Gathering](/workflows/context-gathering) | [LLM Call](/workflows/llm-call) | [Ambiguity Scoring](/workflows/ambiguity-scoring) | [Clarifying Questions](/workflows/clarifying-questions) | [Workflows Index](/workflows)_
